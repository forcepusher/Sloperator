using System.Globalization;
using System.Text;
using Sloperator.Core.Media;
using SkiaSharp;

namespace Sloperator.Core.Video;

/// <summary>What a scan of a recording found (<see cref="VideoCheck.Scan"/>).</summary>
/// <param name="Text">What it is, where its sound is silent and loudest, where its picture changes, stands still and moves most.</param>
/// <param name="Sheet">Frames across it, each with its time; null when its picture can't be read.</param>
/// <param name="Sounding">The stretches with sound in them, in the file's seconds: what is left of it with its silences cut out.</param>
public sealed record VideoScanned(string Text, byte[]? Sheet, IReadOnlyList<(double From, double To)> Sounding);

/// <summary>A recording's sound, measured (<see cref="VideoCheck.ScanSound"/>).</summary>
/// <param name="UsualDb">How loud it usually is where there is sound: louder than nine tenths of it.</param>
/// <param name="QuietDb">Below this it counts as silent.</param>
/// <param name="Silences">The stretches of silence of <see cref="VideoCheck.LeastSilence"/> or more, with a little kept at each end for the sound beside them.</param>
/// <param name="Loudest">The moments that stand out as loud, the loudest first.</param>
internal sealed record SoundScan(double Seconds, double UsualDb, double QuietDb, IReadOnlyList<(double From, double To)> Silences, IReadOnlyList<(double At, double Db)> Loudest)
{
    /// <summary>No sound to speak of anywhere in it.</summary>
    public bool Silent => UsualDb <= -60;

    /// <summary>What is left with the silences cut out, in order.</summary>
    public IReadOnlyList<(double From, double To)> Sounding
    {
        get
        {
            var kept = new List<(double, double)>();
            var at = 0.0;
            foreach (var (from, to) in Silences)
            {
                if (from - at > 0.05)
                    kept.Add((at, from));
                at = to;
            }
            if (Seconds - at > 0.05)
                kept.Add((at, Seconds));
            return kept;
        }
    }
}

/// <summary>A recording's picture, looked at every so often (<see cref="VideoCheck.ScanPicture"/>).</summary>
/// <param name="Every">The seconds between two looks.</param>
/// <param name="Changes">Where the picture cuts to another one, each to the frame: the time of the first frame of the new picture.</param>
/// <param name="Still">The stretches where nothing on screen changes.</param>
/// <param name="Busiest">The moments with the most movement, in order.</param>
/// <param name="Black">The stretches where the picture is black.</param>
internal sealed record PictureScan(double Every, IReadOnlyList<double> Changes, IReadOnlyList<(double From, double To)> Still, IReadOnlyList<double> Busiest,
    IReadOnlyList<(double From, double To)> Black);

public static partial class VideoCheck
{
    /// <summary>The shortest silence that is one: shorter is a breath between two words.</summary>
    internal const double LeastSilence = 0.7;

    /// <summary>How much of a silence is kept at each end, so the word before it isn't clipped and the one after doesn't start cold.</summary>
    internal const double KeptAround = 0.15;

