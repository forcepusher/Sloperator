using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Sloperator.Platform.Windows.ComAbi;

namespace Sloperator.Platform.Windows;

/// <summary>
/// What the Live preview panel asks of WebView2 that Avalonia's web view doesn't offer: a script run
/// before each page's own, a folder served under an https host, and a picture of the page. Called on
/// the ICoreWebView2 the web view hands over, through its vtable (<see cref="ComAbi"/>), on the window's
/// thread as WebView2 wants; the slots are those of WebView2.h, the same Avalonia's own interop declares.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WebView2Page
{
    private static readonly Guid ICoreWebView2_3 = new("A0D6DF20-3B92-416D-AA0C-437A9C727857");

    /// <summary>Runs <paramref name="script"/> in every document the view loads from now on, before the document's own scripts (AddScriptToExecuteOnDocumentCreated, 27).</summary>
    public static unsafe void AddStartScript(nint core, string script)
    {
        fixed (char* text = script)
            Check(((delegate* unmanaged[Stdcall]<nint, char*, nint, int>)Slot(core, 27))(core, text, 0));
    }

    /// <summary>
    /// Serves <paramref name="folder"/> at https://<paramref name="host"/>/ (ICoreWebView2_3's
    /// SetVirtualHostNameToFolderMapping, 71; a mapping of the host before is replaced, ClearVirtualHostNameToFolderMapping 72).
    /// False on a WebView2 too old to.
    /// </summary>
    public static unsafe bool Serve(nint core, string host, string folder)
    {
        nint view3;
        var iid = ICoreWebView2_3;
        if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(core, 0))(core, &iid, &view3) < 0 || view3 == 0)
            return false;
        try
        {
            fixed (char* h = host)
            fixed (char* f = folder)
            {
                _ = ((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(view3, 72))(view3, h);
                // COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_ALLOW: the page's own scripts and other pages may fetch its files.
                return ((delegate* unmanaged[Stdcall]<nint, char*, char*, int, int>)Slot(view3, 71))(view3, h, f, 1) >= 0;
            }
        }
        finally { Release(view3); }
    }

    /// <summary>
    /// The page as the view shows it, a PNG at the view's size in screen pixels (CapturePreview, 30);
    /// null when WebView2 couldn't. Completes on the window's thread, as WebView2 calls back there.
    /// </summary>
    public static unsafe Task<byte[]?> CaptureAsync(nint core)
    {
        nint stream;
        if (CreateStreamOnHGlobal(0, 1, &stream) < 0)
            return Task.FromResult<byte[]?>(null);
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref _next);
        Waiting[id] = done;
        var handler = Handler.Create(id);
        try
        {
            // COREWEBVIEW2_CAPTURE_PREVIEW_IMAGE_FORMAT_PNG.
            var hr = ((delegate* unmanaged[Stdcall]<nint, int, nint, nint, int>)Slot(core, 30))(core, 0, stream, handler);
            if (hr < 0)
            {
                Waiting.TryRemove(id, out _);
                Release(stream);
                return Task.FromResult<byte[]?>(null);
            }
        }
        finally { Release(handler); }
        return Read(done.Task, stream);
    }

    private static async Task<byte[]?> Read(Task<int> done, nint stream)
    {
        try
        {
            return await done >= 0 ? Bytes(stream) : null;
        }
        finally { Release(stream); }
    }

    /// <summary>What was written into the stream: its bytes up to where it stands (IStream's Seek, 5).</summary>
    private static unsafe byte[]? Bytes(nint stream)
    {
        ulong length;
        if (((delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)Slot(stream, 5))(stream, 0, 1 /* STREAM_SEEK_CUR */, &length) < 0 || length == 0)
            return null;
        nint memory;
        Check(GetHGlobalFromStream(stream, &memory));
        var at = GlobalLock(memory);
        try { return new ReadOnlySpan<byte>((void*)at, checked((int)length)).ToArray(); }
        finally { _ = GlobalUnlock(memory); }
    }

    private static int _next;

    /// <summary>The captures waiting for WebView2's call back, by the number their handler carries.</summary>
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<int>> Waiting = new();

    /// <summary>
    /// ICoreWebView2CapturePreviewCompletedHandler made by hand, as the toasts' handler is: a vtable of four
    /// and a count, with the number of the capture it ends.
    /// </summary>
    private unsafe struct Handler
    {
        private void** _vtable;
        private int _refs;
        private int _id;

        private static readonly Guid IUnknown = new("00000000-0000-0000-c000-000000000046");
        private static readonly Guid Completed = new("697E05E9-3D8F-45FA-96F4-8FFE1EDEDAF5");

        private static readonly void** Vtable = MakeVtable();

        private static void** MakeVtable()
        {
            var table = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*)));
            table[0] = (delegate* unmanaged[Stdcall]<Handler*, Guid*, nint*, int>)&QueryInterface;
            table[1] = (delegate* unmanaged[Stdcall]<Handler*, uint>)&AddRef;
            table[2] = (delegate* unmanaged[Stdcall]<Handler*, uint>)&ReleaseHandler;
            table[3] = (delegate* unmanaged[Stdcall]<Handler*, int, int>)&Invoke;
            return table;
        }

        public static nint Create(int id)
        {
            var handler = (Handler*)NativeMemory.Alloc((nuint)sizeof(Handler));
            handler->_vtable = Vtable;
            handler->_refs = 1;
            handler->_id = id;
            return (nint)handler;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(Handler* self, Guid* iid, nint* result)
        {
            if (*iid == IUnknown || *iid == Completed)
            {
                Interlocked.Increment(ref self->_refs);
                *result = (nint)self;
                return 0;
            }
            *result = 0;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(Handler* self) => (uint)Interlocked.Increment(ref self->_refs);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint ReleaseHandler(Handler* self)
        {
            var left = Interlocked.Decrement(ref self->_refs);
            if (left == 0)
            {
                // Released without being called (the view closed): the capture ends empty.
                if (Waiting.TryRemove(self->_id, out var waiting))
                    waiting.TrySetResult(-1);
                NativeMemory.Free(self);
            }
            return (uint)left;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Invoke(Handler* self, int errorCode)
        {
            if (Waiting.TryRemove(self->_id, out var waiting))
                waiting.TrySetResult(errorCode);
            return 0;
        }
    }

    [DllImport("ole32.dll")] private static extern unsafe int CreateStreamOnHGlobal(nint memory, int deleteOnRelease, nint* stream);
    [DllImport("ole32.dll")] private static extern unsafe int GetHGlobalFromStream(nint stream, nint* memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern int GlobalUnlock(nint memory);
}
