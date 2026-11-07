using System.Text.RegularExpressions;

namespace Sloperator.Core.Agent;

/// <summary>
/// Whether a shell command only looks at things: reads or searches files, lists folders, asks git
/// how things stand. Such a command is no try at anything: in a Unity chat feature and a QEMU test
/// setup (2026-11-04) models read through run_shell 121 times against 23 read_file calls, and about
/// half of the "Time to look" notes came after one. What it doesn't know counts as doing something,
/// so a program it has never heard of stays a run.
/// </summary>
internal static partial class ReadOnlyCommand
{
    public static bool Is(string command)
    {
        // Quoted text can hold anything: a pattern's | and ; are no pipes.
        var plain = Quoted().Replace(command, "\"\"");
        if (Changes().IsMatch(plain))
            return false;
        var parts = Parts().Split(plain).Select(p => Assignment().Replace(p.Trim(), "").TrimStart('(', ' ')).Where(p => p.Length > 0).ToList();
        return parts.Count > 0 && parts.All(p => Looks().IsMatch(p));
    }

    [GeneratedRegex("\"[^\"\\n]*\"|'[^'\\n]*'")]
    private static partial Regex Quoted();

    // Statements, pipes and the blocks of foreach, if and %.
    [GeneratedRegex(@"&&|\|\||[;|{}\n]")]
    private static partial Regex Parts();

    [GeneratedRegex(@"^\$[\w:]+\s*=(?!=)\s*")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"\b(Set-Content|Add-Content|Clear-Content|Out-File|Remove-Item|Move-Item|Copy-Item|New-Item|Rename-Item|Set-Item|Start-Process|Stop-Process|Invoke-\w+|Export-\w+|Tee-Object)\b|" +
                    @"\[(System\.)?IO\.\w+\]::(Write|Append|Delete|Move|Copy|Create)|" +
                    @"(?<![\d=-])>>?\s*(?!&|\$null|/dev/null)\S|\s-(delete|exec)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Changes();

    // What starts a statement that only reads, PowerShell's and a Linux shell's; a variable or a string on its own only prints.
    [GeneratedRegex(@"^(cd|chdir|pushd|popd|set-location|sl|" +
                    @"get-content|gc|cat|type|more|head|tail|wc|" +
                    @"select-string|sls|findstr|grep|rg|find|" +
                    @"get-childitem|gci|ls|dir|tree|" +
                    @"test-path|resolve-path|split-path|join-path|get-item|gi|get-itemproperty|get-filehash|" +
                    @"get-command|gcm|where\.exe|which|" +
                    @"get-process|gps|ps|get-service|get-ciminstance|get-wmiobject|get-date|get-location|pwd|get-variable|get-module|" +
                    @"select-object|select|where-object|where|\?|foreach-object|foreach|%|sort-object|sort|group-object|group|measure-object|measure|" +
                    @"format-table|ft|format-list|fl|out-string|out-null|convertto-json|convertfrom-json|" +
                    @"write-output|write-host|echo|start-sleep|sleep|if|elseif|else|" +
                    @"df|du|free|uname|whoami|hostname|id|stat|journalctl|" +
                    @"git(\s+-C\s+\S+)?\s+(status|log|reflog|diff|show|grep|ls-files|ls-tree|branch|rev-parse|blame|remote|describe|shortlog|cat-file|stash\s+list)" +
                    @")(?![\w-])|^\$|^""""|^\[(System\.)?IO\.\w+\]::(Read|Exists|Get)", RegexOptions.IgnoreCase)]
    private static partial Regex Looks();
}
