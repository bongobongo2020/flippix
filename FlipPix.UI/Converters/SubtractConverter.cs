using System;
using System.Globalization;
using System.Windows.Data;

namespace FlipPix.UI.Converters
{
    /// <summary>
    /// Returns <c>value - parameter</c> as a double, clamped at 0. Used to size a panel to its
    /// host viewport minus a fixed allowance (e.g. tab-header + margins) so an inner Grid with
    /// star rows can fill the visible area instead of growing unbounded inside a ScrollViewer.
    /// </summary>
    public class SubtractConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double v = value is double d ? d : 0;
            double p = 0;
            if (parameter != null)
                double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out p);
            var result = v - p;
            return result < 0 ? 0 : result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
