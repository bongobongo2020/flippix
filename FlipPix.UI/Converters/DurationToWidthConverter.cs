using System;
using System.Globalization;
using System.Windows.Data;

namespace FlipPix.UI.Converters
{
    /// <summary>
    /// Converts a clip duration (in seconds) to a pixel width for timeline display.
    /// Width = Duration * PixelsPerSecond, clamped to MinWidth.
    /// </summary>
    public class DurationToWidthConverter : IValueConverter
    {
        /// <summary>
        /// Pixels per second of duration. Default is 20.
        /// </summary>
        public double PixelsPerSecond { get; set; } = 20;

        /// <summary>
        /// Minimum width for a clip, regardless of duration. Default is 60.
        /// </summary>
        public double MinWidth { get; set; } = 60;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double duration = 5; // default 5 seconds
            if (value is double d)
                duration = d;
            else if (value is int i)
                duration = i;
            else if (value is float f)
                duration = f;

            double width = duration * PixelsPerSecond;
            return Math.Max(width, MinWidth);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
