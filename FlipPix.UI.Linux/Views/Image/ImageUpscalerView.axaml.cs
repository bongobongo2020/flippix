using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux.Views.Image
{
    /// <summary>
    /// 🔍 Image Upscaler tab, ported from the WPF window's "🔍 Image Upscaler" TabItem. DataContext is the window's
    /// ImageGeneratorViewModel; the tab is bindings and commands only, so there is no code-behind
    /// logic to carry across.
    /// </summary>
    public partial class ImageUpscalerView : UserControl
    {
        public ImageUpscalerView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
