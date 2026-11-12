using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sloperator.Core.Documents;

internal static partial class SheetEdit
{
    // ---- how cells look: "format A1:E1 bold, yellow fill" ------------------------------------------------------------
    // Asked to make a header row stand out with a yellow background (2026-11-13), Haiku looked for Python and
    // openpyxl, then wrote the fill into styles.xml by hand through PowerShell, and Sonnet drove Excel from
    // PowerShell, which a PC without Office doesn't have: edit_file set what cells hold, not how they look.

    private sealed record FormatLine(string? Sheet, int Row1, int Column1, int Row2, int Column2, DocumentLook Look, string Cells);

    /// <summary>The most cells one format line may cover: each gets a style of its own in the sheet.</summary>
    private const int MaxFormatted = 50_000;

    /// <summary>
    /// A line that formats cells, or null: "format A1:E1 bold, yellow fill", "format Budget!B2:B9 red text, centered",
    /// "format A1:D1 fill #FFF2CC, border". A colour with nothing said of it is the fill.
    /// </summary>
    private static FormatLine? FormatOf(string line, string? sheet)
    {
        if (FormatStart().Match(line.Trim()) is not { Success: true } m)
            return null;
        var (r1, c1) = Parse(m.Groups["a"].Value.Replace("$", "").ToUpperInvariant());
        var (r2, c2) = m.Groups["b"].Success ? Parse(m.Groups["b"].Value.Replace("$", "").ToUpperInvariant()) : (r1, c1);
        var on = m.Groups["sheet"].Success ? m.Groups["sheet"].Value.Trim('\'').Replace("''", "'") : sheet;
        var look = DocumentLook.Parse(m.Groups["what"].Value);
        if (!look.Any)
            throw new InvalidOperationException($"{line.Trim()}: say how the cells should look: {DocumentLook.Words}, wrap, border.");
        var cells = $"{XlsxWriter.Reference(Math.Min(r1, r2), Math.Min(c1, c2))}{(r1 == r2 && c1 == c2 ? "" : ":" + XlsxWriter.Reference(Math.Max(r1, r2), Math.Max(c1, c2)))}";
        return new FormatLine(on, Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2), look, cells);
    }

    /// <summary>The cells of a format line given their look, each keeping the rest of what it had (its number format, its font's size); what was done.</summary>
    private static string ApplyFormat(PartSet parts, (string Name, string Part) sheet, FormatLine line)
    {
        if ((long)(line.Row2 - line.Row1 + 1) * (line.Column2 - line.Column1 + 1) > MaxFormatted)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"format {line.Cells}: one line formats at most {MaxFormatted:N0} cells; give the cells that hold something."));
        if (!parts.Exists("xl/styles.xml"))
            throw new InvalidOperationException("This workbook has no styles to add a format to.");
        var styles = parts.Get("xl/styles.xml").Root!;
        var xfs = styles.Element(S + "cellXfs") ?? throw new InvalidOperationException("This workbook's styles list no cell formats.");
        var xml = parts.Get(sheet.Part);
        var data = xml.Root!.Element(S + "sheetData") ?? throw new InvalidOperationException($"Sheet \"{sheet.Name}\" has no cells part.");
        var made = new Dictionary<int, int>();
        for (var row = line.Row1; row <= line.Row2; row++)
            for (var column = line.Column1; column <= line.Column2; column++)
            {
                // An empty cell is made, so a fill runs across the whole row of a heading.
                var reference = XlsxWriter.Reference(row, column);
                var cell = Find(xml, reference) ?? Create(data, reference);
                var current = (int?)cell.Attribute("s") ?? 0;
                if (!made.TryGetValue(current, out var index))
                    made[current] = index = Styled(styles, xfs, current, line.Look);
                cell.SetAttributeValue("s", index);
            }
        Dimension(xml);
        parts.Changed(sheet.Part);
        parts.Changed("xl/styles.xml");
        return $"formatted {line.Cells} on sheet \"{sheet.Name}\" ({line.Look.Said})";
    }

    /// <summary>The cell format that is format <paramref name="current"/> with <paramref name="look"/>: one there already, or a new one.</summary>
    private static int Styled(XElement styles, XElement xfs, int current, DocumentLook look)
    {
        var all = xfs.Elements(S + "xf").ToList();
        var xf = new XElement(all.ElementAtOrDefault(current) ?? all.FirstOrDefault() ?? new XElement(S + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0)));
        if (look.Bold is not null || look.Italic is not null || look.Underline is not null || look.Size is not null || look.Colour is not null)
        {
            var fonts = Section(styles, "fonts", "numFmts");
            var font = new XElement(fonts.Elements(S + "font").ElementAtOrDefault((int?)xf.Attribute("fontId") ?? 0) ?? new XElement(S + "font"));
            // In the order a font's parts go in: b, i, … sz, color, name.
            if (look.Italic is { } italic)
            {
                font.Elements(S + "i").Remove();
                if (italic) font.AddFirst(new XElement(S + "i"));
            }
            if (look.Bold is { } bold)
            {
                font.Elements(S + "b").Remove();
                if (bold) font.AddFirst(new XElement(S + "b"));
            }
            if (look.Underline is { } underline)
            {
                font.Elements(S + "u").Remove();
                // After the bold and italic marks, before the size.
                if (underline)
                {
                    if (font.Elements().LastOrDefault(e => e.Name.LocalName is "b" or "i" or "strike" or "condense" or "extend" or "outline" or "shadow") is { } mark) mark.AddAfterSelf(new XElement(S + "u"));
                    else font.AddFirst(new XElement(S + "u"));
                }
            }
            if (look.Size is { } points)
            {
                if (font.Element(S + "sz") is { } has) has.SetAttributeValue("val", points.ToString("0.#", CultureInfo.InvariantCulture));
                else if (font.Elements().FirstOrDefault(e => e.Name.LocalName is "color" or "name" or "family" or "charset" or "scheme") is { } later) later.AddBeforeSelf(new XElement(S + "sz", new XAttribute("val", points.ToString("0.#", CultureInfo.InvariantCulture))));
                else font.Add(new XElement(S + "sz", new XAttribute("val", points.ToString("0.#", CultureInfo.InvariantCulture))));
            }
            if (look.Colour is { } colour)
            {
                font.Elements(S + "color").Remove();
                var text = new XElement(S + "color", new XAttribute("rgb", "FF" + colour));
                if (font.Element(S + "sz") is { } size) size.AddAfterSelf(text);
                else if (font.Elements().FirstOrDefault(e => e.Name.LocalName is "name" or "family" or "charset" or "scheme") is { } later) later.AddBeforeSelf(text);
                else font.Add(text);
            }
            xf.SetAttributeValue("fontId", Same(fonts, "font", font));
            xf.SetAttributeValue("applyFont", 1);
        }
        if (look.Fill is { } fill)
        {
            var fills = Section(styles, "fills", "fonts");
            // The first two fills of a workbook are none and Excel's grey pattern, whatever they say: kept there.
            while (fills.Elements(S + "fill").Count() < 2)
                fills.Add(new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", fills.Elements(S + "fill").Any() ? "gray125" : "none"))));
            xf.SetAttributeValue("fillId", fill.Length == 0 ? 0 : Same(fills, "fill", new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "solid"),
                new XElement(S + "fgColor", new XAttribute("rgb", "FF" + fill)), new XElement(S + "bgColor", new XAttribute("indexed", 64))))));
            xf.SetAttributeValue("applyFill", 1);
        }
        if (look.Border)
        {
            var borders = Section(styles, "borders", "fills");
            static XElement Side(string name) => new(S + name, new XAttribute("style", "thin"), new XElement(S + "color", new XAttribute("auto", 1)));
            xf.SetAttributeValue("borderId", Same(borders, "border", new XElement(S + "border", Side("left"), Side("right"), Side("top"), Side("bottom"), new XElement(S + "diagonal"))));
            xf.SetAttributeValue("applyBorder", 1);
        }
        if (look.Align is not null || look.Wrap is not null)
        {
            var alignment = xf.Element(S + "alignment");
            if (alignment is null)
                xf.AddFirst(alignment = new XElement(S + "alignment"));
            if (look.Align is { } align)
                alignment.SetAttributeValue("horizontal", align);
            if (look.Wrap is { } wrap)
                alignment.SetAttributeValue("wrapText", wrap ? 1 : null);
            xf.SetAttributeValue("applyAlignment", 1);
        }
        return Same(xfs, "xf", xf);
    }

    /// <summary>A list of the styles (fonts, fills, borders), made after <paramref name="after"/> when the workbook has none.</summary>
    private static XElement Section(XElement styles, string name, string after)
    {
        if (styles.Element(S + name) is { } there)
            return there;
        var section = new XElement(S + name, new XAttribute("count", 0));
        if (styles.Element(S + after) is { } before) before.AddAfterSelf(section);
        else styles.AddFirst(section);
        return section;
    }

    /// <summary>The place of <paramref name="item"/> in <paramref name="list"/>: one just like it that is there, or added at its end.</summary>
    private static int Same(XElement list, string name, XElement item)
    {
        var items = list.Elements(S + name).ToList();
        var index = items.FindIndex(i => XNode.DeepEquals(i, item));
        if (index >= 0)
            return index;
        list.Add(item);
        list.SetAttributeValue("count", items.Count + 1);
        return items.Count;
    }

    // "format A1:E1 bold, yellow fill", "format Budget!B2 as red text", "Format 'My sheet'!A1:D1: fill #FFF2CC"
    [GeneratedRegex(@"^format\s+(?:(?:the\s+)?cells?\s+)?(?:(?<sheet>'(?:[^']|'')+'|[^\s!'|""]+)!)?(?<a>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6})(?::(?<b>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6}))?\s*[:,]?\s*(?:as\s+|with\s+|in\s+)?(?<what>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FormatStart();
}

internal static partial class SheetEdit
{
    // ---- how wide columns are: "column A width 20", "autofit columns" --------------------------------------------------
    // Asked to make column A about twice as wide (2026-11-13), Haiku wrote the width into the sheet's XML through
    // PowerShell and Sonnet looked for Python, then drove Excel.

    /// <summary>Columns <paramref name="Column1"/> to <paramref name="Column2"/> (0: to the last one used) made <paramref name="Width"/> characters wide, or (null) as wide as what they hold.</summary>
    private sealed record WidthLine(string? Sheet, int Column1, int Column2, double? Width);

    private static WidthLine? WidthOf(string line, string? sheet)
    {
        var text = line.Trim().TrimEnd('.');
        static int Column(string letters) => letters.ToUpperInvariant().Aggregate(0, (n, ch) => n * 26 + ch - 'A' + 1);
        if (ColumnWidth().Match(text) is { Success: true } m)
        {
            var on = m.Groups["sheet"].Success ? m.Groups["sheet"].Value.Trim('\'').Replace("''", "'") : sheet;
            var (a, b) = (Column(m.Groups["a"].Value), m.Groups["b"].Success ? Column(m.Groups["b"].Value) : Column(m.Groups["a"].Value));
            double? width = m.Groups["w"].Success ? double.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture) : null;
            if (width is < 0 or > 255)
                throw new InvalidOperationException($"{text}: a column is 0 to 255 characters wide (Excel's own are 8.43).");
            return new WidthLine(on, Math.Min(a, b), Math.Max(a, b), width);
        }
        return FitAll().IsMatch(text) ? new WidthLine(sheet, 1, 0, null) : null;
    }

    /// <summary>The columns of a width line given their width in the sheet's column list, the ones around them left as they are; what was done.</summary>
    private static string ApplyWidth(PartSet parts, (string Name, string Part) sheet, WidthLine line, List<string> shared)
    {
        var xml = parts.Get(sheet.Part);
        var root = xml.Root!;
        var data = root.Element(S + "sheetData") ?? throw new InvalidOperationException($"Sheet \"{sheet.Name}\" has no cells part.");
        // How long what each column holds is, for a fit: a number shown with its format is a little longer than it is kept.
        var longest = new Dictionary<int, int>();
        foreach (var c in data.Elements(S + "row").Elements(S + "c"))
        {
            if ((string?)c.Attribute("r") is not { } at)
                continue;
            var column = Parse(at).Column;
            var shown = c.Element(S + "f") is not null ? ((string?)c.Element(S + "v") ?? "").Length + 3 : Shown(c, shared).Split('\n').Max(l => l.Length) + (c.Attribute("t") is null ? 3 : 0);
            longest[column] = Math.Max(longest.GetValueOrDefault(column), shown);
        }
        var last = line.Column2 > 0 ? line.Column2 : longest.Count > 0 ? longest.Keys.Max() : 1;
        if (last - line.Column1 > 2000)
            throw new InvalidOperationException("One line sets the width of at most 2,000 columns.");
        var cols = root.Element(S + "cols");
        if (cols is null)
        {
            cols = new XElement(S + "cols");
            data.AddBeforeSelf(cols);
        }
        for (var column = line.Column1; column <= last; column++)
        {
            var width = line.Width ?? Math.Clamp(longest.GetValueOrDefault(column) * 1.1 + 2, 8.43, 60);
            // The entry that covers this column, cut down to it: what it said of the columns beside it stays theirs.
            var over = cols.Elements(S + "col").FirstOrDefault(c => ((int?)c.Attribute("min") ?? 0) <= column && column <= ((int?)c.Attribute("max") ?? 0));
            XElement own;
            if (over is null)
                cols.Add(own = new XElement(S + "col", new XAttribute("min", column), new XAttribute("max", column)));
            else
            {
                var (min, max) = ((int)over.Attribute("min")!, (int)over.Attribute("max")!);
                own = over;
                if (min < column)
                {
                    var before = new XElement(over);
                    before.SetAttributeValue("max", column - 1);
                    over.AddBeforeSelf(before);
                }
                if (max > column)
                {
                    var after = new XElement(over);
                    after.SetAttributeValue("min", column + 1);
                    over.AddAfterSelf(after);
                }
                own.SetAttributeValue("min", column);
                own.SetAttributeValue("max", column);
            }
            own.SetAttributeValue("width", width.ToString("0.##", CultureInfo.InvariantCulture));
            own.SetAttributeValue("customWidth", 1);
            own.Attribute("hidden")?.Remove();
        }
        var sorted = cols.Elements(S + "col").OrderBy(c => (int?)c.Attribute("min") ?? 0).ToList();
        cols.ReplaceNodes(sorted);
        parts.Changed(sheet.Part);
        var named = line.Column1 == last ? $"column {Letters(line.Column1)}" : $"columns {Letters(line.Column1)}-{Letters(last)}";
        return line.Width is { } set
            ? $"made {named} of sheet \"{sheet.Name}\" {set.ToString("0.##", CultureInfo.InvariantCulture)} wide"
            : $"fitted {named} of sheet \"{sheet.Name}\" to what {(line.Column1 == last ? "it holds" : "they hold")}";
    }

    private static string Letters(int column) => XlsxWriter.Reference(1, column).TrimEnd('1');

    // "column A width 20", "columns B:D width 12.5", "set column A to width 18", "column C autofit", "Budget!A width 30"
    [GeneratedRegex(@"^(?:set\s+|make\s+)?(?:the\s+)?(?:width\s+of\s+)?columns?\s+(?:(?<sheet>'(?:[^']|'')+'|[^\s!'|""]+)!)?(?<a>[A-Za-z]{1,3})(?:\s*(?::|-|to)\s*(?<b>[A-Za-z]{1,3}))?\s*(?:to\s+|:\s*|=\s*)?(?:(?:a\s+)?width\s*(?:of\s+|to\s+|=\s*|:\s*)?(?<w>[0-9]+(?:\.[0-9]+)?)|(?<w>[0-9]+(?:\.[0-9]+)?)\s*(?:wide|characters?(?:\s+wide)?)|auto-?fit(?:ted)?|fit(?:ted)?(?:\s+to\s+(?:its|their)\s+(?:content|text)s?)?|auto(?:matic)?\s+width)$", RegexOptions.IgnoreCase)]
    private static partial Regex ColumnWidth();

    // "autofit columns", "fit all columns", "auto-fit the columns"
    [GeneratedRegex(@"^(?:auto-?fit|fit|autosize)\s+(?:all\s+)?(?:the\s+)?columns?(?:\s+to\s+(?:their\s+)?(?:content|text)s?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex FitAll();
}
