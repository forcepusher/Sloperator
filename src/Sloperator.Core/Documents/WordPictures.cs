using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sloperator.Core.Documents;

/// <summary>
/// A picture put into a Word document that is there, by edit_file: new_text lines "![The chart](C:\charts\sales.png)",
/// after the paragraph given as old_text (new_text is that paragraph, then the picture lines) or, old_text empty, at the
/// end of the document. Each picture a paragraph of its own, centred, as wide as the text at most, as write_file puts
/// one in. Until then a picture in a document that was there meant writing its media part, relationship and drawing
/// by hand.
/// </summary>
internal static partial class WordPictures
{
    private static readonly XNamespace W = DocxWriter.W;
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace R = OfficePackage.R;
    private static readonly XNamespace Rel = OfficePackage.Rel;
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <summary>Whether a line of new_text is a picture: "![alt](path)".</summary>
    public static bool IsPicture(string line) => PictureLine().IsMatch(line.Trim());

    /// <summary>
    /// The pictures of new_text put in, and what was done; null when new_text has no picture line. old_text empty: at
    /// the end; else new_text is old_text with picture lines after it (or before it), and they go after (or before) the
    /// paragraph old_text ends (or starts) in.
    /// </summary>
    public static string? Edit(PartSet parts, XDocument main, string oldText, string newText, Func<string, string> resolve)
    {
        var lines = newText.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (!lines.Any(IsPicture))
            return null;
        var body = main.Root?.Element(W + "body") ?? throw new InvalidOperationException("The document has no body.");
        var old = oldText.Replace("\r\n", "\n").Trim();
        List<string> pictures;
        XElement? anchor = null;
        var before = false;
        if (old.Length == 0)
        {
            if (lines.Any(l => !IsPicture(l)))
                throw new InvalidOperationException("With old_text empty, give the picture lines by themselves (\"![The chart](C:\\charts\\sales.png)\"); text goes in an edit of its own. Nothing was changed.");
            pictures = lines;
        }
        else
        {
            var oldLines = old.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var text = lines.Where(l => !IsPicture(l)).ToList();
            if (!text.SequenceEqual(oldLines))
                throw new InvalidOperationException("To put a picture in, new_text is old_text as it is with the picture lines after it (or before it), " +
                    "\"![The chart](C:\\charts\\sales.png)\"; change the text in an edit of its own. Nothing was changed.");
            before = IsPicture(lines[0]);
            pictures = lines.Where(IsPicture).ToList();
            // The paragraph the old text ends in (or starts in, for pictures before it).
            static string Fold(string s) => Regex.Replace(s.Replace('’', '\'').Replace('“', '"').Replace('”', '"'), @"\s+", " ").Trim();
            var edge = before ? oldLines[0] : oldLines[^1];
            List<XElement> found;
            if (edge.Length >= 2 && edge[0] == '|' && edge[^1] == '|')
            {
                // A table's row as read_file shows it: the picture goes after (or before) the table.
                var cells = edge[1..^1].Split('|').Select(c => Fold(c.Replace("**", ""))).ToList();
                found = body.Descendants(W + "tbl").Where(t => t.Elements(W + "tr").Any(r =>
                    r.Elements(W + "tc").Select(c => Fold(string.Join(" ", c.Elements(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(x => x.Value)))))).SequenceEqual(cells)))
                    .Select(t => t.Ancestors(W + "tbl").LastOrDefault() ?? t).Distinct().ToList();
            }
            else
            {
                var wanted = Fold(Regex.Replace(edge, @"^(#{1,6}|[-*+•]|\d{1,3}[.)])\s+", "").Replace("**", ""));
                found = body.Descendants(W + "p").Where(p => Fold(string.Concat(p.Descendants(W + "t").Select(t => t.Value))).Contains(wanted, StringComparison.Ordinal)).ToList();
            }
            anchor = found.Count switch
            {
                1 => found[0],
                0 => throw new InvalidOperationException("old_text was not found in the document. Read it again with read_file and copy the paragraph the picture goes after. Nothing was changed."),
                _ => throw new InvalidOperationException($"old_text is in {found.Count} paragraphs: give more of it, so the picture's place is one. Nothing was changed."),
            };
        }

        // The text's width, for how wide a picture may be.
        var (width, height) = WordPages.TextArea(body);

