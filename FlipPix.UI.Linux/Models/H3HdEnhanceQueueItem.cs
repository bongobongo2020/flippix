using System.Collections.Generic;
using System.IO;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// The megapixel count the H3 latent upscaler lifts the clip to, which is what decides the
    /// finished resolution — the source aspect fills in the rest.
    /// </summary>
    public enum H3HdDetail
    {
        /// <summary>1.5 MP — about 1632×928 at 16:9. The quick look.</summary>
        Hd,

        /// <summary>2.1 MP — about 1920×1088 at 16:9. What the workflow was authored at.</summary>
        TwoK,

        /// <summary>3.0 MP — about 2304×1312 at 16:9. Slow, and only worth it on short clips.</summary>
        TwoKPlus
    }

    /// <summary>
    /// One HD-enhance job: a finished low-res H3 clip, the references and prompt it was rendered
    /// from, and how hard to re-render it. Snapshotted when the item is queued so editing the tab
    /// afterwards leaves jobs already waiting alone.
    /// </summary>
    public class H3HdEnhanceQueueItem : BaseQueueItem
    {
        public string InputVideoPath { get; set; } = string.Empty;
        public string? OutputVideoPath { get; set; }

        /// <summary>The same reference stills the clip was generated from. They carry the identity
        /// through the re-render; without them the pass has only the prompt to hold a face together.</summary>
        public List<string> ReferenceImagePaths { get; set; } = new();

        /// <summary>The clip's original prompt. The pass re-renders what is already there, so a
        /// different prompt here fights the latent rather than replacing it.</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>Finished size. See <see cref="H3HdDetail"/>.</summary>
        public H3HdDetail Detail { get; set; } = H3HdDetail.TwoK;

        /// <summary>Megapixels the source is resized to before it is encoded. The latent upscaler
        /// works from this, so it sets how much real detail the sampler has to start from.</summary>
        public double BaseMegapixels { get; set; } = 0.8;

        /// <summary>How much of the original survives. 0.45 is the authored value; past ~0.6 the
        /// pass starts inventing motion rather than sharpening it, and below ~0.3 it barely bites.</summary>
        public double Denoise { get; set; } = 0.45;

        /// <summary>Sampler steps. The 4-step turbo LoRA is in the model stack, so 4 is the design
        /// point and more steps mostly cost time.</summary>
        public int Steps { get; set; } = 4;

        /// <summary>0 = a fresh seed per job.</summary>
        public long Seed { get; set; }

        /// <summary>0 = the whole clip. The frame count the H3 conditioning is built for comes off
        /// the loaded duration, so a cap shortens the render rather than desyncing it.</summary>
        public int MaxFrames { get; set; }

        /// <summary>"max" encodes references at the reference pipeline's 2048px short edge for the
        /// best identity match; "match" encodes them at the generation canvas and is much faster,
        /// because reference tokens ride through every sampling step.</summary>
        public string ReferenceFidelity { get; set; } = "max";

        public string DisplayText =>
            !string.IsNullOrEmpty(InputVideoPath)
                ? Path.GetFileName(InputVideoPath)
                : "(no input)";

        /// <summary>Queue-row badge: how hard this job is being pushed.</summary>
        public string SettingsDisplay =>
            $"{Detail} · denoise {Denoise:0.##} · {Steps} steps · " +
            $"{ReferenceImagePaths.Count} ref{(ReferenceImagePaths.Count == 1 ? string.Empty : "s")}";
    }
}
