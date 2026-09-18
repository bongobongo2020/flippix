using System.IO;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// A serializable snapshot of a VR 180 job: one flat input video converted to a
    /// 360° equirectangular panorama via the LTX-2.3-22B equirect IC-LoRA.
    /// </summary>
    public class Vr180QueueItem : BaseQueueItem
    {
        public string VideoPath { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string NegativePrompt { get; set; } = string.Empty;

        /// <summary>Equirect output dimensions (always 2:1). Width drives detail/quality.</summary>
        public int EquirectWidth { get; set; } = 960;
        public int EquirectHeight { get; set; } = 480;

        /// <summary>Max number of source frames to load (clip length).</summary>
        public int FrameCap { get; set; } = 121;

        /// <summary>
        /// Depth-based stereo parallax strength for the side-by-side VR180 pass.
        /// 0 = mono duplicated to both eyes (flat-in-VR); higher = more 3D pop.
        /// </summary>
        public double StereoStrength { get; set; } = 0.12;

        public long Seed { get; set; } = -1;
        public string? OutputVideoPath { get; set; }

        public string DisplayText =>
            !string.IsNullOrEmpty(VideoPath)
                ? $"{Path.GetFileName(VideoPath)} → {EquirectWidth}×{EquirectHeight}"
                : "(no input)";
    }
}
