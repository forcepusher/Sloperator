using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sloperator.Core.Platform;
using static Sloperator.Platform.Windows.Native;

namespace Sloperator.Platform.Windows;

/// <summary>
/// The screen with some windows left out, through the Magnification API's window filter: how
/// Sloperator's screenshots leave its own windows out. Marking them excluded from capture
/// (WDA_EXCLUDEFROMCAPTURE) for the moment of each screenshot, as they were, is how apps protect
/// what they show, and NVIDIA's Instant Replay stopped recording at every screenshot: its
/// CaptureCore.log said "Protected Content is running by PID … Sloperator.exe", then StopDVRCapture
/// (the user, 2026-11-01: "Shadowplay screen recording got constantly disabled during the run").
/// The magnifier draws the desktop without them into a window of ours that is never shown, and
/// hands its pixels over; other programs' captures see nothing change.
/// </summary>
/// <remarks>
/// The magnifier lives on a thread of its own with a message loop, made once: made and gone with
/// each picture, a 2560×1080 one took 236 ms (GDI's 42).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static unsafe partial class MagnifierCapture
{
    private const uint WM_APP_CAPTURE = WM_APP + 2;

    private static readonly object Starting = new();
    private static readonly ConcurrentQueue<Job> Jobs = new();
    private static bool _started, _ready;
    private static uint _threadId;
    private static nint _host, _magnifier;

    private sealed class Job(ScreenRect region, nint[] leftOut)
    {
        public ScreenRect Region { get; } = region;
        public nint[] LeftOut { get; } = leftOut;
        public byte[]? Pixels { get; set; }
        public ManualResetEventSlim Done { get; } = new();
    }

    /// <summary>
    /// The region's pixels (BGRA, rows from the top, alpha undefined) as the screen shows them without
    /// <paramref name="leftOut"/> (top-level windows); null where the magnifier isn't there or failed.
    /// </summary>
    public static byte[]? Capture(ScreenRect region, IReadOnlyList<nint> leftOut)
    {
        if (region.Width <= 0 || region.Height <= 0 || !Ready())
            return null;
        // Not disposed: past the wait, the magnifier's thread may still set it.
        var job = new Job(region, leftOut.ToArray());
        Jobs.Enqueue(job);
        return PostThreadMessage(_threadId, WM_APP_CAPTURE, 0, 0) && job.Done.Wait(TimeSpan.FromSeconds(10)) ? job.Pixels : null;
    }

    /// <summary>Starts the magnifier's thread the first time; whether the magnifier is there.</summary>
    private static bool Ready()
    {
        lock (Starting)
        {
            if (_started)
                return _ready;
            _started = true;
            using var up = new ManualResetEventSlim();
            new Thread(() => Run(up)) { IsBackground = true, Name = "Magnifier capture" }.Start();
            up.Wait();
            return _ready;
        }
    }

    private static void Run(ManualResetEventSlim up)
    {
        EnsurePhysicalPixels();
        _threadId = GetCurrentThreadId();
        if (MagInitialize())
        {
            var instance = GetModuleHandle(null);
            // Never shown, and at the primary screen's corner: put on the other screen, the magnifier
            // drew the primary's pixels for a region of it (checked on two screens, 2026-11-01).
            _host = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "Static", "", WS_POPUP, 0, 0, 1, 1, 0, 0, instance, 0);
            _magnifier = _host == 0 ? 0 : CreateWindowEx(0, "Magnifier", "", WS_CHILD | WS_VISIBLE, 0, 0, 1, 1, _host, 0, instance, 0);
            _ready = _magnifier != 0 &&
                     MagSetImageScalingCallback(_magnifier, (nint)(delegate* unmanaged[Stdcall]<nint, nint, MAGIMAGEHEADER, nint, MAGIMAGEHEADER, RECT, RECT, nint, int>)&OnImage);
        }
        up.Set();
        if (!_ready)
        {
            if (_host != 0)
                DestroyWindow(_host);
            MagUninitialize();
            return;
        }

        // A loop, not just a wait for jobs: Windows sends its windows messages (a setting changed,
        // broadcast to every window), and a thread that never took them would hold up their sender.
        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.hwnd == 0 && msg.message == WM_APP_CAPTURE)
            {
                while (Jobs.TryDequeue(out var job))
                    Take(job);
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static void Take(Job job)
    {
        var region = job.Region;
        try
        {
            // The magnifier's window the size of the region: one pixel of the screen to one of the picture.
            SetWindowPos(_host, 0, 0, 0, region.Width, region.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOMOVE);
            SetWindowPos(_magnifier, 0, 0, 0, region.Width, region.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOMOVE);
            _into = new Target(region.Width, region.Height);
            var source = new RECT { Left = region.X, Top = region.Y, Right = region.X + region.Width, Bottom = region.Y + region.Height };
            // It calls back from within MagSetWindowSource.
            if (MagSetWindowFilterList(_magnifier, MW_FILTERMODE_EXCLUDE, job.LeftOut.Length, job.LeftOut) && MagSetWindowSource(_magnifier, source))
                job.Pixels = _into.Pixels;
        }
        finally
        {
            _into = null;
            job.Done.Set();
        }
    }

    /// <summary>Where the picture being taken goes.</summary>
    private static Target? _into;

    private sealed class Target(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte[]? Pixels { get; set; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnImage(nint hwnd, nint source, MAGIMAGEHEADER header, nint destination, MAGIMAGEHEADER destinationHeader, RECT unclipped, RECT clipped, nint dirty)
    {
        if (_into is not { } into || header.width != into.Width || header.height != into.Height || header.stride < into.Width * 4)
            return 0;
        var row = into.Width * 4;
        var pixels = new byte[row * into.Height];
        fixed (byte* to = pixels)
            for (var y = 0; y < into.Height; y++)
                Buffer.MemoryCopy((byte*)source + header.offset + (long)y * header.stride, to + (long)y * row, row, row);
        into.Pixels = pixels;
        return 1;
    }

    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    private const uint MW_FILTERMODE_EXCLUDE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MAGIMAGEHEADER
    {
        public uint width;
        public uint height;
        public Guid format;
        public uint stride;
        public uint offset;
        public nuint cbSize;
    }

    [LibraryImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MagInitialize();

    [LibraryImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MagUninitialize();

    [LibraryImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MagSetWindowSource(nint hwnd, RECT rect);

    [LibraryImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MagSetWindowFilterList(nint hwnd, uint mode, int count, [In] nint[] list);

    [LibraryImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MagSetImageScalingCallback(nint hwnd, nint callback);
}
