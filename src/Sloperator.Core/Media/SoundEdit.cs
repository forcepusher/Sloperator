using System.Globalization;
using System.Text;

namespace Sloperator.Core.Media;

/// <summary>What an edit of a sound does.</summary>
public enum SoundEditKind
{
    /// <summary>The stretch taken out, the sound either side joined.</summary>
    Cut,
    /// <summary>Only the stretch kept.</summary>
    Keep,
    /// <summary>From silence at the stretch's start to as loud as it was at its end.</summary>
    FadeIn,
    /// <summary>From as loud as it was at the stretch's start to silence at its end.</summary>
    FadeOut,
    /// <summary>Louder or quieter by so many dB.</summary>
    Volume,
    /// <summary>Another sound in place of the stretch.</summary>
    Replace,
}

/// <summary>
/// An edit: what, over which stretch in seconds (none: the whole sound; for a fade, its first or last
/// second), by how many dB (<see cref="SoundEditKind.Volume"/>), and with which sound (<see cref="SoundEditKind.Replace"/>).
/// </summary>
public sealed record SoundEditRequest(SoundEditKind Kind, double? From = null, double? To = null, double Db = 0, string? With = null);

/// <summary>A sound's samples, interleaved, from -1 to 1.</summary>
public sealed record SoundSamples(float[] Samples, int Rate, int Channels)
{
    public int Frames => Samples.Length / Channels;

    public double Seconds => Frames / (double)Rate;
}

/// <summary>An edit saved: the new file, what it did, and how long the sound was before and is now.</summary>
public sealed record SoundEdited(string Path, string Label, double Before, double After, int Rate, int Channels);

/// <summary>An edit that can't be done, with why, for the user or the model.</summary>
public sealed class SoundEditException(string message) : Exception(message);

/// <summary>
/// The Audio editor's edits (the panel's buttons, and the agent's edit_sound tool): cut a stretch out,
/// keep only it, fade it in or out, make it louder or quieter, or put another sound in its place. An
/// edit never changes the file it is made on: it is saved beside it as a new WAV, in a sound's folder a
/// new version named after the edit ("Cut 3.18-5.00 s.wav"), elsewhere "rain (Cut 3.18-5.00 s).wav".
/// Joins are crossfaded over 10 ms and what is kept fades in and out over 5 ms at a cut edge, so no edit
/// clicks; the ComfyUI workflow the sound kept goes with it, so the panel still shows how it was made.
/// </summary>
public static class SoundEdit
{
    /// <summary>How much louder or quieter Louder and Quieter make it, in dB.</summary>
    public const double Step = 3;

    /// <summary>The longest sound edited: its samples are all held at once.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(30);

    /// <summary>A join crossfades over this many seconds.</summary>
    private const double Join = 0.010;

    /// <summary>What is kept fades in and out over this many seconds where it was cut from the rest.</summary>
    private const double Edge = 0.005;

    /// <summary>
    /// The edit made on <paramref name="source"/> and saved beside it; what it was made with read by
    /// <paramref name="decoder"/>. <paramref name="soundsRoot"/> is the Audio editor's folder (its versions are named after the edit alone).
    /// </summary>
    public static SoundEdited Save(string source, SoundEditRequest request, IMediaDecoder decoder, string? soundsRoot)
    {
        var sound = Read(source, decoder);
        var (from, to) = Stretch(request, sound.Seconds);
        SoundSamples? with = null;
        if (request.Kind == SoundEditKind.Replace)
        {
            if (request.With is not { Length: > 0 } other)
                throw new SoundEditException("Replace needs the sound to put in the stretch's place (with).");
            with = Convert(Read(other, decoder), sound.Rate, sound.Channels);
            if (with.Frames == 0)
                throw new SoundEditException($"{Path.GetFileName(other)} is empty.");
        }
        var edited = Apply(sound, request.Kind, from, to, request.Db, with);
        var label = Label(request, from, to, sound.Seconds);
        var target = Target(source, label, soundsRoot);
        WriteWav(target, edited, MediaRecipe.Workflow(source)?.ToJsonString());
        return new SoundEdited(target, label, sound.Seconds, edited.Seconds, sound.Rate, sound.Channels);
    }

