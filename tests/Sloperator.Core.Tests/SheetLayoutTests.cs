using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// edit_file's lines that lay a sheet out: sort, freeze, filter, merge, highlight, and how numbers show. Asked for all
/// of it on a workbook Excel had saved (2026-11-13), Haiku rewrote the sheet's XML through PowerShell and Sonnet
/// drove Excel. That Excel opens what these make was checked by hand (`.dev/night-test/excel-check.ps1`).
/// </summary>
public class SheetLayoutTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-layout-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private ToolContext Ctx() => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir };

    private Task<ToolResult> Write(string file, string content) =>
        new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = file, ["content"] = content }, Ctx(), CancellationToken.None);

    private Task<ToolResult> Edit(string file, string newText) =>
        new EditFileTool().ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = "", ["new_text"] = newText }, Ctx(), CancellationToken.None);

    private async Task<string> Read(string file) => System.Text.RegularExpressions.Regex.Replace(
        (await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = file }, Ctx(), CancellationToken.None)).Text.ReplaceLineEndings("\n"), @"(?m)^\d+\t", "");

    private XDocument Part(string file, string part)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(_dir, file));
        using var stream = zip.GetEntry(part)!.Open();
        return XDocument.Load(stream);
    }

    private const string Orders = """
        # Orders
        | date | region | product | units | price | revenue |
        |---|---|---|---|---|---|
        | 2026-01-15 | North | Lamp | 16 | 29.5 | =D2*E2 |
        | 2026-01-15 | South | Desk | 12 | 310 | =D3*E3 |
        | 2026-02-15 | East | Chair | 35 | 89 | =D4*E4 |
        | 2026-02-15 | West | Desk | 4 | 310 | =D5*E5 |
        | 2026-03-15 | North | Chair | 20 | 89 | =D6*E6 |
        """;

    /// <summary>The regions in the sheet's order, read back.</summary>
    private async Task<List<string>> Regions(string file) =>
        [.. (await Read(file)).Split('\n').Select(l => System.Text.RegularExpressions.Regex.Match(l, @"\bB(\d+) (\w+)")).Where(m => m.Success && m.Groups[1].Value != "1").Select(m => m.Groups[2].Value)];

    [Fact]
    public async Task Rows_are_sorted_by_a_column_with_their_formulas_and_the_heading_stays()
    {
        Assert.False((await Write("orders.xlsx", Orders)).IsError);
        // As the request said it (2026-11-13): by revenue, largest first. 3720, 3115, 1780, 1240, 472.
        var result = await Edit("orders.xlsx", "sort by revenue, largest first");
        Assert.False(result.IsError, result.Text);
        Assert.Contains("Sorted rows 2-6 of sheet \"Orders\" by column F (revenue), largest first", result.Text);
        Assert.Equal(["South", "East", "North", "West", "North"], await Regions("orders.xlsx"));
        var text = await Read("orders.xlsx");
        // Each formula moved with its row, its references with it, and its value is that row's.
        Assert.Contains("A2 2026-01-15 | B2 South | C2 Desk | D2 12 | E2 310 | F2 =D2*E2 → 3720", text);
        Assert.Contains("F6 =D6*E6 → 472", text);
        Assert.Contains("A1 date | B1 region", text);

        // The heading given with the rest stays on top; a letter or text sorts the same way, A to Z.
        var named = await Edit("orders.xlsx", "sort A1:F6 by B");
        Assert.False(named.IsError, named.Text);
        Assert.Contains("row 1 kept on top as their heading", named.Text);
        Assert.Equal(["East", "North", "North", "South", "West"], await Regions("orders.xlsx"));
        // Rows of the same value keep their order: the two North rows are as they were.
        Assert.Contains("A3 2026-03-15 | B3 North | C3 Chair", await Read("orders.xlsx"));
        Assert.Contains("A4 2026-01-15 | B4 North | C4 Lamp", await Read("orders.xlsx"));

        var nowhere = await Edit("orders.xlsx", "sort by profit");
        Assert.True(nowhere.IsError);
        Assert.Contains("No column of sheet \"Orders\" is headed \"profit\"", nowhere.Text);
    }

    [Fact]
    public async Task The_header_row_is_frozen_and_filtered_and_read_back()
    {
        Assert.False((await Write("orders.xlsx", Orders)).IsError);
        var result = await Edit("orders.xlsx", "freeze the header row\nturn on filters");
        Assert.False(result.IsError, result.Text);
        Assert.Contains("Froze row 1 of sheet \"Orders\" (they stay in view as the rest scrolls), then put filters on row 1 of sheet \"Orders\", over A1:F6.", result.Text);
        var sheet = Part("orders.xlsx", "xl/worksheets/sheet1.xml").Root!;
        var pane = sheet.Element(S + "sheetViews")!.Element(S + "sheetView")!.Elements().First();
        Assert.Equal(("pane", "1", "A2", "frozen"), (pane.Name.LocalName, (string?)pane.Attribute("ySplit"), (string?)pane.Attribute("topLeftCell"), (string?)pane.Attribute("state")));
        Assert.Equal("A1:F6", (string?)sheet.Element(S + "autoFilter")!.Attribute("ref"));
        // In the order Excel holds a sheet to: the filter right after the cells.
        Assert.Equal("autoFilter", sheet.Element(S + "sheetData")!.ElementsAfterSelf().First().Name.LocalName);
        Assert.Contains(Part("orders.xlsx", "xl/workbook.xml").Descendants(S + "definedName"), n => (string?)n.Attribute("name") == "_xlnm._FilterDatabase" && n.Value == "'Orders'!$A$1:$F$6");
        var text = await Read("orders.xlsx");
        Assert.Contains("(frozen: row 1)", text);
        Assert.Contains("(filter: A1:F6)", text);

        // And taken off again; the first column and a place work too.
        Assert.False((await Edit("orders.xlsx", "unfreeze\nremove the filter")).IsError);
        text = await Read("orders.xlsx");
        Assert.DoesNotContain("(frozen", text);
        Assert.DoesNotContain("(filter", text);
        Assert.DoesNotContain(Part("orders.xlsx", "xl/workbook.xml").Descendants(S + "definedName"), n => (string?)n.Attribute("name") == "_xlnm._FilterDatabase");
        Assert.Contains("Froze row 1 and column A", (await Edit("orders.xlsx", "freeze panes at B2")).Text);
        Assert.Contains("(frozen: row 1 and column A)", await Read("orders.xlsx"));
    }

    [Fact]
    public async Task A_title_row_is_merged_across_the_table_and_numbers_show_as_asked()
    {
        Assert.False((await Write("orders.xlsx", Orders)).IsError);
        // As the follow-up asked it: a title row above the table, across all its columns.
        var result = await Edit("orders.xlsx", "insert row 1\nA1 Orders, January to June 2026\nmerge and center A1:F1\nformat A1 bold, size 14\nformat E3:F7 as dollars");
        Assert.False(result.IsError, result.Text);
        Assert.Contains("merged A1:F1 on sheet \"Orders\", its text centred", result.Text);
        // The amounts no longer fit the columns as they were, which are made wider, as Excel does.
        Assert.True(result.Text.Contains("made wider to show them"), result.Text);
        var widths = Part("orders.xlsx", "xl/worksheets/sheet1.xml").Descendants(S + "col").ToDictionary(c => (int)c.Attribute("min")!, c => double.Parse((string)c.Attribute("width")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(widths[6] >= 11, $"F is {widths[6]} wide");
        var text = await Read("orders.xlsx");
        Assert.Contains("(merged: A1:F1)", text);
        Assert.Contains("A1 Orders, January to June 2026", text);
        Assert.Contains("E3 $29.50 | F3 =D3*E3 → $472.00", text);
        Assert.Equal("A1:F1", (string?)Part("orders.xlsx", "xl/worksheets/sheet1.xml").Descendants(S + "mergeCell").Single().Attribute("ref"));

        // A filter now goes on the table's own header row, under the title.
        Assert.Contains("Put filters on row 2 of sheet \"Orders\", over A2:F7", (await Edit("orders.xlsx", "filter")).Text);
        // Overlapping a merged range is refused; what the other cells held is said when a merge empties them.
        Assert.Contains("overlaps A1:F1", (await Edit("orders.xlsx", "merge A1:B2")).Text);
        var emptied = await Edit("orders.xlsx", "merge B3:C3");
        Assert.Contains("C3 held something, which a merged cell doesn't show: emptied, as Excel does", emptied.Text);
        Assert.Contains("Unmerged B3:C3", (await Edit("orders.xlsx", "unmerge B3:C3")).Text);

        // A colour written as #RRGGBB of noughts is black text, not a number format.
        var black = await Edit("orders.xlsx", "format B3:B7 text #000000");
        Assert.Contains("(text #000000)", black.Text);
        Assert.DoesNotContain("numbers as", black.Text);

        // Percent, dates, decimals, and Excel's own code.
        var numbers = await Edit("orders.xlsx", "format D3:D7 as percent\nformat A3:A7 as dates\nformat E3:E7 #,##0.0\nformat F3:F7 with 1 decimal");
        Assert.False(numbers.IsError, numbers.Text);
        text = await Read("orders.xlsx");
        Assert.Contains("D3 1600%", text);
        Assert.Contains("A3 2026-01-15", text);
        Assert.Contains("E3 29.5", text);
        Assert.Contains("F3 =D3*E3 → 472.0", text);
    }

    [Fact]
    public async Task Cells_get_a_dropdown_of_choices_or_of_other_cells()
    {
        Assert.False((await Write("orders.xlsx", Orders)).IsError);
        var made = await Edit("orders.xlsx", "G1 status\ndropdown G2:G6: Open, Paid, Late");
        Assert.False(made.IsError, made.Text);
        Assert.Contains("put a dropdown on G2:G6 on sheet \"Orders\" with Open, Paid, Late to pick from", made.Text);
        var rule = Part("orders.xlsx", "xl/worksheets/sheet1.xml").Descendants(S + "dataValidation").Single();
        Assert.Equal(("list", "G2:G6", "\"Open,Paid,Late\""), ((string?)rule.Attribute("type"), (string?)rule.Attribute("sqref"), rule.Element(S + "formula1")!.Value));
        Assert.Contains("(dropdown: G2:G6: Open, Paid, Late)", await Read("orders.xlsx"));

        // Another for the same cells takes its place; from other cells; and off again.
        Assert.False((await Edit("orders.xlsx", "add a dropdown to G2:G6 from B2:B6")).IsError);
        rule = Part("orders.xlsx", "xl/worksheets/sheet1.xml").Descendants(S + "dataValidation").Single();
        Assert.Equal("$B$2:$B$6", rule.Element(S + "formula1")!.Value);
        Assert.Contains("Took the dropdown off G2:G6", (await Edit("orders.xlsx", "no dropdown G2:G6")).Text);
        Assert.Empty(Part("orders.xlsx", "xl/worksheets/sheet1.xml").Descendants(S + "dataValidation"));
    }

    [Fact]
    public async Task Cells_are_highlighted_while_what_they_hold_meets_a_rule()
    {
        Assert.False((await Write("orders.xlsx", Orders)).IsError);
        // Whole rows by one column, as the request said it ("the orders over $3,000 stand out in red").
        var rows = await Edit("orders.xlsx", "highlight A2:F6 red where F > 3000");
        Assert.False(rows.IsError, rows.Text);
        Assert.Contains("Highlighted A2:F6 on sheet \"Orders\" (fill #FFC7CE, text #9C0006) where $F2>3000, for as long as that holds.", rows.Text);
        var rule = Part("orders.xlsx", "xl/worksheets/sheet1.xml").Descendants(S + "cfRule").Single();
        Assert.Equal(("expression", "$F2>3000"), ((string?)rule.Attribute("type"), rule.Element(S + "formula")!.Value));
        var dxf = Part("orders.xlsx", "xl/styles.xml").Descendants(S + "dxf").Single();
        Assert.Equal("FFFFC7CE", (string?)dxf.Descendants(S + "bgColor").Single().Attribute("rgb"));

        // One column by its own values, another colour, and in words; text as the value.
        Assert.False((await Edit("orders.xlsx", "highlight F2:F6 under $1,000 in green\ncolour B2:B6 bold where B is North\nformat D2:D6 yellow fill when between 10 and 20")).IsError);
        var text = await Read("orders.xlsx");
        Assert.Contains("(highlight: A2:F6 where $F2>3000: fill #FFC7CE, text #9C0006)", text);
        Assert.Contains("(highlight: F2:F6 where the cell is under 1000: fill #C6EFCE, text #006100)", text);
        Assert.Contains("(highlight: B2:B6 where $B2=\"North\": bold)", text);
        Assert.Contains("(highlight: D2:D6 where the cell is between 10 and 20: fill #FFFF00)", text);
        var sheet = Part("orders.xlsx", "xl/worksheets/sheet1.xml").Root!;
        Assert.Equal([1, 2, 3, 4], sheet.Descendants(S + "cfRule").Select(r => (int)r.Attribute("priority")!));

        // A title row put above all of it: the rules move down with their cells, and so do the rows kept in view.
        Assert.False((await Edit("orders.xlsx", "freeze row 1")).IsError);
        Assert.False((await Edit("orders.xlsx", "insert row 1\nA1 Orders")).IsError);
        text = await Read("orders.xlsx");
        Assert.Contains("(highlight: A3:F7 where $F3>3000: fill #FFC7CE, text #9C0006)", text);
        Assert.Contains("(frozen: rows 1-2)", text);
        Assert.False((await Edit("orders.xlsx", "delete row 1")).IsError);
        Assert.Contains("(highlight: A2:F6 where $F2>3000", await Read("orders.xlsx"));
        Assert.Contains("(frozen: row 1)", await Read("orders.xlsx"));

        // A format line without a where is still how the cells look, whatever words it has.
        var plain = await Edit("orders.xlsx", "format B2:B6 not bold, red text");
        Assert.Contains("Formatted B2:B6 on sheet \"Orders\" (not bold, text #FF0000).", plain.Text);
        Assert.Equal(4, Part("orders.xlsx", "xl/worksheets/sheet1.xml").Descendants(S + "cfRule").Count());
    }
}
