using Avalonia.Controls;
using Avalonia.Threading;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Sloperator.Core.Media;
using Sloperator.Platform.Windows;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>The Audio editor panel: its sounds and versions, what a pick tells the model, the edits each made a new version.</summary>
public sealed class AudioEditorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-audio-editor-").FullName;
    private string Root => Path.Combine(_dir, "sounds");
    private string Picks => Path.Combine(_dir, "pasted");

    private static PreviewServices Previews() => new(new MediaFoundationDecoder(), () => null);

    /// <summary>The folder goes; a sound's small waveform may still be read on the pool as a test ends.</summary>
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

    /// <summary>Two seconds of a quarter-loud steady sound, mono at 8 kHz.</summary>
    private static void Wav(string path, int minutesAgo, double seconds = 2)
    {
        var samples = Enumerable.Repeat(0.25f, (int)(seconds * 8000)).ToArray();
        SoundEdit.WriteWav(path, new SoundSamples(samples, 8000, 1));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
    }

    private static void Wait(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(done());
    }

    [Fact]
    public void A_new_sound_is_picked_and_the_model_is_told_where_to_save_it()
    {
        HeadlessApp.Run(() =>
        {
            using var vm = new AudioEditorViewModel(null, Previews(), Root, Picks);
            Assert.False(vm.HasItems);
            Assert.Contains("No sounds yet", vm.EmptyText);
            vm.NewCommand.Execute(null);
            var sound = Assert.Single(vm.Items);
            Assert.Same(sound, vm.Selected);
            Assert.Equal("Untitled sound · not made yet", vm.Title);
            var (via, context) = vm.About([sound.Folder])!.Value;
            Assert.Equal("Audio editor · Untitled sound · new", via);
            Assert.Contains($"save_to set to the sound's folder, \"{sound.Folder}\"", context);
            Assert.False(vm.MakeAgainCommand.CanExecute(null));
            Assert.False(vm.LouderCommand.CanExecute(null));
            return 0;
        });
    }

    [Fact]
    public void Versions_are_listed_the_newest_shown_and_a_picked_one_told_with_how_to_edit_it()
    {
        var folder = Path.Combine(Root, "Rain");
        Directory.CreateDirectory(folder);
        Wav(Path.Combine(folder, "a.wav"), 10);
        Wav(Path.Combine(folder, "b.wav"), 5);
        HeadlessApp.Run(() =>
        {
            using var vm = new AudioEditorViewModel(null, Previews(), Root, Picks);
            var sound = Assert.Single(vm.Items);
            Assert.Equal("2 versions", sound.Count);
            vm.Pick(sound);
            Assert.True(vm.Versions[1].IsShown);
            Wait(() => vm.HasSound);
            Assert.Equal("Rain · version 2 of 2", vm.Title);
            Assert.Equal("the whole sound", vm.EditsOn);
            Wait(() => sound.Peaks is not null);
            Assert.Equal(AudioItem.ThumbColumns, sound.Peaks!.Length);

            vm.Pick(sound, vm.Versions[0]);
            var (via, context) = vm.About([vm.Versions[0].Path])!.Value;
            Assert.Equal("Audio editor · Rain · version 1", via);
            Assert.Contains("version 1 of 2", context);
            Assert.Contains($"edit_sound with path \"{vm.Versions[0].Path}\"", context);
            Assert.Contains($"2: {Path.Combine(folder, "b.wav")}", context);
            Wait(() => vm.Sound?.Path == vm.Versions[0].Path);
            return 0;
        });
    }

    [Fact]
    public void Edits_are_new_versions_and_a_stretch_stays_picked_through_one_that_keeps_the_length()
    {
        var folder = Path.Combine(Root, "Rain");
        Directory.CreateDirectory(folder);
        Wav(Path.Combine(folder, "rain.wav"), 1);
        HeadlessApp.Run(() =>
        {
            using var vm = new AudioEditorViewModel(null, Previews(), Root, Picks);
            vm.Pick(vm.Items[0]);
            Wait(() => vm.HasSound);
            // The whole sound: louder works on all of it, a cut needs a stretch.
            Assert.False(vm.CutCommand.CanExecute(null));
            Assert.Equal("Drag over a stretch of the sound first", vm.CutTip);
            vm.LouderCommand.Execute(null);
            Wait(() => vm.Versions.Count == 2 && vm.Sound?.Path == vm.Versions[1].Path);
            Assert.Equal(Path.Combine(folder, "+3 dB.wav"), vm.Versions[1].Path);
            Assert.Equal("Rain · version 2 of 2", vm.Title);

            // A stretch dragged over: into the box as its picture, and the edits on it.
            vm.Sound!.Pick((0.5, 1.0));
            Assert.Equal("the stretch picked", vm.EditsOn);
            Assert.True(vm.CutCommand.CanExecute(null));
            Wait(() => Directory.Exists(Picks) && Directory.GetFiles(Picks).Length == 1);
            var picture = Directory.GetFiles(Picks).Single();
            Wait(() => vm.About([picture]) is not null);
            var (via, context) = vm.About([picture])!.Value;
            Assert.Equal("Audio editor · Rain · 0.50-1.00 s of version 2", via);
            Assert.Contains("from 0.500 and to 1.000", context);

            // Quieter keeps its length: the stretch is picked again on the version it makes.
            vm.QuieterCommand.Execute(null);
            Assert.False(File.Exists(picture));
            Wait(() => vm.Versions.Count == 3 && vm.Sound?.Path == vm.Versions[2].Path && vm.Sound.HasRange);
            Assert.Equal(Path.Combine(folder, "-3 dB 0.50-1.00 s.wav"), vm.Versions[2].Path);
            Assert.Equal((0.5, 1.0), vm.Sound!.Range!.Value);

            // A cut doesn't: the stretch goes, and the sound is shorter.
            vm.CutCommand.Execute(null);
            Wait(() => vm.Versions.Count == 4 && vm.Sound?.Path == vm.Versions[3].Path);
            Assert.Equal(1.5, vm.Sound!.Length, 2);
            Assert.False(vm.Sound.HasRange);
            Assert.Equal("4 versions", vm.Items[0].Count);
            return 0;
        });
    }

    [Fact]
    public void The_panel_lays_out_with_its_bar_and_columns()
    {
        HeadlessApp.Run(() =>
        {
            using var vm = new AudioEditorViewModel(null, Previews(), Root, Picks);
            var window = new Window { Width = 880, Height = 600, Content = new AudioEditorPanel { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("No sounds yet", vm.EmptyText);
            window.Close();
            return 0;
        });
    }
}
