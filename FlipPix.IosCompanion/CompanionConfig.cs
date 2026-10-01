using System.Text.Json;
using FlipPix.Core.Models;

namespace FlipPix.IosCompanion;

/// <summary>
/// Where Setup put things, from <c>%AppData%\FlipPix\companion.json</c> (written by the setup wizard).
/// Anything missing falls back to what can be worked out from FlipPix's settings.
/// </summary>
public sealed class CompanionConfig
{
    /// <summary>The ComfyUI portable folder (holds python_embeded and ComfyUI).</summary>
    public string PortableRoot { get; set; } = "";

    /// <summary>start-llm.bat from setup-llm.ps1.</summary>
    public string LlmStartScript { get; set; } = "";

    /// <summary>The content classifier (ONNX).</summary>
    public string FilterModel { get; set; } = "";

    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix");

    public static string LocalDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlipPix", "companion");

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
