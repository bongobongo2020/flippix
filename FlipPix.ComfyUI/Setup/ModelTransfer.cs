using System.Net.Http.Headers;

namespace FlipPix.ComfyUI.Setup;

/// <summary>
/// Moves model files into a ComfyUI models folder (local or on a share): downloads that resume where a
/// previous attempt stopped, copies from a folder the user already has, and an index of model files
/// under folders the user points at, so existing copies are found before anything is downloaded.
/// </summary>
public static class ModelTransfer
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    static ModelTransfer()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("FlipPix-ServerSetup/1.0");
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="target"/> through "target.part", resuming a
    /// partial file when the server supports ranges. Reports (bytes done, total or -1).
    /// </summary>
    public static async Task DownloadAsync(string url, string target, IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        bool resumed = existing > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed) existing = 0;
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength is long len ? len + existing : -1;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[1 << 20];
            long done = existing;
            int read;
            var lastReport = DateTime.MinValue;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                {
                    progress?.Report((done, total));
                    lastReport = DateTime.UtcNow;
                }
            }
            progress?.Report((done, total));
        }

        if (File.Exists(target)) File.Delete(target);
        File.Move(part, target);
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="target"/> through "target.part".</summary>
    public static async Task CopyAsync(string source, string target, IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        var total = new FileInfo(source).Length;
        await using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[4 << 20];
            long done = 0;
            int read;
            var lastReport = DateTime.MinValue;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                {
                    progress?.Report((done, total));
                    lastReport = DateTime.UtcNow;
                }
            }
            progress?.Report((done, total));
        }
        if (File.Exists(target)) File.Delete(target);
        File.Move(part, target);
    }

    /// <summary>
    /// Every model file under <paramref name="folders"/>, keyed by filename (case-insensitive). Unreadable
    /// folders are skipped; the walk stops early when cancelled.
    /// </summary>
    public static Dictionary<string, List<FileInfo>> IndexModelFiles(IEnumerable<string> folders, CancellationToken ct)
    {
        var index = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
            MaxRecursionDepth = 8,
        };
        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!WorkflowRequirementScanner.HasModelExtension(file)) continue;
                    var fi = new FileInfo(file);
                    if (!index.TryGetValue(fi.Name, out var list)) index[fi.Name] = list = new List<FileInfo>();
                    list.Add(fi);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* unreadable folder */ }
        }
        return index;
    }

    /// <summary>Human-readable byte count, e.g. "8.7 GB".</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "?";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u <= 1 ? $"{v:0} {units[u]}" : $"{v:0.#} {units[u]}";
    }
}
