using System;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// One image "shot" on the LTX Director timeline. Each shot is a keyframe with
    /// its own prompt and duration; shots are laid out left→right as video time.
    /// </summary>
    public partial class LtxDirectorTimelineItem : ObservableObject
    {
        [ObservableProperty] private string _imagePath = string.Empty;

        [ObservableProperty] private string _prompt = string.Empty;

        /// <summary>Length of this shot in seconds. Drives segment frame length.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DurationLabel))]
        private double _durationSeconds = 3.0;

        /// <summary>1-based position shown on the card; set by the VM when the list changes.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IndexLabel))]
        private int _index;

        [ObservableProperty] private bool _isSelected;

        private BitmapImage? _thumbnail;
        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            private set => SetProperty(ref _thumbnail, value);
        }

        public string FileName => string.IsNullOrEmpty(ImagePath) ? string.Empty : Path.GetFileName(ImagePath);
        public string IndexLabel => $"#{Index}";
        public string DurationLabel => $"{DurationSeconds:0.0}s";

        public LtxDirectorTimelineItem() { }

        public LtxDirectorTimelineItem(string imagePath)
        {
            _imagePath = imagePath;
            LoadThumbnail();
        }

        private void LoadThumbnail()
        {
            if (string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath))
            {
                Thumbnail = null;
                return;
            }
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(ImagePath, UriKind.Absolute);
                bmp.DecodePixelHeight = 160;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                Thumbnail = bmp;
            }
            catch
            {
                Thumbnail = null;
            }
        }
    }
}
