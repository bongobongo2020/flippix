using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FlipPix.UI.Converters
{
    /// <summary>
    /// Visible when the bound value is there, Collapsed when it is null — for a thumbnail that is decoded in
    /// the background and simply is not on the row until it has been, rather than leaving a hole where it
    /// will be.
    /// </summary>
    public class NotNullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value == null ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }
}
