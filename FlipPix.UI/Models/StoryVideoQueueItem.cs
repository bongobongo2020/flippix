using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// Model for story video queue items
    /// </summary>
    public class StoryVideoQueueItem : INotifyPropertyChanged
    {
        private string _status = "Pending";
        private double _progress = 0;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public int Index { get; set; }  // The prompt index (1-10)
        public string Prompt { get; set; } = string.Empty;
        public string InputImagePath { get; set; } = string.Empty;
        public string? OutputVideoPath { get; set; }
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

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
