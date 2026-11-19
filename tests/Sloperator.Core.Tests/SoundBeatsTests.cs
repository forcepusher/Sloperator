using Sloperator.Core.Media;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>A piece of music's beat found (<see cref="SoundBeats"/>), so a video can be cut on it.</summary>
public class SoundBeatsTests
{
    private const int Rate = 44100;

    /// <summary>A drum loop: a kick on each beat from <paramref name="first"/> seconds, a hat between beats, a held bass note under them.</summary>
    private static SoundSamples Loop(double bpm, double first, double seconds, int seed = 1)
    {
        var random = new Random(seed);
        var samples = new float[(int)(seconds * Rate)];
        var beat = 60 / bpm;
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(0.08 * Math.Sin(2 * Math.PI * 110 * i / Rate));
        for (var k = 0; first + k * beat / 2 < seconds; k++)
        {
            var at = (int)((first + k * beat / 2) * Rate);
            var kick = k % 2 == 0;
            for (var i = 0; i < Rate / 8 && at + i < samples.Length; i++)
            {
                var fade = Math.Exp(-i / (Rate * (kick ? 0.04 : 0.01)));
                samples[at + i] += (float)(kick ? 0.7 * fade * Math.Sin(2 * Math.PI * 60 * i / Rate) + 0.2 * fade * (random.NextDouble() * 2 - 1) : 0.15 * fade * (random.NextDouble() * 2 - 1));
            }
        }
        return new SoundSamples(samples, Rate, 1);
    }

    [Theory]
    [InlineData(90, 0.2)]
    [InlineData(120, 0.31)]
    [InlineData(128, 0.05)]
    [InlineData(150, 0.4)]
    public void The_beat_of_a_drum_loop_is_found_where_it_falls(double bpm, double first)
    {
        var grid = SoundBeats.Find(Loop(bpm, first, 24));
        Assert.NotNull(grid);
        Assert.InRange(grid.Bpm, bpm * 0.99, bpm * 1.01);
        Assert.InRange(grid.Interval, 60 / bpm * 0.995, 60 / bpm * 1.005);
        // Every beat found is on a kick, within three hundredths of a second, and nearly all the kicks are found.
        var beat = 60 / bpm;
        foreach (var time in grid.Times)
        {
            var off = (time - first) / beat;
            Assert.InRange(Math.Abs(off - Math.Round(off)) * beat, 0, 0.03);
        }
        Assert.InRange(grid.Times.Count, (int)(24 / beat) - 4, (int)(24 / beat) + 1);
        Assert.InRange(grid.First, first - 0.03, first + beat + 0.03);
    }

    [Fact]
    public void What_has_no_steady_beat_has_none_found()
    {
        // Noise, a held note, silence, and something too short.
        var random = new Random(3);
        var noise = new float[Rate * 10];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = (float)(random.NextDouble() * 0.6 - 0.3);
        Assert.Null(SoundBeats.Find(new SoundSamples(noise, Rate, 1)));
        var note = new float[Rate * 10];
        for (var i = 0; i < note.Length; i++)
            note[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 220 * i / Rate));
        Assert.Null(SoundBeats.Find(new SoundSamples(note, Rate, 1)));
        Assert.Null(SoundBeats.Find(new SoundSamples(new float[Rate * 10], Rate, 1)));
        Assert.Null(SoundBeats.Find(Loop(120, 0, 2)));
    }

    [Fact]
    public void A_stereo_song_at_another_rate_has_the_same_beat()
    {
        var mono = Loop(100, 0.25, 20);
        var stereo = SoundEdit.Convert(mono, 48000, 2);
        var grid = SoundBeats.Find(stereo);
        Assert.NotNull(grid);
        Assert.InRange(grid.Bpm, 99, 101);
    }
}

