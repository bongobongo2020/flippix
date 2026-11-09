using System.Collections.Generic;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// Everything the ⛓ FFLF tab needs to put a story back exactly as it was left: the opening image, the beats,
    /// every dial, and each beat's take — its seed, video and last frame — so a story reopened after a restart
    /// carries on from the first beat without one. Stored on a <see cref="ScenePrompt"/> in the FFLF story
    /// library (<see cref="ScenePrompt.Fflf"/>).
    /// </summary>
    public sealed class FflfStory
    {
        public string InitialImagePath { get; set; } = string.Empty;
        public string BeatsText { get; set; } = string.Empty;
        public string StyleText { get; set; } = string.Empty;
        public double BeatSeconds { get; set; } = 5;
        public string AspectRatio { get; set; } = string.Empty;
        public double FirstPassMegapixels { get; set; } = 0.3;
        public double FinalMegapixels { get; set; } = 1.0;
        public bool PreviewOnly { get; set; }
        public string DiffusionModel { get; set; } = string.Empty;
        public string Lora { get; set; } = string.Empty;
        public double LoraStrength { get; set; } = 0.25;
        public int FirstPassSteps { get; set; } = 8;
        public bool LockFirstFrame { get; set; } = true;
        public bool KeepInitialReference { get; set; } = true;
        public bool JoinWhenDone { get; set; } = true;
        public long BaseSeed { get; set; }

        /// <summary>The output folder the takes went to (video/fflf/&lt;RunFolder&gt;).</summary>
        public string RunFolder { get; set; } = string.Empty;
        public string JoinedVideoPath { get; set; } = string.Empty;
        public List<FflfStoryBeat> Beats { get; set; } = new();
    }

    /// <summary>One beat's take, by position.</summary>
    public sealed class FflfStoryBeat
    {
        public string Text { get; set; } = string.Empty;
        public long Seed { get; set; }
        public bool Done { get; set; }
        public string VideoPath { get; set; } = string.Empty;
        public string LastFramePath { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
