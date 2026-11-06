using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Builds a <b>character sheet</b> for one cast member with MiniMax H3 itself, rendered as a true single-frame
    /// still by the Fizgig H3 Still pack (github.com/shootthesound/ComfyUI-Fizgig-H3-Still).
    ///
    /// <para>The cast photo goes in as <c>&lt;Picture 1&gt;</c> of <c>MiniMaxH3ReferenceToVideo</c> (the pack's
    /// edit wiring), the sampler runs on <c>FizgigH3StillLatent</c> instead of the 5-frame video latent, and
    /// <c>FizgigH3StillDecode</c> decodes it without the banding the stock VAE Decode gives a lone frame. The
    /// sheet is drawn by the same model that renders the clips, from the same photo, so the identity the clips
    /// are conditioned on is one H3 already reproduced from every side.</para>
    ///
    /// <para>Defaults are the pack's tested still settings: fl2va pruned model, larryvrh Turbo v4 step-600 EMA
    /// LoRA at 0.38, 20 steps, <c>er_sde</c>/<c>simple</c>, and a canvas of 3 MP or more (smaller stills come
    /// out noticeably weaker).</para>
    /// </summary>
    public static class H3StillSheetGraph
    {
        public const string NodeLatent = "FizgigH3StillLatent";
        public const string NodeDecode = "FizgigH3StillDecode";

        public const string DefaultModel = "h3-minimax/minimax_h3_fl2va_pruned_int8_convrot.safetensors";
        public const string DefaultLora = "H3/larryminimax_h3_turbo_v4_step600_ema.safetensors";
        public const double DefaultLoraStrength = 0.38;
        public const string DefaultTextEncoder = "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors";
        public const string DefaultVideoVae = "minimax_h3_video_vae_int8_convrot.safetensors";

        /// <summary>A wide 3.1 MP canvas: five full-body panels at ~540×1152 each. Both multiples of 32.</summary>
        public const int DefaultWidth = 2688;
        public const int DefaultHeight = 1152;
        public const int DefaultSteps = 20;

        public const string SaveNode = "save";

        /// <summary>The views the sheet lays out, left to right — also what the prompt asks for.</summary>
        public static readonly IReadOnlyList<string> Views = new[]
        {
            "full-body front view",
            "full-body three-quarter view",
            "full-body side profile",
            "full-body back view",
            "head-and-shoulders face close-up",
        };

        public sealed record Models(string Model, string? Lora, double LoraStrength, string TextEncoder, string VideoVae);

        public static Models DefaultModels => new(DefaultModel, DefaultLora, DefaultLoraStrength, DefaultTextEncoder, DefaultVideoVae);

        /// <summary>Picks the installed file for each loader, falling back to a close match, then to the default name.</summary>
        public static Models Resolve(IReadOnlyList<string> unets, IReadOnlyList<string> loras,
                                     IReadOnlyList<string> clips, IReadOnlyList<string> vaes)
        {
            var model = CharacterSwapGraph.ResolveFile(DefaultModel, unets,
                @"minimax_h3_fl2va_pruned", @"minimax_h3_ref2va_pruned", @"minimax_h3_.*fl2va");
            string? lora = CharacterSwapGraph.ResolveFile(DefaultLora, loras,
                @"minimax_h3_turbo_v4_step600_ema", @"minimax_h3_turbo_v4_step600");
            // Loras known but none of them the turbo: render without it (the pack's no-Turbo mode needs more steps).
            if (loras.Count > 0 && !loras.Contains(lora)) lora = null;
            var te = CharacterSwapGraph.ResolveFile(DefaultTextEncoder, clips, @"qwen3vl_32b_minimax_h3");
            var vae = CharacterSwapGraph.ResolveFile(DefaultVideoVae, vaes, @"minimax_h3_video_vae_int8", @"minimax_h3_video_vae");
            return new Models(model, lora, DefaultLoraStrength, te, vae);
        }

        /// <summary>The sheet prompt. <paramref name="look"/>/<paramref name="outfit"/> are the story bible's words
        /// for this character; the photo carries the identity, the words keep the outfit locked to the story's.</summary>
        public static string BuildPrompt(string noun, string look, string outfit)
        {
            var sb = new StringBuilder();
            sb.Append("A professional character reference sheet of the person in <Picture 1>");
            if (!string.IsNullOrWhiteSpace(noun)) sb.Append($", {noun.Trim()}");
            if (!string.IsNullOrWhiteSpace(look)) sb.Append($", {look.Trim().TrimEnd('.')}");
            if (!string.IsNullOrWhiteSpace(outfit)) sb.Append($", wearing {outfit.Trim().TrimEnd('.')}");
            sb.Append(". The same single individual is shown ").Append(Views.Count)
              .Append(" times side by side in one row, evenly spaced, on a plain light grey studio background: ")
              .Append(string.Join(", ", Views)).Append(". ");
            sb.Append("Keep the identity exactly the same as <Picture 1> in every view: identical face, facial features, " +
                      "hair, skin tone, body build and clothing. Relaxed neutral standing pose with arms at the sides, " +
                      "the whole outfit visible from head to shoes in every full-body view. Even soft studio lighting, " +
                      "sharp focus, fine skin texture, photorealistic. No text, no labels, no borders.");
            return sb.ToString();
        }

        /// <summary>
        /// The API-format graph. <paramref name="uploadedPhoto"/> is the server-side name returned by the upload.
        /// </summary>
        public static JsonObject Build(Models m, string uploadedPhoto, string prompt, long seed, string filenamePrefix,
                                       int width = DefaultWidth, int height = DefaultHeight, int steps = DefaultSteps)
        {
            width = Math.Max(64, width / 32 * 32);
            height = Math.Max(64, height / 32 * 32);

            var g = new JsonObject
            {
                ["unet"] = Node("UNETLoader", new JsonObject { ["unet_name"] = m.Model, ["weight_dtype"] = "default" }),
                ["clip"] = Node("CLIPLoader", new JsonObject { ["clip_name"] = m.TextEncoder, ["type"] = "minimax", ["device"] = "default" }),
                ["vae"] = Node("VAELoader", new JsonObject { ["vae_name"] = m.VideoVae }),
                ["img1"] = Node("LoadImage", new JsonObject { ["image"] = uploadedPhoto }),
                ["ref"] = Node("MiniMaxH3ReferenceToVideo", new JsonObject
                {
                    ["prompt"] = prompt,
                    ["width"] = width,
                    ["height"] = height,
                    ["length"] = 5,              // conditioning only; the latent comes from the Fizgig node
                    ["ref_image_size"] = "match",
                    ["clip"] = new JsonArray("clip", 0),
                    ["vae"] = new JsonArray("vae", 0),
                    ["ref_images.ref_image_0"] = new JsonArray("img1", 0),
                }),
                ["latent"] = Node(NodeLatent, new JsonObject { ["width"] = width, ["height"] = height, ["batch_size"] = 1 }),
                ["sampler"] = Node("KSamplerSelect", new JsonObject { ["sampler_name"] = "er_sde" }),
                ["noise"] = Node("RandomNoise", new JsonObject { ["noise_seed"] = seed }),
                ["sample"] = Node("SamplerCustomAdvanced", new JsonObject
                {
                    ["noise"] = new JsonArray("noise", 0),
                    ["guider"] = new JsonArray("guider", 0),
                    ["sampler"] = new JsonArray("sampler", 0),
                    ["sigmas"] = new JsonArray("sched", 0),
                    ["latent_image"] = new JsonArray("latent", 0),
                }),
                ["decode"] = Node(NodeDecode, new JsonObject { ["samples"] = new JsonArray("sample", 0), ["vae"] = new JsonArray("vae", 0) }),
                [SaveNode] = Node("SaveImage", new JsonObject { ["images"] = new JsonArray("decode", 0), ["filename_prefix"] = filenamePrefix }),
            };

            var modelOut = "unet";
            if (!string.IsNullOrEmpty(m.Lora) && m.LoraStrength > 0)
            {
                g["lora"] = Node("LoraLoaderModelOnly", new JsonObject
                {
                    ["lora_name"] = m.Lora.Replace('\\', '/'),
                    ["strength_model"] = m.LoraStrength,
                    ["model"] = new JsonArray("unet", 0),
                });
                modelOut = "lora";
            }
            else
            {
                steps = Math.Max(steps, 50);     // the pack's no-Turbo setting
            }

            g["guider"] = Node("BasicGuider", new JsonObject { ["model"] = new JsonArray(modelOut, 0), ["conditioning"] = new JsonArray("ref", 0) });
            g["sched"] = Node("BasicScheduler", new JsonObject
            {
                ["scheduler"] = "simple", ["steps"] = steps, ["denoise"] = 1.0, ["model"] = new JsonArray(modelOut, 0),
            });
            return g;
        }

        private static JsonObject Node(string cls, JsonObject inputs) => new() { ["class_type"] = cls, ["inputs"] = inputs };
    }
}
