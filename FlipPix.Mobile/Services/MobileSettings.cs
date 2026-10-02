using System.Text.Json;
using FlipPix.Remote.Contracts;

namespace FlipPix.Mobile.Services;

/// <summary>
/// The phone's whole configuration: which computer it is paired with. Everything else (ComfyUI,
/// the writing assistant, the output folder) is the computer's business.
/// </summary>
public sealed class MobileSettings
{
    public string ServerUrl { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Token { get; set; } = "";

    public bool IsPaired => ServerUrl.Length > 0 && Token.Length > 0;

    /// <summary>
    /// Where remote.json lives. iOS sets its own: there LocalApplicationData is Documents, which Files
    /// shows as On My iPad › FlipPix, and the pairing must not sit beside the saved pictures.
    /// </summary>
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlipPixMobile");

    private static string FilePath => Path.Combine(Folder, "remote.json");

    public static MobileSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<MobileSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* a corrupt file is the same as no file: the phone pairs again */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Accepts what people type on a phone keyboard ("10.0.0.5", "10.0.0.5:47800", a trailing slash,
    /// stray spaces) and returns "http://host:port", or "" when it cannot be one. No port means the
    /// remote's own.
    /// </summary>
    public static string NormalizeUrl(string? raw)
    {
        var s = (raw ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) return "";
        if (!s.Contains("://")) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https") || u.Host.Length == 0)
            return "";
        var port = u.IsDefaultPort && !HasExplicitPort(s) ? RemoteApi.DefaultPort : u.Port;
        return $"{u.Scheme}://{u.Host}:{port}";
    }

    private static bool HasExplicitPort(string url)
    {
        var afterScheme = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var host = afterScheme.Split('/')[0];
        return host.Contains(':') && !host.StartsWith('[');
    }
}
