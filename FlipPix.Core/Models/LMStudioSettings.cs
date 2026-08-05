using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace FlipPix.Core.Models;

/// <summary>
/// A saved LM Studio / llama-server target: where it lives, which model to send to it, and the
/// friendly names the user gave both so status messages read "Alien Box · Qwen2.5-VL 7B" instead
/// of "http://alien:8080 · qwen2.5-vl-7b-instruct-q4_k_m".
/// </summary>
public class LlmServerProfile
{
    /// <summary>Friendly server name, e.g. "Alien Box". Falls back to the host when blank.</summary>
    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Model id actually sent to the API for this server.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Friendly model name, e.g. "Qwen2.5-VL 7B". Falls back to <see cref="Model"/>.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>True for the profile the app currently sends image analysis to.</summary>
    public bool IsDefault { get; set; }

    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name.Trim() : LMStudioSettings.ParseBaseUrl(BaseUrl).Host;

    [JsonIgnore]
    public string ModelDisplayName =>
        !string.IsNullOrWhiteSpace(ModelName) ? ModelName.Trim() : Model;

    /// <summary>One-line label for the saved-servers dropdown.</summary>
    [JsonIgnore]
    public string ListLabel
    {
        get
        {
            var label = $"{DisplayName} — {BaseUrl}";
            if (!string.IsNullOrWhiteSpace(ModelDisplayName)) label += $" · {ModelDisplayName}";
            if (IsDefault) label += "  (default)";
            return label;
        }
    }

