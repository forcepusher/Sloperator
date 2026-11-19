using System.Diagnostics;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using SkiaSharp;

namespace Sloperator.Core.Video;

/// <summary>
/// Joins the video without ffmpeg: every frame drawn by <see cref="VideoPreviewer"/> at the video's own size (what
/// the Video editor's viewer shows under the playhead, so the file is what was seen there), its sound mixed by
/// <see cref="VideoSoundMix"/>, and both written with the OS's own encoder (<see cref="IMediaWriter"/>). A video is
/// joined on a PC that has only Sloperator; before, joining stopped with "needs ffmpeg" until someone downloaded it.
/// </summary>
public static class VideoComposer
{
    /// <summary>Whether a video can be joined here: there is something that writes video files.</summary>
    public static bool Joins(IMediaDecoder? decoder) => decoder?.Writes == true;

    /// <summary>Joins the video into its file next to the storyboard; returns where it is and what to know.</summary>
    /// <param name="scenes">Draws the overlays that are scenes; null where none can be drawn.</param>
    public static async Task<VideoMade> JoinAsync(Storyboard board, IMediaDecoder decoder, Action<string>? progress, CancellationToken ct, Render.SceneRenderer? scenes = null)
    {
        var notes = new List<string>();
        var (_, length) = board.Timing();
        await DrawScenesAsync(board, scenes, notes, progress, ct).ConfigureAwait(false);
        return await Task.Run(() => Join(board, decoder, notes, progress, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every scene laid over the video drawn as frames where it isn't yet or has changed; returns how many were drawn.
    /// Joining does it first; the Video editor does it when the playhead is moved, so its viewer shows an animated
    /// title before the video is ever made.
    /// </summary>
    public static async Task<int> DrawScenesAsync(Storyboard board, Render.SceneRenderer? scenes, List<string> notes, Action<string>? progress, CancellationToken ct)
    {
        var (_, length) = board.Timing();
        var drawn = 0;
        foreach (var o in board.Overlays.Where(o => o.Scene is not null))
            if (await DrawSceneAsync(board, o, length, scenes, notes, progress, ct).ConfigureAwait(false))
                drawn++;
        return drawn;
    }

    /// <summary>
    /// A scene laid over the video, drawn as a picture a frame at the size of its box (<see cref="VideoPreviewer.SceneFrames"/>),
    /// its background clear: once for the scene as it is, and again only when the scene, its box or its length changed.
    /// </summary>
    private static async Task<bool> DrawSceneAsync(Storyboard board, VideoOverlay o, double length, Render.SceneRenderer? scenes, List<string> notes, Action<string>? progress, CancellationToken ct)
    {
        var start = Math.Min(o.Start, Math.Max(0, length - 0.1));
        var seconds = o.Seconds > 0 ? Math.Min(o.Seconds, length - start) : length - start;
        if (seconds <= 0.01)
            return false;
        if (board.FindFile(o.Scene!) is not { } scene)
            throw new VideoException($"Overlay {o.Id} shows the scene {o.Scene}, which isn't in References or there.");
        if (scenes is null)
            throw new VideoException("Scenes can't be drawn here.");
        var (box, _, _, _) = VideoOverlays.Place(o.Position, false);
        box = o.Box ?? box;
        var (bw, bh) = (Math.Max(2, (int)Math.Round(box.Width * board.Width)), Math.Max(2, (int)Math.Round(box.Height * board.Height)));
        var folder = VideoPreviewer.SceneFrames(board, o);
        var stampFile = Path.Combine(folder, "drawn.txt");
        var stamp = FormattableString.Invariant($"{scene}|{File.GetLastWriteTimeUtc(scene).Ticks}|{bw}x{bh}|{board.Fps}|{seconds:0.###}");
        if (File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp)
            return false;
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        progress?.Invoke($"Drawing the scene {Path.GetFileName(scene)}…");
        var made = await scenes.RenderAsync(scene, folder, new Render.SceneOptions { Width = bw + bw % 2, Height = bh + bh % 2, Fps = board.Fps, Seconds = seconds, Transparent = true, Frames = true },
            null, progress, ct).ConfigureAwait(false);
        notes.AddRange(made.Errors.Select(e => $"Overlay {o.Id}'s scene: {e}"));
        File.WriteAllText(stampFile, stamp);
        return true;
    }

    private static VideoMade Join(Storyboard board, IMediaDecoder decoder, List<string> notes, Action<string>? progress, CancellationToken ct)
    {
        if (board.Shots.Count == 0)
            throw new VideoException("The video has no shots to join.");
        var missing = board.Shots.Where(s => !File.Exists(board.ShotVideo(s))).Select(s => s.Id).ToList();
        if (missing.Count > 0)
            throw new VideoException($"These shots have no clip yet: {string.Join(", ", missing)}. Make them first.");
        var clock = Stopwatch.StartNew();
        var (_, length) = board.Timing();
        var (width, height, fps) = (board.Width, board.Height, Math.Max(1, board.Fps));
        var frames = Math.Max(1, (int)Math.Round(length * fps));
        var output = board.VideoFile;
        var temp = Path.Combine(board.Folder, "." + Path.GetFileNameWithoutExtension(output) + ".joining.mp4");

        progress?.Invoke("Mixing the sound…");
        var sound = VideoSoundMix.Mix(board, decoder, notes);
        ct.ThrowIfCancellationRequested();

        try
        {
            using var previewer = new VideoPreviewer(decoder, Math.Max(width, height));
            using (var writer = decoder.Write(temp, new MediaWriterOptions(width, height, fps, Sound: true, board.Quality))
                                ?? throw new VideoException($"The video can't be written on this PC: Windows gave no H.264 encoder for {width}x{height} at {fps} frames a second."))
            {
                var black = new RawImage(width, height, Opaque(width, height));
                var unread = 0;
                long heard = 0;
                for (var f = 0; f < frames; f++)
                {
                    ct.ThrowIfCancellationRequested();
                    var frame = previewer.FrameAt(board, f / (double)fps);
                    if (frame is null || frame.Width != width || frame.Height != height)
                    {
                        unread++;
                        frame = black;
                    }
                    writer.Frame(frame);
                    // The sound up to the end of this frame, so the two go into the file side by side.
                    var upTo = Math.Min(sound.Length / VideoSoundMix.Channels, (long)Math.Round((f + 1) * (double)VideoSoundMix.Rate / fps));
                    if (f == frames - 1)
                        upTo = sound.Length / VideoSoundMix.Channels;
                    if (upTo > heard)
                        writer.Sound(sound.AsSpan((int)heard * VideoSoundMix.Channels, (int)(upTo - heard) * VideoSoundMix.Channels));
                    heard = upTo;
                    if (f % 10 == 0 || f == frames - 1)
                        progress?.Invoke($"Joining the shots: frame {f + 1} of {frames}" + (f > 20 ? $" · {Left(clock.Elapsed, f + 1, frames)} left" : ""));
                }
                writer.Finish();
                if (unread > 0)
                    notes.Add($"{unread} of the video's {frames} frames couldn't be read from their clips and are black.");
            }
            MediaRelease.Release(output); // the one made before may be playing in the chat
            File.Move(temp, output, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return new VideoMade(output, null, null, clock.Elapsed, notes);
    }

    private static byte[] Opaque(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 3; i < bgra.Length; i += 4)
            bgra[i] = 255;
        return bgra;
    }

    private static string Left(TimeSpan took, int done, int all)
    {
        var left = TimeSpan.FromSeconds(took.TotalSeconds / Math.Max(1, done) * (all - done));
        return left.TotalMinutes >= 1 ? $"{(int)left.TotalMinutes} min {left.Seconds} s" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalSeconds))} s";
    }

    // ---- clips and exports without ffmpeg --------------------------------------------------------------------------

    /// <summary>A picture drawn to fill a frame of the size given (its middle kept when its shape differs), as BGRA.</summary>
    private static RawImage Filling(SKBitmap picture, int width, int height)
    {
        using var frame = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(frame))
        {
            canvas.Clear(SKColors.Black);
            var scale = Math.Max(width / (float)picture.Width, height / (float)picture.Height);
            var (w, h) = (picture.Width * scale, picture.Height * scale);
            using var image = SKImage.FromBitmap(picture);
            canvas.DrawImage(image, SKRect.Create((width - w) / 2, (height - h) / 2, w, h),
                picture.Width == width && picture.Height == height ? new SKSamplingOptions(SKFilterMode.Nearest) : Sampling.High(Math.Max(picture.Width, picture.Height), Math.Max(w, h)));
        }
        var bgra = new byte[width * height * 4];
        System.Runtime.InteropServices.Marshal.Copy(frame.GetPixels(), bgra, 0, bgra.Length);
        return new RawImage(width, height, bgra);
    }

