using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    // The WPF window navigates back to the Image Generator window from its nav bar.
    // (The old XAML bound NavigateToImageGeneratorCommand, which never existed on
    // this window's ViewModel — an Avalonia binding that silently resolved to nothing,
    // so the button did nothing. A code-behind handler matches how the Image
    // Generator window's own nav buttons work.)
    private void NavImageGenerator_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var services = App.Services;
            if (services == null) return;
            var vm = services.GetService(typeof(ImageGeneratorViewModel)) as ImageGeneratorViewModel;
            var settingsService = services.GetService(typeof(FlipPix.Core.Services.SettingsService)) as FlipPix.Core.Services.SettingsService;
            var wps = services.GetService(typeof(WindowPositionService)) as WindowPositionService;
            if (vm != null && settingsService != null && wps != null)
                new ImageGeneratorWindow(vm, settingsService, wps).Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"NavImageGenerator error: {ex.Message}");
        }
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
