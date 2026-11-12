using System.Text.Json.Nodes;
using Sloperator.Core.Documents;
using Sloperator.Core.Platform;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// A document's page as a picture for capture_result: drawn as it prints, cut to what is on it, and
/// drawn again for the after of a before. Word, Excel and PowerPoint go through the converter (Office
/// running hidden on Windows), faked here but for the opt-in test against the real Office.
/// </summary>
[Collection(nameof(DocumentCaptureTests))]
public sealed class DocumentCaptureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-document-capture-").FullName;
    private readonly Action<string, string, string?, CancellationToken>? _converter = DocumentPictures.Converter;

#if WINDOWS
    public DocumentCaptureTests() => PdfPages.Opener ??= Sloperator.Platform.Windows.WindowsPdf.Open;
#endif

    public void Dispose()
    {
        DocumentPictures.Converter = _converter;
        Directory.Delete(_dir, recursive: true);
    }

    private ToolContext Ctx() => new()
    {
        Platform = new FakePlatform { Screens = [new ScreenRect(0, 0, 1600, 896)] },
        Settings = new AgentSettings { ScreenshotMaxHeight = 896 },
        WorkingDirectory = _dir,
        ScratchDirectory = Path.Combine(_dir, "scratch"),
        ChatDirectory = Path.Combine(_dir, "chat"),
    };

    private static Task<ToolResult> Capture(ToolContext ctx, JsonObject args) => new CaptureResultTool().ExecuteAsync(args, ctx, CancellationToken.None);

    private string Pdf(string name, string markdown)
    {
        var path = Path.Combine(_dir, name);
        DocumentFiles.Write(path, markdown, append: false, p => p);
        return path;
    }

    [Fact]
    public void A_page_is_cut_to_what_is_on_it()
    {
        var white = new byte[400 * 300 * 4];
        Array.Fill(white, (byte)255);
        // A dark box at 100..149 x 80..99.
        for (var y = 80; y < 100; y++)
            for (var x = 100; x < 150; x++)
                white.AsSpan((y * 400 + x) * 4, 3).Clear();
        var trimmed = DocumentPictures.Trim(new RawImage(400, 300, white));
        Assert.Equal((50 + 48, 20 + 48), (trimmed.Width, trimmed.Height));

        // A blank page stays a page.
        var blank = new byte[40 * 30 * 4];
        Array.Fill(blank, (byte)255);
        Assert.Equal((40, 30), (DocumentPictures.Trim(new RawImage(40, 30, blank)).Width, DocumentPictures.Trim(new RawImage(40, 30, blank)).Height));
    }

    [Fact]
    public async Task A_pdfs_page_is_shown_and_drawn_again_for_the_after()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Draws with Windows' PDF renderer.");
        var report = Pdf("report.pdf", "# Report\n\nFirst page.\n\n\\newpage\n\n## Totals\n\n| Item | Sum |\n|---|---|\n| Rent | 1200 |\n");
        var ctx = Ctx();

        var before = await Capture(ctx, new JsonObject { ["file"] = report, ["page"] = 2 });
        Assert.Contains("page 2 of 2 of report.pdf as capture 1", before.Text);
        Assert.Contains("the same page of the same document is drawn again", before.Text);
        // One page of several: the others it made are still to look at.
        Assert.Contains("The document has 2 pages and this shows only page 2", before.Text);
        // Cut to what is on it (the page's number at its foot keeps its height), not a whole sheet of
        // paper at 150 dpi (1275 x 1650 for Letter, 1240 x 1754 for A4).
        using (var png = SkiaSharp.SKBitmap.Decode(Path.Combine(ctx.ChatDirectory!, "capture-1.png")))
            Assert.True(png.Width < 1000, $"{png.Width}x{png.Height}");

        // The document changes; compare alone draws the same page of it again.
        Pdf("report.pdf", "# Report\n\nFirst page.\n\n\\newpage\n\n## Totals\n\n| Item | Sum |\n|---|---|\n| Rent | 1200 |\n| Food | 400 |\n");
        var after = await Capture(ctx, new JsonObject { ["compare"] = new JsonArray(1) });
        Assert.Contains("page 2 of 2 of report.pdf as capture 2", after.Text);
        Assert.Contains("after capture 1", after.Text);
        Assert.Empty(((FakePlatform)ctx.Platform).Captures);

        // A page it hasn't, or a file that is neither picture nor document, is explained.
        Assert.Contains("has 2 pages", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["file"] = report, ["page"] = 3 }))).Message);
        var notes = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(notes, "hi");
        Assert.Contains("isn't a picture or a document", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["file"] = notes }))).Message);
        Assert.Equal("Show the result: report.pdf, page 2", new CaptureResultTool().Describe(new JsonObject { ["file"] = report, ["page"] = 2 }));
    }

    [Fact]
    public async Task Word_excel_and_powerpoint_go_through_the_converter_and_its_pdf_goes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Draws with Windows' PDF renderer.");
        var asked = new List<string>();
        DocumentPictures.Converter = (source, pdf, _, _) =>
        {
            asked.Add(source);
            DocumentFiles.Write(pdf, "# Budget\n\n| Item | Jan |\n|---|---|\n| Rent | 1200 |\n", append: false, p => p);
        };
        var budget = Path.Combine(_dir, "budget.xlsx");
        File.WriteAllText(budget, "not read: the fake converter draws its own");
        var ctx = Ctx();

        var result = await Capture(ctx, new JsonObject { ["file"] = budget });
        Assert.Equal([budget], asked);
        Assert.Contains("page 1 of 1 of budget.xlsx as capture 1", result.Text);
        Assert.DoesNotContain("this shows only page", result.Text);
        Assert.Empty(Directory.EnumerateFiles(ctx.ScratchDirectory!, "*.pdf"));

        // A slide is drawn whole, where a sheet or a page is cut to what is on it: the same page through the same
        // converter is the larger picture as a deck.
        var deck = Path.Combine(_dir, "deck.pptx");
        File.WriteAllText(deck, "not read either");
        var sheet = DocumentPictures.Draw(budget, 1, ctx.ScratchDirectory!, null, CancellationToken.None).Picture;
        var slide = DocumentPictures.Draw(deck, 1, ctx.ScratchDirectory!, null, CancellationToken.None).Picture;
        Assert.True(slide.Width > sheet.Width && slide.Height > sheet.Height, $"slide {slide.Width}x{slide.Height}, sheet {sheet.Width}x{sheet.Height}");

        // Why the converter couldn't is the model's to read.
        DocumentPictures.Converter = (_, _, _, _) => throw new InvalidOperationException("No program on this PC turns budget.xlsx into pages: Microsoft Office or LibreOffice does.");
        Assert.Contains("Microsoft Office or LibreOffice", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["file"] = budget }))).Message);
    }

