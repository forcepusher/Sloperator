using Sloperator.Core.Platform;

namespace Sloperator.Core.Media;

/// <summary>
/// Writes an animated GIF frame after frame, with nothing but this: each frame in the 255 colours that suit it best
/// (its own colour table, the colours it has most of split into boxes), a little ordered dither so a gradient
/// doesn't band, and only what changed since the frame before drawn again, so a still background costs nothing.
/// A frame with a clear background keeps it. Before, a GIF needed ffmpeg.
/// </summary>
public sealed class GifWriter : IDisposable
{
    private readonly Stream _file;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly bool _clear;
    private int _frames;
    /// <summary>The colour each pixel shows now (as last drawn), to tell what a new frame changes; unused for a clear background.</summary>
    private readonly byte[] _shown;
    private bool _finished;

    /// <summary>What a frame's colours are told apart by: five bits each of red, green and blue.</summary>
    private const int Bins = 32 * 32 * 32;

    /// <summary>Colours in a frame's table besides the clear one.</summary>
    private const int Colours = 255;

    /// <param name="clear">The frames' clear parts stay clear (each frame replaces the one before); else each is drawn over the last, on black.</param>
    public GifWriter(string path, int width, int height, int fps, bool clear = false)
    {
        (_width, _height, _fps, _clear) = (width, height, Math.Clamp(fps, 1, 50), clear);
        _shown = new byte[width * height * 3];
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        _file.Write("GIF89a"u8);
        Word(width);
        Word(height);
        _file.Write([0x70, 0, 0]); // no table for all frames: each has its own
        // Played round and round.
        _file.Write([0x21, 0xFF, 11]);
        _file.Write("NETSCAPE2.0"u8);
        _file.Write([3, 1, 0, 0, 0]);
    }

    private void Word(int value) => _file.Write([(byte)value, (byte)(value >> 8)]);

