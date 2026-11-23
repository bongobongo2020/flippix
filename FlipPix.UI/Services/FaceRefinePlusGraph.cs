using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace FlipPix.UI.Services
{
    /// <summary>One LoRA on the face-refine model.</summary>
    public sealed record FaceRefineLora(string Name, double Strength);

    /// <summary>The models one face-refine pass loads: the beat's own H3 model, text encoder, VAEs and LoRAs.</summary>
    public sealed record FaceRefineModels
    {
        public string DiffusionModel { get; init; } = string.Empty;
        public string TextEncoder { get; init; } = string.Empty;
        public string VideoVae { get; init; } = MartialGraph.VideoVae;
        public string AudioVae { get; init; } = MartialGraph.AudioVae;
        public IReadOnlyList<FaceRefineLora> Loras { get; init; } = Array.Empty<FaceRefineLora>();
    }

    /// <summary>Everything one face-refine pass needs, already uploaded to the server.</summary>
    public sealed record FaceRefineRequest
    {
        /// <summary>The take, as uploaded to ComfyUI's input folder.</summary>
        public string Video { get; init; } = string.Empty;
        /// <summary>Whether the take has a soundtrack. Without one the lipsync lock is left out (VHS fails on a
        /// silent file when its audio output is wired).</summary>
        public bool HasAudio { get; init; } = true;
        /// <summary>How many frames to read: H3 only takes 17k+5. 0 reads every frame.</summary>
        public int FrameCap { get; init; }
        /// <summary>The picture the face is redrawn toward (uploaded), sent as <c>&lt;Picture 1&gt;</c>.</summary>
        public string IdentityImage { get; init; } = string.Empty;
        public string Prompt { get; init; } = string.Empty;
        public FaceRefineModels Models { get; init; } = new();
        /// <summary>Denoise of the crop re-render. The Plus pack's presets: 0.30 (default) or 0.15 (light).</summary>
        public double Strength { get; init; } = FaceRefinePlusGraph.DefaultStrength;
        public long Seed { get; init; } = 42;
        public string OutputPrefix { get; init; } = "video/face_refine/refined";
    }

    /// <summary>
    /// A face-refine pass on a finished take: MiniMax H3 redraws the face, crop by crop, and the result is pasted
    /// back. Built on Carasibana's ComfyUI-H3-FaceRefine (track + crop, latent inject, per-frame denoise) with the
    /// stitch and clean-up nodes of AnniCodex's ComfyUI-H3-FaceRefine-Plus — its recommended "V1" recipe, minus
    /// the ×2 RTX base (the beats are already at their final size):
    ///
    /// <para>VHS_LoadVideo → H3FaceTrackCrop (largest face, ArcFace identity tracking) → MiniMaxH3ReferenceToVideo
    /// on the crop canvas (the identity picture + the take's own audio) → H3InjectVideoLatent (img2img on the real
    /// crops) → MiniMaxH3NativeAudioLock (the take's audio stays, so the lips stay in sync) → H3PerFrameDenoise →
    /// 8 er_sde steps at <see cref="FaceRefineRequest.Strength"/> → H3FaceStitchPlus (antialiased, freq_sep: light
    /// and tone from the frame, detail from the refined crop) → H3FaceFlowLock (pins the redrawn features so the
    /// face does not wobble on the head) → H3FaceToneMatch (colour back to the take, lips included) → the video,
    /// and its last frame for the beat after.</para>
    ///
    /// <para>Runs as its own job on a saved take rather than inside the render, so it can be run on any one beat
    /// after the fact without rendering it again, and the three tabs' different render graphs need no surgery.</para>
    /// </summary>
    public static class FaceRefinePlusGraph
    {
        public const int Fps = 24;
        public const double DefaultStrength = 0.30;
        public const double LightStrength = 0.15;
        public const int Steps = 8;

        /// <summary>The two node packs the graph needs, for the log when the server lacks them.</summary>
        public const string BaseRepo = "https://github.com/Carasibana/ComfyUI-H3-FaceRefine";
        public const string PlusRepo = "https://github.com/AnniCodex/ComfyUI-H3-FaceRefine-Plus";

        public const string VideoSaveNode = "fr_save";
        public const string LastFrameSaveNode = "fr_last_save";

        private const string Load = "fr_load", Unet = "fr_unet", Clip = "fr_clip", Vae = "fr_vae", AVae = "fr_avae", Loras = "fr_loras";
        private const string Identity = "fr_identity", Track = "fr_track", R2V = "fr_r2v", Inject = "fr_inject", Lock = "fr_lock";
        private const string PerFrame = "fr_perframe", Guider = "fr_guider", Scheduler = "fr_scheduler", Sampler = "fr_sampler";
        private const string Noise = "fr_noise", Sample = "fr_sample", Decode = "fr_decode", Stitch = "fr_stitch";
        private const string FlowLock = "fr_flowlock", ToneMatch = "fr_tonematch", Video = "fr_video", LastFrame = "fr_last";

        /// <summary>The largest 17k+5 frame count that fits in <paramref name="frames"/> (at least 5).</summary>
        public static int H3Length(int frames) => frames < 22 ? 5 : frames - (frames - 5) % 17;

        /// <summary>How many frames to read from a take <paramref name="seconds"/> long at 24 fps; 0 when unknown.</summary>
        public static int FrameCapFor(double seconds) =>
            seconds > 0 ? H3Length((int)Math.Floor(seconds * Fps + 0.25)) : 0;

        /// <summary>The refine prompt: the face picture's role, then the look and the beat, so the crop is redrawn
        /// as the same moment of the same shot.</summary>
        public static string ComposePrompt(string beat, string? style)
        {
            var sb = new StringBuilder();
            sb.Append("<Picture 1> is a photograph of the character in this shot: keep their face identical to it — ")
              .Append("same features, eyes, skin and hair — while keeping the expression, head pose, lighting and lip movement of the shot.\n\n");
            if (!string.IsNullOrWhiteSpace(style)) sb.Append(style.Trim()).Append("\n\n");
            sb.Append((beat ?? string.Empty).Trim());
            return sb.ToString();
        }

        private static JsonObject Node(string cls, string title, JsonObject inputs) => new()
        {
            ["class_type"] = cls,
            ["_meta"] = new JsonObject { ["title"] = title },
            ["inputs"] = inputs,
        };

        private static JsonArray L(string id, int slot = 0) => new() { id, slot };

        private static string Norm(string name) => (name ?? string.Empty).Trim().Replace('\\', '/');

        public static JsonObject Build(FaceRefineRequest r)
        {
            if (string.IsNullOrWhiteSpace(r.Video)) throw new InvalidOperationException("No take to refine.");
            if (string.IsNullOrWhiteSpace(r.Models.DiffusionModel)) throw new InvalidOperationException("Face refine: pick a diffusion model.");
            if (string.IsNullOrWhiteSpace(r.Models.TextEncoder)) throw new InvalidOperationException("Face refine: no text encoder.");
            var root = new JsonObject();

            // The take: frames (and audio) at 24 fps, trimmed to a length H3 accepts.
            root[Load] = Node("VHS_LoadVideo", "Face refine: the take", new JsonObject
            {
                ["video"] = r.Video,
                ["force_rate"] = Fps,
                ["custom_width"] = 0,
                ["custom_height"] = 0,
                ["frame_load_cap"] = Math.Max(0, r.FrameCap),
                ["skip_first_frames"] = 0,
                ["select_every_nth"] = 1,
                ["format"] = "None",
            });
            var frames = L(Load, 0);
            JsonArray? audio = r.HasAudio ? L(Load, 2) : null;

            // Models
            root[Unet] = Node("UNETLoader", "Face refine: diffusion model",
                new JsonObject { ["unet_name"] = Norm(r.Models.DiffusionModel), ["weight_dtype"] = "default" });
            root[Clip] = Node("CLIPLoader", "Face refine: text encoder",
                new JsonObject { ["clip_name"] = Norm(r.Models.TextEncoder), ["type"] = "minimax", ["device"] = "default" });
            root[Vae] = Node("VAELoader", "Face refine: video VAE", new JsonObject { ["vae_name"] = Norm(r.Models.VideoVae) });
            root[AVae] = Node("VAELoader", "Face refine: audio VAE", new JsonObject { ["vae_name"] = Norm(r.Models.AudioVae) });
            var model = Unet;
            var rows = r.Models.Loras.Where(l => !string.IsNullOrWhiteSpace(l.Name) && l.Strength != 0).ToList();
            if (rows.Count > 0)
            {
                var stack = new JsonObject { ["model"] = L(Unet), ["quantized_layers"] = "auto", ["adaln_port"] = "auto" };
                for (var i = 0; i < rows.Count; i++)
                    stack[$"lora_{i + 1}"] = new JsonObject { ["on"] = true, ["lora"] = Norm(rows[i].Name), ["strength"] = rows[i].Strength };
                root[Loras] = Node("H3PowerLoraStack", "Face refine: LoRA stack", stack);
                model = Loras;
            }

            // 1. Track + crop: the largest face, followed by identity across the take.
            root[Track] = Node("H3FaceTrackCrop", "Face refine 1. Track + crop", new JsonObject
            {
                ["images"] = frames.DeepClone(),
                ["detector"] = "bbox/face_yolov8m.pt",
                ["confidence"] = 0.35,
                ["crop_factor"] = 2.2,
                ["canvas_width"] = 768,
                ["canvas_height"] = 768,
                ["canvas_mode"] = "auto_capped_768",
                ["smooth_window"] = 21,
                ["size_smooth_window"] = 51,
                ["smooth_method"] = "gaussian",
                ["size_mode"] = "per_frame",
                ["identity_track"] = true,
                ["identity_threshold"] = 0.28,
                ["select"] = "largest_face",
                ["fallback_detector"] = "segm/person_yolov8m-seg.pt",
                ["fallback_head_frac"] = 0.5,
                ["select_index"] = 0,
                ["identity_model"] = "insightface",
                ["cut_detection"] = "none",
                ["cut_threshold"] = 3,
                ["absent_shots"] = "off",
                ["X"] = 0,
                ["Y"] = 0,
                ["frame_index"] = 0,
            });

            // 2. Conditioning + an empty AV latent on the crop canvas, as long as the take.
            var r2v = new JsonObject
            {
                ["clip"] = L(Clip),
                ["vae"] = L(Vae),
                ["audio_vae"] = L(AVae),
                ["prompt"] = r.Prompt,
                ["width"] = L(Track, 4),
                ["height"] = L(Track, 5),
                ["length"] = L(Track, 6),
                ["ref_image_size"] = "match",
            };
            if (!string.IsNullOrEmpty(r.IdentityImage))
            {
                root[Identity] = Node("LoadImage", "Face refine: <Picture 1> (face)", new JsonObject { ["image"] = r.IdentityImage });
                r2v["ref_images.ref_image_0"] = L(Identity);
            }
            if (audio != null) r2v["ref_audios.ref_audio_0"] = audio.DeepClone();
            root[R2V] = Node("MiniMaxH3ReferenceToVideo", "Face refine 2. Conditioning (crop canvas)", r2v);

            // 3. img2img on the real crops.
            root[Inject] = Node("H3InjectVideoLatent", "Face refine 3. Inject the crops", new JsonObject
            {
                ["av_latent"] = L(R2V, 1),
                ["images"] = L(Track, 0),
                ["vae"] = L(Vae),
            });

            // 4. The take's audio held as is, so the redrawn mouth stays on the words.
            JsonArray lockedModel = L(model), lockedLatent = L(Inject, 0);
            if (audio != null)
            {
                root[Lock] = Node("MiniMaxH3NativeAudioLock", "Face refine 4. Lock the take's audio", new JsonObject
                {
                    ["model"] = L(model),
                    ["av_latent"] = L(Inject, 0),
                    ["audio_vae"] = L(AVae),
                    ["audio"] = audio.DeepClone(),
                });
                lockedModel = L(Lock, 0);
                lockedLatent = L(Lock, 1);
            }

            // 5. Per-frame denoise (sits in the model path: its model output must reach the guider and scheduler).
            root[PerFrame] = Node("H3PerFrameDenoise", "Face refine 5. Per-frame denoise", new JsonObject
            {
                ["model"] = lockedModel,
                ["av_latent"] = lockedLatent,
                ["transform"] = L(Track, 1),
                ["denoise_multiplier_small_face"] = 1.0,
                ["denoise_multiplier_large_face"] = 1.0,
                ["scale_mode"] = "absolute_px",
                ["face_px_small"] = 30,
                ["face_px_large"] = 120,
                ["gamma"] = 1.0,
                ["smooth_frames"] = 9,
            });

            // 6–7. Sample the crops.
            root[Guider] = Node("BasicGuider", "Face refine: guider", new JsonObject { ["model"] = L(PerFrame, 2), ["conditioning"] = L(R2V, 0) });
            root[Scheduler] = Node("BasicScheduler", "Face refine 6. Denoise — the strength", new JsonObject
            {
                ["model"] = L(PerFrame, 2),
                ["scheduler"] = "simple",
                ["steps"] = Steps,
                ["denoise"] = Math.Round(Math.Clamp(r.Strength, 0.05, 1.0), 3),
            });
            root[Sampler] = Node("KSamplerSelect", "Face refine: sampler", new JsonObject { ["sampler_name"] = "er_sde" });
            root[Noise] = Node("RandomNoise", "Face refine: noise", new JsonObject { ["noise_seed"] = r.Seed > 0 ? r.Seed : 42 });
            root[Sample] = Node("SamplerCustomAdvanced", "Face refine 7. Sample the crops", new JsonObject
            {
                ["noise"] = L(Noise),
                ["guider"] = L(Guider),
                ["sampler"] = L(Sampler),
                ["sigmas"] = L(Scheduler),
                ["latent_image"] = L(PerFrame, 0),
            });
            root[Decode] = Node("VAEDecode", "Face refine 8. Decode the refined crops", new JsonObject { ["samples"] = L(Sample, 0), ["vae"] = L(Vae) });

            // 9. Back into the frames: Stitch Plus → Flow Lock → Tone Match.
            root[Stitch] = Node("H3FaceStitchPlus", "Face refine 9. Stitch Plus", new JsonObject
            {
                ["base_images"] = frames.DeepClone(),
                ["refined_crops"] = L(Decode),
                ["transform"] = L(Track, 1),
                ["paste_region"] = "face_only",
                ["mask_dilation"] = 24,
                ["feather"] = 24,
                ["tone_mode"] = "freq_sep",
                ["lowpass_face_frac"] = 0.15,
                ["detail_gain"] = 1.0,
                ["blend"] = 1.0,
                ["undetected_frames"] = "fade_out",
            });
            root[FlowLock] = Node("H3FaceFlowLock", "Face refine 10. Flow Lock (anti-wobble)", new JsonObject
            {
                ["base_images"] = frames.DeepClone(),
                ["refined_images"] = L(Stitch),
                ["sigma_frac"] = Math.Round(1.0 / 150, 6),
                ["threshold"] = 4.0,
                ["base_offset"] = 0,
            });
            root[ToneMatch] = Node("H3FaceToneMatch", "Face refine 11. Tone Match", new JsonObject
            {
                ["source_images"] = frames.DeepClone(),
                ["processed_images"] = L(FlowLock),
                ["sigma_luma_frac"] = 0.01,
                ["sigma_chroma_frac"] = Math.Round(1.0 / 300, 6),
                ["source_offset"] = 0,
                ["lip_fix"] = true,
                ["face_detector"] = "face_yolov8m.pt",
            });

            // Out: the refined take with its own soundtrack, and its last frame.
            var video = new JsonObject { ["images"] = L(ToneMatch, 0), ["fps"] = Fps };
            if (audio != null) video["audio"] = audio.DeepClone();
            root[Video] = Node("CreateVideo", "Face refine: video", video);
            root[VideoSaveNode] = Node("SaveVideo", "Face refine: save", new JsonObject
            {
                ["video"] = L(Video),
                ["filename_prefix"] = r.OutputPrefix,
                ["format"] = "mp4",
                ["format.codec"] = "auto",
            });
            root[LastFrame] = Node("ImageFromBatch", "Face refine: last frame", new JsonObject { ["image"] = L(ToneMatch, 0), ["batch_index"] = -1, ["length"] = 1 });
            root[LastFrameSaveNode] = Node("SaveImage", "Face refine: save last frame", new JsonObject
            {
                ["images"] = L(LastFrame),
                ["filename_prefix"] = r.OutputPrefix + "_last",
            });
            return root;
        }

        /// <summary>Reads the text encoder and VAEs a beat template loads (node ids of h3-erosmax-2pass.json),
        /// falling back to the server's usual files.</summary>
        public static FaceRefineModels FromTwoPassTemplate(string? templateJson, string diffusionModel, IReadOnlyList<FaceRefineLora> loras)
        {
            string? Value(JsonObject? root, string id, string input) =>
                root?[id]?["inputs"]?[input] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
            JsonObject? root = null;
            try { if (!string.IsNullOrWhiteSpace(templateJson)) root = JsonNode.Parse(templateJson) as JsonObject; }
            catch (System.Text.Json.JsonException) { }
            return new FaceRefineModels
            {
                DiffusionModel = diffusionModel,
                TextEncoder = Value(root, "15", "clip_name") ?? FlipPix.Core.Models.H3MartialSettings.DefaultTextEncoder,
                VideoVae = Value(root, "3", "vae_name") ?? MartialGraph.VideoVae,
                AudioVae = Value(root, "4", "vae_name") ?? MartialGraph.AudioVae,
                Loras = loras,
            };
        }
    }
}
