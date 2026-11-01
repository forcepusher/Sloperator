using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// Text selected in the chat goes with the next message (the user, 2026-11-01), so it stays selected
/// while the user goes to the box to type about it; a click elsewhere in the chat or Esc lets it go.
/// </summary>
public sealed class ChatSelectionTests
{
    private sealed record Shown(Window Window, TextBox Box, SelectableTextBlock Mine, SelectableTextBlock Notice, List<SelectableTextBlock> Reply);

    /// <summary>The chat's own scroller, out of a main window, with a message of the user's and a notice, and a box under it.</summary>
    private static Shown Show()
    {
        var main = new MainWindow();
        var scroll = main.FindControl<ScrollViewer>("ChatScroll")!;
        ((Panel)scroll.Parent!).Children.Remove(scroll);
        var items = scroll.GetLogicalDescendantsOfType<ItemsControl>().First();
        items.ItemsSource = new ChatItemViewModel[]
        {
            new UserMessageItem("Make the title bar yellow"),
            new AssistantMessageItem { Text = "First paragraph.\n\nSecond paragraph.", IsStreaming = false },
            new NoticeItem("Stopped.", isError: false),
        };
        // The start screen's cards, shown with no chat behind the window, would push the messages out of view.
        foreach (var intro in scroll.GetLogicalDescendantsOfType<StackPanel>().Where(p => p.Classes.Contains("intro")))
            intro.IsVisible = false;
        var box = new TextBox { Height = 32 };
        DockPanel.SetDock(box, Dock.Bottom);
        var window = new Window { Width = 800, Height = 600, Content = new DockPanel { Children = { box, scroll } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var texts = scroll.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
        return new Shown(window, box, texts.Single(t => t.Inlines?.Text == "Make the title bar yellow"), texts.Single(t => t.Inlines?.Text == "Stopped."),
            texts.Where(t => t.Inlines?.Text?.EndsWith("paragraph.", StringComparison.Ordinal) == true).ToList());
    }

    private static void Select(SelectableTextBlock text)
    {
        text.Focus();
        (text.SelectionStart, text.SelectionEnd) = (9, 18);
    }

    private static Point Middle(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    [Fact]
    public void It_stays_selected_while_the_box_has_the_focus()
    {
        var selected = HeadlessApp.Run(() =>
        {
            var shown = Show();
            Select(shown.Mine);
            shown.Box.Focus();
            Dispatcher.UIThread.RunJobs();
            var text = shown.Mine.SelectedText;
            shown.Window.Close();
            return text;
        });
        Assert.Equal("title bar", selected);
    }

    /// <summary>A reply's paragraphs selected together (a drag from one into the next) stay selected too.</summary>
    [Fact]
    public void A_selection_over_a_replys_paragraphs_stays_while_the_box_has_the_focus()
    {
        var selected = HeadlessApp.Run(() =>
        {
            var shown = Show();
            var (first, second) = (shown.Reply[0], shown.Reply[1]);
            var from = first.TranslatePoint(new Point(first.Padding.Left + 1, first.Bounds.Height / 2), shown.Window)!.Value;
            var to = second.TranslatePoint(new Point(second.Bounds.Width - second.Padding.Right - 1, second.Bounds.Height / 2), shown.Window)!.Value;
            shown.Window.MouseDown(from, MouseButton.Left);
            shown.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            shown.Window.MouseUp(to, MouseButton.Left);
            shown.Box.Focus();
            Dispatcher.UIThread.RunJobs();
            var text = $"{first.SelectedText}|{second.SelectedText}";
            shown.Window.Close();
            return text;
        });
        Assert.Equal("First paragraph.|Second paragraph.", selected);
    }

    [Fact]
    public void A_click_on_other_text_in_the_chat_lets_it_go()
    {
        var selected = HeadlessApp.Run(() =>
        {
            var shown = Show();
            Select(shown.Mine);
            shown.Box.Focus();
            var at = Middle(shown.Window, shown.Notice);
            shown.Window.MouseDown(at, MouseButton.Left);
            shown.Window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var text = shown.Mine.SelectedText;
            shown.Window.Close();
            return text;
        });
        Assert.Equal("", selected);
    }

    [Fact]
    public void Esc_lets_it_go()
    {
        var selected = HeadlessApp.Run(() =>
        {
            var shown = Show();
            Select(shown.Mine);
            shown.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            shown.Window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var text = shown.Mine.SelectedText;
            shown.Window.Close();
            return text;
        });
        Assert.Equal("", selected);
    }

    /// <summary>Text elsewhere still loses its selection with the focus, as Avalonia's does.</summary>
    [Fact]
    public void Text_outside_the_chat_lets_go_with_the_focus()
    {
        var selected = HeadlessApp.Run(() =>
        {
            var text = new SelectableText { Text = "Make the title bar yellow" };
            var box = new TextBox();
            var window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { text, box } } };
            window.Show();
            Select(text);
            box.Focus();
            Dispatcher.UIThread.RunJobs();
            var left = text.SelectedText;
            window.Close();
            return left;
        });
        Assert.Equal("", selected);
    }
}
