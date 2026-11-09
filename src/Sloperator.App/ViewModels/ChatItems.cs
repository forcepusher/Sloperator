using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Sloperator.Core.Agent;
using Sloperator.Core.Chats;
using Sloperator.Core.Llm;
using Sloperator.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sloperator.App.ViewModels;

public abstract partial class ChatItemViewModel : ObservableObject
{
    /// <summary>
    /// When it happened: a message sent, a step or a tool call begun, a notice shown. Shown with it,
    /// so how long things took can be read off the chat. Null in chats saved before times were kept.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTime), nameof(TimeText), nameof(TimeTip))]
    private DateTime? _at = DateTime.UtcNow;

    public bool HasTime => At is not null;

    /// <summary>The time of day it happened ("14:05:09").</summary>
    public virtual string TimeText => At is { } at ? ChatTimes.Clock(at) : "";

    /// <summary>The date and time in full, for a chat from another day.</summary>
    public virtual string? TimeTip => At is { } at ? ChatTimes.Full(at) : null;

    /// <summary>On the line a run ended with: how long the whole run took, from the message that started it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunTook), nameof(RunTookText), nameof(ShowsRunTook))]
    private TimeSpan? _runTook;

    public bool HasRunTook => RunTook is not null;
    public string RunTookText => RunTook is { } took ? $"Worked for {ChatTimes.Duration(took)}" : "";

    /// <summary>Under the line, unless the run's work is folded: its card says it.</summary>
    public bool ShowsRunTook => HasRunTook && Fold is null;

    // ---- folded work (see WorkFold) ---------------------------------------------------------

    /// <summary>The folded work of a finished run this line is in, or ends; null while its run goes on.</summary>
    public WorkFold? Fold { get; private set; }

    /// <summary>The whole line folds away; false for the line the run ended with (its reply, its stop), which stays.</summary>
    private bool _foldsWhole;

    /// <summary>The fold whose card shows over this line: its first line still in the chat.</summary>
    public WorkFold? Card => Fold?.Head == this ? Fold : null;

    public bool HasCard => Card is not null;

    /// <summary>The line itself shows: it isn't folded away.</summary>
    public bool IsShown => !(_foldsWhole && Fold is { IsOpen: false });

    /// <summary>It takes room in the chat: the line, or the card over it. A line folded away takes none, nor the gap after it.</summary>
    public bool InChat => IsShown || HasCard;

    internal void JoinFold(WorkFold fold, bool whole)
    {
        (Fold, _foldsWhole) = (fold, whole);
        FoldChanged();
    }

    /// <summary>The fold opened or closed, or its card moved to this line.</summary>
    internal virtual void FoldChanged()
    {
        OnPropertyChanged(nameof(Fold));
        OnPropertyChanged(nameof(Card));
        OnPropertyChanged(nameof(HasCard));
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(InChat));
        OnPropertyChanged(nameof(ShowsRunTook));
    }

    /// <summary>Files shown under the item: what a tool made or showed, or what a reply names by its path.</summary>
    public System.Collections.ObjectModel.ObservableCollection<FilePreview> Previews { get; } = [];

    public bool HasPreviews => Previews.Count > 0;

    public void AddPreviews(IEnumerable<string> paths, PreviewServices services)
    {
        foreach (var path in paths)
            if (!Previews.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
                Previews.Add(FilePreview.For(path, services));
        OnPropertyChanged(nameof(HasPreviews));
    }

    /// <summary>Stops players and lets go of decoded frames, when the item leaves the chat.</summary>
    public virtual void DisposePreviews()
    {
        foreach (var p in Previews)
            p.Dispose();
    }
}

/// <summary>
/// A message the user sent: what they typed, and the files that came with it (dropped, pasted or
/// picked), with the cards of what the open side panel gave it as they showed over the box.
/// </summary>
public sealed class UserMessageItem(string text, IReadOnlyList<string>? files = null, IReadOnlyList<SidePanelCard>? cards = null) : ChatItemViewModel
{
    public string Text { get; } = text;
    public bool HasText => Text.Length > 0;

