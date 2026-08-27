using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Models
{
    /// <summary>How a stereoscopic pair is packed into a single frame, when a picture is one.</summary>
    public enum StereoLayout
    {
        /// <summary>An ordinary flat picture, or a pair this could not identify.</summary>
        None,

        /// <summary>Left eye and right eye side by side — the frame is twice one eye wide.</summary>
        SideBySide,

        /// <summary>Left eye above right eye — the frame is twice one eye tall.</summary>
        OverUnder,
    }

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
        private (int Width, int Height) _pixels;
        private StereoLayout _stereo;

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

        /// <summary>
        /// The file's true pixel size, measured once when the slot was filled. Cached because the tab
        /// reads it from property getters that re-evaluate on every form change, and re-opening four
        /// files off the UI thread for each of those is exactly the stall this app keeps re-learning.
        /// </summary>
        public (int Width, int Height) Pixels => _pixels;

        /// <summary>
        /// Whether this picture is a stereoscopic pair packed into one frame, and how it is packed.
        /// Measured once when the slot is filled, alongside the preview it is measured from.
        /// </summary>
        public StereoLayout Stereo
        {
            get => _stereo;
            private set { if (_stereo != value) { _stereo = value; OnPropertyChanged(); } }
        }

        /// <summary>Raised when the slot is filled or cleared, so the tab can re-evaluate Auto aspect.</summary>
        public event EventHandler? Changed;

        public void Clear() => Path = string.Empty;

        private void LoadPreview()
        {
            if (!HasImage)
            {
                Preview = null;
                Info = string.Empty;
                _pixels = (0, 0);
                Stereo = StereoLayout.None;
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

                _pixels = MeasurePixels(Path);
                // The preview is already decoded small, which is all the detector wants - it compares
                // halves, and parallax survives downscaling far better than it survives JPEG.
                Stereo = DetectStereoLayout(bitmap);

                var layout = Stereo switch
                {
                    StereoLayout.SideBySide => " • SBS stereo",
                    StereoLayout.OverUnder => " • over-under stereo",
                    _ => string.Empty
                };
                Info = $"{_pixels.Width}×{_pixels.Height}{layout} • {System.IO.Path.GetFileName(Path)}";
            }
            catch (Exception ex)
            {
                Preview = null;
                _pixels = (0, 0);
                Stereo = StereoLayout.None;
                Info = $"Could not load: {ex.Message}";
            }
        }


        /// <summary>Above this the halves differ too much, relative to the picture's own contrast,
        /// to be two views of one scene. Measured: true pairs score up to 0.15, everything else
        /// from 0.73 up, so this sits in a wide gap rather than on a cliff.</summary>
        private const double StereoThreshold = 0.55;

        /// <summary>How decisively the winning split has to beat the other one to be believed.</summary>
        private const double StereoMargin = 0.75;

        /// <summary>Contrast under which a picture is too flat for its halves to mean anything.</summary>
        private const double StereoMinContrast = 6.0;

        /// <summary>Parallax search granularity, in preview pixels.</summary>
        private const int StereoShiftStep = 4;

        /// <summary>
        /// Whether a picture is a stereoscopic pair packed into one frame, and how.
        ///
        /// <para>Aspect ratio cannot answer this in either direction: a 2:1 panorama is not a stereo
        /// pair, and an over-under frame with 16:9 eyes measures 16:18 — barely portrait. What is
        /// distinctive is that the two halves are two views of one scene, so the halves are compared
        /// directly, both ways, and the mean absolute difference is weighed against the picture's own
        /// contrast — otherwise a flat grey frame passes by being uniformly dull rather than by being
        /// stereo.</para>
        ///
        /// <para>The halves cannot be compared where they lie, though. The two eyes are displaced
        /// <em>horizontally</em> whichever way the pair is packed, so the parallax is searched out first
        /// and the best alignment is what gets judged. Without that step a wide-baseline pair scores no
        /// better than two unrelated pictures. The search runs to 10% of one eye's width — which is the
        /// whole frame for an over-under pair and half of it for side-by-side, so the range is taken from
        /// the span being compared rather than from the frame.</para>
        ///
        /// <para>The winning split also has to beat the other one by a clear margin, so a picture that
        /// happens to be roughly symmetric both ways is reported as neither.</para>
        /// </summary>
        private static StereoLayout DetectStereoLayout(BitmapSource bitmap)
        {
            try
            {
                var w = bitmap.PixelWidth;
                var h = bitmap.PixelHeight;
                if (w < 32 || h < 32) return StereoLayout.None;

                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                var stride = w * 4;
                var bytes = new byte[stride * h];
                converted.CopyPixels(bytes, stride, 0);

                var luma = new double[w * h];
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var i = y * stride + x * 4;
                        luma[y * w + x] = 0.114 * bytes[i] + 0.587 * bytes[i + 1] + 0.299 * bytes[i + 2];
                    }

                // Every pass below samples every second pixel on both axes. This runs while the slot is
                // being filled, on the UI thread, and a quarter of an already-small preview is far more
                // than enough to tell two halves apart.
                var samples = 0;
                var total = 0.0;
                for (var y = 0; y < h; y += 2)
                    for (var x = 0; x < w; x += 2)
                    {
                        total += luma[y * w + x];
                        samples++;
                    }
                var mean = total / samples;

                var deviation = 0.0;
                for (var y = 0; y < h; y += 2)
                    for (var x = 0; x < w; x += 2)
                        deviation += Math.Abs(luma[y * w + x] - mean);

                var contrast = deviation / samples;
                if (contrast < StereoMinContrast) return StereoLayout.None;

                var sideBySide = AlignedHalvesDifference(luma, w, w / 2, 0, w / 2, h) / contrast;
                var overUnder = AlignedHalvesDifference(luma, w, 0, h / 2, w, h / 2) / contrast;

                if (sideBySide < StereoThreshold && sideBySide < overUnder * StereoMargin)
                    return StereoLayout.SideBySide;
                if (overUnder < StereoThreshold && overUnder < sideBySide * StereoMargin)
                    return StereoLayout.OverUnder;
                return StereoLayout.None;
            }
            catch
            {
                // A format the converter will not touch is simply not a stereo pair as far as this goes.
                return StereoLayout.None;
            }
        }

        /// <summary>
        /// The lowest mean absolute difference between the region at the frame's origin and a second
        /// region of the same size at <paramref name="originX"/>, <paramref name="originY"/>, searched
        /// over candidate horizontal parallax shifts. The margin left at both edges is what makes the
        /// shifted reads safe.
        /// </summary>
        private static double AlignedHalvesDifference(
            double[] luma, int width, int originX, int originY, int spanWidth, int spanHeight)
        {
            var maxShift = Math.Max(StereoShiftStep, spanWidth / 10);
            var best = double.MaxValue;

            for (var dx = -maxShift; dx <= maxShift; dx += StereoShiftStep)
            {
                var sum = 0.0;
                var count = 0;

                for (var y = 0; y < spanHeight; y += 2)
                    for (var x = maxShift; x < spanWidth - maxShift; x += 2)
                    {
                        sum += Math.Abs(luma[y * width + x]
                                        - luma[(originY + y) * width + originX + x + dx]);
                        count++;
                    }

                if (count == 0) continue;
                var score = sum / count;
                if (score < best) best = score;
            }

            return best;
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
