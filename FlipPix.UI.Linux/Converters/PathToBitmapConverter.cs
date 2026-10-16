using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace FlipPix.UI.Linux.Converters;

/// <summary>
/// Image.Source for whatever the shared view models hand over: a WPF BitmapSource (the compat
/// type, already decoded), a file path or a file Uri — WPF converts the last two implicitly.
/// Paths are cached by modification time so a gallery re-binding does not re-decode its files.
/// </summary>
public class PathToBitmapConverter : IValueConverter
{
    public static readonly PathToBitmapConverter Instance = new();

    private const int CacheSize = 64;
    private static readonly Dictionary<string, (DateTime Stamp, Bitmap Bitmap)> Cache = new();
    private static readonly LinkedList<string> Order = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        switch (value)
        {
            case System.Windows.Media.Imaging.BitmapSource source:
                return source.AvaloniaBitmap;
            case Bitmap bitmap:
                return bitmap;
            case Uri { IsFile: true } uri:
                return Load(uri.LocalPath);
            case string path when !string.IsNullOrEmpty(path):
                return Load(path.StartsWith("file://") ? new Uri(path).LocalPath : path);
            default:
                return null;
        }
    }

    private static Bitmap? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Bitmap;
            }

            var bitmap = new Bitmap(path);
            lock (Cache)
            {
                Cache[path] = (stamp, bitmap);
                Order.Remove(path);
                Order.AddFirst(path);
                while (Order.Count > CacheSize)
                {
                    var last = Order.Last!.Value;
                    Order.RemoveLast();
                    Cache.Remove(last);
                }
            }
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
