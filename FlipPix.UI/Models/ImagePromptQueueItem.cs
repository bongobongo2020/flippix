using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// Model for image generator prompt queue items
    /// </summary>
    public class ImagePromptQueueItem : INotifyPropertyChanged
    {
        private string _status = "Pending";
        private double _progress = 0;
        private string? _outputImagePath;
        private BitmapImage? _thumbnailImage;

        public ImagePromptQueueItem()
        {
            OpenImageCommand = new RelayCommand(OpenImage, () => !string.IsNullOrEmpty(OutputImagePath));
        }

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Prompt { get; set; } = string.Empty;
        public int AspectRatioIndex { get; set; } = 0;
        public int Steps { get; set; } = 9;
        public double Cfg { get; set; } = 1.0;
        public long Seed { get; set; } = 0;
        public double Denoise { get; set; } = 1.0;
        public bool LoraEnabled { get; set; } = false;
        public string SelectedLora { get; set; } = string.Empty;
        public TextGeneratorWorkflow SelectedWorkflow { get; set; } = TextGeneratorWorkflow.Zimage;

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
                    LoadThumbnail();
                }
            }
        }

        public bool HasOutputImage => !string.IsNullOrEmpty(OutputImagePath);

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

        [JsonIgnore]
        public ICommand OpenImageCommand { get; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }

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

        public string StatusDisplay => Status switch
        {
            "Pending" => "⏳ Pending",
            "Processing" => "⚙️ Processing",
            "Completed" => "✅ Completed",
            "Failed" => "❌ Failed",
            _ => Status
        };

        public string StatusColor => Status switch
        {
            "Pending" => "#6C757D",
            "Processing" => "#FFA500",
            "Completed" => "#28A745",
            "Failed" => "#DC3545",
            _ => "#000000"
        };

        public string DisplayPrompt => Prompt.Length > 50 ? Prompt.Substring(0, 47) + "..." : Prompt;

        private void LoadThumbnail()
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

        private void OpenImage()
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

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
