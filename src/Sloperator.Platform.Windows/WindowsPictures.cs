using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Sloperator.Core.Platform;
using static Sloperator.Platform.Windows.ComAbi;

namespace Sloperator.Platform.Windows;

/// <summary>
/// Pictures through Windows' own decoders (the Windows Imaging Component), for the kinds Skia has none for: TIFF
/// (a scanner's, a print shop's), a game's DDS textures, JPEG XR, and whatever the PC has extensions for: a
/// phone's HEIC, AVIF, a camera's raw files. Decoded from memory, so no handle to the file is held, and called
/// through the interfaces' tables like the rest (<see cref="ComAbi"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsPictures
{
    private static readonly Guid ImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid IImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    /// <summary>Blue, green, red and clearness, a byte each, the colours already scaled by the clearness.</summary>
    private static readonly Guid Bgra = new("6fddc324-4e03-4bfe-b185-3d77768dc910");

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* obj);

    /// <summary>The most pixels a picture is decoded with here (a 268-megapixel one is a gigabyte).</summary>
    private const long MostPixels = 268_000_000;

    /// <summary>
    /// The first picture in the bytes of a file, as BGRA with its colours scaled by its clearness; null when Windows
    /// has no decoder for it (or the bytes are no picture), or it is larger than is decoded here.
    /// </summary>
    public static RawImage? Decode(byte[] data)
    {
        if (data.Length < 8)
            return null;
        Start();
        nint factory = 0, stream = 0, decoder = 0, frame = 0, converter = 0;
        try
        {
            var (clsid, iid) = (ImagingFactory, IImagingFactory);
            if (CoCreateInstance(&clsid, 0, 1 /* in this process */, &iid, &factory) < 0)
                return null;
            fixed (byte* bytes = data)
            {
                // IWICImagingFactory: CreateDecoderFromStream 4, CreateFormatConverter 10, CreateStream 14. IWICStream.InitializeFromMemory 16.
                if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(factory, 14))(factory, &stream) < 0
                    || ((delegate* unmanaged[Stdcall]<nint, byte*, uint, int>)Slot(stream, 16))(stream, bytes, (uint)data.Length) < 0
                    || ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int, nint*, int>)Slot(factory, 4))(factory, stream, null, 0, &decoder) < 0)
                    return null;
                // IWICBitmapDecoder.GetFrame 13; IWICFormatConverter.Initialize 8; IWICBitmapSource.GetSize 3, CopyPixels 7.
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(decoder, 13))(decoder, 0, &frame) < 0
                    || ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(factory, 10))(factory, &converter) < 0)
                    return null;
                var format = Bgra;
                if (((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int, nint, double, int, int>)Slot(converter, 8))(converter, frame, &format, 0, 0, 0.0, 0) < 0)
                    return null;
                uint width, height;
                if (((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(converter, 3))(converter, &width, &height) < 0
                    || width == 0 || height == 0 || (long)width * height > MostPixels)
                    return null;
                var pixels = new byte[(long)width * height * 4];
                fixed (byte* into = pixels)
                    if (((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, byte*, int>)Slot(converter, 7))(converter, 0, width * 4, (uint)pixels.Length, into) < 0)
                        return null;
                return new RawImage((int)width, (int)height, pixels);
            }
        }
        catch (Exception ex) when (ex is COMException or OutOfMemoryException or OverflowException)
        {
            return null;
        }
        finally
        {
            Release(converter);
            Release(frame);
            Release(decoder);
            Release(stream);
            Release(factory);
        }
    }
}
