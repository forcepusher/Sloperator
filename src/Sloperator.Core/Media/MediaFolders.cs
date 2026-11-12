namespace Sloperator.Core.Media;

/// <summary>
/// The media of an editor panel in folders (<see cref="PictureFolder"/>, <see cref="SoundFolder"/>), a folder each in
/// a root: its versions are its files of the panel's types, oldest first. One way for both panels: the Audio editor's
/// was a copy of the Image editor's.
/// </summary>
/// <param name="untitled">A new one's folder: "Untitled picture", "Untitled picture 2" when that is taken.</param>
/// <param name="types">The files that are versions, by extension.</param>
public sealed class MediaFolders(string untitled, params string[] types)
{
    /// <summary>A file of the types that are versions.</summary>
    public bool IsVersionFile(string path) => types.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>A folder's versions: its files of the types, oldest first (by when they were written, then by name).</summary>
    public IReadOnlyList<string> Versions(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(IsVersionFile)
                .OrderBy(File.GetLastWriteTimeUtc).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The folders in <paramref name="root"/>, the one changed last first.</summary>
    public IReadOnlyList<string> All(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return [];
            return Directory.EnumerateDirectories(root)
                .OrderByDescending(d => Versions(d) is { Count: > 0 } v ? File.GetLastWriteTimeUtc(v[^1]) : Directory.GetCreationTimeUtc(d))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether <paramref name="folder"/> is still named as <see cref="New"/> named it ("Untitled picture", "Untitled
    /// picture 2"): one the panel calls by what it shows instead, once it has a version that says.
    /// </summary>
    public bool IsUntitled(string folder)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (!name.StartsWith(untitled, StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = name[untitled.Length..];
        return rest.Length == 0 || rest[0] == ' ' && rest.Length > 1 && rest[1..].All(char.IsAsciiDigit);
    }

    /// <summary>A new folder in <paramref name="root"/>, made.</summary>
    public string New(string root)
    {
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, untitled);
        for (var n = 2; Directory.Exists(folder); n++)
            folder = Path.Combine(root, $"{untitled} {n}");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
