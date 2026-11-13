using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sloperator.Core.Documents;

/// <summary>
/// A chart on a sheet of an Excel workbook, drawn from its cells: what write_file makes of a line
/// "chart: bar Sales by month" under a table, and edit_file of a line "add chart line A1:A13,E1:E13 Monthly totals".
/// Until 2026-11-13 a workbook made here held cells only, and a chart asked for sent the models elsewhere: Haiku
/// and Sonnet looked for Python and matplotlib, Kilo's free Nemotron set about installing ComfyUI, 2 GB, to
/// generate a picture of one. A real chart, of Excel's own kind, follows the cells when they change.
/// The cells are laid out as Excel lays a selection out: the first column the labels and each other column a
/// series named by its top cell, or by rows when the cells are wider than they are tall.
/// </summary>
internal static partial class SheetChart
{
    private static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";

    public const string ChartRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart";
    public const string DrawingRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing";
    public const string ChartType = "application/vnd.openxmlformats-officedocument.drawingml.chart+xml";
    public const string DrawingType = "application/vnd.openxmlformats-officedocument.drawing+xml";

    /// <summary>The kinds of chart made, as they are asked for; "column" is "bar": bars standing up, what people mean by a bar chart.</summary>
    public const string Kinds = "bar, line, pie or area";

    /// <summary>How many rows of a sheet a chart covers (9 cm at Excel's 15 pt rows), so the next one goes under it.</summary>
    public const int RowsTall = 18;

    /// <summary>A chart's size on the sheet, in EMU: 15 by 9 cm.</summary>
    private const long Width = 5_400_000, Height = 3_240_000;

    /// <summary>A chart asked for: its kind, its title if given, and the cells to draw (none: the table above it).</summary>
    public sealed record Ask(string Kind, string? Title, IReadOnlyList<Cells> Ranges);

    /// <summary>A block of cells, on a named sheet or (null) the one the chart is on.</summary>
    public sealed record Cells(string? Sheet, int Row1, int Column1, int Row2, int Column2)
    {
        public int Rows => Row2 - Row1 + 1;
        public int Columns => Column2 - Column1 + 1;
    }

    /// <summary>
    /// A cell as a chart reads it: a number, text, or (both null) empty. <paramref name="Formula"/>: a formula whose
    /// value isn't known here (a function Excel works out): it counts as a number to come, drawn once Excel opens the file.
    /// </summary>
    /// <param name="Bold">The cell is bold: a heading of numbers ("2025", "2026" over two columns) is told from the figures under it by that.</param>
    public readonly record struct Value(string? Text, double? Number, bool Formula = false, bool Bold = false)
    {
        public bool IsText => Number is null && !string.IsNullOrWhiteSpace(Text);
    }

    /// <summary>What a cell of a sheet's XML holds: a formula's value is the one kept with it.</summary>
    /// <param name="shared">The workbook's shared strings, which a cell of text points into.</param>
    public static Value ValueOf(XElement? c, IReadOnlyList<string> shared)
    {
        if (c is null)
            return default;
        var v = (string?)c.Element(XlsxWriter.S + "v");
        if (v is null && c.Element(XlsxWriter.S + "f") is not null)
            return new Value(null, null, Formula: true);
        return (string?)c.Attribute("t") switch
        {
            "s" => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Count ? new Value(shared[i], null) : default,
            "inlineStr" => new Value(string.Concat(c.Descendants(XlsxWriter.S + "t").Select(t => t.Value)), null),
            "str" or "e" => v is null ? default : new Value(v, null),
            "b" => v is null ? default : new Value(v == "1" ? "TRUE" : "FALSE", null),
            _ => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? new Value(null, n) : default,
        };
    }

    /// <summary>A series of a chart: its name and where that is written, its labels and its values, each with the cells they are in.</summary>
    public sealed record Series(string Name, string? NameRef, string? LabelsRef, IReadOnlyList<Value> Labels, string ValuesRef, IReadOnlyList<Value> Values);

