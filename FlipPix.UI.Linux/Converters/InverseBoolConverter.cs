using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace FlipPix.UI.Linux.Converters;

/// <summary>
/// Negates a bool. WPF spells this as a BooleanToVisibilityConverter with an inverted
/// parameter or a DataTrigger; on Avalonia the same intent is IsVisible="{Binding X,
/// Converter={StaticResource InverseBoolConverter}}".
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;
}