    /// <summary>Where it was written, when not in the chat's box ("Video editor · The Fox and the Moon · clip 2").</summary>
    public string? Via { get; init; }

    /// <summary>Shown over its words only for a message saved before panels gave cards: those show it now.</summary>
    public bool HasVia => Via is not null && Cards is null;

    /// <summary>What the model was told with it, unseen in the chat (see <see cref="UserInput.Context"/>).</summary>
    public string? Context { get; init; }

    /// <summary>What of the open panel went with it (see <see cref="UserInput.Cards"/>).</summary>
    public IReadOnlyList<SidePanelCard>? Cards { get; } = cards;

    /// <summary>The File changes turn it started, for Undo message and everything below to put the files back to; null in a chat saved before there were turns.</summary>
    public int? Turn { get; init; }

    /// <summary>Which message it is in the model's conversation (see <see cref="UserInput.Ids"/>); none in a chat saved before messages had them.</summary>
    public IReadOnlyList<string> Ids { get; init; } = [];

    /// <summary>The message as sent, to send again.</summary>
    public UserInput Input => new(Text, Files) { Via = Via, Context = Context, Cards = Cards, Ids = Ids };

    /// <summary>
    /// The files as their cards, the panel's first as over the box (the user, 2026-10-26): a pick by what
    /// it is in the panel ("Lines 12-18 · DiffTool.cs"), the picks that have no file yet (a shot not
    /// made), then the user's own files.
    /// </summary>
    public IReadOnlyList<AttachmentItem> Attachments { get; } =
    [
        .. (files ?? []).Select(f => (File: f, Card: cards?.FirstOrDefault(c => string.Equals(c.Path, f, StringComparison.OrdinalIgnoreCase))))
            .Where(p => p.Card is not null).Select(p => new AttachmentItem(p.File, picked: p.Card!.Title)),
        .. (cards ?? []).Where(c => c.Path is { } p && files?.Contains(p, StringComparer.OrdinalIgnoreCase) != true).Select(c => new AttachmentItem(c.Path!, picked: c.Title)),
        .. (files ?? []).Where(f => cards?.Any(c => string.Equals(c.Path, f, StringComparison.OrdinalIgnoreCase)) != true).Select(f => new AttachmentItem(f)),
    ];

    /// <summary>With nothing picked in the open panel, what the message was about there, before the files.</summary>
    public IReadOnlyList<SidePanelCardItem> Wholes { get; } = (cards ?? []).Where(c => c.Path is null).Select(c => new SidePanelCardItem(c)).ToList();

    public bool HasAttachments => Attachments.Count > 0 || Wholes.Count > 0;

    public List<string> Files { get; } = [.. files ?? []];

    public override void DisposePreviews()
    {
        base.DisposePreviews();
        foreach (var a in Attachments)
            a.Dispose();
    }
}

public sealed partial class AssistantMessageItem : ChatItemViewModel
{
    private readonly object _gate = new();
    private readonly StringBuilder _newText = new(), _newReasoning = new();
    private bool _flushQueued;

