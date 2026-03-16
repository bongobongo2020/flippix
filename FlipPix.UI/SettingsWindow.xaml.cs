using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FlipPix.Core.Models;
using FlipPix.Core.Services;
using FlipPix.UI.Models;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace FlipPix.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly ComfyUISettings _originalSettings;
        private readonly SettingsService _settingsService;
        private List<LMStudioModel> _availableModels = new List<LMStudioModel>();

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
                RemoteLoraFolderPath = original.RemoteLoraFolderPath,
                SavedCameraPrompts = original.SavedCameraPrompts,
                AutoRestartComfyUI = original.AutoRestartComfyUI,
                ComfyUIRestartScriptPath = original.ComfyUIRestartScriptPath,
                ComfyUIRestartDelaySeconds = original.ComfyUIRestartDelaySeconds,
                ComfyUIStartupTimeoutSeconds = original.ComfyUIStartupTimeoutSeconds,
                // Clone LM Studio settings
                LMStudioSettings = new LMStudioSettings
                {
                    BaseUrl = original.LMStudioSettings?.BaseUrl ?? "http://alien:8080",
                    SelectedModel = original.LMStudioSettings?.SelectedModel ?? string.Empty,
                    ConnectionTimeout = original.LMStudioSettings?.ConnectionTimeout ?? 30000,
                    MaxRetries = original.LMStudioSettings?.MaxRetries ?? 3,
                    RetryDelayMilliseconds = original.LMStudioSettings?.RetryDelayMilliseconds ?? 2000,
                    MaxImageSize = original.LMStudioSettings?.MaxImageSize ?? 256,
                    AutoConnect = original.LMStudioSettings?.AutoConnect ?? true
                }
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

        private void BrowseRemoteLoraPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Remote LoRA Folder",
                ShowNewFolderButton = false
            };

            if (!string.IsNullOrEmpty(RemoteLoraPathTextBox.Text))
            {
                dialog.SelectedPath = RemoteLoraPathTextBox.Text;
            }

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                RemoteLoraPathTextBox.Text = dialog.SelectedPath;
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

        private async void TestWebSocket_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;

            var results = new System.Text.StringBuilder();
            var serverUrl = ServerUrlTextBox.Text.Trim();

            try
            {
                var originalText = button.Content;
                button.Content = "Testing...";
                button.IsEnabled = false;

                results.AppendLine($"WebSocket Connection Test");
                results.AppendLine($"========================");
                results.AppendLine();
                results.AppendLine($"Server URL: {serverUrl}");
                results.AppendLine();

                // Convert HTTP URL to WebSocket URL
                var wsUrl = serverUrl.Replace("http://", "ws://").Replace("https://", "wss://");
                var wsEndpoint = $"{wsUrl}/ws";

                results.AppendLine($"WebSocket Endpoint: {wsEndpoint}");
                results.AppendLine();

                // Test 1: Validate URL format
                results.AppendLine("1. Validating URL format...");
                if (!Uri.TryCreate(wsEndpoint, UriKind.Absolute, out var wsUri))
                {
                    results.AppendLine("   ❌ Invalid WebSocket URL format");
                    MessageBox.Show(results.ToString(), "WebSocket Test Failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                results.AppendLine($"   ✅ Valid WebSocket URL");
                results.AppendLine();

                // Test 2: Basic TCP connectivity to the WebSocket port
                results.AppendLine("2. Testing TCP connectivity to WebSocket port...");
                try
                {
                    var port = wsUri.Port > 0 ? wsUri.Port : 80;
                    using var tcpClient = new System.Net.Sockets.TcpClient();
                    var connectTask = tcpClient.ConnectAsync(wsUri.DnsSafeHost, port);
                    var timeoutTask = Task.Delay(5000);

                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                    if (completedTask == connectTask)
                    {
                        results.AppendLine($"   ✅ TCP connection successful to {wsUri.DnsSafeHost}:{port}");
                    }
                    else
                    {
                        results.AppendLine($"   ❌ TCP connection timeout to {wsUri.DnsSafeHost}:{port}");
                        results.AppendLine("   ℹ️  The server may be blocking connections on this port");
                        MessageBox.Show(results.ToString(), "WebSocket Test Failed",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    results.AppendLine($"   ❌ TCP connection failed: {ex.Message}");
                    MessageBox.Show(results.ToString(), "WebSocket Test Failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                results.AppendLine();

                // Test 3: WebSocket handshake attempt
                results.AppendLine("3. Testing WebSocket handshake...");
                try
                {
                    using var ws = new System.Net.WebSockets.ClientWebSocket();
                    var clientId = Guid.NewGuid().ToString();
                    var testUri = new Uri($"{wsEndpoint}?clientId={clientId}");

                    var connectTask = ws.ConnectAsync(testUri, CancellationToken.None);
                    var timeoutTask = Task.Delay(10000); // 10 second timeout for WebSocket

                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                    if (completedTask == connectTask && ws.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        results.AppendLine("   ✅ WebSocket handshake successful!");
                        results.AppendLine($"   ✅ WebSocket state: {ws.State}");
                        results.AppendLine($"   ✅ Client ID: {clientId}");

                        // Close the test connection
                        await ws.CloseAsync(
                            System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                            "Test complete",
                            CancellationToken.None);

                        results.AppendLine();
                        results.AppendLine("================================");
                        results.AppendLine("✅ WebSocket connection test PASSED");
                        results.AppendLine("================================");
                        results.AppendLine();
                        results.AppendLine("Your ComfyUI server's WebSocket is working correctly!");

                        MessageBox.Show(results.ToString(), "WebSocket Test Successful",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else if (completedTask == timeoutTask)
                    {
                        results.AppendLine("   ❌ WebSocket handshake timed out");
                        results.AppendLine();
                        results.AppendLine("   This usually means:");
                        results.AppendLine("   • ComfyUI is not running with --listen flag");
                        results.AppendLine("   • A firewall is blocking WebSocket connections");
                        results.AppendLine("   • ComfyUI is overloaded or hanging");
                        MessageBox.Show(results.ToString(), "WebSocket Test Failed",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        results.AppendLine($"   ❌ WebSocket handshake failed");
                        results.AppendLine($"   ❌ WebSocket state: {ws.State}");
                        MessageBox.Show(results.ToString(), "WebSocket Test Failed",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (System.Net.WebSockets.WebSocketException wsEx)
                {
                    results.AppendLine($"   ❌ WebSocket error: {wsEx.WebSocketErrorCode}");
                    results.AppendLine($"   ❌ Message: {wsEx.Message}");
                    results.AppendLine();
                    results.AppendLine("Common causes:");
                    results.AppendLine("• ComfyUI not started with --listen 0.0.0.0");
                    results.AppendLine("• Firewall blocking WebSocket traffic");
                    results.AppendLine("• Reverse proxy not configured for WebSocket");
                    results.AppendLine();
                    results.AppendLine("To fix, start ComfyUI with:");
                    results.AppendLine($"python main.py --listen 0.0.0.0 --port {wsUri.Port}");
                    MessageBox.Show(results.ToString(), "WebSocket Test Failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch (Exception ex)
                {
                    results.AppendLine($"   ❌ Unexpected error: {ex.GetType().Name}");
                    results.AppendLine($"   ❌ Message: {ex.Message}");
                    MessageBox.Show(results.ToString(), "WebSocket Test Failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error running WebSocket test: {ex.Message}", "Test Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                button.Content = "Test WebSocket";
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
                System.Diagnostics.Debug.WriteLine($"RefreshLMStudioModels: Connecting to {baseUrl}/v1/models");

                using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };

                var response = await httpClient.GetAsync($"{baseUrl}/v1/models");
                System.Diagnostics.Debug.WriteLine($"RefreshLMStudioModels: Response status: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"RefreshLMStudioModels: Response JSON ({json.Length} chars): {json.Substring(0, Math.Min(500, json.Length))}...");

                    // Parse the models from the response
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var result = System.Text.Json.JsonSerializer.Deserialize<LMStudioModelsResponse>(json, options);

                    System.Diagnostics.Debug.WriteLine($"RefreshLMStudioModels: Parsed result, Data is null: {result?.Data == null}");

                    if (result?.Data != null)
                    {
                        _availableModels = result.Data.ToList();
                        System.Diagnostics.Debug.WriteLine($"RefreshLMStudioModels: Found {_availableModels.Count} models");

                        // Update the ComboBox items
                        LMStudioModelComboBox.ItemsSource = _availableModels;
                        LMStudioModelComboBox.DisplayMemberPath = "Name";
                        LMStudioModelComboBox.SelectedValuePath = "Name";

                        MessageBox.Show($"Successfully loaded {_availableModels.Count} models from LM Studio!", "Models Refreshed", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("RefreshLMStudioModels: result.Data is null");
                        MessageBox.Show($"Connected but no models found in response.\n\nResponse preview:\n{json.Substring(0, Math.Min(300, json.Length))}...", "No Models", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"RefreshLMStudioModels: Failed with status {response.StatusCode}, content: {errorContent}");
                    MessageBox.Show($"Failed to get models.\n\nStatus: {response.StatusCode}\n\nError:\n{errorContent}", "Refresh Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine($"RefreshLMStudioModels: HTTP request failed: {ex.Message}");
                MessageBox.Show($"Connection error: {ex.Message}\n\nPlease check that LM Studio is running and accessible at the specified address.", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (TaskCanceledException ex)
            {
                System.Diagnostics.Debug.WriteLine($"RefreshLMStudioModels: Request timed out: {ex.Message}");
                MessageBox.Show("Request timed out. LM Studio may be busy or not responding.", "Timeout Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RefreshLMStudioModels: Unexpected error: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Error refreshing models: {ex.Message}\n\nStack trace:\n{ex.StackTrace}", "Refresh Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    RemoteLoraFolderPath = RemoteLoraPathTextBox.Text?.Trim() ?? "",
                    SavedCameraPrompts = _originalSettings.SavedCameraPrompts,
                    AutoRestartComfyUI = AutoRestartCheckBox.IsChecked ?? true,
                    ComfyUIRestartScriptPath = ComfyUIRestartScriptTextBox.Text?.Trim() ?? "",
                    ComfyUIRestartDelaySeconds = int.TryParse(RestartDelayTextBox.Text, out var restartDelay) ? restartDelay : 10,
                    ComfyUIStartupTimeoutSeconds = int.TryParse(StartupTimeoutTextBox.Text, out var startupTimeout) ? startupTimeout : 120,
                    // Preserve LM Studio settings
                    LMStudioSettings = _originalSettings.LMStudioSettings
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