    // The saved-servers dropdown falls back to ToString() for its item text (and for the
    // accessibility name), so make that the label rather than the type name.
    public override string ToString() => ListLabel;
}

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
    /// Kept for backwards compatibility with settings written before <see cref="Servers"/>;
    /// <see cref="EnsureProfiles"/> folds these into named profiles.
    /// </summary>
    public List<string> ServerHistory { get; set; } = new();

    /// <summary>
    /// Saved servers with their friendly names and preferred model. The profile whose
    /// <see cref="LlmServerProfile.BaseUrl"/> matches <see cref="BaseUrl"/> is the default -
    /// the one every image analysis is sent to.
    /// </summary>
    public List<LlmServerProfile> Servers { get; set; } = new();

    /// <summary>
    /// Folds the legacy <see cref="ServerHistory"/> entries and the active <see cref="BaseUrl"/>
    /// into <see cref="Servers"/>, and marks exactly one profile as the default. Cheap and
    /// idempotent - safe to call on every settings load or dialog open.
    /// </summary>
    public void EnsureProfiles()
    {
        Servers ??= new List<LlmServerProfile>();
        ServerHistory ??= new List<string>();

        foreach (var url in ServerHistory)
        {
            var normalized = NormalizeUrl(url);
            if (string.IsNullOrEmpty(normalized)) continue;
            if (FindProfile(normalized) == null)
                Servers.Add(new LlmServerProfile { BaseUrl = normalized });
        }

        var active = FindProfile(BaseUrl);
        if (active == null && !string.IsNullOrEmpty(NormalizeUrl(BaseUrl)))
        {
            active = new LlmServerProfile { BaseUrl = NormalizeUrl(BaseUrl), Model = SelectedModel };
            Servers.Insert(0, active);
        }

        // The active server/model IS the default target; keep the persisted flag in sync so the
        // UI can show which saved entry is in use.
        foreach (var profile in Servers)
            profile.IsDefault = active != null && ReferenceEquals(profile, active);

        if (active != null && string.IsNullOrWhiteSpace(active.Model) && !string.IsNullOrWhiteSpace(SelectedModel))
            active.Model = SelectedModel;
    }

    /// <summary>Finds the saved profile for <paramref name="baseUrl"/>, or null.</summary>
    public LlmServerProfile? FindProfile(string? baseUrl)
    {
        var url = NormalizeUrl(baseUrl);
        if (string.IsNullOrEmpty(url) || Servers == null) return null;
        return Servers.FirstOrDefault(p =>
            string.Equals(NormalizeUrl(p.BaseUrl), url, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The profile the app currently sends analysis requests to (may be null).</summary>
    [JsonIgnore]
    public LlmServerProfile? DefaultProfile => FindProfile(BaseUrl);

    /// <summary>Friendly server name for status messages, falling back to the host.</summary>
    [JsonIgnore]
    public string ServerDisplayName
    {
        get
        {
            var name = DefaultProfile?.Name;
            return !string.IsNullOrWhiteSpace(name) ? name.Trim() : ParseBaseUrl(BaseUrl).Host;
        }
    }

    /// <summary>Friendly name for the default model, falling back to its raw id.</summary>
    [JsonIgnore]
    public string ModelDisplayName => FriendlyModelName(SelectedModel);

    /// <summary>
    /// Friendly name for <paramref name="model"/> if the default profile named it, else the raw id.
    /// </summary>
    public string FriendlyModelName(string? model)
    {
        var id = (model ?? string.Empty).Trim();
        var profile = DefaultProfile;
        if (profile != null
            && !string.IsNullOrWhiteSpace(profile.ModelName)
            && string.Equals((profile.Model ?? string.Empty).Trim(), id, StringComparison.OrdinalIgnoreCase))
        {
            return profile.ModelName.Trim();
        }
        return id;
    }

    /// <summary>
    /// Human-readable description of where an analysis request is going, e.g.
    /// "Alien Box (http://alien:8080) · Qwen2.5-VL 7B [qwen2.5-vl-7b-instruct]".
    /// Pass the model actually being used when it differs from <see cref="SelectedModel"/>.
    /// </summary>
    public string DescribeTarget(string? model = null)
    {
        var url = NormalizeUrl(BaseUrl);
        var profile = DefaultProfile;
        var name = profile?.Name;
        var server = !string.IsNullOrWhiteSpace(name) ? $"{name.Trim()} ({url})" : url;

        var id = (string.IsNullOrWhiteSpace(model) ? SelectedModel : model)?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(id)) return $"{server} · (no model selected)";

        var friendly = FriendlyModelName(id);
        var modelLabel = string.Equals(friendly, id, StringComparison.Ordinal) ? id : $"{friendly} [{id}]";
        return $"{server} · {modelLabel}";
    }

    /// <summary>
    /// Adds or updates the profile for <paramref name="baseUrl"/> and, when
    /// <paramref name="makeDefault"/> is set, points <see cref="BaseUrl"/>/<see cref="SelectedModel"/>
    /// at it so every analysis uses it from now on. Returns the stored profile.
    /// </summary>
    public LlmServerProfile SaveProfile(string? baseUrl, string? name, string? model, string? modelName, bool makeDefault = true)
    {
        Servers ??= new List<LlmServerProfile>();
        var url = NormalizeUrl(baseUrl);

        var profile = FindProfile(url);
        if (profile == null)
        {
            profile = new LlmServerProfile { BaseUrl = url };
            Servers.Insert(0, profile);
        }

        profile.BaseUrl = url;
        profile.Name = (name ?? string.Empty).Trim();
        profile.Model = (model ?? string.Empty).Trim();
        profile.ModelName = (modelName ?? string.Empty).Trim();

        if (makeDefault) ApplyProfile(profile);
        else EnsureProfiles();

        RememberServer(url);
        return profile;
    }

    /// <summary>Makes <paramref name="profile"/> the default target for image analysis.</summary>
    public void ApplyProfile(LlmServerProfile? profile)
    {
        if (profile == null) return;

        BaseUrl = NormalizeUrl(profile.BaseUrl);
        if (!string.IsNullOrWhiteSpace(profile.Model)) SelectedModel = profile.Model.Trim();

        foreach (var p in Servers ?? new List<LlmServerProfile>())
            p.IsDefault = ReferenceEquals(p, profile);
    }

    /// <summary>Removes the saved profile for <paramref name="baseUrl"/> (and its history entry).</summary>
    public void RemoveProfile(string? baseUrl)
    {
        var url = NormalizeUrl(baseUrl);
        if (string.IsNullOrEmpty(url)) return;

        Servers?.RemoveAll(p => string.Equals(NormalizeUrl(p.BaseUrl), url, StringComparison.OrdinalIgnoreCase));
        ServerHistory?.RemoveAll(s => string.Equals(NormalizeUrl(s), url, StringComparison.OrdinalIgnoreCase));
    }

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

        // Keep the named-profile list in step with the plain history.
        Servers ??= new List<LlmServerProfile>();
        if (FindProfile(url) == null)
            Servers.Insert(0, new LlmServerProfile { BaseUrl = url });
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
