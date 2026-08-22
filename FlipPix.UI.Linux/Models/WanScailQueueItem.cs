using System.IO;

namespace FlipPix.UI.Linux.Models
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
        /// SCAIL II only: subject description fed to the SAM3 tracker (workflow node 87)
        /// to detect/mask the character(s) to replace.
        /// </summary>
        public string Subject { get; set; } = string.Empty;

        /// <summary>
        /// SCAIL II only: when true the whole frame is regenerated ("replace character and
        /// background"); when false only the masked character is replaced over the original
        /// background. Drives the workflow's "Replacement Mode?" boolean (node 188 = !this).
        /// </summary>
        public bool ReplaceBackground { get; set; } = true;

        /// <summary>
        /// SCAIL II only: when true the diffusion model loader (node 10) pins weights to
        /// fp8_e4m3fn in VRAM instead of "default", reducing the resident footprint so the
        /// 14B model is less likely to be partially offloaded/streamed on a 24GB card.
        /// Exposed as a toggle so its effect on generation time can be A/B compared.
        /// </summary>
        public bool OptimizeVram { get; set; } = true;

        /// <summary>
        /// SCAIL II only: in/out trim, expressed in target-FPS frames for VHS_LoadVideo.
        /// TrimSkipFrames = frames skipped from the start (in-point);
        /// TrimFrameCap = number of frames to load (0 = to the end).
        /// </summary>
        public int TrimSkipFrames { get; set; }
        public int TrimFrameCap { get; set; }

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
