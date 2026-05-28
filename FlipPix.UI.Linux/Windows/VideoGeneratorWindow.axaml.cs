using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using FlipPix.UI.Linux.Services;
using FlipPix.UI.Linux.ViewModels;

namespace FlipPix.UI.Linux.Windows;

public partial class VideoGeneratorWindow : Window
{
    private readonly VideoGeneratorViewModel? _viewModel;
    private readonly WindowPositionService? _windowPositionService;

    public VideoGeneratorWindow()
    {
        InitializeComponent();
    }

    public VideoGeneratorWindow(VideoGeneratorViewModel viewModel, WindowPositionService wps) : this()
    {
        _viewModel = viewModel;
        _windowPositionService = wps;
        DataContext = viewModel;
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is IDisposable disposable)
            disposable.Dispose();
        DataContext = null;
        base.OnClosed(e);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
