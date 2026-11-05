using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sloperator.Core.Platform;
using SkiaSharp;
using static Sloperator.Platform.Windows.Native;

namespace Sloperator.Platform.Windows;

/// <summary>
/// The on-screen input log: small layered windows on their own UI thread, one listing the latest
/// inputs in the bottom-right corner, one drawing an accent-coloured ring where the agent clicked,
/// four drawing a thin accent frame round the screen an agent is at work on, and one the tag that
/// hangs from its top edge and says how to stop it.
/// </summary>
/// <remarks>
/// Both windows are click-through (WS_EX_TRANSPARENT), never activated (WS_EX_NOACTIVATE) and
/// left out of the taskbar and Alt+Tab (WS_EX_TOOLWINDOW), so they can't take focus or catch clicks;
/// the model's screenshots leave them out with Sloperator's other windows (<see cref="WindowsScreenCapture"/>).
/// They were excluded from screen capture for good, which stopped NVIDIA's Instant Replay for as long
/// as the log showed. They are drawn with Skia and use a colour key plus a window-wide opacity rather
/// than per-pixel alpha: Windows refuses capture exclusion, still the fallback, for windows drawn with
/// UpdateLayeredWindow (error 8).
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsInputOverlay : IInputOverlay
{
    private const double FadeSeconds = 1.5, MarkerSeconds = 3, MarkerFadeSeconds = 1;
    private const int MaxLines = 8, PanelWidth = 460, LineHeight = 34, MarkerSize = 200, FrameWidth = 4;
    private const uint WM_APP_REFRESH = WM_APP + 1;
    private const uint ColorKey = 0x00FF00FF; // magenta, as COLORREF (0x00BBGGRR)
    private const string StopTag = "Pause/Break stops me";

    private readonly object _lock = new();
    private readonly List<Entry> _entries = [];
    private (int X, int Y, DateTime At)? _marker;
    private (object Owner, ScreenRect Screen)? _frame;
    private ScreenRect? _frameShown; // on the overlay's thread
    private volatile bool _enabled;
    private double _logSeconds = 15;
    private SKColor _markerColor = new(0xFC, 0xC2, 0x1B); // the accent, until the app hands over its own
    private Thread? _thread;
    private uint _threadId;
    private nint _logWindow, _markerWindow;
    private nint[] _frameWindows = []; // top, bottom, left, right
    private nint _stopTagWindow;
    private bool _timerOn;
    private WndProc? _wndProc; // kept alive for the window class

    private sealed record Entry(string Text, int Count, DateTime At);

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value)
                EnsureStarted();
            else
            {
                lock (_lock) { _entries.Clear(); _marker = null; _frame = null; }
                Refresh();
            }
        }
    }

    public TimeSpan Duration
    {
        get => TimeSpan.FromSeconds(_logSeconds);
        set => _logSeconds = Math.Max(FadeSeconds, value.TotalSeconds);
    }

    public (byte R, byte G, byte B) MarkerColor
    {
        get => (_markerColor.Red, _markerColor.Green, _markerColor.Blue);
        set => _markerColor = new SKColor(value.R, value.G, value.B);
    }

    public void Show(string text, (int X, int Y)? point = null)
    {
        if (!_enabled)
            return;
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            // "← ×3" instead of three lines of the same key.
            if (_entries.Count > 0 && _entries[^1].Text == text && (now - _entries[^1].At).TotalSeconds < 2)
                _entries[^1] = new Entry(text, _entries[^1].Count + 1, now);
            else
                _entries.Add(new Entry(text, 1, now));
            if (_entries.Count > MaxLines)
                _entries.RemoveAt(0);
            if (point is { } p)
                _marker = (p.X, p.Y, now);
        }
        Refresh();
    }

    public void Frame(object owner, ScreenRect? screen)
    {
        lock (_lock)
        {
            if (screen is { } on)
                _frame = (owner, on);
            else if (_frame is { } shown && ReferenceEquals(shown.Owner, owner))
                _frame = null;
            else
                return;
        }
        if (_enabled)
            Refresh();
    }

    private void Refresh()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_APP_REFRESH, 0, 0);
    }

    private void EnsureStarted()
    {
        if (_thread is not null)
            return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Sloperator input overlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run(ManualResetEventSlim ready)
    {
        EnsurePhysicalPixels();
        _threadId = GetCurrentThreadId();
        _wndProc = DefWindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "SloperatorInputOverlay",
        };
        RegisterClassEx(ref wc);
        _logWindow = CreateOverlayWindow(wc.hInstance);
        _markerWindow = CreateOverlayWindow(wc.hInstance);
        _frameWindows = [.. Enumerable.Range(0, 4).Select(_ => CreateOverlayWindow(wc.hInstance))];
        _stopTagWindow = CreateOverlayWindow(wc.hInstance);
        ready.Set();

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message is WM_APP_REFRESH or WM_TIMER)
            {
                try { Render(); }
                catch { /* a drawing hiccup must never take the agent down */ }
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static nint CreateOverlayWindow(nint instance)
    {
        var hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            "SloperatorInputOverlay", "", WS_POPUP, 0, 0, 1, 1, 0, 0, instance, 0);
        // Colour-key transparency (not UpdateLayeredWindow) so that capture exclusion is allowed.
        SetLayeredWindowAttributes(hwnd, ColorKey, 255, LWA_COLORKEY | LWA_ALPHA);
        return hwnd;
    }

    private void Render()
    {
        var now = DateTime.UtcNow;
        List<(Entry Entry, double Alpha)> lines;
        (int X, int Y, double Age)? marker = null;
        ScreenRect? frame;
        lock (_lock)
        {
            frame = _frame?.Screen;
            _entries.RemoveAll(e => (now - e.At).TotalSeconds >= _logSeconds);
            lines = _entries.Select(e => (e, Fade((now - e.At).TotalSeconds))).ToList();
            if (_marker is { } m && (now - m.At).TotalSeconds < MarkerSeconds)
                marker = (m.X, m.Y, (now - m.At).TotalSeconds);
        }

        if (lines.Count > 0) DrawLog(lines); else ShowWindow(_logWindow, SW_HIDE);
        if (marker is { } mk) DrawMarker(mk.X, mk.Y, mk.Age); else ShowWindow(_markerWindow, SW_HIDE);
        if (frame != _frameShown) DrawFrame(frame);

        // Animate only while something is on screen.
        var active = lines.Count > 0 || marker is not null;
        if (active && !_timerOn) { SetTimer(_logWindow, 1, 40, 0); _timerOn = true; }
        else if (!active && _timerOn) { KillTimer(_logWindow, 1); _timerOn = false; }
    }

    private double Fade(double age) =>
        age < _logSeconds - FadeSeconds ? 1 : Math.Clamp((_logSeconds - age) / FadeSeconds, 0, 1);

    private static readonly SKColor LineBack = new(28, 28, 32);

    private void DrawLog(List<(Entry Entry, double Alpha)> lines)
    {
        var height = lines.Count * LineHeight + 8;
        SystemParametersInfo(SPI_GETWORKAREA, 0, out var work, 0);
        var x = work.Right - PanelWidth - 16;
        var y = work.Bottom - height - 16;
        Paint(_logWindow, x, y, PanelWidth, height, 230, canvas =>
        {
            using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
            // No antialiasing on the boxes: their edges meet the colour key, and blended edge
            // pixels would show as a pink fringe.
            using var back = new SKPaint { IsAntialias = false, Color = LineBack };
            using var font = new SKFont(typeface, 17);
            using var text = new SKPaint { IsAntialias = true };
            for (var i = 0; i < lines.Count; i++)
            {
                var (entry, alpha) = lines[i];
                var label = entry.Count > 1 ? $"{entry.Text}  ×{entry.Count}" : entry.Text;
                var width = Math.Min(PanelWidth, font.MeasureText(label) + 24);
                var top = 4 + i * LineHeight;
                canvas.DrawRect(new SKRect(PanelWidth - width, top, PanelWidth, top + LineHeight - 6), back);
                // A line fades by its text sinking into the box; the box goes when the line does.
                text.Color = Blend(LineBack, SKColors.White, alpha);
                canvas.DrawText(label, PanelWidth - width + 12, top + 20, SKTextAlign.Left, font, text);
            }
        });
    }

    private static readonly SKColor MarkerOutline = new(20, 20, 24);

    private void DrawMarker(int cx, int cy, double age)
    {
        // The ring widens out from the click for its whole life, and the whole window fades at the
        // end, so the ring can be drawn fully opaque. The accent goes on a dark outline, which keeps
        // a light accent visible on a white page.
        var radius = 10 + 78 * (float)Math.Clamp(age / MarkerSeconds, 0, 1);
        var opacity = Math.Clamp((MarkerSeconds - age) / MarkerFadeSeconds, 0, 1);
        Paint(_markerWindow, cx - MarkerSize / 2, cy - MarkerSize / 2, MarkerSize, MarkerSize, (byte)(255 * opacity), canvas =>
        {
            // No antialiasing: blended edge pixels against the colour key would show a pink fringe.
            using var outline = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 12, Color = MarkerOutline };
            using var ring = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 6, Color = _markerColor };
            using var dotOutline = new SKPaint { IsAntialias = false, Color = MarkerOutline };
            using var dot = new SKPaint { IsAntialias = false, Color = _markerColor };
            const float c = MarkerSize / 2f;
            canvas.DrawCircle(c, c, radius, outline);
            canvas.DrawCircle(c, c, radius, ring);
            canvas.DrawCircle(c, c, 10, dotOutline);
            canvas.DrawCircle(c, c, 6, dot);
        });
    }

    /// <summary>The frame round <paramref name="screen"/>, in four strips over its edges, and its tag; none for null. It doesn't move, so it is drawn only when it changes.</summary>
    private void DrawFrame(ScreenRect? screen)
    {
        _frameShown = screen;
        if (screen is not { } r)
        {
            foreach (var hwnd in _frameWindows)
                ShowWindow(hwnd, SW_HIDE);
            ShowWindow(_stopTagWindow, SW_HIDE);
            return;
        }
        const int w = FrameWidth;
        (int X, int Y, int Width, int Height)[] strips =
        [
            (r.X, r.Y, r.Width, w),
            (r.X, r.Y + r.Height - w, r.Width, w),
            (r.X, r.Y + w, w, r.Height - 2 * w),
            (r.X + r.Width - w, r.Y + w, w, r.Height - 2 * w),
        ];
        for (var i = 0; i < strips.Length; i++)
        {
            var (x, y, width, height) = strips[i];
            Paint(_frameWindows[i], x, y, width, height, 255, canvas => canvas.Clear(_markerColor));
        }
        DrawStopTag(r);
    }

    /// <summary>
    /// The tag in the middle of the frame's top edge, out of the way of the screen's corners, as big
    /// as the screen's scale makes text: the start screen's Pause/Break card is gone by the time the
    /// user wants it, and the frame is what they see then. It goes through to the windows under it
    /// like the frame, and the screenshots leave it out.
    /// </summary>
    private void DrawStopTag(ScreenRect screen)
    {
        var monitor = MonitorFromPoint(new POINT { X = screen.X, Y = screen.Y }, MONITOR_DEFAULTTONEAREST);
        var scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96f : 1f;
        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
        using var font = new SKFont(typeface, 15 * scale);
        var width = (int)Math.Ceiling(font.MeasureText(StopTag) + 32 * scale);
        var height = (int)Math.Round(30 * scale);
        Paint(_stopTagWindow, screen.X + (screen.Width - width) / 2, screen.Y, width, height, 255, canvas =>
        {
            // No antialiasing on the shape, as on the log's boxes; the words lie on the accent, not on the colour key.
            using var back = new SKPaint { IsAntialias = false, Color = _markerColor };
            using var text = new SKPaint { IsAntialias = true, Color = MarkerOutline };
            var radius = 8 * scale;
            // Round at the bottom only: the top runs off the window, into the frame.
            canvas.DrawRoundRect(new SKRect(0, -radius, width, height), radius, radius, back);
            var baseline = (height + font.Metrics.CapHeight) / 2;
            canvas.DrawText(StopTag, width / 2f, baseline, SKTextAlign.Center, font, text);
        });
    }

    private static SKColor Blend(SKColor from, SKColor to, double t) => new(
        (byte)(from.Red + (to.Red - from.Red) * t),
        (byte)(from.Green + (to.Green - from.Green) * t),
        (byte)(from.Blue + (to.Blue - from.Blue) * t));

    /// <summary>Where each window was last placed, so a frame that only redraws it doesn't place it again.</summary>
    private readonly Dictionary<nint, (int X, int Y, int Width, int Height)> _placed = [];

    /// <summary>
    /// Moves the window, sets its overall opacity and draws into it. Pixels left in the key colour
    /// are see-through. Layered windows keep what is drawn into them, so no WM_PAINT is needed.
    /// </summary>
    /// <remarks>
    /// The window is placed only when it shows or moves, not on every frame: each SetWindowPos makes
    /// Windows send a mouse move to the window under the pointer, wherever the overlay is, and at
    /// 25 frames a second the app being typed into got a stream of them for as long as the log showed
    /// (17 a second into a text box, measured 2026-10-28), with the pointer still.
    /// </remarks>
    private void Paint(nint hwnd, int x, int y, int width, int height, byte opacity, Action<SKCanvas> draw)
    {
        if (!IsWindowVisible(hwnd) || !_placed.TryGetValue(hwnd, out var at) || at != (x, y, width, height))
        {
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _placed[hwnd] = (x, y, width, height);
        }
        SetLayeredWindowAttributes(hwnd, ColorKey, opacity, LWA_COLORKEY | LWA_ALPHA);
        var target = GetDC(hwnd);
        var memory = CreateCompatibleDC(target);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, // top-down
            biPlanes = 1, biBitCount = 32,
        };
        var bitmap = CreateDIBSection(target, ref header, 0, out var bits, 0, 0);
        var old = SelectObject(memory, bitmap);
        try
        {
            using (var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), bits, width * 4))
            {
                surface.Canvas.Clear(new SKColor(0xFF, 0x00, 0xFF)); // = ColorKey
                draw(surface.Canvas);
                surface.Canvas.Flush();
            }
            BitBlt(target, 0, 0, width, height, memory, 0, 0, SRCCOPY);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(hwnd, target);
        }
    }
}
