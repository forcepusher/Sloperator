using System.Buffers.Binary;

namespace Sloperator.Core.Media;

/// <summary>
/// How a video's colours are written, as its file says: by which of the two standards its brightness-and-colour
/// values turn back into red, green and blue (the older one of TV and DVDs, BT.601, or the newer of HD, BT.709), and
/// whether they run over the whole range or leave room at both ends as TV does. A null is a thing the file doesn't say.
/// </summary>
/// <param name="Old">The older standard (BT.601); false for BT.709.</param>
/// <param name="Full">Values over the whole range (a phone's or a JPEG's), not TV's 16 to 235.</param>
public sealed record VideoColours(bool? Old, bool? Full)
{
    /// <summary>The standard taken where a file names none: by its size, the older for a small picture, as players do.</summary>
    public static bool OldBySize(int width, int height) => width < 1280 && height <= 576;

    /// <summary>
    /// What an H.264 video's own header (its sequence parameter set, the unit as it lies in an MP4, its first byte the
    /// unit's kind) says of its colours; null when it says nothing of them or can't be followed.
    /// </summary>
    public static VideoColours? OfH264(ReadOnlySpan<byte> sps)
    {
        if (sps.Length < 5)
            return null;
        try
        {
            var bits = new Bits(Plain(sps[1..]));
            var profile = bits.Read(8);
            bits.Read(16);
            bits.Golomb();
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                var chroma = bits.Golomb();
                if (chroma == 3)
                    bits.Read(1);
                bits.Golomb();
                bits.Golomb();
                bits.Read(1);
                if (bits.Read(1) == 1)
                    for (var i = 0; i < (chroma != 3 ? 8 : 12); i++)
                        if (bits.Read(1) == 1)
                        {
                            // A scaling list: read past.
                            var (last, next) = (8, 8);
                            for (var j = 0; j < (i < 6 ? 16 : 64); j++)
                            {
                                if (next != 0)
                                    next = (last + bits.Signed() + 256) % 256;
                                last = next == 0 ? last : next;
                            }
                        }
            }
            bits.Golomb();
            var order = bits.Golomb();
            if (order == 0)
                bits.Golomb();
            else if (order == 1)
            {
                bits.Read(1);
                bits.Signed();
                bits.Signed();
                var cycle = bits.Golomb();
                for (var i = 0; i < cycle; i++)
                    bits.Signed();
            }
            bits.Golomb();
            bits.Read(1);
            bits.Golomb();
            bits.Golomb();
            if (bits.Read(1) == 0)
                bits.Read(1);
            bits.Read(1);
            if (bits.Read(1) == 1)
                for (var i = 0; i < 4; i++)
                    bits.Golomb();
            // What a player needs besides the picture itself: its shape, and its colours.
            if (bits.Read(1) == 0)
                return null;
            if (bits.Read(1) == 1 && bits.Read(8) == 255)
                bits.Read(32);
            if (bits.Read(1) == 1)
                bits.Read(1);
            if (bits.Read(1) == 0)
                return null;
            bits.Read(3);
            var full = bits.Read(1) == 1;
            if (bits.Read(1) == 0)
                return new VideoColours(null, full);
            bits.Read(16);
            return new VideoColours(Standard(bits.Read(8)), full);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The standard a file's number for it means (1: BT.709; 5 and 6: BT.601 as PAL and NTSC have it); null for one that says nothing or another.</summary>
    public static bool? Standard(int matrix) => matrix switch
    {
        1 => false,
        5 or 6 => true,
        _ => null,
    };

    /// <summary>What an MP4's note on colours (its <c>colr</c> box, from the four letters of its kind on) says.</summary>
    public static VideoColours? OfNote(ReadOnlySpan<byte> colr)
    {
        if (colr.Length < 10)
            return null;
        var kind = System.Text.Encoding.ASCII.GetString(colr[..4]);
        if (kind is not ("nclx" or "nclc"))
            return null;
        var standard = Standard(BinaryPrimitives.ReadUInt16BigEndian(colr[8..]));
        return new VideoColours(standard, kind == "nclx" && colr.Length > 10 ? (colr[10] & 0x80) != 0 : null);
    }

    /// <summary>The unit without the bytes put in to keep it from looking like a start code (a 3 after two noughts).</summary>
    private static byte[] Plain(ReadOnlySpan<byte> unit)
    {
        var plain = new List<byte>(unit.Length);
        var noughts = 0;
        foreach (var b in unit)
        {
            if (noughts >= 2 && b == 3)
            {
                noughts = 0;
                continue;
            }
            noughts = b == 0 ? noughts + 1 : 0;
            plain.Add(b);
        }
        return [.. plain];
    }

    private ref struct Bits(byte[] data)
    {
        private readonly byte[] _data = data;
        private int _at;

        public int Read(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++, _at++)
                value = (value << 1) | ((_data[_at >> 3] >> (7 - (_at & 7))) & 1);
            return value;
        }

        /// <summary>A number written with as many noughts before it as it has bits after its first.</summary>
        public int Golomb()
        {
            var noughts = 0;
            while (Read(1) == 0)
                if (++noughts > 31)
                    throw new IndexOutOfRangeException();
            return (1 << noughts) - 1 + Read(noughts);
        }

        public int Signed()
        {
            var code = Golomb();
            return (code & 1) == 1 ? (code + 1) / 2 : -(code / 2);
        }
    }
}
