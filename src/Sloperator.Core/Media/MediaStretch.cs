namespace Sloperator.Core.Media;

/// <summary>
/// A stretch of a video or sound file read as a clip of its own, timed from zero: what a shot shows of a long
/// recording it is cut from. A shot's look, the cut into it and the frame it ends on were all taken from the whole
/// file, so five seconds out of twenty minutes were judged by sixteen frames of the twenty minutes.
/// </summary>
public static class MediaStretch
{
    /// <summary>
    /// <paramref name="decoder"/> with <paramref name="path"/> read as its stretch from <paramref name="from"/> to
    /// <paramref name="to"/> seconds, every other file as it is; the decoder itself when the stretch is all of the file
    /// (to within a quarter of a second at its end, as a model's clip runs a frame or two over its shot).
    /// </summary>
    public static IMediaDecoder Of(IMediaDecoder decoder, string path, double from, double to)
    {
        from = Math.Max(0, from);
        if (from < 0.01 && (decoder.Probe(path) is not { } info || to > info.Duration.TotalSeconds - 0.25))
            return decoder;
        return new Stretched(decoder, Path.GetFullPath(path), from, Math.Max(from, to));
    }

    private sealed class Stretched(IMediaDecoder inner, string path, double from, double to) : IMediaDecoder
    {
        private bool Is(string file) => string.Equals(Path.GetFullPath(file), path, StringComparison.OrdinalIgnoreCase);

        public MediaInfo? Probe(string file)
        {
            var info = inner.Probe(file);
            return info is not null && Is(file) ? info with { Duration = TimeSpan.FromSeconds(Math.Max(0, Math.Min(to, info.Duration.TotalSeconds) - from)) } : info;
        }

        public IVideoReader? OpenVideo(string file, int maxSide)
        {
            var reader = inner.OpenVideo(file, maxSide);
            return reader is not null && Is(file) ? new Video(reader, from, to) : reader;
        }

        public IAudioReader? OpenAudio(string file)
        {
            var reader = inner.OpenAudio(file);
            return reader is not null && Is(file) ? new Sound(reader, from, to) : reader;
        }

        public string? Ffmpeg => inner.Ffmpeg;

        public bool Writes => inner.Writes;

        public IMediaWriter? Write(string file, MediaWriterOptions options) => inner.Write(file, options);
    }

    /// <summary>The frames of <paramref name="inner"/> that show from <paramref name="from"/> until <paramref name="to"/>, timed from zero.</summary>
    public sealed class Video(IVideoReader inner, double from, double to) : IVideoReader
    {
        private bool _placed;

        public int Width => inner.Width;
        public int Height => inner.Height;
        public double FrameRate => inner.FrameRate;
        public bool HasAlpha => inner.HasAlpha;
        public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0, Math.Min(to, inner.Duration.TotalSeconds) - from));

        public (TimeSpan Time, Platform.RawImage Frame)? Next()
        {
            if (!_placed)
                Seek(TimeSpan.Zero);
            // The frame showing at the stretch's start began before it: it is the stretch's first, at zero.
            return inner.Next() is { } next && next.Time.TotalSeconds < to - 0.0005
                ? (TimeSpan.FromSeconds(Math.Max(0, next.Time.TotalSeconds - from)), next.Frame)
                : null;
        }

        public void Seek(TimeSpan time)
        {
            inner.Seek(TimeSpan.FromSeconds(from + Math.Max(0, time.TotalSeconds)));
            _placed = true;
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>The sound of <paramref name="inner"/> from <paramref name="from"/> until <paramref name="to"/>.</summary>
    public sealed class Sound(IAudioReader inner, double from, double to) : IAudioReader
    {
        private long _left = -1;

        public int SampleRate => inner.SampleRate;
        public int Channels => inner.Channels;
        public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0, Math.Min(to, inner.Duration.TotalSeconds) - from));

        public int Read(float[] buffer, int offset, int count)
        {
            if (_left < 0)
                Seek(TimeSpan.Zero);
            var read = _left > 0 ? inner.Read(buffer, offset, (int)Math.Min(count, _left)) : 0;
            _left -= read;
            return read;
        }

        public void Seek(TimeSpan time)
        {
            var at = Math.Clamp(time.TotalSeconds, 0, Duration.TotalSeconds);
            inner.Seek(TimeSpan.FromSeconds(from + at));
            _left = (long)Math.Round((Duration.TotalSeconds - at) * SampleRate) * Channels;
        }

        public void Dispose() => inner.Dispose();
    }
}
