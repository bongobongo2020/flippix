using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace FlipPix.UI.Linux.ViewModels;

public class ImagePickerItem : INotifyPropertyChanged
{
    private Bitmap? _thumbnail;
    private bool _isSelected;

    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ImagePickerViewModel : INotifyPropertyChanged
{
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tiff", ".tif" };

    private string _currentPath = "";
    private ImagePickerItem? _selectedItem;
    private string _selectedPath = "";
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<ImagePickerItem> Items { get; } = new();

    public string CurrentPath
    {
        get => _currentPath;
        set
        {
            if (_currentPath == value) return;
            _currentPath = value;
            OnPropertyChanged();
            if (Directory.Exists(value))
                LoadDirectory(value);
        }
    }

    public ImagePickerItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem != null) _selectedItem.IsSelected = false;
            _selectedItem = value;
            if (_selectedItem != null) _selectedItem.IsSelected = true;
            if (_selectedItem?.IsDirectory == false)
                SelectedPath = _selectedItem.FullPath;
            OnPropertyChanged();
        }
    }

    public string SelectedPath
    {
        get => _selectedPath;
        set { _selectedPath = value; OnPropertyChanged(); }
    }

    public bool CanGoUp => !string.IsNullOrEmpty(Directory.GetParent(_currentPath)?.FullName);

    public RelayCommand GoUpCommand { get; }

    public ImagePickerViewModel()
    {
        GoUpCommand = new RelayCommand(GoUp, () => CanGoUp);
    }

    public void Initialize(string? startDirectory)
    {
        var dir = startDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _currentPath = dir;
        OnPropertyChanged(nameof(CurrentPath));
        LoadDirectory(dir);
    }

    private void GoUp()
    {
        var parent = Directory.GetParent(_currentPath)?.FullName;
        if (!string.IsNullOrEmpty(parent))
            CurrentPath = parent;
    }

    public void NavigateTo(ImagePickerItem item)
    {
        if (item.IsDirectory)
            CurrentPath = item.FullPath;
        else
            SelectedItem = item;
    }

    public void LoadDirectory(string path)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var cts = _loadCts;

        Items.Clear();
        SelectedItem = null;
        GoUpCommand.NotifyCanExecuteChanged();

        if (!Directory.Exists(path)) return;

        var imageItems = new List<ImagePickerItem>();

        try
        {
            foreach (var d in Directory.GetDirectories(path).OrderBy(Path.GetFileName))
                Items.Add(new ImagePickerItem { Name = Path.GetFileName(d)!, FullPath = d, IsDirectory = true });
        }
        catch { }

        try
        {
            foreach (var f in Directory.GetFiles(path)
                         .Where(f => ImageExts.Contains(Path.GetExtension(f)))
                         .OrderBy(Path.GetFileName))
            {
                var item = new ImagePickerItem { Name = Path.GetFileName(f)!, FullPath = f };
                Items.Add(item);
                imageItems.Add(item);
            }
        }
        catch { }

        _ = LoadThumbnailsAsync(imageItems, cts.Token);
    }

    private async Task LoadThumbnailsAsync(List<ImagePickerItem> items, CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var path = item.FullPath;
                var bmp = await Task.Run(() =>
                {
                    try
                    {
                        using var stream = File.OpenRead(path);
                        return Bitmap.DecodeToWidth(stream, 120);
                    }
                    catch { return null; }
                }, ct);

                if (ct.IsCancellationRequested) return;
                if (bmp != null)
                    await Dispatcher.UIThread.InvokeAsync(() => item.Thumbnail = bmp);
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
