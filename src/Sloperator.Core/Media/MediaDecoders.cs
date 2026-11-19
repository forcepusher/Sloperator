using Sloperator.Core.Platform;

namespace Sloperator.Core.Media;

public static class MediaDecoders
{
    /// <summary>
    /// The OS's own decoders, and ffmpeg for what they can't read: on the PATH, among the agent's
    /// portable tools, or the copy that comes with ComfyUI.
    /// </summary>
    public static IMediaDecoder Create(IPlatformServices platform, Settings.AppPaths paths, Comfy.ComfyHub? comfy)
    {
        var ffmpeg = new FfmpegDecoder(() => FfmpegFolders(paths, comfy));
        // Pictures of kinds Skia reads none of go to the OS's own decoders from here on.
        PictureFile.Os ??= platform.Pictures;
        // Animated GIFs through Skia, between the two: Windows plays none, and ffmpeg may not be there.
        return platform.Media is { } os ? new FallbackDecoder(os, new GifDecoder(), ffmpeg) : new FallbackDecoder(new GifDecoder(), ffmpeg);
    }

    /// <summary>
    /// Why a video file's picture can't be read here, in a sentence for the user and the model: what the file says it
    /// is coded as, and what would read it. Windows reads H.264, MPEG-4, WMV and Motion JPEG on its own; a phone's HEVC,
    /// a web video's VP9 or AV1 and a camera's ProRes need a decoder added, or ffmpeg.
    /// </summary>
    public static string WhyNoPicture(string path, IMediaDecoder decoder)
    {
        var name = Path.GetFileName(path);
        var codec = Mp4Tracks.Read(path)?.FirstOrDefault(t => t.IsVideo)?.Codec.Trim().ToLowerInvariant() ?? "";
        var kind = codec.Length > 0 ? Mp4Tracks.Named(codec) : Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".webm" => "VP9",
            ".mpg" or ".mpeg" or ".vob" or ".m2ts" or ".mts" => "MPEG-2",
            ".flv" => "Flash",
            ".ogv" => "Theora",
            _ => "",
        };
        var what = kind switch
        {
            "H.264" => $"{name} is H.264 video of a kind Windows doesn't decode (ten bits a colour, or colour at full detail).",
            "" => $"{name} holds no picture this Windows can read.",
            _ => $"{name} is {kind} video, which this Windows has no decoder for.",
        };
        if (decoder.Ffmpeg is not null)
            return what + " ffmpeg couldn't read it either: the file may be damaged.";
        var also = kind switch
        {
            "HEVC (H.265)" => ", and so would Microsoft's HEVC Video Extensions from the Store",
            "AV1" => ", and so would Microsoft's AV1 Video Extension from the Store",
            "VP9" => ", and so would Microsoft's VP9 Video Extensions from the Store",
            "MPEG-2" => ", and so would Microsoft's MPEG-2 Video Extension from the Store",
            _ => "",
        };
        return what + $" ffmpeg in Sloperator's SloperatorData\\tools would read it{also}.";
    }

    private static IEnumerable<string> FfmpegFolders(Settings.AppPaths paths, Comfy.ComfyHub? comfy)
    {
        yield return paths.ToolsDir;
        if (comfy?.SavedInstall is { } install)
        {
            yield return Path.Combine(install.Root, "python_embeded", "Lib", "site-packages", "imageio_ffmpeg", "binaries");
            yield return Path.Combine(install.Root, "venv", "Lib", "site-packages", "imageio_ffmpeg", "binaries");
            yield return Path.Combine(install.Root, ".venv", "Lib", "site-packages", "imageio_ffmpeg", "binaries");
        }
    }
}
