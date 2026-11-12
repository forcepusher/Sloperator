using Microsoft.VisualBasic.FileIO;

namespace Sloperator.App.ViewModels;

/// <summary>
/// An entry of a right-click menu in an editor panel (the Video, Image and Audio editors): what it says, whether it
/// can be done now, and what it does; with <see cref="Items"/>, a submenu. <paramref name="Key"/> is the key that
/// does the same, shown beside it ("F2").
/// </summary>
public sealed record PanelMenuItem(string Header, bool Enabled, Action Do, string? Key = null)
{
    public IReadOnlyList<PanelMenuItem?>? Items { get; init; }
}

/// <summary>Where what the user deletes in a panel goes, as Explorer's Delete puts it: it can be brought back from there.</summary>
internal static class RecycleBin
{
    /// <summary>A folder or a file to the Recycle Bin; one that isn't there is left be.</summary>
    /// <exception cref="IOException">It is open somewhere, or the PC has no Recycle Bin (not Windows).</exception>
    public static void Put(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new IOException("Only Windows has a Recycle Bin to put it in.");
        if (Directory.Exists(path))
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else if (File.Exists(path))
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }
}
