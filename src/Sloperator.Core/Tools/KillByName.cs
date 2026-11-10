using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Tools;

/// <summary>
/// Commands that end a program by its name, which ends every window of it: the user's own too,
/// unsaved work and all. qwen3.8 closed the Word it had opened by mistake with
/// <c>Get-Process winword | Stop-Process -Force</c>, Paint with <c>Stop-Process -Name mspaint</c>, and
/// its own Photoshop with <c>Stop-Process -Name Photoshop</c> after the prompt said not to (2026-11-10).
/// run_shell doesn't run one while the program has windows that were open before the task.
/// </summary>
internal static partial class KillByName
{
    /// <summary>The programs (exe names without .exe) the command ends by name; empty when it ends none, or picks which with a filter.</summary>
    public static IReadOnlyList<string> Programs(string command)
    {
        var names = new List<string>();
        foreach (var statement in Statements().Split(command))
        {
            if (!Kills().IsMatch(statement))
                continue;
            // Narrowed down (by start time or window title), it ends the ones it means.
            if (Filter().IsMatch(statement))
                continue;
            foreach (Match m in StopByName().Matches(statement))
                names.AddRange(Names(m.Groups["names"].Value));
            foreach (Match m in GetByName().Matches(statement))
                names.AddRange(Names(m.Groups["names"].Value));
            foreach (Match m in TaskkillImage().Matches(statement))
                names.AddRange(Names(m.Groups["name"].Value));
        }
        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The windows of program <paramref name="name"/> whose process started before <paramref name="since"/> (one that can't be read counts).</summary>
    public static int WindowsOpenBefore(string name, DateTime since)
    {
        var count = 0;
        foreach (var p in Process.GetProcessesByName(name))
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle != 0 && p.StartTime < since)
                        count++;
                }
                // An elevated program's start time can't be read: it is taken for the user's.
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    count++;
                }
            }
        return count;
    }

    private static IEnumerable<string> Names(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.Trim('"', '\'', '*', ' '))
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Where(n => n.Length > 0 && !n.StartsWith('$') && !n.StartsWith('-'));

    [GeneratedRegex(@"[;\r\n]+")]
    private static partial Regex Statements();

    [GeneratedRegex(@"\b(Stop-Process|spps|taskkill)\b|\|\s*kill\b|\.Kill\(", RegexOptions.IgnoreCase)]
    private static partial Regex Kills();

    [GeneratedRegex(@"\|\s*(Where-Object|where|\?)[\s{]|-Filter\b|/FI\b|\bStartTime\b|\bMainWindowTitle\b", RegexOptions.IgnoreCase)]
    private static partial Regex Filter();

    // Stop-Process -Name a,b / -ProcessName / -n (and spps, kill).
    [GeneratedRegex(@"\b(Stop-Process|spps|kill)\s+(?:-\w+\s+)*?-(Name|ProcessName|n)\s+(?<names>(?:['""]?[\w.*\-]+['""]?\s*,\s*)*['""]?[\w.*\-]+['""]?)", RegexOptions.IgnoreCase)]
    private static partial Regex StopByName();

    // Get-Process a,b (gps, ps; -Name too) in the statement that kills.
    [GeneratedRegex(@"\b(Get-Process|gps|ps)\s+(?:-(?:Name|ProcessName|n)\s+)?(?<names>(?:['""]?[\w.*\-]+['""]?\s*,\s*)*['""]?[A-Za-z][\w.*\-]*['""]?)", RegexOptions.IgnoreCase)]
    private static partial Regex GetByName();

    [GeneratedRegex(@"\btaskkill\b[^\r\n;]*?/im\s+(?<name>['""]?[\w.*\-]+['""]?)", RegexOptions.IgnoreCase)]
    private static partial Regex TaskkillImage();
}
