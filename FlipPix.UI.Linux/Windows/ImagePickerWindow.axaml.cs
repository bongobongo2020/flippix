using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI.Linux.Windows;

public partial class ImagePickerWindow : Window
{
    /// <summary>Tile width (128) plus its 4px margin on each side.</summary>
    private const double TileSlot = 136;

    private readonly ImagePickerViewModel _vm;
    private readonly Dictionary<Control, ImagePickerRow> _shownRows = new();

    public ImagePickerWindow(string? startDirectory = null)
    {
        InitializeComponent();
        _vm = new ImagePickerViewModel();
        _vm.FolderOpened += Vm_FolderOpened;
        DataContext = _vm;
        _vm.Initialize(startDirectory);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnClosed(EventArgs e)
    {
        _vm.FolderOpened -= Vm_FolderOpened;
        _vm.Dispose();
        base.OnClosed(e);
    }

    private void Vm_FolderOpened(object? sender, EventArgs e)
    {
        // A new folder starts at the top; a refresh or a re-flow on resize keeps the position.
        if (this.FindControl<ScrollViewer>("GridScroller") is { } scroller)
            scroller.Offset = default;
    }

    private void GridScroller_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // The rows panel sits inside an 8px margin on each side.
        _vm.Columns = (int)Math.Floor((e.NewSize.Width - 16) / TileSlot);
    }

    private void RowsList_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        var rows = _vm.Rows;
        if (e.Index < 0 || e.Index >= rows.Count) return;
        var row = rows[e.Index];
        if (_shownRows.Remove(e.Container, out var previous))
            _vm.RowHidden(previous);
        _shownRows[e.Container] = row;
        _vm.RowShown(row);
    }

    private void RowsList_ContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (_shownRows.Remove(e.Container, out var row))
            _vm.RowHidden(row);
    }

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
        // Checking the folder exists would be a blocking round trip on a share; the view model
        // lists it in the background and says so if it isn't there.
        if (e.Key == Key.Enter && sender is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text))
            _vm.CurrentPath = tb.Text.Trim();
    }
}