    /// <summary>The next frame (BGRA, at the size the GIF was opened with).</summary>
    public void Frame(RawImage frame)
    {
        if (frame.Width != _width || frame.Height != _height)
            throw new ArgumentException($"The frame is {frame.Width}x{frame.Height}, and the GIF is {_width}x{_height}.", nameof(frame));
        var (bgra, stride) = (frame.Bgra, frame.Stride);
        var pixels = _width * _height;
        // What this frame draws: all of it the first time and with a clear background, else what differs from what shows;
        // and the box that holds it, which is all of the frame that goes into the file.
        var draw = new bool[pixels];
        var (left, top, right, bottom) = (_width, _height, -1, -1);
        for (var y = 0; y < _height; y++)
            for (var x = 0; x < _width; x++)
            {
                var (i, p) = (y * stride + x * 4, y * _width + x);
                if (_clear)
                    draw[p] = bgra[i + 3] >= 128;
                else
                    draw[p] = _frames == 0 || Math.Abs(bgra[i] - _shown[p * 3]) + Math.Abs(bgra[i + 1] - _shown[p * 3 + 1]) + Math.Abs(bgra[i + 2] - _shown[p * 3 + 2]) > 9;
                if (draw[p])
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
            }
        // Nothing to draw: one clear pixel, so the frame still takes its time.
        if (right < 0)
            (left, top, right, bottom) = (0, 0, 0, 0);
        var (boxWidth, boxHeight) = (right - left + 1, bottom - top + 1);

        // The colours it draws, counted, and the table made of them.
        var counts = new int[Bins];
        // And what each really is, added up: a colour the frame has much of goes into the table as itself, not as its bin's middle.
        var sums = new long[Bins * 3];
        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
                if (draw[y * _width + x])
                {
                    var i = y * stride + x * 4;
                    var bin = (bgra[i + 2] >> 3 << 10) | (bgra[i + 1] >> 3 << 5) | (bgra[i] >> 3);
                    counts[bin]++;
                    (sums[bin * 3], sums[bin * 3 + 1], sums[bin * 3 + 2]) = (sums[bin * 3] + bgra[i + 2], sums[bin * 3 + 1] + bgra[i + 1], sums[bin * 3 + 2] + bgra[i]);
                }
        var (table, nearest, entries) = Palette(counts, sums);
        // The clear colour comes after the frame's own; the table is as long as holds them all (2, 4, … 256).
        var clearIndex = (byte)entries;
        var bits = 1;
        while (1 << bits < entries + 1)
            bits++;

        var indices = new byte[boxWidth * boxHeight];
        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
            {
                var (i, p, to) = (y * stride + x * 4, y * _width + x, (y - top) * boxWidth + x - left);
                if (!draw[p])
                {
                    indices[to] = clearIndex; // what shows there stays
                    continue;
                }
                // The table's nearest colour as it is when that is close (a flat colour stays flat, and the same from frame to
                // frame); else a little noise in a fixed pattern before the colour is picked, so a slope of colour steps finely.
                var plain = nearest[(bgra[i + 2] >> 3 << 10) | (bgra[i + 1] >> 3 << 5) | (bgra[i] >> 3)];
                if (Math.Abs(bgra[i + 2] - table[plain * 3]) + Math.Abs(bgra[i + 1] - table[plain * 3 + 1]) + Math.Abs(bgra[i] - table[plain * 3 + 2]) <= 8)
                    indices[to] = plain;
                else
                {
                    var nudge = (Bayer[(y & 3) * 4 + (x & 3)] - 7.5) * 1.6;
                    var (r, g, b) = (Math.Clamp((int)(bgra[i + 2] + nudge), 0, 255), Math.Clamp((int)(bgra[i + 1] + nudge), 0, 255), Math.Clamp((int)(bgra[i] + nudge), 0, 255));
                    indices[to] = nearest[(r >> 3 << 10) | (g >> 3 << 5) | (b >> 3)];
                }
                if (!_clear)
                    (_shown[p * 3], _shown[p * 3 + 1], _shown[p * 3 + 2]) = (bgra[i], bgra[i + 1], bgra[i + 2]);
            }

        // How long it shows, in hundredths of a second: the frame's end rounded, less the one's before, so the whole keeps time.
        var delay = (int)Math.Round((_frames + 1) * 100.0 / _fps) - (int)Math.Round(_frames * 100.0 / _fps);
        _file.Write([0x21, 0xF9, 4, (byte)((_clear ? 2 : 1) << 2 | 1), (byte)delay, (byte)(delay >> 8), clearIndex, 0]);
        _file.WriteByte(0x2C);
        Word(left);
        Word(top);
        Word(boxWidth);
        Word(boxHeight);
        _file.WriteByte((byte)(0x80 | (bits - 1))); // its own table
        _file.Write(table, 0, (1 << bits) * 3);
        Lzw(indices, Math.Max(2, bits));
        _frames++;
    }

