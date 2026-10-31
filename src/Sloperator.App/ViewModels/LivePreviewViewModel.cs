using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sloperator.Core.Documents;
using Sloperator.Core.Platform;
using Sloperator.Core.Web;

namespace Sloperator.App.ViewModels;

/// <summary>A line of the page's console in the Live preview panel: an error its script hit.</summary>
public sealed class ConsoleItem(ConsoleError error, int number)
{
    public ConsoleError Error { get; } = error;

    /// <summary>Its place in the list, from 1, as a pick names it ("Error 2").</summary>
    public int Number { get; } = number;

    public string Summary => Error.Summary;
    public string Where => Error.Where;
    public bool HasWhere => Error.Where.Length > 0;
    public string Tip => Error.Told;

    /// <summary>What UI Automation and a screen reader call its row.</summary>
    public override string ToString() => Error.Where.Length == 0 ? Summary : $"{Summary} ({Error.Where})";
}

/// <summary>
/// The Live preview panel, beside the chat: a web page or web game the agent is building, shown in
/// WebView2 as a browser would show it (its folder served at https://<see cref="LivePage.Host"/>), so the
/// user plays it and points at what is wrong. Opened with Open file, a page dropped on the panel or Open in
/// Live preview on a page's right-click menu; with Watch on, the page the agent writes opens and the page
/// reloads as files in its folder change. Its script errors are listed under it; error lines selected go
/// into the chat's box, and so does an area marked on the page frozen by Area, as a picture of it.
/// </summary>
public sealed partial class LivePreviewViewModel : ObservableObject, IFilePanel
{
    private readonly MainWindowViewModel? _chat;

    /// <summary>Where pictures of marked areas are saved for the model: the chat's pasted pictures.</summary>
    private readonly string? _areasDir;

    /// <summary>The page's folder read again once a burst of changes on disk is over (an edit is written in parts).</summary>
    private readonly DispatcherTimer _reloadTimer;

    private FileSystemWatcher? _watcher;
    private bool _picking;

    /// <summary>Characters of the errors picked the model is given at most, with the count of the rest (as the File inspector's selection).</summary>
    private const int MaxPickedChars = 20_000;

