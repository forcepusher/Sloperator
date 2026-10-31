using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Sloperator.App.ViewModels;
using Sloperator.Core.Documents;
using Sloperator.Core.Web;

namespace Sloperator.App.Views;

/// <summary>
/// The Live preview panel, on the left of the chat window: the page in Avalonia's web view (WebView2 on
/// Windows, its profile in Data\webview), its errors under it. What WebView2 does that the web view doesn't
/// pass on (a script before the page's own, its folder served at an https host, a picture of the page) is
/// asked of WebView2 itself (<see cref="Platform.Windows.WebView2Page"/>).
/// </summary>
public partial class LivePreviewPanel : UserControl
{
    /// <summary>
    /// The web view: WebView2 on Windows (Avalonia's first choice there). Without WebView2 the panel says
    /// where to get it and hides the view, whatever the web view falls back on (the old Edge's on Windows 10
    /// runs none of what the panel asks of WebView2).
    /// </summary>
    private readonly NativeWebView _web = new();

    private LivePreviewViewModel? _vm;

    /// <summary>The page's ICoreWebView2 once WebView2 is up; 0 before.</summary>
    private nint _core;

    /// <summary>The folder served at <see cref="LivePage.Host"/>, null while none is.</summary>
    private string? _served;

    /// <summary>Set while the list's selection is cleared from the view model, so it isn't taken as the user's.</summary>
    private bool _clearing;

    public LivePreviewPanel()
    {
        InitializeComponent();
        _web.EnvironmentRequested += OnEnvironmentRequested;
        _web.AdapterCreated += OnAdapterCreated;
        _web.AdapterDestroyed += (_, _) => (_core, _served) = (0, null);
        _web.NavigationStarted += (_, e) =>
        {
            if (e.Request is { } address)
                _vm?.Navigated(address);
        };
        _web.WebMessageReceived += (_, e) => _vm?.Message(e.Body);
        Host.Children.Add(_web);
        FrozenBox.Marked += area => _vm?.PickArea(area);
        ErrorList.SelectionChanged += OnErrorsSelected;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.ReloadRequested -= OnReload;
            _vm.Unpicked -= ClearSelection;
        }
        _vm = DataContext as LivePreviewViewModel;
        if (_vm is null)
            return;
        _vm.PropertyChanged += OnVmChanged;
        _vm.ReloadRequested += OnReload;
        _vm.Unpicked += ClearSelection;
        CheckRuntime();
        Go();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        CheckRuntime();
    }

    /// <summary>WebView2's runtime isn't on the PC: the view model says so, the page hidden.</summary>
    private void CheckRuntime()
    {
        if (_vm is not null && WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2) is { } info)
            _vm.Missing = !info.IsInstalled || !info.IsSupported;
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is WindowsWebView2EnvironmentRequestedEventArgs windows && _vm is not null)
            windows.UserDataFolder = _vm.ProfileDir;
    }

    /// <summary>WebView2 is up: the console script goes in before any page loads, then the page shown is loaded.</summary>
    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows() && e.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle handle && handle.CoreWebView2 != 0)
        {
            _core = handle.CoreWebView2;
            try { Platform.Windows.WebView2Page.AddStartScript(_core, LivePage.ConsoleScript); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
#endif
        CheckRuntime();
        Go();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null)
            return;
        if (e.PropertyName == nameof(LivePreviewViewModel.Address))
            Go();
        else if (e.PropertyName == nameof(LivePreviewViewModel.AreaMode) && _vm is { AreaMode: true, Frozen: null })
            _ = FreezeAsync();
    }

    /// <summary>The view goes to the page's address, its folder served there; at its file:// address where WebView2 can't serve a folder.</summary>
    private void Go()
    {
        if (_vm is not { Address: { } address, Path: { } path, Folder: { } folder })
            return;
#if WINDOWS
        if (!OperatingSystem.IsWindows() || _core == 0)
            return;
        if (!string.Equals(_served, folder, StringComparison.OrdinalIgnoreCase))
        {
            try { _served = Platform.Windows.WebView2Page.Serve(_core, LivePage.Host, folder) ? folder : null; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _served = null; }
        }
        _web.Navigate(_served is null ? new Uri(path) : address);
#endif
    }

    private void OnReload()
    {
        if (_vm is null)
            return;
        // The first load waits for WebView2; after, the page as it is now, its folder's files read again.
        if (_web.Source is { } source && source.AbsoluteUri != "about:blank")
            _web.Refresh();
        else
            Go();
    }

    // ---- Area -------------------------------------------------------------------------------------

    /// <summary>
    /// Area turned on: a picture of the page as it is (WebView2's CapturePreview) and the page's own view
    /// (its size in CSS pixels, how far it is scrolled), shown frozen to drag an area over.
    /// </summary>
    private async Task FreezeAsync()
    {
        if (_vm is null)
            return;
        Core.Platform.RawImage? pixels = null;
        LivePreviewViewModel.PageView? view = null;
#if WINDOWS
        if (OperatingSystem.IsWindows() && _core != 0)
        {
            try
            {
                view = View(await _web.InvokeScript("JSON.stringify([innerWidth, innerHeight, scrollX, scrollY])"));
                if (await Platform.Windows.WebView2Page.CaptureAsync(_core) is { } png)
                    pixels = await Task.Run(() => DocumentArea.Decode(png));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
#endif
        _vm?.Freeze(pixels, view);
    }

    /// <summary>[width, height, scrollX, scrollY] as the script gave it back (the script's result is JSON, a string here).</summary>
    private static LivePreviewViewModel.PageView? View(string? result)
    {
        try
        {
            if (result is null)
                return null;
            if (JsonNode.Parse(result) is JsonValue quoted && quoted.TryGetValue<string>(out var inner))
                result = inner;
            return JsonNode.Parse(result) is JsonArray { Count: 4 } a
                ? new LivePreviewViewModel.PageView(a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>(), a[3]!.GetValue<double>())
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // ---- errors -----------------------------------------------------------------------------------

    private void OnErrorsSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_clearing || _vm is null)
            return;
        _vm.PickErrors(ErrorList.SelectedItems?.OfType<ConsoleItem>().ToList() ?? []);
    }

    private void ClearSelection()
    {
        _clearing = true;
        try { ErrorList.SelectedItems?.Clear(); }
        finally { _clearing = false; }
    }

    /// <summary>Esc: what is picked leaves the box; with nothing picked, Area goes off.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _vm is null)
            return;
        if (!_vm.Unpick() && _vm.AreaMode)
            _vm.AreaMode = false;
        e.Handled = true;
    }

    // ---- the bar ----------------------------------------------------------------------------------

    /// <summary>Open file: a web page picked from the PC, in the folder of the one shown.</summary>
    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var start = _vm.Path is { } shown ? await storage.TryGetFolderFromPathAsync(System.IO.Path.GetDirectoryName(shown)!) : null;
        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a web page in Live preview",
            FileTypeFilter = [new FilePickerFileType("Web pages") { Patterns = ["*.html", "*.htm"] }],
            SuggestedStartLocation = start,
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path)
            _vm.Show(path);
    }

    private void OnGetRuntime(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            _ = launcher.LaunchUriAsync(new Uri(LivePreviewViewModel.RuntimeAddress));
    }
}
