using System.Buffers.Binary;

namespace Sloperator.Core.Media;

/// <summary>
/// An MP4's index (its <c>moov</c> box) moved to the file's start, where a browser or a chat app finds it before the
/// picture has all come down: the video starts playing while it still loads (ffmpeg's <c>+faststart</c>). A writer
/// that makes a file in one pass puts the index last, since it only knows it at the end; Windows' own setting for it
/// first (MF_MPEG4SINK_MOOV_BEFORE_MDAT) gave an unreadable file here, so the box is moved afterwards.
/// </summary>
public static class Mp4Index
{
    private sealed record Box(string Type, long Start, long Size);

    /// <summary>Moves the index before the media when it is after it; false when there was nothing to do, or the file isn't one this can move it in.</summary>
    public static bool MoveFirst(string path)
    {
        byte[] moov;
        Box index, media;
        long length;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            length = file.Length;
            var boxes = Boxes(file, 0, length);
            if (boxes.FirstOrDefault(b => b.Type == "moov") is not { } m || boxes.FirstOrDefault(b => b.Type == "mdat") is not { } d || m.Start < d.Start)
                return false;
            // One index, small enough to hold, and offsets that still fit once it lies before them.
            if (m.Size > 256 * 1024 * 1024 || boxes.Count(b => b.Type == "moov") != 1)
                return false;
            (index, media) = (m, d);
            moov = new byte[m.Size];
            file.Position = m.Start;
            file.ReadExactly(moov);
        }
        if (!Shift(moov, 8, moov.Length, moov.Length))
            return false;
        var temp = path + ".index";
        try
        {
            using (var from = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var to = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                // What was before the media, the index, the media and what lay between, then what was after the index.
                Copy(from, to, 0, media.Start);
                to.Write(moov);
                Copy(from, to, media.Start, index.Start - media.Start);
                Copy(from, to, index.Start + index.Size, length - (index.Start + index.Size));
            }
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    /// <summary>Whether the file's index is before its media (or it has none of either).</summary>
    public static bool IsFirst(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var boxes = Boxes(file, 0, file.Length);
        return boxes.FirstOrDefault(b => b.Type == "moov") is not { } m || boxes.FirstOrDefault(b => b.Type == "mdat") is not { } d || m.Start < d.Start;
    }

    /// <summary>The boxes one after another in a stretch of the file.</summary>
    private static List<Box> Boxes(Stream file, long from, long to)
    {
        var boxes = new List<Box>();
        Span<byte> head = stackalloc byte[16];
        var at = from;
        while (at + 8 <= to)
        {
            file.Position = at;
            file.ReadExactly(head[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(head);
            var type = System.Text.Encoding.ASCII.GetString(head.Slice(4, 4));
            if (size == 1)
            {
                if (at + 16 > to)
                    break;
                file.ReadExactly(head.Slice(8, 8));
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head[8..]);
            }
            else if (size == 0)
                size = to - at; // to the file's end
            if (size < 8 || at + size > to)
                break;
            boxes.Add(new Box(type, at, size));
            at += size;
        }
        return boxes;
    }

    /// <summary>
    /// Adds <paramref name="by"/> to every place the index says a piece of media is at (stco, co64), through the boxes
    /// that hold them; false when one would no longer fit in its 32 bits.
    /// </summary>
    private static bool Shift(byte[] moov, int from, int to, long by)
    {
        var at = from;
        while (at + 8 <= to)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(moov, at + 4, 4);
            if (size < 8 || at + size > to)
                return false;
            switch (type)
            {
                case "trak" or "mdia" or "minf" or "stbl":
                    if (!Shift(moov, at + 8, at + size, by))
                        return false;
                    break;
                case "stco":
                {
                    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(at + 12));
                    for (var i = 0; i < count; i++)
                    {
                        var entry = moov.AsSpan(at + 16 + i * 4);
                        var moved = BinaryPrimitives.ReadUInt32BigEndian(entry) + by;
                        if (moved > uint.MaxValue)
                            return false;
                        BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)moved);
                    }
                    break;
                }
                case "co64":
                {
                    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(at + 12));
                    for (var i = 0; i < count; i++)
                    {
                        var entry = moov.AsSpan(at + 16 + i * 8);
                        BinaryPrimitives.WriteUInt64BigEndian(entry, BinaryPrimitives.ReadUInt64BigEndian(entry) + (ulong)by);
                    }
                    break;
                }
            }
            at += size;
        }
        return true;
    }

    private static void Copy(Stream from, Stream to, long start, long count)
    {
        from.Position = start;
        var buffer = new byte[1 << 20];
        while (count > 0)
        {
            var n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0)
                break;
            to.Write(buffer, 0, n);
            count -= n;
        }
    }
}
