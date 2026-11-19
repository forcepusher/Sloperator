using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using SkiaSharp;

namespace Sloperator.Core.Video;

/// <summary>
/// The video at a moment, drawn from its clips as the storyboard edits them, for the Video editor's viewer: the
/// clip under the playhead at its own frame (its start trimmed, its speed, backwards, frozen), turned, mirrored,
/// cropped, fitted to the frame, zoomed and coloured, with the clip before it showing through a transition, the
/// titles and pictures over it, and the video's fades. What joining the video does with ffmpeg, done here with
/// Skia at a small size and at once, so the playhead shows a frame as it is moved and Play shows the clips, where
/// the panel showed each clip's first picture and nothing of an edit until the video was joined again. A look at
/// it, not the video itself: a wipe, a slide or a clock comes in as a wipe from the left, grain and sharpening
/// aren't drawn, a picture's colour is keyed out by ffmpeg alone, and a scene or a video over the video isn't shown.
/// </summary>
public sealed class VideoPreviewer(IMediaDecoder decoder, int maxSide = 640) : IDisposable
{
    private readonly object _gate = new();

    /// <summary>A clip's reader, the frame it gave last and when that frame shows.</summary>
    private sealed class Source(IVideoReader reader) : IDisposable
    {
        public IVideoReader Reader { get; } = reader;
        public SKBitmap? Frame { get; set; }
        public double Time { get; set; } = -1;
        public long Used { get; set; }

        public void Dispose()
        {
            Frame?.Dispose();
            Reader.Dispose();
        }
    }

    /// <summary>The readers open, by file and when it was written (a clip made again is read anew); a few at most.</summary>
    private readonly Dictionary<(string File, long Stamp), Source> _sources = [];

    /// <summary>The titles and pictures drawn, by what they are and the size they were drawn at.</summary>
    private readonly Dictionary<string, SKBitmap?> _overlays = [];

    private long _clock;

    /// <summary>The most readers kept open: the clip under the playhead, the one before it in a transition, and one more.</summary>
    private const int MostSources = 3;

