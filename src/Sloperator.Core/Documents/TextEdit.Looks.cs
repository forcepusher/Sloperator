using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sloperator.Core.Documents;

internal static partial class TextEdit
{
    // ---- how text looks: new_text "format: centered, dark blue" ----------------------------------------------------
    // edit_file changed what a Word document or a slide says, not how it looks. Asked to centre a title, make it
    // dark blue and give a table's header row a grey background (2026-11-13), Haiku and Sonnet unzipped the document
    // and rewrote its XML through PowerShell, nine to twelve calls a time, Haiku by way of a script it left in the
    // folder above the user's.

    /// <summary>
    /// The text or table rows given as old_text made to look as new_text's "format: …" line says, their words as
    /// they are; null when new_text is no such line. Text: bold, italic, underline, size, a text colour and a fill
    /// behind it on what matched, and left, center or right on its paragraphs. Rows as read_file shows them
    /// ("| Month | Sales |"): the fill is their cells', the rest their text's.
    /// </summary>
    public static (int Count, List<string> Parts, string Said)? Look(Dictionary<string, XDocument> parts, bool slides, string oldText, string newText, bool all)
    {
        if (LookLine().Match(newText.Trim()) is not { Success: true } line)
            return null;
        // A colour with nothing said of it is the text's ("dark blue"); for a table's rows it is their cells' fill, as in a workbook.
        var look = DocumentLook.Parse(line.Groups[1].Value, text: true);
        if (look is { Bold: null, Italic: null, Underline: null, Size: null, Colour: null, Fill: null, Align: null })
            throw new InvalidOperationException($"{newText.Trim()}: say how it should look: {DocumentLook.Words}.");
        var format = slides ? SlideFormat : WordFormat;

        // Every heading at once ("headings", "all the slide titles"): asked to make a report's headings dark blue,
        // Haiku and Sonnet rewrote its XML rather than give each heading as old_text (2026-11-13).
        if (AllHeadings().IsMatch(oldText.Trim()))
        {
            var headings = parts.Where(p => slides ? p.Key.Contains("/slides/", StringComparison.Ordinal) : p.Key == "word/document.xml")
                .SelectMany(p => p.Value.Descendants(format.Paragraph).Select(q => (Part: p.Key, Paragraph: q)))
                .Where(h => slides
                    ? h.Paragraph.Ancestors(P + "sp").FirstOrDefault()?.Descendants(P + "ph").Any(ph => (string?)ph.Attribute("type") is "title" or "ctrTitle") == true
                    : ((string?)h.Paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")) is { } style
                      && (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) || style.Equals("Title", StringComparison.OrdinalIgnoreCase)))
                .Where(h => format.Units(h.Paragraph).Any(u => format.UnitText(u).Trim().Length > 0)).ToList();
            if (headings.Count == 0)
                throw new InvalidOperationException(slides ? "The deck's slides have no titles to format." : "The document has no headings (paragraphs in a Heading or Title style) to format: give the text as old_text.");
            foreach (var (_, paragraph) in headings)
            {
                foreach (var unit in format.Units(paragraph).ToList())
                    format.Style(unit, look, fill: true);
                if (look.Align is { } align)
                    format.Align(paragraph, align);
            }
            return (headings.Count, headings.Select(h => h.Part).Distinct().ToList(),
                $"Formatted {(headings.Count == 1 ? "the 1 " + (slides ? "title" : "heading") : $"all {headings.Count} {(slides ? "slide titles" : "headings")}")} ({look.Said}); what they say is as it was.");
        }

        if (Rows(oldText) is { } rows && MatchRows(parts, rows) is { Count: > 0 } tables)
        {
            look = DocumentLook.Parse(line.Groups[1].Value);
            if (tables.Count > 1 && !all)
                throw new InvalidOperationException($"Those rows are in the document {tables.Count} times. Give more of the rows around them, or set replace_all to format every one.");
            foreach (var (_, matched, column) in tables)
                for (var r = 0; r < matched.Count; r++)
                    foreach (var cell in matched[r].Skip(column).Take(rows[r].Count))
                    {
                        var of = FormatOf(cell);
                        if (look.Fill is { } fill)
                            of.FillCell(cell, fill);
                        foreach (var paragraph in CellParagraphs(cell))
                        {
                            if (look.Align is { } align)
                                of.Align(paragraph, align);
                            foreach (var unit in of.Units(paragraph).ToList())
                                of.Style(unit, look, fill: false);
                        }
                    }
            return (tables.Count, tables.Select(t => t.Part).Distinct().ToList(), $"Formatted {(rows.Count == 1 ? "the row" : $"the {rows.Count} rows")} of the table ({look.Said}); what {(rows.Count == 1 ? "it says" : "they say")} is as it was.");
        }

        var stretches = parts.SelectMany(p => Stretches(p.Key, p.Value, format)).ToList();
        List<Hit> hits = [];
        foreach (var candidate in Candidates(oldText))
        {
            hits = stretches.SelectMany(s => Find(s, candidate, format)).ToList();
            if (hits.Count > 0)
                break;
        }
        if (hits.Count == 0)
            throw new InvalidOperationException("old_text was not found in the document, so there is nothing to format. Read it again with read_file and copy the text exactly " +
                "(a table's rows as read_file shows them, \"| Month | Sales |\").");
        var within = 0;
        if (hits.Count > 1 && !all)
        {
            (hits, within) = Meant(hits, format);
            if (hits.Count > 1)
                throw new InvalidOperationException(Many(hits.Count, slides, "format"));
        }
        // From the last to the first, so the places still to format stay where they were found.
        foreach (var hit in hits.OrderByDescending(h => h.Stretch.Part).ThenByDescending(h => h.Stretch.Paragraphs[0].ElementsBeforeSelf().Count()).ThenByDescending(h => h.Start))
        {
            var (startPara, startOffset) = Locate(hit.Stretch, hit.Start, format);
            var (endPara, endOffset) = Locate(hit.Stretch, hit.End, format);
            // Runs break where the text starts and ends, so whole runs take the look; the end first, which leaves the start where it was.
            var end = format.SplitAt(hit.Stretch.Paragraphs[endPara], endOffset);
            var start = format.SplitAt(hit.Stretch.Paragraphs[startPara], startOffset);
            for (var p = startPara; p <= endPara; p++)
            {
                var paragraph = hit.Stretch.Paragraphs[p];
                var units = format.Units(paragraph).ToList();
                var from = p == startPara ? (start is null ? units.Count : units.IndexOf(start)) : 0;
                var to = p == endPara ? (end is null ? units.Count : units.IndexOf(end)) : units.Count;
                for (var u = Math.Max(0, from); u < to; u++)
                    format.Style(units[u], look, fill: true);
                if (look.Align is { } align)
                    format.Align(paragraph, align);
            }
        }
        return (hits.Count, hits.Select(h => h.Stretch.Part).Distinct().ToList(),
            $"Formatted {(hits.Count > 1 ? $"{hits.Count} places" : "the text")} ({look.Said}); what {(hits.Count > 1 ? "they say" : "it says")} is as it was."
            + (within > 0 ? " " + Within(within) : ""));
    }

