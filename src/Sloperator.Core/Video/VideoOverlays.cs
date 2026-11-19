using System.Globalization;
using SkiaSharp;
using Sloperator.Core.Media;
using Sloperator.Core.Render;

namespace Sloperator.Core.Video;

/// <summary>
/// An overlay as ffmpeg lays it over the joined video: a picture the size of the frame (words and
/// pictures drawn where they go, see <see cref="VideoOverlays"/>), or a video scaled into its box.
/// </summary>
/// <param name="Still">A picture shown for its seconds; else a video, played round when shorter.</param>
/// <param name="AlignX">Where in its box a video smaller than it sits: 0 left, 0.5 the middle, 1 right (and <paramref name="AlignY"/> top to bottom).</param>
public sealed record OverlayInput(string File, bool Still, int X, int Y, int Width, int Height, double Start, double Seconds,
    double FadeIn, double FadeOut, double Opacity, double AlignX = 0.5, double AlignY = 0.5)
{
    /// <summary>A colour keyed out of it ("0x00FF00", as ffmpeg's chromakey takes it); null none.</summary>
    public string? Key { get; init; }

    /// <summary>A picture the frame's size with what it shows drawn in place (words, a picture placed): laid at 0,0 as it is.</summary>
    public bool Framed { get; init; }

    /// <summary>Where it starts from as it comes in, in pixels from its place (it eases there over <see cref="SlideSeconds"/>); 0, 0 for no slide.</summary>
    public int SlideX { get; init; }

    public int SlideY { get; init; }

    public double SlideSeconds { get; init; }
}

/// <summary>
/// Gets a video's overlays ready to be laid over it: words drawn with Skia in the font asked for
/// (with a box behind them or a shadow under them, so they read over any picture), pictures and SVGs
/// placed in their box, scenes drawn with a clear background, and captions from the shots' lines.
/// What is drawn goes in Generated/overlays.
/// </summary>
public static class VideoOverlays
{
    /// <summary>Where a position puts something on the frame (in fractions), how it sits in that box, and the height of words there (a fraction of the frame's).</summary>
    internal static (VideoBox Box, double AlignX, double AlignY, double TextSize) Place(string position, bool words)
    {
        static VideoBox B(double x, double y, double w, double h) => new() { X = x, Y = y, Width = w, Height = h };
        return position switch
        {
            "top" => (B(0.05, 0.06, 0.9, 0.3), 0.5, 0, 0.07),
            "bottom" => (B(0.05, words ? 0.62 : 0.66, 0.9, words ? 0.3 : 0.3), 0.5, 1, 0.05),
            "lower third" or "lowerthird" => (B(0.06, 0.64, 0.6, 0.24), 0, 0.5, 0.05),
            "top left" or "topleft" => (words ? B(0.05, 0.05, 0.45, 0.3) : B(0.04, 0.04, 0.25, 0.25), 0, 0, 0.045),
            "top right" or "topright" => (words ? B(0.5, 0.05, 0.45, 0.3) : B(0.71, 0.04, 0.25, 0.25), 1, 0, 0.045),
            "bottom left" or "bottomleft" => (words ? B(0.05, 0.65, 0.45, 0.3) : B(0.04, 0.71, 0.25, 0.25), 0, 1, 0.045),
            "bottom right" or "bottomright" => (words ? B(0.5, 0.65, 0.45, 0.3) : B(0.71, 0.71, 0.25, 0.25), 1, 1, 0.045),
            "full" or "fullscreen" or "full screen" => (B(0, 0, 1, 1), 0.5, 0.5, 0.09),
            "center" or "centre" or "middle" => (words ? B(0.05, 0.05, 0.9, 0.9) : B(0.25, 0.25, 0.5, 0.5), 0.5, 0.5, 0.09),
            _ => words ? (B(0.05, 0.05, 0.9, 0.9), 0.5, 0.5, 0.09) : (B(0, 0, 1, 1), 0.5, 0.5, 0.09),
        };
    }

