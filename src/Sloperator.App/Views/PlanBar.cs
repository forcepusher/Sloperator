using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Sloperator.Core.Tools;

namespace Sloperator.App.Views;

/// <summary>
/// The agent's plan above the message box, left to right: a point per step with its word, what the
/// step does on hover, and the party blob walking from point to point. A window too narrow for all
/// the words shows those next to the blob (see <see cref="Radius"/>).
/// </summary>
public sealed class PlanBar : Panel
{
    public static readonly StyledProperty<AgentPlan?> PlanProperty = AvaloniaProperty.Register<PlanBar, AgentPlan?>(nameof(Plan));
    public static readonly StyledProperty<bool> DancingProperty = AvaloniaProperty.Register<PlanBar, bool>(nameof(Dancing));
    public static readonly StyledProperty<bool> WaitingProperty = AvaloniaProperty.Register<PlanBar, bool>(nameof(Waiting));
    public static readonly StyledProperty<bool> FailedProperty = AvaloniaProperty.Register<PlanBar, bool>(nameof(Failed));

    /// <summary>Where the blob is, in steps: a property so that a transition walks it, and a window's resize doesn't.</summary>
    public static readonly StyledProperty<double> SpotProperty = AvaloniaProperty.Register<PlanBar, double>(nameof(Spot));

    public AgentPlan? Plan { get => GetValue(PlanProperty); set => SetValue(PlanProperty, value); }
    public bool Dancing { get => GetValue(DancingProperty); set => SetValue(DancingProperty, value); }
    public bool Waiting { get => GetValue(WaitingProperty); set => SetValue(WaitingProperty, value); }
    public bool Failed { get => GetValue(FailedProperty); set => SetValue(FailedProperty, value); }
    public double Spot { get => GetValue(SpotProperty); set => SetValue(SpotProperty, value); }

    private const double BarHeight = 46, LineTop = 15, LineHeight = 2, DotSize = 14, DotTop = 9, WordTop = 27, BlobSize = 32, BlobTop = -10, WordGap = 8, Overhang = 36;

    /// <summary>
    /// How far past the last point the blob stands when every step is done: its half, the dot's half and 5 between.
    /// On the last dot, the two of one yellow, the dot stuck out under its feet; past it, a finished plan reads apart
    /// from one on its last step.
    /// </summary>
    private const double PastEnd = BlobSize / 2 + DotSize / 2 + 5;

    private sealed record Point(Border Hit, Border Dot, TextBlock Word);

    private readonly Rectangle _line = new() { Classes = { "planLine" } };
    private readonly Rectangle _walked = new() { Classes = { "planLine", "walked" } };
    private readonly PartyBlob _blob = new() { IsHitTestVisible = false, ZIndex = 1 };
    private List<Point> _points = [];

    static PlanBar()
    {
        AffectsArrange<PlanBar>(SpotProperty);
        AffectsMeasure<PlanBar>(PlanProperty);
    }

