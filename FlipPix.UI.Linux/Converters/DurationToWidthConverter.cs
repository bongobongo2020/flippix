using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace FlipPix.UI.Linux.Converters;

/// <summary>
/// Converts a clip duration (in seconds) to a pixel width for timeline display.
/// Width = Duration * PixelsPerSecond, clamped to MinWidth. (Port of the WPF converter.)
/// </summary>
public class DurationToWidthConverter : IValueConverter
{
    public double PixelsPerSecond { get; set; } = 20;
    public double MinWidth { get; set; } = 60;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double duration = value switch
        {
            double d => d,
            int i => i,
            float f => f,
            _ => 5
        };
        return Math.Max(duration * PixelsPerSecond, MinWidth);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
