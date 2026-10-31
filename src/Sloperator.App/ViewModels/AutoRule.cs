using System.Text.Json;
using System.Text.Json.Nodes;
using Sloperator.Core.Documents;
using Sloperator.Core.Video;
using Sloperator.Core.Web;

namespace Sloperator.App.ViewModels;

/// <summary>A panel Auto shows (by its name in the Side panel dropdown), and what in it: a file, a page, a picture's or a video's folder; null for the panel as it opens.</summary>
public sealed record AutoTarget(string Panel, string? Path = null);

/// <summary>A file a write_file or edit_file call changed, as File changes has it, and whether the agent made it in this chat (it wasn't there before).</summary>
public sealed record AutoWritten(string Path, bool Made);

/// <summary>
/// Where the panels Auto shows look: the Image editor's pictures (Data\pictures), the Video editor's videos
/// (Data\videos), the web page the agent wrote last in the chat (null before one), and the folder the Code
/// editor shows (the one the user opened, else the agent's project; null before either).
/// </summary>
public sealed record AutoPlaces(string PicturesDir, string VideosDir, string? LastPage = null, string? EditorFolder = null);

/// <summary>
/// Which panel Auto shows for what the agent does: the panel of its latest tool call that has one,
/// kept through the calls that have none (a screenshot, a click, a file read, a search). One rule for
/// every model and server, from the call alone: its tool, its arguments and what it made.
/// </summary>
public static class AutoRule
{
    public const string FileChanges = "File changes", FileInspector = "File inspector", CodeEditor = "Code editor", ImageEditor = "Image editor",
        VideoEditor = "Video editor", WebPreview = "Web preview", Terminal = "Terminal";

    /// <summary>The panels Auto may show; the room it opens is as narrow and short as the widest and tallest of them needs.</summary>
    public static readonly IReadOnlyList<string> Panels = [FileChanges, FileInspector, CodeEditor, ImageEditor, VideoEditor, WebPreview, Terminal];

    /// <summary>Beside a web page the agent wrote, the files it loads: written, the page reloads in the Web preview, which is where to see them.</summary>
    private static readonly string[] PageFiles = [".css", ".js", ".mjs", ".json", ".svg"];

    /// <summary>
    /// As a call starts, for what shows it as it goes: a command's lines as it prints them (Terminal),
    /// the piece of a video being made (Video editor), a picture of the Image editor being made again.
    /// Null for a call whose panel shows it once it has ended, or that has none.
    /// </summary>
    public static AutoTarget? AtStart(string tool, JsonObject args, AutoPlaces places) => tool switch
    {
        "run_shell" or "ssh" => new(Terminal),
        "video" => new(VideoEditor, VideoFolder(args, places.VideosDir)),
        "comfyui" when Arg(args, "action") == "run" && Arg(args, "save_to") is { } saveTo && PictureOf(saveTo, places.PicturesDir) is { } picture => new(ImageEditor, picture),
        _ => null,
    };

    /// <summary>
    /// Once a call has ended, for what it left: the file a write_file or edit_file call changed (see
    /// <see cref="ForFile"/>), what a ComfyUI run made, the video worked on. Null for a call that failed,
    /// or one with no panel.
    /// </summary>
    /// <param name="written">The file a write_file or edit_file call changed; null when File changes has none (a file on a server, Sloperator's own).</param>
    /// <param name="made">The files the call made or showed (a ComfyUI run's).</param>
    public static AutoTarget? AtEnd(string tool, JsonObject args, bool failed, AutoWritten? written, IReadOnlyList<string>? made, AutoPlaces places)
    {
        if (failed)
            return null;
        switch (tool)
        {
            case "write_file" or "edit_file" when written is not null:
                return ForFile(written, places);
            case "video":
                return new(VideoEditor, VideoFolder(args, places.VideosDir));
            case "comfyui" when made is [var first, ..]:
                return Path.GetDirectoryName(first) is { } folder && PictureOf(folder, places.PicturesDir) is { } picture
                    ? new(ImageEditor, picture)
                    : new(FileInspector, first);
            default:
                return null;
        }
    }

    /// <summary>
    /// The panel for a file the agent changed: a web page, or a file it loads beside the page written
    /// last, in the Web preview (it reloads there); a picture, document, sound or 3D model in the File
    /// inspector, which draws it. Code and text by whose it is: a file the agent made in this chat, in the
    /// Code editor's folder, in the Code editor (there is no "before" to show: the file is the change,
    /// whole, in its project's tree, lit as it is written); a file that was there before, the user's, in
    /// File changes, where what the agent took out shows beside what it put in, with Discard and Stage.
    /// </summary>
    public static AutoTarget ForFile(AutoWritten written, AutoPlaces places)
    {
        var path = Path.GetFullPath(written.Path);
        if (PreviewPage.IsPage(path))
            return new(WebPreview, path);
        if (places.LastPage is { } page && PageFiles.Contains(Path.GetExtension(path).ToLowerInvariant())
            && PreviewPage.IsIn(path, Path.GetDirectoryName(Path.GetFullPath(page))!))
            return new(WebPreview, page);
        if (Core.Documents.FileInspector.KindOf(path) is InspectorKind.Picture or InspectorKind.Animation or InspectorKind.Video or InspectorKind.Sound
            or InspectorKind.Model or InspectorKind.Document)
            return new(FileInspector, path);
        return written.Made && places.EditorFolder is { } folder && PreviewPage.IsIn(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)))
               && Core.Documents.FileInspector.IsText(path)
            ? new(CodeEditor, path)
            : new(FileChanges, path);
    }

    /// <summary>A call's arguments as the model gave them; empty when they don't read as an object.</summary>
    public static JsonObject Args(string arguments)
    {
        try
        {
            return JsonNode.Parse(arguments) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Arg(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim().Trim('"') : null;

    /// <summary>A picture of the Image editor's: a folder right in <paramref name="picturesDir"/>; null for any other.</summary>
    private static string? PictureOf(string folder, string picturesDir)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            return string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(picturesDir)), StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The video a video call is about: its folder, or a video's name in the videos folder, as the tool
    /// reads its path; else (no path, or one not made yet) the video changed last, which is the one the
    /// tool goes on with. Null with no video yet.
    /// </summary>
    private static string? VideoFolder(JsonObject args, string videosDir)
    {
        if (Arg(args, "path") is { } path)
        {
            try
            {
                var full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.Combine(videosDir, path);
                if (File.Exists(full))
                    full = Path.GetDirectoryName(full)!;
                if (Storyboard.IsVideo(full))
                    return full;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return Storyboard.Recent(videosDir).FirstOrDefault();
    }
}
