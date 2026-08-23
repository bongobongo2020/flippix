using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux.Views.Video
{
    /// <summary>
    /// MiniMax Character tab, ported from the WPF window's "MiniMax Character" TabItem. The window sets
    /// DataContext to MiniMaxCharacterVM; everything the tab does is commands and bindings on that
    /// ViewModel, so there is no code-behind logic to carry across.
    /// </summary>
    public partial class MiniMaxCharacterView : UserControl
    {
        public MiniMaxCharacterView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
