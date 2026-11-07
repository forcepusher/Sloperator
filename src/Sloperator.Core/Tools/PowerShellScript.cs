using System.Text;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Tools;

/// <summary>
/// The script run_shell gives Windows PowerShell for a command: the setup Sloperator needs first, then the
/// command as a script block of its own, then its exit code. The command used to follow the setup in the
/// same script, and PowerShell names a top-level script by its text, so a Write-Error showed the setup and
/// the exit-code line in front of its message ("[Console]::OutputEncoding = …; if (-not $?) … : Missing
/// file"), and every other error's "At line" was one more than the command's own. Run as a script block, an
/// error's position is in the command's own lines, and what of Sloperator's still shows (the line that ends
/// the block, the line that runs it) is taken out of the error text by <see cref="Tidy"/>.
/// </summary>
public static partial class PowerShellScript
{
    /// <summary>Forcing UTF-8 output keeps non-ASCII file names and text intact in the redirected stream; no progress bars;
    /// Windows PowerShell's curl is an alias of Invoke-WebRequest, which rejects curl's options, and models mean the real curl.exe.
    /// Get-Content takes a file without a byte order mark for the PC's own code page, so UTF-8 source came back garbled, and written
    /// back it stayed that way; a file with a mark still reads by its mark, and -Encoding still wins.</summary>
    private const string Setup =
        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " +
        "$PSDefaultParameterValues['Get-Content:Encoding'] = 'UTF8'; " +
        "Remove-Item Alias:curl -Force -ErrorAction SilentlyContinue";

    /// <summary>The last line of the command's block: whether its last statement worked, which <see cref="ExitCode"/> reads.</summary>
    private const string Ok = "$SloperatorOk = $?";

    /// <summary>
    /// The process's exit code from how the command ended: a program's own code when it failed with one,
    /// 0 for a program's messages on stderr taken in with 2>&amp;1 (Windows PowerShell 5.1 counts them as an
    /// error), else 1 for a cmdlet that failed. A command that ends with exit or return never gets here
    /// with a failure (its block's end isn't reached: $SloperatorOk stays true).
    /// <para>
    /// In `git log | Select-Object -First 5` Select-Object has all it wants and stops the program, and
    /// PowerShell 5.1 calls the statement failed: $? false, $LASTEXITCODE -1, no error recorded. Good
    /// output came back as "Exit code -1". Only a command with such a pipe gets this reading of -1,
    /// as a program can end with -1 by itself.
    /// </para>
    /// </summary>
    private static string ExitCode(bool cutsPipe) =>
        "if (-not $SloperatorOk) { " + (cutsPipe ? "if ($LASTEXITCODE -eq -1 -and $Error.Count -eq 0) { exit 0 }; " : "") +
        "if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; " +
        "if ($Error.Count -gt 0 -and \"$($Error[0].FullyQualifiedErrorId)\" -like 'NativeCommandError*') { exit 0 }; exit 1 }";

    /// <summary>
    /// The script for <paramref name="command"/>. The command goes in as Base64 of its UTF-16 text, so nothing in
    /// it (quotes, a here-string's '@) can end the string it is in; dot-sourced, it runs in the script's own
    /// scope, as when it followed the setup.
    /// </summary>
    public static string Wrap(string command)
    {
        var block = Convert.ToBase64String(Encoding.Unicode.GetBytes(command + "\n" + Ok));
        return $"{Setup}; $SloperatorOk = $true; " +
               $"$SloperatorCommand = [scriptblock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{block}')))\n" +
               ". $SloperatorCommand\n" + ExitCode(CutPipe().IsMatch(command));
    }

    [GeneratedRegex(@"\|\s*(select|select-object)\b[^|;\n]*-first\b", RegexOptions.IgnoreCase)]
    private static partial Regex CutPipe();

    /// <summary>
    /// Error text PowerShell wrote for a command run by <see cref="Wrap"/>, without what is Sloperator's: the
    /// block's last line, which a Write-Error names with the command ("Write-Error 'x'\n$SloperatorOk = $? : x"),
    /// and the position of the line that runs the block ("At line:2 char:1 + . $SloperatorCommand").
    /// </summary>
    public static string Tidy(string error) =>
        RunLine().Replace(OkLine().Replace(error, " : "), "");

    [GeneratedRegex(@"\r?\n\$SloperatorOk = \$\? : ")]
    private static partial Regex OkLine();

    [GeneratedRegex(@"At line:\d+ char:\d+\r?\n\+ \. \$SloperatorCommand\r?\n\+ ~+\r?\n?")]
    private static partial Regex RunLine();
}
