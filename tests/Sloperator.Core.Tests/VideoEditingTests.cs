using System.Diagnostics;
using Sloperator.Core.Comfy;
using Sloperator.Core.Media;
using Sloperator.Core.Render;
using Sloperator.Core.Tools;
using Sloperator.Core.Video;
using SkiaSharp;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// The video tool edits as a video editor does: clips trimmed, sped up, reversed, frozen, cropped,
/// zoomed and coloured, any of ffmpeg's transitions between them, titles and pictures over them,
/// sounds faded, shots made of the user's files or of scenes drawn, and the video exported.
/// </summary>
public sealed class VideoEditingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sl-edit-").FullName;

    public void Dispose()
    {
        for (var i = 0; i < 10; i++)
            try { Directory.Delete(_dir, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
    }

    private Storyboard Video(string json)
    {
        var board = Storyboard.Parse(json);
        board.Folder = _dir;
        foreach (var shot in board.Shots)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(board.ShotVideo(shot))!);
            File.WriteAllText(board.ShotVideo(shot), "");
        }
        return board;
    }

    private static string Graph(List<string> args) => args[args.IndexOf("-filter_complex") + 1];

    [Fact]
    public void A_clip_is_cut_as_its_shot_says_before_it_fills_the_frame()
    {
        var board = Video("""
            {"width": 640, "height": 360, "fps": 24, "shots": [
              {"id": "s1", "seconds": 3, "in": 1.5, "speed": 2, "reverse": true, "crop": {"x": 0.1, "y": 0.2, "width": 0.5, "height": 0.5}, "color": "bw", "zoom": "in", "freeze": 2},
              {"id": "s2", "seconds": 2, "transition": "wipe", "transitionSeconds": 0.6, "speed": 0.25, "volume": 0},
              {"id": "s3", "seconds": 2, "transition": "iris", "color": {"brightness": 0.2, "saturation": 1.5, "look": "warm"}}],
             "fadeIn": 0, "quality": "draft"}
            """);
        var (args, length, _) = VideoMontage.Plan(board, _ => true, "out.mp4");
        var graph = Graph(args);
        Assert.Contains("[0:v]trim=start=1.5,setpts=PTS-STARTPTS,reverse,setpts=PTS/2,crop=iw*0.5:ih*0.5:iw*0.1:ih*0.2,hue=s=0,trim=duration=2,setpts=PTS-STARTPTS,tpad=", graph);
        Assert.Contains("scale=1280:720,zoompan=z='(1+(0.25)*min(on/71,1))'", graph);
        Assert.Contains("eq=brightness=0.1:contrast=1:saturation=1.5,colorbalance=", graph);
        Assert.Contains("xfade=transition=wipeleft:duration=0.6:offset=2.4", graph);
        Assert.Contains("xfade=transition=circleopen:duration=0.8:offset=3.6", graph);
        // Its sound follows its picture: trimmed, reversed, twice as fast; a muted clip is silence.
        Assert.Contains("[0:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,atrim=start=1.5,asetpts=PTS-STARTPTS,areverse,atempo=2,atrim=duration=2,", graph);
        Assert.Contains("anullsrc=r=48000:cl=stereo,atrim=duration=2,aformat=sample_fmts=fltp:channel_layouts=stereo[a1]", graph);
        Assert.DoesNotContain("fade=t=in", graph);
        Assert.Contains("veryfast", args);
        Assert.Equal(3 + 2 - 0.6 + 2 - 0.8, length, 3);
    }

    [Fact]
    public void A_clip_is_turned_mirrored_fitted_whole_and_given_effects()
    {
        var board = Video("""
            {"width": 640, "height": 360, "fps": 24, "shots": [
              {"id": "s1", "seconds": 2, "rotate": 90, "flip": true, "crop": {"x": 0, "y": 0.25, "width": 1, "height": 0.5}, "fit": "blurred background", "effects": "vignette, film grain"},
              {"id": "s2", "seconds": 2, "rotate": -90, "fit": "letterbox", "effect": "mosaic"},
              {"id": "s-3", "seconds": 2, "rotate": 180, "flip": "vertical", "fit": "blur", "effects": ["Sharpen", "negative", "sparkles", "sharp"]},
              {"id": "s4", "seconds": 2, "rotate": 400, "flip": "sideways", "fit": "stretch", "effects": []}]}
            """);
        // As they are kept: a quarter turn at a time, names as the storyboard has them, what it doesn't know left out.
        Assert.Equal([90, 270, 180, 0], board.Shots.Select(s => s.Rotate)); // 400 is 360 + 40: the nearest quarter turn is none
        Assert.Equal(["horizontal", null, "vertical", null], board.Shots.Select(s => s.Flip));
        Assert.Equal(["blur", "fit", "blur", null], board.Shots.Select(s => s.Fit));
        Assert.Equal(["vignette", "grain"], board.Shots[0].Effects);
        Assert.Equal(["pixelate"], board.Shots[1].Effects);
        Assert.Equal(["sharpen", "invert"], board.Shots[2].Effects);
        Assert.Null(board.Shots[3].Effects);
        Assert.DoesNotContain("effects", Storyboard.Parse(board.ToJson()).ToJsonObject()["shots"]![3]!.AsObject().Select(p => p.Key));

        var graph = Graph(VideoMontage.Plan(board, _ => true, "out.mp4").Arguments);
        // Turned and mirrored before it is cropped; whole over a blurred copy of itself, with labels of its own; then its effects.
        Assert.Contains("[0:v]setpts=PTS-STARTPTS,transpose=clock,hflip,crop=iw*1:ih*0.5:iw*0:ih*0.25,tpad=", graph);
        Assert.Contains("split[fas1][fbs1];[fas1]scale=640:360:force_original_aspect_ratio=increase,crop=640:360,gblur=sigma=10,eq=brightness=-0.06[bgs1];" +
                        "[fbs1]scale=640:360:force_original_aspect_ratio=decrease[fgs1];[bgs1][fgs1]overlay=(W-w)/2:(H-h)/2,setsar=1,fps=24,vignette=PI/4.5,noise=alls=14:allf=t+u,format=yuv420p[v0]", graph);
        // Turned left, whole with black bars, in blocks a thirtieth of the frame's height that come back to its size.
        Assert.Contains("[1:v]setpts=PTS-STARTPTS,transpose=cclock,tpad=", graph);
        Assert.Contains("scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=24,scale=53:30:flags=area,scale=640:360:flags=neighbor,format=yuv420p[v1]", graph);
        // Half a turn is both mirrors; with upside down asked for as well, that one cancels.
        Assert.Contains("[2:v]setpts=PTS-STARTPTS,hflip,tpad=", graph);
        Assert.Contains("split[fas3][fbs3]", graph);
        Assert.Contains("unsharp=5:5:1.2:5:5:0,negate,format=yuv420p[v2]", graph);
        // Nothing asked for: as before.
        Assert.Contains("[3:v]setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration=2,trim=duration=2,setpts=PTS-STARTPTS,scale=640:360:force_original_aspect_ratio=increase,crop=640:360,setsar=1,fps=24,format=yuv420p[v3]", graph);
        Assert.Equal("cropped to 0,0.25 1x0.5, turned 90°, mirrored, whole over a blur, vignette grain", VideoEditorTool.Edits(board.Shots[0]));
        Assert.Null(VideoEditorTool.UnknownFields(System.Text.Json.Nodes.JsonNode.Parse("""{"shots": [{"id": "s1", "rotate": 90, "flip": "horizontal", "fit": "fit", "effects": ["blur"]}]}""")!.AsObject()));
    }

    [Fact]
    public void Overlays_are_laid_over_from_their_start_framed_ones_as_they_are_and_keyed_ones_without_their_colour()
    {
        var board = Video("""{"width": 640, "height": 360, "shots": [{"id": "s1", "seconds": 6}]}""");
        OverlayInput[] overlays =
        [
            new("title.png", true, 0, 0, 640, 360, 1, 2, 0.3, 0.3, 1) { Framed = true },
            new("presenter.mp4", false, 400, 200, 200, 150, 2, 3, 0, 0, 0.8, 1, 1) { Key = "0x00FF00" },
        ];
        var (args, _, _) = VideoMontage.Plan(board, _ => true, "out.mp4", overlays);
        var graph = Graph(args);
        Assert.Contains("-loop", args);
        Assert.Contains("[1:v]format=rgba,fade=t=in:st=0:d=0.3:alpha=1,fade=t=out:st=1.7:d=0.3:alpha=1,setpts=PTS-STARTPTS+1/TB[o0]", graph);
        Assert.Contains("overlay=x=0:y=0:eof_action=pass:enable='between(t,1,3)'", graph);
        // Words that slide up into place: from 29 pixels below, easing to a stop over half a second.
        var (slid, _, _) = VideoMontage.Plan(board, _ => true, "out.mp4", [new("title.png", true, 0, 0, 640, 360, 1, 2, 0.3, 0.3, 1) { Framed = true, SlideY = 29, SlideSeconds = 0.5 }]);
        Assert.Contains("overlay=x=0:y='0+29*pow(1-min(1,(t-1)/0.5),2)'", Graph(slid));
        Assert.Contains("[2:v]scale=200:150:force_original_aspect_ratio=decrease,chromakey=0x00FF00:0.15:0.08,format=rgba,colorchannelmixer=aa=0.8,setpts=PTS-STARTPTS+2/TB[o1]", graph);
        Assert.Contains("overlay=x='400+(200-overlay_w)*1':y='200+(150-overlay_h)*1'", graph);
    }

    [Theory]
    [InlineData(0.25, "atempo=0.5,atempo=0.5")]
    [InlineData(0.8, "atempo=0.8")]
    [InlineData(3, "atempo=2,atempo=1.5")]
    [InlineData(8, "atempo=2,atempo=2,atempo=2")]
    public void A_speed_is_tempo_steps_ffmpeg_takes(double speed, string tempo) =>
        Assert.Equal(tempo, string.Join(",", VideoMontage.Tempo(speed)));

    [Theory]
    [InlineData("Wipe", "wipeleft")]
    [InlineData("cross-fade", "dissolve")]
    [InlineData("fade to black", "fade")]
    [InlineData("dip to white", "fadewhite")]
    [InlineData("slideup", "slideup")]
    [InlineData("star wars swirl", "cut")]
    public void Transitions_are_known_by_their_usual_names(string said, string named) =>
        Assert.Equal(named, Storyboard.TransitionNamed(said));

    [Fact]
    public void Edits_and_overlays_are_kept_with_the_video_and_read_back()
    {
        var board = Video("""
            {"title": "Kept", "shots": [{"id": "s1", "seconds": 4, "in": 1, "speed": 1, "volume": 0.5, "zoom": "out", "transition": "slideleft"}],
             "sounds": [{"id": "a1", "source": "file", "place": "References/rain.mp3", "fadeIn": 1, "fadeOut": 2}],
             "overlays": [{"text": "Hello", "position": "lower third", "start": 1, "seconds": 2}, {"id": "logo", "file": "logo.png", "position": {"x": 0.8, "y": 0.05, "width": 0.15, "height": 0.15}, "opacity": 0.8}],
             "fadeOut": 2, "captions": true, "quality": "best"}
            """);
        Assert.Null(board.Shots[0].Speed); // 1 is as it is
        Assert.Equal(0.5, board.Shots[0].Volume);
        Assert.Equal(1.25, board.Shots[0].Zoom!.From.Scale);
        Assert.Equal(["o1", "logo"], board.Overlays.Select(o => o.Id));
        Assert.Equal(0.8, board.Overlays[1].Box!.X);
        board.Save();
        var again = Storyboard.Load(_dir);
        Assert.Equal(1, again.Shots[0].In);
        Assert.Equal("slideleft", again.Shots[0].Transition);
        Assert.Equal(2, again.Sounds[0].FadeOut);
        Assert.Equal("Hello", again.Overlays[0].Text);
        Assert.Equal("lower third", again.Overlays[0].Position);
        Assert.Equal(2, again.FadeOut);
        Assert.True(again.Captions);
        Assert.Equal("best", again.Quality);
        var overview = VideoEditorTool.Overview(again);
        Assert.Contains("Edited: in 1 s, volume 0.5, zoom 1.25→1", overview);
        Assert.Contains("o1: the words \"Hello\", from 1 s for 2 s, lower third.", overview);

        // A change matched by id, as the shots are.
        var changed = Storyboard.Merge(again, System.Text.Json.Nodes.JsonNode.Parse("""{"overlays": [{"id": "o1", "text": "Bye"}, {"id": "logo", "delete": true}]}""")!.AsObject());
        Assert.Equal("Bye", Assert.Single(changed.Overlays).Text);
    }

    [Fact]
    public void Words_in_the_same_place_at_the_same_time_are_said_to_print_over_each_other()
    {
        var board = Video("""
            {"shots": [{"id": "s1", "seconds": 8}],
             "overlays": [{"id": "o1", "text": "Episode one", "position": "lower third", "start": 3.5, "seconds": 2.5},
                          {"id": "o2", "text": "Chapter one", "position": "lower third", "start": 3, "seconds": 3},
                          {"id": "o3", "text": "The end", "position": "center", "start": 3, "seconds": 3}]}
            """);
        var overview = VideoEditorTool.Overview(board);
        Assert.Contains("Note: o1 and o2 are both lower third from 3.5 to 6 s", overview);
        Assert.DoesNotContain("o3 are both", overview);
        Assert.DoesNotContain("and o3", overview);
    }

    [Fact]
    public void The_judge_is_told_which_shots_are_the_users_own_and_not_to_be_made_again()
    {
        var board = Video("""{"shots": [{"id": "s1", "prompt": "a fox runs"}, {"id": "s2", "file": "bars.mp4"}, {"id": "s3", "scene": "intro.svg", "prompt": "the title"}]}""");
        var judge = VideoCheck.JudgeVideo(board);
        Assert.Contains("- s1: a fox runs", judge);
        Assert.Contains("- s2: the person's own footage, bars.mp4 (not made by a model, so never ask to redo it", judge);
        Assert.Contains("- s3: a scene drawn from intro.svg: the title (not made by a model", judge);
    }

    [Fact]
    public void Background_removal_is_turned_off_where_a_workflow_asks_for_it()
    {
        // rembg without onnxruntime ends ComfyUI's worker and the job never finishes (TRELLIS.2, 2026-11-19).
        var workflow = ComfyWorkflow.FromApi(System.Text.Json.Nodes.JsonNode.Parse("""
            {"194": {"class_type": "Trellis2PreProcessImage", "inputs": {"padding": 10, "remove_background": true, "max_size": 1024}},
             "195": {"class_type": "Other", "inputs": {"remove_background": false}}, "196": {"class_type": "Text", "inputs": {"text": "x"}}}
            """));
        Assert.Equal(["194"], ComfyTool.KeepBackgrounds(workflow));
        Assert.False((bool)workflow.Nodes["194"]!["inputs"]!["remove_background"]!);
    }

    [Fact]
    public void A_file_from_elsewhere_a_shot_or_overlay_is_made_of_is_copied_into_references()
    {
        var outside = Directory.CreateTempSubdirectory("sl-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "holiday.mp4"), "video");
            File.WriteAllText(Path.Combine(outside, "logo.png"), "png");
            var board = Storyboard.Parse($$"""
                {"shots": [{"id": "s1", "file": {{System.Text.Json.Nodes.JsonValue.Create(Path.Combine(outside, "holiday.mp4")).ToJsonString()}}}],
                 "overlays": [{"id": "o1", "file": {{System.Text.Json.Nodes.JsonValue.Create(Path.Combine(outside, "logo.png")).ToJsonString()}}}]}
                """);
            board.Folder = _dir;
            var taken = board.TakeInPictures();
            Assert.Equal(2, taken.Count);
            Assert.Equal("holiday.mp4", board.Shots[0].File);
            Assert.Equal("logo.png", board.Overlays[0].File);
            Assert.True(File.Exists(Path.Combine(_dir, "References", "holiday.mp4")));
            Assert.Equal(Path.Combine(_dir, "References", "logo.png"), board.FindFile("logo"));
        }
        finally { Directory.Delete(outside, true); }
    }

    [Fact]
    public void Music_goes_quieter_under_a_voice_over_and_the_voice_over_does_not()
    {
        var board = Video("""
            {"shots": [{"id": "s1", "seconds": 8}],
             "sounds": [{"id": "a1", "source": "file", "place": "References/song.mp3"}, {"id": "a2", "source": "narration", "text": "Welcome to Bean There.", "start": 2}]}
            """);
        Assert.Equal(VideoSound.Speech, board.Sounds[1].Source);
        Assert.EndsWith(".flac", board.SoundFile(board.Sounds[1]));
        Directory.CreateDirectory(board.ReferencesFolder);
        Directory.CreateDirectory(board.GeneratedFolder);
        File.WriteAllText(board.SoundFile(board.Sounds[0]), "");
        File.WriteAllText(board.SoundFile(board.Sounds[1]), "");
        var (args, _, _) = VideoMontage.Plan(board, _ => true, "out.mp4", null, file => file.EndsWith(".flac") ? 3.5 : 60);
        var graph = Graph(args);
        Assert.Contains("asetpts=PTS-STARTPTS,volume='if(between(t,2,5.5),0.35,1)':eval=frame[s0]", graph);
        Assert.Contains("asetpts=PTS-STARTPTS[s1]", graph);
        Assert.Contains("voice-over saying \"Welcome to Bean There.\"", VideoEditorTool.Overview(board));
    }

    [Fact]
    public void A_voice_over_not_made_yet_is_a_flac_when_the_video_is_read_again()
    {
        var board = Storyboard.Parse("""{"shots": [{"id": "s1", "seconds": 4}], "sounds": [{"id": "v1", "source": "speech", "text": "Hello."}]}""");
        board.Folder = _dir;
        board.Save();
        var again = Storyboard.Load(_dir);
        Assert.EndsWith(Path.Combine("Generated", "v1.flac"), again.SoundFile(again.Sounds[0]));
    }

    [Fact]
    public void Fields_nothing_reads_are_said_and_titles_in_a_shot_are_sent_to_the_overlays()
    {
        var change = System.Text.Json.Nodes.JsonNode.Parse("""
            {"title": "Promo", "shots": [{"id": "s4", "overlays": [{"text": "Bean There"}], "speed": 2, "duration": 3}], "music": "jazz", "overlays": [{"id": "o1", "text": "Hi", "animation": "pop"}]}
            """)!.AsObject();
        var said = VideoEditorTool.UnknownFields(change);
        Assert.NotNull(said);
        Assert.Contains("\"overlays\" in shot s4 (titles and words over the video go in the storyboard's own overlays", said);
        Assert.Contains("\"duration\" in shot s4 (no such field)", said);
        Assert.Contains("\"music\" (the storyboard has no such field)", said);
        Assert.Contains("\"animation\" in overlay o1", said);
        Assert.DoesNotContain("speed", said);
        Assert.Null(VideoEditorTool.UnknownFields(System.Text.Json.Nodes.JsonNode.Parse("""{"shots": [{"id": "s1", "Speed": 2, "zoom": "in"}], "fadeOut": 1}""")!.AsObject()));
    }

    [Fact]
    public void Words_are_drawn_where_their_place_puts_them()
    {
        var png = Path.Combine(_dir, "title.png");
        VideoOverlays.DrawWords(new VideoOverlay { Text = "Top", Position = "top", Background = "#FF0000" }, 640, 360, png);
        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal((640, 360), (bitmap.Width, bitmap.Height));
        // The red box behind the words near the top, nothing at the bottom.
        var red = Enumerable.Range(0, 80).Any(y => bitmap.GetPixel(320, y) is { Red: > 200, Green: < 60, Alpha: > 200 });
        Assert.True(red);
        Assert.Equal(0, bitmap.GetPixel(320, 340).Alpha);
    }

    /// <summary>A test clip from ffmpeg's own pattern and a tone.</summary>
    private static void TestClip(string ffmpeg, string file, double seconds, string pattern = "testsrc2")
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", $"{pattern}{(pattern.Contains('=') ? ':' : '=')}size=320x180:rate=24:duration={seconds}", "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
                     "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", file })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public async Task A_video_of_the_users_clips_a_picture_and_a_scene_is_joined_with_titles_over_it_and_exported()
    {
        var decoder = new FfmpegDecoder(() => []);
        var ffmpeg = decoder.Program;
        if (ffmpeg is null)
            Assert.Skip("No ffmpeg on this PC.");
        if (Web.HeadlessBrowser.FindExecutable() is null)
            Assert.Skip("No Edge or Chrome on this PC.");
        var ct = TestContext.Current.CancellationToken;
        var references = Path.Combine(_dir, "References");
        Directory.CreateDirectory(references);
        TestClip(ffmpeg, Path.Combine(references, "walk.mp4"), 4);
        TestClip(ffmpeg, Path.Combine(_dir, "elsewhere.mov"), 2, "smptebars");
        TestClip(ffmpeg, Path.Combine(references, "green.mp4"), 2, "color=c=0x00FF00");
        using (var bitmap = new SKBitmap(200, 100))
        {
            using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.MediumPurple);
            using var stream = File.Create(Path.Combine(references, "still.png"));
            bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        }
        File.WriteAllText(Path.Combine(references, "sting.svg"), """
            <svg xmlns="http://www.w3.org/2000/svg" width="640" height="360"><rect width="640" height="360" fill="#113"/>
            <circle cx="60" cy="180" r="40" fill="#fc0"><animate attributeName="cx" from="60" to="580" dur="2s" fill="freeze"/></circle></svg>
            """);
        // A moving badge with a clear background, drawn over the video as a scene.
        File.WriteAllText(Path.Combine(references, "badge.svg"), """
            <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200"><circle cx="100" cy="100" r="20" fill="#e33"><animate attributeName="r" from="20" to="90" dur="1s" fill="freeze"/></circle></svg>
            """);
        var board = Storyboard.Create(_dir, "Edit test");
        board = Storyboard.Merge(board, System.Text.Json.Nodes.JsonNode.Parse($$"""
            {"width": 640, "height": 360, "fps": 24,
             "frames": [{"id": "f1", "delete": true}, {"id": "f2", "delete": true}],
             "shots": [{"id": "s1", "file": "walk.mp4", "seconds": 2, "in": 1, "speed": 1.5, "color": "sepia", "first": null, "last": null},
                       {"id": "s2", "file": {{System.Text.Json.Nodes.JsonValue.Create(Path.Combine(_dir, "elsewhere.mov")).ToJsonString()}}, "seconds": 1.5, "transition": "wipeleft", "reverse": true},
                       {"id": "s3", "file": "still.png", "seconds": 2, "zoom": "in", "transition": "dissolve"},
                       {"id": "s4", "scene": "sting.svg", "seconds": 2, "transition": "slideup", "line": "", "freeze": 1.5}],
             "overlays": [{"text": "A title", "position": "center", "start": 0.2, "seconds": 1.5, "motion": "slide up"},
                          {"file": "still.png", "position": "top right", "start": 1, "seconds": 3, "opacity": 0.7},
                          {"file": "green.mp4", "position": "bottom left", "start": 2, "seconds": 2, "key": "#00FF00"},
                          {"scene": "badge.svg", "position": "top left", "start": 0.5, "seconds": 1.5}],
             "sounds": [], "fadeOut": 0.5}
            """)!.AsObject());
        board.Save();
        Assert.All(board.Shots, s => Assert.True(s.Assembled));
        Assert.Empty(board.Frames);

        var maker = new VideoMaker(ComfyHub.For(Path.Combine(_dir, ".comfy")), decoder) { Scenes = new SceneRenderer(Path.Combine(_dir, ".browser")) };
        foreach (var shot in board.Shots)
        {
            var made = await maker.MakeShotAsync(board, shot, null, CancellationToken.None, ct);
            Assert.True(File.Exists(made.File), shot.Id);
            Assert.True(VideoMaker.IsCurrent(board, shot), shot.Id);
        }
        var joined = await VideoMontage.JoinAsync(board, ffmpeg, decoder, null, ct, new SceneRenderer(Path.Combine(_dir, ".browser")));
        Assert.Empty(joined.Notes);
        var info = decoder.Probe(joined.File);
        Assert.NotNull(info);
        // 2 + 1.5 - 0.8 (wipe) + 2 - 0.5 (dissolve) + 2 - 0.8 (slide).
        Assert.InRange(info!.Duration.TotalSeconds, 5.2, 5.6);
        Assert.False(board.JoinedIsStale);

        var gif = await VideoMontage.ExportAsync(board, ffmpeg, Path.Combine(_dir, "out", "small.gif"), 320, null, null, ct);
        Assert.True(new FileInfo(gif).Length > 1000);
        var sound = await VideoMontage.ExportAsync(board, ffmpeg, Path.Combine(_dir, "out", "sound.mp3"), null, null, null, ct);
        Assert.True(File.Exists(sound));
        // A vertical one from it: its middle, cut to that shape, not stretched.
        var vertical = await VideoMontage.ExportAsync(board, ffmpeg, Path.Combine(_dir, "out", "vertical.mp4"), 360, 640, null, ct);
        var tall = decoder.Probe(vertical);
        Assert.Equal((360, 640), (tall!.Width, tall.Height));
    }
}
