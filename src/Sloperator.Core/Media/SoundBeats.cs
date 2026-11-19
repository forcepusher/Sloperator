using System.Collections.Concurrent;
using System.Numerics;

namespace Sloperator.Core.Media;

/// <summary>A sound's beat: how fast it is, and when each beat falls.</summary>
/// <param name="Bpm">Beats a minute.</param>
/// <param name="First">When the first beat falls, in seconds from the sound's start.</param>
/// <param name="Interval">Seconds from one beat to the next, as a line through all of them has it.</param>
/// <param name="Times">Every beat, in seconds from the sound's start.</param>
public sealed record BeatGrid(double Bpm, double First, double Interval, IReadOnlyList<double> Times);

/// <summary>
/// Finds the beat of a piece of music, so a video can be cut on it: where its sound starts anew (notes, drums) is
/// taken from how its spectrum changes, the tempo from how those moments repeat, and the beats themselves by
/// following the moments at that tempo through the whole piece (Ellis's beat tracking, 2007). A song and its video
/// drifted apart when cuts were placed by counting from a tempo alone: at 120 BPM a hundredth of a beat's error is
/// a third of a second after three minutes, so each beat is found where it is, not counted out.
/// </summary>
public static class SoundBeats
{
    /// <summary>The sound is looked at 86 times a second: 512 samples at 11 kHz, a quarter on each time.</summary>
    private const int Rate = 11025, Size = 512, Hop = 128;

    private static readonly ConcurrentDictionary<(string File, long Stamp, long Length), BeatGrid?> Known = new();

    /// <summary>A sound file's beat, found once for the file as it is; null when it can't be read or has no steady beat.</summary>
    public static BeatGrid? Of(string file, IMediaDecoder decoder)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
                return null;
            return Known.GetOrAdd((info.FullName.ToLowerInvariant(), info.LastWriteTimeUtc.Ticks, info.Length), _ => Find(SoundEdit.Read(file, decoder)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or SoundEditException)
        {
            return null;
        }
    }

    /// <summary>The sound's beat; null for one too short (under four seconds) or with no steady beat (speech, noise, a drone).</summary>
    public static BeatGrid? Find(SoundSamples sound)
    {
        var (onsets, perSecond) = Onsets(sound);
        if (onsets.Length < perSecond * 4)
            return null;
        if (Period(onsets, perSecond) is not { } period)
            return null;
        var beats = Track(onsets, period);
        if (beats.Count < 4)
            return null;
        var times = beats.Select(b => Math.Round((b + Size / 2.0 / Hop) / perSecond, 3)).ToList();
        // A line through them: its slope is the beat's length, steadier than any one gap.
        var (n, meanK, meanT) = (times.Count, (times.Count - 1) / 2.0, times.Average());
        double over = 0, under = 0;
        for (var k = 0; k < n; k++)
        {
            over += (k - meanK) * (times[k] - meanT);
            under += (k - meanK) * (k - meanK);
        }
        var interval = over / under;
        return new BeatGrid(Math.Round(60 / interval, 1), times[0], Math.Round(interval, 4), times);
    }

