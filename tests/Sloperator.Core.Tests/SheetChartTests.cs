using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Sloperator.Core.Documents;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Charts in Excel workbooks: a "chart:" line under a table in write_file's Markdown, an "add chart" line in
/// edit_file, and what read_file says of them. That Excel itself opens them is checked by hand (BB_DOC_OUT, see
/// <see cref="DocumentWriteTests.Samples_to_open_in_office"/>).
/// </summary>
public class SheetChartTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-charts-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    private static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

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
        using var stream = (zip.GetEntry(part) ?? throw new InvalidDataException($"{part} is missing; the file has {string.Join(", ", zip.Entries.Select(e => e.FullName))}")).Open();
        return XDocument.Load(stream);
    }

    private const string Sales = """
        # Sales
        | Month | Apples | Pears | Total |
        |---|---|---|---|
        | Jan | 120 | 80 | =B2+C2 |
        | Feb | 150 | 95 | =B3+C3 |
        | Mar | 170 | 60 | =B4+C4 |
        """;

    [Theory]
    [InlineData("chart: bar Sales by month", "bar", "Sales by month", 0)]
    [InlineData("chart: line A1:A13,E1:E13 Monthly totals", "line", "Monthly totals", 2)]
    [InlineData("Chart: pie \"Where the money goes\" Budget!A2:B6", "pie", "Where the money goes", 1)]
    [InlineData("chart: column, A1:D4, Q3 revenue by FY26 plan", "bar", "Q3 revenue by FY26 plan", 1)]
    [InlineData("chart: area", "area", null, 0)]
    public void A_chart_line_of_a_workbooks_markdown_says_its_kind_cells_and_title(string line, string kind, string? title, int ranges)
    {
        var ask = SheetChart.Parse(line, colon: true)!;
        Assert.Equal((kind, title, ranges), (ask.Kind, ask.Title, ask.Ranges.Count));
    }

    [Theory]
    [InlineData("add chart bar A1:D13 Sales by month", "bar", "Sales by month", 1)]
    [InlineData("add a line chart of A1:A13, E1:E13 titled Monthly totals", "line", "Monthly totals", 2)]
    [InlineData("Add chart: pie 'My sheet'!A2:B6 \"Shares\"", "pie", "Shares", 1)]
    [InlineData("insert chart of the cells $A$1:$B$5 as a bar chart", "bar", null, 1)]
    public void A_chart_line_of_an_edit_says_the_same(string line, string kind, string? title, int ranges)
    {
        var ask = SheetChart.Parse(line, colon: false)!;
        Assert.Equal((kind, title, ranges), (ask.Kind, ask.Title, ask.Ranges.Count));
    }

    [Theory]
    [InlineData("Chart of accounts", true)]
    [InlineData("chart: see the next sheet", true)]
    [InlineData("Charts: none this month", true)]
    [InlineData("B7 =SUM(B2:B6)", false)]
    [InlineData("add a row for the chart", false)]
    [InlineData("insert row 7", false)]
    public void Other_lines_are_not_charts(string line, bool colon) => Assert.Null(SheetChart.Parse(line, colon));

    [Fact]
    public async Task A_chart_line_under_a_table_draws_the_table()
    {
        var result = await Write("sales.xlsx", Sales + "\nchart: bar Sales by month\n\nMade by hand.\n");
        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain("left out", result.Text);

        // The chart's part: three series by their headings, over the months, with the numbers the cells hold.
        var chart = Part("sales.xlsx", "xl/charts/chart1.xml");
        var bar = chart.Descendants(C + "barChart").Single();
        Assert.Equal("col", (string?)bar.Element(C + "barDir")!.Attribute("val"));
        var series = bar.Elements(C + "ser").ToList();
        Assert.Equal(["Apples", "Pears", "Total"], series.Select(s => s.Element(C + "tx")!.Descendants(C + "v").Single().Value));
        Assert.Equal(["'Sales'!$B$1", "'Sales'!$C$1", "'Sales'!$D$1"], series.Select(s => s.Element(C + "tx")!.Descendants(C + "f").Single().Value));
        Assert.All(series, s => Assert.Equal("'Sales'!$A$2:$A$4", s.Element(C + "cat")!.Descendants(C + "f").Single().Value));
        Assert.Equal(["Jan", "Feb", "Mar"], series[0].Element(C + "cat")!.Descendants(C + "v").Select(v => v.Value));
        Assert.Equal("'Sales'!$C$2:$C$4", series[1].Element(C + "val")!.Descendants(C + "f").Single().Value);
        Assert.Equal(["80", "95", "60"], series[1].Element(C + "val")!.Descendants(C + "v").Select(v => v.Value));
        // The totals are formulas: their values were worked out here, and the chart has them.
        Assert.Equal(["200", "245", "230"], series[2].Element(C + "val")!.Descendants(C + "v").Select(v => v.Value));
        Assert.Equal("Sales by month", string.Concat(chart.Descendants(C + "title").Single().Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value)));
        Assert.Single(chart.Descendants(C + "legend"));

        // It hangs on the sheet: the sheet's drawing, to the right of the cells, and the relationships between them.
        var sheet = Part("sales.xlsx", "xl/worksheets/sheet1.xml");
        Assert.Equal("rId1", (string?)sheet.Root!.Element(S + "drawing")!.Attribute(R + "id"));
        Assert.Same(sheet.Root.Elements().Last(), sheet.Root.Element(S + "drawing"));
        Assert.Contains("../drawings/drawing1.xml", Part("sales.xlsx", "xl/worksheets/_rels/sheet1.xml.rels").ToString());
        Assert.Contains("../charts/chart1.xml", Part("sales.xlsx", "xl/drawings/_rels/drawing1.xml.rels").ToString());
        var drawing = Part("sales.xlsx", "xl/drawings/drawing1.xml");
        Assert.Equal("5", drawing.Descendants().Single(e => e.Name.LocalName == "col").Value);
        var types = Part("sales.xlsx", "[Content_Types].xml").ToString();
        Assert.Contains("/xl/charts/chart1.xml", types);
        Assert.Contains("/xl/drawings/drawing1.xml", types);

        // The line itself is no cell, and read_file says what the chart draws.
        var text = await Read("sales.xlsx");
        Assert.Contains("A5 Made by hand.", text);
        Assert.Contains("(chart: bar chart \"Sales by month\": 'Sales'!B2:B4, 'Sales'!C2:C4, 'Sales'!D2:D4 over 'Sales'!A2:A4)", text);
    }

    [Fact]
    public async Task A_chart_can_name_its_cells_and_a_pie_draws_one_series()
    {
        var result = await Write("sales.xlsx", Sales + "\nchart: line A1:A4,D1:D4 Totals\nchart: pie A1:B4\n");
        Assert.False(result.IsError, result.Text);
        var line = Part("sales.xlsx", "xl/charts/chart1.xml").Descendants(C + "lineChart").Single();
        var only = Assert.Single(line.Elements(C + "ser"));
        Assert.Equal("'Sales'!$D$2:$D$4", only.Element(C + "val")!.Descendants(C + "f").Single().Value);
        Assert.Equal("'Sales'!$A$2:$A$4", only.Element(C + "cat")!.Descendants(C + "f").Single().Value);
        // One series: no legend to tell them apart.
        Assert.Empty(Part("sales.xlsx", "xl/charts/chart1.xml").Descendants(C + "legend"));

        var pie = Part("sales.xlsx", "xl/charts/chart2.xml");
        Assert.Equal("Apples", Assert.Single(pie.Descendants(C + "pieChart").Single().Elements(C + "ser")).Element(C + "tx")!.Descendants(C + "v").Single().Value);
        Assert.Equal(3, pie.Descendants(C + "dPt").Count());
        Assert.Empty(pie.Descendants(C + "catAx"));
        Assert.Single(pie.Descendants(C + "legend"));
        // The second chart under the first, both in the sheet's one drawing.
        var rows = Part("sales.xlsx", "xl/drawings/drawing1.xml").Descendants().Where(e => e.Name.LocalName == "row").Select(e => int.Parse(e.Value)).ToList();
        Assert.Equal([1, 1 + SheetChart.RowsTall + 2], rows);
    }

    [Fact]
    public async Task Totals_on_a_sheet_of_their_own_are_worked_out_and_charted()
    {
        // As Haiku wrote it (2026-11-13): the figures on one sheet, their totals on another, by formulas across the
        // two. The totals had no values here, the chart of them was refused as holding no numbers, and Haiku typed
        // the totals in as plain numbers to get its chart.
        var result = await Write("sales.xlsx", """
            # Sales
            | Month | Product A | Product B |
            |---|---|---|
            | Jan | $1,200.00 | $800.00 |
            | Feb | $1,350.00 | $760.00 |

            # Monthly totals
            | Month | Total sales | Share of 'Sales' |
            |---|---|---|
            | Jan | =Sales!B2+Sales!C2 | =B2/SUM('Sales'!$B$2:$C$3) |
            | Feb | =Sales!B3+Sales!C3 | =B3/SUM(Sales!B2:C3) |

            chart: bar A1:B3 Monthly totals
            """);
        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain("left out", result.Text);
        var text = await Read("sales.xlsx");
        Assert.Contains("B2 =Sales!B2+Sales!C2 → 2000", text);
        Assert.Contains("B3 =Sales!B3+Sales!C3 → 2110", text);
        Assert.Matches(@"C2 =B2/SUM\('Sales'!\$B\$2:\$C\$3\) → 0\.4866", text);
        var chart = Part("sales.xlsx", "xl/charts/chart1.xml");
        Assert.Equal("'Monthly totals'!$B$2:$B$3", chart.Descendants(C + "val").Single().Descendants(C + "f").Single().Value);
        Assert.Equal(["2000", "2110"], chart.Descendants(C + "val").Single().Descendants(C + "v").Select(v => v.Value));

        // Changed on the first sheet through edit_file, the totals on the second follow.
        Assert.False((await Edit("sales.xlsx", "", "Sales!B2 $1,500.00")).IsError);
        Assert.Contains("B2 =Sales!B2+Sales!C2 → 2300", await Read("sales.xlsx"));
    }

    [Fact]
    public async Task A_chart_of_formulas_only_excel_works_out_is_still_made()
    {
        // IF is Excel's to work out: no numbers here yet, but they are formulas, and the chart draws them once opened.
        var result = await Write("flags.xlsx", "| Day | Late |\n|---|---|\n| Mon | =IF(1>0, 3, 0) |\n| Tue | =IF(1>2, 3, 5) |\n\nchart: line Late by day");
        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain("left out", result.Text);
        var value = Part("flags.xlsx", "xl/charts/chart1.xml").Descendants(C + "val").Single();
        Assert.Equal("'Sheet1'!$B$2:$B$3", value.Descendants(C + "f").Single().Value);
        Assert.Equal("2", (string?)value.Descendants(C + "ptCount").Single().Attribute("val"));
        Assert.Empty(value.Descendants(C + "pt"));
    }

    [Fact]
    public async Task A_chart_with_nothing_to_draw_is_left_out_and_said()
    {
        var none = await Write("none.xlsx", "# Notes\nchart: bar Sales\n\nJust text.");
        Assert.False(none.IsError, none.Text);
        Assert.Contains("The bar chart was left out: its line has no table above it", none.Text);
        using (var zip = ZipFile.OpenRead(Path.Combine(_dir, "none.xlsx")))
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("chart"));

        var words = await Write("words.xlsx", "| Name | City |\n|---|---|\n| Ann | Oslo |\n| Bo | Rome |\nchart: pie Cities");
        Assert.Contains("The pie chart \"Cities\" was left out. Those cells hold no numbers to draw", words.Text);
    }

    [Fact]
    public async Task Edit_file_adds_a_chart_to_a_workbook_and_a_second_goes_under_it()
    {
        Assert.False((await Write("sales.xlsx", Sales)).IsError);
        var added = await Edit("sales.xlsx", "", "add chart line A1:A4,D1:D4 Monthly totals");
        Assert.False(added.IsError, added.Text);
        Assert.Contains("Added a line chart \"Monthly totals\" to sheet \"Sales\" at F2: Total ('Sales'!D2:D4) over 'Sales'!A2:A4", added.Text);

        var chart = Part("sales.xlsx", "xl/charts/chart1.xml");
        Assert.Equal(["200", "245", "230"], chart.Descendants(C + "val").Single().Descendants(C + "v").Select(v => v.Value));
        var sheet = Part("sales.xlsx", "xl/worksheets/sheet1.xml");
        var id = (string)sheet.Root!.Element(S + "drawing")!.Attribute(R + "id")!;
        Assert.Contains($"Id=\"{id}\"", Part("sales.xlsx", "xl/worksheets/_rels/sheet1.xml.rels").ToString());
        Assert.Contains("/xl/charts/chart1.xml", Part("sales.xlsx", "[Content_Types].xml").ToString());
        Assert.Contains("(chart: line chart \"Monthly totals\": 'Sales'!D2:D4 over 'Sales'!A2:A4)", await Read("sales.xlsx"));

        // With cells set in the same edit, which the chart then draws; it goes under the first, in the same drawing.
        var second = await Edit("sales.xlsx", "", "A5 Apr | B5 90 | C5 70 | D5 =B5+C5\nadd chart bar A1:C5 \"Fruit\"");
        Assert.False(second.IsError, second.Text);
        Assert.Contains("added a bar chart \"Fruit\" to sheet \"Sales\" at F22", second.Text);
        var bar = Part("sales.xlsx", "xl/charts/chart2.xml").Descendants(C + "barChart").Single();
        Assert.Equal(["Jan", "Feb", "Mar", "Apr"], bar.Elements(C + "ser").First().Element(C + "cat")!.Descendants(C + "v").Select(v => v.Value));
        Assert.Equal(["120", "150", "170", "90"], bar.Elements(C + "ser").First().Element(C + "val")!.Descendants(C + "v").Select(v => v.Value));
        Assert.Equal(2, Part("sales.xlsx", "xl/drawings/drawing1.xml").Root!.Elements().Count());
        var text = await Read("sales.xlsx");
        Assert.Contains("(chart: line chart \"Monthly totals\"", text);
        Assert.Contains("(chart: bar chart \"Fruit\": 'Sales'!B2:B5, 'Sales'!C2:C5 over 'Sales'!A2:A5)", text);
    }

    [Fact]
    public async Task Edit_file_says_what_is_wrong_with_a_chart_it_cannot_draw()
    {
        Assert.False((await Write("sales.xlsx", Sales)).IsError);
        var nowhere = await Edit("sales.xlsx", "", "add chart bar Sales by month");
        Assert.True(nowhere.IsError);
        Assert.Contains("Say which cells the chart draws", nowhere.Text);
        var missing = await Edit("sales.xlsx", "", "add chart bar Costs!A1:B4");
        Assert.True(missing.IsError);
        Assert.Contains("There is no sheet \"Costs\"", missing.Text);
        var empty = await Edit("sales.xlsx", "", "add chart pie H1:I9 Nothing");
        Assert.True(empty.IsError);
        Assert.Contains("hold no numbers to draw", empty.Text);
        // Nothing of a refused chart is left in the file.
        using var zip = ZipFile.OpenRead(Path.Combine(_dir, "sales.xlsx"));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("chart"));
    }

    [Fact]
    public void Cells_wider_than_tall_are_series_by_rows()
    {
        // Months across, a row of totals under them: as Excel draws a selection of that shape.
        SheetChart.Value Cell(string sheet, int row, int column) => (row, column) switch
        {
            (1, 1) => new("Month", null),
            (2, 1) => new("Total", null),
            (1, _) => new(new[] { "Jan", "Feb", "Mar", "Apr" }[column - 2], null),
            _ => new(null, column * 10),
        };
        var series = Assert.Single(SheetChart.SeriesOf("Year", [new SheetChart.Cells(null, 1, 1, 2, 5)], Cell));
        Assert.Equal(("Total", "'Year'!$A$2", "'Year'!$B$1:$E$1", "'Year'!$B$2:$E$2"), (series.Name, series.NameRef, series.LabelsRef, series.ValuesRef));
        Assert.Equal([20d, 30d, 40d, 50d], series.Values.Select(v => v.Number!.Value));
        Assert.Throws<InvalidOperationException>(() => SheetChart.SeriesOf("Year", [new SheetChart.Cells(null, 1, 1, 2, 2), new SheetChart.Cells(null, 5, 4, 9, 4)], Cell));
    }

    /// <summary>
    /// Opt-in (BB_DOC_OUT): workbooks with charts, made and edited, in a folder to open in Excel, which says so when
    /// a file is damaged in a way no test here sees (2026-11-13: opened in Excel 16 through its automation, each
    /// chart counted and exported as a picture, and in LibreOffice).
    /// </summary>
    [Fact]
    public async Task Samples_to_open_in_excel()
    {
        var output = Environment.GetEnvironmentVariable("BB_DOC_OUT");
        Assert.SkipWhen(output is null, "Set BB_DOC_OUT to a folder to run.");
        async Task Ok(Task<ToolResult> step) { var r = await step; Assert.False(r.IsError, r.Text); Assert.DoesNotContain("left out", r.Text); }
        const string year = """
            # Sales 2026
            | Month | Apples | Pears | Plums | Total |
            |---|---|---|---|---|
            | Jan | $1,200.00 | $800.00 | $300.00 | =SUM(B2:D2) |
            | Feb | $1,500.00 | $950.00 | $280.00 | =SUM(B3:D3) |
            | Mar | $1,700.00 | $600.00 | $410.00 | =SUM(B4:D4) |
            | Apr | $1,400.00 | $720.00 | $390.00 | =SUM(B5:D5) |
            | May | $1,900.00 | $880.00 | $350.00 | =SUM(B6:D6) |
            | Jun | $2,100.00 | $910.00 | $420.00 | =SUM(B7:D7) |
            """;
        await Ok(Write("made.xlsx", year + "\nchart: bar Sales by month\nchart: line A1:A7,E1:E7 Monthly totals\nchart: area A1:D7 \"Stacked up\"\n\n# Budget\n| Item | Cost |\n|---|---|\n| Rent | 900 |\n| Food | 350 |\n| Transport | 80 |\n| Fun | 120 |\n\nchart: pie Where the money goes\n"));
        await Ok(Write("edited.xlsx", year));
        await Ok(Edit("edited.xlsx", "", "add chart line A1:A7,E1:E7 Monthly totals"));
        await Ok(Edit("edited.xlsx", "", "A8 Jul | B8 $2,300.00 | C8 $870.00 | D8 $440.00 | E8 =SUM(B8:D8)\nadd chart bar A1:D8 \"Fruit by month\""));
        await Ok(Write("across.xlsx", "# Year\n| Quarter | Q1 | Q2 | Q3 | Q4 |\n|---|---|---|---|---|\n| North | 10 | 14 | 9 | 17 |\n| South | 7 | 8 | 12 | 11 |\n\nchart: line By quarter"));
        await Ok(Write("budget.xlsx", "# Budget\n| Item | Cost |\n|---|---|\n| Rent | 900 |\n| Food | 350 |\n| Transport | 80 |\n| Fun | 120 |\n\nchart: pie Where the money goes\n"));
        Directory.CreateDirectory(output!);
        foreach (var name in new[] { "made.xlsx", "edited.xlsx", "across.xlsx", "budget.xlsx" })
            File.Copy(Path.Combine(_dir, name), Path.Combine(output!, name), overwrite: true);
        // A workbook Excel saved with a chart of its own, put in the folder as excel.xlsx (cells A1:D5 on its first
        // sheet): a second chart goes into the drawing Excel made, under the first.
        if (File.Exists(Path.Combine(output!, "excel.xlsx")))
        {
            File.Copy(Path.Combine(output!, "excel.xlsx"), Path.Combine(_dir, "excel-edited.xlsx"));
            await Ok(Edit("excel-edited.xlsx", "", "add chart line A1:D5 \"Added here\""));
            Assert.Contains("\"Added here\"", await Read("excel-edited.xlsx"));
            File.Copy(Path.Combine(_dir, "excel-edited.xlsx"), Path.Combine(output!, "excel-edited.xlsx"), overwrite: true);
        }
    }
}
