using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// Model for image analyzer queue items
    /// </summary>
    public class ImageAnalyzerQueueItem : INotifyPropertyChanged
    {
        private string _status = "Queued";
        private double _progress = 0;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string SourceImagePath { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public int SelectedStyleIndex { get; set; } = 0;
        public string StyleName { get; set; } = string.Empty;
        public int AspectRatioIndex { get; set; } = 2; // Default to 9:16 portrait
        public int Steps { get; set; } = 9;
        public double Cfg { get; set; } = 1.0;
        public long Seed { get; set; } = 0;
        public double Denoise { get; set; } = 1.0;
        public bool LoraEnabled { get; set; } = false;
        public string SelectedLora { get; set; } = string.Empty;
        public string NegativePrompt { get; set; } = string.Empty;
        public int Width { get; set; } = 944;
        public int Height { get; set; } = 1408;

        public string? OutputImagePath { get; set; }
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

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
