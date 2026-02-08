using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// Base class for all queue item models with shared properties and functionality
    /// </summary>
    public abstract class BaseQueueItem : INotifyPropertyChanged
    {
        private string _status = "Pending";
        private double _progress = 0;
        private string? _outputImagePath;
        private BitmapImage? _thumbnailImage;

        /// <summary>
        /// Unique identifier for this queue item
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// When this item was created
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// When processing started on this item
        /// </summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>
        /// When processing completed for this item
        /// </summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// Error message if processing failed
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Current status of the item (Pending, Processing, Completed, Failed, etc.)
        /// </summary>
        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusDisplay));
                    OnPropertyChanged(nameof(StatusColor));
                }
            }
        }

        /// <summary>
        /// Progress value (0-100)
        /// </summary>
        public double Progress
        {
            get => _progress;
            set
            {
                if (_progress != value)
                {
                    _progress = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Path to the output image (if any)
        /// </summary>
        public string? OutputImagePath
        {
            get => _outputImagePath;
            set
            {
                if (_outputImagePath != value)
                {
                    _outputImagePath = value;
                    OnPropertyChanged();
                    LoadThumbnail();
                    // Update command can execute state
                    (OpenImageCommand as RelayCommand)?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>
        /// Thumbnail image for display in the queue
        /// </summary>
        [JsonIgnore]
        public BitmapImage? ThumbnailImage
        {
            get => _thumbnailImage;
            private set
            {
                _thumbnailImage = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Command to open the output image
        /// </summary>
        [JsonIgnore]
        public ICommand OpenImageCommand { get; }

        /// <summary>
        /// Display-friendly status with emoji
        /// </summary>
        [JsonIgnore]
        public string StatusDisplay => Status switch
        {
            "Pending" => "Pending ⏳",
            "Queued" => "Queued ⏳",
            "Processing" => "Processing ⚙️",
            "Completed" => "Completed ✅",
            "Failed" => "Failed ❌",
            "Cancelled" => "Cancelled 🚫",
            _ => Status
        };

        /// <summary>
        /// Color for status display
        /// </summary>
        [JsonIgnore]
        public string StatusColor => Status switch
        {
            "Pending" => "#007BFF",
            "Queued" => "#007BFF",
            "Processing" => "#FF6B35",
            "Completed" => "#28A745",
            "Failed" => "#DC3545",
            "Cancelled" => "#6C757D",
            _ => "#6C757D"
        };

        protected BaseQueueItem()
        {
            OpenImageCommand = new RelayCommand(OpenImage, CanOpenImage);
        }

        /// <summary>
        /// Load thumbnail from output image path
        /// </summary>
        protected void LoadThumbnail()
        {
            if (string.IsNullOrEmpty(_outputImagePath) || !File.Exists(_outputImagePath))
            {
                ThumbnailImage = null;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(_outputImagePath, UriKind.Absolute);
                bitmap.DecodePixelHeight = 60;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                ThumbnailImage = bitmap;
            }
            catch
            {
                ThumbnailImage = null;
            }
        }

        /// <summary>
        /// Open the output image with default system viewer
        /// </summary>
        protected void OpenImage()
        {
            try
            {
                if (!string.IsNullOrEmpty(OutputImagePath) && File.Exists(OutputImagePath))
                {
                    Process.Start(new ProcessStartInfo(OutputImagePath) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to open image: {ex.Message}", "Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Check if image can be opened
        /// </summary>
        protected bool CanOpenImage() => !string.IsNullOrEmpty(OutputImagePath) && File.Exists(OutputImagePath);

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
