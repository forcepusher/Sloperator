using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using Sloperator.Core.Diffs;

namespace Sloperator.App.Views;

/// <summary>A run of a line's characters in one of the app's syntax colours (a key in App.axaml, <c>BbSyntax*</c>).</summary>
public readonly record struct SyntaxSpan(int Start, int Length, string Key);

/// <summary>
/// A diff's lines in the Code editor's colours (the user, 2026-11-08): each side of a file highlighted whole
/// by AvaloniaEdit, as the Code editor highlights it, and each line of the diff given its line's colours, an
/// added or kept line the new side's and a removed line the old side's. So a line inside a comment, string or
/// docstring that starts above its hunk is coloured as one: highlighted alone, a hunk starting in a /* */
/// comment showed its end as code, and a docstring's closing quotes as an opening, the rest of the hunk a string.
/// A side not kept (<see cref="DiffFile.OldText"/>, <see cref="DiffFile.NewText"/>) is highlighted from its
/// lines in the diff, hunk by hunk, as near as they tell. Read as lines are drawn, as the Code editor reads
/// its own: a side is highlighted only as far down as a line of it shown (AvaloniaEdit's highlighter keeps
/// where it got to). On the UI thread, AvaloniaEdit's highlighter's own.
/// </summary>
public sealed class DiffColours(DiffFile file, string? language)
{
    private readonly IHighlightingDefinition? _definition = file.Binary ? null : CodeView.Definition(language);
    private readonly IReadOnlyList<SyntaxSpan>?[] _lines = new IReadOnlyList<SyntaxSpan>?[file.Lines.Count];
    private DocumentHighlighter? _new;
    private DocumentHighlighter? _old;

    /// <summary>The colours of the file's diff line <paramref name="index"/>: none for a hunk's line, and for a file of no language.</summary>
    public IReadOnlyList<SyntaxSpan> Of(int index)
    {
        if (_definition is null || index < 0 || index >= _lines.Length)
            return [];
        if (_lines[index] is { } done)
            return done;
        var line = file.Lines[index];
        if (line.Kind is not (DiffLineKind.Added or DiffLineKind.Removed or DiffLineKind.Context))
            return _lines[index] = [];
        var newSide = line.Kind != DiffLineKind.Removed;
        var whole = newSide ? file.NewText : file.OldText;
        if (whole is null)
        {
            Hunk(index, newSide);
            return _lines[index] ?? [];
        }
        // Numbered as the diff numbers them: every line ending as one, as LineDiff splits.
        var highlighter = newSide ? _new ??= Highlighter(whole.ReplaceLineEndings("\n")) : _old ??= Highlighter(whole.ReplaceLineEndings("\n"));
        var number = (newSide ? line.New : line.Old) ?? 0;
        return _lines[index] = number >= 1 && number <= highlighter.Document.LineCount ? Spans(highlighter.HighlightLine(number), line.Text.Length) : [];
    }

    /// <summary>The side's lines of the hunk line <paramref name="index"/> is in (kept ones too, for what they open or close), highlighted together.</summary>
    private void Hunk(int index, bool newSide)
    {
        bool Mine(DiffLine line) => line.Kind == DiffLineKind.Context || line.Kind == (newSide ? DiffLineKind.Added : DiffLineKind.Removed);
        var start = index;
        while (start > 0 && file.Lines[start - 1].Kind != DiffLineKind.Hunk)
            start--;
        var indexes = new List<int>();
        for (var i = start; i < file.Lines.Count && file.Lines[i].Kind != DiffLineKind.Hunk; i++)
            if (Mine(file.Lines[i]))
                indexes.Add(i);
        var highlighter = Highlighter(string.Join("\n", indexes.Select(i => file.Lines[i].Text)));
        for (var k = 0; k < indexes.Count; k++)
            // A kept line takes the new side's colours, as it does with the whole file.
            if (file.Lines[indexes[k]].Kind != DiffLineKind.Context || newSide)
                _lines[indexes[k]] = Spans(highlighter.HighlightLine(k + 1), file.Lines[indexes[k]].Text.Length);
    }

    private DocumentHighlighter Highlighter(string text) => new(new TextDocument(text), _definition!);

    /// <summary>
    /// A highlighted line's colours as runs of the app's: a section inside another (a string's escape) over
    /// it, one in a colour the app leaves as the code's own keeping what is under it, as the Code editor draws
    /// them. Only as far as the line goes in the diff (a line over 1,000 characters is cut there).
    /// </summary>
    private static IReadOnlyList<SyntaxSpan> Spans(HighlightedLine line, int length)
    {
        length = Math.Min(length, line.DocumentLine.Length);
        if (length <= 0 || line.Sections.Count == 0)
            return [];
        var keys = new string?[length];
        foreach (var section in line.Sections)
        {
            if (CodeView.SyntaxColour(section.Color?.Name ?? "") is not { } key)
                continue;
            var from = Math.Max(0, section.Offset - line.DocumentLine.Offset);
            var to = Math.Min(length, section.Offset - line.DocumentLine.Offset + section.Length);
            for (var i = from; i < to; i++)
                keys[i] = key;
        }
        var spans = new List<SyntaxSpan>();
        for (var i = 0; i < length;)
        {
            var j = i + 1;
            while (j < length && keys[j] == keys[i])
                j++;
            if (keys[i] is { } key)
                spans.Add(new SyntaxSpan(i, j - i, key));
            i = j;
        }
        return spans;
    }
}
