using System.Runtime.InteropServices;
using Sloperator.Core.Documents;
using SkiaSharp;

namespace Sloperator.Core.Media;

/// <summary>
/// The pictures of the Image editor panel (SloperatorData\pictures, beside the videos), a folder each: its
/// versions are the picture files in it, oldest first. The agent's comfyui runs copy what they make
/// into the folder (save_to), so a redo is a new version and the earlier ones stay.
/// </summary>
public static class PictureFolder
{
    /// <summary>PNG, JPEG and WebP, what ComfyUI saves.</summary>
    public static readonly MediaFolders Folders = new("Untitled picture", ".png", ".jpg", ".jpeg", ".webp");

    /// <summary>A file that is a version of a picture: a PNG, JPEG or WebP, what ComfyUI saves.</summary>
    public static bool IsPicture(string path) => Folders.IsVersionFile(path);

    /// <summary>A picture folder's versions: its picture files, oldest first (by when they were written, then by name).</summary>
    public static IReadOnlyList<string> Versions(string folder) => Folders.Versions(folder);

    /// <summary>The pictures in <paramref name="root"/>, a folder each, the one changed last first.</summary>
    public static IReadOnlyList<string> All(string root) => Folders.All(root);

    /// <summary>A new picture's folder, made: "Untitled picture", "Untitled picture 2" when that is taken.</summary>
    public static string New(string root) => Folders.New(root);

    /// <summary>
    /// The mask of <paramref name="area"/> on a picture <paramref name="width"/> by <paramref name="height"/>, saved
    /// as a PNG at <paramref name="path"/>: white in the box, black elsewhere, its edge a soft ramp a few pixels
    /// wide each way (a fortieth of the box's shorter side, 4 at least), so with DifferentialDiffusion the
    /// redrawn part blends into what is kept rather than ending in a hard seam.
    /// </summary>
    public static void SaveMask(DocumentArea area, int width, int height, string path)
    {
        var (x, y, w, h) = area.Pixels(width, height);
        var feather = Math.Max(4, Math.Min(w, h) / 40);
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            var dy = Inside(row, y, y + h, feather);
            if (dy == 0)
                continue;
            for (var col = 0; col < width; col++)
            {
                var v = (byte)Math.Round(255 * Math.Min(dy, Inside(col, x, x + w, feather)));
                var i = (row * width + col) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
            }
        }
        // Opaque everywhere, the black rows left out above too.
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        DocumentArea.SavePng(new Platform.RawImage(width, height, pixels), path);

        // How far inside [from, to) a pixel is, 1 at least half a ramp in, 0 half a ramp out.
        static double Inside(int p, int from, int to, int ramp)
        {
            var centre = p + 0.5;
            var d = Math.Min(centre - from, to - centre);
            return Math.Clamp(0.5 + d / ramp, 0, 1);
        }
    }

    /// <summary>
    /// The box around what <paramref name="strokes"/> painted on a picture <paramref name="width"/> by
    /// <paramref name="height"/>, in parts of it and kept inside it; null when they painted nothing.
    /// </summary>
    public static DocumentArea? AreaOf(IReadOnlyList<BrushStroke> strokes, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var stroke in strokes)
        {
            // Half the brush each way: its width is in parts of the picture's width, so more parts of a lower height.
            var (rx, ry) = (stroke.Width / 2, stroke.Width / 2 * width / height);
            foreach (var (x, y) in stroke.Points)
                (left, top, right, bottom) = (Math.Min(left, x - rx), Math.Min(top, y - ry), Math.Max(right, x + rx), Math.Max(bottom, y + ry));
        }
        return right < left ? null : DocumentArea.Between(left, top, right, bottom);
    }

    /// <summary>
    /// The mask of what <paramref name="strokes"/> painted on a picture <paramref name="width"/> by
    /// <paramref name="height"/>, saved as a PNG at <paramref name="path"/>: white where the brush went, black
    /// elsewhere, its edge softened by an eighth of the thinnest brush (4 pixels at least), as the box's mask is,
    /// so the redrawn part blends into what is kept.
    /// </summary>
    public static void SaveMask(IReadOnlyList<BrushStroke> strokes, int width, int height, string path)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            var thinnest = strokes.Count == 0 ? 0 : strokes.Min(s => s.Width) * width;
            var soft = (float)Math.Max(4, thinnest / 8);
            // All of it painted, then softened as one shape: softened stroke by stroke, where two met they left a dip.
            using var blur = SKImageFilter.CreateBlur(soft / 2, soft / 2);
            using var layer = new SKPaint { ImageFilter = blur };
            canvas.SaveLayer(layer);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            foreach (var stroke in strokes)
            {
                var brush = (float)(stroke.Width * width);
                if (stroke.Points.Count == 1)
                {
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawCircle((float)(stroke.Points[0].X * width), (float)(stroke.Points[0].Y * height), brush / 2, paint);
                    continue;
                }
                // Each piece of the line with round ends, which meet as round corners.
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = brush;
                canvas.DrawPoints(SKPointMode.Polygon, [.. stroke.Points.Select(p => new SKPoint((float)(p.X * width), (float)(p.Y * height)))], paint);
            }
            canvas.Restore();
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>A picture's size in pixels, read from its header; null when it can't be read.</summary>
    public static (int Width, int Height)? SizeOf(string path)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            return codec is null ? null : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            return null;
        }
    }
}
