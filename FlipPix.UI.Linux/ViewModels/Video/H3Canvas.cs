using System;
using System.Collections.Generic;
using System.Linq;

namespace FlipPix.UI.Linux.ViewModels.Video
{
    /// <summary>
    /// The canvas vocabulary every MiniMax H3 tab shares: the aspect ratios the workflows'
    /// ResolutionSelector node accepts, and the mapping from an image's own proportions onto that list.
    ///
    /// These used to live on the MiniMax H3 tab's ViewModel because it was the first tab to drive a
    /// ResolutionSelector; nine tabs now read them, so they belong to no single tab.
    /// </summary>
    internal static class H3Canvas
    {
        /// <summary>Dropdown entry that defers the choice to whatever the reference image measures.</summary>
        internal const string AutoAspect = "Auto (match image)";

        /// <summary>The ResolutionSelector's aspect options, widest to tallest.</summary>
        internal static readonly (string Option, double Ratio)[] AspectRatios =
        {
            ("21:9 (Ultrawide)", 21.0 / 9.0),
            ("16:9 (Widescreen)", 16.0 / 9.0),
            ("3:2 (Photo)", 3.0 / 2.0),
            ("4:3 (Standard)", 4.0 / 3.0),
            ("1:1 (Square)", 1.0),
            ("3:4 (Portrait Standard)", 3.0 / 4.0),
            ("2:3 (Portrait Photo)", 2.0 / 3.0),
            ("9:16 (Portrait Widescreen)", 9.0 / 16.0),
        };

        /// <summary>
        /// The integer ratios ResolutionSelector works from. Kept separate from
        /// <see cref="AspectRatios"/> because the node divides the megapixel budget by <c>w × h</c> of
        /// these exact integers, and 21/9 as a double does not reproduce that.
        /// </summary>
        private static readonly Dictionary<string, (int W, int H)> RatioPairs = new()
        {
            ["1:1 (Square)"] = (1, 1),
            ["2:3 (Portrait Photo)"] = (2, 3),
            ["3:2 (Photo)"] = (3, 2),
            ["3:4 (Portrait Standard)"] = (3, 4),
            ["4:3 (Standard)"] = (4, 3),
            ["9:16 (Portrait Widescreen)"] = (9, 16),
            ["16:9 (Widescreen)"] = (16, 9),
            ["21:9 (Ultrawide)"] = (21, 9),
        };

        /// <summary>
        /// The width and height ComfyUI will actually render at — a line-for-line reproduction of
        /// <c>comfy_extras/nodes_resolution.py</c>, including that its budget is megapixels × 1024²
        /// (not × 1,000,000) and that each side is rounded to the multiple <em>independently</em>.
        ///
        /// <para>Both details matter, and skipping them is not conservative. At 0.7 MP with a multiple of
        /// 64, "0.7 MP" is 832×832 (0.69 MP) at 1:1 but 1152×640 (0.74 MP) at 16:9, 3:4, 4:3, 9:16 and
        /// 21:9 — a 6.5% spread that an estimate made from the megapixel <i>target</i> cannot see, and
        /// enough on a 24 GB card to be the difference between a run finishing and a CUDA OOM.</para>
        /// </summary>
        internal static (int Width, int Height) Resolve(string aspectOption, double megapixels, int multiple)
        {
            var (wRatio, hRatio) = RatioPairs.TryGetValue(aspectOption, out var pair) ? pair : (16, 9);
            var totalPixels = megapixels * 1024 * 1024;
            var scale = Math.Sqrt(totalPixels / (wRatio * hRatio));

            // Python's round() and Math.Round both break ties to even, so this matches the node exactly.
            var width = (int)Math.Round(wRatio * scale / multiple) * multiple;
            var height = (int)Math.Round(hRatio * scale / multiple) * multiple;
            return (Math.Max(multiple, width), Math.Max(multiple, height));
        }

        /// <summary>Pixels in one rendered frame at this aspect and megapixel target.</summary>
        internal static double CanvasPixels(string aspectOption, double megapixels, int multiple)
        {
            var (w, h) = Resolve(aspectOption, megapixels, multiple);
            return (double)w * h;
        }

        /// <summary>Nearest ResolutionSelector aspect option for a pixel size (16:9 if unknown).</summary>
        internal static string ClosestAspectRatio(int w, int h)
        {
            if (w <= 0 || h <= 0) return "16:9 (Widescreen)";

            var ratio = (double)w / h;
            return AspectRatios
                .OrderBy(a => Math.Abs(Math.Log(a.Ratio) - Math.Log(ratio)))
                .First().Option;
        }
    }

    /// <summary>A ResolutionSelector megapixel preset shown in an H3 tab's quality dropdown.</summary>
    public record MegapixelOption(double Value, string Label);
}
