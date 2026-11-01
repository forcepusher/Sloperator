using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Sloperator.Core.Web;

namespace Sloperator.Platform.Windows;

/// <summary>
/// LibreOffice fetched into Sloperator's tools folder by the File inspector's Get LibreOffice, for a PC
/// with no Office to draw documents' pages (<see cref="OfficePdf"/>, which finds it there): the newest
/// stable version's Windows x86-64 .msi from The Document Foundation, checked against the SHA-256
/// published beside it, then unpacked with msiexec /a, which installs nothing: no admin, no UAC prompt,
/// nothing in Apps and features. 26.8.2 on the laptop: a 360 MB download, unpacked in 33 s to 1.6 GB.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class LibreOfficeFetch
{
    /// <summary>The Document Foundation's folder of stable versions, a folder for each ("26.8.2/").</summary>
    public static readonly Uri Stable = new("https://download.documentfoundation.org/libreoffice/stable/");

    /// <summary>How long msiexec may take to unpack it.</summary>
    private static readonly TimeSpan UnpackLimit = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Fetches LibreOffice into <paramref name="tools"/>\LibreOffice and returns its soffice.exe;
    /// <paramref name="progress"/> hears how it goes. Unpacked beside it first and then renamed, so a
    /// half-unpacked one is never taken for a whole one. Throws <see cref="InvalidOperationException"/>
    /// or <see cref="DownloadException"/> saying why it couldn't.
    /// </summary>
    public static async Task<string> GetAsync(string tools, Action<string> progress, CancellationToken ct)
    {
        progress("Finding LibreOffice's newest version…");
        var version = Newest(await TextAsync(Stable, ct).ConfigureAwait(false))
            ?? throw new InvalidOperationException($"{Stable} lists no LibreOffice version.");
        var name = $"LibreOffice_{version}_Win_x86-64.msi";
        var url = new Uri(Stable, $"{version}/win/x86_64/{name}");
        // "<hash> *LibreOffice_26.8.2_Win_x86-64.msi"
        var sha = (await TextAsync(new Uri(url + ".sha256"), ct).ConfigureAwait(false)).Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (sha is not { Length: 64 })
            throw new InvalidOperationException($"{name}'s checksum couldn't be read from {url}.sha256.");
        var msi = Path.Combine(tools, name);
        // The link sends each request on to one of many mirrors; one refusing (429 from a mirror the laptop
        // had fetched it from shortly before, 2 November 2026) is passed by asking again, which picks another.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Downloader.DownloadAsync(url, msi, sha, progress, ct).ConfigureAwait(false);
                break;
            }
            catch (DownloadException ex) when (attempt < 3)
            {
                progress($"A download mirror refused it, trying another… ({ex.Message.TrimEnd('.')})");
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct).ConfigureAwait(false);
            }
        }

        progress("Unpacking LibreOffice… (about a minute)");
        var target = Path.Combine(tools, "LibreOffice");
        var unpacking = target + ".part";
        if (Directory.Exists(unpacking))
            Directory.Delete(unpacking, recursive: true);
        await UnpackAsync(msi, unpacking).ConfigureAwait(false);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.Move(unpacking, target);
        try { File.Delete(msi); }
        catch (IOException) { }
        return OfficePdf.LibreOffice(tools) ?? throw new InvalidOperationException($"{name} unpacked without soffice.exe in it.");
    }

    /// <summary>The highest version a listing of <see cref="Stable"/> links to; null with none.</summary>
    internal static string? Newest(string listing) =>
        VersionFolder().Matches(listing).Select(m => m.Groups[1].Value).MaxBy(Version.Parse);

    [GeneratedRegex(@"href=""(\d+\.\d+\.\d+(?:\.\d+)?)/""")]
    private static partial Regex VersionFolder();

    private static async Task<string> TextAsync(Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            return await Downloader.Http.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DownloadException($"{url.Host} didn't answer within a minute.");
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException($"{url} can't be read: {ex.Message}");
        }
    }

    /// <summary>
    /// The .msi's files copied out into <paramref name="folder"/> (an administrative install). Not stopped
    /// part way: msiexec's work is done by the Windows Installer service, which killing it wouldn't stop.
    /// </summary>
    private static async Task UnpackAsync(string msi, string folder)
    {
        var start = new ProcessStartInfo("msiexec.exe")
        {
            // Quoted by hand: msiexec wants the quotes around the property's value, not the whole argument.
            Arguments = $"/a \"{msi}\" /qn TARGETDIR=\"{folder}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("msiexec didn't start.");
        using var limit = new CancellationTokenSource(UnpackLimit);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException($"LibreOffice took longer than {UnpackLimit.TotalMinutes:0} minutes to unpack.");
        }
        if (process.ExitCode is not (0 or 3010))
            throw new InvalidOperationException(process.ExitCode == 1618
                ? "LibreOffice couldn't be unpacked while another installation is running: try again once it has finished."
                : $"LibreOffice couldn't be unpacked: msiexec ended with code {process.ExitCode}.");
    }
}
