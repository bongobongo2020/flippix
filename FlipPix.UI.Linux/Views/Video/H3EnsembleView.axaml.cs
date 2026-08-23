using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux.Views.Video
{
    /// <summary>
    /// H3 Ensemble tab, ported from the WPF window's "H3 Ensemble" TabItem. The window sets
    /// DataContext to H3EnsembleVM; everything the tab does is commands and bindings on that
    /// ViewModel, so there is no code-behind logic to carry across.
    /// </summary>
    public partial class H3EnsembleView : UserControl
    {
        public H3EnsembleView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
