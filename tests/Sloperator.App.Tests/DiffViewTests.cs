using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using AvaloniaEdit.Editing;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Sloperator.Core.Diffs;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>File changes' diff in the Code editor's editor: its rows as lines, lines picked by selecting them, headers and dividers as tall as they were.</summary>
public sealed class DiffViewTests
{
    /// <summary>Two files' rows as the panel makes them: a header, a hunk's divider and its lines each (rows 0-5 and 6-11).</summary>
    private static List<DiffRow> Rows()
    {
        var rows = new List<DiffRow>();
        foreach (var name in (string[])["a.cs", "b.cs"])
        {
            var file = new DiffFile(name, "modified",
            [
                new(DiffLineKind.Hunk, null, null, "@@ -1,3 +1,3 @@"),
                new(DiffLineKind.Context, 1, 1, "int a = 1;"),
                new(DiffLineKind.Removed, 2, null, "int b = 2;"),
                new(DiffLineKind.Added, null, 2, "int b = 3;"),
                new(DiffLineKind.Context, 3, 3, "int c = 4;"),
            ]) { Added = 1, Removed = 1, OldText = "int a = 1;\nint b = 2;\nint c = 4;\n", NewText = "int a = 1;\nint b = 3;\nint c = 4;\n" };
            var item = new DiffFileItem(file, rows.Count, "C:\\work\\" + name);
            var colours = new DiffColours(file, "C#");
            rows.Add(new DiffRow(item, null) { Index = rows.Count });
            for (var i = 0; i < file.Lines.Count; i++)
                rows.Add(new DiffRow(item, file.Lines[i]) { Index = rows.Count, Colouring = (colours, i) });
        }
        return rows;
    }

    private sealed record Shown(DiffView Diff, Window Window, List<IReadOnlyCollection<int>> Picks);

