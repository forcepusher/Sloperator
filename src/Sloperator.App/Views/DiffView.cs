using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

/// <summary>
/// File changes' diff in the Code editor's editor (the user, 2026-11-08: "a real Code editor in place of
/// the diff"), read only: each row of the diff a line of its text, a file's header and a hunk's divider
/// drawn as they were, a line of code in the Code editor's colours (<see cref="DiffRow.Colours"/>), on its
/// green or red band, with its number and marker in a gutter of its own. Text is selected as in the Code
/// editor (a drag, Shift+click, Shift+arrows, a double click; a press on the numbers takes whole lines),
/// and the lines a selection is in are picked (<see cref="Picked"/>). Ctrl held keeps what was selected
/// beside a new selection, so lines in two places are picked; a click on a file's header picks its lines,
/// on a hunk's divider the hunk's. A click that leaves nothing selected is a click away from the picks (<see cref="ClickedAway"/>).
/// </summary>
public sealed class DiffView : CodeView
{
    /// <summary>Room over a file's header, the first's too, as there was over its row.</summary>
    private const double HeaderGap = 10;

    private IReadOnlyList<DiffRow> _rows = [];

    /// <summary>What was selected before a Ctrl+press: picked with the selection, drawn as it.</summary>
    private readonly List<SimpleSegment> _kept = [];

    /// <summary>The rows picked, as the selection and what is kept say: the gutter marks them.</summary>
    private HashSet<int> _picked = [];

    /// <summary>Set while the text or the selection is set from the rows, not by the user: nothing is reported.</summary>
    private bool _quiet;

    /// <summary>A press with no key held, till its release: a click that leaves nothing selected unpicks all.</summary>
    private bool _plainPress;

    private readonly Gutter _gutter;

