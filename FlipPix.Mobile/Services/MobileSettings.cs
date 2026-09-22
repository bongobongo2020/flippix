using System.Text.Json;

namespace FlipPix.Mobile.Services;

/// <summary>
/// The phone's whole configuration: where ComfyUI and the LLM live. Kept deliberately small —
/// everything else is a sensible default chosen per page, not a setting.
/// </summary>
public sealed class MobileSettings
{
    public string ComfyUrl { get; set; } = "";
    /// <summary>An OpenAI-compatible endpoint (LM Studio, llama.cpp, vLLM), with or without /v1.</summary>
    public string LlmUrl { get; set; } = "";
    /// <summary>Empty means "whatever the server has loaded".</summary>
    public string LlmModel { get; set; } = "";

    public bool IsComfyConfigured => Uri.TryCreate(NormalizeUrl(ComfyUrl), UriKind.Absolute, out _);

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlipPixMobile", "settings.json");

    public static MobileSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<MobileSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* a corrupt file is the same as no file: the user re-enters two addresses */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Accepts what people actually type on a phone keyboard — "10.0.0.10:8188", a trailing
    /// slash, stray spaces — and returns an absolute http URL, or "" when it cannot be one.
    /// </summary>
    public static string NormalizeUrl(string? raw)
    {
        var s = (raw ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) return "";
        if (!s.Contains("://")) s = "http://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")
            ? s : "";
    }
}
