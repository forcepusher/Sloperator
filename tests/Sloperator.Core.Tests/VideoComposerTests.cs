using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using Sloperator.Core.Render;
using Sloperator.Core.Video;
using Sloperator.Core.Web;
using SkiaSharp;
using Xunit;
#if WINDOWS
using Sloperator.Platform.Windows;
#endif

namespace Sloperator.Core.Tests;

/// <summary>
/// The video made without ffmpeg: its sound mixed here (<see cref="VideoSoundMix"/>), a clip's sound sped up with its
/// pitch kept (<see cref="SoundStretch"/>), a colour keyed out, and, on Windows, the file written with the OS's own
/// encoder and the whole video joined through it (<see cref="VideoComposer"/>).
/// </summary>
public sealed class VideoComposerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sl-video-compose-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    private const int Rate = VideoSoundMix.Rate;

    /// <summary>Sounds by file name: "level 0.5 4s.wav" is four seconds of the value 0.5 (in stereo; "mono level …" in one channel), so what the mix did to it can be read off a sample.</summary>
    private sealed class Levels : IMediaDecoder
    {
        public MediaInfo? Probe(string path) => null;
        public IVideoReader? OpenVideo(string path, int maxSide) => null;

        public IAudioReader? OpenAudio(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var mono = name.StartsWith("mono ", StringComparison.Ordinal);
            var parts = (mono ? name[5..] : name).Split(' ');
            if (parts.Length < 3 || parts[0] != "level")
                return null;
            var level = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            var seconds = double.Parse(parts[2].TrimEnd('s'), System.Globalization.CultureInfo.InvariantCulture);
            return new Constant(level, (int)(seconds * 8000), mono ? 1 : 2);
        }

        /// <summary>At 8 kHz, to be brought to 48 kHz (and to two channels) by the mix.</summary>
        private sealed class Constant(float level, int frames, int channels) : IAudioReader
        {
            private int _at;
            public int SampleRate => 8000;
            public int Channels => channels;
            public TimeSpan Duration => TimeSpan.FromSeconds(frames / 8000.0);

            public int Read(float[] buffer, int offset, int count)
            {
                var n = Math.Min(count, frames * channels - _at);
                Array.Fill(buffer, level, offset, n);
                _at += n;
                return n;
            }

            public void Seek(TimeSpan time) => _at = (int)(time.TotalSeconds * 8000) * channels;
            public void Dispose() { }
        }
    }

    /// <summary>A video in the test's folder whose clips and sounds are files of the names given (empty: the decoder goes by their names).</summary>
    private Storyboard Video(string json)
    {
        var board = Storyboard.Parse(json);
        board.Folder = _dir;
        foreach (var file in board.Shots.Select(board.ShotVideo).Concat(board.Sounds.Select(board.SoundFile)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "");
        }
        return board;
    }

    /// <summary>The mix's left sample at a time.</summary>
    private static float At(float[] mix, double seconds) => mix[(int)Math.Round(seconds * Rate) * VideoSoundMix.Channels];

    [Fact]
    public void Each_clips_sound_is_cut_as_its_shot_is_and_fades_into_the_next_through_a_transition()
    {
        var board = Video("""
            {"fadeOut": 0, "shots": [
              {"id": "s1", "seconds": 2, "place": "level 0.4 4s.mp4"},
              {"id": "s2", "seconds": 2, "place": "level 0.2 4s.mp4", "volume": 0.5},
              {"id": "s3", "seconds": 2, "place": "level 0.8 4s.mp4", "transition": "dissolve", "transitionSeconds": 1},
              {"id": "s4", "seconds": 2, "place": "level 0.6 4s.mp4", "volume": 0},
              {"id": "s5", "seconds": 2, "place": "level 0.6 0.5s.mp4"},
              {"id": "s6", "seconds": 2, "place": "level 0.6 4s.mp4", "freeze": 1}]}
            """);
        var notes = new List<string>();
        var mix = VideoSoundMix.Mix(board, new Levels(), notes);
        Assert.Empty(notes);
        Assert.Equal((int)Math.Round(11.0 * Rate) * 2, mix.Length);
        Assert.Equal(0.4f, At(mix, 1), 3);
        // At half its volume.
        Assert.Equal(0.1f, At(mix, 2.5), 3);
        // Half way through the dissolve (s3 starts at 3 s): half of each.
        Assert.Equal(0.05f + 0.4f, At(mix, 3.5), 2);
        Assert.Equal(0.8f, At(mix, 4.5), 3);
        // Muted; then a clip shorter than its shot is silent once it has run out; then one frozen is silent from its freeze.
        Assert.Equal(0, At(mix, 6), 3);
        Assert.Equal(0.6f, At(mix, 7.2), 3);
        Assert.Equal(0, At(mix, 8), 3);
        Assert.Equal(0.6f, At(mix, 9.5), 3);
        Assert.Equal(0, At(mix, 10.5), 3);
        // Both channels alike.
        Assert.Equal(mix[Rate * 2], mix[Rate * 2 + 1]);
        // A sound of one channel goes to both at 0.707 of itself, as ffmpeg spreads it.
        var one = VideoSoundMix.Mix(Video("""{"fadeOut": 0, "shots": [{"id": "s1", "seconds": 2, "place": "mono level 0.4 4s.mp4"}]}"""), new Levels(), []);
        Assert.Equal(0.4f * 0.7071f, At(one, 1), 3);
        Assert.Equal(one[Rate * 2], one[Rate * 2 + 1]);
    }

    [Fact]
    public void A_sound_plays_from_its_start_trimmed_and_faded_and_the_music_goes_under_a_voice()
    {
        var board = Video("""
            {"fadeOut": 0, "soundLines": 2, "soundLineVolumes": [1, 0.5], "shots": [{"id": "s1", "seconds": 10, "place": "level 0.2 12s.mp4"}],
             "sounds": [
               {"id": "a1", "source": "song", "place": "level 0.4 20s.wav", "start": 2, "seconds": 6, "fadeIn": 1, "fadeOut": 2},
               {"id": "a2", "source": "speech", "place": "level 0.1 1s.wav", "start": 4, "seconds": 1, "soundLine": 1, "volume": 2},
               {"id": "a3", "source": "file", "place": "gone.wav", "start": 0}]}
            """);
        File.Delete(board.SoundFile(board.Sounds[2]));
        var notes = new List<string>();
        var mix = VideoSoundMix.Mix(board, new Levels(), notes);
        Assert.Contains(notes, n => n.Contains("gone.wav"));
        // Under a song a clip's own sound is at half: 0.1 alone before the song starts.
        Assert.Equal(0.1f, At(mix, 1), 3);
        // Half way up its fade in, then whole.
        Assert.Equal(0.1f + 0.2f, At(mix, 2.5), 2);
        Assert.Equal(0.1f + 0.4f, At(mix, 3.5), 3);
        // While the voice speaks (4 to 5 s) the song is at 35%; the voice at its volume and its line's (0.1 x 2 x 0.5).
        Assert.Equal(0.1f + 0.4f * 0.35f + 0.1f, At(mix, 4.5), 3);
        Assert.Equal(0.1f + 0.4f, At(mix, 5.5), 3);
        // Half way down its fade out (6 to 8 s), and gone after it is cut.
        Assert.Equal(0.1f + 0.2f, At(mix, 7), 2);
        Assert.Equal(0.1f, At(mix, 8.5), 3);
    }

    [Fact]
    public void The_end_fades_out_and_nothing_goes_over_full_scale()
    {
        var board = Video("""
            {"shots": [{"id": "s1", "seconds": 6, "place": "level 0.9 8s.mp4"}],
             "sounds": [{"id": "a1", "source": "file", "place": "level 0.9 8s.wav", "start": 2}]}
            """);
        var mix = VideoSoundMix.Mix(board, new Levels(), []);
        Assert.Equal(0.9f, At(mix, 1), 3);
        // Two sounds of 0.9 together would be 1.8: held at the ceiling, and already down when the second comes in.
        Assert.All(mix, v => Assert.InRange(v, -0.951f, 0.951f));
        Assert.Equal(0.95f, At(mix, 3), 2);
        // A sound that runs past the video's end fades out over its last two seconds (a third of the video at most): a quarter left half a second before the end.
        Assert.Equal(1.8f * 0.25f, At(mix, 5.5), 2);
        Assert.InRange(mix[^2], 0, 0.01f);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    [InlineData(1.3)]
    public void A_sound_sped_up_or_slowed_down_keeps_its_pitch(double speed)
    {
        // Two seconds of 440 Hz, stereo.
        var tone = new float[Rate * 2 * 2];
        for (var i = 0; i < tone.Length / 2; i++)
            tone[i * 2] = tone[i * 2 + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / Rate));
        var stretched = SoundStretch.Tempo(tone, 2, Rate, speed);
        var seconds = stretched.Length / 2 / (double)Rate;
        Assert.Equal(2 / speed, seconds, 2);
        // As many times through zero each second as before: the same note. And about as loud.
        var crossings = 0;
        for (var i = 1; i < stretched.Length / 2; i++)
            if (stretched[(i - 1) * 2] < 0 != stretched[i * 2] < 0)
                crossings++;
        Assert.InRange(crossings / seconds, 880 * 0.97, 880 * 1.03);
        var loud = Math.Sqrt(stretched.Skip(Rate / 5).Take(Rate).Sum(v => (double)v * v) / Rate);
        Assert.InRange(loud, 0.5 / Math.Sqrt(2) * 0.85, 0.5 / Math.Sqrt(2) * 1.15);
        Assert.Same(tone, SoundStretch.Tempo(tone, 2, Rate, 1));
    }

    [Fact]
    public void A_shots_sound_follows_its_trim_its_speed_and_its_way_round()
    {
        // A ramp from 0 to 1 over four seconds: where a sample came from can be read off its value.
        var clip = new float[Rate * 4 * 2];
        for (var i = 0; i < clip.Length / 2; i++)
            clip[i * 2] = clip[i * 2 + 1] = i / (float)(Rate * 4);
        static VideoShot Shot(string edits) => Storyboard.Parse($$"""{"shots": [{"id": "s1", "seconds": 4, "file": "a.mp4"{{edits}}}]}""").Shots[0];
        Assert.Equal(0.25f, VideoSoundMix.ShotSound(Shot(""", "in": 1"""), clip)[0], 3);
        var back = VideoSoundMix.ShotSound(Shot(""", "reverse": true"""), clip);
        Assert.Equal(1f, back[0], 3);
        Assert.Equal(0f, back[^2], 3);
        Assert.Equal(Rate * 2 * 2, VideoSoundMix.ShotSound(Shot(""", "speed": 2"""), clip).Length);
        Assert.Equal(Rate * 2, VideoSoundMix.ShotSound(Shot(""", "freeze": 1"""), clip).Length);
    }

    [Fact]
    public void A_colour_is_taken_out_of_a_picture_and_the_rest_is_kept()
    {
        foreach (var type in new[] { SKColorType.Bgra8888, SKColorType.Rgba8888 })
        {
            using var picture = new SKBitmap(new SKImageInfo(4, 1, type, SKAlphaType.Premul));
            picture.SetPixel(0, 0, new SKColor(0, 255, 0));
            picture.SetPixel(1, 0, new SKColor(20, 235, 30));
            picture.SetPixel(2, 0, new SKColor(250, 240, 220));
            picture.SetPixel(3, 0, new SKColor(200, 30, 30));
            VideoPreviewer.KeyOut(picture, new SKColor(0, 255, 0));
            // The green and a green near it are gone; the cream and the red are as they were.
            Assert.Equal(0, picture.GetPixel(0, 0).Alpha);
            Assert.Equal(0, picture.GetPixel(1, 0).Alpha);
            Assert.Equal(new SKColor(250, 240, 220), picture.GetPixel(2, 0));
            Assert.Equal(new SKColor(200, 30, 30), picture.GetPixel(3, 0));
        }
    }


    [Fact]
    public void A_gif_is_written_frame_by_frame_with_flat_colours_kept_and_only_what_changed_drawn_again()
    {
        const int w = 64, h = 32;
        RawImage Frame(int square, bool clear = false)
        {
            // A dark blue ground (or none) with an orange square that moves.
            var pixels = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    var inSquare = x >= square && x < square + 8 && y >= 12 && y < 20;
                    (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = inSquare ? ((byte)17, (byte)163, (byte)252, (byte)255) : clear ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)61, (byte)33, (byte)20, (byte)255);
                }
            return new RawImage(w, h, pixels);
        }
        var file = Path.Combine(_dir, "moving.gif");
        using (var gif = new GifWriter(file, w, h, 10))
        {
            foreach (var at in new[] { 0, 8, 16, 16 })
                gif.Frame(Frame(at));
            gif.Finish();
        }
        using (var codec = SKCodec.Create(file))
        {
            Assert.Equal((w, h, 4), (codec.Info.Width, codec.Info.Height, codec.FrameCount));
            Assert.Equal(400, codec.FrameInfo.Sum(f => f.Duration));
            Assert.Equal(-1, codec.RepetitionCount); // round and round
            // Drawn one over the other, as a viewer does: the third frame has the square at 16 and the ground where it was.
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);
            for (var i = 0; i < 3; i++)
                Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(i, i - 1)));
            Assert.Equal(new SKColor(252, 163, 17), bitmap.GetPixel(20, 16));
            Assert.Equal(new SKColor(20, 33, 61), bitmap.GetPixel(4, 16));
            Assert.Equal(new SKColor(20, 33, 61), bitmap.GetPixel(12, 16));
        }
        // A frame the same as the one before costs next to nothing: the fourth adds under 60 bytes.
        var three = Path.Combine(_dir, "three.gif");
        using (var gif = new GifWriter(three, w, h, 10))
            foreach (var at in new[] { 0, 8, 16 })
                gif.Frame(Frame(at));
        Assert.InRange(new FileInfo(file).Length - new FileInfo(three).Length, 1, 60);

        // A clear background stays clear, each frame in place of the one before.
        var clear = Path.Combine(_dir, "clear.gif");
        using (var gif = new GifWriter(clear, w, h, 10, clear: true))
            foreach (var at in new[] { 0, 16 })
                gif.Frame(Frame(at, clear: true));
        using (var codec = SKCodec.Create(clear))
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);
            Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(0, -1)));
            Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(1, -1)));
            Assert.Equal(new SKColor(252, 163, 17), bitmap.GetPixel(20, 16));
            Assert.Equal(0, bitmap.GetPixel(4, 16).Alpha);
        }
    }

    [Fact]
    public void A_frame_of_many_colours_is_drawn_in_the_255_that_suit_it()
    {
        // A slope of every hue across, light to dark down: far more colours than a GIF's table holds.
        const int w = 128, h = 64;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var colour = SKColor.FromHsv(x * 360f / w, 80, 100 - y * 70f / h);
                var i = (y * w + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (colour.Blue, colour.Green, colour.Red, 255);
            }
        var file = Path.Combine(_dir, "slope.gif");
        using (var gif = new GifWriter(file, w, h, 10))
            gif.Frame(new RawImage(w, h, pixels));
        using var decoded = SKBitmap.Decode(file);
        Assert.NotNull(decoded);
        // Each pixel near what it was: off by 12 a channel on average at most, and never by a different colour altogether.
        double off = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (got, i) = (decoded.GetPixel(x, y), (y * w + x) * 4);
                var d = Math.Abs(got.Blue - pixels[i]) + Math.Abs(got.Green - pixels[i + 1]) + Math.Abs(got.Red - pixels[i + 2]);
                Assert.True(d < 120, $"{x},{y}: {d}");
                off += d / 3.0;
            }
        Assert.InRange(off / (w * h), 0, 12);
    }


    [Fact]
    public void An_animated_gif_is_read_as_video_frame_by_frame_with_its_clear_parts_clear()
    {
        const int w = 64, h = 32;
        RawImage Frame(int square)
        {
            // An orange square that moves over nothing: clear around it.
            var pixels = new byte[w * h * 4];
            for (var y = 12; y < 20; y++)
                for (var x = square; x < square + 8; x++)
                {
                    var i = (y * w + x) * 4;
                    (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (17, 163, 252, 255);
                }
            return new RawImage(w, h, pixels);
        }
        var sticker = Path.Combine(_dir, "sticker.gif");
        using (var gif = new GifWriter(sticker, w, h, 5, clear: true))
            foreach (var at in new[] { 0, 8, 16, 24 })
                gif.Frame(Frame(at));
        // Over a ground, each frame only what changed: read back, every frame is the whole picture as it stands then.
        var loop = Path.Combine(_dir, "loop.gif");
        using (var gif = new GifWriter(loop, w, h, 5))
            foreach (var at in new[] { 0, 8, 16, 24 })
            {
                var frame = Frame(at);
                for (var i = 0; i < frame.Bgra.Length; i += 4)
                    if (frame.Bgra[i + 3] == 0)
                        (frame.Bgra[i], frame.Bgra[i + 1], frame.Bgra[i + 2], frame.Bgra[i + 3]) = (61, 33, 20, 255);
                gif.Frame(frame);
            }
        var decoder = new GifDecoder();
        Assert.True(GifDecoder.Handles(sticker));
        var info = decoder.Probe(sticker)!;
        Assert.Equal((w, h, true, false), (info.Width, info.Height, info.HasVideo, info.HasAudio));
        Assert.Equal(0.8, info.Duration.TotalSeconds, 2);
        Assert.Equal(5, info.FrameRate, 1);
        static (int B, int G, int R, int A) At(RawImage f, int x, int y) => (f.Bgra[(y * f.Width + x) * 4], f.Bgra[(y * f.Width + x) * 4 + 1], f.Bgra[(y * f.Width + x) * 4 + 2], f.Bgra[(y * f.Width + x) * 4 + 3]);
        using (var video = decoder.OpenVideo(sticker, 0)!)
        {
            Assert.True(video.HasAlpha);
            var times = new List<double>();
            for (var k = 0; video.Next() is { } next; k++)
            {
                times.Add(next.Time.TotalSeconds);
                // The square where this frame has it, nothing where the frame before had it.
                Assert.Equal((17, 163, 252, 255), At(next.Frame, k * 8 + 4, 16));
                if (k > 0)
                    Assert.Equal(0, At(next.Frame, k * 8 - 4, 16).A);
            }
            Assert.Equal([0, 0.2, 0.4, 0.6], times.Select(t => Math.Round(t, 2)));
            // To a moment: the frame showing then; and smaller when asked.
            video.Seek(TimeSpan.FromSeconds(0.5));
            Assert.Equal(0.4, video.Next()!.Value.Time.TotalSeconds, 2);
        }
        using (var small = decoder.OpenVideo(sticker, 32)!)
            Assert.Equal((32, 16), (small.Width, small.Height));
        using (var video = decoder.OpenVideo(loop, 0)!)
        {
            // Backwards to the third frame after the last: the ground is whole though the file holds only what changed.
            while (video.Next() is not null) { }
            video.Seek(TimeSpan.FromSeconds(0.4));
            var third = video.Next()!.Value.Frame;
            Assert.Equal((17, 163, 252, 255), At(third, 20, 16));
            Assert.Equal((61, 33, 20, 255), At(third, 4, 16));
            Assert.Equal((61, 33, 20, 255), At(third, 12, 16));
        }
        // A picture of one frame, or another kind of file, isn't video.
        var still = Path.Combine(_dir, "still.gif");
        using (var gif = new GifWriter(still, w, h, 5))
            gif.Frame(Frame(0));
        Assert.False(GifDecoder.Handles(still));
        Assert.Null(decoder.OpenVideo(still, 0));
        Assert.Null(decoder.Probe(Path.Combine(_dir, "none.gif")));

        // Laid over a video as a sticker: the clip shows through its clear parts, and it plays round.
        var board = Video("""
            {"width": 640, "height": 320, "fps": 10, "fadeIn": 0, "fadeOut": 0, "shots": [{"id": "s1", "seconds": 4, "place": "level 0 4s.mp4"}],
             "overlays": [{"id": "o1", "file": "sticker.gif", "start": 0, "seconds": 4, "box": {"x": 0, "y": 0, "width": 1, "height": 1}}]}
            """);
        using var preview = new VideoPreviewer(new FallbackDecoder(decoder, new Flat()), 64);
        var first = preview.FrameAt(board, 0.1)!;
        Assert.Equal((17, 163, 252, 255), At(first, 4, 16));
        Assert.Equal(200, At(first, 40, 16).G);
        // 1.0 s is 0.2 s into its second time round: the square at 8.
        var again = preview.FrameAt(board, 1.0)!;
        Assert.Equal((17, 163, 252, 255), At(again, 12, 16));
        Assert.Equal(200, At(again, 4, 16).G);
    }

    /// <summary>Any video file as one flat green picture, ten frames a second.</summary>
    private sealed class Flat : IMediaDecoder
    {
        public MediaInfo? Probe(string path) => null;
        public IAudioReader? OpenAudio(string path) => null;
        public IVideoReader? OpenVideo(string path, int maxSide) => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? new Reader() : null;

        private sealed class Reader : IVideoReader
        {
            private int _next;
            public int Width => 64;
            public int Height => 32;
            public TimeSpan Duration => TimeSpan.FromSeconds(4);
            public double FrameRate => 10;

            public (TimeSpan Time, RawImage Frame)? Next()
            {
                if (_next >= 40)
                    return null;
                var pixels = new byte[64 * 32 * 4];
                for (var i = 0; i < pixels.Length; i += 4)
                    (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (40, 200, 40, 255);
                return (TimeSpan.FromSeconds(_next++ / 10.0), new RawImage(64, 32, pixels));
            }

            public void Seek(TimeSpan time) => _next = Math.Clamp((int)Math.Floor(time.TotalSeconds * 10 + 1e-6), 0, 40);
            public void Dispose() { }
        }
    }


    [Fact]
    public void What_is_said_is_written_as_subtitles_beside_the_video()
    {
        var board = Video("""
            {"title": "Talk", "fadeIn": 0, "fadeOut": 0, "shots": [
              {"id": "s1", "seconds": 3, "place": "level 0 3s.mp4", "line": "Hello there."},
              {"id": "s2", "seconds": 2, "place": "level 0 2s.mp4"},
              {"id": "s3", "seconds": 4, "place": "level 0 4s.mp4", "line": "It was a long day,\nand a longer night."}],
             "sounds": [{"id": "a1", "source": "speech", "place": "level 0.1 2s.wav", "text": "Meanwhile, elsewhere.", "start": 3.25, "seconds": 1.5},
                        {"id": "a2", "source": "speech", "place": "level 0.1 9s.wav", "text": "Past the end.", "start": 8.5}]}
            """);
        var lines = VideoSubtitles.Lines(board, new Levels());
        // A shot's line from 0.4 s into its shot to the shot's end; a voice-over while it plays; all in order, none past the video's end.
        Assert.Equal(4, lines.Count);
        Assert.Equal((0.4, 3.0, "Hello there."), lines[0]);
        Assert.Equal((3.25, 4.75, "Meanwhile, elsewhere."), lines[1]);
        Assert.Equal(5.4, lines[2].From, 3);
        Assert.Equal((8.5, 9.0, "Past the end."), lines[3]);
        var file = VideoSubtitles.Write(board, new Levels());
        Assert.Equal(Path.Combine(_dir, "Talk.srt"), file);
        var text = File.ReadAllText(file!);
        Assert.StartsWith("1\r\n00:00:00,400 --> 00:00:03,000\r\nHello there.\r\n\r\n2\r\n00:00:03,250 --> 00:00:04,750\r\nMeanwhile, elsewhere.\r\n\r\n", text);
        Assert.Contains("3\r\n00:00:05,400 --> 00:00:09,000\r\nIt was a long day,\r\nand a longer night.\r\n\r\n", text);
        // Nothing said any more: the file of the join before goes.
        foreach (var shot in board.Shots)
            shot.Line = "";
        board.Sounds.Clear();
        Assert.Null(VideoSubtitles.Write(board, new Levels()));
        Assert.False(File.Exists(Path.Combine(_dir, "Talk.srt")));
    }

#if WINDOWS
    /// <summary>A clip written here: a flat colour, with a tone when it has a pitch.</summary>
    private static void Clip(string path, (byte R, byte G, byte B) colour, double seconds, double hz = 0, int width = 320, int height = 180, int fps = 15)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new MediaFoundationDecoder().Write(path, new MediaWriterOptions(width, height, fps, Sound: hz > 0));
        Assert.NotNull(writer);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (colour.B, colour.G, colour.R, 255);
        long written = 0;
        for (var f = 0; f < Math.Round(seconds * fps); f++)
        {
            writer.Frame(new RawImage(width, height, pixels));
            var upTo = (f + 1) * (long)Rate / fps;
            var sound = new float[(upTo - written) * 2];
            for (var s = 0; s < sound.Length / 2; s++)
                sound[s * 2] = sound[s * 2 + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * hz * (written + s) / Rate));
            writer.Sound(sound);
            written = upTo;
        }
        writer.Finish();
    }

    private static (int R, int G, int B) Middle(RawImage frame)
    {
        var i = (frame.Height / 2 * frame.Width + frame.Width / 2) * 4;
        return (frame.Bgra[i + 2], frame.Bgra[i + 1], frame.Bgra[i]);
    }

    [Fact]
    public void A_video_file_is_written_with_windows_own_encoder_and_reads_back_as_it_went_in()
    {
        var media = new MediaFoundationDecoder();
        Assert.True(media.Writes);
        var file = Path.Combine(_dir, "written ü.mp4");
        Clip(file, (200, 60, 30), 2, hz: 440);
        // Its index is at the file's start, so it plays while it still loads; moving it again finds nothing to do.
        Assert.True(Mp4Index.IsFirst(file));
        Assert.False(Mp4Index.MoveFirst(file));
        var info = media.Probe(file);
        Assert.NotNull(info);
        Assert.Equal((320, 180), (info.Width, info.Height));
        Assert.Equal(15, info.FrameRate, 1);
        Assert.InRange(info.Duration.TotalSeconds, 1.95, 2.1);
        Assert.True(info.HasAudio);
        Assert.Equal((48000, 2), (info.SampleRate, info.Channels));
        using (var video = media.OpenVideo(file, 0)!)
        {
            // The first frame at the very start (no frame held back for later ones), every frame there, the colour as it was.
            var first = video.Next();
            Assert.NotNull(first);
            Assert.Equal(0, first.Value.Time.TotalSeconds, 3);
            var (r, g, b) = Middle(first.Value.Frame);
            Assert.InRange(r, 194, 206);
            Assert.InRange(g, 54, 66);
            Assert.InRange(b, 24, 36);
            var frames = 1;
            while (video.Next() is not null)
                frames++;
            Assert.Equal(30, frames);
        }
        using var sound = media.OpenAudio(file)!;
        var samples = SoundEdit.Read(sound);
        Assert.InRange(samples.Seconds, 1.9, 2.2);
        Assert.InRange(samples.Samples.Max(), 0.25f, 0.35f);

        // An MP4 only, and in whole pairs of pixels.
        Assert.Null(media.Write(Path.Combine(_dir, "clip.webm"), new MediaWriterOptions(320, 180, 15)));
        Assert.Null(media.Write(Path.Combine(_dir, "odd.mp4"), new MediaWriterOptions(321, 180, 15)));
    }

    [Fact]
    public async Task A_video_is_joined_where_there_is_no_ffmpeg()
    {
        var media = new MediaFoundationDecoder();
        var ct = TestContext.Current.CancellationToken;
        var board = Storyboard.Parse("""
            {"title": "Joined", "width": 320, "height": 180, "fps": 15, "fadeIn": 0, "fadeOut": 0, "shots": [
              {"id": "s1", "seconds": 2, "place": "Generated/s1.mp4"},
              {"id": "s2", "seconds": 2, "place": "Generated/s2.mp4", "transition": "dissolve", "transitionSeconds": 1},
              {"id": "s3", "seconds": 2, "place": "Generated/s3.mp4", "transition": "wiperight", "transitionSeconds": 1, "rotate": 180, "effects": ["invert"]}],
             "overlays": [{"id": "o1", "file": "key.png", "key": "#00ff00", "start": 0, "seconds": 1, "box": {"x": 0, "y": 0, "width": 0.25, "height": 0.25}}]}
            """);
        board.Folder = _dir;
        Clip(board.ShotVideo(board.Shots[0]), (200, 0, 0), 2, hz: 330);
        Clip(board.ShotVideo(board.Shots[1]), (0, 0, 200), 2, hz: 440);
        Clip(board.ShotVideo(board.Shots[2]), (0, 0, 0), 2);
        // A picture laid over the corner: green to take out, white to keep.
        Directory.CreateDirectory(board.ReferencesFolder);
        using (var key = new SKBitmap(40, 40))
        {
            key.Erase(new SKColor(0, 255, 0));
            for (var y = 10; y < 30; y++)
                for (var x = 10; x < 30; x++)
                    key.SetPixel(x, y, SKColors.White);
            using var data = key.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(board.ReferencesFolder, "key.png"), data.ToArray());
        }
        var said = new List<string>();
        var made = await VideoMontage.JoinAsync(board, null, media, said.Add, ct);
        Assert.Equal(board.VideoFile, made.File);
        Assert.Empty(made.Notes);
        Assert.Contains(said, s => s.StartsWith("Joining the shots: frame", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_dir, ".*.joining.mp4"));
        var info = media.Probe(made.File)!;
        Assert.Equal((320, 180, true), (info.Width, info.Height, info.HasAudio));
        Assert.InRange(info.Duration.TotalSeconds, 3.95, 4.15);

        using var video = media.OpenVideo(made.File, 0)!;
        RawImage FrameAt(double seconds)
        {
            video.Seek(TimeSpan.FromSeconds(seconds));
            return video.Next()!.Value.Frame;
        }
        (int R, int G, int B) Point(RawImage frame, double x, double y)
        {
            var i = ((int)(y * frame.Height) * frame.Width + (int)(x * frame.Width)) * 4;
            return (frame.Bgra[i + 2], frame.Bgra[i + 1], frame.Bgra[i]);
        }
        // The first clip, with the picture over its corner: its white kept, its green gone (the clip's red shows there).
        var start = FrameAt(0.5);
        Assert.InRange(Middle(start).R, 185, 215);
        Assert.True(Point(start, 0.125, 0.125) is { R: > 230, G: > 230, B: > 230 }, Point(start, 0.125, 0.125).ToString());
        Assert.True(Point(start, 0.02, 0.03) is { R: > 170, G: < 60 }, Point(start, 0.02, 0.03).ToString());
        // Half way through the dissolve: half of each.
        var both = Middle(FrameAt(1.5));
        Assert.InRange(both.R, 80, 120);
        Assert.InRange(both.B, 80, 120);
        // The second clip alone, then the third wiped in from the left (black made white by its negative) with the second still at the right.
        Assert.InRange(Middle(FrameAt(2.2)).B, 185, 215);
        var wipe = FrameAt(2.5);
        Assert.True(Point(wipe, 0.2, 0.5) is { R: > 220, G: > 220, B: > 220 }, Point(wipe, 0.2, 0.5).ToString());
        Assert.InRange(Point(wipe, 0.8, 0.5).B, 185, 215);

        // The clips' tones: there through the first two clips, silent under the third.
        using var audio = media.OpenAudio(made.File)!;
        var sound = SoundEdit.Read(audio);
        double Loud(double from, double to) => Math.Sqrt(sound.Samples.Skip((int)(from * sound.Rate) * sound.Channels).Take((int)((to - from) * sound.Rate) * sound.Channels).Sum(v => (double)v * v)
                                                         / ((to - from) * sound.Rate * sound.Channels));
        Assert.InRange(Loud(0.2, 0.8), 0.15, 0.25);
        Assert.InRange(Loud(3.2, 3.8), 0, 0.01);
    }

    [Fact]
    public async Task An_svg_that_moves_becomes_an_mp4_where_there_is_no_ffmpeg_and_lies_over_a_video_as_frames()
    {
        if (HeadlessBrowser.FindExecutable() is null)
            Assert.Skip("No Edge or Chrome on this PC.");
        var media = new MediaFoundationDecoder();
        var ct = TestContext.Current.CancellationToken;
        var renderer = new SceneRenderer(Path.Combine(_dir, "browser")) { Writer = media };
        var scene = Path.Combine(_dir, "ball.svg");
        File.WriteAllText(scene, """
            <svg xmlns="http://www.w3.org/2000/svg" width="320" height="180" viewBox="0 0 320 180">
              <circle cx="40" cy="90" r="30" fill="#ff8800">
                <animate attributeName="cx" from="40" to="280" dur="2s" fill="freeze"/>
              </circle>
            </svg>
            """);
        var made = await renderer.RenderAsync(scene, Path.Combine(_dir, "ball.mp4"), new SceneOptions { Fps = 10, Width = 320, Height = 180 }, null, null, ct);
        Assert.True(made.Clip);
        Assert.Equal(20, made.Frames);
        var info = media.Probe(made.Output)!;
        Assert.Equal((320, 180), (info.Width, info.Height));
        Assert.InRange(info.Duration.TotalSeconds, 1.9, 2.1);
        // A WebM still needs ffmpeg, and says so.
        var refused = await Assert.ThrowsAsync<RenderException>(() => renderer.RenderAsync(scene, Path.Combine(_dir, "ball.webm"), new SceneOptions { Fps = 10 }, null, null, ct));
        // A GIF is made here too, its background clear when asked.
        var gif = await renderer.RenderAsync(scene, Path.Combine(_dir, "ball.gif"), new SceneOptions { Fps = 10, Width = 320, Height = 180, Transparent = true }, null, null, ct);
        using (var codec = SKCodec.Create(gif.Output))
            Assert.Equal((320, 180, 20), (codec.Info.Width, codec.Info.Height, codec.FrameCount));
        using (var first = SKBitmap.Decode(gif.Output))
        {
            Assert.Equal(0, first.GetPixel(300, 20).Alpha);
            Assert.True(first.GetPixel(40, 90) is { Red: > 240, Green: > 120 and < 150, Blue: < 20 }, first.GetPixel(40, 90).ToString());
        }
        Assert.Contains(".mp4", refused.Message);

        // Over a video: drawn as a picture a frame, clear around the ball, and laid over the clip as it is joined.
        var board = Storyboard.Parse("""
            {"title": "Over", "width": 320, "height": 180, "fps": 10, "fadeIn": 0, "fadeOut": 0, "shots": [{"id": "s1", "seconds": 2, "place": "Generated/s1.mp4"}],
             "overlays": [{"id": "o1", "scene": "ball.svg", "start": 0, "seconds": 2, "box": {"x": 0, "y": 0, "width": 1, "height": 1}}]}
            """);
        board.Folder = _dir;
        Clip(board.ShotVideo(board.Shots[0]), (0, 0, 200), 2, fps: 10);
        var joined = await VideoMontage.JoinAsync(board, null, media, null, ct, renderer);
        Assert.Empty(joined.Notes);
        Assert.Equal(20, Directory.GetFiles(VideoPreviewer.SceneFrames(board, board.Overlays[0]), "*.png").Length);
        using var video = media.OpenVideo(joined.File, 0)!;
        video.Seek(TimeSpan.FromSeconds(1));
        var frame = video.Next()!.Value.Frame;
        // Half way: the ball in the middle, orange, with the clip's blue around it.
        var (r, g, b) = Middle(frame);
        Assert.True(r > 220 && g is > 110 and < 160 && b < 40, (r, g, b).ToString());
        Assert.InRange(frame.Bgra[(10 * frame.Width + 10) * 4], 185, 215);
    }

    [Fact]
    public async Task The_joined_video_exports_as_a_smaller_mp4_a_gif_or_its_sound_and_a_picture_is_held_as_a_clip_without_ffmpeg()
    {
        var media = new MediaFoundationDecoder();
        var ct = TestContext.Current.CancellationToken;
        var board = Storyboard.Parse("""{"title": "Out", "width": 320, "height": 180, "fps": 15, "fadeIn": 0, "fadeOut": 0, "shots": [{"id": "s1", "seconds": 2, "place": "Generated/s1.mp4"}]}""");
        board.Folder = _dir;
        Clip(board.ShotVideo(board.Shots[0]), (200, 0, 0), 2, hz: 330);
        await VideoMontage.JoinAsync(board, null, media, null, ct);

        // Half the size, the other side in proportion; a shape of its own keeps the middle.
        var small = await VideoComposer.ExportAsync(board, media, Path.Combine(_dir, "small.mp4"), 160, null, null, ct);
        var info = media.Probe(small)!;
        Assert.Equal((160, 90, true), (info.Width, info.Height, info.HasAudio));
        Assert.InRange(info.Duration.TotalSeconds, 1.9, 2.15);
        var tall = await VideoComposer.ExportAsync(board, media, Path.Combine(_dir, "tall.mp4"), 90, 160, null, ct);
        Assert.Equal((90, 160), (media.Probe(tall)!.Width, media.Probe(tall)!.Height));

        var gif = await VideoComposer.ExportAsync(board, media, Path.Combine(_dir, "out.gif"), null, null, null, ct);
        using (var codec = SKCodec.Create(gif))
        {
            Assert.Equal((320, 180, 30), (codec.Info.Width, codec.Info.Height, codec.FrameCount));
            Assert.InRange(codec.FrameInfo.Sum(f => f.Duration), 1950, 2050);
        }
        using (var first = SKBitmap.Decode(gif))
            Assert.True(first.GetPixel(160, 90) is { Red: > 185, Green: < 30, Blue: < 30 }, first.GetPixel(160, 90).ToString());

        var wav = await VideoComposer.ExportAsync(board, media, Path.Combine(_dir, "out.wav"), null, null, null, ct);
        Assert.InRange(SoundEdit.Read(wav, media).Seconds, 1.9, 2.15);

        // What only ffmpeg makes says so, and what is made without it.
        var refused = await Assert.ThrowsAsync<VideoException>(() => VideoComposer.ExportAsync(board, media, Path.Combine(_dir, "out.webm"), null, null, null, ct));
        Assert.Contains(".gif", refused.Message);
        Assert.Empty(Directory.GetFiles(_dir, ".*.exporting.*"));

        // A picture of another shape held for three seconds: the frame filled, its middle kept.
        var picture = Path.Combine(_dir, "wide.png");
        using (var wide = new SKBitmap(400, 100))
        {
            wide.Erase(new SKColor(0, 180, 0));
            using var data = wide.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(picture, data.ToArray());
        }
        var held = Path.Combine(_dir, "held.mp4");
        VideoComposer.Hold(picture, held, 320, 180, 15, 3, media);
        var clip = media.Probe(held)!;
        Assert.Equal((320, 180, false), (clip.Width, clip.Height, clip.HasAudio));
        Assert.InRange(clip.Duration.TotalSeconds, 2.9, 3.1);
        using var video = media.OpenVideo(held, 0)!;
        Assert.InRange(Middle(video.Next()!.Value.Frame).G, 170, 190);
    }

    [Fact]
    public async Task One_video_file_is_cut_turned_and_made_a_gif_in_one_call_each_and_stays_as_it_was()
    {
        var media = new MediaFoundationDecoder();
        var ct = TestContext.Current.CancellationToken;
        var source = Path.Combine(_dir, "mine.mp4");
        Clip(source, (200, 0, 0), 4, hz: 330);
        var before = File.ReadAllBytes(source);
        var ctx = new Tools.ToolContext
        {
            Platform = new FakePlatform(), Settings = new Settings.AgentSettings(), WorkingDirectory = _dir, ScratchDirectory = Path.Combine(_dir, "scratch"),
            Media = new FallbackDecoder(media, new GifDecoder()), Comfy = Comfy.ComfyHub.For(Path.Combine(_dir, ".comfy")),
        };
        async Task<string> Edit(string json, string? output = null)
        {
            var args = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!;
            args["action"] = "edit";
            args["path"] = source;
            if (output is not null)
                args["output"] = output;
            var result = await new Tools.VideoEditorTool().ExecuteAsync(args, ctx, ct);
            Assert.False(result.IsError, result.Text);
            return result.Text;
        }
        // From 1 s, for 2 s: half as long, its own size and rate, its sound with it.
        var cut = Path.Combine(_dir, "cut.mp4");
        Assert.Contains(cut, await Edit("""{"storyboard": {"in": 1, "seconds": 2}}""", cut));
        var info = media.Probe(cut)!;
        Assert.Equal((320, 180, true), (info.Width, info.Height, info.HasAudio));
        Assert.InRange(info.Duration.TotalSeconds, 1.9, 2.15);
        // Turned a quarter: upright, at twice the speed, so half as long. No output given: beside the file, under a name of its own.
        await Edit("""{"storyboard": {"rotate": 90, "speed": 2}}""");
        var turned = media.Probe(Path.Combine(_dir, "mine edited.mp4"))!;
        Assert.Equal((180, 320), (turned.Width, turned.Height));
        Assert.InRange(turned.Duration.TotalSeconds, 1.9, 2.15);
        // A GIF half the size.
        var gif = Path.Combine(_dir, "small.gif");
        await Edit("""{"width": 160, "storyboard": {"seconds": 1}}""", gif);
        using (var codec = SKCodec.Create(gif))
            Assert.Equal((160, 90, 15), (codec.Info.Width, codec.Info.Height, codec.FrameCount));
        // And back: an animated GIF made an MP4 (Windows reads no GIF as video; Skia does here).
        var back = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{"action": "edit"}""")!;
        (back["path"], back["output"]) = (gif, Path.Combine(_dir, "from gif.mp4"));
        var fromGif = await new Tools.VideoEditorTool().ExecuteAsync(back, ctx, ct);
        Assert.False(fromGif.IsError, fromGif.Text);
        var again = media.Probe(Path.Combine(_dir, "from gif.mp4"))!;
        Assert.Equal((160, 90), (again.Width, again.Height));
        Assert.InRange(again.Duration.TotalSeconds, 0.9, 1.15);
        // A picture becomes a clip: five seconds unless it says, the picture's own shape, with a slow zoom over it.
        var photo = Path.Combine(_dir, "photo.png");
        using (var wide = new SKBitmap(400, 200))
        {
            wide.Erase(new SKColor(0, 120, 200));
            using var data = wide.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(photo, data.ToArray());
        }
        var moving = Path.Combine(_dir, "photo.mp4");
        var zoom = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{"action": "edit", "storyboard": {"seconds": 2, "zoom": {"from": {"scale": 1}, "to": {"scale": 1.3}}}}""")!;
        (zoom["path"], zoom["output"]) = (photo, moving);
        var zoomed = await new Tools.VideoEditorTool().ExecuteAsync(zoom, ctx, ct);
        Assert.False(zoomed.IsError, zoomed.Text);
        var clip = media.Probe(moving)!;
        Assert.Equal((400, 200), (clip.Width, clip.Height));
        Assert.InRange(clip.Duration.TotalSeconds, 1.9, 2.15);
        // The file itself is never written over, and nothing is left in the scratch folder.
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_dir, "scratch", "video-edit")));
        await Assert.ThrowsAsync<Tools.ToolArgumentException>(() =>
            new Tools.VideoEditorTool().ExecuteAsync(new System.Text.Json.Nodes.JsonObject { ["action"] = "edit", ["path"] = source, ["output"] = source }, ctx, ct));
    }

    [Fact]
    public void A_still_clip_is_a_fault_only_where_a_model_should_have_made_it_move()
    {
        var media = new MediaFoundationDecoder();
        var board = Storyboard.Parse("""
            {"title": "Still", "width": 320, "height": 180, "fps": 15, "shots": [
              {"id": "s1", "seconds": 2, "prompt": "a river", "place": "Generated/s1.mp4"},
              {"id": "s2", "seconds": 2, "file": "photo.png", "place": "Generated/s2.mp4"},
              {"id": "s3", "seconds": 2, "scene": "title.svg", "place": "Generated/s3.mp4"}]}
            """);
        board.Folder = _dir;
        Directory.CreateDirectory(board.ReferencesFolder);
        using (var photo = new SKBitmap(32, 18))
        {
            photo.Erase(new SKColor(90, 120, 60));
            using var data = photo.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(board.ReferencesFolder, "photo.png"), data.ToArray());
        }
        File.WriteAllText(Path.Combine(board.ReferencesFolder, "title.svg"), """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 18"><rect width="32" height="18" fill="#5a783c"/></svg>""");
        foreach (var shot in board.Shots)
            Clip(board.ShotVideo(shot), (90, 120, 60), 2);
        // A clip a model made that stands still is a fault; the user's picture held is what it should be;
        // a scene in which nothing changes at all is told, as its animation may not run.
        var made = VideoCheck.Shot(board, board.Shots[0], board.ShotVideo(board.Shots[0]), media);
        Assert.Contains("it hardly moves", made.Faults);
        var held = VideoCheck.Shot(board, board.Shots[1], board.ShotVideo(board.Shots[1]), media);
        Assert.Empty(held.Faults);
        Assert.Contains("It is the picture held still.", held.Text);
        var drawn = VideoCheck.Shot(board, board.Shots[2], board.ShotVideo(board.Shots[2]), media);
        Assert.Contains("nothing in it moves", drawn.Faults);
    }

    [Fact]
    public async Task The_video_joined_here_is_the_one_ffmpeg_joins()
    {
        var media = new MediaFoundationDecoder();
        var both = new FallbackDecoder(media, new FfmpegDecoder(() => []));
        var ffmpeg = both.Ffmpeg ?? "";
        if (ffmpeg.Length == 0)
            Assert.Skip("No ffmpeg on this PC.");
        var ct = TestContext.Current.CancellationToken;
        // Flat clips of five colours, each coming in over the one before in another way.
        string[] ways = ["wipeleft", "diagtl", "circleopen", "hlslice", "smoothup", "radial", "slideleft", "squeezev", "fade", "fadewhite", "distance", "zoomin", "dissolve"];
        (byte, byte, byte)[] colours = [(200, 60, 60), (60, 90, 200), (60, 160, 90), (200, 150, 40), (140, 70, 180)];
        var shots = string.Join(",", Enumerable.Range(0, ways.Length + 1).Select(i =>
            $$"""{"id": "s{{i + 1}}", "seconds": 2, "place": "Generated/s{{i + 1}}.mp4"{{(i == 0 ? "" : $$""", "transition": "{{ways[i - 1]}}", "transitionSeconds": 1""")}}}"""));
        var board = Storyboard.Parse($$"""{"title": "Same", "width": 320, "height": 180, "fps": 15, "fadeIn": 0, "fadeOut": 0, "shots": [{{shots}}]}""");
        board.Folder = _dir;
        for (var i = 0; i < board.Shots.Count; i++)
            Clip(board.ShotVideo(board.Shots[i]), colours[i % colours.Length], 2, hz: 220 + i * 55);
        var theirs = Path.Combine(_dir, "ffmpeg.mp4");
        File.Move((await VideoMontage.JoinAsync(board, ffmpeg, both, null, ct)).File, theirs);
        var ours = (await VideoMontage.JoinAsync(board, null, media, null, ct)).File;
        Assert.InRange(media.Probe(theirs)!.Duration.TotalSeconds, media.Probe(ours)!.Duration.TotalSeconds - 0.2, media.Probe(ours)!.Duration.TotalSeconds + 0.2);

        using var a = media.OpenVideo(theirs, 0)!;
        using var b = media.OpenVideo(ours, 0)!;
        // ffmpeg's file reads a few frames late through Windows' reader (its frames lean on later ones); ours starts at 0.
        var (lateA, lateB) = (a.Next()!.Value.Time.TotalSeconds, b.Next()!.Value.Time.TotalSeconds);
        Assert.Equal(0, lateB, 3);
        var (starts, _) = board.Timing();
        var worst = new List<string>();
        for (var i = 1; i < board.Shots.Count; i++)
            foreach (var part in new[] { 1 / 3.0, 2 / 3.0 })
            {
                var at = starts[i] + part;
                a.Seek(TimeSpan.FromSeconds(at + lateA));
                b.Seek(TimeSpan.FromSeconds(at + lateB));
                var (fa, fb) = (a.Next()!.Value.Frame, b.Next()!.Value.Frame);
                // How far apart the two frames are over a grid of points, each colour of 255.
                double sum = 0;
                var n = 0;
                for (var y = 6; y < 180; y += 12)
                    for (var x = 6; x < 320; x += 12)
                        for (var c = 0; c < 3; c++, n++)
                            sum += Math.Abs(fa.Bgra[(y * 320 + x) * 4 + c] - fb.Bgra[(y * 320 + x) * 4 + c]);
                if (sum / n > 6)
                    worst.Add($"{board.Shots[i].Transition} {part:0.00} through: {sum / n:0.0} apart");
            }
        Assert.True(worst.Count == 0, string.Join("; ", worst));
    }
#endif
}
