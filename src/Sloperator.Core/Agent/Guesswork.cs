using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sloperator.Core.Llm;
using Sloperator.Core.Mcp;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Agent;

/// <summary>
/// Notices the model guessing, tries that fail with no look-up in between, and points it to
/// web_search, which the system prompt already asks for. It didn't search in the blind-spot runs:
/// an Excel chart call failed the same way four times ("Method invocation failed ... does not
/// contain a method named 'Add'") before the model decided Excel's COM server was "a limited stub"
/// and wrote the chart's XML by hand; one search would have shown that in PowerShell ChartObjects()
/// is a method. And building a geometry nodes graph in Blender 5.2 over MCP, Nemotron went through
/// names from Blender 3 one failed script after another; the server reports a script's error as
/// plain text, which wasn't taken for an error at all. A note, never a block: an error naming
/// something that doesn't exist gets it at once, the same error a second time, any failure from
/// the third since the last look-up, and the first script or command that fails in a task before
/// anything was looked up. Only failures a search can answer count (see <see cref="Answerable"/>),
/// and a reply that looks something up gets no note: it is doing what the note would say.
/// <para>
/// Errors weren't enough. Asked to make a character follow a video's motion with LTX, a run tried
/// workflows for two hours; nothing failed, the videos just didn't move as asked, and the job was
/// done right after the user said to look for guidance (LTX's motion control guide). So a try the
/// model itself calls wrong, or the user does, counts as a failed one; a question the model asks
/// itself about how something is done, which it goes on without looking up, gets a note to search,
/// as does the next failed script after an error it was told to look up and went on past; and after ten
/// minutes of runs (commands, scripts, jobs: not edits, and not commands that only read), or four
/// quick ones, with nothing looked up, the next run says it is time to look, once for each message
/// of the user's and at least four runs after the last time.
/// </para>
/// <para>
/// Sloperator doesn't search for the model. It used to, and put the results under the step with the
/// model's question quoted: Qwen took them for "an injected message … containing fake web-search
/// results", and a Claude run was refused for having its own thinking quoted back to it.
/// </para>
/// </summary>
internal sealed partial class Guesswork(Func<DateTime>? clock = null)
{
    /// <summary>Failures further back than this many calls no longer count.</summary>
    private const int Window = 20;

    /// <summary>Failures since the last look-up (within <see cref="Window"/>) before every next one gets a note.</summary>
    internal const int Tries = 3;

    /// <summary>Time spent on runs with nothing looked up before the next run says it is time to look.</summary>
    internal static readonly TimeSpan CheckAfter = TimeSpan.FromMinutes(10);

    /// <summary>Runs in that time it takes as well: a long wait or one long render is no guesswork.</summary>
    internal const int CheckTries = 3;

    /// <summary>
    /// Runs with nothing looked up that make it time to look however quick they were, and the runs
    /// between one such note and the next: probing Blender 5.2 for how a modifier's inputs are set,
    /// qwen3.8 ran 17 scripts in 7 minutes after its last search and ran out of time without the
    /// file. Said on every try after eight, edits included, the note came with nearly every edit of
    /// a Unity chat feature (15 of its 17 notes), and Haiku 5.5 searched "for something" to obey it.
    /// Said every four runs with no other limit, 49 such notes in three chats brought 4 searches
    /// (2026-11-04): the model passed over the rest, some with "I'm setting web search aside".
    /// </summary>
    internal const int CheckBurst = 4;

    /// <summary>
    /// Look-ups in a row, with no try between them, after which the model is told the installed
    /// version can settle it: told to look things up, qwen3.8 read Blender's release notes, the
    /// manual and then its source on GitHub's main branch for 13 minutes (40 pages), asking whether
    /// Group Input had been renamed, rather than listing the node types its Blender 5.2 had.
    /// </summary>
    internal const int LongSearch = 8;

    internal static string LongSearchNote(int lookUps) =>
        $"[Sloperator] {lookUps} look-ups since your last try. When the pages don't settle it, the installed version can: ask it directly " +
        "(a short script that lists or prints what it has: dir(), help(), --help), then try the likeliest answer. " +
        "A try tells you more now than another page, and an error from it is something a search answers.";

    /// <summary>Calls after a look-up before a question of the model's gets a note: it is reading what it found.</summary>
    internal const int SearchGap = 4;

    // What the model thought isn't quoted (see the summary).
    internal const string QuestionNote =
        "[Sloperator] If you aren't sure how something here is done, what it is called or whether it exists, look it up with web_search " +
        "(then fetch_page with find= the part you need) before your next try, rather than going by memory.";

    internal const string IgnoredNote =
        "[Sloperator] This failed too, and the error before it was never looked up. Unless you know exactly why, " +
        "search for the error with web_search (then fetch_page with find=) before you try again.";

    internal const string RepeatNote =
        "[Sloperator] This error came up before in this chat. Unless you know exactly why, look it up with web_search " +
        "(the error message, and the program or language it comes from) before you try again.";

    internal const string NameNote =
        "[Sloperator] The error says a name doesn't exist here. Names change between versions, and yours may be from another one: " +
        "look up the right one for the version in use with web_search (then fetch_page with find= the name) instead of trying other names.";

