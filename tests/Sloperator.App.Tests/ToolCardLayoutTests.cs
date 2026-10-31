using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// A tool call's card: a shell command under a short title, open; the screen a click left folded,
/// the picture of a look open.
/// </summary>
public sealed class ToolCardLayoutTests
{
    [Fact]
    public void A_shell_card_has_a_short_title_and_the_command_open_under_it()
    {
        const string command = "Get-ChildItem C:\\Users\\user\\Documents -Recurse -Filter *.xlsx | Select-Object FullName, Length";
        var (title, code) = HeadlessApp.Run(() => Shown(new ToolCallItem("1", "run_shell", "Run: " + command), card =>
            (card.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == "Run a command").Text,
             card.GetVisualDescendants().OfType<SelectableTextBlock>().Single(b => b.Classes.Contains("command")) is var b && b.IsEffectivelyVisible ? b.Text : null)));
        Assert.Equal("Run a command", title);
        Assert.Equal(command, code);
    }

    [Fact]
    public void A_shell_card_says_what_the_command_does_when_the_model_said_it()
    {
        var summary = new Sloperator.Core.Tools.ShellTool().Describe(new System.Text.Json.Nodes.JsonObject
        {
            ["command"] = "Get-ChildItem $env:TEMP\n| Select-Object -First 3",
            ["description"] = "List the newest\nlog files ",
        });
        var item = new ToolCallItem("1", "run_shell", summary);
        Assert.Equal("Run a command · List the newest log files", item.Title);
        Assert.Equal("Get-ChildItem $env:TEMP\n| Select-Object -First 3", item.Command);
        var bare = new ToolCallItem("1", "run_shell", new Sloperator.Core.Tools.ShellTool().Describe(new System.Text.Json.Nodes.JsonObject { ["command"] = "dir", ["description"] = " " }));
        Assert.Equal(("Run a command", "dir"), (bare.Title, bare.Command));
    }

    [Fact]
    public void Any_other_card_keeps_its_summary_as_the_title_and_has_no_command()
    {
        var item = new ToolCallItem("1", "read_file", "Read C:\\notes.txt");
        Assert.Equal("Read C:\\notes.txt", item.Title);
        Assert.False(item.HasCommand);
        Assert.False(new ToolCallItem("1", "ssh", "SSH box: ls").HasCommand);
    }

    [Theory]
    [InlineData(@"Read C:\Users\user\Documents\Sloperator\Hello World\hello.py", "Read hello.py")]
    [InlineData(@"List C:\Users\user\Documents\Sloperator\Hello World", "List Hello World")]
    [InlineData(@"List C:\Users\user\Documents\Sloperator\Hello World\ *.py", "List Hello World *.py")]
    [InlineData(@"Copy C:\a b\x.txt to D:\backup\x old.txt", "Copy x.txt to x old.txt")]
    [InlineData(@"Read C:\a folder with a long name\b.txt", "Read b.txt")]
    [InlineData(@"Write C:\work\notes.md (12 chars)", "Write notes.md (12 chars)")]
    [InlineData(@"Read /home/me/app/main.py on web1", "Read main.py on web1")]
    [InlineData(@"Read ~/app/main.py on web1", "Read main.py on web1")]
    [InlineData(@"Copy \\nas\share\film.mkv to C:\Films", "Copy film.mkv to Films")]
    [InlineData(@"Search C:\code\app for ""C:\x\y""", @"Search app for ""C:\x\y""")]
    [InlineData(@"Compare C:\a\one.txt with C:\b\two.txt", "Compare one.txt with two.txt")]
    [InlineData(@"Install ComfyUI in D:\AI\ComfyUI", "Install ComfyUI in ComfyUI")]
    // What is not a whole path stays: a relative one, a root, a web address, typed words, and
    // what follows ": " (a command over SSH, a query).
    [InlineData(@"Read notes\today.md", @"Read notes\today.md")]
    [InlineData(@"List C:\", @"List C:\")]
    [InlineData("Read https://example.com/docs/page.html", "Read https://example.com/docs/page.html")]
    [InlineData(@"Type ""C:\Users\me\file.txt""", @"Type ""C:\Users\me\file.txt""")]
    [InlineData("SSH web1: cat /etc/nginx/nginx.conf", "SSH web1: cat /etc/nginx/nginx.conf")]
    [InlineData(@"Run a command · List C:\logs\app", "Run a command · List app")]
    public void A_card_shows_its_paths_by_their_names(string summary, string shown) => Assert.Equal(shown, ToolLine.Short(summary));

    [Fact]
    public void A_card_line_is_one_line_with_the_whole_title_in_its_tooltip()
    {
        const string summary = @"Read C:\Users\user\Documents\Sloperator\Hello World\a folder with a long name\a file whose name is far too long to fit on one line beside the tool.txt";
        var (line, tip, height, words) = HeadlessApp.Run(() => Shown(new ToolCallItem("1", "read_file", summary), card =>
        {
            var text = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("Read ", StringComparison.Ordinal) == true);
            var chip = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "read_file");
            return (text.Text, ToolTip.GetTip(text), text.Bounds.Height, chip.Bounds.Height);
        }));
        Assert.Equal("Read a file whose name is far too long to fit on one line beside the tool.txt", line);
        Assert.Equal(summary, tip);
        Assert.True(height < 2 * words, $"one line: {height} high");
    }

    [Fact]
    public void The_time_and_how_the_call_ended_are_over_the_card_on_the_left_as_over_the_thoughts()
    {
        var item = new ToolCallItem("1", "read_file", @"Read C:\notes.txt") { At = new DateTime(2026, 10, 31, 4, 51, 10, DateTimeKind.Local).ToUniversalTime() };
        item.Ended = item.At!.Value.AddMilliseconds(1.2);
        item.State = ToolItemState.Done;
        Assert.Equal("04:51:10 · Done in 1 ms", item.HeadText);
        Assert.Equal("Waiting for your OK", new ToolCallItem("1", "read_file", "x") { At = null, State = ToolItemState.AwaitingApproval }.HeadText);

        var (head, card) = HeadlessApp.Run(() => Shown(item, shown =>
        {
            var line = shown.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == item.HeadText);
            var border = shown.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("card"));
            return (line.TranslatePoint(default, shown)!.Value, border.TranslatePoint(default, shown)!.Value);
        }));
        Assert.Equal(card.X, head.X);
        Assert.True(head.Y < card.Y);
    }

    [Theory]
    [InlineData("mouse_click", false)]
    [InlineData("wait", false)]
    [InlineData("press_keys", false)]
    [InlineData("screenshot", true)]
    [InlineData("zoom", true)]
    public void The_screen_an_action_left_is_folded_and_a_look_is_open(string tool, bool open)
    {
        var (imageShown, folded) = HeadlessApp.Run(() =>
        {
            var item = new ToolCallItem("1", tool, tool) { Screenshot = Picture(), ResultText = "done" };
            return Shown(item, card =>
            {
                var shot = card.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => e.IsVisible && Equals(e.Header, "Screenshot"));
                var image = card.GetVisualDescendants().OfType<Image>().Any(i => i.Source == item.Screenshot && i.IsEffectivelyVisible && i.Bounds.Height > 0);
                return (image, shot is { IsExpanded: false });
            });
        });
        Assert.Equal(open, imageShown);
        Assert.Equal(!open, folded);
    }

    private static Bitmap Picture() => new WriteableBitmap(new Avalonia.PixelSize(64, 40), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    /// <summary>The chat's own templates, out of a main window, showing one card.</summary>
    private static T Shown<T>(ToolCallItem item, Func<Control, T> read)
    {
        var main = new MainWindow();
        var scroll = main.FindControl<ScrollViewer>("ChatScroll")!;
        ((Panel)scroll.Parent!).Children.Remove(scroll);
        var items = scroll.GetLogicalDescendantsOfType<ItemsControl>().First();
        items.ItemsSource = new[] { item };
        var window = new Window { Width = 900, Height = 900, Content = scroll };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        try
        {
            return read(scroll);
        }
        finally
        {
            window.Close();
        }
    }
}
