using System;
using System.Globalization;
using System.Windows.Data;

namespace FlipPix.UI.Converters
{
    /// <summary>
    /// A mute flag as a <see cref="System.Windows.Controls.MediaElement.Volume"/>: 0 when muted, otherwise
    /// MediaElement's own default of 0.5. Volume rather than IsMuted, so a tab's own mute binding is left alone.
    /// </summary>
    public class MutedToVolumeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? 0.0 : 0.5;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
