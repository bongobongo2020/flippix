using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace FlipPix.UI.Services
{
    /// <summary>The model files the PikaFlex + ReDiverse graph loads, as the server names them.</summary>
    public sealed record PikaFlexFiles
    {
        public string DiffusionModel { get; init; } = PikaFlexGraph.AuthoredModel;
        public string PikaLora { get; init; } = PikaFlexGraph.AuthoredPikaLora;
        public string RediverseLora { get; init; } = PikaFlexGraph.AuthoredRediverseLora;
        public double PikaStrength { get; init; } = 1.0;
        public double RediverseStrength { get; init; } = 1.0;
    }

    /// <summary>
    /// The ⛓ FFLF tab's second beat workflow: "PikaFlex-ReDiverse-Comfy" (checked in as
    /// workflow/video/h3-minimax/pikaflex-rediverse.json) rebuilt as an API graph, with what the tab needs to chain beats.
    ///
    /// <para><b>As authored.</b> The fl2va model → PikaFlex LoRA 1.0 → comfy kitchen attention. The prompt, the canvas
    /// (ResolutionSelector, 0.6 MP, multiple of 32) and the length go into MiniMaxH3ImageToVideo. One 6-step LCM
    /// schedule (1.0 · 0.973 · 0.923 · 0.8 · 0.571 · 0.333 · 0) is split after its first step: that step runs with the
    /// ReDiverse LoRA stacked on top (it sets the composition and motion), the other five on the PikaFlex wire alone,
    /// continuing the same latent with the same seed. 24 fps.</para>
    ///
    /// <para><b>Added.</b> The frame the beat starts on goes in as the node's <c>first_frame</c> — the video starts on
    /// it. The tab's extra LoRAs ride after PikaFlex; the output is CreateVideo → SaveVideo instead of VHS, the last frame
    /// is saved for the next beat, and a beat with dialogue has its soundtrack locked to it on both passes. The node
    /// takes no reference pictures, so the opening image and character sheets are not sent.</para>
    /// </summary>
    public static class PikaFlexGraph
    {
        public const string Key = "pikaflex";
        public const int Fps = 24;
        public const int ResolutionMultiple = 32;

        public const string AuthoredModel = "h3-minimax/minimax_h3_fl2va_pruned_int8_convrot.safetensors";
        public const string AuthoredPikaLora = "pika_h3_flex_fl2va_v1_bf16.safetensors";
        public const string AuthoredRediverseLora = "rediverse_ft_comfyui.safetensors";
        public const string VideoVae = "minimax_h3_video_vae_fp16.safetensors";
        public const string AudioVae = "minimax_h3_audio_vae_fp32.safetensors";
        public const string TinyVae = "taeh3.safetensors";
        public const string Attention = "comfy kitchen attention";
        public const string SamplerName = "lcm";
        public static readonly double[] Sigmas = { 1.0, 0.972973, 0.923077, 0.8, 0.571429, 0.333333, 0.0 };
        /// <summary>Steps of <see cref="Sigmas"/> run with ReDiverse on.</summary>
        public const int RediverseSteps = 1;

        public const string VideoSaveNode = "pf_save";
        public const string LastFrameSaveNode = "pf_last_save";

        private const string Unet = "pf_unet", Pika = "pf_pika", Attn = "pf_attn", Rediverse = "pf_rediverse";
        private const string Preview1 = "pf_preview1", Preview2 = "pf_preview2";
        private const string Clip = "pf_clip", Vae = "pf_vae", AVae = "pf_avae", Size = "pf_size", Start = "pf_start", I2V = "pf_i2v";
        private const string Sampler = "pf_sampler", Noise = "pf_noise", AllSigmas = "pf_sigmas", Split = "pf_split";
        private const string Guider1 = "pf_guider1", Pass1 = "pf_pass1", Guider2 = "pf_guider2", Pass2 = "pf_pass2";
        private const string Decode = "pf_decode", DecodeAudio = "pf_decode_audio", Video = "pf_video", LastFrame = "pf_last";
        public const string DialogueLockPass1Node = "pf_dlg_lock1", DialogueLockPass2Node = "pf_dlg_lock2";

        /// <summary>The H3 frame count for a beat: seconds × 24 snapped up to 17n+5 (5 s → 124, as authored).</summary>
        public static int FrameCount(double seconds) => MartialGraph.FrameCount(seconds);

        /// <summary>What each authored file may be called on a server: the same weights are published under other
        /// names (pika_h3_flex_fl2va_comfy_bf16 for the v1 file), so a file name that matches is taken when the exact
        /// one is not there.</summary>
        private static readonly Dictionary<string, string> Families = new(StringComparer.OrdinalIgnoreCase)
        {
            [AuthoredModel] = @"^minimax_h3_fl2va.*\.safetensors$",
            [AuthoredPikaLora] = @"^pika_h3_flex.*fl2va.*\.safetensors$",
            [AuthoredRediverseLora] = @"^rediverse.*\.safetensors$",
        };

        /// <summary>A server file for an authored one: the same file name in whatever folder the server keeps it;
        /// failing that, the closest of its family (see <see cref="Families"/>; int8 / bf16 preferred as authored);
        /// failing that, the authored name.</summary>
        public static string Resolve(IEnumerable<string> serverFiles, string authored)
        {
            var files = serverFiles.Select(f => f.Replace('\\', '/')).ToList();
            var name = System.IO.Path.GetFileName(authored.Replace('\\', '/'));
            var exact = files.FirstOrDefault(f => System.IO.Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            if (!Families.TryGetValue(authored, out var pattern)) return authored;

            var family = files.Where(f => System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(f), pattern,
                                     System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
            if (family.Count == 0) return authored;
            // Prefer files that share more of the authored name's parts (pruned, int8, convrot, bf16 …), then the shortest name.
            var parts = name.ToLowerInvariant().Replace(".safetensors", string.Empty).Split('_', '-', '.');
            return family
                .OrderByDescending(f => parts.Count(p => System.IO.Path.GetFileName(f).ToLowerInvariant().Contains(p)))
                .ThenBy(f => f.Length)
                .First();
        }

        private static JsonObject Node(string cls, string title, JsonObject inputs) => new()
        {
            ["class_type"] = cls,
            ["_meta"] = new JsonObject { ["title"] = title },
            ["inputs"] = inputs,
        };

        private static JsonArray L(string id, int slot = 0) => new() { id, slot };

        private static string Norm(string name) => (name ?? string.Empty).Trim().Replace('\\', '/');

        /// <summary>One beat's API graph. <paramref name="megapixels"/> is the single canvas the beat renders at.</summary>
        public static JsonObject Build(FflfBeatRender r, PikaFlexFiles files, double megapixels)
        {
            if (string.IsNullOrWhiteSpace(r.StartImage)) throw new InvalidOperationException("The beat has no frame to start on.");
            var root = new JsonObject();
            var length = FrameCount(r.Seconds);

            // Model wires: PikaFlex (+ the tab's extra LoRAs) → attention; ReDiverse on top for the first step.
            root[Unet] = Node("UNETLoader", "PikaFlex: diffusion model", new JsonObject { ["unet_name"] = Norm(files.DiffusionModel), ["weight_dtype"] = "default" });
            root[Pika] = Node("LoraLoaderModelOnly", "PikaFlex: Pika flex LoRA", new JsonObject
            {
                ["model"] = L(Unet), ["lora_name"] = Norm(files.PikaLora), ["strength_model"] = files.PikaStrength,
            });
            var model = Pika;
            var extras = r.ExtraLoras.Where(l => !string.IsNullOrWhiteSpace(l.Name) && l.Strength > 0).ToList();
            for (var i = 0; i < extras.Count; i++)
            {
                var id = $"pf_extra_{i + 1}";
                root[id] = Node("LoraLoaderModelOnly", $"PikaFlex: extra LoRA {i + 1}", new JsonObject
                {
                    ["model"] = L(model), ["lora_name"] = Norm(extras[i].Name), ["strength_model"] = extras[i].Strength,
                });
                model = id;
            }
            root[Attn] = Node("ModelAttentionBackend", "PikaFlex: attention", new JsonObject { ["model"] = L(model), ["attention"] = Attention });
            root[Rediverse] = Node("LoraLoaderModelOnly", "PikaFlex: ReDiverse LoRA (first step)", new JsonObject
            {
                ["model"] = L(Attn), ["lora_name"] = Norm(files.RediverseLora), ["strength_model"] = files.RediverseStrength,
            });
            root[Preview1] = Preview(Rediverse, length, "first step");
            root[Preview2] = Preview(Attn, length, "remaining steps");

            // Loaders, canvas, conditioning.
            root[Clip] = Node("CLIPLoader", "PikaFlex: text encoder", new JsonObject
            {
                ["clip_name"] = Norm(string.IsNullOrWhiteSpace(r.TextEncoder) ? FlipPix.Core.Models.H3FflfSettings.DefaultTextEncoder : r.TextEncoder),
                ["type"] = "minimax",
                ["device"] = "default",
            });
            root[Vae] = Node("VAELoader", "PikaFlex: video VAE", new JsonObject { ["vae_name"] = VideoVae });
            root[AVae] = Node("VAELoader", "PikaFlex: audio VAE", new JsonObject { ["vae_name"] = AudioVae });
            root[Size] = Node("ResolutionSelector", "PikaFlex: canvas", new JsonObject
            {
                ["aspect_ratio"] = r.AspectRatio,
                ["megapixels"] = megapixels,
                ["multiple"] = ResolutionMultiple,
            });
            root[Start] = Node("LoadImage", "PikaFlex: first frame", new JsonObject { ["image"] = r.StartImage });
            root[I2V] = Node("MiniMaxH3ImageToVideo", "PikaFlex: image to video", new JsonObject
            {
                ["clip"] = L(Clip),
                ["vae"] = L(Vae),
                ["prompt"] = r.Prompt,
                ["width"] = L(Size, 0),
                ["height"] = L(Size, 1),
                ["length"] = length,
                ["first_frame"] = L(Start),
            });

            // Sampling: one LCM schedule, split after the ReDiverse step.
            root[Sampler] = Node("KSamplerSelect", "PikaFlex: sampler", new JsonObject { ["sampler_name"] = SamplerName });
            root[Noise] = Node("RandomNoise", "PikaFlex: noise", new JsonObject { ["noise_seed"] = r.Seed });
            root[AllSigmas] = Node("ManualSigmas", "PikaFlex: sigmas", new JsonObject
            {
                ["sigmas"] = string.Join(", ", Sigmas.Select(s => s.ToString("0.######", CultureInfo.InvariantCulture))),
            });
            root[Split] = Node("SplitSigmas", "PikaFlex: split after the ReDiverse step", new JsonObject { ["sigmas"] = L(AllSigmas), ["step"] = RediverseSteps });
            root[Guider1] = Node("BasicGuider", "PikaFlex: guider (ReDiverse)", new JsonObject { ["model"] = L(Preview1), ["conditioning"] = L(I2V, 0) });
            root[Pass1] = Node("SamplerCustomAdvanced", "PikaFlex: first step", new JsonObject
            {
                ["noise"] = L(Noise), ["guider"] = L(Guider1), ["sampler"] = L(Sampler), ["sigmas"] = L(Split, 0), ["latent_image"] = L(I2V, 1),
            });
            root[Guider2] = Node("BasicGuider", "PikaFlex: guider", new JsonObject { ["model"] = L(Preview2), ["conditioning"] = L(I2V, 0) });
            root[Pass2] = Node("SamplerCustomAdvanced", "PikaFlex: remaining steps", new JsonObject
            {
                ["noise"] = L(Noise), ["guider"] = L(Guider2), ["sampler"] = L(Sampler), ["sigmas"] = L(Split, 1), ["latent_image"] = L(Pass1, 0),
            });

            // Out.
            root[Decode] = Node("VAEDecode", "PikaFlex: decode", new JsonObject { ["samples"] = L(Pass2, 0), ["vae"] = L(Vae) });
            root[DecodeAudio] = Node("VAEDecodeAudio", "PikaFlex: decode audio", new JsonObject { ["samples"] = L(Pass2, 0), ["vae"] = L(AVae) });
            root[Video] = Node("CreateVideo", "PikaFlex: video", new JsonObject { ["images"] = L(Decode), ["audio"] = L(DecodeAudio), ["fps"] = Fps });
            root[VideoSaveNode] = Node("SaveVideo", "PikaFlex: save", new JsonObject
            {
                ["video"] = L(Video),
                ["filename_prefix"] = r.OutputPrefix,
                ["format"] = "mp4",
                ["format.codec"] = "auto",
            });
            root[LastFrame] = Node("ImageFromBatch", "PikaFlex: last frame", new JsonObject { ["image"] = L(Decode), ["batch_index"] = -1, ["length"] = 1 });
            root[LastFrameSaveNode] = Node("SaveImage", "PikaFlex: save last frame", new JsonObject
            {
                ["images"] = L(LastFrame),
                ["filename_prefix"] = r.OutputPrefix + "_last",
            });

            if (!string.IsNullOrEmpty(r.DialogueAudio)) AddDialogueLock(root, r.DialogueAudio!, length);
            return root;
        }

        private static JsonObject Preview(string model, int length, string label) =>
            Node("ModelPreviewOverrideKJ", $"PikaFlex: live preview ({label})", new JsonObject
            {
                ["model"] = L(model),
                ["preview_frames"] = length,
                ["max_resolution"] = 1024,
                ["jpeg_quality"] = 80,
                ["suppress_default_preview"] = true,
                ["preview_fps"] = 12,
                ["tiny_vae"] = TinyVae,
            });

        /// <summary>
        /// Locks the soundtrack to the beat's dialogue, as <see cref="FflfGraph.AddDialogueLock"/> does for the 2-Pass
        /// graph: the line padded with silence and cut to the clip, encoded into the audio half of the latent and held
        /// clean on both samplers, so only the picture is drawn — attending to the words. The export takes the exact audio.
        /// </summary>
        public static void AddDialogueLock(JsonObject root, string audio, int length)
        {
            var seconds = Math.Round(length / (double)Fps, 4);
            root["pf_dlg_load"] = Node("LoadAudio", "PikaFlex: dialogue", new JsonObject { ["audio"] = audio });
            root["pf_dlg_silence"] = Node("EmptyAudio", "PikaFlex: silence to pad the dialogue",
                new JsonObject { ["duration"] = seconds, ["sample_rate"] = 32000, ["channels"] = 2 });
            root["pf_dlg_pad"] = Node("AudioConcat", "PikaFlex: dialogue + silence",
                new JsonObject { ["audio1"] = L("pf_dlg_load"), ["audio2"] = L("pf_dlg_silence"), ["direction"] = "after" });
            root["pf_dlg_fit"] = Node("TrimAudioDuration", "PikaFlex: dialogue fitted to the clip",
                new JsonObject { ["audio"] = L("pf_dlg_pad"), ["start_index"] = 0, ["duration"] = seconds });

            root[DialogueLockPass1Node] = Node("MiniMaxH3NativeAudioLock", "PikaFlex: dialogue lock, first step", new JsonObject
            {
                ["model"] = L(Preview1), ["av_latent"] = L(I2V, 1), ["audio_vae"] = L(AVae), ["audio"] = L("pf_dlg_fit"),
            });
            root[Guider1]!["inputs"]!["model"] = L(DialogueLockPass1Node, 0);
            root[Pass1]!["inputs"]!["latent_image"] = L(DialogueLockPass1Node, 1);

            root[DialogueLockPass2Node] = Node("MiniMaxH3NativeAudioLock", "PikaFlex: dialogue lock, remaining steps", new JsonObject
            {
                ["model"] = L(Preview2), ["av_latent"] = L(Pass1, 0), ["audio_vae"] = L(AVae), ["audio"] = L("pf_dlg_fit"),
            });
            root[Guider2]!["inputs"]!["model"] = L(DialogueLockPass2Node, 0);
            root[Pass2]!["inputs"]!["latent_image"] = L(DialogueLockPass2Node, 1);

            root[Video]!["inputs"]!["audio"] = L(DialogueLockPass1Node, 2);
        }
    }
}
