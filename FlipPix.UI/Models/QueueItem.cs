using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace FlipPix.UI.Models
{
    public class QueueItem : INotifyPropertyChanged
    {
        private string _prompt = string.Empty;
        private QueueItemStatus _status = QueueItemStatus.Pending;
        private string _videoPath = string.Empty;
        private string _imagePath = string.Empty;

        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Prompt
        {
            get => _prompt;
            set
            {
                if (_prompt != value)
                {
                    _prompt = value;
                    OnPropertyChanged(nameof(Prompt));
                }
            }
        }

        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (_imagePath != value)
                {
                    _imagePath = value;
                    OnPropertyChanged(nameof(ImagePath));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string DisplayText => $"📎 {Path.GetFileName(ImagePath)}: {Prompt}";

        public QueueItemStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged(nameof(Status));
                    OnPropertyChanged(nameof(StatusColor));
                    OnPropertyChanged(nameof(StatusDisplay));
                }
            }
        }

        public string StatusDisplay => Status switch
        {
            QueueItemStatus.Pending => "⏳ Pending",
            QueueItemStatus.Processing => "⚙️ Processing",
            QueueItemStatus.Completed => "✅ Completed",
            QueueItemStatus.Failed => "❌ Failed",
            _ => Status.ToString()
        };

        public string StatusColor => Status switch
        {
            QueueItemStatus.Pending => "#6C757D",
            QueueItemStatus.Processing => "#007BFF",
            QueueItemStatus.Completed => "#28A745",
            QueueItemStatus.Failed => "#DC3545",
            _ => "#6C757D"
        };

        public string VideoPath
        {
            get => _videoPath;
            set
            {
                if (_videoPath != value)
                {
                    _videoPath = value;
                    OnPropertyChanged(nameof(VideoPath));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public enum QueueItemStatus
    {
        Pending,
        Processing,
        Completed,
        Failed
    }
}