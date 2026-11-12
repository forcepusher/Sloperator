using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>edit_file's "insert column C" and "delete columns B-C": cells, formulas, rules and widths move as in Excel.</summary>
public class SheetColumnsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-columns-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private ToolContext Ctx() => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir };

    private Task<ToolResult> Write(string file, string content) =>
        new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = file, ["content"] = content }, Ctx(), CancellationToken.None);

    private Task<ToolResult> Edit(string newText) =>
        new EditFileTool().ExecuteAsync(new JsonObject { ["path"] = "orders.xlsx", ["old_text"] = "", ["new_text"] = newText }, Ctx(), CancellationToken.None);

    private async Task<string> Read() => System.Text.RegularExpressions.Regex.Replace(
        (await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = "orders.xlsx" }, Ctx(), CancellationToken.None)).Text.ReplaceLineEndings("\n"), @"(?m)^\d+\t", "");

    private XDocument Sheet()
    {
        using var zip = ZipFile.OpenRead(Path.Combine(_dir, "orders.xlsx"));
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        return XDocument.Load(stream);
    }

    private const string Orders = """
        # Orders
        | date | region | product | units | price | revenue |
        |---|---|---|---|---|---|
        | 2026-01-15 | North | Lamp | 16 | 29.5 | =D2*E2 |
        | 2026-01-15 | South | Desk | 12 | 310 | =D3*E3 |
        | 2026-02-15 | East | Chair | 35 | 89 | =D4*E4 |
        | Total | | | =SUM(D2:D4) | | =SUM(F2:F4) |
        """;

    [Fact]
    public async Task A_column_put_in_moves_the_cells_and_formulas_to_its_right_and_one_taken_out_brings_them_back()
    {
        Assert.False((await Write("orders.xlsx", Orders)).IsError);
        // A column for a product code after the region, filled in the same edit.
        var inserted = await Edit("insert column C\nC1 code\nC2 L-1\nC3 D-2\nC4 C-3");
        Assert.False(inserted.IsError, inserted.Text);
        Assert.Contains("Inserted column C on sheet \"Orders\", then set C1, C2, C3, C4", inserted.Text);
        var text = await Read();
        Assert.Contains("A1 date | B1 region | C1 code | D1 product | E1 units | F1 price | G1 revenue", text);
        Assert.Contains("A2 2026-01-15 | B2 North | C2 L-1 | D2 Lamp | E2 16 | F2 29.5 | G2 =E2*F2 → 472", text);
        Assert.Contains("E5 =SUM(E2:E4) → 63", text);
        Assert.Contains("G5 =SUM(G2:G4) → 7307", text);
        // The new header cell looks like the one to its left: bold.
        Assert.Contains("(bold: A1, B1, C1, D1, E1, F1, G1", text);

        // Taken out again, all is where it was.
        var deleted = await Edit("delete column C");
        Assert.False(deleted.IsError, deleted.Text);
        text = await Read();
        Assert.Contains("A2 2026-01-15 | B2 North | C2 Lamp | D2 16 | E2 29.5 | F2 =D2*E2 → 472", text);
        Assert.Contains("F5 =SUM(F2:F4) → 7307", text);

        // A column the formulas name, taken out: they say so, as Excel's do.
        Assert.False((await Edit("delete column D")).IsError);
        Assert.Contains("E2 =#REF!*D2", await Read());
    }

    [Fact]
    public async Task Rules_frozen_columns_and_widths_move_with_the_columns()
    {
        Assert.False((await Write("orders.xlsx", Orders)).IsError);
        Assert.False((await Edit("highlight A2:F4 red where F > 3000\nfreeze panes at C2\ncolumn B width 20")).IsError);
        var moved = await Edit("insert column B");
        Assert.False(moved.IsError, moved.Text);
        var text = await Read();
        Assert.Contains("(highlight: A2:G4 where $G2>3000", text);
        Assert.Contains("(frozen: row 1 and columns A-C)", text);
        var widths = Sheet().Root!.Element(S + "cols")!.Elements(S + "col").ToDictionary(c => (int)c.Attribute("min")!, c => (string?)c.Attribute("width"));
        Assert.Equal("20", widths[3]);

        // Two at once, and a column past the sheet's end said.
        Assert.Contains("Deleted columns B-C on sheet \"Orders\"", (await Edit("delete columns B-C")).Text);
        Assert.Contains("(highlight: A2:E4 where $E2>3000", await Read());
        var past = await Edit("insert column ZZZ");
        Assert.True(past.IsError);
        Assert.Contains("a sheet ends at column XFD", past.Text);
    }
}
