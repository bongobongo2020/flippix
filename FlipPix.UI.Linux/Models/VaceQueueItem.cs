using System.IO;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// Queue item for a single VACE video generation job.
    /// </summary>
    public class VaceQueueItem : BaseQueueItem
    {
        public string ForegroundImagePath { get; set; } = string.Empty;
        public string InputVideoPath { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string? OutputVideoPath { get; set; }

        public string DisplayText =>
            !string.IsNullOrEmpty(ForegroundImagePath)
                ? $"{Path.GetFileName(ForegroundImagePath)} + {Path.GetFileName(InputVideoPath)}"
                : "(no input)";
    }
}
