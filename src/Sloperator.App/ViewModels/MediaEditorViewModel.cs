using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sloperator.Core.Media;

namespace Sloperator.App.ViewModels;

/// <summary>
/// What the Image and Audio editor panels share (the user's pick, 2026-11-09: the Audio editor's was a copy of the
/// Image editor's): a root of folders, a picture or a sound each, whose files are its versions (<see cref="MediaFolders"/>);
/// the list of them, read again as the folders change; the one picked, its versions as a strip and the version shown
/// with how it was made; New and Make again in the bar; a click picking it into the chat's box; files dropped on the
/// panel making a new one. What a version looks like, what of it can be picked and what the model is told of it are
/// each panel's own.
/// </summary>
public abstract partial class MediaEditorViewModel<TItem> : ObservableObject, ISidePanel where TItem : MediaFolderItem
{
    private readonly MediaFolders _folders;

    /// <summary>What one is called: "picture", "sound".</summary>
    private readonly string _noun;

    /// <summary>A run or an edit writes a version in, and the list follows once the writing rests.</summary>
    private readonly Debounce _read;

    private readonly FileSystemWatcher? _watcher;

    /// <param name="chat">The chat the panel is beside, where picks go and its messages are sent; null in tests.</param>
    /// <param name="root">Where the folders are: SloperatorData\pictures, \sounds.</param>
    /// <param name="picksDir">Where files made for a pick go for the message: the chat's pasted files.</param>
    /// <remarks>The derived panel reads the folders (<see cref="Refresh"/>) once its own fields are set.</remarks>
    protected MediaEditorViewModel(MainWindowViewModel? chat, string root, string picksDir, MediaFolders folders, string noun)
    {
        Chat = chat;
        Root = root;
        PicksDir = picksDir;
        _folders = folders;
        _noun = noun;
        _read = new Debounce(TimeSpan.FromMilliseconds(250), Refresh);
        Box = new BoxPicks(chat, OnTakenOff);
        try { Directory.CreateDirectory(Root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        _watcher = FolderWatch.Start(Root, (_, _) => Dispatcher.UIThread.Post(_read.Restart), () => Dispatcher.UIThread.Post(_read.Restart));
    }

    protected MainWindowViewModel? Chat { get; }

    /// <summary>Where the folders are.</summary>
    public string Root { get; }

    protected string PicksDir { get; }

    private protected BoxPicks Box { get; }

    protected bool Closed { get; private set; }

    // ---- the list ----------------------------------------------------------------------------------

    /// <summary>The pictures or sounds, the one changed last first.</summary>
    public ObservableCollection<TItem> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>The one shown; null with none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected), nameof(Title), nameof(EmptyText), nameof(MakeAgainTip))]
    [NotifyCanExecuteChangedFor(nameof(MakeAgainCommand))]
    private TItem? _selected;

    public bool HasSelected => Selected is not null;

    /// <summary>What the bar says: the one shown and its version, else how the panel is used.</summary>
    public abstract string Title { get; }

    /// <summary>What the big view says with no version to show.</summary>
    public abstract string EmptyText { get; }

    /// <summary>The versions of the one shown, oldest first.</summary>
    public ObservableCollection<VersionItem> Versions { get; } = [];

    /// <summary>The version shown; null before the one picked has one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(EmptyText), nameof(PromptTip))]
    private VersionItem? _shown;

    /// <summary>How the version shown was made, as its file says (ComfyUI's prompt and settings, its workflow); null when it doesn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecipe), nameof(PromptTip))]
    private MediaRecipe? _recipe;

    public bool HasRecipe => Recipe is not null;

    /// <summary>
    /// Prompt metadata is on: how the version shown was made shows under it, as under a file in the File inspector.
    /// Off until asked for (the user, 2026-11-12: "is it even worth having an entirely separate prompt metadata
    /// window?"): in the Image editor it had a column of its own, a quarter of the panel, beside every picture, and in
    /// the Audio editor the room under the Edit buttons of such a column.
    /// </summary>
    [ObservableProperty]
    private bool _showRecipe;

    /// <summary>What the Prompt metadata button says: there for every version, greyed with why for one that doesn't say how it was made.</summary>
    public string PromptTip => HasRecipe ? "Prompts and settings it was made with"
        : Shown is not null ? "This version doesn't say how it was made" : $"Pick a {_noun} that has been made first";

    partial void OnShownChanged(VersionItem? value) => ShownChanged();

    /// <summary>The version shown is another: what of the panel's own follows it.</summary>
    protected virtual void ShownChanged() { }

    partial void OnSelectedChanged(TItem? value)
    {
        var old = Versions.ToList();
        Versions.Clear();
        VersionsGone(old);
        if (value is not null)
            foreach (var (path, i) in value.Versions.Select((p, i) => (p, i)))
                Versions.Add(new VersionItem(i + 1, path));
        VersionsListed(Versions.ToList());
        ShowVersion(Versions.LastOrDefault());
    }

    /// <summary>Versions joined the strip: what the panel shows of each on its button (a picture's small copy).</summary>
    protected virtual void VersionsListed(IReadOnlyList<VersionItem> versions) { }

    /// <summary>Versions left the strip (another one is shown): what they held goes with them.</summary>
    protected virtual void VersionsGone(IReadOnlyList<VersionItem> versions) { }

    /// <summary>
    /// One still named as New named it is called by what it shows (the user's go-ahead, 2026-11-12: every picture
    /// was "Untitled picture", "Untitled picture 2", in the list, the bar, on its card and to the model): the first
    /// clause of its first version's prompt, read off the window's thread. The first version, as a later one can
    /// be a part redrawn, whose prompt says only what went there.
    /// </summary>
    private void ReadTitle(TItem item)
    {
        if (item.Versions.Count == 0 || item.TitledFrom == item.Versions[0] || !_folders.IsUntitled(item.Folder))
            return;
        var path = item.TitledFrom = item.Versions[0];
        Task.Run(() =>
        {
            var title = MediaRecipe.Read(path)?.Title;
            Dispatcher.UIThread.Post(() =>
            {
                if (Closed || item.TitledFrom != path)
                    return;
                item.Titled = title;
                if (ReferenceEquals(item, Selected))
                    OnPropertyChanged(nameof(Title));
            });
        });
    }

    /// <summary>A version into the big view, its Prompt metadata beside it.</summary>
    protected abstract void ShowVersion(VersionItem? version);

    /// <summary>A new item of the list for <paramref name="folder"/>.</summary>
    protected abstract TItem NewItem(string folder);

    /// <summary>The item's folder went: what it holds goes with it (a thumbnail).</summary>
    protected virtual void OnGone(TItem item) { }

    /// <summary>The item's versions were read again: its small look in the list follows (a thumbnail, a waveform).</summary>
    protected virtual void OnVersionsRead(TItem item) { }

    /// <summary>
    /// The one in <paramref name="folder"/> shown, its newest version big (Auto, as the agent makes one), without
    /// picking it: only a click on it picks it, for the box.
    /// </summary>
    public void Show(string folder)
    {
        Refresh();
        if (Items.FirstOrDefault(i => string.Equals(i.Folder, Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase)) is { } item
            && !ReferenceEquals(item, Selected))
            Selected = item;
    }

    /// <summary>The folders read again: new ones in the list, new versions under the one shown (the newest then shown, as a run or an edit makes it).</summary>
    public void Refresh()
    {
        if (Closed)
            return;
        MediaFolderItem.Sync(Items, _folders.All(Root), NewItem, _folders.Versions,
            gone: gone =>
            {
                // Picked into the box, it would go with the next message as a file that isn't there.
                UnpickOf(gone);
                if (ReferenceEquals(gone, Selected))
                    Selected = null;
                OnGone(gone);
            },
            changed: item =>
            {
                OnVersionsRead(item);
                ReadTitle(item);
                if (ReferenceEquals(item, Selected))
                    NewVersions(item);
            });
        // One is always shown when there is any, the one changed last: the panel opened on "Pick a picture" beside
        // the only picture there was (2026-11-12). Shown, not picked into the box: that takes a click.
        if (Selected is null && Items.Count > 0)
            Selected = Items[0];
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(EmptyText));
        // The one picked may have been made meanwhile.
        OnPropertyChanged(nameof(MakeAgainTip));
        MakeAgainCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The one shown got versions: they join the strip, and the newest is shown.</summary>
    private void NewVersions(TItem item)
    {
        var known = Versions.Select(v => v.Path).ToList();
        if (!item.Versions.Take(known.Count).SequenceEqual(known))
        {
            OnSelectedChanged(item);
            return;
        }
        var added = new List<VersionItem>();
        foreach (var path in item.Versions.Skip(known.Count))
        {
            added.Add(new VersionItem(Versions.Count + 1, path));
            Versions.Add(added[^1]);
        }
        VersionsListed(added);
        ShowVersion(Versions[^1]);
        OnPropertyChanged(nameof(Title));
    }

    // ---- the bar's buttons ------------------------------------------------------------------------

    /// <summary>New picture, New sound: an empty folder, picked, so what the user says next is what it is.</summary>
    [RelayCommand]
    private void New()
    {
        // One not made yet is the new one: every press made another empty "Untitled picture 3" beside it (2026-11-12).
        // Not one a message went with already: the agent is making that one, and what is said next is another.
        if (Items.FirstOrDefault(i => !i.IsMade && !Asked(i)) is { } waiting)
        {
            Selected = waiting;
            PickWhole();
            return;
        }
        string folder;
        try { folder = _folders.New(Root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Chat?.AddNotice($"The new {_noun}'s folder could not be made: {ex.Message}", isError: true, ex);
            return;
        }
        Refresh();
        if (Items.FirstOrDefault(i => i.Folder == folder) is { } item)
        {
            Selected = item;
            PickWhole();
        }
    }

    /// <summary>Make again: a new version of the one picked, made as it was (the panel's own message, as Make the video is).</summary>
    [RelayCommand(CanExecute = nameof(CanMakeAgain))]
    private void MakeAgain()
    {
        if (Selected is not { IsMade: true })
            return;
        // Picked now if it wasn't: the message is about it.
        if (!HasPick)
            PickWhole();
        Chat?.SendFromSidePanel($"Make this {_noun} again as a new version.", withPicks: true);
    }

    private bool CanMakeAgain() => Selected is { IsMade: true };

    /// <summary>The ones a message went with that the chat is still at work on: being made or changed.</summary>
    private readonly HashSet<string> _askedFor = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A message went with <paramref name="item"/> picked: the agent is at work on it until the chat rests.</summary>
    protected void AskedFor(TItem item) => _askedFor.Add(item.Folder);

    /// <summary>Whether a message went with <paramref name="item"/> that the chat is still at work on.</summary>
    private bool Asked(TItem item)
    {
        // Once the chat rests, what it was asked for is made, or isn't going to be.
        if (Chat is { IsBusy: false })
            _askedFor.Clear();
        return _askedFor.Contains(item.Folder);
    }

    public string MakeAgainTip => CanMakeAgain() ? "Make a new version of it, as it was made" : $"Pick a {_noun} that has been made first";

    // ---- a right-click: renamed, deleted, shown in its folder ---------------------------------------
    // As the Video editor's cards have it. Until 2026-11-13 a picture or a sound made here could only be renamed
    // or thrown away in Explorer, in a folder the panel didn't show the way to.

    /// <summary>
    /// Whether the agent is at work on <paramref name="item"/> now: a message went with it picked and the chat
    /// hasn't rested since, or a tool call running now names its folder. Renamed or deleted then, the work went
    /// astray: the next version saved into a folder made anew under the old name.
    /// </summary>
    private bool AtWork(TItem item) => Asked(item) || Chat?.RunningCallNames(item.Folder) == true;

    /// <summary>What a right-click on one in the list offers.</summary>
    public IReadOnlyList<PanelMenuItem?> MenuFor(TItem item)
    {
        var free = !AtWork(item);
        return
        [
            new("Rename", free, () => RenameAsked?.Invoke(item), "F2"),
            new("Delete", free, () => Delete(item)),
            new("Show in folder", true, () => FilePreview.ShowInFolder(item.Folder)),
        ];
    }

    /// <summary>What a right-click on a version of the one shown offers.</summary>
    public IReadOnlyList<PanelMenuItem?> MenuFor(VersionItem version) =>
    [
        new("Delete", Selected is { } item && !AtWork(item), () => Delete(version)),
        new("Show in folder", true, () => FilePreview.ShowInFolder(version.Path)),
    ];

    /// <summary>One in the list is to be renamed: the panel shows a box with its name to type the new one in.</summary>
    public event Action<TItem>? RenameAsked;

    /// <summary>F2 on the list: the one shown is renamed. False when it can't be now.</summary>
    public bool AskRename()
    {
        if (Selected is not { } item || AtWork(item))
            return false;
        RenameAsked?.Invoke(item);
        return true;
    }

    /// <summary>
    /// The name typed for <paramref name="item"/>: its folder is renamed, and it stays the one shown. The same
    /// name, or none, changes nothing.
    /// </summary>
    public void Rename(TItem item, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == item.Name || AtWork(item) || !Items.Contains(item))
            return;
        var shown = ReferenceEquals(item, Selected);
        UnpickOf(item);
        // What shows a file of it lets go of it first: a folder can't be moved while a file in it is open.
        if (shown)
            Release();
        string to;
        try
        {
            to = Moved(item.Folder, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Chat?.AddNotice($"The {_noun} could not be renamed: {ex.Message}", isError: true, ex);
            if (shown)
                ShowVersion(Shown);
            return;
        }
        Refresh();
        if (shown && Items.FirstOrDefault(i => string.Equals(i.Folder, to, StringComparison.OrdinalIgnoreCase)) is { } renamed)
            Selected = renamed;
    }

    /// <summary>The folder renamed, once nothing of the panel's own still reads a file in it.</summary>
    private static string Moved(string folder, string name)
    {
        WaitFree(folder);
        return MediaFolders.Rename(folder, name);
    }

    /// <summary>
    /// Waits, a second at most, until no file of <paramref name="path"/> (a version, or a folder of them) is open:
    /// the panel reads each for its small copy in the list and the strip as it is shown, and one deleted or moved
    /// in that moment was refused as in use (a version deleted right after another was, 2026-11-13).
    /// </summary>
    private static void WaitFree(string path)
    {
        try
        {
            var until = Environment.TickCount64 + 1000;
            foreach (var file in Directory.Exists(path) ? Directory.EnumerateFiles(path) : [path])
                while (File.Exists(file) && Environment.TickCount64 < until)
                {
                    try
                    {
                        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
                            break;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(25);
                    }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not to be read at all: the move or the delete says why.
        }
    }

    /// <summary>
    /// <paramref name="item"/> to the Recycle Bin, its versions with it, as Explorer's Delete puts a folder there:
    /// it can be brought back. The one that takes its place in the list is shown.
    /// </summary>
    public void Delete(TItem item)
    {
        if (AtWork(item) || !Items.Contains(item))
            return;
        var (shown, index) = (ReferenceEquals(item, Selected), Items.IndexOf(item));
        UnpickOf(item);
        if (shown)
            Release();
        if (!Binned(item.Folder) && shown)
            ShowVersion(Shown);
        Refresh();
        if (shown && !Items.Contains(item) && Items.Count > 0)
            Selected = Items[Math.Min(index, Items.Count - 1)];
    }

    /// <summary>
    /// A version of the one shown to the Recycle Bin; the one after it is shown, or the one before the last. The
    /// only version takes the picture or sound with it: one deleted down to nothing isn't kept as "not made yet".
    /// </summary>
    public void Delete(VersionItem version)
    {
        if (Selected is not { } item || AtWork(item) || !Versions.Contains(version))
            return;
        if (Versions.Count == 1)
        {
            Delete(item);
            return;
        }
        var index = Versions.IndexOf(version);
        // Another version shown stays the one shown.
        var stays = ReferenceEquals(version, Shown) ? null : Shown?.Path;
        UnpickOf(item);
        if (stays is null)
            Release();
        if (!Binned(version.Path))
        {
            if (stays is null)
                ShowVersion(Shown);
            return;
        }
        Refresh();
        if (!ReferenceEquals(item, Selected) || Versions.Count == 0)
            return;
        var next = stays is not null ? Versions.FirstOrDefault(v => v.Path == stays) : null;
        ShowVersion(next ?? Versions[Math.Min(index, Versions.Count - 1)]);
    }

    /// <summary>A folder or file to the Recycle Bin; false, and said in the chat, when it couldn't go.</summary>
    private bool Binned(string path)
    {
        try
        {
            WaitFree(path);
            Recycle(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Chat?.AddNotice($"The {_noun} could not be deleted: {ex.Message}", isError: true, ex);
            return false;
        }
    }

    /// <summary>How a folder or file is deleted: to the Recycle Bin. A test's own way in its place, which has no bin to fill.</summary>
    public Action<string> Recycle { get; set; } = RecycleBin.Put;

    /// <summary>What holds a file of the one shown open lets go of it (a sound's player), before its folder is renamed or deleted.</summary>
    protected virtual void Release() { }

    /// <summary>What is picked of <paramref name="item"/> leaves the box: it is renamed, deleted or gone, and the pick names files that aren't there any more.</summary>
    protected abstract void UnpickOf(TItem item);

    // ---- what is picked, into the chat's box ------------------------------------------------------

    /// <summary>Raised when what was picked leaves the box (sent, unpicked, taken off), so the view clears its marks.</summary>
    public event Action? Unpicked;

    protected void RaiseUnpicked() => Unpicked?.Invoke();

    /// <summary>Whether something of the panel's is picked into the box.</summary>
    protected abstract bool HasPick { get; }

    /// <summary>The one selected, as the version shown (or the folder of one not made yet), into the box.</summary>
    protected abstract void PickWhole();

    /// <summary>The pick's card taken off the box (its ×): it is no longer picked here either.</summary>
    protected abstract void OnTakenOff();

    /// <summary>A click on one in the list or on a version: it is shown, and picked.</summary>
    public void Pick(TItem item, VersionItem? version = null)
    {
        if (!ReferenceEquals(Selected, item))
            Selected = item;
        if (version is not null && !ReferenceEquals(version, Shown))
            ShowVersion(version);
        PickWhole();
    }

    public abstract (string Via, string Context)? About(IReadOnlyList<string> files);

    public abstract void Sent();

    /// <summary>
    /// Files of the panel's kind dropped on it: each a new one, named as its file, the file its first version; the
    /// first of them is shown. Until 2026-11-13 all of them went into one "Untitled picture" as its versions, so
    /// three photos dropped were one picture's three versions, under a name that said nothing.
    /// </summary>
    public bool Drop(IReadOnlyList<string> paths)
    {
        var files = paths.Where(p => File.Exists(p) && _folders.IsVersionFile(p)).ToList();
        if (files.Count == 0)
            return false;
        try
        {
            string? first = null;
            foreach (var p in files)
            {
                var folder = _folders.New(Root, Path.GetFileNameWithoutExtension(p));
                first ??= folder;
                File.Copy(p, Path.Combine(folder, Path.GetFileName(p)));
            }
            Refresh();
            if (Items.FirstOrDefault(i => i.Folder == first) is { } item)
                Selected = item;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Chat?.AddNotice($"The {_noun}s could not be added: {ex.Message}", isError: true, ex);
        }
        return true;
    }

    public virtual void Dispose()
    {
        Closed = true;
        _read.Stop();
        _watcher?.Dispose();
        Box.Dispose();
    }
}
