using System.IO;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// One generated image shown as a thumbnail in the Ideogram "Generated Images"
    /// gallery. <see cref="ImagePath"/> is the FlipPix-saved copy on disk; the
    /// gallery's per-item delete removes that file, while "Clear All" only drops
    /// the items from view.
    /// </summary>
    public class GeneratedImageItem
    {
        public string ImagePath { get; init; } = string.Empty;

        /// <summary>
        /// Full path to the original file in the local ComfyUI output folder, when known.
        /// Null for images downloaded from a remote ComfyUI (no local filesystem path) or
        /// for items rehydrated from disk on startup. The gallery's delete removes this too.
        /// </summary>
        public string? ComfyUISourcePath { get; init; }

        public BitmapImage? Thumbnail { get; init; }
        public string FileName => string.IsNullOrEmpty(ImagePath) ? string.Empty : Path.GetFileName(ImagePath);
    }
}