    /// <summary>
    /// A program that isn't a command here ("python : The term 'python' is not recognized"): not a name from another
    /// version, as <see cref="NameNote"/> said of it (Haiku 5.5 on a PC with no Python, 2026-11-09), but a program that
    /// isn't installed or isn't on the PATH.
    /// </summary>
    internal static string ProgramNote(string name) =>
        $"[Sloperator] {name} isn't a command here: it isn't installed, or isn't on the PATH. Look for where it is installed, " +
        "or look up how to get it (web_search), and ask the user before you install anything.";

    internal static string TriesNote(int failed) =>
        $"[Sloperator] {failed} tries have failed since you last looked anything up. Unless you know exactly why this one failed, " +
        "search for the error or for how this is done in the version you are working with (web_search, then fetch_page) " +
        "before you try again: guessing costs more than a search.";

    internal const string FirstNote =
        "[Sloperator] Nothing has been looked up in this task yet. Unless you know exactly why this failed, look up how this is done " +
        "in the version you are working with (web_search, then fetch_page with find=) before you try again, rather than trying variations.";

    internal const string DoubtNote =
        "[Sloperator] By your own account the last try didn't give what was wanted, and nothing has been looked up in this task yet. " +
        "Before the next try, search the web for how this is done (the official documentation, the makers' guides and examples, " +
        "for the version in use: web_search, then fetch_page with find=) rather than trying variations.";

    internal const string UserDoubtNote =
        "[Sloperator] The user says the result isn't right yet. Before you try again, search the web for how this is done " +
        "(the official documentation, the makers' guides and examples, for the version in use: web_search, then fetch_page with find=) " +
        "rather than trying variations of what didn't work.";

    internal static string CheckNote(TimeSpan worked, int tries) =>
        $"[Sloperator] Time to look things up: {tries} tries{(worked.TotalMinutes >= 1 ? $" and {(int)worked.TotalMinutes} minute{((int)worked.TotalMinutes == 1 ? "" : "s")}" : "")} since anything was last looked up. " +
        "Search the web for guidance on what you are doing now (the official documentation, the makers' guides and examples, " +
        "for the version in use) before your next try: a better way is often one search away.";

    /// <summary>
    /// What the chat says of a note the model was sent, in the user's words. One line had served them all, "Asked the
    /// model to look this up with web_search before its next try": it stood after a folder listing and after a key
    /// press, which a note about the model's own doubt went out with ("this" was nothing the user could see), and
    /// for the note that says to stop searching and try (2026-11-12).
    /// </summary>
    internal static string NoticeFor(string note) => note switch
    {
        QuestionNote => "The model wasn't sure how something is done; asked it to look that up rather than go by memory.",
        IgnoredNote => "Another try failed with the error before it never looked up; asked the model to search for it before trying again.",
        RepeatNote => "The same error came up again; asked the model to look it up before trying again.",
        NameNote => "The error says a name doesn't exist here; asked the model to look up the right one for the version in use.",
        FirstNote => "That failed with nothing looked up yet; asked the model to look up how it is done before trying again.",
        DoubtNote => "By the model's own account its last try didn't work; asked it to look up how this is done before the next.",
        UserDoubtNote => "You said the result isn't right yet; asked the model to look up how this is done before trying again.",
        _ when note.Contains("look-ups since your last try", StringComparison.Ordinal) => "Many look-ups in a row; told the model to try it on the installed version now.",
        _ when note.Contains("isn't a command here", StringComparison.Ordinal) => "A program it called isn't installed or isn't on the PATH; asked the model to find it or look up how to get it.",
        _ when note.Contains("tries have failed since", StringComparison.Ordinal) => "Several tries have failed with nothing looked up; asked the model to search before the next one.",
        _ when note.Contains("Time to look things up", StringComparison.Ordinal) => "Several tries with nothing looked up; asked the model to search for guidance before the next.",
        _ => "Asked the model to look this up with web_search before its next try.",
    };

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, int> _seen = [];
    private readonly Queue<int> _failures = new();
    private int _calls, _triesSince, _checkSaidAt, _lookUpsSinceTry, _lookedUpAt = -SearchGap;
    private TimeSpan _worked;
    private DateTime? _tick;
    private bool _lookedUp, _firstSaid, _tried, _triedInRun, _checkSaid;
    private string? _pending;

    /// <summary>The reply whose calls are running looks something up (see <see cref="Read"/>).</summary>
    private bool _searching;

    /// <summary>
    /// The last failure's note said to look its error up (<see cref="_noted"/>), the model's next
    /// reply went on without doing so (<see cref="_ignored"/>), or a question of its own got
    /// <see cref="QuestionNote"/> since it last looked anything up (<see cref="_asked"/>).
    /// </summary>
    private bool _noted, _ignored, _asked;

    /// <summary>
    /// The program and version the user's request names ("Blender 5.2"): with one, there is an
    /// installed version to ask after a long search (see <see cref="LongSearchNote"/>).
    /// </summary>
    public string? Subject { get; private set; }

    internal static string? SubjectIn(string request) =>
        NamedVersion().Match(request) is { Success: true } m ? m.Value : null;

