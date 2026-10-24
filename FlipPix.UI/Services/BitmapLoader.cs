using System;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Services;

/// <summary>
/// Loads an image file into a frozen bitmap that holds no file lock (OnLoad) and never serves
/// a stale copy of a file overwritten at the same path (IgnoreImageCache).
/// </summary>
public static class BitmapLoader
{
    /// <summary>decodeWidth &gt; 0 decodes straight to that width (thumbnails); PixelWidth then reports the scaled size.</summary>
    public static BitmapImage Load(string path, int decodeWidth = 0)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.DecodePixelWidth = decodeWidth;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
