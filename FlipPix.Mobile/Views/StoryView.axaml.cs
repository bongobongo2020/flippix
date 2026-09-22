using Avalonia.Controls;
using Avalonia.Interactivity;
using FlipPix.Mobile.ViewModels;

namespace FlipPix.Mobile.Views;

public partial class StoryView : UserControl
{
    public StoryView() => InitializeComponent();

    private async void OnAddPhoto(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StoryViewModel vm) await vm.AddPicturesAsync(await PhotoPicker.PickAsync(this));
    }
}
