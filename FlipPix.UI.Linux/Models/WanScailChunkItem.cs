using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Linux.Models
{
    public enum WanScailChunkStatus { Idle, Selected, Processing, Done, Failed }

    public class WanScailChunkItem : ObservableObject
    {
        public int Index { get; init; }
        public int StartFrame { get; init; }
        public int EndFrame { get; init; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                SetProperty(ref _isSelected, value);
                OnPropertyChanged(nameof(BackgroundColor));
                OnPropertyChanged(nameof(BorderColor));
                OnPropertyChanged(nameof(LabelColor));
                OnPropertyChanged(nameof(BottomBarColor));
            }
        }

        private WanScailChunkStatus _status = WanScailChunkStatus.Idle;
        public WanScailChunkStatus Status
        {
            get => _status;
            set
            {
                SetProperty(ref _status, value);
                OnPropertyChanged(nameof(BackgroundColor));
                OnPropertyChanged(nameof(BorderColor));
                OnPropertyChanged(nameof(LabelColor));
                OnPropertyChanged(nameof(BottomBarColor));
                OnPropertyChanged(nameof(StatusDot));
            }
        }

        private bool _hasCachedPrompt;
        public bool HasCachedPrompt
        {
            get => _hasCachedPrompt;
            set { SetProperty(ref _hasCachedPrompt, value); OnPropertyChanged(nameof(StatusDot)); }
        }

        public string BackgroundColor => Status switch
        {
            WanScailChunkStatus.Processing => "#FEF3C7",
            WanScailChunkStatus.Done       => "#D1FAE5",
            WanScailChunkStatus.Failed     => "#FEE2E2",
            _                              => IsSelected ? "#EDE9FE" : "#F3F4F6"
        };

        public string BorderColor => Status switch
        {
            WanScailChunkStatus.Processing => "#F59E0B",
            WanScailChunkStatus.Done       => "#10B981",
            WanScailChunkStatus.Failed     => "#EF4444",
            _                              => IsSelected ? "#7C3AED" : "#D1D5DB"
        };

        public string LabelColor => Status switch
        {
            WanScailChunkStatus.Processing => "#92400E",
            WanScailChunkStatus.Done       => "#065F46",
            WanScailChunkStatus.Failed     => "#991B1B",
            _                              => IsSelected ? "#5B21B6" : "#6B7280"
        };

        public string BottomBarColor => Status switch
        {
            WanScailChunkStatus.Processing => "#F59E0B",
            WanScailChunkStatus.Done       => "#10B981",
            WanScailChunkStatus.Failed     => "#EF4444",
            _                              => IsSelected ? "#7C3AED" : "Transparent"
        };

        public string StatusDot => Status switch
        {
            WanScailChunkStatus.Processing => "⏳",
            WanScailChunkStatus.Done       => "✓",
            WanScailChunkStatus.Failed     => "✗",
            _ => HasCachedPrompt           ? "🧠" : string.Empty
        };

        public string Label => $"Chunk {Index + 1}";
        public string FrameRange => $"{StartFrame}–{EndFrame}";
    }
}
