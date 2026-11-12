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

    /// <summary>What a format line asks of its cells; what it doesn't say stays as it is.</summary>
    /// <param name="Fill">The background as RRGGBB, or "" for none.</param>
    /// <param name="Colour">The text's colour as RRGGBB.</param>
    /// <param name="Align">"left", "center" or "right".</param>
    private sealed record Look(bool? Bold, bool? Italic, string? Fill, string? Colour, string? Align, bool? Wrap, bool Border)
    {
        public bool Any => Bold is not null || Italic is not null || Fill is not null || Colour is not null || Align is not null || Wrap is not null || Border;

        /// <summary>"bold, yellow fill": what was done, for the tool's answer.</summary>
        public string Said => string.Join(", ", new[]
        {
            Bold switch { true => "bold", false => "not bold", _ => null },
            Italic switch { true => "italic", false => "not italic", _ => null },
            Fill switch { null => null, "" => "no fill", var f => $"fill #{f}" },
            Colour is null ? null : $"text #{Colour}",
            Align,
            Wrap switch { true => "wrapped", false => "not wrapped", _ => null },
            Border ? "border" : null,
        }.OfType<string>());
    }

    private sealed record FormatLine(string? Sheet, int Row1, int Column1, int Row2, int Column2, Look Look, string Cells);

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
        var what = " " + m.Groups["what"].Value.ToLowerInvariant().Replace(',', ' ').Replace(';', ' ').Replace(" and ", " ") + " ";
        bool? Flag(string word) => Regex.IsMatch(what, $@"\b(?:not|no|un)[\s-]?{word}\b") ? false : Regex.IsMatch(what, $@"\b{word}\b") ? true : null;
        string? fill = null, colour = null;
        if (NoFill().IsMatch(what))
            fill = "";
        // "yellow fill", "fill yellow", "background: #FFF2CC"; "red text", "text red", "font colour blue".
        foreach (Match c in ColourWord().Matches(what))
        {
            if (ColourOf(c.Groups["c"].Value) is not { } hex)
                continue;
            var before = what[..c.Index];
            var after = what[(c.Index + c.Length)..];
            var text = TextBefore().IsMatch(before) || TextAfter().IsMatch(after);
            var back = FillBefore().IsMatch(before) || FillAfter().IsMatch(after);
            if (text && !back)
                colour ??= hex;
            else
                fill ??= hex;
        }
        var look = new Look(Flag("bold"), Flag("italics?"), fill, colour,
            Regex.IsMatch(what, @"\bcent(?:er|re)(?:d|ed)?\b") ? "center" : Regex.IsMatch(what, @"\bright\b") ? "right" : Regex.IsMatch(what, @"\bleft\b") ? "left" : null,
            Flag("wrap(?:ped)?"), Regex.IsMatch(what, @"\bborders?\b") && !Regex.IsMatch(what, @"\bno\s+borders?\b"));
        if (!look.Any)
            throw new InvalidOperationException($"{line.Trim()}: say how the cells should look: bold, italic, a fill or text colour by name or #RRGGBB (yellow fill, red text), center, wrap, border.");
        var cells = $"{XlsxWriter.Reference(Math.Min(r1, r2), Math.Min(c1, c2))}{(r1 == r2 && c1 == c2 ? "" : ":" + XlsxWriter.Reference(Math.Max(r1, r2), Math.Max(c1, c2)))}";
        return new FormatLine(on, Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2), look, cells);
    }

    /// <summary>A colour by its name, as Excel's palette has it, or as #RRGGBB; null for a word that is none.</summary>
    private static string? ColourOf(string word)
    {
        var w = Regex.Replace(word.Trim().ToLowerInvariant(), @"\s+", " ").Replace("gray", "grey");
        if (Regex.Match(w, "^#?([0-9a-f]{6})$") is { Success: true } hex)
            return hex.Groups[1].Value.ToUpperInvariant();
        return w switch
        {
            "yellow" => "FFFF00", "light yellow" or "pale yellow" => "FFF2CC", "gold" => "FFD966",
            "orange" => "FFC000", "light orange" => "F8CBAD",
            "red" => "FF0000", "light red" => "FFC7CE", "dark red" => "C00000",
            "green" => "00B050", "light green" => "C6EFCE", "dark green" => "006100",
            "blue" => "0070C0", "light blue" => "BDD7EE", "dark blue" or "navy" => "002060",
            "purple" => "7030A0", "pink" => "FF99CC", "teal" => "00B0A0", "brown" => "7B3F00",
            "grey" => "BFBFBF", "light grey" => "D9D9D9", "dark grey" => "595959",
            "white" => "FFFFFF", "black" => "000000",
            _ => null,
        };
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
    private static int Styled(XElement styles, XElement xfs, int current, Look look)
    {
        var all = xfs.Elements(S + "xf").ToList();
        var xf = new XElement(all.ElementAtOrDefault(current) ?? all.FirstOrDefault() ?? new XElement(S + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0)));
        if (look.Bold is not null || look.Italic is not null || look.Colour is not null)
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

    [GeneratedRegex(@"(?<![\w#])(?<c>#[0-9a-f]{6}|(?:(?:light|pale|dark)\s+)?(?:yellow|orange|red|green|blue|purple|pink|teal|brown|gr[ae]y|white|black)|gold|navy)(?![\w])")]
    private static partial Regex ColourWord();

    [GeneratedRegex(@"\b(?:no|without)\s+(?:fill|background|highlight|shading)\b|\b(?:fill|background|highlight|shading)\s*(?:colou?r\s*)?[:=]?\s*none\b")]
    private static partial Regex NoFill();

    [GeneratedRegex(@"\b(?:text|font|letters?)\s*(?:colou?r\s*)?(?:of\s+|in\s+|[:=]\s*)?$")]
    private static partial Regex TextBefore();

    [GeneratedRegex(@"^\s*(?:text|font|letters?)\b")]
    private static partial Regex TextAfter();

    [GeneratedRegex(@"\b(?:fill|background|bg|highlight(?:ed)?|shad(?:e|ed|ing))\s*(?:colou?r\s*)?(?:of\s+|in\s+|[:=]\s*)?$")]
    private static partial Regex FillBefore();

    [GeneratedRegex(@"^\s*(?:fill|background|bg|highlight|shading)\b")]
    private static partial Regex FillAfter();
}
