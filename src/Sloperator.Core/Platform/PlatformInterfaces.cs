using System.Diagnostics;

namespace Sloperator.Core.Platform;

public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>A captured frame in 32-bit BGRA, top-down rows.</summary>
public sealed class RawImage(int width, int height, byte[] bgra)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Bgra { get; } = bgra;
    public int Stride => Width * 4;

    /// <summary>A copy of a rectangle of this image, in its own pixels.</summary>
    public RawImage Crop(int x, int y, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(Bgra, (y + row) * Stride + x * 4, bgra, row * width * 4, width * 4);
        return new(width, height, bgra);
    }
}

public enum MouseButton { Left, Right, Middle }

/// <summary>Screen capture in physical pixels, without Sloperator's own windows where the OS allows.</summary>
public interface IScreenCapture
{
    /// <summary>All monitors in physical pixels, the primary first, in a stable order.</summary>
    IReadOnlyList<ScreenRect> GetScreens();

    RawImage Capture(ScreenRect region, bool includeCursor);

    /// <summary>
    /// The visible top-level windows of other programs, the frontmost first: each one's title and
    /// where it shows on screen (physical pixels, without the invisible borders Windows puts around it).
    /// </summary>
    IReadOnlyList<ScreenWindow> GetWindows() => [];

    /// <summary>
    /// Puts <paramref name="window"/> over the other windows without making it the active one, so a
    /// capture of where it is shows all of it; nothing where the OS has no way.
    /// </summary>
    void BringToFront(ScreenWindow window) { }
}

/// <summary>
/// A program's window as it shows on screen (see <see cref="IScreenCapture.GetWindows"/>), and the
/// OS's own handle to it (an HWND on Windows; 0 for none), and the name of the program it is
/// (its exe's, without .exe: "explorer" for File Explorer's; null where it can't be read).
/// </summary>
public readonly record struct ScreenWindow(string Title, ScreenRect Bounds, nint Handle = 0, string? Program = null);

/// <summary>Synthetic mouse and keyboard input. Coordinates are physical screen pixels.</summary>
public interface IInputController
{
    (int X, int Y) GetCursorPosition();
    void MoveMouse(int x, int y);
    void MouseDown(MouseButton button);
    void MouseUp(MouseButton button);
    void Click(MouseButton button, int count);

    /// <summary>Scrolls by whole wheel notches. Positive <paramref name="dy"/> scrolls down, positive <paramref name="dx"/> scrolls right.</summary>
    void Scroll(int dx, int dy);

    /// <summary>Types literal text (Unicode-safe, independent of keyboard layout).</summary>
    void TypeText(string text);

    /// <summary>Presses a chord such as ["ctrl", "shift", "s"] and releases it in reverse order.</summary>
    void PressKeys(IReadOnlyList<string> keys);

    /// <summary>
    /// Presses keys down and keeps them down until the result is disposed, then releases them in
    /// reverse order: modifiers held through a click or drag, or a key held for a while in a game.
    /// </summary>
    IDisposable HoldKeys(IReadOnlyList<string> keys);

    /// <summary>Returns false for key names the platform does not understand.</summary>
    bool IsKnownKey(string key);
}

/// <summary>
/// Sloperator's own window. It stays on screen while the agent works, so it is kept out of the
/// agent's way: left out of its screenshots by <see cref="IScreenCapture"/> where the OS allows,
/// and pushed behind other windows for the moment an action lands on it: all of Sloperator's
/// windows (every chat window, the viewer, Video editor, a dialog).
/// </summary>
public interface IHostWindow
{
    /// <summary>If any of the points lie on the window, sends it behind other windows until the result is disposed.</summary>
    IDisposable StepAside(params (int X, int Y)[] points);

    /// <summary>If the window has keyboard focus, hands focus to the window underneath it.</summary>
    void YieldFocus();
}