    /// <summary>The size the video is drawn at: its shape, <paramref name="maxSide"/> along its longer side at most.</summary>
    public static (int Width, int Height) SizeFor(Storyboard board, int maxSide)
    {
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(1, Math.Max(board.Width, board.Height)));
        return (Math.Max(2, (int)Math.Round(board.Width * scale)), Math.Max(2, (int)Math.Round(board.Height * scale)));
    }

    /// <summary>
    /// The video's frame at <paramref name="seconds"/>; null before its first clip, with no clips, or when none of
    /// the clips there is made.
    /// </summary>
    public RawImage? FrameAt(Storyboard board, double seconds)
    {
        lock (_gate)
        {
            var (starts, length) = board.Timing();
            if (board.Shots.Count == 0 || length <= 0)
                return null;
            seconds = Math.Clamp(seconds, 0, Math.Max(0, length - 0.001));
            var at = 0;
            for (var i = 0; i < starts.Count; i++)
                if (starts[i] <= seconds + 1e-6)
                    at = i;
            var (width, height) = SizeFor(board, maxSide);
            using var composed = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(composed);
            canvas.Clear(SKColors.Black);
            var shot = board.Shots[at];
            using var now = ClipFrame(board, shot, seconds - starts[at], width, height);
            // The clip before it is still there while this one comes in over it.
            var overlap = at > 0 ? Storyboard.Overlap(shot) : 0;
            var into = seconds - starts[at];
            if (at > 0 && overlap > 0 && into < overlap)
            {
                using var before = ClipFrame(board, board.Shots[at - 1], seconds - starts[at - 1], width, height);
                Transition(canvas, before, now, shot.Transition, into / overlap, width, height);
            }
            else if (now is not null)
                canvas.DrawImage(now, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
            else
                return null;
            DrawOverlays(canvas, board, seconds, length, width, height);
            // The video's own fades, over the titles too.
            var (fadeIn, fadeOut) = VideoMontage.Fades(board);
            fadeOut = Math.Min(fadeOut, length / 3);
            fadeIn = Math.Min(fadeIn, length / 4);
            var dark = fadeIn > 0 && seconds < fadeIn ? 1 - seconds / fadeIn : fadeOut > 0 && seconds > length - fadeOut ? (seconds - (length - fadeOut)) / fadeOut : 0;
            if (dark > 0.002)
            {
                using var black = new SKPaint { Color = SKColors.Black.WithAlpha((byte)Math.Round(255 * Math.Clamp(dark, 0, 1))) };
                canvas.DrawRect(0, 0, width, height, black);
            }
            canvas.Flush();
            var pixels = new byte[width * height * 4];
            System.Runtime.InteropServices.Marshal.Copy(composed.GetPixels(), pixels, 0, pixels.Length);
            return new RawImage(width, height, pixels);
        }
    }

    // ---- a clip's frame, as its shot shows it --------------------------------------------------------------------

    /// <summary>
    /// Where in a clip's file the shot is <paramref name="into"/> seconds in: its start trimmed, at its speed, held from
    /// its freeze, backwards from the file's end, and on its last frame once the file has run out.
    /// </summary>
    internal static double SourceTime(VideoShot shot, double into, double fileSeconds)
    {
        if (shot.Freeze is { } freeze)
            into = Math.Min(into, freeze);
        var played = Math.Max(0, into) * (shot.Speed ?? 1);
        var time = shot.Reverse ? fileSeconds - played : shot.In + played;
        return Math.Clamp(time, shot.Reverse ? shot.In : 0, Math.Max(0, fileSeconds - 0.02));
    }

    /// <summary>The shot's picture at <paramref name="into"/> seconds, filling a frame of the size given; null when its clip isn't made or can't be read.</summary>
    private SKImage? ClipFrame(Storyboard board, VideoShot shot, double into, int width, int height)
    {
        var file = board.ShotVideo(shot);
        if (!File.Exists(file) || SourceOf(file) is not { } source)
            return null;
        var duration = source.Reader.Duration.TotalSeconds;
        if (Read(source, SourceTime(shot, into, duration)) is not { } frame)
            return null;
        using var seen = Turned(frame, shot);
        using var fitted = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = fitted.Canvas;
        canvas.Clear(SKColors.Black);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        var (w, h) = ((float)seen.Width, (float)seen.Height);
        SKRect Placed(float scale) => SKRect.Create((width - w * scale) / 2, (height - h * scale) / 2, w * scale, h * scale);
        var (fill, whole) = (Math.Max(width / w, height / h), Math.Min(width / w, height / h));
        if (shot.Fit == "blur")
        {
            // Whole, over a copy of itself that fills the frame, blurred and a little darker.
            var sigma = (float)Math.Max(2, height / 36.0);
            using var blur = SKImageFilter.CreateBlur(sigma, sigma);
            using var dim = SKColorFilter.CreateColorMatrix([1, 0, 0, 0, -0.06f, 0, 1, 0, 0, -0.06f, 0, 0, 1, 0, -0.06f, 0, 0, 0, 1, 0]);
            using var behind = new SKPaint { ImageFilter = blur, ColorFilter = dim };
            canvas.DrawImage(seen, Placed(fill), sampling, behind);
            canvas.DrawImage(seen, Placed(whole), sampling);
        }
        else
            canvas.DrawImage(seen, Placed(shot.Fit == "fit" ? whole : fill), sampling);
        using var fittedImage = fitted.Snapshot();

        // Its effects, then the slow zoom or pan over it, then its colours.
        using var result = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var draw = result.Canvas;
        draw.Clear(SKColors.Black);
        using var paint = new SKPaint();
        var effects = shot.Effects ?? [];
        if (effects.Contains("blur"))
        {
            var sigma = (float)Math.Max(1, height / 90.0);
            paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        }
        SKColorFilter? colours = shot.Color is { } color ? Colours(color) : null;
        if (effects.Contains("invert"))
        {
            var negative = SKColorFilter.CreateColorMatrix([-1, 0, 0, 0, 1, 0, -1, 0, 0, 1, 0, 0, -1, 0, 1, 0, 0, 0, 1, 0]);
            colours = colours is null ? negative : SKColorFilter.CreateCompose(negative, colours);
        }
        paint.ColorFilter = colours;
        draw.Save();
        if (shot.Zoom is { } zoom && shot.Seconds > 0)
        {
            var t = Math.Clamp(into / shot.Seconds, 0, 1);
            double Lerp(double a, double b) => a + (b - a) * t;
            var scale = Math.Max(1, Lerp(zoom.From.Scale, zoom.To.Scale));
            // The point it centres on, kept so the view stays inside the picture.
            var (hx, hy) = (width / (2 * scale), height / (2 * scale));
            var cx = Math.Clamp(Lerp(zoom.From.X, zoom.To.X) * width, hx, width - hx);
            var cy = Math.Clamp(Lerp(zoom.From.Y, zoom.To.Y) * height, hy, height - hy);
            draw.Translate(width / 2f, height / 2f);
            draw.Scale((float)scale);
            draw.Translate((float)-cx, (float)-cy);
        }
        if (effects.Contains("pixelate"))
        {
            // Blocks a thirtieth of the frame's height: drawn small, then large again without smoothing.
            var block = Math.Max(2, height / 30);
            using var small = SKSurface.Create(new SKImageInfo(Math.Max(1, width / block), Math.Max(1, height / block), SKColorType.Bgra8888, SKAlphaType.Premul));
            small.Canvas.DrawImage(fittedImage, SKRect.Create(small.Canvas.DeviceClipBounds.Width, small.Canvas.DeviceClipBounds.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            using var blocks = small.Snapshot();
            draw.DrawImage(blocks, SKRect.Create(width, height), new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        else
            draw.DrawImage(fittedImage, SKRect.Create(width, height), sampling, paint);
        draw.Restore();
        colours?.Dispose();
        paint.ImageFilter?.Dispose();
        if (effects.Contains("vignette"))
        {
            // Dark towards the corners, clear in the middle.
            using var shader = SKShader.CreateRadialGradient(new SKPoint(width / 2f, height / 2f), (float)(Math.Sqrt(width * (double)width + height * (double)height) / 2),
                [SKColors.Transparent, SKColors.Transparent, SKColors.Black.WithAlpha(150)], [0, 0.45f, 1], SKShaderTileMode.Clamp);
            using var corners = new SKPaint { Shader = shader };
            draw.DrawRect(0, 0, width, height, corners);
        }
        return result.Snapshot();
    }

    /// <summary>A clip's frame turned, mirrored and cropped as its shot says: the picture as the frame takes it.</summary>
    private static SKImage Turned(SKBitmap frame, VideoShot shot)
    {
        var sideways = shot.Rotate is 90 or 270;
        var (w, h) = sideways ? (frame.Height, frame.Width) : (frame.Width, frame.Height);
        var crop = shot.Crop is { } c
            ? SKRectI.Create((int)Math.Round(c.X * w), (int)Math.Round(c.Y * h), Math.Max(1, (int)Math.Round(c.Width * w)), Math.Max(1, (int)Math.Round(c.Height * h)))
            : SKRectI.Create(w, h);
        crop.Intersect(SKRectI.Create(w, h));
        if (crop.Width < 1 || crop.Height < 1)
            crop = SKRectI.Create(w, h);
        using var surface = SKSurface.Create(new SKImageInfo(crop.Width, crop.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        canvas.Translate(-crop.Left, -crop.Top);
        // Mirrored once it is turned, as the joined video does it.
        var (mirror, flip) = (shot.Flip is "horizontal" or "both", shot.Flip is "vertical" or "both");
        if (mirror || flip)
            canvas.Scale(mirror ? -1 : 1, flip ? -1 : 1, w / 2f, h / 2f);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees(shot.Rotate);
        canvas.Translate(-frame.Width / 2f, -frame.Height / 2f);
        using var image = SKImage.FromBitmap(frame);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        return surface.Snapshot();
    }

    /// <summary>A shot's colours as one filter: its light and colour (as ffmpeg's eq has them), then its look.</summary>
    internal static SKColorFilter Colours(VideoColor color)
    {
        var eq = SKColorFilter.CreateColorMatrix(PictureEdit.ColourMatrix(color.Brightness * 0.5, color.Contrast, color.Saturation));
        float[]? look = color.Look switch
        {
            "bw" => PictureEdit.ColourMatrix(0, 1, 0),
            "sepia" => [.393f, .769f, .189f, 0, 0, .349f, .686f, .168f, 0, 0, .272f, .534f, .131f, 0, 0, 0, 0, 0, 1, 0],
            "warm" => [1, 0, 0, 0, 0.05f, 0, 1, 0, 0, 0.01f, 0, 0, 1, 0, -0.05f, 0, 0, 0, 1, 0],
            "cool" => [1, 0, 0, 0, -0.05f, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0.05f, 0, 0, 0, 1, 0],
            // Faded and a little warm: lifted blacks, less contrast, yellowed.
            "vintage" => [0.9f, 0, 0, 0, 0.07f, 0, 0.85f, 0, 0, 0.06f, 0, 0, 0.7f, 0, 0.06f, 0, 0, 0, 1, 0],
            _ => null,
        };
        if (look is null)
            return eq;
        using var after = SKColorFilter.CreateColorMatrix(look);
        using (eq)
            return SKColorFilter.CreateCompose(after, eq);
    }

    /// <summary>The clip coming in over the one before it, <paramref name="t"/> of the way through (0 to 1).</summary>
    private static void Transition(SKCanvas canvas, SKImage? before, SKImage? now, string transition, double t, int width, int height)
    {
        t = Math.Clamp(t, 0, 1);
        using var paint = new SKPaint();
        void Draw(SKImage? image, double alpha)
        {
            if (image is null || alpha <= 0.002)
                return;
            paint.Color = SKColors.White.WithAlpha((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)));
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
        switch (transition)
        {
            case VideoShot.Fade or "fadewhite":
                // Through black, or white: out over the first half, in over the second.
                canvas.Clear(transition == VideoShot.Fade ? SKColors.Black : SKColors.White);
                if (t < 0.5) Draw(before, 1 - t * 2);
                else Draw(now, t * 2 - 1);
                break;
            case var name when name.StartsWith("wipe", StringComparison.Ordinal) || name.StartsWith("slide", StringComparison.Ordinal) || name.StartsWith("smooth", StringComparison.Ordinal)
                               || name.StartsWith("cover", StringComparison.Ordinal) || name.StartsWith("reveal", StringComparison.Ordinal):
                Draw(before, 1);
                canvas.Save();
                canvas.ClipRect(SKRect.Create((float)(width * t), height));
                Draw(now, 1);
                canvas.Restore();
                break;
            case "circleopen" or "circleclose" or "circlecrop" or "radial":
                Draw(before, 1);
                canvas.Save();
                var radius = (float)(Math.Sqrt(width * (double)width + height * (double)height) / 2 * t);
                using (var circle = new SKRoundRect(SKRect.Create(width / 2f - radius, height / 2f - radius, radius * 2, radius * 2), radius))
                    canvas.ClipRoundRect(circle, antialias: true);
                Draw(now, 1);
                canvas.Restore();
                break;
            default:
                Draw(before, 1);
                Draw(now, t);
                break;
        }
    }

    // ---- titles and pictures over the video ----------------------------------------------------------------------

    private void DrawOverlays(SKCanvas canvas, Storyboard board, double seconds, double length, int width, int height)
    {
        foreach (var o in board.Overlays.Concat(VideoOverlays.Captions(board)))
        {
            var start = Math.Min(o.Start, Math.Max(0, length - 0.1));
            var shows = o.Seconds > 0 ? Math.Min(o.Seconds, length - start) : length - start;
            if (shows <= 0.01 || seconds < start || seconds >= start + shows)
                continue;
            var words = o.Text.Trim().Length > 0 && o.File is null && o.Scene is null;
            if (Drawn(board, o, words, width, height) is not { } picture)
                continue;
            var fadeIn = Math.Min(o.FadeIn ?? (words ? 0.3 : 0), shows / 2);
            var fadeOut = Math.Min(o.FadeOut ?? (words ? 0.3 : 0), shows / 2);
            var into = seconds - start;
            var alpha = (o.Opacity ?? 1) * (fadeIn > 0 && into < fadeIn ? into / fadeIn : 1) * (fadeOut > 0 && into > shows - fadeOut ? (shows - into) / fadeOut : 1);
            // A slide in: from a little way off its place, easing to a stop (the square of what is left of it).
            var (slideX, slideY) = o.Motion switch
            {
                "slide up" => (0.0, height * 0.08),
                "slide down" => (0.0, -height * 0.08),
                "slide left" => (width * 0.1, 0.0),
                "slide right" => (-width * 0.1, 0.0),
                _ => (0.0, 0.0),
            };
            var slideSeconds = Math.Min(Math.Max(fadeIn, 0.5), shows / 2);
            var left = slideSeconds > 0 ? Math.Pow(1 - Math.Min(1, into / slideSeconds), 2) : 0;
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1))) };
            using var image = SKImage.FromBitmap(picture);
            canvas.DrawImage(image, (float)(slideX * left), (float)(slideY * left), new SKSamplingOptions(SKFilterMode.Nearest), paint);
        }
    }

    /// <summary>An overlay's words or picture on a clear picture the size of the frame, drawn once for each look and size; null for what isn't shown here.</summary>
    private SKBitmap? Drawn(Storyboard board, VideoOverlay o, bool words, int width, int height)
    {
        var key = $"{width}x{height}|{o.Text}|{o.File}|{o.Position}|{o.Box?.X},{o.Box?.Y},{o.Box?.Width},{o.Box?.Height}|{o.Font}|{o.Size}|{o.Color}|{o.Background}|{o.Bold}";
        if (_overlays.TryGetValue(key, out var known))
            return known;
        if (_overlays.Count > 64)
        {
            foreach (var old in _overlays.Values)
                old?.Dispose();
            _overlays.Clear();
        }
        SKBitmap? drawn = null;
        try
        {
            if (words)
                drawn = VideoOverlays.Words(o, width, height);
            else if (o.Scene is null && o.Key is null && o.File is { } name && board.FindFile(name) is { } file && !Storyboard.IsVideoFile(file))
            {
                var (place, ax, ay, _) = VideoOverlays.Place(o.Position, false);
                var box = o.Box ?? place;
                var rect = SKRectI.Create((int)Math.Round(box.X * width), (int)Math.Round(box.Y * height), Math.Max(2, (int)Math.Round(box.Width * width)), Math.Max(2, (int)Math.Round(box.Height * height)));
                drawn = VideoOverlays.Picture(file, rect, ax, ay, width, height);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }
        return _overlays[key] = drawn;
    }

    // ---- reading the clips ---------------------------------------------------------------------------------------

    private Source? SourceOf(string file)
    {
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(file).Ticks; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var key = (file.ToLowerInvariant(), stamp);
        if (!_sources.TryGetValue(key, out var source))
        {
            IVideoReader? reader;
            try { reader = decoder.OpenVideo(file, maxSide); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException) { reader = null; }
            if (reader is null)
                return null;
            // The one looked at longest ago gives its place.
            while (_sources.Count >= MostSources)
            {
                var oldest = _sources.MinBy(s => s.Value.Used);
                oldest.Value.Dispose();
                _sources.Remove(oldest.Key);
            }
            _sources[key] = source = new Source(reader);
        }
        source.Used = ++_clock;
        return source;
    }

    /// <summary>
    /// The frame showing at <paramref name="time"/>: the one read last when it is still showing, the next ones when the
    /// time is a little on (playing), else the reader goes there.
    /// </summary>
    private static SKBitmap? Read(Source source, double time)
    {
        var reader = source.Reader;
        var step = reader.FrameRate > 0 ? 1 / reader.FrameRate : 1 / 24.0;
        if (source.Frame is not null && time >= source.Time - 1e-4 && time < source.Time + step - 1e-4)
            return source.Frame;
        try
        {
            if (source.Frame is null || time < source.Time || time > source.Time + 1)
                reader.Seek(TimeSpan.FromSeconds(Math.Max(0, time)));
            // On to the frame showing then; a reader that lands a little before reads on.
            for (var n = 0; n < 90; n++)
            {
                if (reader.Next() is not { } next)
                    break;
                source.Frame?.Dispose();
                source.Frame = MediaPreview.ToBitmap(next.Frame);
                source.Time = next.Time.TotalSeconds;
                if (source.Time + step > time + 1e-4)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException) { }
        return source.Frame;
    }

    /// <summary>The clips' readers closed and the titles drawn let go: the video changed, or the panel closed.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            foreach (var source in _sources.Values)
                source.Dispose();
            _sources.Clear();
            foreach (var overlay in _overlays.Values)
                overlay?.Dispose();
            _overlays.Clear();
        }
    }

    public void Dispose() => Reset();
}
