using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>edit_file's "format A1:E1 bold, yellow fill" lines: how the cells of a workbook look, beside what they hold.</summary>
public class SheetFormatTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-formats-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private ToolContext Ctx() => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir };

    private Task<ToolResult> Write(string file, string content) =>
        new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = file, ["content"] = content }, Ctx(), CancellationToken.None);

    private Task<ToolResult> Edit(string file, string oldText, string newText) =>
        new EditFileTool().ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = oldText, ["new_text"] = newText }, Ctx(), CancellationToken.None);

    private async Task<string> Read(string file) => System.Text.RegularExpressions.Regex.Replace(
        (await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = file }, Ctx(), CancellationToken.None)).Text.ReplaceLineEndings("\n"), @"(?m)^\d+\t", "");

    private XDocument Part(string file, string part)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(_dir, file));
        using var stream = zip.GetEntry(part)!.Open();
        return XDocument.Load(stream);
    }

    /// <summary>The format of a cell: its font, fill, border and the xf itself, from the workbook's styles.</summary>
    private (XElement Xf, XElement Font, XElement Fill, XElement Border) Style(string file, string cell)
    {
        var styles = Part(file, "xl/styles.xml").Root!;
        var c = Part(file, "xl/worksheets/sheet1.xml").Descendants(S + "c").Single(e => (string?)e.Attribute("r") == cell);
        var xf = styles.Element(S + "cellXfs")!.Elements(S + "xf").ElementAt((int?)c.Attribute("s") ?? 0);
        return (xf,
            styles.Element(S + "fonts")!.Elements(S + "font").ElementAt((int)xf.Attribute("fontId")!),
            styles.Element(S + "fills")!.Elements(S + "fill").ElementAt((int)xf.Attribute("fillId")!),
            styles.Element(S + "borders")!.Elements(S + "border").ElementAt((int)xf.Attribute("borderId")!));
    }

    private const string Budget = """
        | Item | Jan | Feb |
        |---|---|---|
        | Rent | $900.00 | $900.00 |
        | Food | $350.00 | $410.00 |
        | Total | =SUM(B2:B3) | =SUM(C2:C3) |
        """;

    [Fact]
    public async Task A_format_line_gives_cells_a_fill_and_bold_and_keeps_the_rest()
    {
        Assert.False((await Write("budget.xlsx", Budget)).IsError);
        // As asked of Haiku and Sonnet (2026-11-13): a header row that stands out with a yellow background.
        var result = await Edit("budget.xlsx", "", "format A1:D1 bold, yellow fill");
        Assert.False(result.IsError, result.Text);
        Assert.Contains("Formatted A1:D1 on sheet \"Sheet1\" (bold, fill #FFFF00).", result.Text);
        foreach (var cell in new[] { "A1", "C1", "D1" })
        {
            var (xf, font, fill, _) = Style("budget.xlsx", cell);
            Assert.Equal("FFFFFF00", (string?)fill.Descendants(S + "fgColor").Single().Attribute("rgb"));
            Assert.Equal("solid", (string?)fill.Element(S + "patternFill")!.Attribute("patternType"));
            Assert.NotNull(font.Element(S + "b"));
            Assert.Equal("1", (string?)xf.Attribute("applyFill"));
        }
        // D1 held nothing: it is there now, so the fill runs to the end of what was asked.
        Assert.Contains("A1 Item | B1 Jan | C1 Feb", await Read("budget.xlsx"));

        // Red text on the amounts: they are still amounts of money, and the header keeps its fill.
        Assert.False((await Edit("budget.xlsx", "", "format B2:C3 red text, italic\nformat A4:C4 fill #DDEBF7, bold, border, centered")).IsError);
        var (money, moneyFont, moneyFill, _) = Style("budget.xlsx", "B2");
        Assert.NotEqual("0", (string?)money.Attribute("numFmtId"));
        Assert.Equal("FFFF0000", (string?)moneyFont.Element(S + "color")!.Attribute("rgb"));
        Assert.NotNull(moneyFont.Element(S + "i"));
        Assert.Equal("none", (string?)moneyFill.Element(S + "patternFill")!.Attribute("patternType"));
        var (total, totalFont, totalFill, totalBorder) = Style("budget.xlsx", "B4");
        Assert.Equal("FFDDEBF7", (string?)totalFill.Descendants(S + "fgColor").Single().Attribute("rgb"));
        Assert.NotNull(totalFont.Element(S + "b"));
        Assert.Equal("thin", (string?)totalBorder.Element(S + "top")!.Attribute("style"));
        Assert.Equal("center", (string?)total.Element(S + "alignment")!.Attribute("horizontal"));
        Assert.Equal("FFFFFF00", (string?)Style("budget.xlsx", "B1").Fill.Descendants(S + "fgColor").Single().Attribute("rgb"));
        var text = await Read("budget.xlsx");
        Assert.Contains("B2 $900.00", text);
        Assert.Contains("B4 =SUM(B2:B3) → $1,250.00", text);
        // The same look twice is one format in the file, not two.
        var count = Part("budget.xlsx", "xl/styles.xml").Root!.Element(S + "cellXfs")!.Elements().Count();
        Assert.False((await Edit("budget.xlsx", "", "format A1:D1 bold, yellow fill")).IsError);
        Assert.Equal(count, Part("budget.xlsx", "xl/styles.xml").Root!.Element(S + "cellXfs")!.Elements().Count());
    }

    [Fact]
    public async Task Cells_set_in_the_same_edit_are_formatted_and_a_look_can_be_taken_back()
    {
        Assert.False((await Write("budget.xlsx", Budget)).IsError);
        var result = await Edit("budget.xlsx", "", "D1 Mar | D2 $900.00 | D3 $380.00 | D4 =SUM(D2:D3)\nformat A1:D1 white text on a dark blue background");
        Assert.False(result.IsError, result.Text);
        Assert.Contains("Set D1, D2, D3, D4 on sheet \"Sheet1\", then formatted A1:D1 on sheet \"Sheet1\" (fill #002060, text #FFFFFF)", result.Text);
        var (_, font, fill, _) = Style("budget.xlsx", "D1");
        Assert.Equal("FF002060", (string?)fill.Descendants(S + "fgColor").Single().Attribute("rgb"));
        Assert.Equal("FFFFFFFF", (string?)font.Element(S + "color")!.Attribute("rgb"));

        Assert.False((await Edit("budget.xlsx", "", "format A1:D1 no fill, not bold, black text")).IsError);
        (_, font, fill, _) = Style("budget.xlsx", "A1");
        Assert.Equal("none", (string?)fill.Element(S + "patternFill")!.Attribute("patternType"));
        Assert.Null(font.Element(S + "b"));
    }

    [Fact]
    public async Task A_format_line_that_says_no_look_or_too_many_cells_is_refused()
    {
        Assert.False((await Write("budget.xlsx", Budget)).IsError);
        var vague = await Edit("budget.xlsx", "", "format A1:C1 nicely");
        Assert.True(vague.IsError);
        Assert.Contains("say how the cells should look", vague.Text);
        var huge = await Edit("budget.xlsx", "", "format A1:Z9999 bold");
        Assert.True(huge.IsError);
        Assert.Contains("at most 50,000 cells", huge.Text);
        var stale = await Edit("budget.xlsx", "format A1:C1 bold", "format A1:C1 italic");
        Assert.True(stale.IsError);
        Assert.Contains("not in old_text", stale.Text);
        // A cell that says "format" is still a cell.
        Assert.False((await Edit("budget.xlsx", "", "A6 format B2 as you like")).IsError);
        Assert.Contains("A6 format B2 as you like", await Read("budget.xlsx"));
    }
}
