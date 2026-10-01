using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace FlipPix.Remote.Library;

/// <summary>
/// Small JPEGs for the phone: a grid thumbnail (<see cref="ThumbSide"/>) and a screen-sized preview
/// (<see cref="PreviewSide"/>). A 2560 px PNG is 8 MB over Wi-Fi and 26 MB of bitmap on the phone; the
/// preview is a few hundred KB and looks the same on a phone screen. The original stays one tap away.
///
/// <para>Results are cached on disk under the desktop's local app data, keyed by the source and its
/// size and time, so an edited or replaced file gets a fresh one and nothing is ever made twice.</para>
/// </summary>
public sealed class Thumbnailer
{
    public const int ThumbSide = 360;
    public const int PreviewSide = 1600;

    private readonly string _cacheDir;
    private readonly SemaphoreSlim _gate = new(3, 3);
    private static readonly object FfmpegLock = new();
    private static string? _ffmpeg;
    private static bool _ffmpegResolved;

    public Thumbnailer(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    /// <summary>
    /// A thumbnail or preview of a file on disk. Videos use their poster PNG when there is one, and
    /// otherwise a frame half a second in, pulled by ffmpeg. Null when neither is possible.
    /// </summary>
    public async Task<string?> ForFileAsync(string path, string? posterPath, bool isVideo, int side, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        var key = Key($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{side}");
        var cached = Path.Combine(_cacheDir, key + ".jpg");
        if (File.Exists(cached)) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(cached)) return cached;
            if (!isVideo) return await FromImageFileAsync(path, side, cached, ct);
            if (posterPath != null && File.Exists(posterPath)) return await FromImageFileAsync(posterPath, side, cached, ct);
            return await FromVideoAsync(path, side, cached, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[FlipPix Remote] No thumbnail for {path}: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A thumbnail of something only reachable over HTTP (ComfyUI's /view), identified by
    /// <paramref name="identity"/>. Images are downloaded by <paramref name="download"/>; videos are
    /// read by ffmpeg straight from <paramref name="url"/>.
    /// </summary>
    public async Task<string?> ForRemoteAsync(string identity, bool isVideo, string url,
        Func<CancellationToken, Task<byte[]>> download, int side, CancellationToken ct)
    {
        var key = Key($"remote|{identity}|{side}");
        var cached = Path.Combine(_cacheDir, key + ".jpg");
        if (File.Exists(cached)) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(cached)) return cached;
            if (isVideo) return await FromVideoAsync(url, side, cached, ct);
            var bytes = await download(ct);
            using var image = Image.Load(new DecoderOptions { TargetSize = new Size(side, side) }, bytes);
            return await SaveAsync(image, side, cached, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[FlipPix Remote] No thumbnail for {identity}: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A thumbnail straight from bytes already in hand (an upload).</summary>
    public static byte[] FromBytes(byte[] bytes, int side)
    {
        using var image = Image.Load(new DecoderOptions { TargetSize = new Size(side, side) }, bytes);
        image.Mutate(x => x.AutoOrient().Resize(new ResizeOptions { Size = new Size(side, side), Mode = ResizeMode.Max }));
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = 82 });
        return ms.ToArray();
    }

    private static async Task<string> FromImageFileAsync(string path, int side, string cached, CancellationToken ct)
    {
        // TargetSize lets the JPEG decoder scale while decoding; PNGs are decoded whole and then shrunk.
        using var image = await Image.LoadAsync(new DecoderOptions { TargetSize = new Size(side, side) }, path, ct);
        return await SaveAsync(image, side, cached, ct);
    }

    private static async Task<string> SaveAsync(Image image, int side, string cached, CancellationToken ct)
    {
        image.Mutate(x => x.AutoOrient());
        if (image.Width > side || image.Height > side)
            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(side, side), Mode = ResizeMode.Max }));
        image.Metadata.ExifProfile = null;
        var tmp = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await image.SaveAsJpegAsync(tmp, new JpegEncoder { Quality = side > ThumbSide ? 88 : 80 }, ct);
        File.Move(tmp, cached, overwrite: true);
        return cached;
    }

