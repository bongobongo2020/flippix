using System;
using System.Collections.Generic;

namespace FlipPix.Core.Models;

public class LMStudioSettings
{
    public string BaseUrl { get; set; } = "http://alien:8080";
    public string SelectedModel { get; set; } = string.Empty;
    public int ConnectionTimeout { get; set; } = 30000; // 30 seconds
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 2000;
    public int MaxImageSize { get; set; } = 256; // Maximum image dimension for token efficiency
    public bool AutoConnect { get; set; } = true;

    /// <summary>
    /// Recently used LM Studio / llama-server base URLs, most-recent first. Lets the user
    /// keep a list of remote computers and switch between them without retyping the address.
    /// </summary>
    public List<string> ServerHistory { get; set; } = new();

    /// <summary>
    /// Moves <paramref name="baseUrl"/> to the front of <see cref="ServerHistory"/> (creating
    /// the list if needed), de-duplicating case-insensitively and capping the list length.
    /// Blank/invalid URLs are ignored.
    /// </summary>
    public void RememberServer(string? baseUrl, int maxEntries = 10)
    {
        var url = NormalizeUrl(baseUrl);
        if (string.IsNullOrEmpty(url)) return;

        ServerHistory ??= new List<string>();
        ServerHistory.RemoveAll(s => string.Equals(NormalizeUrl(s), url, StringComparison.OrdinalIgnoreCase));
        ServerHistory.Insert(0, url);
        if (ServerHistory.Count > maxEntries)
            ServerHistory.RemoveRange(maxEntries, ServerHistory.Count - maxEntries);
    }

    private static string NormalizeUrl(string? url) => (url ?? string.Empty).Trim().TrimEnd('/');

    /// <summary>
    /// Builds an "http://host:port" base URL from separate fields without the strict validation
    /// of <see cref="UriBuilder"/>, which throws on perfectly valid Windows host names (e.g. ones
    /// containing underscores) and silently corrupted the saved URL.
    /// </summary>
    public static string BuildBaseUrl(string? host, string? port)
    {
        host = (host ?? string.Empty).Trim();
        port = (port ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(host)) host = "localhost";
        return string.IsNullOrEmpty(port) ? $"http://{host}" : $"http://{host}:{port}";
    }

    /// <summary>
    /// Splits a base URL into host and port, tolerating values that <see cref="Uri"/> rejects.
    /// </summary>
    public static (string Host, string Port) ParseBaseUrl(string? baseUrl)
    {
        var raw = (baseUrl ?? string.Empty).Trim();
        try
        {
            var uri = new Uri(raw);
            return (uri.Host, uri.Port >= 0 ? uri.Port.ToString() : string.Empty);
        }
        catch
        {
            var s = raw;
            var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0) s = s.Substring(schemeIdx + 3);
            s = s.TrimEnd('/');
            var slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);
            var parts = s.Split(':');
            var host = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : "localhost";
            var port = parts.Length > 1 ? parts[1] : string.Empty;
            return (host, port);
        }
    }
}
