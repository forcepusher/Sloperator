using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sloperator.Core.Chats;
using Sloperator.Core.Documents;
using Sloperator.Core.Media;

namespace Sloperator.App.ViewModels;

/// <summary>A picture in the Image editor panel's list: its folder, its name, and a small copy of its newest version.</summary>
public sealed partial class PictureItem(string folder) : MediaFolderItem(folder)
{
    /// <summary>Its newest version, 160 wide; null before it has one.</summary>
    [ObservableProperty]
    private Bitmap? _thumbnail;

    private string? _thumbnailOf;

    /// <summary>The thumbnail read again when the newest version is another file.</summary>
    internal void LoadThumbnail()
    {
        if (Versions.Count == 0 || Versions[^1] == _thumbnailOf)
            return;
        var path = _thumbnailOf = Versions[^1];
        Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                var bitmap = Bitmap.DecodeToWidth(stream, 160);
                Dispatcher.UIThread.Post(() =>
                {
                    var old = Thumbnail;
                    Thumbnail = bitmap;
                    old?.Dispose();
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
            }
        });
    }
}

/// <summary>A version of the picture shown: its number from 1, and whether it is the one in the big view.</summary>
public sealed partial class VersionItem(int number, string path) : ObservableObject
{
    public int Number { get; } = number;
    public string Path { get; } = path;
    public string Label => Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Tip => $"Version {Number} · {System.IO.Path.GetFileName(Path)}";

    [ObservableProperty]
    private bool _isShown;

    public override string ToString() => $"Version {Number}";
}

/// <summary>
/// The Image editor panel, beside the chat, a sibling of the Video editor for single pictures:
/// the pictures in SloperatorData\pictures (a folder each, its versions the picture files in it), the picked one big
/// with its versions under it, and how the version shown was made beside it (its Prompt metadata, as the
/// File inspector shows it). The agent makes them with the comfyui tool, saving each result into the
/// picture's folder, so a redo is a new version and the earlier ones stay. A click on a picture or a
/// version picks it into the chat's box; a box dragged over the big picture picks that part, with a mask
/// of it, for the agent to redraw only that part (sloperator/redraw_area).
/// </summary>
public sealed partial class ImageEditorViewModel : ObservableObject, ISidePanel
{
    private readonly MainWindowViewModel? _chat;

    /// <summary>Where the pictures are: SloperatorData\pictures.</summary>
    public string Root { get; }

    /// <summary>Where pictures of marked parts and their masks go for the message: the chat's pasted files.</summary>
    private readonly string _picksDir;

    /// <summary>A run copies its picture in, and the list follows once the writing rests.</summary>
    private readonly Debounce _read;

    private readonly FileSystemWatcher? _watcher;
    private readonly BoxPicks _box;

