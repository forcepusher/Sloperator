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
        ("seconds", Num("A clip's length; default: the SMIL or CSS animation's own, else 5"), false),
        ("fps", Int("A clip's frames a second (default 30)"), false),
        ("width", Int("Width in pixels (default: the SVG's own, at least 1024 along its longer side; 1280 for a page)"), false),
        ("height", Int("Height in pixels (default: the SVG's own; 720 for a page)"), false),
        ("at", Num("For a picture of a scene that moves: the moment to draw, in seconds"), false),
        ("transparent", Bool("Keep the background clear (PNG, WebM, MOV or GIF; an MP4 can't)"), false));

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
        var options = new SceneOptions
        {
            Seconds = GetNumber(args, "seconds") is > 0 and var s ? s : null,
            Fps = GetNumber(args, "fps") is > 0 and var f ? (int)Math.Round(f) : 30,
            Width = GetNumber(args, "width") is >= 16 and var w ? (int)Math.Round(w) : null,
            Height = GetNumber(args, "height") is >= 16 and var h ? (int)Math.Round(h) : null,
            At = GetNumber(args, "at") is >= 0 and var at ? at : null,
            Transparent = GetBool(args, "transparent"),
        };
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
        if (options.Transparent && Path.GetExtension(made.Output).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            text.Append("\nAn MP4 can't keep a clear background: render to .webm or .mov for that.");
        var look = MediaPreview.ForFiles([made.Output], ctx.Media, ctx.SeesImages);
        if (look.Lines.Count > 0)
            text.Append('\n').Append(look.Lines[0]);
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

    /// <summary>The renderer whose browser profiles are in Sloperator's scratch folder.</summary>
    internal static SceneRenderer Renderer(ToolContext ctx) =>
        new(Path.Combine(ctx.ScratchDirectory ?? Path.Combine(Path.GetTempPath(), "Sloperator"), "render", "browser"));
}
