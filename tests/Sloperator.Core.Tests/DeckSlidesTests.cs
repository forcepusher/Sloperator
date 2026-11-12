using System.IO.Compression;
using System.Text.Json.Nodes;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>edit_file's lines that take slides out of a deck, move them and copy them.</summary>
public class DeckSlidesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-slides-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    private ToolContext Ctx() => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir };

    private Task<ToolResult> Write(string file, string content) =>
        new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = file, ["content"] = content }, Ctx(), CancellationToken.None);

    private Task<ToolResult> Edit(string newText) =>
        new EditFileTool().ExecuteAsync(new JsonObject { ["path"] = "deck.pptx", ["old_text"] = "", ["new_text"] = newText }, Ctx(), CancellationToken.None);

    /// <summary>The slides' titles in the deck's order, as read_file shows them.</summary>
    private async Task<List<string>> Titles()
    {
        var text = (await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = "deck.pptx" }, Ctx(), CancellationToken.None)).Text.ReplaceLineEndings("\n");
        var lines = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^\d+\t", "").Split('\n');
        return [.. lines.Select((l, i) => (l, i)).Where(x => x.l.StartsWith("--- slide ", StringComparison.Ordinal)).Select(x => lines[x.i + 1])];
    }

    private List<string> Entries()
    {
        using var zip = ZipFile.OpenRead(Path.Combine(_dir, "deck.pptx"));
        return [.. zip.Entries.Select(e => e.FullName)];
    }

    [Fact]
    public async Task Slides_are_taken_out_moved_and_copied_by_their_numbers()
    {
        Assert.False((await Write("deck.pptx", "# Q3\nBy region\n\n---\n\n# Sales\n\n| Region | Sales |\n|---|---|\n| North | 120 |\n| East | 143 |\n\nchart: bar Sales\n\n---\n\n# Outlook\n\n- Steady\n\n---\n\n# Thank you")).IsError);
        Assert.Equal(["Q3", "Sales", "Outlook", "Thank you"], await Titles());

        var deleted = await Edit("delete slide 3");
        Assert.False(deleted.IsError, deleted.Text);
        Assert.Contains("Deleted slide 3 (\"Outlook\"); the deck has 3 slides now", deleted.Text);
        Assert.Equal(["Q3", "Sales", "Thank you"], await Titles());
        Assert.DoesNotContain("ppt/slides/slide3.xml", Entries());

        var moved = await Edit("move slide 3 to the start");
        Assert.False(moved.IsError, moved.Text);
        Assert.Contains("Moved slide 3 (\"Thank you\") to place 1", moved.Text);
        Assert.Equal(["Thank you", "Q3", "Sales"], await Titles());
        Assert.False((await Edit("move slide 1 after slide 3")).IsError);
        Assert.Equal(["Q3", "Sales", "Thank you"], await Titles());

        // A slide with a chart: the copy has a chart and a workbook of its own.
        var copied = await Edit("duplicate slide 2");
        Assert.False(copied.IsError, copied.Text);
        Assert.Contains("Copied slide 2 (\"Sales\") to a new slide after it; the deck has 4 slides now", copied.Text);
        Assert.Equal(["Q3", "Sales", "Sales", "Thank you"], await Titles());
        var entries = Entries();
        Assert.Equal(2, entries.Count(e => e.StartsWith("ppt/charts/chart", StringComparison.Ordinal) && e.EndsWith(".xml", StringComparison.Ordinal)));
        Assert.Equal(2, entries.Count(e => e.StartsWith("ppt/embeddings/", StringComparison.Ordinal)));

        // The numbers of one line are as they were before it.
        Assert.False((await Edit("delete slides 2, 4")).IsError);
        Assert.Equal(["Q3", "Sales"], await Titles());

        // Lines one after the other, as Haiku gave them in one edit (2026-11-13): the copy is of the slide just moved to the front.
        Assert.False((await Write("deck.pptx", "# Q3\nBy region\n\n---\n\n# Sales\n\n| Region | Sales |\n|---|---|\n| North | 120 |\n\nchart: bar Sales\n\n---\n\n# Thank you")).IsError);
        var steps = await Edit("delete slide 3\nmove slide 2 to 1\nduplicate slide 1\nmove slide 2 to the end");
        Assert.False(steps.IsError, steps.Text);
        Assert.Contains("Deleted slide 3 (\"Thank you\"), then moved slide 2 (\"Sales\") to place 1, then copied slide 1 (\"Sales\") to a new slide after it, then moved slide 2 (\"Sales\") to place 3", steps.Text);
        Assert.Equal(["Sales", "Q3", "Sales"], await Titles());

        // What can't be done is said, and nothing is changed: a later line's number past the deck as the lines before leave it too.
        Assert.Contains("There is no slide 9: the deck has 3", (await Edit("move slide 9 to 1")).Text);
        Assert.Contains("There is no slide 3: the deck has 2 by then", (await Edit("delete slide 1\nmove slide 3 to 1")).Text);
        Assert.Contains("A deck keeps at least one slide", (await Edit("delete slides 1-3")).Text);
        Assert.Contains("lines \"delete slide 3\", \"move slide 4 to 1\", \"duplicate slide 2\"", (await Edit("Sales")).Text);
        Assert.Equal(["Sales", "Q3", "Sales"], await Titles());
    }
}
