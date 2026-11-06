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
    public enum QueueItemStatus
    {
        Pending,
        Processing,
        Completed,
        Failed
    }

    /// <summary>
    /// Base class for all queue item models with shared properties and functionality
    /// </summary>
    public abstract class BaseQueueItem : INotifyPropertyChanged
    {
        private string _status = "Pending";
        private string? _errorMessage;
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
        /// Error message if processing failed. Notifies, so a queue row that shows the reason updates
        /// when the item flips to Failed instead of staying blank until the list is rebuilt.
        /// </summary>
        public string? ErrorMessage
        {
            get => _errorMessage;
            set { if (_errorMessage != value) { _errorMessage = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Number of automatic retry attempts made (used by crash-detection logic)
        /// </summary>
        public int RetryCount { get; set; } = 0;

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
                    OnPropertyChanged(nameof(ItemStatus));
                }
            }
        }

        /// <summary>
        /// Map string Status to/from QueueItemStatus enum
        /// </summary>
        [JsonIgnore]
        public QueueItemStatus ItemStatus
        {
            get => Status switch
            {
                "Pending" => QueueItemStatus.Pending,
                "Processing" => QueueItemStatus.Processing,
                "Completed" => QueueItemStatus.Completed,
                "Failed" => QueueItemStatus.Failed,
                _ => QueueItemStatus.Pending
            };
            set => Status = value switch
            {
                QueueItemStatus.Pending => "Pending",
                QueueItemStatus.Processing => "Processing",
                QueueItemStatus.Completed => "Completed",
                QueueItemStatus.Failed => "Failed",
                _ => "Pending"
            };
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
                    OnPropertyChanged(nameof(HasOutputImage));
                    // Thumbnail and command updates must happen on the UI thread
                    if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            LoadThumbnail();
                            (OpenImageCommand as RelayCommand)?.NotifyCanExecuteChanged();
                        });
                    }
                    else
                    {
                        LoadThumbnail();
                        (OpenImageCommand as RelayCommand)?.NotifyCanExecuteChanged();
                    }
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
                OnPropertyChanged(nameof(OutputImageThumbnail));
            }
        }

        private BitmapImage? _storyboardThumbnailImage;

        /// <summary>
        /// Large thumbnail image for the storyboard view (220px height)
        /// </summary>
        [JsonIgnore]
        public BitmapImage? StoryboardThumbnailImage
        {
            get => _storyboardThumbnailImage;
            private set
            {
                _storyboardThumbnailImage = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Alias for OutputImageThumbnail (for XAML binding compatibility)
        /// </summary>
        [JsonIgnore]
        public BitmapImage? OutputImageThumbnail => ThumbnailImage;

        /// <summary>
        /// Whether an output image exists for this item
        /// </summary>
        [JsonIgnore]
        public bool HasOutputImage => !string.IsNullOrEmpty(OutputImagePath);

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
            "Analyzing" => "Writing prompt ✍️",
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
            "Analyzing" => "#8E44AD",
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
        protected void LoadThumbnail() => LoadThumbnailFrom(_outputImagePath);

        /// <summary>
        /// Shows <paramref name="previewPath"/> as the thumbnail before the output itself exists
        /// (e.g. a quick preview while the full image is still downloading). Setting
        /// <see cref="OutputImagePath"/> later replaces it.
        /// </summary>
        public void ShowPreviewThumbnail(string previewPath)
        {
            if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
                System.Windows.Application.Current.Dispatcher.Invoke(() => LoadThumbnailFrom(previewPath));
            else
                LoadThumbnailFrom(previewPath);
        }

        // Bumped on every thumbnail request so a slow decode that finishes late (say the preview's,
        // after the full image's) never overwrites the newer thumbnail.
        private int _thumbnailVersion;

        private void LoadThumbnailFrom(string? imagePath)
        {
            var version = System.Threading.Interlocked.Increment(ref _thumbnailVersion);

            if (string.IsNullOrEmpty(imagePath))
            {
                ThumbnailImage = null;
                StoryboardThumbnailImage = null;
                return;
            }

            // Decoding a multi-megapixel PNG takes a noticeable slice of a second even when scaled
            // down, so it runs off the UI thread; with a queue finishing image after image, doing it
            // on the UI thread made the whole window stutter. Frozen bitmaps can cross threads.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                if (!File.Exists(imagePath)) return (Small: (BitmapImage?)null, Large: (BitmapImage?)null);
                return (Small: DecodeThumbnail(imagePath, 60), Large: DecodeThumbnail(imagePath, 220));
            }).ContinueWith(t =>
            {
                if (t.IsFaulted) _ = t.Exception; // observed; DecodeThumbnail already swallows errors
                var (small, large) = t.IsCompletedSuccessfully ? t.Result : (null, null);
                void Apply()
                {
                    if (version != _thumbnailVersion) return;
                    ThumbnailImage = small;
                    StoryboardThumbnailImage = large;
                }

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess()) Apply();
                else dispatcher.BeginInvoke(new Action(Apply));
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private static BitmapImage? DecodeThumbnail(string imagePath, int height)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.DecodePixelHeight = height;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
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
                    FlipPix.UI.Services.ImageOpener.Open(OutputImagePath);
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
