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
    /// Model for image analyzer queue items
    /// </summary>
    public class ImageAnalyzerQueueItem : INotifyPropertyChanged
    {
        private string _status = "Queued";
        private double _progress = 0;
        private BitmapImage? _outputImageThumbnail;
        private string? _outputImagePath;

        public ImageAnalyzerQueueItem()
        {
            OpenImageCommand = new RelayCommand(OpenImage, () => !string.IsNullOrEmpty(OutputImagePath));
        }

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string SourceImagePath { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public TextGeneratorWorkflow SelectedWorkflow { get; set; } = TextGeneratorWorkflow.Zimage;
        public int SelectedStyleIndex { get; set; } = 0;
        public string StyleName { get; set; } = string.Empty;
        public int AspectRatioIndex { get; set; } = 0; // Default to Portrait
        public int Steps { get; set; } = 9;
        public double Cfg { get; set; } = 1.0;
        public long Seed { get; set; } = 0;
        public double Denoise { get; set; } = 1.0;
        public bool LoraEnabled { get; set; } = false;
        public string SelectedLora { get; set; } = string.Empty;
        public string NegativePrompt { get; set; } = string.Empty;
        public int Width { get; set; } = 944;
        public int Height { get; set; } = 1408;

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
                }
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
            "Queued" => "⏳ Queued",
            "Processing" => "⚙️ Processing",
            "Completed" => "✅ Completed",
            "Failed" => "❌ Failed",
            "Cancelled" => "⏹️ Cancelled",
            _ => Status
        };

        public string StatusColor => Status switch
        {
            "Queued" => "#6C757D",
            "Processing" => "#FFA500",
            "Completed" => "#28A745",
            "Failed" => "#DC3545",
            "Cancelled" => "#FFC107",
            _ => "#000000"
        };

        public string DisplayPrompt => Prompt.Length > 50 ? Prompt.Substring(0, 47) + "..." : Prompt;

        // UI Helper Properties
        public string WorkflowBadge => SelectedWorkflow switch
        {
            TextGeneratorWorkflow.Zimage => "Z",
            TextGeneratorWorkflow.Qwen2512 => "Q",
            TextGeneratorWorkflow.Klien => "K",
            _ => "?"
        };

        public string WorkflowBadgeColor => SelectedWorkflow switch
        {
            TextGeneratorWorkflow.Zimage => "#6366F1",
            TextGeneratorWorkflow.Qwen2512 => "#10B981",
            TextGeneratorWorkflow.Klien => "#F59E0B",
            _ => "#6C757D"
        };

        public bool HasOutputImage => !string.IsNullOrEmpty(OutputImagePath);

        public BitmapImage? OutputImageThumbnail
        {
            get => _outputImageThumbnail;
            set
            {
                if (_outputImageThumbnail != value)
                {
                    _outputImageThumbnail = value;
                    OnPropertyChanged();
                }
            }
        }

        public System.Windows.Visibility StyleNameVisibility =>
            SelectedWorkflow == TextGeneratorWorkflow.Zimage ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        public string AspectRatioDisplay => AspectRatioIndex switch
        {
            0 => "Landscape",
            1 => "Portrait",
            2 => "Square",
            _ => "?"
        };

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
