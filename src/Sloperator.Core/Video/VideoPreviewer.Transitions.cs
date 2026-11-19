using SkiaSharp;

namespace Sloperator.Core.Video;

// How one clip gives way to the next (every transition a shot may name), and what is drawn over a clip's picture
// with code of its own: film grain and a colour taken out (a green screen).
public sealed partial class VideoPreviewer
{
    private static readonly SKSamplingOptions Plain = new(SKFilterMode.Nearest);
    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.None);

    /// <summary>How wide the soft edge of a smooth wipe is, of the frame.</summary>
    private const float Band = 0.2f;

    /// <summary>The clip coming in over the one before it, <paramref name="t"/> of the way through (0 to 1), as its transition has it.</summary>
    private static void Transition(SKCanvas canvas, SKImage? before, SKImage? now, string transition, double t, int width, int height)
    {
        var p = (float)Math.Clamp(t, 0, 1);
        var (w, h) = ((float)width, (float)height);
        var frame = SKRect.Create(w, h);
        using var paint = new SKPaint();

        void Draw(SKImage? image, float alpha = 1, float x = 0, float y = 0)
        {
            if (image is null || alpha <= 0.002f)
                return;
            paint.Color = SKColors.White.WithAlpha((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)));
            canvas.DrawImage(image, x, y, Plain, paint);
        }
        void Into(SKImage? image, SKRect rect)
        {
            if (image is not null && rect.Width > 0.5f && rect.Height > 0.5f)
                canvas.DrawImage(image, rect, Smooth);
        }
        // The picture only where the clip says (a rectangle, a circle, a slice of a pie).
        void Clipped(SKImage? image, Action clip)
        {
            canvas.Save();
            clip();
            Draw(image);
            canvas.Restore();
        }
        // The picture as far as a mask lets it through: where the mask is clear the picture under it stays.
        void Masked(SKImage? image, SKShader mask)
        {
            if (image is null)
                return;
            canvas.SaveLayer();
            Draw(image);
            using var through = new SKPaint { Shader = mask, BlendMode = SKBlendMode.DstIn };
            canvas.DrawRect(frame, through);
            canvas.Restore();
            mask.Dispose();
        }
        // A soft edge across the frame: the picture shows from the point `from` on, fully by `to`.
        SKShader Edge(SKPoint from, SKPoint to) =>
            SKShader.CreateLinearGradient(from, to, [SKColors.Transparent, SKColors.White], [0, 1], SKShaderTileMode.Clamp);
        // A circle of the picture, its rim a little soft.
        SKShader Circle(float radius, bool inside) =>
            SKShader.CreateRadialGradient(new SKPoint(w / 2, h / 2), Math.Max(1, radius + 2),
                inside ? [SKColors.White, SKColors.White, SKColors.Transparent] : [SKColors.Transparent, SKColors.Transparent, SKColors.White],
                [0, Math.Max(0, radius - 2) / Math.Max(1, radius + 2), 1], SKShaderTileMode.Clamp);
        var corner = (float)(Math.Sqrt(w * (double)w + h * (double)h) / 2);

        switch (transition)
        {
            case VideoShot.Fade or "fadewhite":
                // Through black, or white: out over the first half, in over the second.
                canvas.Clear(transition == VideoShot.Fade ? SKColors.Black : SKColors.White);
                if (p < 0.5f) Draw(before, 1 - p * 2);
                else Draw(now, p * 2 - 1);
                break;

            // A wipe: the new picture where the edge has passed, both standing still.
            case "wipeleft":
                Draw(before);
                Clipped(now, () => canvas.ClipRect(SKRect.Create(w * (1 - p), 0, w * p, h)));
                break;
            case "wiperight":
                Draw(before);
                Clipped(now, () => canvas.ClipRect(SKRect.Create(0, 0, w * p, h)));
                break;
            case "wipeup":
                Draw(before);
                Clipped(now, () => canvas.ClipRect(SKRect.Create(0, h * (1 - p), w, h * p)));
                break;
            case "wipedown":
                Draw(before);
                Clipped(now, () => canvas.ClipRect(SKRect.Create(0, 0, w, h * p)));
                break;

            // A slide: the new picture pushes the old one out.
            case "slideleft":
                Draw(before, 1, -w * p);
                Draw(now, 1, w * (1 - p));
                break;
            case "slideright":
                Draw(before, 1, w * p);
                Draw(now, 1, -w * (1 - p));
                break;
            case "slideup":
                Draw(before, 1, 0, -h * p);
                Draw(now, 1, 0, h * (1 - p));
                break;
            case "slidedown":
                Draw(before, 1, 0, h * p);
                Draw(now, 1, 0, -h * (1 - p));
                break;

            // A wipe with a soft edge.
            case "smoothleft":
            {
                var at = w * ((1 + Band) * (1 - p) - Band);
                Draw(before);
                Masked(now, Edge(new SKPoint(at, 0), new SKPoint(at + w * Band, 0)));
                break;
            }
            case "smoothright":
            {
                var at = w * ((1 + Band) * p - Band);
                Draw(before);
                Masked(now, Edge(new SKPoint(at + w * Band, 0), new SKPoint(at, 0)));
                break;
            }
            case "smoothup":
            {
                var at = h * ((1 + Band) * (1 - p) - Band);
                Draw(before);
                Masked(now, Edge(new SKPoint(0, at), new SKPoint(0, at + h * Band)));
                break;
            }
            case "smoothdown":
            {
                var at = h * ((1 + Band) * p - Band);
                Draw(before);
                Masked(now, Edge(new SKPoint(0, at + h * Band), new SKPoint(0, at)));
                break;
            }

            // From a corner, with a soft edge: the edge runs along the frame's other diagonal.
            case "diagtl" or "diagtr" or "diagbl" or "diagbr":
            {
                var (fromLeft, fromTop) = (transition is "diagtl" or "diagbl", transition is "diagtl" or "diagtr");
                var start = new SKPoint(fromLeft ? 0 : w, fromTop ? 0 : h);
                var end = new SKPoint(fromLeft ? w : 0, fromTop ? h : 0);
                // Along the diagonal: the edge at `at` of it, the picture shown behind the edge (towards its corner).
                var at = (1 + Band) * p - Band;
                SKPoint On(float k) => new(start.X + (end.X - start.X) * k, start.Y + (end.Y - start.Y) * k);
                Draw(before);
                Masked(now, Edge(On(at + Band), On(at)));
                break;
            }

            // Laid over, or pulled away.
            case "coverleft":
                Draw(before);
                Draw(now, 1, w * (1 - p));
                break;
            case "coverright":
                Draw(before);
                Draw(now, 1, -w * (1 - p));
                break;
            case "revealleft":
                Draw(now);
                Draw(before, 1, -w * p);
                break;
            case "revealright":
                Draw(now);
                Draw(before, 1, w * p);
                break;

            // A circle opening on the new picture, or closing on the old one.
            case "circleopen":
                Draw(before);
                Masked(now, Circle(corner * p, inside: true));
                break;
            case "circleclose":
                Draw(now);
                Masked(before, Circle(corner * (1 - p), inside: true));
                break;
            // The old picture shrinks into a circle on black, the new one grows out of one.
            case "circlecrop":
                canvas.Clear(SKColors.Black);
                if (p < 0.5f) Masked(before, Circle(corner * (1 - p * 2), inside: true));
                else Masked(now, Circle(corner * (p * 2 - 1), inside: true));
                break;
            case "rectcrop":
            {
                canvas.Clear(SKColors.Black);
                var k = p < 0.5f ? 1 - p * 2 : p * 2 - 1;
                Clipped(p < 0.5f ? before : now, () => canvas.ClipRect(SKRect.Create(w / 2 * (1 - k), h / 2 * (1 - k), w * k, h * k)));
                break;
            }
            // A clock's hand going round from the top.
            case "radial":
                Draw(before);
                if (p >= 0.999f)
                    Draw(now);
                else if (p > 0.001f)
                    Clipped(now, () =>
                    {
                        using var builder = new SKPathBuilder();
                        builder.MoveTo(w / 2, h / 2);
                        builder.ArcTo(SKRect.Create(w / 2 - corner * 1.5f, h / 2 - corner * 1.5f, corner * 3, corner * 3), -90, 360 * p, false);
                        builder.Close();
                        using var pie = builder.Detach();
                        canvas.ClipPath(pie, antialias: true);
                    });
                break;

            // Squeezed flat along one way, the new picture behind it.
            case "squeezeh":
                Draw(now);
                Into(before, SKRect.Create(0, h / 2 * p, w, h * (1 - p)));
                break;
            case "squeezev":
                Draw(now);
                Into(before, SKRect.Create(w / 2 * p, 0, w * (1 - p), h));
                break;

            // The old picture comes at the viewer and thins out.
            case "zoomin":
            {
                Draw(now);
                var scale = 1 + p * 1.5f;
                if (before is not null && p < 0.999f)
                {
                    paint.Color = SKColors.White.WithAlpha((byte)Math.Round(255 * (1 - p)));
                    canvas.DrawImage(before, SKRect.Create(w / 2 - w * scale / 2, h / 2 - h * scale / 2, w * scale, h * scale), Smooth, paint);
                }
                break;
            }

            // Both in blocks that grow to the middle of it and shrink again, one fading into the other.
            case "pixelize":
            {
                var block = 1 + (int)Math.Round(Math.Min(width, height) / 18.0 * (1 - Math.Abs(p * 2 - 1)));
                if (block <= 1)
                {
                    Draw(before);
                    Draw(now, p);
                    break;
                }
                using var small = SKSurface.Create(new SKImageInfo(Math.Max(1, width / block), Math.Max(1, height / block), SKColorType.Bgra8888, SKAlphaType.Premul));
                var into = SKRect.Create(small.Canvas.DeviceClipBounds.Width, small.Canvas.DeviceClipBounds.Height);
                small.Canvas.Clear(SKColors.Black);
                if (before is not null)
                    small.Canvas.DrawImage(before, into, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (now is not null)
                {
                    using var over = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * p)) };
                    small.Canvas.DrawImage(now, into, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), over);
                }
                using var blocks = small.Snapshot();
                canvas.DrawImage(blocks, frame, Plain);
                break;
            }

            // One fading into the other, both smeared sideways most in the middle of it.
            case "hblur":
            {
                var sigma = w * 0.06f * (1 - Math.Abs(p * 2 - 1));
                if (sigma < 0.5f)
                {
                    Draw(before);
                    Draw(now, p);
                    break;
                }
                using var blur = SKImageFilter.CreateBlur(sigma, 0.01f, SKShaderTileMode.Clamp, null, frame);
                using var smeared = new SKPaint { ImageFilter = blur };
                canvas.SaveLayer(smeared);
                Draw(before);
                Draw(now, p);
                canvas.Restore();
                break;
            }

            // Ten strips, each wiped in turn across it.
            case "hlslice" or "hrslice" or "vuslice" or "vdslice":
            {
                const int strips = 10;
                var (across, forward) = (transition[0] == 'h', transition is "hlslice" or "vuslice");
                Draw(before);
                for (var i = 0; i < strips; i++)
                {
                    // Each starts a little after the one before it, in the way it goes.
                    var order = (forward ? i : strips - 1 - i) / (float)strips;
                    var local = Math.Clamp(p * 1.5f - order * 0.5f, 0, 1);
                    if (local <= 0)
                        continue;
                    var size = (across ? w : h) / strips;
                    var shown = size * local;
                    var from = i * size + (forward ? 0 : size - shown);
                    var strip = across ? SKRect.Create(from, 0, shown, h) : SKRect.Create(0, from, w, shown);
                    Clipped(now, () => canvas.ClipRect(strip));
                }
                break;
            }

            // A dissolve, and what has no look of its own here: one fades into the other.
            default:
                Draw(before);
                Draw(now, p);
                break;
        }
    }

    // ---- film grain ------------------------------------------------------------------------------------------------

    private static readonly Lazy<SKImage> GrainTile = new(() =>
    {
        // Grey specks around the middle grey, the same every time (a seed of its own), tiled over the picture.
        const int side = 256;
        var random = new Random(20261119);
        var pixels = new byte[side * side * 4];
        for (var i = 0; i < side * side; i++)
        {
            var v = (byte)Math.Clamp(128 + (random.NextDouble() + random.NextDouble() - 1) * 22, 0, 255);
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = (v, v, v, 255);
        }
        using var bitmap = new SKBitmap(new SKImageInfo(side, side, SKColorType.Bgra8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return SKImage.FromBitmap(bitmap);
    });

    /// <summary>
    /// Film grain over a picture: specks that lighten and darken it a little, in another place each frame (the same
    /// place for the same frame, so a frame looks the same whenever it is drawn), as large in a small frame as in a large one.
    /// </summary>
    private static void Grain(SKCanvas canvas, int width, int height, double seconds)
    {
        var frame = (int)Math.Floor(seconds * 24);
        var random = new Random(frame * 7919 + 17);
        // A speck a 480th of the frame's height: a pixel and a half at 720p, as film grain is coarser than a sensor's noise (and takes far fewer bits to keep).
        var scale = Math.Max(0.25f, height / 480f);
        var matrix = SKMatrix.CreateScale(scale, scale).PostConcat(SKMatrix.CreateTranslation(-random.Next(256) * scale, -random.Next(256) * scale));
        using var shader = GrainTile.Value.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, new SKSamplingOptions(SKFilterMode.Linear), matrix);
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Overlay };
        canvas.DrawRect(0, 0, width, height, paint);
    }

    // ---- a colour taken out ----------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="picture"/> with <paramref name="key"/> taken out of it (a green screen), so what is under it shows
    /// there: a pixel as far from the key's colour as ffmpeg's chromakey measures it (by its colour, not its brightness)
    /// is cleared when it is close (0.15) and half kept over the next 0.08. Its own pixels are changed.
    /// </summary>
    internal static void KeyOut(SKBitmap picture, SKColor key)
    {
        // Blue first, or red first: the two orders pictures are held in here.
        var redFirst = picture.ColorType == SKColorType.Rgba8888;
        if (!redFirst && picture.ColorType != SKColorType.Bgra8888)
            return;
        const double similar = 0.15, blend = 0.08;
        static (double U, double V) Chroma(double r, double g, double b) =>
            (-0.168736 * r - 0.331264 * g + 0.5 * b, 0.5 * r - 0.418688 * g - 0.081312 * b);
        var (ku, kv) = Chroma(key.Red, key.Green, key.Blue);
        var pixels = new byte[picture.RowBytes * picture.Height];
        System.Runtime.InteropServices.Marshal.Copy(picture.GetPixels(), pixels, 0, pixels.Length);
        var premul = picture.AlphaType == SKAlphaType.Premul;
        Parallel.For(0, picture.Height, y =>
        {
            var row = y * picture.RowBytes;
            for (var x = 0; x < picture.Width; x++)
            {
                var i = row + x * 4;
                var a = pixels[i + 3];
                if (a == 0)
                    continue;
                double b = pixels[i + (redFirst ? 2 : 0)], g = pixels[i + 1], r = pixels[i + (redFirst ? 0 : 2)];
                if (premul && a < 255)
                    (b, g, r) = (b * 255 / a, g * 255 / a, r * 255 / a);
                var (u, v) = Chroma(r, g, b);
                var far = Math.Sqrt(((u - ku) * (u - ku) + (v - kv) * (v - kv)) / (255.0 * 255.0 * 2));
                var keep = far < similar ? 0 : Math.Min(1, (far - similar) / blend);
                if (keep >= 1)
                    continue;
                if (premul)
                    (pixels[i], pixels[i + 1], pixels[i + 2]) = ((byte)(pixels[i] * keep), (byte)(pixels[i + 1] * keep), (byte)(pixels[i + 2] * keep));
                pixels[i + 3] = (byte)(a * keep);
            }
        });
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, picture.GetPixels(), pixels.Length);
    }
}
