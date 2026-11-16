using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;

namespace Sloperator.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ---- Auto model and effort: the evaluation before a message ----------------------

    /// <summary>
    /// What Auto has to pick for a message (the user, 2026-11-09), from the check that starts the run: the model
    /// where it is on Auto and the server has two of the three models in Settings that differ, the effort where it
    /// is on Auto and a model to pick from takes one. Null when there is nothing to pick, and the run starts at once.
    /// In a chat under way that the server's cache still holds (<paramref name="upOnly"/>, <see cref="AutoUpOnly"/>)
    /// the model and the effort only move up (<see cref="AutoPick.Next"/>): the model while there is one above it,
    /// the effort while its model takes more (<see cref="AutoPick.CanRaise"/>; the user, 2026-11-13: "escalation is
    /// possible at any time when needed during a warm cache"). On the dearest of the three at the most effort it
    /// takes, or with both picked by the user, nothing is left to pick, and no evaluation holds the message up.
    /// </summary>
    private (AutoModelSet Set, bool Model, bool Effort)? AutoToPick(ProbeResult? probe, bool upOnly)
    {
        var server = _settings.Server;
        if (probe is not { Reachable: true, Auto: { } set })
            return null;
        var model = server.ModelOnAuto && (upOnly ? AutoPick.CanMoveUp(set, server.AutoTier) : set.Differ);
        var effort = server.EffortOnAuto && (model
            ? set.Helper.Efforts.Count + set.Daily.Efforts.Count + set.Complex.Efforts.Count > 0
            : upOnly ? AutoPick.CanRaise(server.EffortInUse, server.EffortLevels) : server.EffortLevels.Count > 0);
        return model || effort ? (set, model, effort) : null;
    }

    /// <summary>
    /// When the server last had this chat's prompt in hand, as the last run's session said it
    /// (<see cref="AgentSession.CacheUsedAt"/>: its last reply, or a read that kept the cache warm after it). Null in
    /// a chat that hasn't run in this window; one opened from History goes by its last reply's time.
    /// </summary>
    private DateTime? _cacheUsedAt;

    /// <summary>
    /// Whether a message in a chat under way may only move the model up (the user, 2026-11-12), which holds while
    /// the server's cache has the chat: for <see cref="AutoPick.CacheLife"/> after its last request. After that
    /// nothing is lost by a move down or another effort, and Auto picks as on a first message (the user, 2026-11-13).
    /// </summary>
    private bool AutoUpOnly(bool chatUnderWay)
    {
        if (!chatUnderWay)
            return false;
        var replied = Items.OfType<AssistantMessageItem>().LastOrDefault(a => a.Ended is not null)?.Ended?.ToUniversalTime();
        var used = replied is { } at && (_cacheUsedAt is not { } kept || at > kept) ? at : _cacheUsedAt;
        return !AutoPick.CacheLapsed(used, DateTime.UtcNow);
    }

    /// <summary>
    /// Auto's question over the message box (the user, 2026-11-09, the mockups' third design with the first's
    /// card): whether to move up to the dearer model Auto picked; null when none is asked.
    /// </summary>
    [ObservableProperty]
    private AutoSwitchQuestion? _switchQuestion;

    /// <summary>
    /// Asks whether to move from <paramref name="from"/> up to <paramref name="to"/>, the model Auto picked, at
    /// <paramref name="effort"/>: true for Switch, or for no answer in <see cref="AutoSwitchQuestion.Wait"/>,
    /// false for Stay. Stop ends the question as it ends the run.
    /// </summary>
    private async Task<bool> AskSwitchAsync(string to, string? effort, string from, CancellationToken ct)
    {
        var question = SwitchQuestion = new AutoSwitchQuestion(to, effort, from);
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (_, _) => question.Tick(DateTime.UtcNow - started));
        using var stop = ct.Register(question.Cancel);
        try
        {
            return await question.Answer;
        }
        finally
        {
            timer.Stop();
            SwitchQuestion = null;
        }
    }

    /// <summary>The card of the evaluation going on; null between them.</summary>
    private ToolCall? _autoCard;

    /// <summary>The name on the evaluation's card.</summary>
    internal const string AutoCardName = "Auto";

    /// <summary>The card's line while the evaluation goes on, after the helper model's name.</summary>
    private const string Picking = " is picking the model for this message…";

    /// <summary>
    /// Shows that Auto's evaluation by <paramref name="by"/> has started: a card in the chat, its calls lines
    /// of progress on it. Returns where those lines go.
    /// </summary>
    private Action<string> ShowAutoPicking(string by)
    {
        var card = _autoCard = new ToolCall { Id = "auto-" + Guid.NewGuid().ToString("N")[..8], Name = AutoCardName, Arguments = "{}" };
        IAgentObserver observer = this;
        observer.OnToolStarted(card, by + Picking);
        return text => observer.OnToolProgress(card, text);
    }

    /// <summary>The run stopped during the evaluation: its card says it was picking, where "is picking the model for this message…" read as still going.</summary>
    private void EndAutoPicking()
    {
        if (_autoCard is not { } card)
            return;
        _autoCard = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (Items.OfType<ToolCallItem>().LastOrDefault(t => t.CallId == card.Id) is { } item && item.Summary.EndsWith(Picking, StringComparison.Ordinal))
                item.Summary = item.Summary[..^Picking.Length] + " was picking the model for this message";
        });
    }

    /// <summary>The evaluation's pick on its card while the user is asked whether to move up to it.</summary>
    private void ShowAutoWants(AutoPick pick, string model, string? effort)
    {
        if (_autoCard is not { } card)
            return;
        var summary = AutoCard.Summary($"{AutoCard.Wants}{AutoCard.At(model, effort)}{AutoCard.ForThis}", pick.Why);
        Dispatcher.UIThread.Post(() =>
        {
            if (Items.OfType<ToolCallItem>().LastOrDefault(t => t.CallId == card.Id) is { } item)
            {
                item.Summary = summary;
                item.Progress = AutoCard.Waiting;
            }
        });
    }

    /// <summary>
    /// What Auto's evaluation picked, on its card: the model and effort the run starts with, why, and what it
    /// found (the picked model gets that with the message); or why it picked nothing, and what the run uses.
    /// </summary>
    /// <param name="declined">The dearer model Auto picked and the user stayed off; null when none was.</param>
    private void ShowAutoPick(AutoPick pick, string model, string? effort, string? declined = null)
    {
        if (_autoCard is not { } card)
            return;
        _autoCard = null;
        var at = AutoCard.At(model, effort);
        var summary = pick.Problem is not null ? $"{AutoCard.Kept}{at}{AutoCard.ForThis}{AutoCard.Failed}"
            : AutoCard.Summary(declined is null ? $"{AutoCard.Picked}{at}{AutoCard.ForThis}" : $"{AutoCard.Stayed}{at}{AutoCard.ForThis}{AutoCard.Not}{declined}", pick.Why);
        // The notes alone: the foldout's header says whom they go to (AutoCard.NotesHeader).
        var output = pick.Problem is not null ? $"{pick.By} couldn't pick: {pick.Problem}" : pick.Found;
        IAgentObserver observer = this;
        // The line, then the end: both are posted, in this order, after the start.
        Dispatcher.UIThread.Post(() =>
        {
            if (Items.OfType<ToolCallItem>().LastOrDefault(t => t.CallId == card.Id) is { } item)
                item.Summary = summary;
        });
        observer.OnToolFinished(card, pick.Problem is not null ? ToolResult.Error(output) : new ToolResult(output));
    }
}

