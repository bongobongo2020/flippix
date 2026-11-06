using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Services;

/// <summary>
/// Decodes small preview bitmaps on a worker thread and hands them back on the UI thread.
///
/// <para>Even with DecodePixelWidth/Height set, WPF reads and inflates the whole file to scale it, so a
/// multi-megapixel PNG costs a visible slice of a second, and a list of queue rows or reference slots
/// doing that on the UI thread makes a page feel stuck. Frozen bitmaps can cross threads, so the decode
/// happens off it and only the assignment runs on it.</para>
/// </summary>
public static class AsyncThumbnail
{
    /// <summary>Decodes <paramref name="path"/> scaled to the given width or height (0 = unconstrained),
    /// frozen and holding no file lock. Null when the file is missing or unreadable. Safe on any thread.</summary>
    public static BitmapImage? Decode(string? path, int width = 0, int height = 0)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (width > 0) bmp.DecodePixelWidth = width;
            else if (height > 0) bmp.DecodePixelHeight = height;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes on a worker thread, then calls <paramref name="apply"/> on the UI thread (or inline when
    /// there is no UI, as in tests). Callers that may issue overlapping loads guard <paramref name="apply"/>
    /// with their own version check.
    /// </summary>
    public static void Load(string? path, int width, int height, Action<BitmapImage?> apply)
    {
        if (string.IsNullOrEmpty(path))
        {
            apply(null);
            return;
        }

        Load(() => path, width, height, apply);
    }

    /// <summary>As <see cref="Load(string?, int, int, Action{BitmapImage?})"/>, with the file picked on the
    /// worker thread too — for callers that probe several candidates with File.Exists, which on a mapped
    /// or network drive can itself stall.</summary>
    public static void Load(Func<string?> resolvePath, int width, int height, Action<BitmapImage?> apply)
    {
        Task.Run(() => Decode(resolvePath(), width, height)).ContinueWith(t =>
        {
            var bitmap = t.IsCompletedSuccessfully ? t.Result : null;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) apply(bitmap);
            else dispatcher.BeginInvoke(new Action(() => apply(bitmap)));
        }, TaskScheduler.Default);
    }
}