    /// <summary>
    /// A new run, on the user's message: the clock only counts time spent working, and a message
    /// saying the result of the run before isn't right counts as a failed try.
    /// </summary>
    public void StartRun(string? userText)
    {
        (_tick, _lookedUp, _firstSaid, _tried) = (_now(), false, false, false);
        if (userText is not null && SubjectIn(userText) is { } subject)
            Subject = subject;
        Heard(userText);
        (_triedInRun, _lookUpsSinceTry) = (false, 0);
    }

    /// <summary>A message of the user's, at the start of a run or while it goes on: one saying a result isn't right counts as a failed try.</summary>
    public void Heard(string? userText)
    {
        _checkSaid = false;
        if (_triedInRun && userText is { } text && UserDoubts(text))
            _pending = Failed() ?? UserDoubtNote;
    }

    /// <summary>
    /// Reads a reply before its calls run: a try it calls wrong counts as a failed one, and a
    /// question about how something is done that it goes on without looking up gets
    /// <see cref="QuestionNote"/>, once until it looks something up; either note comes after the
    /// reply's results. Going on past an error it was told to look up makes the next script or
    /// command that fails say so.
    /// </summary>
    public void Read(ChatMessage reply, IReadOnlyList<Tool?> calls)
    {
        _searching = calls.Any(t => t is WebSearchTool or FetchPageTool);
        if (calls.Count == 0)
            return;
        // It is looking something up: what it wondered and the error it was told about are its to search.
        if (_searching)
        {
            _noted = false;
            return;
        }
        var text = reply.Reasoning + "\n" + reply.Content;
        if (_tried && SaysItFailed(text))
        {
            _tried = false;
            _pending = Failed() ?? (_lookedUp || _firstSaid ? (CheckDue() ? CheckNote(_worked, _triesSince) : null) : DoubtNote);
            _firstSaid |= _pending == DoubtNote;
        }
        if (!_asked && _calls - _lookedUpAt >= SearchGap && QuestionIn(text) is not null)
        {
            _pending ??= QuestionNote;
            _asked = true;
        }
        // Told to look an error up, the model went on without it: Nemotron failed 13 of 29 Blender
        // scripts with a note on many of them and never searched.
        else if (_noted)
            _ignored = true;
        _noted = false;
    }

    /// <summary>The note for a call's result, or null.</summary>
    /// <param name="arguments">The call's arguments: what a comfyui or video call does, what a command runs.</param>
    public string? Check(Tool? tool, ToolResult result, string? arguments = null)
    {
        var note = NoteFor(tool, result, arguments);
        // Told to search in the reply that searches, the note came after the results it asked for.
        return _searching && tool is not (WebSearchTool or FetchPageTool) ? null : note;
    }

    private string? NoteFor(Tool? tool, ToolResult result, string? arguments)
    {
        _calls++;
        var now = _now();
        if (_tick is { } tick)
            _worked += now - tick;
        _tick = now;
        if (tool is WebSearchTool or FetchPageTool)
        {
            _failures.Clear();
            (_lookedUp, _worked, _triesSince, _checkSaidAt, _lookedUpAt, _pending) = (true, TimeSpan.Zero, 0, 0, _calls, null);
            (_noted, _ignored, _asked) = (false, false, false);
            _lookUpsSinceTry++;
            // Only where there is something installed to ask: tries made in the task, or a request that
            // names a program's version. One that is all research (laptops compared) has none.
            return (_triedInRun || Subject is not null) && _lookUpsSinceTry >= LongSearch && (_lookUpsSinceTry - LongSearch) % 6 == 0
                ? LongSearchNote(_lookUpsSinceTry) : null;
        }
        // Reading the installed version's own API (inspect.signature, --help) is looking it up too,
        // and what the note on a long search asks for.
        if (Reads(tool, arguments))
            (_worked, _triesSince, _checkSaidAt, _lookUpsSinceTry) = (TimeSpan.Zero, 0, 0, 0);
        var isTry = IsTry(tool, arguments is null ? null : ActionOf(arguments)) && !Reads(tool, arguments) && !OnlyLooks(tool, arguments);
        // Only a run says it is time to look: an edit is the work itself, and a coding task makes many.
        var isRun = isTry && tool is not (WriteFileTool or EditFileTool);
        if (isTry)
        {
            _triedInRun = true;
            _lookUpsSinceTry = 0;
        }
        if (isRun)
            _triesSince++;
        var pending = _pending;
        _pending = null;
        _tried = false;
        // A call the user declined failed for no lack of knowledge.
        if (result.Text == AgentSession.DeclinedText)
            return pending;
        // No note after a run that worked: Qwen 3.8's tests had all passed when it was told it was time to look things up
        // (the user's pick, 2026-11-09). That note waits for a try the model says didn't work (Read).
        if (ErrorLine(result, ErrorsInText(tool)) is not { } line)
        {
            _tried = isTry;
            return pending;
        }
        if (!Answerable(tool, line))
            return pending;

        var signature = Signature(line);
        var count = _seen[signature] = _seen.GetValueOrDefault(signature) + 1;
        var failed = Failed(count);
        if (failed is null && count == 1 && ProgramMissing().Match(line) is { Success: true } missing && !Cmdlet().IsMatch(missing.Groups["name"].Value))
            failed = ProgramNote(missing.Groups["name"].Value);
        else if (failed is null && count == 1 && NamesWhatIsNotThere(line))
            failed = NameNote;
        // The first script or command that fails in a task, before anything was looked up: in the
        // Blender runs the first script was written from memory, and more failed before a search.
        if (failed is null && !_lookedUp && !_firstSaid && ErrorsInText(tool))
        {
            _firstSaid = true;
            failed = FirstNote;
        }
        // The next script or command that fails after the model went on past an error it was told to look up.
        if (_ignored && ErrorsInText(tool))
        {
            _ignored = false;
            failed ??= IgnoredNote;
        }
        if (failed is not null && Searchable(line))
            _noted = true;
        return failed ?? pending;
    }

