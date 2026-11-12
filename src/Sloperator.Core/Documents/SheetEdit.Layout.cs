using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sloperator.Core.Documents;

/// <summary>
/// The lines of edit_file that change how a sheet is laid out, beside what its cells hold: "sort A2:F73 by F
/// descending", "freeze row 1", "filter A1:F73", "merge A1:F1", "highlight A2:F73 red where F > 3000". Asked to sort
/// a workbook Excel had saved by revenue, freeze and filter its header, show two columns in dollars and make the
/// orders over $3,000 stand out in red (2026-11-13), Haiku rewrote the sheet's XML through PowerShell in 30 calls,
/// rebuilding the zip twice (once with backslashes in its entry names, which Excel can't open), and Sonnet drove
/// Excel through its automation, which a PC without Office doesn't have.
/// </summary>
internal static partial class SheetEdit
{
    /// <summary>A layout line: the sheet it names, if any, and what it does to that sheet; what was done, for the answer.</summary>
    private sealed record LayoutLine(string? Sheet, bool Sort, Func<PartSet, List<(string Name, string Part)>, (string Name, string Part), List<string>, string> Apply);

    /// <summary>The order of a worksheet's parts, which Excel holds a file to.</summary>
    private static readonly string[] SheetOrder =
    [
        "sheetPr", "dimension", "sheetViews", "sheetFormatPr", "cols", "sheetData", "sheetCalcPr", "sheetProtection", "protectedRanges", "scenarios",
        "autoFilter", "sortState", "dataConsolidate", "customSheetViews", "mergeCells", "phoneticPr", "conditionalFormatting", "dataValidations",
        "hyperlinks", "printOptions", "pageMargins", "pageSetup", "headerFooter", "rowBreaks", "colBreaks", "customProperties", "cellWatches",
        "ignoredErrors", "smartTags", "drawing", "legacyDrawing", "legacyDrawingHF", "drawingHF", "picture", "oleObjects", "controls",
        "webPublishItems", "tableParts", "extLst",
    ];

    /// <summary><paramref name="element"/> put into a worksheet where its kind goes: after the last of its own kind or of those before it.</summary>
    private static void Place(XElement root, XElement element)
    {
        var at = Array.IndexOf(SheetOrder, element.Name.LocalName);
        if (root.Elements().LastOrDefault(e => Array.IndexOf(SheetOrder, e.Name.LocalName) is var i && i >= 0 && i <= at) is { } before)
            before.AddAfterSelf(element);
        else
            root.AddFirst(element);
    }

    /// <summary>A layout line, or null when the line is none: the sheet is named as a format line names it.</summary>
    private static LayoutLine? LayoutOf(string line, string? sheet, IReadOnlyList<string> names)
    {
        var (said, text) = SheetSaid(line.Trim().TrimEnd('.'), names);
        // The sheet before the line ("Orders!freeze row 1"), as before a width line.
        if (LeadingSheet().Match(text) is { Success: true } lead && names.Contains(Unquoted(lead.Groups["sheet"].Value), StringComparer.OrdinalIgnoreCase))
        {
            said = Unquoted(lead.Groups["sheet"].Value);
            text = text[lead.Length..].TrimStart();
        }
        var on = said ?? sheet;
        if (FreezeLine().Match(text) is { Success: true } freeze)
            return new LayoutLine(on, false, (parts, _, target, _) => Freeze(parts, target, freeze));
        if (UnfreezeLine().IsMatch(text))
            return new LayoutLine(on, false, (parts, _, target, _) => Freeze(parts, target, null));
        if (FilterLine().Match(text) is { Success: true } filter)
            return new LayoutLine(on, false, (parts, sheets, target, shared) => Filter(parts, sheets, target, shared, filter.Groups["a"].Success ? (filter.Groups["a"].Value, filter.Groups["b"].Value) : null, on: true));
        if (NoFilterLine().IsMatch(text))
            return new LayoutLine(on, false, (parts, sheets, target, shared) => Filter(parts, sheets, target, shared, null, on: false));
        if (MergeLine().Match(text) is { Success: true } merge)
            return new LayoutLine(on, false, (parts, _, target, shared) => Merge(parts, target, shared, merge.Groups["a"].Value, merge.Groups["b"].Value,
                unmerge: merge.Groups["un"].Success, center: merge.Groups["center"].Success || merge.Groups["center2"].Success));
        if (SortLine().Match(text) is { Success: true } sort)
            return new LayoutLine(on, true, (parts, sheets, target, shared) => Sort(parts, sheets, target, shared, sort));
        if (HighlightOf(text) is { } highlight)
            return new LayoutLine(on, false, (parts, _, target, _) => Highlight(parts, target, highlight));
        return null;
    }

    // ---- freeze --------------------------------------------------------------------------------------------------

