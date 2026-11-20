using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using static Sloperator.Platform.Windows.ComAbi;

namespace Sloperator.Platform.Windows;

/// <summary>
/// Video frames and sound through Windows' own Media Foundation (its Source Reader): MP4/H.264,
/// MOV, WMV, MP3, AAC, FLAC and WAV play on every Windows 10 and 11 without anything installed.
/// The COM interfaces are called through their vtables (<see cref="ComAbi"/>).
/// <para>
/// A file is given as a player shows it: upright where a phone wrote it turned, at the width its pixels are meant
/// to be shown at, every frame it holds, and on the film's own clock, the picture and the sound starting together
/// (<see cref="Origin"/>).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class MediaFoundationDecoder : IMediaDecoder
{
    public MediaInfo? Probe(string path)
    {
        using var video = TryOpen(path, video: true, maxSide: 0);
        using var audio = TryOpen(path, video: false, maxSide: 0);
        if (video is null && audio is null)
            return null;
        return new MediaInfo
        {
            Duration = (video ?? audio)!.Duration,
            HasVideo = video is not null,
            Width = video?.NativeWidth ?? 0,
            Height = video?.NativeHeight ?? 0,
            FrameRate = video?.FrameRate ?? 0,
            HasAudio = audio is not null,
            SampleRate = audio?.SampleRate ?? 0,
            Channels = audio?.Channels ?? 0,
        };
    }

    public IVideoReader? OpenVideo(string path, int maxSide) => TryOpen(path, video: true, maxSide);

    public IAudioReader? OpenAudio(string path) => TryOpen(path, video: false, 0);

    private static Reader? TryOpen(string path, bool video, int maxSide)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return new Reader(path, video, maxSide, OriginOf(path));
        }
        catch (COMException)
        {
            return null;
        }
    }

    // ---- The film's own clock --------------------------------------------------------------

    /// <summary>
    /// How a file's times, as Media Foundation gives them, stand to the film's own clock. Media Foundation gets an
    /// MP4's edit list wrong: a clip with frames that lean on later ones (most MP4s that didn't come from a phone)
    /// has its first frame at 0.07 to 0.17 s where it shows at nought, so the picture ran that far behind the sound;
    /// an AAC sound keeps the 21 ms of nothing its encoder starts with; a film that starts late on its clock, or a
    /// broadcast stream, has its first frame seconds in. So the first frame and the first sound are measured once for
    /// a file, the MP4's own edit list is read (<see cref="Mp4Tracks"/>), and every time is set from that.
    /// </summary>
    /// <param name="VideoZero">Media Foundation's time for the film's nought, for the picture.</param>
    /// <param name="VideoFrom">The film time the picture starts at: frames before it are not part of the film.</param>
    /// <param name="SoundZero">Media Foundation's time for the film's nought, for the sound.</param>
    /// <param name="SoundFirst">Media Foundation's time for the sound's first sample.</param>
    /// <param name="Length">How long the film runs, where the file's own count is to be set right.</param>
    /// <param name="FrameRate">Frames a second as the first frames come, where the file's own figure is far off it; else 0.</param>
    /// <param name="Colours">How the picture's colours are written, where the file says.</param>
    private sealed record Origin(TimeSpan VideoZero, TimeSpan VideoFrom, TimeSpan SoundZero, TimeSpan SoundFirst, TimeSpan? Length, double FrameRate, VideoColours? Colours = null)
    {
        public static readonly Origin Plain = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null, 0);
    }

    private static readonly ConcurrentDictionary<string, (long Size, DateTime Written, Origin Origin)> Origins = new(StringComparer.OrdinalIgnoreCase);

    private static Origin OriginOf(string path)
    {
        var file = new FileInfo(path);
        if (Origins.TryGetValue(file.FullName, out var known) && known.Size == file.Length && known.Written == file.LastWriteTimeUtc)
            return known.Origin;
        var origin = Measure(path);
        if (Origins.Count > 512)
            Origins.Clear();
        Origins[file.FullName] = (file.Length, file.LastWriteTimeUtc, origin);
        return origin;
    }

    private static Origin Measure(string path)
    {
        TimeSpan? firstFrame = null, firstSound = null;
        var (length, stated, measured) = (TimeSpan.Zero, 0.0, 0.0);
        try
        {
            using var video = new Reader(path, video: true, maxSide: 96, Origin.Plain);
            (length, stated) = (video.Duration, video.FrameRate);
            var times = new List<double>();
            while (times.Count < 7 && video.ReadSample() is { } s)
                times.Add(s.Time.TotalSeconds);
            if (times.Count > 0)
                firstFrame = TimeSpan.FromSeconds(times[0]);
            if (times.Count > 2 && times[^1] - times[0] > 0.001)
            {
                // Times kept in whole milliseconds (an MKV's) give 30.3 for 30: the usual rate it is nearest, when it is near one.
                measured = (times.Count - 1) / (times[^1] - times[0]);
                var usual = ((double[])[24, 25, 30, 48, 50, 60, 120]).MinBy(u => Math.Abs(measured - u));
                if (Math.Abs(measured - usual) < usual * 0.012)
                    measured = usual;
            }
        }
        catch (COMException)
        {
        }
        try
        {
            using var sound = new Reader(path, video: false, maxSide: 0, Origin.Plain);
            if (length == TimeSpan.Zero)
                length = sound.Duration;
            firstSound = sound.First;
        }
        catch (COMException)
        {
        }
        if (firstFrame is null && firstSound is null)
            return Origin.Plain;
        // A file that says half or twice the frames a second it gives (an MKV here: 15 for 30) is taken at what it gives.
        var rate = measured > 0 && (stated <= 0 || measured / stated > 1.8 || measured / stated < 0.55) ? measured : 0;
        var first = TimeSpan.FromTicks(Math.Min((firstFrame ?? TimeSpan.MaxValue).Ticks, (firstSound ?? TimeSpan.MaxValue).Ticks));
        if (Mp4Tracks.Read(path) is not { } tracks)
            // No edit list to go by: the two keep the times they have to each other, and the earlier one starts the film.
            return new Origin(first, TimeSpan.Zero, first, firstSound ?? first, null, rate);
        var (picture, sounds, start) = Mp4Tracks.Starts(tracks);
        var (videoZero, videoFrom, soundZero) = (firstFrame ?? first, TimeSpan.Zero, firstSound ?? first);
        if (picture is not null && firstFrame is { } frame)
        {
            videoFrom = TimeSpan.FromSeconds(Math.Max(0, picture.Lead - start));
            videoZero = frame - TimeSpan.FromSeconds(picture.FirstShown - picture.From) - videoFrom;
        }
        if (sounds is not null && firstSound is { } sample)
            soundZero = sample + TimeSpan.FromSeconds(sounds.From) - TimeSpan.FromSeconds(Math.Max(0, sounds.Lead - start));
        // The file's count where it has one (less what the film starts late by); a file in fragments has none, so its tracks' own.
        TimeSpan? whole = length > TimeSpan.Zero
            ? (start > 0 ? length - TimeSpan.FromSeconds(start) : null)
            : TimeSpan.FromSeconds(new[] { picture, sounds }.Where(t => t is not null).Select(t => t!.Lead - start + t.Seconds - t.From).DefaultIfEmpty(0).Max());
        return new Origin(videoZero, videoFrom, soundZero, firstSound ?? first, whole is { } w && w > TimeSpan.Zero ? w : null, rate, picture?.Colours);
    }

    // ---- Media Foundation ------------------------------------------------------------------

    private const uint MF_VERSION = 0x00020070;
    private const uint FirstVideoStream = 0xFFFFFFFC, FirstAudioStream = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;
    private const uint EndOfStream = 0x2, StreamError = 0x1, TypeChanged = 0x20;

    private static readonly Guid AdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid VideoType = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid AudioType = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid Avc1 = new("31435641-0000-0010-8000-00AA00389B71");
    private static readonly Guid MotionJpeg = new("47504A4D-0000-0010-8000-00AA00389B71");
    private static readonly Guid Wmv1 = new("31564D57-0000-0010-8000-00AA00389B71");
    private static readonly Guid Wmv2 = new("32564D57-0000-0010-8000-00AA00389B71");
    private static readonly Guid Wmv3 = new("33564D57-0000-0010-8000-00AA00389B71");
    private static readonly Guid FloatAudio = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid Mp3Audio = new("00000055-0000-0010-8000-00AA00389B71");
    private static readonly Guid MpegAudio = new("00000050-0000-0010-8000-00AA00389B71");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRateKey = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid Rotation = new("c380465d-2271-428c-9b83-ecea3b4a85c1");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid AudioRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid AudioBits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid DurationKey = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid SourceReaderEx = new("7b981cf0-560e-4116-9875-b099895f23d7");
    private static readonly Guid VideoProcessors = new("302ea3fc-aa5f-47f9-9f7a-c2188bb16302");
    private static readonly Guid NoFrameRateChange = new("2c0afa19-7a97-4d5a-9ee8-16d4fc518d8c");

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
    }

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out nint attributes, uint size);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);

    private static readonly Lazy<bool> Started = new(() => MFStartup(MF_VERSION, 0) >= 0);

    // IMFAttributes: GetUINT32 7, GetUINT64 8, GetGUID 10, SetUINT32 21, SetUINT64 22, SetGUID 24.
    private static Guid GetGuid(nint attrs, Guid key)
    {
        Guid v;
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attrs, 10))(attrs, &key, &v) >= 0 ? v : Guid.Empty;
    }

    private static int GetUInt32(nint attrs, Guid key, out uint value)
    {
        uint v;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(attrs, 7))(attrs, &key, &v);
        value = v;
        return hr;
    }

    private static int GetUInt64(nint attrs, Guid key, out ulong value)
    {
        ulong v;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(attrs, 8))(attrs, &key, &v);
        value = v;
        return hr;
    }

    private static int SetUInt32(nint attrs, Guid key, uint value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attrs, 21))(attrs, &key, value);
    private static int SetUInt64(nint attrs, Guid key, ulong value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(attrs, 22))(attrs, &key, value);
    private static int SetGuid(nint attrs, Guid key, Guid value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attrs, 24))(attrs, &key, &value);

    /// <summary>
    /// A frame as the decoder has it (NV12: a plane of brightness, then one of the two colour values at half the size
    /// each way) made blue, green, red and a solid fourth byte, by the standard and the range the file's colours are
    /// written in. The colour values, which lie beside every second pixel and between every second pair of rows, are
    /// spread to each pixel by its distance from them, so an edge of colour doesn't come out in steps.
    /// </summary>
    internal static byte[] ToBgra(byte[] nv12, int stride, int rows, int width, int height, bool old, bool full)
    {
        var bgra = new byte[width * height * 4];
        // In 1/8192ths: brightness stretched from TV's 16 to 235 where the file has it so, and each colour value's share.
        var (scale, floor) = full ? (8192, 0) : (9539, 16);
        var (rv, gu, gv, bu) = (old, full) switch
        {
            (true, true) => (11485, 2819, 5850, 14516),
            (true, false) => (13075, 3209, 6660, 16525),
            (false, true) => (12901, 1535, 3835, 15201),
            (false, false) => (14686, 1747, 4366, 17305),
        };
        var (across, down) = ((width + 1) / 2, (height + 1) / 2);
        void Row(int y)
        {
            fixed (byte* from = nv12, to = bgra)
            {
                var light = from + y * stride;
                // The colour row this row lies in, and the one it leans towards: three parts to one.
                var near = from + (long)stride * rows + Math.Min(down - 1, y >> 1) * stride;
                var far = from + (long)stride * rows + Math.Clamp((y & 1) == 0 ? (y >> 1) - 1 : (y >> 1) + 1, 0, down - 1) * stride;
                var pixel = to + (long)y * width * 4;
                for (var x = 0; x < width; x++, pixel += 4)
                {
                    var c = Math.Min(across - 1, x >> 1) * 2;
                    int u = 3 * near[c] + far[c], v = 3 * near[c + 1] + far[c + 1];
                    if ((x & 1) == 1)
                    {
                        // Between two colour values: half of each.
                        var next = Math.Min(across - 1, (x >> 1) + 1) * 2;
                        u = (u + 3 * near[next] + far[next]) >> 1;
                        v = (v + 3 * near[next + 1] + far[next + 1]) >> 1;
                    }
                    // u and v are four times the value: 512 is their middle.
                    var (cu, cv) = (u - 512, v - 512);
                    var l = (light[x] - floor) * scale + 4096;
                    var (r, g, b) = ((l + ((rv * cv) >> 2)) >> 13, (l - ((gu * cu + gv * cv) >> 2)) >> 13, (l + ((bu * cu) >> 2)) >> 13);
                    pixel[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                    pixel[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                    pixel[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                    pixel[3] = 255;
                }
            }
        }
        // A small picture row after row; a large one on every core.
        if (width * height < 200_000)
            for (var y = 0; y < height; y++)
                Row(y);
        else
            Parallel.For(0, height, Row);
        return bgra;
    }

    /// <summary>One file opened for its video or its sound.</summary>
    private sealed class Reader : IVideoReader, IAudioReader
    {
        private nint _reader;
        private readonly string _path;
        private readonly bool _video;
        private readonly uint _stream;
        private int _stride;
        /// <summary>The size the picture is asked for at.</summary>
        private (int W, int H) _size;
        /// <summary>The frames come as the decoder has them (NV12), to be made red, green and blue here.</summary>
        private bool _yuv;
        /// <summary>The picture's colours are in the older standard (BT.601), and over the whole range: as the file says, or as its size and kind have it.</summary>
        private readonly bool _old, _full;
        /// <summary>A Motion JPEG video: its values run over the whole range and nothing on the way says so, so they come as they are.</summary>
        private readonly bool _jpeg;
        /// <summary>
        /// The video processor is between the decoder and here. It brings a video the decoder knows to run over the
        /// whole range (an H.264 that says so) down to TV's range; straight from the decoder the values are as coded.
        /// </summary>
        private bool _processed;
        /// <summary>The file's length as Media Foundation counts it: no place past it can be jumped to.</summary>
        private readonly TimeSpan _length;
        /// <summary>
        /// A file this reader doesn't jump in: a broadcast stream from the first (<see cref="IsBroadcast"/>), any other
        /// once a jump has come back with nothing. It is read onwards to a place, and opened again to go back.
        /// </summary>
        private bool _noJumps;
        /// <summary>A jump was made and nothing has been read since.</summary>
        private bool _jumped;
        /// <summary>The film time a sound was last sought to.</summary>
        private TimeSpan _sought;
        /// <summary>Media Foundation's time of the last frame read.</summary>
        private TimeSpan _last = TimeSpan.MinValue;
        /// <summary>Media Foundation's time for the film's nought, for this stream (<see cref="Origin"/>).</summary>
        private readonly TimeSpan _zero;
        /// <summary>The film time the picture starts at: frames before it are left out.</summary>
        private readonly TimeSpan _from;
        /// <summary>Frames before this (Media Foundation's time) are read past: after a jump the reader starts at the key frame before.</summary>
        private TimeSpan _skipBefore = TimeSpan.MinValue;
        /// <summary>The time (Media Foundation's) a sound was sought to, until the sample holding it has been read: what is before it in that sample is left out.</summary>
        private TimeSpan? _soundFrom;
        /// <summary>
        /// An MP3 (or MP2): Media Foundation seeks it by guesswork, and its times after a seek were up to
        /// half a second off the sound read from the start (the waveform), so it is read from the start.
        /// </summary>
        private readonly bool _roughSeeks;
        /// <summary>How many values (samples × channels) are still to be read past, reading from the start to a time.</summary>
        private long _drop;
        /// <summary>How many values of nothing come first: the film's time before the sound's own start.</summary>
        private long _silence;
        /// <summary>The sound's first sample, read when the file was opened to learn what the sound really is.</summary>
        private (byte[] Bytes, TimeSpan Time)? _primed;
        private float[] _left = [];
        private int _leftAt;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int NativeWidth { get; }
        public int NativeHeight { get; }
        public double FrameRate { get; }
        public TimeSpan Duration { get; }
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        /// <summary>Media Foundation's time for the sound's first sample.</summary>
        public TimeSpan First { get; }

        public Reader(string path, bool video, int maxSide, Origin origin)
        {
            Start();
            if (!Started.Value)
                throw new COMException("Media Foundation is not available (an N edition of Windows without its Media Feature Pack?).");
            _path = path;
            _video = video;
            _stream = video ? FirstVideoStream : FirstAudioStream;
            (_zero, _from) = video ? (origin.VideoZero, origin.VideoFrom) : (origin.SoundZero, TimeSpan.Zero);
            _noJumps = IsBroadcast(path);
            Create();
            try
            {
                PropVariant duration;
                var durationKey = DurationKey;
                if (((delegate* unmanaged[Stdcall]<nint, uint, Guid*, PropVariant*, int>)Slot(_reader, 12))(_reader, MediaSource, &durationKey, &duration) >= 0)
                    Duration = _length = TimeSpan.FromTicks(duration.Value);
                if (origin.Length is { } length)
                    Duration = length;

                if (video)
                {
                    var (w, h, turned) = (0, 0, false);
                    nint native;
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(_reader, 5))(_reader, _stream, 0, &native));
                    try
                    {
                        // H.264 in ten bits, or with colour at full width or height: Windows' decoder takes the file and then never gives a frame.
                        var coded = GetGuid(native, SubType);
                        if ((coded == H264 || coded == Avc1) && GetUInt32(native, Mpeg2Profile, out var profile) >= 0 && profile is 44 or 110 or 122 or 144 or 244)
                            throw new COMException("The video is H.264 of a kind Windows doesn't decode (ten bits, or full colour detail).");
                        if (GetUInt64(native, FrameSize, out var size) >= 0)
                            (w, h) = ((int)(size >> 32), (int)(size & 0xFFFFFFFF));
                        // Its colours as the file says they are written; where it doesn't, as players take them: a
                        // JPEG's over the whole range in the older standard, any other by the picture's size.
                        // (A WMV has nowhere to say, and Windows itself takes one of any size in the older standard.)
                        _jpeg = coded == MotionJpeg;
                        _old = origin.Colours?.Old ?? (_jpeg || coded == Wmv1 || coded == Wmv2 || coded == Wmv3 || VideoColours.OldBySize(w, h));
                        _full = origin.Colours?.Full ?? _jpeg;
                        if (GetUInt64(native, FrameRateKey, out var rate) >= 0 && (rate & 0xFFFFFFFF) != 0)
                            FrameRate = (double)(rate >> 32) / (rate & 0xFFFFFFFF);
                        if (origin.FrameRate > 0)
                            FrameRate = origin.FrameRate;
                        // Pixels that aren't square (a DVD's, a tape camcorder's): the picture at the width it is shown at.
                        if (GetUInt64(native, PixelAspect, out var aspect) >= 0 && aspect >> 32 is var across and > 0 && (aspect & 0xFFFFFFFF) is var down and > 0 && across != down)
                            w = Math.Max(2, (int)Math.Round(w * (double)across / down / 2) * 2);
                        // A phone held upright writes the picture lying down and says how far to turn it.
                        turned = GetUInt32(native, Rotation, out var turn) >= 0 && turn % 180 == 90;
                    }
                    finally
                    {
                        Release(native);
                    }
                    if (turned)
                        (w, h) = (h, w);
                    (NativeWidth, NativeHeight) = (w, h);
                    if (maxSide > 0 && w > 0 && h > 0 && Math.Max(w, h) > maxSide)
                    {
                        var scale = (double)maxSide / Math.Max(w, h);
                        (w, h) = (Math.Max(2, (int)Math.Round(w * scale / 2) * 2), Math.Max(2, (int)Math.Round(h * scale / 2) * 2));
                    }
                    _size = (w, h);
                    Shape();
                }
                else
                {
                    nint native;
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(_reader, 5))(_reader, _stream, 0, &native));
                    try
                    {
                        var coded = GetGuid(native, SubType);
                        _roughSeeks = coded == Mp3Audio || coded == MpegAudio;
                    }
                    finally
                    {
                        Release(native);
                    }
                    Shape();
                    // The first sample: only once it is decoded does a sound say what it really is (an AAC at 22 kHz
                    // was first said to be at 44, and came out twice as fast and high).
                    _primed = ReadSample();
                    First = _primed?.Time ?? TimeSpan.Zero;
                    if (ReferenceEquals(origin, Origin.Plain))
                        _zero = First;
                    else
                        First = origin.SoundFirst;
                    Begin(TimeSpan.Zero);
                }
            }
            catch
            {
                Release(_reader);
                _reader = 0;
                throw;
            }
        }

        /// <summary>
        /// A broadcast stream (.ts, a camcorder's .mts and .m2ts): packets of 188 bytes, each starting with 0x47, with four
        /// bytes before each in a camcorder's. Windows jumps in one by guessing where a time lies and names the frames
        /// it finds there by times they don't have (a white frame came back as the one at 0.25 s of a film that turns
        /// white at 1 s), so such a file is only ever read onwards, from its start again to go back.
        /// </summary>
        private static bool IsBroadcast(string path)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> head = stackalloc byte[197];
                return file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                       && ((head[0] == 0x47 && head[188] == 0x47) || (head[4] == 0x47 && head[196] == 0x47));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>The file opened, with only this reader's stream on.</summary>
        private void Create()
        {
            Check(MFCreateAttributes(out var attrs, 1));
            try
            {
                if (_video)
                    Check(SetUInt32(attrs, AdvancedVideoProcessing, 1));
                Check(MFCreateSourceReaderFromURL(_path, attrs, out _reader));
            }
            finally
            {
                Release(attrs);
            }
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(_reader, 4))(_reader, AllStreams, 0));
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(_reader, 4))(_reader, _stream, 1));
            }
            catch
            {
                Release(_reader);
                _reader = 0;
                throw;
            }
        }

        /// <summary>What the stream is to come out as: pictures of the size asked for, or sound as floats.</summary>
        private void Shape()
        {
            if (_video)
            {
                // Scaled, turned and its pixels squared by the video processor when it can, else as the decoder gives it.
                // Asked for as the decoder has it (brightness and colour apart, NV12), which the processor hands on
                // untouched, and made red, green and blue here (ToBgra): left to the processor, a video that says it
                // is in the older colour standard came out in the newer one, and a small one that says the newer in
                // the older. Where it won't give that (a picture of an odd width), its own red, green and blue.
                _yuv = _size.W % 2 == 0 && _size.H % 2 == 0 && SetOutput(VideoType, Nv12, _size);
                var kind = _yuv ? Nv12 : Rgb32;
                if (!_yuv && !SetOutput(VideoType, kind, _size) && !SetOutput(VideoType, kind, null))
                    throw new COMException("The video can't be decoded to pictures.");
                _processed = KeepEveryFrame();
                if (_processed)
                    SetOutput(VideoType, kind, _size);
                ReadFrameSize();
            }
            else
            {
                if (!SetOutput(AudioType, FloatAudio, null))
                    throw new COMException("The sound can't be decoded.");
                ReadSoundKind();
            }
        }

        /// <summary>The file opened anew, at its start: for one that can't be jumped in.</summary>
        private void Reopen()
        {
            Release(_reader);
            _reader = 0;
            Create();
            Shape();
            _last = TimeSpan.MinValue;
            // A sound says what it really is only with its first sample (see where the file is first opened).
            if (!_video)
                _primed = ReadSample();
        }

        private bool SetOutput(Guid major, Guid sub, (int W, int H)? size)
        {
            Check(MFCreateMediaType(out var type));
            try
            {
                Check(SetGuid(type, MajorType, major));
                Check(SetGuid(type, SubType, sub));
                if (size is { } s)
                {
                    Check(SetUInt64(type, FrameSize, ((ulong)s.W << 32) | (uint)s.H));
                    Check(SetUInt64(type, PixelAspect, (1UL << 32) | 1));
                    // Whole frames, upright: asked for outright, so the processor is put in to weave a camcorder's
                    // half-frames together and to turn a picture said to be upside down, where the size alone
                    // (the same as the decoder's) would have left it out.
                    Check(SetUInt32(type, InterlaceMode, 2 /* progressive */));
                    Check(SetUInt32(type, Rotation, 0));
                }
                return ((delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)Slot(_reader, 7))(_reader, _stream, null, type) >= 0;
            }
            finally
            {
                Release(type);
            }
        }

        /// <summary>
        /// Tells the video processor to pass every frame on as it comes. Left to itself it makes the frames a second the
        /// file says it has, and an MKV said 15 for a video of 30: every second frame was dropped. False where there is
        /// no processor to tell.
        /// </summary>
        private bool KeepEveryFrame()
        {
            nint ex;
            var iid = SourceReaderEx;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(_reader, 0))(_reader, &iid, &ex) < 0)
                return false;
            var told = false;
            try
            {
                // IMFSourceReaderEx.GetTransformForStream 16; IMFTransform.GetAttributes 8.
                for (uint i = 0; i < 8; i++)
                {
                    Guid category;
                    nint transform;
                    if (((delegate* unmanaged[Stdcall]<nint, uint, uint, Guid*, nint*, int>)Slot(ex, 16))(ex, _stream, i, &category, &transform) < 0)
                        break;
                    try
                    {
                        nint settings;
                        if (category != VideoProcessors || ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(transform, 8))(transform, &settings) < 0)
                            continue;
                        told |= SetUInt32(settings, NoFrameRateChange, 1) >= 0;
                        Release(settings);
                    }
                    finally
                    {
                        Release(transform);
                    }
                }
            }
            finally
            {
                Release(ex);
            }
            return told;
        }

        private void ReadFrameSize()
        {
            nint current;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_reader, 6))(_reader, _stream, &current));
            try
            {
                Check(GetUInt64(current, FrameSize, out var outSize));
                (Width, Height) = ((int)(outSize >> 32), (int)(outSize & 0xFFFFFFFF));
                _stride = GetUInt32(current, DefaultStride, out var stride) >= 0 ? (int)stride : _yuv ? Width : Width * 4;
                // Straight from the decoder a picture comes in whole blocks of 16 (1920x1088 for 1080 rows): what is
                // past the size asked for, to the right and below, is no part of it.
                if (_yuv && Width >= _size.W && Height >= _size.H && Width - _size.W < 16 && Height - _size.H < 16)
                    (Width, Height) = _size;
            }
            finally
            {
                Release(current);
            }
        }

        private void ReadSoundKind()
        {
            nint current;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_reader, 6))(_reader, _stream, &current));
            try
            {
                Check(GetUInt32(current, AudioRate, out var rate));
                Check(GetUInt32(current, AudioChannels, out var channels));
                if (GetUInt32(current, AudioBits, out var bits) >= 0 && bits != 32)
                    throw new COMException("The sound didn't decode to 32-bit floats.");
                (SampleRate, Channels) = ((int)rate, (int)channels);
            }
            finally
            {
                Release(current);
            }
        }

        /// <summary>The next sample's bytes and time (Media Foundation's); null at the end of the stream.</summary>
        public (byte[] Bytes, TimeSpan Time)? ReadSample()
        {
            while (true)
            {
                uint actual, flags;
                long time;
                nint sample = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(_reader, 9))(_reader, _stream, 0, &actual, &flags, &time, &sample));
                try
                {
                    if ((flags & StreamError) != 0)
                        throw new COMException("The file couldn't be decoded further.");
                    // What comes out changed with what was decoded: the sound's real rate, the picture's real rows.
                    if ((flags & TypeChanged) != 0)
                    {
                        if (_video) ReadFrameSize();
                        else ReadSoundKind();
                    }
                    if (sample == 0)
                    {
                        if ((flags & EndOfStream) != 0) return null;
                        continue; // a gap in the stream
                    }
                    nint buffer;
                    Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer));
                    try
                    {
                        byte* data;
                        uint max, length;
                        Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &length));
                        try
                        {
                            var bytes = new byte[length];
                            Marshal.Copy((nint)data, bytes, 0, (int)length);
                            return (bytes, TimeSpan.FromTicks(time));
                        }
                        finally
                        {
                            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer);
                        }
                    }
                    finally
                    {
                        Release(buffer);
                    }
                }
                finally
                {
                    Release(sample);
                }
            }
        }

        public (TimeSpan Time, RawImage Frame)? Next()
        {
            var early = TimeSpan.FromSeconds(FrameRate > 0 ? 0.5 / FrameRate : 0.01);
            while (true)
            {
                if (ReadSample() is not { } s)
                {
                    // Nothing at all after a jump: a file that can't be jumped in. From its start, then, up to the place.
                    if (!_jumped || _noJumps)
                        return null;
                    (_noJumps, _jumped) = (true, false);
                    Reopen();
                    continue;
                }
                (_jumped, _last) = (false, s.Time);
                // After a seek the reader starts at the key frame before the time asked for.
                if (s.Time < _skipBefore)
                    continue;
                // Frames the file holds from before its film starts (a clip cut without being coded again).
                var time = s.Time - _zero;
                if (time < _from - early)
                    continue;
                _skipBefore = TimeSpan.MinValue;
                if (_yuv)
                {
                    // Rows as long as the picture is wide unless the buffer is of longer ones, and as many rows of
                    // brightness before the colour as the buffer has room for (a picture 1080 high may be held as 1088).
                    var row = Math.Max(Width, Math.Abs(_stride));
                    if (row * Height * 3 / 2 > s.Bytes.Length)
                        row = Width;
                    var rows = s.Bytes.Length * 2 / 3 / row;
                    if (rows < Height)
                        continue; // a frame cut short
                    return (time < TimeSpan.Zero ? TimeSpan.Zero : time, new RawImage(Width, Height, ToBgra(s.Bytes, row, rows, Width, Height, _old, _jpeg || (_full && !_processed))));
                }
                var rowBytes = Width * 4;
                var bgra = new byte[rowBytes * Height];
                var stride = Math.Abs(_stride) >= rowBytes ? _stride : rowBytes;
                for (var y = 0; y < Height; y++)
                {
                    var from = stride > 0 ? y * stride : (Height - 1 - y) * -stride;
                    if (from + rowBytes > s.Bytes.Length) break;
                    Buffer.BlockCopy(s.Bytes, from, bgra, y * rowBytes, rowBytes);
                }
                for (var i = 3; i < bgra.Length; i += 4)
                    bgra[i] = 255; // RGB32 leaves the fourth byte undefined
                return (time < TimeSpan.Zero ? TimeSpan.Zero : time, new RawImage(Width, Height, bgra));
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count)
            {
                if (_silence > 0)
                {
                    var quiet = (int)Math.Min(count - written, _silence);
                    Array.Clear(buffer, offset + written, quiet);
                    _silence -= quiet;
                    written += quiet;
                    continue;
                }
                if (_leftAt >= _left.Length)
                {
                    var read = _primed ?? ReadSample();
                    _primed = null;
                    if (read is not { } s)
                    {
                        // Nothing at all after a jump: a file that can't be jumped in, read from its start instead.
                        if (!_jumped || _noJumps)
                            break;
                        (_noJumps, _jumped) = (true, false);
                        Reopen();
                        Begin(_sought);
                        continue;
                    }
                    if (_jumped)
                    {
                        // The first sample since a jump. Past the place asked for (the jump is made a little early, so
                        // it should be before it): this file's jumps can't be gone by (an MP4 whose clock starts late
                        // landed a second and a half on), so from the start, counted. At the file's very start whatever
                        // was asked (a file written in fragments, an MKV): counted from here, where times and samples agree.
                        _jumped = false;
                        if (_soundFrom is { } wanted && s.Time > wanted + TimeSpan.FromMilliseconds(2))
                        {
                            FromStart(_sought);
                            continue;
                        }
                        if (_soundFrom is not null && s.Time <= First + TimeSpan.FromMilliseconds(1))
                            Begin(_sought);
                    }
                    // After a seek the reader starts somewhere before the time asked for (a FLAC
                    // block, an MP3 frame or more): read from that time's own sample on. Skipping
                    // whole samples that began before it started an MP3 up to half a second late.
                    var from = 0;
                    if (_drop > 0)
                    {
                        if (_drop >= s.Bytes.Length / 4)
                        {
                            _drop -= s.Bytes.Length / 4;
                            continue;
                        }
                        from = (int)_drop;
                        _drop = 0;
                    }
                    else if (_soundFrom is { } at)
                    {
                        var channels = Math.Max(1, Channels);
                        var frames = s.Bytes.Length / 4 / channels;
                        var skip = (long)Math.Round((at - s.Time).TotalSeconds * SampleRate);
                        if (skip >= frames)
                            continue;
                        _soundFrom = null;
                        from = (int)Math.Max(0, skip) * channels;
                    }
                    _left = new float[s.Bytes.Length / 4 - from];
                    Buffer.BlockCopy(s.Bytes, from * 4, _left, 0, _left.Length * 4);
                    _leftAt = 0;
                }
                var n = Math.Min(count - written, _left.Length - _leftAt);
                Array.Copy(_left, _leftAt, buffer, offset + written, n);
                _leftAt += n;
                written += n;
            }
            return written;
        }

        public void Seek(TimeSpan time)
        {
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            if (_video)
            {
                var at = time + _zero;
                if (at < TimeSpan.Zero) at = TimeSpan.Zero;
                // Frames from the key frame before it up to half a frame early are skipped.
                _skipBefore = at - TimeSpan.FromSeconds(FrameRate > 0 ? 0.5 / FrameRate : 0.001);
                if (_noJumps)
                {
                    // Onwards: read up to it. Back: from the start again.
                    if (at <= _last)
                        Reopen();
                    return;
                }
                _jumped = Position(at);
                if (!_jumped)
                    Reopen();
                return;
            }
            _primed = null;
            _sought = time;
            // The sound's own start on the film's clock: after nought where the picture starts first, before it by
            // the little nothing an encoder puts first.
            var start = First - _zero;
            // A little before the place: an AAC sound in an MP4 starts at the packet after the one asked for, up to
            // 21 ms late, and what is read before the place is left out by its times.
            if (_roughSeeks || _noJumps || time <= start + Early || !Position(time + _zero - Early))
            {
                FromStart(time);
                return;
            }
            (_drop, _silence, _soundFrom, _jumped) = (0, 0, time + _zero, true);
            (_left, _leftAt) = ([], 0);
        }

        /// <summary>How far before the place a sound is jumped to.</summary>
        private static readonly TimeSpan Early = TimeSpan.FromMilliseconds(100);

        /// <summary>The sound read through from its start up to a film time, which takes some 130 ms a minute of MP3 on a laptop.</summary>
        private void FromStart(TimeSpan time)
        {
            if (_noJumps || !Position(TimeSpan.Zero))
                Reopen();
            Begin(time);
        }

        /// <summary>Reading the sound from its first sample, to give it from a film time on: nothing where that is before the sound starts, the samples before it read past.</summary>
        private void Begin(TimeSpan time)
        {
            var channels = Math.Max(1, Channels);
            var apart = (long)Math.Round((time - (First - _zero)).TotalSeconds * SampleRate);
            (_drop, _silence, _soundFrom, _jumped) = (Math.Max(0, apart) * channels, Math.Max(0, -apart) * channels, null, false);
            (_left, _leftAt) = ([], 0);
        }

        /// <summary>
        /// Jumps to a time of Media Foundation's; false when it won't. No place past the file's length is taken (a clip
        /// whose frames run a little past it, as most do, is jumped to just before its end and read on from there).
        /// </summary>
        private bool Position(TimeSpan at)
        {
            var last = _length - TimeSpan.FromMilliseconds(1);
            if (_length > TimeSpan.Zero && at > last)
                at = last < TimeSpan.Zero ? TimeSpan.Zero : last;
            var position = new PropVariant { Type = 20 /* VT_I8 */, Value = at.Ticks };
            var format = Guid.Empty;
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, PropVariant*, int>)Slot(_reader, 8))(_reader, &format, &position) >= 0;
        }

        public void Dispose()
        {
            Release(_reader);
            _reader = 0;
        }
    }
}
