using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sloperator.Core.Media;

namespace Sloperator.Core.Video;

/// <summary>
/// Joins a video's shots into one video with ffmpeg: each clip cut as its shot says (its start trimmed,
/// sped up or slowed down, reversed, frozen, cropped, zoomed, coloured), scaled to the video's frame and
/// frame rate, one after another with the storyboard's transitions, its overlays (titles, captions,
/// pictures, scenes) laid over it, the sounds of its sound lines over the shots' own sound where they
/// start, faded in and out.
/// </summary>
public static class VideoMontage
{
    /// <summary>How the video begins and ends unless it says: a short fade in from black, a longer one out.</summary>
    internal const double FadeIn = 0.4, FadeOut = 1.0;

    /// <summary>How long the video fades in and out over, as it says or as usual.</summary>
    public static (double In, double Out) Fades(Storyboard board) => (board.FadeIn ?? FadeIn, board.FadeOut ?? FadeOut);

    /// <summary>The ffmpeg command line that joins the video, and how long the video is.</summary>
    /// <param name="hasSound">Whether a file has a sound track (shots made without one get silence).</param>
    /// <param name="overlays">What is laid over the joined video (<see cref="VideoOverlays.PrepareAsync"/>).</param>
    public static (List<string> Arguments, double Length, List<string> Notes) Plan(Storyboard board, Func<string, bool> hasSound, string output, IReadOnlyList<OverlayInput>? overlays = null)
    {
        if (board.Shots.Count == 0)
            throw new VideoException("The video has no shots to join.");
        var missing = board.Shots.Where(s => !File.Exists(board.ShotVideo(s))).Select(s => s.Id).ToList();
        if (missing.Count > 0)
            throw new VideoException($"These shots have no clip yet: {string.Join(", ", missing)}. Make them first.");
        var notes = new List<string>();
        var (width, height, fps) = (board.Width, board.Height, board.Fps);
        var sounds = new List<VideoSound>();
        foreach (var sound in board.Sounds)
            if (File.Exists(board.SoundFile(sound)))
                sounds.Add(sound);
            else
                notes.Add(sound.Source == VideoSound.FromFile ? $"The sound file {board.SoundFile(sound)} isn't there, so the video is without it." : $"The {(sound.Source == VideoSound.Foley ? "foley" : "song")} {sound.Label} isn't made yet, so the video is without it.");
        // Under a song the shots' own sound is kept at half: LTX makes a shot's sounds well and
        // its music badly (the user's finding), so a shot is asked for its sounds and no music
        // (VideoMaker.WithoutMusic) and the music is the song. Asked for nothing, LTX's sound was a
        // muddy wash that took the Paper Boat song from 3.1 to 2.7 on SongEval at 30%.
        var shotVolume = sounds.Any(s => s.Source == VideoSound.Song) ? 0.5 : 1.0;
        var args = new List<string> { "-hide_banner", "-y", "-nostats", "-progress", "pipe:1" };
        foreach (var shot in board.Shots)
            args.AddRange(["-i", board.ShotVideo(shot)]);
        foreach (var sound in sounds)
            args.AddRange(["-i", board.SoundFile(sound)]);
        // A shot's line is heard as the voice model said it, clean, not as the video model gave it back.
        var lines = new Dictionary<int, int>();
        var next = board.Shots.Count + sounds.Count;
        for (var i = 0; i < board.Shots.Count; i++)
            if (Storyboard.Speaks(board.Shots[i]) && File.Exists(board.ShotAudio(board.Shots[i])))
            {
                args.AddRange(["-i", board.ShotAudio(board.Shots[i])]);
                lines[i] = next++;
            }
        var overlayAt = new List<int>();
        foreach (var overlay in overlays ?? [])
        {
            if (overlay.Still)
                args.AddRange(["-loop", "1", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-t", N(overlay.Seconds), "-i", overlay.File]);
            else
            {
                // A clip with a clear background keeps it only through libvpx's own decoder.
                if (Path.GetExtension(overlay.File).Equals(".webm", StringComparison.OrdinalIgnoreCase))
                    args.AddRange(["-c:v", "libvpx-vp9"]);
                args.AddRange(["-stream_loop", "-1", "-t", N(overlay.Seconds), "-i", overlay.File]);
            }
            overlayAt.Add(next++);
        }

        var f = new StringBuilder();
        for (var i = 0; i < board.Shots.Count; i++)
        {
            var shot = board.Shots[i];
            var s = shot.Seconds;
            f.Append($"[{i}:v]").Append(ShotPicture(shot, width, height, fps)).Append($"[v{i}];");
            if (lines.TryGetValue(i, out var line))
            {
                f.Append($"[{line}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume={N(shot.Volume ?? 1)},apad=whole_dur={N(s)},atrim=duration={N(s)},asetpts=PTS-STARTPTS[a{i}];");
                continue;
            }
            if (hasSound(board.ShotVideo(shot)) && (shot.Volume ?? 1) > 0)
                f.Append($"[{i}:a]").Append(ShotSound(shot, shotVolume)).Append($"[a{i}];");
            else
                f.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={N(s)},aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
        }
        // One after another: a cut joins them, any other transition lays the next over the end of the one before.
        var (video, audio, length) = ("v0", "a0", board.Shots[0].Seconds);
        for (var i = 1; i < board.Shots.Count; i++)
        {
            var shot = board.Shots[i];
            var overlap = Storyboard.Overlap(shot);
            if (overlap <= 0)
            {
                f.Append($"[{video}][v{i}]concat=n=2:v=1:a=0[vj{i}];[{audio}][a{i}]concat=n=2:v=0:a=1[aj{i}];");
                length += shot.Seconds;
            }
            else
            {
                f.Append($"[{video}][v{i}]xfade=transition={XfadeName(shot.Transition)}:duration={N(overlap)}:offset={N(length - overlap)}[vj{i}];")
                 .Append($"[{audio}][a{i}]acrossfade=d={N(overlap)}[aj{i}];");
                length += shot.Seconds - overlap;
            }
            (video, audio) = ($"vj{i}", $"aj{i}");
        }
        // The overlays, each from where it starts, over what is under it then.
        var k = 0;
        foreach (var overlay in overlays ?? [])
        {
            var input = overlayAt[k];
            var end = overlay.Start + overlay.Seconds;
            f.Append($"[{input}:v]");
            if (!overlay.Framed)
                f.Append($"scale={overlay.Width}:{overlay.Height}:force_original_aspect_ratio=decrease,");
            // A green screen (or any colour) taken out, so what is behind shows there.
            if (overlay.Key is { } key)
                f.Append($"chromakey={key}:0.15:0.08,");
            f.Append("format=rgba");
            if (overlay.FadeIn > 0)
                f.Append($",fade=t=in:st=0:d={N(overlay.FadeIn)}:alpha=1");
            if (overlay.FadeOut > 0)
                f.Append($",fade=t=out:st={N(overlay.Seconds - overlay.FadeOut)}:d={N(overlay.FadeOut)}:alpha=1");
            if (overlay.Opacity < 0.999)
                f.Append($",colorchannelmixer=aa={N(overlay.Opacity)}");
            f.Append($",setpts=PTS-STARTPTS+{N(overlay.Start)}/TB[o{k}];");
            var (x, y) = overlay.Framed
                ? ("0", "0")
                : ($"'{overlay.X}+({overlay.Width}-overlay_w)*{N(overlay.AlignX)}'", $"'{overlay.Y}+({overlay.Height}-overlay_h)*{N(overlay.AlignY)}'");
            f.Append($"[{video}][o{k}]overlay=x={x}:y={y}:eof_action=pass:enable='between(t,{N(overlay.Start)},{N(end)})'[ov{k}];");
            video = $"ov{k}";
            k++;
        }
        var (fadeIn, fadeOut) = Fades(board);
        fadeOut = Math.Min(fadeOut, length / 3);
        var fades = new List<string>();
        if (fadeIn > 0)
            fades.Add($"fade=t=in:d={N(Math.Min(fadeIn, length / 4))}");
        if (fadeOut > 0)
            fades.Add($"fade=t=out:st={N(length - fadeOut)}:d={N(fadeOut)}");
        f.Append($"[{video}]{(fades.Count == 0 ? "null" : string.Join(",", fades))}[vout];");
        if (sounds.Count > 0)
        {
            // Each sound as its clip on the sound line is: its start cut off (trim), as long as it plays,
            // from where it starts in the video, at its volume, faded in and out as it says; quieter
            // while someone speaks, so the line is heard over it.
            var (starts, _) = board.Timing();
            var speaking = lines.Keys.Select(i => $"between(t,{N(starts[i])},{N(starts[i] + board.Shots[i].Seconds)})").ToList();
            var duck = speaking.Count == 0 ? "" : $",volume='if({string.Join("+", speaking)},0.35,1)':eval=frame";
            var mix = new StringBuilder($"[{audio}]");
            for (var j = 0; j < sounds.Count; j++)
            {
                var sound = sounds[j];
                var cut = sound.Seconds > 0 ? $",atrim=duration={N(sound.Seconds)}" : "";
                var delay = (long)Math.Round(sound.Start * 1000);
                var plays = sound.Seconds > 0 ? sound.Seconds : Math.Max(0.1, length - sound.Start);
                var soundFades = (sound.FadeIn > 0 ? $",afade=t=in:st=0:d={N(Math.Min(sound.FadeIn, plays / 2))}" : "") +
                                 (sound.FadeOut > 0 ? $",afade=t=out:st={N(Math.Max(0, plays - Math.Min(sound.FadeOut, plays / 2)))}:d={N(Math.Min(sound.FadeOut, plays / 2))}" : "");
                f.Append($"[{board.Shots.Count + j}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,atrim=start={N(sound.Trim)},asetpts=PTS-STARTPTS{cut}{soundFades},volume={N(sound.Volume * board.LineVolume(sound.SoundLine))},")
                 // Stamped again by their samples after the delay: a sound trimmed at its start (atrim
                 // start > 0) lost its delay in apad and atrim (ffmpeg 8.1), and played from 0 s.
                 .Append($"adelay={delay}|{delay},asetpts=N/SR/TB,apad=whole_dur={N(length)},atrim=duration={N(length)},asetpts=PTS-STARTPTS{duck}[s{j}];");
                mix.Append($"[s{j}]");
            }
            f.Append(mix).Append($"amix=inputs={sounds.Count + 1}:duration=first:normalize=0[mixed];");
            audio = "mixed";
        }
        // A sound cut where the video ends fades out over longer than the picture does.
        var runsOn = sounds.Any(s => s.Seconds <= 0 || s.Start + s.Seconds > length + 0.05);
        var soundFade = runsOn ? Math.Min(2.5, length / 3) : Math.Max(fadeOut, 0.05);
        f.Append($"[{audio}]afade=t=out:st={N(length - soundFade)}:d={N(soundFade)},alimiter=limit=0.95[aout]");

        var (preset, crf) = board.Quality switch
        {
            "draft" => ("veryfast", "26"),
            "best" => ("slow", "15"),
            _ => ("medium", "18"),
        };
        args.AddRange(["-filter_complex", f.ToString(), "-map", "[vout]", "-map", "[aout]",
            "-c:v", "libx264", "-preset", preset, "-crf", crf, "-pix_fmt", "yuv420p", "-r", fps.ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-t", N(length), output]);
        return (args, length, notes);
    }

    /// <summary>ffmpeg's xfade name for a shot's transition: a dissolve is its fade, a fade goes through black.</summary>
    internal static string XfadeName(string transition) => transition switch
    {
        VideoShot.Dissolve => "fade",
        VideoShot.Fade => "fadeblack",
        _ => transition,
    };

    /// <summary>
    /// The filters that make a shot's picture from its clip: its start trimmed, reversed, at its speed,
    /// cropped, coloured, frozen where it says, held on its last frame when it is shorter than the shot,
    /// cut to the shot's length, filling the video's frame at its frame rate, and zoomed or panned over.
    /// </summary>
    internal static string ShotPicture(VideoShot shot, int width, int height, int fps, string pixelFormat = "yuv420p")
    {
        var s = shot.Seconds;
        var chain = new List<string>();
        if (shot.In > 0)
            chain.Add($"trim=start={N(shot.In)}");
        chain.Add("setpts=PTS-STARTPTS");
        if (shot.Reverse)
            chain.Add("reverse");
        if (shot.Speed is { } speed)
            chain.Add($"setpts=PTS/{N(speed)}");
        if (shot.Crop is { } crop)
            chain.Add($"crop=iw*{N(crop.Width)}:ih*{N(crop.Height)}:iw*{N(crop.X)}:ih*{N(crop.Y)}");
        if (shot.Color is { } color)
            chain.AddRange(Colour(color));
        if (shot.Freeze is { } freeze)
            chain.Add($"trim=duration={N(Math.Max(1.0 / fps, freeze))},setpts=PTS-STARTPTS");
        // A clip shorter than its shot holds its last frame; a longer one is cut.
        chain.Add($"tpad=stop_mode=clone:stop_duration={N(s)},trim=duration={N(s)},setpts=PTS-STARTPTS");
        chain.Add($"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},setsar=1,fps={fps}");
        if (shot.Zoom is { } zoom)
        {
            // Over twice the frame first, so the slow zoom moves smoothly and doesn't step pixel by pixel.
            var frames = Math.Max(1, (int)Math.Round(s * fps) - 1);
            string Lerp(double a, double b) => Math.Abs(a - b) < 1e-9 ? N(a) : $"({N(a)}+({N(b - a)})*min(on/{frames},1))";
            var z = Lerp(zoom.From.Scale, zoom.To.Scale);
            var x = $"max(0,min(iw-iw/zoom,iw*{Lerp(zoom.From.X, zoom.To.X)}-iw/zoom/2))";
            var y = $"max(0,min(ih-ih/zoom,ih*{Lerp(zoom.From.Y, zoom.To.Y)}-ih/zoom/2))";
            chain.Add($"scale={width * 2}:{height * 2},zoompan=z='{z}':x='{x}':y='{y}':d=1:s={width}x{height}:fps={fps},setsar=1");
        }
        chain.Add($"format={pixelFormat}");
        return string.Join(",", chain);
    }

    /// <summary>The filters for a shot's colours: brightness, contrast and saturation, then its look.</summary>
    private static IEnumerable<string> Colour(VideoColor color)
    {
        if (color.Brightness != 0 || color.Contrast != 1 || color.Saturation != 1)
            yield return $"eq=brightness={N(color.Brightness * 0.5)}:contrast={N(color.Contrast)}:saturation={N(color.Saturation)}";
        var look = color.Look switch
        {
            "bw" => "hue=s=0",
            "sepia" => "colorchannelmixer=.393:.769:.189:0:.349:.686:.168:0:.272:.534:.131",
            "warm" => "colorbalance=rs=0.08:gs=0.02:bs=-0.08:rm=0.06:bm=-0.06",
            "cool" => "colorbalance=rs=-0.08:bs=0.08:rm=-0.05:bm=0.06",
            "vintage" => "curves=preset=vintage",
            _ => null,
        };
        if (look is not null)
            yield return look;
    }

    /// <summary>The filters for a shot's own sound, as its picture is cut: trimmed, reversed, at its speed, at its volume, frozen with silence, padded to its length.</summary>
    internal static string ShotSound(VideoShot shot, double under)
    {
        var s = shot.Seconds;
        var chain = new List<string> { "aresample=48000", "aformat=sample_fmts=fltp:channel_layouts=stereo" };
        if (shot.In > 0 || shot.Reverse || shot.Speed is not null || shot.Freeze is not null)
        {
            if (shot.In > 0)
                chain.Add($"atrim=start={N(shot.In)}");
            chain.Add("asetpts=PTS-STARTPTS");
            if (shot.Reverse)
                chain.Add("areverse");
            if (shot.Speed is { } speed)
                chain.AddRange(Tempo(speed));
            if (shot.Freeze is { } freeze)
                chain.Add($"atrim=duration={N(Math.Max(0.01, freeze))},asetpts=PTS-STARTPTS");
        }
        chain.Add($"apad=whole_dur={N(s)},atrim=duration={N(s)},asetpts=PTS-STARTPTS");
        chain.Add($"volume={N(under * (shot.Volume ?? 1))}");
        return string.Join(",", chain);
    }

    /// <summary>atempo steps for a speed: each takes 0.5 to 2, so a quarter speed is two halves.</summary>
    internal static IEnumerable<string> Tempo(double speed)
    {
        while (speed < 0.5)
        {
            yield return "atempo=0.5";
            speed /= 0.5;
        }
        while (speed > 2)
        {
            yield return "atempo=2";
            speed /= 2;
        }
        if (Math.Abs(speed - 1) > 1e-6)
            yield return $"atempo={N(speed)}";
    }

    /// <summary>Foley is made from what the video shows at 25 frames a second (MMAudio's own rate), small.</summary>
    internal const int FoleyFps = 25, FoleyHeight = 384;

    /// <summary>
    /// The ffmpeg command line that cuts a stretch of the video out of its clips, as the joined video
    /// would show it (each clip as its shot cuts it, joined by its transitions), without sound, at
    /// <see cref="FoleyFps"/>: what foley is made from. Only the clips under the stretch are needed, and they must be made.
    /// </summary>
    public static List<string> StretchPlan(Storyboard board, double start, double seconds, string output)
    {
        var (starts, length) = board.Timing();
        var end = Math.Min(length, start + seconds);
        if (end - start < 0.5)
            throw new VideoException(start >= length ? $"No clip is under {N(start)} s: the video is {N(length)} s long." : "The stretch is too short to make a sound from.");
        var under = Enumerable.Range(0, board.Shots.Count).Where(i => starts[i] < end - 1e-6 && starts[i] + board.Shots[i].Seconds > start + 1e-6).ToList();
        var missing = under.Where(i => !File.Exists(board.ShotVideo(board.Shots[i]))).Select(i => board.Shots[i].Id).ToList();
        if (missing.Count > 0)
            throw new VideoException($"The clips under it aren't made yet: {string.Join(", ", missing)}. Make them first: the sound is made from what they show.");
        var (width, height) = (board.Width, board.Height);
        var args = new List<string> { "-hide_banner", "-y", "-nostats", "-progress", "pipe:1" };
        foreach (var i in under)
            args.AddRange(["-i", board.ShotVideo(board.Shots[i])]);
        var f = new StringBuilder();
        for (var k = 0; k < under.Count; k++)
            f.Append($"[{k}:v]").Append(ShotPicture(board.Shots[under[k]], width, height, FoleyFps)).Append($"[v{k}];");
        var (video, joined) = ("v0", board.Shots[under[0]].Seconds);
        for (var k = 1; k < under.Count; k++)
        {
            var shot = board.Shots[under[k]];
            var overlap = Storyboard.Overlap(shot);
            if (overlap <= 0)
                f.Append($"[{video}][v{k}]concat=n=2:v=1:a=0[vj{k}];");
            else
                f.Append($"[{video}][v{k}]xfade=transition={XfadeName(shot.Transition)}:duration={N(overlap)}:offset={N(joined - overlap)}[vj{k}];");
            joined += shot.Seconds - overlap;
            video = $"vj{k}";
        }
        f.Append($"[{video}]trim=start={N(start - starts[under[0]])}:duration={N(end - start)},setpts=PTS-STARTPTS,scale=-2:{FoleyHeight}[vout]");
        args.AddRange(["-filter_complex", f.ToString(), "-map", "[vout]", "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-r", FoleyFps.ToString(CultureInfo.InvariantCulture), "-t", N(end - start), output]);
        return args;
    }

    private static string N(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Runs ffmpeg to make <paramref name="output"/> (written to a temporary file next to it first), saying how far it has got.</summary>
    internal static async Task RunAsync(string ffmpeg, List<string> args, string output, double length, string doing, string task, Action<string>? progress, CancellationToken ct)
    {
        var temp = args[^1];
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new VideoException($"ffmpeg ({ffmpeg}) didn't start.");
        var errors = new StringBuilder();
        var said = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                lock (errors)
                {
                    errors.AppendLine(line);
                    if (errors.Length > 20_000)
                        errors.Remove(0, errors.Length - 10_000);
                }
        }, CancellationToken.None);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], out var us) && us > 0)
                    progress?.Invoke($"{doing}… {us / 1e6:0.0} of {length:0.0} s");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        try { await said.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException) { }
        if (process.ExitCode != 0 || !File.Exists(temp))
        {
            string tail;
            lock (errors)
                tail = string.Join("\n", errors.ToString().Split('\n').Where(l => l.Trim().Length > 0).TakeLast(12));
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw new VideoException($"ffmpeg couldn't {task} (exit code {process.ExitCode}):\n{tail}");
        }
        if (!string.Equals(temp, output, StringComparison.OrdinalIgnoreCase))
        {
            MediaRelease.Release(output); // the one made before may be playing in the chat
            File.Move(temp, output, overwrite: true);
        }
    }

