using System.IO;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// How far the three WAN passes are pushed. Each level is a ladder of long edges: the draft pass
    /// finds the detail, the second and third refine it at rising resolution, so the last number is
    /// what the finished clip is rendered at (the source aspect decides the other side).
    /// </summary>
    public enum TargetedEnhanceDetail
    {
        /// <summary>512 → 768 → 1024. Roughly half the render time of <see cref="Standard"/>.</summary>
        Draft,

        /// <summary>768 → 1024 → 1280. The ladder the workflow was authored around.</summary>
        Standard,

        /// <summary>1024 → 1280 → 1536. Slow, and the top rung needs the clip to be short.</summary>
        High
    }

    /// <summary>
    /// One targeted-enhance job: a clip, the things inside it to rebuild, and how hard to push.
    /// Everything the graph needs is snapshotted here when the item is queued, so editing the tab
    /// afterwards does not re-target jobs already waiting.
    /// </summary>
    public class TargetedWanEnhanceQueueItem : BaseQueueItem
    {
        public string InputVideoPath { get; set; } = string.Empty;
        public string? OutputVideoPath { get; set; }

        /// <summary>What SAM3 tracks, comma-separated ("woman", "face, hands"). Everything outside the
        /// mask it returns is copied through from the source untouched.</summary>
        public string Targets { get; set; } = "woman";

        /// <summary>SAM3 detection confidence. Lower catches more (and more of the background).</summary>
        public double DetectionThreshold { get; set; } = 0.3;

        /// <summary>Pixels the mask is grown and blurred by before compositing, so the enhanced region
        /// blends instead of showing a cut edge. Phases two and three widen it by 4/3 and 5/3.</summary>
        public int MaskFeather { get; set; } = 3;

        /// <summary>Fills enclosed gaps in the tracked mask — useful when SAM3 punches holes through
        /// a subject (hair gaps, a hand in front of a torso).</summary>
        public bool FillHoles { get; set; }

        /// <summary>What the enhanced region should look like. Fed to the WAN text encoder.</summary>
        public string Prompt { get; set; } = string.Empty;

        public TargetedEnhanceDetail Detail { get; set; } = TargetedEnhanceDetail.Standard;

        /// <summary>Per-phase denoise. Phase one does the work; two and three clean up after the
        /// upscale. Above ~0.5 on phase one the subject stops being the same person.</summary>
        public double DenoisePhase1 { get; set; } = 0.4;
        public double DenoisePhase2 { get; set; } = 0.2;
        public double DenoisePhase3 { get; set; } = 0.1;

        /// <summary>Sampler steps for all three phases (the 4-step lightning LoRA is in the stack, so
        /// the graph's 6 is already generous).</summary>
        public int Steps { get; set; } = 6;

        /// <summary>0 = a fresh seed per job.</summary>
        public long Seed { get; set; }

        /// <summary>0 = the whole clip. A cap is the only defence against a long clip and a 14B model.</summary>
        public int MaxFrames { get; set; }

        /// <summary>Restrict phases two and three to the mask as well. Off, they refine the whole frame,
        /// which is cleaner but drifts the background.</summary>
        public bool MaskPhase2 { get; set; } = true;
        public bool MaskPhase3 { get; set; } = true;

        public string DisplayText =>
            !string.IsNullOrEmpty(InputVideoPath)
                ? Path.GetFileName(InputVideoPath)
                : "(no input)";

        /// <summary>Queue-row badge: what this job is rebuilding and how hard.</summary>
        public string TargetsDisplay =>
            $"{(string.IsNullOrWhiteSpace(Targets) ? "(no target)" : Targets)} · {Detail} · " +
            $"{DenoisePhase1:0.##}/{DenoisePhase2:0.##}/{DenoisePhase3:0.##}";
    }
}
