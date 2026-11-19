using Sloperator.Core.Media;

namespace Sloperator.Core.Video;

/// <summary>
/// The video's whole sound, mixed as joining it with ffmpeg mixes it (<see cref="VideoMontage.Plan"/>): each
/// clip's own sound as its shot cuts it (its start trimmed, backwards, at its speed with its pitch kept, silent
/// from its freeze, at its volume, and at half under a song), a shot's spoken line in place of the clip's sound,
/// one clip's sound fading into the next's through a transition, each sound on the sound lines from where it
/// starts (trimmed, cut, faded, at its volume and its line's), the music quieter while someone speaks, the end
/// faded out, and nothing over full scale. Stereo at 48 kHz, for the built-in writer.
/// </summary>
public static class VideoSoundMix
{
    public const int Rate = MediaWriterOptions.SoundRate, Channels = MediaWriterOptions.SoundChannels;

    /// <summary>How much of its volume the music keeps while someone speaks, and how long it takes to get there and back.</summary>
    private const double Under = 0.35, UnderRamp = 0.08;

    /// <summary>The mix: interleaved, as long as the video.</summary>
    /// <param name="notes">Takes what the video is without: a sound whose file isn't there or can't be read.</param>
    public static float[] Mix(Storyboard board, IMediaDecoder decoder, List<string> notes)
    {
        var (starts, length) = board.Timing();
        var frames = Math.Max(0, (int)Math.Round(length * Rate));
        var mix = new float[frames * Channels];
        var read = new Dictionary<string, float[]?>(StringComparer.OrdinalIgnoreCase);
        // A file's sound, read once however many shots are cut from it; null for none it has.
        float[]? Sound(string file)
        {
            if (read.TryGetValue(file, out var known))
                return known;
            float[]? samples = null;
            try
            {
                if (File.Exists(file) && decoder.OpenAudio(file) is { } reader)
                    using (reader)
                    {
                        var heard = SoundEdit.Read(reader);
                        samples = SoundEdit.Convert(heard, Rate, Channels).Samples;
                        // A sound of one channel goes to both at 0.707 of itself, as ffmpeg spreads it (as loud in all, not
                        // twice over): at full strength a voice-over or a mono song was 3 dB louder here than in ffmpeg's join.
                        if (heard.Channels == 1)
                            for (var i = 0; i < samples.Length; i++)
                                samples[i] *= 0.70710678f;
                    }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException) { }
            return read[file] = samples;
        }

        var sounds = new List<VideoSound>();
        foreach (var sound in board.Sounds)
            if (File.Exists(board.SoundFile(sound)))
                sounds.Add(sound);
            else
                notes.Add(sound.Source == VideoSound.FromFile ? $"The sound file {board.SoundFile(sound)} isn't there, so the video is without it." : $"The {(sound.Source == VideoSound.Foley ? "foley" : "song")} {sound.Label} isn't made yet, so the video is without it.");
        var shotVolume = sounds.Any(s => s.Source == VideoSound.Song) ? 0.5 : 1.0;
        var speaking = new List<(double From, double To)>();

        for (var i = 0; i < board.Shots.Count; i++)
        {
            var shot = board.Shots[i];
            float[]? own;
            double volume;
            if (Storyboard.Speaks(shot) && Sound(board.ShotAudio(shot)) is { } line)
            {
                // A shot's line is heard as the voice model said it, clean, not as the video model gave it back.
                (own, volume) = (line, shot.Volume ?? 1);
                speaking.Add((starts[i], starts[i] + shot.Seconds));
            }
            else if ((shot.Volume ?? 1) > 0 && Sound(board.ShotVideo(shot)) is { } clip)
                (own, volume) = (ShotSound(shot, clip), shotVolume * (shot.Volume ?? 1));
            else
                continue;
            // Through a transition one clip's sound gives way to the next's.
            var (fadeIn, fadeOut) = (i > 0 ? Storyboard.Overlap(shot) : 0, i + 1 < board.Shots.Count ? Storyboard.Overlap(board.Shots[i + 1]) : 0);
            Add(mix, own, 0, starts[i], shot.Seconds, volume, fadeIn, fadeOut, null);
        }

        // Quieter while someone speaks: a shot's line, and a voice-over for as long as it plays.
        foreach (var voice in sounds.Where(s => s.Source == VideoSound.Speech))
        {
            var plays = voice.Seconds > 0 ? voice.Seconds : Sound(board.SoundFile(voice)) is { } v ? Math.Max(0.1, v.Length / Channels / (double)Rate - voice.Trim) : 0;
            if (plays > 0)
                speaking.Add((voice.Start, voice.Start + plays));
        }
        var duck = speaking.Count == 0 || sounds.All(s => s.Source == VideoSound.Speech) ? null : Ducking(speaking, frames);
        foreach (var sound in sounds)
        {
            if (Sound(board.SoundFile(sound)) is not { } samples)
            {
                notes.Add($"The sound {sound.Label} can't be read on this PC, so the video is without it.");
                continue;
            }
            var plays = sound.Seconds > 0 ? sound.Seconds : Math.Max(0.1, length - sound.Start);
            var (fadeIn, fadeOut) = (Math.Min(sound.FadeIn, plays / 2), Math.Min(sound.FadeOut, plays / 2));
            Add(mix, samples, sound.Trim, sound.Start, plays, sound.Volume * board.LineVolume(sound.SoundLine), fadeIn, fadeOut, sound.Source == VideoSound.Speech ? null : duck);
        }

        // A sound cut where the video ends fades out over longer than the picture does.
        var (_, pictureOut) = VideoMontage.Fades(board);
        pictureOut = Math.Min(pictureOut, length / 3);
        var runsOn = sounds.Any(s => s.Seconds <= 0 || s.Start + s.Seconds > length + 0.05);
        var soundFade = runsOn ? Math.Min(2.5, length / 3) : Math.Max(pictureOut, 0.05);
        var fadeFrames = Math.Min(frames, (int)Math.Round(soundFade * Rate));
        for (var f = 0; f < fadeFrames; f++)
        {
            var gain = (float)(1 - (f + 1) / (double)fadeFrames);
            var at = (frames - fadeFrames + f) * Channels;
            for (var c = 0; c < Channels; c++)
                mix[at + c] *= gain;
        }
        Limit(mix, 0.95f);
        return mix;
    }

