using Avalonia.Controls;
using FlipPix.Mobile.ViewModels;

namespace FlipPix.Mobile.Views.Studio;

public partial class StudioLibraryView : UserControl
{
    /// <summary>About this wide a tile, give or take; the grid fits as many whole columns as it can.</summary>
    private const double TileWidth = 210;

    public StudioLibraryView() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (DataContext is not LibraryViewModel vm || e.NewSize.Width <= 0) return;
        var usable = e.NewSize.Width - 60; // the grid's side margins
        vm.Columns = Math.Max(3, (int)(usable / TileWidth));
    }

    // Two screens from the end, the next page is fetched, so scrolling rarely meets the bottom.
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer s || DataContext is not LibraryViewModel vm) return;
        if (s.Offset.Y + s.Viewport.Height * 3 >= s.Extent.Height) _ = vm.LoadMoreAsync();
    }
}
