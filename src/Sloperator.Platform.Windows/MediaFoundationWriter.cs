using System.Runtime.InteropServices;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using static Sloperator.Platform.Windows.ComAbi;

namespace Sloperator.Platform.Windows;

/// <summary>
/// Video files written through Windows' own Media Foundation (its Sink Writer): an MP4 with H.264 picture and AAC
/// sound, made on every Windows 10 and 11 without anything installed. Before, a clip of a drawn scene and the
/// joined video both needed ffmpeg, which a PC only has once someone downloads it.
/// </summary>
public sealed unsafe partial class MediaFoundationDecoder
{
    public bool Writes => true;

    public IMediaWriter? Write(string path, MediaWriterOptions options)
    {
        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase))
            return null;
        if (options.Width < 2 || options.Height < 2 || options.Width % 2 != 0 || options.Height % 2 != 0 || options.Fps < 1)
            return null;
        try
        {
            return new Writer(path, options);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid Aac = new("00001610-0000-0010-8000-00AA00389B71");
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid AllIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");
    private static readonly Guid AudioBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid AudioBlockAlign = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid DisableThrottling = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    private static readonly Guid BFrames = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid RateControl = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid EncoderQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSinkWriterFromURL(string url, nint stream, nint attributes, out nint writer);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint size, out nint buffer);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint sample);

    /// <summary>One MP4 being written: its picture as NV12 to the H.264 encoder, its sound as 16-bit PCM to the AAC one.</summary>
    private sealed class Writer : IMediaWriter
    {
        private nint _writer;
        private readonly MediaWriterOptions _options;
        private readonly string _path;
        private readonly uint _video;
        private readonly uint _audio;
        private readonly bool _hd;
        private long _frames;
        private long _soundFrames;
        private bool _finished;

        public Writer(string path, MediaWriterOptions options)
        {
            Start();
            if (!Started.Value)
                throw new COMException("Media Foundation is not available (an N edition of Windows without its Media Feature Pack?).");
            _options = options;
            _path = path;
            // HD pictures in BT.709, smaller ones in BT.601: what players take them for when a file doesn't say.
            _hd = !VideoColours.OldBySize(options.Width, options.Height);
            Check(MFCreateAttributes(out var attrs, 2));
            try
            {
                // Written as fast as the frames come: the writer's own pacing is for live capture.
                Check(SetUInt32(attrs, DisableThrottling, 1));
                Check(MFCreateSinkWriterFromURL(path, 0, attrs, out _writer));
            }
            finally
            {
                Release(attrs);
            }
            try
            {
                var (size, rate) = (((ulong)(uint)options.Width << 32) | (uint)options.Height, ((ulong)(uint)options.Fps << 32) | 1);
                _video = AddStream(type =>
                {
                    Check(SetGuid(type, MajorType, VideoType));
                    Check(SetGuid(type, SubType, H264));
                    Check(SetUInt32(type, AverageBitrate, (uint)options.VideoBitrate));
                    Check(SetUInt32(type, InterlaceMode, 2 /* progressive */));
                    Check(SetUInt64(type, FrameSize, size));
                    Check(SetUInt64(type, FrameRateKey, rate));
                    Check(SetUInt64(type, PixelAspect, (1UL << 32) | 1));
                    Check(SetUInt32(type, Mpeg2Profile, 100 /* High */));
                });
                Action<nint> picture = type =>
                {
                    Check(SetGuid(type, MajorType, VideoType));
                    Check(SetGuid(type, SubType, Nv12));
                    Check(SetUInt32(type, InterlaceMode, 2));
                    Check(SetUInt64(type, FrameSize, size));
                    Check(SetUInt64(type, FrameRateKey, rate));
                    Check(SetUInt64(type, PixelAspect, (1UL << 32) | 1));
                    Check(SetUInt32(type, DefaultStride, (uint)options.Width));
                    Check(SetUInt32(type, AllIndependent, 1));
                    Check(SetUInt32(type, YuvMatrix, _hd ? 1u /* BT.709 */ : 2u /* BT.601 */));
                    Check(SetUInt32(type, NominalRange, 2 /* 16 to 235 */));
                };
                Action<nint> settings = encoder =>
                {
                    // No frames that lean on later ones: with them the file's first frame showed a frame late (0.033 s at 30
                    // frames a second), behind its sound, and a clip cut from it began a frame off.
                    Check(SetUInt32(encoder, BFrames, 0));
                    // As good as asked throughout, with as many bits as each stretch needs: a drawn scene's flat colours come
                    // out small, a filmed clip's grain large, where a fixed rate starved one and wasted on the other.
                    Check(SetUInt32(encoder, RateControl, 3 /* by quality */));
                    Check(SetUInt32(encoder, EncoderQuality, (uint)options.VideoQuality));
                };
                // An encoder that won't take those settings (another maker's, an older Windows') still writes the video: by
                // its average bit rate, as the stream above says, where this one goes by quality.
                try
                {
                    SetInput(_video, picture, settings);
                }
                catch (COMException)
                {
                    SetInput(_video, picture);
                }
                if (options.Sound)
                {
                    const uint channels = MediaWriterOptions.SoundChannels, hz = MediaWriterOptions.SoundRate;
                    _audio = AddStream(type =>
                    {
                        Check(SetGuid(type, MajorType, AudioType));
                        Check(SetGuid(type, SubType, Aac));
                        Check(SetUInt32(type, AudioBits, 16));
                        Check(SetUInt32(type, AudioRate, hz));
                        Check(SetUInt32(type, AudioChannels, channels));
                        Check(SetUInt32(type, AudioBytesPerSecond, 24000 /* 192 kbit/s, the most the encoder makes */));
                    });
                    SetInput(_audio, type =>
                    {
                        Check(SetGuid(type, MajorType, AudioType));
                        Check(SetGuid(type, SubType, Pcm));
                        Check(SetUInt32(type, AudioBits, 16));
                        Check(SetUInt32(type, AudioRate, hz));
                        Check(SetUInt32(type, AudioChannels, channels));
                        Check(SetUInt32(type, AudioBlockAlign, channels * 2));
                        Check(SetUInt32(type, AudioBytesPerSecond, hz * channels * 2));
                        Check(SetUInt32(type, AllIndependent, 1));
                    });
                }
                Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(_writer, 5))(_writer)); // BeginWriting
            }
            catch
            {
                Release(_writer);
                _writer = 0;
                throw;
            }
        }

        // IMFSinkWriter: AddStream 3, SetInputMediaType 4, BeginWriting 5, WriteSample 6, Finalize 11.
        private uint AddStream(Action<nint> describe)
        {
            Check(MFCreateMediaType(out var type));
            try
            {
                describe(type);
                uint index;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Slot(_writer, 3))(_writer, type, &index));
                return index;
            }
            finally
            {
                Release(type);
            }
        }

        private void SetInput(uint stream, Action<nint> describe, Action<nint>? encoder = null)
        {
            Check(MFCreateMediaType(out var type));
            try
            {
                describe(type);
                nint settings = 0;
                if (encoder is not null)
                {
                    Check(MFCreateAttributes(out settings, 4));
                    encoder(settings);
                }
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(_writer, 4))(_writer, stream, type, settings));
                }
                finally
                {
                    Release(settings);
                }
            }
            finally
            {
                Release(type);
            }
        }

        /// <summary>A sample of <paramref name="bytes"/> bytes, filled by <paramref name="fill"/>, written to a stream at its time.</summary>
        private void Write(uint stream, int bytes, long time, long duration, Action<nint> fill)
        {
            ObjectDisposedException.ThrowIf(_writer == 0, this);
            Check(MFCreateMemoryBuffer((uint)bytes, out var buffer));
            try
            {
                byte* data;
                // IMFMediaBuffer: Lock 3, Unlock 4, SetCurrentLength 6.
                Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, null, null));
                try
                {
                    fill((nint)data);
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer);
                }
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, 6))(buffer, (uint)bytes));
                Check(MFCreateSample(out var sample));
                try
                {
                    // IMFSample: SetSampleTime 36, SetSampleDuration 38, AddBuffer 42.
                    Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, 42))(sample, buffer));
                    Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 36))(sample, time));
                    Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 38))(sample, duration));
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Slot(_writer, 6))(_writer, stream, sample));
                }
                finally
                {
                    Release(sample);
                }
            }
            finally
            {
                Release(buffer);
            }
        }

        public void Frame(RawImage frame)
        {
            var (w, h) = (_options.Width, _options.Height);
            if (frame.Width != w || frame.Height != h)
                throw new ArgumentException($"The frame is {frame.Width}x{frame.Height}, and the video is {w}x{h}.", nameof(frame));
            const long second = 10_000_000;
            var (time, next) = (_frames * second / _options.Fps, (_frames + 1) * second / _options.Fps);
            var (bgra, stride, hd) = (frame.Bgra, frame.Stride, _hd);
            Write(_video, w * h * 3 / 2, time, next - time, data => ToNv12(bgra, stride, w, h, hd, data));
            _frames++;
        }

        /// <summary>
        /// BGRA to NV12 (a plane of brightness, then one of colour at half the size, U and V in turn), in the 16 to 235
        /// range video is in; the colour of each two-by-two block is its pixels' average. Two rows at a time, on all cores:
        /// 4 ms a 1080p frame on a laptop, a tenth of what drawing the frame takes.
        /// </summary>
        private static void ToNv12(byte[] bgra, int stride, int w, int h, bool hd, nint data)
        {
            // Weights in 1/65536ths: BT.709 for HD, BT.601 below.
            var (yr, yg, yb) = hd ? (11966, 40254, 4064) : (16829, 33039, 6416);
            var (ur, ug, ub) = hd ? (-6596, -22189, 28784) : (-9714, -19070, 28784);
            var (vr, vg, vb) = hd ? (28784, -26145, -2639) : (28784, -24103, -4681);
            Parallel.For(0, h / 2, row =>
            {
                var luma = (byte*)data;
                var chroma = luma + w * h + row * w;
                fixed (byte* source = bgra)
                {
                    byte* top = source + row * 2 * stride, bottom = source + (row * 2 + 1) * stride;
                    byte* y0 = luma + row * 2 * w, y1 = luma + (row * 2 + 1) * w;
                    for (var x = 0; x < w; x += 2)
                    {
                        byte* a = top + x * 4, b = a + 4, c = bottom + x * 4, d = c + 4;
                        y0[x] = (byte)(((yr * a[2] + yg * a[1] + yb * a[0] + 32768) >> 16) + 16);
                        y0[x + 1] = (byte)(((yr * b[2] + yg * b[1] + yb * b[0] + 32768) >> 16) + 16);
                        y1[x] = (byte)(((yr * c[2] + yg * c[1] + yb * c[0] + 32768) >> 16) + 16);
                        y1[x + 1] = (byte)(((yr * d[2] + yg * d[1] + yb * d[0] + 32768) >> 16) + 16);
                        var (red, green, blue) = ((a[2] + b[2] + c[2] + d[2] + 2) >> 2, (a[1] + b[1] + c[1] + d[1] + 2) >> 2, (a[0] + b[0] + c[0] + d[0] + 2) >> 2);
                        chroma[x] = (byte)(((ur * red + ug * green + ub * blue + 32768) >> 16) + 128);
                        chroma[x + 1] = (byte)(((vr * red + vg * green + vb * blue + 32768) >> 16) + 128);
                    }
                }
            });
        }

        public void Sound(ReadOnlySpan<float> samples)
        {
            if (!_options.Sound || samples.Length < MediaWriterOptions.SoundChannels)
                return;
            const long second = 10_000_000;
            const int channels = MediaWriterOptions.SoundChannels, hz = MediaWriterOptions.SoundRate;
            var count = samples.Length / channels * channels;
            var pcm = new short[count];
            for (var i = 0; i < count; i++)
                pcm[i] = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32767);
            var frames = count / channels;
            var (time, next) = (_soundFrames * second / hz, (_soundFrames + frames) * second / hz);
            Write(_audio, count * 2, time, next - time, data => Marshal.Copy(pcm, 0, data, count));
            _soundFrames += frames;
        }

        public void Finish()
        {
            if (_finished)
                return;
            ObjectDisposedException.ThrowIf(_writer == 0, this);
            _finished = true;
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(_writer, 11))(_writer));
            // The file is closed, then its index moved to its start, so it plays while it still loads (ffmpeg's +faststart).
            Release(_writer);
            _writer = 0;
            Mp4Index.MoveFirst(_path);
        }

        public void Dispose()
        {
            Release(_writer);
            _writer = 0;
        }
    }
}
