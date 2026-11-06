using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// A reference image slot (Picture 1-4) for the H3 Video Editor.
    /// Supports multiple images per slot with composition options.
    /// </summary>
    public partial class H3ReferenceSlot : ObservableObject
    {
        public H3ReferenceSlot(int slotNumber)
        {
            SlotNumber = slotNumber;
        }

        /// <summary>The slot number (1-4).</summary>
        public int SlotNumber { get; }

        /// <summary>Display label for the slot.</summary>
        public string Label => $"Picture {SlotNumber}";

        /// <summary>Images loaded in this slot.</summary>
        public ObservableCollection<H3ReferenceImage> Images { get; } = new();

        /// <summary>True if the slot has at least one image.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasImages))]
        [NotifyPropertyChangedFor(nameof(ImageCount))]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        private bool _isLoaded;

        public bool HasImages => Images.Count > 0;
        public int ImageCount => Images.Count;

        public string StatusText => IsLoaded
            ? $"{ImageCount} image{(ImageCount > 1 ? "s" : "")}"
            : "empty";

        /// <summary>The preview thumbnail for the first image.</summary>
        [ObservableProperty]
        private BitmapImage? _previewImage;

        /// <summary>Max megapixels for the composed image.</summary>
        [ObservableProperty]
        private double _maxMegapixels = 2.0;

        /// <summary>Gap between composed images in pixels.</summary>
        [ObservableProperty]
        private int _gap;

        /// <summary>Background color for composition.</summary>
        [ObservableProperty]
        private string _background = "black";

        /// <summary>Adds an image to this slot.</summary>
        public void AddImage(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            var image = new H3ReferenceImage
            {
                Path = path,
                FileName = Path.GetFileName(path)
            };
            Images.Add(image);
            IsLoaded = true;

            // Update preview from first image
            if (Images.Count == 1)
            {
                LoadPreview(path);
            }

            OnPropertyChanged(nameof(HasImages));
            OnPropertyChanged(nameof(ImageCount));
            OnPropertyChanged(nameof(StatusText));
        }

        /// <summary>Puts an image in front of the others — the first image is the one uploaded for this
        /// picture — and shows it as the preview.</summary>
        public void InsertFirst(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            Images.Insert(0, new H3ReferenceImage { Path = path, FileName = Path.GetFileName(path) });
            IsLoaded = true;
            LoadPreview(path);
            OnPropertyChanged(nameof(HasImages));
            OnPropertyChanged(nameof(ImageCount));
            OnPropertyChanged(nameof(StatusText));
        }

        /// <summary>Clears all images from this slot.</summary>
        public void Clear()
        {
            Images.Clear();
            IsLoaded = false;
            _previewVersion++; // drop a decode still in flight
            PreviewImage = null;
            OnPropertyChanged(nameof(HasImages));
            OnPropertyChanged(nameof(ImageCount));
            OnPropertyChanged(nameof(StatusText));
        }

        private int _previewVersion;

        /// <summary>Decoded on a worker thread; a newer image added meanwhile wins.</summary>
        private void LoadPreview(string path)
        {
            var version = ++_previewVersion;
            FlipPix.UI.Services.AsyncThumbnail.Load(path, 120, 0, bitmap =>
            {
                if (version == _previewVersion && IsLoaded) PreviewImage = bitmap;
            });
        }
    }

    /// <summary>
    /// A single image within a reference slot.
    /// </summary>
    public class H3ReferenceImage
    {
        public string Path { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
    }
}
