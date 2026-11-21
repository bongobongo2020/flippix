using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace FlipPix.UI.Services
{
    /// <summary>One LoRA on a pass of the martial graph.</summary>
    public sealed record MartialLora(string Name, double Strength);

    /// <summary>A keyframe for one beat: an uploaded image pinned at a frame.</summary>
    public sealed record MartialKeyframe(string Image, int Frame);

    /// <summary>Everything one 🥷 Martial beat needs, already uploaded to the server.</summary>
    public sealed record MartialBeatRender
    {
        /// <summary>The pictures in prompt order: <c>&lt;Picture 1&gt;</c> first. At most <see cref="MartialGraph.MaxPictures"/>.</summary>
        public IReadOnlyList<string> Pictures { get; init; } = Array.Empty<string>();
        /// <summary>Pinned at frame 0 (the image the beat starts on), or null.</summary>
        public string? FirstFrame { get; init; }
        public IReadOnlyList<MartialKeyframe> Keyframes { get; init; } = Array.Empty<MartialKeyframe>();
        public string Prompt { get; init; } = string.Empty;
        public double Seconds { get; init; } = 10;
        public long Seed { get; init; }
        public string AspectRatio { get; init; } = "16:9 (Widescreen)";
        public double FirstPassMegapixels { get; init; } = 0.8;
        public double FinalMegapixels { get; init; } = 1.2;
        public int Steps { get; init; } = 8;
        /// <summary>Stop after the first pass: no refine, no upscale.</summary>
        public bool Draft { get; init; }
        public string DiffusionModel { get; init; } = string.Empty;
        /// <summary>Empty: the refine runs on <see cref="DiffusionModel"/>.</summary>
        public string RefineModel { get; init; } = string.Empty;
        public string TextEncoder { get; init; } = string.Empty;
        public IReadOnlyList<MartialLora> Loras { get; init; } = Array.Empty<MartialLora>();
        public IReadOnlyList<MartialLora> RefineLoras { get; init; } = Array.Empty<MartialLora>();
        /// <summary>"comfy kitchen attention", "pytorch attention", "sage" (the workflow's two Sage patches) or "none".</summary>
        public string Attention { get; init; } = MartialGraph.DefaultAttention;
        public bool MaxFidelityReferences { get; init; }
        public double RefineStrength { get; init; } = 0.6;
        public int RefineSteps { get; init; } = 10;
        public bool RtxUpscale { get; init; }
        public double RtxScale { get; init; } = 2.0;
        public string OutputPrefix { get; init; } = "video/martial/beat";
    }

    /// <summary>
    /// The 🥷 Martial tab's graph: the "MiniMax H3 martial" workflow (02_MiniMax_H3martial.json, checked in as
    /// workflow/video/h3-minimax/martial-h3-wushu-keyframes.json) rebuilt as an API graph, with what the tab needs
    /// to chain beats.
    ///
    /// <para><b>As authored.</b> UNETLoader → turbo 8-step LoRA 0.8 → wushu spatial physics 0.3 → wushu_action 0.4
    /// → (film look, bypassed) → camera motion 0.3 → Sage patches → ModelPreviewOverrideKJ. Up to nine reference
    /// pictures go into MiniMaxH3ReferenceToVideo; three keyframe images, each scaled to the canvas, are pinned with
    /// a MiniMaxH3AddGuide chain at frames 60/120/180. Pass 1 is a KSampler (euler · simple, 8 steps, cfg 1) at
    /// 0.8 MP; the refine runs the upper half of a 10-step schedule rescaled to start at 0.6 on the pass-1 latent,
    /// upscales the video latent to 1.2 MP with the 3D latent upscaler, and finishes with the lower half — that
    /// second half on the unguided conditioning, since the keyframes were laid on the smaller latent. 24 fps;
    /// the frame count is snapped to 17n+5.</para>
    ///
    /// <para><b>Changed.</b> The LoRAs ride one H3PowerLoraStack (int8-safe) instead of five loaders; the authored
    /// H3SigmaRefiner and H3ModeSwitch are not on the server, so the refine schedule is computed here
    /// (<see cref="RefineSigmas"/>) and fed to ManualSigmas, and the switches are dropped (guides on). Attention
    /// defaults to the server's own comfy kitchen backend, Sage is an option. The output is CreateVideo → SaveVideo,
    /// and the last frame is saved for the next beat.</para>
    /// </summary>
    public static class MartialGraph
    {
        public const int Fps = 24;
        public const int MaxPictures = 9;          // ref_image_0 … ref_image_8
        public const int ResolutionMultiple = 32;
        public const string DefaultAttention = "comfy kitchen attention";
        public const string SageAttention = "sage";
        public const string NoAttentionPatch = "none";

        public const string VideoVae = "minimax_h3_video_vae_fp16.safetensors";
        public const string AudioVae = "minimax_h3_audio_vae_fp32.safetensors";
        public const string Upscaler = "minimax_h3_latent_upscaler_3d_bf16.safetensors";
        public const string TinyVae = "taeh3.safetensors";

        public const string VideoSaveNode = "m_save";
        public const string LastFrameSaveNode = "m_last_save";

        // Node ids.
        private const string Unet = "m_unet", RefineUnet = "m_unet_refine", Clip = "m_clip", Vae = "m_vae", AVae = "m_avae";
        private const string Size = "m_size", R2V = "m_r2v", Zero = "m_negative", Pass1 = "m_pass1";
        private const string Noise = "m_noise", Sampler = "m_sampler", HighSigmas = "m_sigmas_high", LowSigmas = "m_sigmas_low";
        private const string Guider2a = "m_guider_refine", Refine1 = "m_refine1", Separate = "m_separate", Upscale = "m_upscale";
        private const string Concat = "m_concat", Guider2b = "m_guider_finish", Refine2 = "m_refine2";
        private const string Decode = "m_decode", DecodeAudio = "m_decode_audio", Rtx = "m_rtx", Video = "m_video", LastFrame = "m_last";

        // ── Numbers ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>The workflow's frame count: seconds × 24, at least 5, rounded up to 17n+5 — the only lengths H3
        /// accepts (10 s → 243). Python's round, so halves go to even.</summary>
        public static int FrameCount(double seconds)
        {
            var n = Math.Max(5, (int)Math.Round(seconds * Fps, MidpointRounding.ToEven));
            return n + (5 - n % 17 + 17) % 17;
        }

        /// <summary>A keyframe's frame inside a beat, clamped as the workflow does: min(max(0, f), length − 1).</summary>
        public static int KeyframeIndex(double seconds, int length) =>
            Math.Clamp((int)Math.Round(seconds * Fps, MidpointRounding.AwayFromZero), 0, Math.Max(0, length - 1));

        /// <summary>
        /// The refine schedule standing in for the workflow's H3SigmaRefiner(1, 0.6, 0, cosine) on a 10-step
        /// simple schedule split at 5: <paramref name="steps"/> steps from <paramref name="strength"/> down to 0
        /// on a cosine curve, split in half. The high half refines the pass-1 latent at its own size, the low half
        /// finishes after the upscale.
        /// </summary>
        public static (double[] High, double[] Low) RefineSigmas(double strength, int steps)
        {
            steps = Math.Clamp(steps, 2, 40);
            strength = Math.Clamp(strength, 0.05, 1.0);
            var all = Enumerable.Range(0, steps + 1)
                .Select(i => Math.Round(strength * (1 + Math.Cos(Math.PI * i / steps)) / 2, 4))
                .ToArray();
            all[^1] = 0;
            var split = steps / 2;
            return (all.Take(split + 1).ToArray(), all.Skip(split).ToArray());
        }

        private static string SigmaText(IEnumerable<double> sigmas) =>
            string.Join(", ", sigmas.Select(s => s.ToString("0.0000", CultureInfo.InvariantCulture)));

        // ── Prompt ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The beat's prompt: the trigger words first (the workflow's rule — "wushu_action," must lead), then who
        /// and what each picture is, the setting, and the beat.
        /// </summary>
        public static string ComposePrompt(string triggers, string beat, string? setting, IReadOnlyList<string> pictureRoles)
        {
            var sb = new StringBuilder();
            var t = (triggers ?? string.Empty).Trim();
            if (t.Length > 0) sb.Append(t.TrimEnd(',', ' ')).Append(",\n");
            for (var i = 0; i < pictureRoles.Count; i++)
                if (!string.IsNullOrWhiteSpace(pictureRoles[i]))
                    sb.Append($"<Picture {i + 1}> {pictureRoles[i].Trim()}\n");
            if (pictureRoles.Count > 0) sb.Append('\n');
            if (!string.IsNullOrWhiteSpace(setting)) sb.Append(setting.Trim()).Append("\n\n");
            sb.Append((beat ?? string.Empty).Trim());
            return sb.ToString();
        }

        // ── The graph ───────────────────────────────────────────────────────────────────────────────

        private static JsonObject Node(string cls, string title, JsonObject inputs) => new()
        {
            ["class_type"] = cls,
            ["_meta"] = new JsonObject { ["title"] = title },
            ["inputs"] = inputs,
        };

        private static JsonArray L(string id, int slot = 0) => new() { id, slot };

        private static string Norm(string name) => (name ?? string.Empty).Trim().Replace('\\', '/');

        public static JsonObject Build(MartialBeatRender r)
        {
            if (string.IsNullOrWhiteSpace(r.DiffusionModel)) throw new InvalidOperationException("Pick a diffusion model.");
            var root = new JsonObject();
            var length = FrameCount(r.Seconds);

            // Loaders
            root[Unet] = Node("UNETLoader", "Martial: diffusion model", new JsonObject { ["unet_name"] = Norm(r.DiffusionModel), ["weight_dtype"] = "default" });
            root[Clip] = Node("CLIPLoader", "Martial: text encoder", new JsonObject
            {
                ["clip_name"] = Norm(string.IsNullOrWhiteSpace(r.TextEncoder) ? FlipPix.Core.Models.H3MartialSettings.DefaultTextEncoder : r.TextEncoder),
                ["type"] = "minimax",
                ["device"] = "default",
            });
            root[Vae] = Node("VAELoader", "Martial: video VAE", new JsonObject { ["vae_name"] = VideoVae });
            root[AVae] = Node("VAELoader", "Martial: audio VAE", new JsonObject { ["vae_name"] = AudioVae });

            // Model wires: pass 1, and the refine's — the same nodes when nothing differs.
            var pass1Model = ModelChain(root, Unet, r.Loras, r.Attention, "m_p1_", "pass 1");
            var refineModel = pass1Model;
            if (!r.Draft)
            {
                var refineUnet = Unet;
                if (!string.IsNullOrWhiteSpace(r.RefineModel) && !Norm(r.RefineModel).Equals(Norm(r.DiffusionModel), StringComparison.OrdinalIgnoreCase))
                {
                    root[RefineUnet] = Node("UNETLoader", "Martial: refine model", new JsonObject { ["unet_name"] = Norm(r.RefineModel), ["weight_dtype"] = "default" });
                    refineUnet = RefineUnet;
                }
                if (refineUnet != Unet || !SameLoras(r.Loras, r.RefineLoras))
                    refineModel = ModelChain(root, refineUnet, r.RefineLoras, r.Attention, "m_p2_", "refine");
            }

            // Canvas and references
            root[Size] = Node("ResolutionSelector", "Martial: canvas", new JsonObject
            {
                ["aspect_ratio"] = r.AspectRatio,
                ["megapixels"] = r.FirstPassMegapixels,
                ["multiple"] = ResolutionMultiple,
            });
            var pictures = r.Pictures.Where(p => !string.IsNullOrEmpty(p)).Take(MaxPictures).ToList();
            var r2v = new JsonObject
            {
                ["clip"] = L(Clip),
                ["vae"] = L(Vae),
                ["audio_vae"] = L(AVae),
                ["prompt"] = r.Prompt,
                ["width"] = L(Size, 0),
                ["height"] = L(Size, 1),
                ["length"] = length,
                ["ref_image_size"] = FullSizeReferences(r, pictures.Count) ? "max" : "match",
            };
            for (var i = 0; i < pictures.Count; i++)
            {
                root[$"m_pic_{i}"] = Node("LoadImage", $"Martial: <Picture {i + 1}>", new JsonObject { ["image"] = pictures[i] });
                r2v[$"ref_images.ref_image_{i}"] = L($"m_pic_{i}");
            }
            root[R2V] = Node("MiniMaxH3ReferenceToVideo", "Martial: reference to video", r2v);

            // Guides: the first frame at 0, then the beat's keyframes, chained on the positive.
            JsonArray positive = L(R2V, 0);
            var guides = new List<(string Image, int Frame, string Title)>();
            if (!string.IsNullOrEmpty(r.FirstFrame)) guides.Add((r.FirstFrame!, 0, "first frame"));
            foreach (var k in r.Keyframes.Where(k => !string.IsNullOrEmpty(k.Image)))
                guides.Add((k.Image, Math.Clamp(k.Frame, 0, length - 1), $"keyframe @ {k.Frame}"));
            for (var i = 0; i < guides.Count; i++)
            {
                var (image, frame, title) = guides[i];
                root[$"m_guide_img_{i}"] = Node("LoadImage", $"Martial: {title}", new JsonObject { ["image"] = image });
                root[$"m_guide_scale_{i}"] = Node("ImageScale", $"Martial: {title}, to the canvas", new JsonObject
                {
                    ["image"] = L($"m_guide_img_{i}"),
                    ["upscale_method"] = "lanczos",
                    ["width"] = L(Size, 0),
                    ["height"] = L(Size, 1),
                    ["crop"] = "center",
                });
                root[$"m_guide_{i}"] = Node("MiniMaxH3AddGuide", $"Martial: AddGuide {title}", new JsonObject
                {
                    ["positive"] = positive,
                    ["latent"] = L(R2V, 1),
                    ["vae"] = L(Vae),
                    ["audio_vae"] = L(AVae),
                    ["image"] = L($"m_guide_scale_{i}"),
                    ["frame_idx"] = frame,
                });
                positive = L($"m_guide_{i}");
            }

            // Pass 1
            root[Zero] = Node("ConditioningZeroOut", "Martial: negative", new JsonObject { ["conditioning"] = positive.DeepClone() });
            root[Pass1] = Node("KSampler", "Martial: pass 1", new JsonObject
            {
                ["model"] = L(pass1Model),
                ["seed"] = r.Seed,
                ["steps"] = Math.Clamp(r.Steps, 1, 60),
                ["cfg"] = 1,
                ["sampler_name"] = "euler",
                ["scheduler"] = "simple",
                ["positive"] = positive.DeepClone(),
                ["negative"] = L(Zero),
                ["latent_image"] = L(R2V, 1),
                ["denoise"] = 1,
            });

            JsonArray final = L(Pass1);
            if (!r.Draft)
            {
                var (high, low) = RefineSigmas(r.RefineStrength, r.RefineSteps);
                root[Noise] = Node("RandomNoise", "Martial: refine noise", new JsonObject { ["noise_seed"] = r.Seed });
                root[Sampler] = Node("KSamplerSelect", "Martial: refine sampler", new JsonObject { ["sampler_name"] = "euler" });
                root[HighSigmas] = Node("ManualSigmas", "Martial: refine sigmas (before the upscale)", new JsonObject { ["sigmas"] = SigmaText(high) });
                root[LowSigmas] = Node("ManualSigmas", "Martial: refine sigmas (after the upscale)", new JsonObject { ["sigmas"] = SigmaText(low) });
                root[Guider2a] = Node("BasicGuider", "Martial: refine guider", new JsonObject { ["model"] = L(refineModel), ["conditioning"] = positive.DeepClone() });
                root[Refine1] = Node("SamplerCustomAdvanced", "Martial: refine at pass-1 size", new JsonObject
                {
                    ["noise"] = L(Noise), ["guider"] = L(Guider2a), ["sampler"] = L(Sampler), ["sigmas"] = L(HighSigmas), ["latent_image"] = L(Pass1),
                });
                root[Separate] = Node("LTXVSeparateAVLatent", "Martial: split video / audio", new JsonObject { ["av_latent"] = L(Refine1, 1) });
                root[Upscale] = Node("MinimaxH3LatentUpscaler3D", "Martial: 3D latent upscale", new JsonObject
                {
                    ["latent"] = L(Separate, 0),
                    ["model_name"] = Upscaler,
                    ["mode"] = "megapixels",
                    ["mode.megapixels"] = r.FinalMegapixels,
                    ["align"] = ResolutionMultiple,
                    ["enable_temporal_chunking"] = true,
                    ["force_unload"] = true,
                    ["device"] = "cuda",
                    ["precision"] = "bf16",
                });
                root[Concat] = Node("LTXVConcatAVLatent", "Martial: join video / audio", new JsonObject { ["video_latent"] = L(Upscale), ["audio_latent"] = L(Separate, 1) });
                // The keyframes were laid on the pass-1 latent; the upscaled pass reads the plain conditioning, as authored.
                root[Guider2b] = Node("BasicGuider", "Martial: finish guider", new JsonObject { ["model"] = L(refineModel), ["conditioning"] = L(R2V, 0) });
                root[Refine2] = Node("SamplerCustomAdvanced", "Martial: finish at the refined size", new JsonObject
                {
                    ["noise"] = L(Noise), ["guider"] = L(Guider2b), ["sampler"] = L(Sampler), ["sigmas"] = L(LowSigmas), ["latent_image"] = L(Concat),
                });
                final = L(Refine2, 0);
            }

            // Out
            root[Decode] = Node("VAEDecode", "Martial: decode", new JsonObject { ["samples"] = final.DeepClone(), ["vae"] = L(Vae) });
            root[DecodeAudio] = Node("VAEDecodeAudio", "Martial: decode audio", new JsonObject { ["samples"] = final.DeepClone(), ["vae"] = L(AVae) });
            var frames = L(Decode);
            if (r.RtxUpscale && !r.Draft)
            {
                root[Rtx] = Node("RTXVideoSuperResolution", "Martial: RTX upscale", new JsonObject
                {
                    ["images"] = L(Decode),
                    ["resize_type"] = "scale by multiplier",
                    ["resize_type.scale"] = Math.Clamp(r.RtxScale, 1.0, 4.0),
                    ["quality"] = "ULTRA",
                    ["strength"] = 1,
                    ["image_encoding"] = "8-bit RGB",
                });
                frames = L(Rtx);
            }
            root[Video] = Node("CreateVideo", "Martial: video", new JsonObject { ["images"] = frames, ["audio"] = L(DecodeAudio), ["fps"] = Fps });
            root[VideoSaveNode] = Node("SaveVideo", "Martial: save", new JsonObject
            {
                ["video"] = L(Video),
                ["filename_prefix"] = r.OutputPrefix,
                ["format"] = "mp4",
                ["format.codec"] = "auto",
            });
            // The frame the next beat starts on: before RTX, it is scaled to the canvas anyway.
            root[LastFrame] = Node("ImageFromBatch", "Martial: last frame", new JsonObject { ["image"] = L(Decode), ["batch_index"] = -1, ["length"] = 1 });
            root[LastFrameSaveNode] = Node("SaveImage", "Martial: save last frame", new JsonObject
            {
                ["images"] = L(LastFrame),
                ["filename_prefix"] = r.OutputPrefix + "_last",
            });
            return root;
        }

        /// <summary>
        /// Whether the references go in at full size (<c>ref_image_size: max</c>). The workflow's <c>match</c>
        /// scales them to the pass-1 canvas; the finish pass then runs on the 3D-upscaled latent against
        /// references sized for the smaller one. With one picture that holds, but with two or more (every beat
        /// after the first sends the opening image as <c>&lt;Picture 2&gt;</c>) the finish pass falls apart into
        /// a pattern of blocks — reproduced on the server, and gone with <c>max</c>, which the FFLF graph uses.
        /// So a refined render with more than one picture is always <c>max</c>; a draft (no upscale) keeps the
        /// tab's setting.
        /// </summary>
        public static bool FullSizeReferences(MartialBeatRender r, int pictureCount) =>
            r.MaxFidelityReferences || (!r.Draft && pictureCount > 1);

        private static bool SameLoras(IReadOnlyList<MartialLora> a, IReadOnlyList<MartialLora> b) =>
            a.Count == b.Count && a.Zip(b).All(p =>
                Norm(p.First.Name).Equals(Norm(p.Second.Name), StringComparison.OrdinalIgnoreCase) && Math.Abs(p.First.Strength - p.Second.Strength) < 1e-6);

        /// <summary>unet → LoRA stack → attention → live preview. Returns the last node's id.</summary>
        private static string ModelChain(JsonObject root, string unet, IReadOnlyList<MartialLora> loras, string attention, string prefix, string label)
        {
            var model = unet;
            var rows = loras.Where(l => !string.IsNullOrWhiteSpace(l.Name) && l.Strength != 0).ToList();
            if (rows.Count > 0)
            {
                var stack = new JsonObject { ["model"] = L(model), ["quantized_layers"] = "auto", ["adaln_port"] = "auto" };
                for (var i = 0; i < rows.Count; i++)
                    stack[$"lora_{i + 1}"] = new JsonObject { ["on"] = true, ["lora"] = Norm(rows[i].Name), ["strength"] = rows[i].Strength };
                root[prefix + "loras"] = Node("H3PowerLoraStack", $"Martial: LoRA stack ({label})", stack);
                model = prefix + "loras";
            }

            switch (attention)
            {
                case SageAttention:
                    root[prefix + "sage"] = Node("PathchSageAttentionKJ", $"Martial: Sage attention ({label})",
                        new JsonObject { ["model"] = L(model), ["sage_attention"] = "auto", ["allow_compile"] = true });
                    root[prefix + "sage_h3"] = Node("MiniMaxH3MemoryEfficientSageAttentionPatch", $"Martial: H3 Sage patch ({label})",
                        new JsonObject { ["model"] = L(prefix + "sage") });
                    model = prefix + "sage_h3";
                    break;
                case NoAttentionPatch:
                case "":
                case null:
                    break;
                default:
                    root[prefix + "attention"] = Node("ModelAttentionBackend", $"Martial: attention ({label})",
                        new JsonObject { ["model"] = L(model), ["attention"] = attention });
                    model = prefix + "attention";
                    break;
            }

            root[prefix + "preview"] = Node("ModelPreviewOverrideKJ", $"Martial: live preview ({label})", new JsonObject
            {
                ["model"] = L(model),
                ["max_resolution"] = 1024,
                ["jpeg_quality"] = 80,
                ["suppress_default_preview"] = true,
                ["preview_frames"] = 1,
                ["preview_fps"] = 12,
                ["tiny_vae"] = TinyVae,
            });
            return prefix + "preview";
        }
    }
}
