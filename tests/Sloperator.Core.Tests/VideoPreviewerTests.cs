using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using Sloperator.Core.Video;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// The Video editor's look at the video at a moment (<see cref="VideoPreviewer"/>): the clip under the playhead at
/// its own frame, as its shot edits it, with the clip before it through a transition and the titles over it.
/// </summary>
public sealed class VideoPreviewerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sl-video-preview-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    /// <summary>
    /// Clips of ten frames a second, 40 by 20 (or as <paramref name="size"/> says): frame n is a flat colour whose blue
    /// is 10 for each frame and whose green is the clip's own, with a white square in its top left corner.
    /// </summary>
    private sealed class Clips(Func<string, (int Width, int Height)>? size = null) : IMediaDecoder
    {
        public int Opened { get; private set; }

        public MediaInfo? Probe(string path) => new() { Duration = TimeSpan.FromSeconds(5), HasVideo = true, Width = 40, Height = 20, FrameRate = 10 };

        public IVideoReader? OpenVideo(string path, int maxSide)
        {
            Opened++;
            var (w, h) = size?.Invoke(path) ?? (40, 20);
            return new Reader(w, h, (byte)(Path.GetFileNameWithoutExtension(path).EndsWith('2') ? 200 : 100));
        }

        public IAudioReader? OpenAudio(string path) => null;

        private sealed class Reader(int width, int height, byte green) : IVideoReader
        {
            private int _next;
            public int Width => width;
            public int Height => height;
            public TimeSpan Duration => TimeSpan.FromSeconds(5);
            public double FrameRate => 10;

            public (TimeSpan Time, RawImage Frame)? Next()
            {
                if (_next >= 50)
                    return null;
                var n = _next++;
                var pixels = new byte[width * height * 4];
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var i = (y * width + x) * 4;
                        var corner = x < width / 4 && y < height / 4;
                        (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = corner ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)(n * 5), green, (byte)40, (byte)255);
                    }
                return (TimeSpan.FromSeconds(n / 10.0), new RawImage(width, height, pixels));
            }

            public void Seek(TimeSpan time) => _next = Math.Clamp((int)Math.Floor(time.TotalSeconds * 10 + 1e-6), 0, 50);

            public void Dispose() { }
        }
    }

    private Storyboard Video(string json)
    {
        var board = Storyboard.Parse(json);
        board.Folder = _dir;
        foreach (var shot in board.Shots)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(board.ShotVideo(shot))!);
            File.WriteAllText(board.ShotVideo(shot), "");
        }
        return board;
    }

    /// <summary>The frame's blue, green and red at a point, in parts of its width and height.</summary>
    private static (int B, int G, int R) At(RawImage frame, double x, double y)
    {
        var i = ((int)(y * (frame.Height - 1)) * frame.Width + (int)(x * (frame.Width - 1))) * 4;
        return (frame.Bgra[i], frame.Bgra[i + 1], frame.Bgra[i + 2]);
    }

    /// <summary>Which frame of its clip the picture is: the blue of its middle, 5 for each frame.</summary>
    private static int FrameOf(RawImage frame) => (int)Math.Round(At(frame, 0.6, 0.6).B / 5.0);

    [Theory]
    [InlineData("", 1.25, 1.25)]
    [InlineData(""", "in": 1""", 1.25, 2.25)]
    [InlineData(""", "speed": 2""", 1.25, 2.5)]
    [InlineData(""", "in": 1, "speed": 0.5""", 1, 1.5)]
    [InlineData(""", "freeze": 0.7""", 1.25, 0.7)]
    // Backwards from the end of its cut, or of its file where that ends first (as here), a thousandth short of it: the frame showing just before.
    [InlineData(""", "reverse": true""", 1.25, 3.749)]
    [InlineData(""", "reverse": true, "speed": 2""", 1, 2.999)]
    [InlineData("", 9, 4.98)] // the file has run out: its last frame is held
    public void A_shot_is_at_its_own_moment_of_its_clip(string edits, double into, double moment)
    {
        var shot = Storyboard.Parse($$"""{"shots": [{"id": "s1", "seconds": 10, "file": "a.mp4"{{edits}}}]}""").Shots[0];
        Assert.Equal(moment, VideoPreviewer.SourceTime(shot, into, 5), 3);
    }

    [Fact]
    public void The_playhead_shows_the_clip_there_at_its_frame_and_a_reader_is_kept_while_it_plays_on()
    {
        var board = Video("""
            {"width": 640, "height": 320, "fps": 10, "fadeIn": 0, "fadeOut": 0, "shots": [
              {"id": "s1", "seconds": 2, "file": "a.mp4"},
              {"id": "s2", "seconds": 2, "file": "b.mp4", "in": 1, "speed": 2}]}
            """);
        var clips = new Clips();
        using var preview = new VideoPreviewer(clips, 80);
        var frame = preview.FrameAt(board, 0.55)!;
        Assert.Equal((80, 40), (frame.Width, frame.Height));
        Assert.Equal(5, FrameOf(frame));
        Assert.Equal(100, At(frame, 0.6, 0.6).G);
        Assert.Equal((255, 255, 255), At(frame, 0.05, 0.05));
        // Played on, frame after frame, from the one reader; back a second, it goes there.
        Assert.Equal(6, FrameOf(preview.FrameAt(board, 0.65)!));
        Assert.Equal(7, FrameOf(preview.FrameAt(board, 0.75)!));
        Assert.Equal(2, FrameOf(preview.FrameAt(board, 0.2)!));
        Assert.Equal(1, clips.Opened);
        // The second clip: a second of it cut off, at twice the speed, its own colour.
        var second = preview.FrameAt(board, 2.3)!;
        Assert.Equal(16, FrameOf(second)); // 1 s in, then 0.3 s at twice the speed
        Assert.Equal(200, At(second, 0.6, 0.6).G);
        // Past the end it is the last moment; a video with no clip made shows nothing.
        Assert.NotNull(preview.FrameAt(board, 99));
        File.Delete(board.ShotVideo(board.Shots[0]));
        Assert.Null(preview.FrameAt(board, 0.5));
    }

    [Fact]
    public void A_clip_is_shown_turned_mirrored_fitted_and_coloured_as_its_shot_says()
    {
        var clips = new Clips();
        using var preview = new VideoPreviewer(clips, 80);
        RawImage Frame(string edits) => preview.FrameAt(Video($$"""{"width": 640, "height": 320, "fps": 10, "fadeIn": 0, "fadeOut": 0, "shots": [{"id": "s1", "seconds": 2, "file": "a.mp4", {{edits}}}]}"""), 0.5)!;

        // Mirrored: the white corner is at the right. Upside down: at the bottom.
        var mirrored = Frame("\"flip\": \"horizontal\"");
        Assert.Equal((255, 255, 255), At(mirrored, 0.95, 0.05));
        Assert.NotEqual((255, 255, 255), At(mirrored, 0.05, 0.05));
        Assert.Equal((255, 255, 255), At(Frame("\"flip\": \"vertical\""), 0.05, 0.95));
        // A quarter turn to the right stands the clip on end: shown whole, there is black beside it, its corner at the top right of the picture.
        var turned = Frame("\"rotate\": 90, \"fit\": \"fit\"");
        Assert.Equal((0, 0, 0), At(turned, 0.05, 0.5));
        Assert.Equal((0, 0, 0), At(turned, 0.95, 0.5));
        Assert.Equal((255, 255, 255), At(turned, 0.6, 0.1));
        Assert.Equal(100, At(turned, 0.5, 0.8).G);
        // Filling the frame instead, nothing of it is black.
        Assert.Equal(100, At(Frame("\"rotate\": 90"), 0.05, 0.5).G);
        // Its right half kept: no white corner left in it.
        Assert.NotEqual((255, 255, 255), At(Frame("\"crop\": {\"x\": 0.5, \"y\": 0, \"width\": 0.5, \"height\": 1}"), 0.05, 0.05));
        // Black and white: no colour stands out. A negative: the dark red is light.
        var grey = At(Frame("\"color\": \"bw\""), 0.6, 0.6);
        Assert.True(Math.Abs(grey.B - grey.G) <= 2 && Math.Abs(grey.G - grey.R) <= 2, grey.ToString());
        Assert.True(At(Frame("\"effects\": [\"invert\"]"), 0.6, 0.6).R > 200);
        // A slow zoom in: twice as near by its end, the corner's square has grown past the frame's first quarter.
        var zoomed = preview.FrameAt(Video("""{"width": 640, "height": 320, "fps": 10, "fadeIn": 0, "fadeOut": 0, "shots": [{"id": "s1", "seconds": 2, "file": "a.mp4", "zoom": {"from": {"scale": 1, "x": 0, "y": 0}, "to": {"scale": 2, "x": 0, "y": 0}}}]}"""), 1.99)!;
        Assert.Equal((255, 255, 255), At(zoomed, 0.4, 0.4));
    }

    [Fact]
    public void A_transition_shows_both_clips_and_titles_and_fades_lie_over_them()
    {
        var board = Video("""
            {"width": 640, "height": 320, "fps": 10, "fadeIn": 0.5, "fadeOut": 0, "shots": [
              {"id": "s1", "seconds": 2, "file": "a.mp4"},
              {"id": "s2", "seconds": 2, "file": "b.mp4", "transition": "dissolve", "transitionSeconds": 1}],
             "overlays": [{"id": "o1", "text": "HELLO", "position": "full", "start": 2.2, "seconds": 0.6, "fadeIn": 0, "fadeOut": 0, "size": 0.9, "color": "#FF0000"}]}
            """);
        using var preview = new VideoPreviewer(new Clips(), 80);
        // From black at the start: nothing yet, then half there.
        Assert.Equal((0, 0, 0), At(preview.FrameAt(board, 0)!, 0.6, 0.6));
        Assert.InRange(At(preview.FrameAt(board, 0.25)!, 0.6, 0.6).G, 40, 60);
        // Half way through the dissolve (the second clip starts at 1 s): half of each clip's green.
        Assert.InRange(At(preview.FrameAt(board, 1.5)!, 0.6, 0.6).G, 145, 155);
        Assert.Equal(100, At(preview.FrameAt(board, 0.9)!, 0.6, 0.6).G);
        Assert.Equal(200, At(preview.FrameAt(board, 2.1)!, 0.6, 0.6).G);
        // The title's red letters while it shows, none before or after.
        static int Reds(RawImage f) => Enumerable.Range(0, f.Width * f.Height).Count(i => f.Bgra[i * 4 + 2] > 180 && f.Bgra[i * 4 + 1] < 120);
        Assert.Equal(0, Reds(preview.FrameAt(board, 2.1)!));
        Assert.True(Reds(preview.FrameAt(board, 2.5)!) > 10);
        Assert.Equal(0, Reds(preview.FrameAt(board, 2.9)!));
        // A wipe to the left: its edge goes from the right to the left, the new clip behind it, the old one still at the left.
        board.Shots[1].Transition = "wipeleft";
        var wipe = preview.FrameAt(board, 1.5)!;
        Assert.Equal(100, At(wipe, 0.3, 0.6).G);
        Assert.Equal(200, At(wipe, 0.8, 0.6).G);
        // To the right, the other way round; and down, from the top.
        board.Shots[1].Transition = "wiperight";
        Assert.Equal(200, At(preview.FrameAt(board, 1.5)!, 0.3, 0.6).G);
        Assert.Equal(100, At(preview.FrameAt(board, 1.5)!, 0.8, 0.6).G);
        board.Shots[1].Transition = "wipedown";
        Assert.Equal(200, At(preview.FrameAt(board, 1.5)!, 0.6, 0.3).G);
        Assert.Equal(100, At(preview.FrameAt(board, 1.5)!, 0.6, 0.8).G);
        // A circle opening shows the new clip in the middle first; one closing keeps the old clip there last.
        board.Shots[1].Transition = "circleopen";
        // (half way through, with ffmpeg's soft rim: the middle is all the new clip, a corner still all the old one)
        Assert.InRange(At(preview.FrameAt(board, 1.5)!, 0.5, 0.5).G, 190, 200);
        Assert.InRange(At(preview.FrameAt(board, 1.5)!, 0.98, 0.95).G, 100, 110);
        board.Shots[1].Transition = "circleclose";
        Assert.InRange(At(preview.FrameAt(board, 1.5)!, 0.5, 0.5).G, 100, 110);
        Assert.InRange(At(preview.FrameAt(board, 1.5)!, 0.98, 0.95).G, 190, 200);
        // Towards the top left: the new clip starts in the bottom right. A clock's hand: from the top, to the right first.
        board.Shots[1].Transition = "diagtl";
        Assert.True(At(preview.FrameAt(board, 1.5)!, 0.95, 0.9).G > At(preview.FrameAt(board, 1.5)!, 0.4, 0.4).G + 50);
        board.Shots[1].Transition = "radial";
        Assert.True(At(preview.FrameAt(board, 1.35)!, 0.9, 0.3).G > At(preview.FrameAt(board, 1.35)!, 0.1, 0.3).G + 50);
        // Blinds to the left: whole at the right edge first.
        board.Shots[1].Transition = "hlslice";
        Assert.Equal(200, At(preview.FrameAt(board, 1.5)!, 0.97, 0.6).G);
        Assert.Equal(100, At(preview.FrameAt(board, 1.5)!, 0.08, 0.6).G);
        // Every transition there is draws something of its own all the way through, and ends on the new clip.
        foreach (var name in Storyboard.Transitions.Where(t => t != VideoShot.Cut))
        {
            board.Shots[1].Transition = name;
            foreach (var at in new[] { 1.0, 1.25, 1.5, 1.75, 1.95 })
                Assert.NotNull(preview.FrameAt(board, at));
            Assert.Equal(200, At(preview.FrameAt(board, 2.05)!, 0.6, 0.6).G);
        }
    }

    [Fact]
    public void A_video_laid_over_the_video_plays_in_its_box()
    {
        var board = Video("""
            {"width": 640, "height": 320, "fps": 10, "fadeIn": 0, "fadeOut": 0, "shots": [{"id": "s1", "seconds": 4, "file": "a.mp4"}],
             "overlays": [{"id": "o1", "file": "inset2.mp4", "position": "bottom right", "start": 1, "seconds": 2}]}
            """);
        Directory.CreateDirectory(board.ReferencesFolder);
        File.WriteAllText(Path.Combine(board.ReferencesFolder, "inset2.mp4"), "");
        using var preview = new VideoPreviewer(new Clips(), 80);
        // Before it starts: the clip alone. While it shows: the other video's own colour in the bottom right corner, at its own frame.
        Assert.Equal(100, At(preview.FrameAt(board, 0.5)!, 0.85, 0.85).G);
        var with = preview.FrameAt(board, 1.5)!;
        Assert.Equal(200, At(with, 0.85, 0.85).G);
        Assert.Equal(5, (int)Math.Round(At(with, 0.85, 0.85).B / 5.0));
        Assert.Equal(100, At(with, 0.3, 0.3).G);
        Assert.Equal(100, At(preview.FrameAt(board, 3.5)!, 0.85, 0.85).G);
    }

    [Fact]
    public void A_title_can_have_a_line_round_its_letters()
    {
        // White words over a white clip can't be read; with a black line round them they can.
        Storyboard Board(string more) => Video($$"""
            {"width": 640, "height": 320, "fps": 10, "fadeIn": 0, "fadeOut": 0, "shots": [{"id": "s1", "seconds": 2, "file": "a.mp4", "effects": ["invert"], "color": "bw"}],
             "overlays": [{"id": "o1", "text": "WOW", "position": "full", "start": 0, "seconds": 2, "fadeIn": 0, "fadeOut": 0, "size": 0.8, "color": "#FFFFFF", "background": "none"{{more}}}]}
            """);
        using var preview = new VideoPreviewer(new Clips(), 320);
        static int Dark(RawImage f) => Enumerable.Range(0, f.Width * f.Height).Count(i => f.Bgra[i * 4] < 40 && f.Bgra[i * 4 + 1] < 40 && f.Bgra[i * 4 + 2] < 40);
        var plain = Dark(preview.FrameAt(Board(""), 1)!);
        var lined = Dark(preview.FrameAt(Board(""", "outline": "#000000" """), 1)!);
        Assert.True(lined > plain + 500, $"{plain} dark pixels without the line, {lined} with it");
        Assert.Equal("#000000", Board(""", "outline": " #000000 " """).Overlays[0].Outline);
        Assert.True(VideoOverlays.IsDark("#202020"));
        Assert.False(VideoOverlays.IsDark(null));
        Assert.False(VideoOverlays.IsDark("#FFD60A"));
    }

    /// <summary>
    /// A clip as Windows reads an MKV or a WMV: 30 frames a second with each frame's time kept to the millisecond
    /// (0.033, 0.067, 0.1), and a jump that lands on the frame starting nearest the time. Frame n holds n in its blue and green.
    /// </summary>
    private sealed class ToTheMillisecond : IMediaDecoder
    {
        public MediaInfo? Probe(string path) => new() { Duration = TimeSpan.FromSeconds(20), HasVideo = true, Width = 40, Height = 20, FrameRate = 30 };
        public IVideoReader? OpenVideo(string path, int maxSide) => new Reader();
        public IAudioReader? OpenAudio(string path) => null;

        private sealed class Reader : IVideoReader
        {
            private int _next;
            public int Width => 40;
            public int Height => 20;
            public TimeSpan Duration => TimeSpan.FromSeconds(20);
            public double FrameRate => 30;
            private static double TimeOf(int n) => Math.Round(n / 30.0, 3);

            public (TimeSpan Time, RawImage Frame)? Next()
            {
                if (_next >= 600)
                    return null;
                var n = _next++;
                var pixels = new byte[Width * Height * 4];
                for (var i = 0; i < pixels.Length; i += 4)
                    (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(n % 256), (byte)(n / 256), 0, 255);
                return (TimeSpan.FromSeconds(TimeOf(n)), new RawImage(Width, Height, pixels));
            }

            public void Seek(TimeSpan time)
            {
                _next = 0;
                while (_next < 600 && TimeOf(_next) < time.TotalSeconds - 0.5 / 30)
                    _next++;
            }

            public void Dispose() { }
        }
    }

    [Fact]
    public void A_clip_whose_times_are_kept_to_the_millisecond_shows_the_frame_asked_for()
    {
        static int Number(RawImage frame) => At(frame, 0.6, 0.6).B + At(frame, 0.6, 0.6).G * 256;
        // Forwards from two seconds in, every frame its own: frame 89 is kept as 2.967, a third of a millisecond late,
        // and was taken for the one showing at 3.0, so every frame of a cut out of an MKV was the one before it.
        var forwards = Video("""{"width": 640, "height": 320, "fps": 30, "fadeIn": 0, "fadeOut": 0, "shots": [{"id": "s1", "seconds": 4, "in": 2, "file": "a.mkv"}]}""");
        using (var preview = new VideoPreviewer(new ToTheMillisecond(), 80))
            for (var k = 0; k < 120; k++)
                Assert.Equal(60 + k, Number(preview.FrameAt(forwards, k / 30.0)!));
        // Jumped to, one at a time, the same.
        using (var preview = new VideoPreviewer(new ToTheMillisecond(), 80))
            foreach (var k in (int[])[30, 7, 119, 0, 61])
                Assert.Equal(60 + k, Number(preview.FrameAt(forwards, k / 30.0)!));
        // Backwards: the cut's last frame first, down to its first, none from after the cut.
        var backwards = Video("""{"width": 640, "height": 320, "fps": 30, "fadeIn": 0, "fadeOut": 0, "shots": [{"id": "s1", "seconds": 4, "in": 2, "reverse": true, "file": "a.mkv"}]}""");
        using (var preview = new VideoPreviewer(new ToTheMillisecond(), 80))
            for (var k = 0; k < 120; k++)
                Assert.Equal(179 - k, Number(preview.FrameAt(backwards, k / 30.0)!));
    }
}
