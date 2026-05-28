using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FlipPix.UI.Linux.ViewModels;

namespace FlipPix.UI.Linux.Windows;

public partial class ImagePickerWindow : Window
{
    private readonly ImagePickerViewModel _vm;

    public ImagePickerWindow(string? startDirectory = null)
    {
        InitializeComponent();
        _vm = new ImagePickerViewModel();
        DataContext = _vm;
        _vm.Initialize(startDirectory);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        var path = _vm.SelectedPath;
        if (string.IsNullOrEmpty(path) && _vm.SelectedItem?.IsDirectory == false)
            path = _vm.SelectedItem.FullPath;
        Close(string.IsNullOrEmpty(path) ? null : path);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Item_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ImagePickerItem item && !item.IsDirectory)
            _vm.SelectedItem = item;
    }

    private void Item_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ImagePickerItem item) return;
        if (item.IsDirectory)
            _vm.CurrentPath = item.FullPath;
        else
        {
            _vm.SelectedItem = item;
            Close(item.FullPath);
        }
    }

    private void PathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
        {
            var path = tb.Text ?? "";
            if (System.IO.Directory.Exists(path))
                _vm.CurrentPath = path;
        }
    }
}