    /// <summary>
    /// A picture held as a clip: the picture filling the frame (cut to its shape) for <paramref name="seconds"/>, silent.
    /// What a shot made of a picture of the user's is, where there is no ffmpeg to make it.
    /// </summary>
    public static void Hold(string picture, string output, int width, int height, int fps, double seconds, IMediaDecoder decoder)
    {
        using var decoded = SvgFile.Handles(picture)
            ? (SvgFile.Open(picture) ?? throw new VideoException($"{Path.GetFileName(picture)} draws nothing.")).RenderAtLeast(Math.Max(width, height))
            : PictureFile.Decode(picture) ?? throw new VideoException($"{Path.GetFileName(picture)} isn't a picture this can read.");
        var frame = Filling(decoded, width, height);
        using var writer = decoder.Write(output, new MediaWriterOptions(width, height, fps)) ?? throw new VideoException("The clip can't be written on this PC: Windows gave no H.264 encoder.");
        for (var f = 0; f < Math.Max(1, (int)Math.Round(seconds * fps)); f++)
            writer.Frame(frame);
        writer.Finish();
    }

    /// <summary>The size a video of <paramref name="from"/> is written at: as asked (one side given: the other in proportion), in whole pairs of pixels.</summary>
    internal static (int Width, int Height) SizeFor((int Width, int Height) from, int? width, int? height)
    {
        static int Even(double n) => Math.Max(2, (int)Math.Round(n / 2) * 2);
        return (width, height) switch
        {
            ( > 0, > 0) => (Even(width.Value), Even(height.Value)),
            ( > 0, _) => (Even(width.Value), Even(width.Value * (double)from.Height / from.Width)),
            (_, > 0) => (Even(height.Value * (double)from.Width / from.Height), Even(height.Value)),
            _ => (Math.Max(2, from.Width / 2 * 2), Math.Max(2, from.Height / 2 * 2)),
        };
    }

