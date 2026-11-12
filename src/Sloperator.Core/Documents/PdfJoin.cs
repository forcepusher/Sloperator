using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace Sloperator.Core.Documents;

/// <summary>
/// A PDF made of pages of other PDFs: write_file to a .pdf whose content is lines "pages: C:\lease.pdf" and
/// "pages: C:\inventory.pdf 2-3,5" joins them in that order. Joining, taking pages out, dropping and reordering
/// them are one thing said that way. Asked to join two PDFs (2026-11-13), Haiku found no PDF tool on the PC and
/// stopped to ask whether to download one; Sonnet downloaded qpdf from GitHub, 134 s, into the app's tools folder.
/// The pages are copied as they are (PdfPig), not drawn again.
/// </summary>
internal static partial class PdfJoin
{
    /// <summary>Pages of a PDF to take, in order; none given: all of them.</summary>
    internal sealed record Part(string File, string? Pages);

    /// <summary>
    /// The parts <paramref name="content"/> names when every line of it is a pages line; null for any other text
    /// (Markdown to write a PDF from). A text with pages lines among others is refused: it is one or the other.
    /// </summary>
    /// <param name="resolve">Turns a path as written into a full one.</param>
    public static List<Part>? Parse(string content, Func<string, string> resolve)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var parts = new List<Part>();
        var other = 0;
        foreach (var line in lines)
        {
            if (PagesLine().Match(line) is { Success: true } m)
                parts.Add(new Part(resolve(m.Groups["path"].Value.Trim().Trim('"', '\'')), m.Groups["pages"].Success ? m.Groups["pages"].Value.Trim() : null));
            else
                other++;
        }
        if (parts.Count == 0)
            return null;
        if (other > 0)
            throw new InvalidOperationException("A PDF is either written from Markdown or made of pages of other PDFs, where every line is one: " +
                                                "\"pages: C:\\a.pdf\" for all of it, \"pages: C:\\b.pdf 2-5,8\" for some. Make the other part a PDF of its own first, then join them.");
        return parts;
    }

    /// <summary>The PDF of <paramref name="parts"/> joined in order, and what went into it ("lease.pdf: 1 page; inventory.pdf: pages 2-3").</summary>
    /// <param name="toPdf">
    /// Turns a Word, Excel or PowerPoint file into a PDF as it prints, its path; null where nothing can. Asked for a
    /// report as a PDF (2026-11-13), Haiku and Sonnet drove Word through PowerShell, which a PC with only LibreOffice
    /// doesn't have; capture_result already turned such files into pages either way.
    /// </param>
    public static (byte[] Bytes, int Pages, string Said) Build(IReadOnlyList<Part> parts, Func<string, string>? toPdf = null)
    {
        var opened = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        var made = new List<string>();
        try
        {
            using var builder = new PdfDocumentBuilder();
            var said = new List<string>();
            var total = 0;
            foreach (var part in parts)
            {
                var name = Path.GetFileName(part.File);
                if (!File.Exists(part.File))
                    throw new InvalidOperationException($"There is no {part.File} to take pages from.");
                if (!opened.TryGetValue(part.File, out var document))
                {
                    var file = part.File;
                    if (!Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        file = (toPdf ?? throw new InvalidOperationException($"Nothing on this PC turns {name} into pages."))(part.File);
                        made.Add(file);
                    }
                    try
                    {
                        opened[part.File] = document = PdfDocument.Open(file);
                    }
                    catch (Exception ex) when (ex is not (OutOfMemoryException or IOException or UnauthorizedAccessException))
                    {
                        throw new InvalidOperationException($"{name} could not be read as a PDF{(ex.Message.Contains("ncrypt", StringComparison.OrdinalIgnoreCase) ? ": it is protected by a password" : $": {ex.Message}")}");
                    }
                }
                var pages = Numbers(part.Pages, document.NumberOfPages, name);
                foreach (var page in pages)
                    builder.AddPage(document, page);
                total += pages.Count;
                said.Add(part.Pages is null ? $"{name}: {Count(pages.Count)}" : $"{name}: page{(pages.Count == 1 ? "" : "s")} {part.Pages.Replace(" ", "")} of {document.NumberOfPages}");
            }
            if (total == 0)
                throw new InvalidOperationException("Those lines take no pages: nothing to write.");
            return (builder.Build(), total, string.Join("; ", said));
        }
        finally
        {
            foreach (var document in opened.Values)
                document.Dispose();
            foreach (var file in made)
                try { File.Delete(file); }
                catch (IOException) { }
        }
    }

    private static string Count(int pages) => pages == 1 ? "1 page" : $"{pages} pages";

    /// <summary>"2-5,8,10-" as page numbers of a document of <paramref name="count"/> pages; all of them for none given.</summary>
    internal static List<int> Numbers(string? pages, int count, string name)
    {
        if (string.IsNullOrWhiteSpace(pages))
            return [.. Enumerable.Range(1, count)];
        var numbers = new List<int>();
        foreach (var piece in pages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Range().Match(piece) is not { Success: true } m)
                throw new InvalidOperationException($"\"{piece}\" isn't pages: give them as 3, 2-5 or 7- (to the end), with commas between.");
            var from = m.Groups["a"].Success ? int.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture) : 1;
            var to = m.Groups["b"].Success ? int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture) : m.Groups["dash"].Success ? count : from;
            if (from < 1 || to > count || from > count || to < 1)
                throw new InvalidOperationException($"{name} has {Count(count)}: there is no page {(from < 1 || from > count ? from : to)}.");
            // 5-2 is those pages backwards.
            numbers.AddRange(from <= to ? Enumerable.Range(from, to - from + 1) : Enumerable.Range(to, from - to + 1).Reverse());
        }
        return numbers;
    }

    // "pages: C:\Users\me\lease.pdf", "pages: scan of the lease.pdf 2-5, 8", "Pages: "C:\a b.pdf" 3-", "pages: report.docx"
    [GeneratedRegex(@"^pages\s*:\s*(?<path>.+?\.(?:pdf|docx|docm|doc|rtf|odt|xlsx|xlsm|xls|ods|pptx|pptm|ppt|odp)[""']?)(?:\s+(?:pages?\s+)?(?<pages>[0-9][0-9,\-\s]*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex PagesLine();

    [GeneratedRegex(@"^(?<a>[0-9]{1,6})?\s*(?<dash>-)?\s*(?<b>[0-9]{1,6})?$")]
    private static partial Regex Range();
}
