using System.Globalization;
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
        // A picture too: held as a clip, five seconds unless it says how long, for a slow zoom over it or a GIF of it.
        MediaInfo info;
        if (Storyboard.PictureExtensions.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
        {
            var (pw, ph) = PictureFolder.SizeOf(source) ?? throw new VideoException($"{Path.GetFileName(source)} isn't a picture this can read.");
            // A photo's own size is more than a video's: 1920 along its longer side at most.
            var fit = Math.Min(1.0, 1920.0 / Math.Max(pw, ph));
            info = new MediaInfo { HasVideo = true, Width = (int)Math.Round(pw * fit), Height = (int)Math.Round(ph * fit), FrameRate = 30, Duration = TimeSpan.FromSeconds(5) };
        }
        else
            info = media.Probe(source) is { HasVideo: true } read ? read : throw new VideoException($"{Path.GetFileName(source)} isn't a video this PC can read.");
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
            // "skip": "silences": the file with its silences cut out, each stretch with sound in it a shot with the same changes.
            var skips = (shotJson["skip"] ?? shotJson["cut"]) is JsonValue what && what.ToString().Contains("silen", StringComparison.OrdinalIgnoreCase);
            shotJson.Remove("skip");
            shotJson.Remove("cut");
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
            var skipped = skips ? SkipSilences(board, shot, source, media, ct) : null;
            board.Normalize();
            board.Save();
            await Maker(ctx).MakeShotAsync(board, shot, ctx.Progress, ctx.UserMessage, ct).ConfigureAwait(false);
            // Many pieces of one file are joined here where that can be done: each would be an input of ffmpeg's own.
            var ffmpeg = board.Shots.Count > 40 && VideoComposer.Joins(media) ? null : media.Ffmpeg;
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
            var notes = (skipped is null ? "" : "\n" + skipped) + (made.Notes.Count > 0 ? "\n" + string.Join("\n", made.Notes) : "");
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

// A long recording looked through for the agent, and its silences cut out.
public sealed partial class VideoEditorTool
{
    /// <summary>
    /// action=scan: what a video or sound file holds, found without watching it (<see cref="VideoCheck.Scan"/>): where it
    /// is silent and loud, where its picture cuts, stands still and moves most, and frames across it with their times.
    /// A stretch of it (storyboard {"in", "seconds"}) is scanned more closely, which is also how to see the frames around
    /// a moment of any video: asked to check two dissolves, one model went looking for an ffmpeg to cut frames with.
    /// </summary>
    private static async Task<ToolResult> ScanAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var source = GetString(args, "path") is { Length: > 0 } p ? ctx.ResolvePath(p) : throw new ToolArgumentException("action=scan: give path, the video or sound file to scan.");
        if (!File.Exists(source))
            throw new VideoException(Directory.Exists(source) ? $"{source} is a folder: action=scan looks through one video or sound file (a video's joined file is in its folder, under its name)." : $"There is no file {source}.");
        var media = ctx.Media ?? throw new VideoException("Nothing on this PC reads video.");
        var stretch = args["storyboard"] as JsonObject;
        if (stretch?["shots"] is JsonArray { Count: > 0 } shots && shots[0] is JsonObject only)
            stretch = only;
        static double Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) && number > 0 ? number : 0;
        var scanned = await Task.Run(() => VideoCheck.Scan(source, media, Number(stretch?["in"]), Number(stretch?["seconds"]), ctx.Progress, ct), ct).ConfigureAwait(false);
        var sheet = ctx.SeesImages ? scanned.Sheet : null;
        return new ToolResult(scanned.Text, sheet, ImageCaption: sheet is null ? null : "Frames across it, each with its time:");
    }

    /// <summary>
    /// The one shot a file is edited as, made a shot for each stretch of it with sound in it (of what the shot shows of
    /// the file: its "in" and "seconds" still cut it), each with the shot's other changes. What was cut out, to say with
    /// the result; the shot is left as it is when the file has no sound or no silence.
    /// </summary>
    private static string SkipSilences(Storyboard board, VideoShot shot, string source, IMediaDecoder media, CancellationToken ct)
    {
        var (from, to) = shot.Stretch;
        if (media.OpenAudio(source) is not { } opened)
            return "It has no sound to find silences in: nothing was cut out of it.";
        SoundScan heard;
        using (var sound = new MediaStretch.Sound(opened, from, to))
            heard = VideoCheck.ScanSound(sound, ct);
        if (heard.Silent)
            return "Its sound is silent all through: nothing was cut out of it.";
        if (heard.Silences.Count == 0)
            return $"No silence of {VideoCheck.LeastSilence.ToString(CultureInfo.InvariantCulture)} s or more was found in it: nothing was cut out of it.";
        var speed = shot.Speed ?? 1;
        var json = board.ToJson();
        var index = board.Shots.IndexOf(shot);
        board.Shots.Clear();
        var n = 0;
        foreach (var (a, b) in heard.Sounding)
        {
            var piece = Storyboard.Parse(json).Shots[index];
            (piece.Id, piece.In, piece.Seconds, piece.Freeze, piece.Transition, piece.TransitionSeconds) = ($"s{++n}", from + a, (b - a) / speed, null, VideoShot.Cut, 0);
            board.Shots.Add(piece);
        }
        var gone = heard.Silences.Sum(s => s.To - s.From);
        return string.Create(CultureInfo.InvariantCulture, $"Cut {heard.Silences.Count} silence{(heard.Silences.Count == 1 ? "" : "s")} out of it, {gone:0.#} s in all (below {heard.QuietDb:0} dB for {VideoCheck.LeastSilence} s or more, a little kept beside the sound at each end): {board.Shots.Count} pieces, {(heard.Seconds - gone) / speed:0.#} s.");
    }
}

