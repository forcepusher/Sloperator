using Avalonia.Media.Imaging;
using Sloperator.Core.Media;
using SkiaSharp;

namespace Sloperator.App.ViewModels;

/// <summary>
/// A picture file decoded for the window as it is seen. A camera writes which way up a photo is in the file rather
/// than turn its pixels (a phone held upright stores its photos on their side), and Avalonia's own decoding passes
/// that by: such photos lay sideways in the chat, the File inspector and the editors (2026-11-19). Any other picture
/// is decoded by Avalonia as before.
/// </summary>
internal static class UprightPicture
{
    /// <summary>The whole picture.</summary>
    public static Bitmap Load(Stream stream) => Decode(stream, null, null, BitmapInterpolationMode.HighQuality);

    /// <summary>The whole picture in a file.</summary>
    public static Bitmap Load(string path)
    {
        using var stream = MediaFolders.ReadShared(path);
        return Load(stream);
    }

    /// <summary>The picture <paramref name="width"/> pixels wide, its shape kept, as <see cref="Bitmap.DecodeToWidth"/> gives it.</summary>
    public static Bitmap ToWidth(Stream stream, int width, BitmapInterpolationMode mode = BitmapInterpolationMode.HighQuality) => Decode(stream, width, null, mode);

    /// <summary>The picture <paramref name="height"/> pixels high, its shape kept, as <see cref="Bitmap.DecodeToHeight"/> gives it.</summary>
    public static Bitmap ToHeight(Stream stream, int height, BitmapInterpolationMode mode = BitmapInterpolationMode.HighQuality) => Decode(stream, null, height, mode);

    private static Bitmap Decode(Stream stream, int? width, int? height, BitmapInterpolationMode mode)
    {
        // The whole file: its header says which way up it is, before it is decoded one way or the other.
        byte[] bytes;
        if (stream is MemoryStream memory && memory.Position == 0)
            bytes = memory.ToArray();
        else
        {
            using var all = new MemoryStream();
            stream.CopyTo(all);
            bytes = all.ToArray();
        }
        if (Turned(bytes) is { } upright)
            using (upright)
            {
                var scale = width is { } w ? w / (double)upright.Width : height is { } h ? h / (double)upright.Height : 1;
                if (Math.Abs(scale - 1) < 1e-6)
                    return FilePreview.ToBitmap(upright);
                var info = upright.Info.WithSize(Math.Max(1, (int)Math.Round(upright.Width * scale)), Math.Max(1, (int)Math.Round(upright.Height * scale)));
                using var sized = upright.Resize(info, Sampling.High(upright.Width, info.Width)) ?? throw new InvalidOperationException("the picture couldn't be sized");
                return FilePreview.ToBitmap(sized);
            }
        var again = new MemoryStream(bytes);
        return width is { } toWidth ? Bitmap.DecodeToWidth(again, toWidth, mode)
            : height is { } toHeight ? Bitmap.DecodeToHeight(again, toHeight, mode)
            : new Bitmap(again);
    }

    /// <summary>The picture turned as its file says to show it; null for one stored as it is seen, or that Skia can't read.</summary>
    private static SKBitmap? Turned(byte[] bytes)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            return codec is null || PictureFile.IsUpright(codec.EncodedOrigin) ? null : PictureFile.Decode(codec);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