/// <summary>
/// How Auto's card says what it did (the user's pick of four mockups, 2026-11-16, design A: its reason was cut off at
/// the end of a one-line card, and the card didn't say what it was). Its summary, which the chat saves, is a line that
/// says what happened ("Picked claude-sonnet-5-5 at Medium for this message"), then the helper's reason on lines of its
/// own, shown wrapped under it. Chats saved before put it all on one line, "claude-sonnet-5-5 at Medium · the reason":
/// they are read too.
/// </summary>
internal static class AutoCard
{
    internal const string Picked = "Picked ", Stayed = "Stayed on ", Kept = "Kept ", Wants = "Wants ", ForThis = " for this message",
        Not = ", not ", Failed = ": the pick failed", Waiting = "Waiting for your answer";

    // The one line of chats saved before 2026-11-16 (after the model: " (you stayed off …)", ": the evaluation failed", " · why").
    private const string OldStayedOff = " (you stayed off ", OldFailed = ": the evaluation failed", OldBecause = " · ";

    /// <summary>"claude-opus-5-5 at High", or the model alone for one that takes no effort.</summary>
    internal static string At(string model, string? effort) => effort is null ? model : $"{model} at {Efforts.Label(effort)}";

    internal static string Summary(string line, string why) => why.Length > 0 ? $"{line}\n{why}" : line;

