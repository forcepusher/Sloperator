using Sloperator.Core.Llm;
using Sloperator.Core.Settings;

namespace Sloperator.Core.Agent;

public sealed partial class AgentSession
{
    /// <summary>
    /// Auto's evaluation of <paramref name="input"/> (the user, 2026-11-09): a side task on <paramref name="set"/>'s
    /// helper model, with tools that only read (<see cref="AutoPick.Tools"/>), that picks the model where
    /// <paramref name="pickModel"/> and the effort where <paramref name="pickEffort"/>, and says what it found;
    /// in a chat under way that the server's cache still holds (<paramref name="cacheWarm"/>) it is offered
    /// <paramref name="before"/> and the models above it only, and once that has lapsed all of them, as on a first message.
    /// Before the run: the caller then starts the run on what was picked. Its calls are lines of
    /// <paramref name="progress"/>; one that needs the user's OK gets its own card to ask on. A server's
    /// failure, or an answer with no pick, comes back as <see cref="AutoPick.Problem"/> with <paramref name="before"/>
    /// kept; Stop ends it as it ends a run.
    /// </summary>
    public async Task<AutoPick> EvaluateAsync(UserInput input, AutoModelSet set, bool pickModel, bool pickEffort, AutoTier before, bool cacheWarm,
        Action<string>? progress, IAgentObserver observer, CancellationToken ct)
    {
        var by = set.Helper.Id;
        var lastReply = History.LastOrDefault(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Content))?.Content;
        // A chat under way only moves up from the model it is on while the cache holds it (AutoPick.Next).
        var underWay = History.Count > 0;
        var request = AutoPick.Request(input.ToMessage(seesImages: false).Content ?? input.Text, set, pickModel, pickEffort, requestOptions.Model,
            underWay ? TokensInUse() : 0, underWay ? requestOptions.Model : null, lastReply, underWay && cacheWarm ? before : null);
        var effort = Efforts.Nearest("low", set.Helper.Efforts);
        var result = await RunSideTaskAsync(request, true, AutoPick.Tools, null, progress, observer, ct,
            new SideTaskModel(by, effort), AutoPick.MaxSteps).ConfigureAwait(false);
        if (result.IsError)
            return new AutoPick(before, null, "", "", by, result.Text);
        return AutoPick.Read(result.Text, set, pickModel, pickEffort, before, by)
               ?? new AutoPick(before, null, "", "", by, "Its answer named no " + (pickModel ? "model" : "effort") + ".");
    }
}