    /// <summary>
    /// Takes a streamed piece, on any thread. The pieces that arrive until the window is idle are
    /// shown together: one update per piece laid out the whole growing text again each time, and
    /// a long reply froze the window for minutes (6,000 pieces: 139 s).
    /// </summary>
    public void Append(StreamDelta delta)
    {
        lock (_gate)
        {
            _firstWords ??= DateTime.UtcNow;
            (delta.Channel == StreamChannel.Reasoning ? _newReasoning : _newText).Append(delta.Text);
            if (_flushQueued) return;
            _flushQueued = true;
        }
        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    private void Flush()
    {
        string text, reasoning;
        lock (_gate)
        {
            (text, reasoning) = (_newText.ToString(), _newReasoning.ToString());
            _newText.Clear();
            _newReasoning.Clear();
            _flushQueued = false;
        }
        if (!IsStreaming)
            return; // the finished message has replaced what streamed
        if (reasoning.Length > 0) Reasoning += reasoning;
        if (text.Length > 0) Text += text;
    }

    /// <summary>
    /// Ends the stream, which ended <paramref name="at"/>: the finished message replaces what
    /// streamed. Without one (a reply thrown away, or the run ended), what streamed stays as it is.
    /// </summary>
    public void Finish(DateTime at, ChatMessage? message = null)
    {
        Flush();
        if (message is not null)
        {
            Text = (message.Content ?? "").Trim();
            Reasoning = (message.Reasoning ?? Reasoning).Trim();
            Tokens = ReplyTokens.Of(message);
        }
        Ended = at;
        IsStreaming = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText), nameof(ShownText), nameof(ThinkingLabel), nameof(ShowTypingDots), nameof(TypingText))]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasoning), nameof(ShownReasoning), nameof(ShowTypingDots), nameof(TypingText), nameof(ShowsThoughts))]
    private string _reasoning = "";

    /// <summary>The Thoughts show: there are some, and they aren't folded with the run's work (the reply that ended it stays, its thinking folds).</summary>
    public bool ShowsThoughts => HasReasoning && Fold is not { IsOpen: false };

    internal override void FoldChanged()
    {
        base.FoldChanged();
        OnPropertyChanged(nameof(ShowsThoughts));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThinkingLabel), nameof(ShowTypingDots))]
    private bool _isStreaming = true;

    /// <summary>How much of the step's request has gone out to the server; set from the agent's thread.</summary>
    private long _sent, _total;

    /// <summary>The request on its way out: <paramref name="sent"/> of <paramref name="total"/> bytes; shown by the next <see cref="Tick"/>.</summary>
    public void Sending(long sent, long total)
    {
        Interlocked.Exchange(ref _total, total);
        Interlocked.Exchange(ref _sent, sent);
        if (total > 0 && sent >= total)
            SentAt = DateTime.UtcNow; // sent again (a dropped connection, a server that said to wait): the last time counts
    }

    /// <summary>
    /// Under a reply in progress: "…", or while nothing has come yet, for how long. A server
    /// reading a long prompt sends nothing for minutes; the count shows Sloperator is waiting, not
    /// stuck. Before that the request has to get there, which over a slow link takes minutes too.
    /// </summary>
    public string TypingText
    {
        get
        {
            if (HasText || HasReasoning || DateTime.UtcNow - (At ?? DateTime.UtcNow) is not { TotalSeconds: >= 3 } waited)
                return "…";
            var (sent, total) = (Interlocked.Read(ref _sent), Interlocked.Read(ref _total));
            return total > 0 && sent < total
                ? $"Sending to the server… {sent / 1048576.0:0.0} of {total / 1048576.0:0.0} MB, {ChatTimes.Stopwatch(waited)}"
                : $"Waiting for the model… {ChatTimes.Stopwatch(waited)}";
        }
    }

    /// <summary>Called every second while a run is on, for <see cref="TypingText"/>.</summary>
    public void Tick() => OnPropertyChanged(nameof(TypingText));

    // Streams arrive with blank lines around them (thinking usually ends in "\n\n" until the
    // reply starts, and the reply opens with them), which showed as empty bands mid-stream.
    public string ShownText => Text.Trim();

    /// <summary>The thinking as shown; Gemma 4's comes with its channel's name in front ("thought\n…"), which says nothing.</summary>
    public string ShownReasoning => Reasoning.Trim() is var r && r.StartsWith("thought\n", StringComparison.Ordinal) ? r[8..].TrimStart() : Reasoning.Trim();
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);
    public string ThinkingLabel => IsStreaming && !HasText ? "Thinking…" : "Thoughts";

    /// <summary>The "…" under a reply in progress; while the Thoughts header already says "Thinking…" it would only be a stray line.</summary>
    public bool ShowTypingDots => IsStreaming && (HasText || !HasReasoning);

    /// <summary>
    /// In a run's last reply, under its words: the pictures of its work the run took with
    /// capture_result, as the user is to see what was made and what it changed. Empty in any other reply.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<ReplyPicture> Captures { get; } = [];

    public bool HasCapture => Captures.Count > 0;

    /// <summary>Shows the pictures under the reply's words, each with its caption, in place of any it showed.</summary>
    public void ShowCaptures(IEnumerable<(string Path, string? Caption)> pictures, PreviewServices services)
    {
        foreach (var shown in Captures)
            shown.Picture.Dispose();
        Captures.Clear();
        foreach (var (path, caption) in pictures)
            if (FilePreview.For(path, services) is ImagePreview picture)
                Captures.Add(new ReplyPicture(picture, caption?.Trim() ?? ""));
        OnPropertyChanged(nameof(HasCapture));
    }

    public override void DisposePreviews()
    {
        base.DisposePreviews();
        foreach (var shown in Captures)
            shown.Picture.Dispose();
    }

    // ---- how long the step took ----------------------------------------------------------
    // At is when the step asked the model; the rest is set from the agent's thread as it goes.

    private DateTime? _sentAt, _firstWords;

    /// <summary>When the step's request had all gone out to the server.</summary>
    public DateTime? SentAt
    {
        get { lock (_gate) return _sentAt; }
        set { lock (_gate) _sentAt = value; }
    }

    /// <summary>When the model's first words came, thoughts or reply.</summary>
    public DateTime? FirstWords
    {
        get { lock (_gate) return _firstWords; }
        set { lock (_gate) _firstWords = value; }
    }

    /// <summary>The reply's tokens, for the chat's file; shown nowhere.</summary>
    public ReplyTokens? Tokens { get; set; }

    /// <summary>When the reply was complete, or the run stopped it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText), nameof(TimeTip))]
    private DateTime? _ended;

    /// <summary>
    /// Over the reply: when the model was asked and, once it is done, how long it took ("14:05:09 ·
    /// model 41 s"). Named as the model's, since a step that only called tools shows this line right
    /// over the tool call's card, which has a time of its own.
    /// </summary>
    public override string TimeText => At is not { } at ? ""
        : Ended is { } ended ? $"{ChatTimes.Clock(at)} · model {ChatTimes.Duration(ended - at)}"
        : ChatTimes.Clock(at);

    /// <summary>
    /// Where the step's time went: sending the request (over a slow link, minutes), the server
    /// reading it until the first words (longer as the conversation grows), and the writing.
    /// </summary>
    public override string? TimeTip
    {
        get
        {
            if (At is not { } at)
                return null;
            var asked = $"Asked the model on {ChatTimes.Full(at)}.";
            if (Ended is not { } ended)
                return asked;
            var lines = new List<string> { asked };
            var from = at;
            if (SentAt is { } sent && sent <= ended && sent - at >= TimeSpan.FromSeconds(1))
            {
                lines.Add($"Sending the request: {ChatTimes.Duration(sent - at)}");
                from = sent;
            }
            if (FirstWords is { } first && first >= from && first <= ended)
            {
                lines.Add($"{(from == at ? "Until the first words" : "Reading it, until the first words")}: {ChatTimes.Duration(first - from)}");
                lines.Add($"{(HasReasoning ? "Thinking and writing" : "Writing the reply")}: {ChatTimes.Duration(ended - first)}");
            }
            lines.Add($"In all: {ChatTimes.Duration(ended - at)}");
            return string.Join("\n", lines);
        }
    }
}

