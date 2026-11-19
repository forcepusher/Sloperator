using SkiaSharp;

namespace Sloperator.Core.Video;

// How one clip gives way to the next (every transition a shot may name), and what is drawn over a clip's picture
// with code of its own: film grain and a colour taken out (a green screen).
public sealed partial class VideoPreviewer
{
    private static readonly SKSamplingOptions Plain = new(SKFilterMode.Nearest);
    private static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.None);


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
        // How much of a picture shows at each point of the frame (x and y from 0 to 1), by a rule: worked out on a
        // coarse grid and smoothed over the frame, which is all a soft edge needs. The rules are ffmpeg xfade's own.
        SKShader Field(Func<float, float, float> shows)
        {
            const int across = 96, down = 54;
            var grid = new byte[across * down * 4];
            for (var y = 0; y < down; y++)
                for (var x = 0; x < across; x++)
                {
                    var v = (byte)Math.Round(255 * Math.Clamp(shows((x + 0.5f) / across, (y + 0.5f) / down), 0, 1));
                    var i = (y * across + x) * 4;
                    (grid[i], grid[i + 1], grid[i + 2], grid[i + 3]) = (v, v, v, v);
                }
            using var bitmap = new SKBitmap(new SKImageInfo(across, down, SKColorType.Bgra8888, SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(grid, 0, bitmap.GetPixels(), grid.Length);
            using var image = SKImage.FromBitmap(bitmap);
            return image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Smooth, SKMatrix.CreateScale(w / across, h / down));
        }
        // 0 below `low`, 1 above `high`, eased between.
        static float Step(float low, float high, float x)
        {
            var k = Math.Clamp((x - low) / (high - low), 0, 1);
            return k * k * (3 - 2 * k);
        }
        // A circle of the picture, its rim a little soft.
        SKShader Circle(float radius, bool inside) =>
            SKShader.CreateRadialGradient(new SKPoint(w / 2, h / 2), Math.Max(1, radius + 2),
                inside ? [SKColors.White, SKColors.White, SKColors.Transparent] : [SKColors.Transparent, SKColors.Transparent, SKColors.White],
                [0, Math.Max(0, radius - 2) / Math.Max(1, radius + 2), 1], SKShaderTileMode.Clamp);
        var corner = (float)(Math.Sqrt(w * (double)w + h * (double)h) / 2);
        // How far a point of the frame is from its middle: 0 there, 1 in a corner.
        float Far(float x, float y) => MathF.Sqrt((x - 0.5f) * w * (x - 0.5f) * w + (y - 0.5f) * h * (y - 0.5f) * h) / corner;

        switch (transition)
        {
            case VideoShot.Fade or "fadewhite":
            {
                // Through black, or white: the old picture gone within the first fifth, the new one coming up from then on.
                var white = transition != VideoShot.Fade;
                var (old, fresh) = (Step(0.8f, 1, 1 - p) * (1 - p), (1 - Step(0.2f, 1, 1 - p)) * p);
                // ffmpeg mixes towards a black below video's black (0 where black is 16) and a white above its white, so what
                // shows of a picture is darker (or lighter) by that much of the mix again, and nothing shows at all for a tenth
                // of the transition longer: measured frame by frame, the new clip is still black three tenths in. The same here.
                var beyond = (1 - old - fresh) * (white ? 20 : -16) / 219f;
                using var pushed = SKColorFilter.CreateColorMatrix([1, 0, 0, 0, beyond, 0, 1, 0, 0, beyond, 0, 0, 1, 0, beyond, 0, 0, 0, 1, 0]);
                using var layer = new SKPaint { ColorFilter = pushed };
                canvas.SaveLayer(layer);
                canvas.Clear(white ? SKColors.White : SKColors.Black);
                Draw(before, old);
                Draw(now, fresh);
                canvas.Restore();
                break;
            }

            // Where the two pictures are alike the old one stays and fades; where they differ the new one is there at
            // once, more of it as it goes on.
            case "distance":
                if (before is null || now is null || !Distant(canvas, before, now, 1 - p, width, height))
                {
                    Draw(before);
                    Draw(now, p);
                }
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

            // A wipe with an edge as soft as the frame is wide: the new picture comes the way the wipe of that name does.
            case "smoothleft":
                Draw(before);
                Masked(now, Field((x, _) => Step(0, 1, x + 2 * p - 1)));
                break;
            case "smoothright":
                Draw(before);
                Masked(now, Field((x, _) => Step(0, 1, 2 * p - x)));
                break;
            case "smoothup":
                Draw(before);
                Masked(now, Field((_, y) => Step(0, 1, y + 2 * p - 1)));
                break;
            case "smoothdown":
                Draw(before);
                Masked(now, Field((_, y) => Step(0, 1, 2 * p - y)));
                break;

            // Towards a corner, softly: "diagtl" ends in the top left, so the new picture starts from the bottom right.
            case "diagtl":
                Draw(before);
                Masked(now, Field((x, y) => Step(0, 1, x * y + 2 * p - 1)));
                break;
            case "diagtr":
                Draw(before);
                Masked(now, Field((x, y) => Step(0, 1, (1 - x) * y + 2 * p - 1)));
                break;
            case "diagbl":
                Draw(before);
                Masked(now, Field((x, y) => Step(0, 1, x * (1 - y) + 2 * p - 1)));
                break;
            case "diagbr":
                Draw(before);
                Masked(now, Field((x, y) => Step(0, 1, (1 - x) * (1 - y) + 2 * p - 1)));
                break;

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

            // A circle opening on the new picture from the middle, or closing on the old one from the corners, its rim
            // as soft as the frame's half diagonal is long.
            case "circleopen":
                Draw(before);
                Masked(now, Field((x, y) => 1 - Step(0, 1, Far(x, y) + (0.5f - p) * 3)));
                break;
            case "circleclose":
                Draw(before);
                Masked(now, Field((x, y) => Step(0, 1, Far(x, y) + (p - 0.5f) * 3)));
                break;
            // The old picture shrinks into a circle on black, fast at first, and the new one grows out of one.
            case "circlecrop":
            {
                canvas.Clear(SKColors.Black);
                var k = Math.Abs(1 - p * 2);
                Masked(p < 0.5f ? before : now, Circle(corner * k * k * k, inside: true));
                break;
            }
            case "rectcrop":
            {
                canvas.Clear(SKColors.Black);
                var k = p < 0.5f ? 1 - p * 2 : p * 2 - 1;
                Clipped(p < 0.5f ? before : now, () => canvas.ClipRect(SKRect.Create(w / 2 * (1 - k), h / 2 * (1 - k), w * k, h * k)));
                break;
            }
            // A clock's hand going round from the top, the new picture behind it, its edge a soft one.
            case "radial":
                Draw(before);
                Masked(now, Field((x, y) => Step(0, 1, MathF.Atan2((x - 0.5f) * w, (y - 0.5f) * h) - (0.5f - p) * MathF.PI * 2.5f)));
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

            // The old picture comes at the viewer over the first half, until its middle fills the frame, and the new
            // one comes up through it over the second.
            case "zoomin":
            {
                Draw(now);
                var (part, solid) = (Math.Max(Step(0.5f, 1, 1 - p), 1f / Math.Max(width, height)), Step(0, 0.5f, 1 - p));
                if (before is not null && solid > 0.002f)
                {
                    paint.Color = SKColors.White.WithAlpha((byte)Math.Round(255 * solid));
                    canvas.DrawImage(before, SKRect.Create(w / 2 - w * part / 2, h / 2 - h * part / 2, w * part, h * part), frame, Smooth, paint);
                }
                break;
            }

            // Both in blocks that grow to the middle of it and shrink again, one fading into the other.
            case "pixelize":
            {
                var block = 1 + (int)Math.Round(Math.Min(width, height) / 20.0 * (1 - Math.Abs(p * 2 - 1)));
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
                    small.Canvas.DrawImage(before, into, Plain); // one pixel of each block, as ffmpeg takes it, not their average
                if (now is not null)
                {
                    using var over = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * p)) };
                    small.Canvas.DrawImage(now, into, Plain, over);
                }
                using var blocks = small.Snapshot();
                canvas.DrawImage(blocks, frame, Plain);
                break;
            }

            // One fading into the other, both smeared sideways most in the middle of it.
            case "hblur":
            {
                // As a smear half the frame wide at its most, and to the left by half as much: ffmpeg's adds up what is to
                // the right of each pixel, so what is seen moves left as it spreads.
                var spread = 1 + w / 2 * (1 - Math.Abs(p * 2 - 1));
                var sigma = spread / 3.46f;
                if (sigma < 0.5f)
                {
                    Draw(before);
                    Draw(now, p);
                    break;
                }
                using var blur = SKImageFilter.CreateBlur(sigma, 0.01f, SKShaderTileMode.Clamp, null, frame);
                using var smeared = new SKPaint { ImageFilter = blur };
                canvas.SaveLayer(smeared);
                void Smeared(SKImage? image, float alpha)
                {
                    if (image is null || alpha <= 0.002f)
                        return;
                    using var moved = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Plain, SKMatrix.CreateTranslation(-spread / 2, 0));
                    using var fill = new SKPaint { Shader = moved, Color = SKColors.White.WithAlpha((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1))) };
                    canvas.DrawRect(frame, fill);
                }
                Smeared(before, 1);
                Smeared(now, p);
                canvas.Restore();
                break;
            }

            // The new picture comes across in ten strips, as blinds turn: "hlslice" to the left (from the right edge),
            // "hrslice" to the right, "vuslice" up, "vdslice" down. Half the frame ahead of where it is whole, each strip
            // has only its near part, more of it the nearer the strip is.
            case "hlslice" or "hrslice" or "vuslice" or "vdslice":
            {
                const int strips = 10;
                var (across, mirrored) = (transition[0] == 'h', transition is "hrslice" or "vdslice");
                Draw(before);
                var size = (across ? w : h) / strips;
                for (var i = 0; i < strips; i++)
                {
                    // Counted the way ffmpeg counts them: from the left or the top, or mirrored from the other side.
                    // How much of the strip: where its own share (0 at its near edge, 1 at its far one) meets the rule there.
                    var part = 0.5f;
                    for (var turn = 0; turn < 8; turn++)
                        part = Step(-0.5f, 0, (i + part) / strips - (1 - p) * 1.5f);
                    var shown = size * part;
                    if (shown <= 0.01f)
                        continue;
                    var from = mirrored ? (across ? w : h) - i * size - shown : i * size;
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

    /// <summary>
    /// ffmpeg's "distance": each pixel is the old picture's where the two are no further apart than what is left of
    /// the transition (<paramref name="left"/>, 1 to 0; apart as their brightness and colour differ), else the new
    /// one's, and all of it fades to the new picture as it goes. False when a picture's pixels can't be read.
    /// </summary>
    private static bool Distant(SKCanvas canvas, SKImage before, SKImage now, float left, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var (a, b) = (new byte[width * height * 4], new byte[width * height * 4]);
        var (pa, pb) = (System.Runtime.InteropServices.GCHandle.Alloc(a, System.Runtime.InteropServices.GCHandleType.Pinned),
                        System.Runtime.InteropServices.GCHandle.Alloc(b, System.Runtime.InteropServices.GCHandleType.Pinned));
        try
        {
            if (!before.ReadPixels(info, pa.AddrOfPinnedObject(), width * 4, 0, 0) || !now.ReadPixels(info, pb.AddrOfPinnedObject(), width * 4, 0, 0))
                return false;
            var limit = left * left * 255f * 255f;
            Parallel.For(0, height, y =>
            {
                for (var i = y * width * 4; i < (y + 1) * width * 4; i += 4)
                {
                    float db = a[i] - b[i], dg = a[i + 1] - b[i + 1], dr = a[i + 2] - b[i + 2];
                    // Apart in brightness and in the two colour differences, as video holds a picture.
                    var (dy, du, dv) = (0.1826f * dr + 0.6142f * dg + 0.0620f * db, -0.1006f * dr - 0.3386f * dg + 0.4392f * db, 0.4392f * dr - 0.3989f * dg - 0.0403f * db);
                    if (dy * dy + du * du + dv * dv > limit)
                        continue; // the new picture, as b has it
                    b[i] = (byte)(a[i] * left + b[i] * (1 - left));
                    b[i + 1] = (byte)(a[i + 1] * left + b[i + 1] * (1 - left));
                    b[i + 2] = (byte)(a[i + 2] * left + b[i + 2] * (1 - left));
                }
            });
            using var mixed = SKImage.FromPixelCopy(info, b, width * 4);
            canvas.DrawImage(mixed, 0, 0, Plain);
            return true;
        }
        finally
        {
            pa.Free();
            pb.Free();
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