        var rels = parts.Get("word/_rels/document.xml.rels");
        var types = parts.Get("[Content_Types].xml");
        var made = new List<XElement>();
        var said = new List<string>();
        var docPr = main.Descendants(WP + "docPr").Select(d => (int?)d.Attribute("id") ?? 0).DefaultIfEmpty(0).Max();
        foreach (var line in pictures)
        {
            var m = PictureLine().Match(line);
            var source = resolve(m.Groups["src"].Value.Trim().Trim('"', '<', '>'));
            DocPicture picture;
            try { picture = DocPicture.Load(source); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new InvalidOperationException($"The picture {m.Groups["src"].Value} can't be put in: {ex.Message}. Nothing was changed.");
            }
            var name = Enumerable.Range(1, 10000).Select(n => $"word/media/image{n}.{picture.Extension}").First(n => !parts.Exists(n));
            parts.AddRaw(name, picture.Data);
            if (!types.Root!.Elements(Types + "Default").Any(d => string.Equals((string?)d.Attribute("Extension"), picture.Extension, StringComparison.OrdinalIgnoreCase)))
                types.Root.AddFirst(new XElement(Types + "Default", new XAttribute("Extension", picture.Extension), new XAttribute("ContentType", picture.Extension == "png" ? "image/png" : "image/jpeg")));
            var taken = rels.Root!.Elements(Rel + "Relationship").Select(r => (string?)r.Attribute("Id")).ToHashSet();
            var id = Enumerable.Range(1, int.MaxValue).Select(n => $"rId{n}").First(i => !taken.Contains(i));
            rels.Root.Add(new XElement(Rel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", OfficePackage.ImageRel), new XAttribute("Target", name["word/".Length..])));
            var (w, h) = picture.Fit(width / 20.0, height / 20.0 * 0.6);
            long cx = (long)(w * 12700), cy = (long)(h * 12700);
            var alt = m.Groups["alt"].Value;
            docPr++;
            made.Add(new XElement(W + "p",
                new XElement(W + "pPr", new XElement(W + "jc", new XAttribute(W + "val", "center"))),
                new XElement(W + "r", new XElement(W + "drawing",
                    new XElement(WP + "inline", new XAttribute("distT", 0), new XAttribute("distB", 0), new XAttribute("distL", 0), new XAttribute("distR", 0),
                        new XElement(WP + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                        new XElement(WP + "docPr", new XAttribute("id", docPr), new XAttribute("name", $"Picture {docPr}"), new XAttribute("descr", alt)),
                        new XElement(WP + "cNvGraphicFramePr", new XElement(A + "graphicFrameLocks", new XAttribute("noChangeAspect", 1))),
                        new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/picture"),
                            new XElement(Pic + "pic",
                                new XElement(Pic + "nvPicPr", new XElement(Pic + "cNvPr", new XAttribute("id", 0), new XAttribute("name", Path.GetFileName(name))), new XElement(Pic + "cNvPicPr")),
                                new XElement(Pic + "blipFill", new XElement(A + "blip", new XAttribute(R + "embed", id)), new XElement(A + "stretch", new XElement(A + "fillRect"))),
                                new XElement(Pic + "spPr",
                                    new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                                    new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")))))))))));
            said.Add($"{Path.GetFileName(source)} ({(w / 72 * 2.54).ToString("0.0", CultureInfo.InvariantCulture)} × {(h / 72 * 2.54).ToString("0.0", CultureInfo.InvariantCulture)} cm)");
        }
        foreach (var ns in new[] { ("wp", WP), ("a", A), ("pic", Pic), ("r", R) })
            if (main.Root!.GetPrefixOfNamespace(ns.Item2) is null && main.Root.Attribute(XNamespace.Xmlns + ns.Item1) is null)
                main.Root.SetAttributeValue(XNamespace.Xmlns + ns.Item1, ns.Item2.NamespaceName);
        if (anchor is null)
        {
            // At the end: before the section settings and the empty paragraphs that close the body.
            var blocks = body.Elements().Where(e => e.Name != W + "sectPr").ToList();
            var end = blocks.Count;
            while (end > 0 && blocks[end - 1].Name == W + "p" && !blocks[end - 1].Descendants(W + "t").Any(t => t.Value.Trim().Length > 0) && !blocks[end - 1].Descendants(W + "drawing").Any())
                end--;
            if (end < blocks.Count) blocks[end].AddBeforeSelf(made);
            else if (body.Element(W + "sectPr") is { } sectPr) sectPr.AddBeforeSelf(made);
            else body.Add(made);
        }
        else if (before) anchor.AddBeforeSelf(made);
        else anchor.AddAfterSelf(made);
        parts.Changed("word/document.xml");
        parts.Changed("word/_rels/document.xml.rels");
        parts.Changed("[Content_Types].xml");
        var place = anchor?.Name == W + "tbl" ? "table" : "paragraph";
        return $"Put {(said.Count == 1 ? "the picture " + said[0] : "the pictures " + string.Join(", ", said))} {(anchor is null ? "at the end of the document" : before ? $"before the {place} given" : $"after the {place} given")}, centred, as wide as the text at most.";
    }

    // "![The chart](C:\charts\sales.png)"
    [GeneratedRegex(@"^!\[(?<alt>[^\]]*)\]\((?<src>[^)]+)\)$")]
    private static partial Regex PictureLine();
}
