using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Sloperator.Core.Media;

/// <summary>An exact edit of a picture: what the Image editor's Edit buttons and the edit_picture tool do.</summary>
public enum PictureEditKind { Crop, Resize, Rotate, Flip, Colour, Convert }

/// <summary>An edit asked for: its kind, and what that kind takes.</summary>
public sealed record PictureEditRequest(PictureEditKind Kind)
{
    /// <summary>Crop: the box kept, in pixels from the top left. Resize: the new width and height (one alone keeps the picture's shape).</summary>
    public int? X { get; init; }

    public int? Y { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>Crop: the box kept in parts of the picture (the Image editor's marked box), instead of pixels.</summary>
    public Documents.DocumentArea? Area { get; init; }

    /// <summary>Rotate: degrees clockwise; a quarter turn or any angle (the picture grows to hold its corners).</summary>
    public double Degrees { get; init; }

    /// <summary>Flip: upside down; else mirrored, left and right swapped.</summary>
    public bool Vertical { get; init; }

    /// <summary>Colour: -1 (black) to 1 (white), 0 as it is.</summary>
    public double Brightness { get; init; }

    /// <summary>Colour: 0 (grey) to 2 and more, 1 as it is.</summary>
    public double Contrast { get; init; } = 1;

    /// <summary>Colour: 0 (black and white) to 3, 1 as it is.</summary>
    public double Saturation { get; init; } = 1;

    /// <summary>The kind of file saved (png, jpg, webp); null: the picture's own.</summary>
    public string? Format { get; init; }
}

/// <summary>An edit saved: its file, what it is called, and the picture's size before and after.</summary>
public sealed record PictureEdited(string Path, string Label, int Width, int Height, int WasWidth, int WasHeight);

/// <summary>An edit that can't be made as asked, said so the user or the model can fix it.</summary>
public sealed class PictureEditException(string message) : Exception(message);

/// <summary>
/// Exact edits of a picture file, made with Skia and saved beside it as a new file, the picture itself left as it
/// was: a crop, another size, a turn, a mirror, its light and colour, another kind of file. In a picture's folder
/// of the Image editor the new file is its next version. What the picture says of how it was made (ComfyUI's
/// workflow in a PNG) goes with it; the scene it was drawn from doesn't, as the edit is no longer what the scene draws.
/// </summary>
public static class PictureEdit
{
    /// <summary>The longest side a picture is made.</summary>
    public const int MaxSide = 16384;

    /// <param name="picturesRoot">The Image editor's folder: a version there is named after the edit alone.</param>
    public static PictureEdited Save(string source, PictureEditRequest request, string? picturesRoot)
    {
        if (!File.Exists(source))
            throw new PictureEditException($"{source} isn't there.");
        byte[] bytes;
        using (var read = MediaFolders.ReadShared(source))
            bytes = read.ToArray();
        using var picture = Decode(bytes) ?? throw new PictureEditException($"{Path.GetFileName(source)} can't be read as a picture (PNG, JPEG, WebP, BMP and GIF are).");
        var format = FormatOf(request.Format ?? Path.GetExtension(source)) ?? (request.Format is { } asked
            ? throw new PictureEditException($"A picture is saved as png, jpg or webp, not \"{asked}\".")
            : (Extension: ".png", Kind: SKEncodedImageFormat.Png));
        var (edited, label) = Apply(picture, request, format.Kind == SKEncodedImageFormat.Jpeg);
        using (edited)
        {
            if (request.Kind == PictureEditKind.Convert)
                label = $"As {format.Extension[1..].ToUpperInvariant()}";
            var target = Target(source, label, format.Extension, picturesRoot);
            using var image = SKImage.FromBitmap(edited);
            using var data = image.Encode(format.Kind, format.Kind == SKEncodedImageFormat.Png ? 100 : 92)
                             ?? throw new PictureEditException("The edited picture couldn't be written.");
            var encoded = data.ToArray();
            if (format.Kind == SKEncodedImageFormat.Png)
                encoded = WithTexts(encoded, bytes);
            // Beside it first, then in its place: a folder watched never sees it half written.
            var part = target + ".part";
            File.WriteAllBytes(part, encoded);
            File.Move(part, target, overwrite: true);
            return new PictureEdited(target, label, edited.Width, edited.Height, picture.Width, picture.Height);
        }
    }

    /// <summary>The picture as it is seen (<see cref="PictureFile.Upright"/>), its colours as stored, not multiplied by how clear they are.</summary>
    internal static SKBitmap? Decode(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null)
            return null;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            return null;
        }
        if (PictureFile.IsUpright(codec.EncodedOrigin))
            return bitmap;
        using (bitmap)
            return PictureFile.Upright(bitmap, codec.EncodedOrigin);
    }

