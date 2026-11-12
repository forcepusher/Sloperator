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
    public async Task Columns_are_made_wider_by_a_line_or_fitted_to_what_they_hold()
    {
        Assert.False((await Write("budget.xlsx", Budget)).IsError);
        // As asked of Haiku and Sonnet (2026-11-13): column A about twice as wide.
        var result = await Edit("budget.xlsx", "", "column A width 17");
        Assert.False(result.IsError, result.Text);
        Assert.Contains("Made column A of sheet \"Sheet1\" 17 wide.", result.Text);
        List<(int Min, int Max, string? Width)> Cols() => Part("budget.xlsx", "xl/worksheets/sheet1.xml").Root!.Element(S + "cols")!.Elements(S + "col")
            .Select(c => ((int)c.Attribute("min")!, (int)c.Attribute("max")!, (string?)c.Attribute("width"))).ToList();
        Assert.Equal((1, 1, "17"), Cols()[0]);
        // The columns beside it keep the widths the file gave them.
        var others = Cols().Skip(1).ToList();
        Assert.Equal([2, 3], others.Select(c => c.Min));

        // A long name in a cell, then every column fitted: A grows to it, with the cells set in the same edit.
        var fitted = await Edit("budget.xlsx", "", "A2 Rent for the flat on Harbour Street\nautofit columns\ncolumns B:C width 12.5");
        Assert.False(fitted.IsError, fitted.Text);
        Assert.Contains("fitted columns A-C of sheet \"Sheet1\" to what they hold, then made columns B-C of sheet \"Sheet1\" 12.5 wide", fitted.Text);
        var cols = Cols();
        Assert.InRange(double.Parse(cols[0].Width!, System.Globalization.CultureInfo.InvariantCulture), 40, 45);
        Assert.Equal(["12.5", "12.5"], cols.Skip(1).Select(c => c.Width));
        // A width past what a column can be is refused, and other lines that start alike are still cells.
        Assert.True((await Edit("budget.xlsx", "", "column A width 900")).IsError);
        Assert.False((await Edit("budget.xlsx", "", "A7 column A is the items")).IsError);
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

    private const string Orders = "# Orders\n\n| date | region | units |\n|---|---|---|\n| 2026-01-15 | North | 16 |\n| 2026-01-16 | South | 21 |\n\n" +
                                  "# Revenue by region\n\n| region | revenue |\n|---|---|\n| North | 45857.5 |\n| South | 48565 |";

    /// <summary>The fill of a cell on a sheet (1, 2) as AARRGGBB, or null for none.</summary>
    private string? Fill(string file, int sheet, string cell)
    {
        var styles = Part(file, "xl/styles.xml").Root!;
        var c = Part(file, $"xl/worksheets/sheet{sheet}.xml").Descendants(S + "c").SingleOrDefault(e => (string?)e.Attribute("r") == cell);
        var xf = styles.Element(S + "cellXfs")!.Elements(S + "xf").ElementAt((int?)c?.Attribute("s") ?? 0);
        return (string?)styles.Element(S + "fills")!.Elements(S + "fill").ElementAt((int?)xf.Attribute("fillId") ?? 0).Descendants(S + "fgColor").SingleOrDefault()?.Attribute("rgb");
    }

    [Theory]
    // As Sonnet named the second sheet (2026-11-13), which worked, and as Haiku did, which didn't: in double
    // quotes, without quotes, in words after the cells (the first sheet was formatted), and without "format".
    [InlineData("format 'Revenue by region'!A1:B1 bold, light blue fill")]
    [InlineData("format \"Revenue by region\"!A1:B1 bold, light blue fill")]
    [InlineData("format Revenue by region!A1:B1 bold, light blue fill")]
    [InlineData("format A1:B1 on sheet \"Revenue by region\" bold, light blue fill")]
    [InlineData("format A1:B1 bold, light blue fill on sheet Revenue by region")]
    [InlineData("format A1:B1 in the sheet 'Revenue by region': bold, light blue fill")]
    [InlineData("'Revenue by region'!A1:B1 bold, light blue fill")]
    [InlineData("# Sheet \"Revenue by region\" (A1:B3)\nformat A1:B1 bold, light blue fill")]
    [InlineData("Sheet \"Revenue by region\"\nformat A1:B1 bold, light blue fill")]
    [InlineData("sheet: Revenue by region\nA1:B1 bold, light blue fill")]
    public async Task A_format_line_names_its_sheet_as_models_name_it(string lines)
    {
        Assert.False((await Write("orders.xlsx", Orders)).IsError);
        // A header row is written with a fill of its own: the first sheet's stays that.
        var first = Fill("orders.xlsx", 1, "A1");
        Assert.NotEqual("FFBDD7EE", first);
        var result = await Edit("orders.xlsx", "", lines);
        Assert.False(result.IsError, result.Text);
        Assert.Contains("Formatted A1:B1 on sheet \"Revenue by region\" (bold, fill #BDD7EE).", result.Text);
        Assert.Equal("FFBDD7EE", Fill("orders.xlsx", 2, "A1"));
        Assert.Equal("FFBDD7EE", Fill("orders.xlsx", 2, "B1"));
        Assert.Equal(first, Fill("orders.xlsx", 1, "A1"));
        // What the sheets hold is as it was.
        var text = await Read("orders.xlsx");
        Assert.Contains("A1 date | B1 region | C1 units", text);
        Assert.Contains("A1 region | B1 revenue", text);
    }

    [Fact]
    public async Task Widths_name_their_sheet_too_and_a_fit_of_all_columns_is_every_sheets()
    {
        Assert.False((await Write("orders.xlsx", Orders)).IsError);
        List<(int Min, string? Width)> Cols(int sheet) => Part("orders.xlsx", $"xl/worksheets/sheet{sheet}.xml").Root!.Element(S + "cols")?.Elements(S + "col")
            .Where(c => (string?)c.Attribute("customWidth") == "1").Select(c => ((int)c.Attribute("min")!, (string?)c.Attribute("width"))).ToList() ?? [];
        var before = Cols(1);

        // As Sonnet wrote it (2026-11-13), told only to give old_text; then with the sheet inside the line.
        var named = await Edit("orders.xlsx", "", "'Revenue by region'!column A width 18\ncolumn \"Revenue by region\"!B width 16");
        Assert.False(named.IsError, named.Text);
        Assert.Contains("Made column A of sheet \"Revenue by region\" 18 wide, then made column B of sheet \"Revenue by region\" 16 wide.", named.Text);
        Assert.Equal([(1, "18"), (2, "16")], Cols(2));
        Assert.Equal(before, Cols(1));

        // As Haiku wrote it: the sheet after the words.
        foreach (var line in new[] { "autofit columns \"Revenue by region\"", "autofit columns on 'Revenue by region'", "fit the column widths of sheet Revenue by region", "# Sheet \"Revenue by region\"\nautofit columns" })
        {
            var one = await Edit("orders.xlsx", "", line);
            Assert.False(one.IsError, one.Text);
            Assert.Contains("Fitted columns A-B of sheet \"Revenue by region\" to what they hold.", one.Text);
            Assert.DoesNotContain("Orders", one.Text);
        }
        Assert.Equal(before, Cols(1));

        // As Haiku wrote it on its next try: the sheet before the words, without quotes.
        var before2 = await Edit("orders.xlsx", "", "Revenue by region!autofit columns");
        Assert.False(before2.IsError, before2.Text);
        Assert.Contains("Fitted columns A-B of sheet \"Revenue by region\" to what they hold.", before2.Text);

        // No sheet said: "fit the column widths" of a workbook is all of it, as both models meant it.
        foreach (var line in new[] { "autofit columns", "autofit columns on all sheets", "Fit the column widths." })
        {
            var every = await Edit("orders.xlsx", "", line);
            Assert.False(every.IsError, every.Text);
            Assert.Contains("Fitted columns A-C of sheet \"Orders\" to what they hold, then fitted columns A-B of sheet \"Revenue by region\" to what they hold.", every.Text);
        }
        Assert.Equal(3, Cols(1).Count);

        // Columns named are the first sheet's, as cells are; a sheet that isn't there is said with the ones that are.
        Assert.Contains("Fitted columns A-B of sheet \"Orders\"", (await Edit("orders.xlsx", "", "autofit columns A:B")).Text);
        var nowhere = await Edit("orders.xlsx", "", "format A1:B1 on sheet \"Totals\" bold");
        Assert.True(nowhere.IsError);
        Assert.Contains("There is no sheet \"Totals\"; the sheets are \"Orders\", \"Revenue by region\".", nowhere.Text);
    }

    [Fact]
    public async Task Rows_of_a_workbook_of_several_sheets_go_only_on_the_sheet_named()
    {
        Assert.False((await Write("orders.xlsx", Orders)).IsError);
        // As Qwen wrote it (2026-11-13), meaning the second sheet: a row of orders on the first went.
        var unnamed = await Edit("orders.xlsx", "", "delete rows 3");
        Assert.True(unnamed.IsError);
        Assert.Contains("This workbook has 2 sheets (\"Orders\", \"Revenue by region\"): say which one the rows are deleted on", unnamed.Text);
        Assert.Contains("A3 2026-01-16 | B3 South", await Read("orders.xlsx"));
        foreach (var line in new[] { "delete row 3 on \"Revenue by region\"", "'Revenue by region'!delete row 2", "# Sheet \"Revenue by region\"\ninsert row 2" })
        {
            var named = await Edit("orders.xlsx", "", line);
            Assert.False(named.IsError, named.Text);
            Assert.Contains("on sheet \"Revenue by region\"", named.Text);
        }
        Assert.Contains("A3 2026-01-16 | B3 South", await Read("orders.xlsx"));
    }

    [Fact]
    public async Task A_line_that_is_none_of_them_is_said_with_the_lines_there_are()
    {
        Assert.False((await Write("orders.xlsx", Orders)).IsError);
        var unknown = await Edit("orders.xlsx", "", "format A1:C1 yellow fill\nprotect the sheet");
        Assert.True(unknown.IsError);
        Assert.Contains("\"protect the sheet\" is no line edit_file knows for a workbook.", unknown.Text);
        Assert.Contains("\"'My sheet'!A1:B1\"", unknown.Text);
        // Nothing of the edit was done.
        Assert.NotEqual("FFFFFF00", Fill("orders.xlsx", 1, "A1"));
        // A range with words that aren't a look is no format, and a cell with a look's word in it is a cell.
        Assert.Contains("\"A5:C5 Totals for the year\" is no line", (await Edit("orders.xlsx", "", "A5:C5 Totals for the year")).Text);
        Assert.False((await Edit("orders.xlsx", "", "B5 bold")).IsError);
        Assert.Contains("B5 bold", await Read("orders.xlsx"));
    }
}