    private static Shown Show(IReadOnlyList<DiffRow> rows)
    {
        var diff = new DiffView { FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 12 };
        var picks = new List<IReadOnlyCollection<int>>();
        diff.Picked += rows => picks.Add(rows.Order().ToList());
        var window = new Window { Content = diff, Width = 600, Height = 400 };
        window.Show();
        diff.Show(rows);
        Settle(window);
        return new Shown(diff, window, picks);
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>Where in the window line <paramref name="line"/> (from 1) of the diff is, <paramref name="x"/> into its text.</summary>
    private static Point At(Shown shown, int line, double x = 30)
    {
        var view = shown.Diff.TextArea.TextView;
        var top = view.GetVisualTopByDocumentLine(line) - view.VerticalOffset;
        var height = view.GetVisualLine(line)?.Height ?? view.DefaultLineHeight;
        return view.TranslatePoint(new Point(x, top + height - 4), shown.Window)!.Value;
    }

    [Fact]
    public void Each_row_is_a_line_of_the_text_in_order()
    {
        var lines = HeadlessApp.Run(() => Show(Rows()).Diff.Document.Lines.Count);
        Assert.Equal(12, lines);
        var text = HeadlessApp.Run(() => Show(Rows()).Diff.Document.Text);
        Assert.Equal("a.cs\nLines 1-3\nint a = 1;\nint b = 2;\nint b = 3;\nint c = 4;\nb.cs\nLines 1-3\nint a = 1;\nint b = 2;\nint b = 3;\nint c = 4;", text);
    }

    [Fact]
    public void A_divider_is_as_tall_as_a_line_of_code_and_a_header_as_its_words_and_padding()
    {
        var heights = HeadlessApp.Run(() =>
        {
            var view = Show(Rows()).Diff.TextArea.TextView;
            view.EnsureVisualLines();
            return (view.VisualLines.Select(l => l.Height).ToList(), view.DefaultLineHeight);
        });
        var (lines, code) = heights;
        Assert.Equal(code, lines[1], 1);
        Assert.Equal(code, lines[2], 1);
        // The header's words (Inter, 13 px), 5 over and under them and 10 over that.
        Assert.InRange(lines[0], 34, 40);
        Assert.Equal(lines[0], lines[6], 1);
    }

    [Fact]
    public void A_drag_over_lines_picks_the_lines_it_is_in_and_not_a_header_or_divider()
    {
        var picks = HeadlessApp.Run(() =>
        {
            var shown = Show(Rows());
            var editor = shown.Diff;
            // From the middle of line 3 ("int a") to the middle of line 4, as a drag selects it.
            var from = editor.Document.GetLineByNumber(3).Offset + 4;
            editor.TextArea.Selection = Selection.Create(editor.TextArea, from, editor.Document.GetLineByNumber(4).Offset + 3);
            var first = shown.Picks.Last();
            // Ending where line 5 starts leaves line 5 out; starting on the header takes no header.
            editor.TextArea.Selection = Selection.Create(editor.TextArea, 0, editor.Document.GetLineByNumber(5).Offset);
            return (first, shown.Picks.Last());
        });
        Assert.Equal([2, 3], picks.first);
        Assert.Equal([2, 3], picks.Item2);
    }

    [Fact]
    public void A_click_on_a_header_picks_its_files_lines_and_on_a_divider_its_hunks()
    {
        var picks = HeadlessApp.Run(() =>
        {
            var shown = Show(Rows());
            shown.Window.MouseDown(At(shown, 7), MouseButton.Left);
            shown.Window.MouseUp(At(shown, 7), MouseButton.Left);
            var file = shown.Picks.Last();
            shown.Window.MouseDown(At(shown, 2), MouseButton.Left);
            shown.Window.MouseUp(At(shown, 2), MouseButton.Left);
            return (file, shown.Picks.Last());
        });
        Assert.Equal([8, 9, 10, 11], picks.file);
        Assert.Equal([2, 3, 4, 5], picks.Item2);
    }

    [Fact]
    public void Ctrl_held_keeps_what_was_selected_beside_the_next_pick()
    {
        var picks = HeadlessApp.Run(() =>
        {
            var shown = Show(Rows());
            shown.Window.MouseDown(At(shown, 2), MouseButton.Left);
            shown.Window.MouseUp(At(shown, 2), MouseButton.Left);
            shown.Window.MouseDown(At(shown, 11), MouseButton.Left, RawInputModifiers.Control);
            shown.Window.MouseMove(At(shown, 12), RawInputModifiers.LeftMouseButton | RawInputModifiers.Control);
            shown.Window.MouseUp(At(shown, 12), MouseButton.Left, RawInputModifiers.Control);
            return shown.Picks.Last();
        });
        Assert.Equal([2, 3, 4, 5, 10, 11], picks);
    }

    [Fact]
    public void A_ctrl_click_on_a_line_picks_it_and_another_lets_it_go()
    {
        var picks = HeadlessApp.Run(() =>
        {
            var shown = Show(Rows());
            void CtrlClick(int line)
            {
                shown.Window.MouseDown(At(shown, line), MouseButton.Left, RawInputModifiers.Control);
                shown.Window.MouseUp(At(shown, line), MouseButton.Left, RawInputModifiers.Control);
            }
            shown.Window.MouseDown(At(shown, 2), MouseButton.Left);
            shown.Window.MouseUp(At(shown, 2), MouseButton.Left);
            CtrlClick(10);
            var added = shown.Picks.Last();
            CtrlClick(4);
            return (added, shown.Picks.Last());
        });
        Assert.Equal([2, 3, 4, 5, 9], picks.added);
        Assert.Equal([2, 4, 5, 9], picks.Item2);
    }

    [Fact]
    public void A_plain_click_on_code_leaves_nothing_picked_and_says_so()
    {
        var (picks, away) = HeadlessApp.Run(() =>
        {
            var shown = Show(Rows());
            var away = 0;
            shown.Diff.ClickedAway += () => away++;
            shown.Window.MouseDown(At(shown, 2), MouseButton.Left);
            shown.Window.MouseUp(At(shown, 2), MouseButton.Left);
            shown.Window.MouseDown(At(shown, 4), MouseButton.Left);
            shown.Window.MouseUp(At(shown, 4), MouseButton.Left);
            return (shown.Picks.Last(), away);
        });
        Assert.Empty(picks);
        Assert.Equal(1, away);
    }

    [Fact]
    public void Picks_set_from_outside_are_selected_again_and_said_back_as_they_are()
    {
        var (selected, picks) = HeadlessApp.Run(() =>
        {
            var shown = Show(Rows());
            // A refresh keeps lines 3-4 of the first file and line 5 of the second picked.
            shown.Diff.ShowPicks([2, 3, 10]);
            var selected = shown.Diff.TextArea.Selection.GetText();
            shown.Diff.Show(Rows());
            return (selected, shown.Picks.Count);
        });
        // The last run is the selection; the first kept beside it. Neither is reported back as a new pick.
        Assert.Equal("int b = 3;", selected);
        Assert.Equal(0, picks);
    }

    [Fact]
    public void The_same_rows_again_leave_the_selection_as_it_is()
    {
        var text = HeadlessApp.Run(() =>
        {
            var shown = Show(Rows());
            var editor = shown.Diff;
            editor.TextArea.Selection = Selection.Create(editor.TextArea, editor.Document.GetLineByNumber(3).Offset + 4, editor.Document.GetLineByNumber(4).Offset + 3);
            editor.Show(Rows());
            return editor.TextArea.Selection.GetText();
        });
        Assert.Equal("a = 1;\nint", text.ReplaceLineEndings("\n"));
    }
}
