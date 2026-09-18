using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux.Views.Video
{
    /// <summary>
    /// 10Eros ConvRot tab, ported from the WPF window's "10Eros ConvRot" TabItem. The window sets
    /// DataContext to ErosConvRotVM; everything the tab does is commands and bindings on that
    /// ViewModel, so there is no code-behind logic to carry across.
    /// </summary>
    public partial class ErosConvRotView : UserControl
    {
        public ErosConvRotView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
