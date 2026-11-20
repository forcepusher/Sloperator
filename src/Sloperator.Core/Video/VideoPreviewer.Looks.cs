using SkiaSharp;

namespace Sloperator.Core.Video;

// A shot's colours and its vignette, done as ffmpeg does them, so the viewer and a video joined here look as the
// one ffmpeg joins does. Each rule was read off ffmpeg 9.0.2 itself on a grey ramp (2026-11-19): at first the
// looks here were near guesses (a warm look as a tint over everything, where ffmpeg's warms only the dark parts),
// and a shot's contrast, its warm, cool and vintage looks and its vignette came out 8 to 40 of 255 apart.
public sealed partial class VideoPreviewer
{
    /// <summary>
    /// A shot's light and colour as one filter, as ffmpeg's eq has them: its contrast turns the brightness alone about
    /// the middle (the colours keep their strength), its brightness is added, its saturation spreads the colours from
    /// their own grey. In video's own range: 16 is black there and 235 white, which is where the odd numbers come from.
    /// </summary>
    internal static SKColorFilter Eq(VideoColor color)
    {
        // ffmpeg does this in whole numbers, and so that the same numbers come out, so does this: the contrast in 4096ths,
        // the brightness as a whole percent turned into a step of video's 0 to 255 (so -0.1 is 89 hundredths, not 90,
        // and darkens by 34 of 255 where +0.1 lightens by 28). A shot that changes neither keeps its brightness as it is.
        // (It also holds each setting as a float before it does, so -0.1 is a hair under: 89 hundredths, where the
        // same sum in doubles gives 90 and a picture 2 of 255 lighter.)
        var (brightness, s) = ((double)(float)(color.Brightness * 0.5), (int)((float)color.Saturation * 4096) / 4096f);
        var (c, offset) = (1f, 0f);
        if (color.Contrast != 1 || brightness != 0)
        {
            var contrast = (int)((double)(float)color.Contrast * 256 * 16);
            var step = (int)(100.0 * brightness + 100.0) * 511 / 200 - 128 - contrast / 32;
            c = contrast / 4096f;
            offset = (16 * c + step - 16) / 219f;
        }
        const float wr = 0.2126f, wg = 0.7152f, wb = 0.0722f;
        // Its colour planes go through the same whole numbers, which leaves both a step under their middle (127 for 128)
        // whenever the saturation is changed: a trace less red and blue, a trace more green.
        var (tintR, tintG, tintB) = (0f, 0f, 0f);
        if (color.Saturation != 1)
        {
            var under = 255 - 128 - (int)((float)color.Saturation * 4096) / 32 - 128 * (1 - s) - 0.5f;
            (tintR, tintG, tintB) = (1.5748f * under / 224, -(0.1873f + 0.4681f) * under / 224, 1.8556f * under / 224);
        }
        var grey = c - s;
        return SKColorFilter.CreateColorMatrix(
        [
            s + grey * wr, grey * wg, grey * wb, 0, offset + tintR,
            grey * wr, s + grey * wg, grey * wb, 0, offset + tintG,
            grey * wr, grey * wg, s + grey * wb, 0, offset + tintB,
            0, 0, 0, 1, 0,
        ]);
    }

    /// <summary>A shot's colours as one filter: its light and colour, then its look where the look is the same for every pixel (black and white, sepia, vintage).</summary>
    internal static SKColorFilter Colours(VideoColor color)
    {
        var eq = color.Brightness != 0 || color.Contrast != 1 || color.Saturation != 1 ? Eq(color) : null;
        var look = color.Look switch
        {
            "bw" => SKColorFilter.CreateColorMatrix([0.2126f, 0.7152f, 0.0722f, 0, 0, 0.2126f, 0.7152f, 0.0722f, 0, 0, 0.2126f, 0.7152f, 0.0722f, 0, 0, 0, 0, 0, 1, 0]),
            "sepia" => SKColorFilter.CreateColorMatrix([.393f, .769f, .189f, 0, 0, .349f, .686f, .168f, 0, 0, .272f, .534f, .131f, 0, 0, 0, 0, 0, 1, 0]),
            "vintage" => SKColorFilter.CreateTable(Curve([(0, 0), (1, 1)]), VintageRed.Value, VintageGreen.Value, VintageBlue.Value),
            _ => null,
        };
        if (eq is null || look is null)
            return eq ?? look ?? SKColorFilter.CreateColorMatrix([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0]);
        using (eq)
        using (look)
            return SKColorFilter.CreateCompose(look, eq);
    }