    /// <summary>
    /// A line asking for a chart, or null when it isn't one: "chart: bar Sales by month", "chart: line A1:A13,E1:E13
    /// Monthly totals", "add chart pie Budget!A2:B6 \"Where the money goes\"", "add a bar chart of A1:D13 titled
    /// Sales". The kind first, then the cells if any, then the title: a title's own "Q3" or "FY26" isn't taken for
    /// a cell, as cells are only read before it and always as a range with a colon.
    /// </summary>
    /// <param name="colon">Only "chart:" with its colon and a kind right after it starts one (a workbook's Markdown, where any other line is text for a cell).</param>
    public static Ask? Parse(string line, bool colon)
    {
        var start = (colon ? MarkdownLine() : EditLine()).Match(line.Trim());
        if (!start.Success)
            return null;
        var rest = start.Groups["rest"].Value;
        var kind = start.Groups["kind"].Success ? KindOf(start.Groups["kind"].Value) : null;
        string? title = null;
        if (Quoted().Match(rest) is { Success: true } quoted)
        {
            title = (quoted.Groups[1].Success ? quoted.Groups[1] : quoted.Groups[2]).Value.Trim();
            rest = rest.Remove(quoted.Index, quoted.Length);
        }
        // Before the cells: the kind, and the words around it ("bar chart of the cells A1:D13").
        while (true)
        {
            rest = rest.TrimStart(' ', '\t', ',', ':', ';');
            if (kind is null && FirstWord().Match(rest) is { Success: true } word && KindOf(word.Value) is { } found)
            {
                kind = found;
                rest = rest[word.Length..];
            }
            else if (Before().Match(rest) is { Success: true } filler)
                rest = rest[filler.Length..];
            else
                break;
        }
        var ranges = new List<Cells>();
        if (RangeList().Match(rest) is { Success: true } list)
        {
            foreach (Match m in RangeToken().Matches(list.Value))
            {
                var (r1, c1) = SheetEdit.Parse(m.Groups["a"].Value.Replace("$", "").ToUpperInvariant());
                var (r2, c2) = SheetEdit.Parse(m.Groups["b"].Value.Replace("$", "").ToUpperInvariant());
                var sheet = m.Groups["sheet"].Success ? m.Groups["sheet"].Value.Trim('\'').Replace("''", "'") : null;
                ranges.Add(new Cells(sheet, Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2)));
            }
            rest = rest[list.Length..];
        }
        // After them: "as a bar chart", "titled".
        rest = rest.TrimStart(' ', '\t', ',', ':', ';', '-', '–');
        if (kind is null && AsKind().Match(rest) is { Success: true } named)
        {
            kind = KindOf(named.Groups[1].Value);
            rest = rest[named.Length..].TrimStart(' ', '\t', ',', ':', ';', '-', '–');
        }
        if (Titled().Match(rest) is { Success: true } says)
            rest = rest[says.Length..];
        if (kind is null)
            return null;
        title ??= rest.Trim(' ', '\t', ',', ':', ';', '-', '–', '.');
        return new Ask(kind, string.IsNullOrWhiteSpace(title) ? null : title, ranges);
    }

    internal static string? KindOf(string word) => word.Trim(',', ':', '.').ToLowerInvariant() switch
    {
        "bar" or "bars" or "column" or "columns" or "histogram" => "bar",
        "line" or "lines" => "line",
        "pie" or "donut" or "doughnut" => "pie",
        "area" => "area",
        _ => null,
    };

    /// <summary>
    /// The series a chart of <paramref name="ranges"/> has. One block: its first column the labels and each other
    /// column a series, or by rows when it is wider than tall. Several, side by side with the same rows ("A1:A13,
    /// E1:E13") or one under another with the same columns: the first is the labels. A series is named by its
    /// first cell when that is text.
    /// </summary>
    /// <param name="sheet">The sheet the chart is on, for cells that name none.</param>
    /// <param name="cell">What a cell of a sheet holds.</param>
    /// <param name="notes">What to tell of the cells that were left out (a total row).</param>
    public static List<Series> SeriesOf(string sheet, IReadOnlyList<Cells> ranges, Func<string, int, int, Value> cell, List<string>? notes = null)
    {
        if (ranges.Count == 0)
            throw new InvalidOperationException("Say which cells the chart draws, such as A1:D13.");
        var byRows = ranges.Count == 1 ? ranges[0].Columns > ranges[0].Rows && ranges[0].Rows > 1 || ranges[0].Rows == 1 && ranges[0].Columns > 1
            : ranges.All(r => r.Column1 == ranges[0].Column1 && r.Column2 == ranges[0].Column2) ? true
            : ranges.All(r => r.Row1 == ranges[0].Row1 && r.Row2 == ranges[0].Row2) ? false
            : throw new InvalidOperationException("The cells of a chart are one block, or blocks side by side with the same rows (A1:A13,E1:E13), or one under another with the same columns.");
        // Each strip is a line of cells: a column of a block, or a row of it.
        var strips = new List<(string Sheet, int Row, int Column, int Length)>();
        foreach (var r in ranges)
            if (byRows)
                for (var row = r.Row1; row <= r.Row2; row++)
                    strips.Add((r.Sheet ?? sheet, row, r.Column1, r.Columns));
            else
                for (var column = r.Column1; column <= r.Column2; column++)
                    strips.Add((r.Sheet ?? sheet, r.Row1, column, r.Rows));
        Value At((string Sheet, int Row, int Column, int Length) s, int i) => byRows ? cell(s.Sheet, s.Row, s.Column + i) : cell(s.Sheet, s.Row + i, s.Column);
        string Ref((string Sheet, int Row, int Column, int Length) s, int from, int to) =>
            byRows ? Absolute(s.Sheet, s.Row, s.Column + from, s.Row, s.Column + to) : Absolute(s.Sheet, s.Row + from, s.Column, s.Row + to, s.Column);

        var length = strips[0].Length;
        var labels = strips.Count > 1 ? strips[0] : ((string, int, int, int)?)null;
        var values = strips.Skip(labels is null ? 0 : 1).ToList();
        // A heading over the values: text where the numbers start.
        // Or numbers in bold over figures that aren't: years as headings ("Month | 2025 | 2026") were drawn as each
        // series' first value, 2025 beside 10 and 14 (a slide's chart, 2026-11-13).
        var named = length > 1 && (values.Any(v => At(v, 0).IsText) || values.All(v => At(v, 0).Bold && !At(v, 1).Bold));
        var first = named ? 1 : 0;
        if (length - first < 1)
            throw new InvalidOperationException("Those cells hold no values to draw: give the cells with the numbers, their headings and labels with them.");
        // Excel's own limits for a chart; past them the file would open with the chart cut short, after a part of many megabytes was written.
        if (length - first > MaxValues || values.Count > MaxSeries)
            throw new InvalidOperationException($"A chart draws at most {MaxValues:N0} values in each of {MaxSeries} series, as in Excel: give fewer cells (these are {length - first:N0} values in {values.Count} series).");
        // A last row (or column) of totals isn't one of the things compared: as a slice of a pie it was half of it,
        // and as a bar it dwarfed the others. Haiku drew a pie of four regions and their total that way (2026-11-13).
        var end = length - 1;
        if (labels is { } total && end - first >= 2 && At(total, end).IsText && TotalLabel().IsMatch(At(total, end).Text!))
        {
            notes?.Add($"The \"{At(total, end).Text!.Trim()}\" {(byRows ? "column" : "row")} is left out of the chart, as it adds up the others; give the cells to draw to have it in.");
            end--;
        }
        var series = new List<Series>();
        foreach (var v in values)
        {
            var name = named ? At(v, 0) : default;
            var shown = name.Text is { Length: > 0 } text ? text : name.Number is { } number ? number.ToString("G15", CultureInfo.InvariantCulture) : $"Series {series.Count + 1}";
            series.Add(new Series(shown, named && (name.Text is not null || name.Number is not null) ? Ref(v, 0, 0) : null,
                labels is { } l ? Ref(l, first, end) : null,
                labels is { } l2 ? [.. Enumerable.Range(first, end - first + 1).Select(i => At(l2, i))] : [],
                Ref(v, first, end),
                [.. Enumerable.Range(first, end - first + 1).Select(i => At(v, i))]));
        }
        if (series.All(s => s.Values.All(v => v.Number is null && !v.Formula)))
            throw new InvalidOperationException("Those cells hold no numbers to draw: a chart's values are numbers or formulas, with their labels in the first column.");
        return series;
    }

    // "Total", "Totals", "Grand total", "Total revenue", "Sum", "Overall"
    [GeneratedRegex(@"^\s*(?:grand\s+|sub-?\s*)?totals?\b|^\s*(?:sum|overall)\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TotalLabel();

    /// <summary>The most values a series of a chart has, and the most series a chart has: Excel's limits.</summary>
    internal const int MaxValues = 32_000, MaxSeries = 255;

    /// <summary>"'Sales 2026'!$B$2:$B$13": cells as a chart names them.</summary>
    internal static string Absolute(string sheet, int row1, int column1, int row2, int column2)
    {
        static string One(int row, int column) => "$" + XlsxWriter.Reference(row, column).TrimEnd("0123456789".ToCharArray()) + "$" + row.ToString(CultureInfo.InvariantCulture);
        var cells = row1 == row2 && column1 == column2 ? One(row1, column1) : $"{One(row1, column1)}:{One(row2, column2)}";
        return $"'{sheet.Replace("'", "''")}'!{cells}";
    }

    // ---- the chart's part ------------------------------------------------------------------------------

    /// <summary>The colours of the series, Office's own six and two more: a workbook made here has no theme to take them from.</summary>
    private static readonly string[] Colours = ["4472C4", "ED7D31", "A5A5A5", "FFC000", "5B9BD5", "70AD47", "264478", "9E480E"];

    /// <summary>The chart's XML (xl/charts/chartN.xml). A pie draws the first series.</summary>
    /// <param name="workbook">On a slide: the chart's relationship to the workbook kept with it, which its cells are in.</param>
    /// <param name="points">The size of its text in points; a sheet's own 10 when not said (a slide is read from further away).</param>
    public static XDocument Chart(string kind, string? title, IReadOnlyList<Series> series, string? workbook = null, int? points = null)
    {
        var pie = kind == "pie";
        var shown = pie ? series.Take(1).ToList() : series;
        var body = kind switch
        {
            "line" => new XElement(C + "lineChart", new XElement(C + "grouping", Val("standard")), new XElement(C + "varyColors", Val(0))),
            // Stacked: side by side as plain areas, the one in front hid the ones behind it.
            "area" => new XElement(C + "areaChart", new XElement(C + "grouping", Val("stacked")), new XElement(C + "varyColors", Val(0))),
            "pie" => new XElement(C + "pieChart", new XElement(C + "varyColors", Val(1))),
            _ => new XElement(C + "barChart", new XElement(C + "barDir", Val("col")), new XElement(C + "grouping", Val("clustered")), new XElement(C + "varyColors", Val(0))),
        };
        for (var i = 0; i < shown.Count; i++)
            body.Add(Ser(kind, i, shown[i]));
        if (pie)
            body.Add(new XElement(C + "dLbls",
                new XElement(C + "showLegendKey", Val(0)), new XElement(C + "showVal", Val(0)), new XElement(C + "showCatName", Val(0)),
                new XElement(C + "showSerName", Val(0)), new XElement(C + "showPercent", Val(1)), new XElement(C + "showBubbleSize", Val(0)),
                new XElement(C + "showLeaderLines", Val(1))),
                new XElement(C + "firstSliceAng", Val(0)));
        else
        {
            if (kind == "bar")
                body.Add(new XElement(C + "gapWidth", Val(150)));
            if (kind == "line")
                body.Add(new XElement(C + "marker", Val(1)));
            body.Add(new XElement(C + "axId", Val(LabelAxis)), new XElement(C + "axId", Val(ValueAxis)));
        }

        var plot = new XElement(C + "plotArea", new XElement(C + "layout"), body);
        if (!pie)
            plot.Add(
                new XElement(C + "catAx",
                    new XElement(C + "axId", Val(LabelAxis)), new XElement(C + "scaling", new XElement(C + "orientation", Val("minMax"))),
                    new XElement(C + "delete", Val(0)), new XElement(C + "axPos", Val("b")),
                    new XElement(C + "numFmt", new XAttribute("formatCode", "General"), new XAttribute("sourceLinked", 1)),
                    new XElement(C + "majorTickMark", Val("none")), new XElement(C + "minorTickMark", Val("none")), new XElement(C + "tickLblPos", Val("nextTo")),
                    new XElement(C + "spPr", Ln(Soft, 9525)),
                    new XElement(C + "crossAx", Val(ValueAxis)), new XElement(C + "crosses", Val("autoZero")),
                    new XElement(C + "auto", Val(1)), new XElement(C + "lblAlgn", Val("ctr")), new XElement(C + "lblOffset", Val(100)), new XElement(C + "noMultiLvlLbl", Val(0))),
                new XElement(C + "valAx",
                    new XElement(C + "axId", Val(ValueAxis)), new XElement(C + "scaling", new XElement(C + "orientation", Val("minMax")),
                        // Bars and areas from zero: left to itself, the axis of four regions' revenue started at 39,000, and the
                        // gaps between them looked many times what they are (Haiku saw it on its slide, 2026-11-13). A line may start where its values do.
                        kind is "bar" or "area" && shown.All(s => s.Values.All(v => v.Number is not < 0)) ? new XElement(C + "min", Val(0)) : null),
                    new XElement(C + "delete", Val(0)), new XElement(C + "axPos", Val("l")),
                    new XElement(C + "majorGridlines", new XElement(C + "spPr", Ln(Soft, 9525))),
                    new XElement(C + "numFmt", new XAttribute("formatCode", "General"), new XAttribute("sourceLinked", 1)),
                    new XElement(C + "majorTickMark", Val("none")), new XElement(C + "minorTickMark", Val("none")), new XElement(C + "tickLblPos", Val("nextTo")),
                    new XElement(C + "spPr", new XElement(A + "ln", new XElement(A + "noFill"))),
                    new XElement(C + "crossAx", Val(LabelAxis)), new XElement(C + "crosses", Val("autoZero")),
                    new XElement(C + "crossBetween", Val(kind == "area" ? "midCat" : "between"))));

        var chart = new XElement(C + "chart");
        if (title is not null)
            chart.Add(new XElement(C + "title",
                new XElement(C + "tx", new XElement(C + "rich", new XElement(A + "bodyPr"), new XElement(A + "lstStyle"),
                    new XElement(A + "p", new XElement(A + "pPr", new XElement(A + "defRPr", new XAttribute("sz", (points ?? 10) * 140), new XAttribute("b", 0))),
                        new XElement(A + "r", new XElement(A + "rPr", new XAttribute("lang", "en-US"), new XAttribute("sz", (points ?? 10) * 140), new XAttribute("b", 0)), new XElement(A + "t", title))))),
                new XElement(C + "overlay", Val(0))));
        chart.Add(new XElement(C + "autoTitleDeleted", Val(title is null ? 1 : 0)), plot);
        // Who is who: for more than one series, and for a pie's slices.
        if (pie || shown.Count > 1)
            chart.Add(new XElement(C + "legend", new XElement(C + "legendPos", Val("b")), new XElement(C + "overlay", Val(0))));
        chart.Add(new XElement(C + "plotVisOnly", Val(1)), new XElement(C + "dispBlanksAs", Val("gap")));
        return new XDocument(new XElement(C + "chartSpace",
            new XAttribute(XNamespace.Xmlns + "c", C), new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", OfficePackage.R),
            new XElement(C + "roundedCorners", Val(0)), chart,
            // On a sheet a white card with a light frame; on a slide nothing around it, as the slide is its ground.
            workbook is null ? new XElement(C + "spPr", Fill("FFFFFF"), Ln(Soft, 9525))
                : new XElement(C + "spPr", new XElement(A + "noFill"), new XElement(A + "ln", new XElement(A + "noFill"))),
            new XElement(C + "txPr", new XElement(A + "bodyPr"), new XElement(A + "lstStyle"),
                new XElement(A + "p", new XElement(A + "pPr", new XElement(A + "defRPr", points is { } size ? new XAttribute("sz", size * 100) : null, Fill("404040"))), new XElement(A + "endParaRPr", new XAttribute("lang", "en-US")))),
            workbook is null ? null : new XElement(C + "externalData", new XAttribute(OfficePackage.R + "id", workbook), new XElement(C + "autoUpdate", Val(0)))));
    }

    /// <summary>The grey of a chart's frame, grid and axis: Excel's own for a new chart. The defaults of a file with no theme are black.</summary>
    private const string Soft = "D9D9D9";

    private const int LabelAxis = 504210432, ValueAxis = 504210816;

    private static XElement Ser(string kind, int index, Series s)
    {
        var colour = Colours[index % Colours.Length];
        var ser = new XElement(C + "ser", new XElement(C + "idx", Val(index)), new XElement(C + "order", Val(index)));
        ser.Add(new XElement(C + "tx", s.NameRef is { } nameRef
            ? new XElement(C + "strRef", new XElement(C + "f", nameRef), new XElement(C + "strCache", new XElement(C + "ptCount", Val(1)), new XElement(C + "pt", new XAttribute("idx", 0), new XElement(C + "v", s.Name))))
            : new XElement(C + "v", s.Name)));
        switch (kind)
        {
            case "line":
                ser.Add(new XElement(C + "spPr", Ln(colour, 28575)), new XElement(C + "marker", new XElement(C + "symbol", Val("none"))));
                break;
            case "pie":
                // Each slice its colour, a thin white line between them.
                for (var i = 0; i < s.Values.Count && i < 64; i++)
                    ser.Add(new XElement(C + "dPt", new XElement(C + "idx", Val(i)), new XElement(C + "bubble3D", Val(0)),
                        new XElement(C + "spPr", Fill(Colours[i % Colours.Length]), Ln("FFFFFF", 19050))));
                break;
            default:
                ser.Add(new XElement(C + "spPr", Fill(colour)));
                if (kind == "bar")
                    ser.Add(new XElement(C + "invertIfNegative", Val(0)));
                break;
        }
        if (s.LabelsRef is { } labelsRef)
            // Labels that are all numbers (years) are numbers to Excel; any text among them makes them text.
            ser.Add(new XElement(C + "cat", s.Labels.Count > 0 && s.Labels.All(l => l.Number is not null)
                ? Numbers(labelsRef, s.Labels)
                : new XElement(C + "strRef", new XElement(C + "f", labelsRef), new XElement(C + "strCache", new XElement(C + "ptCount", Val(s.Labels.Count)),
                    s.Labels.Select((l, i) => (l, i)).Where(p => p.l.Text is not null || p.l.Number is not null)
                        .Select(p => new XElement(C + "pt", new XAttribute("idx", p.i), new XElement(C + "v", p.l.Text ?? p.l.Number!.Value.ToString("G15", CultureInfo.InvariantCulture))))))));
        ser.Add(new XElement(C + "val", Numbers(s.ValuesRef, s.Values)));
        if (kind == "line")
            ser.Add(new XElement(C + "smooth", Val(0)));
        return ser;
    }

    /// <summary>Cells as numbers, with what they hold now: Excel reads the cells again on opening, other programs draw from this.</summary>
    private static XElement Numbers(string cells, IReadOnlyList<Value> values) =>
        new(C + "numRef", new XElement(C + "f", cells), new XElement(C + "numCache", new XElement(C + "formatCode", "General"), new XElement(C + "ptCount", Val(values.Count)),
            values.Select((v, i) => (v, i)).Where(p => p.v.Number is { } n && double.IsFinite(n))
                .Select(p => new XElement(C + "pt", new XAttribute("idx", p.i), new XElement(C + "v", p.v.Number!.Value.ToString("R", CultureInfo.InvariantCulture))))));

    private static XAttribute Val(object value) => new("val", value);

    private static XElement Fill(string colour) => new(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", colour)));

    /// <summary>A line of that colour and width (EMU), for a shape's properties.</summary>
    private static XElement Ln(string colour, int width) =>
        new(A + "ln", new XAttribute("w", width), new XAttribute("cap", "rnd"), Fill(colour), new XElement(A + "round"));

    // ---- where it is on the sheet ----------------------------------------------------------------------

    /// <summary>The frame of chart number <paramref name="id"/> on a sheet, its corner at a cell (rows and columns from 1), 15 by 9 cm.</summary>
    /// <param name="relation">The drawing's relationship to the chart's part.</param>
    public static XElement Anchor(int id, string relation, int row, int column) =>
        new(Xdr + "oneCellAnchor",
            new XElement(Xdr + "from", new XElement(Xdr + "col", column - 1), new XElement(Xdr + "colOff", 0), new XElement(Xdr + "row", row - 1), new XElement(Xdr + "rowOff", 0)),
            new XElement(Xdr + "ext", new XAttribute("cx", Width), new XAttribute("cy", Height)),
            new XElement(Xdr + "graphicFrame", new XAttribute("macro", ""),
                new XElement(Xdr + "nvGraphicFramePr", new XElement(Xdr + "cNvPr", new XAttribute("id", id + 1), new XAttribute("name", $"Chart {id}")), new XElement(Xdr + "cNvGraphicFramePr")),
                new XElement(Xdr + "xfrm", new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0))),
                new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", C.NamespaceName),
                    new XElement(C + "chart", new XAttribute(XNamespace.Xmlns + "c", C), new XAttribute(XNamespace.Xmlns + "r", OfficePackage.R), new XAttribute(OfficePackage.R + "id", relation))))),
            new XElement(Xdr + "clientData"));

    /// <summary>A sheet's drawing (xl/drawings/drawingN.xml) holding <paramref name="anchors"/>.</summary>
    public static XDocument Drawing(IEnumerable<XElement> anchors) =>
        new(new XElement(Xdr + "wsDr", new XAttribute(XNamespace.Xmlns + "xdr", Xdr), new XAttribute(XNamespace.Xmlns + "a", A), anchors));

    /// <summary>The row under what a drawing holds already (from 1), for the next chart; 0 when it holds nothing.</summary>
    public static int RowUnder(XDocument drawing)
    {
        var last = 0;
        foreach (var anchor in drawing.Root!.Elements())
        {
            // Rows count from 0 in a drawing: the last row it covers, counted from 1.
            var from = (int?)anchor.Element(Xdr + "from")?.Element(Xdr + "row") ?? 0;
            var to = (int?)anchor.Element(Xdr + "to")?.Element(Xdr + "row") ?? from + RowsTall - 1;
            last = Math.Max(last, to + 1);
        }
        // Two rows free between them, as between the charts of a workbook made here.
        return last == 0 ? 0 : last + 3;
    }

    /// <summary>How many frames a drawing holds, for the next one's number.</summary>
    public static int Count(XDocument drawing) => drawing.Root!.Descendants(Xdr + "cNvPr").Count();

    /// <summary>The sheet's drawing element goes before these, in the order Excel wants a sheet's parts in.</summary>
    public static readonly string[] AfterDrawing = ["legacyDrawing", "legacyDrawingHF", "picture", "oleObjects", "controls", "webPublishItems", "tableParts", "extLst"];

    // ---- what a chart is, for read_file ----------------------------------------------------------------

    /// <summary>A chart in a line: "bar chart "Sales by month": Sheet1!$B$2:$B$13, Sheet1!$C$2:$C$13 over Sheet1!$A$2:$A$13".</summary>
    public static string Describe(XDocument chart)
    {
        var plot = chart.Descendants(C + "plotArea").FirstOrDefault();
        var kinds = plot?.Elements().Where(e => e.Name.LocalName.EndsWith("Chart", StringComparison.Ordinal))
            .Select(e => e.Name.LocalName[..^5] switch { "bar" or "bar3D" => "bar", "line" or "line3D" => "line", "pie" or "pie3D" or "doughnut" or "ofPie" => "pie", "area" or "area3D" => "area", "scatter" => "scatter", var other => other })
            .Distinct().ToList() ?? [];
        var title = string.Concat(chart.Descendants(C + "title").FirstOrDefault()?.Descendants(A + "t").Select(t => t.Value) ?? []).Trim();
        var values = chart.Descendants(C + "val").Concat(chart.Descendants(C + "yVal")).Select(v => (string?)v.Descendants(C + "f").FirstOrDefault()).OfType<string>().Select(Plain).ToList();
        var labels = chart.Descendants(C + "cat").Concat(chart.Descendants(C + "xVal")).Select(v => (string?)v.Descendants(C + "f").FirstOrDefault()).OfType<string>().Select(Plain).Distinct().ToList();
        var line = $"{(kinds.Count > 0 ? string.Join("+", kinds) : "a")} chart";
        if (title.Length > 0)
            line += $" \"{title}\"";
        if (values.Count > 0)
            line += ": " + string.Join(", ", values.Take(8)) + (values.Count > 8 ? $" and {values.Count - 8} more" : "");
        if (labels.Count > 0)
            line += " over " + string.Join(", ", labels.Take(2));
        return line;
    }

    /// <summary>A chart's kind, as one is asked for here ("bar", "line", "pie", "area"; another kind by Excel's name for it), and its title if it has one.</summary>
    public static (string Kind, string? Title) About(XDocument chart)
    {
        var first = chart.Descendants(C + "plotArea").FirstOrDefault()?.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith("Chart", StringComparison.Ordinal))?.Name.LocalName;
        var kind = first?[..^5] switch
        {
            "bar" or "bar3D" => "bar",
            "line" or "line3D" => "line",
            "pie" or "pie3D" or "doughnut" or "ofPie" => "pie",
            "area" or "area3D" => "area",
            { Length: > 0 } other => other,
            _ => "a",
        };
        var title = string.Concat(chart.Descendants(C + "title").FirstOrDefault()?.Descendants(A + "t").Select(t => t.Value) ?? []).Trim();
        return (kind, title.Length > 0 ? title : null);
    }

    /// <summary>
    /// The series a chart that is there draws, read back from its part with the cells they name and the values kept
    /// with them: to make it anew as another kind or under another title, of the same cells.
    /// </summary>
    public static List<Series> SeriesIn(XDocument chart)
    {
        static List<Value> Kept(XElement? source)
        {
            if (source?.Descendants().FirstOrDefault(e => e.Name.LocalName is "numCache" or "strCache") is not { } cache)
                return [];
            var count = (int?)cache.Element(C + "ptCount")?.Attribute("val") ?? cache.Elements(C + "pt").Count();
            var values = new Value[Math.Clamp(count, 0, 100_000)];
            foreach (var pt in cache.Elements(C + "pt"))
            {
                if ((int?)pt.Attribute("idx") is not { } i || i < 0 || i >= values.Length)
                    continue;
                var v = (string?)pt.Element(C + "v") ?? "";
                values[i] = cache.Name.LocalName == "numCache" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? new Value(null, n) : new Value(v, null);
            }
            return [.. values];
        }
        var series = new List<Series>();
        foreach (var ser in chart.Descendants(C + "ser"))
        {
            var values = ser.Element(C + "val") ?? ser.Element(C + "yVal");
            if ((string?)values?.Descendants(C + "f").FirstOrDefault() is not { Length: > 0 } valuesRef)
                continue;
            var labels = ser.Element(C + "cat") ?? ser.Element(C + "xVal");
            var tx = ser.Element(C + "tx");
            var name = (string?)tx?.Descendants(C + "v").FirstOrDefault();
            series.Add(new Series(string.IsNullOrEmpty(name) ? $"Series {series.Count + 1}" : name, (string?)tx?.Descendants(C + "f").FirstOrDefault(),
                (string?)labels?.Descendants(C + "f").FirstOrDefault(), Kept(labels), valuesRef, Kept(values)));
        }
        return series;
    }

    /// <summary>
    /// The values a chart keeps beside the cells it names, read from those cells again. Excel reads the cells itself
    /// on opening, but a preview and other programs draw what is kept: the chart of a table sorted after it was made
    /// showed its bars in the order from before (the 1.5.0 release check). Cells that can't be followed here (a
    /// name, several areas, a sheet that isn't there, a formula only Excel works out) keep what they had.
    /// </summary>
    /// <returns>False when the chart already kept what the cells hold.</returns>
    public static bool Refresh(XDocument chart, Func<string, int, int, Value> cell)
    {
        var changed = false;
        foreach (var source in chart.Descendants().Where(e => e.Name == C + "strRef" || e.Name == C + "numRef").ToList())
        {
            if (WholeRange().Match(((string?)source.Element(C + "f") ?? "").Trim()) is not { Success: true } m || !m.Groups["sheet"].Success)
                continue;
            var sheet = m.Groups["sheet"].Value.Trim('\'').Replace("''", "'");
            var (r1, c1) = SheetEdit.Parse(m.Groups["a"].Value.Replace("$", "").ToUpperInvariant());
            var (r2, c2) = m.Groups["b"].Success ? SheetEdit.Parse(m.Groups["b"].Value.Replace("$", "").ToUpperInvariant()) : (r1, c1);
            if ((long)(Math.Abs(r2 - r1) + 1) * (Math.Abs(c2 - c1) + 1) > MaxValues)
                continue;
            var values = new List<Value>();
            try
            {
                for (var r = Math.Min(r1, r2); r <= Math.Max(r1, r2); r++)
                    for (var c = Math.Min(c1, c2); c <= Math.Max(c1, c2); c++)
                        values.Add(cell(sheet, r, c));
            }
            catch (InvalidOperationException)
            {
                continue; // a sheet that is gone
            }
            if (values.Any(v => v.Formula))
                continue;
            var numbers = source.Name == C + "numRef";
            var shown = values.Select(v => numbers
                ? v.Number is { } n && double.IsFinite(n) ? n.ToString("R", CultureInfo.InvariantCulture) : null
                : v.Text ?? v.Number?.ToString("G15", CultureInfo.InvariantCulture)).ToList();
            var cache = source.Element(C + (numbers ? "numCache" : "strCache"));
            var kept = new string?[shown.Count];
            var same = cache is not null && ((int?)cache.Element(C + "ptCount")?.Attribute("val") ?? -1) == shown.Count;
            foreach (var pt in cache?.Elements(C + "pt") ?? [])
                if ((int?)pt.Attribute("idx") is { } i && i >= 0 && i < kept.Length)
                    kept[i] = (string?)pt.Element(C + "v");
                else
                    same = false;
            static bool Alike(string? a, string? b, bool numbers) =>
                a == b || numbers && a is not null && b is not null
                && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && x == y;
            if (same && kept.Zip(shown).All(p => Alike(p.First, p.Second, numbers)))
                continue;
            var made = new XElement(C + (numbers ? "numCache" : "strCache"),
                numbers ? new XElement(C + "formatCode", (string?)cache?.Element(C + "formatCode") ?? "General") : null,
                new XElement(C + "ptCount", Val(shown.Count)),
                shown.Select((v, i) => (v, i)).Where(p => p.v is not null).Select(p => new XElement(C + "pt", new XAttribute("idx", p.i), new XElement(C + "v", p.v))));
            if (cache is not null)
                cache.ReplaceWith(made);
            else if (source.Element(C + "f") is { } f)
                f.AddAfterSelf(made);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// A chart of a slide in a line, with the numbers it draws, which are nowhere else on the slide:
    /// "bar chart "Sales by region": Sales: North 120, South 95, East 143, West 88".
    /// </summary>
    public static string DescribeData(XDocument chart)
    {
        var (kind, title) = About(chart);
        var line = $"{kind} chart" + (title is null ? "" : $" \"{title}\"");
        var series = SeriesIn(chart);
        if (series.Count == 0)
            return line;
        static string Shown(Value v) => v.Number is { } n ? n.ToString("G10", CultureInfo.InvariantCulture) : v.Text ?? "";
        return line + ": " + string.Join("; ", series.Take(8).Select(s => s.Name + ": " + string.Join(", ",
            s.Values.Take(24).Select((v, i) => (i < s.Labels.Count && Shown(s.Labels[i]) is { Length: > 0 } label ? label + " " : "") + Shown(v))) +
            (s.Values.Count > 24 ? $" and {s.Values.Count - 24} more" : ""))) + (series.Count > 8 ? $"; and {series.Count - 8} more series" : "");
    }

    /// <summary>"Sheet1!A2:A13" for "'Sheet1'!$A$2:$A$13": as read_file writes cells.</summary>
    private static string Plain(string cells) => cells.Replace("$", "");

    // "chart: bar …", "Chart : line …"
    [GeneratedRegex(@"^chart\s*:\s*(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownLine();

    // "add chart bar A1:D13 …", "add a line chart of A1:B13 titled …", "insert pie chart Budget!A2:B6"
    [GeneratedRegex(@"^(?:add|insert|create|make)\s+(?:an?\s+)?(?:(?<kind>bar|column|line|pie|area)\s+)?chart\b\s*:?\s*(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex EditLine();

    [GeneratedRegex("\"([^\"]*)\"|“([^”]*)”")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^[A-Za-z]+")]
    private static partial Regex FirstWord();

    // Words between "chart" and its cells: "of", "for the cells", "from range".
    [GeneratedRegex(@"^(?:charts?|of|for|from|cells?|range|the(?=\s+(?:cells?|range)\b))\b", RegexOptions.IgnoreCase)]
    private static partial Regex Before();

    // A1:D13, $A$1:$D$13, Budget!A1:B6, 'My sheet'!C2:C9: a range with its colon, on a sheet if named.
    private const string RangePattern = @"(?:(?<sheet>'(?:[^']|'')+'|[A-Za-z_\p{L}][\w.\p{L}]*)!)?(?<a>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6}):(?<b>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6})";

    [GeneratedRegex(RangePattern + @"(?![A-Za-z0-9_!(])")]
    private static partial Regex RangeToken();

    // What a chart's part names: 'Sales 2026'!$B$2:$B$13, or one cell, Sales!$B$1.
    [GeneratedRegex(@"^(?:(?<sheet>'(?:[^']|'')+'|[A-Za-z_\p{L}][\w.\p{L}]*)!)?(?<a>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6})(?::(?<b>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6}))?$")]
    private static partial Regex WholeRange();

    // One range, or several joined by commas: "A1:A13,E1:E13", "A1:A13; E1:E13".
    [GeneratedRegex(@"^" + RangePattern + @"(?:\s*[,;]\s*" + RangePattern + @")*(?![A-Za-z0-9_!(])", RegexOptions.ExplicitCapture)]
    private static partial Regex RangeList();

    [GeneratedRegex(@"^(?:as\s+)?(?:an?\s+)?(bar|bars|column|columns|line|lines|pie|area)\b(?:\s+charts?\b)?", RegexOptions.IgnoreCase)]
    private static partial Regex AsKind();

    [GeneratedRegex(@"^(?:titled|title|called|named|with\s+(?:the\s+)?title)\b\s*:?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Titled();
}
