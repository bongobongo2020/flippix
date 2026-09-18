using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using FlipPix.Core.Models;
using FlipPix.Core.Services;

namespace FlipPix.UI.Linux.Windows;

/// <summary>
/// Settings dialog, ported to section parity with the WPF window: ComfyUI connection, install
/// path, crash detection &amp; auto-restart, GPU VRAM / workflow tier, output + remote folders,
/// LoRA folders, and LM Studio. (The ComfyUI Backup &amp; Restore panel stays WPF-only — it
/// drives the Windows installer bundle.)
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsService settingsService) : this()
    {
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

        Box("BaseUrlBox").Text = settings.BaseUrl ?? "http://127.0.0.1:8188";
        Box("TimeoutBox").Text = settings.ConnectionTimeout.ToString();
        Box("MaxRetriesBox").Text = settings.MaxRetries.ToString();
        Box("ComfyUIFolderBox").Text = settings.ComfyUIFolderPath ?? string.Empty;
        Box("OutputFolderBox").Text = settings.OutputFolderPath ?? string.Empty;
        Box("RemoteOutputFolderBox").Text = settings.RemoteOutputFolderPath ?? string.Empty;
        Box("RemoteLoraFolderBox").Text = settings.RemoteLoraFolderPath ?? string.Empty;
        Box("KreaLoraFolderBox").Text = settings.KreaLoraFolderPath ?? string.Empty;
        Box("RestartScriptBox").Text = settings.ComfyUIRestartScriptPath ?? string.Empty;
        Box("RestartDelayBox").Text = settings.ComfyUIRestartDelaySeconds.ToString();
        Box("StartupTimeoutBox").Text = settings.ComfyUIStartupTimeoutSeconds.ToString();
        Check("AutoRestartBox").IsChecked = settings.AutoRestartComfyUI;

        // 0 = auto, 1 = 16gb, 2 = full — same order the XAML declares them in.
        var tier = (settings.VramTier ?? "auto").Trim().ToLowerInvariant();
        Combo("VramTierBox").SelectedIndex = tier switch { "16gb" => 1, "full" => 2, _ => 0 };
        Text("DetectedVramText").Text = settings.DetectedVramGb > 0
            ? $"Detected GPU VRAM: {settings.DetectedVramGb:0.#} GB"
            : "GPU VRAM will be detected when ComfyUI connects.";

        var lm = settings.LMStudioSettings;
        Box("LMStudioServerBox").Text = settings.LMStudioServer
            ?? lm?.BaseUrl?.Replace("http://", "").Replace("https://", "").Split(':')[0] ?? string.Empty;
        Box("LMStudioPortBox").Text = settings.LMStudioPort
            ?? lm?.BaseUrl?.Split(':').LastOrDefault() ?? "8080";

        UpdateActiveOutputText(settings);
    }

    /// <summary>Says which of the two output folders the app will actually read from right now.</summary>
    private void UpdateActiveOutputText(ComfyUISettings settings)
    {
        try
        {
            var remote = IsRemote(settings.BaseUrl);
            var active = settings.ResolveOutputFolder(remote) is { Length: > 0 } p
                ? p
                : "(not set)";
            Text("ActiveOutputFolderText").Text =
                $"Reading generated files from: {active}";
        }
        catch
        {
            Text("ActiveOutputFolderText").Text = string.Empty;
        }
    }

    private static bool IsRemote(string? baseUrl)
    {
        try
        {
            var host = new Uri(baseUrl ?? "").Host;
            return !(host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                  || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                  || host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Settings;

        settings.BaseUrl = Box("BaseUrlBox").Text?.Trim() ?? settings.BaseUrl;
        if (int.TryParse(Box("TimeoutBox").Text?.Trim(), out var timeout)) settings.ConnectionTimeout = timeout;
        if (int.TryParse(Box("MaxRetriesBox").Text?.Trim(), out var retries)) settings.MaxRetries = retries;
        settings.ComfyUIFolderPath = Box("ComfyUIFolderBox").Text?.Trim() ?? string.Empty;
        settings.OutputFolderPath = Box("OutputFolderBox").Text?.Trim() ?? string.Empty;
        settings.RemoteOutputFolderPath = Box("RemoteOutputFolderBox").Text?.Trim() ?? string.Empty;
        settings.RemoteLoraFolderPath = Box("RemoteLoraFolderBox").Text?.Trim() ?? string.Empty;
        settings.KreaLoraFolderPath = Box("KreaLoraFolderBox").Text?.Trim() ?? string.Empty;
        settings.ComfyUIRestartScriptPath = Box("RestartScriptBox").Text?.Trim() ?? string.Empty;
        if (int.TryParse(Box("RestartDelayBox").Text?.Trim(), out var delay)) settings.ComfyUIRestartDelaySeconds = delay;
        if (int.TryParse(Box("StartupTimeoutBox").Text?.Trim(), out var startup)) settings.ComfyUIStartupTimeoutSeconds = startup;
        settings.AutoRestartComfyUI = Check("AutoRestartBox").IsChecked == true;
        settings.VramTier = Combo("VramTierBox").SelectedIndex switch
        {
            1 => "16gb",
            2 => "full",
            _ => "auto",
        };

        var serverBox = Box("LMStudioServerBox");
        if (!string.IsNullOrWhiteSpace(serverBox.Text))
        {
            settings.LMStudioServer = serverBox.Text.Trim();
            var port = Box("LMStudioPortBox").Text?.Trim() ?? "8080";
            settings.LMStudioPort = port;
            if (settings.LMStudioSettings != null)
                settings.LMStudioSettings.BaseUrl = $"http://{settings.LMStudioServer}:{port}";
        }

        _settingsService.SaveSettings(settings);
        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close();

    // --- browse helpers (StorageProvider folder picker) ---

    private async void BrowseComfyUIPath_Click(object? sender, RoutedEventArgs e) =>
        await BrowseInto("ComfyUIFolderBox", "Select the ComfyUI installation folder");
    private async void BrowseRestartScript_Click(object? sender, RoutedEventArgs e) =>
        await BrowseFileInto("RestartScriptBox", "Select the ComfyUI restart script");
    private async void BrowseOutputPath_Click(object? sender, RoutedEventArgs e) =>
        await BrowseInto("OutputFolderBox", "Select the ComfyUI output folder");
    private async void BrowseRemoteOutputPath_Click(object? sender, RoutedEventArgs e) =>
        await BrowseInto("RemoteOutputFolderBox", "Select the remote ComfyUI output folder as this machine sees it");
    private async void BrowseRemoteLoraPath_Click(object? sender, RoutedEventArgs e) =>
        await BrowseInto("RemoteLoraFolderBox", "Select the remote LoRA folder as this machine sees it");
    private async void BrowseKreaLoraPath_Click(object? sender, RoutedEventArgs e) =>
        await BrowseInto("KreaLoraFolderBox", "Select the Krea2 LoRA folder");

    private async System.Threading.Tasks.Task BrowseInto(string textBoxName, string title)
    {
        var picked = await PickFolderAsync(title);
        if (picked != null) Box(textBoxName).Text = picked;
    }

    private async System.Threading.Tasks.Task BrowseFileInto(string textBoxName, string title)
    {
        try
        {
            var provider = StorageProvider;
            if (provider == null) return;
            var options = new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Shell script") { Patterns = new[] { "*.sh" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } },
                },
            };
            var files = await provider.OpenFilePickerAsync(options);
            if (files.Count > 0)
                Box(textBoxName).Text = files[0].TryGetLocalPath();
        }
        catch
        {
            // picker unavailable (no owner) — the text box stays editable by hand
        }
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string title)
    {
        try
        {
            var provider = StorageProvider;
            if (provider == null) return null;
            var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
            var picked = await provider.OpenFolderPickerAsync(options);
            return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        }
        catch
        {
            return null;
        }
    }

    // --- control finders ---

    private TextBox Box(string name) => this.FindControl<TextBox>(name)
        ?? throw new InvalidOperationException($"Settings textbox '{name}' missing");
    private CheckBox Check(string name) => this.FindControl<CheckBox>(name)
        ?? throw new InvalidOperationException($"Settings checkbox '{name}' missing");
    private ComboBox Combo(string name) => this.FindControl<ComboBox>(name)
        ?? throw new InvalidOperationException($"Settings combo '{name}' missing");
    private TextBlock Text(string name) => this.FindControl<TextBlock>(name)
        ?? throw new InvalidOperationException($"Settings text '{name}' missing");
}