    private static async Task<string?> FromVideoAsync(string input, int side, string cached, CancellationToken ct)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return null;
        var tmp = cached + "." + Guid.NewGuid().ToString("N") + ".jpg";
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y", "-ss", "0.5", "-i", input, "-frames:v", "1",
                     "-vf", $"scale='min({side},iw)':'min({side},ih)':force_original_aspect_ratio=decrease",
                     "-q:v", side > ThumbSide ? "3" : "5", tmp,
                 })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p == null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var drainErr = p.StandardError.ReadToEndAsync(timeout.Token);
            var drainOut = p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(drainErr, drainOut);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { File.Delete(tmp); } catch (IOException) { }
            if (ct.IsCancellationRequested) throw;
            return null;
        }

        if (p.ExitCode != 0 || !File.Exists(tmp))
        {
            try { File.Delete(tmp); } catch (IOException) { }
            return null;
        }
        File.Move(tmp, cached, overwrite: true);
        return cached;
    }

    /// <summary>
    /// One frame a second of a video (a file or a URL ffmpeg can read), at most <paramref name="max"/>,
    /// each a small JPEG for a content classifier. Empty when ffmpeg is missing or reads nothing.
    /// </summary>
    public static async Task<IReadOnlyList<byte[]>> SampleFramesAsync(string input, int max, int side, CancellationToken ct)
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return Array.Empty<byte[]>();
        var dir = Path.Combine(Path.GetTempPath(), "flippix-frames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-i", input,
                         "-vf", $"fps=1,scale={side}:{side}:force_original_aspect_ratio=decrease",
                         "-frames:v", max.ToString(), "-q:v", "4", Path.Combine(dir, "f%03d.jpg"),
                     })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return Array.Empty<byte[]>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                var drainErr = p.StandardError.ReadToEndAsync(timeout.Token);
                var drainOut = p.StandardOutput.ReadToEndAsync(timeout.Token);
                await p.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(drainErr, drainOut);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                return Array.Empty<byte[]>();
            }

            var frames = new List<byte[]>();
            foreach (var f in Directory.EnumerateFiles(dir, "f*.jpg").OrderBy(f => f, StringComparer.Ordinal))
                frames.Add(await File.ReadAllBytesAsync(f, ct));
            return frames;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// ffmpeg on PATH, found once per run. Only local fixed-drive folders are probed: on this machine a
    /// PATH entry on a disconnected mapped drive costs a 12 s SMB timeout per File.Exists.
    /// </summary>
    public static string? FindFfmpeg()
    {
        lock (FfmpegLock)
        {
            if (_ffmpegResolved) return _ffmpeg;
            _ffmpegResolved = true;
            var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            var dirs = new List<string> { AppContext.BaseDirectory };
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (!OperatingSystem.IsWindows()) dirs.AddRange(new[] { "/usr/bin", "/usr/local/bin" });

            foreach (var dir in dirs)
            {
                try
                {
                    if (!IsLocalFixed(dir)) continue;
                    var candidate = Path.Combine(dir, exe);
                    if (File.Exists(candidate)) return _ffmpeg = candidate;
                }
                catch (Exception) { /* a malformed PATH entry is skipped, not fatal */ }
            }
            return null;
        }
    }

    private static bool IsLocalFixed(string dir)
    {
        if (!OperatingSystem.IsWindows()) return true;
        if (dir.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var root = Path.GetPathRoot(dir);
        if (string.IsNullOrEmpty(root)) return false;
        // DriveType is answered without touching the drive; IsReady on a dead share would hang.
        return new DriveInfo(root).DriveType == DriveType.Fixed;
    }

    private static string Key(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)));
}