    /// <summary>What to say of the longer lines left alone when the one line that is just the text was taken (<see cref="Meant"/>).</summary>
    public static string Within(int places) =>
        $"That is the line that is just this text; the same words are in {(places == 1 ? "1 other place, which is" : $"{places} other places, which are")} as before (replace_all takes those too).";

    /// <summary><paramref name="element"/> put into <paramref name="parent"/> where the order of its kind has it, in place of one of its name.</summary>
    private static void Ordered(XElement parent, XElement element, string[] order)
    {
        parent.Elements(element.Name).Remove();
        var at = Array.IndexOf(order, element.Name.LocalName);
        if (parent.Elements().FirstOrDefault(e => Array.IndexOf(order, e.Name.LocalName) > at) is { } later)
            later.AddBeforeSelf(element);
        else
            parent.Add(element);
    }

    // The order Word wants a run's, a paragraph's and a cell's properties in.
    private static readonly string[] RunOrder =
    [
        "rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline", "shadow", "emboss", "imprint", "noProof", "snapToGrid",
        "vanish", "webHidden", "color", "spacing", "w", "kern", "position", "sz", "szCs", "highlight", "u", "effect", "bdr", "shd", "fitText", "vertAlign", "rtl",
        "cs", "em", "lang", "eastAsianLayout", "specVanish", "oMath", "rPrChange",
    ];

