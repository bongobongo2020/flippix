using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Recognises the placeholder a text-to-image model draws instead of the picture it was asked for: a flat
    /// mid-grey canvas carrying one line of pale text, "Image blocked by safety filter".
    ///
    /// <para><b>Why it has to be recognised from the pixels.</b> The Ideogram 4 graph is entirely local — UNET, CLIP
    /// and VAE loaders, no API — so nothing reports a refusal. The model learned the placeholder and renders it as an
    /// ordinary image, at the requested size, through the ordinary SaveImage. ComfyUI calls the run a success, and
    /// the placeholder went onto an ⚡ H3 Express cast card and into <c>Pictures\cast</c> as that character's
    /// photo.</para>
    ///
    /// <para><b>The thresholds.</b> Measured on the eight H3 Express cast photos of 2026-09-12 (1088×1600): the two
    /// placeholders had a luminance mean of ~112 and a standard deviation of 9.3–9.6, near-neutral in every sample;
    /// the six real portraits — people on a plain light grey studio background — had deviations of 36–87. A person
    /// in frame is what a portrait is, and a person is contrast, so the deviation cut sits between the two with
    /// room on both sides.</para>
    /// </summary>
    public static class SafetyFilterPlaceholder
    {
        /// <summary>Luminance standard deviation at or above which an image has real content.</summary>
        public const double MaxDeviation = 20.0;

        /// <summary>The placeholder's grey is mid-tone; a black frame (a failed decode) or a white one is not it.</summary>
        public const double MinMean = 70.0;
        public const double MaxMean = 160.0;

        /// <summary>Share of samples that must be near-neutral grey — the placeholder has no colour at all.</summary>
        public const double MinNeutralShare = 0.95;

        /// <summary>Largest channel spread (max − min of R, G, B) a sample may have and still count as grey.</summary>
        public const int NeutralSpread = 24;

        /// <summary>How wide the image is decoded for the check. The statistics do not need the full canvas.</summary>
        private const int SampleWidth = 256;

        /// <summary>True when the image file at <paramref name="path"/> is the safety-filter placeholder. Decodes a
        /// small copy; safe to call off the UI thread.</summary>
        public static bool IsPlaceholder(string path)
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = SampleWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            bgra.Freeze();
            var width = bgra.PixelWidth;
            var height = bgra.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            bgra.CopyPixels(pixels, stride, 0);
            return LooksLikePlaceholder(pixels, width, height, stride);
        }

        /// <summary>The check on raw BGRA32 pixels: mid-grey, colourless and nearly flat.</summary>
        public static bool LooksLikePlaceholder(byte[] bgra, int width, int height, int stride)
        {
            if (width <= 0 || height <= 0 || bgra.Length < stride * height) return false;

            double sum = 0, squares = 0;
            long neutral = 0, count = 0;
            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var i = row + x * 4;
                    int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                    var luma = 0.299 * r + 0.587 * g + 0.114 * b;
                    sum += luma;
                    squares += luma * luma;
                    if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= NeutralSpread) neutral++;
                    count++;
                }
            }

            var mean = sum / count;
            var deviation = Math.Sqrt(Math.Max(0, squares / count - mean * mean));
            return deviation < MaxDeviation &&
                   mean >= MinMean && mean <= MaxMean &&
                   (double)neutral / count >= MinNeutralShare;
        }
    }
}
