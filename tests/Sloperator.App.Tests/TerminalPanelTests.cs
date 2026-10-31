using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>The Terminal panel: the agent's commands listed as they run, their whole output, lines picked to ask about.</summary>
public sealed class TerminalPanelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-terminal-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Commands_show_as_they_run_and_the_newest_is_followed()
    {
        HeadlessApp.Run(() =>
        {
            var log = new CommandLog();
            using var vm = new TerminalViewModel(null, _dir, log);
            Assert.False(vm.HasCommands);

            var first = log.Start("dir", "This PC", @"C:\work");
            first.Add("a.txt");
            first.End(0);
            Refresh(vm);
            Assert.Single(vm.Commands);
            Assert.Same(vm.Commands[0], vm.Selected);
            Assert.Equal("a.txt", vm.Document!.Text);
            Assert.StartsWith("This PC · exit 0", vm.Commands[0].Status);

            // A second one, on a server, still running: shown as it starts (Watch), its lines as they come.
            var second = log.Start("journalctl -n 3\nuptime", "admin@web1", "/var/log");
            second.Add("one");
            Refresh(vm);
            Assert.Same(vm.Commands[1], vm.Selected);
            Assert.Equal("journalctl -n 3", vm.Commands[1].Command);
            Assert.StartsWith("admin@web1 · running", vm.Commands[1].Status);
            second.Add("two");
            second.Add("three");
            Refresh(vm);
            Assert.Equal("one\ntwo\nthree", vm.Document!.Text);
            Assert.Contains("3 lines", vm.Details);

            second.End(1);
            Refresh(vm);
            Assert.True(vm.Commands[1].Failed);
            Assert.False(vm.Commands[0].Failed);

            // Watch off: a new command doesn't take the view from the one being read.
            vm.Watch = false;
            vm.Selected = vm.Commands[0];
            log.Start("ver", "This PC", null).End(0);
            Refresh(vm);
            Assert.Equal(3, vm.Commands.Count);
            Assert.Same(vm.Commands[0], vm.Selected);
            return 0;
        });
    }

    [Fact]
    public void Lines_picked_go_to_the_model_numbered_with_the_command()
    {
        HeadlessApp.Run(() =>
        {
            var log = new CommandLog();
            using var vm = new TerminalViewModel(null, _dir, log);
            var run = log.Start("npm test", "This PC", @"C:\game");
            foreach (var line in new[] { "> test", "PASS a", "FAIL b", "  expected 3", "done" })
                run.Add(line);
            run.End(1);
            Refresh(vm);

            vm.PickLines(3, 4);
            var file = Assert.Single(Directory.GetFiles(_dir));
            Assert.Equal("FAIL b\n  expected 3", File.ReadAllText(file));
            var (via, context) = vm.About([file])!.Value;
            Assert.Equal("Terminal · npm test · lines 3-4", via);
            Assert.Contains("npm test", context);
            Assert.Contains("This PC, in C:\\game, exit 1", context);
            Assert.Contains("3: FAIL b\n4:   expected 3\n]", context);

            // Another pick replaces it, its file deleted; unpicked, nothing is told and no file is left.
            vm.PickLines(2, 2);
            Assert.False(File.Exists(file));
            Assert.Equal("Line 2", TerminalViewModel.Place(2, 2));
            Assert.True(vm.Unpick());
            Assert.Empty(Directory.GetFiles(_dir));
            Assert.Null(vm.About([]));
            return 0;
        });
    }

    [Fact]
    public void The_panel_shows_the_output_in_the_code_editors_view()
    {
        HeadlessApp.Run(() =>
        {
            var log = new CommandLog();
            var run = log.Start("echo hi", "This PC", null);
            run.Add("hi");
            run.End(0);
            using var vm = new TerminalViewModel(null, _dir, log);
            var window = new Window { Width = 880, Height = 500, Content = new TerminalPanel { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var output = window.GetVisualDescendants().OfType<CodeView>().Single();
            Assert.True(output.IsReadOnly);
            Assert.Equal("hi", output.Document.Text);
            window.Close();
            return 0;
        });
    }

    /// <summary>The notes the log posted to the window's thread, taken.</summary>
    private static void Refresh(TerminalViewModel vm) => Dispatcher.UIThread.RunJobs();
}
