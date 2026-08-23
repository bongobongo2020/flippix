using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux.Views.Video
{
    /// <summary>
    /// H3 Cast tab, ported from the WPF window's "H3 Cast" TabItem. The window sets
    /// DataContext to H3CastVM; everything the tab does is commands and bindings on that
    /// ViewModel, so there is no code-behind logic to carry across.
    /// </summary>
    public partial class H3CastView : UserControl
    {
        public H3CastView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
