using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sloperator.Core.Web;

/// <summary>
/// A web page or web game shown in the Live preview panel, beside the chat. The page is served from
/// its folder under a made-up https address (WebView2's folder mapping), not as a file:// one, so what a
/// game does as on a web server works: ES modules, fetch of its own files. The page's script errors come
/// back to the panel through a script run before the page's own (<see cref="ConsoleScript"/>).
/// </summary>
public static partial class LivePage
{
    /// <summary>The host the page's folder is served under; .example is reserved, so it is never a real site.</summary>
    public const string Host = "page.sloperator.example";

    /// <summary>A file the panel opens: a web page.</summary>
    public static bool IsPage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm";

    /// <summary>
    /// The folder served for <paramref name="page"/>: the project it is in (<see cref="Projects.ProjectRoot"/>),
    /// so a page in src\ still reaches ../assets; else the page's own folder. Its changes reload the page.
    /// </summary>
    public static string Folder(string page)
    {
        var own = Path.GetDirectoryName(Path.GetFullPath(page))!;
        return Projects.ProjectRoot.Find(own)?.Root is { } root && IsIn(own, root) ? Path.TrimEndingDirectorySeparator(root) : own;
    }

    /// <summary>The address <paramref name="page"/> is shown at, <paramref name="folder"/> served at <see cref="Host"/>.</summary>
    public static Uri Address(string page, string folder)
    {
        var relative = Path.GetRelativePath(folder, Path.GetFullPath(page)).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new Uri($"https://{Host}/" + string.Join('/', relative.Select(Uri.EscapeDataString)));
    }

    /// <summary>The file at <paramref name="address"/> in <paramref name="folder"/> (a link the user followed in the page); null for another site.</summary>
    public static string? PathOf(Uri address, string folder)
    {
        if (address.IsFile)
            return address.LocalPath;
        if (!string.Equals(address.Host, Host, StringComparison.OrdinalIgnoreCase))
            return null;
        var relative = Uri.UnescapeDataString(address.AbsolutePath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(folder, relative));
        return IsIn(path, folder) || string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    /// <summary>
    /// A file changed on disk in the page's folder that reloads the page: any but those in a folder no page
    /// is made from (.git, node_modules, as the folder index leaves out), which a commit or an install fills.
    /// </summary>
    public static bool Reloads(string changed, string folder) =>
        !Path.GetRelativePath(folder, changed).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).SkipLast(1).Any(Projects.FolderWalk.AlwaysLeftOut.Contains);

    public static bool IsIn(string path, string folder) =>
        path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Run in every document before its own scripts: uncaught errors, failed promise rejections, resources
    /// that failed to load and console.error, each posted to the panel as JSON. The page's console still gets them.
    /// </summary>
    public const string ConsoleScript = """
        (() => {
          if (window.__sloperatorConsole || !window.chrome || !window.chrome.webview) return;
          window.__sloperatorConsole = true;
          const post = m => { try { window.chrome.webview.postMessage(JSON.stringify(m)); } catch (_) { } };
          const text = v => {
            try {
              if (v instanceof Error) return v.stack && v.stack.indexOf(v.message) >= 0 ? v.stack : v.name + ': ' + v.message;
              if (typeof v === 'object' && v !== null) return JSON.stringify(v);
            } catch (_) { }
            return String(v);
          };
          window.addEventListener('error', e => {
            if (e instanceof ErrorEvent)
              post({ text: e.error ? text(e.error) : e.message, source: e.filename, line: e.lineno, column: e.colno });
            else if (e.target && e.target !== window)
              post({ text: 'Failed to load ' + (e.target.src || e.target.href || e.target.tagName) });
          }, true);
          window.addEventListener('unhandledrejection', e => post({ text: 'Unhandled promise rejection: ' + text(e.reason) }));
          const error = console.error;
          console.error = function (...args) {
            post({ text: args.map(text).join(' ') });
            return error.apply(this, args);
          };
        })();
        """;

    /// <summary>A message <see cref="ConsoleScript"/> posted: the error; null for anything else.</summary>
    public static ConsoleError? Parse(string? body, string? folder)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            // Posted as a string: one way of reading it hands the string over as JSON, in quotes.
            if (body.TrimStart().StartsWith('"') && JsonNode.Parse(body) is JsonValue quoted && quoted.TryGetValue<string>(out var inner))
                body = inner;
            if (JsonNode.Parse(body) is not JsonObject o || o["text"]?.GetValue<string>() is not { Length: > 0 } text)
                return null;
            var source = o["source"]?.GetValue<string>() is { Length: > 0 } s ? Source(s, folder) : null;
            // The page's own addresses in what it says (a stack, a file that failed to load) as their paths in the folder.
            if (folder is not null)
                text = OwnAddress().Replace(text, m => Source(m.Value, folder));
            return new ConsoleError(text.TrimEnd(), source, Number(o["line"]), Number(o["column"]));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        static int Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : 0;
    }

    [System.Text.RegularExpressions.GeneratedRegex("""https://page\.sloperator\.example/[^\s'")]*""", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex OwnAddress();

    /// <summary>A script's address as the panel shows it: its path in the folder served ("src/game.js"), else the address.</summary>
    private static string Source(string address, string? folder)
    {
        if (folder is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri) && PathOf(uri, folder) is { } path)
            return Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');
        return address;
    }
}

/// <summary>
/// An error the page's script hit: what it says (a stack, for an Error thrown), and where it was thrown
/// when the browser says (a script's path in the folder served, line and column from 1; 0 when not known).
/// </summary>
public sealed record ConsoleError(string Text, string? Source, int Line, int Column)
{
    /// <summary>The first line of what it says, as the panel's list shows it.</summary>
    public string Summary => Text.Split('\n', 2)[0].TrimEnd('\r');

    /// <summary>"src/game.js:12:5", "src/game.js"; empty when not known.</summary>
    public string Where => Source is null ? "" : Line > 0 ? Column > 0 ? $"{Source}:{Line}:{Column}" : $"{Source}:{Line}" : Source;

    /// <summary>As the model is told it: what it says, then where (unless its stack says it already).</summary>
    public string Told => Where.Length == 0 || Text.Contains(Where, StringComparison.Ordinal) ? Text : $"{Text}\n    at {Where}";
}