    /// <summary>
    /// What a recording holds, found without watching it: where its sound is silent and where it stands out, where
    /// its picture changes, stands still and moves most, and frames across it with their times. For choosing what to
    /// cut out of a long recording: frames spread over twenty minutes say little of where anything happens, and the
    /// model went by them alone. <paramref name="from"/> and <paramref name="seconds"/> scan a stretch of it more closely.
    /// </summary>
    public static VideoScanned Scan(string file, IMediaDecoder decoder, double from = 0, double seconds = 0, Action<string>? progress = null, CancellationToken ct = default)
    {
        var whole = decoder.Probe(file) ?? throw new VideoException($"{Path.GetFileName(file)} can't be read on this PC (ffmpeg in Sloperator's SloperatorData\\tools would).");
        var length = whole.Duration.TotalSeconds;
        from = Math.Clamp(from, 0, Math.Max(0, length - 0.1));
        var to = seconds > 0 ? Math.Min(length, from + seconds) : length;
        var part = from > 0.01 || to < length - 0.01;
        var text = new StringBuilder();
        text.AppendLine($"{Path.GetFileName(file)}: {whole.Describe()}." + (part ? $" Scanned from {N(from)} to {N(to)} s. Every time below is in the file's own seconds." : " Every time below is in seconds."));

        IReadOnlyList<(double, double)> sounding = [];
        if (whole.HasAudio && decoder.OpenAudio(file) is { } opened)
        {
            progress?.Invoke("Listening to it…");
            using var sound = part ? new MediaStretch.Sound(opened, from, to) : opened;
            var heard = ScanSound(sound, ct);
            sounding = [.. heard.Sounding.Select(s => (s.From + from, s.To + from))];
            Describe(heard, from, text);
        }
        else
            text.AppendLine("It has no sound.");

        byte[]? sheet = null;
        if (whole.HasVideo && decoder.OpenVideo(file, 320) is { } reader)
            using (reader)
            {
                progress?.Invoke("Looking through it…");
                var (seen, frames) = ScanPicture(reader, from, to, progress, ct);
                try
                {
                    Describe(seen, text);
                    if (frames.Count > 0)
                    {
                        // To the tenth of a second where the frames are less than two seconds apart: a stretch looked at closely.
                        var close = frames.Count > 1 && frames[1].Time - frames[0].Time < 2;
                        using var grid = GridBitmap([.. frames.Select(f => (Clock(f.Time, close), f.Frame))], reader.Width, reader.Height, columns: frames.Count <= 12 ? 4 : 6, sheetWidth: 1536);
                        sheet = Jpeg(grid);
                        text.AppendLine($"The picture shows {frames.Count} of its frames, one every {N(frames.Count > 1 ? frames[1].Time - frames[0].Time : 0)} s, each with its time (minutes:seconds{(close ? " and tenths" : "")}).");
                    }
                }
                finally
                {
                    foreach (var (_, frame) in frames)
                        frame.Dispose();
                }
            }
        text.Append("To cut by this, make shots of the file with \"in\" and \"seconds\"; path with storyboard {\"in\": …, \"seconds\": …} scans a stretch more closely.");
        return new VideoScanned(text.ToString(), sheet, sounding);
    }

