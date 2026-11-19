namespace Sloperator.Core.Media;

/// <summary>
/// A sound made faster or slower without its pitch changing, as a clip sped up or slowed down keeps its voices
/// (what ffmpeg's atempo does): short stretches of it laid over each other closer together or further apart, each
/// taken from where it lines up best with the one before (WSOLA), so no click or flutter is heard where they meet.
/// </summary>
public static class SoundStretch
{
    /// <summary><paramref name="samples"/> (interleaved) played <paramref name="speed"/> times as fast: half as long at 2, twice as long at 0.5.</summary>
    public static float[] Tempo(float[] samples, int channels, int rate, double speed)
    {
        channels = Math.Max(1, channels);
        var frames = samples.Length / channels;
        if (frames == 0 || Math.Abs(speed - 1) < 1e-6 || speed <= 0)
            return samples;
        // 40 ms stretches, half over the one before; each looked for up to 10 ms either side of where it would be taken.
        var size = Math.Max(64, rate * 40 / 1000 / 2 * 2);
        var (hop, reach) = (size / 2, Math.Max(8, rate * 10 / 1000));
        var outFrames = Math.Max(1, (int)Math.Round(frames / speed));
        var result = new float[(outFrames + size) * channels];
        // One channel to line them up by: all of them added.
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += samples[f * channels + c];
            mono[f] = sum;
        }
        var window = new float[size];
        for (var i = 0; i < size; i++)
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size));
        var before = 0;
        for (var k = 0; k * hop < outFrames; k++)
        {
            var ideal = (int)Math.Round(k * hop * speed);
            var start = Math.Clamp(ideal, 0, Math.Max(0, frames - 1));
            if (k > 0)
            {
                // What would have come next had the stretch before played on: the one taken is the one most like it.
                var follows = before + hop;
                var best = double.NegativeInfinity;
                for (var d = -reach; d <= reach; d += 2)
                {
                    var at = ideal + d;
                    if (at < 0 || at + hop > frames || follows + hop > frames)
                        continue;
                    double like = 0, loud = 1e-9;
                    for (var i = 0; i < hop; i += 4)
                    {
                        like += mono[at + i] * mono[follows + i];
                        loud += mono[at + i] * mono[at + i];
                    }
                    like /= Math.Sqrt(loud);
                    if (like > best)
                        (best, start) = (like, at);
                }
            }
            for (var i = 0; i < size; i++)
            {
                var from = start + i;
                if (from >= frames)
                    break;
                // The very first half isn't faded in: nothing lies under it.
                var gain = k == 0 && i < hop ? 1 : window[i];
                var to = (k * hop + i) * channels;
                for (var c = 0; c < channels; c++)
                    result[to + c] += samples[from * channels + c] * gain;
            }
            before = start;
        }
        Array.Resize(ref result, outFrames * channels);
        return result;
    }
}
