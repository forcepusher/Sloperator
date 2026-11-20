using System.Buffers.Binary;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>What an MP4 says of its own tracks (where they start, what they are coded as, their colours), and what readers make of awkward files.</summary>
public class Mp4TracksTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-mp4-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // The headers of real H.264 videos (their sequence parameter sets), as ffmpeg wrote them.
    private const string Old601Full = "6764001EACB40501ED80B702020640000003004000000F03C58BA8";
    private const string New709Tv = "6764001FACB402802DD80B50202014000003000400000300F03C60CA80";
    private const string SaysNothing = "6764001FACD9405005BB0110000003001000000303C0F1831960";
    private const string Squeezed601Full = "6764000AACD94426FFC0040003DB81010320000003002000000781E244B2C0";
    private const string Baseline = "6742C01FDA014016EC0440000003004000000F03C60CA8";

    private static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];
    private static byte[] U32(long v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); return b; }
    private static byte[] U64(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); return b; }
    private static byte[] Text(string s) => System.Text.Encoding.ASCII.GetBytes(s);
    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [.. U32(body.Length + 8), .. Text(type), .. body];
    }

    /// <summary>A track: its number, an edit list (pairs of length in film ticks and start in its own ticks, -1 for an empty edit), its clock, its kind, how it is coded and what follows that.</summary>
    private static byte[] Track(int id, (long Length, long From)[] edits, int scale, long ticks, string kind, byte[] description, long firstOffset = 0) => Box("trak",
        Box("tkhd", [0, 0, 0, 1], U32(0), U32(0), U32(id), new byte[60]),
        Box("edts", Box("elst", [0, 0, 0, 0], U32(edits.Length), [.. edits.SelectMany(e => (byte[])[.. U32(e.Length), .. U32(e.From), .. U32(0x10000)])])),
        Box("mdia",
            Box("mdhd", [0, 0, 0, 0], U32(0), U32(0), U32(scale), U32(ticks), new byte[4]),
            Box("hdlr", [0, 0, 0, 0], U32(0), Text(kind), new byte[13]),
            Box("minf", Box("stbl",
                Box("stsd", [0, 0, 0, 0], U32(1), description),
                Box("ctts", [0, 0, 0, 0], U32(1), U32(1), U32(firstOffset))))));

    private static byte[] Picture(string codec, params byte[][] more) => Box(codec, [.. new byte[78], .. more.SelectMany(m => m)]);

    private static byte[] Header(string sps)
    {
        var unit = Convert.FromHexString(sps);
        return Box("avcC", [1, unit[1], unit[2], unit[3], 0xFF, 0xE1], U16(unit.Length), unit, [1], U16(0));
    }

    private string File(string name, params byte[][] boxes)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(path, [.. Box("ftyp", Text("isom"), U32(0)), .. boxes.SelectMany(b => b)]);
        return path;
    }

    private static byte[] Film(int scale, long ticks) => Box("mvhd", [0, 0, 0, 0], U32(0), U32(0), U32(scale), U32(ticks), new byte[80]);

    [Fact]
    public void An_mp4_says_where_each_track_starts_and_how_its_picture_is_coloured()
    {
        // A film that starts ten seconds late on its clock, its picture two frames into its own track (frames that lean
        // on later ones), its sound past the little nothing an encoder starts with.
        var path = File("late.mp4",
            Box("moov", Film(1000, 13000),
                Track(1, [(10000, -1), (3000, 1024)], 15360, 46080, "vide", Picture("avc1", Header(Old601Full), Box("colr", Text("nclx"), U16(1), U16(1), U16(1), [0])), firstOffset: 1024),
                Track(2, [(10000, -1), (3000, 1024)], 48000, 144000, "soun", Box("mp4a", new byte[28]))),
            Box("mdat", new byte[16]));
        var tracks = Mp4Tracks.Read(path)!;
        Assert.Equal(2, tracks.Count);
        var (picture, sound, start) = Mp4Tracks.Starts(tracks);
        Assert.Equal(10, start, 3);
        Assert.True(picture!.IsVideo);
        Assert.Equal("avc1", picture.Codec);
        Assert.Equal(10, picture.Lead, 3);
        Assert.Equal(1024 / 15360.0, picture.From, 6);
        Assert.Equal(1024 / 15360.0, picture.FirstShown, 6);
        Assert.Equal(3, picture.Seconds, 3);
        // The video's own header is gone by, not the note beside it, which says the newer standard here.
        Assert.Equal(new VideoColours(Old: true, Full: true), picture.Colours);
        Assert.True(sound!.IsSound);
        Assert.Equal(1024 / 48000.0, sound.From, 6);
        Assert.Equal("H.264", Mp4Tracks.Named(picture.Codec));
        Assert.Equal("AAC", Mp4Tracks.Named(sound.Codec));

        // No file, a file that is no MP4, and one cut short: nothing, without a throw.
        Assert.Null(Mp4Tracks.Read(Path.Combine(_dir, "none.mp4")));
        System.IO.File.WriteAllText(Path.Combine(_dir, "text.mp4"), "not a video at all, just words in a file");
        Assert.Null(Mp4Tracks.Read(Path.Combine(_dir, "text.mp4")));
        var whole = System.IO.File.ReadAllBytes(path);
        System.IO.File.WriteAllBytes(Path.Combine(_dir, "short.mp4"), whole[..(whole.Length / 2)]);
        Assert.Null(Mp4Tracks.Read(Path.Combine(_dir, "short.mp4")));
    }

    [Fact]
    public void A_file_written_in_fragments_runs_to_the_end_of_its_last_one()
    {
        // The index knows no length; each fragment says where on the track's clock it starts and how many frames it has.
        byte[] Fragment(long from, int frames, bool each) => Box("moof",
            Box("mfhd", [0, 0, 0, 0], U32(1)),
            Box("traf",
                Box("tfhd", [0, 2, 0, 0], U32(1)),
                Box("tfdt", [1, 0, 0, 0], U64(from)),
                each ? Box("trun", [0, 0, 1, 1], U32(frames), U32(0), [.. Enumerable.Range(0, frames).SelectMany(_ => U32(256))])
                     : Box("trun", [0, 0, 0, 1], U32(frames), U32(0))));
        var path = File("fragments.mp4",
            Box("moov", Film(1000, 0),
                Track(1, [], 15360, 0, "vide", Picture("avc1", Header(SaysNothing))),
                Box("mvex", Box("trex", [0, 0, 0, 0], U32(1), U32(1), U32(512), U32(0), U32(0)))),
            Fragment(0, 30, each: false), Box("mdat", new byte[8]),
            Fragment(15360, 30, each: false), Box("mdat", new byte[8]),
            // The last one names each frame's length, half the usual: 60 frames of 256 ticks.
            Fragment(30720, 60, each: true), Box("mdat", new byte[8]));
        var picture = Assert.Single(Mp4Tracks.Read(path)!);
        Assert.Equal(3, picture.Seconds, 3);
        Assert.Null(picture.Colours);
    }

    [Theory]
    [InlineData(Old601Full, true, true)]
    [InlineData(New709Tv, false, false)]
    [InlineData(Squeezed601Full, true, true)]
    public void An_h264_videos_header_says_how_its_colours_are_written(string sps, bool old, bool full) =>
        Assert.Equal(new VideoColours(old, full), VideoColours.OfH264(Convert.FromHexString(sps)));

    [Fact]
    public void A_header_that_says_nothing_of_colours_and_one_cut_short_give_nothing()
    {
        Assert.Null(VideoColours.OfH264(Convert.FromHexString(SaysNothing)));
        Assert.Null(VideoColours.OfH264(Convert.FromHexString(Baseline)));
        Assert.Null(VideoColours.OfH264(Convert.FromHexString(Old601Full)[..9]));
        Assert.Null(VideoColours.OfH264([]));
        // Where nothing is said a small picture is taken in the older standard, an HD one in the newer, as players do.
        Assert.True(VideoColours.OldBySize(720, 576));
        Assert.True(VideoColours.OldBySize(1024, 576));
        Assert.False(VideoColours.OldBySize(1280, 544));
        Assert.False(VideoColours.OldBySize(960, 720));
        // The file's note on colours, for a video whose own header has none.
        Assert.Equal(new VideoColours(true, true), VideoColours.OfNote([.. Text("nclx"), .. U16(6), .. U16(6), .. U16(6), 0x80]));
        Assert.Equal(new VideoColours(false, null), VideoColours.OfNote([.. Text("nclc"), .. U16(1), .. U16(1), .. U16(1)]));
        Assert.Null(VideoColours.OfNote([.. Text("prof"), .. new byte[12]]));
    }

    private sealed class Reads(MediaInfo? info, string? ffmpeg = null) : IMediaDecoder
    {
        public int Asked;
        public MediaInfo? Probe(string path) { Asked++; return info; }
        public IVideoReader? OpenVideo(string path, int maxSide) => null;
        public IAudioReader? OpenAudio(string path) => null;
        public string? Ffmpeg => ffmpeg;
    }

    [Fact]
    public void A_file_one_reader_finds_only_sound_in_is_asked_of_the_next_unless_it_is_a_sound_file()
    {
        var soundOnly = new MediaInfo { HasAudio = true, SampleRate = 48000, Channels = 2, Duration = TimeSpan.FromSeconds(3) };
        var withPicture = soundOnly with { HasVideo = true, Width = 1920, Height = 1080, FrameRate = 30 };
        // A phone's HEVC video: Windows reads its sound alone, ffmpeg its picture too.
        var (first, second) = (new Reads(soundOnly), new Reads(withPicture));
        Assert.True(new FallbackDecoder(first, second).Probe("clip.mp4")!.HasVideo);
        // A song is a song: the second reader (a program to start) isn't asked.
        (first, second) = (new Reads(soundOnly), new Reads(withPicture));
        Assert.False(new FallbackDecoder(first, second).Probe("song.m4a")!.HasVideo);
        Assert.Equal(0, second.Asked);
        // Nobody finds a picture: the sound the first found.
        Assert.Same(soundOnly, new FallbackDecoder(new Reads(soundOnly), new Reads(null)).Probe("clip.mp4"));
        Assert.Null(new FallbackDecoder(new Reads(null), new Reads(null)).Probe("clip.mp4"));
    }

    [Fact]
    public void Why_a_videos_picture_cant_be_read_is_said_with_what_would_read_it()
    {
        var phone = File("phone.mp4", Box("moov", Film(1000, 3000), Track(1, [], 30000, 90000, "vide", Picture("hvc1"))), Box("mdat", new byte[8]));
        Assert.Equal("phone.mp4 is HEVC (H.265) video, which this Windows has no decoder for. ffmpeg in Sloperator's SloperatorData\\tools would read it, and so would Microsoft's HEVC Video Extensions from the Store.",
            MediaDecoders.WhyNoPicture(phone, new Reads(null)));
        var deep = File("deep.mp4", Box("moov", Film(1000, 3000), Track(1, [], 30000, 90000, "vide", Picture("avc1", Header(SaysNothing)))), Box("mdat", new byte[8]));
        Assert.StartsWith("deep.mp4 is H.264 video of a kind Windows doesn't decode (ten bits a colour, or colour at full detail). ffmpeg in", MediaDecoders.WhyNoPicture(deep, new Reads(null)));
        // With ffmpeg there and the picture still unread, it isn't ffmpeg that is missing.
        Assert.Equal("phone.mp4 is HEVC (H.265) video, which this Windows has no decoder for. ffmpeg couldn't read it either: the file may be damaged.",
            MediaDecoders.WhyNoPicture(phone, new Reads(null, "ffmpeg.exe")));
        // A kind that isn't an MP4 is told by its name; one nothing is known of, plainly.
        Assert.StartsWith("talk.webm is VP9 video, which this Windows has no decoder for.", MediaDecoders.WhyNoPicture(Path.Combine(_dir, "talk.webm"), new Reads(null)));
        Assert.StartsWith("odd.mkv holds no picture this Windows can read.", MediaDecoders.WhyNoPicture(Path.Combine(_dir, "odd.mkv"), new Reads(null)));
    }

    [Fact]
    public void A_surround_sound_is_folded_to_two_channels_with_its_speech_kept()
    {
        // Six channels as Windows gives them: front left, front right, centre, deep bass, back left, back right.
        float[] One(int channel) { var s = new float[6 * 4]; for (var f = 0; f < 4; f++) s[f * 6 + channel] = 0.5f; return s; }
        SoundSamples Folded(int channel, int to = 2) => SoundEdit.Convert(new SoundSamples(One(channel), 48000, 6), 48000, to);
        Assert.Equal([0.5f, 0f], Folded(0).Samples[..2]);
        Assert.Equal([0f, 0.5f], Folded(1).Samples[..2]);
        // The centre, where a film's speech is, to both sides at 0.707: taking the first two channels alone lost it.
        Assert.Equal(0.3536f, Folded(2).Samples[0], 3);
        Assert.Equal(0.3536f, Folded(2).Samples[1], 3);
        Assert.Equal([0f, 0f], Folded(3).Samples[..2]);
        Assert.Equal(0.3536f, Folded(4).Samples[0], 3);
        Assert.Equal(0f, Folded(4).Samples[1], 3);
        Assert.Equal(0.3536f, Folded(5).Samples[1], 3);
        // To one channel: the two sides' middle.
        Assert.Equal(0.25f, Folded(0, to: 1).Samples[0], 3);
        Assert.Equal(0.3536f, Folded(2, to: 1).Samples[0], 3);
        Assert.Equal(4, Folded(2).Frames);
    }

    [Fact]
    public void Ffmpegs_account_of_a_video_gives_the_picture_as_it_is_shown()
    {
        // A DVD's pixels aren't square: 720 across are shown as 768.
        var dvd = FfmpegDecoder.ParseProbe("""
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'pal.mp4':
              Duration: 00:00:03.00, start: 0.000000, bitrate: 1280 kb/s
              Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(progressive), 720x576 [SAR 16:15 DAR 4:3], 1147 kb/s, 25 fps, 25 tbr, 12800 tbn (default)
              Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, mono, fltp, 125 kb/s (default)
            """)!;
        Assert.Equal((768, 576), (dvd.Width, dvd.Height));
        // A phone held upright writes its picture lying down and says how far to turn it; ffmpeg gives it turned.
        var phone = FfmpegDecoder.ParseProbe("""
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'phone.mp4':
              Duration: 00:00:03.00, start: 0.000000, bitrate: 2901 kb/s
              Stream #0:0[0x1](und): Video: hevc (Main) (hvc1 / 0x31637668), yuv420p(tv, bt709), 1920x1080 [SAR 1:1 DAR 16:9], 2770 kb/s, 30 fps, 30 tbr, 15360 tbn (default)
                  Metadata:
                    handler_name    : VideoHandler
                  Side data:
                    displaymatrix: rotation of -90.00 degrees
              Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 125 kb/s (default)
            """)!;
        Assert.Equal((1080, 1920), (phone.Width, phone.Height));
        // Upside down is the same shape; a turn said of another stream is not this one's.
        var over = FfmpegDecoder.ParseProbe("""
              Duration: 00:00:03.00, start: 0.000000, bitrate: 2901 kb/s
              Stream #0:0[0x1](und): Video: h264 (High), yuv420p, 1280x720 [SAR 1:1 DAR 16:9], 30 fps
                  Side data:
                    displaymatrix: rotation of 180.00 degrees
              Stream #0:1[0x2](und): Video: mjpeg, yuvj420p, 320x240, 90k tbr (attached pic)
                  Side data:
                    displaymatrix: rotation of -90.00 degrees
            """)!;
        Assert.Equal((1280, 720), (over.Width, over.Height));
    }
}
