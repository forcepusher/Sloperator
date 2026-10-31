using Sloperator.Core.Documents;
using Sloperator.Core.Media;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>The Image editor's pictures: a folder each, its versions the picture files in it, and the mask of a part to redraw.</summary>
public sealed class PictureFolderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-pictures-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

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

    private static string Write(string folder, string name, int minutes)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [137, 80, 78, 71]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(minutes));
        return path;
    }
}
