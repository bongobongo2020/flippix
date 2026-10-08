using System.Collections.Generic;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// A named preset for the H3 Video Editor containing all engine settings.
    /// Based on the workflow's ValuePresets (obvpm) node structure.
    /// </summary>
    public class H3EditorPreset
    {
        /// <summary>Display name of the preset (e.g., "vanilla", "larryvrh", "lightx2v").</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Attention backend: pytorch, sage, or comfy_kitchen.</summary>
        public string Attention { get; set; } = "comfy_kitchen";

        /// <summary>Sparse attention mode: none or vsa.</summary>
        public string SparseAttention { get; set; } = "none";

        /// <summary>Enable Spectrum forecasting.</summary>
        public bool Spectrum { get; set; }

        /// <summary>Turbo LoRA loader: off, normal, or larryvrh.</summary>
        public string TurboLoader { get; set; } = "off";

        /// <summary>Path to the turbo LoRA file.</summary>
        public string TurboLora { get; set; } = string.Empty;

        /// <summary>Turbo LoRA strength (0-1).</summary>
        public double TurboStrength { get; set; } = 1.0;

        /// <summary>Number of sampling steps.</summary>
        public int Steps { get; set; } = 20;

        /// <summary>Sampler name (euler, res_multistep, etc.).</summary>
        public string Sampler { get; set; } = "res_multistep";

        /// <summary>Noise schedule (beta, simple, etc.).</summary>
        public string Scheduler { get; set; } = "simple";

        /// <summary>Sigma shift for video.</summary>
        public int ShiftVideo { get; set; } = 12;

        /// <summary>Sigma shift for audio.</summary>
        public int ShiftAudio { get; set; } = 3;

        /// <summary>Creates the built-in presets from the workflow.</summary>
        public static List<H3EditorPreset> GetBuiltInPresets()
        {
            return new List<H3EditorPreset>
            {
                new()
                {
                    Name = "vanilla",
                    Attention = "comfy_kitchen",
                    SparseAttention = "none",
                    Spectrum = false,
                    TurboLoader = "off",
                    Steps = 20,
                    Sampler = "res_multistep",
                    Scheduler = "simple",
                    ShiftVideo = 12,
                    ShiftAudio = 3
                },
                new()
                {
                    Name = "larryvrh",
                    Attention = "comfy_kitchen",
                    SparseAttention = "none",
                    Spectrum = false,
                    TurboLoader = "larryvrh",
                    TurboLora = @"h3\minimax_h3_turbo_v4_step600_ema_pruned_comfyui.safetensors",
                    TurboStrength = 1.0,
                    Steps = 8,
                    Sampler = "res_multistep",
                    Scheduler = "simple",
                    ShiftVideo = 12,
                    ShiftAudio = 3
                },
                new()
                {
                    Name = "lightx2v",
                    Attention = "comfy_kitchen",
                    SparseAttention = "none",
                    Spectrum = false,
                    TurboLoader = "normal",
                    TurboLora = @"h3\minimax_h3_ref2v_lightx2v_turbo_4step_v0.1_resized_avg_rank_20_bf16.safetensors",
                    TurboStrength = 1.0,
                    Steps = 8,
                    Sampler = "res_multistep",
                    Scheduler = "simple",
                    ShiftVideo = 12,
                    ShiftAudio = 3
                },
                new()
                {
                    Name = "vanilla 8 steps",
                    Attention = "comfy_kitchen",
                    SparseAttention = "none",
                    Spectrum = false,
                    TurboLoader = "off",
                    Steps = 8,
                    Sampler = "res_multistep",
                    Scheduler = "simple",
                    ShiftVideo = 12,
                    ShiftAudio = 3
                },
                new()
                {
                    Name = "taomate",
                    Attention = "comfy_kitchen",
                    SparseAttention = "none",
                    Spectrum = false,
                    TurboLoader = "normal",
                    TurboLora = @"h3\taomate_h3_3step_comfy.safetensors",
                    TurboStrength = 1.0,
                    Steps = 3,
                    Sampler = "res_multistep",
                    Scheduler = "simple",
                    ShiftVideo = 12,
                    ShiftAudio = 3
                }
            };
        }
    }

    /// <summary>
    /// Upscale settings for the H3 Video Editor.
    /// Based on the workflow's H3 Upscale Options node.
    /// </summary>
    public class H3UpscaleSettings
    {
        /// <summary>How much to enlarge the video (1-10, default 2).</summary>
        public double UpscaleFactor { get; set; } = 2.0;

        /// <summary>How much of the upscaled video to regenerate (0-1, default 0.2).</summary>
        public double RefineAmount { get; set; } = 0.2;

        /// <summary>Video quality setting (CRF).</summary>
        public int CRF { get; set; } = 19;

        /// <summary>Enable crossfade between clips.</summary>
        public bool Crossfade { get; set; } = true;

        /// <summary>Number of frames for crossfade.</summary>
        public int CrossfadeFrames { get; set; }

        /// <summary>Enable brightness normalization.</summary>
        public bool LevelLock { get; set; } = true;

        /// <summary>Frames for level lock window.</summary>
        public int LevelLockFrames { get; set; } = 12;

        /// <summary>Enable flicker reduction in level lock.</summary>
        public bool LevelLockFlicker { get; set; } = true;

        /// <summary>Audio declick processing.</summary>
        public bool AudioDeclick { get; set; }
    }
}
