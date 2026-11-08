namespace Sloperator.Core.Settings;

/// <summary>
/// Portable layout: everything Sloperator owns lives next to the executable.
/// <code>
/// Sloperator/
///   Sloperator.exe
///   SloperatorData/settings.json
///   SloperatorData/mcp.json  (MCP servers, if any)
///   SloperatorData/chats/    (saved chats, one file each)
///   SloperatorData/browser/  (profile of the headless browser web search falls back on)
///   SloperatorData/webview/  (profile of the Web preview panel's web view)
///   SloperatorData/runtime/  (native libraries unpacked by the single-file build)
///   SloperatorData/known_hosts (host keys of the servers the agent logged in to over SSH)
///   SloperatorData/tools/    (portable programs the agent downloaded for its tasks)
///   SloperatorData/scratch/  (helper files the agent made for its tasks: scripts and the like)
///   SloperatorData/pasted/   (pictures pasted into the chat from the clipboard)
///   SloperatorData/videos/    (one folder per video: its storyboard, frames, shots, song and the video)
///   SloperatorData/pictures/  (one folder per picture of the Image editor panel: its versions)
///   SloperatorData/index/    (the indexes of the folders the agent indexed, a file each)
///   SloperatorData/errors.log (every error Sloperator showed, see <see cref="ErrorLog"/>)
/// </code>
/// </summary>
public sealed class AppPaths
{
    /// <summary>The folder next to the exe that holds all of Sloperator's own files.</summary>
    public const string DataFolderName = "SloperatorData";

    /// <summary>What <see cref="DataFolderName"/> was called up to 1.3.0 (see <see cref="Open"/>).</summary>
    public const string OldDataFolderName = "Data";

    public AppPaths(string root) : this(root, DataFolderName) { }

    private AppPaths(string root, string dataFolder)
    {
        Root = Path.GetFullPath(root);
        DataDir = Path.Combine(Root, dataFolder);
    }

    public string Root { get; }
    public string DataDir { get; }
    public string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>The MCP server list, in the format MCP READMEs show (see <see cref="Mcp.McpConfig"/>).</summary>
    public string McpConfigFile => Path.Combine(DataDir, "mcp.json");

    /// <summary>Profile of the headless browser the web tools fall back on (see <see cref="Web.HeadlessBrowser"/>).</summary>
    public string BrowserProfileDir => Path.Combine(DataDir, "browser");

    /// <summary>Profile of the Web preview panel's web view (WebView2's user data), kept with the rest so the app stays portable.</summary>
    public string WebViewDir => Path.Combine(DataDir, "webview");

    /// <summary>Saved chats, one file each (see <see cref="Chats.ChatStore"/>).</summary>
    public string ChatsDir => Path.Combine(DataDir, "chats");

    /// <summary>Where the single-file build unpacks its native libraries.</summary>
    public string RuntimeDir => Path.Combine(DataDir, "runtime");

    /// <summary>The SSH host keys Sloperator has seen, in OpenSSH's known_hosts format (see <see cref="Ssh.SshHosts"/>).</summary>
    public string KnownHostsFile => Path.Combine(DataDir, "known_hosts");

    /// <summary>When Sloperator last asked GitHub for a new version, so a start within the hour doesn't ask again (see <see cref="Web.SelfUpdate"/>).</summary>
    public string UpdateCheckFile => Path.Combine(DataDir, "update-check.txt");

    /// <summary>The version whose download wasn't kept, signed by another publisher than the running exe, so it isn't downloaded again (see <see cref="Web.SelfUpdate.Verify"/>).</summary>
    public string UpdateRejectedFile => Path.Combine(DataDir, "update-rejected.txt");

    /// <summary>Portable programs the agent downloads to open what nothing on the PC opens (a PDF reader's text extractor, say), kept for later tasks.</summary>
    public string ToolsDir => Path.Combine(DataDir, "tools");

    /// <summary>Helper files the agent makes only to get a task done (scripts, a reminder's script), instead of the user's folders.</summary>
    public string ScratchDir => Path.Combine(DataDir, "scratch");

    /// <summary>
    /// Pictures pasted into the chat (a screenshot from the clipboard), saved as files so the agent
    /// can work on them like any other; files dropped on the window stay where they are.
    /// </summary>
    public string PastedDir => Path.Combine(DataDir, "pasted");

    /// <summary>
    /// The videos planned in the Video editor panel or asked for in the chat, a folder each (see
    /// <see cref="Video.Storyboard"/>): portable, like everything else of Sloperator's (the user's
    /// choice), rather than in the PC's Videos folder.
    /// </summary>
    public string VideosDir => Path.Combine(DataDir, "videos");

    /// <summary>The pictures made in the Image editor panel, a folder each holding its versions (see <see cref="Media.PictureFolder"/>), beside the videos.</summary>
    public string PicturesDir => Path.Combine(DataDir, "pictures");

    /// <summary>The sounds made in the Audio editor panel, a folder each holding its versions (see <see cref="Media.SoundFolder"/>), beside the pictures.</summary>
    public string SoundsDir => Path.Combine(DataDir, "sounds");

    /// <summary>The indexes of the folders the agent indexed, a file each (see <see cref="Projects.FolderIndex"/>).</summary>
    public string IndexDir => Path.Combine(DataDir, "index");

    /// <summary>ComfyUI's address and folder as last used, the log of a server Sloperator started, and the browser profile its page is loaded in (see <see cref="Comfy.ComfyHub"/>).</summary>
    public string ComfyDir => Path.Combine(DataDir, "comfyui");

    public void EnsureCreated() => Directory.CreateDirectory(DataDir);

    /// <summary>
    /// The paths under <paramref name="root"/>, with the Data folder of an earlier version moved to
    /// <see cref="DataFolderName"/> first, so its chats and settings stay. A Data folder with none of
    /// Sloperator's files in it (another program's, beside the exe in Downloads) is left alone. While
    /// the move can't be made (an earlier version still runs from the folder and holds its files),
    /// this start keeps to the old folder, as that version does, and a later start moves it.
    /// </summary>
    public static AppPaths Open(string root)
    {
        root = Path.GetFullPath(root);
        var data = Path.Combine(root, DataFolderName);
        var old = Path.Combine(root, OldDataFolderName);
        if (Directory.Exists(data) || !IsSloperators(old))
            return new AppPaths(root);
        try { Directory.Move(old, data); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // Another start may have moved it meanwhile: whichever folder is there now counts.
        return Directory.Exists(data) ? new AppPaths(root) : new AppPaths(root, OldDataFolderName);
    }

    private static bool IsSloperators(string dir) =>
        Directory.Exists(dir)
        && (File.Exists(Path.Combine(dir, "settings.json"))
            || new[] { "chats", "runtime", "tools", "videos", "pictures" }.Any(d => Directory.Exists(Path.Combine(dir, d))));

    /// <summary>
    /// The portable root: SLOPERATOR_HOME if set, otherwise the folder of the executable. With
    /// <paramref name="useRepositoryDevFolder"/>, a build running inside the repository uses
    /// <c>.dev/</c> there instead, so the debug app and the CLI share one SloperatorData folder.
    /// </summary>
    public static string FindRoot(bool useRepositoryDevFolder)
    {
        if (Environment.GetEnvironmentVariable("SLOPERATOR_HOME") is { Length: > 0 } home)
            return home;
        if (useRepositoryDevFolder)
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (dir.EnumerateFiles("Sloperator.sln*").Any())
                    return Path.Combine(dir.FullName, ".dev");
        return AppContext.BaseDirectory;
    }
}
