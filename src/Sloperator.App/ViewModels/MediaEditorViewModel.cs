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
    [NotifyPropertyChangedFor(nameof(Title), nameof(EmptyText), nameof(MadeUnsaid), nameof(PromptTip))]
    private VersionItem? _shown;

    /// <summary>How the version shown was made, as its file says (ComfyUI's prompt and settings, its workflow); null when it doesn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecipe), nameof(MadeUnsaid), nameof(PromptTip))]
    private MediaRecipe? _recipe;

    public bool HasRecipe => Recipe is not null;

    /// <summary>
    /// Prompt metadata is on: how the version shown was made shows under it, as under a file in the File inspector.
    /// Off until asked for (the user, 2026-11-12: "is it even worth having an entirely separate prompt metadata
    /// window?"): in the Image editor it had a column of its own, a quarter of the panel, beside every picture.
    /// </summary>
    [ObservableProperty]
    private bool _showRecipe;

    /// <summary>What the Prompt metadata button says: there for every version, greyed with why for one that doesn't say how it was made.</summary>
    public string PromptTip => HasRecipe ? "Prompts and settings it was made with"
        : Shown is not null ? "This version doesn't say how it was made" : $"Pick a {_noun} that has been made first";

    /// <summary>A version is shown and says nothing of how it was made: said in the Prompt metadata column (with none shown, it said so of no version).</summary>
    public bool MadeUnsaid => Shown is not null && Recipe is null;

    partial void OnShownChanged(VersionItem? value) => ShownChanged();

    /// <summary>The version shown is another: what of the panel's own follows it.</summary>
    protected virtual void ShownChanged() { }

    partial void OnSelectedChanged(TItem? value)
    {
        Versions.Clear();
        if (value is not null)
            foreach (var (path, i) in value.Versions.Select((p, i) => (p, i)))
                Versions.Add(new VersionItem(i + 1, path));
        ShowVersion(Versions.LastOrDefault());
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
                if (ReferenceEquals(gone, Selected))
                    Selected = null;
                OnGone(gone);
            },
            changed: item =>
            {
                OnVersionsRead(item);
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
        foreach (var path in item.Versions.Skip(known.Count))
            Versions.Add(new VersionItem(Versions.Count + 1, path));
        ShowVersion(Versions[^1]);
        OnPropertyChanged(nameof(Title));
    }

    // ---- the bar's buttons ------------------------------------------------------------------------

    /// <summary>New picture, New sound: an empty folder, picked, so what the user says next is what it is.</summary>
    [RelayCommand]
    private void New()
    {
        // One not made yet is the new one: every press made another empty "Untitled picture 3" beside it (2026-11-12).
        if (Items.FirstOrDefault(i => !i.IsMade) is { } waiting)
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

    public string MakeAgainTip => CanMakeAgain() ? "Make a new version of it, as it was made" : $"Pick a {_noun} that has been made first";

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

    /// <summary>Files of the panel's kind dropped on it go into a new one as its first versions.</summary>
    public bool Drop(IReadOnlyList<string> paths)
    {
        var files = paths.Where(p => File.Exists(p) && _folders.IsVersionFile(p)).ToList();
        if (files.Count == 0)
            return false;
        try
        {
            var folder = _folders.New(Root);
            foreach (var p in files)
                File.Copy(p, Path.Combine(folder, Path.GetFileName(p)));
            Refresh();
            if (Items.FirstOrDefault(i => i.Folder == folder) is { } item)
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