public sealed class NoHostWindow : IHostWindow
{
    public static readonly NoHostWindow Instance = new();
    public IDisposable StepAside(params (int X, int Y)[] points) => Nothing.Instance;
    public void YieldFocus() { }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Shows the user what the agent just did with the mouse and keyboard: a short log in a screen
/// corner that fades out, and a marker where it clicked. It never takes focus or clicks, and
/// it is invisible to screenshots so the model never sees it.
/// </summary>
public interface IInputOverlay
{
    bool Enabled { get; set; }

    /// <summary>How long a log line stays on screen, fade-out included.</summary>
    TimeSpan Duration { get; set; }

    /// <summary>The click marker's colour, the app's accent; it gets a dark outline to show on any background.</summary>
    (byte R, byte G, byte B) MarkerColor { get; set; }

    /// <param name="text">What happened, e.g. "Left click" or "Keys: Ctrl+S".</param>
    /// <param name="point">Where on the screen (physical pixels), for a marker; null for keyboard input.</param>
    void Show(string text, (int X, int Y)? point = null);

    /// <summary>
    /// A thin frame in the marker colour around <paramref name="screen"/>, which tells the user an
    /// agent is at work there; null takes it away. A later owner's frame takes the place of an
    /// earlier one's, and only the owner of the frame shown takes it away.
    /// </summary>
    void Frame(object owner, ScreenRect? screen) { }
}

public sealed class NoInputOverlay : IInputOverlay
{
    public static readonly NoInputOverlay Instance = new();
    public bool Enabled { get; set; }
    public TimeSpan Duration { get; set; }
    public (byte R, byte G, byte B) MarkerColor { get; set; }
    public void Show(string text, (int X, int Y)? point = null) { }
}

/// <summary>
/// The user's own mouse and keyboard, which the agent shares with them: its actions wait until the
/// user leaves them alone, lock them for the moment an action takes, and look again when the user
/// changed the screen since the model saw it. Sloperator's own input doesn't count.
/// </summary>
public interface IUserInput
{
    /// <summary>Watches the user's input until disposed: for a run that works on the desktop.</summary>
    IDisposable Watch();

    /// <summary>How long since the user last moved the mouse or pressed anything, locked out or not.</summary>
    TimeSpan SinceLastInput { get; }

    /// <summary>Whether the user holds a key or a mouse button down.</summary>
    bool Holding { get; }

    /// <summary>Whether the mouse pointer is on one of Sloperator's own windows, where Stop is.</summary>
    bool OnSloperator { get; }

    /// <summary>How many times the user pressed a key or a mouse button or turned the wheel, locked out or not; moves don't count.</summary>
    long Presses { get; }

    /// <summary>
    /// Locks the user's mouse and keyboard until disposed: what they do goes nowhere, but for
    /// Pause/Break, which ends the lock too, Ctrl+Alt+Del, which Windows keeps for itself, and
    /// presses on Sloperator's own windows, so that Stop always takes a click.
    /// </summary>
    /// <param name="mouse">Whether the user's mouse stops moving too; an action that only presses keys leaves it free, to get to Stop.</param>
    IDisposable Lock(bool mouse = true);
}

public sealed class NoUserInput : IUserInput
{
    public static readonly NoUserInput Instance = new();
    public IDisposable Watch() => Nothing.Instance;
    public TimeSpan SinceLastInput => TimeSpan.MaxValue;
    public bool Holding => false;
    public bool OnSloperator => false;
    public long Presses => 0;
    public IDisposable Lock(bool mouse = true) => Nothing.Instance;

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// A system-wide stop key (Pause/Break) that works whatever window has focus.
/// </summary>
public interface IStopKey
{
    /// <summary>
    /// Calls <paramref name="onPressed"/> (on any thread) when the user presses the key. It returns
    /// true if it stopped something, in which case the press is swallowed, with any keys pressed
    /// along with it; otherwise the key goes through to the focused app as usual. With
    /// <paramref name="onShiftPressed"/>, a press with Shift held goes to it instead, by the same rules.
    /// </summary>
    IDisposable Watch(Func<bool> onPressed, Func<bool>? onShiftPressed = null);
}

public sealed class NoStopKey : IStopKey
{
    public static readonly NoStopKey Instance = new();
    public IDisposable Watch(Func<bool> onPressed, Func<bool>? onShiftPressed = null) => new Nothing();

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>Everything OS-specific the agent needs. One implementation per platform.</summary>
public interface IPlatformServices
{
    string Name { get; }
    IScreenCapture Screen { get; }
    IInputController Input { get; }
    IStopKey StopKey { get; }

