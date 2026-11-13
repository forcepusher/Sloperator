using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sloperator.Core.Documents;

internal static partial class SheetEdit
{
    // ---- inserting and deleting columns: "insert column C", "delete columns B-C" ------------------------------------
    // Rows could be inserted and deleted, columns not: a column added in the middle of a table meant writing every
    // cell to its right one place over by hand, formulas and all, as models did by rewriting the sheet's XML.

    /// <summary>
    /// Inserts <paramref name="count"/> columns before column <paramref name="from"/>, or deletes that many from it, as
    /// Excel does: the cells to the right move, every reference to them moves along (formulas on any sheet, defined
    /// names, charts, merged cells, highlights, the filter, column widths, the panes kept in view); a range that spans
    /// the place grows or shrinks, a reference to a deleted cell becomes #REF!. New columns take the look of the
    /// column to their left. A sheet whose table spans the place is refused: a table lists its columns by name.
    /// </summary>
    private static void ChangeColumns(PartSet parts, List<(string Name, string Part)> sheets, (string Name, string Part) target, bool insert, int from, int count)
    {
        var last = from + count - 1;
        foreach (var table in Related(parts, target.Part, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/table"))
            if ((string?)parts.Get(table).Root!.Attribute("ref") is { } range && range.Split(':') is [var a, var b] && Box(a, b) is var (_, c1, _, c2)
                && (insert ? from > c1 && from <= c2 : from <= c2 && last >= c1))
                throw new InvalidOperationException($"Sheet \"{target.Name}\" has a table over {range}, which {(insert ? "the new column would split" : "has the columns to delete")}: " +
                    "a table names its columns, so this is done in Excel (right-click a cell of the table, Insert or Delete Table Columns). Nothing was changed.");

        foreach (var (_, part) in sheets)
        {
            var sheetData = parts.Get(part).Root!.Element(S + "sheetData");
            if (sheetData is null) continue;
            var groups = sheetData.Descendants(S + "f").Where(f => (string?)f.Attribute("t") == "shared" && f.Value.Length > 0)
                .Select(f => (string?)f.Parent!.Attribute("r")).OfType<string>().ToList();
            foreach (var reference in groups)
                Unshare(sheetData, reference);
            if (groups.Count > 0) parts.Changed(part);
        }

        var xml = parts.Get(target.Part);
        var root = xml.Root!;
        var data = root.Element(S + "sheetData") ?? throw new InvalidOperationException($"Sheet \"{target.Name}\" has no cells part.");
        foreach (var row in data.Elements(S + "row"))
        {
            row.Attribute("spans")?.Remove();
            var number = (int)row.Attribute("r")!;
            foreach (var c in row.Elements(S + "c").ToList())
            {
                var (_, column) = Parse((string)c.Attribute("r")!);
                if (column < from)
                    continue;
                if (!insert && column <= last)
                    c.Remove();
                else
                    c.SetAttributeValue("r", XlsxWriter.Reference(number, column + (insert ? count : -count)));
            }
            // New columns look like the one to their left, as Excel's Insert does.
            if (insert && from > 1 && row.Elements(S + "c").FirstOrDefault(c => Parse((string)c.Attribute("r")!).Column == from - 1) is { } left && left.Attribute("s") is { } style)
                for (var i = count - 1; i >= 0; i--)
                    left.AddAfterSelf(new XElement(S + "c", new XAttribute("r", XlsxWriter.Reference(number, from + i)), new XAttribute("s", style.Value)));
        }

        string Move(string formula, bool sameSheet) => MoveColumnReferences(formula, sameSheet, target.Name, insert, from, count);
        foreach (var (name, part) in sheets)
        {
            var sheet = parts.Get(part);
            var same = part == target.Part;
            var changed = false;
            foreach (var f in sheet.Descendants(S + "f").Where(f => f.Value.Length > 0))
            {
                var moved = Move(f.Value, same);
                if (moved != f.Value) { f.Value = moved; changed = true; }
                if (same && f.Attribute("ref") is { } fRef) fRef.Value = MoveRanges(fRef.Value, Move);
            }
            if (same)
            {
                foreach (var e in sheet.Root!.Descendants().Where(e => e.Name.LocalName is "mergeCell" or "hyperlink" or "autoFilter" or "conditionalFormatting" or "dataValidation" or "selection").ToList())
                {
                    var attribute = e.Attribute("ref") ?? e.Attribute("sqref");
                    if (attribute is null) continue;
                    var moved = MoveRanges(attribute.Value, Move);
                    if (moved.Length == 0 && e.Name.LocalName is not "selection") e.Remove();
                    else attribute.Value = moved.Length == 0 ? "A1" : moved;
                }
                foreach (var formula in sheet.Root.Descendants().Where(e => e.Name.LocalName is "formula" or "formula1" or "formula2"
                             && e.Ancestors().Any(a => a.Name.LocalName is "conditionalFormatting" or "dataValidation") && e.Value.Length > 0))
                    formula.Value = Move(formula.Value, true);
                foreach (var list in sheet.Root.Elements().Where(e => e.Name.LocalName == "mergeCells").ToList())
                    if (!list.HasElements) list.Remove();
                    else list.SetAttributeValue("count", list.Elements().Count());
                MoveWidths(sheet.Root, insert, from, count);
                if (sheet.Root.Element(S + "sheetViews")?.Element(S + "sheetView")?.Element(S + "pane") is { } pane
                    && ((string?)pane.Attribute("state"))?.StartsWith("frozen", StringComparison.Ordinal) == true && (int?)pane.Attribute("xSplit") is { } split && from <= split)
                {
                    var kept = insert ? split + count : split - Math.Min(count, split - from + 1);
                    var rows = (int?)pane.Attribute("ySplit") ?? 0;
                    if (kept <= 0 && rows == 0)
                        pane.Parent!.Elements(S + "pane").Concat(pane.Parent.Elements(S + "selection")).Remove();
                    else
                    {
                        pane.SetAttributeValue("xSplit", kept > 0 ? kept : null);
                        pane.SetAttributeValue("topLeftCell", XlsxWriter.Reference(rows + 1, Math.Max(kept, 0) + 1));
                    }
                }
                changed = true;
                Dimension(sheet);
            }
            if (changed) parts.Changed(part);
        }

        var workbook = parts.Get("xl/workbook.xml");
        foreach (var definedName in workbook.Root!.Descendants(S + "definedName"))
            definedName.Value = Move(definedName.Value, false);
        parts.Changed("xl/workbook.xml");
        foreach (var chart in parts.Names.Where(n => n.StartsWith("xl/charts/chart", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal)).ToList())
        {
            var changed = false;
            foreach (var f in parts.Get(chart).Descendants().Where(e => e.Name.LocalName == "f"))
            {
                var moved = Move(f.Value, false);
                if (moved != f.Value) { f.Value = moved; changed = true; }
            }
            if (changed) parts.Changed(chart);
        }
        foreach (var table in Related(parts, target.Part, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/table").ToList())
        {
            var tableRoot = parts.Get(table).Root!;
            foreach (var e in tableRoot.DescendantsAndSelf().Where(e => e.Attribute("ref") is not null))
                e.SetAttributeValue("ref", MoveRanges((string)e.Attribute("ref")!, Move));
            parts.Changed(table);
        }
    }

    /// <summary>The sheet's column widths moved with their columns: those of deleted columns go, a new one is as wide as the one to its left.</summary>
    private static void MoveWidths(XElement root, bool insert, int from, int count)
    {
        if (root.Element(S + "cols") is not { } cols)
            return;
        var last = from + count - 1;
        var entries = new List<(int Min, int Max, XElement Col)>();
        foreach (var col in cols.Elements(S + "col").ToList())
        {
            int min = (int?)col.Attribute("min") ?? 0, max = (int?)col.Attribute("max") ?? 0;
            col.Remove();
            // Each width by its own columns, so a run of columns is split around the place.
            for (var c = min; c <= max; c++)
            {
                int? to = insert ? (c >= from ? c + count : c) : c < from ? c : c > last ? c - count : null;
                if (to is { } t)
                    entries.Add((t, t, col));
                if (insert && c == from - 1)
                    for (var i = 0; i < count; i++)
                        entries.Add((from + i, from + i, col));
            }
        }
        foreach (var (column, _, col) in entries.OrderBy(e => e.Min))
        {
            var copy = new XElement(col);
            copy.SetAttributeValue("min", column);
            copy.SetAttributeValue("max", column);
            cols.Add(copy);
        }
        if (!cols.HasElements)
            cols.Remove();
    }

    /// <summary>
    /// A formula with its references to <paramref name="sheet"/>'s columns moved for columns inserted at
    /// <paramref name="from"/> or deleted from it; references without a sheet name are to the formula's own sheet,
    /// which <paramref name="sameSheet"/> says it is.
    /// </summary>
    internal static string MoveColumnReferences(string formula, bool sameSheet, string sheet, bool insert, int from, int count)
    {
        var last = from + count - 1;
        static string Dollar(string letters) => letters.StartsWith('$') ? "$" : "";
        var parts = formula.Split('"');
        for (var i = 0; i < parts.Length; i += 2)
            parts[i] = RangeReference().Replace(parts[i], m =>
            {
                var named = m.Groups["sheet"].Success ? m.Groups["sheet"].Value[..^1] : null;
                var on = named is null ? null : named.StartsWith('\'') ? named[1..^1].Replace("''", "'") : named;
                if (on is null ? !sameSheet : !on.Equals(sheet, StringComparison.OrdinalIgnoreCase))
                    return m.Value;
                var c1 = XlsxWriter.Column(m.Groups["c1"].Value);
                int? c2 = m.Groups["c2"].Success ? XlsxWriter.Column(m.Groups["c2"].Value) : null;
                if (insert)
                {
                    if (c1 >= from) c1 += count;
                    if (c2 >= from) c2 += count;
                }
                else if (c2 is null)
                {
                    if (c1 > last) c1 -= count;
                    else if (c1 >= from) return (named is null ? "" : named + "!") + "#REF!";
                }
                else
                {
                    int a = c1, b = c2.Value;
                    if (a > last) a -= count; else if (a >= from) a = from;
                    if (b > last) b -= count; else if (b >= from) b = from - 1;
                    if (b < a) return (named is null ? "" : named + "!") + "#REF!";
                    (c1, c2) = (a, b);
                }
                var text = m.Groups["sheet"].Value + Dollar(m.Groups["c1"].Value) + Letters(c1) + m.Groups["d1"].Value + m.Groups["r1"].Value;
                return c2 is null ? text : text + ":" + Dollar(m.Groups["c2"].Value) + Letters(c2.Value) + m.Groups["d2"].Value + m.Groups["r2"].Value;
            });
        return string.Join('"', parts);
    }

    // "insert column C", "insert columns C:D", "delete column D", "delete columns B-C on Budget"
    [GeneratedRegex(@"^(?<verb>insert|delete|add|remove)\s+(?:a\s+|an\s+|the\s+)?(?:new\s+)?columns?\s+(?:at\s+)?(?<from>[A-Za-z]{1,3})(?:\s*(?:-|–|to|through|:)\s*(?<to>[A-Za-z]{1,3}))?(?:\s+(?:on|in)\s+(?:sheet\s+)?(?<sheet>.+))?\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ColumnLine();
}
