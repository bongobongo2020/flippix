using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FlipPix.UI.Linux.Converters;

public class StatusToForegroundConverter : IValueConverter
{
    public static readonly StatusToForegroundConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString() switch
        {
            "Pending" => new SolidColorBrush(Color.Parse("#007BFF")),
            "Processing" => new SolidColorBrush(Color.Parse("#FF6B35")),
            "Completed" => new SolidColorBrush(Color.Parse("#28A745")),
            "Failed" => new SolidColorBrush(Color.Parse("#DC3545")),
            "Cancelled" => new SolidColorBrush(Color.Parse("#6C757D")),
            _ => new SolidColorBrush(Color.Parse("#6C757D"))
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
