using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux;

public partial class SplashWindow : Avalonia.Controls.Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
