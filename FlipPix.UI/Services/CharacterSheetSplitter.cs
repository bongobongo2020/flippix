using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Cuts a multi-panel character reference sheet back into the single-view photographs it was made of.
    ///
    /// <para><b>Why.</b> MiniMax H3 treats each <c>ref_image_N</c> as <i>one subject</i>. Handed a three-panel
    /// sheet, the subject it is conditioned on quite literally is a three-panel grid on a plain backdrop — and
    /// so that grid turns up in the rendered video, with the same person standing next to themselves against a
    /// studio wall. No amount of prompt wording reliably outranks the reference image itself. Splitting the
    /// sheet into one reference per view removes the collage from the conditioning entirely while keeping every
    /// view, and as a side effect roughly doubles the pixels each face gets: a face that was a third of one
    /// reference becomes a whole reference of its own.</para>
    ///
    /// <para><b>How the seams are found.</b> A sheet is figures separated by background. Working on a
    /// downscaled copy, every column of pixels is scored by how much of it differs from the sheet's background
    /// colour; columns that are essentially pure background are gaps, and the runs of non-gap columns between
    /// them are the figures. The cut lands in the <i>middle</i> of each gap, so a panel is never clipped —
    /// worst case it carries some spare backdrop. Detection is deliberately allowed to answer "one panel":
    /// an image that is a single portrait must come through untouched.</para>
    /// </summary>
    public static class CharacterSheetSplitter
    {
        /// <summary>Find the panels by looking at the image.</summary>
        public const int Auto = 0;

        /// <summary>Send the image to H3 exactly as it is, as a single reference.</summary>
        public const int WholeSheet = 1;

        /// <summary>Largest panel edge written to disk. Reference tokens ride through every sampling step, so a
        /// sheet split three ways would otherwise cost three times what the whole sheet cost. Capped here, three
        /// panels come to roughly 1.8× a single 1 MP reference while still giving each face more pixels than it
        /// had inside the sheet.</summary>
        private const int MaxPanelEdge = 1024;

        /// <summary>Width the seam search runs at. Panel gaps are tens of pixels wide; this is plenty, and it
        /// keeps the scan on a 4000px sheet down to a few milliseconds.</summary>
        private const int AnalysisWidth = 640;

        /// <summary>
        /// Where the gap/content threshold sits between the quietest and the busiest columns in this
        /// particular image. Relative rather than absolute because backdrops differ: a bright studio sweep and
        /// a dim grey wall have nothing in common in absolute terms, but in both the empty columns are far
        /// quieter than the ones with a person in them.
        /// </summary>
        private const double GapThresholdMix = 0.28;

        /// <summary>Floor under the adaptive threshold, in luminance units. Below this everything is sensor
        /// noise and compression, and an image with no figures at all must not be carved into "panels".</summary>
        private const double MinGapThreshold = 7.0;

        /// <summary>Runs narrower than this fraction of the sheet are specks — a vignette edge, a stray
        /// shadow — not figures.</summary>
        private const double MinRunFraction = 0.08;

        /// <summary>How far a panel may fall below and rise above the average panel width before the split is
        /// rejected as not-a-sheet. Measured off real Qwen sheets, whose panels land within ±15% of even.</summary>
        private const double MinPanelWidthRatio = 0.70;
        private const double MaxPanelWidthRatio = 1.40;

        /// <summary>Background slivers narrower than this — between an arm and a torso, say — do not separate
        /// two panels; the runs either side of them are the same figure.</summary>
        private const double MinGapFraction = 0.02;

        /// <summary>Above this width:height, an image with no detectable seams is assumed to be a sheet whose
        /// panels touch, and is split evenly three ways. Below it, "no seams" means "not a sheet".</summary>
        private const double PanoramicAspect = 2.2;

        /// <summary>Bumped when the cropping changes, so cached panels from an older rule are not reused.</summary>
        private const string CacheVersion = "v1";

        /// <summary>
        /// Splits <paramref name="sheetPath"/> and returns the panel files, left to right.
        ///
        /// <para><paramref name="requestedPanels"/> is <see cref="Auto"/> to detect the seams,
        /// <see cref="WholeSheet"/> to skip splitting, or 2+ to force an even split into that many. The result
        /// always has at least one path; on any failure it is the sheet itself, so a split that cannot be made
        /// degrades to the old behaviour rather than to a broken job.</para>
        /// </summary>
        public static SheetPanels Split(string sheetPath, int requestedPanels)
        {
            if (string.IsNullOrEmpty(sheetPath) || !File.Exists(sheetPath))
                return SheetPanels.Empty;

            var whole = new SheetPanels(new[] { sheetPath }, "sent whole");
            if (requestedPanels == WholeSheet)
                return whole;

            try
            {
                var info = new FileInfo(sheetPath);
                var key = CacheKey(info, requestedPanels);

                double[] cuts;
                string note;
                if (requestedPanels >= 2)
                {
                    cuts = EvenCuts(requestedPanels);
                    note = $"{requestedPanels} panels, even split";
                }
                else
                {
                    var (w, h, pixels, stride) = LoadForAnalysis(sheetPath);
                    var detected = DetectCuts(pixels, w, h, stride);
                    if (detected.Count > 0)
                    {
                        cuts = detected.ToArray();
                        note = $"{cuts.Length + 1} panels found";
                    }
                    else if ((double)w / Math.Max(1, h) >= PanoramicAspect)
                    {
                        cuts = EvenCuts(3);
                        note = "no seams found on a wide image — split evenly in 3";
                    }
                    else
                    {
                        return whole;
                    }
                }

                if (cuts.Length == 0) return whole;

                var paths = WritePanels(sheetPath, key, cuts);
                return paths.Count > 1 ? new SheetPanels(paths, note) : whole;
            }
            catch (Exception ex)
            {
                return new SheetPanels(new[] { sheetPath }, $"sent whole — could not be split ({ex.Message})");
            }
        }

        /// <summary>Cut positions, as fractions of the width, for an even split into <paramref name="panels"/>.</summary>
        private static double[] EvenCuts(int panels) =>
            Enumerable.Range(1, Math.Max(1, panels) - 1).Select(i => (double)i / panels).ToArray();

        /// <summary>
        /// Finds the cuts between figures. Returns them as fractions of the width, or an empty list when the
        /// image does not look like several panels.
        ///
        /// <para>A column is scored by how much its luminance varies from top to bottom, <i>not</i> by how far
        /// it sits from an estimated backdrop colour. That was the first thing tried and it fails on real
        /// sheets: Qwen renders the "plain" studio background with a vignette and a soft floor gradient, so a
        /// single backdrop colour is wrong everywhere except where it was sampled and the whole sheet reads as
        /// one continuous figure. Vertical variation does not care — a backdrop, lit however unevenly, changes
        /// slowly down a column, while a column that crosses a person runs through hair, skin, clothing and
        /// floor.</para>
        /// </summary>
        private static List<double> DetectCuts(byte[] px, int width, int height, int stride)
        {
            var cuts = new List<double>();
            if (width < 16 || height < 16) return cuts;

            var profile = ColumnVariation(px, width, height, stride);
            var threshold = GapThreshold(profile);

            var isGap = new bool[width];
            for (var x = 0; x < width; x++) isGap[x] = profile[x] < threshold;

            // Contiguous stretches of content — one per figure, once the slivers inside a figure are merged in.
            var runs = new List<(int Start, int End)>();
            var minGap = Math.Max(2, (int)(width * MinGapFraction));
            var x0 = 0;
            while (x0 < width)
            {
                while (x0 < width && isGap[x0]) x0++;
                if (x0 >= width) break;
                var x1 = x0;
                while (x1 < width && !isGap[x1]) x1++;

                if (runs.Count > 0 && x0 - runs[^1].End < minGap)
                    runs[^1] = (runs[^1].Start, x1);   // background sliver inside one figure
                else
                    runs.Add((x0, x1));
                x0 = x1;
            }

            var minRun = Math.Max(2, (int)(width * MinRunFraction));
            runs = runs.Where(r => r.End - r.Start >= minRun).ToList();

            // One figure is a portrait, not a sheet; more than five is noise rather than panels.
            if (runs.Count < 2 || runs.Count > 5) return cuts;

            for (var i = 0; i + 1 < runs.Count; i++)
                cuts.Add((runs[i].End + runs[i + 1].Start) / 2.0 / width);

            return LooksLikePanels(cuts) ? cuts : new List<double>();
        }

        /// <summary>
        /// Rejects a split whose panels are wildly different widths.
        ///
        /// <para>A reference sheet is laid out as even panels, so the figures found in one come out close to
        /// equally spaced. An ordinary photograph does not: a scene with a dark quiet region reads as a "gap"
        /// on any brightness-based measure, and the runs either side of it are whatever the composition
        /// happened to be. Requiring evenness is what separates a sheet from a picture that merely has a
        /// contrasty middle — and when it says no, the image is sent whole, which is the safe answer.</para>
        /// </summary>
        private static bool LooksLikePanels(IReadOnlyList<double> cuts)
        {
            var edges = new List<double> { 0 };
            edges.AddRange(cuts);
            edges.Add(1);

            var widths = new List<double>();
            for (var i = 0; i + 1 < edges.Count; i++) widths.Add(edges[i + 1] - edges[i]);

            var average = 1.0 / widths.Count;
            return widths.All(w => w >= average * MinPanelWidthRatio && w <= average * MaxPanelWidthRatio);
        }

        /// <summary>
        /// Per column, the standard deviation of luminance down that column, lightly smoothed sideways so a
        /// single noisy column cannot punch a hole in a figure or bridge a gap.
        /// </summary>
        private static double[] ColumnVariation(byte[] px, int width, int height, int stride)
        {
            var raw = new double[width];
            for (var x = 0; x < width; x++)
            {
                double sum = 0, sumSq = 0;
                for (var y = 0; y < height; y++)
                {
                    var i = y * stride + x * 4;
                    // Rec. 601 luma, integer weights — the exact coefficients do not matter here, only that
                    // the three channels are combined consistently.
                    double lum = (px[i + 2] * 299 + px[i + 1] * 587 + px[i] * 114) / 1000.0;
                    sum += lum;
                    sumSq += lum * lum;
                }
                var mean = sum / height;
                raw[x] = Math.Sqrt(Math.Max(0, sumSq / height - mean * mean));
            }

            var smoothed = new double[width];
            for (var x = 0; x < width; x++)
            {
                var lo = Math.Max(0, x - 1);
                var hi = Math.Min(width - 1, x + 1);
                double sum = 0;
                for (var i = lo; i <= hi; i++) sum += raw[i];
                smoothed[x] = sum / (hi - lo + 1);
            }
            return smoothed;
        }

        /// <summary>
        /// Where "empty" stops and "a person is here" starts, placed between this image's own quiet and busy
        /// columns. The percentiles are what make it survive both a bright sweep and a dim wall; the floor is
        /// what stops a flat image from being carved up into imaginary panels.
        /// </summary>
        private static double GapThreshold(double[] profile)
        {
            var sorted = (double[])profile.Clone();
            Array.Sort(sorted);
            var quiet = sorted[(int)(sorted.Length * 0.15)];
            var busy = sorted[(int)(sorted.Length * 0.85)];
            return Math.Max(MinGapThreshold, quiet + (busy - quiet) * GapThresholdMix);
        }

        /// <summary>Decodes a downscaled BGRA copy of the sheet for the seam search.</summary>
        private static (int Width, int Height, byte[] Pixels, int Stride) LoadForAnalysis(string path)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.DecodePixelWidth = AnalysisWidth;
            bitmap.EndInit();
            bitmap.Freeze();

            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            return (converted.PixelWidth, converted.PixelHeight, pixels, stride);
        }

        /// <summary>
        /// Crops the full-resolution sheet at <paramref name="cuts"/> and writes the panels as PNGs, reusing
        /// files already written for the same sheet and the same split.
        /// </summary>
        private static IReadOnlyList<string> WritePanels(string sheetPath, string key, double[] cuts)
        {
            var dir = CacheDirectory();
            Directory.CreateDirectory(dir);

            var stem = SanitizeStem(Path.GetFileNameWithoutExtension(sheetPath));
            var total = cuts.Length + 1;
            var names = Enumerable.Range(0, total)
                .Select(i => Path.Combine(dir, $"{stem}_{key}_p{i + 1}of{total}.png"))
                .ToList();
            if (names.All(File.Exists)) return names;

            BitmapSource source;
            using (var stream = new FileStream(sheetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                source = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                source.Freeze();
            }

            var edges = new List<int> { 0 };
            edges.AddRange(cuts.Select(c => Math.Clamp((int)Math.Round(c * source.PixelWidth), 1, source.PixelWidth - 1)));
            edges.Add(source.PixelWidth);

            var written = new List<string>();
            for (var i = 0; i < total; i++)
            {
                var x = edges[i];
                var w = edges[i + 1] - x;
                if (w < 8) continue;

                BitmapSource panel = new CroppedBitmap(source, new Int32Rect(x, 0, w, source.PixelHeight));

                var longest = Math.Max(panel.PixelWidth, panel.PixelHeight);
                if (longest > MaxPanelEdge)
                {
                    var scale = (double)MaxPanelEdge / longest;
                    panel = new TransformedBitmap(panel, new ScaleTransform(scale, scale));
                }
                panel.Freeze();

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(panel));
                using var output = new FileStream(names[i], FileMode.Create, FileAccess.Write, FileShare.None);
                encoder.Save(output);
                written.Add(names[i]);
            }

            return written;
        }

        /// <summary>Panels live outside the user's output folder — they are an implementation detail of a
        /// submission, regenerated from the sheet whenever they are missing.</summary>
        private static string CacheDirectory() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlipPix", "cache", "h3cast-panels");

        /// <summary>Identifies a sheet <i>and</i> the split asked of it, so editing the sheet or changing the
        /// panel count writes new files instead of serving stale ones.</summary>
        private static string CacheKey(FileInfo info, int requestedPanels)
        {
            var seed = string.Create(CultureInfo.InvariantCulture,
                $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{requestedPanels}|{CacheVersion}");
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(hash)[..10].ToLowerInvariant();
        }

        private static string SanitizeStem(string stem)
        {
            var clean = new string(stem.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
            if (clean.Length == 0) clean = "sheet";
            return clean.Length > 40 ? clean[..40] : clean;
        }
    }

    /// <summary>The panels a sheet was cut into, plus a line about how, for the log and the tab.</summary>
    public sealed class SheetPanels
    {
        public static readonly SheetPanels Empty = new(Array.Empty<string>(), string.Empty);

        public SheetPanels(IReadOnlyList<string> paths, string note)
        {
            Paths = paths;
            Note = note;
        }

        /// <summary>Panel files, left to right. One entry means the sheet goes to H3 unsplit.</summary>
        public IReadOnlyList<string> Paths { get; }

        /// <summary>Human-readable account of what the splitter did.</summary>
        public string Note { get; }

        public int Count => Paths.Count;

        public bool WasSplit => Paths.Count > 1;
    }
}
