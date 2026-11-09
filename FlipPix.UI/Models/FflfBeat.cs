using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Models
{
    public enum FflfBeatState { Pending, Rendering, Done, Failed }

    /// <summary>
    /// One story beat on the ⛓ FFLF tab: its text, the take rendered for it, and the last frame of that take —
    /// the frame the next beat starts on.
    /// </summary>
    public partial class FflfBeat : ObservableObject
    {
        public FflfBeat(int number, string title, string text)
        {
            _number = number;
            _title = title;
            _text = text;
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label))]
        private int _number;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label))]
        private string _title;

        [ObservableProperty] private string _text;

        public string Label => string.IsNullOrEmpty(Title) ? $"Beat {Number}" : $"Beat {Number} · {Title}";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateIcon), nameof(IsDone), nameof(IsRendering))]
        private FflfBeatState _state;

        public bool IsDone => State == FflfBeatState.Done;
        public bool IsRendering => State == FflfBeatState.Rendering;

        public string StateIcon => State switch
        {
            FflfBeatState.Rendering => "⏳",
            FflfBeatState.Done => "✅",
            FflfBeatState.Failed => "⚠",
            _ => "○",
        };

        /// <summary>The seed the take was (or will be) rendered with; 0 until it has one.</summary>
        [ObservableProperty] private long _seed;

        [ObservableProperty] private string _status = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasVideo))]
        private string _videoPath = string.Empty;

        public bool HasVideo => !string.IsNullOrEmpty(VideoPath) && File.Exists(VideoPath);

        /// <summary>A local copy of the take's last frame.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLastFrame))]
        private string _lastFramePath = string.Empty;

        public bool HasLastFrame => !string.IsNullOrEmpty(LastFramePath) && File.Exists(LastFramePath);

        /// <summary>The same frame as uploaded to ComfyUI's input folder.</summary>
        public string UploadedLastFrame { get; set; } = string.Empty;

        /// <summary>The text the take was rendered from — an edit after that makes the take stale.</summary>
        public string RenderedText { get; set; } = string.Empty;

        private BitmapImage? _thumbnail;
        public BitmapImage? Thumbnail => _thumbnail;

        partial void OnLastFramePathChanged(string value)
        {
            _thumbnail = null;
            OnPropertyChanged(nameof(Thumbnail));
            FlipPix.UI.Services.AsyncThumbnail.Load(value, 220, 0, bitmap =>
            {
                if (LastFramePath != value) return;
                _thumbnail = bitmap;
                OnPropertyChanged(nameof(Thumbnail));
            });
        }

        /// <summary>Back to not rendered.</summary>
        public void Reset()
        {
            State = FflfBeatState.Pending;
            Status = string.Empty;
            VideoPath = string.Empty;
            LastFramePath = string.Empty;
            UploadedLastFrame = string.Empty;
            RenderedText = string.Empty;
        }
    }
}
