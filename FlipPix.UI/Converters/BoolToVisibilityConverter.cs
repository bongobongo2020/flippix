using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FlipPix.UI.Converters
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                // If parameter is "Invert", reverse the logic
                if (parameter?.ToString()?.Equals("Invert", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return boolValue ? Visibility.Collapsed : Visibility.Visible;
                }
                return boolValue ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
            {
                // If parameter is "Invert", reverse the logic
                if (parameter?.ToString()?.Equals("Invert", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return visibility != Visibility.Visible;
                }
                return visibility == Visibility.Visible;
            }
            return false;
        }
    }
}