    /// <summary>Rows above and columns left of a place kept in view, or (null) none.</summary>
    private static string Freeze(PartSet parts, (string Name, string Part) sheet, Match? line)
    {
        var xml = parts.Get(sheet.Part);
        var root = xml.Root!;
        var views = root.Element(S + "sheetViews");
        if (views is null)
            Place(root, views = new XElement(S + "sheetViews"));
        var view = views.Element(S + "sheetView");
        if (view is null)
            views.Add(view = new XElement(S + "sheetView", new XAttribute("workbookViewId", 0)));
        view.Elements(S + "pane").Remove();
        view.Elements(S + "selection").Remove();
        parts.Changed(sheet.Part);
        if (line is null)
            return $"unfroze the panes of sheet \"{sheet.Name}\"";
        int rows = 0, columns = 0;
        if (line.Groups["at"].Success)
        {
            var (row, column) = Parse(line.Groups["at"].Value.ToUpperInvariant());
            (rows, columns) = (row - 1, column - 1);
        }
        else
        {
            var count = line.Groups["n"].Success ? int.Parse(line.Groups["n"].Value, CultureInfo.InvariantCulture) : 1;
            if (line.Groups["r2"].Success)
                count = int.Parse(line.Groups["r2"].Value, CultureInfo.InvariantCulture);
            else if (line.Groups["r1"].Success)
                count = int.Parse(line.Groups["r1"].Value, CultureInfo.InvariantCulture);
            if (line.Groups["col"].Success)
                columns = line.Groups["letter"].Success ? Parse(line.Groups["letter"].Value.ToUpperInvariant() + "1").Column : count;
            else
                rows = count;
        }
        if (rows is < 0 or > 1000 || columns is < 0 or > 100 || rows + columns == 0)
            throw new InvalidOperationException("Say which rows or columns stay in view: \"freeze row 1\", \"freeze the first column\", \"freeze panes at B2\".");
        var pane = new XElement(S + "pane");
        if (columns > 0) pane.SetAttributeValue("xSplit", columns);
        if (rows > 0) pane.SetAttributeValue("ySplit", rows);
        var active = rows > 0 && columns > 0 ? "bottomRight" : rows > 0 ? "bottomLeft" : "topRight";
        pane.SetAttributeValue("topLeftCell", XlsxWriter.Reference(rows + 1, columns + 1));
        pane.SetAttributeValue("activePane", active);
        pane.SetAttributeValue("state", "frozen");
        view.AddFirst(pane);
        pane.AddAfterSelf(new XElement(S + "selection", new XAttribute("pane", active)));
        var what = string.Join(" and ", new[] { rows > 0 ? (rows == 1 ? "row 1" : $"rows 1-{rows}") : null, columns > 0 ? (columns == 1 ? "column A" : $"columns A-{Letters(columns)}") : null }.OfType<string>());
        return $"froze {what} of sheet \"{sheet.Name}\" (they stay in view as the rest scrolls)";
    }

    // ---- filter --------------------------------------------------------------------------------------------------

    /// <summary>The filter buttons on a header row, over the cells under it; or none.</summary>
    private static string Filter(PartSet parts, List<(string Name, string Part)> sheets, (string Name, string Part) sheet, List<string> shared, (string A, string B)? range, bool on)
    {
        var xml = parts.Get(sheet.Part);
        var root = xml.Root!;
        root.Elements(S + "autoFilter").Remove();
        var workbook = parts.Get("xl/workbook.xml");
        var index = sheets.FindIndex(s => s.Part == sheet.Part);
        var names = workbook.Root!.Element(S + "definedNames");
        names?.Elements(S + "definedName").Where(n => (string?)n.Attribute("name") == "_xlnm._FilterDatabase" && (int?)n.Attribute("localSheetId") == index).Remove();
        parts.Changed(sheet.Part);
        parts.Changed("xl/workbook.xml");
        if (!on)
        {
            if (names is { HasElements: false })
                names.Remove();
            return $"took the filter off sheet \"{sheet.Name}\"";
        }
        var (r1, c1, r2, c2) = range is { } given ? Box(given.A, given.B) : TableBox(xml, shared);
        if (r2 <= r1)
            throw new InvalidOperationException($"A filter needs a header row and rows under it: sheet \"{sheet.Name}\" has {(r2 < r1 ? "nothing" : "one row")} there. Give its cells: \"filter A1:F73\".");
        var reference = $"{XlsxWriter.Reference(r1, c1)}:{XlsxWriter.Reference(r2, c2)}";
        Place(root, new XElement(S + "autoFilter", new XAttribute("ref", reference)));
        // Excel keeps the filtered cells as a hidden name of the sheet; it writes one when it saves, and reads one.
        if (names is null)
        {
            names = new XElement(S + "definedNames");
            var after = workbook.Root.Elements().LastOrDefault(e => e.Name.LocalName is "sheets" or "functionGroups" or "externalReferences");
            if (after is null) workbook.Root.Add(names);
            else after.AddAfterSelf(names);
        }
        names.Add(new XElement(S + "definedName", new XAttribute("name", "_xlnm._FilterDatabase"), new XAttribute("localSheetId", index), new XAttribute("hidden", 1),
            SheetChart.Absolute(sheet.Name, r1, c1, r2, c2)));
        return $"put filters on row {r1} of sheet \"{sheet.Name}\", over {reference}";
    }