    /// <summary>
    /// Whether a try the model says didn't work gets <see cref="CheckNote"/>: after ten minutes of runs, or
    /// <see cref="CheckBurst"/> quick ones, with nothing looked up; once for each message of the user's, and
    /// <see cref="CheckBurst"/> runs after the last time. Never after a run that worked (the user's pick, 2026-11-09).
    /// </summary>
    private bool CheckDue()
    {
        if (_checkSaid || !(_worked >= CheckAfter && _triesSince >= CheckTries || _triesSince >= CheckBurst))
            return false;
        if (_checkSaidAt > 0 && _triesSince - _checkSaidAt < CheckBurst)
            return false;
        (_checkSaidAt, _checkSaid) = (_triesSince, true);
        return true;
    }

    /// <summary>Counts a failed try; the note for a failure seen before or for one too many since the last look-up, or null.</summary>
    private string? Failed(int seen = 1)
    {
        _failures.Enqueue(_calls);
        while (_failures.Peek() <= _calls - Window)
            _failures.Dequeue();
        return seen == 2 ? RepeatNote : _failures.Count >= Tries ? TriesNote(_failures.Count) : null;
    }

    /// <summary>
    /// Calls that try to get something done, as against looking or reading: running code or a
    /// command, writing a file or an edit of a sound, a ComfyUI job, making a piece of a video.
    /// </summary>
    internal static bool IsTry(Tool? tool, string? action) => tool switch
    {
        ShellTool or SshTool or WriteFileTool or EditFileTool or EditSoundTool => true,
        McpTool mcp => mcp.Risk != ToolRisk.ReadOnly,
        ComfyTool => action is "run",
        VideoEditorTool => action is "frame" or "shot" or "sound" or "join",
        _ => false,
    };

    /// <summary>Whether a command only reads how the installed program or library works: its signatures, help or members.</summary>
    internal static bool Reads(Tool? tool, string? arguments) =>
        tool is ShellTool or SshTool && arguments is not null && Introspects().IsMatch(arguments);

    [GeneratedRegex(@"inspect\.(signature|getsource|getdoc|getmembers)|\bhelp\(|\.__doc__|--help\b|\bGet-Help\b|\bGet-Member\b|\bpip3? show\b", RegexOptions.IgnoreCase)]
    private static partial Regex Introspects();

    /// <summary>Whether a command only looks at files or at how things stand: no try at anything (see <see cref="ReadOnlyCommand"/>).</summary>
    internal static bool OnlyLooks(Tool? tool, string? arguments) =>
        tool is ShellTool or SshTool && arguments is not null &&
        ToolCallRepair.AsObject(arguments)?["command"] is JsonValue v && v.TryGetValue<string>(out var command) && ReadOnlyCommand.Is(command);

    /// <summary>The action argument of a call's arguments, if it has one.</summary>
    internal static string? ActionOf(string arguments) =>
        ToolCallRepair.AsObject(arguments)?["action"] is JsonValue v && v.TryGetValue<string>(out var a) ? a.Trim().ToLowerInvariant() : null;

    /// <summary>
    /// The last question in a reply's thinking or text about how something is done, what it is
    /// called or whether a program has it, or a statement of not knowing that; null when there is
    /// none. Questions about the screen, the user or how a result looks are no matter for a search.
    /// </summary>
    internal static string? QuestionIn(string text)
    {
        string? found = null;
        foreach (var sentence in Sentences().Split(Plain(text)))
        {
            // A search it means to make is the sentence from where it says so: what it would search often
            // follows a colon, and what came before ("The value on screen didn't change: let me search
            // the web for...") is no part of it. A question is the part of a sentence that asks: "Node 1
            // loads Pony — wait, which checkpoint should I use?"
            var asked = LookUpIntent().Match(sentence) is { Success: true } intent && MeansToLookUp(sentence) is { } what && Words(what).Count >= 2
                ? sentence[intent.Index..].Trim().Trim('*', '_', '-', ' ')
                : Clauses().Split(sentence).Select(c => c.Trim().Trim('*', '_', '`', '-', ' ', '"', '\''))
                    .LastOrDefault(c => (c.EndsWith('?') && Asks().IsMatch(c)) || DoesntKnow().IsMatch(c));
            // "How should I proceed with this?" names nothing to search for.
            if (asked is null || asked.Length is < 20 or > 300 || Screenish().IsMatch(asked) || Words(asked).Count < 3)
                continue;
            found = asked;
        }
        return found;
    }

