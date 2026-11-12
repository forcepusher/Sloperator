namespace Sloperator.Core.Media;

/// <summary>
/// A stroke of the Image editor's brush over a picture, for a mask of any shape (the user, 2026-11-12: "Do we
/// support inpainting mask?"; until then a part to redraw was a box): the points it went through, in parts of the
/// picture (0 the left or top, 1 the right or bottom), and how wide the brush was, in parts of the picture's width.
/// One point is a dab.
/// </summary>
public sealed record BrushStroke(IReadOnlyList<(double X, double Y)> Points, double Width)
{
    /// <summary>The brush's width in a new picture's editor, in parts of the picture's width.</summary>
    public const double DefaultWidth = 0.08;

    /// <summary>The thinnest and the widest brush, in parts of the picture's width.</summary>
    public const double MinWidth = 0.01, MaxWidth = 0.4;
}