    public PlanBar()
    {
        Height = BarHeight;
        Children.Add(_line);
        Children.Add(_walked);
        Children.Add(_blob);
        Transitions = [new DoubleTransition { Property = SpotProperty, Duration = TimeSpan.FromMilliseconds(800), Easing = new CubicEaseInOut() }];
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlanProperty)
            Rebuild();
        if (change.Property == PlanProperty || change.Property == WaitingProperty || change.Property == FailedProperty)
            ShowState();
        if (change.Property == DancingProperty)
            _blob.Dancing = Dancing;
    }

    /// <summary>The points anew when the steps differ; moving on along the same steps keeps them.</summary>
    private void Rebuild()
    {
        var steps = Plan?.Steps ?? [];
        if (_points.Select(p => p.Word.Text).SequenceEqual(steps.Select(s => s.Word))
            && _points.Select(p => ToolTip.GetTip(p.Hit) as string).SequenceEqual(steps.Select(s => s.Detail)))
            return;
        foreach (var point in _points)
            Children.RemoveAll([point.Hit, point.Dot, point.Word]);
        _points = [.. steps.Select(NewPoint)];
        foreach (var point in _points)
            Children.AddRange([point.Dot, point.Word, point.Hit]);
    }

    private static Point NewPoint(PlanStep step)
    {
        // Over the dot and the word, a little wider, so that a point is easy to hover.
        var hit = new Border { Background = Brushes.Transparent };
        ToolTip.SetTip(hit, step.Detail);
        ToolTip.SetPlacement(hit, PlacementMode.Top);
        ToolTip.SetVerticalOffset(hit, -12); // clear of the line and the blob: a tooltip opens 20 lower by default
        ToolTip.SetServiceEnabled(hit, step.Detail.Length > 0);
        AutomationProperties.SetName(hit, $"{step.Word}: {step.Detail}");
        return new Point(hit, new Border { Classes = { "planDot" } }, new TextBlock { Classes = { "planWord" }, Text = step.Word, IsHitTestVisible = false });
    }

    private void ShowState()
    {
        if (Plan is not { } plan)
            return;
        for (var i = 0; i < _points.Count; i++)
        {
            var (done, here) = (i < plan.At, i == plan.At);
            _points[i].Dot.Classes.Set("done", done);
            _points[i].Word.Classes.Set("done", done);
            _points[i].Word.Classes.Set("now", here && !Waiting && !Failed);
            _points[i].Word.Classes.Set("wait", here && Waiting);
            _points[i].Word.Classes.Set("fail", here && Failed);
        }
        // One spot past the last point when all is done: the blob walks off the end (see PastEnd).
        Spot = Math.Min(plan.At, _points.Count);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in new Control[] { _line, _walked, _blob })
            child.Measure(availableSize);
        foreach (var point in _points)
        {
            point.Word.Measure(Size.Infinity);
            point.Dot.Measure(new Size(DotSize, DotSize));
            point.Hit.Measure(availableSize);
        }
        return new Size(0, BarHeight);
    }

    /// <summary>How far from the current step words show: all of them if they fit, else its neighbours', else its own.</summary>
    private int Radius(double spacing, int at)
    {
        bool Fit(int radius)
        {
            var (first, last) = (Math.Max(at - radius, 0), Math.Min(at + radius, _points.Count - 1));
            for (var i = first; i < last; i++)
                if ((_points[i].Word.DesiredSize.Width + _points[i + 1].Word.DesiredSize.Width) / 2 + WordGap > spacing)
                    return false;
            return true;
        }
        return Fit(_points.Count) ? _points.Count : Fit(1) ? 1 : 0;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        var spacing = _points.Count > 1 ? width / (_points.Count - 1) : width;
        var last = Math.Max(_points.Count - 1, 0);
        var walked = _points.Count > 1 ? spacing * Math.Min(Spot, last) : 0;
        var blob = walked + Math.Max(Spot - last, 0) * PastEnd;
        _line.Arrange(new Rect(0, LineTop, width, LineHeight));
        _walked.Arrange(new Rect(0, LineTop, walked, LineHeight));
        _blob.Arrange(new Rect(blob - BlobSize / 2, BlobTop, BlobSize, BlobSize));

        var at = Math.Min(Plan?.At ?? 0, _points.Count - 1);
        var radius = Radius(spacing, at);
        var hitWidth = Math.Clamp(spacing, 24, 96);
        for (var i = 0; i < _points.Count; i++)
        {
            var x = spacing * i;
            var word = _points[i].Word;
            var wordWidth = word.DesiredSize.Width;
            word.Opacity = Math.Abs(i - at) <= radius ? 1 : 0;
            _points[i].Dot.Arrange(new Rect(x - DotSize / 2, DotTop, DotSize, DotSize));
            // The bar's margin leaves room for the words at its ends, a long one too.
            word.Arrange(new Rect(Math.Clamp(x - wordWidth / 2, -Overhang, Math.Max(width + Overhang - wordWidth, -Overhang)), WordTop, wordWidth, word.DesiredSize.Height));
            _points[i].Hit.Arrange(new Rect(x - hitWidth / 2, 0, hitWidth, BarHeight));
        }
        return finalSize;
    }
}
