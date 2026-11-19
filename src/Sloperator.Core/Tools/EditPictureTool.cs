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

    // Kept short: every word of it goes with every request.
    public override string Description =>
        "Edit a picture file exactly, with no model. The picture stays as it was: the result is a new file (output, else beside it, named after the edit; " +
        "in Sloperator's Image editor, the picture's next version). " +
        "edit: crop (keep the box x, y, width, height), resize (to width and height; one alone keeps its shape), rotate, flip, colour, " +
        "convert (to format). Several edits in one call, saved as one file: the first here, the rest in then.";

    public override ToolRisk Risk => ToolRisk.Risky;

    public override JsonObject Parameters => Schema(
        ("path", Str("The picture file"), true),
        ("edit", Str("What to do", "crop", "resize", "rotate", "flip", "colour", "convert"), true),
        ("x", Int("crop: pixels from the left"), false),
        ("y", Int("crop: pixels from the top"), false),
        ("width", Int("In pixels"), false),
        ("height", Int("In pixels"), false),
        ("degrees", Num("rotate: clockwise"), false),
        ("way", Str("flip", "horizontal", "vertical"), false),
        ("brightness", Num("colour: -1 to 1, 0 as it is"), false),
        ("contrast", Num("colour: 1 as it is"), false),
        ("saturation", Num("colour: 1 as it is, 0 black and white"), false),
        ("then", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" }, ["description"] = "More edits after this one, in order, each {edit, …}" }, false),
        ("format", Str("png, jpg or webp"), false),
        ("output", Str("Where to save the result"), false));

    public override string Describe(JsonObject args)
    {
        var name = Path.GetFileName(GetString(args, "path") ?? "");
        if (args["then"] is JsonArray { Count: > 0 })
            return $"Edit {name}";
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

    /// <summary>One edit as the model wrote it, the call's own or one of its "then".</summary>
    private static PictureEditRequest Request(JsonObject args)
    {
        var said = RequireString(args, "edit");
        var kind = Kind(said) ?? throw new ToolArgumentException("edit is one of crop, resize, rotate, flip, colour, convert.");
        static int? Whole(double? n) => n is { } v ? (int)Math.Round(v) : null;
        // "grayscale" for the edit is its colours with none left.
        var grey = said.Trim().ToLowerInvariant() is "grayscale" or "greyscale" or "bw";
        return new PictureEditRequest(kind)
        {
            X = Whole(GetNumber(args, "x")), Y = Whole(GetNumber(args, "y")), Width = Whole(GetNumber(args, "width")), Height = Whole(GetNumber(args, "height")),
            Degrees = GetNumber(args, "degrees") ?? 0,
            Vertical = Vertical(GetString(args, "way")),
            Brightness = GetNumber(args, "brightness") ?? 0,
            Contrast = GetNumber(args, "contrast") ?? 1,
            Saturation = grey ? 0 : GetNumber(args, "saturation") ?? 1,
            Format = GetString(args, "format") is { Length: > 0 } format ? format : null,
        };
    }

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(RequireString(args, "path"));
        var output = GetString(args, "output") is { Length: > 0 } o ? ctx.ResolvePath(o) : null;
        var requests = new List<PictureEditRequest> { Request(args) };
        // The edits after the first, each an object like the call itself; a model may write the list as text.
        var then = args["then"];
        if (then is JsonValue text && text.TryGetValue<string>(out var json))
        {
            try { then = JsonNode.Parse(json); }
            catch (System.Text.Json.JsonException) { throw new ToolArgumentException("then is a list of edits: [{\"edit\": \"flip\"}, {\"edit\": \"resize\", \"width\": 300}]."); }
        }
        foreach (var more in then switch { JsonArray list => list.OfType<JsonObject>(), JsonObject one => [one], _ => [] })
            requests.Add(Request(more));
        // To another kind of file: said by format, or by where it is saved.
        if (requests.Count == 1 && requests[0] is { Kind: PictureEditKind.Convert, Format: null } && (output is null || Path.GetExtension(output).Length == 0))
            throw new ToolArgumentException("convert needs format: png, jpg or webp.");
        return Task.Run(() =>
        {
            try
            {
                var edited = PictureEdit.Save(path, requests, ctx.PicturesDirectory, output);
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