    /// <summary>"3:20" for 200 seconds, "1:02:05" past an hour, "0:02.5" with tenths: a time as a clock shows it, for a label.</summary>
    internal static string Clock(double seconds, bool tenths = false)
    {
        if (tenths)
        {
            var t = (long)Math.Round(seconds * 10);
            return $"{t / 600}:{t / 10 % 60:00}.{t % 10}";
        }
        var s = (long)Math.Floor(seconds + 0.05);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    // ---- its sound -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A sound read through once, a twentieth of a second at a time, none of it held: how loud it usually is, where it
    /// is silent (well below that, for <see cref="LeastSilence"/> or more) and which moments stand out as loud.
    /// </summary>
    internal static SoundScan ScanSound(IAudioReader reader, CancellationToken ct = default)
    {
        const double window = 0.05;
        var (rate, channels) = (Math.Max(1, reader.SampleRate), Math.Max(1, reader.Channels));
        var per = Math.Max(1, (int)Math.Round(rate * window));
        // The energy of each twentieth of a second, over all its channels (added up, two channels that mirror each other would cancel).
        var energy = new List<double>();
        var buffer = new float[per * channels * 20];
        var (sum, count) = (0.0, 0);
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i + channels <= read; i += channels)
            {
                for (var c = 0; c < channels; c++)
                    sum += buffer[i + c] * (double)buffer[i + c];
                if (++count == per)
                {
                    energy.Add(sum / (per * channels));
                    (sum, count) = (0, 0);
                }
            }
        }
        if (count > per / 2)
            energy.Add(sum / (count * channels));
        var seconds = energy.Count * window;
        if (energy.Count == 0)
            return new SoundScan(0, -120, -120, [], []);
        static double Db(double e) => 10 * Math.Log10(Math.Max(e, 1e-12));
        var ordered = energy.Order().ToList();
        var usual = Db(ordered[Math.Min(ordered.Count - 1, ordered.Count * 9 / 10)]);
        // Silent is well below how loud it usually is, not below a fixed level: a recording made in a room has the room in its silences.
        var quiet = Math.Clamp(usual - 30, -60, -35);
        var silences = new List<(double, double)>();
        if (usual > -60)
            for (var i = 0; i < energy.Count; i++)
            {
                if (Db(energy[i]) >= quiet)
                    continue;
                var start = i;
                while (i < energy.Count && Db(energy[i]) < quiet) i++;
                var (a, b) = (start * window, i * window);
                if (b - a < LeastSilence)
                    continue;
                // A little of it kept beside the sound at each end; all of it goes at the recording's own start and end.
                var (cutFrom, cutTo) = (start == 0 ? 0 : a + KeptAround, i >= energy.Count ? seconds : b - KeptAround);
                if (cutTo - cutFrom >= 0.3)
                    silences.Add((cutFrom, cutTo));
            }
        // A second at a time for what stands out: a clap is one loud twentieth, a cheer a loud second.
        var each = new List<double>();
        for (var i = 0; i < energy.Count; i += 20)
            each.Add(Db(energy.Skip(i).Take(20).Average()));
        var middle = each.Order().ElementAt(each.Count / 2);
        var apart = Math.Max(5, seconds / 30);
        var loudest = new List<(double At, double Db)>();
        foreach (var (db, at) in each.Select((db, i) => (db, i + 0.5)).OrderByDescending(p => p.db))
        {
            if (db < middle + 6 || db < quiet + 10 || loudest.Count >= 8)
                break;
            if (loudest.All(l => Math.Abs(l.At - at) >= apart))
                loudest.Add((at, db));
        }
        return new SoundScan(seconds, usual, quiet, silences, loudest);
    }

    private static void Describe(SoundScan heard, double from, StringBuilder text)
    {
        if (heard.Silent)
        {
            text.AppendLine("Its sound is silent all through.");
            return;
        }
        var line = new StringBuilder($"Sound: usually about {heard.UsualDb:0} dB.");
        if (heard.Silences.Count == 0)
            line.Append($" No silence of {N(LeastSilence)} s or more in it.");
        else
        {
            var all = heard.Silences.Sum(s => s.To - s.From);
            line.Append($" Silent (below {heard.QuietDb:0} dB) for {N(Math.Round(all, 1))} s in all, in {heard.Silences.Count} stretch{(heard.Silences.Count == 1 ? "" : "es")}: ")
                .Append(string.Join(", ", heard.Silences.Take(40).Select(s => $"{T(s.From + from)}–{T(s.To + from)}")))
                .Append(heard.Silences.Count > 40 ? $" and {heard.Silences.Count - 40} more." : ".")
                .Append($" With {(heard.Silences.Count == 1 ? "it" : "them")} cut out it is {N(Math.Round(heard.Seconds - all, 1))} s (action=edit with \"skip\": \"silences\" does that in one call).");
        }
        if (heard.Loudest.Count > 0)
            line.Append(" It stands out as loud at ").Append(string.Join(", ", heard.Loudest.Select(l => $"{T(l.At + from)} ({l.Db:0} dB)"))).Append('.');
        text.AppendLine(line.ToString());
    }

    /// <summary>A time in seconds, to a tenth.</summary>
    private static string T(double seconds) => seconds.ToString("0.#", CultureInfo.InvariantCulture);

    // ---- its picture ---------------------------------------------------------------------------------------------

    /// <summary>The most looks taken at a recording: about a quarter of a minute's work on a 720p one, however long it is.</summary>
    private const int MostLooks = 300;

    /// <summary>How many of the frames looked at go into the picture.</summary>
    private const int SheetFrames = 30;

    /// <summary>
    /// A recording's picture looked at every few seconds: two frames a fifth of a second apart each time, the first
    /// against the look before (is it another picture?) and against the second (does it move?). The reader jumps from
    /// look to look, so a twenty-minute recording is 300 jumps, not 36,000 frames.
    /// </summary>
    internal static (PictureScan Seen, List<(double Time, SKBitmap Frame)> Frames) ScanPicture(IVideoReader reader, double from, double to, Action<string>? progress = null, CancellationToken ct = default)
    {
        var span = Math.Max(0.1, to - from);
        var looks = (int)Math.Clamp(Math.Round(span / 2), 8, MostLooks);
        var every = span / looks;
        var rate = reader.FrameRate > 0 ? reader.FrameRate : 30;
        var on = Math.Max(1, (int)Math.Round(Math.Min(0.2, every / 2) * rate));
        var keep = Math.Max(1, (int)Math.Round(looks / (double)Math.Min(SheetFrames, looks)));
        var frames = new List<(double, SKBitmap)>();
        var (times, change, moving, dark) = (new List<double>(), new List<double>(), new List<double>(), new List<bool>());
        var grays = new List<double[]>();
        double[]? before = null;
        try
        {
            for (var i = 0; i < looks; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i % 20 == 0)
                    progress?.Invoke($"Looking through it: {i * 100 / looks}%…");
                var at = from + i * every;
                reader.Seek(TimeSpan.FromSeconds(at));
                if (reader.Next() is not { } first)
                    break;
                using var picture = MediaPreview.ToBitmap(first.Frame);
                var gray = Shrink(picture).Gray;
                var then = gray;
                for (var k = 0; k < on && reader.Next() is { } next; k++)
                    if (k == on - 1)
                    {
                        using var later = MediaPreview.ToBitmap(next.Frame);
                        then = Shrink(later).Gray;
                    }
                // By the frame's own time: the reader lands on the frame nearest the time asked for.
                times.Add(first.Time.TotalSeconds);
                grays.Add(gray);
                dark.Add(gray.Average() < 12);
                change.Add(before is null ? 0 : Apart(gray, before));
                moving.Add(Apart(gray, then));
                before = gray;
                if (i % keep == 0 && frames.Count < SheetFrames)
                    frames.Add((first.Time.TotalSeconds, picture.Copy()));
            }
        }
        catch
        {
            foreach (var (_, frame) in frames)
                frame.Dispose();
            throw;
        }
        if (times.Count == 0)
            return (new PictureScan(every, [], [], [], []), frames);

        static double Middle(IEnumerable<double> values) => values.Order().ToList() is { Count: > 0 } o ? o[o.Count / 2] : 0;
        // Another picture than a look before: far more of it differs than does from look to look as a rule. Whether it
        // came at once (a cut) or bit by bit (a pan, a zoom, a fade: a zoom into a fractal was four "cuts" in ten
        // seconds) is found between the two looks, which also gives the cut to the frame. The strongest twenty.
        var usualChange = Middle(change.Skip(1));
        var changes = new List<double>();
        foreach (var i in Enumerable.Range(1, times.Count - 1).Where(i => change[i] > Math.Max(22, usualChange * 3)).OrderByDescending(i => change[i]).Take(20).Order())
        {
            ct.ThrowIfCancellationRequested();
            // More than what moves in it makes in a fifth of a second, there and before: stripes sliding past, or a
            // camera whipped round, differ from frame to frame as much as from look to look, and are no cut.
            if (CutBetween(reader, times[i - 1], grays[i - 1], times[i], grays[i], change[i], 1 / rate, Math.Max(moving[i - 1], moving[i]) * 2) is { } cut)
                changes.Add(cut);
        }
        // Nothing changes: the same picture look after look, with nothing moving in it either, for ten seconds or more.
        var still = Runs(Enumerable.Range(0, times.Count).Select(i => !dark[i] && change[i] < 1.5 && moving[i] < 0.8).ToList(), times, every, Math.Max(10, every * 3));
        var black = Runs(dark, times, every, Math.Max(2, every * 2));
        // What stands out, not what is usual: in a recording that moves all through, no moment is the busiest.
        var ordered = moving.Order().ToList();
        var usualMove = ordered[ordered.Count * 3 / 4];
        var apart = Math.Max(5, span / 30);
        var busiest = new List<double>();
        foreach (var i in Enumerable.Range(0, times.Count).OrderByDescending(i => moving[i]))
        {
            if (moving[i] < Math.Max(4, usualMove * 1.5) || busiest.Count >= 6)
                break;
            if (busiest.All(b => Math.Abs(b - times[i]) >= apart))
                busiest.Add(times[i]);
        }
        busiest.Sort();
        return (new PictureScan(every, changes, still, busiest, black), frames);
    }

    /// <summary>
    /// Where between two looks the picture becomes another one: the stretch between them halved, each time towards
    /// the half that differs more, until its ends are a frame or two apart. A cut when most of the difference is
    /// still there between those two frames: the time of the later one. Null when it came bit by bit.
    /// </summary>
    /// <param name="least">The least the two frames at the cut differ by, beside half of what the two looks do.</param>
    private static double? CutBetween(IVideoReader reader, double a, double[] before, double b, double[] after, double whole, double frame, double least)
    {
        for (var n = 0; n < 12 && b - a > frame * 1.5; n++)
        {
            reader.Seek(TimeSpan.FromSeconds((a + b) / 2));
            if (reader.Next() is not { } next)
                return null;
            var at = next.Time.TotalSeconds;
            // No frame strictly between them is left: they are next to each other.
            if (at <= a + frame / 4 || at >= b - frame / 4)
                break;
            using var picture = MediaPreview.ToBitmap(next.Frame);
            var gray = Shrink(picture).Gray;
            if (Apart(before, gray) >= Apart(gray, after))
                (b, after) = (at, gray);
            else
                (a, before) = (at, gray);
        }
        return Apart(before, after) >= Math.Max(Math.Max(18, least), whole / 2) ? b : null;
    }

    /// <summary>How far apart two small grey pictures are: the mean difference of their pixels, 0 to 255.</summary>
    private static double Apart(double[] a, double[] b)
    {
        var sum = 0.0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
            sum += Math.Abs(a[i] - b[i]);
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>The stretches where every look is so, when they last <paramref name="least"/> seconds or more.</summary>
    private static List<(double From, double To)> Runs(IReadOnlyList<bool> so, IReadOnlyList<double> times, double every, double least)
    {
        var runs = new List<(double, double)>();
        for (var i = 0; i < so.Count; i++)
        {
            if (!so[i])
                continue;
            var start = i;
            while (i < so.Count && so[i]) i++;
            // From the look that first shows it to the one after the last that does.
            var (a, b) = (times[start], times[i - 1] + every);
            if (b - a >= least)
                runs.Add((a, b));
        }
        return runs;
    }

    private static void Describe(PictureScan seen, StringBuilder text)
    {
        var line = new StringBuilder($"Picture, looked at every {N(Math.Round(seen.Every, 1))} s:");
        var said = false;
        if (seen.Changes.Count > 0)
        {
            line.Append($" it cuts to another picture at {string.Join(", ", seen.Changes.Select(N))};");
            said = true;
        }
        if (seen.Still.Count > 0)
        {
            line.Append($" nothing on screen changes from {string.Join(", ", seen.Still.Take(12).Select(s => $"{T(s.From)} to {T(s.To)}"))}{(seen.Still.Count > 12 ? $" and {seen.Still.Count - 12} more stretches" : "")};");
            said = true;
        }
        if (seen.Black.Count > 0)
        {
            line.Append($" it is black from {string.Join(", ", seen.Black.Take(8).Select(s => $"{T(s.From)} to {T(s.To)}"))};");
            said = true;
        }
        if (seen.Busiest.Count > 0)
        {
            line.Append($" the most movement is around {string.Join(", ", seen.Busiest.Select(T))};");
            said = true;
        }
        text.AppendLine(said ? line.ToString().TrimEnd(';') + "." : line + " it goes on much the same all through, with no cut, standstill or burst of movement found.");
    }
}
