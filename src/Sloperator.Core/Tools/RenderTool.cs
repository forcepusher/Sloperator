using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Sloperator.Core.Comfy;
using Sloperator.Core.Media;
using Sloperator.Core.Render;

namespace Sloperator.Core.Tools;

/// <summary>
/// Draws an SVG (with SMIL) or a Three.js page into a picture or a clip (see <see cref="SceneRenderer"/>):
/// two of the three ways Sloperator makes pictures and video, ComfyUI being the third. The model
/// writes the scene with write_file and gets back the file, a look at it and the page's errors.
/// </summary>
public sealed class RenderTool : Tool
{
    public override string Name => "render";

    public override string Description =>
        "Draw an SVG (2D vector art, animated with SMIL) or an HTML page with a Three.js scene (3D) into a PNG picture, or into a video clip when it moves, " +
        "as Edge on this PC shows it, frame by frame at exact times. Write the scene with write_file first. " +
        "For Three.js, import 'three' and 'three/addons/' with an import map from cdn.jsdelivr.net as its docs do: Sloperator serves its own copy offline. " +
        "Animate by time (setAnimationLoop's time, performance.now, THREE.Clock, SMIL's dur and begin). " +
        "output=model.glb saves a Three.js scene as a 3D model file. " +
        "Returns the file, a look at it and the page's errors. The first call in a chat brings the guide to writing scenes; with no file it shows only the guide.";

    public override ToolRisk Risk => ToolRisk.Risky;

    public override JsonObject Parameters => Schema(
        ("file", Str("The .svg or .html scene to draw"), false),
        ("output", Str("Where the result goes: .png (or .jpg, .webp) for a picture, .mp4 (or .webm, .mov, .gif) for a clip, .glb for a Three.js scene as a 3D model. Default: beside the scene, .png when it doesn't move, else .mp4"), false),
        ("save_to", Str("Instead of output: a folder to put the result in under a new name (a picture's folder in the Image editor: its next version)"), false),
        ("seconds", Num("A clip's length; default: the SMIL or CSS animation's own, else 5"), false),
        ("fps", Int("A clip's frames a second (default 30)"), false),
        ("width", Int("Width in pixels (default: the SVG's own, at least 1024 along its longer side; 1280 for a page)"), false),
        ("height", Int("Height in pixels (default: the SVG's own; 720 for a page)"), false),
        ("at", Num("For a picture of a scene that moves: the moment to draw, in seconds"), false),
        ("transparent", Bool("Keep the background clear, as an icon or a logo wants it (PNG, WebM, MOV or GIF; an MP4 can't)"), false));

    public override string Describe(JsonObject args) =>
        GetString(args, "file") is { Length: > 0 } file
            ? $"Render {Path.GetFileName(file)}" + (GetString(args, "output") is { Length: > 0 } output ? $" to {Path.GetFileName(output)}" : "")
            : "Read the guide to drawing scenes";

    /// <summary>How the guide starts, by which a chat that has it is told.</summary>
    internal const string GuideHead = "# Drawing scenes (Sloperator's guide)";

    private int? _guideShown;

