using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Render;

/// <summary>
/// three.js as Sloperator carries it (three.zip, made by scripts/update-three.ps1): the build and the
/// WebGL add-ons. A scene imports three.js from a CDN as its docs show, at the version it names;
/// when the CDN can't be reached (offline) or hasn't that version, the renderer answers from here, so
/// a scene still draws. A file that isn't here is only had from the web.
/// </summary>
public static partial class ThreeFiles
{
    private static readonly Lazy<(Dictionary<string, byte[]> Files, string Version)> Packed = new(Load);

    /// <summary>The three.js version carried ("0.187.0").</summary>
    public static string Version => Packed.Value.Version;

    /// <summary>The import map a page gets when it imports "three" without one of its own.</summary>
    public static string ImportMap =>
        $$$"""<script type="importmap">{"imports":{"three":"https://cdn.jsdelivr.net/npm/three@{{{Version}}}/build/three.module.js","three/addons/":"https://cdn.jsdelivr.net/npm/three@{{{Version}}}/examples/jsm/"}}</script>""";

    /// <summary>
    /// The file a request for three.js is answered with (by its path in the package: build/three.module.js,
    /// examples/jsm/controls/OrbitControls.js), or null when the URL isn't three.js or the file isn't carried.
    /// </summary>
    public static byte[]? For(string url)
    {
        if (PathIn(url) is not { } path)
            return null;
        var files = Packed.Value.Files;
        if (files.TryGetValue(path, out var bytes))
            return bytes;
        // A minified build named as it was before three.js stopped shipping them.
        var plain = path.Replace(".min.js", ".js", StringComparison.Ordinal);
        return files.GetValueOrDefault(plain);
    }

    /// <summary>The path in three.js's package a URL asks for, or null when it isn't one of three.js's.</summary>
    internal static string? PathIn(string url)
    {
        if (NpmCdn().Match(url) is { Success: true } npm)
            return Clean(npm.Groups["path"].Value);
        if (Cdnjs().Match(url) is { Success: true } cdnjs)
            return "build/" + Clean(cdnjs.Groups["path"].Value);
        if (ThreeJsOrg().Match(url) is { Success: true } site)
            return Clean(site.Groups["path"].Value);
        return null;

        static string Clean(string path)
        {
            var cut = path.IndexOfAny(['?', '#']);
            path = cut >= 0 ? path[..cut] : path;
            // three/addons/ is the docs' name for examples/jsm/.
            return path.StartsWith("addons/", StringComparison.Ordinal) ? "examples/jsm/" + path[7..] : path;
        }
    }

    [GeneratedRegex(@"^https?://(?:cdn\.jsdelivr\.net/npm/|unpkg\.com/|ga\.jspm\.io/npm:|cdn\.skypack\.dev/)three(?:@[^/]*)?/(?<path>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NpmCdn();

    [GeneratedRegex(@"^https?://cdnjs\.cloudflare\.com/ajax/libs/three\.js/[^/]+/(?<path>three\.(?:module|core)(?:\.min)?\.js)$", RegexOptions.IgnoreCase)]
    private static partial Regex Cdnjs();

    [GeneratedRegex(@"^https?://threejs\.org/(?<path>(?:build|examples/jsm)/.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ThreeJsOrg();

    private static (Dictionary<string, byte[]>, string) Load()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var stream = typeof(ThreeFiles).Assembly.GetManifestResourceStream("render:three.zip");
        if (stream is null)
            return (files, "0");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (entry.Length == 0 && entry.FullName.EndsWith('/'))
                continue;
            using var data = entry.Open();
            using var copy = new MemoryStream((int)entry.Length);
            data.CopyTo(copy);
            files[entry.FullName.Replace('\\', '/')] = copy.ToArray();
        }
        var version = files.TryGetValue("VERSION", out var v) ? System.Text.Encoding.UTF8.GetString(v).Trim() : "0";
        return (files, version);
    }
}
