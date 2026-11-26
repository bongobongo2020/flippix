using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// The ⛓ FFLF tab's 🧾 character sheets: one image-edit pass per person in the opening image, turning that
    /// photo into a reference sheet of just them — front, three-quarter, side, back and a face close-up — on one
    /// wide canvas. Six engines:
    /// <list type="bullet">
    /// <item><b>Qwen Image 2.1 Edit</b> (default) — workflow/image/qwen-edit/image_qwen_image_2_1_image_edit-changephoto.json,
    /// with its custom-size switch on so the canvas is the sheet's, not the photo's, and the photo at a 2048 reference.</item>
    /// <item><b>Flux2 Klein 9B</b> and <b>Klein 9B True v2</b> — built here (4 steps, res_3m, the photo as a reference
    /// latent); no workflow file.</item>
    /// <item><b>FIBO 2</b> — workflow/image/fibo2API.json (edit turbo, 4 steps), the size set on its encoder.</item>
    /// <item><b>Qwen Image Edit 2511</b> — workflow/image/qwen-edit/Qwen_Edit_2511_INT8_Convrot_WF.json (8-step lightning).</item>
    /// <item><b>FireRed Image Edit 1.1</b> — workflow/firered-image-edit-1.1API.json (Q8 GGUF, 8-step lightning).</item>
    /// </list>
    /// </summary>
    public static class FflfSheetGraph
    {
        public const string EngineKlein = "klein";
        public const string EngineQwen21 = "qwen21";
        public const string EngineFibo2 = "fibo2";
        public const string EngineKleinTrue = "kleintrue";
        public const string EngineQwen2511 = "qwen2511";
        public const string EngineFireRed = "firered";

        public sealed record EngineOption(string Id, string Label);

        public static readonly IReadOnlyList<EngineOption> Engines = new[]
        {
            // Ranked by tools/fflf-bench (sheets.py / sheet_score.py, 2026-11-26): 7 people from 4 photos, 2 seeds,
            // ArcFace likeness of the sheet's best face to the person's face in the photo. Qwen 2.1 with a 2048
            // reference 0.73 (27 s; 0.60 at 1024), FireRed 0.51 (but drew the wrong person in 7 faces), Klein 0.49
            // (9 s), Qwen Edit 2511 0.46, Klein True v2 0.39, FIBO 2 0.38 — FIBO edits pixel for pixel, so it crops
            // the photo to the sheet's shape and keeps the photo's pose.
            new EngineOption(EngineQwen21, "Qwen Image 2.1 Edit (best likeness)"),
            new EngineOption(EngineFireRed, "FireRed Image Edit 1.1 (can mix people up)"),
            new EngineOption(EngineKlein, "Flux2 Klein 9B (fastest)"),
            new EngineOption(EngineQwen2511, "Qwen Image Edit 2511"),
            new EngineOption(EngineKleinTrue, "Flux2 Klein 9B True v2"),
            new EngineOption(EngineFibo2, "FIBO 2 (keeps the photo's pose — can mix people up)"),
        };

        public static string Normalize(string? engine) => Engines.Any(e => e.Id == engine) ? engine! : EngineQwen21;

        /// <summary>The workflow file each engine starts from, under the app folder; null for Klein (built here).</summary>
        public static string? WorkflowFile(string engine) => Normalize(engine) switch
        {
            EngineQwen21 => "workflow/image/qwen-edit/image_qwen_image_2_1_image_edit-changephoto.json",
            EngineFibo2 => "workflow/image/fibo2API.json",
            EngineQwen2511 => "workflow/image/qwen-edit/Qwen_Edit_2511_INT8_Convrot_WF.json",
            EngineFireRed => "workflow/firered-image-edit-1.1API.json",
            _ => null,
        };

        /// <summary>The SaveImage node the sheet is read from.</summary>
        public static string SaveNode(string engine) => Normalize(engine) switch
        {
            EngineQwen21 => "461",
            EngineFibo2 => "16",
            EngineQwen2511 => "60",
            EngineFireRed => "9",
            _ => "fflf_sheet_save",
        };

        /// <summary>A wide ~1.4 MP canvas — five panels of ~360×768 — inside what all three models were trained on.
        /// Multiples of 32.</summary>
        public const int Width = 1792;
        public const int Height = 768;

        public static readonly IReadOnlyList<string> Views = new[]
        {
            "full-body front view",
            "full-body three-quarter view",
            "full-body side profile",
            "full-body back view",
            "head-and-shoulders close-up of the face",
        };

        /// <summary>
        /// The edit instruction for one person of the opening image. Engine-neutral: no picture tags, which FIBO 2
        /// takes as "keep the original framing". With more than one person in the photo it names the one by place
        /// and says to leave the others out.
        /// </summary>
        public static string Prompt(FflfCastMember member, int castCount)
        {
            var who = castCount <= 1 ? "the person in the photo"
                : member.Position.Length > 0 ? $"only the person {FflfCast.Where(member.Position)} of the photo"
                : $"only person {member.Letter} of the photo";
            var look = member.Look.Trim().TrimEnd('.');
            var outfit = member.Outfit.Trim().TrimEnd('.');

            var s = $"Turn this photo into a character reference sheet of {who}" + (look.Length > 0 ? $" ({look})" : string.Empty) + ". " +
                    $"Show that one person {Views.Count} times side by side in a single row, evenly spaced, on a plain light grey studio background: " +
                    string.Join(", ", Views) + ". " +
                    "Keep exactly the same face, facial features, hair, skin tone and body build as in the photo in every view" +
                    (outfit.Length > 0 ? $", and exactly the same clothes: {outfit}. " : ", and exactly the same clothes. ") +
                    "Where the photo does not show part of the body or outfit, complete it in the same style and colours. " +
                    "Relaxed neutral standing pose, arms at the sides. Even soft studio lighting, sharp focus, photorealistic.";
            if (castCount > 1)
                s += $" Leave out the other {(castCount == 2 ? "person" : "people")} in the photo entirely — nobody else appears on the sheet.";
            return s + " No text, no labels, no borders.";
        }

        /// <summary>One sheet's API graph. <paramref name="workflowJson"/> is the engine's workflow file text (ignored
        /// for Klein); <paramref name="uploadedPhoto"/> the opening image's name on the server.</summary>
        public static JsonObject Build(string engine, string? workflowJson, string uploadedPhoto, string prompt, long seed,
                                       string filenamePrefix, int width = Width, int height = Height, int? referenceResolution = null)
        {
            seed = Math.Abs(seed % 999_999_999_999_999L);
            return Normalize(engine) switch
            {
                EngineQwen21 => BuildQwen21(Parse(workflowJson), uploadedPhoto, prompt, seed, filenamePrefix, width, height,
                                             referenceResolution ?? QwenReferenceResolution),
                EngineFibo2 => BuildFibo2(Parse(workflowJson), uploadedPhoto, prompt, seed, filenamePrefix, width, height),
                EngineQwen2511 => BuildQwen2511(Parse(workflowJson), uploadedPhoto, prompt, seed, filenamePrefix, width, height),
                EngineFireRed => BuildFireRed(Parse(workflowJson), uploadedPhoto, prompt, seed, filenamePrefix, width, height),
                EngineKleinTrue => BuildKlein(uploadedPhoto, prompt, seed, filenamePrefix, width, height, KleinTrueModel),
                _ => BuildKlein(uploadedPhoto, prompt, seed, filenamePrefix, width, height, KleinModel),
            };
        }

        public const string KleinModel = "flux-2-klein-9b-fp8.safetensors";
        public const string KleinTrueModel = "klein/Flux2-Klein-9B-True-v2-bf16.safetensors";
        public const string KleinTextEncoder = "qwen_3_8b.safetensors";
        public const string KleinVae = "flux2-vae.safetensors";

        private static JsonObject BuildKlein(string photo, string prompt, long seed, string prefix, int width, int height, string model) => new()
        {
            ["unet"] = Node("UNETLoader", new() { ["unet_name"] = model, ["weight_dtype"] = "default" }),
            ["clip"] = Node("CLIPLoader", new() { ["clip_name"] = KleinTextEncoder, ["type"] = "flux2", ["device"] = "default" }),
            ["vae"] = Node("VAELoader", new() { ["vae_name"] = KleinVae }),
            ["photo"] = Node("LoadImage", new() { ["image"] = photo }),
            ["scale"] = Node("ImageScaleToTotalPixels", new()
            {
                ["upscale_method"] = "lanczos", ["megapixels"] = 1.0, ["resolution_steps"] = 1, ["image"] = L("photo"),
            }),
            ["encode"] = Node("VAEEncode", new() { ["pixels"] = L("scale"), ["vae"] = L("vae") }),
            ["text"] = Node("CLIPTextEncode", new() { ["text"] = prompt, ["clip"] = L("clip") }),
            ["zero"] = Node("ConditioningZeroOut", new() { ["conditioning"] = L("text") }),
            ["pos"] = Node("ReferenceLatent", new() { ["conditioning"] = L("text"), ["latent"] = L("encode") }),
            ["neg"] = Node("ReferenceLatent", new() { ["conditioning"] = L("zero"), ["latent"] = L("encode") }),
            ["guider"] = Node("CFGGuider", new() { ["cfg"] = 1.0, ["model"] = L("unet"), ["positive"] = L("pos"), ["negative"] = L("neg") }),
            ["sampler"] = Node("KSamplerSelect", new() { ["sampler_name"] = "res_3m" }),
            ["sigmas"] = Node("Flux2Scheduler", new() { ["steps"] = 4, ["width"] = width, ["height"] = height }),
            ["noise"] = Node("RandomNoise", new() { ["noise_seed"] = seed }),
            ["latent"] = Node("EmptyFlux2LatentImage", new() { ["width"] = width, ["height"] = height, ["batch_size"] = 1 }),
            ["sample"] = Node("SamplerCustomAdvanced", new()
            {
                ["noise"] = L("noise"), ["guider"] = L("guider"), ["sampler"] = L("sampler"), ["sigmas"] = L("sigmas"), ["latent_image"] = L("latent"),
            }),
            ["decode"] = Node("VAEDecode", new() { ["samples"] = L("sample"), ["vae"] = L("vae") }),
            ["fflf_sheet_save"] = Node("SaveImage", new() { ["images"] = L("decode"), ["filename_prefix"] = prefix }),
        };

        /// <summary>Qwen 2.1's reference photo is resized to about this many pixels square (0 = its own size). Tested
        /// best-face likeness: 1024 → 0.60, 1536 → 0.71, 2048 → 0.73 (27 s a sheet; a 20 MP photo at its own size
        /// took ~10 minutes).</summary>
        public const int QwenReferenceResolution = 2048;

        private const string QwenNegative = "(worst quality, low quality:1.4), (low resolution, blurry, blur:1.2), jpeg artifacts, " +
            "poorly drawn, bad anatomy, wrong anatomy, deformed, mutated, disfigured, extra limbs, extra people, text, watermark, " +
            "signature, logo, (cartoon, anime, illustration, painting, drawing, sketch:1.3), 3d render, cgi, oversaturated, plastic skin";

        private static JsonObject BuildQwen21(JsonObject root, string photo, string prompt, long seed, string prefix, int width, int height,
                                              int referenceResolution)
        {
            Inputs(root, "470", "LoadImage")["image"] = photo;
            var enc = Inputs(root, "459:474", "TextEncodeQwenImage21");
            enc["prompt"] = prompt;
            enc["negative_prompt"] = QwenNegative;
            // The file's 0 sends the photo at its own size: a 20 MP photo took ~10 minutes a sheet.
            enc["resolution"] = referenceResolution;
            // The template's custom-size switch: on, the sampler starts from an empty latent of the sheet's size
            // instead of the photo's.
            Inputs(root, "459:468", "ComfySwitchNode")["switch"] = true;
            var empty = Inputs(root, "459:456", "EmptyLatentImage");
            empty["width"] = width;
            empty["height"] = height;
            var k = Inputs(root, "459:458", "KSampler");
            k["seed"] = seed;
            k["steps"] = 25;
            k["cfg"] = 3.5;
            Inputs(root, "461", "SaveImageAdvanced")["filename_prefix"] = prefix;
            // Nothing reads them on an API run; the compare widget only drags its stale preview paths along.
            root.Remove("472");
            root.Remove("13");
            return root;
        }

        private static JsonObject BuildFibo2(JsonObject root, string photo, string prompt, long seed, string prefix, int width, int height)
        {
            Inputs(root, "38", "LoadImage")["image"] = photo;
            Inputs(root, "49", "PrimitiveStringMultiline")["value"] = prompt;
            var enc = Inputs(root, "4", "TextEncodeFibo2");
            enc["width"] = width;
            enc["height"] = height;
            Inputs(root, "35", "KSampler")["seed"] = seed;
            // The file ends in a PreviewImage: a SaveImage in its place so the sheet lands in output/.
            root["16"] = Node("SaveImage", new() { ["images"] = L("7"), ["filename_prefix"] = prefix });
            return root;
        }

        /// <summary>Qwen Image Edit 2511 with its 8-step lightning LoRA. The file samples on the photo's own latent and
        /// upscales with RTX super-resolution: here it starts from an empty latent of the sheet's size instead, and saves
        /// the decode as it is.</summary>
        private static JsonObject BuildQwen2511(JsonObject root, string photo, string prompt, long seed, string prefix, int width, int height)
        {
            Inputs(root, "78", "LoadImage")["image"] = photo;
            Inputs(root, "115:111", "TextEncodeQwenImageEditPlus")["prompt"] = prompt;
            var empty = Inputs(root, "115:112", "EmptySD3LatentImage");
            empty["width"] = width;
            empty["height"] = height;
            var k = Inputs(root, "115:3", "KSampler");
            k["seed"] = seed;
            k["latent_image"] = L("115:112");
            var save = Inputs(root, "60", "SaveImage");
            save["images"] = L("115:8");
            save["filename_prefix"] = prefix;
            root.Remove("115:116");
            root.Remove("115:124");
            return root;
        }

        /// <summary>FireRed Image Edit 1.1 (Q8 GGUF) with its 8-step lightning LoRA, on an empty latent of the sheet's size
        /// rather than the photo's.</summary>
        private static JsonObject BuildFireRed(JsonObject root, string photo, string prompt, long seed, string prefix, int width, int height)
        {
            Inputs(root, "143", "LoadImage")["image"] = photo;
            Inputs(root, "118", "TextEncodeQwenImageEditPlus")["prompt"] = prompt;
            Inputs(root, "153", "PrimitiveBoolean")["value"] = true;
            root["fflf_sheet_latent"] = Node("EmptySD3LatentImage", new() { ["width"] = width, ["height"] = height, ["batch_size"] = 1 });
            var k = Inputs(root, "130", "KSampler");
            k["seed"] = seed;
            k["latent_image"] = L("fflf_sheet_latent");
            Inputs(root, "9", "SaveImage")["filename_prefix"] = prefix;
            return root;
        }

        private static JsonObject Parse(string? json) =>
            JsonNode.Parse(json ?? throw new InvalidOperationException("The sheet workflow file is missing."))?.AsObject()
            ?? throw new InvalidOperationException("The sheet workflow could not be parsed.");

        private static JsonObject Inputs(JsonObject root, string id, string cls)
        {
            if (root[id]?["class_type"]?.GetValue<string>() != cls || root[id]?["inputs"] is not JsonObject inputs)
                throw new InvalidOperationException($"Workflow node '{id}' is not {cls} — the sheet workflow no longer matches the FFLF tab.");
            return inputs;
        }

        private static JsonObject Node(string cls, JsonObject inputs) => new() { ["class_type"] = cls, ["inputs"] = inputs };
        private static JsonArray L(string id, int slot = 0) => new() { id, slot };
    }
}
