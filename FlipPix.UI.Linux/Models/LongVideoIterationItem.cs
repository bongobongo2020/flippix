using System.ComponentModel;
using System.IO;

namespace FlipPix.UI.Linux.Models
{
    public class LongVideoIterationItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private QueueItemStatus _itemStatus = QueueItemStatus.Pending;
        private string _outputVideoPath = string.Empty;

        public int Number { get; set; }
        public string InputVideoPath { get; set; } = string.Empty;
        public string LastFramePath { get; set; } = string.Empty;
        public string AnalysisPrompt { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }

        public string OutputVideoPath
        {
            get => _outputVideoPath;
            set
            {
                _outputVideoPath = value;
                OnPropertyChanged(nameof(OutputVideoPath));
                OnPropertyChanged(nameof(HasOutput));
            }
        }

        public QueueItemStatus ItemStatus
        {
            get => _itemStatus;
            set
            {
                _itemStatus = value;
                OnPropertyChanged(nameof(ItemStatus));
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(StatusColor));
            }
        }

        public bool HasOutput => !string.IsNullOrEmpty(OutputVideoPath) && File.Exists(OutputVideoPath);

        public string DisplayText => $"Iteration {Number}";

        public string StatusDisplay => _itemStatus switch
        {
            QueueItemStatus.Pending    => "⏳ Pending",
            QueueItemStatus.Processing => "⚙️ Processing",
            QueueItemStatus.Completed  => "✅ Done",
            QueueItemStatus.Failed     => "❌ Failed",
            _                          => "Unknown"
        };

        public string StatusColor => _itemStatus switch
        {
            QueueItemStatus.Pending    => "#6C757D",
            QueueItemStatus.Processing => "#FFC107",
            QueueItemStatus.Completed  => "#28A745",
            QueueItemStatus.Failed     => "#DC3545",
            _                          => "#6C757D"
        };

        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
