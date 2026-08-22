using System;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// One reference picture on the 🌀 MiniMax I2V tab. H3's Ref2VA mode takes up to four, and they are
    /// references rather than frames: slot 1 is the picture the shot is built around, the rest add a
    /// second character, a prop, or the location. The slot number is the <c>&lt;Picture N&gt;</c> the
    /// prompt refers to, so the order is meaningful and empty slots are closed up before submitting.
    /// </summary>
    public partial class MiniMaxI2VReference : ObservableObject
    {
        private string _path = string.Empty;
        private BitmapImage? _preview;
        private string _info = string.Empty;

        public MiniMaxI2VReference(int slot) => Slot = slot;

        /// <summary>1-based position, shown as the &lt;Picture N&gt; label.</summary>
        public int Slot { get; }

        public string Label => Slot == 1 ? "Picture 1 · required" : $"Picture {Slot}";

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

        /// <summary>Raised when the slot is filled or cleared, so the tab can re-evaluate Auto aspect.</summary>
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
    /// One continuation on the 🌀 MiniMax I2V tab. H3 renders at most ~15s per pass, so a longer take is
    /// rendered as the base pass plus up to three of these: each is a separate sampling pass inside the
    /// same submission, conditioned on the tail of the pass before it and blended back over the overlap.
    /// </summary>
    public partial class MiniMaxI2VSegment : ObservableObject
    {
        private string _prompt = string.Empty;
        private int _seconds = 10;
        private int _index;

        public MiniMaxI2VSegment(int index) => _index = index;

        /// <summary>1-based continuation number; renumbered when an earlier one is removed.</summary>
        public int Index
        {
            get => _index;
            set { if (_index == value) return; _index = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); }
        }

        public string Title => $"CONTINUATION {Index}";

        public string Prompt
        {
            get => _prompt;
            set { if (_prompt == value) return; _prompt = value ?? string.Empty; OnPropertyChanged(); }
        }

        /// <summary>Length of this continuation in seconds; H3's trained range is ~5–15.</summary>
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

        /// <summary>Raised when the length changes, so the tab can retotal the running time.</summary>
        public event EventHandler? Changed;
    }
}
