using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// One link in an FFLF-Dasiwa chain: a single I2V video segment plus the prompt and
    /// first-frame image that produced it. Segment 1's first frame is the user's upload;
    /// each later segment's first frame is the previous segment's extracted last frame.
    /// </summary>
    public partial class FflfDasiwaSegment : ObservableObject
    {
        /// <summary>1-based position in the chain.</summary>
        public int Index { get; set; }

        /// <summary>Local path of the rendered video segment.</summary>
        [ObservableProperty]
        private string _videoPath = string.Empty;

        /// <summary>Local path of the first-frame image used for this segment.</summary>
        public string FirstFramePath { get; set; } = string.Empty;

        /// <summary>Local path of the extracted last frame (feeds the next segment).</summary>
        [ObservableProperty]
        private string _lastFramePath = string.Empty;

        /// <summary>The prompt (manual for segment 1, auto-analyzed otherwise) used for this segment.</summary>
        public string Prompt { get; set; } = string.Empty;

        public long Seed { get; set; }

        public string Label => $"Segment {Index}";
    }
}