    /// <summary>A clip's sound as its shot cuts its picture: from its trim, backwards, at its speed, and none from where it freezes.</summary>
    internal static float[] ShotSound(VideoShot shot, float[] clip)
    {
        var sound = clip;
        if (shot.In > 0)
        {
            var skip = Math.Min(sound.Length, (int)Math.Round(shot.In * Rate) * Channels);
            sound = sound[skip..];
        }
        if (shot.Reverse)
        {
            var back = new float[sound.Length];
            var n = sound.Length / Channels;
            for (var f = 0; f < n; f++)
                for (var c = 0; c < Channels; c++)
                    back[f * Channels + c] = sound[(n - 1 - f) * Channels + c];
            sound = back;
        }
        if (shot.Speed is { } speed)
            sound = SoundStretch.Tempo(sound, Channels, Rate, speed);
        if (shot.Freeze is { } freeze)
        {
            var keep = Math.Min(sound.Length, (int)Math.Round(Math.Max(0.01, freeze) * Rate) * Channels);
            sound = sound[..keep];
        }
        return sound;
    }

    /// <summary>
    /// Adds a sound to the mix: from <paramref name="trim"/> seconds into it, at <paramref name="start"/> in the video, for
    /// <paramref name="seconds"/> at most, faded in over its first <paramref name="fadeIn"/> seconds and out over its last
    /// <paramref name="fadeOut"/>, each sample by <paramref name="duck"/>'s gain then where there is one.
    /// </summary>
    private static void Add(float[] mix, float[] sound, double trim, double start, double seconds, double volume, double fadeIn, double fadeOut, float[]? duck)
    {
        var frames = mix.Length / Channels;
        var (from, at, count) = ((long)Math.Round(trim * Rate), (long)Math.Round(start * Rate), (long)Math.Round(seconds * Rate));
        var (inFrames, outFrames) = ((long)Math.Round(fadeIn * Rate), (long)Math.Round(fadeOut * Rate));
        var has = sound.Length / Channels;
        for (long f = 0; f < count; f++)
        {
            var (source, target) = (from + f, at + f);
            if (source >= has || target >= frames)
                break;
            if (source < 0 || target < 0)
                continue;
            var gain = volume;
            if (f < inFrames)
                gain *= (f + 1) / (double)inFrames;
            if (f >= count - outFrames)
                gain *= (count - f) / (double)outFrames;
            if (duck is not null)
                gain *= duck[target];
            for (var c = 0; c < Channels; c++)
                mix[target * Channels + c] += sound[source * Channels + c] * (float)gain;
        }
    }