    /// <summary>All of a sound's samples; a sound nothing here can read, or one longer than <see cref="Longest"/>, can't be edited.</summary>
    public static SoundSamples Read(string path, IMediaDecoder decoder)
    {
        if (!File.Exists(path))
            throw new SoundEditException($"{path} isn't there.");
        using var reader = decoder.OpenAudio(path) ?? throw new SoundEditException($"{Path.GetFileName(path)} can't be read on this PC.");
        if (reader.Duration > Longest)
            throw new SoundEditException($"{Path.GetFileName(path)} is longer than {Longest.TotalMinutes:0} minutes, the longest sound edited here.");
        return Read(reader);
    }

    /// <summary>All the samples <paramref name="reader"/> gives, up to <see cref="Longest"/>.</summary>
    public static SoundSamples Read(IAudioReader reader)
    {
        var (rate, channels) = (Math.Max(1, reader.SampleRate), Math.Max(1, reader.Channels));
        var limit = (long)(Longest.TotalSeconds * rate) * channels;
        var samples = new float[(int)Math.Min(limit, Math.Max(8192, (long)((reader.Duration.TotalSeconds + 1) * rate) * channels))];
        var count = 0;
        while (count < limit)
        {
            if (count == samples.Length)
                Array.Resize(ref samples, (int)Math.Min(limit, samples.Length * 2L));
            var read = reader.Read(samples, count, samples.Length - count);
            if (read <= 0)
                break;
            count += read;
        }
        count -= count % channels;
        Array.Resize(ref samples, count);
        return new SoundSamples(samples, rate, channels);
    }

    /// <summary>
    /// The stretch an edit is made on, in seconds, kept inside the sound: the one asked for, else the
    /// whole sound, or for a fade its first or last second (half of a shorter sound).
    /// </summary>
    public static (double From, double To) Stretch(SoundEditRequest request, double length)
    {
        if (request.Kind == SoundEditKind.Volume && (request.Db == 0 || Math.Abs(request.Db) > 40 || double.IsNaN(request.Db)))
            throw new SoundEditException("Volume needs db: how many dB louder (3) or quieter (-3), 40 at most.");
        if (request.From is null && request.To is null)
        {
            var fade = Math.Min(1, length / 2);
            return request.Kind switch
            {
                SoundEditKind.Cut => throw new SoundEditException("Cut needs a stretch: from and to, in seconds."),
                SoundEditKind.Keep => throw new SoundEditException("Keep needs a stretch: from and to, in seconds."),
                SoundEditKind.Replace => throw new SoundEditException("Replace needs a stretch: from and to, in seconds (the same for both puts the sound in there)."),
                SoundEditKind.FadeIn => (0, fade),
                SoundEditKind.FadeOut => (length - fade, length),
                _ => (0, length),
            };
        }
        var from = Math.Clamp(request.From ?? 0, 0, length);
        var to = Math.Clamp(request.To ?? length, 0, length);
        if (to < from)
            (from, to) = (to, from);
        if (to - from < 0.001 && request.Kind != SoundEditKind.Replace)
            throw new SoundEditException(string.Create(CultureInfo.InvariantCulture, $"The stretch {from:0.000}-{to:0.000} s is empty: the sound is {length:0.000} s long."));
        if (request.Kind == SoundEditKind.Cut && from < 0.001 && to > length - 0.001)
            throw new SoundEditException("That would cut all of it.");
        return (from, to);
    }