    /// <summary>The overlays of the video (and its captions) ready for ffmpeg; what couldn't be shown is said in <paramref name="notes"/>.</summary>
    public static async Task<List<OverlayInput>> PrepareAsync(Storyboard board, double length, SceneRenderer? scenes, string? ffmpeg, List<string> notes, CancellationToken ct)
    {
        var inputs = new List<OverlayInput>();
        var folder = Path.Combine(board.GeneratedFolder, "overlays");
        var (w, h) = (board.Width, board.Height);
        foreach (var o in board.Overlays.Concat(Captions(board)))
        {
            var start = Math.Min(o.Start, Math.Max(0, length - 0.1));
            var seconds = o.Seconds > 0 ? Math.Min(o.Seconds, length - start) : length - start;
            if (seconds <= 0.01)
                continue;
            var words = o.Text.Trim().Length > 0 && o.File is null && o.Scene is null;
            var (box, ax, ay, _) = Place(o.Position, words);
            box = o.Box ?? box;
            var (bx, by, bw, bh) = ((int)Math.Round(box.X * w), (int)Math.Round(box.Y * h), Math.Max(2, (int)Math.Round(box.Width * w)), Math.Max(2, (int)Math.Round(box.Height * h)));
            var fadeIn = o.FadeIn ?? (words ? 0.3 : 0);
            var fadeOut = o.FadeOut ?? (words ? 0.3 : 0);
            (fadeIn, fadeOut) = (Math.Min(fadeIn, seconds / 2), Math.Min(fadeOut, seconds / 2));
            var opacity = o.Opacity ?? 1;
            // A slide in: from a little way off its place, as long as its fade in (at least half a second).
            var (slideX, slideY) = o.Motion switch
            {
                "slide up" => (0, (int)Math.Round(h * 0.08)),
                "slide down" => (0, -(int)Math.Round(h * 0.08)),
                "slide left" => ((int)Math.Round(w * 0.1), 0),
                "slide right" => (-(int)Math.Round(w * 0.1), 0),
                _ => (0, 0),
            };
            var slideSeconds = Math.Min(Math.Max(fadeIn, 0.5), seconds / 2);
            try
            {
                Directory.CreateDirectory(folder);
                if (words)
                {
                    var png = Path.Combine(folder, Storyboard.Safe(o.Id) + ".png");
                    DrawWords(o, w, h, png);
                    inputs.Add(new OverlayInput(png, true, 0, 0, w, h, start, seconds, fadeIn, fadeOut, opacity) { Framed = true, SlideX = slideX, SlideY = slideY, SlideSeconds = slideSeconds });
                }
                else if (o.Scene is { } sceneName)
                {
                    var scene = board.FindFile(sceneName) ?? throw new VideoException($"Overlay {o.Id} shows the scene {sceneName}, which isn't in References or there.");
                    if (scenes is null)
                        throw new VideoException("Scenes can't be drawn here.");
                    var made = await scenes.RenderAsync(scene, Path.Combine(folder, Storyboard.Safe(o.Id) + ".webm"),
                        new SceneOptions { Width = bw + bw % 2, Height = bh + bh % 2, Fps = board.Fps, Seconds = seconds, Transparent = true, Still = false }, ffmpeg, null, ct).ConfigureAwait(false);
                    notes.AddRange(made.Errors.Select(e => $"Overlay {o.Id}'s scene: {e}"));
                    inputs.Add(new OverlayInput(made.Output, false, bx, by, bw, bh, start, seconds, fadeIn, fadeOut, opacity, ax, ay) { Key = ParseColour(o.Key) is { } sk ? $"0x{sk.Red:X2}{sk.Green:X2}{sk.Blue:X2}" : null });
                }
                else if (o.File is { } fileName)
                {
                    var file = board.FindFile(fileName) ?? throw new VideoException($"Overlay {o.Id} shows {fileName}, which isn't in References or there.");
                    var key = ParseColour(o.Key) is { } k ? $"0x{k.Red:X2}{k.Green:X2}{k.Blue:X2}" : null;
                    if (Storyboard.IsVideoFile(file) || key is not null && !SvgFile.Handles(file))
                        // A keyed picture goes in as it is, placed by ffmpeg, so the key works on its own colours.
                        inputs.Add(new OverlayInput(file, !Storyboard.IsVideoFile(file), bx, by, bw, bh, start, seconds, fadeIn, fadeOut, opacity, ax, ay) { Key = key });
                    else
                    {
                        var png = Path.Combine(folder, Storyboard.Safe(o.Id) + ".png");
                        DrawPicture(file, new SKRectI(bx, by, bx + bw, by + bh), ax, ay, w, h, png);
                        inputs.Add(new OverlayInput(png, true, 0, 0, w, h, start, seconds, fadeIn, fadeOut, opacity) { Framed = true });
                    }
                }
            }
            catch (Exception ex) when (ex is VideoException or RenderException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                notes.Add($"Overlay {o.Id} isn't shown: {ex.Message}");
            }
        }
        return inputs;
    }

