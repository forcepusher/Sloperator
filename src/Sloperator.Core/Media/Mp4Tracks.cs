using System.Buffers.Binary;

namespace Sloperator.Core.Media;

/// <summary>
/// What an MP4 or MOV says of its own tracks, read from its index: where each track starts on the film's clock (its
/// edit list), what it is coded as, and how long a file written in fragments runs. Windows' reader gets the edit list
/// wrong (a clip with frames that lean on later ones came out with its picture a tenth of a second behind its sound)
/// and knows no length for a fragmented file, so a reader sets its own times right by this.
/// </summary>
public static class Mp4Tracks
{
    /// <param name="Kind">"vide", "soun" or what else the track holds.</param>
    /// <param name="Codec">How it is coded, as the file names it: "avc1", "hvc1", "mp4a"…</param>
    /// <param name="Lead">Seconds the film runs before this track starts (an empty edit).</param>
    /// <param name="From">The track's own time, in seconds, that shows first: what is before it is not part of the film.</param>
    /// <param name="FirstShown">The earliest time any of the track's first frames is shown at, on the track's own clock.</param>
    /// <param name="Seconds">How long the track's media runs, fragments counted.</param>
    /// <param name="Colours">How a picture's colours are written, where the file says (an H.264 video's own header, or the MP4's note on it).</param>
    public sealed record Track(string Kind, string Codec, int Timescale, double Lead, double From, double FirstShown, double Seconds, VideoColours? Colours = null)
    {
        public bool IsVideo => Kind == "vide";
        public bool IsSound => Kind == "soun";
    }

    private sealed record Box(string Type, long Start, long Size, int Head)
    {
        public long Body => Start + Head;
        public long End => Start + Size;
    }

    /// <summary>The file's tracks in its own order; null when it is no MP4 or MOV, or its index can't be followed.</summary>
    public static IReadOnlyList<Track>? Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var top = Boxes(file, 0, file.Length);
            if (top.FirstOrDefault(b => b.Type == "moov") is not { } moov || moov.Size > 256 * 1024 * 1024)
                return null;
            var index = new byte[moov.Size];
            file.Position = moov.Start;
            file.ReadExactly(index);
            var inside = Boxes(index, moov.Head, index.Length);
            var movieScale = 0;
            if (inside.FirstOrDefault(b => b.Type == "mvhd") is { } mvhd)
                movieScale = (int)U32(index, mvhd.Body + (index[mvhd.Body] == 1 ? 20 : 12));
            var tracks = new List<(int Id, Track Track)>();
            foreach (var trak in inside.Where(b => b.Type == "trak"))
                if (TrackOf(index, trak, movieScale) is { } track)
                    tracks.Add(track);
            if (tracks.Count == 0)
                return null;
            // A file in fragments: each track runs to the end of its last fragment.
            var fragments = top.Where(b => b.Type == "moof").ToList();
            if (fragments.Count > 0)
            {
                var lasting = Defaults(index, inside);
                var ends = new Dictionary<int, double>();
                for (var i = fragments.Count - 1; i >= 0 && ends.Count < tracks.Count && fragments.Count - i <= 64; i--)
                {
                    if (fragments[i].Size > 64 * 1024 * 1024)
                        continue;
                    var moof = new byte[fragments[i].Size];
                    file.Position = fragments[i].Start;
                    file.ReadExactly(moof);
                    foreach (var traf in Boxes(moof, fragments[i].Head, moof.Length).Where(b => b.Type == "traf"))
                        if (End(moof, traf, lasting) is { } end && !ends.ContainsKey(end.Id))
                            ends[end.Id] = end.Ticks;
                }
                for (var i = 0; i < tracks.Count; i++)
                    if (ends.TryGetValue(tracks[i].Id, out var ticks) && tracks[i].Track.Timescale > 0)
                        tracks[i] = (tracks[i].Id, tracks[i].Track with { Seconds = Math.Max(tracks[i].Track.Seconds, ticks / tracks[i].Track.Timescale) });
            }
            return tracks.Select(t => t.Track).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>How the film's clock stands to what a reader that ignores edit lists gives: the time the first track of each kind starts at, with the earlier of them at nought.</summary>
    public static (Track? Video, Track? Sound, double Start) Starts(IReadOnlyList<Track> tracks)
    {
        var video = tracks.FirstOrDefault(t => t.IsVideo);
        var sound = tracks.FirstOrDefault(t => t.IsSound);
        var start = Math.Min(video?.Lead ?? double.MaxValue, sound?.Lead ?? double.MaxValue);
        return (video, sound, start == double.MaxValue ? 0 : start);
    }

