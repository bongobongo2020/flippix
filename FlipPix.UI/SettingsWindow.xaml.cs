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
                SavedCameraPrompts = original.SavedCameraPrompts,
                AutoRestartComfyUI = original.AutoRestartComfyUI,
                ComfyUIRestartScriptPath = original.ComfyUIRestartScriptPath,
                ComfyUIRestartDelaySeconds = original.ComfyUIRestartDelaySeconds,
                ComfyUIStartupTimeoutSeconds = original.ComfyUIStartupTimeoutSeconds
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

        private void BrowseRestartScript_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select ComfyUI Restart Script",
                Filter = "Batch Files (*.bat)|*.bat|All Files (*.*)|*.*",
                FileName = "run_nvidia_gpu.bat"
            };

            if (!string.IsNullOrEmpty(ComfyUIRestartScriptTextBox.Text))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(ComfyUIRestartScriptTextBox.Text);
                dialog.FileName = Path.GetFileName(ComfyUIRestartScriptTextBox.Text);
            }

            if (dialog.ShowDialog() == true)
            {
                ComfyUIRestartScriptTextBox.Text = dialog.FileName;
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

        private async void TestLMStudioConnection_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var server = LMStudioServerTextBox.Text.Trim();
                var port = LMStudioPortTextBox.Text.Trim();

                if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(port))
                {
                    MessageBox.Show("Please enter both server and port.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var baseUrl = $"http://{server}:{port}";
                var results = new System.Text.StringBuilder();
                results.AppendLine($"Testing connection to: {baseUrl}");
                results.AppendLine();

                // Test 1: Basic TCP connectivity
                results.AppendLine("1. Testing TCP connectivity...");
                try
                {
                    using var tcpClient = new System.Net.Sockets.TcpClient();
                    await tcpClient.ConnectAsync(server, int.Parse(port));
                    results.AppendLine("   ✓ TCP connection successful");
                }
                catch (Exception ex)
                {
                    results.AppendLine($"   ✗ TCP connection failed: {ex.Message}");
                }

                results.AppendLine();

                // Test 2: HTTP connectivity
                results.AppendLine("2. Testing HTTP connectivity...");
                try
                {
                    using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };

                    // Try root endpoint first
                    try
                    {
                        var rootResponse = await httpClient.GetAsync($"{baseUrl}/");
                        results.AppendLine($"   ✓ Root endpoint: {rootResponse.StatusCode}");
                    }
                    catch (Exception ex)
                    {
                        results.AppendLine($"   ✗ Root endpoint failed: {ex.Message}");
                    }

                    // Try models endpoint
                    try
                    {
                        var modelsResponse = await httpClient.GetAsync($"{baseUrl}/v1/models");
                        if (modelsResponse.IsSuccessStatusCode)
                        {
                            results.AppendLine($"   ✓ Models endpoint: {modelsResponse.StatusCode}");
                            var content = await modelsResponse.Content.ReadAsStringAsync();
                            results.AppendLine($"   ✓ Response received ({content.Length} chars)");
                        }
                        else
                        {
                            results.AppendLine($"   ✗ Models endpoint failed: {modelsResponse.StatusCode}");
                            var errorContent = await modelsResponse.Content.ReadAsStringAsync();
                            results.AppendLine($"   ✗ Error: {errorContent}");
                        }
                    }
                    catch (Exception ex)
                    {
                        results.AppendLine($"   ✗ Models endpoint failed: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    results.AppendLine($"   ✗ HTTP connectivity failed: {ex.Message}");
                }

                results.AppendLine();
                // Test 3: DNS Resolution
                results.AppendLine();
                results.AppendLine("3. Testing DNS resolution...");
                try
                {
                    var addresses = System.Net.Dns.GetHostAddresses(server);
                    if (addresses.Length > 0)
                    {
                        results.AppendLine($"   ✓ DNS resolution successful");
                        foreach (var addr in addresses)
                        {
                            results.AppendLine($"   ✓ {addr}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    results.AppendLine($"   ✗ DNS resolution failed: {ex.Message}");
                    results.AppendLine("   ✗ Try using the IP address instead of hostname");
                }

                results.AppendLine();
                results.AppendLine("4. Troubleshooting suggestions:");
                results.AppendLine("   • If DNS fails, try using the IP address directly");
                results.AppendLine("   • Ensure LM Studio is running on the remote machine");
                results.AppendLine("   • Verify port 1234 is open in firewall");
                results.AppendLine("   • Make sure LM Studio accepts remote connections");
                results.AppendLine("   • Try IP address like: 192.168.1.100 or similar");

                MessageBox.Show(results.ToString(), "LM Studio Connection Test Results",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error running connection test: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RefreshLMStudioModels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var server = LMStudioServerTextBox.Text.Trim();
                var port = LMStudioPortTextBox.Text.Trim();

                if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(port))
                {
                    MessageBox.Show("Please enter both server and port.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var baseUrl = $"http://{server}:{port}";
                using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };

                var response = await httpClient.GetAsync($"{baseUrl}/v1/models");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    MessageBox.Show("Successfully connected to LM Studio! Models available.", "Models Refreshed", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Failed to get models. Status: {response.StatusCode}", "Refresh Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error refreshing models: {ex.Message}", "Refresh Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    SavedCameraPrompts = _originalSettings.SavedCameraPrompts,
                    AutoRestartComfyUI = AutoRestartCheckBox.IsChecked ?? true,
                    ComfyUIRestartScriptPath = ComfyUIRestartScriptTextBox.Text?.Trim() ?? "",
                    ComfyUIRestartDelaySeconds = int.TryParse(RestartDelayTextBox.Text, out var restartDelay) ? restartDelay : 10,
                    ComfyUIStartupTimeoutSeconds = int.TryParse(StartupTimeoutTextBox.Text, out var startupTimeout) ? startupTimeout : 120
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