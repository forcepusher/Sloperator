using System.Text;
using System.Text.RegularExpressions;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;

namespace Sloperator.Core.Agent;

/// <summary>
/// What Auto's evaluation picked for a message (the user, 2026-11-09): before the agent starts, the evaluation
/// model looks at the message, and at the project as far as it needs, in a side task, and picks one of the
/// user's three models (<see cref="AutoModels"/>) and an effort, each where it is on Auto. What it found goes to
/// the picked model with the message, so that one needn't look again.
/// </summary>
/// <param name="Tier">The model picked; the one Auto used before when the model isn't on Auto or the evaluation failed.</param>
/// <param name="Effort">The effort picked; null when the effort isn't on Auto or the evaluation said none.</param>
/// <param name="Why">One sentence on what the task is and why that fits; empty when it said none.</param>
/// <param name="Found">What it learnt looking around, for the agent; empty when nothing.</param>
/// <param name="By">The evaluation model.</param>
/// <param name="Problem">Why the evaluation picked nothing (the server failed it, its answer had no pick); null when it worked.</param>
public sealed partial record AutoPick(AutoTier Tier, string? Effort, string Why, string Found, string By, string? Problem = null)
{
    /// <summary>The tools the evaluation looks around with: it reads, it changes nothing.</summary>
    internal static readonly string[] Tools = ["read_file", "list_directory", "index_folder", "web_search", "fetch_page"];

    /// <summary>Replies the evaluation gets at most: a look around, not the task.</summary>
    internal const int MaxSteps = 12;

    /// <summary>
    /// What the evaluation model is asked. It picks the model where <paramref name="pickModel"/> and the effort
    /// where <paramref name="pickEffort"/>; <paramref name="chatTokens"/> and <paramref name="chatModel"/> say how
    /// big the chat is and which model it ran on last, as another reads it all anew; <paramref name="lastReply"/>
    /// is the agent's last answer, for a message such as "yes, do it".
    /// </summary>
    internal static string Request(string message, AutoModelSet set, bool pickModel, bool pickEffort, string inUse,
        int chatTokens, string? chatModel, string? lastReply)
    {
        var text = new StringBuilder();
        text.Append("Sloperator's agent is about to work on the user's message below. Before it starts, pick ")
            .Append(pickModel && pickEffort ? "the model it works with and how hard that model thinks" : pickModel ? "the model it works with" : "how hard its model thinks")
            .Append(". Look around only as far as that takes (a few calls at most: read a file, list a folder, search the web); change nothing, " +
                    "and don't do the task itself.\n\n");
        if (pickModel)
        {
            text.Append("The models, the cheapest first:\n")
                .Append($"- evaluation: {set.Evaluation.Id} (you). For simple tasks: a question, a lookup, a small edit, one command or a few clicks.\n")
                .Append($"- daily: {set.Daily.Id}. For most tasks: writing or fixing code, documents, working in apps on the screen, tasks of several steps.\n")
                .Append($"- complex: {set.Complex.Id}. For hard tasks: large changes across many files, hard bugs, careful design or research, " +
                        "long work where mistakes cost much.\n");
        }
        else
            text.Append($"The model is {inUse}.\n");
        if (pickEffort)
            text.Append("Efforts, least to most: low, medium, high, xhigh, max. More thinks longer: better on hard problems, slower and dearer. " +
                        "Most tasks need medium; low for the simple ones.\n");
        if (chatTokens > 0 && chatModel is not null)
            text.Append($"\nThe chat so far holds about {chatTokens / 1000}k tokens and ran on {chatModel}. Any other model reads all of it anew at its " +
                        "full price, so pick another only when the task needs it.\n");
        if (!string.IsNullOrWhiteSpace(lastReply))
            text.Append("\nThe agent's last reply in the chat, for what the message may refer to:\n<<<\n").Append(lastReply.Trim().Clip(1500)).Append("\n>>>\n");
        text.Append("\nThe user's message:\n<<<\n").Append(message.Trim()).Append("\n>>>\n\nAnswer in exactly this form:\n");
        if (pickModel)
            text.Append("model: evaluation, daily or complex\n");
        if (pickEffort)
            text.Append("effort: low, medium, high, xhigh or max\n");
        text.Append("why: one short sentence on what the task is and why that fits\n")
            .Append("found: what you learnt looking around that the agent should know (which files, where things are, what to watch out for), " +
                    "a few short lines; nothing if you looked at nothing");
        return text.ToString();
    }

