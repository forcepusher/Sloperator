namespace Sloperator.Core.Projects;

/// <summary>Where a path is against a folder, as Windows compares paths: case aside.</summary>
public static class FolderPath
{
    /// <summary>Whether <paramref name="path"/> is inside <paramref name="folder"/>, at any depth (the folder itself isn't); both full paths.</summary>
    public static bool IsIn(string path, string folder) =>
        path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
