using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux.Views.Image
{
    /// <summary>
    /// ♻️ Restore tab, ported from the WPF window's "♻️ Restore" TabItem. DataContext is the window's
    /// ImageGeneratorViewModel; the tab is bindings and commands only, so there is no code-behind
    /// logic to carry across.
    /// </summary>
    public partial class RestoreView : UserControl
    {
        public RestoreView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