// A sound's beat, told to the agent so it can cut on it.
public sealed partial class VideoEditorTool
{
    /// <summary>
    /// What the agent is told of the beat of a song or a sound file of the user's, in the video's own time: its
    /// tempo, when its first beats fall, and how long so many beats are, so clips can be made whole beats long.
    /// Null for a sound with no steady beat, or one not read.
    /// </summary>
    internal static string? BeatLine(Storyboard board, VideoSound sound, IMediaDecoder? decoder)
    {
        if (decoder is null || sound.Source is VideoSound.Speech or VideoSound.Foley || SoundBeats.Of(board.SoundFile(sound), decoder) is not { } grid)
            return null;
        var offset = sound.Start - sound.Trim;
        var beats = grid.Times.Select(t => t + offset).Where(t => t >= sound.Start - 1e-6).ToList();
        if (beats.Count < 4)
            return null;
        static string S(double seconds) => seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return $"Its beat: {grid.Bpm:0.#} BPM, a beat every {grid.Interval.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} s. " +
               $"In the video its beats fall at {string.Join(", ", beats.Take(8).Select(S))} s and so on to {S(beats[^1])} s. " +
               $"To cut on the beat, start the first clip's cut on a beat and make each clip a whole number of beats long: 4 beats are {S(grid.Interval * 4)} s, 8 are {S(grid.Interval * 8)} s " +
               "(with a transition into a clip, add its length to the clip before, as the two overlap).";
    }

    /// <summary>action=check with a sound's id: where it is, what it holds, and its beat when it is music.</summary>
    private static ToolResult CheckSound(Storyboard board, VideoSound sound, ToolContext ctx)
    {
        var file = board.SoundFile(sound);
        if (!File.Exists(file))
            return new ToolResult(sound.Source == VideoSound.FromFile ? $"Sound {sound.Id} is the file {file}, which isn't there." : $"Sound {sound.Id} isn't made yet: action=sound with its id makes it.");
        var look = MediaPreview.ForFiles([file], ctx.Media, ctx.SeesImages);
        var beat = BeatLine(board, sound, ctx.Media) ?? (sound.Source is VideoSound.Song or VideoSound.FromFile ? "No steady beat was found in it." : null);
        return new ToolResult($"Sound {sound.Id} \"{sound.Label}\": {file}\n{string.Join("\n", look.Lines)}" + (beat is null ? "" : "\n" + beat), look.Image,
            ImageCaption: look.Image is null ? null : look.Caption, Files: [file]);
    }
}

// A sound file's beat before there is any video for it: a model asked to cut a video on a song's beat looks for the
// beat first, and Qwen 3.8, finding no way to ask, searched the PC for ffprobe and then wrote a beat finder in PowerShell.
public sealed partial class VideoEditorTool
{
    private static readonly string[] SoundKinds = [".wav", ".mp3", ".flac", ".m4a", ".ogg", ".aac", ".wma", ".opus"];

    /// <summary>The path when it is a sound file that is there; null for a video's folder, a name, or any other file.</summary>
    private static string? SoundFileAt(string? path, ToolContext ctx)
    {
        if (path is not { Length: > 0 })
            return null;
        var full = ctx.ResolvePath(path);
        return SoundKinds.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    /// <summary>action=check with a sound file as its path: what the file holds and its beat, in its own time.</summary>
    private static ToolResult CheckSoundFile(string file, ToolContext ctx)
    {
        var look = MediaPreview.ForFiles([file], ctx.Media, ctx.SeesImages);
        static string S(double seconds) => seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var beat = ctx.Media is { } decoder && SoundBeats.Of(file, decoder) is { } grid
            ? $"Its beat: {grid.Bpm:0.#} BPM, a beat every {S(grid.Interval)} s, the first {S(grid.First)} s into it (then {string.Join(", ", grid.Times.Skip(1).Take(7).Select(S))} s and so on to {S(grid.Times[^1])} s). " +
              $"To cut a video on it, start each clip on a beat and make it a whole number of beats long: 4 beats are {S(grid.Interval * 4)} s, 8 are {S(grid.Interval * 8)} s. " +
              "These are times in the file: in a video add where the sound starts there, less what is trimmed off its start."
            : "No steady beat was found in it.";
        return new ToolResult($"{file}\n{string.Join("\n", look.Lines)}\n{beat}", look.Image, ImageCaption: look.Image is null ? null : look.Caption);
    }
}