/// <summary>Stopped: the run ended (Pause/Break, Stop, an error) while the call was running or waiting for an OK.</summary>
public enum ToolItemState { AwaitingApproval, Running, Done, Error, Denied, Stopped }

public sealed partial class ToolCallItem(string callId, string name, string summary) : ChatItemViewModel
{
    private TaskCompletionSource<bool>? _approval;

    public string CallId { get; } = callId;
    public string Name { get; } = name;

    /// <summary>What the call was given (JSON), while the run goes on: the Video editor panel reads which piece the video tool is making.</summary>
    public string Arguments { get; init; } = "";

    /// <summary>The Input saved with the chat, for a card read back from it (whose <see cref="Arguments"/> are gone).</summary>
    public string? SavedInput { get; init; }

    /// <summary>
    /// What the call was given, as its Input foldout shows it: a side task's prompt (its task is the card's
    /// line, the user 2026-11-02). Empty for other tools, whose line says it.
    /// </summary>
    public string Input => SavedInput ?? (Name == Core.Tools.SideTaskTool.ToolName && Arguments.Length > 0 ? Core.Tools.SideTaskTool.PromptIn(Arguments) : "");

    public bool HasInput => Input.Length > 0;

    /// <summary>Auto's evaluation (see <see cref="MainWindowViewModel.AutoCardName"/>): no call of the model's; what it found shows open.</summary>
    public bool IsAuto => Name == MainWindowViewModel.AutoCardName;

