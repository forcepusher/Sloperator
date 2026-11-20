#if WINDOWS
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using Sloperator.Platform.Windows;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Sound read by Windows' own Media Foundation, through its COM interfaces' tables (ComAbi).</summary>
public class MediaFoundationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-mf-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_wave_file_is_probed_and_read()
    {
        // One second of a 440 Hz tone, 16-bit stereo at 22,050 Hz, at half volume.
        const int rate = 22050, channels = 2;
        var path = Path.Combine(_dir, "tone ü.wav");
        using (var file = new BinaryWriter(File.Create(path)))
        {
            var data = rate * channels * 2;
            file.Write("RIFF"u8); file.Write(36 + data); file.Write("WAVE"u8);
            file.Write("fmt "u8); file.Write(16); file.Write((short)1); file.Write((short)channels);
            file.Write(rate); file.Write(rate * channels * 2); file.Write((short)(channels * 2)); file.Write((short)16);
            file.Write("data"u8); file.Write(data);
            for (var i = 0; i < rate; i++)
            {
                var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * short.MaxValue / 2);
                file.Write(sample);
                file.Write(sample);
            }
        }

        var decoder = new MediaFoundationDecoder();
        var info = decoder.Probe(path);
        Assert.NotNull(info);
        Assert.False(info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.Equal(channels, info.Channels);
        Assert.InRange(info.Duration.TotalSeconds, 0.95, 1.05);

        using var sound = decoder.OpenAudio(path);
        Assert.NotNull(sound);
        var buffer = new float[4096];
        var (read, loudest) = (0, 0f);
        for (int n; (n = sound.Read(buffer, 0, buffer.Length)) > 0; read += n)
            loudest = Math.Max(loudest, buffer.Take(n).Max(Math.Abs));
        Assert.InRange(read / (double)(sound.SampleRate * sound.Channels), 0.95, 1.05);
        Assert.InRange(loudest, 0.45f, 0.55f);

        Assert.Null(decoder.Probe(Path.Combine(_dir, "missing.wav")));
    }

    [Fact]
    public void A_sound_in_surround_is_played_with_its_speech()
    {
        // Two frames of 5.1 as Windows gives it: the first has speech alone (the centre), the second music at the
        // front and something behind on the left. Its first two channels alone, as was played, are silence then music.
        float[] film = [0, 0, 0.5f, 0.9f, 0, 0, 0.25f, -0.25f, 0, 0.9f, 0.5f, 0];
        var played = WaveOutPlayer.ToPcm(film, 2, 6, 2, SoundEdit.Shares(6));
        Assert.Equal(4, played.Length);
        Assert.InRange(played[0], 11580, 11590); // 0.5 x 0.707 of full scale, and the deep bass left out
        Assert.Equal(played[0], played[1]);
        Assert.InRange(played[2], 19770, 19780); // 0.25 + 0.5 x 0.707
        Assert.Equal(-8192, played[3]);

        // Two channels and one are as they were; more than full scale is held at it.
        Assert.Equal(new short[] { 16384, -16384 }, WaveOutPlayer.ToPcm([0.5f, -0.5f], 1, 2, 2, null));
        Assert.Equal(new short[] { 16384, -32768 }, WaveOutPlayer.ToPcm([0.5f, -1.5f], 2, 1, 1, null));
        Assert.Equal(new short[] { 32767, 32767 }, WaveOutPlayer.ToPcm([1, 1, 1, 0, 1, 1], 1, 6, 2, SoundEdit.Shares(6)));
        // Every count of channels has a share for each of them.
        for (var channels = 3; channels <= 12; channels++)
            Assert.Equal(channels, SoundEdit.Shares(channels).Length);
    }

    [Theory]
    [InlineData(0.4567)]
    [InlineData(0.8)]
    [InlineData(1.2345)]
    public void A_sound_read_from_a_time_starts_at_that_sample(double seconds)
    {
        // Two seconds whose every sample is its own number, so the first one read says where it starts.
        const int rate = 16000;
        var path = Path.Combine(_dir, "count.wav");
        using (var file = new BinaryWriter(File.Create(path)))
        {
            const int data = 2 * rate * 2;
            file.Write("RIFF"u8); file.Write(36 + data); file.Write("WAVE"u8);
            file.Write("fmt "u8); file.Write(16); file.Write((short)1); file.Write((short)1);
            file.Write(rate); file.Write(rate * 2); file.Write((short)2); file.Write((short)16);
            file.Write("data"u8); file.Write(data);
            for (var i = 0; i < 2 * rate; i++)
                file.Write((short)i);
        }

        using var sound = new MediaFoundationDecoder().OpenAudio(path);
        Assert.NotNull(sound);
        sound.Seek(TimeSpan.FromSeconds(seconds));
        var buffer = new float[16];
        Assert.Equal(16, sound.Read(buffer, 0, buffer.Length));
        Assert.InRange(Math.Round(buffer[0] * 32768), seconds * rate - 1, seconds * rate + 1);
    }

    // ---- video as a player shows it ----------------------------------------------------------------------------

    /// <summary>A clip written here: the left half one colour and the right another, a white bar crossing it so each frame differs.</summary>
    private string Clip(string name, int width, int height, int frames, (byte R, byte G, byte B) left, (byte R, byte G, byte B) right)
    {
        var path = Path.Combine(_dir, name);
        using var writer = new MediaFoundationDecoder().Write(path, new MediaWriterOptions(width, height, 30, Quality: "best"))!;
        for (var f = 0; f < frames; f++)
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var c = x < width / 2 ? left : right;
                    var i = (y * width + x) * 4;
                    (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.B, c.G, c.R, 255);
                }
            writer.Frame(new RawImage(width, height, pixels));
        }
        writer.Finish();
        return path;
    }

    private static (int R, int G, int B) At(RawImage frame, double x, double y)
    {
        var i = ((int)(y * frame.Height) * frame.Width + (int)(x * frame.Width)) * 4;
        return (frame.Bgra[i + 2], frame.Bgra[i + 1], frame.Bgra[i]);
    }

    private static void Near((int R, int G, int B) want, (int R, int G, int B) got, int within = 12) =>
        Assert.True(Math.Abs(want.R - got.R) <= within && Math.Abs(want.G - got.G) <= within && Math.Abs(want.B - got.B) <= within, $"wanted {want}, got {got}");

    [Theory]
    // One colour (192, 32, 32) as each standard and range writes it: brightness, then the two colour values.
    [InlineData(true, true, 80, 100, 207)]
    [InlineData(true, false, 85, 104, 198)]
    [InlineData(false, true, 66, 109, 207)]
    [InlineData(false, false, 72, 111, 197)]
    public void A_frame_from_the_decoder_is_made_red_green_and_blue_by_the_standard_its_file_names(bool old, bool full, byte light, byte u, byte v)
    {
        // Six by four, held in rows of eight with a row to spare, as a decoder holds a picture in whole blocks.
        const int width = 6, height = 4, stride = 8, rows = 5;
        var frame = new byte[stride * rows * 3 / 2];
        Array.Fill(frame, light, 0, stride * rows);
        for (var i = stride * rows; i < frame.Length; i += 2)
            (frame[i], frame[i + 1]) = (u, v);
        var bgra = MediaFoundationDecoder.ToBgra(frame, stride, rows, width, height, old, full);
        Assert.Equal(width * height * 4, bgra.Length);
        for (var i = 0; i < bgra.Length; i += 4)
        {
            Near((192, 32, 32), (bgra[i + 2], bgra[i + 1], bgra[i]), within: 3);
            Assert.Equal(255, bgra[i + 3]);
        }
        // Read by the wrong standard the same values are another colour: what a video in the older one came out as.
        var wrong = MediaFoundationDecoder.ToBgra(frame, stride, rows, width, height, !old, full);
        Assert.True(Math.Abs(wrong[1] - 32) > 10, "the two standards give the same green");
    }

    [Fact]
    public void A_video_reads_at_its_own_size_from_nought_and_a_jump_to_its_last_frame_or_past_its_end_is_taken()
    {
        var media = new MediaFoundationDecoder();
        // 1080 rows are held by the decoder as 1088: the picture is the 1080.
        var path = Clip("hd.mp4", 1920, 1080, 20, (200, 60, 60), (60, 90, 200));
        using var video = media.OpenVideo(path, 0)!;
        Assert.Equal((1920, 1080), (video.Width, video.Height));
        var times = new List<double>();
        RawImage? first = null;
        while (video.Next() is { } next)
        {
            first ??= next.Frame;
            times.Add(next.Time.TotalSeconds);
        }
        Assert.Equal(20, times.Count);
        Assert.Equal(0, times[0], 3);
        Assert.Equal((1920, 1080), (first!.Width, first.Height));
        Near((200, 60, 60), At(first, 0.25, 0.5));
        Near((60, 90, 200), At(first, 0.75, 0.97));
        // The last frame, after the end was reached; past the end there is none, and no throw.
        video.Seek(TimeSpan.FromSeconds(times[^1]));
        Assert.Equal(times[^1], video.Next()!.Value.Time.TotalSeconds, 3);
        video.Seek(video.Duration + TimeSpan.FromSeconds(1));
        Assert.Null(video.Next());
        video.Seek(TimeSpan.Zero);
        Assert.Equal(0, video.Next()!.Value.Time.TotalSeconds, 3);
        // Scaled to fit a side, in whole pairs of pixels.
        using var small = media.OpenVideo(path, 320)!;
        Assert.Equal((320, 180), (small.Width, small.Height));
        Near((200, 60, 60), At(small.Next()!.Value.Frame, 0.25, 0.5));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void A_video_said_to_be_turned_reads_upright(int turn)
    {
        // A phone held upright writes its picture lying down and notes the turn in the track's header (a matrix).
        var path = Clip($"turned{turn}.mp4", 320, 180, 6, (200, 60, 60), (60, 90, 200));
        var file = File.ReadAllBytes(path);
        var header = Find(file, "tkhd");
        var matrix = header + 8 + (file[header + 8] == 1 ? 52 : 40);
        int[] values = turn switch
        {
            90 => [0, 0x10000, 0, -0x10000, 0, 0, 180 << 16, 0, 0x40000000],
            180 => [-0x10000, 0, 0, 0, -0x10000, 0, 320 << 16, 180 << 16, 0x40000000],
            _ => [0, -0x10000, 0, 0x10000, 0, 0, 0, 320 << 16, 0x40000000],
        };
        for (var i = 0; i < 9; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(matrix + i * 4), values[i]);
        File.WriteAllBytes(path, file);

        var media = new MediaFoundationDecoder();
        var info = media.Probe(path)!;
        Assert.Equal(turn == 180 ? (320, 180) : (180, 320), (info.Width, info.Height));
        using var video = media.OpenVideo(path, 0)!;
        var frame = video.Next()!.Value.Frame;
        Assert.Equal((info.Width, info.Height), (frame.Width, frame.Height));
        var (red, blue) = ((200, 60, 60), (60, 90, 200));
        // Turned a quarter clockwise the left half is on top; half a turn swaps the sides; three quarters puts it below.
        switch (turn)
        {
            case 90: Near(red, At(frame, 0.5, 0.25)); Near(blue, At(frame, 0.5, 0.75)); break;
            case 180: Near(blue, At(frame, 0.25, 0.5)); Near(red, At(frame, 0.75, 0.5)); break;
            default: Near(blue, At(frame, 0.5, 0.25)); Near(red, At(frame, 0.5, 0.75)); break;
        }
    }

    /// <summary>Where a box of this kind starts in an MP4 (its first: a clip written here has one track).</summary>
    private static int Find(byte[] file, string kind)
    {
        var name = System.Text.Encoding.ASCII.GetBytes(kind);
        for (var i = 4; i + 4 <= file.Length; i++)
            if (file.AsSpan(i, 4).SequenceEqual(name))
                return i - 4;
        throw new InvalidOperationException($"no {kind} box");
    }

    /// <summary>ffmpeg run to make a file for a test; the test is skipped where there is no ffmpeg.</summary>
    private string Made(string name, params string[] args)
    {
        var ffmpeg = new FfmpegDecoder(() => []).Program;
        if (ffmpeg is null)
            Assert.Skip("No ffmpeg on this PC.");
        var path = Path.Combine(_dir, name);
        var start = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in (string[])["-hide_banner", "-loglevel", "error", "-y", .. args, path])
            start.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(start)!;
        var said = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(File.Exists(path), "ffmpeg made no file: " + said);
        return path;
    }

    /// <summary>A picture that turns white, and a sound that beeps, at exactly one second.</summary>
    private static string[] FlashAndBeep(int fps) =>
    [
        "-f", "lavfi", "-i", $"color=c=black:size=320x180:rate={fps},drawbox=color=white:t=fill:enable='gte(t,1)*lt(t,1.5)'",
        "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000,volume=volume=0:enable='lt(t,1)+gte(t,1.5)'", "-t", "2.5",
    ];

    [Theory]
    // Frames that lean on later ones (most MP4s that aren't a phone's): Windows gave the picture four frames late.
    [InlineData("leaning.mp4", 30, "-c:v libx264 -pix_fmt yuv420p -preset veryfast -c:a aac")]
    [InlineData("film24.mp4", 24, "-c:v libx264 -pix_fmt yuv420p -preset veryfast -c:a aac")]
    [InlineData("camera.mov", 30, "-c:v libx264 -pix_fmt yuv420p -preset veryfast -c:a pcm_s16le")]
    // A film whose clock starts at ten seconds, and a broadcast stream, which starts anywhere.
    [InlineData("late.mp4", 30, "-c:v libx264 -pix_fmt yuv420p -preset veryfast -c:a aac -output_ts_offset 10")]
    [InlineData("stream.ts", 30, "-c:v libx264 -pix_fmt yuv420p -preset veryfast -c:a aac")]
    [InlineData("recorded.mkv", 30, "-c:v libx264 -pix_fmt yuv420p -preset veryfast -c:a aac")]
    public void The_picture_and_the_sound_of_a_file_stay_together_on_the_films_own_clock(string name, int fps, string coding)
    {
        var path = Made(name, [.. FlashAndBeep(fps), .. coding.Split(' ')]);
        var media = new MediaFoundationDecoder();
        var (white, beep) = (-1.0, -1.0);
        using (var video = media.OpenVideo(path, 0)!)
        {
            Assert.InRange(video.Duration.TotalSeconds, 2.3, 2.8);
            while (video.Next() is { } next)
                if (At(next.Frame, 0.5, 0.5).G > 128)
                {
                    white = next.Time.TotalSeconds;
                    break;
                }
            // A jump lands on the frame showing then, here too: just before the white, and into it.
            video.Seek(TimeSpan.FromSeconds(white - 0.04));
            Assert.True(At(video.Next()!.Value.Frame, 0.5, 0.5).G < 128, "the frame before the white one is white");
            video.Seek(TimeSpan.FromSeconds(white + 0.1));
            Assert.True(At(video.Next()!.Value.Frame, 0.5, 0.5).G > 128, "a jump into the white found black");
        }
        using (var audio = media.OpenAudio(path)!)
        {
            var sound = SoundEdit.Read(audio);
            for (var i = 0; i < sound.Samples.Length && beep < 0; i++)
                if (Math.Abs(sound.Samples[i]) > 0.05)
                    beep = i / sound.Channels / (double)sound.Rate;
            // And from a time on: the beep is as far off as it should be.
            audio.Seek(TimeSpan.FromSeconds(0.5));
            var from = SoundEdit.Read(audio);
            var then = Array.FindIndex(from.Samples, s => Math.Abs(s) > 0.05) / from.Channels / (double)from.Rate;
            Assert.InRange(then, beep - 0.5 - 0.005, beep - 0.5 + 0.005);
        }
        Assert.InRange(white, 0.96, 1.04);
        Assert.InRange(beep, 0.96, 1.04);
        Assert.InRange(white - beep, -0.035, 0.035);
    }

    [Fact]
    public void An_mkv_gives_every_frame_and_a_squeezed_picture_its_shown_width()
    {
        var media = new MediaFoundationDecoder();
        // The MKV said 15 frames a second for 30, and every second frame was dropped to make it so.
        var mkv = Made("thirty.mkv", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "veryfast");
        using (var video = media.OpenVideo(mkv, 0)!)
        {
            var frames = 0;
            while (video.Next() is not null)
                frames++;
            Assert.Equal(60, frames);
            Assert.Equal(30, video.FrameRate, 1);
        }
        // A DVD's 720 pixels across are shown as 768: the picture comes at that width, not squeezed.
        var dvd = Made("dvd.mp4", "-f", "lavfi", "-i", "testsrc2=size=720x576:rate=25", "-t", "1", "-vf", "setsar=16/15", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "veryfast");
        Assert.Equal((768, 576), (media.Probe(dvd)!.Width, media.Probe(dvd)!.Height));
        using (var video = media.OpenVideo(dvd, 0)!)
            Assert.Equal((768, 576), (video.Next()!.Value.Frame.Width, video.Next()!.Value.Frame.Height));
    }

    [Fact]
    public void A_low_rate_sound_reads_at_its_own_rate_and_surround_at_all_its_channels()
    {
        var media = new MediaFoundationDecoder();
        // AAC at 22 kHz was first said to be at 44, and came out twice as fast.
        var low = Made("low.m4a", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=22050", "-t", "3", "-c:a", "aac", "-ac", "2");
        using (var audio = media.OpenAudio(low)!)
        {
            Assert.Equal(22050, audio.SampleRate);
            var sound = SoundEdit.Read(audio);
            Assert.InRange(sound.Frames / (double)sound.Rate, 2.95, 3.1);
        }
        Assert.Equal(22050, media.Probe(low)!.SampleRate);
    }

    [Fact(Timeout = 20_000)]
    public async Task A_ten_bit_video_is_refused_at_once_and_its_sound_still_read()
    {
        // Windows' decoder took such a file and then never gave a frame: whatever read it hung for good.
        var deep = Made("deep.mp4", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "1",
            "-c:v", "libx264", "-pix_fmt", "yuv420p10le", "-preset", "veryfast", "-c:a", "aac");
        var media = new MediaFoundationDecoder();
        await Task.Run(() =>
        {
            Assert.Null(media.OpenVideo(deep, 0));
            var info = media.Probe(deep)!;
            Assert.False(info.HasVideo);
            Assert.True(info.HasAudio);
        }, TestContext.Current.CancellationToken);
        Assert.StartsWith("deep.mp4 is H.264 video of a kind Windows doesn't decode", MediaDecoders.WhyNoPicture(deep, media));
    }

    [Theory]
    // A video that says it is in the older colour standard came out in the newer (some phones' videos, DVDs), and a
    // small one that says the newer in the older: up to 30 of 255 off in strong colours.
    [InlineData("1280x720", "bt601", "tv")]
    [InlineData("1280x720", "bt601", "pc")]
    [InlineData("640x480", "bt709", "tv")]
    [InlineData("640x480", "bt601", "tv")]
    [InlineData("1280x720", "bt709", "pc")]
    public void A_videos_colours_are_read_by_the_standard_and_the_range_it_names(string size, string standard, string range)
    {
        (int R, int G, int B)[] made = [(0xC0, 0x20, 0x20), (0x20, 0xC0, 0x20), (0x20, 0x20, 0xC0), (0xE0, 0xB0, 0x90)];
        var (w, h) = (int.Parse(size.Split('x')[0]) / made.Length, size.Split('x')[1]);
        var inputs = made.SelectMany(c => (string[])["-f", "lavfi", "-i", $"color=c=0x{c.R:X2}{c.G:X2}{c.B:X2}:s={w}x{h}:r=30"]);
        var named = standard == "bt709" ? "bt709" : "smpte170m";
        var path = Made($"{size}-{standard}-{range}.mp4", [.. inputs,
            "-filter_complex", $"[0][1][2][3]hstack=inputs=4,scale=out_color_matrix={standard}:out_range={range},format={(range == "pc" ? "yuvj420p" : "yuv420p")}",
            "-t", "0.5", "-c:v", "libx264", "-preset", "veryfast", "-bf", "0", "-colorspace", named, "-color_primaries", named, "-color_trc", named, "-color_range", range]);
        using var video = new MediaFoundationDecoder().OpenVideo(path, 0)!;
        var frame = video.Next()!.Value.Frame;
        for (var i = 0; i < made.Length; i++)
            Near(made[i], At(frame, (2 * i + 1) / (2.0 * made.Length), 0.5), within: 7);
    }
}
#endif
