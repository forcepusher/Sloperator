using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>edit_file's new_text "format: …": how text and table rows of a Word document or a slide look, their words as they are.</summary>
public class TextLookTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-looks-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private ToolContext Ctx() => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir };

    private Task<ToolResult> Write(string file, string content) =>
        new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = file, ["content"] = content }, Ctx(), CancellationToken.None);

    private Task<ToolResult> Edit(string file, string oldText, string newText, bool all = false) =>
        new EditFileTool().ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = oldText, ["new_text"] = newText, ["replace_all"] = all }, Ctx(), CancellationToken.None);

    private async Task<string> Read(string file) => System.Text.RegularExpressions.Regex.Replace(
        (await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = file }, Ctx(), CancellationToken.None)).Text.ReplaceLineEndings("\n"), @"(?m)^\d+\t", "");

    private XDocument Part(string file, string part)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(_dir, file));
        using var stream = zip.GetEntry(part)!.Open();
        return XDocument.Load(stream);
    }

    private const string Report = """
        # Harbour Cycles: third quarter

        Sales rose in July and fell back in September.

        | Month | Bikes |
        |---|---|
        | July | 41 |
        | August | 47 |

        The figures are provisional.
        """;

    private static string Text(XElement e) => string.Concat(e.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value));

    [Fact]
    public async Task A_format_line_changes_how_a_word_documents_text_looks_and_not_what_it_says()
    {
        Assert.False((await Write("report.docx", Report)).IsError);
        var before = await Read("report.docx");
        // As asked of Haiku and Sonnet (2026-11-13), who rewrote document.xml through PowerShell for it.
        var title = await Edit("report.docx", "Harbour Cycles: third quarter", "format: centered, dark blue, size 20");
        Assert.False(title.IsError, title.Text);
        Assert.Contains("Formatted the text (size 20, text #002060, center); what it says is as it was.", title.Text);
        var header = await Edit("report.docx", "| Month | Bikes |", "format: light grey fill, bold");
        Assert.False(header.IsError, header.Text);
        Assert.Contains("Formatted the row of the table (bold, fill #D9D9D9)", header.Text);
        Assert.False((await Edit("report.docx", "The figures are provisional.", "format: italic")).IsError);
        Assert.False((await Edit("report.docx", "fell back", "format: bold, red text, yellow highlight, underlined")).IsError);
        // The words are the same; read_file marks what is bold or italic now.
        var after = await Read("report.docx");
        Assert.Contains("Sales rose in July and **fell back** in September.", after);
        Assert.Equal(before.Replace("*", ""), after.Replace("*", ""));

        var body = Part("report.docx", "word/document.xml").Root!.Element(W + "body")!;
        var heading = body.Elements(W + "p").First();
        Assert.Equal("center", (string?)heading.Element(W + "pPr")!.Element(W + "jc")!.Attribute(W + "val"));
        var run = heading.Elements(W + "r").Single().Element(W + "rPr")!;
        Assert.Equal("002060", (string?)run.Element(W + "color")!.Attribute(W + "val"));
        Assert.Equal("40", (string?)run.Element(W + "sz")!.Attribute(W + "val"));
        // The heading keeps its style: only what was asked is added to it.
        Assert.NotNull(heading.Element(W + "pPr")!.Element(W + "pStyle"));

        var cells = body.Descendants(W + "tr").First().Elements(W + "tc").ToList();
        Assert.All(cells, c => Assert.Equal("D9D9D9", (string?)c.Element(W + "tcPr")!.Element(W + "shd")!.Attribute(W + "fill")));
        Assert.All(cells, c => Assert.NotNull(c.Descendants(W + "r").Single().Element(W + "rPr")!.Element(W + "b")));
        // The rows under it are as they were.
        Assert.All(body.Descendants(W + "tr").Skip(1).Elements(W + "tc"), c => Assert.Null(c.Element(W + "tcPr")?.Element(W + "shd")));

        Assert.NotNull(body.Elements(W + "p").Last(p => Text(p).Length > 0).Elements(W + "r").Single().Element(W + "rPr")!.Element(W + "i"));

        // A part of a paragraph: only its words take the look, the paragraph's runs breaking around them.
        var sentence = body.Elements(W + "p").Single(p => Text(p).StartsWith("Sales rose", StringComparison.Ordinal));
        var runs = sentence.Elements(W + "r").ToList();
        Assert.Equal(["Sales rose in July and ", "fell back", " in September."], runs.Select(Text));
        var marked = runs[1].Element(W + "rPr")!;
        Assert.Equal("FF0000", (string?)marked.Element(W + "color")!.Attribute(W + "val"));
        Assert.Equal("FFFF00", (string?)marked.Element(W + "shd")!.Attribute(W + "fill"));
        Assert.Equal("single", (string?)marked.Element(W + "u")!.Attribute(W + "val"));
        Assert.Null(runs[0].Element(W + "rPr")?.Element(W + "b"));
        // In the order Word wants a run's properties in: bold before colour, colour before underline, underline before the fill.
        Assert.Equal(["b", "bCs", "color", "u", "shd"], marked.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public async Task A_format_line_that_says_nothing_or_finds_nothing_is_refused()
    {
        Assert.False((await Write("report.docx", Report)).IsError);
        var vague = await Edit("report.docx", "Harbour Cycles: third quarter", "format: nicer");
        Assert.True(vague.IsError);
        Assert.Contains("say how it should look", vague.Text);
        var missing = await Edit("report.docx", "No such text", "format: bold");
        Assert.True(missing.IsError);
        Assert.Contains("nothing to format", missing.Text);
        // Taken back as it was given.
        Assert.False((await Edit("report.docx", "The figures are provisional.", "format: bold, underlined")).IsError);
        Assert.False((await Edit("report.docx", "The figures are provisional.", "format: not bold, not underlined")).IsError);
        var run = Part("report.docx", "word/document.xml").Descendants(W + "p").Last(p => Text(p).Length > 0).Elements(W + "r").Single().Element(W + "rPr")!;
        Assert.Equal("0", (string?)run.Element(W + "b")!.Attribute(W + "val"));
        Assert.Equal("none", (string?)run.Element(W + "u")!.Attribute(W + "val"));
    }

    [Fact]
    public async Task A_format_line_changes_how_a_slides_text_and_table_look()
    {
        Assert.False((await Write("deck.pptx", "# Update\nWeekly numbers\n\n---\n\n# Sales by region\n\n- East leads with 143\n\n| Region | Sales |\n|---|---|\n| North | 120 |\n| East | 143 |")).IsError);
        var before = await Read("deck.pptx");
        Assert.False((await Edit("deck.pptx", "Sales by region", "format: dark blue, bold, centered")).IsError);
        Assert.False((await Edit("deck.pptx", "| East | 143 |", "format: yellow fill, bold")).IsError);
        // "143" is in the bullet and in the table: which one isn't said, so it is refused; a word of the bullet alone is not.
        Assert.Contains("occurs 2 times", (await Edit("deck.pptx", "143", "format: red text")).Text);
        Assert.False((await Edit("deck.pptx", "leads", "format: red, size 28")).IsError);
        Assert.Equal(before.Replace("*", ""), (await Read("deck.pptx")).Replace("*", ""));

        XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        var slide = Part("deck.pptx", "ppt/slides/slide2.xml");
        var title = slide.Descendants(p + "sp").First();
        Assert.Equal("ctr", (string?)title.Descendants(A + "p").Single().Element(A + "pPr")!.Attribute("algn"));
        var run = title.Descendants(A + "r").Single().Element(A + "rPr")!;
        Assert.Equal("1", (string?)run.Attribute("b"));
        Assert.Equal("002060", (string?)run.Element(A + "solidFill")!.Element(A + "srgbClr")!.Attribute("val"));

        var east = slide.Descendants(A + "tr").Single(r => Text(r).StartsWith("East", StringComparison.Ordinal)).Elements(A + "tc").ToList();
        Assert.All(east, c => Assert.Equal("FFFF00", (string?)c.Element(A + "tcPr")!.Element(A + "solidFill")!.Element(A + "srgbClr")!.Attribute("val")));
        Assert.All(east, c => Assert.Equal("1", (string?)c.Descendants(A + "rPr").First().Attribute("b")));
        var bullet = slide.Descendants(A + "p").Single(q => Text(q) == "East leads with 143");
        Assert.Equal(["East ", "leads", " with 143"], bullet.Elements(A + "r").Select(Text));
        var word = bullet.Elements(A + "r").ElementAt(1).Element(A + "rPr")!;
        Assert.Equal("2800", (string?)word.Attribute("sz"));
        Assert.Equal("FF0000", (string?)word.Element(A + "solidFill")!.Element(A + "srgbClr")!.Attribute("val"));
    }

    /// <summary>
    /// Opt-in (BB_DOC_OUT): a document Word itself saved, put in the folder as word.docx (a title, a paragraph, a
    /// heading and a table with a Month row), formatted here and left as word-formatted.docx for Word to open and
    /// read back (2026-11-13: alignment, colours, sizes and shading as asked, nothing repaired).
    /// </summary>
    [Fact]
    public async Task Samples_to_open_in_word()
    {
        var output = Environment.GetEnvironmentVariable("BB_DOC_OUT");
        Assert.SkipWhen(output is null || !File.Exists(Path.Combine(output, "word.docx")), "Set BB_DOC_OUT to a folder with word.docx to run.");
        File.Copy(Path.Combine(output!, "word.docx"), Path.Combine(_dir, "word-formatted.docx"));
        async Task Ok(Task<ToolResult> step) { var r = await step; Assert.False(r.IsError, r.Text); }
        await Ok(Edit("word-formatted.docx", "Harbour Cycles: third quarter", "format: centered, dark blue, size 30"));
        await Ok(Edit("word-formatted.docx", "| Month | Bikes | Service |", "format: light grey fill, bold"));
        await Ok(Edit("word-formatted.docx", "fell back in September", "format: italic, red text, yellow highlight"));
        await Ok(Edit("word-formatted.docx", "Sales by month", "format: right, underlined"));
        File.Copy(Path.Combine(_dir, "word-formatted.docx"), Path.Combine(output!, "word-formatted.docx"), overwrite: true);
    }
}
