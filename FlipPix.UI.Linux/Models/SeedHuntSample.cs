using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// One of the four low-res "seed hunt" sample previews produced by a Stage-1 batch.
    /// Slot is 1-based and maps to the workflow's ImpactSwitch select index (node 5144 / 5152).
    /// </summary>
    public partial class SeedHuntSample : ObservableObject
    {
        public SeedHuntSample(int slot)
        {
            Slot = slot;
        }

        /// <summary>1-based slot index (1-4), used as the Stage-2 select value.</summary>
        public int Slot { get; }

        public string Label => $"Sample {Slot}";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasVideo))]
        private string? _videoPath;

        /// <summary>Local file path/URI of the sample video (played in the shared preview player).</summary>
        [ObservableProperty]
        private string? _videoFileUri;

        /// <summary>First-frame thumbnail shown in the sample grid (4 MediaElements can't render at once).</summary>
        [ObservableProperty]
        private BitmapImage? _thumbnailImage;

        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        private string _status = string.Empty;

        public bool HasVideo => !string.IsNullOrEmpty(VideoPath) && System.IO.File.Exists(VideoPath);

        /// <summary>Clears the preview for a fresh batch.</summary>
        public void Reset()
        {
            VideoPath = null;
            VideoFileUri = null;
            ThumbnailImage = null;
            IsSelected = false;
            Status = string.Empty;
        }
    }
}
