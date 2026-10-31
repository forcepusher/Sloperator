namespace Sloperator.App.ViewModels;

/// <summary>A folder a side panel watches, and the folders under it: the Code editor's tree, the Web preview's page, the Image editor's pictures.</summary>
internal static class FolderWatch
{
    /// <summary>
    /// A watcher of <paramref name="folder"/> for any file or folder added, deleted, renamed or written:
    /// <paramref name="changed"/> is called on the watcher's thread for each (a rename too), and
    /// <paramref name="overflowed"/> when more came at once than its buffer holds. Null when the folder
    /// can't be watched (gone, no access).
    /// </summary>
    public static FileSystemWatcher? Start(string folder, FileSystemEventHandler changed, Action overflowed)
    {
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Changed += changed;
            watcher.Renamed += (sender, e) => changed(sender, e);
            watcher.Error += (_, _) => overflowed();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
