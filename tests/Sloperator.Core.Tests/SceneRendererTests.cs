using System.Text.Json.Nodes;
using Sloperator.Core.Media;
using Sloperator.Core.Render;
using Sloperator.Core.Web;
using SkiaSharp;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// SVG and Three.js scenes drawn in the PC's own Edge (or Chrome) into pictures and clips: a scene
/// that doesn't move is a PNG, one that does a clip of its own length, each frame at its own moment.
/// </summary>
public class SceneRendererTests
{
    private const string Smil = """
        <svg xmlns="http://www.w3.org/2000/svg" width="320" height="180" viewBox="0 0 320 180">
          <rect width="320" height="180" fill="#102040"/>
          <circle cx="40" cy="90" r="30" fill="#ff8800">
            <animate attributeName="cx" from="40" to="280" dur="2s" fill="freeze"/>
          </circle>
        </svg>
        """;

    private const string Still = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 32">
          <rect width="64" height="32" fill="#2266cc"/><circle cx="16" cy="16" r="10" fill="#ffffff"/>
        </svg>
        """;

    private const string Spinning = """
        <!doctype html><html><head><style>body{margin:0;background:#000}</style></head><body>
        <script type="module">
          import * as THREE from 'three';
          const renderer = new THREE.WebGLRenderer({ antialias: true });
          renderer.setSize(innerWidth, innerHeight);
          document.body.appendChild(renderer.domElement);
          const scene = new THREE.Scene();
          const camera = new THREE.PerspectiveCamera(50, innerWidth / innerHeight, 0.1, 100);
          camera.position.z = 4;
          const cube = new THREE.Mesh(new THREE.BoxGeometry(1.5, 1.5, 1.5), new THREE.MeshNormalMaterial());
          scene.add(cube);
          const clock = new THREE.Clock();
          renderer.setAnimationLoop(() => { cube.rotation.y = clock.getElapsedTime(); renderer.render(scene, camera); });
        </script></body></html>
        """;

    private const string StaticThree = """
        <!doctype html><html><head>
        <script type="importmap">{"imports":{"three":"https://cdn.jsdelivr.net/npm/three@0.0.0-none/build/three.module.js","three/addons/":"https://cdn.jsdelivr.net/npm/three@0.0.0-none/examples/jsm/"}}</script>
        </head><body style="margin:0">
        <script type="module">
          import * as THREE from 'three';
          import { RoundedBoxGeometry } from 'three/addons/geometries/RoundedBoxGeometry.js';
          const renderer = new THREE.WebGLRenderer();
          renderer.setSize(innerWidth, innerHeight);
          document.body.appendChild(renderer.domElement);
          const scene = new THREE.Scene();
          scene.background = new THREE.Color(0x335577);
          const camera = new THREE.PerspectiveCamera(50, innerWidth / innerHeight, 0.1, 100);
          camera.position.set(2, 2, 3); camera.lookAt(0, 0, 0);
          scene.add(new THREE.Mesh(new RoundedBoxGeometry(1, 1, 1, 4, 0.2), new THREE.MeshNormalMaterial()));
          renderer.render(scene, camera);
        </script></body></html>
        """;

    private static string Ffmpeg() => new FfmpegDecoder(() => []).Program ?? "";

    private static (string Folder, SceneRenderer Renderer) Setup()
    {
        if (HeadlessBrowser.FindExecutable() is null)
            Assert.Skip("No Edge or Chrome on this PC.");
        var folder = Directory.CreateTempSubdirectory("sl-scene-").FullName;
        return (folder, new SceneRenderer(Path.Combine(folder, "browser")));
    }

    [Fact]
    public async Task An_svg_with_smil_becomes_a_clip_as_long_as_its_animation_with_each_frame_at_its_moment()
    {
        var (folder, renderer) = Setup();
        if (Ffmpeg().Length == 0)
            Assert.Skip("No ffmpeg on this PC.");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var file = Path.Combine(folder, "ball.svg");
            File.WriteAllText(file, Smil);
            var made = await renderer.RenderAsync(file, null, new SceneOptions { Fps = 10 }, Ffmpeg(), null, ct);
            Assert.True(made.Clip);
            Assert.Equal(Path.Combine(folder, "ball.mp4"), made.Output);
            Assert.Equal(20, made.Frames);
            Assert.Equal(2.0, made.Seconds, 3);
            // 320x180 drawn at 1024 along its longer side.
            Assert.Equal((1024, 576), (made.Width, made.Height));
            Assert.Empty(made.Errors);
            var decoder = new FfmpegDecoder(() => []);
            var info = decoder.Probe(made.Output);
            Assert.NotNull(info);
            Assert.InRange(info!.Duration.TotalSeconds, 1.9, 2.1);
        }
        finally { Delete(folder); }
    }

    [Fact]
    public async Task An_svg_that_does_not_move_becomes_a_picture_at_least_1024_wide()
    {
        var (folder, renderer) = Setup();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var file = Path.Combine(folder, "badge.svg");
            File.WriteAllText(file, Still);
            var made = await renderer.RenderAsync(file, null, new SceneOptions(), Ffmpeg(), null, ct);
            Assert.False(made.Clip);
            Assert.Equal(Path.Combine(folder, "badge.png"), made.Output);
            using var bitmap = SKBitmap.Decode(made.Output);
            Assert.Equal((1024, 512), (bitmap.Width, bitmap.Height));
            // The circle's middle is white and the corner blue: drawn to fit, not cropped.
            Assert.Equal(SKColors.White, bitmap.GetPixel(256, 256));
            var corner = bitmap.GetPixel(1000, 20);
            Assert.Equal((0x22, 0x66, 0xcc), (corner.Red, corner.Green, corner.Blue));
        }
        finally { Delete(folder); }
    }

    [Fact]
    public async Task A_spinning_three_js_scene_becomes_a_clip_with_an_import_map_put_in()
    {
        var (folder, renderer) = Setup();
        if (Ffmpeg().Length == 0)
            Assert.Skip("No ffmpeg on this PC.");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var file = Path.Combine(folder, "cube.html");
            File.WriteAllText(file, Spinning);
            var made = await renderer.RenderAsync(file, null, new SceneOptions { Seconds = 1, Fps = 12, Width = 320, Height = 240 }, Ffmpeg(), null, ct);
            Assert.True(made.Clip, string.Join("\n", made.Errors.Concat(made.Notes)));
            Assert.Equal(12, made.Frames);
            Assert.Empty(made.Errors);
            Assert.DoesNotContain(made.Notes, n => n.Contains("one colour", StringComparison.Ordinal));
        }
        finally { Delete(folder); }
    }

    [Fact]
    public async Task A_three_js_scene_drawn_once_becomes_a_picture_with_its_add_ons_from_the_copy_when_the_cdn_has_not_its_version()
    {
        var (folder, renderer) = Setup();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var file = Path.Combine(folder, "box.html");
            File.WriteAllText(file, StaticThree);
            var made = await renderer.RenderAsync(file, Path.Combine(folder, "out", "box.png"), new SceneOptions { Width = 400, Height = 300 }, Ffmpeg(), null, ct);
            Assert.False(made.Clip);
            Assert.Empty(made.Errors);
            Assert.Contains(made.Notes, n => n.Contains("own copy", StringComparison.Ordinal));
            using var bitmap = SKBitmap.Decode(made.Output);
            Assert.Equal((400, 300), (bitmap.Width, bitmap.Height));
            var corner = bitmap.GetPixel(5, 5);
            Assert.Equal((0x33, 0x55, 0x77), (corner.Red, corner.Green, corner.Blue));
            Assert.NotEqual(corner, bitmap.GetPixel(200, 150)); // the box in the middle
        }
        finally { Delete(folder); }
    }

    [Fact]
    public async Task A_script_error_and_a_missing_file_are_told()
    {
        var (folder, renderer) = Setup();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var file = Path.Combine(folder, "broken.html");
            File.WriteAllText(file, """
                <!doctype html><html><body><script>
                fetch('missing.png');
                requestAnimationFrame(() => { undefinedFunction(); });
                </script></body></html>
                """);
            var made = await renderer.RenderAsync(file, Path.Combine(folder, "broken.png"), new SceneOptions(), Ffmpeg(), null, ct);
            Assert.Contains(made.Errors, e => e.Contains("undefinedFunction", StringComparison.Ordinal));
            Assert.Contains(made.Errors, e => e.Contains("missing.png", StringComparison.Ordinal));
            Assert.Contains(made.Notes, n => n.Contains("one colour (#FFFFFF)", StringComparison.Ordinal));
        }
        finally { Delete(folder); }
    }

    [Theory]
    [InlineData("https://cdn.jsdelivr.net/npm/three@0.160.0/build/three.module.js", "build/three.module.js")]
    [InlineData("https://unpkg.com/three@0.150.1/examples/jsm/controls/OrbitControls.js?module", "examples/jsm/controls/OrbitControls.js")]
    [InlineData("https://cdn.jsdelivr.net/npm/three/addons/loaders/GLTFLoader.js", "examples/jsm/loaders/GLTFLoader.js")]
    [InlineData("https://cdnjs.cloudflare.com/ajax/libs/three.js/0.160.0/three.module.min.js", "build/three.module.min.js")]
    [InlineData("https://threejs.org/build/three.module.js", "build/three.module.js")]
    [InlineData("https://cdn.jsdelivr.net/npm/threejs-extras@1/x.js", null)]
    public void Three_js_is_recognised_on_the_usual_cdns(string url, string? path) => Assert.Equal(path, ThreeFiles.PathIn(url));

    [Fact]
    public void The_copy_has_the_build_and_the_add_ons()
    {
        Assert.Matches(@"^0\.\d+\.\d+$", ThreeFiles.Version);
        Assert.NotNull(ThreeFiles.For("https://cdn.jsdelivr.net/npm/three@0.160.0/build/three.module.min.js"));
        Assert.NotNull(ThreeFiles.For("https://cdn.jsdelivr.net/npm/three@0.160.0/build/three.core.js"));
        Assert.NotNull(ThreeFiles.For("https://unpkg.com/three/examples/jsm/loaders/GLTFLoader.js"));
        Assert.Null(ThreeFiles.For("https://unpkg.com/three/examples/jsm/nothing/Here.js"));
    }

    [Fact]
    public void An_import_map_goes_into_a_page_that_imports_three_without_one()
    {
        var page = SceneRenderer.WithImportMap("<html><head><title>x</title></head><body><script type=\"module\">import * as THREE from 'three';</script></body></html>");
        Assert.Contains("<head><script type=\"importmap\">", page);
        var own = "<html><head><script type=\"importmap\">{}</script></head><body><script type=\"module\">import * as THREE from 'three';</script></body></html>";
        Assert.Equal(own, SceneRenderer.WithImportMap(own));
        Assert.Equal("<p>no three</p>", SceneRenderer.WithImportMap("<p>no three</p>"));
    }

    [Theory]
    [InlineData(null, null, false, 0, 0, 1280, 720)]
    [InlineData(null, null, true, 320, 180, 1024, 576)]
    [InlineData(null, null, true, 2000, 1000, 2000, 1000)]
    [InlineData(640, null, true, 320, 160, 640, 320)]
    [InlineData(null, 360, false, 0, 0, 640, 360)]
    [InlineData(800, 600, true, 10, 10, 800, 600)]
    public void A_scene_is_drawn_at_the_size_given_else_its_own(int? width, int? height, bool svg, double ownWidth, double ownHeight, int w, int h) =>
        Assert.Equal((w, h), SceneRenderer.Size(new SceneOptions { Width = width, Height = height }, svg, ownWidth, ownHeight));

    [Fact]
    public void A_scene_is_as_long_as_its_last_animation_or_one_loop_of_its_repeating_ones()
    {
        Assert.Equal((3.5, 3.5), SceneRenderer.NaturalLength(JsonNode.Parse("""{"smilEnd": 3.5, "cssEnd": 1}""")!.AsObject()));
        Assert.Equal((6.0, 0.0), SceneRenderer.NaturalLength(JsonNode.Parse("""{"smilLoops": [1.5, 2], "cssLoops": [3]}""")!.AsObject()));
        Assert.Equal((0.0, 0.0), SceneRenderer.NaturalLength(JsonNode.Parse("{}")!.AsObject()));
        // Loops that meet again only after 30 s: the longest one.
        Assert.Equal(7.3, SceneRenderer.NaturalLength(JsonNode.Parse("""{"smilLoops": [7.3, 6.1]}""")!.AsObject()).Length, 3);
    }

    private static void Delete(string folder)
    {
        for (var i = 0; i < 10; i++)
            try { Directory.Delete(folder, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
    }
}
