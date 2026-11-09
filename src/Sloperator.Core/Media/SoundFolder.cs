namespace Sloperator.Core.Media;

/// <summary>
/// The sounds of the Audio editor panel (SloperatorData\sounds, beside the pictures), a folder each: its
/// versions are the sound files in it, oldest first. The agent's comfyui runs copy what they make into
/// the folder (save_to), and every edit (<see cref="SoundEdit"/>) is saved there as a new file, so the
/// earlier versions stay.
/// </summary>
public static class SoundFolder
{
    /// <summary>The sound files the File inspector plays, ComfyUI's MP3, FLAC and Opus among them.</summary>
    private static readonly MediaFolders Folders = new("Untitled sound", ".wav", ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac");

    /// <summary>A file that is a version of a sound.</summary>
    public static bool IsSound(string path) => Folders.IsVersionFile(path);

    /// <summary>A sound folder's versions: its sound files, oldest first (by when they were written, then by name).</summary>
    public static IReadOnlyList<string> Versions(string folder) => Folders.Versions(folder);

    /// <summary>The sounds in <paramref name="root"/>, a folder each, the one changed last first.</summary>
    public static IReadOnlyList<string> All(string root) => Folders.All(root);

    /// <summary>A new sound's folder, made: "Untitled sound", "Untitled sound 2" when that is taken.</summary>
    public static string New(string root) => Folders.New(root);

    /// <summary>Whether <paramref name="file"/> is a version of a sound of <paramref name="root"/>: in a folder right in it.</summary>
    public static bool IsVersion(string file, string? root)
    {
        if (root is null)
            return false;
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(file));
            return folder is not null && string.Equals(Path.GetDirectoryName(folder), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
