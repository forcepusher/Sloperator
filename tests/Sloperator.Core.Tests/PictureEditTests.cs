using System.Text.Json.Nodes;
using Sloperator.Core.Media;
using Sloperator.Core.Render;
using Sloperator.Core.Tools;
using SkiaSharp;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Exact edits of a picture (the Image editor's Edit buttons, the edit_picture tool): each is a new file beside
/// the picture, which stays as it was, to the pixel.
/// </summary>
public sealed class PictureEditTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sl-picture-edit-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    private static readonly SKColor Red = new(220, 30, 30), Green = new(30, 180, 60), Blue = new(40, 60, 220), Yellow = new(240, 220, 40);

    /// <summary>A picture 40 by 20 in four blocks: red and green on top, blue and yellow under them.</summary>
    private string Quarters(string name = "quarters.png", SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(40, 20, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint();
            foreach (var (colour, x, y) in new[] { (Red, 0, 0), (Green, 20, 0), (Blue, 0, 10), (Yellow, 20, 10) })
            {
                paint.Color = colour;
                canvas.DrawRect(x, y, 20, 10, paint);
            }
        }
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var data = bitmap.Encode(format, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static SKBitmap Read(string path) => SKBitmap.Decode(File.ReadAllBytes(path));

    private static void Near(SKColor expected, SKColor actual, int within = 0) =>
        Assert.True(Math.Abs(expected.Red - actual.Red) <= within && Math.Abs(expected.Green - actual.Green) <= within && Math.Abs(expected.Blue - actual.Blue) <= within,
            $"expected {expected}, got {actual}");

    [Fact]
    public void A_crop_keeps_the_box_and_the_picture_stays_as_it_was()
    {
        var source = Quarters();
        var before = File.ReadAllBytes(source);
        var edited = PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Crop) { X = 15, Y = 5, Width = 10, Height = 10 }, null);
        Assert.Equal(Path.Combine(_dir, "quarters (Crop 10x10).png"), edited.Path);
        Assert.Equal((10, 10, 40, 20), (edited.Width, edited.Height, edited.WasWidth, edited.WasHeight));
        using var crop = Read(edited.Path);
        Assert.Equal((10, 10), (crop.Width, crop.Height));
        // The four blocks' corners meet in its middle, each pixel as it was.
        Near(Red, crop.GetPixel(4, 4));
        Near(Green, crop.GetPixel(5, 4));
        Near(Blue, crop.GetPixel(4, 5));
        Near(Yellow, crop.GetPixel(5, 5));
        Assert.Equal(before, File.ReadAllBytes(source));
        // The same edit again is another file; a box past the edge is cut to the picture, and one that is all of it or none is said.
        Assert.Equal(Path.Combine(_dir, "quarters (Crop 10x10) 2.png"), PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Crop) { X = 15, Y = 5, Width = 10, Height = 10 }, null).Path);
        Assert.Equal((10, 5), PictureFolder.SizeOf(PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Crop) { X = 30, Y = 15, Width = 100, Height = 100 }, null).Path));
        Assert.Contains("the whole picture", Assert.Throws<PictureEditException>(() => PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Crop) { Width = 40, Height = 20 }, null)).Message);
        Assert.Contains("is empty", Assert.Throws<PictureEditException>(() => PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Crop) { X = 5, Width = 0, Height = 5 }, null)).Message);
    }

    [Fact]
    public void A_resize_keeps_the_shape_with_one_side_given_and_a_turn_or_mirror_moves_whole_pixels()
    {
        var source = Quarters();
        var half = PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Resize) { Width = 20 }, null);
        Assert.Equal((20, 10, "Resize 20x10"), (half.Width, half.Height, half.Label));
        using (var small = Read(half.Path))
            Near(Yellow, small.GetPixel(15, 7), 2);
        Assert.Equal((80, 100), PictureFolder.SizeOf(PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Resize) { Width = 80, Height = 100 }, null).Path));
        Assert.Equal((100, 50), PictureFolder.SizeOf(PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Resize) { Height = 50 }, null).Path));

        // A quarter turn to the right: what was at the left is on top, the blue block in the top left corner.
        var right = PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Rotate) { Degrees = 90 }, null);
        Assert.Equal((20, 40, "Turn 90°"), (right.Width, right.Height, right.Label));
        using (var turned = Read(right.Path))
        {
            Near(Blue, turned.GetPixel(0, 0));
            Near(Red, turned.GetPixel(19, 0));
            Near(Yellow, turned.GetPixel(0, 39));
            Near(Green, turned.GetPixel(19, 39));
        }
        // To the left, given as -90.
        using (var left = Read(PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Rotate) { Degrees = -90 }, null).Path))
        {
            Assert.Equal((20, 40), (left.Width, left.Height));
            Near(Green, left.GetPixel(0, 0));
            Near(Blue, left.GetPixel(19, 39));
        }
        // Any other angle grows the picture to hold its corners, clear where nothing is.
        using (var tilted = Read(PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Rotate) { Degrees = 45 }, null).Path))
        {
            Assert.Equal((42, 42), (tilted.Width, tilted.Height));
            Assert.Equal(0, tilted.GetPixel(0, 0).Alpha);
            Assert.Equal(255, tilted.GetPixel(21, 21).Alpha);
        }
        using (var mirrored = Read(PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Flip), null).Path))
        {
            Near(Green, mirrored.GetPixel(0, 0));
            Near(Red, mirrored.GetPixel(39, 0));
        }
        using (var flipped = Read(PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Flip) { Vertical = true }, null).Path))
        {
            Near(Blue, flipped.GetPixel(0, 0));
            Near(Red, flipped.GetPixel(0, 19));
        }
    }

    [Fact]
    public void Colours_change_by_a_matrix_and_another_format_is_another_kind_of_file()
    {
        var source = Quarters();
        var grey = PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Colour) { Saturation = 0 }, null);
        Assert.Equal("Black and white", grey.Label);
        using (var bw = Read(grey.Path))
        {
            var p = bw.GetPixel(5, 5);
            Assert.True(Math.Abs(p.Red - p.Green) <= 1 && Math.Abs(p.Green - p.Blue) <= 1, p.ToString());
            // Red's luma: about a fifth of its red, a little of the rest.
            Assert.InRange(p.Red, 60, 80);
        }
        using (var brighter = Read(PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Colour) { Brightness = 0.2 }, null).Path))
            Near(new SKColor(255, 81, 81), brighter.GetPixel(5, 5), 1);
        Assert.Contains("needs brightness", Assert.Throws<PictureEditException>(() => PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Colour), null)).Message);

        // As a JPEG: a real one, with white where a turn leaves nothing (a JPEG has no clear pixels).
        var jpg = PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Convert) { Format = "JPEG" }, null);
        Assert.Equal((Path.Combine(_dir, "quarters (As JPG).jpg"), "As JPG"), (jpg.Path, jpg.Label));
        Assert.Equal([0xFF, 0xD8], File.ReadAllBytes(jpg.Path)[..2]);
        using (var tilted = Read(PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Rotate) { Degrees = 45, Format = "jpg" }, null).Path))
            Near(SKColors.White, tilted.GetPixel(1, 1), 8);
        Assert.Equal("RIFF"u8.ToArray(), File.ReadAllBytes(PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Resize) { Width = 20, Format = "webp" }, null).Path)[..4]);
        Assert.Contains("png, jpg or webp", Assert.Throws<PictureEditException>(() => PictureEdit.Save(source, new PictureEditRequest(PictureEditKind.Convert) { Format = "tiff" }, null)).Message);
        Assert.Contains("isn't there", Assert.Throws<PictureEditException>(() => PictureEdit.Save(Path.Combine(_dir, "none.png"), new PictureEditRequest(PictureEditKind.Flip), null)).Message);
        File.WriteAllText(Path.Combine(_dir, "words.png"), "not a picture");
        Assert.Contains("can't be read as a picture", Assert.Throws<PictureEditException>(() => PictureEdit.Save(Path.Combine(_dir, "words.png"), new PictureEditRequest(PictureEditKind.Flip), null)).Message);
    }

    /// <summary>A JPEG with the orientation a camera writes in it (EXIF's tag 0x0112) put in after its first two bytes.</summary>
    internal static byte[] WithOrientation(byte[] jpeg, byte orientation)
    {
        byte[] exif = [(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, orientation, 0, 0, 0, 0, 0, 0, 0];
        var length = exif.Length + 2;
        return [.. jpeg.AsSpan(0, 2), 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. exif, .. jpeg.AsSpan(2)];
    }

    [Fact]
    public void A_photo_taken_with_the_phone_on_its_side_is_edited_as_it_is_seen()
    {
        // Stored 40 by 20 with "turn me a quarter to the right to show me" (6), as a phone held upright stores a photo.
        var stored = Quarters("photo.jpg", SKEncodedImageFormat.Jpeg);
        File.WriteAllBytes(stored, WithOrientation(File.ReadAllBytes(stored), 6));
        using (var seen = PictureEdit.Decode(File.ReadAllBytes(stored))!)
        {
            Assert.Equal((20, 40), (seen.Width, seen.Height));
            Near(Blue, seen.GetPixel(2, 2), 12);
            Near(Green, seen.GetPixel(17, 37), 12);
        }
        // Made smaller, it stands upright in any program: the edit's file needs no orientation of its own.
        var small = PictureEdit.Save(stored, new PictureEditRequest(PictureEditKind.Resize) { Width = 10 }, null);
        Assert.Equal((10, 20, 20, 40), (small.Width, small.Height, small.WasWidth, small.WasHeight));
        using var read = Read(small.Path);
        Assert.Equal((10, 20), (read.Width, read.Height));
        Near(Blue, read.GetPixel(1, 1), 16);
        // Mirrored and turned (5 and 7) are told apart: transposed, the red block stays in the top left corner.
        File.WriteAllBytes(stored, WithOrientation(File.ReadAllBytes(Quarters("photo.jpg", SKEncodedImageFormat.Jpeg)), 5));
        using (var transposed = PictureEdit.Decode(File.ReadAllBytes(stored))!)
        {
            Assert.Equal((20, 40), (transposed.Width, transposed.Height));
            Near(Red, transposed.GetPixel(2, 2), 12);
            Near(Yellow, transposed.GetPixel(17, 37), 12);
        }
    }

    [Fact]
    public void A_version_in_the_image_editor_is_named_after_the_edit_and_keeps_what_it_says_of_how_it_was_made_but_not_its_scene()
    {
        var pictures = Path.Combine(_dir, "pictures");
        var version = Quarters(Path.Combine("pictures", "Lighthouse", "Sloperator_00001_.png"));
        // As ComfyUI saves it, with its prompt; and as render stamps a scene's picture.
        static byte[] Text(string key, string text)
        {
            byte[] data = [.. System.Text.Encoding.Latin1.GetBytes(key), 0, .. System.Text.Encoding.Latin1.GetBytes(text)];
            byte[] chunk = [.. BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(data.Length)), .. "tEXt"u8, .. data, 0, 0, 0, 0];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), SceneStamp.Crc(chunk.AsSpan(4, 4 + data.Length)));
            return chunk;
        }
        var png = File.ReadAllBytes(version);
        const string prompt = """{"3": {"class_type": "KSampler", "inputs": {"seed": 7, "steps": 20, "cfg": 7, "sampler_name": "euler", "scheduler": "normal", "denoise": 1, "positive": ["6", 0]}}, "6": {"class_type": "CLIPTextEncode", "inputs": {"text": "a white lighthouse"}}}""";
        png = [.. png.AsSpan(0, 33), .. Text("prompt", prompt), .. png.AsSpan(33)];
        File.WriteAllBytes(version, SceneStamp.Stamp(png, new SceneSource(@"C:\scenes\lighthouse.svg", true, "<svg/>")));
        Assert.NotNull(SceneStamp.Of(version));

        var edited = PictureEdit.Save(version, new PictureEditRequest(PictureEditKind.Flip), pictures);
        Assert.Equal(Path.Combine(pictures, "Lighthouse", "Mirror.png"), edited.Path);
        Assert.True(PictureFolder.IsVersion(edited.Path, pictures));
        Assert.Equal("a white lighthouse", MediaRecipe.Read(edited.Path)!.Prompt);
        Assert.Null(SceneStamp.Of(edited.Path));
        using var read = Read(edited.Path);
        Near(Green, read.GetPixel(0, 0));
        // The box the panel marks, in parts of the picture: its right half.
        var half = PictureEdit.Save(version, new PictureEditRequest(PictureEditKind.Crop) { Area = new Documents.DocumentArea(0.5, 0, 1, 1) }, pictures);
        Assert.Equal((Path.Combine(pictures, "Lighthouse", "Crop 20x20.png"), 20, 20), (half.Path, half.Width, half.Height));
    }

    [Fact]
    public async Task The_tool_edits_as_asked_and_says_what_it_saved()
    {
        var source = Quarters();
        var pictures = Path.Combine(_dir, "pictures");
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new Settings.AgentSettings(), WorkingDirectory = _dir, PicturesDirectory = pictures, SeesImages = false };
        var tool = new EditPictureTool();
        var ct = TestContext.Current.CancellationToken;
        Task<ToolResult> Run(string json) => tool.ExecuteAsync(JsonNode.Parse(json.Replace("{p}", source.Replace(@"\", @"\\")))!.AsObject(), ctx, ct);

        var crop = await Run("""{"path": "{p}", "edit": "crop", "x": 20, "y": 0, "width": 20, "height": 10}""");
        Assert.False(crop.IsError, crop.Text);
        Assert.StartsWith($"Crop 20x10: saved {Path.Combine(_dir, "quarters (Crop 20x10).png")}, 20x10 pixels (40x20 before).", crop.Text);
        Assert.Equal([Path.Combine(_dir, "quarters (Crop 20x10).png")], crop.Files);
        var resized = await Run("""{"path": "{p}", "edit": "Resize", "width": 400, "format": "jpg"}""");
        Assert.Contains("quarters (Resize 400x200).jpg, 400x200 pixels", resized.Text);
        Assert.Contains("Turn 270°", (await Run("""{"path": "{p}", "edit": "rotate", "degrees": -90}""")).Text);
        Assert.Contains("Flip:", (await Run("""{"path": "{p}", "edit": "flip", "way": "vertical"}""")).Text);
        Assert.Contains("Mirror:", (await Run("""{"path": "{p}", "edit": "mirror"}""")).Text);
        Assert.Contains("Black and white:", (await Run("""{"path": "{p}", "edit": "grayscale"}""")).Text);
        Assert.Contains("As WEBP:", (await Run("""{"path": "{p}", "edit": "convert", "format": "webp"}""")).Text);
        // What can't be done is said, as an error the model reads.
        Assert.True((await Run("""{"path": "{p}", "edit": "resize"}""")).IsError);
        await Assert.ThrowsAsync<ToolArgumentException>(() => Run("""{"path": "{p}", "edit": "sharpen"}"""));
        await Assert.ThrowsAsync<ToolArgumentException>(() => Run("""{"path": "{p}", "edit": "convert"}"""));
        Assert.Equal("Crop quarters.png", tool.Describe(JsonNode.Parse("""{"path": "C:\\x\\quarters.png", "edit": "crop"}""")!.AsObject()));
        Assert.Equal("Save quarters.png as JPG", tool.Describe(JsonNode.Parse("""{"path": "C:\\x\\quarters.png", "edit": "convert", "format": "jpg"}""")!.AsObject()));

        // A version of a picture of the Image editor: the edit is its next one.
        var version = Quarters(Path.Combine("pictures", "Untitled picture", "a.png"));
        var next = await tool.ExecuteAsync(new JsonObject { ["path"] = version, ["edit"] = "rotate", ["degrees"] = 180 }, ctx, ct);
        Assert.Contains("It is the picture's newest version in the Image editor; a.png stays as it was.", next.Text);
        Assert.True(File.Exists(Path.Combine(pictures, "Untitled picture", "Turn 180°.png")));
    }
}
