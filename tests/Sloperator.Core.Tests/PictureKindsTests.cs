using Sloperator.Core.Media;
using Sloperator.Core.Tools;
using SkiaSharp;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Pictures of kinds Skia has no decoder for: a game's Targa textures read here, a TIFF by Windows' own decoders.</summary>
public class PictureKindsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-kinds-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    /// <summary>A Targa file's 18 bytes of header.</summary>
    private static byte[] Head(int kind, int width, int height, int depth, int descriptor, int mapCount = 0, int mapDepth = 0) =>
        [0, (byte)(mapCount > 0 ? 1 : 0), (byte)kind, 0, 0, (byte)mapCount, (byte)(mapCount >> 8), (byte)mapDepth, 0, 0, 0, 0, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), (byte)depth, (byte)descriptor];

    private static (int R, int G, int B, int A) At(SKBitmap picture, int x, int y)
    {
        var c = picture.GetPixel(x, y);
        return (c.Red, c.Green, c.Blue, c.Alpha);
    }

    [Fact]
    public void A_targa_texture_is_read_whichever_way_it_is_stored()
    {
        // Two by two, plain, blue first, rows from the bottom up as most are: red and green below, blue and white above.
        using var plain = TgaFile.Decode([.. Head(2, 2, 2, 24, 0), 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255])!;
        Assert.Equal((2, 2), (plain.Width, plain.Height));
        Assert.Equal(SKAlphaType.Opaque, plain.AlphaType);
        Assert.Equal((0, 0, 255, 255), At(plain, 0, 0));
        Assert.Equal((255, 255, 255, 255), At(plain, 1, 0));
        Assert.Equal((255, 0, 0, 255), At(plain, 0, 1));
        Assert.Equal((0, 255, 0, 255), At(plain, 1, 1));

        // Packed in runs, with clearness, rows from the top down: three half-clear red pixels in one run, then one green as it is.
        using var packed = TgaFile.Decode([.. Head(10, 2, 2, 32, 0x28), 0x82, 0, 0, 255, 128, 0x00, 0, 255, 0, 255])!;
        Assert.Equal(SKAlphaType.Premul, packed.AlphaType);
        Assert.Equal(128, At(packed, 0, 0).A);
        Assert.InRange(At(packed, 0, 0).R, 250, 255);
        Assert.Equal(128, At(packed, 0, 1).A);
        Assert.Equal((0, 255, 0, 255), At(packed, 1, 1));
        // The same bytes with a header that keeps no bits for clearness: solid.
        using var solid = TgaFile.Decode([.. Head(10, 2, 2, 32, 0x20), 0x82, 0, 0, 255, 128, 0x00, 0, 255, 0, 255])!;
        Assert.Equal((255, 0, 0, 255), At(solid, 0, 0));

        // Grey, a table of colours, and five bits a colour.
        using var grey = TgaFile.Decode([.. Head(3, 2, 1, 8, 0x20), 0, 200])!;
        Assert.Equal((200, 200, 200, 255), At(grey, 1, 0));
        using var mapped = TgaFile.Decode([.. Head(1, 2, 1, 8, 0x20, mapCount: 2, mapDepth: 24), 0, 0, 255, 255, 0, 0, 1, 0])!;
        Assert.Equal((0, 0, 255, 255), At(mapped, 0, 0));
        Assert.Equal((255, 0, 0, 255), At(mapped, 1, 0));
        using var five = TgaFile.Decode([.. Head(2, 1, 1, 16, 0x20), 0x00, 0x7C])!;
        Assert.Equal((255, 0, 0, 255), At(five, 0, 0));

        // Cut short, or no Targa at all: nothing, without a throw.
        Assert.Null(TgaFile.Decode([.. Head(2, 2, 2, 24, 0), 0, 0, 255]));
        Assert.Null(TgaFile.Decode([.. Head(10, 2, 2, 32, 0x28), 0x82, 0, 0]));
        Assert.Null(TgaFile.Decode("II*\0 this is the start of a TIFF file, not a Targa one"u8));
        Assert.Null(TgaFile.Decode("just some words in a text file"u8));
        Assert.Null(TgaFile.Decode([]));
    }

    [Fact]
    public void A_targa_file_is_a_picture_to_read_show_and_edit()
    {
        var path = Path.Combine(_dir, "wall.tga");
        // 64 by 32: left half red, right half blue, rows from the top.
        var pixels = new byte[64 * 32 * 3];
        for (var i = 0; i < 64 * 32; i++)
            (pixels[i * 3], pixels[i * 3 + 1], pixels[i * 3 + 2]) = i % 64 < 32 ? ((byte)0, (byte)0, (byte)255) : ((byte)255, (byte)0, (byte)0);
        File.WriteAllBytes(path, [.. Head(2, 64, 32, 24, 0x20), .. pixels]);

        Assert.True(Picture.Handles(path));
        Assert.Equal((64, 32), PictureFolder.SizeOf(path));
        using (var read = PictureFile.Decode(path)!)
        {
            Assert.Equal((255, 0, 0, 255), At(read, 10, 16));
            Assert.Equal((0, 0, 255, 255), At(read, 50, 16));
        }
        // What the model is shown of it, and told.
        var shown = Picture.Read(path);
        Assert.False(shown.IsError);
        Assert.Contains("a 64x32 picture", shown.Text);
        Assert.NotNull(shown.Image);
        // Made a PNG, cut to its right half: a new file, the texture as it was.
        var made = PictureEdit.Save(path, new PictureEditRequest(PictureEditKind.Crop) { X = 32, Y = 0, Width = 32, Height = 32 }, null);
        Assert.EndsWith(".png", made.Path);
        using var cut = SKBitmap.Decode(made.Path);
        Assert.Equal((32, 32), (cut.Width, cut.Height));
        Assert.Equal((0, 0, 255, 255), At(cut, 16, 16));

        // A file of that name that is no picture says so.
        var not = Path.Combine(_dir, "not.tga");
        File.WriteAllText(not, "words");
        Assert.True(Picture.Read(not).IsError);
        Assert.Null(PictureFile.Decode(not));
    }

    [Theory]
    [InlineData("p3", ".jpg")]
    [InlineData("p3", ".png")]
    [InlineData("adobe", ".jpg")]
    [InlineData("srgb", ".png")]
    public void A_photo_in_a_wider_colour_space_is_read_into_the_usual_one(string space, string kind)
    {
        // A phone's photo names Display P3, a camera's often Adobe RGB: the same numbers mean stronger colours there.
        var named = space switch
        {
            "p3" => SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3),
            "adobe" => SKColorSpace.CreateRgb(SKColorSpaceTransferFn.TwoDotTwo, SKColorSpaceXyz.AdobeRgb),
            _ => SKColorSpace.CreateSrgb(),
        };
        var path = Path.Combine(_dir, space + kind);
        using (var bitmap = new SKBitmap(new SKImageInfo(96, 32, SKColorType.Rgba8888, SKAlphaType.Opaque, named)))
        {
            (byte R, byte G, byte B)[] colours = [(200, 60, 60), (60, 200, 60), (128, 128, 128)];
            for (var x = 0; x < 96; x++)
                for (var y = 0; y < 32; y++)
                    bitmap.SetPixel(x, y, new SKColor(colours[x / 32].R, colours[x / 32].G, colours[x / 32].B));
            using var pixels = bitmap.PeekPixels();
            using var data = pixels.Encode(kind == ".png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 95);
            File.WriteAllBytes(path, data!.ToArray());
        }
        using var read = PictureFile.Decode(path)!;
        var (red, green, grey) = (At(read, 16, 16), At(read, 48, 16), At(read, 80, 16));
        if (space == "srgb")
        {
            Assert.InRange(red.R, 198, 202);
            Assert.InRange(green.G, 198, 202);
            Assert.InRange(green.R, 58, 62);
        }
        else
        {
            // Stronger than the numbers read as they stand: the red redder, the green with no red left in it.
            Assert.InRange(red.R, 212, 236);
            Assert.InRange(red.G, 38, 58);
            Assert.InRange(green.R, 0, 5);
            Assert.InRange(green.G, 199, 206);
        }
        Assert.InRange(grey.R, 127, 130);
        Assert.True(read.ColorSpace is null or { IsSrgb: true });
        // An edit of it is saved in the usual space too, so the copy looks as the photo does.
        var made = PictureEdit.Save(path, new PictureEditRequest(PictureEditKind.Crop) { X = 0, Y = 0, Width = 32, Height = 32 }, null);
        using var cut = SKBitmap.Decode(made.Path);
        Assert.InRange(At(cut, 16, 16).R, space == "srgb" ? 198 : 212, space == "srgb" ? 202 : 236);
    }