    private string? GuideFor(ToolContext ctx)
    {
        if (_guideShown == ctx.ContextResets || ctx.InConversation?.Invoke(GuideHead) == true)
            return null;
        _guideShown = ctx.ContextResets;
        return ComfyGuides.Text("scenes");
    }

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (GetString(args, "file") is not { Length: > 0 } given)
        {
            _guideShown = ctx.ContextResets;
            return new ToolResult(ComfyGuides.Text("scenes"));
        }
        var file = ctx.ResolvePath(given);
        var output = GetString(args, "output") is { Length: > 0 } o ? ctx.ResolvePath(o) : null;
        var into = GetString(args, "save_to") is { Length: > 0 } folder ? ctx.ResolvePath(folder) : null;
        // A folder given as the output is where it goes, as save_to says it.
        if (output is not null && (Directory.Exists(output) || Path.EndsInDirectorySeparator(output) || into is null && Path.GetExtension(output).Length == 0))
            (into, output) = (into ?? output, null);
        // Both given: the output's name in the folder.
        else if (output is not null && into is { } inFolder)
            (into, output) = (null, Path.Combine(inFolder, Path.GetFileName(output)));
        // A picture of the Image editor keeps its versions: drawn again over a file that is there, it is the next one.
        string? earlier = null;
        if (output is not null && File.Exists(output) && SceneRenderer.IsPicture(output) == true && InPictureFolder(output, ctx))
            (earlier, output) = (output, SceneRenderer.FreeName(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output), Path.GetExtension(output)));
        var options = new SceneOptions
        {
            Seconds = GetNumber(args, "seconds") is > 0 and var s ? s : null,
            Fps = GetNumber(args, "fps") is > 0 and var f ? (int)Math.Round(f) : 30,
            Width = GetNumber(args, "width") is >= 16 and var w ? (int)Math.Round(w) : null,
            Height = GetNumber(args, "height") is >= 16 and var h ? (int)Math.Round(h) : null,
            At = GetNumber(args, "at") is >= 0 and var at ? at : null,
            Transparent = GetBool(args, "transparent"),
            Into = into,
        };
        // A picture's next version is as big as its newest, when that one was drawn from the same kind of scene and
        // no size is given: an icon asked for at 512 came back at 1024 once its colour was changed.
        var svg = Path.GetExtension(file).Equals(".svg", StringComparison.OrdinalIgnoreCase);
        if (options is { Width: null, Height: null } && (into ?? Path.GetDirectoryName(output)) is { } pictureFolder && InPictureFolder(Path.Combine(pictureFolder, "next"), ctx)
            && PictureFolder.Versions(pictureFolder).LastOrDefault() is { } newest && SceneStamp.Of(newest) is { } before && before.Svg == svg
            && PictureFolder.SizeOf(newest) is var (nw, nh))
            // An SVG keeps its own shape (it may have changed): its longer side as before. A page fills the window it is given.
            options = svg ? nw >= nh ? options with { Width = nw } : options with { Height = nh } : options with { Width = nw, Height = nh };
        SceneMade made;
        try
        {
            made = await Renderer(ctx).RenderAsync(file, output, options, ctx.Media?.Ffmpeg, ctx.Progress, ct).ConfigureAwait(false);
        }
        catch (RenderException ex)
        {
            var error = ToolResult.Error(ex.Message);
            return GuideFor(ctx) is { } g ? error with { Text = error.Text + "\n\n" + g } : error;
        }
        var text = new StringBuilder(Said(made));
        foreach (var note in made.Notes)
            text.Append('\n').Append(note);
        if (made.Errors.Count > 0)
            text.Append("\nThe page's errors (fix them and render again):\n").AppendJoin("\n", made.Errors.Select(e => "- " + e));
        // Beside a scene written in the scratch folder, the result is out of the user's sight: said, so what they asked for goes to them.
        if (output is null && into is null && ctx.ScratchDirectory is { } scratch && Projects.FolderPath.IsIn(made.Output, Path.TrimEndingDirectorySeparator(Path.GetFullPath(scratch))))
            text.Append("\nIt is in Sloperator's scratch folder, beside the scene: when it is what the user asked for, render it again with output where it should go.");
        if (earlier is not null)
            text.Append($"\n{Path.GetFileName(earlier)} is an earlier version of this picture in the Image editor and is kept: this one is its next version.");
        if (options.Transparent && Path.GetExtension(made.Output).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            text.Append("\nAn MP4 can't keep a clear background: render to .webm or .mov for that.");
        var look = MediaPreview.ForFiles([made.Output], ctx.Media, ctx.SeesImages);
        // What the file is, without the movement measured from its pixels: made for a video model's camera, it read a
        // logo's bar growing and words fading as the camera tilting, and Haiku said its readings didn't match the frames.
        if (look.Lines.Count > 0)
            text.Append('\n').Append(look.Lines[0].Split('\n')[0].TrimEnd());
        if (GuideFor(ctx) is { } guide)
            text.Append("\n\n").Append(guide);
        return new ToolResult(text.ToString(), look.Image, ImageCaption: look.Image is null ? null : look.Caption, Files: [made.Output]);
    }

    private static string Said(SceneMade made)
    {
        var took = made.Took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        if (SceneRenderer.IsModel(made.Output))
            return $"Saved the scene as a 3D model, {made.Output}, in {took} s.";
        return made.Clip
            ? $"Made {made.Output}: a clip {made.Width}x{made.Height}, {made.Seconds.ToString("0.##", CultureInfo.InvariantCulture)} s at {made.Fps} fps ({made.Frames} frames), in {took} s."
            : $"Made {made.Output}: a picture {made.Width}x{made.Height}, in {took} s.";
    }

    /// <summary>Whether a file is right in a picture's folder of the Image editor (a folder right in Sloperator's pictures).</summary>
    private static bool InPictureFolder(string file, ToolContext ctx)
    {
        if (ctx.PicturesDirectory is not { Length: > 0 } pictures)
            return false;
        var folder = Path.GetDirectoryName(Path.GetFullPath(file));
        return folder is not null && string.Equals(Path.GetDirectoryName(folder), Path.TrimEndingDirectorySeparator(Path.GetFullPath(pictures)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The renderer whose browser profiles are in Sloperator's scratch folder.</summary>
    internal static SceneRenderer Renderer(ToolContext ctx) =>
        new(Path.Combine(ctx.ScratchDirectory ?? Path.Combine(Path.GetTempPath(), "Sloperator"), "render", "browser"));
}
