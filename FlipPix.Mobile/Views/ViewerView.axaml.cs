using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FlipPix.Mobile.Controls;
using FlipPix.Mobile.Services;
using FlipPix.Mobile.ViewModels;

namespace FlipPix.Mobile.Views;

public partial class ViewerView : UserControl
{
    private Point? _pressedAt;

    public ViewerView()
    {
        InitializeComponent();
        var stage = this.FindControl<Panel>("Stage")!;
        stage.PointerPressed += (_, e) => _pressedAt = e.GetPosition(this);
        stage.PointerReleased += OnStageReleased;

        // Without a native player (headless checks, desktop preview) the surface stays empty; say so.
        this.FindControl<TextBlock>("NoPlayer")!.IsVisible = false;
        DataContextChanged += (_, _) => Watch();
    }

    private void Watch()
    {
        if (DataContext is not ViewerViewModel vm) return;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ViewerViewModel.IsVideo) or nameof(ViewerViewModel.Picture) or nameof(ViewerViewModel.IsImage))
                UpdateStage(vm);
        };
        UpdateStage(vm);
    }

    private void UpdateStage(ViewerViewModel vm)
    {
        this.FindControl<TextBlock>("NoPlayer")!.IsVisible = vm.IsVideo && !VideoSurface.IsSupported;
        this.FindControl<Border>("PictureWait")!.IsVisible = vm.IsImage && vm.Picture == null;
    }

    // A sideways swipe on a picture moves to the next or previous one. Videos keep their touches:
    // the native player draws over this panel and handles its own.
    private void OnStageReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressedAt is not { } start || DataContext is not ViewerViewModel vm) return;
        _pressedAt = null;
        var end = e.GetPosition(this);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (Math.Abs(dx) < 60 || Math.Abs(dx) < Math.Abs(dy) * 1.5) return;
        if (dx < 0) vm.Next();
        else vm.Previous();
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewerViewModel vm) await SaveCurrentAsync(this, vm);
    }

    /// <summary>
    /// The system save sheet: the person picks where the copy goes (Downloads, Pictures, Drive). iOS has
    /// no save sheet, so there the copy goes straight into the app's own folder. Shared with the studio
    /// viewer.
    /// </summary>
    internal static async Task SaveCurrentAsync(Control owner, ViewerViewModel vm)
    {
        if (vm.Current is not { } entry) return;
        if (DeviceInfo.SaveFolder is { } folder)
        {
            await SaveIntoFolderAsync(vm, entry, folder);
            return;
        }
        var storage = TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (storage == null || !storage.CanSave) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = entry.IsVideo ? "Save video" : "Save picture",
            SuggestedFileName = entry.FileName,
            ShowOverwritePrompt = true,
        });
        if (file == null) return;
        await using var stream = await file.OpenWriteAsync();
        await vm.SaveToAsync(stream);
    }

    // Never over an earlier save: "name.png", then "name (2).png", and so on.
    private static async Task SaveIntoFolderAsync(ViewerViewModel vm, ViewerEntry entry, string folder)
    {
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(entry.FileName);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var path = Path.Combine(folder, name);
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}){extension}");

        bool saved;
        await using (var stream = File.Create(path))
            saved = await vm.SaveToAsync(stream, $"Saved to Files › On My {DeviceInfo.Noun} › FlipPix.");
        if (!saved) File.Delete(path);
    }
}