#if WINDOWS
    /// <summary>
    /// Opt-in (set BB_OFFICE_TESTS=1, starts Word and Excel hidden for a few seconds): the real Office
    /// turns our own documents into pages, Excel's with its sums worked out; one downloaded from the
    /// internet isn't opened.
    /// </summary>
    [Fact]
    public void Office_turns_documents_into_pages_unseen()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("BB_OFFICE_TESTS") is null, "Set BB_OFFICE_TESTS=1 to run (starts Office hidden).");
        var report = Path.Combine(_dir, "report.docx");
        DocumentFiles.Write(report, "# Report\n\n| Region | Q3 |\n|---|---|\n| North | 140 |\n", append: false, p => p);
        var budget = Path.Combine(_dir, "budget.xlsx");
        DocumentFiles.Write(budget, "# Budget\n\n| Item | Jan | Feb | Total |\n|---|---|---|---|\n| Rent | 1200 | 1200 | =SUM(B2:C2) |\n", append: false, p => p);
        var scratch = Path.Combine(_dir, "scratch");
        foreach (var file in new[] { report, budget })
        {
            var pdf = Path.Combine(scratch, Path.GetFileName(file) + ".pdf");
            Directory.CreateDirectory(scratch);
            Sloperator.Platform.Windows.OfficePdf.Convert(file, pdf, null, CancellationToken.None);
            using var pages = PdfPages.Open(pdf);
            Assert.Single(pages.Sizes);
        }
        // Excel worked the sum out: its text is in the PDF.
        var sheet = Path.Combine(scratch, "budget.xlsx.pdf");
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(sheet))
            Assert.Contains("2400", string.Join(" ", pdf.GetPages().Select(p => p.Text)));

        File.WriteAllText(budget + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        Assert.Contains("downloaded from the internet", Assert.Throws<InvalidOperationException>(() =>
            Sloperator.Platform.Windows.OfficePdf.Convert(budget, Path.Combine(scratch, "marked.pdf"), null, CancellationToken.None)).Message);
    }

    [Fact]
    public void Libreoffice_is_fetched_at_its_newest_version()
    {
        // As download.documentfoundation.org/libreoffice/stable/ listed them on 2 November 2026, with a
        // later 26.8.10 put before an older one: versions are compared as numbers, not as text.
        const string listing = """
            <a href="?C=N;O=D">Name</a> <a href="/libreoffice/">Parent Directory</a>
            <a href="25.8.7/">25.8.7/</a> <a href="26.2.6/">26.2.6/</a> <a href="26.8.10/">26.8.10/</a>
            <a href="26.8.1/">26.8.1/</a> <a href="26.8.2/">26.8.2/</a> <a href="mailto:hostmaster@documentfoundation.org">
            """;
        Assert.Equal("26.8.10", Sloperator.Platform.Windows.LibreOfficeFetch.Newest(listing));
        Assert.Null(Sloperator.Platform.Windows.LibreOfficeFetch.Newest("<html>Not found</html>"));
    }

    [Fact]
    public void A_libreoffice_unpacked_into_the_tools_folder_is_found()
    {
        var tools = Path.Combine(_dir, "tools");
        // msiexec /a puts the package's folders under TARGETDIR as they are.
        var soffice = Path.Combine(tools, "LibreOffice", "PFiles", "LibreOffice", "program", "soffice.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(soffice)!);
        File.WriteAllText(soffice, "");
        var found = Sloperator.Platform.Windows.OfficePdf.LibreOffice(tools);
        // One installed in Program Files is used first.
        if (Sloperator.Platform.Windows.OfficePdf.LibreOffice(null) is null)
            Assert.Equal(soffice, found);
        else
            Assert.NotNull(found);
    }

    /// <summary>
    /// Opt-in (set BB_LIBREOFFICE to a soffice.exe, which runs it hidden for a few seconds): LibreOffice
    /// turns our documents into pages, Excel's with its sums worked out, as on a PC without Office.
    /// </summary>
    [Fact]
    public void Libreoffice_turns_documents_into_pages_unseen()
    {
        var soffice = Environment.GetEnvironmentVariable("BB_LIBREOFFICE") ?? "";
        Assert.SkipWhen(soffice.Length == 0, "Set BB_LIBREOFFICE to a soffice.exe to run.");
        var report = Path.Combine(_dir, "report.docx");
        DocumentFiles.Write(report, "# Report\n\n| Region | Q3 |\n|---|---|\n| North | 140 |\n", append: false, p => p);
        var budget = Path.Combine(_dir, "budget.xlsx");
        DocumentFiles.Write(budget, "# Budget\n\n| Item | Jan | Feb | Total |\n|---|---|---|---|\n| Rent | 1200 | 1200 | =SUM(B2:C2) |\n", append: false, p => p);
        var deck = Path.Combine(_dir, "deck.pptx");
        DocumentFiles.Write(deck, "# Plan\n\n- One\n- Two\n", append: false, p => p);
        var scratch = Path.Combine(_dir, "scratch");
        Directory.CreateDirectory(scratch);
        foreach (var file in new[] { report, budget, deck })
        {
            var pdf = Path.Combine(scratch, Path.GetFileName(file) + ".pdf");
            Sloperator.Platform.Windows.OfficePdf.RunLibreOffice(soffice, file, pdf, CancellationToken.None);
            using var pages = PdfPages.Open(pdf);
            Assert.NotEmpty(pages.Sizes);
        }
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(Path.Combine(scratch, "budget.xlsx.pdf")))
            Assert.Contains("2400", string.Join(" ", pdf.GetPages().Select(p => p.Text)));
    }
#endif
}

[CollectionDefinition(nameof(DocumentCaptureTests), DisableParallelization = true)]
public sealed class DocumentCaptureCollection;
