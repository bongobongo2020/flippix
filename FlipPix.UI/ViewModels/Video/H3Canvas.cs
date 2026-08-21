using System;
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
