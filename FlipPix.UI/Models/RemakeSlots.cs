using System;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// One character on the 🎞 Remake tab (swap mode): who in the video it replaces, and how it looks, from a
    /// photo, from words, or both. The photo is what Qwen Image 2.1 Edit paints into each frame.
    /// </summary>
    public partial class RemakeCastSlot : ObservableObject
    {
        public RemakeCastSlot(int number, Action<RemakeCastSlot> browse, Action<RemakeCastSlot> remove)
        {
            Number = number;
            BrowseCommand = new RelayCommand(() => browse(this));
            ClearCommand = new RelayCommand(() => PhotoPath = string.Empty, () => HasPhoto);
            RemoveCommand = new RelayCommand(() => remove(this));
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label), nameof(Tag))]
        private int _number;

        public string Label => $"Character {Number}";
        /// <summary>How the prompt names it.</summary>
        public string Tag => $"<Subject {Number}>";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPhoto), nameof(FileName))]
        private string _photoPath = string.Empty;

        /// <summary>The person in the video this character takes the place of: "the pizza maker", "the man on the right".</summary>
        [ObservableProperty] private string _replaces = string.Empty;

        /// <summary>Optional name used in the prompt ("Pete").</summary>
        [ObservableProperty] private string _name = string.Empty;

        /// <summary>Optional look in words. Blank: Qwen3-VL describes the photo (or the first painted frame).</summary>
        [ObservableProperty] private string _look = string.Empty;

        public bool HasPhoto => !string.IsNullOrEmpty(PhotoPath) && File.Exists(PhotoPath);
        public string FileName => HasPhoto ? Path.GetFileName(PhotoPath) : "No photo — described in words";

        private BitmapImage? _thumbnail;
        public BitmapImage? Thumbnail => _thumbnail;

        partial void OnPhotoPathChanged(string value)
        {
            ClearCommand.NotifyCanExecuteChanged();
            _thumbnail = null;
            OnPropertyChanged(nameof(Thumbnail));
            FlipPix.UI.Services.AsyncThumbnail.Load(value, 200, 0, bitmap =>
            {
                if (PhotoPath != value) return;
                _thumbnail = bitmap;
                OnPropertyChanged(nameof(Thumbnail));
            });
        }

        public RelayCommand BrowseCommand { get; }
        public RelayCommand ClearCommand { get; }
        public RelayCommand RemoveCommand { get; }
    }

    /// <summary>
    /// One painted frame on the 🎞 Remake tab: a moment of the clip, the source frame there, and that frame
    /// repainted to show the result. Pinned, it is a keyframe the render passes through at that moment.
    /// </summary>
    public partial class RemakeKeyframeSlot : ObservableObject
    {
        public RemakeKeyframeSlot(int number, double seconds,
                                  Action<RemakeKeyframeSlot> paint, Action<RemakeKeyframeSlot> browse,
                                  Action<RemakeKeyframeSlot> remove, Action<RemakeKeyframeSlot, string> show)
        {
            Number = number;
            _seconds = seconds;
            PaintCommand = new RelayCommand(() => paint(this), () => !IsBusy);
            BrowseCommand = new RelayCommand(() => browse(this), () => !IsBusy);
            RemoveCommand = new RelayCommand(() => remove(this), () => !IsBusy);
            ShowSourceCommand = new RelayCommand(() => show(this, FramePath), () => HasFrame);
            ShowPaintedCommand = new RelayCommand(() => show(this, PaintedPath), () => HasPainted);
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label), nameof(Tag))]
        private int _number;

        public string Label => $"Frame {Number}";
        public string Tag => $"<Picture {Number}>";

        /// <summary>Seconds from the start of the clip (not of the source file).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TimeLabel))]
        private double _seconds;

        /// <summary>The clip's length, for the slider's range.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TimeLabel))]
        private double _maxSeconds = 5;

        public string TimeLabel => $"{Seconds:0.00} s";

        /// <summary>The source frame at <see cref="Seconds"/>, grabbed on the server.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasFrame))]
        private string _framePath = string.Empty;

        /// <summary>The source time <see cref="FramePath"/> was grabbed at, so a moved slider grabs again.</summary>
        public double FrameAtSourceSeconds { get; set; } = double.NaN;

        /// <summary>The painted frame: what the result shows at this moment.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPainted), nameof(StateLabel))]
        private string _paintedPath = string.Empty;

        /// <summary>The painted frame no longer matches its moment (the slider moved after painting).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateLabel))]
        private bool _isStale;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateLabel))]
        private bool _isBusy;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateLabel))]
        private string _status = string.Empty;

        /// <summary>A painted frame the user chose from disk rather than one painted here.</summary>
        public bool IsOwnImage { get; set; }

        public bool HasFrame => !string.IsNullOrEmpty(FramePath) && File.Exists(FramePath);
        public bool HasPainted => !string.IsNullOrEmpty(PaintedPath) && File.Exists(PaintedPath);

        public string StateLabel =>
            IsBusy ? (Status.Length > 0 ? Status : "Working…")
            : Status.Length > 0 ? Status
            : !HasPainted ? "Not painted yet — Generate paints it"
            : IsStale ? "Moved since painting — repaint?"
            : IsOwnImage ? "Your painted frame" : "Painted ✓";

        private BitmapImage? _frameThumb, _paintedThumb;
        public BitmapImage? FrameThumbnail => _frameThumb;
        public BitmapImage? PaintedThumbnail => _paintedThumb;

        partial void OnFramePathChanged(string value)
        {
            ShowSourceCommand.NotifyCanExecuteChanged();
            _frameThumb = null;
            OnPropertyChanged(nameof(FrameThumbnail));
            FlipPix.UI.Services.AsyncThumbnail.Load(value, 320, 0, bitmap =>
            {
                if (FramePath != value) return;
                _frameThumb = bitmap;
                OnPropertyChanged(nameof(FrameThumbnail));
            });
        }

        partial void OnPaintedPathChanged(string value)
        {
            ShowPaintedCommand.NotifyCanExecuteChanged();
            _paintedThumb = null;
            OnPropertyChanged(nameof(PaintedThumbnail));
            FlipPix.UI.Services.AsyncThumbnail.Load(value, 320, 0, bitmap =>
            {
                if (PaintedPath != value) return;
                _paintedThumb = bitmap;
                OnPropertyChanged(nameof(PaintedThumbnail));
            });
        }

        partial void OnIsBusyChanged(bool value)
        {
            PaintCommand.NotifyCanExecuteChanged();
            BrowseCommand.NotifyCanExecuteChanged();
            RemoveCommand.NotifyCanExecuteChanged();
        }

        public RelayCommand PaintCommand { get; }
        public RelayCommand BrowseCommand { get; }
        public RelayCommand RemoveCommand { get; }
        public RelayCommand ShowSourceCommand { get; }
        public RelayCommand ShowPaintedCommand { get; }
    }
}
