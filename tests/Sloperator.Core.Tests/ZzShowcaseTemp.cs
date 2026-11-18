using Sloperator.Core.Media;
using Sloperator.Core.Render;
using Xunit;

namespace Sloperator.Core.Tests;

public class ZzShowcaseTemp
{
    [Fact]
    public async Task Demo()
    {
        var dir = Environment.GetEnvironmentVariable("SL_DEMO");
        if (dir is null) Assert.Skip("not asked");
        var decoder = new FfmpegDecoder(() => []);
        var ffmpeg = decoder.Program!;
        var refs = Path.Combine(dir, "References");
        Directory.CreateDirectory(refs);
        void Clip(string file, double seconds, string pattern)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", $"{pattern}=size=1280x720:rate=24:duration={seconds}", "-f", "lavfi", "-i", $"sine=frequency=330:duration={seconds}",
                         "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", file })
                psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardError.ReadToEnd();
            p.WaitForExit();
        }
        Clip(Path.Combine(refs, "city.mp4"), 6, "gradients");
        Clip(Path.Combine(refs, "bars.mp4"), 4, "testsrc2");
        File.WriteAllText(Path.Combine(refs, "intro.svg"), """
            <svg xmlns="http://www.w3.org/2000/svg" width="1280" height="720" viewBox="0 0 1280 720">
              <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#0f1b3d"/><stop offset="1" stop-color="#3d0f2a"/></linearGradient></defs>
              <rect width="1280" height="720" fill="url(#g)"/>
              <circle cx="640" cy="320" r="120" fill="none" stroke="#ff8a3d" stroke-width="18" stroke-dasharray="754" stroke-dashoffset="754" stroke-linecap="round">
                <animate attributeName="stroke-dashoffset" from="754" to="0" dur="1.5s" fill="freeze" calcMode="spline" keyTimes="0;1" keySplines="0.4 0 0.2 1"/>
              </circle>
              <text x="640" y="560" text-anchor="middle" font-family="Segoe UI" font-weight="700" font-size="80" fill="#fff" opacity="0">Night drive
                <animate attributeName="opacity" from="0" to="1" begin="1s" dur="0.8s" fill="freeze"/>
              </text>
            </svg>
            """);
        var board = Sloperator.Core.Video.Storyboard.Create(dir, Path.GetFileName(dir));
        board = Sloperator.Core.Video.Storyboard.Merge(board, System.Text.Json.Nodes.JsonNode.Parse("""
            {"width": 1280, "height": 720, "fps": 24,
             "frames": [{"id": "f1", "delete": true}, {"id": "f2", "delete": true}],
             "shots": [{"id": "s1", "name": "intro", "scene": "intro.svg", "seconds": 3, "first": null, "last": null},
                       {"id": "s2", "name": "city", "file": "city.mp4", "seconds": 4, "transition": "circleopen", "speed": 1.5, "color": "warm", "zoom": "in"},
                       {"id": "s3", "name": "bars", "file": "bars.mp4", "seconds": 3, "transition": "slideleft", "color": "bw"}],
             "overlays": [{"text": "Episode one", "position": "lower third", "start": 3.5, "seconds": 2.5, "background": "#000000A0"},
                          {"text": "The end", "position": "center", "start": 8, "seconds": 1.5}],
             "fadeOut": 0.8}
            """)!.AsObject());
        board.Save();
        var maker = new Sloperator.Core.Video.VideoMaker(Sloperator.Core.Comfy.ComfyHub.For(Path.Combine(Path.GetTempPath(), "sl-demo-comfy")), decoder) { Scenes = new SceneRenderer(Path.Combine(Path.GetTempPath(), "sl-demo-browser")) };
        foreach (var shot in board.Shots)
            await maker.MakeShotAsync(board, shot, null, CancellationToken.None, TestContext.Current.CancellationToken);
        var joined = await Sloperator.Core.Video.VideoMontage.JoinAsync(board, ffmpeg, decoder, null, TestContext.Current.CancellationToken);
        var look = MediaPreview.ForFiles([joined.File], decoder, true);
        File.WriteAllBytes(joined.File + ".look.jpg", look.Image!);
        File.WriteAllText(joined.File + ".notes.txt", string.Join("\n", joined.Notes));
    }

    [Fact]
    public async Task Showcase()
    {
        var dir = Environment.GetEnvironmentVariable("SL_SHOWCASE");
        if (dir is null) Assert.Skip("not asked");
        var renderer = new SceneRenderer(Path.Combine(dir, "browser"));
        var ffmpeg = new FfmpegDecoder(() => []).Program;
        var log = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (SceneRenderer.Handles(file) && Environment.GetEnvironmentVariable("SL_RENDER") is not null)
            {
                var made = await renderer.RenderAsync(file, null, new SceneOptions(), ffmpeg, null, TestContext.Current.CancellationToken);
                log.Add($"{made.Output} clip={made.Clip} {made.Width}x{made.Height} {made.Seconds}s frames={made.Frames} took={made.Took.TotalSeconds:0.0}s errors=[{string.Join(" | ", made.Errors)}] notes=[{string.Join(" | ", made.Notes)}]");
            }
            if (file.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                var look = MediaPreview.ForFiles([file], new FfmpegDecoder(() => []), true);
                log.Add(string.Join(" ", look.Lines));
                if (look.Image is { } image)
                    File.WriteAllBytes(file + ".look.jpg", image);
            }
        }
        File.WriteAllLines(Path.Combine(dir, "log.txt"), log);
    }
}
