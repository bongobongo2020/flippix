using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FlipPix.Core.Models;
using FlipPix.Core.Services;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace FlipPix.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly ComfyUISettings _originalSettings;
        private readonly SettingsService _settingsService;

        public SettingsWindow(SettingsService settingsService)
        {
            InitializeComponent();
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            // Clone the current settings so we can cancel if needed
            _originalSettings = CloneSettings(settingsService.Settings);

            // Set DataContext for binding
            DataContext = _originalSettings;

            // Update status on load
            UpdateStatus();
        }

        private ComfyUISettings CloneSettings(ComfyUISettings original)
        {
            return new ComfyUISettings
            {
                BaseUrl = original.BaseUrl,
                ConnectionTimeout = original.ConnectionTimeout,
                MaxRetries = original.MaxRetries,
                RetryDelayMilliseconds = original.RetryDelayMilliseconds,
                ComfyUIFolderPath = original.ComfyUIFolderPath,
                OutputFolderPath = original.OutputFolderPath,
                RemoteOutputFolderPath = original.RemoteOutputFolderPath,
                SavedCameraPrompts = original.SavedCameraPrompts
            };
        }

        private void BrowseComfyUIPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select ComfyUI Installation Folder",
                ShowNewFolderButton = false
            };

            if (!string.IsNullOrEmpty(ComfyUIPathTextBox.Text))
            {
                dialog.SelectedPath = ComfyUIPathTextBox.Text;
            }

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ComfyUIPathTextBox.Text = dialog.SelectedPath;
                UpdateStatus();
            }
        }

        private void BrowseOutputPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select ComfyUI Output Folder",
                ShowNewFolderButton = true
            };

            if (!string.IsNullOrEmpty(OutputPathTextBox.Text))
            {
                dialog.SelectedPath = OutputPathTextBox.Text;
            }

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                OutputPathTextBox.Text = dialog.SelectedPath;
                UpdateStatus();
            }
        }

        
        private void UpdateStatus()
        {
            try
            {
                var status = "Configuration Status:\n\n";

                // Check ComfyUI path
                if (string.IsNullOrEmpty(ComfyUIPathTextBox.Text))
                {
                    status += "❌ ComfyUI installation path not set\n";
                }
                else if (Directory.Exists(ComfyUIPathTextBox.Text))
                {
                    status += $"✅ ComfyUI path exists: {ComfyUIPathTextBox.Text}\n";
                }
                else
                {
                    status += $"❌ ComfyUI path not found: {ComfyUIPathTextBox.Text}\n";
                }

                // Check Output path
                if (string.IsNullOrEmpty(OutputPathTextBox.Text))
                {
                    status += "❌ Output folder path not set\n";
                }
                else if (Directory.Exists(OutputPathTextBox.Text))
                {
                    var pngFiles = Directory.GetFiles(OutputPathTextBox.Text, "*.png", SearchOption.TopDirectoryOnly).Length;
                    status += $"✅ Output folder exists: {OutputPathTextBox.Text} ({pngFiles} PNG files found)\n";
                }
                else
                {
                    status += $"❌ Output folder not found: {OutputPathTextBox.Text}\n";
                }

                // Check server URL
                if (Uri.TryCreate(ServerUrlTextBox.Text, UriKind.Absolute, out var uri))
                {
                    status += $"✅ Server URL valid: {ServerUrlTextBox.Text}\n";
                }
                else
                {
                    status += $"❌ Invalid server URL: {ServerUrlTextBox.Text}\n";
                }

                StatusTextBlock.Text = status.Trim();
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error checking configuration: {ex.Message}";
            }
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;

            try
            {
                var originalText = button.Content;
                button.Content = "Testing...";
                button.IsEnabled = false;

                // Create a temporary HTTP client to test the connection
                var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);

                var response = await client.GetAsync($"{ServerUrlTextBox.Text}/system_stats");

                if (response.IsSuccessStatusCode)
                {
                    MessageBox.Show("✅ Connection to ComfyUI successful!", "Connection Test",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"❌ Connection failed with status: {response.StatusCode}", "Connection Test",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Connection test failed: {ex.Message}", "Connection Test",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                button.Content = "Test Connection";
                button.IsEnabled = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Validate inputs
                if (string.IsNullOrWhiteSpace(OutputPathTextBox.Text))
                {
                    MessageBox.Show("Please specify the ComfyUI output folder path.", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!Directory.Exists(OutputPathTextBox.Text))
                {
                    var result = MessageBox.Show(
                        $"The output folder does not exist:\n\n{OutputPathTextBox.Text}\n\nDo you want to create it?",
                        "Create Output Folder",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        Directory.CreateDirectory(OutputPathTextBox.Text);
                    }
                    else
                    {
                        return;
                    }
                }

                // Update and save settings
                var newSettings = new ComfyUISettings
                {
                    BaseUrl = ServerUrlTextBox.Text?.Trim() ?? "http://localhost:8188",
                    ConnectionTimeout = int.TryParse(TimeoutTextBox.Text, out var timeout) ? timeout : 10000,
                    MaxRetries = int.TryParse(MaxRetriesTextBox.Text, out var retries) ? retries : 3,
                    RetryDelayMilliseconds = _originalSettings.RetryDelayMilliseconds,
                    ComfyUIFolderPath = ComfyUIPathTextBox.Text?.Trim() ?? "",
                    OutputFolderPath = OutputPathTextBox.Text?.Trim() ?? "",
                    RemoteOutputFolderPath = _originalSettings.RemoteOutputFolderPath,
                    SavedCameraPrompts = _originalSettings.SavedCameraPrompts
                };

                _settingsService.SaveSettings(newSettings);

                MessageBox.Show("Settings saved successfully!\n\nPlease restart the application for all changes to take effect.",
                    "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}