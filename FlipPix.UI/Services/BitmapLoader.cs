using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Services;

/// <summary>
/// Loads an image file into a frozen bitmap that holds no file lock (OnLoad) and never serves
/// a stale copy of a file overwritten at the same path (IgnoreImageCache).
/// </summary>
public static class BitmapLoader
{
    /// <summary>Widest a result preview pane is decoded at. The pane is a screen-sized view; decoding
    /// a 4K PNG at full size only costs time and memory. Opening the image still uses the file.</summary>
    public const int PreviewMaxWidth = 1280;

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

    /// <summary>
    /// Decodes <paramref name="path"/> for a preview pane on a worker thread, no wider than
    /// <see cref="PreviewMaxWidth"/>, so the UI thread never stalls on a big PNG. Also returns the
    /// file's real size and pixel dimensions (read from its header), since the bitmap's are scaled.
    /// </summary>
    public static Task<(BitmapImage Bitmap, long Bytes, int Width, int Height)> LoadPreviewAsync(string path) =>
        Task.Run(() =>
        {
            var bytes = new FileInfo(path).Length;
            var (w, h) = TryReadSize(path);
            var bmp = Load(path, w > PreviewMaxWidth ? PreviewMaxWidth : 0);
            if (w <= 0) { w = bmp.PixelWidth; h = bmp.PixelHeight; }
            return (bmp, bytes, w, h);
        });

    /// <summary>Pixel size from a PNG or JPEG header without decoding; (0, 0) when unknown.</summary>
    public static (int Width, int Height) TryReadSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[24];
            if (fs.Read(head, 0, 24) < 24) return (0, 0);

            // PNG: 8-byte signature, then the IHDR chunk holding big-endian width and height.
            if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
                return (BigEndian(head, 16), BigEndian(head, 20));

            // JPEG: walk the segments to the first start-of-frame marker.
            if (head[0] == 0xFF && head[1] == 0xD8)
            {
                fs.Position = 2;
                var seg = new byte[9];
                while (fs.Read(seg, 0, 4) == 4)
                {
                    if (seg[0] != 0xFF) return (0, 0);
                    var marker = seg[1];
                    var len = (seg[2] << 8) | seg[3];
                    bool isSof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                    if (isSof)
                    {
                        if (fs.Read(seg, 0, 5) < 5) return (0, 0);
                        return ((seg[3] << 8) | seg[4], (seg[1] << 8) | seg[2]);
                    }
                    if (len < 2) return (0, 0);
                    fs.Position += len - 2;
                }
            }
        }
        catch
        {
            // Unknown size just means a full-size decode.
        }
        return (0, 0);

        static int BigEndian(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    }
}