    /// <summary>
    /// What a clause says the model means to look up ("Let me do a quick web search for Blender
    /// 5.2's geometry nodes Python API", "I need to check the node names in Blender 5.2"), or null.
    /// In the Blender runs it said so, then wrote the script from memory: "Actually, let me think
    /// about what I know".
    /// </summary>
    internal static string? MeansToLookUp(string clause) =>
        LookUpIntent().Match(clause) is { Success: true } m && !LocalThing().IsMatch(m.Groups["what"].Value) ? m.Groups["what"].Value : null;

    /// <summary>
    /// Whether an error is one a search answers, so that going on past it without one makes the
    /// next failure say so: an exception a program or library raised, or a name that isn't there.
    /// A command line's own slips (PowerShell's quoting, a path cut at its space) aren't: searched
    /// for Nemotron as "Unexpected token 'version' in expression or statement Blender 5.2", they
    /// brought pages about nothing it was doing.
    /// </summary>
    internal static bool Searchable(string line) =>
        !ShellSlip().IsMatch(line) && (Raised().IsMatch(line) || NamesWhatIsNotThere(line));

    /// <summary>
    /// Whether a page could say anything about this failure. Sloperator's own tools fail on how they
    /// were called or on a file that isn't there; a command that ran out of time or tripped on its own
    /// quoting says nothing about the program. A program that isn't on this PC is worth a look: in
    /// a Unity run "unity : The term 'unity' is not recognized" brought the way to install it.
    /// </summary>
    internal static bool Answerable(Tool? tool, string line) =>
        tool is ShellTool or SshTool or McpTool or ComfyTool or VideoEditorTool &&
        !line.StartsWith("Timed out after", StringComparison.Ordinal) &&
        (!ShellSlip().IsMatch(line) || ProgramMissing().IsMatch(line)) &&
        !PathMissing().IsMatch(line);

    // A file or folder at a path that isn't there, as .NET, Python, Node and the shells say it: a page knows
    // nothing of the user's files. Sonnet 5.5's script read "sample.txt" from the process's folder ("Exception
    // calling "ReadAllText" … Could not find file 'C:\Users\user\sample.txt'") and was told to search the web
    // before its next try. A bare name ('ffmpeg') can be a program that isn't installed, and still counts.
    [GeneratedRegex(@"(could not find (file|a part of the path)|cannot find path|no such file or directory|ENOENT\b)[^'""]*['""][^'""]*[\\/.][^'""]*['""]",
        RegexOptions.IgnoreCase)]
    private static partial Regex PathMissing();

