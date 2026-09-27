using Avalonia;
using Avalonia.Controls;

namespace FlipPix.Mobile.Views.Studio;

/// <summary>
/// The iPad layout. Below <see cref="CompactWidth"/> (portrait, most Split View sizes) it wears the
/// "compact" class: the sidebar folds to a rail and the composer panels narrow (Styles/Studio.axaml).
/// </summary>
public partial class StudioShell : UserControl
{
    public const double CompactWidth = 1100;

    public StudioShell() => InitializeComponent();

    /// <summary>
    /// The safe area (and an open keyboard, at the bottom): backgrounds reach the screen's edges, content
    /// stays inside. The sidebar ignores the right inset, the pages the left one, since each only touches one side.
    /// </summary>
    public void ApplyInsets(Thickness insets)
    {
        var side = new Thickness(insets.Left, insets.Top, 0, insets.Bottom);
        this.FindControl<Border>("SideWide")!.Padding = side;
        this.FindControl<Border>("SideRail")!.Padding = side;
        this.FindControl<Panel>("Pages")!.Margin = new Thickness(0, insets.Top, insets.Right, insets.Bottom);
        this.FindControl<StudioViewerView>("Viewer")!.Padding = insets;
        this.FindControl<StudioConnectView>("Welcome")!.Padding = insets;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Classes.Set("compact", e.NewSize.Width < CompactWidth);
    }
}