    // ffmpeg's "vintage" curves: through these points, smoothly (a natural spline), one curve a colour.
    private static readonly Lazy<byte[]> VintageRed = new(() => Curve([(0, 0.11), (0.42, 0.51), (1, 0.95)]));
    private static readonly Lazy<byte[]> VintageGreen = new(() => Curve([(0, 0), (0.50, 0.48), (1, 1)]));
    private static readonly Lazy<byte[]> VintageBlue = new(() => Curve([(0, 0.22), (0.49, 0.44), (1, 0.8)]));

    /// <summary>A curve through points (each from 0 to 1) as a table of 256 values: a natural cubic spline, as ffmpeg's curves filter draws it.</summary>
    internal static byte[] Curve((double X, double Y)[] points)
    {
        var n = points.Length;
        // The spline's second derivatives: none at either end, and the bend shared smoothly at each point between.
        var second = new double[n];
        var (lower, upper) = (new double[n], new double[n]);
        for (var i = 1; i < n - 1; i++)
        {
            var (before, after) = (points[i].X - points[i - 1].X, points[i + 1].X - points[i].X);
            var share = before / (before + after);
            var pivot = share * lower[i - 1] + 2;
            lower[i] = (share - 1) / pivot;
            upper[i] = (6 * ((points[i + 1].Y - points[i].Y) / after - (points[i].Y - points[i - 1].Y) / before) / (before + after) - share * upper[i - 1]) / pivot;
        }
        for (var i = n - 2; i > 0; i--)
            second[i] = lower[i] * second[i + 1] + upper[i];
        var table = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            var x = v / 255.0;
            var k = 0;
            while (k < n - 2 && x > points[k + 1].X)
                k++;
            var width = points[k + 1].X - points[k].X;
            var (a, b) = ((points[k + 1].X - x) / width, (x - points[k].X) / width);
            var y = a * points[k].Y + b * points[k + 1].Y + ((a * a * a - a) * second[k] + (b * b * b - b) * second[k + 1]) * width * width / 6;
            table[v] = (byte)Math.Round(Math.Clamp(y, 0, 1) * 255);
        }
        return table;
    }

    /// <summary>What a warm or a cool look adds to red, green and blue in the dark parts, and in those just above them (ffmpeg's colorbalance: shadows and midtones).</summary>
    private static ((float R, float G, float B) Shadows, (float R, float G, float B) Mid)? Balance(string? look) => look switch
    {
        "warm" => ((0.08f, 0.02f, -0.08f), (0.06f, 0, -0.06f)),
        "cool" => ((-0.08f, 0, 0.08f), (-0.05f, 0, 0.06f)),
        _ => null,
    };

    /// <summary>
    /// A picture as its shot colours it: its light and colour and its look, before it is fitted into the frame, as ffmpeg
    /// has them in its chain (so bars beside a picture stay black, and a blurred copy behind it is coloured with it).
    /// </summary>
    private static SKImage Coloured(SKImage seen, VideoColor color)
    {
        var info = new SKImageInfo(seen.Width, seen.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        using (var filter = Colours(color))
        using (var paint = new SKPaint { ColorFilter = filter })
            surface.Canvas.DrawImage(seen, 0, 0, Plain, paint);
        if (Balance(color.Look) is not { } balance)
            return surface.Snapshot();
        // Warm and cool depend on how light each pixel is, which no one table or matrix does: pixel by pixel.
        var pixels = new byte[info.Width * info.Height * 4];
        using (var pixmap = surface.PeekPixels())
            for (var y = 0; y < info.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixmap.GetPixels() + y * pixmap.RowBytes, pixels, y * info.Width * 4, info.Width * 4);
        var (shadows, mid) = balance;
        Parallel.For(0, info.Height, y =>
        {
            for (var i = y * info.Width * 4; i < (y + 1) * info.Width * 4; i += 4)
            {
                float b = pixels[i] / 255f, g = pixels[i + 1] / 255f, r = pixels[i + 2] / 255f;
                // ffmpeg's measure of how light it is: its largest colour and its smallest, added (0 to 2), so its
                // "shadows" are the darkest eighth or so and its "midtones" what lies just above them.
                var light = Math.Max(r, Math.Max(g, b)) + Math.Min(r, Math.Min(g, b));
                var dark = Math.Clamp((0.333f - light) * 4 + 0.5f, 0, 1) * 0.7f;
                var middle = Math.Clamp((light - 0.333f) * 4 + 0.5f, 0, 1) * Math.Clamp((1 - light - 0.333f) * 4 + 0.5f, 0, 1) * 0.7f;
                if (dark <= 0 && middle <= 0)
                    continue;
                pixels[i] = (byte)Math.Round(Math.Clamp(b + shadows.B * dark + mid.B * middle, 0, 1) * 255);
                pixels[i + 1] = (byte)Math.Round(Math.Clamp(g + shadows.G * dark + mid.G * middle, 0, 1) * 255);
                pixels[i + 2] = (byte)Math.Round(Math.Clamp(r + shadows.R * dark + mid.R * middle, 0, 1) * 255);
            }
        });
        return SKImage.FromPixelCopy(info, pixels, info.Width * 4);
    }

    /// <summary>
    /// How much of something shows at each point of a frame (x and y from 0 to 1), by a rule, as a mask (or, solid, as a
    /// grey that light everywhere): worked out on
    /// a coarse grid and smoothed over the frame, which is all a soft edge needs.
    /// </summary>
    private static SKShader Mask(float width, float height, Func<float, float, float> shows, bool solid = false)
    {
        const int across = 96, down = 54;
        var grid = new byte[across * down * 4];
        for (var y = 0; y < down; y++)
            for (var x = 0; x < across; x++)
            {
                var v = (byte)Math.Round(255 * Math.Clamp(shows((x + 0.5f) / across, (y + 0.5f) / down), 0, 1));
                var i = (y * across + x) * 4;
                (grid[i], grid[i + 1], grid[i + 2], grid[i + 3]) = (v, v, v, solid ? (byte)255 : v);
            }
        using var bitmap = new SKBitmap(new SKImageInfo(across, down, SKColorType.Bgra8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(grid, 0, bitmap.GetPixels(), grid.Length);
        using var image = SKImage.FromBitmap(bitmap);
        var matrix = SKMatrix.CreateScale(width / across, height / down);
        return image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Smooth, matrix);
    }

    /// <summary>
    /// Dark towards the corners, as ffmpeg's vignette at its usual angle: each pixel by the fourth power of the cosine
    /// of how far it is from the middle (0.34 in a corner). ffmpeg scales video's own numbers, where black is 16, so
    /// what it darkens also falls below black by a share of that: taken off as well.
    /// </summary>
    private static void Vignette(SKCanvas canvas, int width, int height)
    {
        var corner = MathF.Sqrt(width * (float)width + height * (float)height) / 2;
        float Dark(float x, float y)
        {
            var far = MathF.Sqrt((x - 0.5f) * width * (x - 0.5f) * width + (y - 0.5f) * height * (y - 0.5f) * height) / corner;
            var cos = MathF.Cos(MathF.PI / 4.5f * far);
            return 1 - cos * cos * cos * cos;
        }
        var frame = SKRect.Create(width, height);
        // Black laid over by how much is taken away…
        using (var mask = Mask(width, height, Dark))
        using (var black = new SKPaint { Shader = mask, ColorFilter = SKColorFilter.CreateBlendMode(SKColors.Black, SKBlendMode.SrcIn) })
            canvas.DrawRect(frame, black);
        // …and the same share of video's black level taken off what is left.
        using (var mask = Mask(width, height, (x, y) => Dark(x, y) * 16 / 219f, solid: true))
        using (var below = new SKPaint { Shader = mask, BlendMode = SKBlendMode.Difference })
            canvas.DrawRect(frame, below);
    }

    /// <summary>
    /// A picture in blocks, so many across and down, as ffmpeg makes them (scaled down by area, then up without
    /// smoothing, in video's own form): each block has the average brightness of what it covers, and each two by two
    /// blocks share one colour, since video keeps its colour at half the size. With one colour a block (as a
    /// picture editor would have it) the blocks came out 5 of 255 from ffmpeg's.
    /// </summary>
    private static SKImage Blocks(SKImage picture, int across, int down)
    {
        var (width, height) = (picture.Width, picture.Height);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixels = new byte[width * height * 4];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            picture.ReadPixels(info, pinned.AddrOfPinnedObject(), width * 4, 0, 0);
        }
        finally
        {
            pinned.Free();
        }
        // The average red, green and blue of every cell of a grid over the picture.
        (float[] Average, int Across, int Down) Grid(int cellsAcross, int cellsDown)
        {
            var sums = new long[cellsAcross * cellsDown * 3];
            var counts = new int[cellsAcross * cellsDown];
            for (var y = 0; y < height; y++)
            {
                var row = Math.Min(cellsDown - 1, y * cellsDown / height);
                for (var x = 0; x < width; x++)
                {
                    var (cell, i) = (row * cellsAcross + Math.Min(cellsAcross - 1, x * cellsAcross / width), (y * width + x) * 4);
                    sums[cell * 3] += pixels[i];
                    sums[cell * 3 + 1] += pixels[i + 1];
                    sums[cell * 3 + 2] += pixels[i + 2];
                    counts[cell]++;
                }
            }
            var average = new float[sums.Length];
            for (var k = 0; k < sums.Length; k++)
                average[k] = sums[k] / (float)Math.Max(1, counts[k / 3]);
            return (average, cellsAcross, cellsDown);
        }
        static float Light(float[] grid, int cell) => 0.0722f * grid[cell * 3] + 0.7152f * grid[cell * 3 + 1] + 0.2126f * grid[cell * 3 + 2];
        var light = Grid(across, down);
        var colour = Grid((across + 1) / 2, (down + 1) / 2);
        var blocks = new byte[pixels.Length];
        Parallel.For(0, height, y =>
        {
            var (lightRow, colourRow) = (Math.Min(light.Down - 1, y * light.Down / height), Math.Min(colour.Down - 1, y * colour.Down / height));
            for (var x = 0; x < width; x++)
            {
                var lightCell = lightRow * light.Across + Math.Min(light.Across - 1, x * light.Across / width);
                var colourCell = colourRow * colour.Across + Math.Min(colour.Across - 1, x * colour.Across / width);
                // The colour of the larger block, as light as the smaller one.
                var lift = Light(light.Average, lightCell) - Light(colour.Average, colourCell);
                var i = (y * width + x) * 4;
                blocks[i] = (byte)Math.Clamp(MathF.Round(colour.Average[colourCell * 3] + lift), 0, 255);
                blocks[i + 1] = (byte)Math.Clamp(MathF.Round(colour.Average[colourCell * 3 + 1] + lift), 0, 255);
                blocks[i + 2] = (byte)Math.Clamp(MathF.Round(colour.Average[colourCell * 3 + 2] + lift), 0, 255);
                blocks[i + 3] = 255;
            }
        });
        return SKImage.FromPixelCopy(info, blocks, width * 4);
    }

    /// <summary>
    /// A picture blurred as ffmpeg's gblur blurs it at one step, which is not quite a Gaussian blur: each row, then each
    /// column, is run through once forwards and once backwards, every pixel taking a share of the one before it, so a
    /// point spreads as a peak with long tails rather than as a bell. And ffmpeg blurs video's colour planes, which are
    /// half the size, by the same number of their own pixels: the colours spread twice as far as the brightness. With a
    /// true Gaussian for both, the blurred copy behind a fitted picture was 4.5 of 255 from ffmpeg's.
    /// </summary>
    private static SKImage Gblur(SKImage picture, float sigma)
    {
        var (width, height) = (picture.Width, picture.Height);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixels = new byte[width * height * 4];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            picture.ReadPixels(info, pinned.AddrOfPinnedObject(), width * 4, 0, 0);
        }
        finally
        {
            pinned.Free();
        }
        // Each colour as its own plane, twice: one for the brightness, one blurred twice as wide for the colour.
        var near = new float[3][];
        var wide = new float[3][];
        for (var c = 0; c < 3; c++)
        {
            (near[c], wide[c]) = (new float[width * height], new float[width * height]);
            for (var i = 0; i < width * height; i++)
                near[c][i] = wide[c][i] = pixels[i * 4 + c];
        }
        Parallel.For(0, 6, k => Spread(k < 3 ? near[k] : wide[k - 3], width, height, k < 3 ? sigma : sigma * 2));
        Parallel.For(0, height, y =>
        {
            for (var i = y * width; i < (y + 1) * width; i++)
            {
                // The wider blur's colour, as light as the nearer blur.
                var lift = 0.0722f * (near[0][i] - wide[0][i]) + 0.7152f * (near[1][i] - wide[1][i]) + 0.2126f * (near[2][i] - wide[2][i]);
                pixels[i * 4] = (byte)Math.Clamp(MathF.Round(wide[0][i] + lift), 0, 255);
                pixels[i * 4 + 1] = (byte)Math.Clamp(MathF.Round(wide[1][i] + lift), 0, 255);
                pixels[i * 4 + 2] = (byte)Math.Clamp(MathF.Round(wide[2][i] + lift), 0, 255);
                pixels[i * 4 + 3] = 255;
            }
        });
        return SKImage.FromPixelCopy(info, pixels, width * 4);
    }

    /// <summary>One plane blurred in place, ffmpeg's way: along its rows, then down its columns, each forwards and back.</summary>
    private static void Spread(float[] plane, int width, int height, float sigma)
    {
        var lambda = sigma * (double)sigma / 2;
        var share = (1 + 2 * lambda - Math.Sqrt(1 + 4 * lambda)) / (2 * lambda);
        var (nu, edge, scale) = ((float)share, (float)(1 / (1 - share)), (float)(share / lambda * (share / lambda)));
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            plane[row] *= edge;
            for (var x = 1; x < width; x++)
                plane[row + x] += nu * plane[row + x - 1];
            plane[row + width - 1] *= edge;
            for (var x = width - 1; x > 0; x--)
                plane[row + x - 1] += nu * plane[row + x];
        }
        for (var x = 0; x < width; x++)
            plane[x] *= edge;
        for (var y = 1; y < height; y++)
            for (int x = 0, at = y * width; x < width; x++, at++)
                plane[at] += nu * plane[at - width];
        for (int x = 0, at = (height - 1) * width; x < width; x++, at++)
            plane[at] *= edge;
        for (var y = height - 1; y > 0; y--)
            for (int x = 0, at = y * width; x < width; x++, at++)
                plane[at - width] += nu * plane[at];
        for (var i = 0; i < plane.Length; i++)
            plane[i] *= scale;
    }
}
