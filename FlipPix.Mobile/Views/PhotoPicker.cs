using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace FlipPix.Mobile.Views;

/// <summary>The system photo picker (Android: the Photos sheet), several at once.</summary>
public static class PhotoPicker
{
    /// <summary>Openers for the picked photos, or none when the picker was cancelled.</summary>
    public static async Task<IReadOnlyList<Func<Task<Stream>>>> PickAsync(Control owner)
    {
        var storage = TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (storage == null || !storage.CanOpen) return Array.Empty<Func<Task<Stream>>>();

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose photos",
            AllowMultiple = true,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
        });
        return files.Select(f => (Func<Task<Stream>>)(() => f.OpenReadAsync())).ToList();
    }
}