    /// <summary>Joins the video into its file next to the storyboard (named after it); returns where it is and what to know.</summary>
    /// <param name="scenes">Draws the overlays that are scenes; null where none can be drawn.</param>
    public static async Task<VideoMade> JoinAsync(Storyboard board, string? ffmpeg, IMediaDecoder? decoder, Action<string>? progress, CancellationToken ct,
        Render.SceneRenderer? scenes = null)
    {
        if (ffmpeg is null)
            throw new VideoException("Joining the shots needs ffmpeg, and there is none on this PC: download a portable build (ffmpeg's own site links them, such as gyan.dev's " +
                                    "ffmpeg-release-essentials.zip) and unpack it into Sloperator's SloperatorData\\tools, where it is found, then join again.");
        var output = board.VideoFile;
        var temp = Path.Combine(board.Folder, "." + Path.GetFileNameWithoutExtension(output) + ".joining.mp4");
        var clock = Stopwatch.StartNew();
        var notes = new List<string>();
        var (_, length) = board.Timing();
        List<OverlayInput> overlays = [];
        if (board.Overlays.Count > 0 || board.Captions)
        {
            progress?.Invoke("Drawing the titles and overlays…");
            overlays = await VideoOverlays.PrepareAsync(board, length, scenes, ffmpeg, notes, ct).ConfigureAwait(false);
        }
        var (args, joined, planNotes) = Plan(board, file => decoder?.Probe(file)?.HasAudio ?? true, temp, overlays);
        await RunAsync(ffmpeg, args, output, joined, "Joining the shots", "join the shots", progress, ct).ConfigureAwait(false);
        return new VideoMade(output, null, null, clock.Elapsed, [.. planNotes, .. notes]);
    }

