using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using FlipPix.UI.Linux.Services;
using FlipPix.UI.Linux.ViewModels;
using FlipPix.Core.Services;

namespace FlipPix.UI.Linux.Windows;

public partial class ImageGeneratorWindow : Window
{
    private readonly ImageGeneratorViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly WindowPositionService _windowPositionService;

    public ImageGeneratorWindow(ImageGeneratorViewModel viewModel, SettingsService settingsService, WindowPositionService windowPositionService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsService = settingsService;
        _windowPositionService = windowPositionService;
        DataContext = viewModel;

        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpened(object? sender, EventArgs e)
    {
        FileDialogService.SetTopLevel(TopLevel.GetTopLevel(this)!);
        _windowPositionService.LoadPosition("ImageGenerator", this);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _windowPositionService.SavePosition("ImageGenerator", this);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        try { new SettingsWindow(_settingsService).ShowDialog(this); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Settings error: {ex.Message}"); }
    }

    private void NavVideoGenerator_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var services = App.Services;
            if (services == null) return;
            var vm = services.GetService(typeof(VideoGeneratorViewModel)) as VideoGeneratorViewModel;
            var wps = services.GetService(typeof(WindowPositionService)) as WindowPositionService;
            if (vm != null && wps != null) new VideoGeneratorWindow(vm, wps).Show();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"NavVideoGenerator error: {ex.Message}"); }
    }

    private void NavVideoEnhance_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var services = App.Services;
            if (services == null) return;
            var vm = services.GetService(typeof(FlipPix.UI.Linux.ViewModels.Video.VideoEnhanceViewModel)) as FlipPix.UI.Linux.ViewModels.Video.VideoEnhanceViewModel;
            var wps = services.GetService(typeof(WindowPositionService)) as WindowPositionService;
            if (vm != null && wps != null) new VideoEnhanceWindow(vm, wps).Show();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"NavVideoEnhance error: {ex.Message}"); }
    }
}
