using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Agent;

public enum AgentStopReason { Completed, Cancelled, Error }

/// <summary>Callbacks from the agent loop. Called on a background thread.</summary>
public interface IAgentObserver
{
    void OnStepStarted(int step);
    void OnDelta(StreamDelta delta);
    void OnAssistantMessage(ChatMessage message);

    /// <summary>
    /// In place of <see cref="OnAssistantMessage"/>, the handoff note the model wrote for a context reset (see
    /// <see cref="AgentSession.AskReset"/>): words for its own next steps, no answer to the user. Written after the
    /// answer when Compact context was pressed as the model answered, and taken for the reply there.
    /// </summary>
    void OnHandoffNote(ChatMessage note) => OnAssistantMessage(note);

    Task<bool> RequestApprovalAsync(ToolCall call, Tool tool, string summary, CancellationToken ct);
    void OnToolStarted(ToolCall call, string summary);

    /// <summary>How a long call is getting on ("KSampler, step 3 of 20 · 1:12"), for its card while it runs.</summary>
    void OnToolProgress(ToolCall call, string text) { }

    /// <summary>
    /// A message the user sent during the run (see <see cref="AgentSession.Steer(UserInput)"/>) has
    /// joined the conversation: the model reads it in the step that starts next. The same instance
    /// that was sent. Called before that step's <see cref="OnStepStarted"/>.
    /// </summary>
    void OnUserMessageDelivered(UserInput input) { }

    /// <summary>Status the user should see that is not part of the conversation (e.g. reconnecting).</summary>
    void OnNotice(string text);

    /// <summary>
    /// A notice of what the run does about a server's failure (tries again, waits, goes on with a
    /// smaller context): shown as <see cref="OnNotice(string)"/>, with what failed for a log.
    /// </summary>
    void OnNotice(string text, Exception error) => OnNotice(text);

    /// <summary>
    /// How much of a step's request has gone out: it carries every screenshot in the context, and
    /// over a slow VPN sending it took minutes that looked like the model thinking.
    /// </summary>
    void OnSending(long sent, long total) { }

    /// <summary>
    /// find_text found no text recognition installed: the app offers it on a card of its own (install,
    /// not now, turn off), which the user sees where the model's own words were often skipped.
    /// </summary>
    void OfferTextRecognition() { }

    /// <summary>The plan tool set, moved or took away the plan shown to the user.</summary>
    void OnPlanChanged(AgentPlan? plan) { }

    void OnToolFinished(ToolCall call, ToolResult result);

    /// <summary>
    /// The run is ending on an exception that is no server's answer: a bug, whose message alone
    /// ("Object reference not set to an instance of an object.") tells nobody where. Called before
    /// <see cref="OnFinished(AgentStopReason, string?, Exception)"/>, with the whole exception for a log.
    /// </summary>
    void OnFailed(Exception error) { }

    void OnFinished(AgentStopReason reason, string? detail);

    /// <summary>
    /// <see cref="OnFinished(AgentStopReason, string?)"/> for a run that ended on <paramref name="error"/>,
    /// a server's answer or a bug, whose message is the detail: the whole exception for a log.
    /// </summary>
    void OnFinished(AgentStopReason reason, string? detail, Exception error) => OnFinished(reason, detail);
}

