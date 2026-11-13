using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace Sloperator.Core.Documents;

/// <summary>
/// write_file and edit_file for documents: Word, Excel and PowerPoint files and PDFs made from
/// Markdown, and changed in place with their formatting kept. Before, a model made them with
/// PowerShell and the Office apps' COM interfaces, which took qwen3.8 24 minutes for a budget
/// workbook, or not at all on a PC without Office.
/// </summary>
internal static partial class DocumentFiles
{
    /// <summary>"docx", "xlsx", "pptx" or "pdf" for a document write_file makes from Markdown; null for a plain file.</summary>
    public static string? WriteKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => "docx",
        ".xlsx" => "xlsx",
        ".pptx" => "pptx",
        ".pdf" => "pdf",
        _ => null,
    };

    /// <summary>"docx", "xlsx" or "pptx" for a document edit_file changes in place; "pdf" for one it can't.</summary>
    public static string? EditKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" or ".docm" => "docx",
        ".xlsx" or ".xlsm" => "xlsx",
        ".pptx" or ".pptm" => "pptx",
        ".pdf" => "pdf",
        _ => null,
    };

    /// <summary>
    /// The Markdown each document made here came from, by path, to add to it with append; and what
    /// its file was. The last ones made: kept for the process's life, every document's whole text
    /// only added up.
    /// </summary>
    /// <remarks>
    /// Without its Markdown once edit_file has changed it: still the model's own to write again whole, but no
    /// longer what its Markdown said, so not to append to. Until 2026-11-13 an edit made it a stranger's: Sonnet
    /// made a deck, changed a line of it with edit_file, went to write it again and was told the file "isn't one
    /// you wrote in this task, so it may be the user's own".
    /// </remarks>
    private static readonly RecentMap<string, (string Hash, string? Markdown)> Made = new(64, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Makes the document at <paramref name="path"/> from Markdown; returns what to tell the model.
    /// Throws <see cref="InvalidOperationException"/> with the reason when it won't.
    /// </summary>
    /// <param name="resolve">Turns a picture's path as written into a full one.</param>
    /// <param name="toPdf">Turns a Word, Excel or PowerPoint file into a PDF for a "pages:" line (see <see cref="PdfJoin.Build"/>).</param>
    public static string Write(string path, string markdown, bool append, Func<string, string> resolve, Func<string, string>? toPdf = null)
    {
        var kind = WriteKind(path) ?? throw new ArgumentException("not a document", nameof(path));
        var name = Path.GetFileName(path);
        var existed = File.Exists(path);
        (string Hash, string? Markdown) made = ("", "");
        var ours = existed && Made.TryGet(path, out made) && made.Hash == Hash(File.ReadAllBytes(path));
        if (append)
        {
            if (!ours || made.Markdown is null)
                throw new InvalidOperationException($"append adds to a document write_file made in this task, unchanged since. To add to {name}, " +
                    (kind == "pdf" ? "write it again whole." : "use edit_file: old_text its last paragraph, new_text that paragraph and what comes after it."));
            markdown = made.Markdown.TrimEnd() + "\n\n" + markdown;
        }
        // "Delete it first" was read as the way to go: asked for a cover letter on the Desktop,
        // Nemotron deleted the one already there, which another task had made, without asking.
        else if (existed && !ours)
            throw new InvalidOperationException($"{name} already exists, and it isn't one you wrote in this task, so it may be the user's own: " +
                "writing it would replace it whole, its formatting with it. " +
                $"Write to a new name, such as \"{Path.GetFileNameWithoutExtension(path)} (2){Path.GetExtension(path)}\"" +
                (kind == "pdf" ? "" : ", or change what needs changing in it with edit_file, which keeps the rest as it is") +
                ". Replace or delete it only when the user asked for that.");

        // A PDF made of pages of other PDFs ("pages: C:\lease.pdf", "pages: C:\inventory.pdf 2-3"): joined, not written.
        if (kind == "pdf" && !append && PdfJoin.Parse(markdown, resolve) is { } pages)
        {
            if (pages.Any(p => string.Equals(Path.GetFullPath(p.File), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{name} can't be made of its own pages: write to another name.");
            var (joined, count, from) = PdfJoin.Build(pages, toPdf);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Save(path, joined);
            Made.Set(path, (Hash(joined), null));
            return $"{(existed ? "Overwrote" : "Created")} {path}: a PDF of {(count == 1 ? "1 page" : $"{count} pages")} ({from}).";
        }

        var blocks = MarkdownDocument.Parse(markdown);
        var notes = new List<string>();
        var bytes = kind switch
        {
            "docx" => DocxWriter.Write(blocks, resolve, notes),
            "xlsx" => XlsxWriter.Write(blocks, notes),
            "pptx" => PptxWriter.Write(blocks, resolve, notes),
            _ => PdfWriter.Write(blocks, resolve, notes),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Save(path, bytes);
        Made.Set(path, (Hash(bytes), markdown));
        var what = $"{(append ? "Added to" : existed ? "Overwrote" : "Created")} {path}: {Describe(kind, blocks, bytes)}.";
        return notes.Count == 0 ? what : what + " " + string.Join(" ", notes.Distinct());
    }

    /// <summary>
    /// Replaces text in a Word, PowerPoint or Excel file, the rest of it as it was; returns what
    /// changed. Throws <see cref="InvalidOperationException"/> with what to tell the model when it can't.
    /// </summary>
    /// <param name="toPdf">Draws a Word, Excel or PowerPoint file as a PDF, its path: for a table of contents' page numbers.</param>
    /// <param name="resolve">Turns a picture's path as written into a full one.</param>
    public static string Edit(string path, string oldText, string newText, bool all, Func<string, string>? toPdf = null, Func<string, string>? resolve = null)
    {
        List<WordContents.Entry>? contents = null;
        var kind = EditKind(path);
        if (kind == "pptx" && oldText.Length == 0 && !newText.Replace("\r\n", "\n").Split('\n').Any(l => l.Trim().Length > 0 && Regex.IsMatch(l.Trim(), @"^(?:delete|remove|take\s+out|drop|move|duplicate|copy|add|insert|new|put)\s", RegexOptions.IgnoreCase)))
            throw new InvalidOperationException("old_text must not be empty. To add text, give the paragraph it goes after as old_text, and that paragraph followed by the new text as new_text; " +
                "to take out, move or copy slides, lines \"delete slide 3\", \"move slide 4 to 1\", \"duplicate slide 2\"; a new slide is \"add slide after 2\" with its title and points under it.");
        if (kind == "pdf")
            throw new InvalidOperationException("A PDF can't be changed in place. Read it with read_file and write a new one with write_file " +
                "(Markdown), or change the document it was made from and make the PDF again.");
        byte[] original;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            original = new byte[file.Length];
            file.ReadExactly(original);
        }
        using var buffer = new MemoryStream();
        buffer.Write(original);
        buffer.Position = 0;
        string message;
        try
        {
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true);
            var parts = new PartSet(zip);
            if (kind == "xlsx")
                message = SheetEdit.Edit(parts, oldText, newText, all);
            else
            {
                var names = kind == "docx"
                    ? parts.Names.Where(n => n == "word/document.xml" || WordPart().IsMatch(n)).OrderBy(n => n == "word/document.xml" ? 0 : 1).ThenBy(n => n)
                    : parts.Names.Where(n => SlidePart().IsMatch(n)).OrderBy(n => int.Parse(SlidePart().Match(n).Groups[1].Value));
                var documents = names.ToDictionary(n => n, parts.Get);
                // A deck's speaker notes too, which read_file shows; one that can't be read is left
                // out rather than the whole change refused.
                if (kind == "pptx")
                    foreach (var notes in parts.Names.Where(n => NotesPart().IsMatch(n)).OrderBy(n => int.Parse(NotesPart().Match(n).Groups[1].Value)).ToList())
                    {
                        try { documents[notes] = parts.Get(notes); }
                        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException) { }
                    }
                if (documents.Count == 0)
                    throw new InvalidOperationException($"{Path.GetFileName(path)} has no text to change.");
                // Slides taken out, moved or copied ("delete slide 3", "move slide 4 to 1", "duplicate slide 2").
                string? slidesSaid = null;
                if (kind == "pptx" && oldText.Length == 0)
                    slidesSaid = DeckSlides.Edit(parts, newText) ?? throw new InvalidOperationException(
                        "With old_text empty, each line of new_text is \"delete slide 3\", \"move slide 4 to 1\" (or \"to the end\", \"after slide 2\"), \"duplicate slide 2\", " +
                        "or \"add slide after 2\" with the new slide's title and points on the lines under it. To change text, give it as old_text.");
                // "--- slide 2 of 5 ---" before the text, as read_file shows a slide: the text on that slide and in its
                // notes, which is how a title is told from the same words on another slide.
                if (kind == "pptx" && SlideLine().Match(oldText.Replace("\r\n", "\n")) is { Success: true } on)
                {
                    var order = SlideOrder(parts, documents.Keys);
                    var number = int.Parse(on.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (number < 1 || number > order.Count || order[number - 1] is not { } slide || !documents.ContainsKey(slide))
                        throw new InvalidOperationException($"There is no slide {number}: the deck has {order.Count}.");
                    var notes = NotesOf(parts, slide);
                    documents = documents.Where(d => d.Key == slide || d.Key == notes).ToDictionary(d => d.Key, d => d.Value);
                    oldText = on.Groups["rest"].Value;
                    // The same line copied into new_text goes with it.
                    if (SlideLine().Match(newText.Replace("\r\n", "\n")) is { Success: true } same && same.Groups[1].Value == on.Groups[1].Value)
                        newText = same.Groups["rest"].Value;
                }
                // Pictures put in a Word document ("![The chart](C:\chart.png)" after a paragraph, or at the end).
                string? picturesSaid = null;
                if (kind == "docx" && documents.TryGetValue("word/document.xml", out var pictured) && newText.Replace("\r\n", "\n").Split('\n').Any(WordPictures.IsPicture))
                    picturesSaid = WordPictures.Edit(parts, pictured, oldText, newText, resolve ?? (p => p));
                if (slidesSaid is not null)
                    message = slidesSaid;
                else if (picturesSaid is not null)
                    message = picturesSaid;
                // Nothing given to find, in a Word document: the new text goes at its end.
                else if (oldText.Length == 0 && documents.TryGetValue("word/document.xml", out var main))
                {
                    if (newText.Trim().Length == 0)
                        throw new InvalidOperationException("Both old_text and new_text are empty: there is nothing to change.");
                    // A table of contents after the title, its page numbers once the file is written; with lines for every
                    // page's header or footer too, as Haiku and Sonnet both first gave "page numbers at the bottom" and
                    // "table of contents" in one edit (2026-11-13).
                    if (newText.Replace("\r\n", "\n").Split('\n').Any(WordContents.Asks))
                    {
                        var others = string.Join("\n", newText.Replace("\r\n", "\n").Split('\n').Where(l => !WordContents.Asks(l)));
                        var pagesSaid = others.Trim().Length == 0 ? null
                            : WordPages.Edit(parts, others) ?? throw new InvalidOperationException("\"table of contents\" goes in an edit by itself or with lines for every page's header or footer (\"page numbers at the bottom\"): " +
                                $"\"{others.Trim().Split('\n')[0].Trim()}\" is neither. Nothing was changed.");
                        contents = WordContents.Insert(main, parts);
                        parts.Changed("word/document.xml");
                        message = (pagesSaid is null ? "" : pagesSaid.TrimEnd('.') + "; then a")
                            + $"{(pagesSaid is null ? "A" : "")}dded a table of contents after the title, listing {(contents.Count == 1 ? "its 1 heading" : $"its {contents.Count} headings")}";
                    }
                    // Lines for every page's header or footer ("page numbers at the bottom", "footer left: Confidential").
                    else if (WordPages.Edit(parts, newText) is { } pages)
                        message = pages;
                    else
                    {
                        TextEdit.AppendToBody(main, newText.Replace("\r\n", "\n"));
                        parts.Changed("word/document.xml");
                        message = "Added the text at the end of the document, looking like its last paragraph.";
                    }
                }
                // new_text "format: centered, dark blue": how the text given looks, not what it says.
                // new_text "comment: …": a comment in the margin on the text given.
                else if (kind == "docx" && TextEdit.Comment(parts, documents, oldText, newText) is { } commented)
                    message = commented;
                else if (TextEdit.Look(documents, slides: kind == "pptx", oldText, newText, all) is { } looked)
                {
                    foreach (var part in looked.Parts) parts.Changed(part);
                    message = looked.Said;
                }
                else
                {
                    try
                    {
                        var (count, changed, within) = TextEdit.Replace(documents, slides: kind == "pptx", oldText, newText, all);
                        foreach (var part in changed) parts.Changed(part);
                        message = count > 1 ? $"Replaced {count} occurrences." : "Replaced the text; the formatting around it is as it was." + (within > 0 ? " " + TextEdit.Within(within) : "");
                    }
                    // Lines given after a slide's chart, as read_file shows it: no text to find, but where they go is
                    // clear, and they are the slide's text as lines after its title are.
                    catch (InvalidOperationException) when (kind == "pptx" && AfterSlideChart(parts, documents, oldText, newText) is not null)
                    {
                        var (slide, title, lines) = AfterSlideChart(parts, documents, oldText, newText)!.Value;
                        TextEdit.Replace(new Dictionary<string, XDocument> { [slide] = documents[slide] }, slides: true, title, title + "\n" + lines, all: false);
                        parts.Changed(slide);
                        message = "Added the text to the chart's slide, as the slide's text beside the chart.";
                    }
                }
            }
            parts.Save();
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} could not be read as a document: {ex.Message}", ex);
        }
        var edited = buffer.ToArray();
        Save(path, edited);
        if (contents is not null)
        {
            (edited, var said) = Numbered(path, edited, contents, toPdf);
            message += said;
        }
        // One made here stays the model's own after its own edit, to write again whole; a file that was there doesn't become it.
        if (Made.TryGet(path, out var made) && made.Hash == Hash(original))
            Made.Set(path, (Hash(edited), null));
        else
            Made.Remove(path);
        return $"Edited {path}: {message}";
    }

    /// <summary>
    /// The slide a chart line given as old_text is on, with that slide's title and the lines new_text adds after
    /// the chart's line; null when old_text is no slide's chart, or more than one's, or new_text isn't it followed
    /// by more. Asked for two bullets beside a chart (2026-11-13), Haiku gave the chart's line as read_file shows
    /// it, then the end of that line, each followed by the bullets, and was told twice the text wasn't in the
    /// document; Sonnet rewrote the slide's XML with a script.
    /// </summary>
    private static (string Slide, string Title, string Lines)? AfterSlideChart(PartSet parts, Dictionary<string, XDocument> documents, string oldText, string newText)
    {
        var anchor = oldText.Replace("\r\n", "\n").Trim();
        var replacement = newText.Replace("\r\n", "\n").Trim();
        if (anchor.Length < 4 || anchor.Contains('\n') || !replacement.StartsWith(anchor, StringComparison.Ordinal))
            return null;
        var lines = replacement[anchor.Length..].Trim('\n', ' ');
        var bare = anchor.Trim('(', ')', ' ');
        if (lines.Length == 0 || bare.Length < 4)
            return null;
        XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var asked = SheetChart.Parse(bare, colon: true);
        var found = new List<(string Slide, string Title)>();
        foreach (var slide in documents.Keys.Where(k => SlidePart().IsMatch(k)))
        {
            var folder = slide[..(slide.LastIndexOf('/') + 1)];
            var rels = $"{folder}_rels/{slide[folder.Length..]}.rels";
            if (!parts.Exists(rels))
                continue;
            var title = documents[slide].Descendants(p + "sp")
                .FirstOrDefault(sp => (string?)sp.Element(p + "nvSpPr")?.Element(p + "nvPr")?.Element(p + "ph")?.Attribute("type") is "title" or "ctrTitle") is { } shape
                ? string.Concat(shape.Descendants(a + "t").Select(t => t.Value)).Trim() : "";
            foreach (var relation in parts.Get(rels).Root!.Elements().Where(r => ((string?)r.Attribute("Type"))?.EndsWith("/chart", StringComparison.Ordinal) == true))
            {
                var chartPart = PartSet.Resolve(folder.TrimEnd('/'), (string?)relation.Attribute("Target") ?? "");
                if (!parts.Exists(chartPart))
                    continue;
                var chart = parts.Get(chartPart);
                var (kind, name) = SheetChart.About(chart);
                // The line read_file shows for it, or a part of that line; or the line it was written with, by its kind and its title or its slide's.
                if (("chart: " + SheetChart.DescribeData(chart)).Contains(bare, StringComparison.OrdinalIgnoreCase)
                    || asked is not null && asked.Kind == kind && (asked.Title is null || string.Equals(asked.Title, name, StringComparison.OrdinalIgnoreCase) || string.Equals(asked.Title, title, StringComparison.OrdinalIgnoreCase)))
                    found.Add((slide, title));
            }
        }
        return found.Count == 1 && found[0].Title.Length > 0 ? (found[0].Slide, found[0].Title, lines) : null;
    }

    /// <summary>
    /// The page numbers of a table of contents just put in, from the document drawn as a PDF, written into the file;
    /// the file as it is now and what to say of the numbers.
    /// </summary>
    private static (byte[] File, string Said) Numbered(string path, byte[] edited, List<WordContents.Entry> contents, Func<string, string>? toPdf)
    {
        const string without = ", without page numbers: Word puts them in with Update Field (F9 on the table).";
        if (toPdf is null)
            return (edited, without);
        string pdf;
        try { pdf = toPdf(path); }
        catch (InvalidOperationException ex)
        {
            return (edited, $", without page numbers, as they need the pages laid out ({ex.Message.TrimEnd('.')}): Word puts them in with Update Field (F9 on the table).");
        }
        try
        {
            var pages = WordContents.Pages(pdf, contents);
            if (pages.Count == 0)
                return (edited, without);
            using var buffer = new MemoryStream();
            buffer.Write(edited);
            buffer.Position = 0;
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
            {
                var parts = new PartSet(zip);
                var numbered = WordContents.Number(parts.Get("word/document.xml"), pages);
                parts.Changed("word/document.xml");
                parts.Save();
                if (numbered == 0)
                    return (edited, without);
            }
            var file = buffer.ToArray();
            Save(path, file);
            return (file, pages.Count == contents.Count ? ", with the page each is on." : $", with the pages of {pages.Count} of them (the others weren't found as lines of their own in the laid-out pages).");
        }
        finally
        {
            try { File.Delete(pdf); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// A deck's slides as their parts in the order it shows them, which is how read_file numbers them: a slide
    /// moved in PowerPoint keeps its part's name. One that isn't there is null, its number kept; by the parts' own
    /// numbers when the deck's list can't be read.
    /// </summary>
    private static List<string?> SlideOrder(PartSet parts, IEnumerable<string> documents)
    {
        var byName = documents.Where(k => SlidePart().IsMatch(k)).Cast<string?>().ToList();
        try
        {
            if (!parts.Exists("ppt/presentation.xml") || !parts.Exists("ppt/_rels/presentation.xml.rels"))
                return byName;
            XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
            var targets = parts.Get("ppt/_rels/presentation.xml.rels").Root!.Elements()
                .Where(r => r.Attribute("Id") is not null && r.Attribute("Target") is not null)
                .ToDictionary(r => (string)r.Attribute("Id")!, r => PartSet.Resolve("ppt", (string)r.Attribute("Target")!));
            var listed = parts.Get("ppt/presentation.xml").Root!.Element(p + "sldIdLst")?.Elements(p + "sldId")
                .Select(s => (string?)s.Attribute(OfficePackage.R + "id") is { } id && targets.TryGetValue(id, out var target) && parts.Exists(target) ? target : null).ToList();
            return listed is { Count: > 0 } ? listed : byName;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException or ArgumentException)
        {
            return byName;
        }
    }

    /// <summary>The part of a slide's speaker notes, or null.</summary>
    private static string? NotesOf(PartSet parts, string slide)
    {
        var folder = slide[..(slide.LastIndexOf('/') + 1)];
        var rels = $"{folder}_rels/{slide[folder.Length..]}.rels";
        if (!parts.Exists(rels))
            return null;
        try
        {
            return parts.Get(rels).Root!.Elements().FirstOrDefault(r => ((string?)r.Attribute("Type"))?.EndsWith("/notesSlide", StringComparison.Ordinal) == true)?.Attribute("Target") is { } target
                ? PartSet.Resolve(folder.TrimEnd('/'), target.Value) : null;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Writes the file through a temporary one, so a failure leaves the old one whole.</summary>
    private static void Save(string path, byte[] bytes)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (ex is IOException && File.Exists(path))
                throw new InvalidOperationException($"{Path.GetFileName(path)} is open in another program, which keeps it locked: close it there (save your changes first), then try again.", ex);
            throw;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>What was made: "a Word document: 3 headings, 12 paragraphs, 1 table".</summary>
    private static string Describe(string kind, List<Block> blocks, byte[] bytes)
    {
        static string Count(int n, string one) => n == 1 ? $"1 {one}" : $"{n} {one}s";
        switch (kind)
        {
            case "pdf":
                using (var pdf = PdfDocument.Open(bytes))
                    return $"a PDF of {Count(pdf.NumberOfPages, "page")}";
            case "pptx":
                var slides = PptxWriter.Slides(blocks);
                return $"a presentation of {Count(slides.Count, "slide")}: " + string.Join("; ", slides.Select((s, i) =>
                    s.OfType<HeadingBlock>().FirstOrDefault() is { } h ? MarkdownDocument.Plain(h.Text) : $"slide {i + 1} (no title)"));
            case "xlsx":
                using (var zip = new ZipArchive(new MemoryStream(bytes)))
                {
                    var parts = new PartSet(zip);
                    var sheets = parts.Get("xl/workbook.xml").Root!.Descendants(XlsxWriter.S + "sheet").Select((s, i) =>
                    {
                        var range = (string?)parts.Get($"xl/worksheets/sheet{i + 1}.xml").Root!.Element(XlsxWriter.S + "dimension")?.Attribute("ref");
                        return $"\"{(string?)s.Attribute("name")}\" ({range})";
                    }).ToList();
                    return $"a workbook with {Count(sheets.Count, "sheet")}: {string.Join(", ", sheets)}";
                }
            default:
                var counts = new[]
                {
                    (blocks.OfType<HeadingBlock>().Count(), "heading"), (blocks.OfType<ParagraphBlock>().Count(), "paragraph"),
                    (blocks.OfType<ListItemBlock>().Count(), "list item"), (blocks.OfType<TableBlock>().Count(), "table"),
                    (blocks.OfType<ImageBlock>().Count(), "picture"),
                }.Where(c => c.Item1 > 0).Select(c => Count(c.Item1, c.Item2)).ToList();
                return "a Word document" + (counts.Count > 0 ? ": " + string.Join(", ", counts) : " with nothing in it");
        }
    }

    [GeneratedRegex(@"^word/(header\d*|footer\d*|footnotes|endnotes)\.xml$")]
    private static partial Regex WordPart();

    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$")]
    private static partial Regex SlidePart();

    [GeneratedRegex(@"^ppt/notesSlides/notesSlide(\d+)\.xml$")]
    private static partial Regex NotesPart();

    // "--- slide 2 of 5 ---" as read_file shows it, and the text after it
    [GeneratedRegex(@"^\s*-{2,}\s*slide\s+(\d{1,5})(?:\s+of\s+\d+)?\s*-{2,}[ \t]*\n(?<rest>[\s\S]*\S[\s\S]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SlideLine();
}

/// <summary>The XML parts of an Office file open for changing; the changed ones are written back on <see cref="Save"/>.</summary>
internal sealed class PartSet(ZipArchive zip)
{
    private readonly Dictionary<string, XDocument> _loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);

    public IEnumerable<string> Names => zip.Entries.Select(e => e.FullName).Where(n => !_deleted.Contains(n)).ToList();

    public bool Exists(string name) => !_deleted.Contains(name) && (_loaded.ContainsKey(name) || _raw.ContainsKey(name) || zip.GetEntry(name) is not null);

    public XDocument Get(string name)
    {
        if (_loaded.TryGetValue(name, out var loaded)) return loaded;
        using var stream = (zip.GetEntry(name) ?? throw new InvalidDataException($"{name} is missing")).Open();
        return _loaded[name] = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    public void Changed(string name) => _changed.Add(name);

    /// <summary>A part the file doesn't have yet (a chart added to a workbook), saved with the changed ones.</summary>
    public void Add(string name, XDocument xml)
    {
        _loaded[name] = xml;
        _changed.Add(name);
        _deleted.Remove(name);
    }

    public void Delete(string name) => _deleted.Add(name);

    private readonly Dictionary<string, byte[]> _raw = new(StringComparer.Ordinal);

    /// <summary>A new part of bytes (a picture put into a document).</summary>
    public void AddRaw(string name, byte[] bytes)
    {
        _raw[name] = bytes;
        _deleted.Remove(name);
    }

    /// <summary>A copy of a part as its bytes (a workbook kept in a deck), under another name.</summary>
    public void CopyRaw(string from, string to)
    {
        using var stream = (zip.GetEntry(from) ?? throw new InvalidDataException($"{from} is missing")).Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        _raw[to] = memory.ToArray();
        _deleted.Remove(to);
    }

    public void Save()
    {
        foreach (var name in _deleted)
            zip.GetEntry(name)?.Delete();
        foreach (var (name, bytes) in _raw.Where(r => !_deleted.Contains(r.Key)))
        {
            zip.GetEntry(name)?.Delete();
            using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            stream.Write(bytes);
        }
        foreach (var name in _changed.Where(n => !_deleted.Contains(n)))
        {
            zip.GetEntry(name)?.Delete();
            using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            var xml = _loaded[name];
            xml.Declaration ??= new XDeclaration("1.0", "UTF-8", "yes");
            xml.Save(stream, SaveOptions.DisableFormatting);
        }
    }

    /// <summary>
    /// A relationship's target as a part name, from the folder of the part it belongs to: "worksheets/sheet1.xml"
    /// from "xl" is "xl/worksheets/sheet1.xml", "../notesSlides/notesSlide2.xml" from "ppt/slides" is
    /// "ppt/notesSlides/notesSlide2.xml".
    /// </summary>
    public static string Resolve(string folder, string target)
    {
        if (target.StartsWith('/'))
            return target.TrimStart('/');
        var path = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var piece in target.Split('/'))
        {
            if (piece == "..") { if (path.Count > 0) path.RemoveAt(path.Count - 1); }
            else if (piece is not ("." or "")) path.Add(piece);
        }
        return string.Join('/', path);
    }
}