#if WINDOWS
    /// <summary>A TIFF as a scanner writes its plainest: red, green and blue a byte each, unpacked, one strip.</summary>
    private static byte[] Tiff(int width, int height, Func<int, int, (byte R, byte G, byte B)> colour)
    {
        using var file = new MemoryStream();
        using var w = new BinaryWriter(file);
        var pixels = width * height * 3;
        w.Write("II"u8); w.Write((short)42); w.Write(8);
        (short Tag, short Type, int Count, int Value)[] entries =
        [
            (256, 3, 1, width), (257, 3, 1, height), (258, 3, 3, 8 + 2 + 9 * 12 + 4), (259, 3, 1, 1), (262, 3, 1, 2),
            (273, 4, 1, 8 + 2 + 9 * 12 + 4 + 6), (277, 3, 1, 3), (278, 3, 1, height), (279, 4, 1, pixels),
        ];
        w.Write((short)entries.Length);
        foreach (var e in entries) { w.Write(e.Tag); w.Write(e.Type); w.Write(e.Count); w.Write(e.Value); }
        w.Write(0);
        w.Write((short)8); w.Write((short)8); w.Write((short)8);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var c = colour(x, y);
                w.Write(c.R); w.Write(c.G); w.Write(c.B);
            }
        w.Flush();
        return file.ToArray();
    }

    [Fact]
    public void A_tiff_is_read_by_windows_own_decoders_and_is_a_picture_like_any_other()
    {
        PictureFile.Os ??= Sloperator.Platform.Windows.WindowsPictures.Decode;
        var bytes = Tiff(64, 32, (x, _) => x < 32 ? ((byte)200, (byte)60, (byte)60) : ((byte)60, (byte)90, (byte)200));
        // Skia has no decoder for it; Windows has.
        using (var data = SKData.CreateCopy(bytes))
            Assert.Null(SKCodec.Create(data));
        var raw = Sloperator.Platform.Windows.WindowsPictures.Decode(bytes)!;
        Assert.Equal((64, 32), (raw.Width, raw.Height));
        Assert.Equal((60, 60, 200, 255), (raw.Bgra[0], raw.Bgra[1], raw.Bgra[2], raw.Bgra[3]));
        Assert.Null(Sloperator.Platform.Windows.WindowsPictures.Decode("no picture, whatever a decoder makes of it"u8.ToArray()));

        var path = Path.Combine(_dir, "scan.tif");
        File.WriteAllBytes(path, bytes);
        Assert.Equal((64, 32), PictureFolder.SizeOf(path));
        using (var read = PictureFile.Decode(path)!)
        {
            Assert.Equal(SKAlphaType.Opaque, read.AlphaType);
            Assert.Equal((200, 60, 60, 255), At(read, 10, 16));
            Assert.Equal((60, 90, 200, 255), At(read, 50, 16));
        }
        Assert.Contains("a 64x32 picture", Picture.Read(path).Text);
        // The bytes as something Skia reads, for what decodes a picture itself: a PNG of it. A PNG's own bytes are handed back as they are.
        var png = PictureFile.Readable(bytes)!;
        using (var again = SKBitmap.Decode(png))
            Assert.Equal((200, 60, 60, 255), At(again, 10, 16));
        Assert.Same(png, PictureFile.Readable(png));
        // Saved as a JPEG, as the agent would for a scan too large to mail.
        var made = PictureEdit.Save(path, new PictureEditRequest(PictureEditKind.Convert) { Format = "jpg" }, null);
        Assert.EndsWith(".jpg", made.Path);
        using var jpeg = SKBitmap.Decode(made.Path);
        Assert.InRange(At(jpeg, 10, 16).R, 190, 210);
        // The file isn't held: it can be deleted at once.
        File.Delete(path);
    }
#endif
}
