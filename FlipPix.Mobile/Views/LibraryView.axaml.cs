using Avalonia.Controls;
using FlipPix.Mobile.ViewModels;

namespace FlipPix.Mobile.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    // Two screens from the end, the next page is fetched, so scrolling rarely meets the bottom.
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer s || DataContext is not LibraryViewModel vm) return;
        if (s.Offset.Y + s.Viewport.Height * 3 >= s.Extent.Height) _ = vm.LoadMoreAsync();
    }
}