    private static readonly string[] ParagraphOrder =
    [
        "pStyle", "keepNext", "keepLines", "pageBreakBefore", "framePr", "widowControl", "numPr", "suppressLineNumbers", "pBdr", "shd", "tabs", "suppressAutoHyphens",
        "kinsoku", "wordWrap", "overflowPunct", "topLinePunct", "autoSpaceDE", "autoSpaceDN", "bidi", "adjustRightInd", "snapToGrid", "spacing", "ind",
        "contextualSpacing", "mirrorIndents", "suppressOverlap", "jc", "textDirection", "textAlignment", "textboxTightWrap", "outlineLvl", "divId", "cnfStyle", "rPr",
        "sectPr", "pPrChange",
    ];

    private static readonly string[] CellOrder =
        ["cnfStyle", "tcW", "gridSpan", "hMerge", "vMerge", "tcBorders", "shd", "noWrap", "tcMar", "textDirection", "tcFitText", "vAlign", "hideMark", "tcPrChange"];

    private static void StyleWord(XElement unit, DocumentLook look, bool fill)
    {
        // A run, on its own or inside a link; a field's or a break's look is its run's.
        if (unit.Name != W + "r")
            return;
        var properties = unit.Element(W + "rPr");
        if (properties is null)
            unit.AddFirst(properties = new XElement(W + "rPr"));
        XElement Flag(string name, bool on) => new(W + name, on ? null : new XAttribute(W + "val", "0"));
        if (look.Bold is { } bold)
        {
            Ordered(properties, Flag("b", bold), RunOrder);
            Ordered(properties, Flag("bCs", bold), RunOrder);
        }
        if (look.Italic is { } italic)
        {
            Ordered(properties, Flag("i", italic), RunOrder);
            Ordered(properties, Flag("iCs", italic), RunOrder);
        }
        if (look.Underline is { } underline)
            Ordered(properties, new XElement(W + "u", new XAttribute(W + "val", underline ? "single" : "none")), RunOrder);
        if (look.Colour is { } colour)
            Ordered(properties, new XElement(W + "color", new XAttribute(W + "val", colour)), RunOrder);
        if (look.Size is { } size)
        {
            // Half points, as Word counts them.
            var half = (int)Math.Round(size * 2);
            Ordered(properties, new XElement(W + "sz", new XAttribute(W + "val", half)), RunOrder);
            Ordered(properties, new XElement(W + "szCs", new XAttribute(W + "val", half)), RunOrder);
        }
        if (fill && look.Fill is { } behind)
        {
            properties.Elements(W + "highlight").Remove();
            properties.Elements(W + "shd").Remove();
            if (behind.Length > 0)
                Ordered(properties, new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", behind)), RunOrder);
        }
    }

    private static void AlignWord(XElement paragraph, string align)
    {
        var properties = paragraph.Element(W + "pPr");
        if (properties is null)
            paragraph.AddFirst(properties = new XElement(W + "pPr"));
        Ordered(properties, new XElement(W + "jc", new XAttribute(W + "val", align)), ParagraphOrder);
    }

    private static void FillWordCell(XElement cell, string colour)
    {
        var properties = cell.Element(W + "tcPr");
        if (properties is null)
            cell.AddFirst(properties = new XElement(W + "tcPr"));
        properties.Elements(W + "shd").Remove();
        if (colour.Length > 0)
            Ordered(properties, new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", colour)), CellOrder);
    }

    /// <summary>The fills a slide's run or cell can have, one at a time.</summary>
    private static readonly string[] SlideFills = ["noFill", "solidFill", "gradFill", "blipFill", "pattFill", "grpFill"];

    private static XElement SlideColour(string name, string colour) => new(A + name, new XElement(A + "srgbClr", new XAttribute("val", colour)));

    private static void StyleSlide(XElement unit, DocumentLook look, bool fill)
    {
        if (unit.Name != A + "r" && unit.Name != A + "fld")
            return;
        var properties = unit.Element(A + "rPr");
        if (properties is null)
            unit.AddFirst(properties = new XElement(A + "rPr", new XAttribute("lang", "en-US")));
        if (look.Bold is { } bold)
            properties.SetAttributeValue("b", bold ? 1 : 0);
        if (look.Italic is { } italic)
            properties.SetAttributeValue("i", italic ? 1 : 0);
        if (look.Underline is { } underline)
            properties.SetAttributeValue("u", underline ? "sng" : "none");
        if (look.Size is { } size)
            // Hundredths of a point, as a slide counts them.
            properties.SetAttributeValue("sz", (int)Math.Round(size * 100));
        if (look.Colour is { } colour)
        {
            properties.Elements().Where(e => SlideFills.Contains(e.Name.LocalName)).Remove();
            // After the outline, before everything else of a run's look.
            if (properties.Element(A + "ln") is { } outline) outline.AddAfterSelf(SlideColour("solidFill", colour));
            else properties.AddFirst(SlideColour("solidFill", colour));
        }
        if (fill && look.Fill is { } behind)
        {
            properties.Elements(A + "highlight").Remove();
            if (behind.Length > 0)
            {
                var highlight = SlideColour("highlight", behind);
                if (properties.Elements().LastOrDefault(e => e.Name.LocalName is "ln" or "effectLst" or "effectDag" || SlideFills.Contains(e.Name.LocalName)) is { } before) before.AddAfterSelf(highlight);
                else properties.AddFirst(highlight);
            }
        }
    }

    private static void AlignSlide(XElement paragraph, string align)
    {
        var properties = paragraph.Element(A + "pPr");
        if (properties is null)
            paragraph.AddFirst(properties = new XElement(A + "pPr"));
        properties.SetAttributeValue("algn", align switch { "center" => "ctr", "right" => "r", _ => "l" });
    }

    private static void FillSlideCell(XElement cell, string colour)
    {
        var properties = cell.Element(A + "tcPr");
        if (properties is null)
            cell.Add(properties = new XElement(A + "tcPr"));
        properties.Elements().Where(e => SlideFills.Contains(e.Name.LocalName)).Remove();
        if (colour.Length == 0)
            return;
        // After the cell's lines, before anything else of it.
        if (properties.Elements().LastOrDefault(e => e.Name.LocalName.StartsWith("ln", StringComparison.Ordinal) || e.Name.LocalName == "cell3D") is { } lines) lines.AddAfterSelf(SlideColour("solidFill", colour));
        else properties.AddFirst(SlideColour("solidFill", colour));
    }

    // "headings", "all headings", "the headings", "every heading", "all slide titles", "the titles"
    [GeneratedRegex(@"^(?:all\s+(?:of\s+)?(?:the\s+)?|every\s+|each\s+|the\s+)?(?:slide\s+)?(?:headings?|titles?|section\s+headings?)(?:\s+and\s+(?:the\s+)?(?:title|headings?))?$", RegexOptions.IgnoreCase)]
    private static partial Regex AllHeadings();

    // "format: centered, dark blue", "Format: bold, size 20"
    [GeneratedRegex(@"^format\s*:\s*([^\n]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LookLine();
}