    /// <summary>The rows and columns of two corners.</summary>
    private static (int Row1, int Column1, int Row2, int Column2) Box(string a, string b)
    {
        var (r1, c1) = Parse(a.Replace("$", "").ToUpperInvariant());
        var (r2, c2) = Parse(b.Replace("$", "").ToUpperInvariant());
        return (Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2));
    }

    /// <summary>
    /// The table of a sheet: its cells that hold something, from its header row (the first with more than one cell
    /// filled, below a title across the top) to its last row.
    /// </summary>
    private static (int Row1, int Column1, int Row2, int Column2) TableBox(XDocument xml, List<string> shared)
    {
        var filled = xml.Root!.Element(S + "sheetData")?.Elements(S + "row").Elements(S + "c")
            .Where(c => (string?)c.Attribute("r") is not null && Shown(c, shared).Length > 0)
            .Select(c => Parse((string)c.Attribute("r")!)).ToList() ?? [];
        if (filled.Count == 0)
            return (1, 1, 0, 1);
        var header = filled.GroupBy(f => f.Row).OrderBy(g => g.Key).FirstOrDefault(g => g.Count() > 1)?.Key ?? filled.Min(f => f.Row);
        var below = filled.Where(f => f.Row >= header).ToList();
        return (header, below.Min(f => f.Column), below.Max(f => f.Row), below.Max(f => f.Column));
    }

    // ---- merge ---------------------------------------------------------------------------------------------------

    private static string Merge(PartSet parts, (string Name, string Part) sheet, List<string> shared, string a, string b, bool unmerge, bool center)
    {
        var xml = parts.Get(sheet.Part);
        var root = xml.Root!;
        var (r1, c1, r2, c2) = Box(a, b);
        var reference = $"{XlsxWriter.Reference(r1, c1)}:{XlsxWriter.Reference(r2, c2)}";
        var list = root.Element(S + "mergeCells");
        parts.Changed(sheet.Part);
        if (unmerge)
        {
            var gone = list?.Elements(S + "mergeCell").Where(m => string.Equals((string?)m.Attribute("ref"), reference, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
            if (gone.Count == 0)
                throw new InvalidOperationException($"{reference} isn't merged on sheet \"{sheet.Name}\"{(list?.HasElements == true ? "; these are: " + string.Join(", ", list.Elements(S + "mergeCell").Select(m => (string?)m.Attribute("ref"))) : "")}.");
            gone.ForEach(m => m.Remove());
            if (list!.HasElements) list.SetAttributeValue("count", list.Elements().Count());
            else list.Remove();
            return $"unmerged {reference} on sheet \"{sheet.Name}\"";
        }
        if (r1 == r2 && c1 == c2)
            throw new InvalidOperationException($"merge {reference}: give two cells or more, such as A1:F1.");
        foreach (var m in list?.Elements(S + "mergeCell") ?? [])
            if ((string?)m.Attribute("ref") is { } other && other.Split(':') is [var o1, var o2] && Box(o1, o2) is var (or1, oc1, or2, oc2)
                && or1 <= r2 && r1 <= or2 && oc1 <= c2 && c1 <= oc2)
                throw new InvalidOperationException($"{reference} overlaps {other}, which is merged already: unmerge it first (\"unmerge {other}\").");
        // As Excel does: what the other cells held goes, and the merged cell shows the first one's.
        var cleared = new List<string>();
        foreach (var c in root.Element(S + "sheetData")?.Elements(S + "row").Elements(S + "c").ToList() ?? [])
            if ((string?)c.Attribute("r") is { } at && Parse(at) is var (row, column) && row >= r1 && row <= r2 && column >= c1 && column <= c2 && (row, column) != (r1, c1)
                && Shown(c, shared).Length > 0)
            {
                cleared.Add(at);
                c.Elements().Where(e => e.Name == S + "f" || e.Name == S + "v" || e.Name == S + "is").Remove();
                c.Attribute("t")?.Remove();
            }
        if (list is null)
            Place(root, list = new XElement(S + "mergeCells"));
        list.Add(new XElement(S + "mergeCell", new XAttribute("ref", reference)));
        list.SetAttributeValue("count", list.Elements().Count());
        if (center)
            ApplyFormat(parts, sheet, new FormatLine(sheet.Name, r1, c1, r1, c1, new DocumentLook(null, null, null, null, null, null, "center", null, false), XlsxWriter.Reference(r1, c1)));
        return $"merged {reference} on sheet \"{sheet.Name}\"{(center ? ", its text centred" : "")}" +
               (cleared.Count > 0 ? $" ({string.Join(", ", cleared)} held something, which a merged cell doesn't show: emptied, as Excel does)" : "");
    }

    // ---- sort ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Rows put in the order of one column's values, as Excel's Sort does: each row's cells in the range move
    /// together, their formats with them, and a formula's references to its own row move with it (=D2*E2 in row 2,
    /// moved to row 5, is =D5*E5). Numbers before text, empty cells last either way; rows of equal value keep their order.
    /// </summary>
    private static string Sort(PartSet parts, List<(string Name, string Part)> sheets, (string Name, string Part) sheet, List<string> shared, Match line)
    {
        var xml = parts.Get(sheet.Part);
        var data = xml.Root!.Element(S + "sheetData") ?? throw new InvalidOperationException($"Sheet \"{sheet.Name}\" has no cells to sort.");
        // Formulas get their values first, to sort by them.
        SheetFormula.WriteValues([.. sheets.Select(s => (s.Name, parts.Get(s.Part).Descendants(S + "c"), s.Part == sheet.Part))]);
        var table = TableBox(xml, shared);
        int r1, c1, r2, c2;
        if (line.Groups["a"].Success)
            (r1, c1, r2, c2) = Box(line.Groups["a"].Value, line.Groups["b"].Value);
        else if (line.Groups["r1"].Success)
            (r1, c1, r2, c2) = (int.Parse(line.Groups["r1"].Value, CultureInfo.InvariantCulture), table.Column1, int.Parse(line.Groups["r2"].Value, CultureInfo.InvariantCulture), table.Column2);
        else
            (r1, c1, r2, c2) = (table.Row1 + 1, table.Column1, table.Row2, table.Column2);
        if (r2 > MaxRow || r2 - r1 > MaxInsert)
            throw new InvalidOperationException($"One line sorts at most {MaxInsert:N0} rows.");
        // The column to sort by: its letter, or the text of its heading.
        var key = line.Groups["key"].Value.Trim().Trim('"', '\'', '“', '”');
        int column;
        if (Regex.IsMatch(key, "^[A-Za-z]{1,3}$") && (key.All(char.IsUpper) || key.Length == 1))
            column = Parse(key.ToUpperInvariant() + "1").Column;
        else
        {
            var heading = Math.Max(1, r1 - 1);
            var match = data.Elements(S + "row").Where(r => (int?)r.Attribute("r") <= heading).Reverse()
                .SelectMany(r => r.Elements(S + "c")).FirstOrDefault(c => Shown(c, shared).Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
            column = match is null ? 0 : Parse((string)match.Attribute("r")!).Column;
            if (column == 0)
                throw new InvalidOperationException($"No column of sheet \"{sheet.Name}\" is headed \"{key}\": sort by a column's letter (\"sort A2:F73 by F descending\") or its heading as it is written.");
        }
        if (column < c1 || column > c2)
            throw new InvalidOperationException($"Column {Letters(column)} is outside the cells sorted ({XlsxWriter.Reference(r1, c1)}:{XlsxWriter.Reference(r2, c2)}).");
        if (xml.Root.Element(S + "mergeCells")?.Elements(S + "mergeCell").Select(m => ((string?)m.Attribute("ref"))?.Split(':')).FirstOrDefault(p => p is [var a, var b] && Box(a, b) is var (mr1, mc1, mr2, mc2)
                && mr1 <= r2 && r1 <= mr2 && mc1 <= c2 && c1 <= mc2) is { } merged)
            throw new InvalidOperationException($"{string.Join(':', merged)} is merged and among the rows sorted: as in Excel, unmerge it or sort the rows under it.");
        var descending = line.Groups["dir"].Success && Regex.IsMatch(line.Groups["dir"].Value, "desc|decreas|largest|biggest|highest|most|newest|latest|z", RegexOptions.IgnoreCase);

        // Each row's cells in the range, its formulas written out where they were shared.
        var rows = data.Elements(S + "row").Where(r => (int?)r.Attribute("r") is { } n && n >= r1 && n <= r2).ToList();
        foreach (var c in rows.SelectMany(r => r.Elements(S + "c")).ToList())
            if ((string?)c.Attribute("r") is { } at)
                Unshare(data, at);
        var taken = new Dictionary<int, List<XElement>>();
        foreach (var row in rows)
        {
            var number = (int)row.Attribute("r")!;
            var cells = row.Elements(S + "c").Where(c => (string?)c.Attribute("r") is { } at && Parse(at).Column is var col && col >= c1 && col <= c2).ToList();
            cells.ForEach(c => c.Remove());
            taken[number] = cells;
        }
        // A heading row given with the rest stays on top, as Excel's "My data has headers": a row frozen above the
        // rest, or bold over rows that aren't, or text over a column of numbers.
        var first = r1;
        SheetChart.Value KeyOf(int row) => SheetChart.ValueOf(taken.GetValueOrDefault(row)?.FirstOrDefault(c => Parse((string)c.Attribute("r")!).Column == column), shared);
        // Every cell of the row that holds something is bold.
        bool Bold(int row) => taken.GetValueOrDefault(row) is { Count: > 0 } cells
            && cells.All(c => SheetChart.ValueOf(c, shared) is { Number: null, Text: null } || (int?)c.Attribute("s") is { } style && BoldStyle(parts, style));
        var frozen = (int?)xml.Root.Element(S + "sheetViews")?.Element(S + "sheetView")?.Element(S + "pane")?.Attribute("ySplit") ?? 0;
        var headed = r2 > r1 && (frozen == r1
            || KeyOf(r1).IsText && Bold(r1) && !Bold(r1 + 1)
            || KeyOf(r1).IsText && Enumerable.Range(r1 + 1, r2 - r1).Count(r => KeyOf(r).Number is not null) * 2 > r2 - r1);
        if (headed)
            first = r1 + 1;
        var order = Enumerable.Range(first, r2 - first + 1).Select(r => (Row: r, Key: KeyOf(r))).ToList();
        static int Rank(SheetChart.Value v) => v.Number is not null ? 0 : v.IsText ? 1 : 2;
        var sorted = order.OrderBy(o => Rank(o.Key) == 2 ? 1 : 0)
            .ThenBy(o => descending ? -Rank(o.Key) : Rank(o.Key))
            .ThenBy(o => o.Key.Number is { } n ? (descending ? -n : n) : 0)
            .ThenBy(o => o.Key.IsText ? o.Key.Text!.Trim() : "", descending ? Comparer<string>.Create((x, y) => string.Compare(y, x, StringComparison.OrdinalIgnoreCase)) : StringComparer.OrdinalIgnoreCase)
            .Select(o => o.Row).ToList();
        if (headed)
            sorted.Insert(0, r1);
        for (var i = 0; i < sorted.Count; i++)
        {
            var from = sorted[i];
            var to = (headed ? r1 : first) + i;
            if (!taken.TryGetValue(from, out var cells) || cells.Count == 0)
                continue;
            var row = data.Elements(S + "row").FirstOrDefault(r => (int?)r.Attribute("r") == to);
            if (row is null)
            {
                row = new XElement(S + "row", new XAttribute("r", to));
                if (data.Elements(S + "row").LastOrDefault(r => (int?)r.Attribute("r") < to) is { } above) above.AddAfterSelf(row);
                else data.AddFirst(row);
            }
            row.Attribute("spans")?.Remove();
            foreach (var cell in cells)
            {
                var col = Parse((string)cell.Attribute("r")!).Column;
                cell.SetAttributeValue("r", XlsxWriter.Reference(to, col));
                if (cell.Element(S + "f") is { } f && f.Value.Length > 0)
                    f.Value = Shift(f.Value, to - from, 0);
                if (row.Elements(S + "c").LastOrDefault(c => Parse((string)c.Attribute("r")!).Column < col) is { } left) left.AddAfterSelf(cell);
                else row.AddFirst(cell);
            }
        }
        Dimension(xml);
        parts.Changed(sheet.Part);
        Recalculate(parts);
        SheetFormula.WriteValues([.. sheets.Select(s => (s.Name, parts.Get(s.Part).Descendants(S + "c"), s.Part == sheet.Part))]);
        var heading2 = data.Elements(S + "row").Where(r => (int?)r.Attribute("r") < (headed ? r1 + 1 : r1)).Reverse().SelectMany(r => r.Elements(S + "c"))
            .FirstOrDefault(c => Parse((string)c.Attribute("r")!).Column == column) is { } h && Shown(h, shared) is { Length: > 0 } named && !named.StartsWith('=') ? $" ({named})" : "";
        return $"sorted rows {(headed ? r1 + 1 : r1)}-{r2} of sheet \"{sheet.Name}\" by column {Letters(column)}{heading2}, {(descending ? "largest first" : "smallest first")}" +
               (headed ? $", row {r1} kept on top as their heading" : "");
    }

    /// <summary>Whether cell format <paramref name="style"/> of the workbook is bold.</summary>
    private static bool BoldStyle(PartSet parts, int style)
    {
        if (!parts.Exists("xl/styles.xml"))
            return false;
        var styles = parts.Get("xl/styles.xml").Root!;
        var xf = styles.Element(S + "cellXfs")?.Elements(S + "xf").ElementAtOrDefault(style);
        var font = xf is null ? null : styles.Element(S + "fonts")?.Elements(S + "font").ElementAtOrDefault((int?)xf.Attribute("fontId") ?? 0);
        return font?.Element(S + "b") is { } b && (string?)b.Attribute("val") is not ("0" or "false");
    }

    // ---- highlight -----------------------------------------------------------------------------------------------

    private sealed record HighlightLine(int Row1, int Column1, int Row2, int Column2, int? By, string Operator, string[] Values, DocumentLook Look);

    /// <summary>
    /// A rule that colours cells while what they hold meets it, as Excel's conditional formatting does: "highlight
    /// F2:F73 over 3000 in red", or whole rows by one column, "highlight A2:F73 red where F > 3000".
    /// </summary>
    private static HighlightLine? HighlightOf(string text)
    {
        if (HighlightStart().Match(text) is not { Success: true } m)
            return null;
        var rest = text[m.Length..];
        // "format F2:F73 red where F > 3000" is a rule only with its where: "format B2:B9 not bold" is a look.
        if (Condition().Match(rest) is not { Success: true } condition || m.Groups["verb"].Value.Equals("format", StringComparison.OrdinalIgnoreCase) && !condition.Groups["w"].Success)
            return null;
        var (r1, c1, r2, c2) = m.Groups["b"].Success ? Box(m.Groups["a"].Value, m.Groups["b"].Value) : Box(m.Groups["a"].Value, m.Groups["a"].Value);
        int? by = condition.Groups["col"].Success ? Parse(condition.Groups["col"].Value.Replace("$", "").ToUpperInvariant() + "1").Column : null;
        var said = condition.Groups["op"].Value.Trim().ToLowerInvariant();
        var op = said switch
        {
            ">" or "over" or "above" or "more than" or "greater than" or "bigger than" or "higher than" or "larger than" => "greaterThan",
            ">=" or "at least" or "no less than" => "greaterThanOrEqual",
            "<" or "under" or "below" or "less than" or "lower than" or "smaller than" => "lessThan",
            "<=" or "at most" or "no more than" => "lessThanOrEqual",
            "<>" or "!=" or "not" or "not equal to" or "is not" or "isn't" => "notEqual",
            "between" => "between",
            _ => "equal",
        };
        static string Number(string n) => n.Replace("$", "").Replace("€", "").Replace("£", "").Replace(",", "").Trim();
        var values = condition.Groups["n2"].Success ? new[] { Number(condition.Groups["n"].Value), Number(condition.Groups["n2"].Value) } : [Number(condition.Groups["n"].Value)];
        if (op == "between" && values.Length < 2)
            return null;
        var words = (rest[..condition.Index] + " " + rest[(condition.Index + condition.Length)..]).Trim();
        var look = DocumentLook.Parse(words);
        // A colour said alone is Excel's own pair for it: a light fill with dark text of the same colour.
        if (look is { Fill: { } fill, Colour: null } && !Regex.IsMatch(words, @"\b(?:fill|background|shad(?:e|ed|ing)|bg)\b", RegexOptions.IgnoreCase) && Pairs.TryGetValue(fill, out var pair))
            look = look with { Fill = pair.Fill, Colour = pair.Text };
        if (!look.Any)
            look = look with { Fill = "FFC7CE", Colour = "9C0006" };
        return new HighlightLine(r1, c1, r2, c2, by, op, values, look);
    }

    /// <summary>Excel's light fills with dark text: red, yellow and green as its Highlight Cells Rules have them.</summary>
    private static readonly Dictionary<string, (string Fill, string Text)> Pairs = new()
    {
        ["FF0000"] = ("FFC7CE", "9C0006"), ["FFC7CE"] = ("FFC7CE", "9C0006"), ["C00000"] = ("FFC7CE", "9C0006"),
        ["FFFF00"] = ("FFEB9C", "9C5700"), ["FFF2CC"] = ("FFEB9C", "9C5700"), ["FFC000"] = ("FFEB9C", "9C5700"),
        ["00B050"] = ("C6EFCE", "006100"), ["C6EFCE"] = ("C6EFCE", "006100"), ["006100"] = ("C6EFCE", "006100"),
    };

    private static string Highlight(PartSet parts, (string Name, string Part) sheet, HighlightLine line)
    {
        if (!parts.Exists("xl/styles.xml"))
            throw new InvalidOperationException("This workbook has no styles to add a highlight to.");
        var styles = parts.Get("xl/styles.xml").Root!;
        // The look as a "differential format": what changes on the cells while the rule holds.
        var dxfs = styles.Element(S + "dxfs");
        if (dxfs is null)
        {
            dxfs = new XElement(S + "dxfs", new XAttribute("count", 0));
            if (styles.Elements().LastOrDefault(e => e.Name.LocalName is "numFmts" or "fonts" or "fills" or "borders" or "cellStyleXfs" or "cellXfs" or "cellStyles") is { } before) before.AddAfterSelf(dxfs);
            else styles.AddFirst(dxfs);
        }
        var look = line.Look;
        var font = new XElement(S + "font",
            look.Bold is { } b ? new XElement(S + "b", b ? null : new XAttribute("val", 0)) : null,
            look.Italic is { } i ? new XElement(S + "i", i ? null : new XAttribute("val", 0)) : null,
            look.Underline is true ? new XElement(S + "u") : null,
            look.Colour is { } colour ? new XElement(S + "color", new XAttribute("rgb", "FF" + colour)) : null);
        var dxf = new XElement(S + "dxf", font.HasElements ? font : null,
            look.Fill is { Length: > 0 } fill ? new XElement(S + "fill", new XElement(S + "patternFill", new XElement(S + "bgColor", new XAttribute("rgb", "FF" + fill)))) : null,
            look.Border ? new XElement(S + "border", new XElement(S + "left", new XAttribute("style", "thin")), new XElement(S + "right", new XAttribute("style", "thin")),
                new XElement(S + "top", new XAttribute("style", "thin")), new XElement(S + "bottom", new XAttribute("style", "thin"))) : null);
        var id = Same(dxfs, "dxf", dxf);
        parts.Changed("xl/styles.xml");

        var xml = parts.Get(sheet.Part);
        var root = xml.Root!;
        var range = $"{XlsxWriter.Reference(line.Row1, line.Column1)}:{XlsxWriter.Reference(line.Row2, line.Column2)}";
        var priority = root.Descendants(S + "cfRule").Select(r => (int?)r.Attribute("priority") ?? 0).DefaultIfEmpty(0).Max() + 1;
        XElement rule;
        string said;
        if (line.By is { } by)
        {
            // Whole rows by one column's value: a formula on the range's first row, which Excel moves down each row.
            var cell = $"${Letters(by)}{line.Row1}";
            var formula = line.Operator switch
            {
                "greaterThan" => $"{cell}>{line.Values[0]}",
                "greaterThanOrEqual" => $"{cell}>={line.Values[0]}",
                "lessThan" => $"{cell}<{line.Values[0]}",
                "lessThanOrEqual" => $"{cell}<={line.Values[0]}",
                "notEqual" => $"{cell}<>{Quoted(line.Values[0])}",
                "between" => $"AND({cell}>={line.Values[0]},{cell}<={line.Values[1]})",
                _ => $"{cell}={Quoted(line.Values[0])}",
            };
            rule = new XElement(S + "cfRule", new XAttribute("type", "expression"), new XAttribute("dxfId", id), new XAttribute("priority", priority), new XElement(S + "formula", formula));
            said = $"where {formula}";
        }
        else
        {
            rule = new XElement(S + "cfRule", new XAttribute("type", "cellIs"), new XAttribute("dxfId", id), new XAttribute("priority", priority), new XAttribute("operator", line.Operator),
                line.Values.Select(v => new XElement(S + "formula", Quoted(v))));
            said = $"where the cell is {Said(line.Operator)} {string.Join(" and ", line.Values)}";
        }
        Place(root, new XElement(S + "conditionalFormatting", new XAttribute("sqref", range), rule));
        parts.Changed(sheet.Part);
        return $"highlighted {range} on sheet \"{sheet.Name}\" ({look.Said}) {said}, for as long as that holds";
    }

    private static string Quoted(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? value : "\"" + value.Trim('"').Replace("\"", "\"\"") + "\"";

    private static string Said(string op) => op switch
    {
        "greaterThan" => "over", "greaterThanOrEqual" => "at least", "lessThan" => "under", "lessThanOrEqual" => "at most",
        "notEqual" => "not", "between" => "between", _ => "",
    };

    // "Orders!freeze row 1", "'My sheet'!sort by F"
    [GeneratedRegex(@"^(?<sheet>'(?:[^']|'')+'|""[^""]+""|“[^”]+”|[^!|'""“”]+?)!\s*(?=[a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingSheet();

    // "freeze row 1", "freeze the header row", "freeze the top 2 rows", "freeze rows 1-2", "freeze the first column",
    // "freeze column B", "freeze panes at B2", "freeze B2"
    [GeneratedRegex(@"^(?:freeze|lock)\s+(?:the\s+)?(?:panes?\s+(?:at\s+|below\s+and\s+right\s+of\s+)?(?<at>[A-Za-z]{1,3}[1-9][0-9]{0,5})|(?<at>[A-Za-z]{1,3}[1-9][0-9]{0,5})|(?:(?:top|first|header|heading|title)\s+)?(?:(?<n>[0-9]{1,3})\s+)?(?:header\s+|heading\s+)?rows?(?:\s+(?<r1>[0-9]{1,3})(?:\s*(?:-|to|:)\s*(?<r2>[0-9]{1,3}))?)?|(?<col>(?:(?:first|left|leftmost)\s+)?(?:(?<n>[0-9]{1,2})\s+)?columns?(?:\s+(?<letter>[A-Za-z]{1,2}))?))(?:\s+(?:of\s+the\s+sheet|in\s+place))?$", RegexOptions.IgnoreCase)]
    private static partial Regex FreezeLine();

    [GeneratedRegex(@"^(?:unfreeze|unlock)(?:\s+(?:the\s+)?(?:panes?|rows?|columns?|all))?$", RegexOptions.IgnoreCase)]
    private static partial Regex UnfreezeLine();

    // "filter A1:F73", "turn on filters", "add a filter to the header row", "autofilter", "filters on row 1"
    [GeneratedRegex(@"^(?:(?:turn\s+on|add|apply|put|set(?:\s+up)?)\s+(?:an?\s+|the\s+)?)?(?:auto-?)?filters?(?:\s+(?:buttons?\s+)?(?:on|to|for|over))?(?:\s+(?:the\s+)?(?:header|heading|top|first)\s+row)?(?:\s+(?:row\s+[0-9]+))?(?:\s+(?<a>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6}):(?<b>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6}))?(?:\s+on)?$", RegexOptions.IgnoreCase)]
    private static partial Regex FilterLine();

    [GeneratedRegex(@"^(?:(?:turn\s+off|remove|clear|take\s+off|delete)\s+(?:the\s+)?(?:auto-?)?filters?|(?:auto-?)?filters?\s+off|no\s+filters?)$", RegexOptions.IgnoreCase)]
    private static partial Regex NoFilterLine();

    // "merge A1:F1", "merge and center A1:F1", "merge cells A1:F1, centered", "unmerge A1:F1"
    [GeneratedRegex(@"^(?:(?<un>un)merge|merge(?<center>\s+(?:and|&)\s+cent(?:er|re))?)(?:\s+(?:the\s+)?cells?)?\s+(?<a>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6}):(?<b>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6})(?:\s*,?\s*(?:and\s+)?(?<center2>cent(?:er|re)(?:d|ed)?(?:\s+(?:it|the\s+text))?))?$", RegexOptions.IgnoreCase)]
    private static partial Regex MergeLine();

    // "sort A2:F73 by F descending", "sort rows 2-73 by column F, largest first", "sort by revenue, largest first", "sort the table by Date"
    [GeneratedRegex(@"^sort\s+(?:the\s+)?(?:(?:rows|table|data|list|cells|sheet|orders|records|entries)\s+)?(?:(?<a>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6}):(?<b>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6})\s+|(?<r1>[1-9][0-9]{0,6})\s*(?:-|to|through|:)\s*(?<r2>[1-9][0-9]{0,6})\s+)?(?:(?:by|on)\s+)(?:the\s+)?(?:column\s+)?(?<key>.+?)(?:\s+column)?(?:\s*,?\s*(?:in\s+)?(?<dir>descending|desc|decreasing|largest\s+(?:to\s+smallest|first)|biggest\s+first|highest\s+(?:to\s+lowest|first)|most\s+first|newest\s+first|latest\s+first|z\s*(?:-|to)\s*a|ascending|asc|increasing|smallest\s+(?:to\s+largest|first)|lowest\s+(?:to\s+highest|first)|least\s+first|oldest\s+first|earliest\s+first|a\s*(?:-|to)\s*z)(?:\s+order)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SortLine();

    // "highlight A2:F73", "colour F2:F73", "format F2:F73" (a condition follows, or it is no highlight)
    [GeneratedRegex(@"^(?<verb>highlight|colou?r|shade|mark|format)\s+(?:the\s+)?(?:rows?\s+|cells?\s+)?(?<a>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6})(?::(?<b>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6}))?(?=\s|,|$)", RegexOptions.IgnoreCase)]
    private static partial Regex HighlightStart();

    // "where F > 3000", "when column F is over $3,000", "over 3000", "between 10 and 20", "if F = Late"
    [GeneratedRegex(@"(?:\b(?<w>where|when|if|whose)\s+(?:the\s+)?(?:value\s+(?:in|of)\s+)?(?:column\s+)?(?:(?<col>\$?[A-Z]{1,3})\b\s*)?(?:is\s+|are\s+)?)?(?<op>>=|<=|<>|!=|==|=|>|<|(?<!\w)(?:over|above|more\s+than|greater\s+than|bigger\s+than|higher\s+than|larger\s+than|at\s+least|no\s+less\s+than|under|below|less\s+than|lower\s+than|smaller\s+than|at\s+most|no\s+more\s+than|not\s+equal\s+to|is\s+not|isn't|not|equal\s+to|equals|between|is|are|says|reads)(?!\w))\s*(?<n>-?[$€£]?\s?[0-9][0-9,]*(?:\.[0-9]+)?|""[^""]+""|'[^']+'|[A-Za-z][\w ]*?)(?:\s+and\s+(?<n2>-?[$€£]?\s?[0-9][0-9,]*(?:\.[0-9]+)?))?(?=\s*(?:,|$|\s+(?:in|with|as|to|red|yellow|green|blue|orange|bold)\b))", RegexOptions.IgnoreCase)]
    private static partial Regex Condition();
}