    public DiffView()
    {
        IsReadOnly = true;
        Dress();
        Options.HighlightCurrentLine = false;
        // A press in what is selected starts a new selection, not a drag of the text.
        Options.EnableTextDragDrop = false;
        Document = new TextDocument { UndoStack = { SizeLimit = 0 } };
        _gutter = new Gutter(this);
        TextArea.LeftMargins.Insert(0, _gutter);
        var view = TextArea.TextView;
        // Under the light of what the agent just changed.
        view.BackgroundRenderers.Insert(0, new Bands(this));
        view.BackgroundRenderers.Add(new Kept(this));
        view.LineTransformers.Add(new Colours(this));
        view.ElementGenerators.Add(new RowElements(this));
        TextArea.SelectionChanged += (_, _) => Report();
        // Ahead of the editor's own handling of the press, which starts a selection.
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override bool Zooms => false;

    /// <summary>The rows of the lines selected (and kept) changed: the diff's picks follow.</summary>
    public event Action<IReadOnlyCollection<int>>? Picked;

    /// <summary>A click (no key held) left nothing selected: no line or file is picked any more, as Esc does.</summary>
    public event Action? ClickedAway;

    /// <summary>The row a line of the text (from 1) shows.</summary>
    private DiffRow? RowAt(int line) => line >= 1 && line <= _rows.Count ? _rows[line - 1] : null;

    /// <summary>
    /// The diff's rows as the text: the same text left as it is (only its colours drawn again), so the
    /// view and what is lit stay; a new one in place of the old, at the same scroll, its fresh rows lit.
    /// </summary>
    public void Show(IReadOnlyList<DiffRow> rows)
    {
        _rows = rows;
        var text = string.Join("\n", rows.Select(Line));
        _gutter.InvalidateMeasure();
        if (Document.Text == text)
        {
            TextArea.TextView.Redraw();
            _gutter.InvalidateVisual();
            return;
        }
        _quiet = true;
        try
        {
            _kept.Clear();
            TextArea.ClearSelection();
            Document.Text = text;
        }
        finally
        {
            _quiet = false;
        }
        TextArea.TextView.Redraw();
        LightOnly(Runs(rows.Where(r => r.IsFresh).Select(r => r.Index)));
    }

    /// <summary>A row as a line of the text: a header its file's name, a divider its words, a line its code.</summary>
    private static string Line(DiffRow row) =>
        (row.IsHeader ? row.File.Name : row.IsHunk ? row.Divider : row.Text).Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>A file's header at the top of the view, as far as it scrolls (a file clicked in the list).</summary>
    public void ShowFile(int row)
    {
        if (RowAt(row + 1) is null)
            return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (RowAt(row + 1) is not null)
                ScrollToVerticalOffset(TextArea.TextView.GetVisualTopByDocumentLine(row + 1));
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Watching: the agent's change lit again, its first row left in the upper half of the view, else brought a quarter of the way down.</summary>
    public void ShowChange(int row)
    {
        LightOnly(Runs(_rows.Where(r => r.IsFresh).Select(r => r.Index)));
        if (RowAt(row + 1) is not null)
            BringNear(row + 1);
    }

    /// <summary>Rows next to one another as runs of lines (from 1).</summary>
    private static List<(int From, int To)> Runs(IEnumerable<int> rows)
    {
        var runs = new List<(int From, int To)>();
        foreach (var row in rows.Order())
        {
            if (runs.Count > 0 && runs[^1].To == row)
                runs[^1] = (runs[^1].From, row + 1);
            else
                runs.Add((row + 1, row + 1));
        }
        return runs;
    }

    // ---- what is picked ------------------------------------------------------------------------------

    /// <summary>
    /// The rows picked, as the panel has them (after a refresh, a message sent, Esc): selected again where
    /// they aren't what is selected now, whole lines, each run apart kept as a Ctrl+drag would keep it and
    /// the last one the selection. Rows that are no line between two (a hunk's divider) don't part them.
    /// </summary>
    public void ShowPicks(IReadOnlyCollection<int> rows)
    {
        if (rows.ToHashSet().SetEquals(Lines()))
        {
            Mark(rows);
            return;
        }
        var runs = new List<(int From, int To)>();
        foreach (var row in rows.Order())
        {
            if (runs.Count > 0 && Enumerable.Range(runs[^1].To + 1, Math.Max(0, row - runs[^1].To - 1)).All(r => RowAt(r + 1) is { IsLine: false }))
                runs[^1] = (runs[^1].From, row);
            else
                runs.Add((row, row));
        }
        _quiet = true;
        try
        {
            _kept.Clear();
            foreach (var (from, to) in runs.Take(runs.Count - 1))
                _kept.Add(Segment(from, to));
            if (runs.Count == 0)
                TextArea.ClearSelection();
            else
                TextArea.Selection = Selection.Create(TextArea, Segment(runs[^1].From, runs[^1].To));
        }
        finally
        {
            _quiet = false;
        }
        TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        Mark(rows);
    }

    /// <summary>Rows <paramref name="from"/> to <paramref name="to"/> (from 0) as text, from the first's start to the last's end.</summary>
    private SimpleSegment Segment(int from, int to)
    {
        var first = Document.GetLineByNumber(Math.Clamp(from + 1, 1, Document.LineCount));
        var last = Document.GetLineByNumber(Math.Clamp(to + 1, first.LineNumber, Document.LineCount));
        return new SimpleSegment(first.Offset, last.EndOffset - first.Offset);
    }

    /// <summary>The rows of the code lines the selection and what is kept are in: a part of a line means the line, a selection ending where a line starts leaves it out.</summary>
    private HashSet<int> Lines()
    {
        var rows = new HashSet<int>();
        var segments = _kept.Select(s => (ISegment)s).ToList();
        if (!TextArea.Selection.IsEmpty)
            segments.Add(TextArea.Selection.SurroundingSegment);
        foreach (var segment in segments)
        {
            var from = Document.GetLineByOffset(segment.Offset).LineNumber;
            var end = Document.GetLineByOffset(segment.EndOffset);
            var to = end.LineNumber > from && end.Offset == segment.EndOffset ? end.LineNumber - 1 : end.LineNumber;
            for (var line = from; line <= to; line++)
                if (RowAt(line) is { IsLine: true })
                    rows.Add(line - 1);
        }
        return rows;
    }

    /// <summary>The gutter marks these rows as picked.</summary>
    private void Mark(IEnumerable<int> rows)
    {
        _picked = rows.ToHashSet();
        _gutter.InvalidateVisual();
    }

    /// <summary>The selection or what is kept changed by the user: the lines they are in are picked.</summary>
    private void Report()
    {
        if (_quiet)
            return;
        var rows = Lines();
        Mark(rows);
        Picked?.Invoke(rows);
    }

    /// <summary>Where a Ctrl+press on the text was, while its button is down; null with none.</summary>
    private int? _ctrlFrom;

    /// <summary>The pointer moved off where the Ctrl+press was: a drag, not a click.</summary>
    private bool _ctrlDragged;

    /// <summary>
    /// A press: on a file's header or a hunk's divider (its band, or the gutter beside it), picks what is under
    /// it, the editor not starting a selection there; with no key held, what was kept is let go. Ctrl held keeps
    /// what was selected beside what comes next: on the numbers the editor selects the line as ever; on the text,
    /// which AvaloniaEdit doesn't select with Ctrl held, a drag selects from here (<see cref="OnMoved"/>), and a
    /// click picks the line, or lets it go if it was picked, as a Ctrl+click on a row did.
    /// </summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(TextArea.TextView);
        // The text and its gutter, not the scrollbars.
        if (!point.Properties.IsLeftButtonPressed || (e.Source as Visual)?.FindAncestorOfType<TextArea>(includeSelf: true) != TextArea)
            return;
        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        _plainPress = !ctrl && !shift;
        if (ctrl && !TextArea.Selection.IsEmpty)
        {
            var selected = TextArea.Selection.SurroundingSegment;
            _kept.Add(new SimpleSegment(selected.Offset, selected.Length));
        }
        else if (!ctrl && !shift)
            _kept.Clear();
        var line = TextArea.TextView.GetDocumentLineByVisualTop(point.Position.Y + TextArea.TextView.VerticalOffset);
        if (point.Position.Y >= 0 && line is not null && RowAt(line.LineNumber) is { IsLine: false } row && e.ClickCount == 1)
        {
            e.Handled = true;
            _plainPress = false;
            TextArea.Focus();
            // From its first line to its last: a file's first divider isn't selected with it.
            var lines = FileChangesViewModel.Under(_rows, row.Index) is var (from, to) ? Enumerable.Range(from, to - from + 1).Where(r => _rows[r].IsLine).ToList() : [];
            if (lines.Count > 0)
                TextArea.Selection = Selection.Create(TextArea, Segment(lines[0], lines[^1]));
            else if (!ctrl)
                TextArea.ClearSelection();
            Report();
            return;
        }
        if (ctrl && !shift && point.Position.X >= 0 && OffsetAt(point.Position) is { } at)
        {
            e.Handled = true;
            TextArea.Focus();
            _ctrlFrom = at;
            _ctrlDragged = false;
            _quiet = true;
            try { TextArea.ClearSelection(); }
            finally { _quiet = false; }
            TextArea.Caret.Offset = at;
            e.Pointer.Capture(TextArea.TextView);
        }
        TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        if (ctrl)
            Report();
    }

    /// <summary>The text's offset under a point over the text view; null past its lines.</summary>
    private int? OffsetAt(Point point) =>
        TextArea.TextView.GetPositionFloor(point + TextArea.TextView.ScrollOffset) is { } position ? Document.GetOffset(position.Location) : null;

    /// <summary>A Ctrl+drag: selected from where it was pressed to the pointer, what was kept staying.</summary>
    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_ctrlFrom is not { } from || OffsetAt(e.GetPosition(TextArea.TextView)) is not { } to || to == from && !_ctrlDragged)
            return;
        _ctrlDragged = true;
        TextArea.Selection = Selection.Create(TextArea, Math.Min(from, to), Math.Max(from, to));
        TextArea.Caret.Offset = to;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_ctrlFrom is { } from)
        {
            _ctrlFrom = null;
            e.Pointer.Capture(null);
            if (!_ctrlDragged)
                ToggleLine(Document.GetLineByOffset(from));
        }
        if (_plainPress && e.InitialPressMouseButton == MouseButton.Left && TextArea.Selection.IsEmpty && _kept.Count == 0)
            ClickedAway?.Invoke();
        _plainPress = false;
    }

    /// <summary>A Ctrl+click on a line: picked beside what is, or let go if it was picked (what is kept around it stays).</summary>
    private void ToggleLine(DocumentLine line)
    {
        if (RowAt(line.LineNumber) is not { IsLine: true })
            return;
        if (!Lines().Contains(line.LineNumber - 1))
            _kept.Add(new SimpleSegment(line.Offset, line.Length));
        else
        {
            var after = line.NextLine?.Offset ?? line.EndOffset;
            foreach (var segment in _kept.Where(s => s.Offset <= line.EndOffset && s.EndOffset >= line.Offset).ToList())
            {
                var at = _kept.IndexOf(segment);
                _kept.RemoveAt(at);
                if (segment.EndOffset > after)
                    _kept.Insert(at, new SimpleSegment(after, segment.EndOffset - after));
                if (segment.Offset < line.Offset)
                    _kept.Insert(at, new SimpleSegment(segment.Offset, line.Offset - segment.Offset));
            }
        }
        TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        Report();
    }

    // ---- how the rows look ---------------------------------------------------------------------------

    private static IBrush Brush(string key) => Application.Current?.FindResource(key) as IBrush ?? Brushes.Transparent;

    /// <summary>The room a row's band leaves over it: a file's header's.</summary>
    private static double Gap(DiffRow row) => row.IsHeader ? HeaderGap : 0;

    /// <summary>A row's band, the width of the view: green for an added line, red for a removed one, a card's colour for a file's header.</summary>
    private static IBrush? Band(DiffRow row) =>
        row.IsAdded ? Brush("BbAddedRow") : row.IsRemoved ? Brush("BbRemovedRow") : row.IsHeader ? Brush("BbSurface") : null;

    private sealed class Bands(DiffView diff) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            foreach (var line in textView.VisualLines)
            {
                if (diff.RowAt(line.FirstDocumentLine.LineNumber) is not { } row || Band(row) is not { } band)
                    continue;
                var top = line.VisualTop - textView.VerticalOffset + Gap(row);
                drawingContext.FillRectangle(band, new Rect(0, top, textView.Bounds.Width, line.Height - Gap(row)));
            }
        }
    }

    /// <summary>What a Ctrl+press kept, drawn as the selection is.</summary>
    private sealed class Kept(DiffView diff) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (diff._kept.Count == 0 || diff.TextArea.SelectionBrush is not { } brush)
                return;
            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 0 };
            foreach (var segment in diff._kept.Where(s => s.EndOffset <= textView.Document.TextLength))
                builder.AddSegment(textView, segment);
            if (builder.CreateGeometry() is { } geometry)
                drawingContext.DrawGeometry(brush, null, geometry);
        }
    }

    /// <summary>Each line's code in its colours.</summary>
    private sealed class Colours(DiffView diff) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (diff.RowAt(line.LineNumber) is not { IsCode: true } row)
                return;
            foreach (var span in row.Colours)
            {
                var from = line.Offset + span.Start;
                var to = Math.Min(line.EndOffset, from + span.Length);
                if (from >= to)
                    continue;
                var brush = Brush(span.Key);
                ChangeLinePart(from, to, element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }

    /// <summary>A file's header and a hunk's divider, drawn as they were in place of their text.</summary>
    private sealed class RowElements(DiffView diff) : VisualLineElementGenerator
    {
        private static readonly Cursor Arrow = new(StandardCursorType.Arrow);

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.Document.GetLineByOffset(startOffset);
            return line.Offset == startOffset && line.Length > 0 && diff.RowAt(line.LineNumber) is { IsCode: false } ? startOffset : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var line = CurrentContext.Document.GetLineByOffset(offset);
            if (diff.RowAt(line.LineNumber) is not { IsCode: false } row)
                return null;
            // The code's font, as a line of text measures it: its ascent over the baseline, its descent under it,
            // and its gap, which the line adds to whatever stands in it (a line is at least the editor's line height).
            var text = CurrentContext.GlobalTextRunProperties;
            var metrics = text.Typeface.GlyphTypeface.Metrics;
            var scale = text.FontRenderingEmSize / metrics.DesignEmHeight;
            var (ascent, descent, gap) = (-metrics.Ascent * scale, metrics.Descent * scale, metrics.LineGap * scale);
            return new RowElement(line.Length, row.IsHeader ? Header(row, gap) : Divider(row, ascent + descent), descent);
        }

        /// <summary>
        /// A header or divider standing on the line's baseline as text does, down to the text's descent: AvaloniaEdit's own
        /// hung it under the baseline, which made its line taller by a line of text's ascent (39 px for a 26 px header).
        /// </summary>
        private sealed class RowElement(int length, Control element, double descent) : InlineObjectElement(length, element)
        {
            public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) => new Standing(TextRunProperties, Element, descent);

            private sealed class Standing(TextRunProperties properties, Control element, double descent) : InlineObjectRun(1, properties, element)
            {
                public override double Baseline => Math.Max(0, Size.Height - descent);
            }
        }

        /// <summary>
        /// The file's name (13 px, semibold), then "modified · +10 -3" muted with its counts green and red; its
        /// full path as its tooltip. 5 px over and under, on its band.
        /// </summary>
        private static Control Header(DiffRow row, double gap)
        {
            var details = new TextBlock
            {
                FontSize = 12,
                Foreground = Brush("BbMuted"),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Inlines = [new Run(row.HeaderStatus), new Run(row.File.AddedCount) { Foreground = Brush("BbAdded") }, new Run(row.File.RemovedCount) { Foreground = Brush("BbRemoved") }, new Run(row.File.OtherCount)],
            };
            var name = new TextBlock { Text = row.File.Name, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brush("BbText"), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(name, row.File.FullPath);
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { name, details },
                Margin = new Thickness(0, Gap(row) + 5, 0, Math.Max(0, 5 - gap)),
                [TextElement.FontFamilyProperty] = FontFamily.Default,
                DataContext = row,
                Cursor = Arrow,
            };
        }

        /// <summary>"Lines 96-104": 11 px muted, its row as tall as a line of code's.</summary>
        private static Control Divider(DiffRow row, double height) => new TextBlock
        {
            Text = row.Divider,
            FontSize = 11,
            FontFamily = FontFamily.Default,
            Foreground = Brush("BbMuted"),
            Height = height,
            DataContext = row,
            Cursor = Arrow,
        };
    }

    /// <summary>
    /// The number of each line, the new one's or a removed line's old one, and its + or -, after 4 px: as wide as
    /// the widest number. Green and red on an added and a removed line, as their bands, which run under it; a
    /// picked line's edge in the accent. A press here takes whole lines, as on the Code editor's numbers.
    /// </summary>
    private sealed class Gutter(DiffView diff) : LineNumberMargin
    {
        private Typeface Face => new(diff.FontFamily);

        private FormattedText Text(string text, IBrush brush) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, diff.FontSize, brush);

        private double Digit => Text("0", Brushes.Transparent).WidthIncludingTrailingWhitespace;

        private int Digits => Math.Max(1, diff._rows.Count == 0 ? 1 : diff._rows.Max(r => r.Number.Length));

        protected override Size MeasureOverride(Size availableSize) => new(4 + Digit * (Digits + 1) + 4, 0);

        public override void Render(DrawingContext drawingContext)
        {
            var textView = TextView;
            if (textView is not { VisualLinesValid: true })
                return;
            var digit = Digit;
            var digits = Digits;
            foreach (var line in textView.VisualLines)
            {
                if (diff.RowAt(line.FirstDocumentLine.LineNumber) is not { } row)
                    continue;
                var top = line.VisualTop - textView.VerticalOffset;
                if (Band(row) is { } band)
                    drawingContext.FillRectangle(band, new Rect(0, top + Gap(row), Bounds.Width, line.Height - Gap(row)));
                if (diff._picked.Contains(row.Index))
                    drawingContext.FillRectangle(Brush("BbAccent"), new Rect(0, top, 2, line.Height));
                if (!row.IsCode)
                    continue;
                var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;
                var colour = Brush(row.IsAdded ? "BbAdded" : row.IsRemoved ? "BbRemoved" : "BbFaint");
                var number = Text(row.Number, colour);
                drawingContext.DrawText(number, new Point(4 + digit * digits - number.WidthIncludingTrailingWhitespace, y));
                if (row.Marker.Length > 0)
                    drawingContext.DrawText(Text(row.Marker, colour), new Point(4 + digit * digits, y));
            }
        }
    }
}
