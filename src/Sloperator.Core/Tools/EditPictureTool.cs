using System.Globalization;
using System.Text.Json.Nodes;
using Sloperator.Core.Media;

namespace Sloperator.Core.Tools;

/// <summary>
/// The Image editor's exact edits for the agent (<see cref="PictureEdit"/>): a crop, another size, a turn, a
/// mirror, light and colour, another kind of file, so "crop it to a square" or "make it 512 pixels wide as a JPG"
/// goes as the user asks it, to the pixel and at once, without a model drawing the picture again or a script of
/// the model's own. The file edited stays as it was; the edit is a new file beside it, in a picture of the panel
/// its next version.
/// </summary>
public sealed class EditPictureTool : Tool
{
    public override string Name => "edit_picture";

    public override string Description =>
        "Edit a picture file exactly, without changing it: the edit is saved beside it as a new file, which the result names " +
        "(for a picture of Sloperator's Image editor panel, its next version there). " +
        "edit: crop (keep the box x, y, width, height, in pixels from the top left), resize (to width and height in pixels; one alone keeps its shape), " +
        "rotate (degrees clockwise), flip (way: horizontal mirrors it, vertical turns it upside down), " +
        "colour (brightness -1 to 1, contrast and saturation around 1; saturation 0 is black and white), convert (to format). " +
        "format (png, jpg or webp) saves any edit as that kind of file.";

    public override ToolRisk Risk => ToolRisk.Risky;

    public override JsonObject Parameters => Schema(
        ("path", Str("The picture file to edit"), true),
        ("edit", Str("What to do", "crop", "resize", "rotate", "flip", "colour", "convert"), true),
        ("x", Int("crop: the box's left edge"), false),
        ("y", Int("crop: the box's top edge"), false),
        ("width", Int("crop: the box's width; resize: the new width"), false),
        ("height", Int("crop: the box's height; resize: the new height"), false),
        ("degrees", Num("rotate: degrees clockwise (90, 180, 270, or any angle)"), false),
        ("way", Str("flip: horizontal or vertical"), false),
        ("brightness", Num("colour: -1 to 1, 0 as it is"), false),
        ("contrast", Num("colour: 1 as it is"), false),
        ("saturation", Num("colour: 1 as it is, 0 black and white"), false),
        ("format", Str("The kind of file to save: png, jpg or webp (default: the picture's own)"), false));

    public override string Describe(JsonObject args)
    {
        var name = Path.GetFileName(GetString(args, "path") ?? "");
        return Kind(GetString(args, "edit")) switch
        {
            PictureEditKind.Crop => $"Crop {name}",
            PictureEditKind.Resize => $"Resize {name}",
            PictureEditKind.Rotate => string.Create(CultureInfo.InvariantCulture, $"Turn {name} {GetNumber(args, "degrees") ?? 0:0.#}°"),
            PictureEditKind.Flip => Vertical(GetString(args, "way")) ? $"Flip {name}" : $"Mirror {name}",
            PictureEditKind.Colour => $"Change the colours of {name}",
            PictureEditKind.Convert => $"Save {name} as {(GetString(args, "format") ?? "another kind of file").Trim().TrimStart('.').ToUpperInvariant()}",
            _ => $"Edit {name}",
        };
    }

    /// <summary>The edit as the model named it, forgiving of case, spaces and dashes; null for none of them.</summary>
    private static PictureEditKind? Kind(string? edit) => edit?.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "") switch
    {
        "crop" or "cut" or "trim" => PictureEditKind.Crop,
        "resize" or "scale" or "size" => PictureEditKind.Resize,
        "rotate" or "turn" => PictureEditKind.Rotate,
        "flip" or "mirror" => PictureEditKind.Flip,
        "colour" or "color" or "colours" or "colors" or "adjust" or "grayscale" or "greyscale" or "bw" => PictureEditKind.Colour,
        "convert" or "format" or "saveas" => PictureEditKind.Convert,
        _ => null,
    };

    private static bool Vertical(string? way) => way?.Trim().ToLowerInvariant() is "vertical" or "v" or "upside down" or "upsidedown" or "y";

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(RequireString(args, "path"));
        var said = RequireString(args, "edit");
        var kind = Kind(said) ?? throw new ToolArgumentException("edit is one of crop, resize, rotate, flip, colour, convert.");
        static int? Whole(double? n) => n is { } v ? (int)Math.Round(v) : null;
        // "grayscale" for the edit is its colours with none left.
        var grey = said.Trim().ToLowerInvariant() is "grayscale" or "greyscale" or "bw";
        // "mirror" says the way itself.
        var request = new PictureEditRequest(kind)
        {
            X = Whole(GetNumber(args, "x")), Y = Whole(GetNumber(args, "y")), Width = Whole(GetNumber(args, "width")), Height = Whole(GetNumber(args, "height")),
            Degrees = GetNumber(args, "degrees") ?? 0,
            Vertical = Vertical(GetString(args, "way")),
            Brightness = GetNumber(args, "brightness") ?? 0,
            Contrast = GetNumber(args, "contrast") ?? 1,
            Saturation = grey ? 0 : GetNumber(args, "saturation") ?? 1,
            Format = GetString(args, "format") is { Length: > 0 } format ? format : null,
        };
        if (kind == PictureEditKind.Convert && request.Format is null)
            throw new ToolArgumentException("convert needs format: png, jpg or webp.");
        return Task.Run(() =>
        {
            try
            {
                var edited = PictureEdit.Save(path, request, ctx.PicturesDirectory);
                var version = PictureFolder.IsVersion(edited.Path, ctx.PicturesDirectory)
                    ? $" It is the picture's newest version in the Image editor; {Path.GetFileName(path)} stays as it was." : "";
                var look = MediaPreview.ForFiles([edited.Path], ctx.Media, ctx.SeesImages);
                return new ToolResult($"{edited.Label}: saved {edited.Path}, {edited.Width}x{edited.Height} pixels ({edited.WasWidth}x{edited.WasHeight} before).{version}",
                    look.Image, ImageCaption: look.Image is null ? null : look.Caption, Files: [edited.Path]);
            }
            catch (PictureEditException ex)
            {
                return ToolResult.Error(ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ToolResult.Error($"The edit could not be saved: {ex.Message}");
            }
        }, ct);
    }
}
