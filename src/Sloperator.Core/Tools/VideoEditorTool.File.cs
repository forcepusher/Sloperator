using System.Text.Json.Nodes;
using Sloperator.Core.Media;
using Sloperator.Core.Video;

namespace Sloperator.Core.Tools;

// One video file changed in one call, without a video of its own in the Video editor: "trim this clip", "make a
// GIF of it", "turn it upright", "half the size". Before, that took a storyboard, a shot, a join and an export
// (four calls and a video folder the user never asked for), or ffmpeg in a shell on a PC that may have none.
public sealed partial class VideoEditorTool
{
    private static readonly string[] EditOutputs = [".mp4", ".m4v", ".gif", ".webm", ".mov", ".wav", ".mp3", ".m4a"];

    /// <summary>
    /// The file edited as one shot of a video made for it in the scratch folder and joined as any video is, so a
    /// file is cut, turned, sped up or coloured exactly as a clip in a video is; then saved where it was asked for.
    /// </summary>
    private async Task<ToolResult> EditFileAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var source = GetString(args, "path") is { Length: > 0 } p ? ctx.ResolvePath(p) : throw new ToolArgumentException("action=edit: give path, the video file to change.");
        if (!File.Exists(source))
            throw new VideoException(Directory.Exists(source) ? $"{source} is a folder: action=edit changes one video file. A video in the Video editor is changed with action=storyboard." : $"There is no file {source}.");
        var media = ctx.Media ?? throw new VideoException("Nothing on this PC reads video.");
        if (media.Probe(source) is not { HasVideo: true } info)
            throw new VideoException($"{Path.GetFileName(source)} isn't a video this PC can read.");
        // What to do to it: a shot's fields, given as they are or as a storyboard with one shot.
        var change = args["storyboard"] as JsonObject ?? [];
        if (change["shots"] is JsonArray { Count: > 0 } shots && shots[0] is JsonObject only)
            change = only;
        var output = GetString(args, "output") is { Length: > 0 } o ? ctx.ResolvePath(o)
            : Render.SceneRenderer.FreeName(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + " edited", ".mp4");
        var ext = Path.GetExtension(output).ToLowerInvariant();
        if (!EditOutputs.Contains(ext))
            throw new ToolArgumentException("output must be a .mp4, .gif, .webm or .mov, or .wav, .mp3 or .m4a for the sound alone.");
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            throw new ToolArgumentException("output is the file itself: give another name, so the file you were given stays as it was.");
        var width = GetNumber(args, "width") is > 0 and var w ? (int?)w : null;
        var height = GetNumber(args, "height") is > 0 and var h ? (int?)h : null;

        var work = Path.Combine(ctx.ScratchDirectory ?? Path.GetTempPath(), "video-edit", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var shotJson = (JsonObject)change.DeepClone();
            (shotJson["id"], shotJson["file"]) = ("s1", source);
            var board = Storyboard.Merge(new Storyboard { Title = "edit", Folder = work }, new JsonObject { ["fadeIn"] = 0, ["fadeOut"] = 0, ["shots"] = new JsonArray(shotJson) });
            board.Folder = work;
            var shot = board.Shots.FirstOrDefault(s => s.Id == "s1") ?? throw new VideoException("The change can't be read as a shot's fields.");
            board.Shots.RemoveAll(s => s != shot);
            board.Frames.Clear();
            // As long as what is left of the file from its trim, at its speed, unless it says how long.
            if (shotJson["seconds"] is null)
                shot.Seconds = Math.Max(0.1, Math.Round((info.Duration.TotalSeconds - shot.In) / (shot.Speed ?? 1), 3));
            // Its own size (turned when it is turned, the shape of what is cut out of it) and frame rate, unless a size is given.
            var (sw, sh) = shot.Rotate is 90 or 270 ? ((double)info.Height, (double)info.Width) : (info.Width, info.Height);
            if (shot.Crop is { } crop)
                (sw, sh) = (sw * crop.Width, sh * crop.Height);
            (board.Width, board.Height) = VideoComposer.SizeFor(((int)Math.Round(sw), (int)Math.Round(sh)), width, height);
            board.Fps = Math.Clamp((int)Math.Round(info.FrameRate > 0 ? info.FrameRate : 30), 1, 60);
            board.Normalize();
            board.Save();
            await Maker(ctx).MakeShotAsync(board, shot, ctx.Progress, ctx.UserMessage, ct).ConfigureAwait(false);
            var ffmpeg = media.Ffmpeg;
            var made = await VideoMontage.JoinAsync(board, ffmpeg, media, ctx.Progress, ct, RenderTool.Renderer(ctx)).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (ext is ".mp4" or ".m4v")
            {
                MediaRelease.Release(output);
                File.Move(made.File, output, overwrite: true);
            }
            else if (ffmpeg is not null)
                await VideoMontage.ExportAsync(board, ffmpeg, output, null, null, ctx.Progress, ct).ConfigureAwait(false);
            else
                await VideoComposer.ExportAsync(board, media, output, null, null, ctx.Progress, ct).ConfigureAwait(false);
            var look = MediaPreview.ForFiles([output], ctx.Media, ctx.SeesImages);
            var notes = made.Notes.Count > 0 ? "\n" + string.Join("\n", made.Notes) : "";
            return new ToolResult($"Saved {output} ({Path.GetFileName(source)} is as it was).\n{string.Join("\n", look.Lines)}{notes}", look.Image, ImageCaption: look.Image is null ? null : look.Caption, Files: [output]);
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
