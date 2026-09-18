using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux.Views.Image
{
    /// <summary>
    /// 📷 Amateur tab, ported from the WPF window's "📷 Amateur" TabItem. DataContext is the window's
    /// ImageGeneratorViewModel; the tab is bindings and commands only, so there is no code-behind
    /// logic to carry across.
    /// </summary>
    public partial class AmateurView : UserControl
    {
        public AmateurView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
