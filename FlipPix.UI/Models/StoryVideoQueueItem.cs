using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Models
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
                if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
                    System.Windows.Application.Current.Dispatcher.Invoke(TryLoadExistingThumbnail);
                else
                    TryLoadExistingThumbnail();
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
                VideoThumbnailImage = bitmap;
            }
            catch
            {
                VideoThumbnailImage = null;
            }
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
