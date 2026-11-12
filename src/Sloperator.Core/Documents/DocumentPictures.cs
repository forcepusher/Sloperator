using Sloperator.Core.Platform;

namespace Sloperator.Core.Documents;

/// <summary>
/// A document drawn as it prints, a page at a time, for capture_result: the user asked for pictures of
/// documents in before/after as of an app's window (1 November 2026). A PDF is drawn by the platform's
/// renderer (<see cref="PdfPages"/>); Word, Excel and PowerPoint files (and OpenDocument's) are turned
/// into a PDF first by the program the PC has for them (<see cref="Converter"/>: on Windows, Office
/// running hidden, else LibreOffice). Nothing shows on screen meanwhile, and no window has to be open.
/// </summary>
public static class DocumentPictures
{
    /// <summary>Pixels per inch a page is drawn at: an A4 page 1240 pixels wide, as sharp as a screenshot's text.</summary>
    public const double Dpi = 150;

    /// <summary>White left around what a page shows, once its empty margins are cut off.</summary>
    private const int Margin = 24;

    /// <summary>
    /// Turns the document at the first path into a PDF at the second, with the third Sloperator's tools
    /// folder (where a LibreOffice fetched for it is; null for none); set by the platform, null where it
    /// can't. Throws <see cref="InvalidOperationException"/> saying why, for the model to read.
    /// </summary>
    public static Action<string, string, string?, CancellationToken>? Converter { get; set; }

    /// <summary>What <see cref="Converter"/> throws when the PC has no program for the kind (on Windows, no Office and no LibreOffice).</summary>
    public sealed class NoProgramException(string message) : InvalidOperationException(message);

    /// <summary>
    /// Fetches a program for <see cref="Converter"/> into the tools folder given, telling the second
    /// argument how it goes (on Windows, LibreOffice: the File inspector's Get LibreOffice); null where
    /// none can be. Throws saying why it couldn't.
    /// </summary>
    public static Func<string, Action<string>, CancellationToken, Task>? Fetcher { get; set; }

    /// <summary>The kinds the converter takes: what Word, Excel and PowerPoint open, OpenDocument's among them.</summary>
    private static readonly HashSet<string> Office = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".doc", ".dotx", ".dotm", ".dot", ".rtf", ".odt",
        ".xlsx", ".xlsm", ".xlsb", ".xls", ".xltx", ".xltm", ".ods",
        ".pptx", ".pptm", ".ppt", ".potx", ".ppsx", ".odp",
    };

    /// <summary>Presentations, whose pages are slides: drawn whole, not cut to what is on them.</summary>
    private static readonly HashSet<string> Slides = new(StringComparer.OrdinalIgnoreCase) { ".pptx", ".pptm", ".ppt", ".potx", ".ppsx", ".odp" };

    /// <summary>Whether <paramref name="path"/> is a document this draws: a PDF, or one the converter turns into one.</summary>
    public static bool Handles(string path) =>
        Path.GetExtension(path) is var ext && (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || Office.Contains(ext));

    /// <summary>
    /// Page <paramref name="page"/> (from 1) of the document at <paramref name="path"/>, cut to what it
    /// shows with a little white around it, and how many pages it has. A PDF made on the way goes in
    /// <paramref name="scratch"/> and is deleted after. Throws <see cref="InvalidOperationException"/>
    /// saying why it can't (no program for the kind, a page it hasn't).
    /// </summary>
    public static (RawImage Picture, int Pages) Draw(string path, int page, string scratch, string? tools, CancellationToken ct)
    {
        var pdf = path;
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var convert = Converter ?? throw new InvalidOperationException($"Nothing on this system turns {Path.GetExtension(path)} files into pages.");
            Directory.CreateDirectory(scratch);
            pdf = Path.Combine(scratch, $"pages-{Guid.NewGuid():N}.pdf");
            convert(path, pdf, tools, ct);
        }
        try
        {
            IPdfDocument document;
            try { document = PdfPages.Open(pdf); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
            {
                throw new InvalidOperationException($"{Path.GetFileName(path)} couldn't be drawn: {ex.Message}");
            }
            using (document)
            {
                var pages = document.Sizes.Count;
                if (pages == 0)
                    throw new InvalidOperationException($"{Path.GetFileName(path)} has no pages to draw.");
                if (page < 1 || page > pages)
                    throw new InvalidOperationException($"{Path.GetFileName(path)} has {pages} page{(pages == 1 ? "" : "s")}: give a page from 1 to {pages}.");
                var width = (int)Math.Round(document.Sizes[page - 1].Width / 72 * Dpi);
                var (bgra, w, h) = document.Render(page - 1, width) ?? throw new InvalidOperationException($"{Path.GetFileName(path)} couldn't be drawn.");
                // A slide whole: where things are on it is what is looked at. Cut to what it shows, a pie in the
                // middle of its slide was a pie at the right of a picture that began at the title's left edge, and
                // Sonnet set about filling "the empty left half" (2026-11-13).
                var drawn = new RawImage(w, h, bgra);
                return (Slides.Contains(Path.GetExtension(path)) ? drawn : Trim(drawn), pages);
            }
        }
        finally
        {
            if (pdf != path)
                try { File.Delete(pdf); }
                catch (IOException) { }
        }
    }

    /// <summary>
    /// The page cut to what is on it, <see cref="Margin"/> pixels of white around: a short table is
    /// a picture of the table, not of an empty sheet of paper. A blank page stays as it is.
    /// </summary>
    internal static RawImage Trim(RawImage page)
    {
        var (left, top, right, bottom) = (page.Width, page.Height, -1, -1);
        for (var y = 0; y < page.Height; y++)
        {
            var row = y * page.Stride;
            for (var x = 0; x < page.Width; x++)
            {
                var i = row + x * 4;
                // Anything not near white: text, lines, a tinted header row.
                if (page.Bgra[i] > 245 && page.Bgra[i + 1] > 245 && page.Bgra[i + 2] > 245)
                    continue;
                (left, right) = (Math.Min(left, x), Math.Max(right, x));
                (top, bottom) = (Math.Min(top, y), Math.Max(bottom, y));
            }
        }
        if (right < 0)
            return page;
        (left, top) = (Math.Max(0, left - Margin), Math.Max(0, top - Margin));
        (right, bottom) = (Math.Min(page.Width - 1, right + Margin), Math.Min(page.Height - 1, bottom + Margin));
        return page.Crop(left, top, right - left + 1, bottom - top + 1);
    }
}
