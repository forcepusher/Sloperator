using Sloperator.Core.Comfy;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using Sloperator.Core.Tools;
using Sloperator.Core.Video;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// A shot made of a video of the user's reads the video where it is: nothing is copied for it and nothing made of it,
/// and what is looked at, checked and gone on from is the stretch the shot shows, not the whole file.
/// </summary>
public sealed class FileShotTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sl-file-shot-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    /// <summary>
    /// Videos by their names: "1200s talk.mkv" runs twenty minutes at ten frames a second, each frame holding its own
    /// number (in its blue and green), and its sound is its own time (seconds / 10000); "unread …" has no picture.
    /// </summary>
    private sealed class Clock : IMediaDecoder
    {
        public const int Fps = 10, Rate = 8000;

        /// <summary>How many frames were read, and how many sound values.</summary>
        public long Seen, Heard;

        private static double? Seconds(string path) =>
            double.TryParse(Path.GetFileName(path).Split('s')[0], System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : null;

        public MediaInfo? Probe(string path) => Seconds(path) is { } seconds
            ? new MediaInfo { HasVideo = true, Width = 64, Height = 36, FrameRate = Fps, Duration = TimeSpan.FromSeconds(seconds), HasAudio = true, SampleRate = Rate, Channels = 2 }
            : null;

        public IVideoReader? OpenVideo(string path, int maxSide) => Seconds(path) is { } seconds ? new Frames((int)Math.Round(seconds * Fps), this) : null;

        public IAudioReader? OpenAudio(string path) => Seconds(path) is { } seconds ? new Ramp((int)Math.Round(seconds * Rate), this) : null;

        private sealed class Frames(int count, Clock owner) : IVideoReader
        {
            private int _at;
            public int Width => 64;
            public int Height => 36;
            public TimeSpan Duration => TimeSpan.FromSeconds(count / (double)Fps);
            public double FrameRate => Fps;

            public (TimeSpan Time, RawImage Frame)? Next()
            {
                if (_at >= count)
                    return null;
                var pixels = new byte[Width * Height * 4];
                for (var i = 0; i < pixels.Length; i += 4)
                    (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(_at % 256), (byte)(_at / 256), 0, 255);
                owner.Seen++;
                return (TimeSpan.FromSeconds(_at++ / (double)Fps), new RawImage(Width, Height, pixels));
            }

            // The frame showing then: the last that starts at or before the time.
            public void Seek(TimeSpan time) => _at = Math.Clamp((int)Math.Floor(time.TotalSeconds * Fps + 1e-6), 0, count);
            public void Dispose() { }
        }

        private sealed class Ramp(int frames, Clock owner) : IAudioReader
        {
            private int _at;
            public int SampleRate => Rate;
            public int Channels => 2;
            public TimeSpan Duration => TimeSpan.FromSeconds(frames / (double)Rate);

            public int Read(float[] buffer, int offset, int count)
            {
                var n = Math.Max(0, Math.Min(count, frames * 2 - _at));
                for (var i = 0; i < n; i++)
                    buffer[offset + i] = (_at + i) / 2 / (float)Rate / 10000f;
                _at += n;
                owner.Heard += n;
                return n;
            }

            public void Seek(TimeSpan time) => _at = (int)Math.Round(time.TotalSeconds * Rate) * 2;
            public void Dispose() { }
        }
    }

    /// <summary>The number of the frame a picture is, as <see cref="Clock"/> wrote it.</summary>
    private static int Number(SkiaSharp.SKBitmap? frame)
    {
        Assert.NotNull(frame);
        var colour = frame.GetPixel(3, 3);
        return colour.Blue + colour.Green * 256;
    }

    private static int Number(RawImage frame) => frame.Bgra[0] + frame.Bgra[1] * 256;

    private Storyboard Video(string shots, params string[] files)
    {
        var board = Storyboard.New(_dir, "Cuts");
        Directory.CreateDirectory(board.ReferencesFolder);
        foreach (var name in files)
            File.WriteAllText(Path.Combine(board.ReferencesFolder, name), name);
        board = Storyboard.Merge(board, (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(
            $$"""{"width": 640, "height": 360, "fps": 24, "fadeIn": 0, "fadeOut": 0, "frames": [{"id": "f1", "delete": true}, {"id": "f2", "delete": true}], "shots": [{{shots}}]}""")!);
        // A shot made of a file starts and ends on no frame of the video's.
        foreach (var shot in board.Shots.Where(s => s.File is not null))
            (shot.First, shot.Last) = (null, null);
        board.Save();
        return board;
    }

    [Fact]
    public void A_shot_made_of_a_video_reads_it_where_it_is_and_keeps_no_clip_of_its_own()
    {
        var board = Video("""
            {"id": "s1", "file": "1200s talk.mkv", "in": 300, "seconds": 5},
            {"id": "s2", "file": "1200s talk", "in": 900, "seconds": 5, "speed": 2},
            {"id": "s3", "file": "20s phone.MOV"}, {"id": "s4", "file": "30s old.wmv"}, {"id": "s5", "file": "2s loop.gif"},
            {"id": "s6", "file": "60s tv.ts"}, {"id": "s7", "file": "photo.png"}, {"id": "s8", "prompt": "A fox runs."}
            """, "1200s talk.mkv", "20s phone.MOV", "30s old.wmv", "2s loop.gif", "60s tv.ts", "photo.png");
        VideoShot Shot(string id) => board.Shots.First(s => s.Id == id);
        var talk = Path.Combine(board.ReferencesFolder, "1200s talk.mkv");

        // The recording itself, for both cuts out of it (one names it without its kind): three cuts of a 600 MB
        // recording were 1.8 GB of copies, and an MKV was written again as an MP4, all 20 minutes, for each.
        Assert.Equal(talk, board.ShotSource(Shot("s1")));
        Assert.Equal(talk, board.ShotClip(Shot("s2")));
        Assert.Equal(Path.Combine(board.ReferencesFolder, "20s phone.MOV"), board.ShotClip(Shot("s3")));
        // A WMV was taken for a picture: it is a video, and read where it is too.
        Assert.Equal(Path.Combine(board.ReferencesFolder, "30s old.wmv"), board.ShotClip(Shot("s4")));
        // A GIF and a broadcast stream are made a clip once, as a picture held and a model's shot have their own.
        foreach (var id in (string[])["s5", "s6", "s7", "s8"])
        {
            Assert.Null(board.ShotSource(Shot(id)));
            Assert.Equal(board.ShotVideo(Shot(id)), board.ShotClip(Shot(id)));
            Assert.False(board.HasClip(Shot(id)));
            Assert.False(VideoMaker.IsCurrent(board, Shot(id)));
            Assert.False(VideoMaker.TakeClip(board, Shot(id)));
        }
        Assert.True(Storyboard.IsVideoFile("a.3gp") && Storyboard.IsVideoFile("a.TS") && !Storyboard.ReadsWhereItIs("a.mpg") && !Storyboard.ReadsWhereItIs("a.png"));

        // There to be joined with nothing made: no step to take it, and nothing to be out of date.
        foreach (var id in (string[])["s1", "s2", "s3", "s4"])
        {
            Assert.True(board.HasClip(Shot(id)));
            Assert.True(VideoMaker.IsCurrent(board, Shot(id)));
            Assert.False(File.Exists(board.ShotVideo(Shot(id))));
        }

        // A clip the shot had before (a model's, or the copy that used to be made) goes when the video is taken; the
        // video is never touched, and is the same file afterwards.
        var own = board.ShotVideo(Shot("s1"));
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        File.WriteAllText(own, "an old copy");
        Assert.True(VideoMaker.TakeClip(board, Shot("s1")));
        Assert.False(File.Exists(own));
        Assert.Equal("1200s talk.mkv", File.ReadAllText(talk));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.mp4", SearchOption.AllDirectories));

        // With the video gone, a clip of its own the shot still has is what it is read from, as before.
        File.WriteAllText(own, "an old copy");
        File.Delete(talk);
        Assert.Null(board.ShotSource(Shot("s1")));
        Assert.Equal(own, board.ShotClip(Shot("s1")));
        Assert.True(board.HasClip(Shot("s1")));
        Assert.False(board.HasClip(Shot("s2")));
    }

    [Fact]
    public async Task Making_a_shot_made_of_a_video_copies_nothing_and_says_when_it_cannot_be_read()
    {
        var board = Video("""{"id": "s1", "file": "1200s talk.mkv", "in": 300, "seconds": 5}, {"id": "s2", "file": "unread.mp4"}""", "1200s talk.mkv", "unread.mp4");
        var media = new Clock();
        var maker = new VideoMaker(ComfyHub.For(Path.Combine(_dir, ".comfy")), media);
        var ct = TestContext.Current.CancellationToken;
        var made = await maker.MakeShotAsync(board, board.Shots[0], null, CancellationToken.None, ct);
        Assert.Equal(Path.Combine(board.ReferencesFolder, "1200s talk.mkv"), made.File);
        Assert.InRange(made.Took.TotalSeconds, 0, 2);
        Assert.DoesNotContain(Directory.EnumerateFiles(_dir, "*.mp4", SearchOption.AllDirectories), f => !f.StartsWith(board.ReferencesFolder, StringComparison.OrdinalIgnoreCase));
        // One nothing here reads is said when it is taken, with why, not found black in the joined video.
        var unread = await Assert.ThrowsAsync<VideoException>(() => maker.MakeShotAsync(board, board.Shots[1], null, CancellationToken.None, ct));
        Assert.StartsWith("unread.mp4 ", unread.Message);

        // What the tool says of a cut: the file it is, and from when to when.
        var overview = VideoEditorTool.Overview(board, media);
        Assert.Contains($"Read where it is: {made.File}", overview);
    }

    [Fact]
    public void A_stretch_of_a_file_is_read_as_a_clip_of_its_own()
    {
        var media = new Clock();
        var file = Path.Combine(_dir, "1200s talk.mkv");
        // All of a file, or all but the frame or two a model's clip runs over its shot: the decoder as it is.
        Assert.Same(media, MediaStretch.Of(media, file, 0, 1200));
        Assert.Same(media, MediaStretch.Of(media, file, 0, 1199.9));
        var cut = MediaStretch.Of(media, file, 300, 305);
        Assert.NotSame(media, cut);
        Assert.Equal(5, cut.Probe(file)!.Duration.TotalSeconds, 3);
        Assert.Equal(1200, cut.Probe(Path.Combine(_dir, "1200s other.mkv"))!.Duration.TotalSeconds, 3);

        using var video = cut.OpenVideo(file, 0)!;
        Assert.Equal(5, video.Duration.TotalSeconds, 3);
        var frames = new List<(double Time, int Number)>();
        while (video.Next() is { } next)
            frames.Add((next.Time.TotalSeconds, Number(next.Frame)));
        // Its fifty frames, timed from zero, and not one of the 11,950 others read.
        Assert.Equal(50, frames.Count);
        Assert.Equal((0, 3000), (Math.Round(frames[0].Time, 3), frames[0].Number));
        Assert.Equal((4.9, 3049), (Math.Round(frames[^1].Time, 3), frames[^1].Number));
        Assert.InRange(media.Seen, 50, 52);
        video.Seek(TimeSpan.FromSeconds(2));
        Assert.Equal(3020, Number(video.Next()!.Value.Frame));

        using var sound = cut.OpenAudio(file)!;
        Assert.Equal(5, sound.Duration.TotalSeconds, 3);
        var buffer = new float[Clock.Rate * 2 * 8];
        var read = 0;
        for (int n; (n = sound.Read(buffer, read, 6000)) > 0; read += n) { }
        Assert.Equal(Clock.Rate * 2 * 5, read);
        Assert.Equal(0.03f, buffer[0], 6);
        Assert.Equal(0.0305f, buffer[read - 2], 5);
        sound.Seek(TimeSpan.FromSeconds(4));
        Assert.Equal(Clock.Rate * 2, sound.Read(buffer, 0, buffer.Length));
        Assert.Equal(0.0304f, buffer[0], 6);

        // A stretch that runs past the file's end is what there is of it.
        using var late = MediaStretch.Of(media, file, 1198, 1205).OpenVideo(file, 0)!;
        Assert.Equal(2, late.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void A_cut_is_looked_at_and_gone_on_from_by_its_own_seconds()
    {
        var board = Video("""
            {"id": "s1", "file": "1200s talk.mkv", "in": 300, "seconds": 5},
            {"id": "s2", "file": "1200s talk.mkv", "in": 900, "seconds": 2, "speed": 2, "reverse": true},
            {"id": "s3", "file": "20s phone.MOV"}
            """, "1200s talk.mkv", "20s phone.MOV");
        var media = new Clock();
        var (s1, s2, s3) = (board.Shots[0], board.Shots[1], board.Shots[2]);
        s3.Seconds = 20;
        Assert.Equal((300, 305), s1.Stretch);
        Assert.Equal((900, 904), s2.Stretch);

        // The frame the next shot goes on from: the cut's last, not the recording's; the cut's first when it plays
        // backwards; the very last of a file shown to its end.
        using (var reader = media.OpenVideo(board.ShotClip(s1), 0)!)
            Assert.Equal(3049, Number(VideoCheck.EndFrame(reader, s1)));
        using (var reader = media.OpenVideo(board.ShotClip(s2), 0)!)
            Assert.Equal(9000, Number(VideoCheck.EndFrame(reader, s2)));
        using (var reader = media.OpenVideo(board.ShotClip(s3), 0)!)
            Assert.Equal(199, Number(VideoCheck.EndFrame(reader, s3)));

        // A frame taken from where a cut ends is taken again when the cut moves.
        var frame = new VideoFrame { Id = "f9", Of = "s1" };
        var before = VideoMaker.FrameStamp(board, frame);
        s1.In = 301;
        Assert.NotEqual(before, VideoMaker.FrameStamp(board, frame));
        s1.In = 300;
        Assert.Equal(before, VideoMaker.FrameStamp(board, frame));

        // Where in its file a shot is: backwards from the end of its cut (it was the end of its file), and of its file
        // where that ends first.
        Assert.Equal(903.999, VideoPreviewer.SourceTime(s2, 0, 1200), 3);
        Assert.Equal(901.999, VideoPreviewer.SourceTime(s2, 1, 1200), 3);
        Assert.Equal(900, VideoPreviewer.SourceTime(s2, 2, 1200), 3);
        Assert.Equal(902.48, VideoPreviewer.SourceTime(s2, 0, 902.5), 3);
        Assert.Equal(302, VideoPreviewer.SourceTime(s1, 2, 1200), 3);

        // The look at a cut is at its five seconds: sixteen frames of the twenty minutes told nothing of it, and
        // every frame of the recording was read for how it moves.
        var look = VideoCheck.Shot(board, s1, board.ShotClip(s1), media);
        Assert.StartsWith("Shot s1 (1200s talk.mkv, from 300 to 305 s of it): video 64x36, 50 frames at 10 fps (5.0 s), with sound (8 kHz stereo); the storyboard gives it 5 s.", look.Text);
        Assert.DoesNotContain("shorter than its shot", look.Text);
        Assert.InRange(media.Seen, 50, 400);
        Assert.InRange(media.Heard, 1, Clock.Rate * 2 * 5);

        // ffmpeg is given each clip from its cut, for as long as it shows: a jump to it, not twenty minutes read to find it.
        var (args, _, _) = VideoMontage.Plan(board, _ => true, "out.mp4");
        var talk = board.ShotClip(s1);
        Assert.Equal(["-ss", "300", "-t", "5.5", "-i", talk, "-ss", "900", "-t", "4", "-i", talk, "-t", "20.5", "-i", board.ShotClip(s3)], args.Skip(args.IndexOf("-ss")).Take(16));
        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.DoesNotContain("trim=start", graph);
        Assert.Contains("[1:v]setpts=PTS-STARTPTS,reverse,setpts=PTS/2,", graph);
    }
}
