using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Linux.Models
{
    public class StoryVideoQueueItem : BaseQueueItem
    {
        public int Index { get; set; }
        public string Prompt { get; set; } = string.Empty;
        public string InputImagePath { get; set; } = string.Empty;

        private BitmapImage? _videoThumbnailImage;

        [JsonIgnore]
        public BitmapImage? VideoThumbnailImage
        {
            get => _videoThumbnailImage;
            private set
            {
                _videoThumbnailImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasVideoThumbnail));
            }
        }

        [JsonIgnore]
        public bool HasVideoThumbnail => _videoThumbnailImage != null;

        public string? OutputVideoPath
        {
            get => OutputImagePath;
            set
            {
                OutputImagePath = value;
                // Loading an existing thumbnail probes and reads the output folder, which may live on
                // a slow/mapped network drive (e.g. Z:\). Bulk queue loads set this for every item, so
                // a synchronous File.Exists/read per item froze the Video Generator window for ~12s.
                // Always do it on a background thread; the image assignment marshals back to the UI thread.
                System.Threading.Tasks.Task.Run(TryLoadExistingThumbnail);
            }
        }

        public void LoadVideoThumbnail(string thumbPath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(thumbPath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                SetThumbnail(bitmap);
            }
            catch
            {
                SetThumbnail(null);
            }
        }

        // Raises PropertyChanged on the UI thread so bindings update safely even when the
        // thumbnail is loaded from a background thread.
        private void SetThumbnail(BitmapImage? bitmap)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.Invoke(() => VideoThumbnailImage = bitmap);
            else
                VideoThumbnailImage = bitmap;
        }

        private void TryLoadExistingThumbnail()
        {
            if (string.IsNullOrEmpty(OutputImagePath)) return;
            var thumbPath = Path.ChangeExtension(OutputImagePath, null) + "_thumb.jpg";
            if (File.Exists(thumbPath))
                LoadVideoThumbnail(thumbPath);
        }
    }
}
