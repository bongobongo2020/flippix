using Avalonia.Controls;
using Avalonia.Interactivity;
using FlipPix.Mobile.ViewModels;

namespace FlipPix.Mobile.Views;

public partial class VideoView : UserControl
{
    public VideoView() => InitializeComponent();

    private async void OnAddPhoto(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoViewModel vm) await vm.AddPhotosAsync(await PhotoPicker.PickAsync(this));
    }
}
