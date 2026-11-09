using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sloperator.App.ViewModels;

/// <summary>
/// A picture or a sound in its editor panel's list (<see cref="PictureItem"/>, <see cref="AudioItem"/>): its folder,
/// its name and its versions. What the two lists share, kept once.
/// </summary>
public abstract partial class MediaFolderItem(string folder) : ObservableObject
{
    public string Folder { get; } = folder;
    public string Name => Path.GetFileName(Folder);

    /// <summary>Its versions, oldest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Count), nameof(IsMade))]
    private IReadOnlyList<string> _versions = [];

    public bool IsMade => Versions.Count > 0;

    /// <summary>"3 versions", "1 version", "not made yet".</summary>
    public string Count => Versions.Count switch { 0 => "not made yet", 1 => "1 version", var n => $"{n} versions" };

    public override string ToString() => Name;

    /// <summary>
    /// <paramref name="items"/> made to match <paramref name="folders"/> (the root read again), in their order:
    /// <paramref name="gone"/> for each that left, and <paramref name="changed"/> for each whose versions
    /// (<paramref name="versionsOf"/>) changed, new ones too, after they are set.
    /// </summary>
    public static void Sync<T>(ObservableCollection<T> items, IReadOnlyList<string> folders, Func<string, T> make,
        Func<string, IReadOnlyList<string>> versionsOf, Action<T> gone, Action<T> changed) where T : MediaFolderItem
    {
        foreach (var item in items.Where(i => !folders.Contains(i.Folder, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            gone(item);
            items.Remove(item);
        }
        for (var i = 0; i < folders.Count; i++)
        {
            var item = items.FirstOrDefault(p => string.Equals(p.Folder, folders[i], StringComparison.OrdinalIgnoreCase));
            if (item is null)
                items.Insert(Math.Min(i, items.Count), item = make(folders[i]));
            else if (items.IndexOf(item) != i)
                items.Move(items.IndexOf(item), Math.Min(i, items.Count - 1));
            var versions = versionsOf(item.Folder);
            if (!versions.SequenceEqual(item.Versions))
            {
                item.Versions = versions;
                changed(item);
            }
        }
    }
}