/// <summary>The beat told to the agent (<c>video action=check</c> with a sound's id), in the video's own time.</summary>
public sealed class SoundBeatToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sl-beats-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    /// <summary>Reads a WAV as it was written here: mono 16-bit.</summary>
    private sealed class Waves : IMediaDecoder
    {
        public MediaInfo? Probe(string path) => null;
        public IVideoReader? OpenVideo(string path, int maxSide) => null;

        public IAudioReader? OpenAudio(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var samples = new float[(bytes.Length - 44) / 2];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = BitConverter.ToInt16(bytes, 44 + i * 2) / 32768f;
            return new Reader(samples);
        }

        private sealed class Reader(float[] samples) : IAudioReader
        {
            private int _at;
            public int SampleRate => 44100;
            public int Channels => 1;
            public TimeSpan Duration => TimeSpan.FromSeconds(samples.Length / 44100.0);

            public int Read(float[] buffer, int offset, int count)
            {
                var n = Math.Min(count, samples.Length - _at);
                Array.Copy(samples, _at, buffer, offset, n);
                _at += n;
                return n;
            }

            public void Seek(TimeSpan time) => _at = (int)(time.TotalSeconds * 44100);
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task Checking_a_sound_tells_its_beat_in_the_videos_time()
    {
        var board = Video.Storyboard.Parse("""
            {"title": "Beat", "shots": [{"id": "s1", "seconds": 8, "prompt": "a street"}],
             "sounds": [{"id": "a1", "source": "file", "place": "Sounds/loop.wav", "start": 2, "trim": 1},
                        {"id": "a2", "source": "file", "place": "Sounds/hum.wav", "start": 0}]}
            """);
        board.Folder = _dir;
        Directory.CreateDirectory(Path.Combine(_dir, "Sounds"));
        // 120 BPM from 0.25 s: a beat every half second. Its first second is cut and it starts at 2 s, so its beats fall at 2.25, 2.75, …
        var random = new Random(1);
        var loop = new float[44100 * 20];
        for (var k = 0; 0.25 + k * 0.5 < 20; k++)
            for (var i = 0; i < 4000; i++)
                loop[(int)((0.25 + k * 0.5) * 44100) + i] += (float)(0.7 * Math.Exp(-i / 1800.0) * Math.Sin(2 * Math.PI * 60 * i / 44100.0) + 0.2 * Math.Exp(-i / 1800.0) * (random.NextDouble() * 2 - 1));
        SoundEdit.WriteWav(board.SoundFile(board.Sounds[0]), new SoundSamples(loop, 44100, 1));
        var hum = new float[44100 * 10];
        for (var i = 0; i < hum.Length; i++)
            hum[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 100 * i / 44100.0));
        SoundEdit.WriteWav(board.SoundFile(board.Sounds[1]), new SoundSamples(hum, 44100, 1));
        board.Save();
        var ctx = new Tools.ToolContext { Platform = new FakePlatform(), Settings = new Settings.AgentSettings(), WorkingDirectory = _dir, Media = new Waves() };
        var ct = TestContext.Current.CancellationToken;
        var loud = await new Tools.VideoEditorTool().ExecuteAsync(new System.Text.Json.Nodes.JsonObject { ["action"] = "check", ["path"] = _dir, ["id"] = "a1" }, ctx, ct);
        Assert.False(loud.IsError, loud.Text);
        Assert.Contains("120 BPM, a beat every 0.5 s", loud.Text);
        // Each within a hundredth of a second of where it is (the sound is looked at 86 times a second).
        Assert.Matches(@"its beats fall at 2.2[4-6], 2.7[4-6], 3.2[4-6]", loud.Text);
        Assert.Contains("4 beats are 2 s, 8 are 4 s", loud.Text);
        var quiet = await new Tools.VideoEditorTool().ExecuteAsync(new System.Text.Json.Nodes.JsonObject { ["action"] = "check", ["path"] = _dir, ["id"] = "a2" }, ctx, ct);
        Assert.Contains("No steady beat was found in it.", quiet.Text);
        // The file alone, before there is any video for it: its beat in its own time.
        var alone = await new Tools.VideoEditorTool().ExecuteAsync(new System.Text.Json.Nodes.JsonObject { ["action"] = "check", ["path"] = board.SoundFile(board.Sounds[0]) }, ctx, ct);
        Assert.False(alone.IsError, alone.Text);
        Assert.Matches(@"120 BPM, a beat every 0\.5 s, the first 0\.2[4-6]\d? s into it", alone.Text);
        Assert.Contains("4 beats are 2 s, 8 are 4 s", alone.Text);
    }
}
