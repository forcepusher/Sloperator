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
/// <param name="By">The helper model.</param>
/// <param name="Problem">Why the evaluation picked nothing (the server failed it, its answer had no pick); null when it worked.</param>
public sealed partial record AutoPick(AutoTier Tier, string? Effort, string Why, string Found, string By, string? Problem = null)
{
    /// <summary>The tools the evaluation looks around with: it reads, it changes nothing.</summary>
    internal static readonly string[] Tools = ["read_file", "list_directory", "index_folder", "web_search", "fetch_page"];

    /// <summary>Replies the evaluation gets at most: a look around, not the task.</summary>
    internal const int MaxSteps = 12;

    /// <summary>The most words a reply is short at: <see cref="IsShortReply"/>.</summary>
    internal const int ShortReplyWords = 5;

    /// <summary>
    /// A short reply in a chat under way ("thanks", "yes, go on", "do it"), with no file or panel pick: it goes
    /// on with the model and effort in use, with no look first (the user's pick, 2026-11-09: each such message
    /// took 2 to 7 s and a request of the helper model). A first message is looked at however short.
    /// </summary>
    public static bool IsShortReply(UserInput input, bool chatUnderWay) =>
        chatUnderWay && input.Files.Count == 0 && input.Cards is not { Count: > 0 } && input.Context is null &&
        input.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= ShortReplyWords;

    /// <summary>
    /// Whether <paramref name="to"/> is a move up from <paramref name="from"/>: a tier above it whose model is
    /// another (two tiers can name the same one).
    /// </summary>
    public static bool StepsUp(AutoModelSet set, AutoTier from, AutoTier to) => to > from && set[to].Id != set[from].Id;

    /// <summary>
    /// Whether a chat under way on <paramref name="from"/> has a model to move up to. With none, there is nothing
    /// for the evaluation to pick: the chat neither moves down nor changes its effort alone.
    /// </summary>
    public static bool CanMoveUp(AutoModelSet set, AutoTier from) => Enum.GetValues<AutoTier>().Any(to => StepsUp(set, from, to));

    /// <summary>
    /// How long a server's prompt cache holds a chat after its last request (Anthropic's five minutes, the lifetime
    /// the user kept, 2026-11-13; see <see cref="AgentSession.KeepWarmEvery"/>). The same for every server: one
    /// that caches nothing, or for longer, picks freely a little later than it could.
    /// </summary>
    public static readonly TimeSpan CacheLife = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether the server's cache of a chat has lapsed by <paramref name="now"/>: it last had the chat's prompt in
    /// hand at <paramref name="lastUsed"/> (null: never, or nobody knows when). From then on a chat under way picks
    /// as freely as a first message does (the user, 2026-11-13): there is no cache left for a move down, or another
    /// effort, to miss.
    /// </summary>
    public static bool CacheLapsed(DateTime? lastUsed, DateTime now) => lastUsed is not { } at || now - at >= CacheLife;

    /// <summary>
    /// The tier and effort a message runs on, from this pick (the user, 2026-11-12: "I want the model only to
    /// escalate, not de-escalate"). A chat's first message takes what was picked, a dearer model only on the
    /// user's Switch (<paramref name="stayed"/>: they stayed off it). Under way, while the server's cache holds the
    /// chat (<paramref name="upOnly"/>), the chat only moves up, and the effort changes only with that move: either
    /// change has the model read the whole chat anew at its full price (Sonnet to Haiku on a 190k chat cost about 4
    /// times what Sonnet's cache did; Sonnet's effort from medium to low alone had all 126k of its messages written
    /// anew, 2026-11-11). Once that cache has lapsed (<see cref="CacheLapsed"/>) the chat is read anew whatever
    /// runs it, and the message takes what was picked as a first one does.
    /// </summary>
    public (AutoTier Tier, string? Effort) Next(AutoModelSet set, AutoTier tier, string? effort, bool upOnly, bool stayed)
    {
        if (Problem is not null)
            return (tier, effort);
        if (!upOnly)
            return (stayed ? tier : Tier, Effort ?? effort);
        if (stayed || Tier <= tier)
            return (tier, effort);
        return (Tier, StepsUp(set, tier, Tier) ? Effort ?? effort : effort);
    }

