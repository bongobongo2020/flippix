using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using FlipPix.Mobile.ViewModels;

namespace FlipPix.Mobile.Views.Studio;

public partial class StudioImageView : UserControl
{
    public StudioImageView() => InitializeComponent();

    // The sheet keeps tiles about 190 points wide: three columns beside the composer on a 13-inch, five without it.
    private void FitSheet()
    {
        var sheet = this.FindControl<ItemsControl>("Sheet");
        if (sheet?.ItemsPanelRoot is UniformGrid grid && sheet.Bounds.Width > 0)
            grid.Columns = Math.Clamp((int)(sheet.Bounds.Width / 190), 2, 6);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Avalonia.Threading.Dispatcher.UIThread.Post(FitSheet, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    // Put the keyboard away and scroll back to the top, where the new tiles just landed.
    private void OnMakeClicked(object? sender, RoutedEventArgs e)
    {
        TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        this.FindControl<ScrollViewer>("Scroller")?.ScrollToHome();
    }

    private async void OnFromPhoto(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImageViewModel vm) return;
        var photos = await PhotoPicker.PickAsync(this, allowMany: false);
        if (photos.Count > 0) await vm.PromptFromPhotoAsync(photos[0]);
    }
}
