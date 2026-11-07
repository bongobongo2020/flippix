using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlipPix.UI.Services;

/// <summary>
/// Copies videos and images that live on a network drive into a local cache before anything shows them.
///
/// <para>ComfyUI's output folder is often a mapped share (Z:\ over Tailscale, say) that can drop to tens of
/// KB/s while an upload or a render is using the same link. A MediaElement pointed straight at such a file
/// opens it with blocking reads, so a timeline of preview tiles left the window black and unresponsive.
/// Copying once, off the UI thread, and playing the local copy keeps the window responsive however slow
/// the share is.</para>
///
/// <para>Local paths are returned unchanged. A copy is keyed by path, size and modified time, so a file
/// that is re-rendered under the same name is copied again. Copies older than a week are deleted on first
/// use.</para>
/// </summary>
public static class LocalMediaCache
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlipPix", "MediaCache");

    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Copies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> NetworkRoots = new(StringComparer.OrdinalIgnoreCase);

    // Two at a time: enough to keep a slow link busy without one tile starving the rest.
    private static readonly SemaphoreSlim CopySlots = new(2);
    private static int _pruned;

    /// <summary>The local file to show for <paramref name="path"/>: the path itself when it is local, else a
    /// cached copy, made now if need be. Falls back to <paramref name="path"/> if the copy fails.</summary>
    public static Task<string> GetLocalAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !IsOnNetwork(path)) return Task.FromResult(path);
        return Task.Run(() => GetLocalCoreAsync(path));
    }

    /// <summary>Blocking form of <see cref="GetLocalAsync"/>, for code already on a worker thread.</summary>
    public static string GetLocal(string path) => GetLocalAsync(path).GetAwaiter().GetResult();

    private static async Task<string> GetLocalCoreAsync(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return path;

            var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            var copy = Copies.GetOrAdd(key, k => new Lazy<Task<string>>(() => CopyAsync(info, k)));
            var local = await copy.Value.ConfigureAwait(false);
            if (local == path) Copies.TryRemove(key, out _); // failed: let a later request try again
            return local;
        }
        catch
        {
            return path;
        }
    }

    private static async Task<string> CopyAsync(FileInfo source, string key)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            PruneOnce();

            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            var local = Path.Combine(CacheDir, $"{Path.GetFileNameWithoutExtension(source.Name)}_{hash}{source.Extension}");
            if (File.Exists(local) && new FileInfo(local).Length == source.Length) return local;

            await CopySlots.WaitAsync().ConfigureAwait(false);
            try
            {
                var partial = local + ".part";
                await using (var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                                        1 << 16, useAsync: true))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    await input.CopyToAsync(output).ConfigureAwait(false);
                }
                File.Move(partial, local, overwrite: true);
                return local;
            }
            finally
            {
                CopySlots.Release();
            }
        }
        catch
        {
            return source.FullName;
        }
    }

    private static bool IsOnNetwork(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            return NetworkRoots.GetOrAdd(root, r => new DriveInfo(r).DriveType == DriveType.Network);
        }
        catch
        {
            return false;
        }
    }

    private static void PruneOnce()
    {
        if (Interlocked.Exchange(ref _pruned, 1) == 1) return;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var file in Directory.EnumerateFiles(CacheDir))
            {
                try
                {
                    if (File.GetLastAccessTimeUtc(file) < cutoff && File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }
}
