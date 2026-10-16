using System;
using Avalonia.Input;
using FlipPix.UI.Linux.Windows;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI;

/// <summary>Port of FlipPix.UI/StoryVideoWindow.xaml.cs.</summary>
public partial class StoryVideoWindow : AppWindow
{
    private readonly StoryVideoViewModel _viewModel;
    private readonly WindowPositionService _windowPositionService;

    /// <summary>For the XAML loader and previewer only.</summary>
    public StoryVideoWindow()
    {
        InitializeComponent();
        _viewModel = null!;
        _windowPositionService = null!;
    }

    public StoryVideoWindow(StoryVideoViewModel viewModel, WindowPositionService windowPositionService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
        Opened += (_, _) => EnsureOnScreen();
    }

    private void Header_MouseLeftButtonDown(object? sender, PointerPressedEventArgs e) => Header_PointerPressed(sender, e);

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is IDisposable disposable) disposable.Dispose();
        DataContext = null;
        base.OnClosed(e);
    }
}
