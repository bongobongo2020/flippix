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

    public RemoteEngine(Func<ComfyUISettings> settings, IAppLogger? logger, string dataDir)
    {
        _settings = settings;
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

    public void Dispose() => Comfy.Dispose();
}
