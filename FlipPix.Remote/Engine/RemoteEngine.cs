using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.Remote.Contracts;
using FlipPix.Remote.Jobs;
using FlipPix.Remote.Library;

namespace FlipPix.Remote.Engine;

/// <summary>
/// Everything a phone job needs from the desktop, read from the desktop's settings at the moment it
/// is needed: change ComfyUI or the LLM in FlipPix's Settings and the next job uses the new one.
/// </summary>
public sealed class RemoteEngine : IDisposable
{
    private readonly Func<ComfyUISettings> _settings;

    public RemoteEngine(Func<ComfyUISettings> settings, IAppLogger? logger, string dataDir, RemoteOptions? options = null)
    {
        _settings = settings;
        Options = options ?? new RemoteOptions();
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        Logger = new RemoteLogger(logger);
        Comfy = new ComfyGateway(Logger);
        Llm = new LlmClient(() =>
        {
            var lm = _settings().LMStudioSettings;
            return new LlmTarget(lm?.BaseUrl ?? "", lm?.SelectedModel ?? "");
        }, Comfy);
        Library = new LibraryIndex(OutputRoot);
        Thumbs = new Thumbnailer(Path.Combine(dataDir, "thumbs"));
        Library.Scanned += WarmNewest;
        Uploads = new UploadStore(Path.Combine(dataDir, "uploads"), Library);
    }

    public string DataDir { get; }
    public RemoteOptions Options { get; }

    /// <summary>The looks this host offers: all of them, or the ones <see cref="RemoteOptions.Looks"/> names.</summary>
    public IReadOnlyList<ImageLook> Looks =>
        Options.Looks is { } keys ? ImageLook.All.Where(l => keys.Contains(l.Key)).ToList() : ImageLook.All;
    public IAppLogger Logger { get; }
    public ComfyGateway Comfy { get; }
    public LlmClient Llm { get; }
    public LibraryIndex Library { get; }
    public Thumbnailer Thumbs { get; }
    public UploadStore Uploads { get; }

    public ComfyUISettings Settings => _settings();

    /// <summary>Points the ComfyUI client at the address in Settings; a no-op when it hasn't changed.</summary>
    public void SyncComfy() => Comfy.Configure(Settings.BaseUrl);

    /// <summary>
    /// The folder ComfyUI saves into, as this desktop sees it. A remote server's output is read over
    /// the share in RemoteOutputFolderPath; ResolveOutputFolder falls back to the other setting when
    /// the preferred one is unset or unreachable.
    /// </summary>
    public string OutputRoot()
    {
        var s = Settings;
        try { return s.ResolveOutputFolder(!RemoteUrls.IsLocalHost(s.BaseUrl)); }
        catch (Exception) { return s.OutputFolderPath ?? ""; }
    }

    /// <summary>"Alien Box (http://…) · Qwen2.5-VL 7B", or "" when no LLM is set.</summary>
    public string LlmLabel()
    {
        var lm = Settings.LMStudioSettings;
        if (lm == null || !Llm.Target.IsSet) return "";
        try { return lm.DescribeTarget(); }
        catch (Exception) { return lm.BaseUrl; }
    }

    /// <summary>The output folder's entry for a job item, when this desktop can see the file.</summary>
    public LibraryEntry? LocalEntry(RemoteJobItem item) =>
        item.RelativePath is { } rel ? Library.Find(LibraryIndex.IdOf(rel)) : null;

    /// <summary>
    /// A thumbnail or preview for a job item: from the output folder when it's reachable, otherwise
    /// through ComfyUI's /view (ffmpeg reads a video straight from the URL).
    /// </summary>
    public Task<string?> ThumbForItemAsync(RemoteJobItem item, int side, CancellationToken ct)
    {
        if (item.Output is not { } output) return Task.FromResult<string?>(null);
        var isVideo = item.MediaKind == MediaKinds.Video;
        if (LocalEntry(item) is { } local)
            return Thumbs.ForFileAsync(local.FullPath, local.PosterPath, isVideo, side, ct);
        SyncComfy();
        var identity = $"{Comfy.Url}|{output.Type}|{output.Subfolder}|{output.FileName}";
        return Thumbs.ForRemoteAsync(identity, isVideo, Comfy.ViewUrl(output),
            t => Comfy.DownloadAsync(output, t), side, ct);
    }

