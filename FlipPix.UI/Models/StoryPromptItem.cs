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
    public class StoryPromptItem : INotifyPropertyChanged
    {
        private string _status = "Queued";
        private double _progress = 0;
        private string? _outputImagePath;
        private BitmapImage? _thumbnailImage;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public int Index { get; set; }  // The prompt index
        public string Prompt { get; set; } = string.Empty;
        public string InputImagePath { get; set; } = string.Empty;

        // Settings snapshot (captured when prompt is added to queue)
        public string StyleName { get; set; } = string.Empty;
        public string StyleWorkflowFile { get; set; } = string.Empty;
        public bool LoraEnabled { get; set; }
        public string SelectedLora { get; set; } = string.Empty;
        public double LoraStrengthModel { get; set; } = 1.0;
        public double LoraStrengthClip { get; set; } = 1.0;
        public string SelectedStyle { get; set; } = "Phone Photo";
        public bool SpicyContentEnabled { get; set; }
        public string NegativePrompt { get; set; } = string.Empty;
        public string SelectedOrientation { get; set; } = "Portrait (944x1408)";

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
                }
            }
        }

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

        public StoryPromptItem()
        {
            OpenImageCommand = new RelayCommand(OpenImage, () => !string.IsNullOrEmpty(OutputImagePath));
        }

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

        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
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

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