    [GeneratedRegex(@"^(?<name>[\w-]+)(\.exe)? : The term '\k<name>(\.exe)?' is not recognized", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramMissing();

    // A PowerShell command's Verb-Noun name (Get-Thing), where a program's is lower case (python, yt-dlp, docker-compose).
    [GeneratedRegex(@"^[A-Z][a-z]+-[A-Z]")]
    private static partial Regex Cmdlet();

    [GeneratedRegex(@"^([A-Za-z_]\w*\.)*[A-Z]\w*(Error|Exception)\b")]
    private static partial Regex Raised();

    [GeneratedRegex(@"^[\w-]+ : |\bUnexpected token\b|syntax is incorrect|missing the terminator|\bMissing (expression|closing|argument)\b|ParserError|is not recognized as the name of a cmdlet|positional parameter cannot be found",
        RegexOptions.IgnoreCase)]
    private static partial Regex ShellSlip();

    private static HashSet<string> Words(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 2 && !Common.Contains(w)).ToHashSet();

    private static readonly HashSet<string> Common =
        ["the", "and", "how", "can", "could", "should", "would", "does", "what", "which", "this", "that", "with", "for", "not", "sure", "know",
         "there", "way", "any", "into", "from", "you", "use", "proceed", "task", "next", "step", "now", "here", "then", "need", "want", "get",
         "done", "doing", "thing", "things", "it's", "i'm", "will", "work", "best", "about", "right", "correct", "exactly",
         "wait", "but", "hmm", "also", "actually", "maybe", "well", "okay", "problem", "issue", "specific", "particular"];

    // Curly apostrophes as straight ones, so "doesn’t" reads as "doesn't".
    private static string Plain(string text) => text.Replace('’', '\'').Replace('‘', '\'');

    /// <summary>Whether the model's words say its last try didn't do what it should.</summary>
    internal static bool SaysItFailed(string text) => Doubts().IsMatch(Plain(text));

    /// <summary>Whether the user's message says the last result isn't right.</summary>
    /// <remarks>
    /// "Instead of" in a sentence that asks for a change ("Make the numbers a bar chart instead of a table") is no
    /// doubt: taken for one, Haiku and Sonnet were told the result wasn't right and searched the web, Haiku for
    /// python-pptx, after they had made the change (2026-11-13). "It moves left instead of right" still is one.
    /// </remarks>
    internal static bool UserDoubts(string text)
    {
        var plain = ChangeInsteadOf().Replace(Plain(text), m => Regex.Replace(m.Value, @"\binstead of\b", "in place of", RegexOptions.IgnoreCase));
        return SaysItFailed(plain) || UserSaysWrong().IsMatch(plain);
    }

    // A sentence that starts by asking for a change and has "instead of" in it.
    [GeneratedRegex(@"(?:^|[.!?\n]\s*)(?:(?:please|now|also|and|then|ok|okay|great|thanks)[,!]?\s+)*(?:make|use|put|show|change|turn|set|add|write|give|convert|draw|display|switch|replace|move|keep|have|let|create|format|colou?r|do|go\s+with|try|swap|render|plot|chart|list|sort|send)\b[^.!?\n]*\binstead\s+of\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChangeInsteadOf();

    /// <summary>
    /// Tools that run code report its errors as their output: an MCP server's "Error executing
    /// code: ..." comes as an ordinary result, and Blender run with a script prints the script's
    /// traceback and exits with 0.
    /// </summary>
    private static bool ErrorsInText(Tool? tool) => tool is ShellTool or SshTool or McpTool;

    private const string Traceback = "Traceback (most recent call last):";

    /// <summary>
    /// The first line of the error that says what went wrong; null when the result isn't an
    /// error. A PowerShell error record counts even when the script went on and its exit code was
    /// 0, and with <paramref name="errorsInText"/> so do a Python traceback and output that begins
    /// with "Error". For a traceback it is the line under its stack ("AttributeError: ..."): the
    /// one above is the same for every Python error.
    /// <para>
    /// After a command's exit code the output is mostly what it printed, not its error: read as an
    /// error, `using System;` from two files read through the shell came "up before in this chat".
    /// So a record's own line goes first, then a line that says something went wrong, and a command
    /// with none (`git grep` finding nothing, a check with -ErrorAction SilentlyContinue) didn't fail.
    /// </para>
    /// </summary>
    internal static string? ErrorLine(ToolResult result, bool errorsInText = false)
    {
        var text = result.Text;
        var traceback = text.LastIndexOf(Traceback, StringComparison.Ordinal);
        if (!result.IsError && !text.Contains("FullyQualifiedErrorId", StringComparison.Ordinal) &&
            !(errorsInText && (traceback >= 0 || text.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase))))
            return null;
        // The stack's lines are indented; the error follows them. mcp-for-blender sends the
        // traceback inside JSON, on one line with its line breaks written as \n.
        var stack = traceback >= 0 ? text[(traceback + Traceback.Length)..] : "";
        if (stack.StartsWith("\\n", StringComparison.Ordinal))
            stack = JsonEscape().Replace(stack, m => m.Groups[1].Value switch { "n" => "\n", "t" => "\t", "r" => "", var c => c });
        if (traceback >= 0 && FirstMeaningful(stack.Split('\n').Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0]))) is { } raised)
            return raised;
        var lines = text.Split('\n');
        if (!text.StartsWith("Exit code ", StringComparison.Ordinal))
            return FirstMeaningful(lines);
        var meant = lines.Select(l => l.Trim()).Where(l => l.Length >= 8 && !Noise().IsMatch(l)).ToList();
        return (text.Contains("FullyQualifiedErrorId", StringComparison.Ordinal) ? meant.Find(l => CmdletError().IsMatch(l)) ?? RecordLine(lines) : null) ?? meant.Find(l => SaysError().IsMatch(l) && !CodeLine().IsMatch(l));
    }

    /// <summary>
    /// What a PowerShell error record says when no cmdlet is named before it (a .NET call's "Exception calling
    /// "ReadAllText" …", a throw): the line above where it happened ("At x.ps1:4 char:1", then "+ " and the code).
    /// Without it the first line of the output that said something failed was taken for the error: a test run's own
    /// "1 failed" above the record of a file that wasn't there, and Sonnet 5.5 was told to search the web (2026-11-12).
    /// </summary>
    private static string? RecordLine(string[] lines)
    {
        var echo = Array.FindIndex(lines, l => l.StartsWith("+ ", StringComparison.Ordinal));
        for (var i = echo - 2; i >= 0; i--)
            if (lines[i].Trim() is { Length: >= 8 } line && !Noise().IsMatch(line))
            {
                // PowerShell wraps a long message, and a long path goes to a line of its own ("…Could not find a part of
                // the path" / "'C:\…\chat.json'."): with the line above it, where that names a path that isn't there.
                var whole = i > 0 ? lines[i - 1].Trim() + " " + line : line;
                return !PathMissing().IsMatch(line) && PathMissing().IsMatch(whole) ? whole : line;
            }
        return null;
    }

    // A file printed before the failure: "LogExtension.Error($"Popup not found");" is no error of the command's.
    [GeneratedRegex(@"^(//|/\*|\*|\#)|[;{}]$")]
    private static partial Regex CodeLine();

    // PowerShell's error record, in any language: "Get-Content : Не удается найти путь".
    [GeneratedRegex(@"^[\w-]+ : ")]
    private static partial Regex CmdletError();

