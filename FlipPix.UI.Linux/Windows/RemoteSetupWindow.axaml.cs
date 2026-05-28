using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FlipPix.Core.Services;

namespace FlipPix.UI.Linux.Windows;

public partial class RemoteSetupWindow : Window
{
    private readonly SettingsService _settingsService;

    public RemoteSetupWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;

        var s = settingsService.Settings;
        if (!string.IsNullOrEmpty(s.BaseUrl) && this.FindControl<TextBox>("ServerUrlBox") is { } urlBox)
            urlBox.Text = s.BaseUrl;
        if (!string.IsNullOrEmpty(s.RemoteOutputFolderPath) && this.FindControl<TextBox>("OutputFolderBox") is { } outBox)
            outBox.Text = s.RemoteOutputFolderPath;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Settings;
        if (this.FindControl<TextBox>("ServerUrlBox") is { } urlBox && !string.IsNullOrEmpty(urlBox.Text))
            settings.BaseUrl = urlBox.Text;
        if (this.FindControl<TextBox>("OutputFolderBox") is { } outputBox && !string.IsNullOrEmpty(outputBox.Text))
            settings.RemoteOutputFolderPath = outputBox.Text;
        _settingsService.SaveSettings(settings);
        Close(true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