    /// <summary>The Output foldout's header: "What it found" on Auto's card.</summary>
    public string OutputHeader => IsAuto && !IsError ? "What it found" : "Output";

    /// <summary>On Auto's card once it picked: the model and effort the run used, "claude-haiku-5-5 at Low"; else null.</summary>
    public string? AutoPicked => IsAuto && State is ToolItemState.Done or ToolItemState.Error ? MainWindowViewModel.PickedIn(Summary) : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Line), nameof(Command), nameof(HasCommand))]
    private string _summary = summary;

    /// <summary>
    /// The card's line beside the tool's name: the summary, or for a shell command a short title with
    /// what the model said it does ("Run a command · List the newest log files"), the command itself
    /// under it in a block of its own (a whole command in bold was too much, the user, 2026-10-29).
    /// </summary>
    public string Title => !HasCommand ? Summary : CommandParts().Description is { } d ? $"Run a command · {d}" : "Run a command";

    /// <summary>The title as the card shows it, on one line: its paths by their names (<see cref="ToolLine"/>); the whole title is its tooltip.</summary>
    public string Line => ToolLine.Short(Title);

    /// <summary>The command a run_shell call ran, from its summary, so a saved chat shows it too; null for any other call.</summary>
    public string? Command => CommandParts().Command;

    /// <summary>A run_shell summary: "Run: " and the command, with the model's description on a line over it if it gave one (ShellTool.Describe).</summary>
    private (string? Description, string? Command) CommandParts()
    {
        if (Name != "run_shell")
            return (null, null);
        if (Summary.StartsWith("Run: ", StringComparison.Ordinal))
            return (null, Summary[5..]);
        return Summary.IndexOf("\nRun: ", StringComparison.Ordinal) is var at and > 0 ? (Summary[..at], Summary[(at + 6)..]) : (null, null);
    }

    public bool HasCommand => Command is { Length: > 0 };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAwaitingApproval), nameof(StatusText), nameof(IsError), nameof(CanEndWait), nameof(OutputHeader))]
    private ToolItemState _state = ToolItemState.Running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _resultText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScreenshot), nameof(HasOpenScreenshot), nameof(HasFoldedScreenshot))]
    private Bitmap? _screenshot;

    /// <summary>The tools called to look: their picture is the answer, shown open on the card.</summary>
    private static readonly HashSet<string> Looks = ["screenshot", "zoom"];

    /// <summary>
    /// A picture a look gave stays open. The screen an action left (a click, a wait, a key) is folded
    /// as the output is: a chat of clicks was mostly screenshots. It is the same picture the model got.
    /// </summary>
    public bool HasOpenScreenshot => HasScreenshot && Looks.Contains(Name);

    public bool HasFoldedScreenshot => HasScreenshot && !Looks.Contains(Name);

    /// <summary>The screenshot as the model got it, at its full size, for Copy; it goes when the thumbnail does.</summary>
    public byte[]? ScreenshotData { get; set; }

    /// <summary>The files the call made or showed, for the chat's file until their previews come (they are looked for in the background).</summary>
    public IReadOnlyList<string>? SavedFiles { get; set; }

    /// <summary>Ends the call's wait; null on a card read back from a saved chat.</summary>
    public Action? EndsWait { get; init; }

    /// <summary>A wait going on: Skip wait shows on its card.</summary>
    public bool CanEndWait => EndsWait is not null && State == ToolItemState.Running && Name is "wait" or "wait_until_change";

    [RelayCommand]
    private void EndWait() => EndsWait?.Invoke();

    public bool IsAwaitingApproval => State == ToolItemState.AwaitingApproval;
    public bool IsError => State is ToolItemState.Error or ToolItemState.Denied;
    public bool HasResult => ResultText.Length > 0;
    public bool HasScreenshot => Screenshot is not null;

    /// <summary>How a long call is getting on ("KSampler, step 3 of 20 · 1:12"), while it runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _progress = "";

    /// <summary>When the call ended, or the run stopped it. <see cref="ChatItemViewModel.At"/> is when it began to run (after an OK, if it waited for one).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private DateTime? _ended;

    private TimeSpan? Took => At is { } at && Ended is { } ended ? ended - at : null;

    /// <summary>What became of the call and how long it took ("Done in 1.2 s"); while it runs, for how long so far.</summary>
    public string StatusText => State switch
    {
        ToolItemState.AwaitingApproval => "Waiting for your OK",
        ToolItemState.Running => Progress.Length > 0 ? Progress : RunningText,
        ToolItemState.Done => Took is { } took ? $"Done in {ChatTimes.Duration(took)}" : "Done",
        ToolItemState.Error => Took is { } took ? $"Failed after {ChatTimes.Duration(took)}" : "Failed",
        ToolItemState.Denied => "Declined",
        ToolItemState.Stopped => Took is { } took ? $"Stopped after {ChatTimes.Duration(took)}" : "Stopped",
        _ => "",
    };

    /// <summary>"Running…", and after a few seconds for how long ("Running… 0:12"), as the wait for the model counts: a long command doesn't look stuck.</summary>
    private string RunningText => At is { } at && DateTime.UtcNow - at is { TotalSeconds: >= 3 } running ? $"Running… {ChatTimes.Stopwatch(running)}" : "Running…";

    /// <summary>Called every second while a run is on, for the count in <see cref="StatusText"/>.</summary>
    public void Tick()
    {
        if (State == ToolItemState.Running && Progress.Length == 0)
            OnPropertyChanged(nameof(StatusText));
    }

    public Task<bool> WaitForApprovalAsync(CancellationToken ct)
    {
        _approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => _approval.TrySetResult(false));
        State = ToolItemState.AwaitingApproval;
        return _approval.Task;
    }

    [RelayCommand]
    private void Approve()
    {
        State = ToolItemState.Running;
        _approval?.TrySetResult(true);
    }

    [RelayCommand]
    private void Deny()
    {
        State = ToolItemState.Denied;
        _approval?.TrySetResult(false);
    }
}

