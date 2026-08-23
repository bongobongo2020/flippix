using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FlipPix.UI.Linux.Services;

namespace FlipPix.UI.Linux.Views.Image
{
    /// <summary>
    /// Camera Angle tab, ported from the WPF window's "🎥 Camera Angle" TabItem. DataContext is
    /// the window's ImageGeneratorViewModel, so the bindings keep their "CameraAngle." prefix.
    /// </summary>
    public partial class CameraAngleView : UserControl
    {
        public CameraAngleView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        /// <summary>
        /// Opens a generated angle in the desktop's image viewer. WPF used ShellExecute on the
        /// path in the button's Tag; DesktopIntegration does the xdg-open equivalent.
        /// </summary>
        private void OpenOutputImage_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string path } && !string.IsNullOrEmpty(path))
                DesktopIntegration.OpenFile(path);
        }
    }
}