    /// <param name="chat">The chat the panel is beside, where picks go and its messages are sent; null in tests.</param>
    public ImageEditorViewModel(MainWindowViewModel? chat, string root, string picksDir)
    {
        _chat = chat;
        Root = root;
        _picksDir = picksDir;
        _read = new Debounce(TimeSpan.FromMilliseconds(250), Refresh);
        _box = new BoxPicks(chat, OnTakenOff);
        try { Directory.CreateDirectory(Root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        _watcher = FolderWatch.Start(Root, (_, _) => Dispatcher.UIThread.Post(_read.Restart), () => Dispatcher.UIThread.Post(_read.Restart));
        Refresh();
    }

    // ---- the pictures -------------------------------------------------------------------------------

    public ObservableCollection<PictureItem> Pictures { get; } = [];

    public bool HasPictures => Pictures.Count > 0;

    /// <summary>The picture shown big; null with none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected), nameof(Title), nameof(EmptyText), nameof(MakeAgainTip))]
    [NotifyCanExecuteChangedFor(nameof(MakeAgainCommand))]
    private PictureItem? _selected;

    public bool HasSelected => Selected is not null;

    /// <summary>What the bar says: the picture and the version shown, else how the panel is used.</summary>
    public string Title => Selected is not { } p ? "Click a picture or a version to ask about it, or drag over a part of it to redraw that part."
        : Shown is { } v ? $"{p.Name} · version {v.Number} of {p.Versions.Count}" : $"{p.Name} · not made yet";

    /// <summary>What the big view says with no version to show.</summary>
    public string EmptyText => Selected is { IsMade: false } ? "Not made yet. Say in the chat what it shows, and the agent makes it here."
        : HasPictures ? "Pick a picture" : "No pictures yet. New picture starts one; say in the chat what it shows.";

    /// <summary>The versions of the picture shown, oldest first.</summary>
    public ObservableCollection<VersionItem> Versions { get; } = [];

    /// <summary>The version in the big view; null before the picture has one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(HasPicture))]
    private VersionItem? _shown;

    /// <summary>The version shown, decoded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture))]
    private Bitmap? _picture;

    public bool HasPicture => Picture is not null && Shown is not null;

    /// <summary>The version's size in pixels.</summary>
    private (int Width, int Height) _size;

    /// <summary>How the version shown was made, as its file says (ComfyUI's prompt, the settings); null when it doesn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecipe))]
    private MediaRecipe? _recipe;

    public bool HasRecipe => Recipe is not null;

    /// <summary>The zoom of the big view (1 fits it).</summary>
    [ObservableProperty]
    private double _zoom = 1;

    /// <summary>The part marked on the big view; null with none.</summary>
    [ObservableProperty]
    private DocumentArea? _mark;

    partial void OnSelectedChanged(PictureItem? value)
    {
        Versions.Clear();
        if (value is not null)
            foreach (var (path, i) in value.Versions.Select((p, i) => (p, i)))
                Versions.Add(new VersionItem(i + 1, path));
        ShowVersion(Versions.LastOrDefault());
    }

    /// <summary>A version into the big view, its Prompt metadata beside it.</summary>
    private void ShowVersion(VersionItem? version)
    {
        foreach (var v in Versions)
            v.IsShown = ReferenceEquals(v, version);
        Shown = version;
        Mark = null;
        Zoom = 1;
        var old = Picture;
        Picture = null;
        old?.Dispose();
        Recipe = null;
        if (version is null)
            return;
        var path = version.Path;
        Task.Run(() =>
        {
            Bitmap? bitmap = null;
            try { bitmap = new Bitmap(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
            var recipe = MediaRecipe.Read(path);
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || Shown?.Path != path)
                {
                    bitmap?.Dispose();
                    return;
                }
                Picture = bitmap;
                _size = bitmap is null ? default : (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                Recipe = recipe;
            });
        });
    }

    /// <summary>
    /// The picture in <paramref name="folder"/> shown, its newest version big (Auto, as the agent makes
    /// one), without picking it: only a click on it picks it, for the box.
    /// </summary>
    public void Show(string folder)
    {
        Refresh();
        if (Pictures.FirstOrDefault(p => string.Equals(p.Folder, Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase)) is { } picture
            && !ReferenceEquals(picture, Selected))
            Selected = picture;
    }

    /// <summary>The pictures folder read again: new pictures in the list, new versions under the one shown (the newest then shown, as the agent's run makes it).</summary>
    public void Refresh()
    {
        if (_closed)
            return;
        MediaFolderItem.Sync(Pictures, PictureFolder.All(Root), folder => new PictureItem(folder), PictureFolder.Versions,
            gone: gone =>
            {
                if (ReferenceEquals(gone, Selected))
                    Selected = null;
                gone.Thumbnail?.Dispose();
            },
            changed: item =>
            {
                item.LoadThumbnail();
                if (ReferenceEquals(item, Selected))
                    NewVersions(item);
            });
        OnPropertyChanged(nameof(HasPictures));
        OnPropertyChanged(nameof(EmptyText));
        // The picture picked may have been made meanwhile.
        OnPropertyChanged(nameof(MakeAgainTip));
        MakeAgainCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The picture shown got versions: they join the strip, and the newest is shown.</summary>
    private void NewVersions(PictureItem item)
    {
        var known = Versions.Select(v => v.Path).ToList();
        if (!item.Versions.Take(known.Count).SequenceEqual(known))
        {
            OnSelectedChanged(item);
            return;
        }
        foreach (var path in item.Versions.Skip(known.Count))
            Versions.Add(new VersionItem(Versions.Count + 1, path));
        ShowVersion(Versions[^1]);
        OnPropertyChanged(nameof(Title));
    }

    // ---- the bar's buttons ------------------------------------------------------------------------

    /// <summary>New picture: an empty folder, picked, so what the user says next is what it shows.</summary>
    [RelayCommand]
    private void NewPicture()
    {
        string folder;
        try { folder = PictureFolder.New(Root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _chat?.AddNotice($"The new picture's folder could not be made: {ex.Message}", isError: true, ex);
            return;
        }
        Refresh();
        if (Pictures.FirstOrDefault(p => p.Folder == folder) is { } item)
        {
            Selected = item;
            PickPicture();
        }
    }

    /// <summary>Make again: a new version of the picture picked, made as it was (the panel's own message, as Make the video is).</summary>
    [RelayCommand(CanExecute = nameof(CanMakeAgain))]
    private void MakeAgain()
    {
        if (Selected is not { IsMade: true })
            return;
        // Picked now if it wasn't: the message is about it.
        if (_pick is null)
            PickPicture();
        _chat?.SendFromSidePanel("Make this picture again as a new version.", withPicks: true);
    }

    private bool CanMakeAgain() => Selected is { IsMade: true };

    public string MakeAgainTip => CanMakeAgain() ? "Make a new version of it, as it was made" : "Pick a picture that has been made first";

    // ---- what is picked, into the chat's box ------------------------------------------------------

    /// <summary>What is picked: the picture (its version shown, or none yet), or a part of the version with its mask.</summary>
    private sealed record Picked(PictureItem Picture, VersionItem? Version, string File, string? Mask = null, string? Place = null);

    private Picked? _pick;

    /// <summary>Raised when what was picked leaves the box (sent, unpicked, taken off), so the view clears its marks.</summary>
    public event Action? Unpicked;

    /// <summary>A click on a picture in the list or on a version: it is shown, and picked.</summary>
    public void Pick(PictureItem picture, VersionItem? version = null)
    {
        if (!ReferenceEquals(Selected, picture))
            Selected = picture;
        if (version is not null && !ReferenceEquals(version, Shown))
            ShowVersion(version);
        PickPicture();
    }

    /// <summary>The picture selected, as the version shown (or the folder of one not made yet), into the box.</summary>
    private void PickPicture()
    {
        if (Selected is not { } picture)
            return;
        Mark = null;
        DropFiles();
        var file = Shown?.Path ?? picture.Folder;
        _pick = new Picked(picture, Shown, file);
        _box.Put([(file, Shown is { } v ? $"Version {v.Number} · {picture.Name}" : $"New · {picture.Name}")]);
    }

    /// <summary>
    /// A box dragged over the big picture: a picture of that part and a mask of it (white in the box, soft at
    /// its edge) are saved for the message, and the part goes into the box as "Area of version 2 · Lighthouse";
    /// the agent redraws only it. A click (null) picks the version shown whole.
    /// </summary>
    public void PickArea(DocumentArea? area)
    {
        if (area is null)
        {
            PickPicture();
            return;
        }
        if (Selected is not { } picture || Shown is not { } version || _size.Width == 0)
            return;
        Mark = area;
        var (width, height) = _size;
        var dir = _picksDir;
        Task.Run(() =>
        {
            try
            {
                var pixels = DocumentArea.Decode(version.Path) ?? throw new IOException("the picture can't be read");
                var crop = DocumentArea.SaveNew(area.Crop(pixels), dir);
                var mask = Path.Combine(dir, Path.GetFileNameWithoutExtension(crop).Replace("Area", "Mask", StringComparison.Ordinal) + ".png");
                PictureFolder.SaveMask(area, pixels.Width, pixels.Height, mask);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_closed || !Equals(Mark, area) || !ReferenceEquals(Shown, version))
                    {
                        PastedFiles.TryDelete(crop);
                        PastedFiles.TryDelete(mask);
                        return;
                    }
                    DropFiles();
                    _pick = new Picked(picture, version, crop, mask, area.Place(pixels.Width, pixels.Height, "pixels"));
                    _box.Put([(crop, $"Area of version {version.Number} · {picture.Name}")]);
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Dispatcher.UIThread.Post(() => _chat?.AddNotice($"The marked part could not be saved: {ex.Message}", isError: true, ex));
            }
        });
    }

    /// <summary>A part picked before goes: its picture and mask, which weren't sent, with it.</summary>
    private void DropFiles()
    {
        if (_pick is { Mask: { } mask } pick)
        {
            PastedFiles.TryDelete(pick.File);
            PastedFiles.TryDelete(mask);
        }
        _pick = null;
    }

    /// <summary>Esc, a click away: what was picked leaves the box. False when nothing was.</summary>
    public bool Unpick()
    {
        Mark = null;
        if (_pick is null)
            return false;
        DropFiles();
        _box.Put([]);
        Unpicked?.Invoke();
        return true;
    }

    /// <summary>The pick's card taken off the box (its ×): it is no longer picked here either.</summary>
    private void OnTakenOff()
    {
        if (_pick is null)
            return;
        DropFiles();
        Mark = null;
        Unpicked?.Invoke();
    }

    /// <summary>
    /// What a message sent with a pick carries: over its bubble the picture and what of it; for the model, the
    /// picture's folder and versions, and how to make the next one there (the comfyui tool's save_to), or for a
    /// part, the picture, the box in its pixels and the mask, with how to redraw only that part.
    /// </summary>
    public (string Via, string Context)? About(IReadOnlyList<string> files)
    {
        if (_pick is not { } pick || !files.Contains(pick.File, StringComparer.OrdinalIgnoreCase))
            return null;
        var picture = pick.Picture;
        var save = $"Make it with the comfyui tool and save_to set to the picture's folder, \"{picture.Folder}\", so the result is its next version there " +
                   "(the panel shows the newest; earlier versions stay)";
        var opening = $"[The Image editor panel is open beside the chat. ";
        if (pick.Version is null)
            return ($"Image editor · {picture.Name} · new",
                opening + $"They picked a new picture, \"{picture.Name}\", not made yet: what they say is what it shows. {save}; " +
                "sloperator/text_to_image suits a PC without a graphics card.]");
        var version = pick.Version;
        var versions = string.Join(", ", picture.Versions.Select((v, i) => $"{i + 1}: {v}"));
        if (pick.Mask is { } mask)
            return ($"Image editor · {picture.Name} · area of version {version.Number}",
                opening + $"They marked a part of version {version.Number} of the picture \"{picture.Name}\" ({version.Path}) to redraw: {pick.Place}. " +
                $"The picture {pick.File} attached below is that part. Redraw only that part as they say, the rest kept: run the comfyui tool with " +
                $"name=sloperator/redraw_area, set 4.image to \"{version.Path}\", 5.image to the mask \"{mask}\" (white where to redraw), 2.text to what goes there " +
                $"in the picture's style (and 1.ckpt_name to the checkpoint it was made with, if its metadata says), and save_to \"{picture.Folder}\", " +
                $"so it becomes the picture's next version. Its versions: {versions}.]");
        return ($"Image editor · {picture.Name} · version {version.Number}",
            opening + $"They picked version {version.Number} of {picture.Versions.Count} of the picture \"{picture.Name}\" ({version.Path}, attached below). " +
            $"To change it or make it again, {char.ToLowerInvariant(save[0])}{save[1..]}; image_to_image keeps its layout, text_to_image starts afresh from " +
            $"the prompt in its metadata. Its versions: {versions}.]");
    }

    /// <summary>The message went with the pick: it is no longer picked (its files went with it, and stay).</summary>
    public void Sent()
    {
        if (_pick is null)
            return;
        _pick = null;
        Mark = null;
        Unpicked?.Invoke();
    }

    /// <summary>Pictures dropped on the panel go into a new picture as its first versions.</summary>
    public bool Drop(IReadOnlyList<string> paths)
    {
        var pictures = paths.Where(p => File.Exists(p) && PictureFolder.IsPicture(p)).ToList();
        if (pictures.Count == 0)
            return false;
        try
        {
            var folder = PictureFolder.New(Root);
            foreach (var p in pictures)
                File.Copy(p, Path.Combine(folder, Path.GetFileName(p)));
            Refresh();
            if (Pictures.FirstOrDefault(i => i.Folder == folder) is { } item)
                Selected = item;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _chat?.AddNotice($"The pictures could not be added: {ex.Message}", isError: true, ex);
        }
        return true;
    }

    private bool _closed;

    public void Dispose()
    {
        _closed = true;
        _read.Stop();
        _watcher?.Dispose();
        _box.Dispose();
        DropFiles();
        Picture?.Dispose();
        foreach (var picture in Pictures)
            picture.Thumbnail?.Dispose();
    }
}
