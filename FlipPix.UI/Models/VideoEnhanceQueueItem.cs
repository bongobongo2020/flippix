using System.IO;

namespace FlipPix.UI.Models
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

        public string DisplayText =>
            !string.IsNullOrEmpty(InputVideoPath)
                ? Path.GetFileName(InputVideoPath)
                : "(no input)";
    }
}