    private static string Key(AutoTier tier) => tier switch
    {
        AutoTier.Helper => "helper",
        AutoTier.Complex => "complex",
        _ => "daily",
    };

    private static string Use(AutoTier tier) => tier switch
    {
        AutoTier.Helper => "For simple tasks: a question, a lookup, a small edit, one command or a few clicks.",
        AutoTier.Complex => "For hard tasks: large changes across many files, hard bugs, careful design or research, long work where mistakes cost much.",
        _ => "For most tasks: writing or fixing code, documents, working in apps on the screen, tasks of several steps.",
    };

    /// <summary>
    /// What the helper model is asked. It picks the model where <paramref name="pickModel"/> and the effort
    /// where <paramref name="pickEffort"/>; <paramref name="chatTokens"/> and <paramref name="chatModel"/> say how
    /// big the chat is and which model it ran on last, as another reads it all anew; <paramref name="lastReply"/>
    /// is the agent's last answer, for a message such as "yes, do it". <paramref name="floor"/>: the tier of a chat
    /// under way, which it can only move up from (<see cref="Next"/>); null on a chat's first message, and in a
    /// chat the server's cache no longer holds (<see cref="CacheLapsed"/>), which is told so in place of what
    /// another model costs.
    /// </summary>
    internal static string Request(string message, AutoModelSet set, bool pickModel, bool pickEffort, string inUse,
        int chatTokens, string? chatModel, string? lastReply, AutoTier? floor = null)
    {
        var text = new StringBuilder();
        text.Append("Sloperator's agent is about to work on the user's message below. Before it starts, pick ")
            .Append(pickModel && pickEffort ? "the model it works with and how hard that model thinks" : pickModel ? "the model it works with" : "how hard its model thinks")
            .Append(". Look around only as far as that takes (a few calls at most: read a file, list a folder, search the web); change nothing, " +
                    "and don't do the task itself.\n\n");
        var tiers = Enum.GetValues<AutoTier>().Where(t => floor is not { } from || t == from || StepsUp(set, from, t)).ToList();
        if (pickModel)
        {
            if (floor is { } now)
                text.Append($"The chat works with {Key(now)} now. It stays on it or moves up to a stronger model; it never moves back down.\n");
            text.Append("The models, the cheapest first:\n");
            foreach (var tier in tiers)
                text.Append($"- {Key(tier)}: {set[tier].Id}{(tier == AutoTier.Helper ? " (you)" : "")}{(tier == floor ? " (in use)" : "")}. {Use(tier)}\n");
        }
        else
            text.Append($"The model is {inUse}.\n");
        if (pickEffort)
            text.Append("Efforts, least to most: low, medium, high, xhigh, max. More thinks longer: better on hard problems, slower and dearer. " +
                        "Most tasks need medium; low for the simple ones.")
                .Append(floor is null ? "\n" : " The effort in use changes only with a move up: give the level for the model you pick.\n");
        if (chatTokens > 0 && chatModel is not null)
            text.Append($"\nThe chat so far holds about {chatTokens / 1000}k tokens and ran on {chatModel}. ")
                .Append(floor is not null
                    ? "Any other model reads all of it anew at its full price, so pick another only when the task needs it.\n"
                    // Under way with no floor: the cache has lapsed, and staying costs what moving does.
                    : "The server no longer holds it in its cache, so all of it is read anew at full price whatever you pick: pick what fits this message.\n");
        if (!string.IsNullOrWhiteSpace(lastReply))
            text.Append("\nThe agent's last reply in the chat, for what the message may refer to:\n<<<\n").Append(lastReply.Trim().Clip(1500)).Append("\n>>>\n");
        text.Append("\nThe user's message:\n<<<\n").Append(message.Trim()).Append("\n>>>\n\nAnswer in exactly this form:\n");
        if (pickModel)
            text.Append("model: ").Append(tiers.Count switch
            {
                1 => Key(tiers[0]),
                _ => string.Join(", ", tiers.SkipLast(1).Select(Key)) + " or " + Key(tiers[^1]),
            }).Append('\n');
        if (pickEffort)
            text.Append("effort: low, medium, high, xhigh or max\n");
        text.Append("why: one short sentence on what the task is and why that fits\n")
            // Haiku 5.5 told the agent a parameter's default it had misread, which the agent then corrected to the user
            // (2026-11-10): the agent goes by these notes. It then wrote "I did not open any files, so I can't confirm the
            // page's markup" under what the chat had said, which told the agent nothing.
            .Append("found: facts the agent should know about what you read or saw (which files, where things are, what to watch out for), " +
                    "as they stand there, in a few short lines; nothing about yourself, such as what you did or didn't open, and nothing the chat already says; " +
                    "nothing if you looked at nothing");
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
                : named.Contains("helper") || named.Contains(set.Helper.Id.ToLowerInvariant()) && set.Helper.Id != set.Daily.Id ? AutoTier.Helper
                : named.Contains("daily") || named.Contains(set.Daily.Id.ToLowerInvariant()) ? AutoTier.Daily
                : null;
        }
        var effort = pickEffort && fields.GetValueOrDefault("effort") is { } level
            ? Efforts.Of(Regex.Split(level.ToLowerInvariant().Replace("extra high", "xhigh"), @"[^a-z]+")).FirstOrDefault()
            : null;
        if ((!pickModel || tier is null) && (!pickEffort || effort is null))
            return null;
        // One sentence was asked for: what follows a blank line is the helper thinking on. Haiku 5.5's went on "Hmm, but
        // I'm changing my answer, so let me be precise: I'm not switching it. … daily fits better, at medium effort",
        // all of it on the card under "claude-haiku-5-5 at Low" (2026-11-12).
        var why = Regex.Split(fields.GetValueOrDefault("why") ?? "", @"\n\s*\n")[0].Trim();
        return new AutoPick(tier ?? before, effort, why, Nothing(fields.GetValueOrDefault("found")), by);
    }

    /// <summary>
    /// "nothing" (or "none", "-") as the found field says it, alone or with why after it: no notes. Haiku 5.5 wrote
    /// "nothing; I looked at nothing, since the message is self-contained", which went to the agent as what it found.
    /// "None of the tests cover it" is a note.
    /// </summary>
    private static string Nothing(string? found) =>
        found is null || Regex.IsMatch(found.Trim(), @"^(?:nothing|none|n/?a|-+)(?:$|\s*[.;:,(—–-])", RegexOptions.IgnoreCase) ? "" : found.Trim();

    /// <summary>
    /// The answer's fields by their names; a field's text runs to the next field. A line that begins like a field
    /// is a line of the text when that field was given already or the notes (found, the last field) have begun
    /// ("- model: the data model is in models.py", "effort: medium would be too much"). Only a pick ("model: daily")
    /// starts the answer over there: an answer given again is the helper's correction, and counts.
    /// </summary>
    private static Dictionary<string, string> Fields(string answer)
    {
        var fields = new Dictionary<string, string>();
        string? key = null;
        var value = new StringBuilder();
        foreach (var line in answer.Replace("\r", "").Split('\n'))
        {
            var start = FieldStart().Match(line);
            var name = start.Success ? start.Groups[1].Value.ToLowerInvariant() : null;
            var over = name == "model" && key is not null && TierWord().IsMatch(line[start.Length..]);
            if (name is not null && (over || key != "found" && name != key && !fields.ContainsKey(name)))
            {
                Keep();
                if (over)
                    fields.Clear();
                key = name;
                value.Clear().Append(line[start.Length..].Trim());
            }
            else if (key is not null)
                value.Append('\n').Append(line);
        }
        Keep();
        return fields;

        void Keep()
        {
            if (key is not null)
                fields[key] = value.ToString().Trim().Trim('*', '_').Trim();
        }
    }

    // "model: complex", "**Model:** complex", "- effort: high".
    [GeneratedRegex(@"^\s*(?:[-*]\s*)?[*_]{0,2}(model|effort|why|found)[*_]{0,2}\s*:\s*[*_]{0,2}", RegexOptions.IgnoreCase)]
    private static partial Regex FieldStart();

    [GeneratedRegex(@"^\W*(helper|daily|complex)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TierWord();

    /// <summary>
    /// What the picked model is told with the message, after the user's words: who looked, and what it
    /// found. Null when it found nothing.
    /// </summary>
    public string? Notes => Found.Length == 0 ? null
        : $"[Sloperator] Before you started, {By} took a first look at this task to pick the model for it. What it found:\n{Found}";
}