    /// <summary>
    /// A video read with what the PC has and handed on frame by frame at a steady rate: each frame as long as it shows,
    /// filling the size asked for. <paramref name="take"/> gets each frame; returns how many there were.
    /// </summary>
    private static int Frames(IVideoReader reader, int fps, int width, int height, Action<string>? progress, string doing, CancellationToken ct, Action<RawImage> take)
    {
        // A file that doesn't say how long it is (Windows gives none for some) is read to its end.
        var known = reader.Duration.TotalSeconds > 0;
        var frames = known ? Math.Max(1, (int)Math.Round(reader.Duration.TotalSeconds * fps)) : int.MaxValue;
        var (showing, next) = (reader.Next(), reader.Next());
        RawImage? fitted = null;
        var written = 0;
        for (var k = 0; k < frames && showing is not null; k++)
        {
            ct.ThrowIfCancellationRequested();
            var moved = fitted is null;
            while (next is { } n && n.Time.TotalSeconds <= k / (double)fps + 1e-4)
                (showing, next, moved) = (next, reader.Next(), true);
            if (!known && next is null && k / (double)fps > showing.Value.Time.TotalSeconds + 1.0 / fps)
                break;
            if (moved)
            {
                var raw = showing.Value.Frame;
                if (raw.Width == width && raw.Height == height)
                    fitted = raw;
                else
                {
                    using var bitmap = MediaPreview.ToBitmap(raw);
                    fitted = Filling(bitmap, width, height);
                }
            }
            take(fitted!);
            written++;
            if (k % 15 == 0 || k == frames - 1)
                progress?.Invoke(known ? $"{doing}: frame {k + 1} of {frames}" : $"{doing}: frame {k + 1}");
        }
        return written;
    }

