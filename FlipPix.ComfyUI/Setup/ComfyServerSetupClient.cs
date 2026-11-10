using System.Net;
using System.Text;
using System.Text.Json;

namespace FlipPix.ComfyUI.Setup;

/// <summary>Which ComfyUI-Manager API the server exposes.</summary>
public enum ManagerApi
{
    None,

    /// <summary>ComfyUI-Manager 3.x custom node: /manager/..., /customnode/....</summary>
    Legacy,

    /// <summary>ComfyUI-Manager 4.x (the pip package ComfyUI starts with --enable-manager): /v2/....</summary>
    V2,
}

/// <summary>What the server says about itself.</summary>
public sealed class ServerInfo
{
    public string ComfyVersion { get; set; } = "";
    public string Os { get; set; } = "";
    public string PythonVersion { get; set; } = "";
    public string PytorchVersion { get; set; } = "";
    public string GpuName { get; set; } = "";
    public double VramGb { get; set; }
    public ManagerApi Manager { get; set; }
    public string ManagerVersion { get; set; } = "";

    public bool IsWindows => Os.StartsWith("nt", StringComparison.OrdinalIgnoreCase)
                             || Os.StartsWith("win", StringComparison.OrdinalIgnoreCase);

    public char PathSeparator => IsWindows ? '\\' : '/';
}

/// <summary>One node pack from ComfyUI-Manager's list.</summary>
public sealed class ManagerPack
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Repository { get; set; } = "";
    public List<string> Files { get; set; } = new();

    /// <summary>Latest Comfy Node Registry version, or "" when the pack is only on GitHub.</summary>
    public string CnrLatest { get; set; } = "";

    public string Version { get; set; } = "";

    /// <summary>"not-installed", "enabled", "disabled", "import-fail", ...</summary>
    public string State { get; set; } = "";

    public bool IsInstalled => State is "enabled" or "import-fail" or "disabled";
}

/// <summary>A model ComfyUI-Manager is allowed to download onto the server (its model-list.json).</summary>
public sealed class ManagerModel
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Base { get; set; } = "";
    public string SavePath { get; set; } = "";
    public string Url { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Size { get; set; } = "";
}

public enum QueueResult { Queued, Forbidden, NotFound, Failed }

