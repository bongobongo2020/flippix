using System.IO;

namespace FlipPix.UI.Models
{
    public enum VideoEnhanceMode { Interpolate, Upscale }

    /// <summary>
    /// Which graph an upscale job runs. <see cref="Rtx"/> is first so queue items persisted before this
    /// existed deserialize to the original behaviour.
    /// </summary>
    public enum VideoUpscaleEngine
    {
        /// <summary>RTX Video Super Resolution — <c>upscale nvidaAPI.json</c>. Fast, no model load.</summary>
        Rtx,

        /// <summary>SeedVR2 7B INT8 diffusion restore — <c>video/utility_seedvr2_7b_int8_upscale_video.json</c>.</summary>
        SeedVr2
    }

    /// <summary>
    /// Queue item for a single video enhance job (interpolate or upscale).
    /// </summary>
    public class VideoEnhanceQueueItem : BaseQueueItem
    {
        public string InputVideoPath { get; set; } = string.Empty;
        public VideoEnhanceMode Mode { get; set; }
        public string? OutputVideoPath { get; set; }

        /// <summary>Upscale jobs only. Snapshotted when the item is queued, so changing the tab's selection
        /// afterwards does not re-target jobs already waiting.</summary>
        public VideoUpscaleEngine UpscaleEngine { get; set; } = VideoUpscaleEngine.Rtx;

        /// <summary>SeedVR2 only — the pre-resize multiplier fed to the graph. 0 in items persisted before
        /// this existed; callers fall back to the workflow's own default.</summary>
        public double UpscaleScale { get; set; }

        public string DisplayText =>
            !string.IsNullOrEmpty(InputVideoPath)
                ? Path.GetFileName(InputVideoPath)
                : "(no input)";

        /// <summary>Queue-row badge, so a mixed queue shows which upscaler each job is waiting for.</summary>
        public string EngineDisplay => Mode != VideoEnhanceMode.Upscale
            ? string.Empty
            : UpscaleEngine == VideoUpscaleEngine.SeedVr2
                ? $"SeedVR2 · {(UpscaleScale > 0 ? UpscaleScale : 1.5):0.##}×"
                : "RTX VSR";
    }
}