    /// <summary>
    /// The joined video as another file: another kind (.mp4, .webm, .mov, .gif) or size, for where it is
    /// going (a GIF for a chat, a smaller MP4 to send, a WebM for a web page).
    /// </summary>
    public static async Task<string> ExportAsync(Storyboard board, string ffmpeg, string output, int? width, int? height, Action<string>? progress, CancellationToken ct)
    {
        var source = board.JoinedFile;
        if (!File.Exists(source))
            throw new VideoException("The video isn't joined yet: join it first (action=join), then export it.");
        var (_, length) = board.Timing();
        var ext = Path.GetExtension(output).ToLowerInvariant();
        var size = width is > 0 || height is > 0 ? $"scale={(width is > 0 ? Even(width.Value).ToString(CultureInfo.InvariantCulture) : "-2")}:{(height is > 0 ? Even(height.Value).ToString(CultureInfo.InvariantCulture) : "-2")}" : null;
        var args = new List<string> { "-hide_banner", "-y", "-nostats", "-progress", "pipe:1", "-i", source };
        switch (ext)
        {
            case ".gif":
                var fps = Math.Min(board.Fps, 15);
                args.AddRange(["-filter_complex", $"[0:v]fps={fps}{(size is null ? (board.Width > 640 ? ",scale=640:-2" : "") : "," + size)},split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a", "-loop", "0"]);
                break;
            case ".webm":
                if (size is not null) args.AddRange(["-vf", size]);
                args.AddRange(["-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "32", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k"]);
                break;
            case ".mov":
                if (size is not null) args.AddRange(["-vf", size]);
                args.AddRange(["-c:v", "prores_ks", "-profile:v", "3", "-c:a", "pcm_s16le"]);
                break;
            case ".mp4" or ".m4v":
                if (size is not null) args.AddRange(["-vf", size]);
                args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"]);
                break;
            case ".mp3" or ".wav" or ".m4a":
                args.AddRange(["-vn"]);
                break;
            default:
                throw new VideoException("Export to .mp4, .webm, .mov, .gif, or a sound alone to .mp3, .wav or .m4a.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "." + Path.GetFileNameWithoutExtension(output) + ".exporting" + ext);
        args.Add(temp);
        await RunAsync(ffmpeg, args, Path.GetFullPath(output), length, "Exporting", "export the video", progress, ct).ConfigureAwait(false);
        return Path.GetFullPath(output);

        static int Even(int n) => Math.Max(2, n / 2 * 2);
    }
}
