using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using FlipPix.UI.Linux.Services;
using FlipPix.UI.Linux.ViewModels;

namespace FlipPix.UI.Linux.Windows;

public partial class FlipPixWindow : Window
{
    private readonly WindowPositionService? _windowPositionService;

    public FlipPixWindow()
    {
        InitializeComponent();
    }

    public FlipPixWindow(FlipPixViewModel viewModel, WindowPositionService wps) : this()
    {
        _windowPositionService = wps;
        DataContext = viewModel;
        // Position loaded on startup if needed
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
        DataContext = null;
        base.OnClosed(e);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
