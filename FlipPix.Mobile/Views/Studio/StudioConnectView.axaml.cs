using Avalonia.Controls;

namespace FlipPix.Mobile.Views.Studio;

public partial class StudioConnectView : UserControl
{
    public StudioConnectView() => InitializeComponent();

    // The welcome takes a little under half a landscape iPad; in portrait or Split View the card has it all.
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (this.FindControl<Border>("Hero") is not { } hero) return;
        hero.IsVisible = e.NewSize.Width >= 1000;
        hero.Width = Math.Round(e.NewSize.Width * 0.46);
    }
}
