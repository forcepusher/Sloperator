using Sloperator.Core.Platform;
using SkiaSharp;

namespace Sloperator.Core.Media;

/// <summary>
/// Animated GIFs (and animated WebP) read as video, with Skia: Windows' own decoders play neither, so without
/// ffmpeg a GIF could not be a clip of a video, lie over one as a sticker, be looked at frame by frame or be made
/// an MP4. Each frame is the picture as a viewer shows it then (a GIF's frames are often only what changed), with
/// its clear parts clear, so a sticker stays a sticker over a video.
/// </summary>
public sealed class GifDecoder : IMediaDecoder
{
    private static readonly string[] Kinds = [".gif", ".webp"];

    /// <summary>Whether the file is one this reads: a GIF or a WebP with more than one frame.</summary>
    public static bool Handles(string path)
    {
        if (!Kinds.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || !File.Exists(path))
            return false;
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(File.ReadAllBytes(path))); // read whole: the file is not held open
            return codec is { FrameCount: > 1 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public MediaInfo? Probe(string path)
    {
        using var reader = Open(path, 0);
        return reader is null ? null : new MediaInfo { Duration = reader.Duration, HasVideo = true, Width = reader.Width, Height = reader.Height, FrameRate = reader.FrameRate };
    }

    public IVideoReader? OpenVideo(string path, int maxSide) => Open(path, maxSide);

    public IAudioReader? OpenAudio(string path) => null;

    private static Reader? Open(string path, int maxSide)
    {
        if (!Kinds.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || !File.Exists(path))
            return null;
        try
        {
            var codec = SKCodec.Create(new SKMemoryStream(File.ReadAllBytes(path))); // read whole, so the file is not held open while it is looked at
            if (codec is not { FrameCount: > 1 })
            {
                codec?.Dispose();
                return null;
            }
            return new Reader(codec, maxSide);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private sealed class Reader : IVideoReader
    {
        private readonly SKCodec _codec;
        private readonly SKCodecFrameInfo[] _frames;
        /// <summary>When each frame starts, in seconds.</summary>
        private readonly double[] _starts;
        /// <summary>The picture as it stands after the frame <see cref="_held"/>, at the file's own size.</summary>
        private readonly SKBitmap _canvas;
        private int _held = -1;
        private int _next;

        public int Width { get; }
        public int Height { get; }
        public TimeSpan Duration { get; }
        public double FrameRate { get; }
        public bool HasAlpha => true;

        public Reader(SKCodec codec, int maxSide)
        {
            _codec = codec;
            _frames = codec.FrameInfo;
            _starts = new double[_frames.Length];
            var at = 0.0;
            for (var i = 0; i < _frames.Length; i++)
            {
                _starts[i] = at;
                // A frame that says it shows for no time shows for a tenth of a second, as browsers have it.
                at += _frames[i].Duration > 10 ? _frames[i].Duration / 1000.0 : 0.1;
            }
            Duration = TimeSpan.FromSeconds(at);
            FrameRate = at > 0 ? _frames.Length / at : 10;
            var (w, h) = (codec.Info.Width, codec.Info.Height);
            _canvas = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            var scale = maxSide > 0 && Math.Max(w, h) > maxSide ? maxSide / (double)Math.Max(w, h) : 1;
            (Width, Height) = (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
        }

        /// <summary>Brings the picture to how it stands at a frame: from the frame it builds on, drawn first when it isn't what is held.</summary>
        private bool Compose(int index)
        {
            if (_held == index)
                return true;
            // The frames this one stands on, back to one that stands alone or is held.
            var chain = new List<int>();
            for (var i = index; i >= 0 && i != _held; i = _frames[i].RequiredFrame)
                chain.Add(i);
            chain.Reverse();
            foreach (var i in chain)
            {
                var on = _frames[i].RequiredFrame;
                if (on < 0)
                    _canvas.Erase(SKColors.Transparent);
                if (_codec.GetPixels(_canvas.Info, _canvas.GetPixels(), new SKCodecOptions(i, on)) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    _held = -1;
                    return false;
                }
                _held = i;
            }
            return true;
        }

        public (TimeSpan Time, RawImage Frame)? Next()
        {
            if (_next >= _frames.Length || !Compose(_next))
                return null;
            var index = _next++;
            SKBitmap? scaled = null;
            try
            {
                var source = _canvas;
                if (Width != _canvas.Width || Height != _canvas.Height)
                {
                    scaled = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                    using var canvas = new SKCanvas(scaled);
                    canvas.Clear(SKColors.Transparent);
                    using var image = SKImage.FromBitmap(_canvas);
                    canvas.DrawImage(image, SKRect.Create(Width, Height), Sampling.Medium);
                    source = scaled;
                }
                var pixels = new byte[Width * Height * 4];
                for (var y = 0; y < Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(source.GetPixels() + y * source.RowBytes, pixels, y * Width * 4, Width * 4);
                return (TimeSpan.FromSeconds(_starts[index]), new RawImage(Width, Height, pixels));
            }
            finally
            {
                scaled?.Dispose();
            }
        }

        public void Seek(TimeSpan time)
        {
            var seconds = Math.Max(0, time.TotalSeconds);
            var index = 0;
            for (var i = 0; i < _starts.Length; i++)
                if (_starts[i] <= seconds + 1e-6)
                    index = i;
            _next = index;
        }

        public void Dispose()
        {
            _canvas.Dispose();
            _codec.Dispose();
        }
    }
}
