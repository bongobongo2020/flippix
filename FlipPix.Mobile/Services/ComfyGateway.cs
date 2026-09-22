using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json;
using FlipPix.ComfyUI.Http;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.ComfyUI.WebSocket;
using FlipPix.Core.Models;

namespace FlipPix.Mobile.Services;

/// <summary>One file a finished prompt wrote, addressed the way ComfyUI's /view expects.</summary>
public sealed record ComfyOutput(string NodeId, string FileName, string Subfolder, string Type)
{
    public bool IsVideo => FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                        || FileName.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                        || FileName.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The phone's handle on ComfyUI. Wraps the desktop's <see cref="ComfyUIService"/>, so a graph sent
/// from here goes through the same pre-submit repairs (missing inputs, path separators, RTX widget
/// renames, error surfacing). The shared client fixes its base address at construction, so a changed
/// address in Settings rebuilds the whole stack rather than mutating it.
/// </summary>
public sealed class ComfyGateway : IDisposable
{
    private readonly MobileLogger _logger = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ComfyUIService? _service;
    private ComfyUIHttpClient? _http;
    private HttpClient? _raw;
    private string _url = "";

    public string Url => _url;

    public void Configure(string url)
    {
        url = MobileSettings.NormalizeUrl(url);
        if (url == _url && _service != null) return;
        DisposeStack();
        _url = url;
        if (url.Length == 0) return;

        var settings = new ComfyUISettings { BaseUrl = url, MaxRetries = 2, RetryDelayMilliseconds = 1500 };
        _http = new ComfyUIHttpClient(new HttpClient(), _logger, settings);
        _service = new ComfyUIService(_http, new ComfyUIWebSocketClient(_logger, url), _logger, settings);
        _raw = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>Returns null when reachable, otherwise a sentence saying what went wrong.</summary>
    public static async Task<string?> ProbeAsync(string url, CancellationToken ct = default)
    {
        url = MobileSettings.NormalizeUrl(url);
        if (url.Length == 0) return "That doesn't look like an address. Try 10.0.0.10:8188.";
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(6) };
            using var resp = await http.GetAsync("/system_stats", ct);
            if (!resp.IsSuccessStatusCode) return $"The server answered {(int)resp.StatusCode}, which isn't ComfyUI.";
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("system", out _) ? null : "The server answered, but not like ComfyUI.";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return "No answer within 6 seconds. Is the phone on the same network as the server?";
        }
        catch (HttpRequestException ex)
        {
            return "Couldn't reach it: " + ex.Message;
        }
    }

