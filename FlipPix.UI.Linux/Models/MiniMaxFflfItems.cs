using System;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// One keyframe on the 🌀🎯 MiniMax FFLF tab: a still the take has to pass through. The opening
    /// frame is the picture at 0.00s; every other keyframe is the picture a clip has to arrive at.
    /// </summary>
    public partial class MiniMaxFflfFrame : ObservableObject
    {
        private string _path = string.Empty;
        private BitmapImage? _preview;
        private string _info = string.Empty;

        private string _label;

        public MiniMaxFflfFrame(string label) => _label = label;

        /// <summary>What this frame is called in the UI and in the log — "Opening frame", "Keyframe 2"…
        /// Clips renumber when one is removed, so the label moves with them.</summary>
        public string Label
        {
            get => _label;
            set { if (_label == value) return; _label = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string Path
        {
            get => _path;
            set
            {
                if (_path == value) return;
                _path = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasImage));
                LoadPreview();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public BitmapImage? Preview
        {
            get => _preview;
            private set { _preview = value; OnPropertyChanged(); }
        }

        public string Info
        {
            get => _info;
            private set { _info = value; OnPropertyChanged(); }
        }

        public bool HasImage => !string.IsNullOrEmpty(Path) && File.Exists(Path);

        /// <summary>Raised when the frame is filled or cleared, so the tab can re-evaluate Auto aspect.</summary>
        public event EventHandler? Changed;

        public void Clear() => Path = string.Empty;

        private void LoadPreview()
        {
            if (!HasImage)
            {
                Preview = null;
                Info = string.Empty;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 320;
                bitmap.UriSource = new Uri(Path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                Preview = bitmap;

                var (w, h) = MeasurePixels(Path);
                Info = $"{w}×{h} • {System.IO.Path.GetFileName(Path)}";
            }
            catch (Exception ex)
            {
                Preview = null;
                Info = $"Could not load: {ex.Message}";
            }
        }

        /// <summary>True pixel size of the file — the preview is decoded small, so it cannot supply it.</summary>
        public static (int Width, int Height) MeasurePixels(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                return (frame.PixelWidth, frame.PixelHeight);
            }
            catch { return (0, 0); }
        }
    }

    /// <summary>
    /// One clip of the chain on the 🌀🎯 MiniMax FFLF tab. Every clip ends on a keyframe: clip 1 runs
    /// from the opening frame to its own end frame, and each later clip continues out of the tail of the
    /// one before it and has to arrive at its own end frame.
    ///
    /// <para>Clip 1 is the base sampling pass. Clips 2–4 are the loop's iterations, and the loop indexes
    /// its prompt, duration and end-frame off the loop counter — which is why there are exactly three of
    /// them.</para>
    /// </summary>
    public partial class MiniMaxFflfClip : ObservableObject
    {
        private string _prompt = string.Empty;
        private int _seconds = 10;
        private int _index;

        public MiniMaxFflfClip(int index)
        {
            _index = index;
            EndFrame = new MiniMaxFflfFrame(FrameLabel(index));
            EndFrame.Changed += (_, _) =>
            {
                OnPropertyChanged(nameof(HasEndFrame));
                Changed?.Invoke(this, EventArgs.Empty);
            };
        }

        /// <summary>1-based clip number; renumbered when an earlier clip is removed.</summary>
        public int Index
        {
            get => _index;
            set
            {
                if (_index == value) return;
                _index = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Subtitle));
                EndFrame.Label = FrameLabel(value);
            }
        }

        public string Title => $"CLIP {Index}";

        /// <summary>Where this clip starts and where it has to land, in the user's own terms.</summary>
        public string Subtitle => Index == 1
            ? "opening frame → keyframe 2"
            : $"tail of clip {Index - 1} → keyframe {Index + 1}";

        /// <summary>The still this clip has to arrive at — its last frame.</summary>
        public MiniMaxFflfFrame EndFrame { get; }

        public bool HasEndFrame => EndFrame.HasImage;

        /// <summary>The FL2VA prompt: the motion path from this clip's first frame to its last.</summary>
        public string Prompt
        {
            get => _prompt;
            set { if (_prompt == value) return; _prompt = value ?? string.Empty; OnPropertyChanged(); }
        }

        /// <summary>Length of this clip in seconds; H3's trained range is ~5–15.</summary>
        public int Seconds
        {
            get => _seconds;
            set
            {
                var clamped = Math.Clamp(value, 5, 15);
                if (_seconds == clamped) return;
                _seconds = clamped;
                OnPropertyChanged();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Raised when the length or the end frame changes, so the tab can retotal the take.</summary>
        public event EventHandler? Changed;

        private static string FrameLabel(int index) => $"Keyframe {index + 1}";
    }
}
