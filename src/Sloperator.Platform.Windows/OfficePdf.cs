using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Sloperator.Platform.Windows;

/// <summary>
/// A Word, Excel or PowerPoint file (or OpenDocument's) turned into a PDF, for capture_result's pictures
/// of documents (<see cref="Core.Documents.DocumentPictures"/>): by Office, running hidden (no window,
/// no taskbar button; Word 3 s, Excel 1.3 s on the workstation), else LibreOffice without a window.
/// Office is driven from Windows PowerShell, as the trimmed build has no COM interop. A copy of Office
/// the user has open is used as it is and left open: only a hidden one this started is quit.
/// </summary>
[SupportedOSPlatform("windows")]
public static class OfficePdf
{
    /// <summary>How long a document may take to turn into a PDF before it is given up on.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private enum App { Word, Excel, PowerPoint }

    private static App? AppFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" or ".docm" or ".doc" or ".dotx" or ".dotm" or ".dot" or ".rtf" or ".odt" => App.Word,
        ".xlsx" or ".xlsm" or ".xlsb" or ".xls" or ".xltx" or ".xltm" or ".ods" => App.Excel,
        ".pptx" or ".pptm" or ".ppt" or ".potx" or ".ppsx" or ".odp" => App.PowerPoint,
        _ => null,
    };

    /// <summary>
    /// Writes <paramref name="source"/> as the PDF <paramref name="pdf"/>; throws <see cref="InvalidOperationException"/>
    /// saying why it couldn't. A file marked as downloaded from the internet isn't opened: Office opens
    /// such a file in Protected View only, as opening it can fetch what it links to (a remote template)
    /// and send the user's sign-in hash with the request.
    /// </summary>
    public static void Convert(string source, string pdf, CancellationToken ct)
    {
        var name = Path.GetFileName(source);
        if (AppFor(source) is not { } app)
            throw new InvalidOperationException($"{name} isn't a Word, Excel or PowerPoint file.");
        if (DownloadMark.FromInternet(source))
            throw new InvalidOperationException($"{name} is marked as downloaded from the internet, and Office opens such files in Protected View only: " +
                                                "it isn't drawn. If the user trusts it, they can Unblock it in its Properties.");
        if (Registered($"{app}.Application"))
            Run(OfficeScript(app), source, pdf, ct, $"{app}");
        else if (LibreOffice() is { } soffice)
            RunLibreOffice(soffice, source, pdf, ct);
        else
            throw new InvalidOperationException($"No program on this PC turns {name} into pages: Microsoft Office or LibreOffice does.");
        if (!File.Exists(pdf))
            throw new InvalidOperationException($"{name} wasn't turned into pages: the program made no PDF.");
    }

    private static bool Registered(string progId)
    {
        using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId + @"\CLSID");
        return key is not null;
    }

    /// <summary>
    /// The script for <paramref name="app"/>, reading its paths from BB_DOC_SOURCE and BB_DOC_PDF.
    /// A copy that shows (the user's) or holds files of its own is used and left open; a fresh
    /// automation copy starts hidden and empty, and is quit. Excel's sheets print with their grid and
    /// row and column headings, so the pages look like the sheet; links to other files aren't updated.
    /// </summary>
    private static string OfficeScript(App app) => app switch
    {
        // Quit and Close are called bare: their arguments are by reference, which PowerShell refuses a
        // plain 0 for (the first try left Word running hidden). Opened read-only, the file has nothing to save.
        App.Word => """
            $app = New-Object -ComObject Word.Application
            $mine = $false
            try {
                $mine = -not $app.Visible -and $app.Documents.Count -eq 0
                $alerts = $app.DisplayAlerts
                $app.DisplayAlerts = 0
                try {
                    # Read-only, not in Recent files, no conversion prompt, no window of its own.
                    $doc = $app.Documents.Open($env:BB_DOC_SOURCE, $false, $true, $false, '', '', $false, '', '', 0, [Type]::Missing, $false)
                    try { $doc.ExportAsFixedFormat($env:BB_DOC_PDF, 17) } finally { $doc.Saved = $true; $doc.Close() }
                } finally { $app.DisplayAlerts = $alerts }
            } finally {
                if ($mine) { $app.Quit() }
            }
            """,
        App.Excel => """
            $app = New-Object -ComObject Excel.Application
            $mine = $false
            try {
                $mine = -not $app.Visible -and $app.Workbooks.Count -eq 0
                $alerts = $app.DisplayAlerts
                $app.DisplayAlerts = $false
                try {
                    $book = $app.Workbooks.Open($env:BB_DOC_SOURCE, 0, $true)
                    try {
                        try {
                            $app.PrintCommunication = $false
                            foreach ($sheet in $book.Worksheets) { $sheet.PageSetup.PrintGridlines = $true; $sheet.PageSetup.PrintHeadings = $true }
                        } catch { } finally { $app.PrintCommunication = $true }
                        $book.ExportAsFixedFormat(0, $env:BB_DOC_PDF)
                    } finally { $book.Close($false) }
                } finally { $app.DisplayAlerts = $alerts }
            } finally {
                if ($mine) { $app.Quit() }
            }
            """,
        _ => """
            $app = New-Object -ComObject PowerPoint.Application
            $mine = $false
            try {
                $mine = $app.Presentations.Count -eq 0
                # Read-only, not untitled, no window.
                $deck = $app.Presentations.Open($env:BB_DOC_SOURCE, -1, 0, 0)
                try { $deck.SaveAs($env:BB_DOC_PDF, 32) } finally { $deck.Close() }
            } finally {
                if ($mine) { $app.Quit() }
            }
            """,
    };

    private static void Run(string script, string source, string pdf, CancellationToken ct, string what)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                             System.Convert.ToBase64String(Encoding.Unicode.GetBytes(
                                 "$ErrorActionPreference = 'Stop'\n$ProgressPreference = 'SilentlyContinue'\n" +
                                 // Its message as plain text: with its output redirected, PowerShell writes errors as CLIXML.
                                 "try {\n" + script + "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }")) },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            Environment = { ["BB_DOC_SOURCE"] = source, ["BB_DOC_PDF"] = pdf },
        };
        Wait(start, ct, $"{what} couldn't turn {Path.GetFileName(source)} into pages");
    }

    /// <summary>LibreOffice's soffice.exe where its installer puts it; null without one.</summary>
    private static string? LibreOffice() =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "LibreOffice", "program", "soffice.exe"))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// LibreOffice without a window, with a profile of its own: one the user has open would take the job
    /// and drop it.
    /// </summary>
    private static void RunLibreOffice(string soffice, string source, string pdf, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetDirectoryName(pdf)!, "libreoffice-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var start = new ProcessStartInfo(soffice)
            {
                ArgumentList = { "-env:UserInstallation=" + new Uri(Path.Combine(folder, "profile")).AbsoluteUri, "--headless", "--norestore",
                                 "--convert-to", "pdf", "--outdir", folder, source },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            Wait(start, ct, $"LibreOffice couldn't turn {Path.GetFileName(source)} into pages");
            var made = Path.Combine(folder, Path.GetFileNameWithoutExtension(source) + ".pdf");
            if (File.Exists(made))
                File.Move(made, pdf, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Wait(ProcessStartInfo start, CancellationToken ct, string failed)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException(failed + ": it didn't start.");
        var errors = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"{failed}: it took longer than {Timeout.TotalMinutes:0} minutes.");
        }
        if (process.ExitCode != 0)
        {
            var why = errors.IsCompletedSuccessfully ? FirstLine(errors.Result) : "";
            throw new InvalidOperationException($"{failed}{(why.Length > 0 ? ": " + why : "")}.");
        }
    }

    /// <summary>The first line PowerShell said of an error, which is its message.</summary>
    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()?.TrimEnd('.') ?? "";
}
