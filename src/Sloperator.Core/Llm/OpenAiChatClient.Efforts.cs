using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Sloperator.Core.Settings;

namespace Sloperator.Core.Llm;

// The effort levels a model takes, as its server's refusals say: a list that says nothing about a
// model's efforts gets all of them offered (see ServerProbe), and the server's answer narrows them.
public sealed partial class OpenAiChatClient
{
    /// <summary>
    /// The effort levels each model takes, by server and model, as a refusal of one said, for as
    /// long as Sloperator runs; empty for a model that takes none. Requests ask for the nearest of
    /// them, and the server check narrows the top bar's dropdown to them (<see cref="EffortsTaken"/>).
    /// Those its Responses API takes are kept apart (see <see cref="EffortKey"/>).
    /// </summary>
    private static readonly ConcurrentDictionary<(string Server, string Model), IReadOnlyList<string>> TakenEfforts = new();

    /// <summary>The effort levels <paramref name="model"/> takes, as a refusal from its server said; null when none said.</summary>
    public static IReadOnlyList<string>? EffortsTaken(string baseUrl, string model) =>
        TakenEfforts.TryGetValue(EffortKey(NormalizeBaseUrl(baseUrl), model), out var taken) ? taken : null;

    /// <summary>
    /// Where a model's levels are kept: under its server, or its server's Responses API once it goes
    /// there, which may take others. gpt-6-sol on proxyapi.ru refused max on chat/completions and
    /// takes it on /v1/responses, where all its requests with tools go (2026-11-03).
    /// </summary>
    private static (string Server, string Model) EffortKey(string baseUrl, string model) =>
        (ResponsesRoutes.ContainsKey((baseUrl, model)) ? baseUrl + "/responses" : baseUrl, model);

    /// <summary>The options with the nearest effort the model takes, where a refusal said which it takes.</summary>
    private ChatRequestOptions WithTakenEffort(ChatRequestOptions options)
    {
        if (options.Effort is not { } effort || !TakenEfforts.TryGetValue(EffortKey(_baseUrl, options.Model), out var taken))
            return options;
        var nearest = Efforts.Nearest(effort, taken);
        if (nearest == effort)
            return options;
        var copy = options.Clone();
        copy.Effort = nearest;
        return copy;
    }

    /// <summary>
    /// Learns from a refusal of the effort that was sent which levels the model takes, and says
    /// whether it was one: "Unsupported value: 'reasoning_effort' does not support 'max' with this
    /// model. Supported values are: 'none', 'low', 'medium', 'high', and 'xhigh'." or "Unsupported
    /// value: 'minimal' is not supported with the 'gpt-6-sol' model. Supported values are: …"
    /// (proxyapi.ru, 2026-11-03), "output_config.effort: Input should be 'low', 'medium' or 'high'".
    /// The levels it lists are taken; with none listed, all but the refused one (another refusal
    /// narrows them further). An error about the effort that quotes no level ("reasoning_effort is
    /// not supported for this model") means the model takes none: no effort is sent from then on.
    /// </summary>
    private bool LearnEfforts(string model, string? sent, string err)
    {
        var key = EffortKey(_baseUrl, model);
        if (EffortRefusal(err, sent, TakenEfforts.TryGetValue(key, out var known) ? known : null) is not { } taken)
            return false;
        TakenEfforts[key] = taken;
        return true;
    }

    /// <summary>The levels a refusal of <paramref name="sent"/> says the model takes (see <see cref="LearnEfforts"/>); null when it isn't one.</summary>
    internal static IReadOnlyList<string>? EffortRefusal(string err, string? sent, IReadOnlyList<string>? known = null)
    {
        if (sent is null)
            return null;
        var said = ErrorText(err);
        var quoted = QuotedWord().Matches(said).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet();
        var listed = Efforts.Of(quoted.Where(q => q != sent));
        if (listed.Count > 0 && (quoted.Contains(sent) || err.Contains("effort", StringComparison.OrdinalIgnoreCase)))
            return listed;
        if (!err.Contains("effort", StringComparison.OrdinalIgnoreCase))
            return null;
        return quoted.Contains(sent) ? (known ?? Efforts.Levels).Where(l => l != sent).ToList() : [];
    }

    [GeneratedRegex(@"['""`‘’“”]([A-Za-z]+)['""`‘’“”]")]
    private static partial Regex QuotedWord();
}
