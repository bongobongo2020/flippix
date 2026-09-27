using Avalonia.Controls;
using Avalonia.Interactivity;
using FlipPix.Mobile.ViewModels;

namespace FlipPix.Mobile.Views.Studio;

public partial class StudioVideoView : UserControl
{
    public StudioVideoView() => InitializeComponent();

    private async void OnAddPhoto(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoViewModel vm) await vm.AddPhotosAsync(await PhotoPicker.PickAsync(this));
    }

    // The new take lands at the top of the list.
    private void OnMakeClicked(object? sender, RoutedEventArgs e)
    {
        TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        this.FindControl<ScrollViewer>("Scroller")?.ScrollToHome();
    }
}
