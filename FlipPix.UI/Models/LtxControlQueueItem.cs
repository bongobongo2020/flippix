using System.IO;

namespace FlipPix.UI.Models
{
    public class LtxControlQueueItem : BaseQueueItem
    {
        public string RefImagePath { get; set; } = string.Empty;
        public string RefVideoPath { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string NegativePrompt { get; set; } = string.Empty;
        public long Seed { get; set; } = -1;
        public string? OutputVideoPath { get; set; }

        public string DisplayText =>
            !string.IsNullOrEmpty(RefImagePath)
                ? $"{Path.GetFileName(RefImagePath)} + {Path.GetFileName(RefVideoPath)}"
                : "(no input)";
    }
}
