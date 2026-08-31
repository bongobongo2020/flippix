using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.ComfyUI.Services;
using FlipPix.UI.Services;
using YamlDotNet.Serialization;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// The machinery two H3 cast tabs share: reading a story's characters out of the llama-server,
    /// and rendering a character's photo with one of the Image Generator tab's base text-to-image
    /// workflows. Kept in one place because the 🪪👥 H3 Cast and 🎬🎭 H3 Ensemble tabs want exactly
    /// the same behaviour — a story names the cast and the ✨ Generate button photographs it — with
    /// only the slot bookkeeping differing between them.
    /// </summary>
    public static class CastPhotoWorkflows
    {
        /// <summary>
        /// One entry of the ✨ Generate menu's LoRA list. <see cref="Reference"/> is the path as
        /// ComfyUI's lora_name wants it ("zimage/Foo.safetensors"); null is the workflow's own LoRA,
        /// exactly as the file ships it.
        /// </summary>
        public sealed record CastLora(string Name, string? Reference)
        {
            public static readonly CastLora AsAuthored = new("(as authored — the workflow's own)", null);
            public bool IsDefault => Reference == null;
        }
        // ── Node ids of the three Image Generator base graphs ──────────────────────────────────
        // z-image-base.json — run as authored except for these three inputs.
        private const string ZBasePromptNode = "76:67";   // CLIPTextEncode
        private const string ZBaseSamplerNode = "76:69";  // KSampler (seed only — steps/cfg stay as authored)
        private const string ZBaseSaveNode = "9";         // SaveImage
        private const string ZBaseLoraNode = "76:96";     // LoraLoaderModelOnly (lora_name, when a LoRA is picked)

        // krea2RealismV1_krea2RealismV1WF.json — turbo sampler, its LoRA stays as authored.
        private const string Krea2PromptNode = "6";       // CLIPTextEncode
        private const string Krea2SamplerNode = "27";     // ClownsharKSampler_Beta (seed only)
        private const string Krea2LatentNode = "10";      // EmptyLatentImage
        private const string Krea2LoraNode = "17";        // Power Lora Loader (rgthree) — lora_1 slot
        private const string Krea2PreviewNode = "5";      // PreviewImage — dropped
        private const string Krea2RtxNode = "28";         // RTXVideoSuperResolution
        private const string Krea2SaveNode = "23";        // SaveImageKJ — replaced with a real SaveImage

        // Qwen_Image_2512_INT8_Convrot_WF.json — lightning steps are baked in by the workflow.
        private const string QwenImgPromptNode = "108";   // CLIPTextEncode
        private const string QwenImgSamplerNode = "106";  // KSampler (seed only)
        private const string QwenImgLatentNode = "107";   // EmptySD3LatentImage
        private const string QwenImgSaveNode = "123";     // SaveImage

        // Zimage-Famegrid.json — the Z-Image "igmodel" look: a 20-step base pass, a 0.3-denoise refine
        // pass, the famegrid spice LoRA on both, a Laplacian sharpen between the passes and a 4× photo
        // upscale on the way out.
        private const string FamegridPromptNode = "48";     // PrimitiveStringMultiline → CLIPTextEncode 6
        private const string FamegridSamplerNode = "265";   // ClownsharKSampler_Beta — the base pass
        private const string FamegridRefineNode = "344";    // ClownsharKSampler_Beta — the refine pass
        private const string FamegridSaveNode = "213";      // SaveImage
        // The workflow's own character-LoRA slot ("YOUR CHARACTER LORA HERE OR BYPASS"): one easy
        // loraNames feeding a loader on each model chain. As authored the slot is bypassed — both
        // shift nodes read the spice chain around it — and a picked LoRA puts it back by pointing the
        // shifts at the loaders again.
        private const string FamegridCharLoraNode = "364";  // easy loraNames
        private const string FamegridBaseShiftNode = "316"; // ModelSamplingAuraFlow — end of the base chain
        private const string FamegridRefineShiftNode = "348";// ModelSamplingAuraFlow — end of the refine chain
        private const string FamegridCharLoraBase = "362";  // LoraLoaderModelOnly — the base chain's slot
        private const string FamegridCharLoraRefine = "363";// LoraLoaderModelOnly — the refine chain's slot

        /// <summary>
        /// Loads the chosen Image Generator base graph and patches it for one portrait: the prompt,
        /// a fresh seed, a portrait canvas where the graph takes one, and a save prefix the caller
        /// can find again. Everything else is left exactly as the Image Generator tab ships it.
        /// </summary>
        /// <param name="engine">"zimage", "famegrid", "krea2" or "qwen".</param>
        /// <param name="prefix">SaveImage filename_prefix — an output-subfolder path ending in a
        /// unique run token, so the caller's disk scan can find the file.</param>
        /// <param name="lora">A LoRA picked from the ✨ menu, or null for the workflow's own. Qwen
        /// ignores it — its lightning LoRA is baked in by the workflow.</param>
        public static async Task<(string Json, string SaveNode)> BuildAsync(
            string engine, string prefix, long seed, string prompt, Action<string> log, CastLora? lora = null)
        {
            switch (engine)
            {
                case "krea2":
                {
                    var json = await ReadWorkflowAsync("workflow/image/krea/krea2RealismV1_krea2RealismV1WF.json");
                    var root = ParseGraph(json);
                    RequireClass(root, Krea2PromptNode, "CLIPTextEncode");
                    RequireClass(root, Krea2SamplerNode, "ClownsharKSampler_Beta");
                    RequireClass(root, Krea2LatentNode, "EmptyLatentImage");
                    RequireClass(root, Krea2RtxNode, "RTXVideoSuperResolution");
                    if (lora != null) RequireClass(root, Krea2LoraNode, "Power Lora Loader");
                    json = root.ToJsonString();

                    SetInput(ref json, Krea2PromptNode, "text", prompt);
                    SetInput(ref json, Krea2SamplerNode, "seed", seed);
                    SetInput(ref json, Krea2LatentNode, "width", 1024);
                    SetInput(ref json, Krea2LatentNode, "height", 1280);

                    // The picked LoRA rides the Power Lora Loader's first slot, replacing the realism
                    // LoRA the workflow ships with — the same slot the Image Generator tab writes.
                    if (lora?.Reference is { } reference)
                    {
                        root = ParseGraph(json);
                        if (root[Krea2LoraNode]?["inputs"] is JsonObject loraInputs)
                        {
                            loraInputs["lora_1"] = new JsonObject
                            {
                                ["on"] = true,
                                ["lora"] = reference,
                                ["strength"] = 1.0,
                            };
                            json = root.ToJsonString();
                        }
                    }

                    // SaveImageKJ writes files but never reports them in /history, and the preview
                    // node would register a second image — one standard SaveImage on the RTX pass
                    // instead, exactly as the Image Generator tab submits this graph.
                    root = ParseGraph(json);
                    root.Remove(Krea2PreviewNode);
                    root[Krea2SaveNode] = new JsonObject
                    {
                        ["inputs"] = new JsonObject
                        {
                            ["filename_prefix"] = prefix,
                            ["images"] = new JsonArray { Krea2RtxNode, 0 },
                        },
                        ["class_type"] = "SaveImage",
                        ["_meta"] = new JsonObject { ["title"] = "Save Image (FlipPix cast photo)" },
                    };
                    json = RtxSuperResolutionCompat.Normalize(root.ToJsonString(), log);
                    return (json, Krea2SaveNode);
                }

                case "qwen":
                {
                    var json = await ReadWorkflowAsync("workflow/image/qwen/Qwen_Image_2512_INT8_Convrot_WF.json");
                    var root = ParseGraph(json);
                    RequireClass(root, QwenImgPromptNode, "CLIPTextEncode");
                    RequireClass(root, QwenImgSamplerNode, "KSampler");
                    RequireClass(root, QwenImgLatentNode, "EmptySD3LatentImage");
                    RequireClass(root, QwenImgSaveNode, "SaveImage");
                    json = root.ToJsonString();

                    SetInput(ref json, QwenImgPromptNode, "text", prompt);
                    SetInput(ref json, QwenImgSamplerNode, "seed", seed);
                    SetInput(ref json, QwenImgLatentNode, "width", 1088);
                    SetInput(ref json, QwenImgLatentNode, "height", 1600);
                    SetInput(ref json, QwenImgSaveNode, "filename_prefix", prefix);
                    return (json, QwenImgSaveNode);
                }

                case "famegrid": // Z-Famegrid — the igmodel look, run as authored except prompt / seed / save prefix / character LoRA
                {
                    var json = await ReadWorkflowAsync("workflow/image/zimage/base/Zimage-Famegrid.json");
                    var root = ParseGraph(json);
                    RequireClass(root, FamegridPromptNode, "PrimitiveStringMultiline");
                    RequireClass(root, FamegridSamplerNode, "ClownsharKSampler_Beta");
                    RequireClass(root, FamegridRefineNode, "ClownsharKSampler_Beta");
                    RequireClass(root, FamegridSaveNode, "SaveImage");
                    if (lora != null)
                    {
                        RequireClass(root, FamegridCharLoraNode, "easy loraNames");
                        RequireClass(root, FamegridCharLoraBase, "LoraLoaderModelOnly");
                        RequireClass(root, FamegridCharLoraRefine, "LoraLoaderModelOnly");
                    }
                    json = root.ToJsonString();

                    SetInput(ref json, FamegridPromptNode, "value", prompt);
                    // The workflow feeds both passes from one Seed (rgthree) node; the API export
                    // carries that as a literal, so the same seed is written into both samplers.
                    SetInput(ref json, FamegridSamplerNode, "seed", seed);
                    SetInput(ref json, FamegridRefineNode, "seed", seed);
                    SetInput(ref json, FamegridSaveNode, "filename_prefix", prefix);

                    // As authored the character-LoRA slot is bypassed: both shift nodes read the
                    // spice chain around it, and the slot's loaders sit in the file defined, wired
                    // and unreachable. A picked LoRA puts the slot back in — the loaders are pointed
                    // at by the shifts, and the name node feeds them as the UI graph designed it.
                    if (lora?.Reference is { } reference)
                    {
                        SetInput(ref json, FamegridCharLoraNode, "lora_name", reference);
                        root = ParseGraph(json);
                        if (root[FamegridBaseShiftNode]?["inputs"] is JsonObject baseShift)
                            baseShift["model"] = new JsonArray(FamegridCharLoraBase, 0);
                        if (root[FamegridRefineShiftNode]?["inputs"] is JsonObject refineShift)
                            refineShift["model"] = new JsonArray(FamegridCharLoraRefine, 0);
                        json = root.ToJsonString();
                    }
                    return (json, FamegridSaveNode);
                }

                default: // zimage — the base graph, run as authored except prompt / seed / save prefix / LoRA
                {
                    var path = WorkflowLocator.Resolve("workflow", "image", "zimage", "base", "z-image-base.json");
                    if (!File.Exists(path))
                        throw new FileNotFoundException($"Workflow file not found: {path}");
                    var json = await File.ReadAllTextAsync(path);
                    var root = ParseGraph(json);
                    RequireClass(root, ZBasePromptNode, "CLIPTextEncode");
                    RequireClass(root, ZBaseSamplerNode, "KSampler");
                    RequireClass(root, ZBaseSaveNode, "SaveImage");
                    if (lora != null) RequireClass(root, ZBaseLoraNode, "LoraLoaderModelOnly");
                    json = root.ToJsonString();

                    SetInput(ref json, ZBasePromptNode, "text", prompt);
                    SetInput(ref json, ZBaseSamplerNode, "seed", seed);
                    SetInput(ref json, ZBaseSaveNode, "filename_prefix", prefix);
                    // The graph's own skin-texture LoRA node, repointed at the picked LoRA — the
                    // strength stays what the workflow ships (0.8), as the Image Generator does.
                    if (lora?.Reference is { } reference)
                        SetInput(ref json, ZBaseLoraNode, "lora_name", reference);
                    return (json, ZBaseSaveNode);
                }
            }
        }

        /// <summary>The engine's display name, for the logs.</summary>
        public static string LabelFor(string engine) => engine switch
        {
            "krea2" => "Krea2",
            "qwen" => "Qwen 2.5.1.2",
            "famegrid" => "Z-Famegrid",
            _ => "Z-Image",
        };

        #region LoRA folders — the same resolution the Image Generator tab uses

        /// <summary>
        /// The Z-Image LoRAs on offer: every <c>.safetensors</c> under the LoRA root's <c>zimage</c>
        /// folder, <b>including its subfolders</b> — the folder is typically organised as
        /// <c>zimage/zib/…</c>, <c>zimage/amateur/…</c> and so on, and ComfyUI's <c>lora_name</c>
        /// wants the path exactly as it lies under the LoRA root, subfolders included.
        /// </summary>
        public static IReadOnlyList<CastLora> ListZimageLoras(FlipPix.Core.Models.ComfyUISettings? settings, Action<string> log)
        {
            var root = ResolveLoraBasePath(settings, log);
            if (root == null) return Array.Empty<CastLora>();

            var folder = Path.Combine(root, "zimage");
            if (!Directory.Exists(folder))
            {
                log($"No zimage LoRA folder at {folder} — the cast menu offers only the workflow's own LoRA.");
                return Array.Empty<CastLora>();
            }
            return Scan(folder, "zimage");
        }

        /// <summary>
        /// The Krea2 LoRAs on offer: the configured Krea2 LoRA folder when one is set, else the LoRA
        /// root's <c>krea2</c>/<c>Krea2</c> subfolder. The folder's own name is what ComfyUI expects
        /// in the lora reference, exactly as the Image Generator tab derives it.
        /// </summary>
        public static IReadOnlyList<CastLora> ListKrea2Loras(FlipPix.Core.Models.ComfyUISettings? settings, Action<string> log)
        {
            var configured = settings?.KreaLoraFolderPath;
            if (!string.IsNullOrEmpty(configured))
            {
                if (Directory.Exists(configured))
                    return Scan(configured, new DirectoryInfo(configured).Name);
                log($"Configured Krea2 LoRA folder not accessible: {configured}");
            }

            var root = ResolveLoraBasePath(settings, log);
            if (root == null) return Array.Empty<CastLora>();

            foreach (var name in new[] { "krea2", "Krea2" })
            {
                var folder = Path.Combine(root, name);
                if (Directory.Exists(folder))
                    return Scan(folder, name);
            }
            log($"No krea2 LoRA folder under {root} — the cast menu offers only the workflow's own LoRA.");
            return Array.Empty<CastLora>();
        }

        /// <summary>
        /// Scans a LoRA folder <b>recursively</b>. The menu name is the path under the folder without
        /// the extension (<c>foo</c>, or <c>zib/foo</c> when nested); the reference is the full path
        /// under the LoRA root with forward slashes, which is what ComfyUI's <c>lora_name</c> wants.
        /// </summary>
        private static IReadOnlyList<CastLora> Scan(string folder, string subfolder) =>
            Directory.GetFiles(folder, "*.safetensors", SearchOption.AllDirectories)
                .Select(file =>
                {
                    var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                    var dot = relative.LastIndexOf('.');
                    return (Name: dot > 0 ? relative[..dot] : relative,
                            Reference: $"{subfolder}/{relative}");
                })
                .Where(x => !string.IsNullOrEmpty(x.Name))
                .OrderBy(x => x.Name)
                .Select(x => new CastLora(x.Name, x.Reference))
                .ToList();

        /// <summary>
        /// The LoRA root, resolved the way the Image Generator tab resolves it: for a remote ComfyUI,
        /// the explicitly configured network LoRA path or one derived from the remote output path;
        /// for a local one, the ComfyUI folder — honouring <c>extra_model_paths.yaml</c> — and its
        /// default <c>models/loras</c>.
        /// </summary>
        private static string? ResolveLoraBasePath(FlipPix.Core.Models.ComfyUISettings? settings, Action<string> log)
        {
            try
            {
                var baseUrl = settings?.BaseUrl ?? string.Empty;
                var isRemote = IsRemoteUrl(baseUrl);

                if (isRemote)
                {
                    var explicitPath = settings?.RemoteLoraFolderPath;
                    if (!string.IsNullOrEmpty(explicitPath))
                    {
                        if (Directory.Exists(explicitPath)) return explicitPath;
                        log($"Configured remote LoRA path not accessible: {explicitPath}");
                    }

                    var remoteOutput = settings?.RemoteOutputFolderPath;
                    var comfyRoot = string.IsNullOrEmpty(remoteOutput) ? null : Path.GetDirectoryName(remoteOutput);
                    if (comfyRoot == null)
                    {
                        log("Remote LoRA path not configured and the output path cannot be derived from — " +
                            "cannot list cast-photo LoRAs.");
                        return null;
                    }

                    var derived = Path.Combine(comfyRoot, "models", "loras");
                    if (Directory.Exists(derived)) return derived;
                    log($"Derived remote LoRA path not found: {derived}");
                    return null;
                }

                var comfyFolder = settings?.ComfyUIFolderPath;
                if (string.IsNullOrEmpty(comfyFolder))
                {
                    log("ComfyUI installation path not configured — cannot list cast-photo LoRAs.");
                    return null;
                }

                // extra_model_paths.yaml — the usual place a real install keeps its models.
                var yamlFile = Path.Combine(comfyFolder, "extra_model_paths.yaml");
                if (File.Exists(yamlFile))
                {
                    try
                    {
                        var yaml = new DeserializerBuilder().Build()
                            .Deserialize<Dictionary<string, object>>(File.ReadAllText(yamlFile));
                        if (yaml != null)
                        {
                            var basePath = string.Empty;
                            var lorasRel = string.Empty;

                            if (yaml.TryGetValue("comfyui", out var section) &&
                                section is Dictionary<object, object> pairs)
                            {
                                foreach (var kvp in pairs)
                                {
                                    var key = kvp.Key?.ToString();
                                    if (key == "base_path") basePath = kvp.Value?.ToString() ?? string.Empty;
                                    else if (key == "loras") lorasRel = kvp.Value?.ToString() ?? string.Empty;
                                }
                            }
                            if (lorasRel.Length == 0 && yaml.TryGetValue("loras", out var direct))
                                lorasRel = direct?.ToString() ?? string.Empty;

                            if (lorasRel.Length > 0)
                            {
                                var full = (basePath.Length > 0 ? Path.Combine(basePath, lorasRel) : lorasRel)
                                    .Replace('/', Path.DirectorySeparatorChar);
                                if (Directory.Exists(full)) return full;
                                log($"LoRA path from extra_model_paths.yaml not found: {full}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log($"extra_model_paths.yaml could not be read: {ex.Message}");
                    }
                }

                var defaultPath = Path.Combine(comfyFolder, "models", "loras");
                if (Directory.Exists(defaultPath)) return defaultPath;
                log($"No LoRA directory found in: {comfyFolder}");
                return null;
            }
            catch (Exception ex)
            {
                log($"LoRA folder lookup failed: {ex.Message}");
                return null;
            }
        }

        private static bool IsRemoteUrl(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return false;
                var host = new Uri(url).Host.ToLowerInvariant();
                return !host.Equals("localhost") && !host.Equals("127.0.0.1") &&
                       !host.Equals("0.0.0.0") && !host.Equals("::1");
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region The story's cast, read by the llama-server

        /// <summary>
        /// The one llama-server turn both tabs send: list the story's characters, most important
        /// first, as strict <c>CAST|kind|name — description</c> lines.
        /// </summary>
        /// <param name="personKindsOnly">The two-hander tab casts only people — its sheet builder,
        /// wardrobe pass and Sex dropdown have no branch for a cloud or a herd. The ensemble tab
        /// takes every kind its cards offer.</param>
        public static async Task<string> AskCastAsync(
            LMStudioService lm,
            string model,
            string storyText,
            int maxCharacters,
            bool personKindsOnly,
            CancellationToken token)
        {
            var systemPrompt =
                "You are a casting director reading a story and listing the characters who belong in " +
                "its cast. You reply with nothing but the cast lines you were asked for — no preamble, " +
                "no headings, no markdown, no notes, no explanation.";

            var kinds = personKindsOnly
                ? "<kind> is exactly one word from this list: man, woman, boy, girl. This film casts " +
                  "only people — never list an animal, an object or a group as a character."
                : "<kind> is exactly one word from this list: man, woman, boy, girl, creature, " +
                  "character, crowd, group. Use man / woman / boy / girl for one person, creature for an " +
                  "animal or fantastic being, character for anything else that is not a person (a cloud, " +
                  "a mountain, a car), crowd for several people acting as one (a village, a choir), and " +
                  "group for several non-people acting as one (a herd, a flock).";

            var userMessage =
                "Read the story below and list the characters it is actually about, most important " +
                $"first — at most {maxCharacters} of them. Background extras, narrators and characters " +
                "who never appear in a scene do not belong in the cast.\n\n" +
                "Reply with one line per character, in EXACTLY this format, and nothing else:\n" +
                "CAST|<kind>|<who>\n\n" +
                kinds + "\n" +
                "<who> is the character's NAME if the story names one, then a dash, then ONE sentence " +
                "of at most 40 words describing them: who they are in the story, their age and look " +
                "where the story gives it, and their clothing where the story gives it. Write it so " +
                "somebody who has never read the story could picture them.\n\n" +
                $"The story:\n{storyText.Trim()}";

            return await lm.SendTextChatAsync(
                model, systemPrompt, userMessage, maxTokens: 800, cancellationToken: token);
        }

        /// <summary>Parses the <c>CAST|kind|who</c> lines out of the reply. A role containing a pipe
        /// survives — only the first two pipes split it. Lines whose kind is not recognized are
        /// dropped rather than guessed at.</summary>
        public static IReadOnlyList<(string Kind, string Role)> ParseCastLines(string reply, int maxCharacters)
        {
            var cast = new List<(string, string)>();
            foreach (var raw in (reply ?? string.Empty).Split('\n'))
            {
                var line = raw.Trim().TrimEnd('\r');
                if (!line.StartsWith("CAST|", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = line.Split('|');
                if (parts.Length < 3) continue;

                var kind = KindFromWord(parts[1].Trim());
                var role = string.Join("|", parts.Skip(2)).Trim();
                if (kind == null || role.Length == 0) continue;

                cast.Add((kind!, role));
                if (cast.Count >= maxCharacters) break;
            }
            return cast;
        }

        private static string? KindFromWord(string word) => word.ToLowerInvariant().TrimEnd('.', ';') switch
        {
            "man" or "male" => CharacterSlot.Male,
            "woman" or "female" => CharacterSlot.Female,
            "boy" => CharacterSlot.Boy,
            "girl" => CharacterSlot.Girl,
            "creature" or "animal" => CharacterSlot.Creature,
            "character" or "thing" => CharacterSlot.Thing,
            "crowd" => CharacterSlot.Crowd,
            "group" or "herd" or "flock" => CharacterSlot.Group,
            _ => null,
        };

        #endregion

        #region JSON helpers — the ensemble tab's SetInput/RequireClass, self-contained

        private static async Task<string> ReadWorkflowAsync(string relativePath)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Workflow file not found: {path}");
            return await File.ReadAllTextAsync(path);
        }

        private static JsonObject ParseGraph(string json) =>
            JsonNode.Parse(json)?.AsObject() ?? throw new Exception("Workflow JSON could not be parsed.");

        /// <summary>Fails loudly on a node id or input that is no longer in the graph — a silent
        /// no-op here would mean shipping the workflow's baked-in demo prompt to the GPU.</summary>
        private static void SetInput(ref string json, string nodeId, string input, object value)
        {
            if (WorkflowNodeUpdater.GetNodeInput(json, nodeId, input) == null)
                throw new Exception($"Workflow node '{nodeId}' has no input '{input}' — the workflow file no longer matches this code.");
            WorkflowNodeUpdater.UpdateNodeInput(ref json, nodeId, input, value);
        }

        private static void RequireClass(JsonObject root, string nodeId, string expected)
        {
            if (root[nodeId]?["class_type"]?.GetValue<string>() is { } ct &&
                ct.Contains(expected, StringComparison.OrdinalIgnoreCase))
                return;
            throw new Exception($"Workflow node '{nodeId}' is not the expected {expected} — the workflow file no longer matches this code.");
        }

        #endregion
    }
}
