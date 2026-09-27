using Avalonia.Controls;
using Avalonia.Interactivity;
using FlipPix.One.ViewModels;

namespace FlipPix.One.Views;

public partial class VideoView : UserControl
{
    public VideoView() => InitializeComponent();

    private async void OnAddPhoto(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoViewModel vm) await vm.AddPicturesAsync(await PhotoPicker.PickAsync(this));
    }
}