    /// <summary>The shots' lines as captions at the bottom, while they are said, when the video has captions on.</summary>
    internal static IEnumerable<VideoOverlay> Captions(Storyboard board)
    {
        if (!board.Captions)
            yield break;
        var (starts, _) = board.Timing();
        for (var i = 0; i < board.Shots.Count; i++)
            if (board.Shots[i].Line.Trim() is { Length: > 0 } line)
                yield return new VideoOverlay
                {
                    Id = "caption-" + board.Shots[i].Id, Text = line, Position = "bottom", Start = starts[i] + VideoMaker.LineLead,
                    Seconds = Math.Max(0.5, board.Shots[i].Seconds - VideoMaker.LineLead), Size = 0.045, Background = "#000000A0", Bold = false,
                };
    }

    /// <summary>Draws an overlay's words on a clear picture the size of the frame, where its position puts them.</summary>
    internal static void DrawWords(VideoOverlay o, int width, int height, string png)
    {
        using var bitmap = Words(o, width, height);
        Save(bitmap, png);
    }

    /// <summary>An overlay's words on a clear picture the size of the frame, where its position puts them (the joined video's, and the panel's look at it).</summary>
    internal static SKBitmap Words(VideoOverlay o, int width, int height)
    {
        var (place, ax, ay, defaultSize) = Place(o.Position, true);
        var box = o.Box ?? place;
        var rect = SKRect.Create((float)(box.X * width), (float)(box.Y * height), (float)(box.Width * width), (float)(box.Height * height));
        var size = (float)((o.Size ?? defaultSize) * height);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var typeface = SKTypeface.FromFamilyName(o.Font ?? "Segoe UI", o.Bold ?? true ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        var lines = Wrap(o.Text.Replace("\\n", "\n"), font, rect.Width);
        // Words that don't fit the box's height get smaller until they do.
        while (lines.Count * size * 1.25f > rect.Height && size > 8)
        {
            size *= 0.9f;
            font.Size = size;
            lines = Wrap(o.Text.Replace("\\n", "\n"), font, rect.Width);
        }
        var lineHeight = size * 1.25f;
        var blockHeight = lines.Count * lineHeight;
        var blockWidth = lines.Select(l => font.MeasureText(l)).DefaultIfEmpty(0).Max();
        var top = rect.Top + (float)((rect.Height - blockHeight) * ay);
        var left = rect.Left + (float)((rect.Width - blockWidth) * ax);
        var colour = ParseColour(o.Color) ?? SKColors.White;
        if (ParseColour(o.Background) is { } background)
        {
            var pad = size * 0.35f;
            using var fill = new SKPaint { Color = background, IsAntialias = true };
            canvas.DrawRoundRect(SKRect.Create(left - pad, top - pad * 0.6f, blockWidth + pad * 2, blockHeight + pad * 1.2f), pad * 0.5f, pad * 0.5f, fill);
        }
        using var paint = new SKPaint { Color = colour, IsAntialias = true };
        if (o.Background is null)
            paint.ImageFilter = SKImageFilter.CreateDropShadow(0, size * 0.04f, size * 0.08f, size * 0.08f, new SKColor(0, 0, 0, 170));
        // A line round the letters, under them: a tenth of their height, with round corners so no spike sticks out of a W or an M.
        using var outline = ParseColour(o.Outline) is { } edge
            ? new SKPaint { Color = edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, size * 0.1f), StrokeJoin = SKStrokeJoin.Round }
            : null;
        for (var i = 0; i < lines.Count; i++)
        {
            var lineWidth = font.MeasureText(lines[i]);
            var x = rect.Left + (float)((rect.Width - lineWidth) * ax);
            var baseline = top + i * lineHeight + size * 0.95f;
            if (outline is not null)
                canvas.DrawText(lines[i], x, baseline, SKTextAlign.Left, font, outline);
            canvas.DrawText(lines[i], x, baseline, SKTextAlign.Left, font, paint);
        }
        return bitmap;
    }

