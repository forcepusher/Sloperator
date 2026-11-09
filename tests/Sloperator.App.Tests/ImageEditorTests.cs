using Avalonia.Controls;
using Avalonia.Threading;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Sloperator.Core.Documents;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>The Image editor panel: its pictures and versions, what a pick tells the model, a part marked to redraw.</summary>
public sealed class ImageEditorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-picture-gen-").FullName;
    private string Root => Path.Combine(_dir, "pictures");
    private string Picks => Path.Combine(_dir, "pasted");

    /// <summary>The folder goes; a picture's thumbnail may still be read on the pool as a test ends (c.png held, on the workstation).</summary>
    public void Dispose()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void Png(string path, int minutesAgo)
    {
        DocumentArea.SavePng(new RawImage(64, 48, Enumerable.Repeat((byte)200, 64 * 48 * 4).ToArray()), path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
    }

    private static void Wait(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(done());
    }

    [Fact]
    public void A_new_picture_is_picked_and_the_model_is_told_where_to_save_it()
    {
        HeadlessApp.Run(() =>
        {
            using var vm = new ImageEditorViewModel(null, Root, Picks);
            Assert.False(vm.HasItems);
            Assert.False(vm.MakeAgainCommand.CanExecute(null));
            vm.NewCommand.Execute(null);
            var picture = Assert.Single(vm.Items);
            Assert.Same(picture, vm.Selected);
            Assert.Equal("Untitled picture · not made yet", vm.Title);
            var (via, context) = vm.About([picture.Folder])!.Value;
            Assert.Equal("Image editor · Untitled picture · new", via);
            Assert.Contains($"save_to set to the picture's folder, \"{picture.Folder}\"", context);
            Assert.False(vm.MakeAgainCommand.CanExecute(null));
            return 0;
        });
    }

    [Fact]
    public void Versions_are_listed_the_newest_shown_and_a_picked_one_told_with_the_rest()
    {
        var folder = Path.Combine(Root, "Lighthouse");
        Directory.CreateDirectory(folder);
        Png(Path.Combine(folder, "a.png"), 10);
        Png(Path.Combine(folder, "b.png"), 5);
        HeadlessApp.Run(() =>
        {
            using var vm = new ImageEditorViewModel(null, Root, Picks);
            var picture = Assert.Single(vm.Items);
            Assert.Equal("2 versions", picture.Count);
            vm.Pick(picture);
            Assert.Equal(2, vm.Versions.Count);
            Assert.True(vm.Versions[1].IsShown);
            Wait(() => vm.HasPicture);
            Assert.Equal("Lighthouse · version 2 of 2", vm.Title);
            Assert.True(vm.MakeAgainCommand.CanExecute(null));

            vm.Pick(picture, vm.Versions[0]);
            Assert.True(vm.Versions[0].IsShown);
            var (via, context) = vm.About([vm.Versions[0].Path])!.Value;
            Assert.Equal("Image editor · Lighthouse · version 1", via);
            Assert.Contains("version 1 of 2", context);
            Assert.Contains($"2: {Path.Combine(folder, "b.png")}", context);

            // A version the agent's run adds is listed and shown, once the folder is read again (the watcher's
            // note, 250 ms after the writing rests; the headless dispatcher's timers don't run here).
            Png(Path.Combine(folder, "c.png"), 0);
            vm.Refresh();
            Assert.Equal(3, vm.Versions.Count);
            Assert.True(vm.Versions[2].IsShown);
            Assert.Equal("3 versions", picture.Count);
            // Read before the test ends: its folder is deleted then.
            Wait(() => vm.HasPicture);
            return 0;
        });
    }

    [Fact]
    public void A_part_dragged_over_goes_in_with_its_mask_and_how_to_redraw_it()
    {
        var folder = Path.Combine(Root, "House");
        Directory.CreateDirectory(folder);
        var version = Path.Combine(folder, "v1.png");
        Png(version, 1);
        HeadlessApp.Run(() =>
        {
            using var vm = new ImageEditorViewModel(null, Root, Picks);
            vm.Pick(vm.Items[0]);
            Wait(() => vm.HasPicture);
            vm.PickArea(new DocumentArea(0.5, 0, 1, 0.5));
            Wait(() => vm.About(Directory.Exists(Picks) ? Directory.GetFiles(Picks) : []) is not null);
            var crop = Directory.GetFiles(Picks, "Area *.png").Single();
            var mask = Directory.GetFiles(Picks, "Mask *.png").Single();
            Assert.Equal((32, 24), PictureFolder.SizeOf(crop));
            Assert.Equal((64, 48), PictureFolder.SizeOf(mask));
            var (via, context) = vm.About([crop])!.Value;
            Assert.Equal("Image editor · House · area of version 1", via);
            Assert.Contains("x 32-64, y 0-24 of the 64x48 pixels", context);
            Assert.Contains("name=sloperator/redraw_area", context);
            Assert.Contains($"5.image to the mask \"{mask}\"", context);

            // Unpicked before sending, its files go.
            Assert.True(vm.Unpick());
            Assert.Empty(Directory.GetFiles(Picks));
            return 0;
        });
    }

    [Fact]
    public void The_panel_lays_out_with_its_bar_and_columns()
    {
        HeadlessApp.Run(() =>
        {
            using var vm = new ImageEditorViewModel(null, Root, Picks);
            var window = new Window { Width = 880, Height = 600, Content = new ImageEditorPanel { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("No pictures yet", vm.EmptyText);
            window.Close();
            return 0;
        });
    }
}