    /// <summary>The music's gain for each frame of the video: <see cref="Under"/> while someone speaks, eased down before and up after.</summary>
    private static float[] Ducking(List<(double From, double To)> speaking, int frames)
    {
        var gain = new float[frames];
        Array.Fill(gain, 1f);
        var ramp = Math.Max(1, (int)Math.Round(UnderRamp * Rate));
        foreach (var (from, to) in speaking)
        {
            var (a, b) = ((int)Math.Round(from * Rate), (int)Math.Round(to * Rate));
            for (var f = Math.Max(0, a - ramp); f < Math.Min(frames, b + ramp); f++)
            {
                var outside = f < a ? a - f : f >= b ? f - b + 1 : 0;
                gain[f] = Math.Min(gain[f], (float)(Under + (1 - Under) * outside / ramp));
            }
        }
        return gain;
    }

    /// <summary>
    /// Keeps the mix under <paramref name="ceiling"/>: where it would go over, it is turned down from 5 ms before, and let
    /// back up over the next 50 ms, so a loud moment is held rather than clipped. Left as it is when nothing goes over.
    /// </summary>
    internal static void Limit(float[] mix, float ceiling)
    {
        var frames = mix.Length / Channels;
        var over = false;
        for (var i = 0; i < mix.Length && !over; i++)
            over = Math.Abs(mix[i]) > ceiling;
        if (!over)
            return;
        var need = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var peak = 0f;
            for (var c = 0; c < Channels; c++)
                peak = Math.Max(peak, Math.Abs(mix[f * Channels + c]));
            need[f] = peak > ceiling ? ceiling / peak : 1f;
        }
        // The lowest gain needed within the next 5 ms, then averaged over the 5 ms before: down smoothly by the time the peak comes.
        var ahead = Math.Max(1, Rate * 5 / 1000);
        var low = new float[frames];
        var queue = new int[frames];
        var (head, tail) = (0, 0);
        for (var j = 0; j < frames + ahead; j++)
        {
            if (j < frames)
            {
                while (tail > head && need[queue[tail - 1]] >= need[j])
                    tail--;
                queue[tail++] = j;
            }
            var f = j - ahead;
            while (tail > head && queue[head] < f)
                head++;
            if (f >= 0)
                low[f] = tail > head ? need[queue[head]] : 1f;
        }
        var release = 1f / (Rate * 50 / 1000);
        double sum = 0;
        var gain = 1f;
        for (var f = 0; f < frames; f++)
        {
            sum += low[f];
            if (f >= ahead)
                sum -= low[f - ahead];
            var smooth = (float)(sum / Math.Min(f + 1, ahead));
            gain = Math.Min(smooth, gain + release);
            for (var c = 0; c < Channels; c++)
                mix[f * Channels + c] *= gain;
        }
    }
}
