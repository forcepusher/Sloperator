using System.Globalization;

namespace Sloperator.Core.Chats;

/// <summary>
/// Files Sloperator saves for a message in the chat's pasted files (Data\pasted): a pasted picture, an area
/// marked in a side panel, lines picked in the Terminal, named by when they were made.
/// </summary>
public static class PastedFiles
{
    /// <summary>
    /// A new file's path in <paramref name="dir"/> (made if missing): "Area 2026-10-25 14-03-09.png" for
    /// <paramref name="name"/> "Area" and <paramref name="extension"/> ".png", " (2)" added when that is taken.
    /// </summary>
    public static string NewPath(string dir, string name, string extension)
    {
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        var path = Path.Combine(dir, $"{name} {stamp}{extension}");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(dir, $"{name} {stamp} ({n}){extension}");
        return path;
    }

    /// <summary>A file saved for a message that didn't go (unpicked, another picked): deleted, if it can be.</summary>
    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
