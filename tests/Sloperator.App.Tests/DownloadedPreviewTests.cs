using Avalonia.Threading;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// A document downloaded from the internet, which Windows' previewers don't draw (Excel's says only
/// E_FAIL): the inspector says so over its text, and Preview anyway draws a copy without the mark,
/// which goes again with the file shown. The file keeps its mark.
/// </summary>
public sealed class DownloadedPreviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-downloaded-preview-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Page(string name, bool downloaded)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "<html><body><h1>Budget</h1><p>Rent 1200</p></body></html>");
        if (downloaded)
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/page.html\r\n");
        return path;
    }

    [Fact]
    public void A_downloaded_file_the_previewer_fails_on_is_said_and_can_be_previewed_anyway()
    {
        if (!OperatingSystem.IsWindows() || !ShellPreviewHost.CanShow("page.html"))
            return;
        var page = Page("page.html", downloaded: true);
        var mine = Page("mine.html", downloaded: false);
        Assert.True(ShellPreviewHost.Downloaded(page));
        Assert.False(ShellPreviewHost.Downloaded(mine));
        HeadlessApp.Run(() =>
        {
            var vm = new FileInspectorViewModel(new PreviewServices(null, () => null));
            vm.Show(page);
            Wait(() => vm.ShellPath == page);

            // The previewer fails on it: said, over its text, with the way past it.
            vm.ShellFailed("Unexpected HRESULT has been returned from a call to a COM component.");
            Assert.True(vm.PreviewBlocked);
            Wait(() => vm.Details.Contains("its text only"));
            Assert.Contains("Windows doesn't preview files downloaded from the internet", vm.Details);
            Assert.DoesNotContain("HRESULT", vm.Details);

            // Preview anyway: a copy without the mark is drawn; the file keeps its mark.
            vm.PreviewAnywayCommand.Execute(null);
            var copy = vm.ShellPath!;
            Assert.NotEqual(page, copy);
            Assert.Equal("page.html", Path.GetFileName(copy));
            Assert.Equal(File.ReadAllBytes(page), File.ReadAllBytes(copy));
            Assert.False(ShellPreviewHost.Downloaded(copy));
            Assert.True(ShellPreviewHost.Downloaded(page));
            Assert.False(vm.PreviewBlocked);
            Assert.Equal(page, vm.Path);

            // The copy failing too failed for its own reason: said as it is.
            vm.ShellFailed("Class not registered");
            Assert.False(vm.PreviewBlocked);
            Wait(() => vm.Details.Contains("its text only"));
            Assert.Contains("the previewer for it failed: Class not registered", vm.Details);

            // Another file: the copy goes. One not downloaded that fails is said as it is.
            vm.Show(mine);
            Assert.False(File.Exists(copy));
            Wait(() => vm.ShellPath == mine);
            vm.ShellFailed("Class not registered");
            Assert.False(vm.PreviewBlocked);
            return true;
        });
    }

    private static void Wait(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(done());
    }
}
