using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sloperator.App.Views;

/// <summary>
/// A sound's small waveform in the Audio editor's list (<see cref="ViewModels.AudioItem.Peaks"/>): each
/// column's lowest and highest, grey, drawn at the screen's own pixels so it stays sharp at any scale.
/// </summary>
public sealed class WaveThumb : Control
{
    public static readonly StyledProperty<(float Min, float Max)[]?> PeaksProperty = AvaloniaProperty.Register<WaveThumb, (float Min, float Max)[]?>(nameof(Peaks));

    public (float Min, float Max)[]? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }

    /// <summary>BbMuted, as the File inspector's ruler labels.</summary>
    private static readonly IBrush Wave = new SolidColorBrush(Color.FromRgb(0xA2, 0xA2, 0xA2));

    static WaveThumb()
    {
        AffectsRender<WaveThumb>(PeaksProperty);
        ClipToBoundsProperty.OverrideDefaultValue<WaveThumb>(true);
    }

    public override void Render(DrawingContext context)
    {
        if (Peaks is not { Length: > 0 } peaks)
            return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var (width, height) = (Bounds.Width, Bounds.Height);
        var columns = (int)Math.Round(width * scaling);
        var mid = height / 2;
        var half = height / 2 - 2 / scaling;
        for (var p = 0; p < columns; p++)
        {
            var (a, b) = (p * peaks.Length / columns, Math.Max(p * peaks.Length / columns + 1, (p + 1) * peaks.Length / columns));
            float lo = 0, hi = 0;
            for (var i = a; i < b && i < peaks.Length; i++)
            {
                lo = Math.Min(lo, peaks[i].Min);
                hi = Math.Max(hi, peaks[i].Max);
            }
            // A pixel at least, so silence still shows as a line.
            var top = Math.Floor((mid - hi * half) * scaling) / scaling;
            var bottom = Math.Max(top + 1 / scaling, Math.Ceiling((mid - lo * half) * scaling) / scaling);
            context.FillRectangle(Wave, new Rect(p / scaling, top, 1 / scaling, bottom - top));
        }
    }
}