    private static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    /// <summary>
    /// A table of 255 colours for the colours counted (the 256th is the clear one), and for every colour there could be
    /// the table's nearest: the colours in one box, split again and again where most of them are along its longest side.
    /// </summary>
    private static (byte[] Table, byte[] Nearest, int Entries) Palette(int[] counts, long[] sums)
    {
        var used = new List<int>();
        for (var c = 0; c < Bins; c++)
            if (counts[c] > 0)
                used.Add(c);
        var boxes = new List<List<int>> { used };
        while (boxes.Count < Colours)
        {
            // The box with the most pixels that can still be split.
            var (pick, most) = (-1, 0L);
            for (var b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count < 2)
                    continue;
                long sum = 0;
                foreach (var c in boxes[b]) sum += counts[c];
                if (sum > most) (pick, most) = (b, sum);
            }
            if (pick < 0)
                break;
            var box = boxes[pick];
            int[] low = [31, 31, 31], high = [0, 0, 0];
            foreach (var c in box)
                for (var k = 0; k < 3; k++)
                {
                    var v = (c >> (10 - k * 5)) & 31;
                    (low[k], high[k]) = (Math.Min(low[k], v), Math.Max(high[k], v));
                }
            // Green counts for more to the eye than red, red than blue.
            var axis = 0;
            double widest = -1;
            double[] weight = [0.8, 1, 0.6];
            for (var k = 0; k < 3; k++)
                if ((high[k] - low[k]) * weight[k] > widest)
                    (axis, widest) = (k, (high[k] - low[k]) * weight[k]);
            var shift = 10 - axis * 5;
            box.Sort((a, b) => ((a >> shift) & 31).CompareTo((b >> shift) & 31));
            long half = most / 2, run = 0;
            var cut = 1;
            for (var n = 0; n < box.Count - 1; n++)
            {
                run += counts[box[n]];
                cut = n + 1;
                if (run >= half)
                    break;
            }
            boxes[pick] = box.GetRange(0, cut);
            boxes.Add(box.GetRange(cut, box.Count - cut));
        }
        var table = new byte[256 * 3];
        for (var b = 0; b < boxes.Count; b++)
        {
            long r = 0, g = 0, bl = 0, n = 0;
            foreach (var c in boxes[b])
            {
                var w = counts[c];
                r += sums[c * 3];
                g += sums[c * 3 + 1];
                bl += sums[c * 3 + 2];
                n += w;
            }
            if (n > 0)
                (table[b * 3], table[b * 3 + 1], table[b * 3 + 2]) = ((byte)(r / n), (byte)(g / n), (byte)(bl / n));
        }
        var entries = Math.Max(1, boxes.Count(b => b.Count > 0));
        var nearest = new byte[Bins];
        Parallel.For(0, Bins, c =>
        {
            var (r, g, b) = (((c >> 10) & 31) * 8 + 4, ((c >> 5) & 31) * 8 + 4, (c & 31) * 8 + 4);
            var (best, least) = (0, int.MaxValue);
            for (var e = 0; e < entries; e++)
            {
                var (dr, dg, db) = (r - table[e * 3], g - table[e * 3 + 1], b - table[e * 3 + 2]);
                var far = dr * dr * 2 + dg * dg * 4 + db * db;
                if (far < least)
                    (best, least) = (e, far);
            }
            nearest[c] = (byte)best;
        });
        return (table, nearest, entries);
    }

    /// <summary>The frame's pixels packed as a GIF packs them (LZW, codes from one bit more than its colours take up to 12), in blocks of 255 bytes.</summary>
    private void Lzw(byte[] indices, int minBits)
    {
        int clear = 1 << minBits, end = clear + 1;
        _file.WriteByte((byte)minBits);
        var block = new byte[255];
        var (filled, bits, held) = (0, 0, 0u);
        void Code(int code, int size)
        {
            held |= (uint)code << bits;
            bits += size;
            while (bits >= 8)
            {
                block[filled++] = (byte)held;
                held >>= 8;
                bits -= 8;
                if (filled == 255)
                {
                    _file.WriteByte(255);
                    _file.Write(block, 0, 255);
                    filled = 0;
                }
            }
        }
        // What has been seen so far, by the code of all but its last pixel and that pixel.
        var seen = new Dictionary<int, int>(4096);
        var (next, size) = (end + 1, minBits + 1);
        Code(clear, size);
        var run = (int)indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            var pixel = indices[i];
            var key = (run << 8) | pixel;
            if (seen.TryGetValue(key, out var code))
            {
                run = code;
                continue;
            }
            Code(run, size);
            if (next < 4096)
            {
                seen[key] = next++;
                if (next > 1 << size && size < 12)
                    size++;
            }
            else
            {
                // The table is full: begun again.
                Code(clear, size);
                seen.Clear();
                (next, size) = (end + 1, minBits + 1);
            }
            run = pixel;
        }
        Code(run, size);
        Code(end, size);
        if (bits > 0)
            block[filled++] = (byte)held;
        if (filled > 0)
        {
            _file.WriteByte((byte)filled);
            _file.Write(block, 0, filled);
        }
        _file.WriteByte(0);
    }

    /// <summary>Ends the file.</summary>
    public void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        _file.WriteByte(0x3B);
        _file.Flush();
    }

    public void Dispose()
    {
        Finish();
        _file.Dispose();
    }
}