    /// <summary>A codec as people call it: "HEVC (H.265)" for "hvc1"; the file's own name for one not known here.</summary>
    public static string Named(string codec) => codec.Trim().ToLowerInvariant() switch
    {
        "avc1" or "avc3" => "H.264",
        "hvc1" or "hev1" or "dvh1" or "dvhe" => "HEVC (H.265)",
        "av01" => "AV1",
        "vp09" => "VP9",
        "vp08" => "VP8",
        "mp4v" => "MPEG-4 video",
        "apch" or "apcn" or "apcs" or "apco" or "ap4h" or "ap4x" => "ProRes",
        "jpeg" or "mjpa" or "mjpb" or "mjpg" => "Motion JPEG",
        "mp4a" => "AAC",
        "ac-3" => "Dolby Digital (AC-3)",
        "ec-3" => "Dolby Digital Plus (E-AC-3)",
        "opus" => "Opus",
        "flac" => "FLAC",
        "alac" => "Apple Lossless",
        ".mp3" => "MP3",
        "samr" or "sawb" => "AMR",
        var other => other,
    };

    private static (int Id, Track Track)? TrackOf(byte[] index, Box trak, int movieScale)
    {
        var parts = Boxes(index, trak.Body, trak.End);
        if (parts.FirstOrDefault(b => b.Type == "mdia") is not { } mdia)
            return null;
        var id = 0;
        if (parts.FirstOrDefault(b => b.Type == "tkhd") is { } tkhd)
            id = (int)U32(index, tkhd.Body + (index[tkhd.Body] == 1 ? 20 : 12));
        var media = Boxes(index, mdia.Body, mdia.End);
        var (kind, scale, ticks) = ("", 0, 0.0);
        if (media.FirstOrDefault(b => b.Type == "hdlr") is { } hdlr)
            kind = System.Text.Encoding.ASCII.GetString(index, (int)hdlr.Body + 8, 4);
        if (media.FirstOrDefault(b => b.Type == "mdhd") is { } mdhd)
        {
            var one = index[mdhd.Body] == 1;
            scale = (int)U32(index, mdhd.Body + (one ? 20 : 12));
            ticks = one ? U64(index, mdhd.Body + 24) : U32(index, mdhd.Body + 16);
        }
        if (scale <= 0)
            return null;
        var (codec, shown) = ("", 0.0);
        VideoColours? colours = null;
        if (media.FirstOrDefault(b => b.Type == "minf") is { } minf && Boxes(index, minf.Body, minf.End).FirstOrDefault(b => b.Type == "stbl") is { } stbl)
        {
            var table = Boxes(index, stbl.Body, stbl.End);
            if (table.FirstOrDefault(b => b.Type == "stsd") is { } stsd && stsd.Size >= stsd.Head + 16 && U32(index, stsd.Body + 4) > 0)
            {
                codec = System.Text.Encoding.ASCII.GetString(index, (int)stsd.Body + 12, 4);
                if (kind == "vide")
                    colours = ColoursOf(index, stsd.Body + 8, Math.Min(stsd.End, stsd.Body + 8 + (long)U32(index, stsd.Body + 8)), codec);
            }
            // The first frame's own offset: a clip with frames that lean on later ones shows its first frame a little after nought.
            if (table.FirstOrDefault(b => b.Type == "ctts") is { } ctts && ctts.Size >= ctts.Head + 16 && U32(index, ctts.Body + 4) > 0)
                shown = (index[ctts.Body] == 0 ? U32(index, ctts.Body + 12) : I32(index, ctts.Body + 12)) / scale;
        }
        // The edit list: empty edits first (the track starts late), then where in the track the film begins.
        var (lead, from) = (0.0, 0.0);
        if (parts.FirstOrDefault(b => b.Type == "edts") is { } edts && Boxes(index, edts.Body, edts.End).FirstOrDefault(b => b.Type == "elst") is { } elst)
        {
            var one = index[elst.Body] == 1;
            var count = (int)Math.Min(U32(index, elst.Body + 4), 64);
            var at = elst.Body + 8;
            for (var i = 0; i < count && at + (one ? 20 : 12) <= elst.End; i++, at += one ? 20 : 12)
            {
                var length = one ? U64(index, at) : U32(index, at);
                var time = one ? I64(index, at + 8) : I32(index, at + 4);
                if (time < 0)
                {
                    if (movieScale > 0)
                        lead += length / movieScale;
                    continue;
                }
                from = time / scale;
                break;
            }
        }
        return (id, new Track(kind, codec, scale, lead, from, shown, ticks / scale, colours));
    }

