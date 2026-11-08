using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace FlipPix.UI.Linux.Converters;

/// <summary>
/// A weight (0–1) as a star column width, for bars laid out as proportions of a grid — the 🎞 Remake
/// timeline. WPF binds the view model's "0.25*" strings straight to the column width; Avalonia's
/// compiled bindings take this converter on the plain weight instead.
/// </summary>
public class StarConverter : IValueConverter
{
    public static readonly StarConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new GridLength(Math.Max(0.0001, value is double d ? d : 0), GridUnitType.Star);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
