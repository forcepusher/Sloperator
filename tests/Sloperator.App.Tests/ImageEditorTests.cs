using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

            // Pressed again before it is made, it is the same new picture, picked again: not a second empty one.
            vm.Unpick();
            vm.NewCommand.Execute(null);
            Assert.Same(picture, Assert.Single(vm.Items));
            Assert.NotNull(vm.About([picture.Folder]));
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

    [Fact]
    public void The_panel_opens_on_the_newest_picture_and_prompt_metadata_waits_for_its_button()
    {
        // It opened on "Pick a picture" beside the pictures, with a column for Prompt metadata a quarter of its width.
        Directory.CreateDirectory(Path.Combine(Root, "Old"));
        Png(Path.Combine(Root, "Old", "a.png"), minutesAgo: 30);
        Directory.CreateDirectory(Path.Combine(Root, "New"));
        Png(Path.Combine(Root, "New", "a.png"), minutesAgo: 9);
        Png(Path.Combine(Root, "New", "b.png"), minutesAgo: 5);
        HeadlessApp.Run(() =>
        {
            using var vm = new ImageEditorViewModel(null, Root, Picks);
            var panel = new ImageEditorPanel { DataContext = vm };
            var window = new Window { Width = 880, Height = 600, Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Shown, its newest version; nothing goes into the message for it until it is clicked.
            Assert.Equal("New", vm.Selected?.Name);
            Assert.Equal(2, vm.Shown?.Number);
            Assert.Null(vm.About([Path.Combine(Root, "New", "b.png")]));
            Wait(() => vm.HasPicture);

            // A plain picture says nothing of how it was made: the button is there, greyed, and says why.
            Assert.False(vm.ShowRecipe);
            var button = panel.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Prompt metadata");
            Assert.False(button.IsEnabled);
            Assert.Equal("This version doesn't say how it was made", vm.PromptTip);
            Assert.False(panel.FindControl<Border>("RecipeBox")!.IsVisible);

            // The one shown going leaves the next one shown.
            Directory.Delete(Path.Combine(Root, "New"), recursive: true);
            vm.Refresh();
            Assert.Equal("Old", vm.Selected?.Name);
            window.Close();
            return 0;
        });
    }

    /// <summary>A picture file that says how it was made, as ComfyUI's do: the prompt in a text chunk in front of the pixels.</summary>
    private static void Made(string path, string prompt, int minutesAgo)
    {
        Png(path, minutesAgo);
        var png = File.ReadAllBytes(path);
        var graph = """{"3": {"inputs": {"seed": 7, "steps": 10, "cfg": 7, "sampler_name": "euler", "scheduler": "normal", "denoise": 1, "model": ["4", 0], "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["5", 0]}, "class_type": "KSampler"}, """ +
                    """ "4": {"inputs": {"ckpt_name": "sd15.safetensors"}, "class_type": "CheckpointLoaderSimple"}, "5": {"inputs": {"width": 64, "height": 48, "batch_size": 1}, "class_type": "EmptyLatentImage"}, """ +
                    """ "6": {"inputs": {"text": "PROMPT", "clip": ["4", 1]}, "class_type": "CLIPTextEncode"}, "7": {"inputs": {"text": "blurry", "clip": ["4", 1]}, "class_type": "CLIPTextEncode"}}""";
        var body = System.Text.Encoding.Latin1.GetBytes("prompt\0" + graph.Replace("PROMPT", prompt));
        var head = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(head, body.Length);
        System.Text.Encoding.ASCII.GetBytes("tEXt").CopyTo(head, 4);
        // After the IHDR chunk (8 + 25 bytes in); the CRC is four bytes nothing here checks.
        File.WriteAllBytes(path, [.. png[..33], .. head, .. body, 0, 0, 0, 0, .. png[33..]]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
    }

    [Fact]
    public void An_untitled_picture_is_called_by_what_it_shows_and_each_version_has_its_small_copy()
    {
        // Every picture was "Untitled picture", "Untitled picture 2", and its versions were numbers.
        Directory.CreateDirectory(Path.Combine(Root, "Untitled picture 2"));
        Made(Path.Combine(Root, "Untitled picture 2", "a.png"), "a white lighthouse on a green cliff by the sea, blue sky, oil painting", minutesAgo: 9);
        // A later version is a part redrawn: its prompt says what went there, not what the picture is.
        Made(Path.Combine(Root, "Untitled picture 2", "b.png"), "a red hot air balloon", minutesAgo: 5);
        Directory.CreateDirectory(Path.Combine(Root, "Harbour"));
        Made(Path.Combine(Root, "Harbour", "a.png"), "a harbour at dawn, fishing boats", minutesAgo: 30);
        Directory.CreateDirectory(Path.Combine(Root, "Untitled picture"));
        Png(Path.Combine(Root, "Untitled picture", "a.png"), minutesAgo: 40);
        HeadlessApp.Run(() =>
        {
            using var vm = new ImageEditorViewModel(null, Root, Picks);
            var lighthouse = vm.Selected!;
            Assert.Equal(lighthouse.Folder, Path.Combine(Root, "Untitled picture 2"));
            Wait(() => lighthouse.Name != "Untitled picture 2");
            Assert.Equal("A white lighthouse on a green cliff by the sea", lighthouse.Name);
            Assert.Equal("A white lighthouse on a green cliff by the sea · version 2 of 2", vm.Title);
            // A name of its own stays, and so does "Untitled picture" where the file doesn't say what it shows.
            Assert.Equal(["A white lighthouse on a green cliff by the sea", "Harbour", "Untitled picture"], vm.Items.Select(i => i.Name));

            // On its card in the box and to the model it goes by that name; the folder keeps its own.
            vm.Pick(lighthouse);
            var (via, context) = vm.About([Path.Combine(Root, "Untitled picture 2", "b.png")])!.Value;
            Assert.Equal("Image editor · A white lighthouse on a green cliff by the sea · version 2", via);
            Assert.Contains($"save_to set to the picture's folder, \"{Path.Combine(Root, "Untitled picture 2")}\"", context);

            Wait(() => vm.Versions.All(v => v.Thumbnail is not null));
            Assert.Equal(2, vm.Versions.Count);
            // Another picture shown: the strip is its versions, and the ones that left hold nothing.
            var left = vm.Versions.ToList();
            vm.Selected = vm.Items[1];
            Assert.All(left, v => Assert.Null(v.Thumbnail));
            Wait(() => vm.Versions.Count == 1 && vm.Versions[0].Thumbnail is not null);
            return 0;
        });
    }
}
