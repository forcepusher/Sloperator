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

    /// <param name="Number">How numbers show, as Excel's number format code ("$"#,##0.00), or null to leave it.</param>
    private sealed record FormatLine(string? Sheet, int Row1, int Column1, int Row2, int Column2, DocumentLook Look, string Cells, string? Number = null);

    /// <summary>The most cells one format line may cover: each gets a style of its own in the sheet.</summary>
    private const int MaxFormatted = 50_000;

    /// <summary>
    /// A line that formats cells, or null: "format A1:E1 bold, yellow fill", "format Budget!B2:B9 red text, centered",
    /// "format A1:D1 fill #FFF2CC, border". A colour with nothing said of it is the fill.
    /// </summary>
    private static FormatLine? FormatOf(string line, string? sheet, IReadOnlyList<string> names)
    {
        var (said, text) = SheetSaid(line.Trim(), names);
        if (FormatStart().Match(text) is not { Success: true } m)
            return null;
        var (number, words) = NumberIn(m.Groups["what"].Value);
        // Without the word "format" it is one only as a range with nothing but a look after it ("A1:B1 bold, light
        // blue fill"): a cell and what it holds is written the same way ("B2 bold").
        if (!m.Groups["kw"].Success && (!m.Groups["b"].Success || !(number is not null && words.Trim(' ', ',', '.').Length == 0 || DocumentLook.OnlyLook(words))))
            return null;
        var (r1, c1) = Parse(m.Groups["a"].Value.Replace("$", "").ToUpperInvariant());
        var (r2, c2) = m.Groups["b"].Success ? Parse(m.Groups["b"].Value.Replace("$", "").ToUpperInvariant()) : (r1, c1);
        var on = m.Groups["sheet"].Success ? Unquoted(m.Groups["sheet"].Value) : said ?? sheet;
        var look = DocumentLook.Parse(words);
        if (!look.Any && number is null)
            throw new InvalidOperationException($"{line.Trim()}: say how the cells should look: {DocumentLook.Words}, wrap, border, or how numbers show (as dollars, as percent, as dates, 2 decimals).");
        var cells = $"{XlsxWriter.Reference(Math.Min(r1, r2), Math.Min(c1, c2))}{(r1 == r2 && c1 == c2 ? "" : ":" + XlsxWriter.Reference(Math.Max(r1, r2), Math.Max(c1, c2)))}";
        return new FormatLine(on, Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2), look, cells, number);
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
                    made[current] = index = Styled(styles, xfs, current, line.Look, line.Number);
                cell.SetAttributeValue("s", index);
            }
        Dimension(xml);
        parts.Changed(sheet.Part);
        parts.Changed("xl/styles.xml");
        var said = string.Join(", ", new[] { line.Look.Said, line.Number is null ? null : $"numbers as {line.Number}" }.Where(s => !string.IsNullOrEmpty(s)));
        var widened = line.Number is null ? [] : Widen(parts, sheet, line);
        return $"formatted {line.Cells} on sheet \"{sheet.Name}\" ({said})" +
               (widened.Count == 0 ? "" : $", {(widened.Count == 1 ? "column " + widened[0] : "columns " + string.Join(", ", widened))} made wider to show them");
    }

    /// <summary>
    /// The columns of a format line made wide enough for its numbers as they now show, as Excel widens a column when a
    /// number format is applied to it; never narrower. Shown in dollars in a workbook Excel had fitted to the plain
    /// numbers, the amounts were ##### (2026-11-13).
    /// </summary>
    private static List<string> Widen(PartSet parts, (string Name, string Part) sheet, FormatLine line)
    {
        var xml = parts.Get(sheet.Part);
        var root = xml.Root!;
        var longest = new Dictionary<int, int>();
        foreach (var c in root.Element(S + "sheetData")?.Elements(S + "row").Elements(S + "c") ?? [])
            if ((string?)c.Attribute("r") is { } at && Parse(at) is var (row, column) && row >= line.Row1 && row <= line.Row2 && column >= line.Column1 && column <= line.Column2
                && (string?)c.Attribute("t") is null or "n" && double.TryParse((string?)c.Element(S + "v"), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                && Tools.DocumentText.Formatted(n, line.Number) is { } shown)
                longest[column] = Math.Max(longest.GetValueOrDefault(column), shown.Length);
        var standard = double.TryParse((string?)root.Element(S + "sheetFormatPr")?.Attribute("defaultColWidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 8.43;
        var widened = new List<string>();
        foreach (var (column, length) in longest.OrderBy(l => l.Key))
        {
            var needed = Math.Min(60, length * 1.1 + 2);
            var col = root.Element(S + "cols")?.Elements(S + "col").FirstOrDefault(e => ((int?)e.Attribute("min") ?? 0) <= column && column <= ((int?)e.Attribute("max") ?? 0));
            var now = double.TryParse((string?)col?.Attribute("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : standard;
            if (needed <= now)
                continue;
            ApplyWidth(parts, sheet, new WidthLine(sheet.Name, column, column, Math.Round(needed, 2)), []);
            widened.Add(Letters(column));
        }
        return widened;
    }

    /// <summary>
    /// How numbers are to show, as a format line says it in words ("as dollars", "as percent", "as dates", "2
    /// decimals", "with a thousands separator") or as Excel's own code ("#,##0.00"), and the line's other words.
    /// </summary>
    private static (string? Code, string Others) NumberIn(string what)
    {
        string? kind = null;
        int? decimals = null;
        var rest = what;
        string Take(Regex pattern, string? set)
        {
            if (pattern.Match(rest) is { Success: true } m)
            {
                kind ??= set;
                rest = rest.Remove(m.Index, m.Length).Insert(m.Index, " ");
                return m.Value;
            }
            return "";
        }
        if (NumberCode().Match(rest) is { Success: true } code)
            return (code.Groups["code"].Value.Trim('"', '\'') is { Length: > 0 } given ? given : null, rest.Remove(code.Index, code.Length));
        Take(Dollars(), "$");
        Take(Euros(), "€");
        Take(Pounds(), "£");
        Take(Percents(), "%");
        Take(Dates(), "date");
        if (Decimals().Match(rest) is { Success: true } d)
        {
            decimals = d.Groups["d"].Value.ToLowerInvariant() switch { "one" => 1, "two" => 2, "three" => 3, "no" or "zero" => 0, var n => int.Parse(n, CultureInfo.InvariantCulture) };
            rest = rest.Remove(d.Index, d.Length).Insert(d.Index, " ");
            kind ??= "number";
        }
        Take(Thousands(), "number");
        if (kind is null)
            return (null, what);
        var places = Math.Clamp(decimals ?? (kind is "%" or "number" ? 0 : 2), 0, 10);
        var tail = places > 0 ? "." + new string('0', places) : "";
        var number = "#,##0" + tail;
        return (kind switch
        {
            "$" => "\"$\"" + number,
            "€" => number + " \"€\"",
            "£" => "\"£\"" + number,
            "%" => "0" + tail + "%",
            "date" => "yyyy-mm-dd",
            _ => number,
        }, rest);
    }

    // "number format #,##0.00", "format code 0.0%", or the code alone: "#,##0", "0.00%", "$#,##0.00"
    [GeneratedRegex(@"(?:\bnumber\s+format(?:\s+code)?|\bformat\s+code|\bcode)\s*[:=]?\s*(?<code>""[^""]+""|'[^']+'|\S+)|(?<![\w#$€£])(?!#[0-9a-f]{6}\b)(?<code>[$€£]?[#0](?:[#0,]*[#0])?(?:\.0+)?%?)(?![\w#])", RegexOptions.IgnoreCase)]
    private static partial Regex NumberCode();

    [GeneratedRegex(@"\b(?:(?:shown\s+|show\s+)?(?:in|as)\s+)?(?:us\s+)?(?:dollars?|usd)\b|\b(?:(?:in|as)\s+)?(?:currency|money)\b|(?:\bas\s+)?(?<![\w$])\$(?![\w#0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Dollars();

    [GeneratedRegex(@"\b(?:(?:in|as)\s+)?(?:euros?|eur)\b|(?:\bas\s+)?(?<!\w)€(?![\w#0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Euros();

    [GeneratedRegex(@"\b(?:(?:in|as)\s+)?(?:pounds?|gbp|sterling)\b|(?:\bas\s+)?(?<!\w)£(?![\w#0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Pounds();

    [GeneratedRegex(@"\b(?:(?:in|as)\s+(?:a\s+)?)?(?:percent(?:age)?s?)\b|(?:\bas\s+)?(?<![\w%])%(?![\w%])", RegexOptions.IgnoreCase)]
    private static partial Regex Percents();

    [GeneratedRegex(@"\b(?:(?:in|as)\s+)?dates?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Dates();

    [GeneratedRegex(@"\b(?:with\s+)?(?<d>[0-9]|no|zero|one|two|three)\s+decimals?(?:\s+places?)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Decimals();

    [GeneratedRegex(@"\b(?:with\s+)?(?:a\s+)?thousands?\s+separators?\b|\bwith\s+commas\b|\b(?:as\s+)?whole\s+numbers?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Thousands();

    /// <summary>The cell format that is format <paramref name="current"/> with <paramref name="look"/>: one there already, or a new one.</summary>
    private static int Styled(XElement styles, XElement xfs, int current, DocumentLook look, string? number = null)
    {
        var all = xfs.Elements(S + "xf").ToList();
        var xf = new XElement(all.ElementAtOrDefault(current) ?? all.FirstOrDefault() ?? new XElement(S + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0)));
        if (number is not null)
        {
            xf.SetAttributeValue("numFmtId", NumberFormatId(styles, number));
            xf.SetAttributeValue("applyNumberFormat", 1);
        }
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

    // "format A1:E1 bold, yellow fill", "format Budget!B2 as red text", "Format 'My sheet'!A1:D1: fill #FFF2CC",
    // "format "My sheet"!A1:B1 bold", "format My sheet!A1:B1 bold"
    [GeneratedRegex(@"^(?:(?<kw>format)\s+(?:(?:the\s+)?(?:cells?|range)\s+)?)?(?:(?<sheet>'(?:[^']|'')+'|""[^""]+""|“[^”]+”|[^!|'""“”]+?)!)?(?<a>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6})(?::(?<b>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6}))?\s*[:,]?\s*(?:as\s+|with\s+|in\s+|to\s+)?(?<what>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FormatStart();

    // ---- which sheet a line is for ---------------------------------------------------------------------------------
    // Asked for the header rows of both sheets (2026-11-13), Sonnet named the second as a formula does and it was
    // formatted; Haiku tried it in double quotes, without quotes, and as "format A1:B1 on sheet "Revenue by region"
    // bold", which formatted the first sheet, the words read past. Nemotron went to write the workbook again.

    /// <summary>A sheet's name as a line gives it before a "!" or after "on sheet": without the quotes around it.</summary>
    private static string Unquoted(string name)
    {
        name = name.Trim();
        if (name.Length >= 2 && name[0] == '\'' && name[^1] == '\'')
            return name[1..^1].Replace("''", "'");
        return name.Length >= 2 && (name[0] == '"' && name[^1] == '"' || name[0] == '“' && name[^1] == '”') ? name[1..^1] : name;
    }

    /// <summary>
    /// The sheet a line names in words, and the line without them: "format A1:B1 on sheet "Revenue by region" bold"
    /// is "Revenue by region" and "format A1:B1 bold". A name without quotes is one of the workbook's own (the
    /// longest that fits), else one word.
    /// </summary>
    private static (string? Sheet, string Line) SheetSaid(string line, IReadOnlyList<string> names)
    {
        if (OnSheet().Match(line) is not { Success: true } m)
            return (null, line);
        var rest = line[(m.Index + m.Length)..];
        string name;
        int taken;
        if (QuotedName().Match(rest) is { Success: true } quoted)
            (name, taken) = (Unquoted(quoted.Value), quoted.Length);
        else if (names.Where(n => rest.StartsWith(n, StringComparison.OrdinalIgnoreCase) && (rest.Length == n.Length || !char.IsLetterOrDigit(rest[n.Length])))
                     .OrderByDescending(n => n.Length).FirstOrDefault() is { } known)
            (name, taken) = (known, known.Length);
        else
        {
            taken = rest.IndexOfAny([' ', ',', ':', ';']) is var end and >= 0 ? end : rest.Length;
            name = rest[..taken];
        }
        return (name, (line[..m.Index] + " " + rest[taken..].TrimStart(' ', ',', ':', ';')).Trim());
    }

    // " on sheet ", " in the sheet ", " of worksheet "
    [GeneratedRegex(@"\s+(?:on|in|of|for)\s+(?:the\s+)?(?:work)?sheet\s+", RegexOptions.IgnoreCase)]
    private static partial Regex OnSheet();

    [GeneratedRegex(@"^(?:'(?:[^']|'')+'|""[^""]+""|“[^”]+”)")]
    private static partial Regex QuotedName();
}

internal static partial class SheetEdit
{
    // ---- how wide columns are: "column A width 20", "autofit columns" --------------------------------------------------
    // Asked to make column A about twice as wide (2026-11-13), Haiku wrote the width into the sheet's XML through
    // PowerShell and Sonnet looked for Python, then drove Excel.

    /// <summary>Columns <paramref name="Column1"/> to <paramref name="Column2"/> (0: to the last one used) made <paramref name="Width"/> characters wide, or (null) as wide as what they hold.</summary>
    /// <param name="Every">Every sheet's columns: a fit of all columns with no sheet said ("autofit columns").</param>
    private sealed record WidthLine(string? Sheet, int Column1, int Column2, double? Width, bool Every = false);

    private static WidthLine? WidthOf(string line, string? sheet, IReadOnlyList<string> names)
    {
        var (said, text) = SheetSaid(line.Trim().TrimEnd('.'), names);
        var m = ColumnWidth().Match(text);
        // "autofit columns A:C" too, unless what follows is a sheet's name ("autofit columns Q1" in a workbook with a sheet Q1).
        if (!m.Success && FitSome().Match(text) is { Success: true } some && !names.Contains(some.Groups["a"].Value, StringComparer.OrdinalIgnoreCase))
            m = some;
        if (m.Success)
        {
            var on = m.Groups["sheet0"].Success ? Unquoted(m.Groups["sheet0"].Value) : m.Groups["sheet"].Success ? Unquoted(m.Groups["sheet"].Value) : said ?? sheet;
            var (a, b) = (XlsxWriter.Column(m.Groups["a"].Value), m.Groups["b"].Success ? XlsxWriter.Column(m.Groups["b"].Value) : XlsxWriter.Column(m.Groups["a"].Value));
            double? width = m.Groups["w"].Success ? double.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture) : null;
            if (width is < 0 or > 255)
                throw new InvalidOperationException($"{text}: a column is 0 to 255 characters wide (Excel's own are 8.43).");
            return new WidthLine(on, Math.Min(a, b), Math.Max(a, b), width);
        }
        if (FitAll().Match(text) is not { Success: true } all)
            return null;
        // What follows is a sheet's name ("autofit columns 'Revenue by region'"), or all of them ("on all sheets"); other words make it no such line.
        var where = all.Groups["where"].Success ? all.Groups["where"].Value.Trim() : null;
        if (all.Groups["sheet0"].Success)
            return where is null ? new WidthLine(Unquoted(all.Groups["sheet0"].Value), 1, 0, null) : null;
        if (where is null || EverySheet().IsMatch(where))
            return new WidthLine(said ?? sheet, 1, 0, null, Every: where is not null || (said ?? sheet) is null);
        var named = QuotedName().Match(where) is { Success: true } quoted && quoted.Length == where.Length ? Unquoted(where)
            : names.FirstOrDefault(n => n.Equals(where, StringComparison.OrdinalIgnoreCase));
        return named is null ? null : new WidthLine(named, 1, 0, null);
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

    // "column A width 20", "columns B:D width 12.5", "set column A to width 18", "column C autofit", "column Budget!A width 30",
    // "'My sheet'!column A width 18"
    [GeneratedRegex(@"^(?:(?<sheet0>'(?:[^']|'')+'|""[^""]+""|“[^”]+”|[^!|'""“”]+?)!\s*)?(?:set\s+|make\s+)?(?:the\s+)?(?:width\s+of\s+)?columns?\s+(?:(?<sheet>'(?:[^']|'')+'|""[^""]+""|“[^”]+”|[^\s!'|""]+)!)?(?<a>[A-Za-z]{1,3})(?:\s*(?::|-|to)\s*(?<b>[A-Za-z]{1,3}))?\s*(?:to\s+|:\s*|=\s*)?(?:(?:a\s+)?width\s*(?:of\s+|to\s+|=\s*|:\s*)?(?<w>[0-9]+(?:\.[0-9]+)?)|(?<w>[0-9]+(?:\.[0-9]+)?)\s*(?:wide|characters?(?:\s+wide)?)|auto-?fit(?:ted)?|fit(?:ted)?(?:\s+to\s+(?:its|their)\s+(?:content|text)s?)?|auto(?:matic)?\s+width)$", RegexOptions.IgnoreCase)]
    private static partial Regex ColumnWidth();

    // "autofit columns A:C", "fit column B", "'My sheet'!autofit columns A:F": the columns in capitals, as words follow too ("autofit columns on all sheets")
    [GeneratedRegex(@"^(?:(?<sheet0>'(?:[^']|'')+'|""[^""]+""|“[^”]+”|[^!|'""“”]+?)!\s*)?(?:auto[- ]?fit|fit|auto[- ]?size)\s+(?:the\s+)?(?:width\s+of\s+)?columns?\s+(?:(?<sheet>'(?:[^']|'')+'|""[^""]+""|“[^”]+”|[^\s!'|""]+)!)?(?<a>(?-i:[A-Z]{1,3}))(?:\s*(?::|-|to)\s*(?<b>(?-i:[A-Z]{1,3})))?$", RegexOptions.IgnoreCase)]
    private static partial Regex FitSome();

    // "autofit columns", "fit all columns", "auto-fit the column widths", "autofit columns 'My sheet'", "autofit columns on all sheets",
    // "Orders!autofit columns" (as Haiku wrote it, 2026-11-13)
    [GeneratedRegex(@"^(?:(?<sheet0>'(?:[^']|'')+'|""[^""]+""|“[^”]+”|[^!|'""“”]+?)!\s*)?(?:auto[- ]?fit|fit|auto[- ]?size)(?:\s+(?:all|every|each))?(?:\s+(?:of\s+)?the)?\s+columns?(?:\s+widths?)?(?:\s+to\s+(?:their\s+|the\s+)?(?:content|text|data)s?)?(?:\s+(?:(?:on|in|of|for)\s+)?(?<where>.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex FitAll();

    [GeneratedRegex(@"^(?:all|every|each|both)(?:\s+(?:of\s+)?the)?\s+(?:work)?sheets?$|^(?:the\s+)?(?:whole\s+|entire\s+)?workbook$", RegexOptions.IgnoreCase)]
    private static partial Regex EverySheet();
}
