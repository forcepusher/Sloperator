using Sloperator.Core.Documents;
using Sloperator.Core.Media;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>The Image editor's pictures: a folder each, its versions the picture files in it, and the mask of a part to redraw.</summary>
public sealed class PictureFolderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-pictures-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    [Fact]
    public void A_pictures_versions_are_its_picture_files_oldest_first()
    {
        var first = PictureFolder.New(_dir);
        var second = PictureFolder.New(_dir);
        Assert.Equal("Untitled picture", Path.GetFileName(first));
        Assert.Equal("Untitled picture 2", Path.GetFileName(second));
        Assert.Empty(PictureFolder.Versions(first));

        var a = Write(first, "Sloperator_00002_.png", -60);
        var b = Write(first, "Sloperator_redraw_00001_.png", -10);
        File.WriteAllText(Path.Combine(first, "notes.txt"), "not a version");
        Assert.Equal([a, b], PictureFolder.Versions(first));
        // The one changed last first: the first, which has a version newer than the second's making.
        Write(second, "x.jpg", -120);
        Assert.Equal([first, second], PictureFolder.All(_dir));
        Assert.Empty(PictureFolder.All(Path.Combine(_dir, "none")));
    }

    [Fact]
    public void A_new_picture_can_have_a_name_from_the_start_and_one_is_renamed_with_its_versions()
    {
        // Named as a file dropped on the panel is: safe as a folder's name, numbered when taken.
        var harbour = PictureFolder.Folders.New(_dir, "harbour");
        Assert.Equal("harbour", Path.GetFileName(harbour));
        Assert.Equal("harbour 2", Path.GetFileName(PictureFolder.Folders.New(_dir, "harbour")));
        Assert.Equal("a_b", Path.GetFileName(PictureFolder.Folders.New(_dir, "a:b")));
        Assert.Equal("Untitled picture", Path.GetFileName(PictureFolder.Folders.New(_dir, " .. ")));

        var a = Write(harbour, "a.png", -5);
        var renamed = MediaFolders.Rename(harbour, " Harbour at night ");
        Assert.Equal(Path.Combine(_dir, "Harbour at night"), renamed);
        Assert.False(Directory.Exists(harbour));
        Assert.Equal([Path.Combine(renamed, Path.GetFileName(a))], PictureFolder.Versions(renamed));
        // A name another has is refused, and nothing moves; its own name, or none, is no change.
        var taken = Assert.Throws<IOException>(() => MediaFolders.Rename(renamed, "harbour 2"));
        Assert.Contains("harbour 2", taken.Message);
        Assert.True(Directory.Exists(renamed));
        Assert.Equal(renamed, MediaFolders.Rename(renamed, "Harbour at night"));
        Assert.Equal(renamed, MediaFolders.Rename(renamed, "  "));
    }

    [Fact]
    public void The_mask_is_white_in_the_box_black_outside_and_soft_at_its_edge()
    {
        var mask = Path.Combine(_dir, "mask.png");
        // A box from 100,100 to 300,200 on a 400x300 picture.
        PictureFolder.SaveMask(new DocumentArea(0.25, 1 / 3.0, 0.75, 2 / 3.0), 400, 300, mask);
        Assert.Equal((400, 300), PictureFolder.SizeOf(mask));
        var pixels = DocumentArea.Decode(mask)!;
        byte At(int x, int y) => pixels.Bgra[(y * pixels.Width + x) * 4 + 2];
        Assert.Equal(255, At(200, 150));
        Assert.Equal(0, At(20, 20));
        Assert.Equal(0, At(200, 290));
        // At the box's side, half way up the ramp; a few pixels out, nothing.
        Assert.InRange(At(100, 150), 100, 160);
        Assert.Equal(0, At(90, 150));
        Assert.Equal(255, At(110, 150));
    }

    [Fact]
    public void A_painted_mask_is_white_where_the_brush_went_and_soft_at_its_edge()
    {
        var mask = Path.Combine(_dir, "painted.png");
        // On a 400x300 picture: a line across the middle with a brush 40 wide, and a dab 80 wide near the top.
        BrushStroke[] strokes = [new([(0.25, 0.5), (0.75, 0.5)], 0.1), new([(0.5, 0.2)], 0.2)];
        PictureFolder.SaveMask(strokes, 400, 300, mask);
        Assert.Equal((400, 300), PictureFolder.SizeOf(mask));
        var pixels = DocumentArea.Decode(mask)!;
        byte At(int x, int y) => pixels.Bgra[(y * pixels.Width + x) * 4 + 2];
        // The line: white along it and in its round ends, half way at its side, black a little way out.
        Assert.Equal(255, At(200, 150));
        Assert.Equal(255, At(90, 150));
        Assert.InRange(At(200, 170), 90, 170);
        Assert.Equal(0, At(200, 185));
        Assert.Equal(0, At(65, 150));
        // The dab: a disc.
        Assert.Equal(255, At(200, 60));
        Assert.Equal(255, At(225, 60));
        Assert.Equal(0, At(250, 60));
        Assert.Equal(0, At(240, 100));
        // Nothing elsewhere, and all of it opaque: the mask is read from one colour.
        Assert.Equal(0, At(20, 280));
        Assert.Equal(255, pixels.Bgra[3]);
    }

    [Fact]
    public void The_box_around_what_was_painted_takes_in_the_brush_and_stays_on_the_picture()
    {
        // A brush a tenth of a 400x300 picture's width is 40 pixels: 0.05 of the width each way, 0.0667 of the height.
        var area = PictureFolder.AreaOf([new([(0.25, 0.5), (0.75, 0.5)], 0.1)], 400, 300)!;
        Assert.Equal(0.20, area.Left, 6);
        Assert.Equal(0.80, area.Right, 6);
        Assert.Equal(0.5 - 20.0 / 300, area.Top, 6);
        Assert.Equal(0.5 + 20.0 / 300, area.Bottom, 6);
        // At the picture's corner: no further than its edge.
        var corner = PictureFolder.AreaOf([new([(0, 0)], 0.2)], 400, 300)!;
        Assert.Equal((0, 0), (corner.Left, corner.Top));
        Assert.Equal((0, 0, 40, 40), corner.Pixels(400, 300));
        Assert.Null(PictureFolder.AreaOf([], 400, 300));
    }

    private static string Write(string folder, string name, int minutes)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [137, 80, 78, 71]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(minutes));
        return path;
    }
}
