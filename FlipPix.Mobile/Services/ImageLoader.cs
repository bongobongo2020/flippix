using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace FlipPix.Mobile.Services;

/// <summary>
/// Pictures from the computer, decoded for the screen and kept twice: the JPEG bytes on disk (the
/// app's cache folder, which Android may clear when space runs low) and the newest bitmaps in memory.
/// A thumbnail or preview never changes under its URL, so a cached one is never stale.
///
/// <para>Memory is the limit on a phone: a 360 px tile is half a megabyte decoded, so only the most
/// recently shown <see cref="MemoryLimit"/> stay alive; scrolling back to an older one decodes it again
/// from disk, which is quick.</para>
/// </summary>
public static class ImageLoader
{
    private const int MemoryLimit = 160;
    private static readonly SemaphoreSlim Gate = new(4, 4);
    private static readonly object Lock = new();
    private static readonly LinkedList<string> Order = new();
    private static readonly Dictionary<string, (Bitmap Bitmap, LinkedListNode<string> Node)> Memory = new();
    private static readonly Dictionary<string, Task<Bitmap?>> InFlight = new();
    private static readonly string DiskDir = Path.Combine(Path.GetTempPath(), "flippix-img");

    static ImageLoader()
    {
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(DiskDir);
                foreach (var f in new DirectoryInfo(DiskDir).EnumerateFiles())
                    if (DateTime.UtcNow - f.LastAccessTimeUtc > TimeSpan.FromDays(21)) f.Delete();
            }
            catch (Exception) { /* housekeeping only */ }
        });
    }

    /// <summary>The bitmap for a server-relative URL, decoded to <paramref name="width"/>; null when it can't be had.</summary>
    public static Task<Bitmap?> LoadAsync(string? relative, int width, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(relative) || !AppServices.Remote.IsConfigured) return Task.FromResult<Bitmap?>(null);
        var key = AppServices.Remote.BaseUrl + relative + "|" + width;
        lock (Lock)
        {
            if (Memory.TryGetValue(key, out var hit))
            {
                Order.Remove(hit.Node);
                Order.AddFirst(hit.Node);
                return Task.FromResult<Bitmap?>(hit.Bitmap);
            }
            if (InFlight.TryGetValue(key, out var pending)) return pending;
            var task = FetchAsync(key, relative, width, ct);
            InFlight[key] = task;
            return task;
        }
    }

    private static async Task<Bitmap?> FetchAsync(string key, string relative, int width, CancellationToken ct)
    {
        try
        {
            await Gate.WaitAsync(ct);
            try
            {
                var file = Path.Combine(DiskDir, Hash(AppServices.Remote.BaseUrl + relative) + ".jpg");
                byte[] bytes;
                if (File.Exists(file))
                {
                    bytes = await File.ReadAllBytesAsync(file, ct);
                }
                else
                {
                    bytes = await AppServices.Remote.GetBytesAsync(relative, ct);
                    try
                    {
                        Directory.CreateDirectory(DiskDir);
                        await File.WriteAllBytesAsync(file, bytes, CancellationToken.None);
                    }
                    catch (IOException) { /* a full disk only costs the next download */ }
                }

                var bitmap = await Task.Run(() =>
                {
                    using var ms = new MemoryStream(bytes);
                    return Bitmap.DecodeToWidth(ms, width, BitmapInterpolationMode.MediumQuality);
                }, ct);

                lock (Lock)
                {
                    var node = Order.AddFirst(key);
                    Memory[key] = (bitmap, node);
                    // The oldest are only dropped, never disposed: one may still be on screen, and
                    // the garbage collector frees it once nothing shows it.
                    while (Order.Count > MemoryLimit)
                    {
                        var last = Order.Last!;
                        Memory.Remove(last.Value);
                        Order.RemoveLast();
                    }
                }
                return bitmap;
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (Exception)
        {
            return null; // a tile without a picture shows its placeholder; nothing to report
        }
        finally
        {
            lock (Lock) InFlight.Remove(key);
        }
    }

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)));
}