    /// <summary>
    /// A video made an MP4 (H.264 with AAC sound) with what the PC has: a MOV, a WMV or an AVI of the user's as a shot's
    /// clip, or the joined video at another size. Its own frame rate and size unless a size is given.
    /// </summary>
    public static void Transcode(string source, string output, IMediaDecoder decoder, Action<string>? progress, CancellationToken ct, int? width = null, int? height = null, string doing = "Reading the video")
    {
        using var reader = decoder.OpenVideo(source, 0) ?? throw new VideoException($"{Path.GetFileName(source)} can't be read on this PC without ffmpeg.");
        var fps = Math.Clamp((int)Math.Round(reader.FrameRate > 0 ? reader.FrameRate : 30), 1, 120);
        var (w, h) = SizeFor((reader.Width, reader.Height), width, height);
        float[]? sound = null;
        try
        {
            using var audio = decoder.OpenAudio(source);
            if (audio is not null)
                sound = SoundEdit.Convert(SoundEdit.Read(audio), VideoSoundMix.Rate, VideoSoundMix.Channels).Samples;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException) { }
        using var writer = decoder.Write(output, new MediaWriterOptions(w, h, fps, Sound: sound is not null)) ?? throw new VideoException("The video can't be written on this PC: Windows gave no H.264 encoder.");
        long heard = 0;
        var k = 0;
        Frames(reader, fps, w, h, progress, doing, ct, frame =>
        {
            writer.Frame(frame);
            k++;
            if (sound is null)
                return;
            var upTo = Math.Min(sound.Length / VideoSoundMix.Channels, (long)Math.Round(k * (double)VideoSoundMix.Rate / fps));
            if (upTo > heard)
                writer.Sound(sound.AsSpan((int)heard * VideoSoundMix.Channels, (int)(upTo - heard) * VideoSoundMix.Channels));
            heard = upTo;
        });
        if (sound is not null && heard < sound.Length / VideoSoundMix.Channels)
            writer.Sound(sound.AsSpan((int)heard * VideoSoundMix.Channels));
        writer.Finish();
    }

    /// <summary>
    /// The joined video as another file without ffmpeg: an MP4 at another size, a GIF (15 frames a second at most, 640
    /// wide unless a size is given), or its sound alone as a WAV. Other kinds (.webm, .mov, .mp3, .m4a) need ffmpeg.
    /// </summary>
    public static Task<string> ExportAsync(Storyboard board, IMediaDecoder decoder, string output, int? width, int? height, Action<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var source = board.JoinedFile;
        if (!File.Exists(source))
            throw new VideoException("The video isn't joined yet: join it first (action=join), then export it.");
        output = Path.GetFullPath(output);
        var ext = Path.GetExtension(output).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileNameWithoutExtension(output) + ".exporting" + ext);
        try
        {
            switch (ext)
            {
                case ".mp4" or ".m4v" when width is not > 0 && height is not > 0:
                    File.Copy(source, temp, overwrite: true);
                    break;
                case ".mp4" or ".m4v":
                    Transcode(source, temp, decoder, progress, ct, width, height, "Exporting");
                    break;
                case ".gif":
                {
                    using var reader = decoder.OpenVideo(source, 0) ?? throw new VideoException("The joined video can't be read on this PC.");
                    var fps = Math.Clamp(Math.Min(board.Fps, 15), 1, 50);
                    var (w, h) = width is > 0 || height is > 0 ? SizeFor((reader.Width, reader.Height), width, height)
                        : SizeFor((reader.Width, reader.Height), reader.Width > 640 ? 640 : null, null);
                    using var gif = new GifWriter(temp, w, h, fps);
                    Frames(reader, fps, w, h, progress, "Exporting", ct, gif.Frame);
                    gif.Finish();
                    break;
                }
                case ".wav":
                {
                    using var audio = decoder.OpenAudio(source) ?? throw new VideoException("The joined video has no sound to export.");
                    SoundEdit.WriteWav(temp, SoundEdit.Read(audio));
                    break;
                }
                case ".webm" or ".mov" or ".mp3" or ".m4a":
                    throw new VideoException($"Exporting a {ext} needs ffmpeg, and there is none on this PC: without it the video exports as .mp4 (at any size), .gif, or its sound as .wav. " +
                                             "For the rest, unpack a portable ffmpeg into Sloperator's SloperatorData\\tools.");
                default:
                    throw new VideoException("Export to .mp4, .webm, .mov, .gif, or a sound alone to .mp3, .wav or .m4a.");
            }
            MediaRelease.Release(output);
            File.Move(temp, output, overwrite: true);
            return output;
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }, ct);
}
