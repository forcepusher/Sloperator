using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Sloperator.Core.Web;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>The Live preview panel: its bar, what WebView2 missing shows, and what of the page goes with a message.</summary>
public sealed class LivePreviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-live-preview-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ConsoleItem Error(int number, string text, string? source = null, int line = 0) => new(new ConsoleError(text, source, line, 0), number);

    [Fact]
    public void Errors_picked_are_named_by_their_numbers()
    {
        Assert.Equal("Error 2", LivePreviewViewModel.ErrorsCaption([Error(2, "a")]));
        Assert.Equal("Errors 1-3", LivePreviewViewModel.ErrorsCaption([Error(1, "a"), Error(2, "b"), Error(3, "c")]));
        Assert.Equal("2 errors", LivePreviewViewModel.ErrorsCaption([Error(1, "a"), Error(3, "c")]));
    }

    [Fact]
    public void The_errors_picked_go_to_the_model_whole_and_only_while_picked()
    {
        var page = Path.Combine(_dir, "index.html");
        File.WriteAllText(page, "<p>hi</p>");
        HeadlessApp.Run(() =>
        {
            using var vm = new LivePreviewViewModel(null, _dir, Path.Combine(_dir, "webview"));
            vm.Show(page);
            Assert.Equal(new Uri("https://page.sloperator.example/index.html"), vm.Address);
            Assert.Equal(_dir, vm.PageFolder);
            vm.Message("""{"text":"TypeError: x is undefined\n    at https://page.sloperator.example/game.js:4:2","source":"https://page.sloperator.example/game.js","line":4,"column":2}""");
            vm.Message("""{"text":"Sound off"}""");
            vm.Message("not an error");
            Assert.Equal(2, vm.Errors.Count);
            Assert.Equal("2", vm.ErrorCount);

            // Nothing picked: the message is about the page, no errors in it.
            Assert.DoesNotContain("TypeError", vm.About([page])!.Value.Context);

            vm.PickErrors([vm.Errors[0]]);
            var (via, context) = vm.About([page])!.Value;
            Assert.Equal("Live preview · index.html · error 1", via);
            Assert.Contains(page, context);
            Assert.Contains("1. TypeError: x is undefined\n    at game.js:4:2\n]", context);
            Assert.DoesNotContain("Sound off", context);

            // Sent, they are no longer picked; a page loading again clears the list.
            var unpicked = false;
            vm.Unpicked += () => unpicked = true;
            vm.Sent();
            Assert.True(unpicked);
            Assert.DoesNotContain("TypeError", vm.About([page])!.Value.Context);
            vm.Navigated(vm.Address!);
            Assert.Empty(vm.Errors);
            return 0;
        });
    }

    [Fact]
    public void The_bar_keeps_its_buttons_greyed_with_why_and_a_missing_webview2_says_where_to_get_it()
    {
        var page = Path.Combine(_dir, "index.html");
        File.WriteAllText(page, "<p>hi</p>");
        HeadlessApp.Run(() =>
        {
            using var vm = new LivePreviewViewModel(null, _dir, Path.Combine(_dir, "webview"));
            var panel = new LivePreviewPanel { DataContext = vm };
            // Avalonia's stand-in for a browser: WebView2 wants the window's thread of a real app.
            panel.GetLogicalDescendants().OfType<NativeWebView>().Single().AdapterPreference = [Avalonia.Platform.WebViewAdapterType.Headless];
            var window = new Window { Width = 880, Height = 600, Background = Brushes.Black, Content = panel };
            window.Show();
            T Named<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(b => AutomationProperties.GetName(b) == name);
            var reload = Named<Button>("Reload");
            var area = Named<ToggleButton>("Area");
            var watch = Named<ToggleButton>("Watch");
            var open = Named<Button>("Open file");
            var places = new[] { reload, area, watch, open }.Select(c => c.TranslatePoint(new Point(0, 0), window)).ToList();

            // No page: every button there, Reload and Area greyed saying why.
            Assert.All([reload, area, watch, open], c => Assert.True(c.IsVisible));
            Assert.False(reload.IsEffectivelyEnabled);
            Assert.False(area.IsEffectivelyEnabled);
            Assert.Equal("Open a page first", ToolTip.GetTip(reload));
            Assert.True(watch.IsEffectivelyEnabled);

            // A page: Reload goes; Area needs a chat to pick into.
            vm.Show(page);
            Dispatcher.UIThread.RunJobs();
            Assert.True(reload.IsEffectivelyEnabled);
            Assert.Equal("Load the page again", ToolTip.GetTip(reload));

            // WebView2 missing: the page's place says so, with where to get it; the buttons stay, greyed.
            vm.Missing = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.ShowsPage);
            Assert.True(Named<Button>("Get WebView2").IsEffectivelyVisible);
            Assert.False(reload.IsEffectivelyEnabled);
            Assert.Equal("WebView2 isn't installed", ToolTip.GetTip(reload));
            Assert.Equal(places, new[] { reload, area, watch, open }.Select(c => c.TranslatePoint(new Point(0, 0), window)).ToList());
            window.Close();
            return 0;
        });
    }

    [Fact]
    public void Only_web_pages_open_in_it_from_a_files_menu_or_a_drop()
    {
        var page = Path.Combine(_dir, "index.html");
        var script = Path.Combine(_dir, "game.js");
        File.WriteAllText(page, "<p>hi</p>");
        File.WriteAllText(script, "1");
        HeadlessApp.Run(() =>
        {
            using var vm = new LivePreviewViewModel(null, _dir, Path.Combine(_dir, "webview"));
            Assert.False(vm.Drop([script]));
            Assert.Null(vm.Path);
            Assert.True(vm.Drop([script, page]));
            Assert.Equal(page, vm.Path);
            return 0;
        });
    }
}
