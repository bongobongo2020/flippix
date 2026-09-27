namespace FlipPix.Remote.Engine;

public static class RemoteUrls
{
    /// <summary>
    /// "10.0.0.10:8188", a trailing slash, stray spaces → an absolute http URL, or "" when it
    /// cannot be one. Settings files hold what people typed, so nothing is assumed about them.
    /// </summary>
    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) return "";
        if (!s.Contains("://")) s = "http://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")
            ? s : "";
    }

    /// <summary>Whether ComfyUI runs on this machine, which decides which output-folder setting applies.</summary>
    public static bool IsLocalHost(string url)
    {
        if (!Uri.TryCreate(Normalize(url), UriKind.Absolute, out var u)) return true;
        return u.Host is "localhost" or "127.0.0.1" or "0.0.0.0" or "::1" or "[::1]";
    }
}