    /// <summary>
    /// The evaluation's answer read: "model: complex", "effort: high", "why: …", "found: …" and the lines after
    /// it, written plain or in Markdown. A model named by its name counts as its tier. Null when it names no
    /// model it was to pick, and no effort it was to pick.
    /// </summary>
    internal static AutoPick? Read(string answer, AutoModelSet set, bool pickModel, bool pickEffort, AutoTier before, string by)
    {
        var fields = Fields(answer);
        AutoTier? tier = null;
        if (pickModel && fields.GetValueOrDefault("model") is { } model)
        {
            var named = model.ToLowerInvariant();
            tier = named.Contains("complex") || named.Contains(set.Complex.Id.ToLowerInvariant()) && set.Complex.Id != set.Daily.Id ? AutoTier.Complex
                : named.Contains("evaluation") || named.Contains(set.Evaluation.Id.ToLowerInvariant()) && set.Evaluation.Id != set.Daily.Id ? AutoTier.Evaluation
                : named.Contains("daily") || named.Contains(set.Daily.Id.ToLowerInvariant()) ? AutoTier.Daily
                : null;
        }
        var effort = pickEffort && fields.GetValueOrDefault("effort") is { } level
            ? Efforts.Of(Regex.Split(level.ToLowerInvariant().Replace("extra high", "xhigh"), @"[^a-z]+")).FirstOrDefault()
            : null;
        if ((!pickModel || tier is null) && (!pickEffort || effort is null))
            return null;
        return new AutoPick(tier ?? before, effort, fields.GetValueOrDefault("why") ?? "", Nothing(fields.GetValueOrDefault("found")), by);
    }

    /// <summary>
    /// "nothing" (or "none", "-") as the found field says it, alone or with why after it: no notes. Haiku 5.5 wrote
    /// "nothing; I looked at nothing, since the message is self-contained", which went to the agent as what it found.
    /// "None of the tests cover it" is a note.
    /// </summary>
    private static string Nothing(string? found) =>
        found is null || Regex.IsMatch(found.Trim(), @"^(?:nothing|none|n/?a|-+)(?:$|\s*[.;:,(—–-])", RegexOptions.IgnoreCase) ? "" : found.Trim();

    /// <summary>The answer's fields by their names; a field's text runs to the next field.</summary>
    private static Dictionary<string, string> Fields(string answer)
    {
        var fields = new Dictionary<string, string>();
        string? key = null;
        var value = new StringBuilder();
        foreach (var line in answer.Replace("\r", "").Split('\n'))
        {
            var start = FieldStart().Match(line);
            if (start.Success)
            {
                Keep();
                key = start.Groups[1].Value.ToLowerInvariant();
                value.Clear().Append(line[start.Length..].Trim());
            }
            else if (key is not null)
                value.Append('\n').Append(line);
        }
        Keep();
        return fields;

        void Keep()
        {
            if (key is not null && !fields.ContainsKey(key))
                fields[key] = value.ToString().Trim().Trim('*', '_').Trim();
        }
    }

    // "model: complex", "**Model:** complex", "- effort: high".
    [GeneratedRegex(@"^\s*(?:[-*]\s*)?[*_]{0,2}(model|effort|why|found)[*_]{0,2}\s*:\s*[*_]{0,2}", RegexOptions.IgnoreCase)]
    private static partial Regex FieldStart();

    /// <summary>
    /// What the picked model is told with the message, before the user's words: who looked, and what it
    /// found. Null when it found nothing.
    /// </summary>
    public string? Notes => Found.Length == 0 ? null
        : $"[Sloperator] Before you started, {By} took a first look at this task to pick the model for it. What it found:\n{Found}";
}
