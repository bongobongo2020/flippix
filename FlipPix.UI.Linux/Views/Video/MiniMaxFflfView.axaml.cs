using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux.Views.Video
{
    /// <summary>
    /// MiniMax FFLF tab, ported from the WPF window's "MiniMax FFLF" TabItem. The window sets
    /// DataContext to MiniMaxFflfVM; everything the tab does is commands and bindings on that
    /// ViewModel, so there is no code-behind logic to carry across.
    /// </summary>
    public partial class MiniMaxFflfView : UserControl
    {
        public MiniMaxFflfView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
