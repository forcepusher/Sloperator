using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sloperator.Core.Tools;

/// <summary>
/// The commands the agent ran in a chat (run_shell on this PC, ssh on a server), in order, each with
/// all it printed: the Terminal panel shows them as they run. The model gets only a command's head and
/// tail; this keeps every line. Kept in the chat's folder (<see cref="KeepIn"/>), so a chat opened
/// again shows its commands.
/// </summary>
public sealed class CommandLog
{
    private readonly Lock _gate = new();
    private readonly List<CommandRun> _runs = [];
    private string? _store;

    /// <summary>Raised on the agent's thread when a command starts, prints a line or ends; the panel reads what is new.</summary>
    public event Action<CommandLog>? Changed;

    /// <summary>The commands so far, oldest first.</summary>
    public IReadOnlyList<CommandRun> Runs
    {
        get
        {
            lock (_gate)
                return _runs.ToList();
        }
    }

    /// <summary>A command starting: <paramref name="where"/> is "This PC" or the server ("admin@web1"), <paramref name="folder"/> where it runs.</summary>
    public CommandRun Start(string command, string where, string? folder)
    {
        CommandRun run;
        lock (_gate)
        {
            run = new CommandRun(this, _runs.Count + 1, command, where, folder, DateTime.Now);
            _runs.Add(run);
        }
        Changed?.Invoke(this);
        return run;
    }

    internal void Touched() => Changed?.Invoke(this);

    internal void Ended()
    {
        Changed?.Invoke(this);
        Save();
    }

    /// <summary>Keeps the log in <paramref name="file"/> from now on (the chat's, once it has one), and writes it there now.</summary>
    public void KeepIn(string file)
    {
        lock (_gate)
        {
            if (_store is not null)
                return;
            _store = file;
        }
        Save();
    }

    /// <summary>The log kept in <paramref name="file"/>, kept there from now on; an empty one when there is none.</summary>
    public static CommandLog Load(string file)
    {
        var log = new CommandLog { _store = file };
        try
        {
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file))?["commands"] is not JsonArray commands)
                return log;
            foreach (var c in commands.OfType<JsonObject>())
            {
                var run = new CommandRun(log, log._runs.Count + 1, (string?)c["command"] ?? "", (string?)c["where"] ?? "", (string?)c["folder"],
                    (DateTime?)c["started"] ?? DateTime.MinValue);
                foreach (var line in c["lines"]?.AsArray() ?? [])
                    run.Lines.Add((string?)line ?? "");
                run.Restore((int?)c["exitCode"], c["seconds"] is { } s ? TimeSpan.FromSeconds((double)s) : null, (string?)c["ending"]);
                log._runs.Add(run);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
        }
        return log;
    }

    /// <summary>Writes the commands that ended to the chat's file (one still running is written when it ends).</summary>
    private void Save()
    {
        string? file;
        JsonArray commands;
        lock (_gate)
        {
            if ((file = _store) is null || _runs.Count == 0)
                return;
            commands = new JsonArray(_runs.Select(r => (JsonNode)r.ToJson()).ToArray());
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, new JsonObject { ["commands"] = commands }.ToJsonString());
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A command the agent ran: what it was, where, all it printed, and how it ended. Lines come from the
/// tool's thread while the panel reads them on its own: <see cref="LinesFrom"/> hands over a copy.
/// </summary>
public sealed class CommandRun
{
    private readonly CommandLog _log;
    private readonly Lock _gate = new();

    internal CommandRun(CommandLog log, int number, string command, string where, string? folder, DateTime started)
    {
        _log = log;
        Number = number;
        Command = command;
        Where = where;
        Folder = folder;
        Started = started;
    }

    /// <summary>Its place in the chat's list, from 1.</summary>
    public int Number { get; }

    public string Command { get; }

    /// <summary>"This PC", or the server it ran on ("admin@web1").</summary>
    public string Where { get; }

    /// <summary>The folder it ran in, when known.</summary>
    public string? Folder { get; }

    public DateTime Started { get; }

    /// <summary>Its exit code once it ended with one; null while it runs, or when it ended without one.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>How long it ran, once it ended.</summary>
    public TimeSpan? Took { get; private set; }

    /// <summary>How it ended when not by exiting: "timed out", "stopped", "the terminal ended"; null otherwise.</summary>
    public string? Ending { get; private set; }

    public bool HasEnded => Took is not null;

    internal List<string> Lines { get; } = [];

    public int LineCount
    {
        get
        {
            lock (_gate)
                return Lines.Count;
        }
    }

    /// <summary>The lines from <paramref name="index"/> on (0 for all), a copy.</summary>
    public IReadOnlyList<string> LinesFrom(int index)
    {
        lock (_gate)
            return index >= Lines.Count ? [] : Lines.GetRange(index, Lines.Count - index);
    }

    /// <summary>A line it printed, as it comes.</summary>
    public void Add(string line)
    {
        lock (_gate)
        {
            if (HasEnded)
                return;
            Lines.Add(line);
        }
        _log.Touched();
    }

    /// <summary>It ended: with <paramref name="exitCode"/>, or as <paramref name="ending"/> says.</summary>
    public void End(int? exitCode, string? ending = null)
    {
        lock (_gate)
        {
            if (HasEnded)
                return;
            ExitCode = exitCode;
            Ending = ending;
            Took = DateTime.Now - Started;
        }
        _log.Ended();
    }

    internal void Restore(int? exitCode, TimeSpan? took, string? ending)
    {
        ExitCode = exitCode;
        // A command that was running when the app closed is over by now, its end unknown.
        Took = took ?? TimeSpan.Zero;
        Ending = took is null ? "not seen to end" : ending;
    }

    internal JsonObject ToJson()
    {
        lock (_gate)
            return new JsonObject
            {
                ["command"] = Command,
                ["where"] = Where,
                ["folder"] = Folder,
                ["started"] = Started,
                ["exitCode"] = ExitCode,
                ["seconds"] = Took?.TotalSeconds,
                ["ending"] = Ending,
                ["lines"] = new JsonArray(Lines.Select(l => (JsonNode?)l).ToArray()),
            };
    }
}