    /// <param name="chat">The chat the panel is beside: the agent's pages, and where what is picked goes; null in tests of the page alone.</param>
    /// <param name="areasDir">Where pictures of marked areas are saved.</param>
    /// <param name="profileDir">WebView2's user data, kept in Data with the rest of the portable app.</param>
    public LivePreviewViewModel(MainWindowViewModel? chat, string? areasDir, string profileDir)
    {
        _chat = chat;
        _areasDir = areasDir;
        ProfileDir = profileDir;
        _reloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _reloadTimer!.Stop();
            if (!_closed && Path is not null)
                Reload();
        });
        _watch = chat?.Panels.WatchPreview ?? true;
        Errors.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasErrors));
            OnPropertyChanged(nameof(ErrorCount));
        };
        if (chat is null)
            return;
        chat.Attachments.CollectionChanged += OnBoxChanged;
        chat.ChangesUpdated += OnChangesUpdated;
        // A change made before the panel opened isn't followed, but the page the agent wrote last is shown.
        (_followedList, _followedNumber) = (chat.Changes, chat.Changes.LastChange?.Number ?? 0);
        if (Watch && chat.Changes.Last(LivePage.IsPage) is { } page && File.Exists(page))
            Show(page);
    }

    // ---- the page ---------------------------------------------------------------------------------

    /// <summary>Where WebView2 keeps its profile: Datawebview.</summary>
    public string ProfileDir { get; }

    /// <summary>The page shown; null before one is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(HasFile), nameof(CanFreeze), nameof(AreaTip), nameof(ReloadTip))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    private string? _path;

    public string FileName => System.IO.Path.GetFileName(Path ?? "");
    public bool HasFile => Path is not null;

    /// <summary>The folder served at <see cref="LivePage.Host"/>: the page's project, else its folder.</summary>
    [ObservableProperty]
    private string? _folder;

    /// <summary>The page's own folder, shown in the bar and watched: its files changing reload the page.</summary>
    [ObservableProperty]
    private string? _pageFolder;

    /// <summary>Where the page is shown from; the view goes there when it changes.</summary>
    [ObservableProperty]
    private Uri? _address;

    /// <summary>Asked of the view: the page loaded again.</summary>
    internal event Action? ReloadRequested;

    /// <summary>The page loaded as the view says: the number of loads, so a capture or pick tells its page from the next.</summary>
    private int _loads;

    /// <summary>What the panel's middle says with no page, or when WebView2 is missing.</summary>
    public string EmptyText => "Open a web page, or have the agent make one: with Watch on, it shows here as it is written.";

    /// <summary>
    /// WebView2 isn't on this PC (Windows 10 may lack it): the panel says so, with where to get it,
    /// in place of the page.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFreeze), nameof(AreaTip), nameof(ReloadTip))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    private bool _missing;

    /// <summary>Microsoft's page for the WebView2 runtime.</summary>
    public const string RuntimeAddress = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>Shows <paramref name="path"/> (Open file, a page dropped, Open in Live preview, the agent's page with Watch on).</summary>
    public void Show(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var folder = LivePage.Folder(path);
        Unpick();
        AreaMode = false;
        Path = path;
        var own = System.IO.Path.GetDirectoryName(path)!;
        if (!string.Equals(own, PageFolder, StringComparison.OrdinalIgnoreCase))
            WatchFolder(own);
        PageFolder = own;
        Folder = folder;
        var address = LivePage.Address(path, folder);
        if (address == Address)
            Reload();
        else
            Address = address;
    }

    /// <summary>Reload, and the page's folder changing with Watch on: the page loaded again.</summary>
    [RelayCommand(CanExecute = nameof(CanReload))]
    private void Reload()
    {
        Unpick();
        AreaMode = false;
        ReloadRequested?.Invoke();
    }

    private bool CanReload() => HasFile && !Missing;

    public string ReloadTip => Missing ? "WebView2 isn't installed" : HasFile ? "Load the page again" : "Open a page first";

    /// <summary>
    /// The view went to <paramref name="address"/> (the page's own link, the user's click in it): its file
    /// is the page shown; another site keeps the page's name, the folder still watched.
    /// </summary>
    public void Navigated(Uri address)
    {
        _loads++;
        Errors.Clear();
        if (Folder is { } folder && LivePage.PathOf(address, folder) is { } path && !string.Equals(path, Path, StringComparison.OrdinalIgnoreCase))
        {
            Unpick();
            Path = path;
        }
    }

    // ---- Watch: the agent's page --------------------------------------------------------------------

    /// <summary>
    /// Watch: the page the agent writes opens, and the page reloads 250 ms after files in its folder
    /// stop changing (as the Code editor reads its folder again). Kept in settings.
    /// </summary>
    [ObservableProperty]
    private bool _watch;

    partial void OnWatchChanged(bool value)
    {
        if (_chat is null)
            return;
        _chat.Panels.WatchPreview = value;
        _chat.SavePanels();
        // Turned on, it goes to the agent's last page.
        if (value && _chat.Changes.Last(LivePage.IsPage) is { } page && File.Exists(page) && !string.Equals(page, Path, StringComparison.OrdinalIgnoreCase))
            Show(page);
    }

    private Core.Diffs.FileChanges? _followedList;
    private int _followedNumber;

    /// <summary>The agent changed a file: with Watch on, a page it wrote opens (one in the folder shown reloads by itself).</summary>
    private void OnChangesUpdated()
    {
        if (_chat is null || _closed)
            return;
        var changes = _chat.Changes;
        if (!ReferenceEquals(changes, _followedList))
            (_followedList, _followedNumber) = (changes, 0);
        if (changes.LastChange is not { } last || last.Number == _followedNumber)
            return;
        _followedNumber = last.Number;
        if (Watch && LivePage.IsPage(last.Path) && File.Exists(last.Path) && !string.Equals(last.Path, Path, StringComparison.OrdinalIgnoreCase))
            Show(last.Path);
    }

    /// <summary>The page's folder, watched for any file written, added, deleted or renamed in it or a folder under it.</summary>
    private void WatchFolder(string folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += OnDisk;
            watcher.Deleted += OnDisk;
            watcher.Renamed += OnDisk;
            watcher.Changed += OnDisk;
            watcher.Error += (_, _) => Dispatcher.UIThread.Post(Changed);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

    // On the watcher's thread.
    private void OnDisk(object? sender, FileSystemEventArgs e)
    {
        // A folder's own date changing says nothing a file in it doesn't.
        if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath) || PageFolder is not { } folder || !LivePage.Reloads(e.FullPath, folder))
            return;
        Dispatcher.UIThread.Post(Changed);
    }

    /// <summary>A file in the folder changed: with Watch on, the page reloads once the changes rest.</summary>
    internal void Changed()
    {
        if (!Watch || _closed || Missing)
            return;
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    // ---- the page's console -------------------------------------------------------------------------

    /// <summary>The errors the page's scripts hit since it last loaded, oldest first.</summary>
    public ObservableCollection<ConsoleItem> Errors { get; } = [];

    public bool HasErrors => Errors.Count > 0;

    /// <summary>How many, beside the list's heading; nothing with none.</summary>
    public string ErrorCount => Errors.Count == 0 ? "" : Errors.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>What the empty list says.</summary>
    public string NoErrorsText => HasFile ? "No errors since the page loaded" : "The page's script errors show here";

    /// <summary>A message the page's start script posted (<see cref="LivePage.ConsoleScript"/>).</summary>
    public void Message(string? body)
    {
        if (LivePage.Parse(body, Folder) is { } error)
            Errors.Add(new ConsoleItem(error, Errors.Count + 1));
    }

    /// <summary>The errors picked into the box; none with none.</summary>
    private IReadOnlyList<ConsoleItem> _pickedErrors = [];

    /// <summary>Raised to clear the list's selection: what was picked was sent, unpicked or taken off the box.</summary>
    public event Action? Unpicked;

    /// <summary>Error lines selected in the list: into the box as "Errors 1-3 · game.html" ("Error 2", "3 errors"); none selected takes them out.</summary>
    public void PickErrors(IReadOnlyList<ConsoleItem> picked)
    {
        if (picked.Count == 0 || Path is not { } path)
        {
            if (_pickedErrors.Count > 0)
                Unpick();
            return;
        }
        DropArea();
        Mark = null;
        _pickedErrors = picked.OrderBy(p => p.Number).ToList();
        _picking = true;
        try { _chat?.Pick([(path, $"{ErrorsCaption(_pickedErrors)} · {FileName}")]); }
        finally { _picking = false; }
    }

    /// <summary>"Error 2", "Errors 1-3", "3 errors" for ones not one after another.</summary>
    public static string ErrorsCaption(IReadOnlyList<ConsoleItem> picked)
    {
        if (picked.Count == 1)
            return $"Error {picked[0].Number}";
        var (first, last) = (picked[0].Number, picked[^1].Number);
        return last - first + 1 == picked.Count ? $"Errors {first}-{last}" : $"{picked.Count} errors";
    }

    // ---- Area: the page frozen to be marked on ----------------------------------------------------

    /// <summary>
    /// Area is on: the page shown as a still picture of it (<see cref="Frozen"/>), to drag an area over.
    /// Nothing Sloperator draws can go over WebView2's window, so the still stands in for it meanwhile.
    /// </summary>
    [ObservableProperty]
    private bool _areaMode;

    public bool CanFreeze => _chat is not null && HasFile && !Missing;

    public string AreaTip => Missing ? "WebView2 isn't installed" : HasFile ? "Freeze the page to drag over a part of it" : "Open a page first";

    /// <summary>The page frozen while Area is on; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFrozen), nameof(ShowsPage))]
    private Bitmap? _frozen;

    public bool IsFrozen => Frozen is not null;

    /// <summary>The live page is shown: not while it is frozen (it is hidden once captured: hidden, WebView2 may not draw it).</summary>
    public bool ShowsPage => HasFile && Frozen is null && !Missing;

    partial void OnPathChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowsPage));
        OnPropertyChanged(nameof(NoErrorsText));
    }

    partial void OnMissingChanged(bool value) => OnPropertyChanged(nameof(ShowsPage));

    /// <summary>The page's pixels frozen, and the page's view as the page saw it (CSS pixels, scrolled where it was).</summary>
    private (RawImage Pixels, PageView View)? _still;

    /// <summary>The page's own view when frozen: its size in CSS pixels and how far it was scrolled.</summary>
    public sealed record PageView(double Width, double Height, double ScrollX, double ScrollY);

    /// <summary>The page frozen (by the view, which captured it) to mark an area on; with no picture, Area goes off.</summary>
    internal void Freeze(RawImage? pixels, PageView? view)
    {
        Unfreeze();
        if (!AreaMode)
            return;
        if (pixels is null)
        {
            AreaMode = false;
            return;
        }
        _still = (pixels, view ?? new PageView(pixels.Width, pixels.Height, 0, 0));
        Frozen = PdfPageItem.ToBitmap(pixels.Bgra, pixels.Width, pixels.Height);
    }

    partial void OnAreaModeChanged(bool value)
    {
        if (value)
            return;
        if (_area is not null)
            Unpick();
        Unfreeze();
    }

    private void Unfreeze()
    {
        _still = null;
        if (Frozen is not { } frozen)
            return;
        Frozen = null;
        frozen.Dispose();
    }

    /// <summary>The area marked on the frozen page; null with none.</summary>
    [ObservableProperty]
    private DocumentArea? _mark;

    /// <summary>The area picked into the box: its picture saved for the model, and where it is as the model is told it.</summary>
    private (string Picture, string Told)? _area;

    /// <summary>An area dragged over the frozen page: a picture of it is cut, saved and put in the box as "Area · game.html". Null (a click) takes it out.</summary>
    public void PickArea(DocumentArea? area)
    {
        if (area is null || _still is not { } still || _areasDir is null || Path is null)
        {
            Unpick();
            return;
        }
        Mark = area;
        var (pixels, view) = still;
        var name = FileName;
        var dir = _areasDir;
        var loads = _loads;
        var told = $"They marked an area of the page as the panel showed it, {area.Place(view.Width, view.Height, "CSS pixels of the page's view")}" +
                   (view.ScrollX > 0 || view.ScrollY > 0 ? $", the page scrolled {view.ScrollX:0} right and {view.ScrollY:0} down" : "");
        Task.Run(() =>
        {
            try
            {
                var file = DocumentArea.SaveNew(area.Crop(pixels), dir);
                Dispatcher.UIThread.Post(() =>
                {
                    // Another area marked meanwhile, the page loaded again, or the mark taken off: it isn't wanted.
                    if (_closed || loads != _loads || !Equals(Mark, area))
                    {
                        TryDelete(file);
                        return;
                    }
                    // Errors picked before leave the box: their rows let go too.
                    if (_pickedErrors.Count > 0)
                    {
                        _pickedErrors = [];
                        Unpicked?.Invoke();
                    }
                    DropArea();
                    _area = (file, told);
                    _picking = true;
                    try { _chat?.Pick([(file, $"Area · {name}")]); }
                    finally { _picking = false; }
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Dispatcher.UIThread.Post(() => _chat?.AddNotice($"The marked area could not be saved: {ex.Message}", isError: true, ex));
            }
        });
    }

    /// <summary>The area picked before goes: its picture, which wasn't sent, with it.</summary>
    private void DropArea()
    {
        if (_area is { } area)
            TryDelete(area.Picture);
        _area = null;
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- what is picked, into the chat's box ------------------------------------------------------

    /// <summary>Esc, a click away, another page: what was picked leaves the box. False when there was nothing.</summary>
    public bool Unpick()
    {
        Mark = null;
        if (_pickedErrors.Count == 0 && _area is null)
            return false;
        _pickedErrors = [];
        DropArea();
        _picking = true;
        try { _chat?.Pick([]); }
        finally { _picking = false; }
        Unpicked?.Invoke();
        return true;
    }

    /// <summary>What was picked taken off the box another way: it is no longer picked here either.</summary>
    private void OnBoxChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_picking || _pickedErrors.Count == 0 && _area is null || e.OldItems is null || !e.OldItems.OfType<AttachmentItem>().Any(a => a.Picked))
            return;
        if (_chat?.Attachments.Any(a => a.Picked) == true)
            return;
        _pickedErrors = [];
        DropArea();
        Mark = null;
        Unpicked?.Invoke();
    }

    /// <summary>
    /// What a message sent with something picked carries: over its bubble, the page and what of it; for
    /// the model, the page's path and address, and the errors picked (whole, with where they were thrown)
    /// or the area's picture, attached as the picked file.
    /// </summary>
    public (string Via, string Context)? About(IReadOnlyList<string> files)
    {
        if (Path is not { } path)
            return null;
        var opening = $"[The Live preview panel is open beside the chat, showing the web page {path} in a browser (WebView2), served from the folder {Folder} at https://{LivePage.Host}/. ";
        if (_area is { } area && files.Contains(area.Picture, StringComparer.OrdinalIgnoreCase))
            return ($"Live preview · {FileName} · area",
                $"{opening}{area.Told}. The picture {area.Picture} attached below is that area, cut from it, which is what they refer to.]");
        if (_pickedErrors.Count == 0 || !files.Contains(path, StringComparer.OrdinalIgnoreCase))
            return ("Live preview · " + FileName, opening + "What they say is about this page unless they say otherwise.]");
        var caption = ErrorsCaption(_pickedErrors);
        var context = new System.Text.StringBuilder(opening)
            .Append($"They picked {(_pickedErrors.Count == 1 ? "this error" : "these errors")} from the page's console (its script errors since it last loaded, numbered from 1), which is what they refer to:\n");
        var told = string.Join("\n", _pickedErrors.Select(e => $"{e.Number}. {e.Error.Told}"));
        context.Append(told.Length <= MaxPickedChars ? told : told[..MaxPickedChars] + $"\n[... {told.Length - MaxPickedChars:N0} more characters]");
        context.Append("\n]");
        return ($"Live preview · {FileName} · {char.ToLowerInvariant(caption[0])}{caption[1..]}", context.ToString());
    }

    /// <summary>The message went with what was picked: it is no longer picked (an area's picture went with it, and stays).</summary>
    public void Sent()
    {
        if (_pickedErrors.Count == 0 && _area is null)
            return;
        _pickedErrors = [];
        _area = null;
        Mark = null;
        AreaMode = false;
        Unpicked?.Invoke();
    }

    /// <summary>A page dropped on the panel is shown (the first of several); other files go with the next message.</summary>
    public bool Drop(IReadOnlyList<string> paths)
    {
        if (paths.FirstOrDefault(p => LivePage.IsPage(p) && File.Exists(p)) is not { } page)
            return false;
        Show(page);
        return true;
    }

    private bool _closed;

    public void Dispose()
    {
        _closed = true;
        _reloadTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
        if (_chat is not null)
        {
            _chat.Attachments.CollectionChanged -= OnBoxChanged;
            _chat.ChangesUpdated -= OnChangesUpdated;
        }
        DropArea();
        Unfreeze();
    }
}
