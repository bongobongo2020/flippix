using System.IO;

namespace FlipPix.UI.Models
{
    public class WanScailQueueItem : BaseQueueItem
    {
        public string CharacterImagePath { get; set; } = string.Empty;
        public string InputVideoPath { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string NegativePrompt { get; set; } = string.Empty;
        public int Fps { get; set; } = 24;
        public int MaxEdge { get; set; } = 1280;
        public long Seed { get; set; } = -1;
        public string? OutputVideoPath { get; set; }

        /// <summary>
        /// When set, only this chunk index is processed (single-chunk mode).
        /// When null, all chunks are processed sequentially.
        /// </summary>
        public int? SingleChunkIndex { get; set; }

        public string DisplayText =>
            !string.IsNullOrEmpty(CharacterImagePath)
                ? $"{Path.GetFileName(CharacterImagePath)} + {Path.GetFileName(InputVideoPath)}"
                : "(no input)";
    }
}