    /// <summary>The edit made on a picture, and what it is called ("Crop 512x512", "Turn 90°").</summary>
    /// <param name="opaque">Saved as a kind of file with no clear pixels (a JPEG): what a turn leaves empty is white.</param>
    internal static (SKBitmap Picture, string Label) Apply(SKBitmap picture, PictureEditRequest request, bool opaque)
    {
        switch (request.Kind)
        {
            case PictureEditKind.Crop:
            {
                if (request.Area is { } area)
                {
                    var (ax, ay, aw, ah) = area.Pixels(picture.Width, picture.Height);
                    request = request with { X = ax, Y = ay, Width = aw, Height = ah };
                }
                var (x, y) = (Math.Clamp(request.X ?? 0, 0, picture.Width - 1), Math.Clamp(request.Y ?? 0, 0, picture.Height - 1));
                var (w, h) = (Math.Min(request.Width ?? picture.Width - x, picture.Width - x), Math.Min(request.Height ?? picture.Height - y, picture.Height - y));
                if (w < 1 || h < 1)
                    throw new PictureEditException($"The box to keep is empty: the picture is {picture.Width}x{picture.Height} pixels, and x, y, width and height are a box inside it.");
                if ((x, y, w, h) == (0, 0, picture.Width, picture.Height))
                    throw new PictureEditException($"That box is the whole picture ({picture.Width}x{picture.Height} pixels): nothing would be cut off.");
                var cropped = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                using (var canvas = new SKCanvas(cropped))
                {
                    canvas.Clear(SKColors.Transparent);
                    using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                    using var image = SKImage.FromBitmap(picture);
                    canvas.DrawImage(image, new SKRect(x, y, x + w, y + h), new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Nearest), paint);
                }
                return (cropped, $"Crop {w}x{h}");
            }
            case PictureEditKind.Resize:
            {
                if (request.Width is not > 0 && request.Height is not > 0)
                    throw new PictureEditException("Resize needs the new width or height in pixels (one alone keeps the picture's shape).");
                var w = request.Width is > 0 and var gw ? gw : (int)Math.Round(picture.Width * (request.Height!.Value / (double)picture.Height));
                var h = request.Height is > 0 and var gh ? gh : (int)Math.Round(picture.Height * (request.Width!.Value / (double)picture.Width));
                (w, h) = (Math.Clamp(w, 1, MaxSide), Math.Clamp(h, 1, MaxSide));
                if ((w, h) == (picture.Width, picture.Height))
                    throw new PictureEditException($"The picture is {w}x{h} pixels already.");
                var resized = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                // Made smaller through its mipmaps (no shimmer of skipped pixels), larger with a cubic curve.
                var smaller = w < picture.Width || h < picture.Height;
                var sampling = smaller ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear) : new SKSamplingOptions(SKCubicResampler.Mitchell);
                using (var canvas = new SKCanvas(resized))
                using (var image = SKImage.FromBitmap(picture))
                {
                    canvas.Clear(SKColors.Transparent);
                    using var paint = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = true };
                    canvas.DrawImage(image, new SKRect(0, 0, w, h), sampling, paint);
                }
                return (resized, $"Resize {w}x{h}");
            }
            case PictureEditKind.Rotate:
            {
                var degrees = ((request.Degrees % 360) + 360) % 360;
                if (degrees < 0.01 || degrees > 359.99)
                    throw new PictureEditException("Rotate needs degrees, clockwise: 90 turns it right, 270 (or -90) left, 180 upside down.");
                return (Turned(picture, degrees, opaque), string.Create(CultureInfo.InvariantCulture, $"Turn {degrees:0.#}°"));
            }
            case PictureEditKind.Flip:
                return (Flipped(picture, request.Vertical), request.Vertical ? "Flip" : "Mirror");
            case PictureEditKind.Colour:
            {
                if (request is { Brightness: 0, Contrast: 1, Saturation: 1 })
                    throw new PictureEditException("Colour needs brightness (-1 to 1, 0 as it is), contrast or saturation (1 as it is; saturation 0 is black and white).");
                var coloured = new SKBitmap(new SKImageInfo(picture.Width, picture.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                using (var canvas = new SKCanvas(coloured))
                {
                    canvas.Clear(SKColors.Transparent);
                    using var filter = SKColorFilter.CreateColorMatrix(ColourMatrix(request.Brightness, request.Contrast, request.Saturation));
                    using var paint = new SKPaint { BlendMode = SKBlendMode.Src, ColorFilter = filter };
                    using var whole = SKImage.FromBitmap(picture);
                    canvas.DrawImage(whole, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
                }
                var said = new List<string>();
                if (request.Saturation == 0) said.Add("Black and white");
                else if (request.Saturation != 1) said.Add(string.Create(CultureInfo.InvariantCulture, $"Colour {request.Saturation:0.##}"));
                if (request.Brightness != 0) said.Add(string.Create(CultureInfo.InvariantCulture, $"{(request.Brightness > 0 ? "Brighter" : "Darker")} {Math.Abs(request.Brightness):0.##}"));
                if (request.Contrast != 1) said.Add(string.Create(CultureInfo.InvariantCulture, $"Contrast {request.Contrast:0.##}"));
                return (coloured, string.Join(", ", said));
            }
            default:
                return (picture.Copy(), "Copy");
        }
    }

    /// <summary>The picture turned clockwise: a quarter turn swaps its sides; any other angle grows it to hold its corners.</summary>
    private static SKBitmap Turned(SKBitmap picture, double degrees, bool opaque)
    {
        var radians = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Abs(Math.Cos(radians)), Math.Abs(Math.Sin(radians)));
        var quarter = Math.Abs(degrees / 90 - Math.Round(degrees / 90)) < 1e-6;
        var w = (int)Math.Round(picture.Width * cos + picture.Height * sin);
        var h = (int)Math.Round(picture.Width * sin + picture.Height * cos);
        if (w > MaxSide || h > MaxSide)
            throw new PictureEditException($"Turned, the picture would be {w}x{h} pixels: more than {MaxSide} a side.");
        var turned = new SKBitmap(new SKImageInfo(Math.Max(1, w), Math.Max(1, h), SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(turned);
        canvas.Clear(opaque ? SKColors.White : SKColors.Transparent);
        canvas.Translate(turned.Width / 2f, turned.Height / 2f);
        canvas.RotateDegrees((float)degrees);
        canvas.Translate(-picture.Width / 2f, -picture.Height / 2f);
        using var image = SKImage.FromBitmap(picture);
        using var paint = new SKPaint { IsAntialias = !quarter };
        // A quarter turn moves whole pixels; any other angle is drawn through a cubic curve.
        canvas.DrawImage(image, 0, 0, quarter ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        return turned;
    }

    /// <summary>The picture mirrored (left and right swapped), or upside down.</summary>
    private static SKBitmap Flipped(SKBitmap picture, bool vertical)
    {
        var flipped = new SKBitmap(new SKImageInfo(picture.Width, picture.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(flipped);
        canvas.Clear(SKColors.Transparent);
        if (vertical)
            canvas.Scale(1, -1, 0, picture.Height / 2f);
        else
            canvas.Scale(-1, 1, picture.Width / 2f, 0);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        using var whole = SKImage.FromBitmap(picture);
        canvas.DrawImage(whole, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        return flipped;
    }

    /// <summary>
    /// Saturation, then contrast about middle grey, then brightness, as one 4x5 colour matrix (rows red, green, blue
    /// and alpha; the fifth column is added, in parts of 1).
    /// </summary>
    internal static float[] ColourMatrix(double brightness, double contrast, double saturation)
    {
        var (s, c) = ((float)Math.Clamp(saturation, 0, 4), (float)Math.Clamp(contrast, 0, 4));
        var add = (float)Math.Clamp(brightness, -1, 1) + (1 - c) / 2;
        // Rec. 709 luma: how bright each colour looks.
        const float r = 0.2126f, g = 0.7152f, b = 0.0722f;
        return
        [
            c * (r * (1 - s) + s), c * g * (1 - s), c * b * (1 - s), 0, add,
            c * r * (1 - s), c * (g * (1 - s) + s), c * b * (1 - s), 0, add,
            c * r * (1 - s), c * g * (1 - s), c * (b * (1 - s) + s), 0, add,
            0, 0, 0, 1, 0,
        ];
    }

    /// <summary>The kind of file a format's name or an extension means; null for none a picture is saved as.</summary>
    internal static (string Extension, SKEncodedImageFormat Kind)? FormatOf(string format) => format.Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "png" => (".png", SKEncodedImageFormat.Png),
        "jpg" or "jpeg" => (".jpg", SKEncodedImageFormat.Jpeg),
        "webp" => (".webp", SKEncodedImageFormat.Webp),
        _ => null,
    };

    /// <summary>
    /// Where the edit of <paramref name="source"/> goes: beside it, a version of a picture in <paramref name="picturesRoot"/>
    /// named after the edit, any other picture after itself and the edit; " 2", " 3" when that is taken.
    /// </summary>
    public static string Target(string source, string label, string extension, string? picturesRoot)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var name = Video.Storyboard.Safe(PictureFolder.IsVersion(source, picturesRoot) ? label : $"{Path.GetFileNameWithoutExtension(source)} ({label})");
        var path = Path.Combine(folder, name + extension);
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{name} {n}{extension}");
        return path;
    }

    /// <summary>
    /// The new PNG with the text chunks of the PNG it was made from (ComfyUI's prompt and workflow, AUTOMATIC1111's
    /// parameters), so the edit still says how the picture was made; not the scene it was drawn from.
    /// </summary>
    internal static byte[] WithTexts(byte[] png, byte[] from)
    {
        const int afterHeader = 8 + 25;
        if (png.Length < afterHeader || from.Length < afterHeader || from[1] != (byte)'P' || from[2] != (byte)'N' || from[3] != (byte)'G')
            return png;
        var kept = new List<byte>();
        for (var at = 8; at + 12 <= from.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(from.AsSpan(at));
            var type = Encoding.ASCII.GetString(from, at + 4, 4);
            if (length < 0 || at + 12 + length > from.Length || type == "IEND")
                break;
            if (type is "tEXt" or "zTXt" or "iTXt")
            {
                var zero = Array.IndexOf(from, (byte)0, at + 8, length);
                var key = zero < 0 ? "" : Encoding.Latin1.GetString(from, at + 8, zero - at - 8);
                if (!key.Equals(Render.SceneStamp.Key, StringComparison.OrdinalIgnoreCase))
                    kept.AddRange(from.AsSpan(at, 12 + length));
            }
            at += 12 + length;
        }
        return kept.Count == 0 ? png : [.. png.AsSpan(0, afterHeader), .. kept, .. png.AsSpan(afterHeader)];
    }
}
