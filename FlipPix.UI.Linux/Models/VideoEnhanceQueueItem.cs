using System.IO;

namespace FlipPix.UI.Linux.Models
{
    public enum VideoEnhanceMode { Interpolate, Upscale }

    /// <summary>
    /// Queue item for a single video enhance job (interpolate or upscale).
    /// </summary>
    public class VideoEnhanceQueueItem : BaseQueueItem
    {
        public string InputVideoPath { get; set; } = string.Empty;
        public VideoEnhanceMode Mode { get; set; }
        public string? OutputVideoPath { get; set; }

        /// <summary>Upscale jobs only — the RTX multiplier. 0 in items persisted before this existed;
        /// callers fall back to the workflow's own default.</summary>
        public double UpscaleScale { get; set; }

        public string DisplayText =>
            !string.IsNullOrEmpty(InputVideoPath)
                ? Path.GetFileName(InputVideoPath)
                : "(no input)";
    }
}
