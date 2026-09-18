using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux.Views.Video
{
    /// <summary>
    /// MiniMax I2V tab, ported from the WPF window's "MiniMax I2V" TabItem. The window sets
    /// DataContext to MiniMaxI2VVM; everything the tab does is commands and bindings on that
    /// ViewModel, so there is no code-behind logic to carry across.
    /// </summary>
    public partial class MiniMaxI2VView : UserControl
    {
        public MiniMaxI2VView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
