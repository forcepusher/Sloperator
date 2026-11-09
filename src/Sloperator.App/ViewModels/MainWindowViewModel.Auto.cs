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
    /// </summary>
    private (AutoModelSet Set, bool Model, bool Effort)? AutoToPick(ProbeResult? probe)
    {
        var server = _settings.Server;
        if (probe is not { Reachable: true, Auto: { } set })
            return null;
        var model = server.ModelOnAuto && set.Differ;
        var effort = server.EffortOnAuto && (model
            ? set.Evaluation.Efforts.Count + set.Daily.Efforts.Count + set.Complex.Efforts.Count > 0
            : server.EffortLevels.Count > 0);
        return model || effort ? (set, model, effort) : null;
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

    /// <summary>
    /// Shows that Auto's evaluation by <paramref name="by"/> has started: a card in the chat, its calls lines
    /// of progress on it. Returns where those lines go.
    /// </summary>
    private Action<string> ShowAutoPicking(string by)
    {
        var card = _autoCard = new ToolCall { Id = "auto-" + Guid.NewGuid().ToString("N")[..8], Name = AutoCardName, Arguments = "{}" };
        IAgentObserver observer = this;
        observer.OnToolStarted(card, $"{by} picks the model for this message…");
        return text => observer.OnToolProgress(card, text);
    }

    /// <summary>The evaluation's pick on its card while the user is asked whether to move up to it.</summary>
    private void ShowAutoWants(AutoPick pick, string model, string? effort)
    {
        if (_autoCard is not { } card)
            return;
        var line = $"Wants {(effort is null ? model : $"{model} at {Efforts.Label(effort)}")}" + (pick.Why.Length > 0 ? $"{Because}{pick.Why}" : "");
        Dispatcher.UIThread.Post(() =>
        {
            if (Items.OfType<ToolCallItem>().LastOrDefault(t => t.CallId == card.Id) is { } item)
            {
                item.Summary = line;
                item.Progress = "";
            }
        });
    }

    // How the card's line goes on after the model it picked (ShowAutoPick), for PickedIn to read back.
    private const string StayedOff = " (you stayed off ", Failed = ": the evaluation failed", Because = " · ";

    /// <summary>
    /// The model and effort an Auto card's line says the run used ("claude-opus-5-5 at High" of "claude-opus-5-5 at High
    /// (you stayed off claude-fable-5-1) · A refactor"), for the folded card that hides it; read from the line, so a chat
    /// opened from History has it too.
    /// </summary>
    internal static string PickedIn(string line)
    {
        var end = line.IndexOf(Because, StringComparison.Ordinal) is >= 0 and var because ? because : line.Length;
        foreach (var after in (string[])[Failed, StayedOff])
            if (line.IndexOf(after, StringComparison.Ordinal) is >= 0 and var at && at < end)
                end = at;
        return line[..end];
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
        var at = (effort is null ? model : $"{model} at {Efforts.Label(effort)}") + (declined is null ? "" : $"{StayedOff}{declined})");
        var line = pick.Problem is not null ? $"{at}{Failed}" : pick.Why.Length > 0 ? $"{at}{Because}{pick.Why}" : at;
        var output = pick.Problem is not null ? $"{pick.By} couldn't pick: {pick.Problem}"
            : pick.Found.Length > 0 ? $"{pick.Found}\n\nFound by {pick.By}. These notes go to {model} with your message." : "";
        IAgentObserver observer = this;
        // The line, then the end: both are posted, in this order, after the start.
        Dispatcher.UIThread.Post(() =>
        {
            if (Items.OfType<ToolCallItem>().LastOrDefault(t => t.CallId == card.Id) is { } item)
                item.Summary = line;
        });
        observer.OnToolFinished(card, pick.Problem is not null ? ToolResult.Error(output) : new ToolResult(output));
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
