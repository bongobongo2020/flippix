using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace FlipPix.UI.Linux.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? b : false;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? b : false;
}