    /// <summary>Makes a finished item's thumbnail now, so the phone's tile fills the moment it asks.</summary>
    public void Warm(RemoteJobItem item)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ThumbForItemAsync(item, Thumbnailer.ThumbSide, CancellationToken.None);
                if (item.MediaKind == MediaKinds.Image) await ThumbForItemAsync(item, Thumbnailer.PreviewSide, CancellationToken.None);
            }
            catch (Exception) { /* made on demand instead */ }
        });
    }

    private int _warming;

    /// <summary>
    /// After a scan, makes the thumbnails of the newest screenful or three, one at a time, so opening
    /// the library shows pictures rather than placeholders. Already-cached ones cost a file check.
    /// </summary>
    private void WarmNewest(IReadOnlyList<LibraryEntry> entries)
    {
        if (Interlocked.Exchange(ref _warming, 1) == 1) return;
        var newest = entries.Take(90).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var e in newest)
                    await Thumbs.ForFileAsync(e.FullPath, e.PosterPath, e.Kind == MediaKinds.Video, Thumbnailer.ThumbSide, CancellationToken.None);
            }
            catch (Exception) { /* made on demand instead */ }
            finally { Interlocked.Exchange(ref _warming, 0); }
        });
    }

    // ── Screening ─────────────────────────────────────────────────────────────────────────────

    /// <summary>How many frames of a video the filter reads: one a second, and the phone's longest clip is 15 s.</summary>
    private const int VideoFrames = 16;

    /// <summary>
    /// Runs the content filter over a finished output. Null when it may be shown (or there is no
    /// filter); otherwise the output is deleted from the output folder, so neither the job nor the
    /// library can hand it out, and the reason is returned. A video that can't be read is not shown.
    /// </summary>
    public async Task<string?> ScreenAsync(ComfyOutput output, CancellationToken ct)
    {
        if (Options.Filter is not { } filter) return null;
        string? reason;
        if (output.IsVideo)
        {
            SyncComfy();
            var source = LocalPathOf(output) is { } local && File.Exists(local) ? local : Comfy.ViewUrl(output);
            var frames = await Thumbnailer.SampleFramesAsync(source, VideoFrames, 336, ct);
            reason = frames.Count == 0 ? "The video couldn't be checked, so it isn't shown." : null;
            foreach (var frame in frames)
            {
                if (reason != null) break;
                reason = await filter.CheckImageAsync(frame, ct);
            }
        }
        else
        {
            SyncComfy();
            reason = await filter.CheckImageAsync(await Comfy.DownloadAsync(output, ct), ct);
        }
        if (reason != null) Discard(output);
        return reason;
    }

    /// <summary>Screens a photo the phone sent. Null when it may be used.</summary>
    public Task<string?> ScreenUploadAsync(byte[] jpeg, CancellationToken ct) =>
        Options.Filter is { } filter ? filter.CheckImageAsync(jpeg, ct) : Task.FromResult<string?>(null);

    private string? LocalPathOf(ComfyOutput output)
    {
        if (output.Type != "output") return null;
        var root = OutputRoot();
        if (string.IsNullOrWhiteSpace(root)) return null;
        return Path.Combine(root, output.Subfolder ?? "", output.FileName);
    }

    /// <summary>Deletes a blocked output and the poster / audio copies VHS saves beside a video.</summary>
    private void Discard(ComfyOutput output)
    {
        if (LocalPathOf(output) is not { } path) return;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        foreach (var candidate in new[] { path, Path.Combine(dir, stem + ".png"), Path.Combine(dir, stem + "-audio" + Path.GetExtension(path)) })
        {
            try { if (File.Exists(candidate)) File.Delete(candidate); }
            catch (Exception ex) { Logger.LogWarning("Couldn't delete a blocked output {0}: {1}", candidate, ex.Message); }
        }
        Library.Invalidate();
    }

    public void Dispose() => Comfy.Dispose();
}
