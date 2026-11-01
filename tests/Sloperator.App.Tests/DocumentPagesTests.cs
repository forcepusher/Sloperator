using Avalonia.Threading;
using Sloperator.App.ViewModels;
using Sloperator.Core.Documents;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// A Word, Excel or PowerPoint file no previewer on the PC draws, or whose previewer fails: the File
/// inspector shows its pages, turned into a PDF as capture_result's pictures of documents are, and the
/// PDF goes. With no program for it, its text and Get LibreOffice, after which its pages show; a file
/// downloaded from the internet isn't opened until Preview anyway, which turns a copy without the mark.
/// </summary>
public sealed class DocumentPagesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-document-pages-").FullName;
    private readonly Action<string, string, string?, CancellationToken>? _converter = DocumentPictures.Converter;
    private readonly Func<string, Action<string>, CancellationToken, Task>? _fetcher = DocumentPictures.Fetcher;
    private readonly Func<string, IPdfDocument>? _opener = PdfPages.Opener;

    public void Dispose()
    {
        (DocumentPictures.Converter, DocumentPictures.Fetcher, PdfPages.Opener) = (_converter, _fetcher, _opener);
        Directory.Delete(_dir, recursive: true);
    }

    private string Rtf(string name, bool downloaded)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, @"{\rtf1\ansi Rent 1200\par}");
        if (downloaded)
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/report.rtf\r\n");
        return path;
    }

    /// <summary>Two letter-size pages, all white: what is drawn doesn't matter here.</summary>
    private sealed class TwoPages : IPdfDocument
    {
        public IReadOnlyList<(double Width, double Height)> Sizes { get; } = [(612, 792), (612, 792)];

        public (byte[] Bgra, int Width, int Height)? Draw(int index, int pageWidth, int pageHeight, int x, int y, int width, int height) =>
            (Enumerable.Repeat((byte)255, width * height * 4).ToArray(), width, height);

        public void Dispose() { }
    }

    [Fact]
    public void A_document_without_a_previewer_shows_its_pages_and_offers_libreoffice_without_a_program()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var tools = Path.Combine(_dir, "tools");
        var report = Rtf("report.rtf", downloaded: false);
        var asked = new List<(string Source, string? Tools)>();
        var made = new List<string>();
        Action<string, string, string?, CancellationToken> converter = (source, pdf, folder, _) =>
        {
            asked.Add((source, folder));
            made.Add(pdf);
            File.WriteAllText(pdf, "%PDF-1.7");
        };
        DocumentPictures.Converter = converter;
        PdfPages.Opener = _ => new TwoPages();
        HeadlessApp.Run(() =>
        {
            var vm = new FileInspectorViewModel(new PreviewServices(null, () => null, tools));
            vm.Show(report);
            Wait(vm, () => vm.Pages is not null);
            Assert.Equal(2, vm.Pages!.Count);
            Assert.StartsWith("2 pages as printed, ", vm.Details);
            Assert.Equal([(report, tools)], asked);
            Assert.False(File.Exists(made[0]));
            Assert.False(vm.NeedsProgram);
            Assert.True(vm.CanShowText);

            // No Office, no LibreOffice: its text, and the way to draw it.
            DocumentPictures.Converter = (_, _, _, _) => throw new DocumentPictures.NoProgramException("No program on this PC turns report.rtf into pages.");
            vm.Show(report);
            Wait(vm, () => vm.Details.Contains("its text only"));
            Assert.True(vm.NeedsProgram);
            Assert.Null(vm.Pages);
            Assert.Contains("no program here draws its pages", vm.Details);
            Assert.Contains("Rent 1200", vm.Text);

            // Get LibreOffice: how it goes is said, then the document is drawn by it.
            var fetched = new List<string>();
            DocumentPictures.Fetcher = async (folder, said, _) =>
            {
                fetched.Add(folder);
                said("Downloading LibreOffice_26.8.2_Win_x86-64.msi… 12 of 360 MB");
                await Task.Delay(50);
                DocumentPictures.Converter = converter;
            };
            Assert.True(vm.GetLibreOfficeCommand.CanExecute(null));
            vm.GetLibreOfficeCommand.Execute(null);
            Wait(vm, () => vm.ProgramText.StartsWith("Downloading"));
            Assert.False(vm.GetLibreOfficeCommand.CanExecute(null));
            Wait(vm, () => vm.Pages is not null);
            Assert.Equal([tools], fetched);
            Assert.False(vm.NeedsProgram);

            // Downloaded from the internet: not opened, until Preview anyway, which turns a copy without the mark.
            var marked = Rtf("marked.rtf", downloaded: true);
            asked.Clear();
            vm.Show(marked);
            Wait(vm, () => vm.PreviewBlocked && vm.Details.Contains("its text only"));
            Assert.Empty(asked);
            vm.PreviewAnywayCommand.Execute(null);
            Wait(vm, () => vm.Pages is not null);
            var copy = Assert.Single(asked).Source;
            Assert.NotEqual(marked, copy);
            Assert.Equal("marked.rtf", Path.GetFileName(copy));
            Assert.Equal(marked, vm.Path);

            // A converter that fails says why over the text.
            DocumentPictures.Converter = (_, _, _, _) => throw new InvalidOperationException("Word couldn't turn report.rtf into pages: it is damaged.");
            vm.Show(report);
            Wait(vm, () => vm.Details.Contains("its text only"));
            Assert.Contains("it is damaged", vm.Details);
            Assert.False(vm.NeedsProgram);
            return true;
        });
    }

    /// <summary>
    /// Waits for <paramref name="done"/>. Where Windows has a previewer for the kind (Word's, with Office),
    /// it is made to fail, as without one the file goes straight to its pages.
    /// </summary>
    private static void Wait(FileInspectorViewModel vm, Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
            if (vm.ShellPath is not null)
                vm.ShellFailed("Class not registered");
        }
        Assert.True(done());
    }
}