/// <summary>
/// The offer of text recognition, on a card on the chat bar above the message box where find_text
/// found none installed: the model's words offering it were often skipped, and a card in the chat
/// scrolled away unread (the user, 2026-10-30). Install, Not now (offered again the next time it is
/// needed), or Turn off (Settings' Use text recognition). Installing, Windows asks whether PowerShell
/// may make changes, which looks alarming, so the card says so first. Not now closes it at once; after
/// an install or Turn off, what happened shows for <see cref="Linger"/>, then <paramref name="close"/>.
/// </summary>
public sealed partial class TextRecognitionOffer(ITextReader reader, Action turnOff, Action close) : ObservableObject
{
    /// <summary>How long the outcome stays on the chat bar before the card goes.</summary>
    public TimeSpan Linger { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>What the card says under its title: the offer, then how the install went.</summary>
    [ObservableProperty]
    private string _status = "Makes model's clicks more accurate. Part of Windows, a few MB.";

    /// <summary>The buttons show until one is chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsWarning))]
    private bool _isOpen = true;

    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>Said before Install is pressed, so Windows' prompt comes as no surprise.</summary>
    public bool ShowsWarning => IsOpen;

    [RelayCommand]
    private async Task InstallAsync()
    {
        IsOpen = false;
        IsInstalling = true;
        Status = "Installing… When Windows asks about PowerShell, click Yes.";
        var result = await reader.InstallAsync(CancellationToken.None);
        IsInstalling = false;
        Status = result switch
        {
            TextInstall.Installed => $"Installed: {string.Join(", ", reader.Languages())}.",
            TextInstall.Declined => "Not installed: Windows' prompt was declined. Offered again next time.",
            _ => "Not installed: Windows couldn't add it. Try Settings → General.",
        };
        await LingerThenCloseAsync();
    }

    [RelayCommand]
    private void NotNow()
    {
        IsOpen = false;
        close();
    }

    [RelayCommand]
    private async Task TurnOffAsync()
    {
        IsOpen = false;
        turnOff();
        Status = "Turned off. Back on in Settings → General.";
        await LingerThenCloseAsync();
    }

    private async Task LingerThenCloseAsync()
    {
        await Task.Delay(Linger);
        close();
    }
}

/// <param name="carryOn">For the notice a stopped run leaves ("Stopped with Pause/Break."): carries on with the task. Null for any other notice.</param>
/// <param name="unsent">The run stopped before the model had the user's message: carrying on sends that message again.</param>
public sealed partial class NoticeItem(string text, bool isError, Action? carryOn = null, bool unsent = false) : ChatItemViewModel
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;

