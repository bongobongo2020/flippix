using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux.Views.Video
{
    /// <summary>
    /// H3 Cast Hybrid tab, ported from the WPF window's "H3 Cast Hybrid" TabItem. The window sets
    /// DataContext to H3CastHybridVM; everything the tab does is commands and bindings on that
    /// ViewModel, so there is no code-behind logic to carry across.
    /// </summary>
    public partial class H3CastHybridView : UserControl
    {
        public H3CastHybridView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
