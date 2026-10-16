using System;
using Avalonia.Input;
using FlipPix.UI.Linux.Windows;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.UI;

/// <summary>Port of FlipPix.UI/VideoEnhanceWindow.xaml.cs.</summary>
public partial class VideoEnhanceWindow : AppWindow
{
    private readonly VideoEnhanceViewModel _viewModel;
    private readonly WindowPositionService _windowPositionService;

    /// <summary>For the XAML loader and previewer only.</summary>
    public VideoEnhanceWindow()
    {
        InitializeComponent();
        _viewModel = null!;
        _windowPositionService = null!;
    }

    public VideoEnhanceWindow(VideoEnhanceViewModel viewModel, WindowPositionService windowPositionService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
        Opened += (_, _) => EnsureOnScreen();
        _viewModel.PlayRequested += OnPlayRequested;
    }

    private void Header_MouseLeftButtonDown(object? sender, PointerPressedEventArgs e) => Header_PointerPressed(sender, e);

    private void OnPlayRequested(object? sender, EventArgs e)
    {
        if (VideoPlayer?.Source == null) return;
        VideoPlayer.Position = TimeSpan.Zero;
        VideoPlayer.Play();
    }

    protected override void OnClosed(EventArgs e)
    {
        VideoPlayer?.Stop();
        _viewModel.PlayRequested -= OnPlayRequested;
        if (_viewModel is IDisposable disposable) disposable.Dispose();
        DataContext = null;
        base.OnClosed(e);
    }
}
