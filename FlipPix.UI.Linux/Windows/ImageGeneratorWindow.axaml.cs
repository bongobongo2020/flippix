using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FlipPix.UI.Linux.Services;
using FlipPix.UI.Linux.ViewModels;
using FlipPix.Core.Services;
using FlipPix.UI.Linux.Windows;

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

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // Register TopLevel for file dialogs
        FileDialogService.SetTopLevel(TopLevel.GetTopLevel(this)!);
        _windowPositionService.LoadPosition("ImageGenerator", this);
    }

    private void OnClosing(object? sender, Avalonia.Controls.WindowClosingEventArgs e)
    {
        _windowPositionService.SavePosition("ImageGenerator", this);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settingsWindow = new SettingsWindow(_settingsService);
            settingsWindow.ShowDialog(this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error opening settings: {ex.Message}");
        }
    }

    private void ComfyUIButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsService?.Settings?.BaseUrl != null)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _settingsService.Settings.BaseUrl,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }
    }
}
