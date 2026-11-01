using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Sloperator.Platform.Windows;

/// <summary>
/// The mark Windows puts on a file downloaded from the internet: its Zone.Identifier stream (the
/// "mark of the web"). Windows' previewers don't draw such a file: Excel's failed with E_FAIL on a
/// Google Sheets export in Downloads, and on our own workbook once marked, while a copy without the
/// mark drew (1 November 2026). Windows stopped previewing them because a preview can follow a link
/// in the file and send the user's sign-in hash out with it, so the mark is left on the file.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class DownloadMark
{
    /// <summary>Whether <paramref name="path"/> is marked as from the internet or a restricted site (zone 3 or 4).</summary>
    public static bool FromInternet(string path)
    {
        try
        {
            return ZoneId().Match(File.ReadAllText(path + ":Zone.Identifier")) is { Success: true } zone
                && int.Parse(zone.Groups[1].Value, CultureInfo.InvariantCulture) >= 3;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"(?im)^\s*ZoneId\s*=\s*(\d+)")]
    private static partial Regex ZoneId();

    /// <summary>
    /// A copy of <paramref name="path"/> without the mark, under its own name in a folder of its own in
    /// Windows' temp folder, for the user's "Preview anyway"; <see cref="Delete"/> takes it away again.
    /// </summary>
    public static string UnmarkedCopy(string path)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Sloperator", "preview", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(path));
        // Its bytes only: File.Copy takes the file's streams along, the mark among them.
        using (var from = File.OpenRead(path))
        using (var to = File.Create(copy))
            from.CopyTo(to);
        return copy;
    }

    /// <summary>Deletes a copy <see cref="UnmarkedCopy"/> made, with its folder; one a previewer still holds is left to Windows' temp cleanup.</summary>
    public static void Delete(string copy)
    {
        try { Directory.Delete(Path.GetDirectoryName(copy)!, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
