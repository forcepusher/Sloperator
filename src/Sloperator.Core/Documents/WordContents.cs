using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace Sloperator.Core.Documents;

/// <summary>
/// A table of contents in a Word document, as edit_file's line "table of contents" (old_text empty) puts it after the
/// title: Word's own TOC field, its entries the document's headings (Heading 1 to 3), each a link to its heading and its
/// page number. The page numbers need the pages laid out: the document is drawn as a PDF through Office or LibreOffice
/// (the converter capture_result draws with) and each heading's page is the first with a line that is just its text.
/// A field marked for Word to work out on opening made Word ask "update the fields?" each time it opened the file, so
/// the numbers are written in. Asked for one after the title (2026-11-13), Haiku wrote a plain list of the headings and
/// Sonnet wrote the field's XML by hand through PowerShell.
/// </summary>
internal static partial class WordContents
{
    private static readonly XNamespace W = DocxWriter.W;

    /// <summary>A heading listed: the bookmark its entry links to, its text and its level (1 to 3).</summary>
    internal sealed record Entry(string Bookmark, string Text, int Level);

    /// <summary>Whether new_text is the line that asks for a table of contents.</summary>
    public static bool Asks(string newText) => Line().IsMatch(newText.Trim().TrimEnd('.'));

    /// <summary>
    /// The table of contents put into <paramref name="document"/>, after its title (its first paragraph, in the Title
    /// style or the first heading) or at its start; the headings it lists.
    /// </summary>
    public static List<Entry> Insert(XDocument document, PartSet parts)
    {
        var body = document.Root?.Element(W + "body") ?? throw new InvalidOperationException("The document has no body.");
        if (document.Descendants(W + "instrText").Any(t => t.Value.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase))
            || document.Descendants(W + "fldSimple").Any(f => ((string?)f.Attribute(W + "instr"))?.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase) == true))
            throw new InvalidOperationException("The document has a table of contents already: Word brings it up to date with Update Field (F9 on it).");
        var styles = parts.Exists("word/styles.xml") ? parts.Get("word/styles.xml").Root : null;
        bool Has(string id) => styles?.Elements(W + "style").Any(s => (string?)s.Attribute(W + "styleId") == id) == true;
        static string Text(XElement p) => string.Concat(p.Descendants(W + "t").Select(t => t.Value)).Trim();
        static string Style(XElement p) => (string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "";
        static int LevelOf(XElement p) => Style(p) is var s && s.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(s[7..], out var l) ? l : 0;

        var paragraphs = body.Elements(W + "p").ToList();
        var first = paragraphs.FirstOrDefault(p => Text(p).Length > 0);
        // The title: in the Title style, or the one Heading 1 above sections in Heading 2.
        var title = first is not null && (Style(first).Equals("Title", StringComparison.OrdinalIgnoreCase) || LevelOf(first) == 1 && paragraphs.Count(p => LevelOf(p) == 1) == 1) ? first : null;
        // A title in Heading 1 is no entry: the field lists levels 2 and 3, so Word's own update leaves it out too.
        var top = title is not null && LevelOf(title) == 1 ? 2 : 1;
        var headings = paragraphs.Where(p => p != title && LevelOf(p) >= top && LevelOf(p) <= 3 && Text(p).Length > 0).ToList();
        if (headings.Count == 0)
            throw new InvalidOperationException("The document has no headings (paragraphs in the Heading 1, 2 or 3 style) to list: make its section titles headings first, as \"# \" lines do.");

        // Each heading marked, for its entry to link to and Word to find again when it updates the table.
        var id = document.Descendants(W + "bookmarkStart").Select(b => int.TryParse((string?)b.Attribute(W + "id"), out var v) ? v : 0).DefaultIfEmpty(0).Max() + 1;
        var entries = new List<Entry>();
        foreach (var heading in headings)
        {
            var name = $"_Toc{(900000000 + id).ToString(CultureInfo.InvariantCulture)}";
            var start = new XElement(W + "bookmarkStart", new XAttribute(W + "id", id), new XAttribute(W + "name", name));
            if (heading.Element(W + "pPr") is { } pPr) pPr.AddAfterSelf(start);
            else heading.AddFirst(start);
            heading.Add(new XElement(W + "bookmarkEnd", new XAttribute(W + "id", id)));
            entries.Add(new Entry(name, Text(heading), LevelOf(heading)));
            id++;
        }

        // The text's width, for the page numbers' tab at the right edge.
        var section = body.Element(W + "sectPr");
        static int Twips(XElement? e, string name, int otherwise) => int.TryParse((string?)e?.Attribute(W + name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : otherwise;
        var width = Math.Max(1440, Twips(section?.Element(W + "pgSz"), "w", 12240) - Twips(section?.Element(W + "pgMar"), "left", 1440) - Twips(section?.Element(W + "pgMar"), "right", 1440));

        static XElement Run(params object[] content) => new(W + "r", content);
        static XElement Field(string type) => Run(new XElement(W + "fldChar", new XAttribute(W + "fldCharType", type)));
        var made = new List<XElement>
        {
            Has("TOCHeading")
                ? new XElement(W + "p", new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", "TOCHeading"))), Run(new XElement(W + "t", "Contents")))
                : new XElement(W + "p", new XElement(W + "pPr", new XElement(W + "spacing", new XAttribute(W + "before", 240), new XAttribute(W + "after", 120))),
                    Run(new XElement(W + "rPr", new XElement(W + "b"), new XElement(W + "sz", new XAttribute(W + "val", 32))), new XElement(W + "t", "Contents"))),
        };
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var style = $"TOC{entry.Level}";
            var p = new XElement(W + "p", new XElement(W + "pPr",
                Has(style) ? new XElement(W + "pStyle", new XAttribute(W + "val", style)) : null,
                new XElement(W + "tabs", new XElement(W + "tab", new XAttribute(W + "val", "right"), new XAttribute(W + "leader", "dot"), new XAttribute(W + "pos", width))),
                Has(style) ? null : new XElement(W + "ind", new XAttribute(W + "left", (entry.Level - 1) * 220))));
            if (i == 0)
                p.Add(Field("begin"), Run(new XElement(W + "instrText", new XAttribute(XNamespace.Xml + "space", "preserve"), $" TOC \\o \"{top}-3\" \\h \\z \\u ")), Field("separate"));
            p.Add(new XElement(W + "hyperlink", new XAttribute(W + "anchor", entry.Bookmark), new XAttribute(W + "history", 1),
                Run(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), entry.Text)),
                Run(new XElement(W + "tab")),
                Field("begin"), Run(new XElement(W + "instrText", new XAttribute(XNamespace.Xml + "space", "preserve"), $" PAGEREF {entry.Bookmark} \\h ")), Field("separate"),
                Run(new XElement(W + "t", "")), Field("end")));
            if (i == entries.Count - 1)
                p.Add(Field("end"));
            made.Add(p);
        }
        if (title is not null) title.AddAfterSelf(made);
        else body.AddFirst(made);
        return entries;
    }

    /// <summary>
    /// The page of each entry's heading in a PDF of the document: the first page with a line that is just the
    /// heading's text (a contents line has dots and its number after it). Headings not found are left out.
    /// </summary>
    public static Dictionary<string, int> Pages(string pdf, IReadOnlyList<Entry> entries)
    {
        static string Plain(string text) => Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();
        var lines = new List<(int Page, string Text)>();
        using (var document = PdfDocument.Open(pdf))
            foreach (var page in document.GetPages())
                foreach (var line in page.GetWords().GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3)).OrderByDescending(g => g.Key))
                    lines.Add((page.Number, Plain(string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)))));
        var pages = new Dictionary<string, int>();
        var from = 0;
        foreach (var entry in entries)
        {
            var wanted = Plain(entry.Text);
            // In order, each after the one before: the same words as a later heading's are not taken for it.
            var at = lines.FindIndex(from, l => l.Text == wanted);
            if (at < 0)
                continue;
            pages[entry.Bookmark] = lines[at].Page;
            from = at + 1;
        }
        return pages;
    }

    /// <summary>The page numbers written as the contents' PAGEREF results; how many were.</summary>
    public static int Number(XDocument document, IReadOnlyDictionary<string, int> pages)
    {
        var numbered = 0;
        foreach (var instruction in document.Descendants(W + "instrText").ToList())
        {
            if (Regex.Match(instruction.Value, @"^\s*PAGEREF\s+(\S+)") is not { Success: true } m || !pages.TryGetValue(m.Groups[1].Value, out var page))
                continue;
            // The run after the field's separator holds what it shows.
            var result = instruction.Parent!.ElementsAfterSelf(W + "r").SkipWhile(r => (string?)r.Element(W + "fldChar")?.Attribute(W + "fldCharType") != "separate").Skip(1).FirstOrDefault();
            if (result?.Element(W + "t") is { } t)
            {
                t.Value = page.ToString(CultureInfo.InvariantCulture);
                numbered++;
            }
        }
        return numbered;
    }

    // "table of contents", "add a table of contents after the title", "contents at the start", "TOC"
    [GeneratedRegex(@"^(?:add|insert|put|make)?\s*(?:an?\s+|the\s+)?(?:table\s+of\s+contents|contents(?:\s+page)?|toc)(?:\s+(?:after|below|under)\s+the\s+title|\s+at\s+the\s+(?:start|top|beginning))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Line();
}
