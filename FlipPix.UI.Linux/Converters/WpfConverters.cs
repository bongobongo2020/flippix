using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FlipPix.UI.Linux.Converters;

/// <summary>
/// Converters the WPF→Avalonia port emits (tools/xaml-port). WPF did these conversions itself,
/// or through triggers that Avalonia expresses as bound style classes.
/// </summary>
public static class WpfConverters
{
    /// <summary>
    /// A DataTrigger's test: true when the value equals ConverterParameter, compared the way WPF
    /// compares a trigger Value (enums and numbers by their text, case-insensitively for bools).
    /// </summary>
    public static readonly IValueConverter Equal = new FuncValueConverter<object?, object?, bool>((value, parameter) =>
    {
        if (value == null) return parameter == null || parameter as string == "{x:Null}";
        var a = System.Convert.ToString(value, CultureInfo.InvariantCulture);
        var b = System.Convert.ToString(parameter, CultureInfo.InvariantCulture);
        return string.Equals(a, b, value is bool || value is Enum ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    });

    /// <summary>A WPF Visibility (or a bool) → IsVisible.</summary>
    public static readonly IValueConverter VisibilityToBool = new FuncValueConverter<object?, bool>(value => value switch
    {
        System.Windows.Visibility v => v == System.Windows.Visibility.Visible,
        bool b => b,
        null => false,
        string s => s == "Visible",
        _ => true
    });

    /// <summary>The WPF value types the shared view models expose → their Avalonia counterparts.</summary>
    public static readonly IValueConverter ToAvalonia = new ToAvaloniaConverter();

    private sealed class ToAvaloniaConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
        {
            System.Windows.Media.Brush b => b.ToAvalonia(),
            System.Windows.Media.Color c => targetType == typeof(Color)
                ? Color.FromArgb(c.A, c.R, c.G, c.B)
                : new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)),
            System.Windows.Thickness t => new Thickness(t.Left, t.Top, t.Right, t.Bottom),
            System.Windows.FontWeight w => (FontWeight)w.Weight,
            System.Windows.Visibility v => v == System.Windows.Visibility.Visible,
            System.Windows.Media.Imaging.BitmapSource s => s.AvaloniaBitmap,
            _ => value
        };

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