    /// <summary>The user's own mouse and keyboard; none to watch where the OS has no way.</summary>
    IUserInput UserInput => NoUserInput.Instance;

    IHostWindow HostWindow { get; }

    IInputOverlay InputOverlay { get; }

    /// <summary>Start info for running one command string in the platform shell (no redirection set).</summary>
    ProcessStartInfo CreateShellStartInfo(string command);

    /// <summary>The OS's own video and sound decoders; null where there are none (ffmpeg stands in).</summary>
    Media.IMediaDecoder? Media => null;

    /// <summary>A way to play sound through the speakers, for the chat's players; null where there is none.</summary>
    Media.IAudioOutput? CreateAudioOutput() => null;

    /// <summary>The OS's own notifications, for "Notify me when done"; null where there are none.</summary>
    INotifications? Notifications => null;

    /// <summary>The OS's own text recognition, for find_text; null where there is none.</summary>
    ITextReader? Text => null;
}

/// <summary>The OS's own notifications (toasts on Windows): a title and a line, one at a time per tag.</summary>
public interface INotifications
{
    /// <summary>
    /// Shows a notification in place of the one shown under <paramref name="tag"/> before, with
    /// <paramref name="iconFile"/> as the app's picture. <paramref name="clicked"/> runs on a thread
    /// of the OS's when the user clicks it. False when the OS wouldn't show it; nothing is thrown.
    /// </summary>
    bool Show(string tag, string title, string text, string? iconFile, Action clicked);

    /// <summary>Takes down the notification shown under <paramref name="tag"/>, if it is still there.</summary>
    void Hide(string tag);

    /// <summary>Takes away what showing them left outside Sloperator's folder: the user turned them off.</summary>
    void Forget();
}

/// <summary>Fallback for operating systems without an implementation yet.</summary>
public sealed class UnsupportedPlatform : IPlatformServices, IScreenCapture, IInputController
{
    public string Name => $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} (desktop control not implemented)";
    public IScreenCapture Screen => this;
    public IInputController Input => this;
    public IHostWindow HostWindow => NoHostWindow.Instance;
    public IInputOverlay InputOverlay => NoInputOverlay.Instance;
    public IStopKey StopKey => NoStopKey.Instance;
    public ProcessStartInfo CreateShellStartInfo(string command) => new("/bin/sh") { ArgumentList = { "-c", command } };

    private static NotSupportedException Fail() => new("Desktop control is not implemented on this OS yet.");

    public IReadOnlyList<ScreenRect> GetScreens() => throw Fail();
    public RawImage Capture(ScreenRect region, bool includeCursor) => throw Fail();
    public (int X, int Y) GetCursorPosition() => throw Fail();
    public void MoveMouse(int x, int y) => throw Fail();
    public void MouseDown(MouseButton button) => throw Fail();
    public void MouseUp(MouseButton button) => throw Fail();
    public void Click(MouseButton button, int count) => throw Fail();
    public void Scroll(int dx, int dy) => throw Fail();
    public void TypeText(string text) => throw Fail();
    public void PressKeys(IReadOnlyList<string> keys) => throw Fail();
    public IDisposable HoldKeys(IReadOnlyList<string> keys) => throw Fail();
    public bool IsKnownKey(string key) => false;
}
