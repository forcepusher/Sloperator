using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using Sloperator.Core.Video;
using Xunit;
#if WINDOWS
using Sloperator.Platform.Windows;
#endif

namespace Sloperator.Core.Tests;

/// <summary>
/// A recording looked through without watching it (<see cref="VideoCheck.Scan"/>): where its sound is silent and
/// stands out, where its picture cuts, stands still and is black; and its silences cut out in one call.
/// </summary>
public sealed class VideoScanTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sl-video-scan-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    private const int Rate = 8000, Fps = 10;

    /// <summary>A sound of stretches, each so many seconds at a level (a steady tone of that height; 0 is silence), in stereo.</summary>
    private sealed class Stretches(params (double Seconds, float Level)[] parts) : IAudioReader
    {
        private long _at;
        private long Frames => (long)Math.Round(parts.Sum(p => p.Seconds) * Rate);
        public int SampleRate => Rate;
        public int Channels => 2;
        public TimeSpan Duration => TimeSpan.FromSeconds(Frames / (double)Rate);

        public int Read(float[] buffer, int offset, int count)
        {
            var n = (int)Math.Max(0, Math.Min(count / 2, Frames - _at));
            for (var i = 0; i < n; i++)
            {
                var (t, level) = ((_at + i) / (double)Rate, 0f);
                foreach (var (seconds, l) in parts)
                {
                    if (t < seconds) { level = l; break; }
                    t -= seconds;
                }
                // Up and down by turns: a tone of the level, whose energy is the level's square.
                buffer[offset + i * 2] = buffer[offset + i * 2 + 1] = (_at + i) % 2 == 0 ? level : -level;
            }
            _at += n;
            return n * 2;
        }

        public void Seek(TimeSpan time) => _at = (long)Math.Round(time.TotalSeconds * Rate);
        public void Dispose() { }
    }

    /// <summary>
    /// A picture of scenes, each so many seconds of a kind: soft "bars" that slide on with every frame, a flat "grey"
    /// with a white square, "black", and a still "ramp" from dark to light. Ten frames a second, 64 by 36.
    /// </summary>
    private sealed class Scenes(params (double Seconds, string Kind)[] parts) : IVideoReader
    {
        private int _next;
        private int Count => (int)Math.Round(parts.Sum(p => p.Seconds) * Fps);
        public int Read { get; private set; }
        public int Width => 64;
        public int Height => 36;
        public TimeSpan Duration => TimeSpan.FromSeconds(Count / (double)Fps);
        public double FrameRate => Fps;

        public (TimeSpan Time, RawImage Frame)? Next()
        {
            if (_next >= Count)
                return null;
            var n = _next++;
            Read++;
            var (t, kind) = (n / (double)Fps, "black");
            foreach (var (seconds, k) in parts)
            {
                if (t < seconds - 1e-9) { kind = k; break; }
                t -= seconds;
            }
            var pixels = new byte[Width * Height * 4];
            for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                {
                    var v = kind switch
                    {
                        "bars" => (byte)(128 + 60 * Math.Sin((x + n) / 10.0)),
                        "grey" => (byte)(x is > 20 and < 40 && y is > 10 and < 26 ? 255 : 110),
                        "ramp" => (byte)(x * 4),
                        _ => (byte)0,
                    };
                    var i = (y * Width + x) * 4;
                    (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (v, v, v, 255);
                }
            return (TimeSpan.FromSeconds(n / (double)Fps), new RawImage(Width, Height, pixels));
        }

        // The frame that starts nearest the time, as Windows' reader lands.
        public void Seek(TimeSpan time) => _next = Math.Clamp((int)Math.Round(time.TotalSeconds * Fps), 0, Count);
        public void Dispose() { }
    }

    private sealed class Recording(Func<IAudioReader?> sound, Func<IVideoReader?> picture, double seconds) : IMediaDecoder
    {
        public MediaInfo? Probe(string path) => new()
        {
            Duration = TimeSpan.FromSeconds(seconds), HasVideo = picture() is not null, Width = 64, Height = 36, FrameRate = Fps,
            HasAudio = sound() is not null, SampleRate = Rate, Channels = 2,
        };
        public IVideoReader? OpenVideo(string path, int maxSide) => picture();
        public IAudioReader? OpenAudio(string path) => sound();
    }

    [Fact]
    public void A_sounds_silences_are_found_and_what_stands_out_as_loud()
    {
        // Talk, two seconds of nothing, talk, a breath, talk, a second of shouting, and three seconds left running at the end.
        var heard = VideoCheck.ScanSound(new Stretches((5, 0.1f), (2, 0), (3, 0.1f), (0.4, 0), (3, 0.1f), (1, 0.5f), (3, 0)), TestContext.Current.CancellationToken);
        Assert.Equal(17.4, heard.Seconds, 1);
        Assert.InRange(heard.UsualDb, -21, -19);
        Assert.Equal(-50, heard.QuietDb, 1);
        Assert.False(heard.Silent);
        // The two seconds, with a little kept beside the talk at each end, and all that is left at the end; not the breath.
        Assert.Equal(2, heard.Silences.Count);
        Assert.Equal((5.15, 6.85), (Math.Round(heard.Silences[0].From, 2), Math.Round(heard.Silences[0].To, 2)));
        Assert.Equal((14.55, 17.4), (Math.Round(heard.Silences[1].From, 2), Math.Round(heard.Silences[1].To, 2)));
        // What is left with them cut out, in order.
        Assert.Equal([(0, 5.15), (6.85, 14.55)], heard.Sounding.Select(s => (Math.Round(s.From, 2), Math.Round(s.To, 2))));
        // The shout stands out; the talk around it doesn't.
        Assert.Single(heard.Loudest);
        Assert.InRange(heard.Loudest[0].At, 13, 15);

        // A recording made in a room has the room in its silences: silent is well below how loud it usually is.
        var room = VideoCheck.ScanSound(new Stretches((4, 0.2f), (3, 0.004f), (4, 0.2f)), TestContext.Current.CancellationToken);
        Assert.Single(room.Silences);
        Assert.InRange(room.Silences[0].From, 4.1, 4.2);
        // Sound all through, and none at all.
        var steady = VideoCheck.ScanSound(new Stretches((6, 0.1f)), TestContext.Current.CancellationToken);
        Assert.Empty(steady.Silences);
        Assert.Empty(steady.Loudest);
        Assert.Equal([(0, 6)], steady.Sounding.Select(s => (Math.Round(s.From, 2), Math.Round(s.To, 2))));
        Assert.True(VideoCheck.ScanSound(new Stretches((3, 0)), TestContext.Current.CancellationToken).Silent);
        Assert.Equal(0, VideoCheck.ScanSound(new Stretches(), TestContext.Current.CancellationToken).Seconds);
    }

    [Fact]
    public void A_pictures_cuts_are_found_to_the_frame_and_where_it_stands_still_or_is_black()
    {
        // Forty-four seconds: soft bars that slide, a still card, six seconds of black, a still ramp.
        var reader = new Scenes((10, "bars"), (14, "grey"), (6, "black"), (14, "ramp"));
        var (seen, frames) = VideoCheck.ScanPicture(reader, 0, 44, null, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(2, seen.Every, 3);
            // Each cut by the first frame of the new picture, though it was looked at every two seconds; the stripes
            // moving on from look to look are no cut.
            Assert.Equal([10, 24, 30], seen.Changes.Select(c => Math.Round(c, 2)));
            Assert.Equal([(12, 24), (32, 44)], seen.Still.Select(s => (Math.Round(s.From, 1), Math.Round(s.To, 1))));
            Assert.Equal([(24, 30)], seen.Black.Select(s => (Math.Round(s.From, 1), Math.Round(s.To, 1))));
            // Twenty looks of two frames each and a few more to find each cut: not the four hundred frames there are.
            Assert.InRange(reader.Read, 44, 140);
            Assert.Equal(22, frames.Count);
            Assert.Equal(0, frames[0].Time, 3);
        }
        finally
        {
            foreach (var (_, frame) in frames)
                frame.Dispose();
        }

        // A stretch of it, looked at more closely: eight looks at least, in the file's own seconds.
        var close = new Scenes((10, "bars"), (14, "grey"), (6, "black"), (14, "ramp"));
        var (part, shown) = VideoCheck.ScanPicture(close, 22, 26, null, TestContext.Current.CancellationToken);
        foreach (var (_, frame) in shown)
            frame.Dispose();
        Assert.Equal(0.5, part.Every, 3);
        Assert.Equal([24], part.Changes.Select(c => Math.Round(c, 2)));
        Assert.Equal(8, shown.Count);
        Assert.Equal(22, shown[0].Time, 3);
    }

    [Fact]
    public void A_scan_says_what_a_recording_holds_and_shows_frames_across_it()
    {
        var media = new Recording(() => new Stretches((10, 0.1f), (14, 0), (6, 0), (14, 0.1f)), () => new Scenes((10, "bars"), (14, "grey"), (6, "black"), (14, "ramp")), 44);
        var scanned = VideoCheck.Scan(Path.Combine(_dir, "talk.mkv"), media, ct: TestContext.Current.CancellationToken);
        Assert.StartsWith("talk.mkv: video 64x36, 440 frames at 10 fps (44.0 s), with sound (8 kHz stereo). Every time below is in seconds.", scanned.Text);
        Assert.Contains("Sound: usually about -20 dB. Silent (below -50 dB) for 19.7 s in all, in 1 stretch: 10.2–29.9. With it cut out it is 24.3 s (action=edit with \"skip\": \"silences\" does that in one call).", scanned.Text);
        Assert.True(scanned.Text.Contains("Picture, looked at every 2 s: it cuts to another picture at 10, 24, 30; nothing on screen changes from 12 to 24, 32 to 44; it is black from 24 to 30; the most movement is around 0, 6."), scanned.Text);
        Assert.Contains("The picture shows 22 of its frames, one every 2 s, each with its time (minutes:seconds).", scanned.Text);
        Assert.Equal([(0, 10.15), (29.85, 44)], scanned.Sounding.Select(s => (Math.Round(s.From, 2), Math.Round(s.To, 2))));
        Assert.NotNull(scanned.Sheet);
        using (var sheet = SkiaSharp.SKBitmap.Decode(scanned.Sheet))
            Assert.Equal(1536, sheet.Width);

        // A stretch of it: its own sound and picture, in the file's seconds; a sound file has no picture to look through.
        var part = VideoCheck.Scan(Path.Combine(_dir, "talk.mkv"), media, 22, 4, ct: TestContext.Current.CancellationToken);
        Assert.Contains("Scanned from 22 to 26 s. Every time below is in the file's own seconds.", part.Text);
        Assert.Contains("Its sound is silent all through.", part.Text);
        Assert.Contains("it cuts to another picture at 24", part.Text);
        Assert.Contains("(minutes:seconds and tenths)", part.Text);
        var song = VideoCheck.Scan(Path.Combine(_dir, "song.mp3"), new Recording(() => new Stretches((6, 0.1f)), () => null, 6), ct: TestContext.Current.CancellationToken);
        Assert.Contains("No silence of 0.7 s or more in it.", song.Text);
        Assert.DoesNotContain("Picture", song.Text);
        Assert.Null(song.Sheet);
        Assert.Equal("3:20", VideoCheck.Clock(200));
        Assert.Equal("1:02:05", VideoCheck.Clock(3725));
        Assert.Equal("0:02.5", VideoCheck.Clock(2.52, tenths: true));
    }

#if WINDOWS
    [Fact]
    public async Task A_files_silences_are_cut_out_in_one_call_and_a_scan_is_an_action()
    {
        // Two seconds of a tone on red, two of nothing on green, two of a tone on blue.
        var media = new MediaFoundationDecoder();
        var ct = TestContext.Current.CancellationToken;
        var source = Path.Combine(_dir, "talk.mp4");
        const int rate = 48000, fps = 15;
        using (var writer = media.Write(source, new MediaWriterOptions(320, 180, fps, Sound: true))!)
        {
            long written = 0;
            for (var f = 0; f < 6 * fps; f++)
            {
                var part = f / (2 * fps);
                var pixels = new byte[320 * 180 * 4];
                for (var i = 0; i < pixels.Length; i += 4)
                    (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(part == 2 ? 220 : 0), (byte)(part == 1 ? 220 : 0), (byte)(part == 0 ? 220 : 0), 255);
                writer.Frame(new RawImage(320, 180, pixels));
                var upTo = (f + 1) * (long)rate / fps;
                var sound = new float[(upTo - written) * 2];
                if (part != 1)
                    for (var s = 0; s < sound.Length / 2; s++)
                        sound[s * 2] = sound[s * 2 + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * 330 * (written + s) / rate));
                writer.Sound(sound);
                written = upTo;
            }
            writer.Finish();
        }
        var ctx = new Tools.ToolContext
        {
            Platform = new FakePlatform(), Settings = new Settings.AgentSettings(), WorkingDirectory = _dir, ScratchDirectory = Path.Combine(_dir, "scratch"),
            Media = new FallbackDecoder(media, new GifDecoder()), Comfy = Comfy.ComfyHub.For(Path.Combine(_dir, ".comfy")), SeesImages = true,
        };
        var scan = await new Tools.VideoEditorTool().ExecuteAsync(new System.Text.Json.Nodes.JsonObject { ["action"] = "scan", ["path"] = source }, ctx, ct);
        Assert.False(scan.IsError, scan.Text);
        Assert.True(scan.Text.Contains("in 1 stretch: 2."), scan.Text);
        Assert.True(scan.Text.Contains("it cuts to another picture at 2, 4"), scan.Text);
        Assert.NotNull(scan.Image);

        var output = Path.Combine(_dir, "tight.mp4");
        var args = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{"action": "edit", "storyboard": {"skip": "silences"}}""")!;
        (args["path"], args["output"]) = (source, output);
        var cut = await new Tools.VideoEditorTool().ExecuteAsync(args, ctx, ct);
        Assert.False(cut.IsError, cut.Text);
        Assert.True(cut.Text.Contains("Cut 1 silence out of it, 1."), cut.Text);
        Assert.Contains("2 pieces", cut.Text);
        // The talk and the talk, with a little of the quiet kept between them: about four and a third seconds of the six.
        var info = media.Probe(output)!;
        Assert.InRange(info.Duration.TotalSeconds, 4.1, 4.7);
        using var video = media.OpenVideo(output, 0)!;
        (int R, int G, int B) At(double seconds)
        {
            video.Seek(TimeSpan.FromSeconds(seconds));
            var frame = video.Next()!.Value.Frame;
            var i = (frame.Height / 2 * frame.Width + frame.Width / 2) * 4;
            return (frame.Bgra[i + 2], frame.Bgra[i + 1], frame.Bgra[i]);
        }
        Assert.True(At(1).R > 150 && At(1).B < 60, At(1).ToString());
        Assert.True(At(3.5).B > 150 && At(3.5).R < 60, At(3.5).ToString());
        // The file itself is as it was.
        Assert.InRange(media.Probe(source)!.Duration.TotalSeconds, 5.9, 6.1);
    }
#endif
}