/// <summary>
/// One conversation with the model. Each user turn runs the loop: ask the model, run the tools it
/// calls, feed results back, repeat until it answers without tool calls.
/// </summary>
public sealed partial class AgentSession(
    OpenAiChatClient client,
    ChatRequestOptions requestOptions,
    ToolRegistry tools,
    ToolContext toolContext,
    string systemPrompt,
    List<ChatMessage>? history = null,
    int contextTokens = 0)
{
    /// <summary>The model's context size as this session knows it: as reported at the start, or corrected by a server error.</summary>
    public int ContextTokens => contextTokens;

    /// <summary>The most pictures the server takes in one request, as a refusal showed (0: none known).</summary>
    public int MaxImages { get; private set; }

    /// <summary>
    /// Auto pricing lookup (the user, 2026-11-02): asked at a run's end, after its answer, in a chat of
    /// <see cref="PriceLookupFrom"/> or more, gives the task when the prices of the run's model are to be
    /// looked up (a reply came with no cost from its server and it has none; see
    /// <see cref="SetApiPricingTool.LookupRequest"/>), and null when not. Then, once a run, the model looks
    /// them up and keeps them in a side task (see <see cref="LookUpPricesAsync"/>).
    /// </summary>
    public Func<string?>? PriceLookup { get; set; }

    /// <summary>
    /// How long a chat is before <see cref="PriceLookup"/> is asked, in tokens as the top bar shows them
    /// (<see cref="TokensInUse"/>; a new chat starts around 15k): a short chat isn't worth the lookup's
    /// requests (the user, 2026-11-07).
    /// </summary>
    internal int PriceLookupFrom { get; set; } = 50_000;

    /// <summary>The lookup to run now, if any: the size comes first, as a model's one lookup is spent by asking.</summary>
    private string? PriceLookupDue() =>
        tools.Find("set_api_pricing") is not null && TokensInUse() >= PriceLookupFrom ? PriceLookup?.Invoke() : null;

    /// <summary>Conversation so far (without the system prompt). Can be shared across sessions so settings changes keep the chat.</summary>
    public List<ChatMessage> History { get; } = history ?? [];

    /// <summary>Messages the user sent while the run goes on, waiting for the model's next step. Locked on itself.</summary>
    private readonly List<UserInput> _incoming = [];

    private bool HasIncoming
    {
        get { lock (_incoming) return _incoming.Count > 0; }
    }

    /// <summary>Cancelled when such a message arrives, so a wait in progress ends early (see <see cref="ToolContext.UserMessage"/>).</summary>
    private CancellationTokenSource _arrived = new();

    /// <inheritdoc cref="Steer(UserInput)"/>
    public void Steer(string text) => Steer(new UserInput(text));

    /// <summary>
    /// A message the user sent while the agent works: it joins the conversation before the model's
    /// next step, and a wait in progress ends early so the model reads it soon. Until now Enter did
    /// nothing during a run, even while Sloperator waited for an OK, and the text sat in the box.
    /// Any thread.
    /// </summary>
    public void Steer(UserInput input)
    {
        lock (_incoming)
        {
            _incoming.Add(input);
            _arrived.Cancel();
        }
    }

    /// <inheritdoc cref="ToolContext.EndWait"/>
    public bool EndWait() => toolContext.EndWait();

    /// <summary>
    /// True while the user is writing a message in this agent's own box (the app's window); null where
    /// there is no box. Meanwhile no request goes to the model and no tool call starts (<see cref="HoldWhileTypingAsync"/>).
    /// Read from the run's thread.
    /// </summary>
    public Func<bool>? UserTyping { get; set; }

    /// <summary>
    /// Waits for as long as the user types to the agent (see <see cref="UserTyping"/>). Before, only
    /// its clicks and key presses held off: the model went on with its requests, commands and file
    /// edits under "Paused while you type", and the message the user was typing came a step or more late.
    /// Held before a step's request, the message sent at its end is in that very request.
    /// </summary>
    private async Task HoldWhileTypingAsync(CancellationToken ct)
    {
        while (UserTyping?.Invoke() == true)
            await Task.Delay(TypingPoll, ct).ConfigureAwait(false);
    }

    /// <summary>How often a held run looks whether the user still types.</summary>
    private static readonly TimeSpan TypingPoll = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Takes back a message the model hasn't read yet (Cancel on it in the app): true while it was
    /// still waiting, false once the model has it. The very instance that was sent. Any thread.
    /// </summary>
    public bool Unsteer(UserInput input)
    {
        lock (_incoming)
        {
            if (_incoming.RemoveAll(i => ReferenceEquals(i, input)) == 0)
                return false;
            // None left: a wait that starts now goes on, rather than ending for a message that isn't there.
            if (_incoming.Count == 0 && _arrived.IsCancellationRequested)
            {
                _arrived = new CancellationTokenSource();
                toolContext.UserMessage = _arrived.Token;
                _interruptAsked = false;
            }
            return true;
        }
    }

    /// <summary>Messages that arrived too late for the run (it ended first); the caller sends them on.</summary>
    public List<UserInput> TakeUndelivered()
    {
        lock (_incoming)
        {
            var left = _incoming.ToList();
            _incoming.Clear();
            return left;
        }
    }

    /// <summary>The model's reply in progress, or the tool calls of its reply, which <see cref="Interrupt"/> cuts short. Locked with <see cref="_incoming"/>.</summary>
    private CancellationTokenSource? _step;

    /// <summary>Interrupt was pressed with messages waiting: what of the step is still to come is cut short as well.</summary>
    private bool _interruptAsked;

    /// <summary>
    /// The Interrupt button: the messages the user sent meanwhile go to the model now, not after
    /// what it is doing. Its reply in progress (thinking or writing) is cut off, or the tool calls
    /// it is running are stopped and the rest of them not run, and the next step starts with the
    /// messages. A long think could otherwise go on for minutes past a correction that made it
    /// pointless. False when no message waits. Any thread.
    /// </summary>
    public bool Interrupt()
    {
        CancellationTokenSource? step;
        lock (_incoming)
        {
            if (_incoming.Count == 0)
                return false;
            _interruptAsked = true;
            step = _step;
        }
        try { step?.Cancel(); }
        catch (ObjectDisposedException) { } // the step ended at that very moment; the next one delivers the messages
        return true;
    }

    /// <summary>The scope of one part of a step that <see cref="Interrupt"/> can cut short; cut at once when Interrupt came just before it.</summary>
    private sealed class StepScope : IDisposable
    {
        private readonly AgentSession _session;
        public CancellationTokenSource Source { get; }

        public StepScope(AgentSession session, CancellationToken ct)
        {
            _session = session;
            Source = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (session._incoming)
            {
                session._step = Source;
                if (session._interruptAsked && session._incoming.Count > 0)
                    Source.Cancel();
            }
        }

        public CancellationToken Token => Source.Token;

        /// <summary>Interrupt cut it short, and not a stop of the whole run.</summary>
        public bool Interrupted(CancellationToken run) => Source.IsCancellationRequested && !run.IsCancellationRequested;

        public void Dispose()
        {
            lock (_session._incoming)
                if (ReferenceEquals(_session._step, Source))
                    _session._step = null;
            Source.Dispose();
        }
    }

    /// <summary>Said in the chat where Interrupt cut the model's reply off.</summary>
    internal const string InterruptedNotice = "Interrupted to read your message.";

    /// <summary>The result of a tool call Interrupt stopped, or kept from running.</summary>
    public const string InterruptedResult = "Interrupted: the user's message (below) came in before this finished, and the user asked for it to be read at once. " +
                                            "It may have done part of its work: check before doing it again.";

    public const string NotRunResult = "Not run: the user's message (below) came in first, and the user asked for it to be read at once.";

    // The same where the user took the message back after Interrupt, as the step was already being cut short.
    public const string TakenBackResult = "Interrupted: the user asked for a message of theirs to be read at once, then took it back. " +
                                          "It may have done part of its work: check before doing it again.";

    public const string TakenBackNotRunResult = "Not run: the user asked for a message of theirs to be read at once, then took it back. Run it if it is still needed.";

    /// <summary>The result of a call Interrupt stopped (or kept from running), saying whether the message it was for is still there to read.</summary>
    private string CutShort(bool ran)
    {
        lock (_incoming)
            return _incoming.Count > 0 ? ran ? InterruptedResult : NotRunResult : ran ? TakenBackResult : TakenBackNotRunResult;
    }

    /// <summary>Whether a call's result is one Interrupt left: it stopped, rather than failed or was declined.</summary>
    public static bool IsCutShort(string text) => text is InterruptedResult or NotRunResult or TakenBackResult or TakenBackNotRunResult;

    /// <summary>Adds the user's messages that arrived since the last step, as user messages: the history only grows.</summary>
    /// <param name="turnEnded">
    /// The model's last reply ended its turn: the messages are what it works on next, as if sent
    /// after the run, not something on top of a task in progress. Marked as that, a message sent as
    /// the model wrote its final answer told it to carry on with a task it had already finished.
    /// </param>
    private void DeliverIncoming(bool turnEnded, IAgentObserver observer)
    {
        List<UserInput> delivered;
        lock (_incoming)
        {
            if (!_arrived.IsCancellationRequested)
                return;
            // A fresh token for the next message; the one a wait may still hold stays cancelled.
            _arrived = new CancellationTokenSource();
            toolContext.UserMessage = _arrived.Token;
            _interruptAsked = false;
            delivered = _incoming.ToList();
            _incoming.Clear();
            var first = true;
            foreach (var input in delivered)
            {
                if (turnEnded)
                {
                    if (first)
                    {
                        _request = RequestOf(input);
                        _guesswork?.StartRun(input.Text);
                    }
                    first = false;
                    History.Add(input.ToMessage(toolContext.SeesImages));
                    continue;
                }
                _guesswork?.Heard(input.Text);
                History.Add(Interjection(input.ToMessage(toolContext.SeesImages)));
            }
            toolContext.UserPictures = UserInput.PicturesIn(History);
        }
        foreach (var input in delivered)
            observer.OnUserMessageDelivered(input);
    }

    /// <summary>What the user asked for in this run, quoted to the model when a message of theirs comes in on top of it.</summary>
    private string? _request;

    /// <summary>
    /// A message that came in while the model works, marked as one: answered or acted on, and then
    /// the request goes on. Unmarked, a question read as the new task: asked mid-task why something
    /// had failed, while it was trying Pony Diffusion for a picture, the model answered the question
    /// and ended there, the picture forgotten.
    /// </summary>
    private ChatMessage Interjection(ChatMessage message)
    {
        var request = _request is { } r ? $"\"{r}\"" : "the task above";
        var note = $"[Sent while you were working on {request}. Answer it or do what it asks, then carry on with {(_request is null ? "the task" : "that")} unless it changes or cancels it.]\n";
        if (message.Parts is { Count: > 0 } parts)
            parts[0] = ContentPart.FromText(note + parts[0].Text);
        else
            message.Content = note + message.Content;
        return message;
    }

    /// <summary>
    /// The request a run works on, in the user's words (its first lines): the message that started
    /// it, or for one that only says to go on ("Continue."), the last message before it that asked
    /// for something. Null for a message of files alone.
    /// </summary>
    private string? RequestOf(UserInput input)
    {
        var text = input.Text.Trim();
        if (GoOn().IsMatch(text))
            text = History.LastOrDefault(m => m.FromUser && !GoOn().IsMatch(TypedText(m))) is { } earlier ? TypedText(earlier) : "";
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length == 0 ? null : text.Length <= 200 ? text : text[..197].TrimEnd() + "…";
    }

    /// <summary>What the user typed in a message of theirs, without the list of files that came with it.</summary>
    private static string TypedText(ChatMessage m)
    {
        var text = (m.Parts is { Count: > 0 } parts ? parts[0].Text : m.Content) ?? "";
        var files = text.IndexOf("[The user attached ", StringComparison.Ordinal);
        return (files < 0 ? text : text[..files]).Trim();
    }

    [GeneratedRegex(@"^(continue|go on|carry on|keep going)[.!]?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoOn();

    /// <summary>
    /// The monitor the conversation's latest screenshot showed (0 = primary) and how many there
    /// were, from what the screenshot said ("Screenshot of screen 2 of 2") or, after a context
    /// reset, the screen it carried; null when no screenshot names one (a single monitor).
    /// </summary>
    internal static (int Screen, int Count)? ScreenShown(IReadOnlyList<ChatMessage> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var m = history[i];
            if (m.FromUser)
                continue;
            var text = m.Role == ChatRole.Tool ? m.Content : m.Parts is [{ Text: { } said }, { IsImage: true }] ? said : null;
            if (text is not null && ScreenOf().Match(text) is { Success: true } found)
                return (int.Parse(found.Groups[1].Value, CultureInfo.InvariantCulture) - 1, int.Parse(found.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        return null;
    }

    [GeneratedRegex(@"^(?:Screenshot of|The screen right now:) screen (\d+) of (\d+)[.,]", RegexOptions.Multiline)]
    private static partial Regex ScreenOf();

    public Task RunAsync(string userText, IAgentObserver observer, CancellationToken ct) => RunAsync(new UserInput(userText), observer, ct);

    /// <summary>Runs the task the user sent: the model works on it, with its tools, until it is done or stopped.</summary>
    public async Task RunAsync(UserInput input, IAgentObserver observer, CancellationToken ct)
    {
        _request = RequestOf(input);
        _projects.Recall(History, toolContext);
        // The plan of a task the user stopped leaves the bar with their next message (see StoppedPlanNote).
        var stoppedPlan = toolContext.Plan is { Finished: false } && History.Count > 0 && History[^1].Content?.StartsWith(StoppedNote, StringComparison.Ordinal) == true;
        // Each message starts a run, which looked at the primary screen: told "Continue" with
        // screen 2 in its last screenshot, the model clicked and typed into Notepad on screen 1.
        if (ScreenShown(History) is { } lastSeen)
            toolContext.CurrentScreen = lastSeen.Screen;
        History.Add(input.ToMessage(toolContext.SeesImages));
        // A picture given earlier in the chat (a reference sheet) is still the one to follow.
        toolContext.UserPictures = UserInput.PicturesIn(History);
        toolContext.UserMessage = _arrived.Token;
        toolContext.RunStarted = DateTime.Now;
        if (toolContext.Ask is null)
        {
            var eyes = new FreshLook(client, requestOptions);
            toolContext.Ask = (images, question, ct2) => toolContext.SeesImages ? eyes.AskAsync(images, question, ct2) : Task.FromResult<string?>(null);
        }
        toolContext.ResetContext ??= ModelAsksReset;
        // A side_task call's: its steps are lines on its card, set as its progress when it starts.
        toolContext.SideTask = (task, output, c) => RunSideTaskAsync(task, output, null, null, toolContext.Progress, observer, c);
        toolContext.OfferTextRecognition = observer.OfferTextRecognition;
        toolContext.PlanChanged = observer.OnPlanChanged;
        if (stoppedPlan)
        {
            toolContext.Plan = null;
            observer.OnPlanChanged(null);
        }
        toolContext.InConversation ??= text => History.Any(m => m.Content?.Contains(text, StringComparison.Ordinal) == true ||
                                                                m.Parts?.Any(p => p.Text?.Contains(text, StringComparison.Ordinal) == true) == true);
        var cutOffs = 0;
        var missChecked = false;
        var (planWork, planChecked) = (false, false);
        // The plan nudge (see PlanNote): steps so far without a plan, whether the run needs no asking any more, whether
        // it was asked, whether it was told to carry on after stopping at its plan, whether it made a plan call, and
        // whether it has worked since it was asked.
        var (planSteps, planAsked, planNudged, planPushed, planMoved, planNudgeWork) = (0, false, false, false, false, false);
        // Whether the run was told to carry on with the steps its plan had left when it answered (see PlanLeftNote).
        var planCarried = false;
        var pricesLooked = false;
        var loops = new LoopDetector();
        // Pointing at web_search only makes sense when the model has it. Kept for the chat: "still
        // wrong" in the next message is a failed try, and the time since the last look-up goes on.
        var guesswork = _guesswork ??= tools.Find("web_search") is not null ? new Guesswork() : null;
        guesswork?.StartRun(input.Text);
        var justReset = false;
        var turnEnded = false;

        try
        {
            // No step limit: a run ends when the model is done or the user presses Stop or Pause/Break.
            // Tasks like "keep chatting for 12 hours" are valid;
            // runaway loops are handled by LoopDetector, and growth by context resets.
            for (var step = 1; ; step++)
            {
                ct.ThrowIfCancellationRequested();
                await HoldWhileTypingAsync(ct).ConfigureAwait(false);
                DeliverIncoming(turnEnded, observer);
                turnEnded = false;
                // The reset first: its handoff note is a reply of its own, and the step's reply
                // comes after it, in a new step.
                if (ResetDue())
                    await ResetContextAsync(observer, ct, asked: true).ConfigureAwait(false);
                else if (contextTokens > 0 && TokensInUse() > ResetThreshold)
                    await ResetContextAsync(observer, ct).ConfigureAwait(false);
                observer.OnStepStarted(step);
                LeaveOutOldImages();

                var request = new List<ChatMessage>(History.Count + 1) { ChatMessage.System(systemPrompt) };
                request.AddRange(History);
                // When the server last had this prompt in hand: its cache counts from there (see KeepCacheWarmAsync).
                var sentAt = DateTime.UtcNow;

                ChatMessage reply;
                using (var thinking = new StepScope(this, ct))
                {
                    try
                    {
                        reply = await CompleteWithResetAsync(request, observer, ct, thinking.Token).ConfigureAwait(false);
                        CacheUsed();
                    }
                    catch (OperationCanceledException) when (thinking.Interrupted(ct))
                    {
                        // Interrupt: what streamed stays on screen, and the reply is left out of the
                        // conversation, which goes on with the user's messages.
                        observer.OnNotice(InterruptedNotice);
                        continue;
                    }
                }
                var cutCall = reply.FinishReason == "length" && DropCutCalls(reply);
                var cutOff = reply.FinishReason == "length" && !reply.HasToolCalls;
                if (cutOff && contextTokens > 0 && !justReset && OutOfRoom(reply))
                {
                    // The server ran out of context mid-reply, so the size estimate was off. The
                    // cut-off reply is thrown away and this step is redone after a reset.
                    observer.OnNotice("The model ran out of context in the middle of its reply.");
                    observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "" });
                    await ResetContextAsync(observer, ct).ConfigureAwait(false);
                    justReset = true;
                    continue;
                }
                var afterReset = justReset;
                justReset = false;
                TextToolCallParser.TryExtract(reply, tools);
                cutCall |= reply.FinishReason == "length" && DropCutCalls(reply);
                // A limit of the server's own, not the context's end: under the room each request
                // asks for (ReplyRoom), as LM Studio's Limit Response Length can be.
                // Not one server's name for its limit (the user, 2026-11-01: LM Studio named in it "weird").
                if (cutCall)
                    observer.OnNotice("The server's length limit cut a tool call short; it wasn't run, and the model is asked to write in smaller parts.");
                else if (cutOff && !afterReset)
                    observer.OnNotice("The server stopped the reply at its length limit; asking the model to go on.");
                // "The task is complete" followed by yet another screenshot: small models can keep
                // re-verifying forever. A completion claim with only look-only calls ends the turn.
                // Not a reset it asked for ("Handoff written, all done." with reset_context), nor the
                // picture of its work it takes for the user then, which is what it is for, nor the effort
                // it was asked to set ("Done, effort set to high."), nor the last step of its plan marked done.
                if (reply.HasToolCalls && LooksFinished(reply.Content) &&
                    reply.ToolCalls!.All(c => ToolCallRepair.ResolveTool(c.Name, tools) is { Risk: ToolRisk.ReadOnly } t && t is not (ResetContextTool or CaptureResultTool or SetEffortTool or PlanTool)))
                    reply.ToolCalls = null;
                History.Add(reply);
                observer.OnAssistantMessage(reply);

                if (!reply.HasToolCalls)
                {
                    // The server's reply-length limit cut it off, in its thinking or in its only
                    // call: it is told so and goes on, a few times in a row at most. Ended there, a
                    // run capped at 2,000 tokens stopped with nothing written. Its only call cut
                    // short comes before a message of the user's, which then follows the note.
                    if (cutOff && (cutCall || !HasIncoming) && ++cutOffs <= MaxCutOffs)
                    {
                        History.Add(ChatMessage.User(CutOffNote));
                        continue;
                    }
                    // The user wrote while the model answered: it goes on with that, as its next
                    // request when this answer finished the task.
                    if (HasIncoming)
                    {
                        turnEnded = !cutOff;
                        continue;
                    }
                    // A reply without tools ends the run: whether it looks finished isn't guessed.
                    // The Continue nudge that pushed on a reply ending in "Let me click Save." or
                    // ":" never fired in about 40 tasks on Qwen 3.8, Nemotron and Haiku (2026-10-27),
                    // and a nudge after an answer to a message that came in on top of the request
                    // made the model answer a second time what it had just said.
                    // Ending right after a click that changed nothing: Qwen 3.8 often talked itself
                    // into the click having worked ("the cursor is on it", "that may be normal").
                    // It is asked once a run to look again; a second end stands.
                    if (!missChecked && LastClickMissed())
                    {
                        missChecked = true;
                        observer.OnNotice("The last click changed nothing on screen; asked the model to check it before finishing.");
                        History.Add(ChatMessage.User(MissedEndNote));
                        continue;
                    }
                    // The turn ended on the plan's last step: the bar is done, without asking. Haiku 5.5 set now to 5
                    // of 5 and answered (that step was the answer), and the bar stood on its last step in accent, as
                    // a plan the run stopped short of; with work since the move it was asked aside where the plan
                    // stood, for "all done" and a line in the chat every time (2026-11-12). A question to the user
                    // leaves it, as does a run that neither moved the plan nor worked, and one told to carry on with
                    // that step (PlanLeftNote) that did nothing more: it waits for the user.
                    if ((planCarried ? planWork : planMoved || planWork) && toolContext.Plan is { Finished: false, At: > 0 } ending && ending.At == ending.Steps.Count - 1 && !EndsInQuestion(reply.Content))
                    {
                        toolContext.Plan = ending with { At = ending.Steps.Count };
                        toolContext.PlanChanged?.Invoke(toolContext.Plan);
                    }
                    // Ending right after the plan Sloperator asked for (PlanNote), with no work since the note: Haiku 5.5
                    // set its four steps, said "The plan bar is now set… Next I'll add the parameters" and ended
                    // there, the task not begun (2026-11-12). Told once to carry on; a question to the user stands.
                    // Only then: pushed on after any plan call, it answered a finished task a second time. And after
                    // the check above: asked for a plan when only the answer was left, Sonnet 5.5 set "Wait, Reply"
                    // on its second step and answered, was told to carry on, and answered again.
                    if (planNudged && !planPushed && planMoved && !planNudgeWork && toolContext.Plan is { Finished: false } going && !EndsInQuestion(reply.Content))
                    {
                        planPushed = true;
                        observer.OnNotice("The model stopped after setting its plan; asked it to carry on with the task.");
                        History.Add(ChatMessage.User(PlanGoOnNote(going)));
                        continue;
                    }
                    if (!planChecked && planWork && toolContext.Plan is { Finished: false })
                    {
                        planChecked = true;
                        AgentPlan? left;
                        try
                        {
                            left = await PlanBehindAsync(observer, carryOn: !planCarried && planMoved && !EndsInQuestion(reply.Content), ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            // The answer was complete: no note that the task was stopped.
                            observer.OnFinished(AgentStopReason.Cancelled, null);
                            return;
                        }
                        // By its own account steps are still to do: it carries on with them, once a run (see PlanLeftNote).
                        // What it does then may leave the plan behind again, which is asked about as before. Only for
                        // a plan this run set or moved: one left on the bar by a task the user stopped isn't this
                        // message's ("Commit it to a branch" would have sent it back to the check it was stopped in).
                        if (left is not null)
                        {
                            (planCarried, planChecked, planWork) = (true, false, false);
                            History.Add(ChatMessage.User(PlanLeftNote(left)));
                            continue;
                        }
                    }
                    // Prices to look up, once the chat is long and a reply came with no cost from the
                    // server (whose own cost would win): the side task comes after the answer, and the
                    // run ends after the side task.
                    if (!pricesLooked && PriceLookupDue() is { } lookup)
                    {
                        pricesLooked = true;
                        try
                        {
                            await LookUpPricesAsync(lookup, observer, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            // The answer was complete: no note that the task was stopped.
                            observer.OnFinished(AgentStopReason.Cancelled, null);
                            return;
                        }
                        // The user wrote meanwhile: the answer ended the turn, and theirs is next.
                        if (HasIncoming)
                        {
                            turnEnded = true;
                            continue;
                        }
                    }
                    // Compact context, picked as the model wrote this answer: before the end.
                    if (ResetDue())
                        await ResetContextAsync(observer, ct, asked: true).ConfigureAwait(false);
                    observer.OnFinished(AgentStopReason.Completed, null);
                    return;
                }
                if (!cutCall)
                    cutOffs = 0; // it is moving again; a later cut gets its own notes

                // Only the last screenshot goes to the model: the tools' results come before it.
                ChatMessage? shown = null;
                ContentPart? screenshot = null;
                string? caption = null;
                ChatMessage? last = null;
                // This reply's calls were decided on the screen in the last screenshot.
                toolContext.Seen = toolContext.LastShot;
                toolContext.UserPressesSeen = toolContext.LastShot?.UserPresses;
                toolContext.ClickedAtPresses = null;
                toolContext.ScreenStale = false;
                var called = reply.ToolCalls!.Select(c => ToolCallRepair.ResolveTool(c.Name, tools)).ToList();
                guesswork?.Read(reply, called);
                var notes = new List<string>();
                using var calls = new StepScope(this, ct);
                // Until the next request goes out: calls that take long (a wait, a build) outlast the server's prompt cache.
                await using var warm = new CacheWarmer(stop => KeepCacheWarmAsync(request, reply, sentAt, observer, stop), ct);
                foreach (var call in reply.ToolCalls!)
                {
                    try { await HoldWhileTypingAsync(calls.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (calls.Interrupted(ct)) { }
                    // Interrupt: the rest of the reply's calls give way to the user's message.
                    if (calls.Interrupted(ct))
                    {
                        History.Add(last = ChatMessage.ToolResult(call.Id, CutShort(ran: false)));
                        continue;
                    }
                    var result = await RunToolAsync(call, observer, ct, calls.Token).ConfigureAwait(false);
                    // Work since the plan last moved: the plan may have fallen behind it (see PlanBehindAsync).
                    if (ToolCallRepair.ResolveTool(call.Name, tools) is not PlanTool)
                        (planWork, planNudgeWork) = (true, planNudged);
                    else if (!result.IsError)
                        (planWork, planMoved) = (false, true);
                    // A call Interrupt stopped failed for no fault of the model's.
                    // Each note the model is sent shows in the chat as a line of its own, as the
                    // user asked: what Sloperator tells the model is no secret.
                    if (!calls.Interrupted(ct) && loops.Check(call, result) is { } warning)
                    {
                        notes.Add("[Sloperator] " + warning);
                        observer.OnNotice("Told the model it seems to be going in circles and should try another way.");
                    }
                    if (!calls.Interrupted(ct) && guesswork?.Check(ToolCallRepair.ResolveTool(call.Name, tools), result, call.Arguments) is { } lookItUp)
                    {
                        notes.Add(lookItUp);
                        observer.OnNotice(Guesswork.NoticeFor(lookItUp));
                    }
                    if (_projects.Check(ToolCallRepair.ResolveTool(call.Name, tools), call.Arguments, result, toolContext, tools.Find("index_folder") is not null) is { } project)
                    {
                        notes.Add(project);
                        var root = ProjectNudge.RootIn(project);
                        observer.OnNotice((ProjectNudge.AsksToIndex(project), ProjectNudge.GivesNotes(project)) switch
                        {
                            (true, true) => $"Asked the model to index {root} before going through its files, and gave it the project's notes for AI agents.",
                            (true, false) => $"Asked the model to index {root} before going through its files.",
                            _ => $"Gave the model the notes for AI agents in {root}.",
                        });
                    }
                    // A model that can't see gets no picture (an MCP tool's, say): its server
                    // refused that request and every one after it, since the conversation keeps it.
                    if (result.Image is not null && !toolContext.SeesImages)
                        result = result with { Image = null, Text = result.Text + "\n(A picture came with this, left out: the model in use can't see images.)" };
                    var message = last = ChatMessage.ToolResult(call.Id, result.Text);
                    History.Add(message);
                    if (result.Image is not null)
                        (shown, screenshot, caption) = (message, ContentPart.FromImage(result.Image, result.ImageMime), result.ImageCaption);
                }
                // The whole calls ran; the cut one is said after their results.
                if (cutCall && last is not null)
                    notes.Add(CutToolCallNote);
                // The task begun with nothing on the user's plan bar: asked for a plan, once a run (see PlanNote).
                // A run that kept one from the start, or that has no bar to show it on, is never asked.
                if (!planAsked && (called.Any(t => t is PlanTool) || tools.Find(PlanTool.ToolName) is null))
                    planAsked = true;
                // One note at a time: with another to answer first (index the project, look an error up), this one waits a step.
                else if (!planAsked && toolContext.Plan is not { Finished: false } && ++planSteps >= PlanAfterSteps && notes.Count == 0 && !calls.Interrupted(ct))
                {
                    planAsked = planNudged = true;
                    notes.Add(PlanNote);
                    observer.OnNotice("Asked the model to show its plan on the bar above the message box, if more than one thing is left to do.");
                }

                // Most OpenAI-compatible servers only accept images in user messages,
                // so the screenshot (or a picture read_file opened) is delivered right after the tool results.
                if (screenshot is not null)
                {
                    shown!.Content += caption is null ? "\n(The screenshot is attached in the next message.)" : "\n(The picture is attached in the next message.)";
                    History.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText(caption ?? ScreenshotText), screenshot] });
                }
                // Sloperator's notes come in a message of their own, after the results: inside a
                // tool result, where text from a web page or a file could have put them, Claude
                // Haiku took one for an injection and went on without it.
                if (notes.Count > 0)
                    History.Add(ChatMessage.User(string.Join("\n", notes)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Said in the conversation: stopped in Paint and given a story to write next, the model
            // wrote it and then "returned to the Paint task, which is still incomplete".
            History.Add(ChatMessage.User(toolContext.Plan is { Finished: false } stoppedAt ? StoppedNote + StoppedPlanNote(stoppedAt) : StoppedNote));
            observer.OnFinished(AgentStopReason.Cancelled, null);
        }
        catch (Exception ex)
        {
            if (ex is not LlmException)
                observer.OnFailed(ex);
            observer.OnFinished(AgentStopReason.Error, ex.Message, ex);
        }
        finally
        {
            RepairDanglingToolCalls();
            // What tools left to do once the task is over (ComfyUI letting go of its models), a
            // few seconds at most: a server that went away must not hold up the end of the run.
            try { await toolContext.FinishRunAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
    }

    /// <summary>
    /// Auto pricing lookup's side task on <paramref name="lookup"/>, out of the chat's context: with web_search,
    /// fetch_page and set_api_pricing only, done once the prices are kept, and with no output, since it only
    /// keeps them. Its own side_task card. Asked in the conversation itself, the model wrote its answer anew
    /// after the lookup, Haiku 5.5 with the prices in it and a line made up on top (2026-11-02).
    /// </summary>
    private async Task LookUpPricesAsync(string lookup, IAgentObserver observer, CancellationToken ct)
    {
        var task = $"Look up the prices of {requestOptions.Model}";
        var card = new ToolCall
        {
            Id = SideTaskTool.AfterAnswerId + Guid.NewGuid().ToString("N")[..8],
            Name = SideTaskTool.ToolName,
            Arguments = new JsonObject { ["task"] = task, ["prompt"] = lookup, ["output"] = false }.ToJsonString(),
        };
        observer.OnToolStarted(card, task);
        var looked = await RunSideTaskAsync(lookup, false, ["web_search", "fetch_page", "set_api_pricing"], t => t is SetApiPricingTool,
            text => observer.OnToolProgress(card, text), observer, ct).ConfigureAwait(false);
        observer.OnToolFinished(card, looked);
    }

    /// <summary>How many dropped connections one step survives.</summary>
    internal const int MaxReconnects = 12;

    /// <summary>How many looping replies one step survives before the run ends with an error.</summary>
    private const int MaxRepetitionRetries = 3;

    internal const string LongToolCallNote =
        "[Sloperator] Your last reply was lost: the connection drops when a single tool call takes more than about a minute to write. " +
        "Keep every tool call short. Write long files in parts of at most about 100 lines: create the file with write_file, " +
        "then add each next part with write_file and append set to true. Continue with the task.";
    internal const string StillTooLongNote =
        "[Sloperator] Your reply was lost again for the same reason: that part was still too long to write in under a minute. " +
        "Make each part much smaller, at most about 40 lines (about 1,500 characters), and add the rest with further " +
        "write_file calls with append set to true. Continue with the task.";

    /// <summary>How many replies in a row the server's content filter may stop before the run ends on it.</summary>
    internal const int MaxOutputBlocks = 2;

    /// <summary>
    /// Said to the model when the server's content filter stopped its reply (<see cref="OutputBlockedException"/>):
    /// the same reply again would be stopped again, another way of saying it gets through.
    /// </summary>
    internal const string OutputBlockedNote =
        "[Sloperator] The server's content filter stopped your last reply before it arrived, so nothing in it was done. " +
        "Give it again another way: in shorter steps, and without long runs of the same words or items in one call. Continue with the task.";

    // Marked as Sloperator's: taken for the user's words, "continue" read as a go-ahead, and the
    // model asked again for a command the user had just declined twice.
    /// <summary>
    /// Said with the last result of a reply whose other calls ran when the server's length limit
    /// cut one short, its arguments half written (see <see cref="DropCutCalls"/>).
    /// </summary>
    internal const string CutToolCallNote =
        "[Sloperator] Your last tool call was cut off by the server's limit on how long a reply may be, before its arguments were complete, " +
        "so it was not run. Write long content in parts of at most about 100 lines: create the file with write_file, then add each next part " +
        "with write_file and append set to true. Continue with the task.";

    /// <summary>A reply the server's length limit cut off before it made a call, in its thinking or its only call.</summary>
    internal const string CutOffNote =
        "[Sloperator] Your last reply was cut off by the server's limit on how long a reply may be, so nothing in it was run. " +
        "Write long content in parts of at most about 100 lines: create the file with write_file, then add each next part " +
        "with write_file and append set to true. Continue with the task.";

    /// <summary>How many replies in a row the length limit may cut off before the run ends.</summary>
    private const int MaxCutOffs = 3;

    /// <summary>A reply that ends the task right after a click that changed nothing on screen.</summary>
    internal const string MissedEndNote =
        "[Sloperator] Your last click changed nothing on screen, so it probably missed, yet you ended there. Zoom in on what you meant to click: " +
        "if it isn't selected or changed, click it again; if it already was, say so.";

    /// <summary>
    /// How long after a request the same prompt is sent again while the step's tool calls still run, so that the
    /// server's prompt cache, which it keeps for five minutes from its last use, still holds the chat for the next step.
    /// </summary>
    internal TimeSpan KeepWarmEvery { get; set; } = TimeSpan.FromSeconds(270);

    /// <summary>
    /// How many times a step's prompt is read again at most. Reading costs about a tenth of the prompt's price and
    /// writing it anew one and a quarter: six reads (a wait of up to 32 minutes) are half of the write they save,
    /// and a call that outlasts them pays that half on top.
    /// </summary>
    internal const int KeepWarmTimes = 6;

    /// <summary>Whether a reply of this session's server said it wrote to a prompt cache: there a lapse is paid for.</summary>
    private bool _writesCache;

    private long _cacheUsed;

    /// <summary>
    /// When the server last had the chat's prompt in hand in this session: its latest reply to a step, or the
    /// latest read that kept the cache warm (<see cref="KeepCacheWarmAsync"/>). Null before the first. Auto counts
    /// the cache's lifetime from it (<see cref="AutoPick.CacheLapsed"/>). Taken when the answer has come, the latest
    /// the server can count from, so that a cache said to have lapsed has.
    /// </summary>
    public DateTime? CacheUsedAt => Interlocked.Read(ref _cacheUsed) is > 0 and var ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;

    private void CacheUsed() => Interlocked.Exchange(ref _cacheUsed, DateTime.UtcNow.Ticks);

    /// <summary>
    /// Keeps the server's prompt cache from lapsing while a step's tool calls run (2026-11-12). A cache such as
    /// Anthropic's holds a prompt for five minutes after its last use; a step that comes later pays to have the
    /// whole chat written to it again, at more than its full input price. In the user's Unity chat the model
    /// waited 420 and 480 seconds for a download, and each next step wrote 103k and 105k tokens anew (about $0.26
    /// each on Sonnet 5.5) where reading them costs $0.01. So while the calls run, the request the model just
    /// answered is sent again every <see cref="KeepWarmEvery"/>, counted from when it was first sent, and dropped
    /// at the first sign of an answer: the server has read its cache by then, which is all that was wanted. At
    /// most <see cref="KeepWarmTimes"/> times a step. Only where a reply said it wrote to a cache
    /// (<see cref="ChatMessage.CacheWriteTokens"/>): a server that reports none (a local one, whose cache doesn't
    /// lapse) is left alone, whichever it is. The reads are counted in the chat's spending as read tokens, and
    /// the chat says so once a step.
    /// </summary>
    private async Task KeepCacheWarmAsync(List<ChatMessage> request, ChatMessage reply, DateTime sentAt, IAgentObserver observer, CancellationToken ct)
    {
        _writesCache |= reply.CacheWriteTokens is > 0;
        if (!_writesCache || reply.PromptTokens is not { } prompt)
            return;
        var next = sentAt + KeepWarmEvery;
        for (var read = 0; read < KeepWarmTimes; read++)
        {
            if (next - DateTime.UtcNow is var wait && wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);
            next = DateTime.UtcNow + KeepWarmEvery;
            using var asked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                await client.CompleteAsync(request, tools.Definitions(), StepOptions(), _ => asked.Cancel(), asked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Dropped as its answer began: what it read is counted, as the server counts it.
                client.Spending?.Add(client.BaseUrl, requestOptions.Model, new ChatMessage { Role = ChatRole.Assistant, PromptTokens = prompt, CachedTokens = prompt, CompletionTokens = 0 });
                CacheUsed();
            }
            catch (Exception ex) when (ex is LlmException or HttpRequestException or IOException)
            {
                return; // the step's own request says what is wrong, if it still is
            }
            if (read == 0)
                observer.OnNotice("Kept this chat in the server's prompt cache, which lapses after 5 minutes, so the next step doesn't pay for all of it again.");
        }
    }

    /// <summary>Runs <see cref="KeepCacheWarmAsync"/> beside a step's tool calls and ends it with them.</summary>
    private sealed class CacheWarmer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly Task _reads;

        public CacheWarmer(Func<CancellationToken, Task> reads, CancellationToken ct)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _reads = Task.Run(() => reads(_stop.Token));
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            // Whatever went wrong with a read, the run it was made for goes on: it only saves money.
            try { await _reads.ConfigureAwait(false); }
            catch (Exception) { }
            _stop.Dispose();
        }
    }

    /// <summary>
    /// How many steps (replies with tool calls) a run makes with no plan on the bar before the model is asked for one.
    /// One: its first look at the task. At three, Haiku 5.5 and Sonnet 5.5, which read every file in one step and
    /// write them all in the next, were asked when little was left: the plan showed for the last five seconds.
    /// </summary>
    internal const int PlanAfterSteps = 1;

    /// <summary>
    /// What the model is told once a run when it has begun a task with no plan on the user's bar (the user,
    /// 2026-11-12: "I have yet to see a single plan built by the thing. User loves feedback and readable
    /// non-congested visual progress, so we have to nudge the plan-building somehow"). The prompt's line on
    /// keeping a plan had not been enough: their Unity chat on Sonnet 5.5 made 79 replies and over a hundred calls
    /// without one plan call, and Haiku 5.5 made none in a task of nine calls. Said after the first step's results
    /// like every note, the same to every model; a run that set a plan itself, or goes on with one that isn't
    /// finished, is never told. It asks for few, plain steps: the bar is for reading at a glance.
    /// </summary>
    internal const string PlanNote =
        "[Sloperator] You have started on this task and the user's plan bar is empty, so they can't see how far along you are. " +
        "If more than one thing is still to be made or changed, or the work ahead is long, call plan now: the whole task as a handful of steps in order, any already done too, " +
        "one plain word each, with now set to the step you are on. Then go straight on with the task, moving the plan on as you start each step. " +
        "If only one thing and its check are left, or just the answer, make no plan: a plan of two steps tells the user nothing. " +
        "Either way write nothing in answer to this note: no word on the plan, on what is left or on skipping it. The user doesn't see this note, and the bar shows them the plan.";

    /// <summary>
    /// What the model is told, once a run, when it ends its turn right after the plan <see cref="PlanNote"/> asked
    /// for, with nothing done since and the plan short of its end: the first wording of that note had Haiku 5.5 set
    /// a plan, announce it ("The plan bar is now set with four steps… Next I'll add the parameters") and stop, where
    /// the build before it had finished the task. Not after a reply that ends in a question to the user.
    /// </summary>
    internal static string PlanGoOnNote(AgentPlan plan) =>
        $"[Sloperator] The plan is on the bar at step {plan.At + 1} of {plan.Steps.Count}, \"{plan.Steps[plan.At].Word}\", but you ended your turn with that step not done. " +
        "The user sees the plan and needs no word on it. Carry on with the task now, and end your turn only when it is finished or you need something from the user.";

    /// <summary>
    /// Whether a reply ends in a question, as one put to the user does: its last character (closing quotes, brackets
    /// and emphasis aside), or a question in its last paragraph with a sentence after it. Haiku 5.5 ended "Do you
    /// want me to install Python (with openpyxl) so the chart can be added? If you'd rather not install anything,
    /// I'll make the workbook without the chart. Tell me which you prefer." and was told to carry on with its plan
    /// (2026-11-12). A "?" inside an address or a pattern (one with no space after it) is no question.
    /// </summary>
    /// <remarks>
    /// Or a last paragraph that waits for the user's answer, after a question put further up with the choices under
    /// it: Haiku 5.5 asked "How should I name them?", listed three ways, and ended "Once you choose, I'll resize them…";
    /// told to carry on with its plan, it resized the photos before the user had chosen (2026-11-13).
    /// </remarks>
    internal static bool EndsInQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var said = text.Replace("\r", "").TrimEnd();
        var last = said[(said.LastIndexOf("\n\n", StringComparison.Ordinal) + 1)..];
        return said.TrimEnd('*', '_', ')', '"', '\'', '`', (char)0x201D).EndsWith('?') ||
               AsksInside().IsMatch(last) ||
               AwaitsAnswer().IsMatch(last) && AsksInside().IsMatch(said + " ");
    }

    [GeneratedRegex(@"\?(?=[\s""')*_”])")]
    private static partial Regex AsksInside();

    // "Once you choose, I'll…", "Let me know which", "Tell me how", "Pick one and I'll…", "I'll wait for your answer"
    [GeneratedRegex(@"\b(?:once|when|after|as\s+soon\s+as|if)\s+you(?:'ve|\s+have)?\s+(?:choose|chosen|chose|pick(?:ed)?|decide(?:d)?|confirm(?:ed)?|tell\s+me|told\s+me|let\s+me\s+know|reply|replied|answer(?:ed)?|say|said)\b|\blet\s+me\s+know\s+(?:which|what|how|whether|where|when|your|and\s+I(?:'ll|\s+will))\b|\btell\s+me\s+(?:which|what|how|whether|if|where|when)\b|\b(?:say|choose|pick)\s+(?:which|one)\b|\bwait(?:ing)?\s+(?:on|for)\s+(?:you|your)\b|\bwhich\s+(?:one\s+)?(?:do\s+you|would\s+you|should\s+i)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AwaitsAnswer();

    /// <summary>What the model is asked, aside, when its plan fell behind its work (see <see cref="PlanBehindAsync"/>).</summary>
    internal static string PlanBehindNote(AgentPlan plan) =>
        $"[Sloperator] Your plan on the user's bar still says step {plan.At + 1} of {plan.Steps.Count}, \"{plan.Steps[plan.At].Word}\", " +
        "though you went on working and then ended your turn. Call plan with now set to where the work stands " +
        $"({plan.Steps.Count + 1} when all of it is done). Call nothing else and write nothing: your answer above stands.";

    /// <summary>
    /// The plan on the user's bar left behind its work (the user's pick, 2026-11-09): Sonnet 5.5 set a plan of four
    /// steps, did all four without moving it, and answered, and the bar said "Module", the first, for good. When a run
    /// ends short of its plan's end with work done since the plan last moved, the model is asked once where the plan
    /// stands, aside: the conversation and <see cref="PlanBehindNote"/> go as one request with the same tools (so
    /// the server's cache holds), only its plan calls run, and neither the note nor the reply joins the
    /// conversation or the chat, so the answer stays the run's last word (a reply to a note in the chat became the
    /// reply the run folded under). A plan set and then asked about ("does this suit you?") had no work after it.
    /// </summary>
    /// <summary>
    /// Whether a notice is <see cref="PlanBehindAsync"/>'s with nothing left to explain: the plan all done, or taken
    /// away. The chat folds such a line with the run's work, as it folds the lines sent mid-run: with a plan in every
    /// task, "The plan was left at step 2 of 4; asked the model where it stands: all done." stood after the answer
    /// whenever Sonnet 5.5 did several steps in one go (2026-11-12). One that leaves steps to do says why the bar
    /// isn't done, and stays in sight.
    /// </summary>
    public static bool IsPlanSettledNotice(string text) =>
        text.StartsWith("The plan was left at step ", StringComparison.Ordinal) &&
        (text.EndsWith(": all done.", StringComparison.Ordinal) || text.EndsWith(": it took the plan away.", StringComparison.Ordinal));

    /// <param name="carryOn">Whether the run may be told to carry on with what the model says is left.</param>
    /// <returns>
    /// The plan as the model set it just now, when by that steps are still to do and <paramref name="carryOn"/>
    /// allows telling it to do them (<see cref="PlanLeftNote"/>); else null. A plan it didn't touch says nothing either way.
    /// </returns>
    private async Task<AgentPlan?> PlanBehindAsync(IAgentObserver observer, bool carryOn, CancellationToken ct)
    {
        if (toolContext.Plan is not { Finished: false } before)
            return null;
        var request = new List<ChatMessage>(History.Count + 2) { ChatMessage.System(systemPrompt) };
        request.AddRange(History);
        request.Add(ChatMessage.User(PlanBehindNote(before)));
        ChatMessage reply;
        try
        {
            reply = await CompleteWithReconnectAsync(request, tools.Definitions(), StepOptions(), observer, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // the answer stands, and the plan as it was
        }
        foreach (var call in reply.ToolCalls ?? [])
            if (ToolCallRepair.ResolveTool(call.Name, tools) is PlanTool plan && ToolCallRepair.AsObject(call.Arguments) is { } args)
            {
                try { await plan.ExecuteAsync(args, toolContext, ct).ConfigureAwait(false); }
                catch (ToolArgumentException) { }
            }
        var was = $"The plan was left at step {before.At + 1} of {before.Steps.Count}; asked the model where it stands";
        var left = carryOn && toolContext.Plan is { Finished: false } set && !ReferenceEquals(set, before) ? set : null;
        observer.OnNotice(toolContext.Plan switch
        {
            null => $"{was}: it took the plan away.",
            { Finished: true } => $"{was}: all done.",
            { } now when left is not null => $"{was}: step {now.At + 1}, \"{now.Steps[now.At].Word}\", is still to do. Asked it to carry on.",
            { } now when !ReferenceEquals(now, before) => $"{was}: step {now.At + 1}, \"{now.Steps[now.At].Word}\".",
            _ => $"{was}: it left it there.",
        });
        return left;
    }

    /// <summary>
    /// What the model is told, once a run, when it answered with steps of its plan still to do, by its own account
    /// (asked aside where the plan stands, it set an unfinished step): to carry on with them. In the user's Unity
    /// chat Sonnet 5.5 edited the prefab and answered "I haven't looked at the result in Unity… please open a chat
    /// message in the editor to check it", with Unity open in front of it; the user had to ask three times for the
    /// check (2026-11-12). The prompt's "a check you can do yourself is part of the task" hadn't held, and whether an
    /// answer looks finished isn't guessed from its words: the plan says it. Not when the answer ends in a question
    /// to the user, nor when the plan was left untouched.
    /// </summary>
    internal static string PlanLeftNote(AgentPlan plan) =>
        $"[Sloperator] By your own plan the task isn't finished: step {plan.At + 1} of {plan.Steps.Count}, \"{plan.Steps[plan.At].Word}\", is still to do" +
        (plan.At + 1 < plan.Steps.Count ? $", and {plan.Steps.Count - plan.At - 1} more after it" : "") + ". " +
        "Carry on with it now rather than leave it to the user. If it can't be done from here, tell the user what stops you; " +
        "if you are waiting for their answer, say nothing more.";

    /// <summary>Whether the tool results since the model's previous reply include a click that changed nothing.</summary>
    private bool LastClickMissed()
    {
        for (var i = History.Count - 2; i >= 0; i--)
        {
            var m = History[i];
            if (m.Role == ChatRole.Assistant)
                return false;
            if (m.Role == ChatRole.Tool && (m.Content?.Contains(ClickCheck.MissedNote, StringComparison.Ordinal) == true ||
                                            m.Parts?.Any(p => p.Text?.Contains(ClickCheck.MissedNote, StringComparison.Ordinal) == true) == true))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Takes the tool calls whose arguments aren't whole JSON out of a reply the length limit
    /// ended; true when there was one. Repaired by closing what was left open, such a call ran:
    /// write_file wrote half the file, and edit_file put half its text in, and both said done.
    /// </summary>
    internal static bool DropCutCalls(ChatMessage reply)
    {
        if (reply.ToolCalls is not { Count: > 0 } calls)
            return false;
        var whole = calls.Where(c => ToolCallRepair.IsWholeJson(c.Arguments)).ToList();
        if (whole.Count == calls.Count)
            return false;
        reply.ToolCalls = whole.Count > 0 ? whole : null;
        return true;
    }

    /// <summary>Where the user stopped a run (Stop, Pause/Break), so what they write next is read in that light (see <see cref="SystemPrompt"/>).</summary>
    internal const string StoppedNote = "[Sloperator] The user stopped you here.";

    /// <summary>
    /// Said with <see cref="StoppedNote"/> when the stopped run's plan isn't finished. The plan stays on the bar, the
    /// step it was stopped on in accent, until the user's next message, and leaves it then: that message may be
    /// another task, and a run that goes on with an unfinished plan is never asked for one. Stopped in a task of four
    /// steps and asked for a haiku, Sonnet 5.5 wrote it with "Script, README, Tests, Run" still on the bar at its
    /// first step, where it would have stayed (2026-11-12). Told to take the plan away itself in that case, it wrote
    /// the haiku and left the plan: an answer needs no call. Whether the next message goes on with the work isn't
    /// guessed from its words: a run that does is asked for its plan like any run with an empty bar (PlanNote).
    /// </summary>
    internal static string StoppedPlanNote(AgentPlan plan) =>
        $" Your plan, at step {plan.At + 1} of {plan.Steps.Count}, \"{plan.Steps[plan.At].Word}\", leaves their bar with their next message. " +
        "If that message has you go on with this work, set the plan again, with now at the step the work stands on.";

    /// <summary>Said above a screenshot a tool took; any other picture a tool gives comes with its own caption.</summary>
    internal const string ScreenshotText = "Screenshot after the last action:";

    // Not "try another approach": told that after a declined listing command, the model asked for
    // it twice more in other words and then listed the folder with another tool.
    internal const string DeclinedText =
        "The user declined this action. Don't do the same thing another way; ask them what they would like instead, or go on with the parts of the task that don't need it.";

    /// <summary>
    /// Whether a reply cut off by the length limit ran out of context, rather than into a
    /// reply-length cap set on the server; without a token count it is taken as out of context.
    /// </summary>
    private bool OutOfRoom(ChatMessage reply) =>
        reply.PromptTokens is not { } prompt || prompt + (reply.CompletionTokens ?? 0) >= contextTokens * 9L / 10;

    /// <summary>
    /// The reply limit every step asks for (max_tokens): what the context has left, and no more than
    /// Claude's API is asked for (<see cref="AnthropicMessages.MaxReply"/>). Asked for none, some
    /// servers set a small one of their own: proxyapi.ru's LiteLLM gives Claude 4k or so, which cut
    /// eight replies in a quarter of an hour of writing code (chat 20261101-171229-f4b4fa), each
    /// thought through again. A high one costs nothing: only what is written is billed, and
    /// OpenRouter holds a fixed amount of credit for a request with none. The model's own limit, from
    /// the server's model list or a refusal, caps it (see <see cref="OpenAiChatClient"/>).
    /// </summary>
    private int ReplyRoom() =>
        Math.Clamp(contextTokens > 0 ? contextTokens - TokensInUse() - 2_048 : AnthropicMessages.MaxReply, 4_096, AnthropicMessages.MaxReply);

    /// <summary>The options of the step's request, asking for <see cref="ReplyRoom"/>.</summary>
    private ChatRequestOptions StepOptions() => requestOptions.MaxTokens is not null ? requestOptions : new ChatRequestOptions
    {
        Model = requestOptions.Model,
        MaxTokens = ReplyRoom(),
        Extra = requestOptions.Extra,
        SendReasoning = requestOptions.SendReasoning,
        Effort = requestOptions.Effort,
    };

    internal static bool LooksFinished(string? content) => content is { Length: > 0 } && CompletionClaim().IsMatch(content);

    [GeneratedRegex(@"\b((?<!\b(whether|if|once|when)\s+(the\s+)?)task (is|has been) (now )?(complete|completed|done|finished)|i('ve| have) (successfully )?(completed|finished) (the|your|this) task|everything is done|all done)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompletionClaim();

    /// <summary>Guesswork seen in this chat (see <see cref="Guesswork"/>); null without web_search.</summary>
    private Guesswork? _guesswork;

    /// <summary>The projects indexed in this chat, and those the model was told to index (see <see cref="ProjectNudge"/>).</summary>
    private readonly ProjectNudge _projects = new();

    /// <param name="ct">The run's: Stop and Pause/Break.</param>
    /// <param name="step">Also cancelled by <see cref="Interrupt"/>, which stops the call and leaves the run going.</param>
    private async Task<ToolResult> RunToolAsync(ToolCall call, IAgentObserver observer, CancellationToken ct, CancellationToken step)
    {
        var tool = ToolCallRepair.ResolveTool(call.Name, tools);
        if (tool is null)
        {
            var unknown = ToolResult.Error($"Unknown tool '{call.Name}'. Available: {string.Join(", ", tools.All.Select(t => t.Name))}.");
            observer.OnToolStarted(call, call.Name);
            observer.OnToolFinished(call, unknown);
            return unknown;
        }

        JsonObject args;
        string summary;
        try
        {
            (args, summary) = PrepareCall(call, tool);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Arguments the tool can't take are the model's to fix, as whatever goes wrong while it runs:
            // anything else thrown here ended the whole run.
            var bad = ToolResult.Error(ArgumentProblem(ex, tool));
            observer.OnToolStarted(call, call.Name);
            observer.OnToolFinished(call, bad);
            return bad;
        }

        if (NeedsApproval(tool) && !await observer.RequestApprovalAsync(call, tool, summary, step).ConfigureAwait(false))
        {
            // A stop while the call waited for an OK is no answer to it. Taken for a no, it went into
            // the conversation as declined, and after Continue the model asked what to do instead.
            ct.ThrowIfCancellationRequested();
            var denied = step.IsCancellationRequested ? ToolResult.Error(CutShort(ran: false)) : ToolResult.Error(DeclinedText);
            observer.OnToolFinished(call, denied);
            return denied;
        }

        observer.OnToolStarted(call, summary);
        ToolResult result;
        toolContext.Progress = text => observer.OnToolProgress(call, text);
        // The file it writes is read first, the folder a command runs in watched: the File changes panel shows what changed.
        var changing = toolContext.Changes?.Starting(tool, args, toolContext);
        try
        {
            result = await tool.ExecuteAsync(args, toolContext, step).ConfigureAwait(false);
        }
        catch (ToolArgumentException ex)
        {
            result = ToolResult.Error(ArgumentProblem(ex, tool));
        }
        catch (OperationCanceledException) when (step.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            result = ToolResult.Error(CutShort(ran: true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Whatever goes wrong in one tool (a timeout, a file in use, a bug) is the model's to
            // work around; only Stop ends the run.
            result = ToolResult.Error(ex.Message);
        }
        finally
        {
            toolContext.Progress = null;
            if (changing is not null)
                await changing.EndAsync().ConfigureAwait(false);
        }
        observer.OnToolFinished(call, result);
        return result;
    }

    /// <summary>A call's arguments, repaired and normalized for <paramref name="tool"/>, and its card's line; throws on arguments the tool can't take.</summary>
    private static (JsonObject Args, string Summary) PrepareCall(ToolCall call, Tool tool)
    {
        var args = ToolCallRepair.ParseArguments(call.Arguments, tool);
        // The conversation keeps the call as repaired: sent back as it came, arguments that
        // aren't JSON can make a server's chat template fail every request after it.
        if (!ToolCallRepair.IsWholeJson(call.Arguments))
            call.Arguments = args.ToJsonString(Comfy.ComfyWorkflow.Compact);
        ToolCallRepair.ApplyNameHints(call.Name, args, tool);
        ToolCallRepair.NormalizeArguments(args, tool);
        return (args, tool.Describe(args));
    }

    /// <summary>What the model is told of arguments <paramref name="tool"/> couldn't take: with its parameters when they were wrong.</summary>
    private static string ArgumentProblem(Exception ex, Tool tool) =>
        ex is ToolArgumentException ? $"{ex.Message} Parameters for {tool.Name}: {tool.Parameters.ToJsonString()}" : ex.Message;

    /// <summary>The system prompt from the next request on (an MCP server added mid-run, which the prompt lists).</summary>
    internal void UsePrompt(string prompt) => systemPrompt = prompt;

    /// <summary>Called once <see cref="ToolContext.MaxScreenshotSide"/> changed mid-run, for the prompt to say the screenshots' new size.</summary>
    internal Action? ScreenshotsResized { get; set; }

    /// <summary>Whether the model takes pictures as far as this run found: false once its server refused them, so the next runs are set up without.</summary>
    public bool SeesImages => toolContext.SeesImages;

    private bool HasImages() => History.Any(m => m.Parts?.Any(p => p.IsImage) == true);

    /// <summary>Puts each picture in the conversation in words: the model can't be shown them.</summary>
    private void DropImages()
    {
        foreach (var m in History.Where(m => m.Parts?.Any(p => p.IsImage) == true))
        {
            m.Content = string.Join("\n", m.Parts!.Select(p => p.IsImage ? "(A picture, left out: the model in use can't see images.)" : p.Text));
            m.Parts = null;
        }
    }

    internal const string OldScreenshotText = "(An earlier screenshot, left out: the server takes only so many pictures in one request.)";

    /// <summary>A screenshot Sloperator took after a step, or the screen a context reset carried; not a picture the user or a tool gave.</summary>
    private static bool IsScreenshot(ChatMessage m) =>
        !m.FromUser && m.Parts is [{ Text: { } said }, { IsImage: true }] && (said == ScreenshotText || said.StartsWith(CurrentScreenText, StringComparison.Ordinal));

    /// <summary>
    /// Over <see cref="MaxImages"/>, every screenshot but the latest is put in words at once; the
    /// pictures tools and the user gave stay (a ComfyUI result, a reference). Only if they alone are
    /// still too many, the oldest of them go too, the user's last. All at once, not one a step: the
    /// requests after keep the same start, which the server has cached, until the cap is reached
    /// again, and Claude's thinking, bound to what came before it, is dropped that rarely too.
    /// </summary>
    private void LeaveOutOldImages()
    {
        List<(ChatMessage m, int i)> Images() =>
            [.. History.Where(m => m.Parts is not null).SelectMany(m => m.Parts!.Select((p, i) => (m, i, p))).Where(x => x.p.IsImage).Select(x => (x.m, x.i))];
        if (MaxImages == 0 || Images().Count <= MaxImages)
            return;
        foreach (var m in History.Where(IsScreenshot).SkipLast(1))
            m.Parts = [m.Parts![0], ContentPart.FromText(OldScreenshotText)];
        var images = Images();
        if (images.Count <= MaxImages)
            return;
        var leftOut = ContentPart.FromText($"(An earlier picture, left out: the server takes {MaxImages} in one request.)");
        foreach (var (m, i) in images.SkipLast(1).OrderBy(x => x.m.FromUser).Take(images.Count - MaxImages))
            m.Parts = [.. m.Parts!.Select((p, j) => j == i ? leftOut : p)];
    }

    private bool NeedsApproval(Tool tool) => toolContext.Settings.Approval switch
    {
        ApprovalMode.AskAll => tool.Risk != ToolRisk.ReadOnly,
        ApprovalMode.AskRisky => tool.Risk == ToolRisk.Risky,
        _ => false,
    };

    /// <summary>
    /// Sends the request; if the server says it does not fit, resets the context and tries again
    /// (twice at most) before giving up.
    /// </summary>
    /// <param name="replying">The run's token, or also cancelled by <see cref="Interrupt"/>: the reply and the waits to send it again. A reset goes by <paramref name="ct"/>.</param>
    private async Task<ChatMessage> CompleteWithResetAsync(List<ChatMessage> request, IAgentObserver observer, CancellationToken ct, CancellationToken replying)
    {
        var repeats = 0;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var guard = new RepetitionGuard(observer.OnDelta);
                return await CompleteWithReconnectAsync(request, tools.Definitions(), StepOptions(), observer, guard.OnDelta, replying).ConfigureAwait(false);
            }
            catch (RepetitionException) when (++repeats <= MaxRepetitionRetries)
            {
                // Dropping the stream stops the server too. The looping reply is thrown away and
                // the same request sent again, with nothing added to it: the note that told the
                // model it had looped never fired in about 40 tasks on three models (2026-10-27).
                observer.OnNotice("The model's thinking started repeating itself; cut it off and asked again.");
                observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "" });
                observer.OnStepStarted(0);
                attempt--;
            }
            catch (TooManyImagesException ex) when (OpenAiChatClient.ImageCount(request) is var sent and > 1)
            {
                // A cap, not a model that can't see: the screenshots but the latest leave the
                // conversation and the step is sent again. Unless the error names the cap, it is one
                // under what was refused: a run grows by a screenshot a step, so it is refused at one
                // over the cap. It is kept, so the next time the run gets there its screenshots go
                // before the request rather than after a refusal. A chat that already held more (12
                // on munito, which takes 8) learns 11, and is refused again at 9, when it learns 8;
                // each refusal lowers it, so a step is sent a few times at most.
                var cap = ex.Limit is { } said && said < sent ? said : sent - 1;
                MaxImages = cap;
                observer.OnNotice($"The server takes {cap} pictures in one request at most; the screenshots before the latest are left out.", ex);
                LeaveOutOldImages();
                request.Clear();
                request.Add(ChatMessage.System(systemPrompt));
                request.AddRange(History);
                attempt--;
            }
            catch (TooManyImagesException ex) when (HasImages())
            {
                // Refused with one picture: it takes none after all.
                toolContext.SeesImages = false;
                observer.OnNotice(ex.Message.TrimEnd().TrimEnd('.') + ". Going on without pictures.", ex);
                DropImages();
                request.Clear();
                request.Add(ChatMessage.System(systemPrompt));
                request.AddRange(History);
                attempt--;
            }
            catch (PictureTooLargeException ex) when (History.Count(IsScreenshot) > 1 || History.Any(IsScreenshot) && ex.Side < (toolContext.MaxScreenshotSide ?? int.MaxValue))
            {
                // Pictures too large, not too many: screenshots fit the size the server names from
                // now on, here and in the next runs. Those in the conversation go in words at once, as
                // for too many pictures, the latest too: a click is worked out for the new size, and
                // the model points on the last picture it got, so it gets the screen anew at that size.
                client.NotePictureSide(requestOptions.Model, ex.Side);
                toolContext.MaxScreenshotSide = Math.Min(ex.Side, toolContext.MaxScreenshotSide ?? ex.Side);
                ScreenshotsResized?.Invoke();
                observer.OnNotice($"The server takes no pictures over {ex.Side} px a side in a request this big; screenshots fit that from now on, and the earlier ones are left out.", ex);
                foreach (var m in History.Where(IsScreenshot))
                    m.Parts = [m.Parts![0], ContentPart.FromText(OldScreenshotText)];
                var screen = ScreenshotTool.Take(toolContext);
                History.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText($"{CurrentScreenText} {screen.Text}"), ContentPart.FromImage(screen.Image!, screen.ImageMime)] });
                request.Clear();
                request.Add(ChatMessage.System(systemPrompt));
                request.AddRange(History);
                attempt--;
            }
            catch (RequestTooLargeException ex) when (History.Count(IsScreenshot) > 1)
            {
                // Too many bytes, which is the screenshots: the ones before the latest go, as for
                // too many pictures, and the step is sent again. Only if that is still too large is
                // the context reset (below).
                observer.OnNotice($"The request, {ex.Bytes / 1e6:0.#} MB with its screenshots, was more than the server takes; the screenshots before the latest are left out.", ex);
                foreach (var m in History.Where(IsScreenshot).SkipLast(1))
                    m.Parts = [m.Parts![0], ContentPart.FromText(OldScreenshotText)];
                request.Clear();
                request.Add(ChatMessage.System(systemPrompt));
                request.AddRange(History);
                attempt--;
            }
            catch (KeyRefusedException ex) when (ex.OutOfCredit && History.Count(IsScreenshot) > 1)
            {
                // A server that checks credit before the call, from the most it might cost, can
                // count screenshots far above what they are billed: proxyapi.ru refused 80 of them
                // with 2000₽ on the account, ~27 with a long conversation (chat 20261103-161905-5a0987).
                // The ones before the latest go, all at once as for too many pictures, and the step
                // is sent again; with only the latest left, a second refusal is a real empty account.
                observer.OnNotice("The server said the account's credit doesn't cover the request; the screenshots before the latest are left out and it is sent again.", ex);
                foreach (var m in History.Where(IsScreenshot).SkipLast(1))
                    m.Parts = [m.Parts![0], ContentPart.FromText(OldScreenshotText)];
                request.Clear();
                request.Add(ChatMessage.System(systemPrompt));
                request.AddRange(History);
                attempt--;
            }
            catch (ImagesRefusedException ex) when (HasImages())
            {
                // The model was taken to see, or the chat holds pictures from one that could, and
                // its server refused them: they leave the conversation (which the server then reads
                // anew, once), and later ones are left out as they come. Kept in, they failed every
                // request after the first screenshot, or every message after switching models.
                toolContext.SeesImages = false;
                observer.OnNotice(ex.Message.TrimEnd().TrimEnd('.') + ". Going on without them.", ex);
                DropImages();
                request.Clear();
                request.Add(ChatMessage.System(systemPrompt));
                request.AddRange(History);
                attempt--;
            }
            catch (ContextOverflowException ex) when (attempt < 2)
            {
                if (ex is InputRateException rate)
                {
                    // Four fifths of it, so a request with its new screenshots and replies still fits.
                    var fits = Math.Max(16_384, rate.PerMinute * 4 / 5);
                    if (contextTokens == 0 || fits < contextTokens)
                    {
                        observer.OnNotice($"The API key's account takes {rate.PerMinute / 1000}k input tokens a minute, and one request of the whole conversation was more than that; working with {fits / 1000}k tokens of context from now on.", ex);
                        contextTokens = fits;
                    }
                }
                else if (ex is RequestTooLargeException tooLarge)
                {
                    // Too many bytes, not tokens: the screenshots in it. A context a quarter smaller
                    // resets before a request gets that large again.
                    var smaller = Math.Max(16_384, TokensInUse() * 3 / 4);
                    if (contextTokens == 0 || smaller < contextTokens)
                    {
                        observer.OnNotice($"The request, {tooLarge.Bytes / 1e6:0.#} MB with its screenshots, was more than the server takes; working with {smaller / 1000}k tokens of context from now on.", ex);
                        contextTokens = smaller;
                    }
                }
                // The context size was assumed or wrong; most servers name the real one.
                else if (ex.ContextLimit is { } limit && (contextTokens == 0 || limit < contextTokens))
                {
                    observer.OnNotice($"The server's context is {limit / 1000}k tokens, not {contextTokens / 1000}k; working with that from now on.", ex);
                    contextTokens = limit;
                    // The conversation fits; the reply room asked on top of it didn't ("you requested
                    // 70000 tokens (6000 in the messages, 64000 in the completion)"). Sent again with
                    // the room the real size leaves, no reset.
                    if (TokensInUse() < ResetThreshold)
                        continue;
                }
                // It names none, or one no smaller than this size: the server counts more than the
                // usage it reported (Kilo's gateway counts a fourth of the request's characters, half
                // again Nemotron's count of plain text, and refuses past 1M), and kept at the size, the run
                // would grow into the same refusal after the reset. A quarter below where it broke.
                else if (Math.Max(16_384, TokensInUse() * 3 / 4) is var below && (contextTokens == 0 || below < contextTokens))
                {
                    observer.OnNotice($"The server refused {TokensInUse() / 1000}k tokens of conversation as too long; working with {below / 1000}k tokens of context from now on.", ex);
                    contextTokens = below;
                }
                await ResetContextAsync(observer, ct, overflowed: true).ConfigureAwait(false);
                request.Clear();
                request.Add(ChatMessage.System(systemPrompt));
                request.AddRange(History);
                observer.OnStepStarted(0); // the retried reply is a new one, after the handoff note
            }
        }
    }

    /// <summary>
    /// Sends one request, retrying when the connection drops. Unstable links (VPNs, proxies that
    /// cut silent connections) drop long requests, but the server keeps what it already processed
    /// in its prompt cache, so sending the identical request again resumes where it stopped.
    /// </summary>
    private async Task<ChatMessage> CompleteWithReconnectAsync(
        IReadOnlyList<ChatMessage> request, IReadOnlyList<ToolDefinition> toolDefs, ChatRequestOptions options,
        IAgentObserver observer, Action<StreamDelta>? onDelta, CancellationToken ct)
    {
        var drops = 0;
        var held = 0;
        var away = 0;
        var blocked = 0;
        while (true)
        {
            try
            {
                return await client.CompleteAsync(request, toolDefs, options, onDelta, ct, observer.OnSending).ConfigureAwait(false);
            }
            catch (RateLimitedException ex)
            {
                // Not a broken link: the server holds requests back for a while (Kilo's free models
                // take 200 an hour), so the same request goes again after the wait it names, or a
                // growing one. As many times as it takes: Stop ends the wait.
                var wait = RateLimitWait(ex.RetryAfter, ++held);
                // Kilo doesn't say for how long, and five-minute waits read as Sloperator being stuck.
                // A free model's daily allowance, which Kilo shares among all its users, comes back no sooner.
                var why = !KiloGateway.Is(client.BaseUrl) ? ""
                    : ex.Message.Contains("daily", StringComparison.OrdinalIgnoreCase)
                        ? " Kilo shares a free model's daily allowance among all its users, so this can last until tomorrow: pick another free model at the top, or use a Kilo key with credits or a model server of your own."
                        : " Kilo's free models take 200 requests an hour from each PC, so this can last until the hour is up; a Kilo key with credits, or a model server of your own, has no such limit.";
                observer.OnNotice($"{ex.Message}{why} Trying again in {Describe(wait)} (Stop ends the wait)…", ex);
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OutputBlockedException ex) when (++blocked <= MaxOutputBlocks)
            {
                // The task used to end here. The model is told once, as the same reply would be stopped again;
                // the note stays in the history so what follows keeps to it. A second stop is asked again as it is.
                if (request[^1].Content != OutputBlockedNote)
                {
                    var note = ChatMessage.User(OutputBlockedNote);
                    History.Add(note);
                    request = [.. request, note];
                }
                observer.OnNotice($"The server's content filter stopped the model's reply ({ex.Message}). Asked the model to give it another way, attempt {blocked} of {MaxOutputBlocks}…", ex);
                if (ex.ReceivedTokens && onDelta is not null)
                {
                    // Discard the half-streamed reply; the next try writes it anew.
                    observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "" });
                    observer.OnStepStarted(0);
                }
            }
            catch (ConnectionLostException ex) when (ex.Unreachable)
            {
                // Nothing got through: the server's PC asleep or restarting, LM Studio closed, the
                // VPN down. Twelve quick tries were over in half a minute, and an unattended task of
                // hours ended on a short outage; it waits for the server instead, the waits growing
                // as for a busy server. Stop ends the wait.
                var wait = RateLimitWait(null, ++away);
                observer.OnNotice($"{ex.Message} Trying again in {Describe(wait)} (Stop ends the wait)…", ex);
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (ConnectionLostException ex) when (++drops <= MaxReconnects)
            {
                // Still no room in llama.cpp's KV cache after three tries: what other requests hold
                // (another Sloperator on the same LM Studio keeps its cached prompt there) leaves too
                // little for this conversation. 2048 waited out all twelve tries that way. A fresh,
                // shorter conversation fits, and a smaller context keeps it fitting.
                if (ex.ServerFailed && drops > 3 && OpenAiChatClient.OutOfRoomWords().IsMatch(ex.Message) && TokensInUse() > 16_384)
                {
                    var smaller = Math.Max(16_384, TokensInUse() * 3 / 4);
                    if (contextTokens == 0 || smaller < contextTokens)
                    {
                        observer.OnNotice($"The server has no room for {TokensInUse() / 1000}k tokens of conversation right now; working with {smaller / 1000}k tokens of context from now on.", ex);
                        contextTokens = smaller;
                    }
                    throw new ContextOverflowException("the server has no room left for it (" + ex.Message + ")");
                }
                // The server's own failure is given time to pass: other requests filling its memory,
                // its model reloading after a crash. Sent again at once, all twelve tries were gone in seconds.
                var pause = ex.ServerFailed ? TimeSpan.FromTicks(Math.Min(RateLimitWaits[Math.Min(drops, RateLimitWaits.Length) - 1].Ticks, TimeSpan.TicksPerMinute))
                    : drops > 1 ? TimeSpan.FromSeconds(Math.Min(drops - 1, 3)) : TimeSpan.Zero;
                observer.OnNotice(ex.ServerFailed
                    ? $"{ex.Message} Trying again in {Describe(pause)}, attempt {drops} of {MaxReconnects}…"
                    : $"Connection lost ({ex.Message}). Reconnecting, attempt {drops} of {MaxReconnects}…", ex);
                // A model server that fails a request may have got stuck on how the prompt begins,
                // as Kilo's free Nemotron did for half an hour: the request goes again a line lower.
                if (ex.Upstream)
                    client.ShiftPrompt();
                // Resending would only repeat a tool call too long to get through, so the model is
                // asked to split it. The note stays in the history so later calls stay short too.
                // If the smaller parts are still too long, it is told once more, with a firmer limit.
                if (!ex.ServerFailed && ex.ToolCallInProgress is not null && toolDefs.Count > 0 && request[^1].Content != StillTooLongNote)
                {
                    var again = request[^1].Content == LongToolCallNote;
                    var note = ChatMessage.User(again ? StillTooLongNote : LongToolCallNote);
                    History.Add(note);
                    request = [.. request, note];
                    observer.OnNotice(again
                        ? "The model's smaller parts were still too long; asking for much smaller ones."
                        : "The reply was lost while the model was writing a long tool call; asking it to write in smaller parts.");
                }
                if (ex.ReceivedTokens && onDelta is not null)
                {
                    // Discard the half-streamed reply; the retry produces it again.
                    observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "" });
                    observer.OnStepStarted(0);
                }
                // The server keeps what it processed, so resending at once loses nothing; a short
                // pause only after repeated drops, in case the link itself is down.
                if (pause > TimeSpan.Zero)
                    await Task.Delay(pause, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The waits after a 429 that names none: a server that was only busy answers after the first,
    /// an hourly allowance used up gets asked every five minutes.
    /// </summary>
    internal static readonly TimeSpan[] RateLimitWaits =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>
    /// How long to wait after the <paramref name="attempt"/>th 429 in a row: what the server says,
    /// within a second and an hour, else <see cref="RateLimitWaits"/>. From the second try on, no
    /// sooner than those, up to a minute: a server that went on saying "in a second" (or a reset
    /// time already past) was asked once a second for as long as it said no, with a line each time.
    /// </summary>
    internal static TimeSpan RateLimitWait(TimeSpan? said, int attempt)
    {
        var growing = RateLimitWaits[Math.Clamp(attempt, 1, RateLimitWaits.Length) - 1];
        if (said is not { } s)
            return growing;
        var named = TimeSpan.FromTicks(Math.Clamp(s.Ticks, TimeSpan.TicksPerSecond, TimeSpan.TicksPerHour));
        var least = attempt <= 1 ? TimeSpan.Zero : growing < TimeSpan.FromMinutes(1) ? growing : TimeSpan.FromMinutes(1);
        return named >= least ? named : least;
    }

    private static string Describe(TimeSpan wait) => wait.TotalSeconds < 90 ? $"{Math.Ceiling(wait.TotalSeconds):0} s" : $"{Math.Ceiling(wait.TotalMinutes):0} min";

    /// <summary>Rough token count of the conversation (about 3.5 characters per token; a screenshot one per 32x32 pixels).</summary>
    internal int EstimateTokens() =>
        (int)(((long)systemPrompt.Length + _toolChars.Value + History.Sum(CharsOf)) * 2 / 7);

    /// <summary>Size of the tool definitions, which go into every request; MCP servers can add a lot.</summary>
    private readonly Lazy<long> _toolChars = new(() =>
        tools.Definitions().Sum(d => (long)d.Name.Length + d.Description.Length + d.Parameters.ToJsonString().Length + 40));

    /// <summary>Rough token count of one message.</summary>
    internal static int EstimateTokens(ChatMessage m) => (int)((long)CharsOf(m) * 2 / 7);

    private static long CharsOf(ChatMessage m) =>
        (m.Content?.Length ?? 0) + (m.Reasoning?.Length ?? 0) +
        (m.ToolCalls?.Sum(c => c.Name.Length + c.Arguments.Length) ?? 0) +
        (m.Parts?.Sum(p => p.IsImage ? ImageTokens(p.ImageBytes!) * 7 / 2 : p.Text?.Length ?? 0) ?? 0);

    /// <summary>What the Qwen vision encoder spends on an image: a token per 32x32 pixels (2000 if it can't be read).</summary>
    internal static long ImageTokens(byte[] image)
    {
        using var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(image));
        return codec is null ? 2000 : (long)Math.Ceiling(codec.Info.Width / 32.0) * (long)Math.Ceiling(codec.Info.Height / 32.0);
    }

    /// <summary>
    /// Keeps the prompt under <paramref name="budget"/> tokens. Preserved thinking is the bulk of
    /// a long run, so older reasoning goes first, then the text of old tool results.
    /// </summary>
    internal void FitContext(int budget)
    {
        if (budget <= 0 || EstimateTokens() <= budget)
            return;

        DropOldReasoning(budget);
        if (EstimateTokens() <= budget)
            return;

        foreach (var m in History.Take(Math.Max(0, History.Count - 8)).Where(m => m.Role == ChatRole.Tool && m.Content is { Length: > 400 }))
        {
            m.Content = m.Content![..300] + "\n[older output trimmed to save context]";
            if (EstimateTokens() <= budget) return;
        }

        // Last resort: drop whole steps from the middle. The original request (first message)
        // and the most recent steps stay, and tool results never lose their tool call.
        const string gap = "[Earlier steps were removed to fit the context window. Take a screenshot if you need to see the current state.]";
        var removedAny = false;
        while (EstimateTokens() > budget && History.Count > 8)
        {
            var at = History.Count > 1 && History[1].Content == gap ? 2 : 1;
            History.RemoveAt(at);
            while (at < History.Count - 6 && History[at].Role == ChatRole.Tool)
                History.RemoveAt(at); // results whose call was just removed
            removedAny = true;
        }
        if (removedAny && History[1].Content != gap)
            History.Insert(1, ChatMessage.User(gap));
    }

    /// <summary>
    /// If a run was interrupted between the model asking for tools and the results arriving,
    /// add placeholder results so the next request is still a valid conversation.
    /// </summary>
    private void RepairDanglingToolCalls() => RepairDanglingToolCalls(History);

    /// <inheritdoc cref="RepairDanglingToolCalls()"/>
    public static void RepairDanglingToolCalls(List<ChatMessage> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role != ChatRole.Assistant || !msg.HasToolCalls)
                continue;
            var answered = history.Skip(i + 1).Where(m => m.Role == ChatRole.Tool).Select(m => m.ToolCallId).ToHashSet();
            var insertAt = i + 1 + history.Skip(i + 1).TakeWhile(m => m.Role == ChatRole.Tool).Count();
            foreach (var call in msg.ToolCalls!.Where(c => !answered.Contains(c.Id)))
                history.Insert(insertAt++, ChatMessage.ToolResult(call.Id, "Not run: the user stopped the agent."));
            break;
        }
    }
}