    /// <summary>The words in lines no wider than <paramref name="width"/>, broken between words (a word longer than that on a line of its own).</summary>
    private static List<string> Wrap(string text, SKFont font, float width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var tried = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && font.MeasureText(tried) > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                    line = tried;
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>Draws a picture (an SVG too) into its box on a clear picture the size of the frame, all of it in view.</summary>
    internal static void DrawPicture(string file, SKRectI box, double ax, double ay, int width, int height, string png)
    {
        using var bitmap = Picture(file, box, ax, ay, width, height);
        Save(bitmap, png);
    }

    /// <summary>A picture (an SVG too) in its box on a clear picture the size of the frame, all of it in view.</summary>
    internal static SKBitmap Picture(string file, SKRectI box, double ax, double ay, int width, int height)
    {
        using var picture = SvgFile.Handles(file)
            ? (SvgFile.Open(file) ?? throw new InvalidDataException($"{Path.GetFileName(file)} draws nothing.")).RenderAtLeast(Math.Max(box.Width, box.Height))
            : PictureFile.Decode(file) ?? throw new InvalidDataException($"{Path.GetFileName(file)} isn't a picture this can read.");
        var scale = Math.Min(box.Width / (double)picture.Width, box.Height / (double)picture.Height);
        var (w, h) = ((float)(picture.Width * scale), (float)(picture.Height * scale));
        var x = box.Left + (float)((box.Width - w) * ax);
        var y = box.Top + (float)((box.Height - h) * ay);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(picture);
        canvas.DrawImage(image, SKRect.Create(x, y, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return bitmap;
    }

    private static void Save(SKBitmap bitmap, string png)
    {
        MediaRelease.Release(png);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(png);
        data.SaveTo(file);
    }

    /// <summary>#RGB, #RRGGBB or #RRGGBBAA (or a colour's name Skia knows); null for none or one it can't read.</summary>
    /// <summary>Whether a words' colour is a dark one (so a line round the letters should be light); white, the usual colour, is not.</summary>
    public static bool IsDark(string? colour) => ParseColour(colour) is { } c && 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue < 110;

    internal static SKColor? ParseColour(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;
        var t = text.Trim();
        if (t.StartsWith('#') && t.Length == 9 && uint.TryParse(t[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba))
            return new SKColor((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
        if (SKColor.TryParse(t, out var colour))
            return colour;
        return Named.TryGetValue(t, out var named) ? named : null;
    }

    private static readonly Dictionary<string, SKColor> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = SKColors.White, ["black"] = SKColors.Black, ["red"] = SKColors.Red, ["green"] = SKColors.Green, ["blue"] = SKColors.Blue,
        ["yellow"] = SKColors.Yellow, ["orange"] = SKColors.Orange, ["purple"] = SKColors.Purple, ["pink"] = SKColors.Pink, ["gray"] = SKColors.Gray,
        ["grey"] = SKColors.Gray, ["gold"] = SKColors.Gold, ["cyan"] = SKColors.Cyan, ["magenta"] = SKColors.Magenta, ["brown"] = SKColors.Brown,
        ["transparent"] = SKColors.Transparent,
    };
}
