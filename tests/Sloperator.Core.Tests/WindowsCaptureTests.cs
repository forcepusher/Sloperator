#if WINDOWS
using System.Runtime.InteropServices;
using Sloperator.Core.Platform;
using Sloperator.Platform.Windows;
using Xunit;
using static Sloperator.Platform.Windows.Native;

namespace Sloperator.Core.Tests;

/// <summary>
/// Sloperator's own windows and its screenshots: left out of each one while it is taken, and back in
/// what screen recorders and screen sharing capture right after (there was an option for that before).
/// Puts a small red window in the screen corner for a moment.
/// </summary>
[Collection(RealScreen.Name)]
public class WindowsCaptureTests
{
    [Fact]
    public void Sloperators_windows_are_left_out_of_its_screenshots_only_while_one_is_taken()
    {
        var capture = new WindowsPlatform().Screen; // physical pixels from here on
        var region = new ScreenRect(40, 40, 160, 160);
        using var window = new RedWindow(region);

        Assert.True(RedShare(WindowsScreenCapture.CaptureNow(region, includeCursor: false)) > 0.9, "the test window isn't on screen");
        Assert.True(RedShare(capture.Capture(region, includeCursor: false)) < 0.1, "the window showed in Sloperator's screenshot");
        Assert.True(RedShare(WindowsScreenCapture.CaptureNow(region, includeCursor: false)) > 0.9, "the window stayed hidden from other programs' captures");
        Assert.True(GetWindowDisplayAffinity(window.Handle, out var affinity) && affinity == WDA_NONE);

        // One left out of capture by its own program stays that way.
        SetWindowDisplayAffinity(window.Handle, WDA_EXCLUDEFROMCAPTURE);
        Assert.True(RedShare(capture.Capture(region, includeCursor: false)) < 0.1);
        Assert.True(GetWindowDisplayAffinity(window.Handle, out affinity) && affinity == WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>
    /// Without marking the window excluded from capture even for a moment, which NVIDIA's Instant Replay
    /// takes for protected video and stops at: the magnifier draws the screen without it, on every
    /// screen, and the pointer goes on when asked for.
    /// </summary>
    [Fact]
    public void The_magnifier_leaves_the_windows_out_on_every_screen()
    {
        var capture = new WindowsPlatform().Screen;
        foreach (var screen in capture.GetScreens())
        {
            var region = new ScreenRect(screen.X + 40, screen.Y + 40, 160, 160);
            using var window = new RedWindow(region);
            Assert.True(RedShare(WindowsScreenCapture.CaptureNow(region, includeCursor: false)) > 0.9, $"the test window isn't on screen at {screen}");
            var pixels = MagnifierCapture.Capture(region, [window.Handle]);
            Assert.NotNull(pixels);
            Assert.True(RedShare(new RawImage(region.Width, region.Height, pixels)) < 0.1, $"the window showed in the magnifier's picture of {screen}");
            // The region's own pixels, not the primary screen's for another screen's region.
            var beside = new ScreenRect(region.X + 160, region.Y, 40, 160);
            Assert.True(Same(WindowsScreenCapture.CaptureNow(beside, includeCursor: false).Bgra, MagnifierCapture.Capture(beside, [window.Handle])!) > 0.9,
                $"the magnifier's picture beside the window on {screen} isn't what is there");
        }

        GetCursorPos(out var at);
        var around = new ScreenRect(at.X - 40, at.Y - 40, 80, 80);
        using (new RedWindow(around))
        {
            var plain = capture.Capture(around, includeCursor: false);
            var pointed = capture.Capture(around, includeCursor: true);
            Assert.False(plain.Bgra.AsSpan().SequenceEqual(pointed.Bgra), "no pointer was drawn on the magnifier's picture");
        }
    }

    /// <summary>The share of pixels within a few levels of each other's.</summary>
    private static double Same(byte[] a, byte[] b)
    {
        var same = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) < 8 && Math.Abs(a[i + 1] - b[i + 1]) < 8 && Math.Abs(a[i + 2] - b[i + 2]) < 8)
                same++;
        return same / (a.Length / 4.0);
    }

    private static double RedShare(RawImage image)
    {
        var red = 0;
        for (var i = 0; i < image.Bgra.Length; i += 4)
            if (image.Bgra[i] < 40 && image.Bgra[i + 1] < 40 && image.Bgra[i + 2] > 215)
                red++;
        return red / (double)(image.Width * image.Height);
    }

    /// <summary>A plain red, topmost window of this process over the region, on its own thread with a message loop.</summary>
    private sealed class RedWindow : IDisposable
    {
        private readonly Thread _thread;
        private uint _threadId;
        private readonly WndProc _proc = DefWindowProc;

        public nint Handle { get; private set; }

        public RedWindow(ScreenRect r)
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                    hInstance = GetModuleHandle(null),
                    hbrBackground = CreateSolidBrush(0x000000FF),
                    lpszClassName = "SloperatorCaptureTest",
                };
                RegisterClassEx(ref wc);
                Handle = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, wc.lpszClassName, "capture test",
                    WS_POPUP | 0x10000000 /* WS_VISIBLE */, r.X, r.Y, r.Width, r.Height, 0, 0, wc.hInstance, 0);
                ready.Set();
                while (GetMessage(out var msg, 0, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }) { IsBackground = true };
            _thread.Start();
            ready.Wait();
            Thread.Sleep(300); // painted and composed
        }

        public void Dispose()
        {
            // Its windows go with the thread.
            PostThreadMessage(_threadId, WM_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        [DllImport("gdi32.dll")]
        private static extern nint CreateSolidBrush(uint color);
    }
}

/// <summary>
/// The tests that put windows on the real screen and capture it: one at a time, with no other test
/// running. Side by side, one's window covered the other's, and Sloperator's own screenshot (which
/// leaves out every window of this process while it is taken) left the other's window out.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealScreen
{
    public const string Name = "Real screen";
}
#endif
