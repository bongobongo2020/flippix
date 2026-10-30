using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FlipPix.Core.Models;
using FlipPix.Core.Services;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace FlipPix.UI.Linux.Windows;

/// <summary>
/// Port of FlipPix.UI/SettingsWindow.xaml.cs. Everything but the Windows-only maintenance tools
/// behaves as on Windows: the backup uses the system tar, and the WSL/Windows restore paths explain
/// what to do on Linux instead of launching cmd.exe.
/// </summary>
public partial class SettingsWindow : DialogWindow
{
    private readonly ComfyUISettings _originalSettings = new();
    private readonly SettingsService _settingsService = null!;
    private List<LMStudioModel> _availableModels = new();

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

        _originalSettings = CloneSettings(settingsService.Settings);
        DataContext = _originalSettings;

        InitLlmUi();
        InitVramTierUi();

        ServerUrlTextBox.TextChanged += (_, _) => UpdateActiveOutputFolderText();
        OutputPathTextBox.TextChanged += (_, _) => UpdateActiveOutputFolderText();
        RemoteOutputPathTextBox.TextChanged += (_, _) => UpdateActiveOutputFolderText();

        UpdateStatus();
    }

    private void InitVramTierUi()
    {
        var tier = (_originalSettings.VramTier ?? "auto").Trim().ToLowerInvariant();
        foreach (var item in VramTierComboBox.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), tier, StringComparison.OrdinalIgnoreCase))
            {
                VramTierComboBox.SelectedItem = cbi;
                break;
            }
        }
        if (VramTierComboBox.SelectedItem == null && VramTierComboBox.ItemCount > 0)
            VramTierComboBox.SelectedIndex = 0;

        DetectedVramText.Text = _originalSettings.DetectedVramGb > 0
            ? $"Detected GPU VRAM: {_originalSettings.DetectedVramGb:0.#} GB"
            : "GPU VRAM not detected yet (connect to ComfyUI once).";
    }

    private string SelectedVramTier() =>
        (VramTierComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()?.ToLowerInvariant() ?? "auto";

    private void InitLlmUi()
    {
        var lm = _originalSettings.LMStudioSettings;
        lm?.EnsureProfiles();

        var profile = lm?.DefaultProfile;
        LMStudioServerNameTextBox.Text = profile?.Name ?? string.Empty;
        LMStudioModelNameTextBox.Text = profile?.ModelName ?? string.Empty;

        LMStudioServerNameTextBox.TextChanged += (_, _) => UpdateDefaultLlmTargetText();
        LMStudioModelNameTextBox.TextChanged += (_, _) => UpdateDefaultLlmTargetText();
        LMStudioServerTextBox.TextChanged += (_, _) => UpdateDefaultLlmTargetText();
        LMStudioPortTextBox.TextChanged += (_, _) => UpdateDefaultLlmTargetText();

        SavedServerComboBox.DisplayMemberBinding = new Binding(nameof(LlmServerProfile.ListLabel));
        RefreshSavedServerItems();
        UpdateDefaultLlmTargetText();
    }

    private void RefreshSavedServerItems()
    {
        var lm = _originalSettings.LMStudioSettings;
        lm?.EnsureProfiles();
        var servers = lm?.Servers?.ToList() ?? new List<LlmServerProfile>();

        var currentUrl = LMStudioSettings.BuildBaseUrl(LMStudioServerTextBox.Text, LMStudioPortTextBox.Text);
        var selected = servers.FirstOrDefault(p =>
                           string.Equals(p.BaseUrl?.TrimEnd('/'), currentUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                       ?? servers.FirstOrDefault(p => p.IsDefault);

        SavedServerComboBox.SelectionChanged -= SavedServerComboBox_SelectionChanged;
        SavedServerComboBox.ItemsSource = servers;
        SavedServerComboBox.SelectedItem = selected;
        SavedServerComboBox.SelectionChanged += SavedServerComboBox_SelectionChanged;
    }

    private string CurrentModelId()
    {
        if (LMStudioModelComboBox.SelectedValue is string selected && !string.IsNullOrWhiteSpace(selected))
            return selected.Trim();
        return _originalSettings.LMStudioSettings?.SelectedModel?.Trim() ?? string.Empty;
    }

    private void UpdateDefaultLlmTargetText()
    {
        if (DefaultLlmTargetText is null || LMStudioServerTextBox is null || LMStudioPortTextBox is null
            || LMStudioServerNameTextBox is null || LMStudioModelNameTextBox is null || LMStudioModelComboBox is null)
            return;

        var url = LMStudioSettings.BuildBaseUrl(LMStudioServerTextBox.Text, LMStudioPortTextBox.Text);
        var serverName = LMStudioServerNameTextBox.Text?.Trim() ?? string.Empty;
        var modelId = CurrentModelId();
        var modelName = LMStudioModelNameTextBox.Text?.Trim() ?? string.Empty;

        var server = string.IsNullOrWhiteSpace(serverName) ? url : $"{serverName} ({url})";
        var model = string.IsNullOrWhiteSpace(modelId)
            ? "(no model selected — click Refresh)"
            : string.IsNullOrWhiteSpace(modelName) ? modelId : $"{modelName} [{modelId}]";

        DefaultLlmTargetText.Text = $"Image analysis will be sent to {server} · {model}";
    }

    private void LMStudioModelComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateDefaultLlmTargetText();

    private void RemoveSavedServer_Click(object? sender, RoutedEventArgs e)
    {
        if (SavedServerComboBox.SelectedItem is not LlmServerProfile profile)
        {
            MessageBox.Show("Pick a saved server to remove first.", "Remove Server", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = MessageBox.Show($"Forget the saved server \"{profile.DisplayName}\" ({profile.BaseUrl})?",
            "Remove Server", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _originalSettings.LMStudioSettings?.RemoveProfile(profile.BaseUrl);
        RefreshSavedServerItems();
        UpdateDefaultLlmTargetText();
    }

    private static ComfyUISettings CloneSettings(ComfyUISettings original) => new()
    {
        BaseUrl = original.BaseUrl,
        ConnectionTimeout = original.ConnectionTimeout,
        MaxRetries = original.MaxRetries,
        RetryDelayMilliseconds = original.RetryDelayMilliseconds,
        ComfyUIFolderPath = original.ComfyUIFolderPath,
        OutputFolderPath = original.OutputFolderPath,
        RemoteOutputFolderPath = original.RemoteOutputFolderPath,
        RemoteLoraFolderPath = original.RemoteLoraFolderPath,
        KreaLoraFolderPath = original.KreaLoraFolderPath,
        WslModelsFolderPath = original.WslModelsFolderPath,
        SavedCameraPrompts = original.SavedCameraPrompts,
        AutoRestartComfyUI = original.AutoRestartComfyUI,
        VramTier = original.VramTier,
        DetectedVramGb = original.DetectedVramGb,
        ComfyUIRestartScriptPath = original.ComfyUIRestartScriptPath,
        ComfyUIRestartDelaySeconds = original.ComfyUIRestartDelaySeconds,
        ComfyUIStartupTimeoutSeconds = original.ComfyUIStartupTimeoutSeconds,
        LMStudioSettings = new LMStudioSettings
        {
            BaseUrl = original.LMStudioSettings?.BaseUrl ?? "http://alien:8080",
            SelectedModel = original.LMStudioSettings?.SelectedModel ?? string.Empty,
            ConnectionTimeout = original.LMStudioSettings?.ConnectionTimeout ?? 30000,
            MaxRetries = original.LMStudioSettings?.MaxRetries ?? 3,
            RetryDelayMilliseconds = original.LMStudioSettings?.RetryDelayMilliseconds ?? 2000,
            MaxImageSize = original.LMStudioSettings?.MaxImageSize ?? 256,
            AutoConnect = original.LMStudioSettings?.AutoConnect ?? true,
            ServerHistory = original.LMStudioSettings?.ServerHistory != null
                ? new List<string>(original.LMStudioSettings.ServerHistory)
                : new List<string>(),
            Servers = original.LMStudioSettings?.Servers?.Select(p => new LlmServerProfile
            {
                Name = p.Name,
                BaseUrl = p.BaseUrl,
                Model = p.Model,
                ModelName = p.ModelName,
                IsDefault = p.IsDefault
            }).ToList() ?? new List<LlmServerProfile>(),
            ModelSwitchPort = original.LMStudioSettings?.ModelSwitchPort ?? 8812,
            VisionSwitchModel = original.LMStudioSettings?.VisionSwitchModel ?? "qwen-vision",
            RewriterSwitchModel = original.LMStudioSettings?.RewriterSwitchModel ?? "h3-rewriter"
        }
    };

    // ── folder / file pickers ─────────────────────────────────────────────────

    private async Task<string?> PickFolderAsync(string title, string? current)
    {
        var start = !string.IsNullOrEmpty(current) && Directory.Exists(current) ? current
            : Directory.Exists("/mnt/chromeos") ? "/mnt/chromeos" : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start != null ? await StorageProvider.TryGetFolderFromPathAsync(start) : null
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void BrowseComfyUIPath_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Select ComfyUI Installation Folder", ComfyUIPathTextBox.Text) is { } p)
        {
            ComfyUIPathTextBox.Text = p;
            UpdateStatus();
        }
    }

    private async void BrowseOutputPath_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Select ComfyUI Output Folder", OutputPathTextBox.Text) is { } p)
        {
            OutputPathTextBox.Text = p;
            UpdateStatus();
        }
    }

    private async void BrowseRemoteOutputPath_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Select the remote ComfyUI output folder, as this computer sees it", RemoteOutputPathTextBox.Text) is { } p)
        {
            RemoteOutputPathTextBox.Text = p;
            UpdateStatus();
        }
    }

    private async void BrowseRemoteLoraPath_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Select Remote LoRA Folder", RemoteLoraPathTextBox.Text) is { } p)
            RemoteLoraPathTextBox.Text = p;
    }

    private async void BrowseKreaLoraPath_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Select Krea2 LoRA Folder", KreaLoraPathTextBox.Text) is { } p)
            KreaLoraPathTextBox.Text = p;
    }

    private async void BrowseRestartScript_Click(object? sender, RoutedEventArgs e)
    {
        var current = ComfyUIRestartScriptTextBox.Text;
        var dir = !string.IsNullOrEmpty(current) ? Path.GetDirectoryName(current) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select ComfyUI Restart Script",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Shell scripts") { Patterns = new[] { "*.sh" } },
                new FilePickerFileType("Batch files") { Patterns = new[] { "*.bat" } },
                FilePickerFileTypes.All
            },
            SuggestedStartLocation = dir != null && Directory.Exists(dir) ? await StorageProvider.TryGetFolderFromPathAsync(dir) : null
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            ComfyUIRestartScriptTextBox.Text = path;
            UpdateStatus();
        }
    }

    // ── status ─────────────────────────────────────────────────────────────────

    private bool ServerLooksRemote()
    {
        var host = Uri.TryCreate(ServerUrlTextBox.Text?.Trim(), UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        return !(string.IsNullOrEmpty(host)
                 || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                 || host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase)
                 || host.Equals("::1", StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateActiveOutputFolderText()
    {
        if (ActiveOutputFolderText is null || ServerUrlTextBox is null || OutputPathTextBox is null || RemoteOutputPathTextBox is null)
            return;

        var isRemote = ServerLooksRemote();
        var active = (isRemote ? RemoteOutputPathTextBox.Text : OutputPathTextBox.Text)?.Trim() ?? string.Empty;
        var other = (isRemote ? OutputPathTextBox.Text : RemoteOutputPathTextBox.Text)?.Trim() ?? string.Empty;
        var which = isRemote ? "remote" : "local";

        if (string.IsNullOrEmpty(active))
        {
            ActiveOutputFolderText.Text = ComfyUISettings.IsReachableFolder(other)
                ? $"⚠ This server is {which}, but no {which} output folder is set — the app will fall back to: {other}"
                : $"⚠ This server is {which}, but no {which} output folder is set.";
        }
        else if (ComfyUISettings.IsReachableFolder(active))
            ActiveOutputFolderText.Text = $"➜ Generated files will be read from the {which} folder: {active}";
        else if (ComfyUISettings.IsReachableFolder(other))
            ActiveOutputFolderText.Text = $"⚠ The {which} folder is not reachable ({active}) — the app will fall back to: {other}";
        else
            ActiveOutputFolderText.Text = $"❌ The {which} folder is not reachable: {active}";
    }

    private void UpdateStatus()
    {
        try
        {
            var status = "Configuration Status:\n\n";
            var comfy = ComfyUIPathTextBox.Text;
            if (string.IsNullOrEmpty(comfy)) status += "❌ ComfyUI installation path not set\n";
            else if (Directory.Exists(comfy)) status += $"✅ ComfyUI path exists: {comfy}\n";
            else status += $"❌ ComfyUI path not found: {comfy}\n";

            var output = OutputPathTextBox.Text;
            if (string.IsNullOrEmpty(output)) status += "❌ Output folder path not set\n";
            else if (Directory.Exists(output))
            {
                var pngFiles = Directory.GetFiles(output, "*.png", SearchOption.TopDirectoryOnly).Length;
                status += $"✅ Output folder exists: {output} ({pngFiles} PNG files found)\n";
            }
            else status += $"❌ Output folder not found: {output}\n";

            status += Uri.TryCreate(ServerUrlTextBox.Text, UriKind.Absolute, out _)
                ? $"✅ Server URL valid: {ServerUrlTextBox.Text}\n"
                : $"❌ Invalid server URL: {ServerUrlTextBox.Text}\n";

            if (!string.IsNullOrEmpty(RemoteOutputPathTextBox.Text))
            {
                status += ComfyUISettings.IsReachableFolder(RemoteOutputPathTextBox.Text)
                    ? $"✅ Remote output folder reachable: {RemoteOutputPathTextBox.Text}\n"
                    : $"❌ Remote output folder not reachable: {RemoteOutputPathTextBox.Text}\n";
            }

            StatusTextBlock.Text = status.Trim();
            UpdateActiveOutputFolderText();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Error checking configuration: {ex.Message}";
        }
    }

    // ── connection tests ──────────────────────────────────────────────────────

    private async void TestConnection_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        try
        {
            button.Content = "Testing...";
            button.IsEnabled = false;
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await client.GetAsync($"{ServerUrlTextBox.Text}/system_stats");
            if (response.IsSuccessStatusCode)
                MessageBox.Show("✅ Connection to ComfyUI successful!", "Connection Test", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show($"❌ Connection failed with status: {response.StatusCode}", "Connection Test", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"❌ Connection test failed: {ex.Message}", "Connection Test", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            button.Content = "Test Connection";
            button.IsEnabled = true;
        }
    }

    private async void TestWebSocket_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var results = new System.Text.StringBuilder();
        var serverUrl = (ServerUrlTextBox.Text ?? string.Empty).Trim();

        try
        {
            button.Content = "Testing...";
            button.IsEnabled = false;

            results.AppendLine("WebSocket Connection Test");
            results.AppendLine("========================");
            results.AppendLine();
            results.AppendLine($"Server URL: {serverUrl}");
            results.AppendLine();

            var wsUrl = serverUrl.Replace("http://", "ws://").Replace("https://", "wss://");
            var wsEndpoint = $"{wsUrl}/ws";
            results.AppendLine($"WebSocket Endpoint: {wsEndpoint}");
            results.AppendLine();

            results.AppendLine("1. Validating URL format...");
            if (!Uri.TryCreate(wsEndpoint, UriKind.Absolute, out var wsUri))
            {
                results.AppendLine("   ❌ Invalid WebSocket URL format");
                MessageBox.Show(results.ToString(), "WebSocket Test Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            results.AppendLine("   ✅ Valid WebSocket URL");
            results.AppendLine();

            results.AppendLine("2. Testing TCP connectivity to WebSocket port...");
            try
            {
                var port = wsUri.Port > 0 ? wsUri.Port : 80;
                using var tcpClient = new System.Net.Sockets.TcpClient();
                var connectTask = tcpClient.ConnectAsync(wsUri.DnsSafeHost, port);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) == connectTask && tcpClient.Connected)
                    results.AppendLine($"   ✅ TCP connection successful to {wsUri.DnsSafeHost}:{port}");
                else
                {
                    results.AppendLine($"   ❌ TCP connection timeout to {wsUri.DnsSafeHost}:{port}");
                    results.AppendLine("   ℹ️  The server may be blocking connections on this port");
                    MessageBox.Show(results.ToString(), "WebSocket Test Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            catch (Exception ex)
            {
                results.AppendLine($"   ❌ TCP connection failed: {ex.Message}");
                MessageBox.Show(results.ToString(), "WebSocket Test Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            results.AppendLine();

            results.AppendLine("3. Testing WebSocket handshake...");
            try
            {
                using var ws = new System.Net.WebSockets.ClientWebSocket();
                var clientId = Guid.NewGuid().ToString();
                var connectTask = ws.ConnectAsync(new Uri($"{wsEndpoint}?clientId={clientId}"), CancellationToken.None);
                var completed = await Task.WhenAny(connectTask, Task.Delay(10000));

                if (completed == connectTask && ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    results.AppendLine("   ✅ WebSocket handshake successful!");
                    results.AppendLine($"   ✅ WebSocket state: {ws.State}");
                    results.AppendLine($"   ✅ Client ID: {clientId}");
                    await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Test complete", CancellationToken.None);
                    results.AppendLine();
                    results.AppendLine("================================");
                    results.AppendLine("✅ WebSocket connection test PASSED");
                    results.AppendLine("================================");
                    results.AppendLine();
                    results.AppendLine("Your ComfyUI server's WebSocket is working correctly!");
                    MessageBox.Show(results.ToString(), "WebSocket Test Successful", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (completed != connectTask)
                {
                    results.AppendLine("   ❌ WebSocket handshake timed out");
                    results.AppendLine();
                    results.AppendLine("   This usually means:");
                    results.AppendLine("   • ComfyUI is not running with --listen flag");
                    results.AppendLine("   • A firewall is blocking WebSocket connections");
                    results.AppendLine("   • ComfyUI is overloaded or hanging");
                    MessageBox.Show(results.ToString(), "WebSocket Test Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    results.AppendLine("   ❌ WebSocket handshake failed");
                    results.AppendLine($"   ❌ WebSocket state: {ws.State}");
                    MessageBox.Show(results.ToString(), "WebSocket Test Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                MessageBox.Show(results.ToString(), "WebSocket Test Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                results.AppendLine($"   ❌ Unexpected error: {ex.GetType().Name}");
                results.AppendLine($"   ❌ Message: {ex.Message}");
                MessageBox.Show(results.ToString(), "WebSocket Test Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error running WebSocket test: {ex.Message}", "Test Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            button.Content = "Test WebSocket";
            button.IsEnabled = true;
        }
    }

    private async void TestLMStudioConnection_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var server = (LMStudioServerTextBox.Text ?? string.Empty).Trim();
            var port = (LMStudioPortTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(port))
            {
                MessageBox.Show("Please enter both server and port.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var baseUrl = $"http://{server}:{port}";
            var results = new System.Text.StringBuilder();
            results.AppendLine($"Testing connection to: {baseUrl}");
            results.AppendLine();

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

            results.AppendLine("2. Testing HTTP connectivity...");
            try
            {
                using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                try
                {
                    var rootResponse = await httpClient.GetAsync($"{baseUrl}/");
                    results.AppendLine($"   ✓ Root endpoint: {rootResponse.StatusCode}");
                }
                catch (Exception ex)
                {
                    results.AppendLine($"   ✗ Root endpoint failed: {ex.Message}");
                }
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
                        results.AppendLine($"   ✗ Error: {await modelsResponse.Content.ReadAsStringAsync()}");
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
            results.AppendLine();
            results.AppendLine("3. Testing DNS resolution...");
            try
            {
                var addresses = System.Net.Dns.GetHostAddresses(server);
                if (addresses.Length > 0)
                {
                    results.AppendLine("   ✓ DNS resolution successful");
                    foreach (var addr in addresses) results.AppendLine($"   ✓ {addr}");
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

            MessageBox.Show(results.ToString(), "LM Studio Connection Test Results", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error running connection test: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshLMStudioModels_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var server = (LMStudioServerTextBox.Text ?? string.Empty).Trim();
            var port = (LMStudioPortTextBox.Text ?? string.Empty).Trim();
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
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var result = System.Text.Json.JsonSerializer.Deserialize<LMStudioModelsResponse>(json, options);

                if (result?.Data != null)
                {
                    _availableModels = result.Data.ToList();
                    LMStudioModelComboBox.ItemsSource = _availableModels;
                    LMStudioModelComboBox.DisplayMemberBinding = new Binding(nameof(LMStudioModel.Name));
                    LMStudioModelComboBox.SelectedValueBinding = new Binding(nameof(LMStudioModel.Name));

                    if (_originalSettings.LMStudioSettings != null)
                    {
                        _originalSettings.LMStudioSettings.RememberServer(baseUrl);
                        RefreshSavedServerItems();

                        var savedModel = _originalSettings.LMStudioSettings.FindProfile(baseUrl)?.Model
                                         ?? _originalSettings.LMStudioSettings.SelectedModel;
                        if (!string.IsNullOrWhiteSpace(savedModel)
                            && _availableModels.Any(m => string.Equals(m.Name, savedModel, StringComparison.OrdinalIgnoreCase)))
                            LMStudioModelComboBox.SelectedValue = savedModel;
                    }

                    UpdateDefaultLlmTargetText();
                    MessageBox.Show($"Successfully loaded {_availableModels.Count} models from LM Studio!", "Models Refreshed",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Connected but no models found in response.\n\nResponse preview:\n{json.Substring(0, Math.Min(300, json.Length))}...",
                        "No Models", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                MessageBox.Show($"Failed to get models.\n\nStatus: {response.StatusCode}\n\nError:\n{errorContent}", "Refresh Failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            MessageBox.Show($"Connection error: {ex.Message}\n\nPlease check that LM Studio is running and accessible at the specified address.",
                "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (TaskCanceledException)
        {
            MessageBox.Show("Request timed out. LM Studio may be busy or not responding.", "Timeout Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error refreshing models: {ex.Message}\n\nStack trace:\n{ex.StackTrace}", "Refresh Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SavedServerComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not LlmServerProfile profile) return;

        var (host, port) = LMStudioSettings.ParseBaseUrl(profile.BaseUrl);
        LMStudioServerTextBox.Text = host;
        LMStudioPortTextBox.Text = port;
        LMStudioServerNameTextBox.Text = profile.Name ?? string.Empty;
        LMStudioModelNameTextBox.Text = profile.ModelName ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(profile.Model))
        {
            if (_originalSettings.LMStudioSettings != null)
                _originalSettings.LMStudioSettings.SelectedModel = profile.Model;
            if (_availableModels.Any(m => string.Equals(m.Name, profile.Model, StringComparison.OrdinalIgnoreCase)))
                LMStudioModelComboBox.SelectedValue = profile.Model;
        }

        UpdateDefaultLlmTargetText();
        UpdateStatus();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var output = OutputPathTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(output))
            {
                MessageBox.Show("Please specify the ComfyUI output folder path.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!Directory.Exists(output))
            {
                var result = MessageBox.Show($"The output folder does not exist:\n\n{output}\n\nDo you want to create it?",
                    "Create Output Folder", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;
                Directory.CreateDirectory(output);
            }

            if (ServerLooksRemote())
            {
                var remoteOut = RemoteOutputPathTextBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(remoteOut) || !ComfyUISettings.IsReachableFolder(remoteOut))
                {
                    var message = string.IsNullOrEmpty(remoteOut)
                        ? "The ComfyUI server is on another machine, but no remote output folder is set.\n\n"
                          + $"The app will fall back to the local folder ({output}) when it can reach it.\n\nSave anyway?"
                        : $"The remote output folder is not reachable:\n\n{remoteOut}\n\n"
                          + "This is where the app looks for generated files when ComfyUI is on another machine.\n\nSave anyway?";
                    if (MessageBox.Show(message, "Remote Output Folder", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        return;
                }
            }

            var lmSettings = _originalSettings.LMStudioSettings ?? new LMStudioSettings();
            var lmBaseUrl = LMStudioSettings.BuildBaseUrl(LMStudioServerTextBox.Text, LMStudioPortTextBox.Text);
            var lmModel = CurrentModelId();
            if (!string.IsNullOrEmpty(lmModel)) lmSettings.SelectedModel = lmModel;
            lmSettings.SaveProfile(lmBaseUrl, LMStudioServerNameTextBox.Text, lmModel, LMStudioModelNameTextBox.Text, makeDefault: true);

            // Write onto the live settings object; this dialog covers only part of ComfyUISettings.
            var newSettings = _settingsService.Settings;
            newSettings.BaseUrl = ServerUrlTextBox.Text?.Trim() ?? "http://localhost:8188";
            newSettings.ConnectionTimeout = int.TryParse(TimeoutTextBox.Text, out var timeout) ? timeout : 10000;
            newSettings.MaxRetries = int.TryParse(MaxRetriesTextBox.Text, out var retries) ? retries : 3;
            newSettings.ComfyUIFolderPath = ComfyUIPathTextBox.Text?.Trim() ?? "";
            newSettings.OutputFolderPath = output;
            newSettings.RemoteOutputFolderPath = RemoteOutputPathTextBox.Text?.Trim() ?? "";
            newSettings.RemoteLoraFolderPath = RemoteLoraPathTextBox.Text?.Trim() ?? "";
            newSettings.KreaLoraFolderPath = KreaLoraPathTextBox.Text?.Trim() ?? "";
            newSettings.WslModelsFolderPath = WslModelsFolderTextBox.Text?.Trim() ?? "";
            newSettings.AutoRestartComfyUI = AutoRestartCheckBox.IsChecked ?? true;
            newSettings.VramTier = SelectedVramTier();
            newSettings.ComfyUIRestartScriptPath = ComfyUIRestartScriptTextBox.Text?.Trim() ?? "";
            newSettings.ComfyUIRestartDelaySeconds = int.TryParse(RestartDelayTextBox.Text, out var restartDelay) ? restartDelay : 10;
            newSettings.ComfyUIStartupTimeoutSeconds = int.TryParse(StartupTimeoutTextBox.Text, out var startupTimeout) ? startupTimeout : 120;
            newSettings.LMStudioSettings = lmSettings;

            _settingsService.SaveSettings(newSettings);

            MessageBox.Show("Settings saved successfully!\n\nPlease restart the application for all changes to take effect.",
                "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── ComfyUI backup & restore ──────────────────────────────────────────────

    private async void BrowseWslModels_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Select your models folder (contains checkpoints, loras, vae, ...)", WslModelsFolderTextBox.Text) is { } p)
            WslModelsFolderTextBox.Text = p;
    }

    private void ApplyWslModels_Click(object? sender, RoutedEventArgs e)
    {
        // WSL is Windows' Linux layer. On Linux, ComfyUI reads extra_model_paths.yaml directly.
        MessageBox.Show(
            "This sets up ComfyUI-in-WSL on a Windows PC, so there is nothing to apply on Linux.\n\n" +
            "If ComfyUI runs on this machine, point its extra_model_paths.yaml at your models folder; " +
            "if it runs on your Windows PC, use this button from FlipPix on that PC.",
            "WSL models", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RestoreComfyUI_Click(object? sender, RoutedEventArgs e)
    {
        var repo = HfRepoTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/'))
        {
            MessageBox.Show("Enter a Hugging Face repo as user/repo (e.g. bongo2k22/flippix-comfyui).",
                "Restore ComfyUI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The bundle needs an NVIDIA GPU. A Chromebook (or any machine without one) runs ComfyUI on
        // the GPU PC and connects to it as a remote server.
        if (!File.Exists("/usr/bin/nvidia-smi") && !File.Exists("/usr/local/bin/nvidia-smi"))
        {
            MessageBox.Show(
                "No NVIDIA GPU was found on this computer, and the ComfyUI bundle needs one.\n\n" +
                "Restore ComfyUI on your GPU PC instead (FlipPix there → Settings → Set up / Restore ComfyUI), " +
                "start it with --listen 0.0.0.0, then set the ComfyUI Server URL above to http://<that PC's IP>:8188.",
                "Restore ComfyUI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cmd = $"curl -fsSL https://raw.githubusercontent.com/bongobongo2020/flippix/flippix-prompt-image/scripts/restore-comfyui.sh | bash -s -- --hf {repo}";
        var confirm = MessageBox.Show(
            $"Set up ComfyUI by downloading the bundle from:\n\n  {repo}\n\n" +
            "This downloads ~15 GB, verifies the checksum, and extracts it. Run this in a terminal:\n\n" + cmd +
            "\n\nCopy the command to the clipboard?",
            "Restore ComfyUI", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        ClipboardService.SetText(cmd);
        BackupRestoreStatus.Text = "Restore command copied — paste it into a terminal. When it finishes, set the "
            + "ComfyUI Server URL above to that machine's IP:8188 and Save.";
    }

    private async void BackupComfyUI_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        var src = ComfyUIPathTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(src) || !Directory.Exists(src))
        {
            MessageBox.Show("Set a valid 'ComfyUI Installation Path' above first (a folder this computer can read).",
                "Back up ComfyUI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save ComfyUI backup",
            SuggestedFileName = $"flippix-comfyui-backup-{DateTime.Now:yyyyMMdd-HHmmss}.tar.gz",
            FileTypeChoices = new[] { new FilePickerFileType("Gzip tarball") { Patterns = new[] { "*.tar.gz" } } }
        });
        var dest = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(dest)) return;

        var trimmed = src.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(trimmed) ?? trimmed;
        var leaf = Path.GetFileName(trimmed);
        var argList = new List<string> { "-C", parent, "-czf", dest,
            "--exclude=__pycache__", "--exclude=*.pyc",
            "--exclude=*/models", "--exclude=*/output", "--exclude=*/temp",
            "--exclude=*/venv", "--exclude=*/.venv",
            leaf };

        var original = button.Content;
        button.IsEnabled = false;
        RestoreComfyUIButton.IsEnabled = false;
        button.Content = "Backing up… (several min)";
        BackupRestoreStatus.Text = $"Archiving {src} → {dest} (models/outputs excluded). Please wait…";

        try
        {
            var exit = await Task.Run(() => RunProcess("tar", argList));
            if (exit != 0)
            {
                BackupRestoreStatus.Text = $"Backup failed (tar exit {exit}).";
                MessageBox.Show($"tar exited with code {exit}; the backup may be incomplete.", "Back up ComfyUI",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sha = await Task.Run(() => Sha256Hex(dest));
            File.WriteAllText(dest + ".sha256", $"{sha}  {Path.GetFileName(dest)}\n");
            var sizeMB = new FileInfo(dest).Length / (1024.0 * 1024.0);
            BackupRestoreStatus.Text = $"Backup complete: {Path.GetFileName(dest)} ({sizeMB:N0} MB) + .sha256";
            MessageBox.Show($"Backup complete:\n\n{dest}\n({sizeMB:N0} MB)\n\nSHA-256: {sha}\n\n"
                + "Restore on a same-OS machine with:\n  bash restore-comfyui.sh \"" + Path.GetFileName(dest) + "\"",
                "Back up ComfyUI", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            BackupRestoreStatus.Text = "Backup failed.";
            MessageBox.Show($"Backup failed: {ex.Message}", "Back up ComfyUI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            button.Content = original;
            button.IsEnabled = true;
            RestoreComfyUIButton.IsEnabled = true;
        }
    }

    private static int RunProcess(string fileName, List<string> argList)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var a in argList) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return -1;
        _ = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string Sha256Hex(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
