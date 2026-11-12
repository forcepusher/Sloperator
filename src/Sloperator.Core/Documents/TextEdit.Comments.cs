using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sloperator.Core.Documents;

internal static partial class TextEdit
{
    // ---- comments: new_text "comment: check the date with the council" ---------------------------------------------
    // Asked to put a comment on a word of a Word document (2026-11-13), Haiku and Sonnet wrote comments.xml, its
    // relationship and the comment's marks into document.xml through PowerShell.

    /// <summary>
    /// A comment in the margin on the text given as old_text, as Word's New Comment puts one: the text marked from
    /// start to end and the comment kept in comments.xml, by Sloperator. Null when new_text is no "comment: …" line.
    /// </summary>
    public static string? Comment(PartSet package, Dictionary<string, XDocument> parts, string oldText, string newText)
    {
        if (CommentLine().Match(newText.Trim()) is not { Success: true } line)
            return null;
        var said = line.Groups[1].Value.Trim().Trim('"', '“', '”');
        if (said.Length == 0)
            throw new InvalidOperationException("Say what the comment says: new_text \"comment: check the date with the council\".");
        if (!parts.TryGetValue("word/document.xml", out var main))
            throw new InvalidOperationException("Comments go in a Word document's text.");
        var format = WordFormat;
        var stretches = Stretches("word/document.xml", main, format).ToList();
        List<Hit> hits = [];
        foreach (var candidate in Candidates(oldText))
        {
            hits = stretches.SelectMany(s => Find(s, candidate, format)).ToList();
            if (hits.Count > 0)
                break;
        }
        if (hits.Count == 0)
            throw new InvalidOperationException("old_text was not found in the document, so there is nothing to comment on. Read it again with read_file and copy the words exactly.");
        if (hits.Count > 1)
            (hits, _) = Meant(hits, format);
        if (hits.Count > 1)
            throw new InvalidOperationException($"old_text occurs {hits.Count} times. Include more of the text around it, so the comment is on one place.");
        var hit = hits[0];

        // The comments' part: there already, or made with its relationship and content type.
        const string part = "word/comments.xml";
        XDocument comments;
        if (package.Exists(part))
            comments = package.Get(part);
        else
        {
            comments = new XDocument(new XElement(W + "comments", new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName)));
            package.Add(part, comments);
            var rels = package.Get("word/_rels/document.xml.rels");
            XNamespace rel = OfficePackage.Rel;
            var taken = rels.Root!.Elements(rel + "Relationship").Select(r => (string?)r.Attribute("Id")).ToHashSet();
            var rid = Enumerable.Range(1, int.MaxValue).Select(n => $"rId{n}").First(i => !taken.Contains(i));
            rels.Root.Add(new XElement(rel + "Relationship", new XAttribute("Id", rid),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments"), new XAttribute("Target", "comments.xml")));
            package.Changed("word/_rels/document.xml.rels");
            XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
            var contentTypes = package.Get("[Content_Types].xml");
            contentTypes.Root!.Add(new XElement(types + "Override", new XAttribute("PartName", "/word/comments.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml")));
            package.Changed("[Content_Types].xml");
        }
        var id = comments.Root!.Elements(W + "comment").Select(c => (int?)c.Attribute(W + "id") ?? -1)
            .Concat(main.Descendants(W + "commentRangeStart").Select(c => (int?)c.Attribute(W + "id") ?? -1)).DefaultIfEmpty(-1).Max() + 1;
        comments.Root.Add(new XElement(W + "comment", new XAttribute(W + "id", id), new XAttribute(W + "author", "Sloperator"),
            new XAttribute(W + "date", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)), new XAttribute(W + "initials", "S"),
            new XElement(W + "p", new XElement(W + "r", new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), said)))));
        package.Changed(part);

        // The marks around the words, the end first so the start stays where it was found.
        var (startPara, startOffset) = Locate(hit.Stretch, hit.Start, format);
        var (endPara, endOffset) = Locate(hit.Stretch, hit.End, format);
        var endParagraph = hit.Stretch.Paragraphs[endPara];
        var startParagraph = hit.Stretch.Paragraphs[startPara];
        var end = format.SplitAt(endParagraph, endOffset);
        var start = format.SplitAt(startParagraph, startOffset);
        var rangeEnd = new XElement(W + "commentRangeEnd", new XAttribute(W + "id", id));
        var reference = new XElement(W + "r", new XElement(W + "commentReference", new XAttribute(W + "id", id)));
        if (end is not null) TopOf(end, endParagraph).AddBeforeSelf(rangeEnd, reference);
        else endParagraph.Add(rangeEnd, reference);
        var rangeStart = new XElement(W + "commentRangeStart", new XAttribute(W + "id", id));
        if (start is not null) TopOf(start, startParagraph).AddBeforeSelf(rangeStart);
        else rangeEnd.AddBeforeSelf(rangeStart);
        package.Changed("word/document.xml");
        return $"Put a comment on \"{(oldText.Length > 60 ? oldText[..60] + "…" : oldText).Replace("\n", " ")}\": \"{said}\", by Sloperator.";
    }

    /// <summary>The element of <paramref name="paragraph"/> that holds <paramref name="unit"/> (a run in a link is the link's place).</summary>
    private static XElement TopOf(XElement unit, XElement paragraph) => unit.AncestorsAndSelf().First(e => e.Parent == paragraph);

    // "comment: check the date with the council", "Comment: …", "add a comment: …"
    [GeneratedRegex(@"^(?:add\s+(?:a\s+)?)?comment\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CommentLine();
}
