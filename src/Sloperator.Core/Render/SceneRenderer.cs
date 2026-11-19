using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Sloperator.Core.Render;

/// <summary>How a scene is drawn; what isn't given comes from the scene.</summary>
public sealed record SceneOptions
{
    /// <summary>How long a clip is; null: the scene's own animation (SMIL, CSS), else 5 s.</summary>
    public double? Seconds { get; init; }

    public int Fps { get; init; } = 30;

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>For a picture of a scene that moves: the moment drawn, in seconds.</summary>
    public double? At { get; init; }

    /// <summary>The background left clear (a PNG, WebM, MOV or GIF keeps it; an MP4 can't).</summary>
    public bool Transparent { get; init; }

    /// <summary>True for a picture, false for a clip; null: by the output's extension, else by whether the scene moves.</summary>
    public bool? Still { get; init; }

    /// <summary>A clip as a folder of PNGs, one a frame (<see cref="SceneRenderer.FrameName"/>), at the output's path: no encoder needed, and a clear background kept.</summary>
    public bool Frames { get; init; }

    /// <summary>
    /// A folder the result goes into when no output is given, under a name no file there has ("plane.png", then
    /// "plane 2.png"): a picture's folder in the Image editor, where each result is the picture's next version.
    /// </summary>
    public string? Into { get; init; }
}

/// <summary>What the renderer made.</summary>
/// <param name="Errors">What the page's scripts threw or logged as errors, and files it asked for that aren't there.</param>
/// <param name="Notes">What to know about the result (a blank picture, a page still loading).</param>
public sealed record SceneMade(string Output, bool Clip, int Width, int Height, double Seconds, int Fps, int Frames,
    IReadOnlyList<string> Errors, IReadOnlyList<string> Notes, TimeSpan Took);

/// <summary>
/// Draws an SVG (still or animated with SMIL or CSS) or an HTML page (a Three.js scene) in the PC's
/// Edge, run headless, into a PNG or, when it moves, a video clip: frame by frame on a stopped clock
/// (<see cref="SceneClock"/>), each frame a screenshot piped to ffmpeg. The page is served from its own
/// folder at https://scene.sloperator/, and three.js from Sloperator's own copy (<see cref="ThreeFiles"/>).
/// One scene at a time per profile folder.
/// </summary>
public sealed partial class SceneRenderer(string profileRoot)
{
    /// <summary>What writes a clip where there is no ffmpeg (<see cref="Media.IMediaDecoder.Write"/>); null where nothing does.</summary>
    public Media.IMediaDecoder? Writer { get; init; }

    private const string Host = "scene.sloperator";

    /// <summary>The longest side a scene is drawn at, and the most frames a clip has.</summary>
    public const int MaxSide = 4096, MaxFrames = 60 * 120;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The browsers to try instead of the PC's (tests).</summary>
    internal IReadOnlyList<string>? Browsers { get; init; }

    public static bool Handles(string path) => Path.GetExtension(path).ToLowerInvariant() is ".svg" or ".html" or ".htm";

    /// <summary>Whether an output file is a picture (PNG, JPEG, WebP) rather than a clip (MP4, WebM, MOV, GIF); null for neither.</summary>
    public static bool? IsPicture(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".webp" => true,
        ".mp4" or ".webm" or ".mov" or ".gif" or ".m4v" => false,
        _ => null,
    };

