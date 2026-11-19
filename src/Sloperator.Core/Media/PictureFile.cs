using Sloperator.Core.Tools;
using SkiaSharp;

namespace Sloperator.Core.Media;

/// <summary>
/// Picture files opened for Skia through .NET, never by their path. Skia opens a path with the C
/// runtime's fopen, whose handle every program started meanwhile inherits: a command the agent
/// started while a picture was read then held it open until it ended, and the picture couldn't be
/// deleted or replaced (the read_file picture test failed on its temporary folder that way).
/// </summary>
public static class PictureFile
{
    /// <summary>The file's codec; null when it can't be read, or isn't a picture Skia reads.</summary>
    public static SKCodec? Codec(string path)
    {
        try
        {
            // Read whole (the codec keeps its own reference to the bytes), so no handle stays open.
            using var stream = TextFiles.OpenRead(path);
            using var data = SKData.Create(stream);
            return data is null ? null : SKCodec.Create(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The picture in the file as it is seen, an SVG drawn at its own size; null when it can't be read. A photo's
    /// orientation, which a camera writes in the file rather than turn the pixels (a phone held upright stores its
    /// photos on their side), is applied: read without it, such photos lay sideways in every panel, in what the
    /// model was shown and in the videos made of them (2026-11-19).
    /// </summary>
    public static SKBitmap? Decode(string path)
    {
        if (SvgFile.Handles(path))
            return SvgFile.Open(path)?.Render(1);
        using var codec = Codec(path);
        return codec is null ? null : Decode(codec);
    }

    /// <summary>The codec's picture as it is seen: its orientation applied.</summary>
    public static SKBitmap? Decode(SKCodec codec)
    {
        var stored = SKBitmap.Decode(codec);
        if (stored is null || IsUpright(codec.EncodedOrigin))
            return stored;
        using (stored)
            return Upright(stored, codec.EncodedOrigin);
    }

    /// <summary>The pixels as the file stores them, whatever orientation it says: a texture is laid on a model as stored.</summary>
    public static SKBitmap? DecodeStored(string path)
    {
        using var codec = Codec(path);
        return codec is null ? null : SKBitmap.Decode(codec);
    }

    /// <summary>Whether any of the picture is see-through (a pixel not fully there): shown, it wants something behind it to be told from.</summary>
    public static bool HasClear(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque || bitmap.ColorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888))
            return false;
        var pixels = bitmap.GetPixelSpan();
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] < 255)
                return true;
        return false;
    }

    /// <summary>Whether a file's pixels are stored as the picture is seen (as nearly all but a camera's are).</summary>
    public static bool IsUpright(SKEncodedOrigin origin) => origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default;

    /// <summary>Whether a picture is stored on its side: seen, its width is its stored height.</summary>
    public static bool IsSideways(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>The picture's size as it is seen.</summary>
    public static (int Width, int Height) SizeOf(SKCodec codec) =>
        IsSideways(codec.EncodedOrigin) ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);

    /// <summary>Stored pixels as the picture is seen, for the orientation the file says (EXIF's 2 to 8): whole pixels moved, none changed.</summary>
    public static SKBitmap Upright(SKBitmap stored, SKEncodedOrigin origin)
    {
        var (w, h) = IsSideways(origin) ? (stored.Height, stored.Width) : (stored.Width, stored.Height);
        // In the stored pixels' own format when a canvas draws in it, else the screen's.
        var info = stored.ColorType is SKColorType.Bgra8888 or SKColorType.Rgba8888 ? stored.Info.WithSize(w, h)
            : new SKImageInfo(w, h, SKImageInfo.PlatformColorType, stored.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        var upright = new SKBitmap(info);
        using var canvas = new SKCanvas(upright);
        canvas.Clear(SKColors.Transparent);
        // Where each stored point is seen: x' = a·x + b·y + c, y' = d·x + e·y + f.
        canvas.SetMatrix(origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // mirrored
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // upside down
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // flipped
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // its sides swapped
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),     // a quarter turn to the right
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1), // its sides swapped the other way
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),   // a quarter turn to the left
            _ => SKMatrix.Identity,
        });
        using var image = SKImage.FromBitmap(stored);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        return upright;
    }
}
