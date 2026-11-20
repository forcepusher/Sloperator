using SkiaSharp;

namespace Sloperator.Core.Media;

/// <summary>
/// A Targa picture (.tga), which game engines and 3D programs keep textures in: read here, since neither Skia nor
/// Windows reads one. True colour in 15, 16, 24 or 32 bits, grey in 8 (with 8 of clearness in 16), and a table of
/// colours, each plain or run-length packed, stored from the bottom row up or the top row down.
/// </summary>
public static class TgaFile
{
    /// <summary>Whether the bytes could be a Targa file: it has no signature at its start, so its header's fields have to make sense.</summary>
    public static bool LooksLikeOne(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18)
            return false;
        var (map, kind, depth) = (data[1], data[2], data[16]);
        var (width, height) = (data[12] | data[13] << 8, data[14] | data[15] << 8);
        return map <= 1 && kind is 1 or 2 or 3 or 9 or 10 or 11 && width > 0 && height > 0 && depth is 8 or 15 or 16 or 24 or 32
               && (kind is 1 or 9) == (map == 1) && (data[17] & 0xC0) == 0;
    }

    /// <summary>The picture, upright, with its clear parts clear; null when the bytes are no Targa file this reads, or it is cut short.</summary>
    public static SKBitmap? Decode(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeOne(data))
            return null;
        var (idLength, kind, depth, descriptor) = (data[0], data[2], data[16], data[17]);
        var (width, height) = (data[12] | data[13] << 8, data[14] | data[15] << 8);
        var (mapFirst, mapCount, mapDepth) = (data[3] | data[4] << 8, data[5] | data[6] << 8, data[7]);
        if ((long)width * height > 268_000_000)
            return null;
        var at = 18 + idLength;
        // The table of colours, for a picture that names each pixel's colour by its number.
        var table = Array.Empty<uint>();
        if (data[1] == 1)
        {
            var size = (mapDepth + 7) / 8;
            if (size is < 2 or > 4 || at + mapCount * size > data.Length)
                return null;
            table = new uint[mapFirst + mapCount];
            for (var i = 0; i < mapCount; i++, at += size)
                table[mapFirst + i] = Colour(data.Slice(at, size), mapDepth, clear: mapDepth == 32);
        }
        var bytes = (depth + 7) / 8;
        var packed = kind >= 9;
        var grey = kind is 3 or 11;
        var mapped = kind is 1 or 9;
        if (mapped && depth != 8)
            return null;
        // Clearness only where the header says the last bits are for it (some 32-bit files leave them at nought).
        var clear = (descriptor & 0x0F) > 0 && depth is 32 or 16;
        var pixels = new uint[width * height];
        uint Read(ReadOnlySpan<byte> from) =>
            mapped ? (from[0] < table.Length ? table[from[0]] : 0xFF000000)
            : grey ? (uint)((bytes == 2 && clear ? from[1] : 255) << 24 | from[0] << 16 | from[0] << 8 | from[0])
            : Colour(from, depth, clear);
        var filled = 0;
        if (!packed)
        {
            if (at + (long)pixels.Length * bytes > data.Length)
                return null;
            for (; filled < pixels.Length; filled++, at += bytes)
                pixels[filled] = Read(data.Slice(at, bytes));
        }
        else
            while (filled < pixels.Length)
            {
                if (at >= data.Length)
                    return null;
                var head = data[at++];
                var count = Math.Min((head & 0x7F) + 1, pixels.Length - filled);
                if ((head & 0x80) != 0)
                {
                    // A run: one pixel, so many times.
                    if (at + bytes > data.Length)
                        return null;
                    Array.Fill(pixels, Read(data.Slice(at, bytes)), filled, count);
                    at += bytes;
                    filled += count;
                }
                else
                {
                    if (at + count * bytes > data.Length)
                        return null;
                    for (var i = 0; i < count; i++, at += bytes)
                        pixels[filled++] = Read(data.Slice(at, bytes));
                }
            }
        // Rows from the bottom up unless the header says from the top, and from the right when it says so.
        var (fromTop, fromRight) = ((descriptor & 0x20) != 0, (descriptor & 0x10) != 0);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, clear ? SKAlphaType.Premul : SKAlphaType.Opaque));
        var into = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            var row = (fromTop ? y : height - 1 - y) * width;
            for (var x = 0; x < width; x++)
            {
                var pixel = pixels[row + (fromRight ? width - 1 - x : x)];
                var (a, r, g, b) = ((byte)(pixel >> 24), (byte)(pixel >> 16), (byte)(pixel >> 8), (byte)pixel);
                var i = (y * width + x) * 4;
                // Colours scaled by their clearness, as every picture here holds them.
                (into[i], into[i + 1], into[i + 2], into[i + 3]) = a == 255 ? (b, g, r, a) : ((byte)(b * a / 255), (byte)(g * a / 255), (byte)(r * a / 255), a);
            }
        }
        return bitmap;
    }

    /// <summary>A pixel as it is stored (blue first) made 0xAARRGGBB: five bits a colour in two bytes, or a byte each in three or four.</summary>
    private static uint Colour(ReadOnlySpan<byte> from, int depth, bool clear)
    {
        if (depth is 15 or 16)
        {
            var value = from[0] | from[1] << 8;
            static uint Eight(int five) => (uint)(five << 3 | five >> 2);
            return (clear && (value & 0x8000) == 0 ? 0u : 0xFF000000) | Eight(value >> 10 & 31) << 16 | Eight(value >> 5 & 31) << 8 | Eight(value & 31);
        }
        return (uint)((from.Length > 3 && clear ? from[3] : 255) << 24 | from[2] << 16 | from[1] << 8 | from[0]);
    }
}
