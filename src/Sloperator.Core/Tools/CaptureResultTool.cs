using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;
using SkiaSharp;

namespace Sloperator.Core.Tools;

/// <summary>
/// A picture of what the agent made, for the user: a window, a box on the screen, or the whole
/// screen. In test chats no model ever showed its finished work in its final reply: there was no
/// tool for it, and the screen an action left is folded on its card. The user gets it as a PNG at
/// the screen's own resolution, kept with the chat; the model sees the same crop, to check it.
/// With earlier captures or picture files in <c>compare</c>, the user gets them all in one, labelled.
/// </summary>
public sealed partial class CaptureResultTool : Tool
{
    /// <summary>How many windows an error lists by name, the frontmost first.</summary>
    private const int ListedWindows = 8;

    public override string Name => "capture_result";
    public override string Description =>
        "Show the user your work as a picture under your final reply, which you see too: " +
        "a window, part of one (window and a box), a box on the screen, or a picture file that is the result (an edit, an upscale) as file. " +
        "When the task changes how something looks (a button from red to green, a layout, a chart), the user wants to see that change: " +
        "capture how it looks before your first change (open or run it first if it isn't showing), " +
        "then capture the result with the before's number in compare and no window or box: the same part of the same window is taken again, " +
        "wherever the window is, and the user gets the two side by side. " +
        "Crop to the work: its window, with a box around the button, the cells or the dialog when only they changed; leave window and box out only when the whole screen is the point. " +
        "Pictures you only check (a video's frames, a step on the way) aren't for the user: look at them with read_file.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("window", Str("Part of the title of the window to capture, e.g. \"Paint\" or \"Budget.xlsx\"; with a box, only the part of it in the box"), false),
        ("x", Num("Left edge of the box to capture, in screenshot coordinates"), false),
        ("y", Num("Top edge of the box, in screenshot coordinates"), false),
        ("width", Num("The box's width, in screenshot coordinates"), false),
        ("height", Num("The box's height, in screenshot coordinates"), false),
        ("caption", Str("One line for the user, shown under the picture"), false),
        ("file", Str("A picture file to show as the result instead of the screen"), false),
        ("compare", StrArray("Earlier captures' numbers (each capture says its number) or picture files' paths, shown before this one in this order. " +
                             "With no window, box or file, this capture takes what the first of them took: the same part of the same window, wherever it is now"), false),
        ("labels", StrArray("Short labels, one per picture with this one last, in the user's language, e.g. [\"Before\", \"After\"] or [\"Light\", \"Dark\"]. Default: Before and After for two, numbers for more"), false),
        ("columns", Int("How many compared pictures go in a row: 1 stacks them, their number puts them all side by side. Default: the layout nearest 4:3"), false));

    public override string Describe(JsonObject args)
    {
        var box = string.Create(CultureInfo.InvariantCulture, $"({GetNumber(args, "x")}, {GetNumber(args, "y")}), {GetNumber(args, "width")}x{GetNumber(args, "height")}");
        var compare = Items(args, "compare");
        var what = GetString(args, "file") is { Length: > 0 } file ? $"Show the result: {Path.GetFileName(file.Trim())}"
            : GetString(args, "window") is { Length: > 0 } window ? $"Capture the result: {window.Trim()}" + (HasBox(args) ? $", at {box} in it" : "")
            : HasBox(args) ? $"Capture the result at {box}"
            : compare.Count > 0 && CaptureNumber(compare[0]) is { } before ? $"Capture the result like capture {before}" + (compare.Count == 1 ? ", beside it" : "")
            : "Capture the result: the whole screen";
        return compare.Count == 0 || what.EndsWith("beside it") ? what
            : what + ", beside " + string.Join(", ", compare.Select(c => CaptureNumber(c) is { } n ? $"capture {n}" : Path.GetFileName(c)));
    }

    private static bool HasBox(JsonObject args) => new[] { "x", "y", "width", "height" }.Any(k => args[k] is not null);

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var window = GetString(args, "window")?.Trim();
        var picture = GetString(args, "file")?.Trim() is { Length: > 0 } given ? given : null;
        if (picture is not null && (window is { Length: > 0 } || HasBox(args)))
            throw new ToolArgumentException("Give one of file, window or a box (x, y, width, height), not more; window and a box together take the part of the window in the box.");
        var folder = ctx.ChatDirectory ?? Path.Combine(ctx.ScratchDirectory ?? Path.GetTempPath(), "captures");
        // A picture file the model made goes in as it is, at its own size; the screen is captured.
        var shownFile = picture is null ? null : PicturePath(picture, ctx);
        var compare = Items(args, "compare");
        // The after of a before: with no other place named, what the first capture compared took, found
        // again where it is now (the app may open elsewhere on its second run).
        var same = shownFile is null && !HasBox(args) && compare.Count > 0 && CaptureNumber(compare[0]) is { } before
            && ReadView(folder, before) is { } taken && (window is not { Length: > 0 } || string.Equals(window, taken.Window, StringComparison.OrdinalIgnoreCase))
            ? (before, taken) : ((int Number, View View)?)null;
        var (region, screen, what, view) = shownFile is not null ? (default, ctx.CurrentScreen, $"the picture {Path.GetFileName(shownFile)}", null)
            : same is { } sameAs ? SameRegion(ctx, sameAs.View, sameAs.Number)
            : window is { Length: > 0 } ? WindowRegion(ctx, window, HasBox(args) ? BoxRegion(ctx, args).Region : null)
            : HasBox(args) ? BoxRegion(ctx, args)
            : (ctx.GetScreenSpace().Screen, ctx.CurrentScreen, "the screen", new View(null, ctx.CurrentScreen, null));
        // Read before anything is captured: an earlier picture that isn't there is the model's to fix first.
        var earlier = compare.Select(c => Earlier(c, folder, ctx)).ToList();
        var labels = Labels(args, earlier.Count);
        var columns = GetNumber(args, "columns") is { } wanted
            ? wanted < 1 ? throw new ToolArgumentException("columns is how many pictures go in a row: 1 or more.") : (int)Math.Min(wanted, earlier.Count + 1)
            : (int?)null;

        // Fresh, without the pointer: the click marker and the log are never in a capture.
        var frame = shownFile is not null ? Read(shownFile) : ctx.Platform.Screen.Capture(region, includeCursor: false);
        var took = shownFile is not null ? "Took" : "Captured";
        using var image = Image(frame, out var handle);
        try
        {
            var (file, number) = SaveNext(image, folder);
            if (view is not null)
                WriteView(folder, number, view);
            var caption = GetString(args, "caption")?.Trim();
            var captioned = caption is { Length: > 0 } ? $", captioned \"{caption}\"" : "";
            if (earlier.Count == 0)
            {
                var again = view switch
                {
                    null => "",
                    { Window: not null, Part: not null } => "; with no window or box, the same part of the same window is taken again, wherever the window is then",
                    { Window: not null } => "; with no window or box, the same window is taken again, wherever it is then",
                    _ => "; with no window or box, the same is taken again",
                };
                var text = $"{took} {what} as capture {number}, {frame.Width}x{frame.Height}. " +
                           $"The user sees it at full size in the chat, the last you capture under your final reply{captioned}. " +
                           $"If it doesn't show what they asked for, fix that and capture it again. Taken before a change, pass compare=[{number}] " +
                           $"when you capture the result, to show them side by side{again}.";
                return Task.FromResult(new ToolResult(text, ForModel(image, ctx.GetScreenSpace(screen)), ImageCaption: "What the user now sees of your work:", Files: [file]));
            }
            using var all = CaptureCompare.Compose([.. earlier.Select((e, i) => (e.Picture, labels[i])), (frame, labels[^1])], columns);
            var compared = Path.Combine(folder, $"capture-{number}-compared.png");
            using (var png = all.Encode(SKEncodedImageFormat.Png, 100))
            using (var stream = File.Create(compared))
                png.SaveTo(stream);
            var shown = $"{took} {what} as capture {number}, {frame.Width}x{frame.Height}, and put it after {string.Join(", ", earlier.Select(e => e.Name))}: " +
                        $"the user sees them in one picture, labelled {string.Join(", ", labels.Select(l => $"\"{l}\""))}, the last you capture under your final reply{captioned}. " +
                        "If the change doesn't show, fix that and capture it again.";
            return Task.FromResult(new ToolResult(shown, ForModel(all, ctx.GetScreenSpace(screen)), ImageCaption: "What the user now sees of your work, compared:", Files: [compared]));
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// What a screen capture took, kept beside it (capture-N.json) for the after of a before: the
    /// window as the model named it and the part of it (from the window's corner, in screen pixels),
    /// or a box on the screen; no part for the whole window or screen.
    /// </summary>
    private sealed record View(string? Window, int Screen, ScreenRect? Part);

    /// <summary>
    /// The window <paramref name="title"/> names, cut to the screen it is mostly on, or the part of it
    /// in <paramref name="box"/> (screen pixels); an argument error for a box that misses it.
    /// </summary>
    private static (ScreenRect Region, int Screen, string What, View? View) WindowRegion(ToolContext ctx, string title, ScreenRect? box)
    {
        var (found, best) = FindWindow(ctx, title);
        if (box is not { } b)
            return (Intersect(found.Bounds, ctx.Screens[best]), best, $"the window \"{found.Title}\"", new View(title, best, null));
        var part = Intersect(b, found.Bounds);
        if (part.Width == 0 || part.Height == 0)
            throw new ToolArgumentException($"The box isn't on the window \"{found.Title}\": give one inside it, in screenshot coordinates.");
        return (part, ctx.CurrentScreen, $"part of the window \"{found.Title}\"",
            new View(title, ctx.CurrentScreen, part with { X = part.X - found.Bounds.X, Y = part.Y - found.Bounds.Y }));
    }

    /// <summary>What capture <paramref name="number"/> took, again: its window found where it is now, and the same part of it.</summary>
    private static (ScreenRect Region, int Screen, string What, View? View) SameRegion(ToolContext ctx, View view, int number)
    {
        if (view.Window is not { } title)
        {
            var screen = view.Screen < ctx.Screens.Count ? view.Screen : ctx.CurrentScreen;
            return (view.Part ?? ctx.Screens[screen], screen, view.Part is null ? "the screen" : $"the box capture {number} took", view);
        }
        var (found, best) = FindWindow(ctx, title);
        if (view.Part is not { } p)
            return (Intersect(found.Bounds, ctx.Screens[best]), best, $"the window \"{found.Title}\"", view);
        var part = Intersect(Intersect(new ScreenRect(found.Bounds.X + p.X, found.Bounds.Y + p.Y, p.Width, p.Height), found.Bounds), ctx.Screens[best]);
        if (part.Width == 0 || part.Height == 0)
            throw new ToolArgumentException($"The window \"{found.Title}\" no longer has the part capture {number} took ({p.Width}x{p.Height} at {p.X}, {p.Y} from its corner; " +
                                            $"it is {found.Bounds.Width}x{found.Bounds.Height} now): give the window and a box.");
        return (part, best, $"the same part of the window \"{found.Title}\"", view);
    }

    /// <summary>The frontmost window whose title is <paramref name="title"/> or has it in it, and the screen it is mostly on.</summary>
    private static (ScreenWindow Window, int Screen) FindWindow(ToolContext ctx, string title)
    {
        var windows = ctx.Platform.Screen.GetWindows();
        var found = windows.FirstOrDefault(w => w.Title.Trim().Equals(title, StringComparison.OrdinalIgnoreCase)) is { Title.Length: > 0 } exact ? exact
            : windows.FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (found.Title is null)
            throw new ToolArgumentException(windows.Count == 0
                ? $"No window's title has \"{title}\" in it: no windows of other programs are open on the screen."
                : $"No window's title has \"{title}\" in it. Windows on the screen: " +
                  string.Join(", ", windows.Take(ListedWindows).Select(w => $"\"{w.Title}\"")) + (windows.Count > ListedWindows ? "…" : "") + ".");
        var screens = ctx.Screens;
        var (best, area) = (-1, 0L);
        for (var i = 0; i < screens.Count; i++)
            if (Intersect(found.Bounds, screens[i]) is var part && (long)part.Width * part.Height > area)
                (best, area) = (i, (long)part.Width * part.Height);
        if (best < 0)
            throw new ToolArgumentException($"The window \"{found.Title}\" is off the screen: move it onto the screen first.");
        return (found, best);
    }

    /// <summary>The box the model gave, in screenshot coordinates of the screen it looks at, cut to that screen.</summary>
    private static (ScreenRect Region, int Screen, string What, View? View) BoxRegion(ToolContext ctx, JsonObject args)
    {
        var (x, y, width, height) = (GetNumber(args, "x"), GetNumber(args, "y"), GetNumber(args, "width"), GetNumber(args, "height"));
        if (x is null || y is null || width is null || height is null)
            throw new ToolArgumentException("A box needs all of x, y (its top-left corner), width and height, in screenshot coordinates.");
        if (width <= 0 || height <= 0)
            throw new ToolArgumentException("The box's width and height must be more than 0.");
        var space = ctx.GetScreenSpace();
        var region = space.ToScreen(x.Value, y.Value, width.Value, height.Value);
        if (region.Width == 0 || region.Height == 0)
            throw new ToolArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"The box ({x}, {y}), {width}x{height} is off the screen, which is {space.ImageWidth}x{space.ImageHeight} in screenshot coordinates."));
        return (region, ctx.CurrentScreen, string.Create(CultureInfo.InvariantCulture, $"the box at ({x}, {y}), {width}x{height}"), new View(null, ctx.CurrentScreen, region));
    }

    private static string ViewPath(string folder, int number) => Path.Combine(folder, $"capture-{number}.json");

    private static void WriteView(string folder, int number, View view)
    {
        var json = new JsonObject { ["window"] = view.Window, ["screen"] = view.Screen };
        if (view.Part is { } p)
        {
            json["x"] = p.X;
            json["y"] = p.Y;
            json["width"] = p.Width;
            json["height"] = p.Height;
        }
        File.WriteAllText(ViewPath(folder, number), json.ToJsonString());
    }

    /// <summary>What capture <paramref name="number"/> took; null for a picture file, or a capture from before views were kept.</summary>
    private static View? ReadView(string folder, int number)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(ViewPath(folder, number))) is not JsonObject json)
                return null;
            var part = json["width"] is null ? (ScreenRect?)null
                : new ScreenRect((int)json["x"]!, (int)json["y"]!, (int)json["width"]!, (int)json["height"]!);
            return new View((string?)json["window"], (int?)json["screen"] ?? 0, part);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static ScreenRect Intersect(ScreenRect a, ScreenRect b)
    {
        var (left, top) = (Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        var (right, bottom) = (Math.Min(a.X + a.Width, b.X + b.Width), Math.Min(a.Y + a.Height, b.Y + b.Height));
        return new ScreenRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    // ---- earlier pictures -------------------------------------------------------------------------------

    /// <summary>An earlier picture to compare with, and what the model calls it ("capture 2", a file's name).</summary>
    private sealed record Shown(string Name, RawImage Picture);

    /// <summary>"2", "capture 2", "capture-2.png", "#2": the number of an earlier capture; null for anything else (a path).</summary>
    private static int? CaptureNumber(string given) =>
        CaptureName().Match(given) is { Success: true } m && int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var n) ? n : null;

    [GeneratedRegex(@"^(?:capture[\s_-]*)?#?(\d+)(?:\.png)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CaptureName();

    /// <summary>
    /// A list argument as strings: an array of strings or numbers, one value, or an array a model
    /// wrote as a string ("[1, 2]"). Blank items are left out.
    /// </summary>
    private static List<string> Items(JsonObject args, string key)
    {
        var node = args[key];
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && s.TrimStart().StartsWith('['))
        {
            try { node = JsonNode.Parse(s); }
            catch (System.Text.Json.JsonException) { }
        }
        IEnumerable<JsonNode?> items = node switch { JsonArray a => a, null => Array.Empty<JsonNode?>(), _ => new[] { node } };
        return items.Select(n => n is JsonValue value && value.TryGetValue<string>(out var text) ? text : n?.ToJsonString())
            .Select(t => t?.Trim()).Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToList();
    }

    /// <summary>The labels the model gave, one per picture with the new one last, or the defaults; an argument error for a count that doesn't match.</summary>
    private static List<string> Labels(JsonObject args, int earlier)
    {
        var given = Items(args, "labels");
        if (given.Count == 0)
            return CaptureCompare.DefaultLabels(earlier + 1);
        if (earlier == 0)
            throw new ToolArgumentException("labels go with compare: pass the earlier pictures in compare, then one label for each and one for this capture.");
        if (given.Count != earlier + 1)
            throw new ToolArgumentException($"Give one label per picture, {earlier + 1} in all with this capture's last (you gave {given.Count}), or leave labels out.");
        return given.Select(CaptureCompare.Tidy).ToList();
    }

    /// <summary>The picture <paramref name="given"/> names: an earlier capture in <paramref name="folder"/>, or a picture file.</summary>
    private static Shown Earlier(string given, string folder, ToolContext ctx)
    {
        if (CaptureNumber(given) is { } number)
        {
            var path = Path.Combine(folder, $"capture-{number}.png");
            if (!File.Exists(path))
            {
                var taken = Captures(folder);
                throw new ToolArgumentException($"There is no capture {number} in this chat. " +
                    (taken.Count == 0 ? "Nothing has been captured yet: capture it before you change it." : $"Its captures: {string.Join(", ", taken)}."));
            }
            return new Shown($"capture {number}", Read(path));
        }
        var file = ctx.ResolvePath(given);
        if (!File.Exists(file))
            throw new ToolArgumentException($"There is no picture at {file}. Pass an earlier capture's number or a picture's path.");
        return new Shown(Path.GetFileName(file), Read(file));
    }

    /// <summary>The full path of a picture file the model gave as the result; an argument error for one that isn't there or isn't a picture.</summary>
    private static string PicturePath(string given, ToolContext ctx)
    {
        var path = ctx.ResolvePath(given);
        if (!File.Exists(path))
            throw new ToolArgumentException($"There is no picture at {path}.");
        if (!Picture.Handles(path))
            throw new ToolArgumentException($"{Path.GetFileName(path)} isn't a picture: give a PNG, JPEG, GIF, BMP, WebP or ICO file.");
        return path;
    }

    /// <summary>
    /// A picture file at its own size, on white where it is transparent (as read_file shows one), as a
    /// capture's pixels; an argument error for one that can't be read.
    /// </summary>
    private static RawImage Read(string path)
    {
        using var decoded = PictureFile.Decode(path) ?? throw new ToolArgumentException($"{path} could not be read as a picture (damaged, or not what its name says).");
        using var surface = SKSurface.Create(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White);
        using (var image = SKImage.FromBitmap(decoded))
            surface.Canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default);
        var pixels = new byte[decoded.Width * decoded.Height * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            surface.ReadPixels(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul), handle.AddrOfPinnedObject(), decoded.Width * 4, 0, 0);
        }
        finally
        {
            handle.Free();
        }
        return new RawImage(decoded.Width, decoded.Height, pixels);
    }

    /// <summary>The numbers of the captures in <paramref name="folder"/>, in order.</summary>
    private static List<int> Captures(string folder) =>
        !Directory.Exists(folder) ? []
        : Directory.EnumerateFiles(folder, "capture-*.png").Select(f => CaptureNumber(Path.GetFileName(f))).OfType<int>().Order().ToList();

    // ---- the pictures --------------------------------------------------------------------------------

    /// <summary>
    /// The picture as a lossless PNG in the chat's folder (capture-1.png, capture-2.png…), its path and number.
    /// Named apart from the conversation's numbered pictures, which ChatStore drops once no message names them.
    /// </summary>
    private static (string Path, int Number) SaveNext(SKImage image, string folder)
    {
        Directory.CreateDirectory(folder);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(folder, $"capture-{n}.png");
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                png.SaveTo(file);
                return (path, n);
            }
            catch (IOException) when (File.Exists(path)) { }
        }
    }

    /// <summary>
    /// The picture as the model gets it: no larger than a screenshot of its screen (so within the
    /// model's limit on picture sizes), never enlarged, JPEG at the zoom's quality.
    /// </summary>
    internal static byte[] ForModel(SKImage image, ScreenSpace space)
    {
        var scale = Math.Min(1.0, Math.Min((double)space.ImageWidth / image.Width, (double)space.ImageHeight / image.Height));
        var (w, h) = (Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.DrawImage(image, new SKRect(0, 0, w, h), Sampling.High(image.Width, w));
        using var snapshot = surface.Snapshot();
        using var jpeg = snapshot.Encode(SKEncodedImageFormat.Jpeg, ZoomTool.Quality);
        return jpeg.ToArray();
    }

    /// <summary>The frame as an image over its own pixels, pinned until <paramref name="handle"/> is freed.</summary>
    private static SKImage Image(RawImage frame, out System.Runtime.InteropServices.GCHandle handle)
    {
        handle = System.Runtime.InteropServices.GCHandle.Alloc(frame.Bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var pixels = new SKPixmap(info, handle.AddrOfPinnedObject(), frame.Stride);
        return SKImage.FromPixels(pixels);
    }
}
