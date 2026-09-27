using Avalonia.Controls;

namespace FlipPix.Mobile.Views;

/// <summary>
/// The phone layout: a header, four pages and a bottom bar. <see cref="AppShell"/> hosts it on narrow
/// screens and handles the safe area, the keyboard and the back gesture for it.
/// </summary>
public partial class MainView : UserControl
{
    public MainView() => InitializeComponent();
}
