using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sloperator.Core.Platform;
using static Sloperator.Platform.Windows.Native;

namespace Sloperator.Platform.Windows;

/// <summary>
/// The user's own mouse and keyboard, through low-level hooks on a message-loop thread of their own,
/// there while a run watches (<see cref="Watch"/>), not for as long as Sloperator runs: every mouse
/// move on the PC passes through them. What Sloperator sends is tagged
/// (<see cref="WindowsInputController.InputTag"/>), so anything else is the user's, or another
/// program's that sends input, which counts the same. Locked (<see cref="Lock"/>), the user's moves,
/// clicks, wheel and keys go nowhere, but Pause/Break, which ends the lock and goes on to the stop key
/// (<see cref="WindowsStopKey"/>). SetCursorPos, which moves the agent's mouse, isn't input and
/// passes no hook, so it moves the mouse while the user's can't.
/// </summary>
/// <remarks>
/// Not BlockInput, which takes administrator rights. A press swallowed is swallowed until let go
/// (its repeats and its key-up), and a press that went through before the lock goes through until
/// let go, so the lock never leaves a key or a button stuck down in Windows.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUserInput : IUserInput
{
    private static readonly IDisposable NotLocked = new Locked(null);

    private readonly System.Threading.Lock _gate = new();
    private Hooks? _hooks;
    private int _watchers;
    private long _lastInput = Environment.TickCount64; // Environment.TickCount64 at the user's last input
    private long _presses;
    private Locked? _lock;

    /// <summary>The keys and buttons the user pressed and hasn't let go of, as far as the hooks saw, and when each went down.</summary>
    private readonly ConcurrentDictionary<uint, long> _held = new();

    public IDisposable Watch()
    {
        lock (_gate)
        {
            if (_watchers++ == 0)
            {
                Seed();
                _hooks = new Hooks(this);
            }
        }
        return new Unwatch(this);
    }

    /// <summary>
    /// When the last input came before the hooks were there: Sloperator's own too, which can't be told
    /// apart then. What was held down already isn't asked for: a key Windows takes for stuck down
    /// would hold the agent for good, and a key held down repeats, so it doesn't look left alone.
    /// </summary>
    private void Seed()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (GetLastInputInfo(ref info))
            Volatile.Write(ref _lastInput, Environment.TickCount64 - unchecked((uint)Environment.TickCount - info.dwTime));
    }

    public TimeSpan SinceLastInput => TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - Volatile.Read(ref _lastInput)));

    public bool Holding
    {
        get
        {
            // Windows has the key down a moment after the hook saw it go down; a key-up the hooks
            // didn't see (let go over an administrator's window) leaves none down.
            var now = Environment.TickCount64;
            return _held.Any(k => now - k.Value < 250 || IsHeldDown(k.Key));
        }
    }

    public long Presses => Interlocked.Read(ref _presses);

    public IDisposable Lock()
    {
        var locked = new Locked(this);
        // Locked already: the outer lock holds it.
        return Interlocked.CompareExchange(ref _lock, locked, null) is null ? locked : NotLocked;
    }

    /// <summary>A key event of the user's, on the hooks' thread: whether it goes no further.</summary>
    private bool OnKey(uint vk, bool down, HashSet<uint> swallowed)
    {
        var now = Environment.TickCount64;
        Volatile.Write(ref _lastInput, now);
        if (!down)
        {
            _held.TryRemove(vk, out _);
            // Never the key-up of a key Windows has down (its key-down got past after all): it would stay stuck.
            return swallowed.Remove(vk) && !IsDown(vk);
        }
        Interlocked.Increment(ref _presses);
        if (vk is VK_PAUSE or VK_CANCEL)
        {
            Volatile.Write(ref _lock, null); // the stop key: the user has it all back at once
            return false;
        }
        if (swallowed.Contains(vk))
            return true; // a key swallowed, held down and repeating
        if (Volatile.Read(ref _lock) is null)
        {
            _held.TryAdd(vk, now);
            return false;
        }
        // A key Windows has down since before the lock repeats: swallowed, but its key-up goes through.
        if (!IsDown(vk))
            swallowed.Add(vk);
        return true;
    }

    /// <summary>A mouse event of the user's, on the hooks' thread: whether it goes no further.</summary>
    private bool OnMouse(uint message, uint mouseData, HashSet<uint> swallowed)
    {
        var now = Environment.TickCount64;
        Volatile.Write(ref _lastInput, now);
        var locked = Volatile.Read(ref _lock) is not null;
        if (message is WM_MOUSEMOVE)
            return locked;
        if (message is WM_MOUSEWHEEL or WM_MOUSEHWHEEL)
        {
            Interlocked.Increment(ref _presses);
            return locked;
        }
        if (Button(message, mouseData) is not var (vk, down))
            return false;
        if (!down)
        {
            _held.TryRemove(vk, out _);
            return swallowed.Remove(vk);
        }
        Interlocked.Increment(ref _presses);
        if (!locked)
        {
            _held.TryAdd(vk, now);
            return false;
        }
        swallowed.Add(vk);
        return true;
    }

    private static (uint Vk, bool Down)? Button(uint message, uint mouseData) => message switch
    {
        WM_LBUTTONDOWN => (VK_LBUTTON, true),
        WM_LBUTTONUP => (VK_LBUTTON, false),
        WM_RBUTTONDOWN => (VK_RBUTTON, true),
        WM_RBUTTONUP => (VK_RBUTTON, false),
        WM_MBUTTONDOWN => (VK_MBUTTON, true),
        WM_MBUTTONUP => (VK_MBUTTON, false),
        WM_XBUTTONDOWN or WM_XBUTTONUP => ((mouseData >> 16) == 1 ? VK_XBUTTON1 : VK_XBUTTON2, message == WM_XBUTTONDOWN),
        _ => null,
    };

    /// <summary>Whether Windows has the key down; in a hook, as it was before the event at hand.</summary>
    private static bool IsDown(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;

    // GetAsyncKeyState reads the buttons as they are, not as Windows swaps them for the left-handed.
    private static bool IsHeldDown(uint vk) => vk is VK_LBUTTON or VK_RBUTTON ? IsDown(VK_LBUTTON) || IsDown(VK_RBUTTON) : IsDown(vk);

    private sealed class Locked(WindowsUserInput? owner) : IDisposable
    {
        public void Dispose()
        {
            if (owner is not null)
                Interlocked.CompareExchange(ref owner._lock, null, this);
        }
    }

    private sealed class Unwatch(WindowsUserInput owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0)
                return;
            lock (owner._gate)
            {
                if (--owner._watchers > 0)
                    return;
                owner._hooks?.Dispose();
                owner._hooks = null;
                Volatile.Write(ref owner._lock, null);
            }
        }
    }

    private sealed class Hooks : IDisposable
    {
        private readonly WindowsUserInput _owner;
        private readonly LowLevelHookProc _keyProc, _mouseProc; // kept alive for as long as the hooks are installed
        // Swallowed until let go; used on the hooks' thread only.
        private readonly HashSet<uint> _keys = [], _buttons = [];
        private uint _threadId;

        public Hooks(WindowsUserInput owner)
        {
            _owner = owner;
            _keyProc = OnKey;
            _mouseProc = OnMouse;
            using var ready = new ManualResetEventSlim();
            new Thread(() => Run(ready)) { IsBackground = true, Name = "Sloperator user input" }.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }

        private void Run(ManualResetEventSlim ready)
        {
            _threadId = GetCurrentThreadId();
            var module = GetModuleHandle(null);
            var keyboard = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
            var mouse = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            ready.Set();
            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            UnhookWindowsHookEx(mouse);
            UnhookWindowsHookEx(keyboard);
        }

        private nint OnKey(int code, nint wParam, nint lParam)
        {
            if (code >= 0)
            {
                var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                try
                {
                    if (key.dwExtraInfo != WindowsInputController.InputTag && _owner.OnKey(key.vkCode, (uint)wParam is WM_KEYDOWN or WM_SYSKEYDOWN, _keys))
                        return 1;
                }
                catch { } // a hook callback must never throw into the system
            }
            return CallNextHookEx(0, code, wParam, lParam);
        }

        private nint OnMouse(int code, nint wParam, nint lParam)
        {
            if (code >= 0)
            {
                var mouse = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                try
                {
                    if (mouse.dwExtraInfo != WindowsInputController.InputTag && _owner.OnMouse((uint)wParam, mouse.mouseData, _buttons))
                        return 1;
                }
                catch { }
            }
            return CallNextHookEx(0, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_threadId != 0)
                PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        }
    }
}