    /// <summary>The card's line and the reason under it; a saved one-line summary is split at its " · ".</summary>
    internal static (string Line, string Reason) Parts(string summary)
    {
        if (summary.IndexOf('\n') is >= 0 and var end)
            return (summary[..end], summary[(end + 1)..].Trim());
        return summary.IndexOf(OldBecause, StringComparison.Ordinal) is >= 0 and var at ? (summary[..at], summary[(at + OldBecause.Length)..]) : (summary, "");
    }

    /// <summary>
    /// The model and effort a card says the run used ("claude-opus-5-5 at High" of "Stayed on claude-opus-5-5 at High for
    /// this message, not claude-fable-5-1"), for the folded card that hides it; read from the line, so a chat opened
    /// from History has it too.
    /// </summary>
    internal static string PickedIn(string summary)
    {
        var line = Parts(summary).Line;
        foreach (var word in (string[])[Picked, Stayed, Kept])
            if (line.StartsWith(word, StringComparison.Ordinal) && line.IndexOf(ForThis, word.Length, StringComparison.Ordinal) is >= 0 and var end)
                return line[word.Length..end];
        var cut = line.Length;
        foreach (var after in (string[])[OldFailed, OldStayedOff])
            if (line.IndexOf(after, StringComparison.Ordinal) is >= 0 and var at && at < cut)
                cut = at;
        return line[..cut];
    }

    /// <summary>"Notes it passed to claude-sonnet-5-5": the model the helper's notes went to with the message.</summary>
    internal static string NotesHeader(string summary)
    {
        var model = PickedIn(summary);
        foreach (var level in Efforts.Levels)
            if (model.EndsWith($" at {Efforts.Label(level)}", StringComparison.Ordinal))
                model = model[..^($" at {Efforts.Label(level)}".Length)];
        return model.Length > 0 ? $"Notes it passed to {model}" : "Notes it passed on";
    }

    /// <summary>The notes alone: a chat saved before 2026-11-16 has "Found by …, with your message." after them, which the header now says.</summary>
    internal static string Notes(string output)
    {
        const string by = "\n\nFound by ", end = " with your message.";
        return output.EndsWith(end, StringComparison.Ordinal) && output.LastIndexOf(by, StringComparison.Ordinal) is >= 0 and var at ? output[..at] : output;
    }
}

/// <summary>
/// The question over the message box when Auto picked a dearer model than the one in use (see
/// <see cref="MainWindowViewModel.SwitchQuestion"/>): Switch or Stay, and with no answer Auto's pick after <see cref="Wait"/>.
/// </summary>
public sealed partial class AutoSwitchQuestion(string to, string? effort, string from) : ObservableObject
{
    /// <summary>How long the question waits before Auto's pick is taken.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Text { get; } = $"Switch to {to}{(effort is null ? "" : $" at {Efforts.Label(effort)}")} for this? It costs more than {from}.";

    public string StayText { get; } = $"Stay on {from}";

    /// <summary>"Switches in 7 s".</summary>
    [ObservableProperty]
    private string _countdown = $"Switches in {Wait.TotalSeconds:0} s";

    /// <summary>How much of the wait is left, 1 to 0: the bar under the question.</summary>
    [ObservableProperty]
    private double _left = 1;

    /// <summary>True for Switch (or the wait running out), false for Stay.</summary>
    public Task<bool> Answer => _answer.Task;

    [RelayCommand]
    private void Switch() => _answer.TrySetResult(true);

    [RelayCommand]
    private void Stay() => _answer.TrySetResult(false);

    public void Cancel() => _answer.TrySetCanceled();

    /// <summary>The time since the question came: the countdown, and Auto's pick once it runs out.</summary>
    public void Tick(TimeSpan elapsed)
    {
        var left = Wait - elapsed;
        Left = Math.Clamp(left / Wait, 0, 1);
        Countdown = $"Switches in {Math.Max(1, Math.Ceiling(left.TotalSeconds)):0} s";
        if (left <= TimeSpan.Zero)
            _answer.TrySetResult(true);
    }
}