    /// <summary>The edit made on the samples (a new copy) over <paramref name="from"/>-<paramref name="to"/> seconds.</summary>
    public static SoundSamples Apply(SoundSamples sound, SoundEditKind kind, double from, double to, double db = 0, SoundSamples? with = null)
    {
        var (a, b) = (FrameOf(sound, from), FrameOf(sound, to));
        return kind switch
        {
            SoundEditKind.Cut => Cut(sound, a, b),
            SoundEditKind.Keep => Keep(sound, a, b),
            SoundEditKind.FadeIn => Fade(sound, a, b, fadeIn: true),
            SoundEditKind.FadeOut => Fade(sound, a, b, fadeIn: false),
            SoundEditKind.Volume => Gain(sound, a, b, db),
            SoundEditKind.Replace => Replace(sound, a, b, with ?? throw new ArgumentNullException(nameof(with))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static int FrameOf(SoundSamples sound, double seconds) => (int)Math.Clamp(Math.Round(seconds * sound.Rate), 0, sound.Frames);

    /// <summary>Rises from 0 to 1 as <paramref name="t"/> goes from 0 to 1, smoothly at both ends; the two sides of a crossfade add up to 1.</summary>
    private static float Ramp(double t) => (float)(0.5 - 0.5 * Math.Cos(Math.PI * Math.Clamp(t, 0, 1)));

    /// <summary>
    /// Frames <paramref name="a"/>-<paramref name="b"/> out: the sound before fades into the sound after over
    /// <see cref="Join"/> centred on the join, so the length is exactly the rest's.
    /// </summary>
    private static SoundSamples Cut(SoundSamples sound, int a, int b)
    {
        var (s, ch, frames) = (sound.Samples, sound.Channels, sound.Frames);
        var h = Math.Min((int)(Join * sound.Rate / 2), Math.Min(a, frames - b));
        var o = new float[(a + frames - b) * ch];
        Array.Copy(s, 0, o, 0, (a - h) * ch);
        for (var k = 0; k < 2 * h; k++)
        {
            var w = Ramp((k + 0.5) / (2 * h));
            for (var c = 0; c < ch; c++)
                o[(a - h + k) * ch + c] = s[(a - h + k) * ch + c] * (1 - w) + s[(b - h + k) * ch + c] * w;
        }
        Array.Copy(s, (b + h) * ch, o, (a + h) * ch, (frames - b - h) * ch);
        return sound with { Samples = o };
    }

    /// <summary>Only frames <paramref name="a"/>-<paramref name="b"/>, fading in and out over <see cref="Edge"/> where they were cut from the rest.</summary>
    private static SoundSamples Keep(SoundSamples sound, int a, int b)
    {
        var ch = sound.Channels;
        var o = sound.Samples[(a * ch)..(b * ch)];
        var n = Math.Min((int)(Edge * sound.Rate), (b - a) / 2);
        for (var k = 0; k < n; k++)
        {
            var w = Ramp((k + 0.5) / n);
            for (var c = 0; c < ch; c++)
            {
                if (a > 0)
                    o[k * ch + c] *= w;
                if (b < sound.Frames)
                    o[(b - a - 1 - k) * ch + c] *= w;
            }
        }
        return sound with { Samples = o };
    }

    private static SoundSamples Fade(SoundSamples sound, int a, int b, bool fadeIn)
    {
        var ch = sound.Channels;
        var o = (float[])sound.Samples.Clone();
        for (var f = a; f < b; f++)
        {
            var t = (f - a + 0.5) / (b - a);
            var g = Ramp(fadeIn ? t : 1 - t);
            for (var c = 0; c < ch; c++)
                o[f * ch + c] *= g;
        }
        return sound with { Samples = o };
    }

    /// <summary>
    /// Frames <paramref name="a"/>-<paramref name="b"/> louder or quieter by <paramref name="db"/>, going there over
    /// <see cref="Join"/> inside the stretch where it meets the rest; past 0.9 the peaks are rounded off softly
    /// rather than clipped.
    /// </summary>
    private static SoundSamples Gain(SoundSamples sound, int a, int b, double db)
    {
        var ch = sound.Channels;
        var o = (float[])sound.Samples.Clone();
        var factor = Math.Pow(10, db / 20);
        var ramp = Math.Max(1, Math.Min((int)(Join * sound.Rate), (b - a) / 2));
        for (var f = a; f < b; f++)
        {
            // How far into the change: all of it but near an end that meets the rest.
            var w = 1f;
            if (a > 0)
                w = Math.Min(w, Ramp((f - a + 0.5) / ramp));
            if (b < sound.Frames)
                w = Math.Min(w, Ramp((b - f - 0.5) / ramp));
            var g = 1 + (factor - 1) * w;
            for (var c = 0; c < ch; c++)
                o[f * ch + c] = Limit((float)(o[f * ch + c] * g));
        }
        return sound with { Samples = o };
    }

    /// <summary>Up to 0.9 as it is; above, rounded off towards 1 (its slope still 1 at 0.9), so a louder sound never clips.</summary>
    private static float Limit(float x)
    {
        const float knee = 0.9f;
        var v = Math.Abs(x);
        return v <= knee ? x : Math.Sign(x) * (knee + (1 - knee) * MathF.Tanh((v - knee) / (1 - knee)));
    }

    /// <summary>
    /// <paramref name="with"/> in place of frames <paramref name="a"/>-<paramref name="b"/>, crossfaded into the
    /// sound either side over <see cref="Join"/> (which the joins overlap by, so the sound is that much shorter).
    /// </summary>
    private static SoundSamples Replace(SoundSamples sound, int a, int b, SoundSamples with)
    {
        var (s, w, ch, frames, wf) = (sound.Samples, with.Samples, sound.Channels, sound.Frames, with.Frames);
        var join = (int)(Join * sound.Rate);
        var n1 = Math.Min(join, Math.Min(a, wf / 2));
        var n2 = Math.Min(join, Math.Min(frames - b, wf / 2));
        var o = new float[(a + wf + frames - b - n1 - n2) * ch];
        Array.Copy(s, 0, o, 0, (a - n1) * ch);
        for (var k = 0; k < n1; k++)
        {
            var g = Ramp((k + 0.5) / n1);
            for (var c = 0; c < ch; c++)
                o[(a - n1 + k) * ch + c] = s[(a - n1 + k) * ch + c] * (1 - g) + w[k * ch + c] * g;
        }
        Array.Copy(w, n1 * ch, o, a * ch, (wf - n1 - n2) * ch);
        var at = a + wf - n1 - n2;
        for (var k = 0; k < n2; k++)
        {
            var g = Ramp((k + 0.5) / n2);
            for (var c = 0; c < ch; c++)
                o[(at + k) * ch + c] = w[(wf - n2 + k) * ch + c] * (1 - g) + s[(b + k) * ch + c] * g;
        }
        Array.Copy(s, (b + n2) * ch, o, (at + n2) * ch, (frames - b - n2) * ch);
        return sound with { Samples = o };
    }

    /// <summary><paramref name="sound"/> at <paramref name="rate"/> samples a second (between two samples, the line between them) and with <paramref name="channels"/> channels.</summary>
    public static SoundSamples Convert(SoundSamples sound, int rate, int channels)
    {
        var (s, from, frames) = (sound.Samples, sound.Channels, sound.Frames);
        var outFrames = rate == sound.Rate ? frames : (int)Math.Round(frames * (double)rate / sound.Rate);
        var o = new float[outFrames * channels];
        for (var f = 0; f < outFrames; f++)
        {
            var at = rate == sound.Rate ? f : f * (double)sound.Rate / rate;
            var i = Math.Min((int)at, frames - 1);
            var j = Math.Min(i + 1, frames - 1);
            var t = (float)(at - i);
            for (var c = 0; c < channels; c++)
            {
                float Sample(int frame)
                {
                    if (from == channels)
                        return s[frame * from + c];
                    // Down to one: all of them; up from one: the one; else the nearest there is.
                    if (channels == 1)
                    {
                        var sum = 0f;
                        for (var k = 0; k < from; k++) sum += s[frame * from + k];
                        return sum / from;
                    }
                    return s[frame * from + Math.Min(c, from - 1)];
                }
                o[f * channels + c] = Sample(i) * (1 - t) + Sample(j) * t;
            }
        }
        return new SoundSamples(o, rate, channels);
    }

    /// <summary>"Cut 3.18-5.00 s", "Fade out 7.00-8.00 s", "+3 dB" (the whole sound), "-3 dB 3.18-5.00 s": the new version's name.</summary>
    public static string Label(SoundEditRequest request, double from, double to, double length)
    {
        var range = string.Create(CultureInfo.InvariantCulture, $"{from:0.00}-{to:0.00} s");
        var whole = from < 0.0005 && to > length - 0.0005;
        return request.Kind switch
        {
            SoundEditKind.Cut => $"Cut {range}",
            SoundEditKind.Keep => $"Keep only {range}",
            SoundEditKind.FadeIn => $"Fade in {range}",
            SoundEditKind.FadeOut => $"Fade out {range}",
            SoundEditKind.Replace => $"Replace {range}",
            _ => string.Create(CultureInfo.InvariantCulture, $"{request.Db:+0.#;-0.#} dB") + (whole ? "" : $" {range}"),
        };
    }

    /// <summary>
    /// Where the edit of <paramref name="source"/> goes: beside it, a version of a sound in <paramref name="soundsRoot"/>
    /// named after the edit, any other sound after itself and the edit; " 2", " 3" when that is taken.
    /// </summary>
    public static string Target(string source, string label, string? soundsRoot)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var name = SoundFolder.IsVersion(source, soundsRoot) ? label : $"{Path.GetFileNameWithoutExtension(source)} ({label})";
        var path = Path.Combine(folder, name + ".wav");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{name} {n}.wav");
        return path;
    }

    /// <summary>
    /// The samples as a 16-bit WAV at <paramref name="path"/>, written beside it first and then put in its
    /// place, so a folder watched never sees it half written. <paramref name="workflow"/>, the ComfyUI workflow
    /// the sound was made with, goes in a comment after the samples, where <see cref="MediaRecipe"/> finds it.
    /// </summary>
    public static void WriteWav(string path, SoundSamples sound, string? workflow = null)
    {
        var part = path + ".part";
        var comment = workflow is null ? [] : Encoding.UTF8.GetBytes(workflow + "\0");
        var padded = comment.Length + comment.Length % 2;
        var list = comment.Length == 0 ? 0 : 4 + 8 + padded;
        var bytes = sound.Samples.Length * 2;
        using (var file = new BinaryWriter(File.Create(part)))
        {
            file.Write("RIFF"u8);
            file.Write(36 + bytes + (list == 0 ? 0 : 8 + list));
            file.Write("WAVEfmt "u8);
            file.Write(16);
            file.Write((short)1);
            file.Write((short)sound.Channels);
            file.Write(sound.Rate);
            file.Write(sound.Rate * sound.Channels * 2);
            file.Write((short)(sound.Channels * 2));
            file.Write((short)16);
            file.Write("data"u8);
            file.Write(bytes);
            var block = new byte[64 * 1024];
            for (var i = 0; i < sound.Samples.Length;)
            {
                var n = Math.Min(block.Length / 2, sound.Samples.Length - i);
                for (var k = 0; k < n; k++)
                {
                    var v = (short)Math.Round(Math.Clamp(sound.Samples[i + k], -1f, 1f) * 32767);
                    block[2 * k] = (byte)v;
                    block[2 * k + 1] = (byte)(v >> 8);
                }
                file.Write(block, 0, 2 * n);
                i += n;
            }
            if (list > 0)
            {
                file.Write("LIST"u8);
                file.Write(list);
                file.Write("INFO"u8);
                file.Write("ICMT"u8);
                file.Write(comment.Length);
                file.Write(comment);
                if (padded > comment.Length)
                    file.Write((byte)0);
            }
        }
        File.Move(part, path, overwrite: true);
    }
}
