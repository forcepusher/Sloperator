using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sloperator.Core.Documents;

/// <summary>
/// edit_file's lines that take slides out of a deck, move them or copy them, old_text empty: "delete slide 3",
/// "delete slides 2-3", "move slide 4 to 1", "move slide 2 after slide 4", "move slide 5 to the end", "duplicate slide 2".
/// Slides are numbered as read_file shows them, in the deck's order, each line by the numbering the line before left.
/// Until then a deck that wasn't the model's own could only be changed slide by slide's text: taking
/// one out meant rewriting presentation.xml, its relationships and content types by hand.
/// </summary>
internal static partial class DeckSlides
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace P14 = "http://schemas.microsoft.com/office/powerpoint/2010/main";
    private static readonly XNamespace R = OfficePackage.R;
    private static readonly XNamespace Rel = OfficePackage.Rel;
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string Presentation = "ppt/presentation.xml";
    private const string PresentationRels = "ppt/_rels/presentation.xml.rels";

    private abstract record Line;
    private sealed record Delete(List<int> Numbers) : Line;
    private sealed record Move(int Number, string Where, int? Other) : Line;
    private sealed record Duplicate(int Number) : Line;
    /// <summary>A new slide: after slide After (0: at the start, -1: at the end), its title and its lines.</summary>
    private sealed record Add(int After, string Title, List<string> Lines) : Line;

    /// <summary>The lines done to the deck and what was done; null when not every line of new_text is one of them.</summary>
    public static string? Edit(PartSet parts, string newText)
    {
        var lines = new List<Line>();
        Add? adding = null;
        foreach (var raw in newText.Replace("\r\n", "\n").Split('\n'))
        {
            var text = raw.Trim();
            if (text.Length == 0)
                continue;
            // "add slide after 2" (or "insert slide 3", "add slide at the end"), then the new slide's title and lines.
            if (AddLine().Match(text.TrimEnd('.', ':')) is { Success: true } add)
            {
                var after = add.Groups["after"].Success ? int.Parse(add.Groups["after"].Value, CultureInfo.InvariantCulture)
                    : add.Groups["at"].Success ? int.Parse(add.Groups["at"].Value, CultureInfo.InvariantCulture) - 1
                    : add.Groups["start"].Success ? 0 : -1;
                lines.Add(adding = new Add(after, "", []));
                continue;
            }
            if (Of(text.TrimEnd('.')) is { } line)
            {
                adding = null;
                lines.Add(line);
                continue;
            }
            if (adding is null)
                return null;
            var plain = Regex.Replace(text, @"^(?:#{1,6}|[-*+•]|\d{1,3}[.)])\s+", "").Replace("**", "").Trim();
            if (adding.Title.Length == 0 && (text.StartsWith('#') || adding.Lines.Count == 0))
            {
                var index = lines.IndexOf(adding);
                lines[index] = adding = adding with { Title = plain };
            }
            else
                adding.Lines.Add(plain);
        }
        if (lines.Count == 0 || !parts.Exists(Presentation))
            return null;

        var presentation = parts.Get(Presentation);
        var list = presentation.Root!.Element(P + "sldIdLst") ?? throw new InvalidOperationException("The deck lists no slides.");
        var rels = parts.Get(PresentationRels);
        string PartOf(XElement sldId) => rels.Root!.Elements(Rel + "Relationship").FirstOrDefault(r => (string?)r.Attribute("Id") == (string?)sldId.Attribute(R + "id")) is { } r
            ? PartSet.Resolve("ppt", (string)r.Attribute("Target")!) : throw new InvalidOperationException("A slide of the deck has no part.");
        string TitleOf(XElement sldId)
        {
            var slide = parts.Get(PartOf(sldId));
            var title = slide.Descendants(P + "sp").FirstOrDefault(sp => (string?)sp.Descendants(P + "ph").FirstOrDefault()?.Attribute("type") is "title" or "ctrTitle");
            var text = title is null ? "" : string.Concat(title.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value)).Trim();
            return text.Length == 0 ? "" : $" (\"{text}\")";
        }

        // One line after the other, each by the slides' numbers as the line before left them, as a person does them:
        // Haiku gave "delete slide 3", "move slide 2 to 1" and "duplicate slide 1" in one edit, meaning the chart slide
        // it had just moved to the front (2026-11-13). The numbers within one line ("delete slides 2, 4") are as they
        // were before that line.
        var done = new List<string>();
        var touched = false;
        foreach (var line in lines)
        {
            var slides = list.Elements(P + "sldId").ToList();
            XElement SlideAt(int number) => number >= 1 && number <= slides.Count ? slides[number - 1]
                : throw new InvalidOperationException($"There is no slide {number}: the deck has {slides.Count}{(done.Count > 0 ? " by then" : "")}. Nothing was changed.");
            string Said(XElement slide) => $"slide {slides.IndexOf(slide) + 1}{TitleOf(slide)}";
            switch (line)
            {
                case Delete d:
                    var going = d.Numbers.Distinct().Select(SlideAt).ToList();
                    if (going.Count >= slides.Count)
                        throw new InvalidOperationException("A deck keeps at least one slide: that would take out all of them. Nothing was changed.");
                    foreach (var slide in going)
                    {
                        var said = Said(slide);
                        Remove(parts, presentation, rels, slide, PartOf(slide));
                        done.Add($"deleted {said}");
                    }
                    touched = true;
                    break;
                case Move m:
                    var moving = SlideAt(m.Number);
                    var other = m.Other is { } o ? SlideAt(o) : null;
                    var was = Said(moving);
                    if (other == moving)
                        break;
                    moving.Remove();
                    var now = list.Elements(P + "sldId").ToList();
                    switch (m.Where)
                    {
                        case "start": list.AddFirst(moving); break;
                        case "end": list.Add(moving); break;
                        case "after": other!.AddAfterSelf(moving); break;
                        case "before": other!.AddBeforeSelf(moving); break;
                        default:
                            // "to 3": the place it takes among the others.
                            var at = Math.Clamp(int.Parse(m.Where, CultureInfo.InvariantCulture), 1, now.Count + 1);
                            if (at > now.Count) list.Add(moving);
                            else now[at - 1].AddBeforeSelf(moving);
                            break;
                    }
                    Sections(presentation, moving);
                    done.Add($"moved {was} to place {list.Elements(P + "sldId").ToList().IndexOf(moving) + 1}");
                    break;
                case Add a:
                    if (a.Title.Length == 0)
                        throw new InvalidOperationException("Give the new slide's title on the line after \"add slide after 2\" (\"# Next steps\"), and its points under it (\"- Open on Sundays\"). Nothing was changed.");
                    var where = a.After < 0 ? slides.Count : a.After;
                    if (where > slides.Count)
                        throw new InvalidOperationException($"There is no slide {where}: the deck has {slides.Count}{(done.Count > 0 ? " by then" : "")}. Nothing was changed.");
                    // Built on a slide with a title of its own (not the title slide's), the one before the place first.
                    string? TitleKind(XElement slide) => (string?)parts.Get(PartOf(slide)).Descendants(P + "ph").FirstOrDefault(ph => (string?)ph.Attribute("type") is "title" or "ctrTitle")?.Attribute("type");
                    var candidates = (where > 0 ? slides.Take(where).Reverse().Concat(slides.Skip(where)) : slides).ToList();
                    var model = candidates.FirstOrDefault(sl => TitleKind(sl) == "title") ?? candidates.First();
                    var made = Copy(parts, presentation, rels, model, PartOf(model), bare: true);
                    made.Remove();
                    if (where == 0) list.AddFirst(made);
                    else slides[where - 1].AddAfterSelf(made);
                    Sections(presentation, made);
                    Fill(parts, presentation, PartOf(made), a.Title, a.Lines);
                    done.Add($"added slide {list.Elements(P + "sldId").ToList().IndexOf(made) + 1} (\"{a.Title}\"){(a.Lines.Count > 0 ? $" with {a.Lines.Count} point{(a.Lines.Count == 1 ? "" : "s")}" : "")}");
                    touched = true;
                    break;
                case Duplicate c:
                    var source = SlideAt(c.Number);
                    var from = Said(source);
                    Copy(parts, presentation, rels, source, PartOf(source));
                    done.Add($"copied {from} to a new slide after it");
                    touched = true;
                    break;
            }
        }
        parts.Changed(Presentation);
        parts.Changed(PresentationRels);
        if (touched)
            parts.Changed("[Content_Types].xml");
        var count = list.Elements(P + "sldId").Count();
        var text2 = string.Join(", then ", done);
        return char.ToUpperInvariant(text2[0]) + text2[1..] + $"; the deck has {count} slide{(count == 1 ? "" : "s")} now, numbered as read_file shows them.";
    }

    private static Line? Of(string text)
    {
        if (DeleteLine().Match(text) is { Success: true } d)
        {
            var numbers = new List<int>();
            foreach (var piece in d.Groups["list"].Value.Split([',', ' ', '&'], StringSplitOptions.RemoveEmptyEntries).Where(p => !p.Equals("and", StringComparison.OrdinalIgnoreCase)))
            {
                var range = piece.Split('-', '–');
                if (range.Length == 2 && int.TryParse(range[0], out var a) && int.TryParse(range[1], out var b))
                    numbers.AddRange(Enumerable.Range(Math.Min(a, b), Math.Abs(b - a) + 1));
                else if (int.TryParse(piece, out var n))
                    numbers.Add(n);
                else
                    return null;
            }
            return numbers.Count > 0 ? new Delete(numbers) : null;
        }
        if (MoveLine().Match(text) is { Success: true } m)
        {
            var number = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (m.Groups["start"].Success) return new Move(number, "start", null);
            if (m.Groups["end"].Success) return new Move(number, "end", null);
            if (m.Groups["rel"].Success)
                return new Move(number, m.Groups["rel"].Value.ToLowerInvariant() is "after" or "behind" ? "after" : "before", int.Parse(m.Groups["other"].Value, CultureInfo.InvariantCulture));
            return new Move(number, m.Groups["to"].Value, null);
        }
        if (DuplicateLine().Match(text) is { Success: true } c)
            return new Duplicate(int.Parse(c.Groups["n"].Value, CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>A slide out of the deck: its place in the list, its relationship, its part with its notes, and any section or custom show naming it.</summary>
    private static void Remove(PartSet parts, XDocument presentation, XDocument rels, XElement sldId, string part)
    {
        var id = (string?)sldId.Attribute("id");
        var relation = (string?)sldId.Attribute(R + "id");
        sldId.Remove();
        rels.Root!.Elements(Rel + "Relationship").Where(r => (string?)r.Attribute("Id") == relation).Remove();
        presentation.Descendants(P14 + "sldId").Where(s => (string?)s.Attribute("id") == id).Remove();
        presentation.Descendants(P + "sld").Where(s => (string?)s.Attribute(R + "id") == relation).Remove();
        var gone = new List<string> { part };
        var slideRels = RelsOf(part);
        if (parts.Exists(slideRels))
        {
            foreach (var r in parts.Get(slideRels).Root!.Elements(Rel + "Relationship").Where(r => ((string?)r.Attribute("Type"))?.EndsWith("/notesSlide", StringComparison.Ordinal) == true))
            {
                var notes = PartSet.Resolve(part[..part.LastIndexOf('/')], (string)r.Attribute("Target")!);
                gone.Add(notes);
                if (parts.Exists(RelsOf(notes))) parts.Delete(RelsOf(notes));
            }
            parts.Delete(slideRels);
        }
        var types = parts.Get("[Content_Types].xml");
        foreach (var g in gone.Where(parts.Exists))
        {
            parts.Delete(g);
            types.Root!.Elements(Types + "Override").Where(o => string.Equals((string?)o.Attribute("PartName"), "/" + g, StringComparison.OrdinalIgnoreCase)).Remove();
        }
    }

    /// <summary>
    /// A copy of a slide right after it: its part and relationships copied (the same layout and pictures), its charts
    /// copied with their workbooks so the two don't share one, and no speaker notes.
    /// </summary>
    private static XElement Copy(PartSet parts, XDocument presentation, XDocument rels, XElement sldId, string part, bool bare = false)
    {
        string Unused(string pattern)
        {
            for (var n = 1; ; n++)
                if (string.Format(CultureInfo.InvariantCulture, pattern, n) is var name && !parts.Exists(name))
                    return name;
        }
        var types = parts.Get("[Content_Types].xml");
        void Typed(string name, string type) => types.Root!.Add(new XElement(Types + "Override", new XAttribute("PartName", "/" + name), new XAttribute("ContentType", type)));
        string TypeOf(string name) => (string?)types.Root!.Elements(Types + "Override").FirstOrDefault(o => string.Equals((string?)o.Attribute("PartName"), "/" + name, StringComparison.OrdinalIgnoreCase))?.Attribute("ContentType") ?? "";

        var slidePart = Unused("ppt/slides/slide{0}.xml");
        parts.Add(slidePart, new XDocument(parts.Get(part)));
        Typed(slidePart, TypeOf(part));
        if (parts.Exists(RelsOf(part)))
        {
            var copyRels = new XDocument(parts.Get(RelsOf(part)));
            copyRels.Root!.Elements(Rel + "Relationship").Where(r => ((string?)r.Attribute("Type"))?.EndsWith("/notesSlide", StringComparison.Ordinal) == true).Remove();
            // A new slide built on this one keeps only its layout.
            if (bare)
                copyRels.Root.Elements(Rel + "Relationship").Where(r => ((string?)r.Attribute("Type"))?.EndsWith("/slideLayout", StringComparison.Ordinal) != true).Remove();
            foreach (var r in copyRels.Root.Elements(Rel + "Relationship").Where(r => ((string?)r.Attribute("Type"))?.EndsWith("/chart", StringComparison.Ordinal) == true).ToList())
            {
                var chart = PartSet.Resolve("ppt/slides", (string)r.Attribute("Target")!);
                if (!parts.Exists(chart))
                    continue;
                var chartCopy = Unused("ppt/charts/chart{0}.xml");
                parts.Add(chartCopy, new XDocument(parts.Get(chart)));
                Typed(chartCopy, TypeOf(chart));
                if (parts.Exists(RelsOf(chart)))
                {
                    var chartRels = new XDocument(parts.Get(RelsOf(chart)));
                    foreach (var e in chartRels.Root!.Elements(Rel + "Relationship").Where(x => ((string?)x.Attribute("Type"))?.EndsWith("/package", StringComparison.Ordinal) == true))
                    {
                        var book = PartSet.Resolve("ppt/charts", (string)e.Attribute("Target")!);
                        if (!parts.Exists(book))
                            continue;
                        var bookCopy = Unused("ppt/embeddings/Microsoft_Excel_Sheet{0}.xlsx");
                        parts.CopyRaw(book, bookCopy);
                        e.SetAttributeValue("Target", "/" + bookCopy);
                    }
                    parts.Add(RelsOf(chartCopy), chartRels);
                }
                r.SetAttributeValue("Target", "/" + chartCopy);
            }
            parts.Add(RelsOf(slidePart), copyRels);
        }
        var taken = rels.Root!.Elements(Rel + "Relationship").Select(r => (string?)r.Attribute("Id")).ToHashSet();
        var relation = Enumerable.Range(1, int.MaxValue).Select(n => $"rId{n}").First(i => !taken.Contains(i));
        rels.Root.Add(new XElement(Rel + "Relationship", new XAttribute("Id", relation),
            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide"), new XAttribute("Target", "slides/" + slidePart["ppt/slides/".Length..])));
        var id = presentation.Root!.Element(P + "sldIdLst")!.Elements(P + "sldId").Select(s => (int?)s.Attribute("id") ?? 255).Max() + 1;
        var copy = new XElement(P + "sldId", new XAttribute("id", id), new XAttribute(R + "id", relation));
        sldId.AddAfterSelf(copy);
        // In the same section as the slide it copies, right after it.
        if (presentation.Descendants(P14 + "sldId").FirstOrDefault(s => (string?)s.Attribute("id") == (string?)sldId.Attribute("id")) is { } inSection)
            inSection.AddAfterSelf(new XElement(P14 + "sldId", new XAttribute("id", id)));
        return copy;
    }

    /// <summary>
    /// A slide copied to be a new one made into it: its title the one given, its points in a content box, everything
    /// else on it gone. The content box takes its place from the layout when the layout has one, else the place a
    /// content box has on a slide of the deck's size.
    /// </summary>
    private static void Fill(PartSet parts, XDocument presentation, string part, string title, List<string> points)
    {
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var slide = parts.Get(part);
        var tree = slide.Descendants(P + "spTree").First();
        XElement Paragraph(string text) => new(a + "p", new XElement(a + "r", new XElement(a + "rPr", new XAttribute("lang", "en-US")), new XElement(a + "t", text)));
        var titleShape = tree.Elements(P + "sp").FirstOrDefault(sp => (string?)sp.Descendants(P + "ph").FirstOrDefault()?.Attribute("type") is "title" or "ctrTitle");
        foreach (var e in tree.Elements().Where(e => e.Name.LocalName is not ("nvGrpSpPr" or "grpSpPr") && e != titleShape).ToList())
            e.Remove();
        var id = 2;
        if (titleShape is null)
            tree.Add(titleShape = PptxWriter.Placeholder(id, "Title", new XElement(P + "ph", new XAttribute("type", "title")), null, [Paragraph(title)]));
        else
        {
            var body = titleShape.Element(P + "txBody")!;
            body.Elements(a + "p").Remove();
            body.Add(Paragraph(title));
        }
        id = tree.Descendants(P + "cNvPr").Select(c => (int?)c.Attribute("id") ?? 0).DefaultIfEmpty(1).Max() + 1;
        if (points.Count > 0)
        {
            // Where the layout puts its content box, or where one goes on a slide of this size.
            var rels = parts.Get(RelsOf(part));
            var layout = rels.Root!.Elements(Rel + "Relationship").FirstOrDefault(r => ((string?)r.Attribute("Type"))?.EndsWith("/slideLayout", StringComparison.Ordinal) == true) is { } l
                ? PartSet.Resolve("ppt/slides", (string)l.Attribute("Target")!) : null;
            var hasBody = layout is not null && parts.Exists(layout) && parts.Get(layout).Descendants(P + "ph").Any(ph => (string?)ph.Attribute("idx") == "1" || (string?)ph.Attribute("type") == "body");
            XElement? place = null;
            if (!hasBody)
            {
                var size = presentation.Root!.Element(P + "sldSz");
                long cx = (long?)size?.Attribute("cx") ?? 12192000, cy = (long?)size?.Attribute("cy") ?? 6858000;
                place = PptxWriter.Transform(cx * 687 / 10000, cy * 2662 / 10000, cx * 8625 / 10000, cy * 6345 / 10000);
            }
            tree.Add(PptxWriter.Placeholder(id, "Content", new XElement(P + "ph", new XAttribute("idx", 1)), place, points.Select(Paragraph)));
        }
        parts.Changed(part);
    }

    /// <summary>A moved slide in the section of the slide now before it (the first section at the start), keeping each section's slides together.</summary>
    private static void Sections(XDocument presentation, XElement moved)
    {
        var id = (string?)moved.Attribute("id");
        var entry = presentation.Descendants(P14 + "sldId").FirstOrDefault(s => (string?)s.Attribute("id") == id);
        if (entry is null)
            return;
        entry.Remove();
        var before = moved.ElementsBeforeSelf(P + "sldId").LastOrDefault();
        if (before is not null && presentation.Descendants(P14 + "sldId").FirstOrDefault(s => (string?)s.Attribute("id") == (string?)before.Attribute("id")) is { } after)
            after.AddAfterSelf(entry);
        else if (presentation.Descendants(P14 + "section").FirstOrDefault()?.Element(P14 + "sldIdLst") is { } first)
            first.AddFirst(entry);
    }

    private static string RelsOf(string part) => $"{part[..part.LastIndexOf('/')]}/_rels/{part[(part.LastIndexOf('/') + 1)..]}.rels";

    // "delete slide 3", "delete slides 2-3", "remove slides 2, 4 and 5"
    [GeneratedRegex(@"^(?:delete|remove|take\s+out|drop)\s+(?:the\s+)?slides?\s+(?:number\s+)?(?<list>[0-9][0-9,\s\-–&]*(?:\s*and\s*[0-9]+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteLine();

    // "move slide 4 to 1", "move slide 4 to the start", "move slide 2 to the end", "move slide 2 after slide 4", "move slide 5 before 2", "move slide 4 to position 2"
    [GeneratedRegex(@"^move\s+slide\s+(?<n>[0-9]+)\s+(?:(?:to\s+)?(?:the\s+)?(?<start>start|front|beginning|top|first)(?:\s+of\s+the\s+deck)?|(?:to\s+)?(?:the\s+)?(?<end>end|back|last)(?:\s+of\s+the\s+deck)?|(?<rel>after|behind|before)\s+(?:slide\s+)?(?<other>[0-9]+)|to\s+(?:position\s+|place\s+|slide\s+)?(?<to>[0-9]+))$", RegexOptions.IgnoreCase)]
    private static partial Regex MoveLine();

    // "add slide after 2", "add a slide after slide 2", "insert slide 3", "add slide at the end", "new slide at the start"
    [GeneratedRegex(@"^(?:add|insert|new|put)\s+(?:a\s+)?(?:new\s+)?slide\s*(?:(?:after|behind)\s+(?:slide\s+)?(?<after>[0-9]+)|(?:at\s+|as\s+)?(?:slide\s+|position\s+)?(?<at>[0-9]+)|(?:at\s+|to\s+)?(?:the\s+)?(?<end>end|back)|(?:at\s+|to\s+)?(?:the\s+)?(?<start>start|front|beginning))?$", RegexOptions.IgnoreCase)]
    private static partial Regex AddLine();

    // "duplicate slide 2", "copy slide 2"
    [GeneratedRegex(@"^(?:duplicate|copy)\s+slide\s+(?<n>[0-9]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DuplicateLine();
}
