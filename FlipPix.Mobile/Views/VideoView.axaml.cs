using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FlipPix.Mobile.ViewModels;

namespace FlipPix.Mobile.Views;

public partial class VideoView : UserControl
{
    public VideoView() => InitializeComponent();

    // The system photo picker (Android's is the Photos sheet). Several at once, up to the free slots.
    private async void OnAddPhoto(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VideoViewModel vm) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null || !storage.CanOpen) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose photos",
            AllowMultiple = true,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
        });
        await vm.AddPicturesAsync(files.Select(f => (Func<Task<Stream>>)(() => f.OpenReadAsync())));
    }
}
