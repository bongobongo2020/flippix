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
        public string SettingText { get; set; } = string.Empty;
        public string WardrobeText { get; set; } = string.Empty;
        public string WardrobeImagePath { get; set; } = string.Empty;
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
        public bool MaxFidelityReferences { get; set; } = true;
        public bool RepairHandoffFace { get; set; } = true;
        public double FaceRepairStrength { get; set; } = 1.0;
        public bool JoinWhenDone { get; set; } = true;
        public long BaseSeed { get; set; }

        /// <summary>The output folder the takes went to (video/fflf/&lt;RunFolder&gt;).</summary>
        public string RunFolder { get; set; } = string.Empty;
        public string JoinedVideoPath { get; set; } = string.Empty;
        public List<FflfStoryBeat> Beats { get; set; } = new();

        /// <summary>Set on stories of the 🥋 Action tab: its preset, LoRA mix, cast and story idea.</summary>
        public FflfActionStory? Action { get; set; }

        /// <summary>Set on stories of the 🥷 Martial tab: its LoRA stack, references, keyframes and refine dials.</summary>
        public FflfMartialStory? Martial { get; set; }
    }

    /// <summary>What a 🥷 Martial story keeps on top of an FFLF one.</summary>
    public sealed class FflfMartialStory
    {
        public string RefineModel { get; set; } = string.Empty;
        public string TextEncoder { get; set; } = string.Empty;
        public List<FlipPix.Core.Models.H3MartialLoraRow> Loras { get; set; } = new();
        public List<FlipPix.Core.Models.H3MartialReference> References { get; set; } = new();
        public List<FlipPix.Core.Models.H3MartialGuide> Guides { get; set; } = new();
        public string Triggers { get; set; } = string.Empty;
        public string Attention { get; set; } = string.Empty;
        public double RefineStrength { get; set; } = 0.6;
        public int RefineSteps { get; set; } = 10;
        public bool RtxUpscale { get; set; }
        public double RtxScale { get; set; } = 2.0;
    }

    /// <summary>What a 🥋 Action story keeps on top of an FFLF one.</summary>
    public sealed class FflfActionStory
    {
        public int Preset { get; set; } = 2;
        public bool ManualMix { get; set; }
        public double CombatStrength { get; set; }
        public double WeaponStrength { get; set; }
        public double GunFuStrength { get; set; }
        public double MotionStrength { get; set; }
        public double FightFlowStrength { get; set; }
        public bool SoftenRefine { get; set; } = true;
        public string FighterAPath { get; set; } = string.Empty;
        public string FighterBPath { get; set; } = string.Empty;
        public string StoryIdea { get; set; } = string.Empty;
        public int BeatCount { get; set; } = 4;
        public string Sampling { get; set; } = string.Empty;
    }

    /// <summary>One beat's take, by position.</summary>
    public sealed class FflfStoryBeat
    {
        public string Text { get; set; } = string.Empty;
        public long Seed { get; set; }
        public bool Done { get; set; }
        public string VideoPath { get; set; } = string.Empty;
        public string LastFramePath { get; set; } = string.Empty;
        /// <summary>The take as rendered, before ✨ face refine replaced <see cref="VideoPath"/>; empty when not refined.</summary>
        public string OriginalVideoPath { get; set; } = string.Empty;
        public string OriginalLastFramePath { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        /// <summary>The beat's optional dialogue audio, and its length in seconds.</summary>
        public string DialoguePath { get; set; } = string.Empty;
        public double DialogueSeconds { get; set; }
        /// <summary>The dialogue the saved take was rendered with.</summary>
        public string RenderedDialogue { get; set; } = string.Empty;
        /// <summary>The spoken line and the Yakkinator engine/voice it is voiced with.</summary>
        public string LineText { get; set; } = string.Empty;
        public string TtsEngine { get; set; } = string.Empty;
        public string TtsVoice { get; set; } = string.Empty;
        public string VoicedFor { get; set; } = string.Empty;
    }
}
