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

        /// <summary>Turbo LoRA loader: off, normal, or larryvrh. Anything but "off" loads <see cref="TurboLora"/>.</summary>
        public string TurboLoader { get; set; } = "off";

        /// <summary>Turbo LoRA file, as the ComfyUI server lists it (loras folder, forward slashes).</summary>
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

        /// <summary>
        /// True for the 💎 All-In-One Ultra entry: clips render through 🌀 MiniMax I2V's Ultra graph
        /// (h3-minimax-ultra.json, SelfLift full-res tiled) with its own card of dials, and every engine field
        /// above is ignored.
        /// </summary>
        public bool IsUltra { get; set; }

        /// <summary>The dropdown name of the Ultra entry — also what settings store when it is picked.</summary>
        public const string UltraName = "💎 All-In-One Ultra (Full-Res Tiled)";

        /// <summary>
        /// True for the 🌹 ErosMax Turbo 2-Pass entry: clips render through 🌀 MiniMax I2V's 2-Pass graph
        /// (h3-erosmax-2pass.json) in two stages — every beat's first pass as a quick preview, then, once the user
        /// approves the previews, the full two-pass refinement from the same seeds. Its card of dials replaces
        /// the engine fields above.
        /// </summary>
        public bool IsTwoPass { get; set; }

        /// <summary>The dropdown name of the 2-Pass entry — also what settings store when it is picked.</summary>
        public const string TwoPassName = "🌹 ErosMax Turbo 2-Pass (preview → refine)";

        /// <summary>The H3 diffusion model (unet folder) the preset samples with.</summary>
        public string Model { get; set; } = VanillaModel;

        /// <summary>Base Ref2VA model — full quality at 20+ steps, or with a turbo LoRA at few steps.</summary>
        public const string VanillaModel = "h3-minimax/minimax_h3_ref2va_pruned_int8_convrot.safetensors";

        /// <summary>Ref2VA with the turbo distillation fused in — 8 steps, no LoRA.</summary>
        public const string FusedTurboModel = "h3-minimax/minimax_h3_fused_refdelta_r1024_turbo8_mystic07_int8_convrot.safetensors";

        /// <summary>The step count a preset is built for, so a hand-typed count far below it can be flagged —
        /// a distilled or vanilla model sampled with too few steps comes out smeared, not just softer.</summary>
        public static int MinimumSteps(string model, string turboLoader) =>
            turboLoader != "off" ? 3 : model == FusedTurboModel ? 6 : 16;

        /// <summary>
        /// The built-in presets. Every one is a model + LoRA + step count that belong together: the step count
        /// alone means nothing if the model it was tuned for is not what is loaded.
        /// </summary>
        public static List<H3EditorPreset> GetBuiltInPresets()
        {
            return new List<H3EditorPreset>
            {
                new()
                {
                    Name = "vanilla",
                    Model = VanillaModel,
                    TurboLoader = "off",
                    Steps = 20,
                    Sampler = "res_multistep",
                    Scheduler = "simple",
                    ShiftVideo = 12,
                    ShiftAudio = 3
                },
                new()
                {
                    Name = "fused turbo8",
                    Model = FusedTurboModel,
                    TurboLoader = "off",
                    Steps = 8,
                    Sampler = "res_multistep",
                    Scheduler = "simple",
                    ShiftVideo = 12,
                    ShiftAudio = 3
                },
                new()
                {
                    Name = "larryvrh",
                    Model = VanillaModel,
                    TurboLoader = "larryvrh",
                    TurboLora = "H3/larryminimax_h3_turbo_v4_step600_ema.safetensors",
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
                    Model = VanillaModel,
                    TurboLoader = "normal",
                    TurboLora = "H3/minimax_h3_ref2v_lightx2v_turbo_4step_v0.1_resized_avg_rank_20_bf16.safetensors",
                    TurboStrength = 1.0,
                    Steps = 8,
                    Sampler = "res_multistep",
                    Scheduler = "simple",
                    ShiftVideo = 12,
                    ShiftAudio = 3
                },
                new()
                {
                    Name = "taomate",
                    Model = VanillaModel,
                    TurboLoader = "normal",
                    TurboLora = "H3/taomate_h3_3step_comfy.safetensors",
                    TurboStrength = 1.0,
                    Steps = 3,
                    Sampler = "res_multistep",
                    Scheduler = "simple",
                    ShiftVideo = 12,
                    ShiftAudio = 3
                },
                new()
                {
                    // Its dials live on H3VideoEditorSettings.Ultra, not here.
                    Name = UltraName,
                    IsUltra = true
                },
                new()
                {
                    // Its dials live on H3VideoEditorSettings.TwoPass, not here.
                    Name = TwoPassName,
                    IsTwoPass = true
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
