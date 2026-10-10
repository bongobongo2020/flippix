using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// H3 Video Editor project file format. Contains all state needed to fully restore
    /// a project including timeline clips, reference images, settings, and story mode data.
    /// </summary>
    public class H3Project
    {
        /// <summary>Project file format version for future compatibility.</summary>
        public string Version { get; set; } = "1.0";

        /// <summary>Project metadata.</summary>
        public H3ProjectMetadata Metadata { get; set; } = new();

        /// <summary>Timeline clips with prompts, durations, and output paths.</summary>
        public List<H3ProjectClip> Clips { get; set; } = new();

        /// <summary>Reference image paths (Picture 1-4).</summary>
        public H3ProjectReferenceImages ReferenceImages { get; set; } = new();

        /// <summary>Generation engine settings.</summary>
        public H3ProjectSettings Settings { get; set; } = new();

        /// <summary>Story mode state (optional).</summary>
        public H3ProjectStoryMode? StoryMode { get; set; }

        /// <summary>Path to joined video if available.</summary>
        public string? JoinedVideoPath { get; set; }

        /// <summary>Current prompt text in the editor.</summary>
        public string? CurrentPrompt { get; set; }
    }

    public class H3ProjectMetadata
    {
        public string ProjectName { get; set; } = "Untitled Project";
        public string ProjectFolder { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime LastModified { get; set; } = DateTime.Now;
        public string? Description { get; set; }
    }

    public class H3ProjectClip
    {
        public string Id { get; set; } = string.Empty;
        public int Index { get; set; }
        public string Prompt { get; set; } = string.Empty;
        public double DurationSeconds { get; set; } = 5.0;
        public string? OutputPath { get; set; }
        public string? ThumbnailPath { get; set; }
        public string State { get; set; } = "Pending";
        public bool UseMotionContext { get; set; }
    }

    public class H3ProjectReferenceImages
    {
        public string? Picture1Path { get; set; }
        public string? Picture2Path { get; set; }
        public string? Picture3Path { get; set; }
        public string? Picture4Path { get; set; }
    }

    public class H3ProjectSettings
    {
        // Preset & sampling
        public string PresetName { get; set; } = "vanilla";
        public int Steps { get; set; } = 20;
        public string Sampler { get; set; } = "res_multistep";
        public string Scheduler { get; set; } = "simple";
        public int ShiftVideo { get; set; } = 12;
        public int ShiftAudio { get; set; } = 3;

        // Attention & performance
        public string Attention { get; set; } = "comfy_kitchen";
        public string SparseAttention { get; set; } = "none";
        public bool Spectrum { get; set; }

        // Turbo/LoRA
        public string TurboLoader { get; set; } = "off";
        public string? TurboLora { get; set; }
        public double TurboStrength { get; set; } = 1.0;

        // Resolution & output
        public string AspectRatio { get; set; } = "16:9 (Widescreen)";
        public double Megapixels { get; set; } = 0.5;
        public double DefaultClipDuration { get; set; } = 5.0;
        public bool ChainClips { get; set; } = true;

        // Upscale & join
        public double UpscaleFactor { get; set; } = 2;
        public double RefineAmount { get; set; } = 0.2;
        public int Crf { get; set; } = 19;
        public bool Crossfade { get; set; }
        public bool LevelLock { get; set; }
    }

    public class H3ProjectStoryMode
    {
        public bool Enabled { get; set; }
        public string? StoryText { get; set; }
        public string? StoryFilePath { get; set; }
        public string? StoryFileName { get; set; }
        public string? StorySetting { get; set; }
        public double TargetDurationSeconds { get; set; } = 30.0;
        public bool IsStoryAnalyzed { get; set; }
        public string CastPhotoEngine { get; set; } = "qwen21";
        public bool AutoGenerateCast { get; set; } = true;

        /// <summary>Detected cast members for restoration.</summary>
        public List<H3ProjectCastMember>? DetectedCast { get; set; }

        /// <summary>Story beats for restoration.</summary>
        public List<H3ProjectStoryBeat>? StoryBeats { get; set; }
    }

    public class H3ProjectCastMember
    {
        public string Kind { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
    }

    public class H3ProjectStoryBeat
    {
        public int Index { get; set; }
        public string Text { get; set; } = string.Empty;
        public string? Environment { get; set; }
    }
}
