using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlipPix.Core.Models;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.UI.Services
{
    /// <summary>One beat of a Long Form chain: its prompt, the seconds it delivers and its seed.</summary>
    public sealed record LongFormShot(string Id, string Prompt, double Seconds, long Seed);

    /// <summary>Everything a Long Form run needs, already uploaded to the server.</summary>
    public sealed record LongFormRender
    {
        public IReadOnlyList<LongFormShot> Shots { get; init; } = Array.Empty<LongFormShot>();
        /// <summary>The opening image: beat 1's first frame, and <c>&lt;Picture 1&gt;</c> of every beat.</summary>
        public string OpeningImage { get; init; } = string.Empty;
        /// <summary>Character sheets, sent as <c>&lt;Picture 2&gt;</c>… on every beat.</summary>
        public IReadOnlyList<string> CastImages { get; init; } = Array.Empty<string>();
        /// <summary>The chain's folder under <c>output/h3_chains</c>; the same name resumes from its checkpoints.</summary>
        public string RunName { get; init; } = "longform";
        /// <summary>1-based beat the run starts at; beats before it continue from their saved checkpoints.</summary>
        public int StartClip { get; init; } = 1;
        public string AspectRatio { get; init; } = "16:9 (Widescreen)";
        public double Megapixels { get; init; } = 0.5;
        public int Steps { get; init; } = LongFormGraph.Steps;
        public string DiffusionModel { get; init; } = string.Empty;
        public string TextEncoder { get; init; } = string.Empty;
        public IReadOnlyList<MiniMaxI2VLora> Loras { get; init; } = Array.Empty<MiniMaxI2VLora>();
        public bool MaxFidelityReferences { get; init; } = true;
        /// <summary>Where the assembled story is copied, under ComfyUI's output folder.</summary>
        public string OutputSubfolder { get; init; } = "video/fflf";
        public string Filename { get; init; } = "longform";
    }

    /// <summary>
    /// The ⛓ FFLF tab's Long Form workflow: the whole story as ONE ComfyUI prompt on the MiniMax H3 Context-Loop pack
    /// (<c>MiniMaxH3Chain*</c>), rendered beat after beat on the server. See docs/longform-design.md.
    ///
    /// <para>Why it does not drift like the per-beat graphs past ~30 s: each beat continues from the previous beat's
    /// <b>sampled latent</b> (a 39-frame AV prefix) instead of a decoded, upscaled and re-encoded PNG; the
    /// <c>color_drift_av</c> transition (Color-Stable Drift AV) shows that prefix at the sampler's own noise level, so a
    /// clean predecessor cannot take over the noisiest steps, and pulls its colour toward beat 1; and the opening image
    /// is <c>&lt;Picture 1&gt;</c> on every beat — an anchor that has not drifted. On a 15-beat story it held mean colour
    /// at dE 4–5 where FFLF climbed to 9–13.</para>
    /// </summary>
    public static class LongFormGraph
    {
        public const string Key = "longform";

        /// <summary>Drift-Control's validated schedule: res_multistep · simple · 20 steps.</summary>
        public const int Steps = 20;
        public const int DraftSteps = 12;
        public const double DraftMegapixels = 0.3;
        public const int ResolutionMultiple = 32;

        /// <summary>The AV prefix each beat after the first carries (39 frames at 24 fps), trimmed before delivery.
        /// Context-Loop's <c>duration_seconds</c> is the raw length, so it is added on top of the beat's own seconds.</summary>
        public const double CarriedSeconds = 39.0 / 24.0;

        public const string Transition = "color_drift_av";
        public const string VideoVae = "minimax_h3_video_vae_int8_convrot.safetensors";
        public const string AudioVae = "minimax_h3_audio_vae_fp32.safetensors";

        public const string AssembleNode = "lf_assemble";
        public const string ClipPreviewPrefix = "flippix_longform_clip";

        private const string Unet = "lf_unet", Clip = "lf_clip", Vae = "lf_vae", AudioVaeNode = "lf_audio_vae",
            Attention = "lf_attention", LoraStack = "lf_loras", Shift = "lf_shift", Profile = "lf_profile", Policy = "lf_policy",
            Plan = "lf_plan", Preflight = "lf_preflight", LoopStart = "lf_loop_start", Current = "lf_current",
            Opening = "lf_opening", FirstScene = "lf_first_scene", RefToVideo = "lf_ref2v", Guide = "lf_first_frame",
            GuideSwitch = "lf_first_frame_switch", Context = "lf_context", Guider = "lf_guider", Scheduler = "lf_scheduler",
            Sampler = "lf_sampler", Noise = "lf_noise", Sample = "lf_sample", Decode = "lf_decode", DecodeAudio = "lf_decode_audio",
            Trim = "lf_trim", Save = "lf_save", Review = "lf_review", LoopEnd = "lf_loop_end";

        /// <summary>The Plan's JSON: every beat, its delivered seconds plus the carried prefix (beats 2+), and its seed.</summary>
        public static string PlanJson(IReadOnlyList<LongFormShot> shots, int steps)
        {
            var plan = new JsonObject
            {
                ["defaults"] = new JsonObject { ["steps"] = steps },
                ["shots"] = new JsonArray(shots.Select((s, i) => (JsonNode)new JsonObject
                {
                    ["id"] = s.Id,
                    ["duration_seconds"] = Math.Round(s.Seconds + (i > 0 ? CarriedSeconds : 0), 3),
                    ["seed"] = s.Seed,
                    ["prompt"] = s.Prompt,
                }).ToArray()),
            };
            return plan.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        public static JsonObject Build(LongFormRender r)
        {
            if (r.Shots.Count == 0) throw new InvalidOperationException("Long Form needs at least one beat.");
            if (string.IsNullOrWhiteSpace(r.OpeningImage)) throw new InvalidOperationException("Long Form needs the opening image.");
            var (width, height) = H3Canvas.Resolve(r.AspectRatio, r.Megapixels, ResolutionMultiple);
            var g = new JsonObject();
            void Node(string id, string cls, string title, JsonObject inputs) =>
                g[id] = new JsonObject { ["class_type"] = cls, ["_meta"] = new JsonObject { ["title"] = title }, ["inputs"] = inputs };

            // ── Model ──
            Node(Unet, "UNETLoader", "Long Form: diffusion model", new JsonObject
            {
                ["unet_name"] = r.DiffusionModel.Trim().Replace('\\', '/'), ["weight_dtype"] = "default",
            });
            Node(Clip, "CLIPLoader", "Long Form: text encoder", new JsonObject
            {
                ["clip_name"] = r.TextEncoder.Trim(), ["type"] = "minimax", ["device"] = "default",
            });
            Node(Vae, "VAELoader", "Long Form: video VAE", new JsonObject { ["vae_name"] = VideoVae });
            Node(AudioVaeNode, "VAELoader", "Long Form: audio VAE", new JsonObject { ["vae_name"] = AudioVae });
            Node(Attention, "ModelAttentionBackend", "Long Form: attention", new JsonObject
            {
                ["model"] = Link(Unet), ["attention"] = "comfy kitchen attention",
            });
            var model = Link(Attention);
            var loras = r.Loras.Where(l => !string.IsNullOrWhiteSpace(l.Name) && l.Strength > 0).ToList();
            if (loras.Count > 0)
            {
                var stack = new JsonObject { ["model"] = model, ["quantized_layers"] = "auto", ["adaln_port"] = "auto" };
                for (var i = 0; i < loras.Count; i++)
                    stack[$"lora_{i + 1}"] = new JsonObject
                    {
                        ["on"] = true, ["lora"] = loras[i].Name.Trim().Replace('\\', '/'), ["strength"] = loras[i].Strength,
                    };
                Node(LoraStack, "H3PowerLoraStack", "Long Form: LoRAs", stack);
                model = Link(LoraStack);
            }
            Node(Shift, "MiniMaxH3SigmaShift", "Long Form: sigma shift", new JsonObject
            {
                ["model"] = model, ["shift_video"] = 12.0, ["shift_audio"] = 3.0,
            });

            // ── The chain ──
            Node(Profile, "MiniMaxH3GenerationProfile", "Long Form: continuity", new JsonObject
            {
                ["scene_continuity"] = "Visual continuity", ["audio_profile"] = "Generate audio",
            });
            Node(Policy, "MiniMaxH3AdvancedPolicy", "Long Form: Color-Stable Drift AV", new JsonObject
            {
                ["chain_policy"] = Link(Profile), ["incoming_transition"] = Transition,
            });
            Node(Plan, "MiniMaxH3ChainPlanModern", "Long Form: plan", new JsonObject
            {
                ["plan_json"] = PlanJson(r.Shots, r.Steps), ["chain_policy"] = Link(Policy), ["run_name"] = r.RunName,
                ["generation_fingerprint"] = string.Empty, ["width"] = width, ["height"] = height,
                ["encode_mode"] = "video", ["crop"] = "disabled", ["default_duration_seconds"] = 5.0,
                ["default_steps"] = r.Steps, ["base_seed"] = r.Shots[0].Seed, ["segment_crf"] = 18, ["video_blend_frames"] = 0,
            });
            var start = Math.Clamp(r.StartClip, 1, r.Shots.Count);
            // Resuming: the earlier beats' prompts may be reworded (the cast is re-read), which must not refuse the
            // checkpoints they were rendered with; their files are still checked.
            var verify = start == 1;
            Node(Preflight, "MiniMaxH3ChainPreflight", "Long Form: preflight", new JsonObject
            {
                ["plan"] = Link(Plan), ["start_clip"] = start, ["scene_range"] = string.Empty, ["verify_resume_history"] = verify,
            });
            Node(LoopStart, "MiniMaxH3ChainLoopStart", "Long Form: loop start", new JsonObject
            {
                ["plan"] = Link(Preflight), ["start_clip"] = start, ["scene_range"] = string.Empty, ["verify_resume_history"] = verify,
            });
            Node(Current, "MiniMaxH3ChainCurrent", "Long Form: current beat", new JsonObject
            {
                ["state"] = Link(LoopStart, 1), ["align_audio_reference"] = false,
            });

            // ── Pictures: the opening image on every beat, then the sheets ──
            Node(Opening, "LoadImage", "Long Form: opening image", new JsonObject { ["image"] = r.OpeningImage });
            var refs = new JsonObject
            {
                ["clip"] = Link(Clip), ["vae"] = Link(Vae), ["audio_vae"] = Link(AudioVaeNode), ["prompt"] = Link(Current, 4),
                ["width"] = Link(Current, 8), ["height"] = Link(Current, 9), ["length"] = Link(Current, 6),
                ["ref_image_size"] = r.MaxFidelityReferences ? "max" : "match",
                ["ref_images.ref_image_0"] = Link(Opening),
            };
            var picture = 1;
            foreach (var cast in r.CastImages.Where(c => !string.IsNullOrWhiteSpace(c)).Take(MiniMaxI2VViewModel.MaxTwoPassPictures - 1))
            {
                var id = $"lf_cast_{picture}";
                Node(id, "LoadImage", $"Long Form: character sheet {picture}", new JsonObject { ["image"] = cast });
                refs[$"ref_images.ref_image_{picture}"] = Link(id);
                picture++;
            }
            Node(RefToVideo, "MiniMaxH3ReferenceToVideo", "Long Form: references", refs);

            // Beat 1 only: the opening image pinned at frame 0. AddGuide refuses a missing image, so a lazy switch
            // leaves it out of the later beats.
            Node(FirstScene, "MiniMaxH3ChainFirstSceneImage", "Long Form: beat 1 first frame", new JsonObject
            {
                ["state"] = Link(Current), ["image"] = Link(Opening),
            });
            Node(Guide, "MiniMaxH3AddGuide", "Long Form: pin the opening image", new JsonObject
            {
                ["positive"] = Link(RefToVideo, 0), ["latent"] = Link(RefToVideo, 1), ["frame_idx"] = 0,
                ["vae"] = Link(Vae), ["image"] = Link(FirstScene, 0),
            });
            Node(GuideSwitch, "ComfySwitchNode", "Long Form: first frame on beat 1 only", new JsonObject
            {
                ["switch"] = Link(FirstScene, 1), ["on_true"] = Link(Guide), ["on_false"] = Link(RefToVideo, 0),
            });
            Node(Context, "MiniMaxH3ChainContext", "Long Form: carry the previous beat", new JsonObject
            {
                ["state"] = Link(Current), ["conditioning"] = Link(GuideSwitch), ["vae"] = Link(Vae), ["latent"] = Link(RefToVideo, 1),
                ["audio_vae"] = Link(AudioVaeNode), ["model"] = Link(Shift),
            });

            // ── Sample, decode, save ──
            Node(Guider, "BasicGuider", "Long Form: guider", new JsonObject { ["model"] = Link(Context, 4), ["conditioning"] = Link(Context, 0) });
            Node(Scheduler, "BasicScheduler", "Long Form: scheduler", new JsonObject
            {
                ["model"] = Link(Context, 4), ["scheduler"] = "simple", ["steps"] = Link(Current, 7), ["denoise"] = 1.0,
            });
            Node(Sampler, "KSamplerSelect", "Long Form: sampler", new JsonObject { ["sampler_name"] = "res_multistep" });
            Node(Noise, "RandomNoise", "Long Form: noise", new JsonObject { ["noise_seed"] = Link(Current, 5) });
            Node(Sample, "SamplerCustomAdvanced", "Long Form: sample", new JsonObject
            {
                ["noise"] = Link(Noise), ["guider"] = Link(Guider), ["sampler"] = Link(Sampler), ["sigmas"] = Link(Scheduler),
                ["latent_image"] = Link(Context, 3),
            });
            Node(Decode, "VAEDecode", "Long Form: decode", new JsonObject { ["samples"] = Link(Sample), ["vae"] = Link(Vae) });
            Node(DecodeAudio, "VAEDecodeAudio", "Long Form: decode audio", new JsonObject { ["samples"] = Link(Sample), ["vae"] = Link(AudioVaeNode) });
            Node(Trim, "MiniMaxH3LoopTrim", "Long Form: trim the carried prefix", new JsonObject
            {
                ["images"] = Link(Decode), ["trim_frames"] = Link(Context, 1), ["audio"] = Link(DecodeAudio), ["fps"] = 24.0,
                ["match_tail"] = true, ["state"] = Link(Current), ["audio_trim_mode"] = "sync_with_video",
            });
            Node(Save, "MiniMaxH3ChainSegmentSave", "Long Form: save beat + checkpoint", new JsonObject
            {
                ["state"] = Link(Current), ["images"] = Link(Trim, 0), ["sampled_latent"] = Link(Sample), ["audio"] = Link(Trim, 1),
                ["images_with_overlap"] = Link(Trim, 2),
            });
            Node(Review, "MiniMaxH3ChainReview", "Long Form: review (off)", new JsonObject
            {
                ["state"] = Link(Current), ["segment"] = Link(Save), ["enabled"] = false, ["play_notification_sound"] = false,
                ["auto_continue_timeout_minutes"] = 0.0, ["unload_models_while_waiting"] = false, ["assemble_partial_on_stop"] = true,
                ["partial_audio_source"] = "checkpointed", ["audio"] = Link(Trim, 1),
            });
            Node(LoopEnd, "MiniMaxH3ChainLoopEnd", "Long Form: next beat", new JsonObject
            {
                ["flow"] = Link(LoopStart, 0), ["state"] = Link(Current), ["images"] = Link(Trim, 0), ["sampled_latent"] = Link(Sample),
                ["segment"] = Link(Review), ["between_scene_cleanup"] = "off", ["execution_mode"] = "recursive",
            });
            Node(AssembleNode, "MiniMaxH3ChainAssemble", "Long Form: assemble the story", new JsonObject
            {
                ["manifest"] = Link(LoopEnd), ["audio_source"] = "plan", ["filename"] = r.Filename, ["audio_bitrate"] = 256,
                ["copy_to_output"] = true, ["output_subfolder"] = r.OutputSubfolder, ["color_stabilization"] = "scene_1_anchor",
            });

            CharacterSwapGraph.AddClipPreview(g, ClipPreviewPrefix);
            return g;
        }

        private static JsonArray Link(string id, int slot = 0) => new() { id, slot };
    }
}
