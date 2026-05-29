using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using FlipPix.UI.Linux.ViewModels;

namespace FlipPix.UI.Linux.Windows;

public partial class ImageAnalyzerWindow : Window
{
    private readonly ImageAnalyzerViewModel? _viewModel;

    public ImageAnalyzerWindow()
    {
        InitializeComponent();
    }

    public ImageAnalyzerWindow(ImageAnalyzerViewModel viewModel) : this()
    {
        _viewModel = viewModel;
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
