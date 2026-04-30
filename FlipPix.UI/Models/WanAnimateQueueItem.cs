using System.IO;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// Queue item for a single WanAnimate video generation job.
    /// Stores reference image, face image, input video, and prompts.
    /// </summary>
    public class WanAnimateQueueItem : BaseQueueItem
    {
        public string ReferenceImagePath { get; set; } = string.Empty;
        public string FaceImagePath { get; set; } = string.Empty;
        public string InputVideoPath { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string NegativePrompt { get; set; } = string.Empty;
        public string? OutputVideoPath { get; set; }

        public string DisplayText =>
            !string.IsNullOrEmpty(ReferenceImagePath)
                ? $"{Path.GetFileName(ReferenceImagePath)} + {Path.GetFileName(InputVideoPath)}"
                : "(no input)";
    }
}
