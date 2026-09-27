using Avalonia;
using Avalonia.Controls;

namespace FlipPix.Mobile.Controls;

/// <summary>
/// Takes the width it is offered and a height of width ÷ <see cref="Ratio"/>, and gives every child
/// exactly that box. The children never influence the size, so a tile keeps its picture's shape
/// before the picture exists, and a poster of a different shape can't stretch it.
/// </summary>
public class AspectPanel : Panel
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectPanel, double>(nameof(Ratio), 1.0);

    /// <summary>Width over height.</summary>
    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    static AspectPanel() => AffectsMeasure<AspectPanel>(RatioProperty);

    private Size Box(Size available)
    {
        var ratio = Ratio > 0 ? Ratio : 1.0;
        var width = double.IsInfinity(available.Width) ? 320 : available.Width;
        var height = width / ratio;
        if (height > available.Height) { height = available.Height; width = height * ratio; }
        return new Size(width, height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var box = Box(availableSize);
        foreach (var child in Children) child.Measure(box);
        return box;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var box = Box(finalSize);
        var origin = new Point((finalSize.Width - box.Width) / 2, 0);
        foreach (var child in Children) child.Arrange(new Rect(origin, box));
        return finalSize;
    }
}