    /// <summary>A run ended here before the task was done (stopped, or an error it couldn't get past).</summary>
    public bool Stopped { get; } = carryOn is not null;

    /// <summary>The run stopped before the model had the user's message: Continue sends that message again.</summary>
    public bool Unsent { get; } = unsent;

    /// <summary>Its Continue button shows: until the chat goes on, by Continue or a message of the user's.</summary>
    [ObservableProperty]
    private bool _canContinue = carryOn is not null;

    /// <summary>The run it starts takes the button off (and every other one); with a run still going it does nothing.</summary>
    [RelayCommand]
    private void Continue() => carryOn?.Invoke();
}

/// <summary>
/// The work of a finished run folded under one card (the user, 2026-11-08: once the agent has
/// answered, its tool calls and thinking go out of the way, with a button to see them again). The
/// card says how long the run worked and how many tool calls it made, with the tools that ran as
/// their chips; Show steps unfolds the lines in place, Hide steps folds them again. What the run
/// ended with stays out of the fold: its reply (but its thinking folds) or its stop with Continue,
/// as does any message of the user's (one sent during the run ends one fold, the rest of the run
/// makes another). Folded only once the run is over: while it runs, every line shows.
/// </summary>
public sealed partial class WorkFold : ObservableObject
{
    private readonly List<ChatItemViewModel> _lines;

    /// <summary>What the run ended with, which stays in the chat with its thinking folded: its reply and any line after it, or its stop.</summary>
    private readonly List<ChatItemViewModel> _kept;

    /// <param name="lines">The lines that fold away, in order.</param>
    /// <param name="kept">The lines the run ended with, which stay (its reply, or its stop).</param>
    /// <param name="took">How long the run worked, from the message that started it; null when the chat didn't keep the times.</param>
    public WorkFold(IReadOnlyList<ChatItemViewModel> lines, IReadOnlyList<ChatItemViewModel> kept, TimeSpan? took)
    {
        _lines = [.. lines];
        _kept = [.. kept];
        // Auto's card is no call of the model's; folded away, its pick is said here (the user's pick, 2026-11-09):
        // nothing else in sight said which model had answered.
        var calls = _lines.OfType<ToolCallItem>().Where(c => !c.IsAuto).ToList();
        Tools = calls.Select(c => c.Name).Distinct().ToList();
        Title = string.Join(" · ", new[]
        {
            took is { } t ? $"Worked for {ChatTimes.Duration(t)}" : null,
            calls.Count switch { 0 => null, 1 => "1 tool call", var n => $"{n} tool calls" },
            _lines.OfType<ToolCallItem>().Select(c => c.AutoPicked).LastOrDefault(p => p is not null),
        }.OfType<string>().DefaultIfEmpty("Thoughts"));
        foreach (var line in _lines)
            line.JoinFold(this, whole: true);
        foreach (var line in _kept)
            line.JoinFold(this, whole: false);
    }

    /// <summary>"Worked for 3 min 12 s · 4 tool calls".</summary>
    public string Title { get; }

    /// <summary>The tools the run called, each once, in the order it first called them.</summary>
    public IReadOnlyList<string> Tools { get; }

