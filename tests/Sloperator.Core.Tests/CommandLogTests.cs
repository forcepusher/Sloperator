using System.Text.Json.Nodes;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>The Terminal panel's log: every line the agent's commands print, as they print it, kept with the chat.</summary>
public sealed class CommandLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-commands-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ToolContext Ctx(CommandLog log) => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir, Commands = log };

    [Fact]
    public async Task A_shell_command_logs_every_line_the_model_gets_only_the_ends_of()
    {
        var log = new CommandLog();
        var seen = 0;
        log.Changed += _ => Interlocked.Increment(ref seen);
        // 5,000 lines of 20 characters: 100,000 characters, past the 30,000 the model is given.
        var result = await new ShellTool().ExecuteAsync(new JsonObject { ["command"] = "for /L %i in (1,1,5000) do @echo line %i of the output" }, Ctx(log), CancellationToken.None);
        Assert.Contains("lines (", result.Text); // the model's copy left the middle out
        var run = Assert.Single(log.Runs);
        Assert.Equal("This PC", run.Where);
        Assert.Equal(_dir, run.Folder);
        Assert.Equal(0, run.ExitCode);
        Assert.NotNull(run.Took);
        var lines = run.LinesFrom(0);
        Assert.Equal(5000, lines.Count);
        Assert.Equal("line 2500 of the output", lines[2499]);
        Assert.True(seen > 2);

        // One that fails, and one that runs out of time, end as they did.
        await new ShellTool().ExecuteAsync(new JsonObject { ["command"] = "exit /b 3" }, Ctx(log), CancellationToken.None);
        await new ShellTool().ExecuteAsync(new JsonObject { ["command"] = "ping -n 30 127.0.0.1 >nul", ["timeout_seconds"] = 1 }, Ctx(log), CancellationToken.None);
        Assert.Equal(3, log.Runs[1].ExitCode);
        Assert.Null(log.Runs[2].ExitCode);
        Assert.Equal("timed out after 1 s", log.Runs[2].Ending);
    }

    [Fact]
    public void The_log_is_kept_in_the_chats_folder_and_comes_back_with_it()
    {
        var file = Path.Combine(_dir, "chat", "terminal.json");
        var log = new CommandLog();
        var run = log.Start("dir", "This PC", @"C:\work");
        run.Add("a.txt");
        run.Add("");
        run.Add("b.txt");
        // Not kept anywhere before the chat has a file.
        Assert.False(File.Exists(file));
        log.KeepIn(file);
        run.End(0);
        var ssh = log.Start("tail -f app.log", "admin@web1", "/var/log");
        ssh.Add("started");

        var again = CommandLog.Load(file);
        var back = Assert.Single(again.Runs); // the one still running wasn't written yet
        Assert.Equal(["a.txt", "", "b.txt"], back.LinesFrom(0));
        Assert.Equal((0, "This PC", @"C:\work"), (back.ExitCode, back.Where, back.Folder));

        ssh.End(null, "the terminal ended");
        var both = CommandLog.Load(file).Runs;
        Assert.Equal(2, both.Count);
        Assert.Equal(("admin@web1", "the terminal ended", 2), (both[1].Where, both[1].Ending, both[1].Number));
        Assert.Empty(CommandLog.Load(Path.Combine(_dir, "none.json")).Runs);
    }

    [Fact]
    public void Lines_after_the_end_are_left_out()
    {
        var run = new CommandLog().Start("x", "This PC", null);
        run.Add("one");
        run.End(0);
        run.Add("late");
        run.End(1);
        Assert.Equal(["one"], run.LinesFrom(0));
        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.LinesFrom(5));
    }
}

/// <summary>An SSH terminal's output cut into the Terminal panel's lines.</summary>
public sealed class TerminalLinesTests
{
    [Fact]
    public void Lines_come_whole_without_escape_codes_or_sloperators_markers()
    {
        var lines = new List<string>();
        var cut = new Ssh.TerminalLines();
        // Read in pieces that split a line and an escape code; output without a line break before the end marker.
        cut.Feed("__bb_id=4; ( trap 'pwd' EXIT; . /tmp/4.sh )\nhel", lines.Add);
        cut.Feed("lo \x1b[1mwor", lines.Add);
        cut.Feed("ld\x1b[0m\n\nlast without a break\u001eBB-4-0-/home/admin\u001e\n", lines.Add);
        Assert.Equal(["hello world", "", "last without a break"], lines);

        // A progress line drawn over itself shows as it ended.
        cut.Feed("10%\r50%\r100%\n\u001eBB-5-1-/tmp\u001e\n", lines.Add);
        Assert.Equal("100%", lines[^1]);
        Assert.Equal(4, lines.Count);
    }
}
