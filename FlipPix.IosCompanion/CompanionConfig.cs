using System.Text.Json;
using FlipPix.Core.Models;

namespace FlipPix.IosCompanion;

/// <summary>
/// Where Setup put things, from <c>%AppData%\FlipPix\companion.json</c> (<c>~/.config/FlipPix/companion.json</c>
/// on Linux), written by the setup wizard or the Ubuntu installer.
/// Anything missing falls back to what can be worked out from FlipPix's settings.
/// </summary>
public sealed class CompanionConfig
{
    /// <summary>The ComfyUI portable folder (holds python_embeded and ComfyUI; on Linux, venv and ComfyUI).</summary>
    public string PortableRoot { get; set; } = "";

    /// <summary>start-llm.bat from setup-llm.ps1 (start-llm.sh on Linux).</summary>
    public string LlmStartScript { get; set; } = "";

    /// <summary>The content classifier (ONNX).</summary>
    public string FilterModel { get; set; } = "";

    /// <summary>When true, connect to an external FlipPix desktop instance instead of starting local ComfyUI/LLM.</summary>
    public bool UseExternalFlipPix { get; set; }

    /// <summary>URL of the external FlipPix ComfyUI server (e.g., "http://192.168.1.100:8188").</summary>
    public string ExternalComfyUrl { get; set; } = "";

    /// <summary>URL of the external FlipPix LLM server (e.g., "http://192.168.1.100:8080").</summary>
    public string ExternalLlmUrl { get; set; } = "";

    // Create: on Linux these are ~/.config and ~/.local/share, which a fresh account may not have
    // yet, and without it GetFolderPath returns "" and every path below becomes relative.
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "FlipPix");

    public static string LocalDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "FlipPix", "companion");

    public static string ConfigPath => Path.Combine(AppDataDir, "companion.json");

    /// <summary>True only when there's no config file AND no FlipPix settings file at all.</summary>
    public static bool NeedsConfiguration(ComfyUISettings settings)
    {
        // If companion.json exists, we're already configured
        if (File.Exists(ConfigPath)) return false;
        // If FlipPix has a BaseUrl configured (it always does if FlipPix has ever run), use it
        if (!string.IsNullOrWhiteSpace(settings.BaseUrl)) return false;
        // Only need configuration if truly nothing exists
        return true;
    }

    public static CompanionConfig Load(ComfyUISettings settings)
    {
        CompanionConfig? config = null;
        try
        {
            if (File.Exists(ConfigPath)) config = JsonSerializer.Deserialize<CompanionConfig>(File.ReadAllText(ConfigPath));
        }
        catch (Exception) { /* fall back below */ }
        config ??= new CompanionConfig();

        if (string.IsNullOrWhiteSpace(config.PortableRoot) && !string.IsNullOrWhiteSpace(settings.ComfyUIFolderPath))
            config.PortableRoot = Path.GetDirectoryName(settings.ComfyUIFolderPath.TrimEnd('\\', '/')) ?? "";
        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