    [GeneratedRegex(@"\b(\w*error|\w*exception|failed|failure|fatal|denied|cannot|can't|could not|couldn't|unable to|not found|not recognized|no such|invalid|unknown|unexpected|unrecognized|missing|timed out|refused)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SaysError();

    [GeneratedRegex(@"\\(.)")]
    private static partial Regex JsonEscape();

    private static string? FirstMeaningful(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length >= 8 && !Noise().IsMatch(l));

    /// <summary>The error line with its numbers made general, so the same error matches however the call differed (another line, another cell).</summary>
    internal static string Signature(string errorLine)
    {
        var line = Spaces().Replace(Numbers().Replace(errorLine.ToLowerInvariant(), "#"), " ");
        return line.Length > 160 ? line[..160] : line;
    }

    /// <inheritdoc cref="Signature(string)"/>
    internal static string? Signature(ToolResult result, bool errorsInText = false) =>
        ErrorLine(result, errorsInText) is { } line ? Signature(line) : null;

    /// <summary>
    /// Whether the error says a name doesn't exist: an attribute, method, argument, enum value,
    /// node type or command the model remembered from another version, or made up.
    /// </summary>
    internal static bool NamesWhatIsNotThere(string errorLine) =>
        // Python's "'NoneType' object has no attribute 'modifiers'" says a variable was empty, not that a name is wrong.
        MissingName().IsMatch(errorLine) && !errorLine.Contains("'NoneType' object", StringComparison.Ordinal);

    // Lines that say where, not what: exit codes, PowerShell's "At line:3 char:5", "+ ..." echoes, ~~~ markers.
    [GeneratedRegex(@"^(exit code -?\d+|at line:\d+|at [a-z]:\\.*:\d+ char:\d+|\+|~+$|screenshot)", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}.+-]*")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?<=[.!?])\s+|\n")]
    private static partial Regex Sentences();

    // A program and its version as people write them: "Blender 5.2", "Unity 6", "LTX-2.5", "moviepy 2.x".
    // Only a dotted or joined version, so "Make 3 cubes" names no program.
    [GeneratedRegex(@"\b[A-Za-z][\w+-]*[A-Za-z](\s+v?\d+\.(\d+|x)(\.\d+)*|-v?\d+(\.(\d+|x))*)\b")]
    private static partial Regex NamedVersion();

    [GeneratedRegex(@"\s+[—–-]+\s+|[:;]\s+")]
    private static partial Regex Clauses();

    // Python, Blender, C#, PowerShell, JavaScript, GDScript and command-line tools, as they say it.
    [GeneratedRegex(@"has no attribute|is not defined|not declared|no module named|cannot import name|unexpected keyword|positional argument|" +
                    @"\b(key|enum|attribute|property|method|function|module|command|option|parameter|argument|identifier|member|field|node|socket|type)\b[^.;]{0,80}\bnot found|" +
                    @"(could|can) ?not be found|unable to find type|cannot find (module|an overload|type|symbol)|" +
                    @"does not contain an? (definition|method|property|member)|does not exist in the current context|" +
                    @"is not recognized as (the name|an internal or external command)|is not a function|nonexistent (function|method|property)|\btype \S+ undefined\b|" +
                    @"\bunknown (option|argument|parameter|command|flag|property|attribute|field|key|type|function|method|keyword|encoder|decoder|format|node|socket)|" +
                    @"\bunrecognized (option|argument|command|parameter)|\binvalid (choice|option|keyword|enum)|\bno such (option|command|method|property|attribute|module|function)",
        RegexOptions.IgnoreCase)]
    private static partial Regex MissingName();

