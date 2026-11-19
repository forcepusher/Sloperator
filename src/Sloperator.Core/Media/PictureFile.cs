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
    /// <summary>
    /// The OS's own picture decoders, for the kinds Skia has none for (TIFF, a game's DDS textures, and on a PC with
    /// the extensions for them a phone's HEIC, AVIF, a camera's raw files): a file's bytes to BGRA, its colours scaled
    /// by its clearness. Set once at start (<see cref="MediaDecoders.Create"/>); none where the OS has none.
    /// </summary>
    public static Func<byte[], Platform.RawImage?>? Os { get; set; }

    /// <summary>
    /// The kinds of picture file read here, by the end of their names: Skia's own, Targa (<see cref="TgaFile"/>), and
    /// those the OS decodes. Whether one of the last is read depends on the PC; a file that isn't says so when opened.
    /// </summary>
    public static readonly string[] Kinds =
        [".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".ico", ".tga", ".tif", ".tiff", ".dds", ".heic", ".heif", ".avif", ".jxr", ".wdp"];

    /// <summary>The file's codec; null when it can't be read, or isn't a picture read here. A kind Skia doesn't read is handed to it as a PNG of the picture.</summary>
    public static SKCodec? Codec(string path)
    {
        try
        {
            // Read whole (the codec keeps its own reference to the bytes), so no handle stays open.
            using var stream = TextFiles.OpenRead(path);
            using var data = SKData.Create(stream);
            if (data is null)
                return null;
            if (SKCodec.Create(data) is { } codec)
                return codec;
            if (AsPng(path, data.ToArray()) is not { } png)
                return null;
            using var again = SKData.CreateCopy(png);
            return SKCodec.Create(again);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A picture file's bytes as something Skia reads: the bytes themselves for a kind it knows, else a PNG of the
    /// picture as another reader here gives it; null when nothing reads them. For what decodes a picture itself from
    /// bytes (the window's own bitmaps).
    /// </summary>
    public static byte[]? Readable(byte[] bytes)
    {
        using (var data = SKData.CreateCopy(bytes))
        using (var codec = SKCodec.Create(data))
            if (codec is not null)
                return bytes;
        using var other = Other(bytes);
        return other is null ? null : Png(other);
    }

    private static readonly Lock Kept = new();
    private static readonly List<(string Path, long Size, DateTime Written, byte[] Png)> Pngs = [];

    /// <summary>The PNG of a file Skia doesn't read, kept for the few such files last asked for: its size is asked, then its pixels, then a small copy of it.</summary>
    private static byte[]? AsPng(string path, byte[] bytes)
    {
        var file = new FileInfo(path);
        lock (Kept)
            if (Pngs.FirstOrDefault(k => string.Equals(k.Path, file.FullName, StringComparison.OrdinalIgnoreCase) && k.Size == file.Length && k.Written == file.LastWriteTimeUtc) is { Png: { } known })
                return known;
        using var other = Other(bytes);
        if (other is null)
            return null;
        var png = Png(other);
        lock (Kept)
        {
            Pngs.RemoveAll(k => string.Equals(k.Path, file.FullName, StringComparison.OrdinalIgnoreCase));
            Pngs.Add((file.FullName, file.Length, file.LastWriteTimeUtc, png));
            // A few, and not more than 96 MB of them.
            while (Pngs.Count > 6 || (Pngs.Count > 1 && Pngs.Sum(k => (long)k.Png.Length) > 96 * 1024 * 1024))
                Pngs.RemoveAt(0);
        }
        return png;
    }

    /// <summary>A picture as a PNG, packed lightly: it is read again at once, not kept.</summary>
    private static byte[] Png(SKBitmap picture)
    {
        using var pixels = picture.PeekPixels();
        using var data = pixels.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 1)) ?? throw new NotSupportedException("the picture couldn't be written as a PNG");
        return data.ToArray();
    }

    /// <summary>A picture of a kind Skia doesn't read: by the OS's decoders, else as a Targa file; null when neither reads it.</summary>
    private static SKBitmap? Other(byte[] bytes)
    {
        Platform.RawImage? raw = null;
        try
        {
            raw = Os?.Invoke(bytes);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
        }
        if (raw is null)
            return TgaFile.Decode(bytes);
        // Opaque when no pixel of it is clear, so it is written and shown as a picture without clearness.
        var solid = true;
        for (var i = 3; i < raw.Bgra.Length && solid; i += 4)
            solid = raw.Bgra[i] == 255;
        var bitmap = new SKBitmap(new SKImageInfo(raw.Width, raw.Height, SKColorType.Bgra8888, solid ? SKAlphaType.Opaque : SKAlphaType.Premul));
        raw.Bgra.AsSpan(0, Math.Min(raw.Bgra.Length, raw.Width * raw.Height * 4)).CopyTo(bitmap.GetPixelSpan());
        return bitmap;
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
        try
        {
            // Read whole, so no handle stays open (see the class's note); a kind Skia doesn't read goes to the other readers.
            byte[] bytes;
            using (var stream = TextFiles.OpenRead(path))
            using (var all = new MemoryStream())
            {
                stream.CopyTo(all);
                bytes = all.ToArray();
            }
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            return codec is null ? Other(bytes) : Decode(codec);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The codec's picture as it is seen: its orientation applied.</summary>
    public static SKBitmap? Decode(SKCodec codec)
    {
        var stored = SKBitmap.Decode(codec);
        if (stored is not null)
            stored = Usual(stored);
        if (stored is null || IsUpright(codec.EncodedOrigin))
            return stored;
        using (stored)
            return Upright(stored, codec.EncodedOrigin);
    }

    /// <summary>Whether a picture names a colour space other than the usual one (sRGB): a phone's Display P3, a camera's Adobe RGB.</summary>
    public static bool HasWideColours(SKCodec codec) => codec.Info.ColorSpace is { IsSrgb: false };

    /// <summary>
    /// A picture's colours in the usual colour space (sRGB). A phone's photo names a wider one (Display P3), a
    /// camera's often Adobe RGB, and its numbers mean stronger colours than the same numbers do in sRGB: read as
    /// they stand, such a photo was duller here than it is (its reds and greens some 20 of 255 short), in every
    /// panel, in what the model was shown and in every copy an edit saved, which no longer named the space either.
    /// </summary>
    public static SKBitmap Usual(SKBitmap stored)
    {
        if (stored.ColorSpace is not { IsSrgb: false })
            return stored;
        var info = stored.Info.WithColorSpace(SKColorSpace.CreateSrgb());
        var usual = new SKBitmap(info);
        using var pixels = stored.PeekPixels();
        if (pixels is null || !pixels.ReadPixels(info, usual.GetPixels(), info.RowBytes))
        {
            usual.Dispose();
            return stored;
        }
        stored.Dispose();
        return usual;
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
