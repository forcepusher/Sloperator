using System.Text.Json.Nodes;
using Sloperator.Core.Media;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>The Audio editor's sounds and edits: versions in a folder each, cut, keep, fade, louder, quieter and replace, saved beside the sound.</summary>
public sealed class SoundEditTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-sounds-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A second of a steady tone at <paramref name="level"/>, at 1000 samples a second (so frame n is n ms), in <paramref name="channels"/> channels.</summary>
    private static SoundSamples Tone(double seconds = 1, float level = 0.5f, int channels = 1, int rate = 1000)
    {
        var frames = (int)(seconds * rate);
        var s = new float[frames * channels];
        for (var f = 0; f < frames; f++)
            for (var c = 0; c < channels; c++)
                s[f * channels + c] = level;
        return new SoundSamples(s, rate, channels);
    }

    [Fact]
    public void A_sounds_versions_are_its_sound_files_oldest_first()
    {
        var first = SoundFolder.New(_dir);
        var second = SoundFolder.New(_dir);
        Assert.Equal("Untitled sound", Path.GetFileName(first));
        Assert.Equal("Untitled sound 2", Path.GetFileName(second));
        var a = Path.Combine(first, "ComfyUI_00001_.mp3");
        var b = Path.Combine(first, "Cut 1.00-2.00 s.wav");
        File.WriteAllText(a, "");
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(b, "");
        File.WriteAllText(Path.Combine(first, "notes.txt"), "not a version");
        File.WriteAllText(b + ".part", "half written");
        Assert.Equal([a, b], SoundFolder.Versions(first));
        Assert.Equal([first, second], SoundFolder.All(_dir));
        Assert.True(SoundFolder.IsVersion(b, _dir));
        Assert.False(SoundFolder.IsVersion(Path.Combine(_dir, "loose.wav"), _dir));
        Assert.False(SoundFolder.IsVersion(b, null));
    }

    [Fact]
    public void A_cut_takes_the_stretch_out_and_crossfades_the_join()
    {
        // 0.2 on the first half, 0.8 on the second: the cut of 300-700 ms leaves 600 ms, the join a smooth step.
        var s = Tone(1, 0.2f);
        for (var f = 500; f < 1000; f++)
            s.Samples[f] = 0.8f;
        var cut = SoundEdit.Apply(s, SoundEditKind.Cut, 0.3, 0.7);
        Assert.Equal(600, cut.Frames);
        Assert.Equal(0.2f, cut.Samples[0], 4);
        Assert.Equal(0.8f, cut.Samples[^1], 4);
        // The 10 ms either side of the join go from one to the other without a jump.
        for (var f = 290; f < 310; f++)
            Assert.InRange(cut.Samples[f + 1] - cut.Samples[f], -0.0001f, 0.1f);
    }

    [Fact]
    public void Keep_only_keeps_the_stretch_faded_in_and_out_where_it_was_cut()
    {
        var kept = SoundEdit.Apply(Tone(1, 0.5f, channels: 2), SoundEditKind.Keep, 0.25, 0.75);
        Assert.Equal(500, kept.Frames);
        Assert.Equal(2, kept.Channels);
        Assert.True(kept.Samples[0] < 0.1f);
        Assert.True(kept.Samples[^1] < 0.1f);
        Assert.Equal(0.5f, kept.Samples[2 * 250], 4);
        // Kept from the very start, nothing fades there.
        Assert.Equal(0.5f, SoundEdit.Apply(Tone(), SoundEditKind.Keep, 0, 0.5).Samples[0], 4);
    }

    [Fact]
    public void Fades_go_from_silence_to_full_over_the_stretch_or_the_first_and_last_second()
    {
        var s = Tone(3);
        var (from, to) = SoundEdit.Stretch(new SoundEditRequest(SoundEditKind.FadeIn), s.Seconds);
        Assert.Equal((0.0, 1.0), (from, to));
        Assert.Equal((2.0, 3.0), SoundEdit.Stretch(new SoundEditRequest(SoundEditKind.FadeOut), s.Seconds));
        // Half of a sound under two seconds.
        Assert.Equal((0.75, 1.5), SoundEdit.Stretch(new SoundEditRequest(SoundEditKind.FadeOut), 1.5));
        var faded = SoundEdit.Apply(s, SoundEditKind.FadeIn, from, to);
        Assert.Equal(s.Frames, faded.Frames);
        Assert.True(faded.Samples[0] < 0.001f);
        Assert.InRange(faded.Samples[500], 0.24f, 0.26f);
        Assert.Equal(0.5f, faded.Samples[1500], 4);
        var outro = SoundEdit.Apply(s, SoundEditKind.FadeOut, 2, 3);
        Assert.Equal(0.5f, outro.Samples[1000], 4);
        Assert.True(outro.Samples[^1] < 0.001f);
    }

    [Fact]
    public void Louder_and_quieter_change_the_stretch_and_never_clip()
    {
        var s = Tone(1, 0.25f);
        var louder = SoundEdit.Apply(s, SoundEditKind.Volume, 0, 1, db: 6);
        Assert.InRange(louder.Samples[500], 0.49f, 0.51f);
        var quieter = SoundEdit.Apply(s, SoundEditKind.Volume, 0.4, 0.6, db: -6);
        Assert.Equal(0.25f, quieter.Samples[100], 4);
        Assert.InRange(quieter.Samples[500], 0.12f, 0.13f);
        // At the stretch's edges it goes there over 10 ms rather than in a step.
        Assert.InRange(quieter.Samples[400], 0.125f, 0.25f);
        // A loud sound made much louder is rounded off under 1, never clipped.
        var loud = SoundEdit.Apply(Tone(1, 0.9f), SoundEditKind.Volume, 0, 1, db: 12);
        Assert.All(loud.Samples, v => Assert.InRange(v, 0.9f, 1f));
    }

    [Fact]
    public void Replace_puts_another_sound_in_the_stretch_crossfaded_at_both_joins()
    {
        var s = Tone(1, 0.2f);
        var other = Tone(0.5, 0.8f);
        var replaced = SoundEdit.Apply(s, SoundEditKind.Replace, 0.4, 0.6, with: other);
        // 400 + 500 + 400 frames, less the two 10 ms overlaps.
        Assert.Equal(1280, replaced.Frames);
        Assert.Equal(0.2f, replaced.Samples[100], 4);
        Assert.Equal(0.8f, replaced.Samples[640], 4);
        Assert.Equal(0.2f, replaced.Samples[^1], 4);
    }

    [Fact]
    public void A_sound_is_fitted_to_the_rate_and_channels_it_goes_into()
    {
        var stereo = new SoundSamples([0.2f, 0.6f, 0.2f, 0.6f, 0.2f, 0.6f, 0.2f, 0.6f], 4, 2);
        var mono = SoundEdit.Convert(stereo, 8, 1);
        Assert.Equal(8, mono.Frames);
        Assert.All(mono.Samples, v => Assert.Equal(0.4f, v, 4));
        var back = SoundEdit.Convert(new SoundSamples([0f, 1f], 2, 1), 4, 2);
        Assert.Equal([0f, 0f, 0.5f, 0.5f, 1f, 1f, 1f, 1f], back.Samples);
    }

    [Fact]
    public void Edits_that_make_no_sense_say_why()
    {
        Assert.Throws<SoundEditException>(() => SoundEdit.Stretch(new SoundEditRequest(SoundEditKind.Cut), 5));
        Assert.Throws<SoundEditException>(() => SoundEdit.Stretch(new SoundEditRequest(SoundEditKind.Keep), 5));
        Assert.Contains("all of it", Assert.Throws<SoundEditException>(() => SoundEdit.Stretch(new SoundEditRequest(SoundEditKind.Cut, 0, 9), 5)).Message);
        Assert.Contains("empty", Assert.Throws<SoundEditException>(() => SoundEdit.Stretch(new SoundEditRequest(SoundEditKind.Cut, 7, 9), 5)).Message);
        Assert.Throws<SoundEditException>(() => SoundEdit.Stretch(new SoundEditRequest(SoundEditKind.Volume), 5));
        // From and to the wrong way round, and past the end, are taken as meant.
        Assert.Equal((2.0, 5.0), SoundEdit.Stretch(new SoundEditRequest(SoundEditKind.Cut, 9, 2), 5));
        // The whole sound louder; a point to put a sound in.
        Assert.Equal((0.0, 5.0), SoundEdit.Stretch(new SoundEditRequest(SoundEditKind.Volume, Db: 3), 5));
        Assert.Equal((1.0, 1.0), SoundEdit.Stretch(new SoundEditRequest(SoundEditKind.Replace, 1, 1, With: "x.wav"), 5));
    }

    [Fact]
    public void An_edit_is_named_after_what_it_did_beside_the_sound()
    {
        var sounds = Path.Combine(_dir, "sounds");
        var rain = Path.Combine(sounds, "Rain");
        Directory.CreateDirectory(rain);
        var version = Path.Combine(rain, "ComfyUI_00001_.mp3");
        Assert.Equal("Cut 3.18-5.00 s", SoundEdit.Label(new SoundEditRequest(SoundEditKind.Cut), 3.18, 5, 8));
        Assert.Equal("+3 dB", SoundEdit.Label(new SoundEditRequest(SoundEditKind.Volume, Db: 3), 0, 8, 8));
        Assert.Equal("-3 dB 1.00-2.50 s", SoundEdit.Label(new SoundEditRequest(SoundEditKind.Volume, Db: -3), 1, 2.5, 8));
        Assert.Equal("Fade out 62.00-65.10 s", SoundEdit.Label(new SoundEditRequest(SoundEditKind.FadeOut), 62, 65.1, 70));
        // A version of the panel's sound is named after the edit alone; " 2" when that is taken.
        var target = SoundEdit.Target(version, "+3 dB", sounds);
        Assert.Equal(Path.Combine(rain, "+3 dB.wav"), target);
        File.WriteAllText(target, "");
        Assert.Equal(Path.Combine(rain, "+3 dB 2.wav"), SoundEdit.Target(version, "+3 dB", sounds));
        // Any other sound after itself and the edit.
        Assert.Equal(Path.Combine(_dir, "song (Cut 1.00-2.00 s).wav"), SoundEdit.Target(Path.Combine(_dir, "song.mp3"), "Cut 1.00-2.00 s", sounds));
    }

