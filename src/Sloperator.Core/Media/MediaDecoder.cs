using Sloperator.Core.Platform;

namespace Sloperator.Core.Media;

/// <summary>What a video or sound file holds.</summary>
public sealed record MediaInfo
{
    public TimeSpan Duration { get; init; }
    public bool HasVideo { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public bool HasAudio { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }

    /// <summary>"video 640x640, 81 frames at 16 fps (5.1 s), with sound" or "sound 12.0 s, 44.1 kHz stereo".</summary>
    public string Describe()
    {
        var seconds = $"{Duration.TotalSeconds:0.0} s";
        if (HasVideo)
        {
            var frames = FrameRate > 0 ? $"{Math.Round(Duration.TotalSeconds * FrameRate):0} frames at {FrameRate:0.##} fps ({seconds})" : seconds;
            return $"video {Width}x{Height}, {frames}, " + (HasAudio ? $"with sound ({Rate()})" : "no sound");
        }
        return HasAudio ? $"sound {seconds}, {Rate()}" : $"media {seconds}";

        string Rate() => $"{SampleRate / 1000.0:0.#} kHz {(Channels == 1 ? "mono" : Channels == 2 ? "stereo" : Channels + " channels")}";
    }
}

/// <summary>A video's frames one after another, scaled as asked when it was opened.</summary>
public interface IVideoReader : IDisposable
{
    int Width { get; }
    int Height { get; }
    TimeSpan Duration { get; }
    double FrameRate { get; }

    /// <summary>The next frame and when it shows; null at the end.</summary>
    (TimeSpan Time, RawImage Frame)? Next();

    /// <summary>Whether the frames have clear parts (an animated GIF's): their fourth byte is how solid each pixel is, the colours already scaled by it.</summary>
    bool HasAlpha => false;

    /// <summary>Goes to <paramref name="time"/>; the next frame is the one showing then (or the first after it).</summary>
    void Seek(TimeSpan time);
}

/// <summary>A sound as interleaved 32-bit float samples, at its own rate and channel count.</summary>
public interface IAudioReader : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    TimeSpan Duration { get; }

    /// <summary>Fills <paramref name="buffer"/> with up to <paramref name="count"/> samples (all channels counted); 0 at the end.</summary>
    int Read(float[] buffer, int offset, int count);

    void Seek(TimeSpan time);
}

/// <summary>Reads video and sound files: the OS's own decoders where there are some, ffmpeg otherwise.</summary>
public interface IMediaDecoder
{
    /// <summary>What the file holds; null when it can't be read.</summary>
    MediaInfo? Probe(string path);

    /// <summary>Its video, scaled to fit <paramref name="maxSide"/> (0: as it is); null when it has none or can't be read.</summary>
    IVideoReader? OpenVideo(string path, int maxSide);

    /// <summary>Its sound; null when it has none or can't be read.</summary>
    IAudioReader? OpenAudio(string path);

    /// <summary>The ffmpeg program a decoder reads with, when it uses one: also the agent's, to cut or join media.</summary>
    string? Ffmpeg => null;

    /// <summary>Whether <see cref="Write"/> makes video files here.</summary>
    bool Writes => false;

    /// <summary>A new video file at <paramref name="path"/> (.mp4), to write frame after frame; null where the decoder writes none.</summary>
    IMediaWriter? Write(string path, MediaWriterOptions options) => null;
}

/// <summary>Plays a sound through the PC's speakers, for the chat's audio and video players.</summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>Plays <paramref name="source"/> from where it stands until it ends or is stopped; the source belongs to the output from then on.</summary>
    void Play(IAudioReader source);

    void Pause();
    void Resume();

    /// <summary>How far the sound has played, counted from where the source stood at <see cref="Play"/>.</summary>
    TimeSpan Played { get; }

    /// <summary>Played everything it was given.</summary>
    bool Ended { get; }
}

/// <summary>One decoder tried after another: the OS's first, ffmpeg for what it can't read.</summary>
public sealed class FallbackDecoder(params IMediaDecoder[] decoders) : IMediaDecoder
{
    private static readonly string[] SoundFiles = [".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".ogg", ".oga", ".opus", ".aiff", ".aif"];

    public MediaInfo? Probe(string path)
    {
        // A file the first reader finds only sound in may hold a picture the next can read (a video coded in a way
        // Windows has no decoder for): only a sound file's own kind is taken as sound without asking on.
        MediaInfo? first = null;
        foreach (var decoder in decoders)
        {
            if (Try(() => decoder.Probe(path)) is not { } info)
                continue;
            if (info.HasVideo || SoundFiles.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                return info;
            first ??= info;
        }
        return first;
    }

    public IVideoReader? OpenVideo(string path, int maxSide) => decoders.Select(d => Try(() => d.OpenVideo(path, maxSide))).FirstOrDefault(r => r is not null);

    public IAudioReader? OpenAudio(string path) => decoders.Select(d => Try(() => d.OpenAudio(path))).FirstOrDefault(r => r is not null);

    public string? Ffmpeg => decoders.Select(d => d.Ffmpeg).FirstOrDefault(f => f is not null);

    public bool Writes => decoders.Any(d => d.Writes);

    public IMediaWriter? Write(string path, MediaWriterOptions options) => decoders.Where(d => d.Writes).Select(d => d.Write(path, options)).FirstOrDefault(w => w is not null);

    private static T? Try<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or FormatException)
        {
            return null;
        }
    }
}

/// <summary>What a video file is written as: its frame (even sides), its frames a second, and whether it has sound.</summary>
/// <param name="Quality">"draft", "best", or anything else for the usual: how many bits a second of picture gets.</param>
public sealed record MediaWriterOptions(int Width, int Height, int Fps, bool Sound = false, string? Quality = null)
{
    /// <summary>The sound a writer takes: stereo at 48 kHz, as interleaved floats.</summary>
    public const int SoundRate = 48000, SoundChannels = 2;

    /// <summary>
    /// The picture's bits a second: a fifth of a bit for each pixel of each frame as usual (12 Mbit/s at 1080p30, where
    /// a drawn scene's flat colours and a filmed clip both come out clean), half that for a draft, half as much again for the best.
    /// </summary>
    public int VideoBitrate => (int)Math.Clamp(Width * (long)Height * Fps * (Quality switch { "draft" => 0.1, "best" => 0.3, _ => 0.2 }), 400_000, 60_000_000);

    /// <summary>How good the picture is kept, of 100, where a writer encodes by quality: the bits then follow what each stretch needs.</summary>
    public int VideoQuality => Quality switch { "draft" => 60, "best" => 92, _ => 80 };
}

/// <summary>
/// Writes a video file (an MP4: H.264 with AAC sound) frame after frame, with what the OS has for it, so a clip is
/// made and a video joined where there is no ffmpeg. The frames and the sound are given in step, some sound after
/// each frame or so; the file is whole only after <see cref="Finish"/>.
/// </summary>
public interface IMediaWriter : IDisposable
{
    /// <summary>The next frame, at the size the writer was opened with.</summary>
    void Frame(RawImage frame);

    /// <summary>The sound that comes next: stereo at 48 kHz, interleaved (<see cref="MediaWriterOptions.SoundRate"/>).</summary>
    void Sound(ReadOnlySpan<float> samples);

    /// <summary>Ends the file; what was written is a video from here on.</summary>
    void Finish();
}
