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

    // Create: on Linux these are ~/.config and ~/.local/share, which a fresh account may not have
    // yet, and without it GetFolderPath returns "" and every path below becomes relative.
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "FlipPix");

    public static string LocalDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "FlipPix", "companion");

    public static CompanionConfig Load(ComfyUISettings settings)
    {
        CompanionConfig? config = null;
        var path = Path.Combine(AppDataDir, "companion.json");
        try
        {
            if (File.Exists(path)) config = JsonSerializer.Deserialize<CompanionConfig>(File.ReadAllText(path));
        }
        catch (Exception) { /* fall back below */ }
        config ??= new CompanionConfig();

        if (string.IsNullOrWhiteSpace(config.PortableRoot) && !string.IsNullOrWhiteSpace(settings.ComfyUIFolderPath))
            config.PortableRoot = Path.GetDirectoryName(settings.ComfyUIFolderPath.TrimEnd('\\', '/')) ?? "";
        return config;
    }
}