    /// <summary>
    /// How much the sound starts anew at each moment: the rise of its spectrum from one look to the next (only what
    /// got louder counts), less what is usual around it, in units of how much that varies.
    /// </summary>
    internal static (float[] Onsets, double PerSecond) Onsets(SoundSamples sound)
    {
        // One channel at about 11 kHz: enough for drums and notes, a quarter of the work.
        var step = Math.Max(1, (int)Math.Round(sound.Rate / (double)Rate));
        var rate = sound.Rate / (double)step;
        var mono = new float[sound.Frames / step];
        double power = 0;
        for (var i = 0; i < mono.Length; i++)
        {
            var sum = 0f;
            for (var k = 0; k < step; k++)
                for (var c = 0; c < sound.Channels; c++)
                    sum += sound.Samples[(i * step + k) * sound.Channels + c];
            mono[i] = sum / (step * sound.Channels);
            power += mono[i] * (double)mono[i];
        }
        // At one loudness whatever the file's: what follows squeezes loud and quiet together, and must do so alike for every file.
        var level = Math.Sqrt(power / Math.Max(1, mono.Length));
        var frames = Math.Max(0, (mono.Length - Size) / Hop + 1);
        if (level < 1e-6 || frames == 0)
            return ([], rate / Hop);
        var gain = (float)(0.1 / level);
        // Two looks at each moment: everything that got louder, and the bass alone (under 200 Hz), where the beat is
        // carried: a hat between beats is as much of a new start as a kick by the first, far less by the second.
        var bass = Math.Max(2, (int)(200 / (rate / Size)));
        var (all, low) = (new float[frames], new float[frames]);
        var window = new float[Size];
        for (var i = 0; i < Size; i++)
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Size));
        var before = new float[Size / 2];
        var spectrum = new Complex[Size];
        for (var f = 0; f < frames; f++)
        {
            for (var i = 0; i < Size; i++)
                spectrum[i] = mono[f * Hop + i] * window[i] * gain;
            Fourier(spectrum);
            var (rise, riseLow) = (0f, 0f);
            for (var b = 1; b < Size / 2; b++)
            {
                var loud = (float)Math.Log(1 + spectrum[b].Magnitude);
                if (loud > before[b])
                {
                    rise += loud - before[b];
                    if (b <= bass)
                        riseLow += loud - before[b];
                }
                before[b] = loud;
            }
            (all[f], low[f]) = f == 0 ? (0, 0) : (rise, riseLow);
        }
        var (a, l) = (Stood(all, rate), Stood(low, rate));
        var onsets = new float[frames];
        var both = new float[frames];
        for (var f = 0; f < frames; f++)
            both[f] = a[f] + l[f];
        // Spread over the looks beside it: a beat seldom falls on a look, and one that falls between two
        // would line up with the next beat worse than with the one after, which gave half the tempo (75 for 150).
        for (var f = 0; f < frames; f++)
            onsets[f] = 0.4f * both[f] + 0.24f * ((f > 0 ? both[f - 1] : 0) + (f + 1 < frames ? both[f + 1] : 0)) + 0.06f * ((f > 1 ? both[f - 2] : 0) + (f + 2 < frames ? both[f + 2] : 0));
        return (onsets, rate / Hop);
    }

    /// <summary>A run of rises less the average of the half second around each, nothing below that, in units of how much it varies.</summary>
    private static float[] Stood(float[] flux, double rate)
    {
        var frames = flux.Length;
        var half = (int)(rate / Hop / 4);
        var stood = new float[frames];
        double running = 0;
        var (from, to) = (0, 0);
        for (var f = 0; f < frames; f++)
        {
            while (to < Math.Min(frames, f + half + 1))
                running += flux[to++];
            while (from < f - half)
                running -= flux[from++];
            stood[f] = Math.Max(0, flux[f] - (float)(running / (to - from)));
        }
        double mean = frames > 0 ? stood.Average() : 0, spread = 0;
        foreach (var v in stood)
            spread += (v - mean) * (v - mean);
        spread = Math.Sqrt(spread / Math.Max(1, frames));
        // A sound that never starts anything anew (a held note, a hum) varies by rounding alone: that is no rise at all.
        if (spread < 0.02)
            return new float[frames];
            for (var f = 0; f < frames; f++)
                stood[f] = (float)(stood[f] / spread);
        return stood;
    }

    /// <summary>
    /// The beat's length in looks: the gap at which the sound's new starts line up with themselves best, between 50
    /// and 210 beats a minute, with a leaning towards 120 and towards a gap whose double lines up too (so half or
    /// twice the real tempo doesn't win). Null when no gap lines up much better than the rest: no steady beat.
    /// </summary>
    private static double? Period(float[] onsets, double perSecond)
    {
        var (shortest, longest) = ((int)(perSecond * 60 / 210), (int)Math.Ceiling(perSecond * 60 / 50));
        var reach = longest * 2 + 2;
        // About its own average: two runs that are only both above nothing line up at every gap, beat or no beat.
        var mean = onsets.Length > 0 ? onsets.Average() : 0;
        var lined = new double[reach + 1];
        for (var lag = 1; lag <= reach; lag++)
        {
            double sum = 0;
            for (var t = 0; t + lag < onsets.Length; t++)
                sum += (onsets[t] - mean) * (onsets[t + lag] - mean);
            lined[lag] = sum / Math.Max(1, onsets.Length - lag);
        }
        double energy = 0;
        foreach (var v in onsets)
            energy += (v - mean) * (v - mean);
        energy /= Math.Max(1, onsets.Length);
        var (best, bestScore) = (0, double.NegativeInfinity);
        var scores = new double[longest + 1];
        for (var lag = shortest; lag <= longest; lag++)
        {
            var bpm = perSecond * 60 / lag;
            var leaning = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 120) / 0.9, 2));
            scores[lag] = leaning * (lined[lag] + 0.5 * lined[lag * 2] + 0.25 * lined[lag * 2 - 1] + 0.25 * lined[lag * 2 + 1]);
            if (scores[lag] > bestScore)
                (best, bestScore) = (lag, scores[lag]);
        }
        // Music lines up with itself a beat on by a good part of its own strength; speech and noise hardly at all.
        if (best == 0 || energy <= 1e-9 || lined[best] / energy < 0.08)
            return null;
        // Between looks: the top of the curve through the best gap and its neighbours.
        if (best > shortest && best < longest)
        {
            var (a, b, c) = (scores[best - 1], scores[best], scores[best + 1]);
            var bend = a - 2 * b + c;
            if (bend < 0)
                return best + 0.5 * (a - c) / bend;
        }
        return best;
    }

    /// <summary>
    /// The beats, as looks: the chain of moments that are strong new starts and about a beat apart, the best such chain
    /// through the whole sound (each moment's score is its own strength plus the best chain's before it, less what
    /// the gap is off the beat), followed back from its end; beats before the music starts or after it ends are left out.
    /// </summary>
    private static List<int> Track(float[] onsets, double period)
    {
        const double tight = 100;
        var n = onsets.Length;
        var score = new double[n];
        var from = new int[n];
        for (var t = 0; t < n; t++)
        {
            var (best, bestAt) = (double.NegativeInfinity, -1);
            for (var p = Math.Max(0, (int)Math.Round(t - 2 * period)); p <= t - period / 2 && p < t; p++)
            {
                var off = Math.Log((t - p) / period);
                var s = score[p] - tight * off * off;
                if (s > best)
                    (best, bestAt) = (s, p);
            }
            (score[t], from[t]) = (onsets[t] + (bestAt >= 0 ? Math.Max(best, 0) : 0), bestAt >= 0 && best > 0 ? bestAt : -1);
        }
        // From the best moment in the last beat's length, back along the chain.
        var end = n - 1;
        for (var t = Math.Max(0, n - (int)Math.Ceiling(period)); t < n; t++)
            if (score[t] > score[end])
                end = t;
        var beats = new List<int>();
        for (var t = end; t >= 0; t = from[t])
            beats.Add(t);
        beats.Reverse();
        // Not through silence at either end: from the first beat to the last that is a real start of something.
        var loud = beats.Count > 0 ? beats.Select(b => (double)onsets[b]).OrderBy(v => v).ElementAt(beats.Count / 2) * 0.25 : 0;
        while (beats.Count > 0 && onsets[beats[0]] < loud)
            beats.RemoveAt(0);
        while (beats.Count > 0 && onsets[beats[^1]] < loud)
            beats.RemoveAt(beats.Count - 1);
        return beats;
    }

    /// <summary>A fast Fourier transform in place (a length that is a power of two).</summary>
    private static void Fourier(Complex[] a)
    {
        var n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
                (a[i], a[j]) = (a[j], a[i]);
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var turn = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var k = 0; k < len / 2; k++)
                {
                    var (u, v) = (a[i + k], a[i + k + len / 2] * w);
                    (a[i + k], a[i + k + len / 2]) = (u + v, u - v);
                    w *= turn;
                }
            }
        }
    }
}