    /// <summary>Whether an output file is a 3D model (.glb): the Three.js scene saved as glTF.</summary>
    public static bool IsModel(string path) => Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase);

    /// <param name="output">Where the result goes; null: beside the scene, a .png or (moving) an .mp4, a .webm when transparent.</param>
    /// <param name="ffmpeg">ffmpeg's program, which clips need; null when there is none.</param>
    public async Task<SceneMade> RenderAsync(string scene, string? output, SceneOptions options, string? ffmpeg, Action<string>? progress, CancellationToken ct)
    {
        scene = Path.GetFullPath(scene);
        if (!File.Exists(scene))
            throw new RenderException($"There is no file {scene}.");
        if (!Handles(scene))
            throw new RenderException("Only an SVG (.svg) or an HTML page (.html) can be drawn: write the scene as one of those first.");
        if (output is not null && !options.Frames && IsPicture(output) is null && !IsModel(output))
            throw new RenderException("The output must be a picture (.png, .jpg, .webp), a clip (.mp4, .webm, .mov, .gif) or, for a Three.js scene, a 3D model (.glb).");
        var gate = Gates.GetOrAdd(Path.GetFullPath(profileRoot), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await DrawAsync(scene, output, options, ffmpeg, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The switches the browser is started with: the graphics card if there is one, else WebGL in software; sRGB colours as a screen shows them.</summary>
    private static readonly string[] Switches = ["--ignore-gpu-blocklist", "--enable-unsafe-swiftshader", "--force-color-profile=srgb", "--autoplay-policy=no-user-gesture-required"];

    private async Task<SceneMade> DrawAsync(string scene, string? output, SceneOptions options, string? ffmpeg, Action<string>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var folder = Path.GetDirectoryName(scene)!;
        var errors = new List<string>();
        var notes = new List<string>();
        var loading = new ConcurrentDictionary<string, string>(); // request id в†’ URL
        var lastActivity = Stopwatch.StartNew();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var usedCopy = false;
        void Error(string text)
        {
            text = text.Trim();
            lock (errors)
                if (text.Length > 0 && errors.Count < 40 && !errors.Contains(text))
                    errors.Add(text.Clip(500));
        }

        progress?.Invoke("Opening the scene in the browserвЂ¦");
        await using var browser = await DevToolsBrowser.LaunchAsync(Directory.CreateDirectory(profileRoot).FullName, Browsers, Switches, ct).ConfigureAwait(false);
        browser.Event += (method, p) =>
        {
            switch (method)
            {
                case "Fetch.requestPaused":
                    _ = Task.Run(() => AnswerAsync(browser, p, scene, folder, Error, () => usedCopy = true));
                    break;
                case "Network.requestWillBeSent":
                    if ((string?)p["requestId"] is { } id && (string?)p["request"]?["url"] is { } url && !url.StartsWith("data:", StringComparison.Ordinal))
                        loading[id] = url;
                    lastActivity.Restart();
                    break;
                case "Network.loadingFinished":
                case "Network.loadingFailed":
                    if ((string?)p["requestId"] is { } done)
                        loading.TryRemove(done, out _);
                    if (method == "Network.loadingFailed" && (string?)p["errorText"] is { } why && !why.Contains("ERR_ABORTED", StringComparison.Ordinal))
                        Error($"A file failed to load ({why}).");
                    lastActivity.Restart();
                    break;
                case "Page.loadEventFired":
                    loaded.TrySetResult();
                    break;
                case "Runtime.exceptionThrown":
                    var details = p["exceptionDetails"];
                    Error((string?)details?["exception"]?["description"] ?? (string?)details?["text"] ?? "A script error.");
                    break;
                case "Runtime.consoleAPICalled":
                    if (Text(p["type"]) == "error")
                        Error(string.Join(" ", (p["args"] as JsonArray ?? []).Select(a => Text(a?["value"]) ?? Text(a?["description"]) ?? "")));
                    break;
                case "Log.entryAdded":
                    // A file of the page's own that isn't there is told by AnswerAsync, with where it was looked for.
                    if (Text(p["entry"]?["level"]) == "error" && !(Text(p["entry"]?["url"]) ?? "").StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase))
                    {
                        var at = Text(p["entry"]?["url"]) ?? "";
                        Error((Text(p["entry"]?["text"]) ?? "") + (at.Length > 0 ? $" ({at})" : ""));
                    }
                    break;
            }
        };
        var call = TimeSpan.FromSeconds(30);
        await browser.CallAsync("Page.enable", null, call, ct).ConfigureAwait(false);
        await browser.CallAsync("Runtime.enable", null, call, ct).ConfigureAwait(false);
        await browser.CallAsync("Log.enable", null, call, ct).ConfigureAwait(false);
        await browser.CallAsync("Network.enable", null, call, ct).ConfigureAwait(false);
        await browser.CallAsync("Fetch.enable", new JsonObject { ["patterns"] = new JsonArray(new JsonObject { ["urlPattern"] = "*", ["requestStage"] = "Request" }) }, call, ct).ConfigureAwait(false);
        await browser.CallAsync("Page.addScriptToEvaluateOnNewDocument", new JsonObject { ["source"] = SceneClock.Script }, call, ct).ConfigureAwait(false);
        await SizeAsync(browser, options.Width ?? 1280, options.Height ?? 720, ct).ConfigureAwait(false);
        if (options.Transparent)
            await browser.CallAsync("Emulation.setDefaultBackgroundColorOverride", new JsonObject { ["color"] = new JsonObject { ["r"] = 0, ["g"] = 0, ["b"] = 0, ["a"] = 0 } }, call, ct).ConfigureAwait(false);

        var url = $"https://{Host}/{Uri.EscapeDataString(Path.GetFileName(scene))}";
        var navigated = await browser.CallAsync("Page.navigate", new JsonObject { ["url"] = url }, call, ct).ConfigureAwait(false);
        if ((string?)navigated["errorText"] is { Length: > 0 } navError)
            throw new RenderException($"The browser couldn't open the scene: {navError}.");
        try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
        catch (TimeoutException) { notes.Add("The page hadn't finished loading after 60 seconds; it was drawn as it was."); }
        await IdleAsync(loading, lastActivity, notes, ct).ConfigureAwait(false);

        var info = await browser.EvaluateAsync("window.__scene ? window.__scene.info() : null", call, ct).ConfigureAwait(false) as JsonObject
                   ?? throw new RenderException("The page wouldn't run Sloperator's clock (is it an SVG or HTML file?).");
        var svg = (bool?)info["svg"] == true;
        var (width, height) = Size(options, svg, (double?)info["width"] ?? 0, (double?)info["height"] ?? 0);

        // The size is set before the scene's first frame, so a renderer made at window size is made at it.
        await SizeAsync(browser, width, height, ct).ConfigureAwait(false);
        if (svg)
            await browser.EvaluateAsync($"window.__scene.fit({width}, {height})", call, ct).ConfigureAwait(false);
        // A few frames at 0, with time for what they set off (a texture decoded, a model parsed): a
        // scene that sets window.sceneReady to a promise is waited for until it settles.
        for (var i = 0; i < 3; i++)
        {
            await browser.EvaluateAsync("window.__scene.seek(0)", call, ct).ConfigureAwait(false);
            await Task.Delay(60, ct).ConfigureAwait(false);
        }
        await browser.EvaluateAsync("(async () => { const r = window.sceneReady; if (r && typeof r.then === 'function') { try { await r; } catch (e) { window.__scene.errors.push('sceneReady failed: ' + e); } } return true; })()",
            TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        await IdleAsync(loading, lastActivity, notes, ct).ConfigureAwait(false);
        await browser.EvaluateAsync("window.__scene.seek(0)", call, ct).ConfigureAwait(false);

        info = await browser.EvaluateAsync("window.__scene.info()", call, ct).ConfigureAwait(false) as JsonObject ?? info;
        var natural = NaturalLength(info);
        if (output is not null && !options.Frames && IsModel(output))
            return await SaveModelAsync(browser, Path.GetFullPath(output), options.At ?? 0, width, height, errors, notes, Error, clock, ct).ConfigureAwait(false);
        var still = options.Frames ? false : options.Still ?? (output is not null ? IsPicture(output) : null);
        if (still is null)
        {
            progress?.Invoke("Seeing whether the scene movesвЂ¦");
            still = !await MovesAsync(browser, ct).ConfigureAwait(false);
        }
        var clip = still == false;
        if (clip && (width % 2 == 1 || height % 2 == 1))
        {
            // Video is made in whole pairs of pixels.
            (width, height) = (width + width % 2, height + height % 2);
            await SizeAsync(browser, width, height, ct).ConfigureAwait(false);
            if (svg)
                await browser.EvaluateAsync($"window.__scene.fit({width}, {height})", call, ct).ConfigureAwait(false);
        }
        var extension = !clip ? ".png" : options.Transparent ? ".webm" : ".mp4";
        output ??= options.Into is { Length: > 0 } into
            ? FreeName(into, Path.GetFileNameWithoutExtension(scene), extension)
            : Path.Combine(folder, Path.GetFileNameWithoutExtension(scene) + extension);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var fps = Math.Clamp(options.Fps, 1, 120);

        SceneMade made;
        if (!clip)
        {
            var at = options.At ?? (natural.End > 0 ? natural.End : 0);
            await browser.EvaluateAsync($"window.__scene.seek({N(at)})", call, ct).ConfigureAwait(false);
            var png = await ShotAsync(browser, ct).ConfigureAwait(false);
            SavePicture(png, output, new SceneSource(scene, svg, ReadScene(scene), at));
            if (Blank(png) is { } colour)
                notes.Add($"The picture is all one colour ({colour}): the scene drew nothing there.");
            made = new SceneMade(output, false, width, height, 0, fps, 1, [], notes, TimeSpan.Zero);
        }
        else
        {
            var seconds = Math.Clamp(options.Seconds ?? (natural.Length > 0 ? natural.Length : 5), 1.0 / fps, MaxFrames / (double)fps);
            if (options.Seconds is null && natural.Length <= 0)
                notes.Add("The scene names no length of its own (no SMIL or CSS animation ends), so the clip is 5 s: give seconds for another.");
            var frames = Math.Max(1, (int)Math.Round(seconds * fps));
            byte[]? middle;
            if (options.Frames)
            {
                // A picture a frame, in a folder: what is laid over a video joined without ffmpeg, its clear background kept.
                Directory.CreateDirectory(output);
                middle = await FramesAsync(browser, fps, frames, progress, ct, (i, png) => File.WriteAllBytes(Path.Combine(output, FrameName(i)), png)).ConfigureAwait(false);
            }
            else if (ffmpeg is not null)
                middle = await EncodeAsync(browser, output, width, height, fps, frames, options.Transparent, ffmpeg, progress, ct).ConfigureAwait(false);
            else if (Writer?.Writes == true && Path.GetExtension(output).ToLowerInvariant() is ".mp4" or ".m4v")
                middle = await WriteAsync(browser, output, width, height, fps, frames, Writer, progress, ct).ConfigureAwait(false);
            else if (Path.GetExtension(output).Equals(".gif", StringComparison.OrdinalIgnoreCase))
                middle = await GifAsync(browser, output, width, height, fps, frames, options.Transparent, progress, ct).ConfigureAwait(false);
            else
                throw new RenderException(Writer?.Writes == true
                    ? $"Without ffmpeg a clip is made as an .mp4 (no clear background) or a .gif, and {Path.GetFileName(output)} is neither: give one of those, or for a .webm or a .mov " +
                      "download a portable ffmpeg and unpack it into Sloperator's SloperatorData\\tools, then render again."
                    : "Making a clip needs ffmpeg, and there is none on this PC: download a portable build (ffmpeg's own site links them, such as gyan.dev's " +
                      "ffmpeg-release-essentials.zip) and unpack it into Sloperator's SloperatorData\\tools, where it is found, then render again.");
            if (middle is not null && Blank(middle) is { } colour)
                notes.Add($"The middle frame is all one colour ({colour}): the scene drew nothing there.");
            made = new SceneMade(output, true, width, height, frames / (double)fps, fps, frames, [], notes, TimeSpan.Zero);
        }
        if (usedCopy)
            notes.Add($"three.js came from Sloperator's own copy (r{ThreeFiles.Version.Split('.').ElementAtOrDefault(1)}): the CDN the page names couldn't be reached or hasn't that version.");
        if (await browser.EvaluateAsync("window.__scene.errors.slice(0, 20)", call, ct).ConfigureAwait(false) is JsonArray thrown)
            foreach (var e in thrown)
                Error((string?)e ?? "");
        lock (errors)
            return made with { Errors = errors.ToList(), Took = clock.Elapsed };
    }

    /// <summary>Saves the scene (the biggest three.js scene the page made, as it is at <paramref name="at"/>) as a binary glTF.</summary>
    private static async Task<SceneMade> SaveModelAsync(DevToolsBrowser browser, string output, double at, int width, int height, List<string> errors, List<string> notes,
        Action<string> error, Stopwatch clock, CancellationToken ct)
    {
        await browser.EvaluateAsync($"window.__scene.seek({N(at)})", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var fallback = $"https://cdn.jsdelivr.net/npm/three@{ThreeFiles.Version}/examples/jsm/exporters/GLTFExporter.js";
        var data = await browser.EvaluateAsync($"window.__scene.glb({JsonValue.Create(fallback).ToJsonString()})", TimeSpan.FromSeconds(180), ct).ConfigureAwait(false);
        if (data is not JsonValue v || !v.TryGetValue<string>(out var base64) || base64.Length == 0)
            throw new RenderException("The scene couldn't be saved as a .glb.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Media.MediaRelease.Release(output);
        await File.WriteAllBytesAsync(output, Convert.FromBase64String(base64), ct).ConfigureAwait(false);
        if (await browser.EvaluateAsync("window.__scene.errors.slice(0, 20)", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false) is JsonArray thrown)
            foreach (var e in thrown)
                error((string?)e ?? "");
        lock (errors)
            return new SceneMade(output, false, width, height, 0, 0, 0, errors.ToList(), notes, clock.Elapsed);
    }

    /// <summary>The picture's size: as given, the SVG's own (at least 1024 along its longer side, as a drawing stays sharp), else 1280x720.</summary>
    internal static (int Width, int Height) Size(SceneOptions options, bool svg, double ownWidth, double ownHeight)
    {
        double w, h;
        var aspect = svg && ownWidth > 0 && ownHeight > 0 ? ownWidth / ownHeight : 16.0 / 9;
        if (options.Width is { } gw && options.Height is { } gh)
            (w, h) = (gw, gh);
        else if (options.Width is { } ow)
            (w, h) = (ow, ow / aspect);
        else if (options.Height is { } oh)
            (w, h) = (oh * aspect, oh);
        else if (svg && ownWidth > 0 && ownHeight > 0)
        {
            var scale = Math.Max(1, 1024 / Math.Max(ownWidth, ownHeight));
            (w, h) = (ownWidth * scale, ownHeight * scale);
        }
        else
            (w, h) = svg ? (1024, 1024) : (1280, 720);
        var fit = Math.Min(1, MaxSide / Math.Max(w, h));
        return (Math.Max(16, (int)Math.Round(w * fit)), Math.Max(16, (int)Math.Round(h * fit)));
    }

    /// <summary>
    /// How long the scene's own animation is: where its last finite SMIL or CSS animation ends, else
    /// one loop of its repeating ones (all of them back where they started, up to 30 s, else the longest).
    /// </summary>
    internal static (double Length, double End) NaturalLength(JsonObject info)
    {
        var end = Math.Max((double?)info["smilEnd"] ?? 0, (double?)info["cssEnd"] ?? 0);
        var loops = (info["smilLoops"] as JsonArray ?? []).Concat(info["cssLoops"] as JsonArray ?? [])
            .Select(n => (double?)n ?? 0).Where(d => d > 0.01 && double.IsFinite(d)).ToList();
        if (end > 0)
            return (Math.Max(end, (double?)info["smilLoop"] ?? 0), end);
        if (loops.Count == 0)
            return (0, 0);
        // In hundredths of a second, so 1.5 and 2 loop together at 6.
        long lcm = 1;
        foreach (var loop in loops)
        {
            var hundredths = Math.Max(1, (long)Math.Round(loop * 100));
            lcm = lcm / Gcd(lcm, hundredths) * hundredths;
            if (lcm > 3000)
                return (Math.Max(loops.Max(), (double?)info["smilLoop"] ?? 0), 0);
        }
        return (lcm / 100.0, 0);

        static long Gcd(long a, long b) => b == 0 ? a : Gcd(b, a % b);
    }

    private static Task SizeAsync(DevToolsBrowser browser, int width, int height, CancellationToken ct) =>
        browser.CallAsync("Emulation.setDeviceMetricsOverride", new JsonObject
        {
            ["width"] = width, ["height"] = height, ["deviceScaleFactor"] = 1, ["mobile"] = false,
            ["screenWidth"] = width, ["screenHeight"] = height,
        }, TimeSpan.FromSeconds(30), ct);

    /// <summary>Waits until no file has been loading for a moment (a minute at most).</summary>
    private static async Task IdleAsync(ConcurrentDictionary<string, string> loading, Stopwatch lastActivity, List<string> notes, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        while (!loading.IsEmpty || lastActivity.ElapsedMilliseconds < 250)
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(60))
            {
                notes.Add("Still loading after a minute, drawn without them: " + string.Join(", ", loading.Values.Take(4).Select(u => u.Clip(120))));
                return;
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Whether frames at a few moments differ: a scene that never changes is a picture.</summary>
    private static async Task<bool> MovesAsync(DevToolsBrowser browser, CancellationToken ct)
    {
        byte[]? first = null;
        foreach (var t in new[] { 0, 0.5, 1.3, 2.9 })
        {
            await browser.EvaluateAsync($"window.__scene.seek({N(t)})", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            var shot = await ShotAsync(browser, ct).ConfigureAwait(false);
            if (first is null)
                first = shot;
            else if (!shot.AsSpan().SequenceEqual(first))
                return true;
        }
        return false;
    }

    private static async Task<byte[]> ShotAsync(DevToolsBrowser browser, CancellationToken ct)
    {
        var shot = await browser.CallAsync("Page.captureScreenshot", new JsonObject
        {
            ["format"] = "png", ["fromSurface"] = true, ["captureBeyondViewport"] = false, ["optimizeForSpeed"] = true,
        }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return Convert.FromBase64String((string?)shot["data"] ?? throw new RenderException("The browser gave back no picture of the page."));
    }

    /// <summary>The name of a clip's frame in a folder of them (<see cref="SceneOptions.Frames"/>).</summary>
    public static string FrameName(int index) => $"{index:00000}.png";

    /// <summary>Draws every frame and hands its PNG to <paramref name="take"/>; returns the middle frame, to check.</summary>
    private static async Task<byte[]?> FramesAsync(DevToolsBrowser browser, int fps, int frames, Action<string>? progress, CancellationToken ct, Action<int, byte[]> take)
    {
        byte[]? middle = null;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
        {
            await browser.EvaluateAsync($"window.__scene.seek({N(i / (double)fps)})", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            var png = await ShotAsync(browser, ct).ConfigureAwait(false);
            if (i == frames / 2)
                middle = png;
            take(i, png);
            if (i % 5 == 0 || i == frames - 1)
                progress?.Invoke($"Drawing frame {i + 1} of {frames}" + (i > 4 ? $" · {Remaining(clock.Elapsed, i + 1, frames)} left" : ""));
        }
        return middle;
    }

    /// <summary>
    /// Draws every frame and writes the clip with the OS's own encoder (<see cref="Media.IMediaWriter"/>): an MP4 made where
    /// there is no ffmpeg. Returns the middle frame, to check.
    /// </summary>
    private static async Task<byte[]?> WriteAsync(DevToolsBrowser browser, string output, int width, int height, int fps, int frames, Media.IMediaDecoder media,
        Action<string>? progress, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileNameWithoutExtension(output) + ".rendering" + Path.GetExtension(output));
        try
        {
            byte[]? middle;
            using (var writer = media.Write(temp, new Media.MediaWriterOptions(width, height, fps))
                                ?? throw new RenderException("The clip can't be written on this PC: Windows gave no H.264 encoder, and there is no ffmpeg."))
            {
                middle = await FramesAsync(browser, fps, frames, progress, ct, (_, png) => writer.Frame(Frame(png, width, height))).ConfigureAwait(false);
                writer.Finish();
            }
            Media.MediaRelease.Release(output); // the clip made before may be playing in the chat
            File.Move(temp, output, overwrite: true);
            return middle;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Draws every frame into an animated GIF (<see cref="Media.GifWriter"/>), its clear background kept when asked: made with nothing but Sloperator. Returns the middle frame, to check.</summary>
    private static async Task<byte[]?> GifAsync(DevToolsBrowser browser, string output, int width, int height, int fps, int frames, bool transparent, Action<string>? progress, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileNameWithoutExtension(output) + ".rendering.gif");
        try
        {
            byte[]? middle;
            using (var gif = new Media.GifWriter(temp, width, height, fps, transparent))
            {
                middle = await FramesAsync(browser, fps, frames, progress, ct, (_, png) => gif.Frame(Frame(png, width, height, clear: transparent))).ConfigureAwait(false);
                gif.Finish();
            }
            Media.MediaRelease.Release(output);
            File.Move(temp, output, overwrite: true);
            return middle;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>A frame's PNG as a writer takes it: BGRA at the clip's size, on black where the page is clear unless that is to stay clear.</summary>
    private static Platform.RawImage Frame(byte[] png, int width, int height, bool clear = false)
    {
        using var decoded = SKBitmap.Decode(png) ?? throw new RenderException("The browser gave back a picture that can't be read.");
        using var frame = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(frame))
        {
            canvas.Clear(clear ? SKColors.Transparent : SKColors.Black);
            using var image = SKImage.FromBitmap(decoded);
            canvas.DrawImage(image, SKRect.Create(width, height), new SKSamplingOptions(decoded.Width == width && decoded.Height == height ? SKFilterMode.Nearest : SKFilterMode.Linear));
        }
        var bgra = new byte[width * height * 4];
        System.Runtime.InteropServices.Marshal.Copy(frame.GetPixels(), bgra, 0, bgra.Length);
        return new Platform.RawImage(width, height, bgra);
    }

    /// <summary>Draws every frame and pipes it to ffmpeg; returns the middle frame, to check.</summary>
    private static async Task<byte[]?> EncodeAsync(DevToolsBrowser browser, string output, int width, int height, int fps, int frames, bool transparent,
        string ffmpeg, Action<string>? progress, CancellationToken ct)
    {
        var ext = Path.GetExtension(output).ToLowerInvariant();
        var temp = Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileNameWithoutExtension(output) + ".rendering" + ext);
        var args = new List<string> { "-hide_banner", "-y", "-loglevel", "error", "-f", "image2pipe", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-c:v", "png", "-i", "-" };
        args.AddRange(ext switch
        {
            ".webm" => ["-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "28", "-row-mt", "1", "-deadline", "good", "-cpu-used", "2",
                        "-pix_fmt", transparent ? "yuva420p" : "yuv420p", "-auto-alt-ref", "0"],
            ".mov" when transparent => ["-c:v", "prores_ks", "-profile:v", "4444", "-pix_fmt", "yuva444p10le"],
            ".gif" => ["-filter_complex", "[0:v]split[a][b];[a]palettegen=reserve_transparent=1:stats_mode=full[p];[b][p]paletteuse=dither=sierra2_4a", "-loop", "0"],
            _ => ["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart"],
        });
        args.Add(temp);
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new RenderException($"ffmpeg ({ffmpeg}) didn't start.");
        var said = new StringBuilder();
        var reading = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                lock (said)
                    if (said.Length < 8000) said.AppendLine(line);
        }, CancellationToken.None);
        _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        byte[]? middle = null;
        var input = process.StandardInput.BaseStream;
        var clock = Stopwatch.StartNew();
        try
        {
            for (var i = 0; i < frames; i++)
            {
                await browser.EvaluateAsync($"window.__scene.seek({N(i / (double)fps)})", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                var png = await ShotAsync(browser, ct).ConfigureAwait(false);
                if (i == frames / 2)
                    middle = png;
                try
                {
                    await input.WriteAsync(png, ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    break; // ffmpeg stopped: what it said is read below
                }
                if (i % 5 == 0 || i == frames - 1)
                    progress?.Invoke($"Drawing frame {i + 1} of {frames}" + (i > 4 ? $" В· {Remaining(clock.Elapsed, i + 1, frames)} left" : ""));
            }
            try { input.Close(); }
            catch (IOException) { }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            TryDelete(temp);
            throw;
        }
        try { await reading.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException) { }
        if (process.ExitCode != 0 || !File.Exists(temp))
        {
            TryDelete(temp);
            string tail;
            lock (said) tail = said.ToString().Trim().Clip(1200);
            throw new RenderException($"ffmpeg couldn't make the clip (exit code {process.ExitCode}): {tail}");
        }
        Media.MediaRelease.Release(output); // the clip made before may be playing in the chat
        File.Move(temp, output, overwrite: true);
        return middle;
    }

    private static string Remaining(TimeSpan took, int done, int all)
    {
        var left = TimeSpan.FromTicks(took.Ticks / done * (all - done));
        return left.TotalMinutes >= 1 ? $"{(int)left.TotalMinutes}:{left.Seconds:00}" : $"{left.Seconds} s";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A file in <paramref name="folder"/> that isn't there yet: "name.png", else "name 2.png", "name 3.png".</summary>
    internal static string FreeName(string folder, string name, string extension)
    {
        folder = Path.GetFullPath(folder);
        var file = Path.Combine(folder, name + extension);
        for (var n = 2; File.Exists(file) || Directory.Exists(file); n++)
            file = Path.Combine(folder, $"{name} {n}{extension}");
        return file;
    }

    /// <summary>The scene's own text, to keep in the picture drawn from it; empty when it can't be read.</summary>
    private static string ReadScene(string scene)
    {
        try
        {
            return new FileInfo(scene).Length <= SceneStamp.MaxText * 2 ? File.ReadAllText(scene) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Writes the screenshot as the output's kind of picture.</summary>
    private static void SavePicture(byte[] png, string output, SceneSource scene)
    {
        var format = Path.GetExtension(output).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png,
        };
        Media.MediaRelease.Release(output);
        if (format == SKEncodedImageFormat.Png)
        {
            // What it was drawn from goes in the file, as ComfyUI's pictures keep their workflow.
            File.WriteAllBytes(output, SceneStamp.Stamp(png, scene));
            return;
        }
        using var bitmap = SKBitmap.Decode(png) ?? throw new RenderException("The browser's picture of the page couldn't be read.");
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 92);
        using var file = File.Create(output);
        data.SaveTo(file);
    }

    /// <summary>The colour every pixel of the picture is ("#FFFFFF", "clear"), or null when it has more than one.</summary>
    internal static string? Blank(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null)
            return null;
        var first = bitmap.GetPixel(0, 0);
        var stepX = Math.Max(1, bitmap.Width / 97);
        var stepY = Math.Max(1, bitmap.Height / 89);
        for (var y = 0; y < bitmap.Height; y += stepY)
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                var c = bitmap.GetPixel(x, y);
                if (Math.Abs(c.Red - first.Red) > 3 || Math.Abs(c.Green - first.Green) > 3 || Math.Abs(c.Blue - first.Blue) > 3 || Math.Abs(c.Alpha - first.Alpha) > 3)
                    return null;
            }
        return first.Alpha == 0 ? "clear" : $"#{first.Red:X2}{first.Green:X2}{first.Blue:X2}";
    }

    /// <summary>
    /// Answers a request the page made: its own files from its folder (an HTML page given an import map
    /// for three.js when it imports it without one), three.js from the CDN or, failing that, Sloperator's
    /// copy (<paramref name="offline"/> is called then), and the rest from the web.
    /// </summary>
    private static async Task AnswerAsync(DevToolsBrowser browser, JsonObject p, string scene, string folder, Action<string> error, Action offline)
    {
        var id = (string?)p["requestId"];
        var url = (string?)p["request"]?["url"] ?? "";
        if (id is null)
            return;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
            {
                var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                // A picture, font, clip or model the scene names by its full path on this PC (see Localized).
                if (relative.StartsWith(Elsewhere + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    var named = relative[(Elsewhere.Length + 1)..];
                    if (!Path.IsPathRooted(named) || !MediaKinds.Contains(Path.GetExtension(named).ToLowerInvariant()) || !File.Exists(named))
                    {
                        error(File.Exists(named) ? $"The page asked for {named}: only a picture, a font, a clip, a sound or a 3D model is read from outside the scene's folder."
                                                 : $"The page asked for {named}, which isn't there.");
                        await FulfillAsync(browser, id, 404, "text/plain", []).ConfigureAwait(false);
                        return;
                    }
                    await FulfillAsync(browser, id, 200, ContentType(named), await File.ReadAllBytesAsync(named).ConfigureAwait(false)).ConfigureAwait(false);
                    return;
                }
                var file = Path.GetFullPath(Path.Combine(folder, relative));
                var inside = file.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (relative.Length == 0 || (relative.Equals("favicon.ico", StringComparison.OrdinalIgnoreCase) && !File.Exists(file)))
                {
                    await FulfillAsync(browser, id, 200, "image/x-icon", []).ConfigureAwait(false);
                    return;
                }
                if (!inside || !File.Exists(file))
                {
                    error($"The page asked for {Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/')}, which isn't in {folder}.");
                    await FulfillAsync(browser, id, 404, "text/plain", []).ConfigureAwait(false);
                    return;
                }
                var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                if (string.Equals(file, scene, StringComparison.OrdinalIgnoreCase) && Path.GetExtension(file).ToLowerInvariant() is ".html" or ".htm")
                    bytes = Encoding.UTF8.GetBytes(WithImportMap(Encoding.UTF8.GetString(bytes)));
                if (Path.GetExtension(file).ToLowerInvariant() is ".svg" or ".html" or ".htm" or ".css" or ".js" or ".mjs")
                    bytes = Encoding.UTF8.GetBytes(Localized(Encoding.UTF8.GetString(bytes)));
                await FulfillAsync(browser, id, 200, ContentType(file), bytes).ConfigureAwait(false);
                return;
            }
            // three.js comes from the CDN the page names, at the version it names; when that can't be
            // had (offline, a version that isn't there), from Sloperator's own copy: the request is held
            // again with the CDN's answer (the response stage), and an error or a refusal is replaced.
            var answered = p["responseStatusCode"] is not null || p["responseErrorReason"] is not null;
            if (answered)
            {
                var status = (int?)p["responseStatusCode"] ?? 0;
                if ((p["responseErrorReason"] is not null || status >= 400) && ThreeFiles.For(url) is { } copy)
                {
                    offline();
                    await FulfillAsync(browser, id, 200, ContentType(url.Split('?')[0]), copy).ConfigureAwait(false);
                }
                else
                    await browser.SendAsync("Fetch.continueResponse", new JsonObject { ["requestId"] = id }).ConfigureAwait(false);
                return;
            }
            var asksForThree = ThreeFiles.For(url) is not null;
            await browser.SendAsync("Fetch.continueRequest", asksForThree
                ? new JsonObject { ["requestId"] = id, ["interceptResponse"] = true }
                : new JsonObject { ["requestId"] = id }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RenderException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            try { await browser.SendAsync("Fetch.failRequest", new JsonObject { ["requestId"] = id, ["errorReason"] = "Failed" }).ConfigureAwait(false); }
            catch (RenderException) { }
        }
    }

    private static Task FulfillAsync(DevToolsBrowser browser, string id, int status, string type, byte[] body) =>
        browser.SendAsync("Fetch.fulfillRequest", new JsonObject
        {
            ["requestId"] = id,
            ["responseCode"] = status,
            ["responseHeaders"] = new JsonArray(
                new JsonObject { ["name"] = "Content-Type", ["value"] = type },
                new JsonObject { ["name"] = "Access-Control-Allow-Origin", ["value"] = "*" },
                new JsonObject { ["name"] = "Cache-Control", ["value"] = "no-store" }),
            ["body"] = Convert.ToBase64String(body),
        });

    /// <summary>The path on the page's own host under which a file named by its full path on this PC is served.</summary>
    private const string Elsewhere = "__file";

    /// <summary>What a scene may take from outside its own folder: what a picture is made of, not documents or code.</summary>
    private static readonly HashSet<string> MediaKinds =
        [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".svg", ".avif", ".mp4", ".webm", ".mp3", ".wav", ".ogg", ".woff", ".woff2", ".ttf", ".otf", ".glb", ".gltf", ".hdr", ".exr"];

    /// <summary>
    /// A scene's text with the files it names by their full path (<c>file:///C:/…/photo.png</c>, or <c>href="C:\…\photo.png"</c>)
    /// named on the page's own host instead, where <see cref="AnswerAsync"/> reads them from the disk. The page is served
    /// over https, and a browser lets such a page load nothing from <c>file:</c>: a caption over the user's photo came
    /// out as the caption alone, until the model copied the photo beside the scene and rendered again.
    /// </summary>
    internal static string Localized(string text)
    {
        if (!text.Contains(":/", StringComparison.Ordinal) && !text.Contains(":\\", StringComparison.Ordinal))
            return text;
        static string Served(string path) =>
            $"https://{Host}/{Elsewhere}/" + string.Join('/', Uri.UnescapeDataString(path).Replace('\\', '/').Split('/').Select(Uri.EscapeDataString)).Replace("%3A", ":");
        text = FileUrl().Replace(text, m => Served(m.Groups[1].Value));
        return DrivePath().Replace(text, m => m.Groups[1].Value + Served(m.Groups[2].Value));
    }

    [GeneratedRegex(@"file:/{2,3}([A-Za-z]:/[^""'<>)\s]+)")]
    private static partial Regex FileUrl();

    [GeneratedRegex(@"((?:href|src|url)\s*[=(]\s*[""']?)([A-Za-z]:[\\/][^""'<>)]+)")]
    private static partial Regex DrivePath();

    /// <summary>The page with an import map for three.js put first in it, when it imports "three" and has no import map.</summary>
    internal static string WithImportMap(string html)
    {
        if (ImportMapTag().IsMatch(html) || !ImportsThree().IsMatch(html))
            return html;
        if (HeadTag().Match(html) is { Success: true } head)
            return html.Insert(head.Index + head.Length, ThreeFiles.ImportMap);
        return ThreeFiles.ImportMap + html;
    }

    [GeneratedRegex(@"<script[^>]*type\s*=\s*[""']?importmap", RegexOptions.IgnoreCase)]
    private static partial Regex ImportMapTag();

    [GeneratedRegex(@"(?:from\s*|import\s*\(\s*|import\s+)[""']three(?:/[^""']*)?[""']")]
    private static partial Regex ImportsThree();

    [GeneratedRegex(@"<head[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadTag();

    internal static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".glb" => "model/gltf-binary",
        ".gltf" => "model/gltf+json",
        ".wasm" => "application/wasm",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;



    private static string N(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
}