#if WINDOWS
    [Fact]
    public void A_saved_edit_is_a_wav_that_keeps_how_the_sound_was_made()
    {
        var sounds = Path.Combine(_dir, "sounds");
        var rain = Path.Combine(sounds, "Rain");
        Directory.CreateDirectory(rain);
        var source = Path.Combine(rain, "ComfyUI_00001_.wav");
        var workflow = """{"3":{"inputs":{"seed":7,"steps":100,"cfg":6,"sampler_name":"dpmpp_3m_sde","scheduler":"exponential","denoise":1,"model":["4",0],"positive":["6",0],"negative":["7",0],"latent_image":["11",0]},"class_type":"KSampler"},"4":{"inputs":{"ckpt_name":"stable-audio-open-1.0.safetensors"},"class_type":"CheckpointLoaderSimple"},"6":{"inputs":{"text":"heavy rain on a tin roof","clip":["10",0]},"class_type":"CLIPTextEncode"},"7":{"inputs":{"text":"","clip":["10",0]},"class_type":"CLIPTextEncode"},"11":{"inputs":{"seconds":8},"class_type":"EmptyLatentAudio"}}""";
        SoundEdit.WriteWav(source, Tone(2, 0.25f, channels: 2, rate: 22050), workflow);
        Assert.Equal("heavy rain on a tin roof", MediaRecipe.Read(source)?.Prompt);

        var decoder = new Sloperator.Platform.Windows.MediaFoundationDecoder();
        var edited = SoundEdit.Save(source, new SoundEditRequest(SoundEditKind.Cut, 0.5, 1.25), decoder, sounds);
        Assert.Equal(Path.Combine(rain, "Cut 0.50-1.25 s.wav"), edited.Path);
        Assert.Equal(1.25, edited.After, 3);
        Assert.Equal((22050, 2), (edited.Rate, edited.Channels));
        Assert.Equal([source, edited.Path], SoundFolder.Versions(rain));
        // What the panel shows beside it: the prompt it was made with.
        Assert.Equal("heavy rain on a tin roof", MediaRecipe.Read(edited.Path)?.Prompt);
        using var reader = decoder.OpenAudio(edited.Path)!;
        var read = SoundEdit.Read(reader);
        Assert.Equal(1.25, read.Seconds, 3);
        Assert.Equal(0.25f, read.Samples[1000], 3);
    }

    [Fact]
    public async Task The_edit_sound_tool_saves_the_edit_and_says_where()
    {
        var sounds = Path.Combine(_dir, "sounds");
        var song = Path.Combine(_dir, "song.wav");
        SoundEdit.WriteWav(song, Tone(2, 0.25f, rate: 8000));
        var ctx = new ToolContext
        {
            Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir,
            Media = new Sloperator.Platform.Windows.MediaFoundationDecoder(), SoundsDirectory = sounds,
        };
        var tool = new EditSoundTool();
        var args = new JsonObject { ["path"] = "song.wav", ["edit"] = "fade-out", ["from"] = 1.5, ["to"] = 2 };
        Assert.Equal("Fade out 1.50-2.00 s of song.wav", tool.Describe(args));
        var result = await tool.ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.False(result.IsError, result.Text);
        var made = Path.Combine(_dir, "song (Fade out 1.50-2.00 s).wav");
        Assert.Equal([made], result.Files);
        Assert.Contains("2.000 s long (2.000 s before), 8 kHz mono", result.Text);
        Assert.True(File.Exists(made));
        var cutAll = await tool.ExecuteAsync(new JsonObject { ["path"] = song, ["edit"] = "cut", ["from"] = 0, ["to"] = 2 }, ctx, CancellationToken.None);
        Assert.True(cutAll.IsError);
        Assert.Contains("all of it", cutAll.Text);
    }
#endif
}
