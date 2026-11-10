using System.Text.Json.Nodes;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

public sealed class KillByNameTests
{
    [Theory]
    [InlineData("Get-Process winword -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Process write.exe", "winword")] // qwen3.8, 2026-11-10
    [InlineData("Stop-Process -Name mspaint -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500", "mspaint")]
    [InlineData("Stop-Process -Name \"Photoshop\" -Force", "Photoshop")]
    [InlineData("Stop-Process -Force -Name notepad, 'mspaint'", "notepad,mspaint")]
    [InlineData("taskkill /F /IM EXCEL.EXE", "EXCEL")]
    [InlineData("Stop-Process -Id (Get-Process notepad).Id", "notepad")]
    [InlineData("(Get-Process -Name notepad).Kill()", "notepad")]
    [InlineData("gps chrome | kill", "chrome")]
    // By id, or narrowed down to the ones it started: ends only those.
    [InlineData("Stop-Process -Id 16452 -Force", "")]
    [InlineData("Get-Process notepad | Where-Object { $_.StartTime -gt $t } | Stop-Process", "")]
    [InlineData("Get-Process | ? { $_.MainWindowTitle -like '*haiku*' } | Stop-Process", "")]
    [InlineData("taskkill /PID 1234 /F", "")]
    [InlineData("taskkill /IM notepad.exe /FI \"WINDOWTITLE eq haiku*\"", "")]
    // Only looks: no kill in the statement.
    [InlineData("Get-Process notepad -ErrorAction SilentlyContinue | Measure-Object; Stop-Process -Id 12", "")]
    [InlineData("Get-Process | Sort-Object CPU -Descending | Select-Object -First 5", "")]
    [InlineData("Stop-Process -Id $p.Id", "")]
    public void Commands_that_end_a_program_by_its_name_are_told(string command, string programs) =>
        Assert.Equal(programs, string.Join(',', KillByName.Programs(command)));

    [Fact]
    public async Task Run_shell_ends_no_program_by_name_while_the_user_has_windows_of_it()
    {
        var since = new List<(string, DateTime)>();
        var ctx = new ToolContext
        {
            Platform = new FakePlatform(),
            Settings = new AgentSettings(),
            WorkingDirectory = Path.GetTempPath(),
            RunStarted = new DateTime(2026, 11, 10, 13, 0, 0),
            WindowsOpenBefore = (name, before) => { since.Add((name, before)); return name == "notepad" ? 1 : 0; },
        };

        var refused = await new ShellTool().ExecuteAsync(new JsonObject { ["command"] = "Stop-Process -Name notepad -Force" }, ctx, CancellationToken.None);
        Assert.True(refused.IsError);
        Assert.StartsWith("Not run: ending notepad by name would also close the user's own notepad windows", refused.Text);
        Assert.Equal([("notepad", new DateTime(2026, 11, 10, 13, 0, 0))], since);

        // A program the user has no window of is the agent's own to end.
        var ran = await new ShellTool().ExecuteAsync(new JsonObject { ["command"] = "Stop-Process -Name sloperator-test-nothing -ErrorAction SilentlyContinue; 'ok'" }, ctx, CancellationToken.None);
        Assert.DoesNotContain("Not run", ran.Text);
    }
}
