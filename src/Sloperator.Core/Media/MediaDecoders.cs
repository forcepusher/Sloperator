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
        // Animated GIFs through Skia, between the two: Windows plays none, and ffmpeg may not be there.
        return platform.Media is { } os ? new FallbackDecoder(os, new GifDecoder(), ffmpeg) : new FallbackDecoder(new GifDecoder(), ffmpeg);
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
