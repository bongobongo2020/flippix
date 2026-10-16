using System;
using System.IO;
using System.Linq;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FlipPix.Core.Services;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace FlipPix.UI.Linux.Windows;

/// <summary>Port of FlipPix.UI/RemoteSetupWindow.xaml.cs: server address + its output folder.</summary>
public partial class RemoteSetupWindow : DialogWindow
{
    private readonly SettingsService _settingsService;

    public string ServerUrl { get; private set; } = string.Empty;
    public string RemoteOutputFolder { get; private set; } = string.Empty;

    public RemoteSetupWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

        if (!string.IsNullOrEmpty(_settingsService.Settings.BaseUrl))
            ServerUrlTextBox.Text = _settingsService.Settings.BaseUrl;
        if (!string.IsNullOrEmpty(_settingsService.Settings.RemoteOutputFolderPath))
            RemoteOutputFolderTextBox.Text = _settingsService.Settings.RemoteOutputFolderPath;
        else if (DesktopIntegration.IsChromeOS && Directory.Exists("/mnt/chromeos"))
            RemoteOutputFolderTextBox.Watermark = "/mnt/chromeos/… (a network share you shared with Linux)";
    }

    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var start = RemoteOutputFolderTextBox.Text;
        if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
            start = Directory.Exists("/mnt/chromeos") ? "/mnt/chromeos" : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the network path to the remote ComfyUI output folder",
            AllowMultiple = false,
            SuggestedStartLocation = start != null ? await StorageProvider.TryGetFolderFromPathAsync(start) : null
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) RemoteOutputFolderTextBox.Text = path;
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            ServerUrl = (ServerUrlTextBox.Text ?? string.Empty).Trim();
            RemoteOutputFolder = (RemoteOutputFolderTextBox.Text ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(ServerUrl))
            {
                MessageBox.Show("Please enter a server URL.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(RemoteOutputFolder))
            {
                MessageBox.Show("Please select a remote output folder.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!Directory.Exists(RemoteOutputFolder))
            {
                var hint = DesktopIntegration.IsChromeOS
                    ? "\n\nOn a Chromebook: add the PC's shared ComfyUI output folder in the Files app " +
                      "(⋮ → Services → Network file share / SMB), right-click it → \"Share with Linux\", " +
                      "then pick it here under /mnt/chromeos."
                    : string.Empty;
                MessageBox.Show("The selected remote output folder is not accessible. Please check the network path." + hint,
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settingsService.Settings.BaseUrl = ServerUrl;
            _settingsService.Settings.RemoteOutputFolderPath = RemoteOutputFolder;
            _settingsService.SaveSettings(_settingsService.Settings);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => DialogResult = false;
}