/// <summary>
/// Talks to a (possibly freshly installed) ComfyUI server for the setup scan: what it is, which nodes
/// and models it exposes, where its model folders are, and — through ComfyUI-Manager — installing node
/// packs and whitelisted models on it. Owns its own HttpClient pointed at the URL being set up, so it
/// works before the app is restarted against a new server.
/// </summary>
public sealed class ComfyServerSetupClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private ManagerApi _manager = ManagerApi.None;

    public ComfyServerSetupClient(string baseUrl)
    {
        _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public string BaseUrl => _baseUrl;

    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------------ server

    public async Task<ServerInfo> GetServerInfoAsync(CancellationToken ct)
    {
        var info = new ServerInfo();
        using (var doc = await GetJsonAsync("/api/system_stats", TimeSpan.FromSeconds(15), ct)
                         ?? await GetJsonAsync("/system_stats", TimeSpan.FromSeconds(15), ct)
                         ?? throw new HttpRequestException($"No ComfyUI answered at {_baseUrl}."))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("system", out var sys))
            {
                info.ComfyVersion = Str(sys, "comfyui_version");
                info.Os = Str(sys, "os");
                info.PythonVersion = Str(sys, "python_version").Split(' ')[0];
                info.PytorchVersion = Str(sys, "pytorch_version");
            }
            if (root.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array && devs.GetArrayLength() > 0)
            {
                var d = devs[0];
                info.GpuName = Str(d, "name");
                if (d.TryGetProperty("vram_total", out var v) && v.TryGetDouble(out var bytes))
                    info.VramGb = Math.Round(bytes / 1024 / 1024 / 1024, 1);
            }
        }

        // Manager 4 first: a server can carry both, and the pip one is what ComfyUI actually serves then.
        foreach (var (api, path) in new[] { (ManagerApi.V2, "/api/v2/manager/version"), (ManagerApi.Legacy, "/api/manager/version") })
        {
            var text = await GetTextAsync(path, TimeSpan.FromSeconds(10), ct);
            if (text == null) continue;
            info.Manager = api;
            info.ManagerVersion = text.Trim().Trim('"');
            break;
        }
        _manager = info.Manager;
        return info;
    }

    /// <summary>The full /object_info. Several MB on a big install, so it gets a long timeout.</summary>
    public async Task<JsonDocument> GetObjectInfoAsync(CancellationToken ct)
    {
        return await GetJsonAsync("/api/object_info", TimeSpan.FromMinutes(4), ct)
               ?? throw new HttpRequestException("The server didn't return its node list (/object_info).");
    }

    /// <summary>
    /// The server's model folders per category, as absolute paths on the server (from /internal/folder_paths).
    /// Empty when the server doesn't expose them.
    /// </summary>
    public async Task<Dictionary<string, List<string>>> GetFolderPathsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var doc = await GetJsonAsync("/internal/folder_paths", TimeSpan.FromSeconds(20), ct)
                        ?? await GetJsonAsync("/api/internal/folder_paths", TimeSpan.FromSeconds(20), ct);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var list = new List<string>();
            // Shapes seen: ["path", ...]  or  [["path", ...], [".ext", ...]]
            if (p.Value.ValueKind == JsonValueKind.Array)
            {
                var arr = p.Value;
                if (arr.GetArrayLength() > 0 && arr[0].ValueKind == JsonValueKind.Array) arr = arr[0];
                foreach (var v in arr.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String) list.Add(v.GetString()!);
            }
            if (list.Count > 0) result[p.Name] = list;
        }
        return result;
    }

    public async Task<bool> IsReadyAsync(CancellationToken ct) =>
        await GetTextAsync("/api/system_stats", TimeSpan.FromSeconds(8), ct) != null;

    /// <summary>Restarts ComfyUI through Manager and waits until it answers again. False when it couldn't.</summary>
    public async Task<bool> RebootAsync(IProgress<string>? log, CancellationToken ct)
    {
        var path = _manager == ManagerApi.V2 ? "/api/v2/manager/reboot" : "/api/manager/reboot";
        try
        {
            log?.Report("Restarting ComfyUI ...");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            // Older Managers take GET, newer POST; the server may drop the connection as it goes down.
            try { await _http.PostAsync(_baseUrl + path, JsonBody("{}"), cts.Token); }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log?.Report($"Restart request failed: {ex.Message}");
        }

        // Wait for it to go away (briefly), then come back with its nodes loaded.
        var down = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < down && await IsReadyAsync(ct)) await Task.Delay(1000, ct);

        var until = DateTime.UtcNow.AddMinutes(6);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(2000, ct);
            if (await GetTextAsync("/api/object_info/KSampler", TimeSpan.FromSeconds(10), ct) != null)
            {
                log?.Report("ComfyUI is back up.");
                return true;
            }
        }
        log?.Report("ComfyUI didn't come back within 6 minutes. Check the server's console.");
        return false;
    }

    // ------------------------------------------------------------------ Manager: node packs

    /// <summary>Node class -> providing repo URL, from Manager's map. Empty without Manager.</summary>
    public async Task<Dictionary<string, string>> GetNodeMappingsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_manager == ManagerApi.None) return map;
        var path = _manager == ManagerApi.V2 ? "/api/v2/customnode/getmappings?mode=cache" : "/api/customnode/getmappings?mode=cache";
        using var doc = await GetJsonAsync(path, TimeSpan.FromMinutes(2), ct);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object) return map;

        // { "<repo url>": [ ["ClassA", "ClassB"], { "title_aux": ... } ], ... }
        foreach (var repo in doc.RootElement.EnumerateObject())
        {
            if (repo.Value.ValueKind != JsonValueKind.Array || repo.Value.GetArrayLength() == 0) continue;
            var classes = repo.Value[0];
            if (classes.ValueKind != JsonValueKind.Array) continue;
            foreach (var c in classes.EnumerateArray())
                if (c.ValueKind == JsonValueKind.String && !map.ContainsKey(c.GetString()!))
                    map[c.GetString()!] = repo.Name;
        }
        return map;
    }

    /// <summary>Every node pack Manager knows, with its install state on this server.</summary>
    public async Task<List<ManagerPack>> GetPackListAsync(CancellationToken ct)
    {
        var packs = new List<ManagerPack>();
        if (_manager == ManagerApi.None) return packs;
        var path = _manager == ManagerApi.V2
            ? "/api/v2/customnode/getlist?mode=cache&skip_update=true"
            : "/api/customnode/getlist?mode=cache&skip_update=true";
        using var doc = await GetJsonAsync(path, TimeSpan.FromMinutes(2), ct);
        if (doc == null || !doc.RootElement.TryGetProperty("node_packs", out var np) || np.ValueKind != JsonValueKind.Object)
            return packs;

        foreach (var p in np.EnumerateObject())
        {
            var v = p.Value;
            if (v.ValueKind != JsonValueKind.Object) continue;
            var pack = new ManagerPack
            {
                Id = Str(v, "id") is { Length: > 0 } id ? id : p.Name,
                Title = Str(v, "title"),
                Repository = Str(v, "repository"),
                CnrLatest = Str(v, "cnr_latest"),
                Version = Str(v, "version"),
                State = Str(v, "state"),
            };
            if (v.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                foreach (var f in files.EnumerateArray())
                    if (f.ValueKind == JsonValueKind.String) pack.Files.Add(f.GetString()!);
            packs.Add(pack);
        }
        return packs;
    }

    /// <summary>The pack in <paramref name="packs"/> that is published from <paramref name="repoUrl"/>.</summary>
    public static ManagerPack? FindPackByRepo(IEnumerable<ManagerPack> packs, string repoUrl)
    {
        var want = NormalizeRepo(repoUrl);
        if (want.Length == 0) return null;
        return packs.FirstOrDefault(p => NormalizeRepo(p.Repository) == want || p.Files.Any(f => NormalizeRepo(f) == want));
    }

    /// <summary>
    /// The pack Manager's node map names: a repo URL for GitHub packs, but the registry id
    /// ("comfyui-image-saver") for registry packs.
    /// </summary>
    public static ManagerPack? FindPack(IEnumerable<ManagerPack> packs, string repoOrId)
    {
        if (string.IsNullOrWhiteSpace(repoOrId)) return null;
        if (repoOrId.Contains("://") || repoOrId.StartsWith("git@")) return FindPackByRepo(packs, repoOrId);
        return packs.FirstOrDefault(p => p.Id.Equals(repoOrId, StringComparison.OrdinalIgnoreCase))
               ?? packs.FirstOrDefault(p => NodeCatalogName(p).Equals(repoOrId, StringComparison.OrdinalIgnoreCase));

        static string NodeCatalogName(ManagerPack p) =>
            FlipPix.Core.Services.NodeCatalog.PackNameFromRepo(string.IsNullOrEmpty(p.Repository) ? p.Files.FirstOrDefault() : p.Repository);
    }

    public static string NormalizeRepo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var s = url.Trim().ToLowerInvariant().TrimEnd('/');
        if (s.EndsWith(".git")) s = s[..^4];
        s = s.Replace("http://", "https://");
        if (s.StartsWith("git@github.com:")) s = "https://github.com/" + s["git@github.com:".Length..];
        return s;
    }

    /// <summary>
    /// Queues a node-pack install (or, for a disabled pack, enabling it). Registry packs install as their
    /// latest release; GitHub-only packs Manager lists install from that URL. Manager's default security
    /// level allows both; it refuses URLs it doesn't list (that needs allow_git_url_install).
    /// </summary>
    public async Task<(QueueResult result, string message)> QueuePackInstallAsync(ManagerPack pack, CancellationToken ct)
    {
        var uiId = "flippix-" + pack.Id;
        var registry = !string.IsNullOrEmpty(pack.CnrLatest);
        var enableOnly = pack.State == "disabled";

        if (_manager == ManagerApi.V2)
        {
            object task = enableOnly && registry
                ? new { ui_id = uiId, client_id = "flippix", kind = "enable", @params = new { cnr_id = pack.Id } }
                : new
                {
                    ui_id = uiId,
                    client_id = "flippix",
                    kind = "install",
                    @params = new
                    {
                        id = pack.Id,
                        version = registry ? pack.CnrLatest : "nightly",
                        selected_version = registry ? "latest" : "nightly",
                        repository = FirstNonEmpty(pack.Repository, pack.Files.FirstOrDefault()),
                        mode = "cache",
                        channel = "default",
                        skip_post_install = false,
                    },
                };
            return await PostQueueAsync("/api/v2/manager/queue/task", task, ct);
        }

        if (_manager == ManagerApi.Legacy)
        {
            var body = new Dictionary<string, object?>
            {
                ["id"] = pack.Id,
                ["ui_id"] = uiId,
                ["channel"] = "default",
                ["mode"] = "cache",
                ["files"] = pack.Files.Count > 0 ? pack.Files : new List<string> { pack.Repository },
                ["repository"] = pack.Repository,
                ["skip_post_install"] = enableOnly,
            };
            if (registry)
            {
                body["version"] = pack.CnrLatest;
                body["selected_version"] = "latest";
            }
            else
            {
                body["version"] = "unknown";
                body["selected_version"] = "unknown";
            }
            return await PostQueueAsync("/api/manager/queue/install", body, ct);
        }

        return (QueueResult.NotFound, "ComfyUI-Manager isn't installed on the server.");
    }

    /// <summary>
    /// Queues Manager's "Try fix" for an installed pack that fails to import: it reinstalls the pack's
    /// Python requirements with Manager's own (torch-safe) handling.
    /// </summary>
    public async Task<(QueueResult result, string message)> QueuePackFixAsync(ManagerPack pack, CancellationToken ct)
    {
        var registry = !string.IsNullOrEmpty(pack.CnrLatest);
        var version = string.IsNullOrEmpty(pack.Version) ? (registry ? pack.CnrLatest : "unknown") : pack.Version;
        if (_manager == ManagerApi.V2)
        {
            var task = new
            {
                ui_id = "flippix-fix-" + pack.Id,
                client_id = "flippix",
                kind = "fix",
                @params = new { node_name = pack.Id, node_ver = version },
            };
            return await PostQueueAsync("/api/v2/manager/queue/task", task, ct);
        }
        if (_manager == ManagerApi.Legacy)
        {
            var body = new
            {
                id = pack.Id,
                ui_id = "flippix-fix-" + pack.Id,
                version,
                files = pack.Files.Count > 0 ? pack.Files : new List<string> { pack.Repository },
            };
            return await PostQueueAsync("/api/manager/queue/fix", body, ct);
        }
        return (QueueResult.NotFound, "ComfyUI-Manager isn't installed on the server.");
    }

    // ------------------------------------------------------------------ Manager: models

    /// <summary>The models Manager may download onto the server (its whitelist).</summary>
    public async Task<List<ManagerModel>> GetManagerModelListAsync(CancellationToken ct)
    {
        var list = new List<ManagerModel>();
        if (_manager == ManagerApi.None) return list;
        var path = _manager == ManagerApi.V2 ? "/api/v2/externalmodel/getlist?mode=cache" : "/api/externalmodel/getlist?mode=cache";
        using var doc = await GetJsonAsync(path, TimeSpan.FromMinutes(1), ct);
        if (doc == null || !doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var m in models.EnumerateArray())
        {
            list.Add(new ManagerModel
            {
                Name = Str(m, "name"),
                Type = Str(m, "type"),
                Base = Str(m, "base"),
                SavePath = Str(m, "save_path"),
                Url = Str(m, "url"),
                Filename = Str(m, "filename"),
                Size = Str(m, "size"),
            });
        }
        return list;
    }

    public async Task<(QueueResult result, string message)> QueueModelInstallAsync(ManagerModel m, CancellationToken ct)
    {
        var payload = new
        {
            name = m.Name,
            type = m.Type,
            @base = m.Base,
            save_path = m.SavePath,
            url = m.Url,
            filename = m.Filename,
            ui_id = "flippix-model-" + m.Filename,
        };
        if (_manager == ManagerApi.V2)
            return await PostQueueAsync("/api/v2/manager/queue/task",
                new { ui_id = payload.ui_id, client_id = "flippix", kind = "install-model", @params = payload }, ct);
        if (_manager == ManagerApi.Legacy)
            return await PostQueueAsync("/api/manager/queue/install_model", payload, ct);
        return (QueueResult.NotFound, "ComfyUI-Manager isn't installed on the server.");
    }

    // ------------------------------------------------------------------ Manager: queue

    public async Task StartQueueAsync(CancellationToken ct)
    {
        var path = _manager == ManagerApi.V2 ? "/api/v2/manager/queue/start" : "/api/manager/queue/start";
        using var resp = await _http.PostAsync(_baseUrl + path, JsonBody("{}"), ct);
    }

    /// <summary>Waits for Manager's queue to finish, reporting "done/total" as it goes.</summary>
    public async Task WaitForQueueAsync(IProgress<string>? log, TimeSpan timeout, CancellationToken ct)
    {
        var path = _manager == ManagerApi.V2 ? "/api/v2/manager/queue/status" : "/api/manager/queue/status";
        var until = DateTime.UtcNow + timeout;
        var last = "";
        var idleChecks = 0;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(2000, ct);
            using var doc = await GetJsonAsync(path, TimeSpan.FromSeconds(15), ct);
            if (doc == null) continue;
            var r = doc.RootElement;
            int total = Int(r, "total_count"), done = Int(r, "done_count");
            bool processing = r.TryGetProperty("is_processing", out var p) && p.ValueKind == JsonValueKind.True;
            var line = $"Manager queue: {done}/{total} done";
            if (line != last) { log?.Report(line); last = line; }
            if (!processing && done >= total) { if (++idleChecks >= 2) return; }
            else idleChecks = 0;
        }
        log?.Report("Manager's queue is still running; it will carry on in the background.");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(QueueResult, string)> PostQueueAsync(string path, object body, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsync(_baseUrl + path, JsonBody(JsonSerializer.Serialize(body)), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return (QueueResult.Queued, "");
            // Manager answers a security refusal with 403 (level) or 404 (risk) and "security error" text.
            if (resp.StatusCode == HttpStatusCode.Forbidden || text.Contains("security", StringComparison.OrdinalIgnoreCase))
                return (QueueResult.Forbidden, "ComfyUI-Manager's security level refused it.");
            if (resp.StatusCode == HttpStatusCode.NotFound) return (QueueResult.NotFound, Trim(text));
            return (QueueResult.Failed, $"{(int)resp.StatusCode}: {Trim(text)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (QueueResult.Failed, ex.Message); }

        static string Trim(string s) => s.Length > 200 ? s[..200] : s;
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<JsonDocument?> GetJsonAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var resp = await _http.GetAsync(_baseUrl + path, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private async Task<string?> GetTextAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var resp = await _http.GetAsync(_baseUrl + path, cts.Token);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(cts.Token) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.ToString() : ""
            : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
