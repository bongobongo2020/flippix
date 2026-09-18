using System;
using System.Collections.Generic;
using System.Linq;

namespace FlipPix.UI.ViewModels.Video
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
        /// A 2:1 canvas — two square eyes side by side, which is what a stereoscopic side-by-side pair
        /// actually measures.
        ///
        /// <para>Deliberately <b>not</b> in <see cref="AspectRatios"/>: ResolutionSelector's combo accepts
        /// only the eight strings above, so sending this one to the node fails validation. A tab that
        /// offers it has to resolve the canvas itself and write the two numbers into the graph —
        /// <see cref="RequiresLiteralCanvas"/> is the test for that.</para>
        ///
        /// <para>It is worth the special case because <see cref="ClosestAspectRatio"/> otherwise rounds a
        /// 2:1 source down to 16:9 — the log distance is 0.118 against 0.154 to 21:9 — which leaves an SBS
        /// reference asking for two eye panels in a frame that fits neither, and the model divides the
        /// frame into whatever does fit instead.</para>
        /// </summary>
        internal const string StereoAspect = "2:1 (Stereo SBS)";

        /// <summary>
        /// A 1:2 canvas — two square eyes stacked, the over-under packing of the same stereoscopic pair.
        /// Everything said about <see cref="StereoAspect"/> applies to it.
        /// </summary>
        internal const string StereoOverUnderAspect = "1:2 (Stereo Over-Under)";

        /// <summary><see cref="AspectRatios"/> with both stereo canvases sorted into the widest-to-tallest
        /// order, for the tabs that implement the literal canvas.</summary>
        internal static readonly (string Option, double Ratio)[] StereoAspectRatios =
            AspectRatios.Append((Option: StereoAspect, Ratio: 2.0))
                        .Append((Option: StereoOverUnderAspect, Ratio: 0.5))
                        .OrderByDescending(a => a.Ratio)
                        .ToArray();

        /// <summary>True when the option cannot be set on ResolutionSelector, and the tab has to resolve
        /// the width and height itself and feed them into the graph.</summary>
        internal static bool RequiresLiteralCanvas(string aspectOption) =>
            aspectOption == StereoAspect || aspectOption == StereoOverUnderAspect;

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
            // Not ResolutionSelector options — see StereoAspect. Present so Resolve and the megapixel
            // estimates built on it work for the stereo canvases the same way as for the node's own.
            [StereoAspect] = (2, 1),
            [StereoOverUnderAspect] = (1, 2),
        };

        /// <summary>
        /// The width and height ComfyUI will actually render at — for the node's own eight options, a
        /// line-for-line reproduction of
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

            if (RequiresLiteralCanvas(aspectOption))
            {
                // Not the node's arithmetic, because the node cannot produce these aspects at all — and
                // here the exact proportion is the point. Rounding the two sides independently would put
                // 2:1 at 608×288, which is 2.11:1, and its halves are not the equal eyes a stereo pair
                // needs. Snapping the ratio unit instead keeps the proportion exact, keeps both sides on
                // the multiple, and — the ratio and the upscale factor both being integers — keeps them
                // there after the 2× as well.
                var unit = Math.Max(multiple, (int)Math.Round(scale / multiple) * multiple);
                return (wRatio * unit, hRatio * unit);
            }

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

        /// <summary>
        /// Nearest aspect option for a pixel size (16:9 if unknown). <paramref name="includeStereo"/>
        /// puts <see cref="StereoAspect"/> in the running, and only tabs that implement the literal
        /// canvas may pass it — everyone else would hand ResolutionSelector a value it rejects.
        /// </summary>
        internal static string ClosestAspectRatio(int w, int h, bool includeStereo = false)
        {
            if (w <= 0 || h <= 0) return "16:9 (Widescreen)";

            var ratio = (double)w / h;
            return (includeStereo ? StereoAspectRatios : AspectRatios)
                .OrderBy(a => Math.Abs(Math.Log(a.Ratio) - Math.Log(ratio)))
                .First().Option;
        }
    }

    /// <summary>A ResolutionSelector megapixel preset shown in an H3 tab's quality dropdown.</summary>
    public record MegapixelOption(double Value, string Label);
}