    // The model saying a try didn't do what it should: nothing failed, it just didn't work.
    [GeneratedRegex(@"\bstill\s+(\w+\s+){0,3}?(wrong|broken|static|frozen|stuck|missing|empty|blank|the same|not|no|isn't|doesn't|didn't|won't|fails?|failing|failed)\b|" +
                    @"\b(didn't|did not|doesn't|does not|isn't|is not|wasn't|was not|won't|aren't|are not)\s+(work|working|help|fix|change|move|moving|follow|following|match|matching|look right|come out right|do (anything|what))\b|" +
                    @"\bno (visible )?(change|effect|difference|improvement|motion|movement) (in|at all|to|from)\b|\b(had|has|made|makes) no (effect|difference)\b|" +
                    @"\bnot what (i|we|the user|they|you) (wanted|asked|expected|intended|meant)\b|" +
                    @"\b(try|use|need) (a |an )?(different|another|other|new) (approach|way|method|node|workflow|technique|strategy)\b|" +
                    @"\b(that|this|it) (failed|did not work|didn't work|doesn't work)\b|\bfailed (again|to (work|follow|move|change))\b|" +
                    // What a video does wrong, as a look or a measurement shows it: "rolls the wrong way", "barely moves".
                    @"\b(wrong|opposite) (direction|way|side)\b|\bopposite of what\b|\b(barely|hardly) (moves?|moving|changes?)\b|\b(is|stays|looks) (static|frozen)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Doubts();

    // What else a user writes when a result isn't right: "Dude it's still ...", "wrong again", "nope",
    // or what it should have done: "it should fade from transparent", "it was supposed to move".
    [GeneratedRegex(@"\bstill\b|\b(wrong|worse|nope|not right|isn't right|no good|try again|redo it|start over)\b|" +
                    @"\b(it|they|that|this|he|she) (should|shouldn't|should not|was supposed to|were supposed to|is supposed to)\b|\binstead of\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex UserSaysWrong();

    // A question about how something is done, what it is called, which one does it, whether it exists.
    [GeneratedRegex(@"\bhow (do|does|can|could|should|would|to)\b(?! (it|this|that|they|things) (look|seem|feel)\b)|" +
                    @"\bhow (is|are) [^?]{0,60}\b(done|made|set|configured|called|named|used|enabled|controlled|specified|passed|handled|supported|defined|installed|loaded|connected)\b|" +
                    @"\bwhat('s| is| are| was) the (right|correct|proper|best|usual|official|recommended|exact|actual|current|new) (way|name|syntax|node|setting|parameter|argument|option|value|method|function|api|format|workflow|approach|command|flag|field|property|input|output|class)|" +
                    @"\bwhat('s| is| are) [^?]{1,50}\b(called|named)\b|" +
                    @"\b(which|what) (nodes?|models?|settings?|parameters?|arguments?|options?|functions?|methods?|api|loras?|workflows?|samplers?|schedulers?|flags?|propert(y|ies)|attributes?|class(es)?|modules?|packages?|librar(y|ies)|codecs?|formats?|versions?|templates?|checkpoints?|controlnets?|preprocessors?|inputs?|sockets?|operators?|commands?|fields?|keys?) " +
                    @"(do|does|should|would|can|could|is|are|was|were|to|i|we|handles?|supports?|controls?|makes?|gives?)\b|" +
                    @"\bis there (a|an|any) (way|node|setting|option|parameter|function|method|api|built-in|builtin|tool|template|model|lora|flag|feature|command|official|example)\b|" +
                    @"\bdoes (?!(it|this|that|they)\b)(the \w+|\w+) (support|accept|allow|handle|have an?|expose|provide)\b|" +
                    @"\b((is|are) (it|they|these|those|the \w+)|it's|they're|it is|they are) (built into|part of|included in|supported (by|in))\b|" +
                    // A library's default, which the model then answers from memory: "the default for the axis's `delete` attribute is True?"
                    @"\bdefault (value |setting )?(for|of) [^?]{3,80}\b(is|be)\b|\bdefaults? to\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Asks();

    // Not knowing, said as a statement: "I'm not sure how LTX takes a reference video." What isn't known starts at "what".
    // "Whether" and "if" only for what a program can do: "I don't know if the original s3 was good" is no question for the web.
    [GeneratedRegex(@"\b((i'm|i am|not) (not )?(sure|certain)|i (don't|do not) (know|remember|recall)|i (need|have|will need) to (figure|find) out|unclear) " +
                    @"((?<what>how|what|which|where)\b|(?<what>whether|if)\b[^,;]*\b(supports?|accepts?|allows?|handles?|has|have|exists?|works? with|is built into|comes with)\b|" +
                    @"(?<what>the (exact|right|correct|proper) (name|syntax|parameter|argument|option|setting|node|function|method|api|flag|command|format|signature|value)s?)\b)",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex DoesntKnow();

    // Meaning to look something up on the web: a web search, the docs, or names in a version
    // ("Let me search for TTS templates" is a search in ComfyUI, which the model then makes).
    [GeneratedRegex(@"\b(let me|let's|i'll|i will|i should|i need to|i have to|i could|i must|i'd better|i want to)\s+(quickly\s+|first\s+|just\s+)?(" +
                    @"((do|run|make) an?\s+)?((quick|fast)\s+)?(web|online|internet) search\s+((for|on|about)\s+)?(?<what>\S.*)|" +
                    @"(search|look|check|look up)\s+(online|on the web|the web|the internet|the docs|the documentation|the api docs)\s+((for|about|on)\s+)?(?<what>\S.*)|" +
                    @"google\s+(?<what>\S.*)|" +
                    @"(look up|search for|search the web for)\s+(?<what>(the\s+)?((correct|right|exact|current|actual|new)\s+)?((node|api|python|input|socket|parameter)\s+)?" +
                    @"(names?|api|syntax|signatures?|parameters?|arguments?|options?|docs|documentation)\b.*?\b(in|for|of) [\w.+-]+ (v(ersion)?\s*)?\d.*))",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex LookUpIntent();

    // What is looked up on this PC, not on the web: "let me search for the templates", "look up the output folder".
    [GeneratedRegex(@"^((the|my|our|this|that|a|an)\s+)?(templates?|files?|folders?|output|logs?|results?|images?|pictures?|videos?|frames?|status|queue|screenshot|window)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LocalThing();

    // Questions the web can't answer: about the screen, the user, how something looks, what a check
    // would show here, or Sloperator's own tools.
    [GeneratedRegex(@"\b(screen|screenshot|cursor|click|clicked|button|the user|user's|they want|he|she)\b|\b(look|looks|seem|seems)\?$|\blooks? (best|better|good|right|nicer)\b|" +
                    @"\b(determine|tell|check|see|verify|confirm) (whether|if|which)\b|\b(video|comfyui) tool\b|\bthe tool\b|\bsloperator\b|\bstoryboard\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Screenish();
}
