using Sloperator.App.Views;
using Sloperator.Core.Diffs;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>File changes' diff in the Code editor's colours: each side highlighted whole, each line given its own line's colours.</summary>
public sealed class DiffColoursTests
{
    private const string Before = """
        class Rock
        {
            /* Splits into two,
               or none when it is the smallest. */
            int Split() => 2;
        }
        """;

    private const string After = """
        class Rock
        {
            /* Splits into two,
               or none when it is the smallest. */
            int Split() => 3;
        }
        """;

    /// <summary>The diff of <see cref="Before"/> to <see cref="After"/> from line 4, inside the comment, as a hunk with one line of context shows it.</summary>
    private static DiffFile File(bool whole) => new("Rock.cs", "modified",
    [
        new(DiffLineKind.Hunk, null, null, "@@ -4,3 +4,3 @@"),
        new(DiffLineKind.Context, 4, 4, "       or none when it is the smallest. */"),
        new(DiffLineKind.Removed, 5, null, "    int Split() => 2;"),
        new(DiffLineKind.Added, null, 5, "    int Split() => 3;"),
        new(DiffLineKind.Context, 6, 6, "}"),
    ])
    {
        OldText = whole ? Before : null,
        NewText = whole ? After : null,
    };

    /// <summary>Each of the file's diff lines in its colours, read on the UI thread as the diff reads them.</summary>
    private static IReadOnlyList<SyntaxSpan>[] Colours(DiffFile file, string? language) =>
        HeadlessApp.Run(() => { var colours = new DiffColours(file, language); return file.Lines.Select((_, i) => colours.Of(i)).ToArray(); });

    /// <summary>The colour of <paramref name="token"/> in line <paramref name="index"/> of the diff; null for the code's own.</summary>
    private static string? Colour(IReadOnlyList<SyntaxSpan>[] colours, DiffFile file, int index, string token)
    {
        var at = file.Lines[index].Text.IndexOf(token, StringComparison.Ordinal);
        return colours[index].FirstOrDefault(s => s.Start <= at && s.Start + s.Length >= at + token.Length).Key;
    }

    [Fact]
    public void A_line_inside_a_comment_that_starts_above_its_hunk_is_a_comment()
    {
        var file = File(whole: true);
        var colours = Colours(file, "C#");
        Assert.Equal("BbSyntaxComment", Colour(colours, file, 1, "smallest"));
        Assert.Equal("BbSyntaxComment", Colour(colours, file, 1, "is"));
    }

    [Fact]
    public void A_removed_line_is_coloured_as_the_old_file_and_an_added_one_as_the_new()
    {
        var file = File(whole: true);
        var colours = Colours(file, "C#");
        Assert.Equal("BbSyntaxNumber", Colour(colours, file, 2, "2"));
        Assert.Equal("BbSyntaxKeyword", Colour(colours, file, 2, "int"));
        Assert.Equal("BbSyntaxNumber", Colour(colours, file, 3, "3"));
        Assert.Equal("BbSyntaxMethod", Colour(colours, file, 3, "Split"));
        Assert.Empty(colours[0]);
    }

    [Fact]
    public void Without_the_whole_files_a_hunk_is_coloured_from_its_own_lines()
    {
        var file = File(whole: false);
        var colours = Colours(file, "C#");
        // "is" is a keyword to a highlighter that doesn't know the comment started above.
        Assert.Equal("BbSyntaxKeyword", Colour(colours, file, 1, "is"));
        Assert.Equal("BbSyntaxNumber", Colour(colours, file, 3, "3"));
    }

    [Fact]
    public void A_docstring_closed_in_the_hunk_leaves_the_code_after_it_as_code()
    {
        const string before = "def save():\n    \"\"\"Writes it.\n\n    Kept beside the game.\n    \"\"\"\n    if score > best:\n        return 1\n";
        var after = before.Replace("score > best", "score >= best");
        var file = new DiffFile("score.py", "modified",
        [
            new(DiffLineKind.Hunk, null, null, "@@ -4,4 +4,4 @@"),
            new(DiffLineKind.Context, 4, 4, "    Kept beside the game."),
            new(DiffLineKind.Context, 5, 5, "    \"\"\""),
            new(DiffLineKind.Removed, 6, null, "    if score > best:"),
            new(DiffLineKind.Added, null, 6, "    if score >= best:"),
            new(DiffLineKind.Context, 7, 7, "        return 1"),
        ]) { OldText = before, NewText = after };
        var colours = Colours(file, "Python");
        Assert.Equal("BbSyntaxString", Colour(colours, file, 1, "Kept"));
        Assert.Equal("BbSyntaxKeyword", Colour(colours, file, 3, "if"));
        Assert.Equal("BbSyntaxKeyword", Colour(colours, file, 4, "if"));
        Assert.Equal("BbSyntaxKeyword", Colour(colours, file, 5, "return"));
    }

    [Fact]
    public void A_file_of_no_language_and_a_binary_one_have_no_colours()
    {
        Assert.All(Colours(File(whole: true), null), Assert.Empty);
        Assert.All(Colours(new DiffFile("a.png", "modified", []) { Binary = true }, "C#"), Assert.Empty);
    }

    [Fact]
    public void Lines_read_in_any_order_as_the_view_scrolls_get_the_same_colours()
    {
        var file = File(whole: true);
        var upward = HeadlessApp.Run(() =>
        {
            var colours = new DiffColours(file, "C#");
            return Enumerable.Range(0, file.Lines.Count).Reverse().Select(colours.Of).Reverse().ToArray();
        });
        var downward = Colours(file, "C#");
        for (var i = 0; i < file.Lines.Count; i++)
            Assert.Equal(downward[i], upward[i]);
    }
}