    /// <summary>
    /// Submits a graph and waits for it. <paramref name="onStep"/> receives sampler steps as they
    /// arrive (value, max). One job at a time: the phone never races itself for the GPU.
    /// </summary>
    public async Task<IReadOnlyList<ComfyOutput>> RunAsync(object graph, Action<int, int>? onStep, CancellationToken ct)
    {
        var service = _service ?? throw new InvalidOperationException("Add your ComfyUI address in Settings first.");
        // A marker only this job carries, so Stop can find it in the server's queue and cancel it
        // without touching anyone else's job on a shared server. _meta is ignored by execution.
        var marker = "flippix-mobile-" + Guid.NewGuid().ToString("N");
        if (graph is JsonObject g && g.FirstOrDefault().Value is JsonObject first)
        {
            if (first["_meta"] is not JsonObject meta) first["_meta"] = meta = new JsonObject();
            meta["flippix_run"] = marker;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (!service.IsConnected) await service.ConnectAsync(ct);
            var progress = new Progress<ProgressMessage>(m =>
            {
                if (m.Data is { Max: > 0 } d) onStep?.Invoke(d.Value, d.Max);
            });
            try
            {
                var promptId = await service.ExecuteWorkflowAsync(graph, progress, ct, TimeSpan.FromHours(2));
                return await ReadOutputsAsync(promptId, ct);
            }
            catch (OperationCanceledException)
            {
                await CancelOnServerAsync(marker);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes a stopped job off the server: deleted if still waiting, interrupted if running. Only
    /// the job carrying <paramref name="marker"/> is touched; the interrupt is sent only when the
    /// running job is that one, because /interrupt stops whatever is running.
    /// </summary>
    private async Task CancelOnServerAsync(string marker)
    {
        if (_raw == null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var doc = JsonDocument.Parse(await _raw.GetStringAsync("/queue", cts.Token));
            string? Find(string key) => doc.RootElement.TryGetProperty(key, out var list)
                ? list.EnumerateArray().Where(e => e.GetRawText().Contains(marker, StringComparison.Ordinal))
                      .Select(e => e[1].GetString()).FirstOrDefault()
                : null;

            if (Find("queue_pending") is { } pending)
                await _raw.PostAsync("/queue", JsonContent.Create(new { delete = new[] { pending } }), cts.Token);
            if (Find("queue_running") is not null)
                await _raw.PostAsync("/interrupt", JsonContent.Create(new { }), cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("Couldn't take the stopped job off the server: " + ex.Message);
        }
    }

    /// <summary>
    /// Asks ComfyUI to unload its models and free VRAM. Used when an LLM on the same GPU can't load:
    /// ComfyUI keeps a finished render's weights resident, which is right for the next render and
    /// leaves nothing for a model that wants to start.
    /// </summary>
    public async Task<bool> FreeMemoryAsync(CancellationToken ct = default)
    {
        if (_service == null) return false;
        try { return await _service.FreeMemoryAsync(unloadModels: true, freeMemory: true, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    /// <summary>Uploads JPEG bytes to ComfyUI's input folder and returns the name to load them by.</summary>
    public async Task<string> UploadJpegAsync(byte[] jpeg, CancellationToken ct)
    {
        var service = _service ?? throw new InvalidOperationException("Add your ComfyUI address in Settings first.");
        // The shared client uploads from a path; the file name becomes the server-side name.
        var path = Path.Combine(Path.GetTempPath(), $"flippix_ref_{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, jpeg, ct);
        try { return await service.UploadImageAsync(path, ct); }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    public async Task<byte[]> DownloadAsync(ComfyOutput output, CancellationToken ct)
    {
        var raw = _raw ?? throw new InvalidOperationException("Not connected.");
        return await raw.GetByteArrayAsync(ViewPath(output), ct);
    }

    /// <summary>An absolute /view URL, for players that stream rather than download.</summary>
    public string ViewUrl(ComfyOutput output) => _url + ViewPath(output);

    private static string ViewPath(ComfyOutput o) =>
        $"/view?filename={Uri.EscapeDataString(o.FileName)}&subfolder={Uri.EscapeDataString(o.Subfolder)}&type={Uri.EscapeDataString(o.Type)}";

    /// <summary>
    /// Reads /history/{id} directly: the per-prompt endpoint instead of the full history, which on a
    /// busy server is megabytes a phone shouldn't pull per image. Scans every media key a node can
    /// report under (images, videos, gifs, audio, files) — VHS reports its mp4 under "gifs".
    /// </summary>
    private async Task<IReadOnlyList<ComfyOutput>> ReadOutputsAsync(string promptId, CancellationToken ct)
    {
        var raw = _raw!;
        var found = new List<ComfyOutput>();
        for (var attempt = 0; attempt < 6 && found.Count == 0; attempt++)
        {
            if (attempt > 0) await Task.Delay(1500, ct);
            using var doc = JsonDocument.Parse(await raw.GetStringAsync($"/history/{promptId}", ct));
            if (!doc.RootElement.TryGetProperty(promptId, out var entry)
                || !entry.TryGetProperty("outputs", out var outputs)) continue;

            foreach (var node in outputs.EnumerateObject())
            foreach (var key in new[] { "images", "videos", "gifs", "audio", "files" })
            {
                if (!node.Value.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array) continue;
                foreach (var f in list.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object || !f.TryGetProperty("filename", out var fn)) continue;
                    var type = f.TryGetProperty("type", out var t) ? t.GetString() ?? "output" : "output";
                    if (type == "temp") continue; // previews, not results
                    found.Add(new ComfyOutput(node.Name, fn.GetString() ?? "",
                        f.TryGetProperty("subfolder", out var sf) ? sf.GetString() ?? "" : "", type));
                }
            }
        }
        return found;
    }

    private void DisposeStack()
    {
        _service?.Dispose();
        _raw?.Dispose();
        _service = null; _http = null; _raw = null;
    }

    public void Dispose() => DisposeStack();
}
