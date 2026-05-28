using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FlipPix.Core.Services;

namespace FlipPix.UI.Linux.Windows;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;

    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        LoadSettings();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Settings;
        if (this.FindControl<TextBox>("BaseUrlBox") is { } baseUrlBox)
            baseUrlBox.Text = settings.BaseUrl ?? "http://127.0.0.1:8188";
        if (this.FindControl<TextBox>("OutputFolderBox") is { } outputBox)
            outputBox.Text = settings.OutputFolderPath ?? string.Empty;
        if (this.FindControl<TextBox>("ComfyUIFolderBox") is { } comfyBox)
            comfyBox.Text = settings.ComfyUIFolderPath ?? string.Empty;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Settings;
        if (this.FindControl<TextBox>("BaseUrlBox") is { } baseUrlBox)
            settings.BaseUrl = baseUrlBox.Text ?? settings.BaseUrl;
        if (this.FindControl<TextBox>("OutputFolderBox") is { } outputBox)
            settings.OutputFolderPath = outputBox.Text ?? settings.OutputFolderPath;
        if (this.FindControl<TextBox>("ComfyUIFolderBox") is { } comfyBox)
            settings.ComfyUIFolderPath = comfyBox.Text ?? settings.ComfyUIFolderPath;
        _settingsService.SaveSettings(settings);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
