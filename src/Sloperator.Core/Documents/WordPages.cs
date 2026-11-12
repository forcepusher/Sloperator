using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sloperator.Core.Documents;

/// <summary>
/// edit_file's lines for what is on every page of a Word document: "page numbers at the bottom", "footer left:
/// Confidential", "header right: Harbour Cycles", "footer right: Page {page} of {pages}", "no footer". A header or
/// footer is one line in three places, left, centre and right, as Word's own are laid out (tab stops at the middle
/// and the right edge of the text); a line changes its place and leaves the others as they were. Asked for page
/// numbers at the bottom and then a footer saying Confidential on the left (2026-11-13), Haiku and Sonnet unzipped
/// the document and wrote footer1.xml, its relationship and the section's reference through PowerShell, Haiku by way
/// of a script left in Sloperator's folder, Sonnet leaving a copy of the file beside it.
/// </summary>
internal static partial class WordPages
{
    private static readonly XNamespace W = DocxWriter.W;
    private static readonly XNamespace R = OfficePackage.R;
    private static readonly XNamespace Rel = OfficePackage.Rel;
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string Main = "word/document.xml";

    private sealed record PageLine(bool Header, int Place, List<XElement>? Content, bool Remove);

    /// <summary>The change new_text's lines ask for, made, and what was done; null when not every line is one of them.</summary>
    public static string? Edit(PartSet parts, string newText)
    {
        var lines = new List<PageLine>();
        var others = new List<string>();
        string? first = null;
        foreach (var raw in newText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim().TrimEnd('.');
            if (line.Length == 0)
                continue;
            if (Of(line) is { } page)
            {
                lines.Add(page);
                first ??= line;
            }
            else
                others.Add(line);
        }
        if (lines.Count == 0 || !parts.Exists(Main))
            return null;
        // Mixed with other lines, those went at the end of the document as text, the page lines with them: Haiku gave
        // "page numbers at the bottom" and "headings: format: dark blue" together and had to take both out again (2026-11-13).
        if (others.Count > 0)
            throw new InvalidOperationException($"\"{first}\" sets every page's header or footer, and \"{others[0]}\" is no such line: give them in edits of their own. " +
                (others[0].Contains("format:", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(others[0], @"^(?:all\s+)?(?:the\s+)?headings?\b", RegexOptions.IgnoreCase)
                    ? "To change how text looks, old_text is the text (\"headings\" for every heading) and new_text \"format: dark blue\"."
                    : "Text to add at the end of the document goes in an edit by itself.") + " Nothing was changed.");
        var done = new List<string>();
        foreach (var group in lines.GroupBy(l => l.Header))
        {
            var kind = group.Key ? "header" : "footer";
            if (group.Any(l => l.Remove))
            {
                done.Add(RemoveAll(parts, group.Key) ? $"took the {kind} off every page" : $"there was no {kind} to take off");
                continue;
            }
            var (part, zones, replaced) = Open(parts, group.Key);
            foreach (var line in group)
                zones[line.Place] = line.Content!;
            Save(parts, part, group.Key, zones);
            done.Add($"set the {kind} of every page to: {Said(zones)}" + (replaced ? $" (the {kind} had more than one line, and is this one line now)" : ""));
        }
        var said = string.Join("; ", done);
        return char.ToUpperInvariant(said[0]) + said[1..] + ".";
    }

    /// <summary>One line of new_text as a header or footer change, or null.</summary>
    private static PageLine? Of(string line)
    {
        if (Removal().Match(line) is { Success: true } removal)
            return new PageLine(removal.Groups["part"].Value.StartsWith("head", StringComparison.OrdinalIgnoreCase), 0, null, true);
        if (Numbers().Match(line) is { Success: true } numbers)
        {
            var top = numbers.Groups["where"].Value.StartsWith("t", StringComparison.OrdinalIgnoreCase) || numbers.Groups["where"].Value.StartsWith("head", StringComparison.OrdinalIgnoreCase);
            var content = numbers.Groups["of"].Success ? Content("Page {page} of {pages}") : Content("{page}");
            return new PageLine(top, PlaceOf(numbers.Groups["side"].Value, 1), content, false);
        }
        if (Text().Match(line) is { Success: true } text)
        {
            var side = text.Groups["side"].Success ? text.Groups["side"].Value : text.Groups["side2"].Value;
            var words = text.Groups["text"].Value.Trim().Trim('"', '“', '”');
            return new PageLine(text.Groups["part"].Value.StartsWith("head", StringComparison.OrdinalIgnoreCase), PlaceOf(side, 0), Content(words), false);
        }
        return null;
    }

    private static int PlaceOf(string side, int otherwise) => side.ToLowerInvariant() switch
    {
        "left" => 0,
        "center" or "centre" or "middle" or "centered" or "centred" => 1,
        "right" => 2,
        _ => otherwise,
    };

    /// <summary>Runs for text with {page} and {pages} in it as Word's fields for the page's number and the count of pages.</summary>
    private static List<XElement> Content(string text)
    {
        var runs = new List<XElement>();
        foreach (var piece in Regex.Split(text, @"(\{page\}|\{pages\})", RegexOptions.IgnoreCase))
        {
            if (piece.Length == 0)
                continue;
            if (piece.Equals("{page}", StringComparison.OrdinalIgnoreCase) || piece.Equals("{pages}", StringComparison.OrdinalIgnoreCase))
                runs.Add(new XElement(W + "fldSimple", new XAttribute(W + "instr", piece.Equals("{page}", StringComparison.OrdinalIgnoreCase) ? " PAGE " : " NUMPAGES "),
                    new XElement(W + "r", new XElement(W + "t", "1"))));
            else
                runs.Add(new XElement(W + "r", new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), piece)));
        }
        return runs;
    }

    /// <summary>A zone's content as read_file shows it: its text with {page} and {pages} for the fields.</summary>
    private static string Shown(List<XElement> zone) => string.Concat(zone.Select(e =>
        e.Name == W + "fldSimple" ? (((string?)e.Attribute(W + "instr"))?.Trim().StartsWith("NUMPAGES", StringComparison.OrdinalIgnoreCase) == true ? "{pages}" : "{page}")
        : string.Concat(e.Descendants(W + "t").Select(t => t.Value))));

    private static string Said(List<XElement>[] zones)
    {
        var places = new[] { "left", "centre", "right" };
        var said = zones.Select((z, i) => z.Count == 0 ? null : $"\"{Shown(z)}\" on the {places[i]}").OfType<string>().ToList();
        return said.Count == 0 ? "nothing" : string.Join(", ", said);
    }

    /// <summary>
    /// The default header or footer of the document's sections: its part, and what is on its left, centre and right;
    /// a new part when there is none. One that isn't one line in those places is taken whole as its left (replaced).
    /// </summary>
    private static (string Part, List<XElement>[] Zones, bool Replaced) Open(PartSet parts, bool header)
    {
        var zones = new[] { new List<XElement>(), new List<XElement>(), new List<XElement>() };
        var document = parts.Get(Main);
        var kind = header ? "headerReference" : "footerReference";
        var rels = Relations(parts);
        foreach (var reference in document.Descendants(W + kind).Where(r => (string?)r.Attribute(W + "type") is null or "default"))
        {
            if ((string?)reference.Attribute(R + "id") is not { } id || !rels.TryGetValue(id, out var target) || !parts.Exists(target))
                continue;
            var paragraphs = parts.Get(target).Root!.Elements(W + "p").ToList();
            var filled = paragraphs.Where(p => p.Descendants(W + "t").Any(t => t.Value.Trim().Length > 0) || p.Descendants().Any(e => e.Name == W + "fldSimple" || e.Name == W + "instrText")).ToList();
            if (filled.Count > 1 || paragraphs.Any(p => p.Descendants(W + "drawing").Any() || p.Descendants(W + "pict").Any()) || parts.Get(target).Root!.Elements(W + "tbl").Any())
                return (target, zones, true);
            if (filled.Count == 1)
            {
                var p = filled[0];
                var content = p.Elements().Where(e => e.Name != W + "pPr").ToList();
                var tabs = content.Count(e => e.Name == W + "r" && e.Elements().Any(c => c.Name == W + "tab"));
                // No tab: the place its alignment puts it in.
                var place = tabs == 0 ? PlaceOf(((string?)p.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val")) switch { "center" => "center", "right" or "end" => "right", _ => "left" }, 0) : 0;
                foreach (var e in content)
                {
                    if (e.Name == W + "r" && e.Elements().Any(c => c.Name == W + "tab") && !e.Elements(W + "t").Any())
                    {
                        place = Math.Min(2, place + e.Elements(W + "tab").Count());
                        continue;
                    }
                    if (e.Name == W + "r" || e.Name == W + "fldSimple" || e.Name == W + "hyperlink" || e.Name == W + "sdt")
                        zones[place].Add(new XElement(e));
                }
                return (target, Complex(zones), false);
            }
            return (target, zones, false);
        }
        return ("", zones, false);
    }

    /// <summary>A field written as Word writes it, in runs (begin, instruction, separate, result, end), made one simple field.</summary>
    private static List<XElement>[] Complex(List<XElement>[] zones)
    {
        foreach (var zone in zones)
        {
            for (var i = 0; i < zone.Count; i++)
            {
                if (zone[i].Element(W + "fldChar") is not { } begin || (string?)begin.Attribute(W + "fldCharType") != "begin")
                    continue;
                var end = zone.FindIndex(i, e => (string?)e.Element(W + "fldChar")?.Attribute(W + "fldCharType") == "end");
                if (end < 0)
                    break;
                var instruction = string.Concat(zone.Skip(i).Take(end - i + 1).SelectMany(e => e.Elements(W + "instrText")).Select(t => t.Value));
                var result = zone.Skip(i).Take(end - i + 1).SkipWhile(e => (string?)e.Element(W + "fldChar")?.Attribute(W + "fldCharType") != "separate").Skip(1)
                    .Where(e => e.Element(W + "fldChar") is null).Select(e => new XElement(e)).ToList();
                zone.RemoveRange(i, end - i + 1);
                zone.Insert(i, new XElement(W + "fldSimple", new XAttribute(W + "instr", instruction), result.Count > 0 ? result : [new XElement(W + "r", new XElement(W + "t", "1"))]));
            }
        }
        return zones;
    }

    /// <summary>The header or footer written as one line in its three places, referred to by every section as its default.</summary>
    private static void Save(PartSet parts, string part, bool header, List<XElement>[] zones)
    {
        var document = parts.Get(Main);
        var body = document.Root!.Element(W + "body") ?? throw new InvalidOperationException("The document has no body.");
        // The text's width, from the last section's page and margins: the tab stops of the middle and the right edge.
        var section = body.Element(W + "sectPr");
        static int Twips(XElement? e, string name, int otherwise) => int.TryParse((string?)e?.Attribute(W + name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : otherwise;
        var width = Twips(section?.Element(W + "pgSz"), "w", 12240) - Twips(section?.Element(W + "pgMar"), "left", 1440) - Twips(section?.Element(W + "pgMar"), "right", 1440);
        width = Math.Max(1440, width);
        var styleId = header ? "Header" : "Footer";
        var styled = parts.Exists("word/styles.xml") && parts.Get("word/styles.xml").Root!.Elements(W + "style").Any(s => (string?)s.Attribute(W + "styleId") == styleId);
        var paragraph = new XElement(W + "p", new XElement(W + "pPr",
            styled ? new XElement(W + "pStyle", new XAttribute(W + "val", styleId)) : null,
            new XElement(W + "tabs", new XElement(W + "tab", new XAttribute(W + "val", "center"), new XAttribute(W + "pos", width / 2)),
                new XElement(W + "tab", new XAttribute(W + "val", "right"), new XAttribute(W + "pos", width)))));
        var last = Array.FindLastIndex(zones, z => z.Count > 0);
        for (var i = 0; i <= last; i++)
        {
            if (i > 0)
                paragraph.Add(new XElement(W + "r", new XElement(W + "tab")));
            paragraph.Add(zones[i]);
        }
        var root = new XElement(W + (header ? "hdr" : "ftr"), new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName), new XAttribute(XNamespace.Xmlns + "r", R.NamespaceName), paragraph);
        if (part.Length == 0)
        {
            var name = Enumerable.Range(1, 1000).Select(n => $"word/{(header ? "header" : "footer")}{n}.xml").First(n => !parts.Exists(n));
            parts.Add(name, new XDocument(root));
            var types = parts.Get("[Content_Types].xml");
            types.Root!.Add(new XElement(Types + "Override", new XAttribute("PartName", "/" + name),
                new XAttribute("ContentType", $"application/vnd.openxmlformats-officedocument.wordprocessingml.{(header ? "header" : "footer")}+xml")));
            parts.Changed("[Content_Types].xml");
            var rels = parts.Get("word/_rels/document.xml.rels");
            var taken = rels.Root!.Elements(Rel + "Relationship").Select(r => (string?)r.Attribute("Id")).ToHashSet();
            var id = Enumerable.Range(1, int.MaxValue).Select(n => $"rId{n}").First(i => !taken.Contains(i));
            rels.Root.Add(new XElement(Rel + "Relationship", new XAttribute("Id", id),
                new XAttribute("Type", $"http://schemas.openxmlformats.org/officeDocument/2006/relationships/{(header ? "header" : "footer")}"), new XAttribute("Target", name["word/".Length..])));
            parts.Changed("word/_rels/document.xml.rels");
            // Every section: its own default one goes, this one is its default.
            var kind = header ? "headerReference" : "footerReference";
            var sections = document.Descendants(W + "sectPr").ToList();
            if (sections.Count == 0)
                body.Add(section = new XElement(W + "sectPr"));
            foreach (var s in document.Descendants(W + "sectPr").ToList())
            {
                s.Elements(W + kind).Where(r => (string?)r.Attribute(W + "type") is null or "default").Remove();
                var reference = new XElement(W + kind, new XAttribute(W + "type", "default"), new XAttribute(R + "id", id));
                // Headers before footers, both first in a section.
                if (s.Elements().LastOrDefault(e => e.Name == W + "headerReference" || header == false && e.Name == W + "footerReference") is { } before) before.AddAfterSelf(reference);
                else s.AddFirst(reference);
            }
            if (document.Root.GetPrefixOfNamespace(R) is null)
                document.Root.SetAttributeValue(XNamespace.Xmlns + "r", R.NamespaceName);
            parts.Changed(Main);
        }
        else
        {
            var existing = parts.Get(part).Root!;
            existing.RemoveNodes();
            existing.Add(paragraph);
            parts.Changed(part);
        }
    }

    /// <summary>The document's default headers or footers taken off its sections; false when there were none.</summary>
    private static bool RemoveAll(PartSet parts, bool header)
    {
        var document = parts.Get(Main);
        var references = document.Descendants(W + (header ? "headerReference" : "footerReference")).ToList();
        if (references.Count == 0)
            return false;
        var ids = references.Select(r => (string?)r.Attribute(R + "id")).OfType<string>().Distinct().ToList();
        references.Remove();
        parts.Changed(Main);
        // The parts go with them, so nothing reads them as the document's any more.
        var rels = Relations(parts);
        var relsDoc = parts.Get("word/_rels/document.xml.rels");
        var types = parts.Get("[Content_Types].xml");
        foreach (var id in ids)
        {
            if (!rels.TryGetValue(id, out var target))
                continue;
            parts.Delete(target);
            relsDoc.Root!.Elements(Rel + "Relationship").Where(r => (string?)r.Attribute("Id") == id).Remove();
            types.Root!.Elements(Types + "Override").Where(o => string.Equals((string?)o.Attribute("PartName"), "/" + target, StringComparison.OrdinalIgnoreCase)).Remove();
            if (parts.Exists($"word/_rels/{target["word/".Length..]}.rels"))
                parts.Delete($"word/_rels/{target["word/".Length..]}.rels");
        }
        parts.Changed("word/_rels/document.xml.rels");
        parts.Changed("[Content_Types].xml");
        return true;
    }

    private static Dictionary<string, string> Relations(PartSet parts)
    {
        if (!parts.Exists("word/_rels/document.xml.rels"))
            return [];
        return parts.Get("word/_rels/document.xml.rels").Root!.Elements(Rel + "Relationship")
            .Where(r => r.Attribute("Id") is not null && r.Attribute("Target") is not null && (string?)r.Attribute("TargetMode") != "External")
            .ToDictionary(r => (string)r.Attribute("Id")!, r => PartSet.Resolve("word", (string)r.Attribute("Target")!));
    }

    // "page numbers", "add page numbers at the bottom", "page numbers bottom right", "page numbers at the top, centered",
    // "page numbers as Page X of Y"
    [GeneratedRegex(@"^(?:add\s+|put\s+|insert\s+|show\s+)?(?:the\s+)?page\s+numbers?(?:ing)?(?:\s+(?:as\s+)?(?<of>""?page\s+x\s+of\s+y""?))?(?:\s*(?::|at|in|on|to)?\s*(?:the\s+)?(?<where>bottom|footer|foot|top|header|head)(?:\s+of\s+(?:every|each|all|the)\s+pages?)?)?(?:\s*(?:-|,)?\s*(?:on\s+the\s+|at\s+the\s+|in\s+the\s+)?(?<side>left|cent(?:er|re)(?:d)?|middle|right))?(?:\s+(?:of\s+|on\s+)?(?:every|each|all)\s+pages?)?(?:\s+(?:as\s+)?(?<of>""?page\s+x\s+of\s+y""?))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Numbers();

    // "footer: Confidential", "footer left: Confidential", "header right: Harbour Cycles", "footer: Confidential, on the left"
    [GeneratedRegex(@"^(?<part>header|footer)(?:\s+(?:on\s+the\s+)?(?<side>left|cent(?:er|re)|middle|right))?\s*:\s*(?<text>.+?)(?:\s*,?\s*(?:on|at|to)\s+the\s+(?<side2>left|right|cent(?:er|re)|middle))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Text();

    // "no footer", "remove the header", "delete the footer"
    [GeneratedRegex(@"^(?:no|remove\s+the|delete\s+the|take\s+off\s+the|clear\s+the)\s+(?<part>header|footer)s?$", RegexOptions.IgnoreCase)]
    private static partial Regex Removal();
}