    public bool HasTools => Tools.Count > 0;

    /// <summary>The line the card shows over: the first still in the chat (a long run drops its oldest), the reply when nothing else folds.</summary>
    public ChatItemViewModel? Head => _lines.Count > 0 ? _lines[0] : _kept.FirstOrDefault();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isOpen;

    public string ToggleText => IsOpen ? "Hide steps" : "Show steps";

    partial void OnIsOpenChanged(bool value)
    {
        foreach (var line in _lines.Concat(_kept))
            line.FoldChanged();
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    /// <summary>A line left the chat (the oldest of a long run's): the card moves to the next.</summary>
    public void Drop(ChatItemViewModel line)
    {
        var wasHead = ReferenceEquals(Head, line);
        if (!_lines.Remove(line))
            _kept.Remove(line);
        if (wasHead)
            Head?.FoldChanged();
    }

    /// <summary>
    /// Folds every finished run in <paramref name="chat"/> not folded yet, each stretch of its work
    /// on its own: a stretch ends at a message of the user's, at the line a run ended with (it has
    /// <see cref="ChatItemViewModel.RunTook"/>), and at a stop or an error, which stays in sight. A
    /// stretch with no tool call and no thinking (a model loaded, a plain answer) stays as it is.
    /// Called once a run is over, and on a chat opened from History.
    /// </summary>
    /// <param name="toTheEnd">
    /// The lines after the last that ends a stretch fold too: in a chat opened from History, saved
    /// before runs kept their times (no line ends one). Not as a run ends: the next can be under way.
    /// </param>
    public static void FoldFinished(IReadOnlyList<ChatItemViewModel> chat, bool toTheEnd)
    {
        var stretch = new List<ChatItemViewModel>();
        foreach (var line in chat)
        {
            if (line.Fold is not null)
            {
                stretch.Clear(); // an earlier run's, folded already
                continue;
            }
            switch (line)
            {
                case UserMessageItem:
                    Close(stretch, Between(stretch, line), endsWith: false);
                    break;
                case { RunTook: { } took }:
                    stretch.Add(line);
                    Close(stretch, took, endsWith: line is NoticeItem);
                    break;
                case NoticeItem { IsError: true } or NoticeItem { Stopped: true }:
                    var stopped = Between(stretch, line);
                    stretch.Add(line);
                    Close(stretch, stopped, endsWith: true);
                    break;
                default:
                    stretch.Add(line);
                    break;
            }
        }
        if (toTheEnd)
            Close(stretch, null, endsWith: false);
    }

    /// <summary>How long a stretch the run's end didn't time took: from its first line to what came after it.</summary>
    private static TimeSpan? Between(List<ChatItemViewModel> stretch, ChatItemViewModel next) =>
        stretch.Count > 0 && stretch[0].At is { } from && next.At is { } to && to >= from ? to - from : null;

    /// <summary>
    /// Folds the stretch, but for what it ended with: its reply (the last words of the model's with
    /// no tool call after them) and anything after that, or with no reply, a last line that stays in
    /// sight (<paramref name="endsWith"/>: a stop, an error).
    /// </summary>
    private static void Close(List<ChatItemViewModel> stretch, TimeSpan? took, bool endsWith)
    {
        var reply = stretch.FindLastIndex(l => l is AssistantMessageItem { HasText: true });
        if (reply < 0 || stretch.Skip(reply).Any(l => l is ToolCallItem))
            reply = endsWith ? stretch.Count - 1 : stretch.Count;
        var (lines, kept) = (stretch[..reply], stretch[reply..]);
        if (lines.Any(l => l is ToolCallItem or AssistantMessageItem { HasReasoning: true }) || kept.FirstOrDefault() is AssistantMessageItem { HasReasoning: true })
            _ = new WorkFold(lines, kept, took);
        stretch.Clear();
    }
}

/// <summary>A picture of the work under a reply (see <see cref="AssistantMessageItem.Captures"/>), with the line the model gave it; empty for none.</summary>
public sealed record ReplyPicture(ImagePreview Picture, string Caption)
{
    public bool HasCaption => Caption.Length > 0;
}
