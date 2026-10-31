namespace Sloperator.App.ViewModels;

/// <summary>
/// A tool call's line on its card, on one line: every whole path in it by its file or folder name
/// ("Read C:\Users\me\Documents\Sloperator\Hello World\hello.py" reads "Read hello.py"). A whole path
/// wrapped over two or three ragged lines between the tool's name and its time (the user, 2026-10-31).
/// The line as the tool gave it stays in the card's tooltip and in what the model was told.
/// </summary>
public static class ToolLine
{
    /// <summary>Words that end a path with a space in it: "Copy C:\a b\x.txt to D:\y", "Read /srv/app.py on web1".</summary>
    private static readonly HashSet<string> After = ["on", "to", "with", "into", "in", "against", "for", "beside", "and", "as", "from", "at"];

    public static string Short(string line)
    {
        // What follows ": " is the model's own words (a command over SSH, a query, keys), and what is
        // in quotes is typed or looked for: both are shown as they are.
        var colon = line.IndexOf(": ", StringComparison.Ordinal);
        var end = colon < 0 ? line.Length : colon;
        var shown = new System.Text.StringBuilder(line.Length);
        var quoted = false;
        var i = 0;
        while (i < end)
        {
            var c = line[i];
            if (c == '"')
                quoted = !quoted;
            if (!quoted && (i == 0 || line[i - 1] is ' ' or '(') && PathStart(line, i, end) is > 0 and var length && PathEnd(line, i + length, end) is var to && Name(line[i..to]) is { } name)
            {
                shown.Append(name);
                i = to;
                continue;
            }
            shown.Append(c);
            i++;
        }
        return shown.Append(line, end, line.Length - end).ToString();
    }

    /// <summary>How long the root of a whole path at <paramref name="i"/> is ("C:\", "\\", "/", "~/"), 0 if none starts there.</summary>
    private static int PathStart(string s, int i, int end)
    {
        bool Sep(int at) => at < end && s[at] is '\\' or '/';
        bool Part(int at) => at < end && s[at] is not ('\\' or '/' or ' ' or '\n');
        if (i + 2 < end && char.IsAsciiLetter(s[i]) && s[i + 1] == ':' && Sep(i + 2) && Part(i + 3))
            return 3;
        if (s[i] == '\\' && Sep(i + 1) && s[i + 1] == '\\' && Part(i + 2))
            return 2;
        if (s[i] == '~' && Sep(i + 1) && s[i + 1] == '/' && Part(i + 2))
            return 2;
        return s[i] == '/' && Part(i + 1) ? 1 : 0;
    }

    /// <summary>Where the path from <paramref name="i"/> ends: at a new line, a quote, ", ", " (", a wildcard, or one of <see cref="After"/>.</summary>
    private static int PathEnd(string s, int i, int end)
    {
        for (; i < end; i++)
        {
            if (s[i] is '\n' or '"' || s[i] == ',' && i + 1 < end && s[i + 1] == ' ')
                return i;
            if (s[i] != ' ')
                continue;
            var next = s.IndexOf(' ', i + 1, end - i - 1) is var space and >= 0 ? space : end;
            var word = s[(i + 1)..next];
            if (word.Length == 0 || word[0] == '(' || word.Contains('*') || word.Contains('?') || After.Contains(word) && !GoesOn(s, next, end))
                return i;
        }
        return end;
    }

    /// <summary>
    /// Whether a path goes on past a word of <see cref="After"/> that ends at <paramref name="i"/>: it
    /// does when a separator comes later that doesn't begin a whole path of its own ("C:\a folder
    /// with a long name\b.txt", but not "C:\a.txt to D:\b.txt").
    /// </summary>
    private static bool GoesOn(string s, int i, int end)
    {
        for (; i < end; i++)
        {
            if (s[i] is '\n' or '"')
                return false;
            if ((s[i - 1] is ' ' or '(') && PathStart(s, i, end) > 0)
                return false;
            if (s[i] is '\\' or '/')
                return true;
        }
        return false;
    }

    /// <summary>The last part of <paramref name="path"/>, its file or folder; null for a root alone.</summary>
    private static string? Name(string path)
    {
        path = path.TrimEnd('\\', '/');
        var cut = path.LastIndexOfAny(['\\', '/']);
        var name = path[(cut + 1)..];
        return cut < 0 || name.Length == 0 || name.EndsWith(':') ? null : name;
    }
}