    /// <summary>
    /// What a picture track's description says of its colours: an H.264 video's own header first (in its <c>avcC</c>
    /// box), which every player goes by, then the file's note beside it (<c>colr</c>) for what the header leaves out.
    /// </summary>
    private static VideoColours? ColoursOf(byte[] index, long entry, long end, string codec)
    {
        // A picture's description: 86 bytes of its own, then the boxes that say more.
        const int own = 86;
        if (entry + own >= end)
            return null;
        VideoColours? header = null, note = null;
        foreach (var box in Boxes(index, entry + own, end))
        {
            if (box.Type == "avcC" && codec is "avc1" or "avc3" && box.Size > box.Head + 8 && (index[box.Body + 5] & 0x1F) > 0)
            {
                var length = (int)BinaryPrimitives.ReadUInt16BigEndian(index.AsSpan((int)box.Body + 6, 2));
                if (box.Body + 8 + length <= box.End)
                    header = VideoColours.OfH264(index.AsSpan((int)box.Body + 8, length));
            }
            else if (box.Type == "colr")
                note = VideoColours.OfNote(index.AsSpan((int)box.Body, (int)(box.End - box.Body)));
        }
        return header is null && note is null ? null : new VideoColours(header?.Old ?? note?.Old, header?.Full ?? note?.Full);
    }

    /// <summary>How long a frame lasts where a fragment doesn't say, by track (mvex/trex).</summary>
    private static Dictionary<int, long> Defaults(byte[] index, List<Box> inside)
    {
        var lasting = new Dictionary<int, long>();
        if (inside.FirstOrDefault(b => b.Type == "mvex") is { } mvex)
            foreach (var trex in Boxes(index, mvex.Body, mvex.End).Where(b => b.Type == "trex" && b.Size >= b.Head + 24))
                lasting[(int)U32(index, trex.Body + 4)] = (long)U32(index, trex.Body + 12);
        return lasting;
    }

    /// <summary>Where a track's part of a fragment ends, on the track's own clock.</summary>
    private static (int Id, double Ticks)? End(byte[] moof, Box traf, Dictionary<int, long> lasting)
    {
        var parts = Boxes(moof, traf.Body, traf.End);
        if (parts.FirstOrDefault(b => b.Type == "tfhd") is not { } tfhd || parts.FirstOrDefault(b => b.Type == "tfdt") is not { } tfdt)
            return null;
        var flags = (long)U32(moof, tfhd.Body) & 0xFFFFFF;
        var id = (int)U32(moof, tfhd.Body + 4);
        var at = tfhd.Body + 8;
        if ((flags & 0x1) != 0) at += 8;  // base data offset
        if ((flags & 0x2) != 0) at += 4;  // sample description
        long each = lasting.GetValueOrDefault(id);
        if ((flags & 0x8) != 0) each = (long)U32(moof, at);
        double end = moof[tfdt.Body] == 1 ? U64(moof, tfdt.Body + 4) : U32(moof, tfdt.Body + 4);
        foreach (var trun in parts.Where(b => b.Type == "trun"))
        {
            var runFlags = (long)U32(moof, trun.Body) & 0xFFFFFF;
            var count = U32(moof, trun.Body + 4);
            var entry = trun.Body + 8 + ((runFlags & 0x1) != 0 ? 4 : 0) + ((runFlags & 0x4) != 0 ? 4 : 0);
            var size = ((runFlags & 0x100) != 0 ? 4 : 0) + ((runFlags & 0x200) != 0 ? 4 : 0) + ((runFlags & 0x400) != 0 ? 4 : 0) + ((runFlags & 0x800) != 0 ? 4 : 0);
            if ((runFlags & 0x100) == 0)
                end += (double)count * each;
            else
                for (long i = 0; i < count && entry + size <= trun.End; i++, entry += size)
                    end += U32(moof, entry);
        }
        return (id, end);
    }

    private static double U32(byte[] data, long at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)at, 4));

    private static double U64(byte[] data, long at) => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan((int)at, 8));

    private static double I32(byte[] data, long at) => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan((int)at, 4));

    private static double I64(byte[] data, long at) => BinaryPrimitives.ReadInt64BigEndian(data.AsSpan((int)at, 8));

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
            var header = 8;
            if (size == 1)
            {
                if (at + 16 > to)
                    break;
                file.ReadExactly(head.Slice(8, 8));
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head[8..]);
                header = 16;
            }
            else if (size == 0)
                size = to - at;
            if (size < header || at + size > to)
                break;
            boxes.Add(new Box(type, at, size, header));
            at += size;
        }
        return boxes;
    }

    /// <summary>The boxes one after another in a stretch of an index held in memory.</summary>
    private static List<Box> Boxes(byte[] data, long from, long to)
    {
        var boxes = new List<Box>();
        var at = from;
        while (at + 8 <= to)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)at, 4));
            var type = System.Text.Encoding.ASCII.GetString(data, (int)at + 4, 4);
            var header = 8;
            if (size == 1)
            {
                if (at + 16 > to)
                    break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan((int)at + 8, 8));
                header = 16;
            }
            else if (size == 0)
                size = to - at;
            if (size < header || at + size > to)
                break;
            boxes.Add(new Box(type, at, size, header));
            at += size;
        }
        return boxes;
    }
}
