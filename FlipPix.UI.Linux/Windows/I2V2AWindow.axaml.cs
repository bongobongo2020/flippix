using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FlipPix.UI.Linux.Services;
using FlipPix.UI.Linux.ViewModels;

namespace FlipPix.UI.Linux.Windows;

public partial class I2V2AWindow : Window
{
    private readonly I2V2AViewModel? _viewModel;
    private readonly INavigationService? _navigationService;
    private readonly WindowPositionService? _windowPositionService;

    public I2V2AWindow()
    {
        InitializeComponent();
    }

    public I2V2AWindow(I2V2AViewModel viewModel, INavigationService nav, WindowPositionService wps) : this()
    {
        _viewModel = viewModel;
        _navigationService = nav;
        _windowPositionService = wps;
        DataContext = viewModel;
        _windowPositionService?.LoadPosition("I2V2AWindow", this);
    }

    private void NavigateToHome(object sender, RoutedEventArgs e)
    {
        _windowPositionService?.SavePosition("I2V2AWindow", this);
        _navigationService?.NavigateToAndClose<FlipPixWindow>(this);
    }

    private void NavigateToImageGen(object sender, RoutedEventArgs e)
    {
        _windowPositionService?.SavePosition("I2V2AWindow", this);
        _navigationService?.NavigateToAndClose<ImageGeneratorWindow>(this);
    }

    private void NavigateToVideoGen(object sender, RoutedEventArgs e)
    {
        _windowPositionService?.SavePosition("I2V2AWindow", this);
        _navigationService?.NavigateToAndClose<VideoGeneratorWindow>(this);
    }

    private void NavigateToSettings(object sender, RoutedEventArgs e)
    {
        // Settings navigation if needed
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
