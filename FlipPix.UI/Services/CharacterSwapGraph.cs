using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// The request-side half of Openjutsu (github.com/D3voz/openjutsu, MIT), ported from its
    /// <c>lib/simple.ts</c>, <c>lib/advanced.ts</c>, <c>lib/model.ts</c> and <c>lib/comfy.ts</c>. The three
    /// graphs in <c>workflow/video/h3-minimax/character-swap</c> are Openjutsu's API exports, unchanged:
    /// each is exported with every optional branch on (hat fix, No lip-sync, all four pictures) and every
    /// request prunes what it does not use, so the files stay byte-identical to upstream and a re-export
    /// drops straight in.
    ///
    /// <para>Nodes are found by the titles Openjutsu's builder scripts give them, except where upstream
    /// itself pins ids (<see cref="SimpleNodes"/>, <see cref="MaskNodes"/>). Every edit that removes a node
    /// re-checks that nothing still links to it, so a re-export that moved a link fails at build time of
    /// the request rather than as a ComfyUI validation error minutes into a render.</para>
    /// </summary>
    public static class CharacterSwapGraph
    {
        public const string SimpleFile = "simple.api.json";
        public const string AdvancedFile = "advanced.api.json";
        public const string MaskFile = "mask.api.json";

        public static readonly string[] Resolutions = { "480p", "576p", "736p", "768p", "1080p" };
        public static readonly int[] MotionSizes = { 512, 768, 1376 };
        public const double MinClipSeconds = 2.5, MaxClipSeconds = 15, DefaultClipSeconds = 6;
        public const int MaxCharacters = 4;
        public const string DefaultTarget = "person";

        /// <summary>Edit-mask grow in px at SAM's 1008 px canvas. The motion still comes from the tight mannequin.</summary>
        public static int MarginPixels(SwapMargin margin) => margin switch
        {
            SwapMargin.Tight => 20,
            SwapMargin.Loose => 60,
            _ => 40,
        };

        /// <summary>Node ids upstream pins in <c>simple.api.json</c> (NODES in lib/simple.ts).</summary>
        public static class SimpleNodes
        {
            public const string Picture = "2", Video = "5", Plan = "11", Target = "24", PeopleMask = "39",
                                GrowMask = "42", Final = "67";
            public static readonly string[] Hat = { "47", "48", "49", "50", "51", "52" };
        }

        /// <summary>Node ids upstream pins in <c>mask.api.json</c> (MASK_NODES in lib/simple.ts).</summary>
        public static class MaskNodes
        {
            public const string Video = "1", Target = "3", PeopleMask = "7", GrowMask = "9", Preview = "18";
            public static readonly string[] Hat = { "10", "11", "12", "13", "19", "20" };
        }

        public const string AdvancedPlanTitle = "Swap Plan (roles, seed, size)";
        public const string AdvancedVideoTitle = "Source video (people get replaced)";
        public const string FinalVideoTitle = "Final Video";

        // ── Defaults (DEFAULT_MODEL in lib/model.ts) ─────────────────────────────────────────────────

        public const string DefaultDescriber = "qwen3vl_8b_int8_convrot.safetensors";
        public const string DefaultModel = "minimax_h3_ref2va_pruned_int8_convrot.safetensors";
        public const string DefaultLora = "minimax_h3_DMAD_4step_full_lora_avg_rank_39_bf16.safetensors";
        public const string DefaultTextEncoder = "qwen3vl_32b_minimax_h3_int4_convrot.safetensors";
        public const string DefaultVideoVae = "minimax_h3_video_vae_int8_convrot.safetensors";
        public const string DefaultAudioVae = "minimax_h3_audio_vae_fp32.safetensors";
        public const int DefaultSteps = 6;

        // ── Small helpers ────────────────────────────────────────────────────────────────────────────

        public static bool IsLink(JsonNode? value, out string id, out int slot)
        {
            id = string.Empty; slot = 0;
            if (value is not JsonArray a || a.Count != 2) return false;
            if (a[0] is not JsonValue v0 || !v0.TryGetValue<string>(out var s)) return false;
            if (a[1] is not JsonValue v1 || !v1.TryGetValue<int>(out var n)) return false;
            id = s; slot = n;
            return true;
        }

        private static JsonArray Link(string id, int slot) => new() { id, slot };

        private static JsonObject Node(JsonObject graph, string id) =>
            graph[id] as JsonObject ?? throw new InvalidOperationException($"Node {id} is not in the workflow.");

        public static JsonObject Inputs(JsonObject graph, string id) =>
            Node(graph, id)["inputs"] as JsonObject ?? throw new InvalidOperationException($"Node {id} has no inputs.");

        public static string ClassOf(JsonObject graph, string id) =>
            Node(graph, id)["class_type"]?.GetValue<string>() ?? string.Empty;

        public static string TitleOf(JsonNode? node) =>
            node?["_meta"]?["title"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : string.Empty;

        public static string NodeByTitle(JsonObject graph, string title)
        {
            var ids = graph.Where(kv => TitleOf(kv.Value) == title).Select(kv => kv.Key).ToList();
            if (ids.Count != 1) throw new InvalidOperationException($"Expected one \"{title}\" node in the workflow, found {ids.Count}.");
            return ids[0];
        }

        private static IEnumerable<(string id, JsonObject inputs)> AllInputs(JsonObject graph)
        {
            foreach (var (id, node) in graph.ToList())
                if (node?["inputs"] is JsonObject inputs) yield return (id, inputs);
        }

        /// <summary>Throws when any input still links to a node that is no longer in the graph.</summary>
        public static void AssertNoDanglingLinks(JsonObject graph)
        {
            foreach (var (id, inputs) in AllInputs(graph))
                foreach (var (name, value) in inputs)
                    if (IsLink(value, out var target, out _) && !graph.ContainsKey(target))
                        throw new InvalidOperationException($"Node {id}.{name} still links to removed node {target}.");
        }

        private static string Collapse(string s) => Regex.Replace(s ?? string.Empty, @"\s+", " ").Trim();

        // ── Shared edits ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// VHS fails as soon as anything reads the soundtrack of a video that has none, so every reader of a
        /// VHS_LoadVideo's audio output gets <c>H3SoundtrackOrSilence</c> instead (silenceWhenSoundless).
        ///
        /// <para>FlipPix also appends a second of silence. A file's audio stream commonly ends a few
        /// milliseconds before its last video frame (5.152 s of sound under 5.167 s of picture), and the
        /// Context Loop's Source Timeline sizes itself from the audio, so the plan's last clip asked for frame
        /// 124 of a 123-frame timeline and the final assembly failed after every clip had rendered. The tail is
        /// never heard: every reader slices the soundtrack to the frames it renders.</para>
        /// </summary>
        public static void SilenceWhenSoundless(JsonObject graph)
        {
            foreach (var videoId in graph.Where(kv => kv.Value?["class_type"]?.GetValue<string>() == "VHS_LoadVideo")
                                         .Select(kv => kv.Key).ToList())
            {
                var soundtrack = $"{videoId}_soundtrack";
                var padded = $"{videoId}_soundtrack_padded";
                var read = false;
                foreach (var (_, inputs) in AllInputs(graph))
                    foreach (var name in inputs.Select(kv => kv.Key).ToList())
                        if (IsLink(inputs[name], out var src, out var slot) && src == videoId && slot == 2)
                        {
                            inputs[name] = Link(padded, 0);
                            read = true;
                        }
                if (!read) continue;

                graph[soundtrack] = new JsonObject
                {
                    ["class_type"] = "H3SoundtrackOrSilence",
                    ["inputs"] = new JsonObject { ["audio"] = Link(videoId, 2), ["video_info"] = Link(videoId, 3) },
                    ["_meta"] = new JsonObject { ["title"] = "Soundtrack (silence if none)" },
                };
                graph[$"{videoId}_tail"] = new JsonObject
                {
                    ["class_type"] = "EmptyAudio",
                    ["inputs"] = new JsonObject { ["duration"] = 1.0, ["sample_rate"] = 44100, ["channels"] = 2 },
                    ["_meta"] = new JsonObject { ["title"] = "Silent tail (audio covers every frame)" },
                };
                graph[padded] = new JsonObject
                {
                    ["class_type"] = "AudioConcat",
                    ["inputs"] = new JsonObject
                    {
                        ["audio1"] = Link(soundtrack, 0),
                        ["audio2"] = Link($"{videoId}_tail", 0),
                        ["direction"] = "after",
                    },
                    ["_meta"] = new JsonObject { ["title"] = "Soundtrack + silent tail" },
                };
            }
        }

        /// <summary>
        /// For a source with no audio stream: the soundtrack becomes silence as long as the loaded frames, and
        /// nothing links VHS_LoadVideo's audio output at all.
        /// <para>H3SoundtrackOrSilence alone is not enough. Since ComfyUI 0.42, once a node finishes, the
        /// executor walks each of its linked outputs (model_patcher's output-link callback), and walking
        /// VHS's lazy audio runs ffmpeg on a file with no audio stream, which throws before the
        /// silence node ever runs ("VHS failed to extract audio … Output file does not contain any stream").
        /// Leaving the audio output unlinked means nothing reads it.</para>
        /// <para>Run after <see cref="SilenceWhenSoundless"/>, which routes every reader through
        /// <c>{video}_soundtrack</c>.</para>
        /// </summary>
        public static void UseSilenceForSoundless(JsonObject graph)
        {
            foreach (var videoId in graph.Where(kv => kv.Value?["class_type"]?.GetValue<string>() == "VHS_LoadVideo")
                                         .Select(kv => kv.Key).ToList())
            {
                var soundtrack = $"{videoId}_soundtrack";
                if (graph[soundtrack] is not JsonObject) continue;

                var info = $"{videoId}_loaded_info";
                graph[info] = new JsonObject
                {
                    ["class_type"] = "VHS_VideoInfoLoaded",
                    ["inputs"] = new JsonObject { ["video_info"] = Link(videoId, 3) },
                    ["_meta"] = new JsonObject { ["title"] = "Loaded length (source has no audio)" },
                };
                graph[soundtrack] = new JsonObject
                {
                    ["class_type"] = "EmptyAudio",
                    // VHS_VideoInfoLoaded slot 2 = duration of the frames actually loaded (after skip/cap/rate).
                    ["inputs"] = new JsonObject { ["duration"] = Link(info, 2), ["sample_rate"] = 44100, ["channels"] = 2 },
                    ["_meta"] = new JsonObject { ["title"] = "Soundtrack (source has no audio: silence)" },
                };
            }
        }

        /// <summary>Part of the source to use; every export loads the video at force_rate 24. Length 0 = to the end.</summary>
        public static void SetSection(JsonObject graph, string videoId, double startSeconds, double lengthSeconds)
        {
            var inputs = Inputs(graph, videoId);
            inputs["skip_first_frames"] = (int)Math.Round(Math.Max(0, startSeconds) * 24);
            inputs["frame_load_cap"] = (int)Math.Round(Math.Max(0, lengthSeconds) * 24);
        }

        private static readonly Regex Mute = new(@"^Mute the (clip|reference) audio H3 hears$");
        private static readonly Regex NoLipSync = new(@"^(No lip-sync in the prompt|SAM: faces|SAM 3\.1 track faces|Faces \(shown on the mannequins?\))$");

        /// <summary>No lip-sync off: its nodes go, H3 hears the soundtrack again and the mannequins get no faces.</summary>
        public static void DropNoLipSync(JsonObject graph)
        {
            var through = new Dictionary<string, JsonNode?>();
            foreach (var (id, node) in graph)
            {
                var title = TitleOf(node);
                if (Mute.IsMatch(title)) through[id] = node?["inputs"]?["audio"]?.DeepClone();
                else if (NoLipSync.IsMatch(title)) through[id] = null;
            }
            foreach (var id in through.Keys) graph.Remove(id);
            foreach (var (id, inputs) in AllInputs(graph))
                foreach (var name in inputs.Select(kv => kv.Key).ToList())
                {
                    if (!IsLink(inputs[name], out var src, out _) || !through.TryGetValue(src, out var replacement)) continue;
                    if (replacement != null) inputs[name] = replacement.DeepClone();
                    else if (name is "no_lip_sync" or "faces" or "face_size") inputs.Remove(name);
                    else throw new InvalidOperationException($"Node {id}.{name} still links to removed node {src}.");
                }
        }

        /// <summary>
        /// Settings the Production Plan's run name does not know get a run of their own, so a rerun never
        /// resumes from saved clips made without them.
        /// </summary>
        public static void SuffixRunName(JsonObject graph, string id, string suffix, string title)
        {
            var plans = graph.Where(kv => kv.Value?["class_type"]?.GetValue<string>() == "MiniMaxH3ChainPlanModern").ToList();
            if (plans.Count != 1) throw new InvalidOperationException($"Expected one Production Plan node in the workflow, found {plans.Count}.");
            var planInputs = (JsonObject)plans[0].Value!["inputs"]!;
            graph[id] = new JsonObject
            {
                ["class_type"] = "StringConcatenate",
                ["inputs"] = new JsonObject
                {
                    ["string_a"] = planInputs["run_name"]?.DeepClone(),
                    ["string_b"] = suffix,
                    ["delimiter"] = "",
                },
                ["_meta"] = new JsonObject { ["title"] = title },
            };
            planInputs["run_name"] = Link(id, 0);
        }

        /// <summary>Every motion-reference resize, sized; non-default sizes resume from runs of their own.</summary>
        public static void SetMotionSize(JsonObject graph, int size)
        {
            var nodes = graph.Where(kv => kv.Value?["class_type"]?.GetValue<string>() == "ImageScaleToMaxDimension"
                                          && TitleOf(kv.Value).Contains("motion reference", StringComparison.OrdinalIgnoreCase))
                             .Select(kv => (JsonObject)kv.Value!["inputs"]!).ToList();
            if (nodes.Count == 0) throw new InvalidOperationException("Expected a motion reference resize in the workflow.");
            foreach (var inputs in nodes) inputs["largest_size"] = size;
            if (size != MotionSizes[0])
                SuffixRunName(graph, "motion_run_name", $"_motion{size}", "Run name for this motion reference size");
        }

        // ── Simple / mask ────────────────────────────────────────────────────────────────────────────

        private static void MaskSettings(JsonObject graph, bool hatFix, SwapMargin margin, string target,
                                         string targetId, string growId, string peopleId, string[] hat)
        {
            Inputs(graph, targetId)["text"] = string.IsNullOrWhiteSpace(target) ? DefaultTarget : target.Trim();
            Inputs(graph, growId)["expand"] = MarginPixels(margin);
            if (!hatFix)
            {
                foreach (var id in hat) graph.Remove(id);
                Inputs(graph, growId)["mask"] = Link(peopleId, 0);
            }
            AssertNoDanglingLinks(graph);
        }

        /// <summary>Simple motion transfer (pruneSimple): one character replaces the person filling the most of the frame.</summary>
        public static JsonObject PruneSimple(JsonObject source, SwapSettings s)
        {
            var graph = (JsonObject)source.DeepClone();
            MaskSettings(graph, s.HatFix, s.Margin, s.Target, SimpleNodes.Target, SimpleNodes.GrowMask, SimpleNodes.PeopleMask, SimpleNodes.Hat);
            if (!s.NoLipSync) DropNoLipSync(graph);
            SetMotionSize(graph, s.MotionSize);
            var plan = Inputs(graph, SimpleNodes.Plan);
            plan["resolution"] = s.Resolution;
            plan["seed"] = s.Seed;
            plan["clip_seconds"] = s.ClipSeconds;
            plan["roles"] = Collapse(s.Roles.FirstOrDefault() ?? string.Empty);
            return graph;
        }

        /// <summary>Check mask (pruneMask): SAM + the edit mask only, tinted next to the grey mannequin.</summary>
        public static JsonObject PruneMask(JsonObject source, bool hatFix, SwapMargin margin, string target)
        {
            var graph = (JsonObject)source.DeepClone();
            MaskSettings(graph, hatFix, margin, target, MaskNodes.Target, MaskNodes.GrowMask, MaskNodes.PeopleMask, MaskNodes.Hat);
            // Upstream keeps the preview as a temp file and serves it through the SDK; FlipPix fetches
            // outputs from history, so it is saved under the tab's folder instead.
            var preview = Inputs(graph, MaskNodes.Preview);
            preview["save_output"] = true;
            preview["filename_prefix"] = "character_swap/mask";
            return graph;
        }

        // ── Live clip previews ───────────────────────────────────────────────────────────────────────

        /// <summary>Filename prefix of the per-clip previews, so the page can tell them from other temp files.</summary>
        public const string ClipPreviewPrefix = "flippix_swap_clip";
        public const string ClipPreviewNode = "clip_preview";

        /// <summary>
        /// Adds a small H.264 preview of each finished clip, so a long swap (196 s is about 46 clips)
        /// shows how it is going instead of nothing until the final assembly.
        /// <para>The chain's own "Save Clip + Checkpoint" only reports its segment to the UI when no Review
        /// Gate follows it, and these graphs have one. So a VHS_VideoCombine reads the Trim node's delivered
        /// frames and audio, the same ones the clip is saved from. Context Loop's Loop End clones every
        /// output node that directly consumes a loop-body node into each next clip, so it renders once per
        /// clip. It saves to temp (save_output false): nothing is added to the output folder.</para>
        /// </summary>
        public static void AddClipPreview(JsonObject graph)
        {
            var trim = graph.FirstOrDefault(kv => kv.Value?["class_type"]?.GetValue<string>() == "MiniMaxH3LoopTrim").Key;
            if (trim == null) return;
            graph[ClipPreviewNode] = new JsonObject
            {
                ["class_type"] = "VHS_VideoCombine",
                ["inputs"] = new JsonObject
                {
                    ["images"] = Link(trim, 0),
                    ["audio"] = Link(trim, 1),
                    ["frame_rate"] = 24,
                    ["loop_count"] = 0,
                    ["filename_prefix"] = ClipPreviewPrefix,
                    ["format"] = "video/h264-mp4",
                    ["pix_fmt"] = "yuv420p",
                    ["crf"] = 23,
                    ["save_metadata"] = false,
                    ["trim_to_audio"] = false,
                    ["pingpong"] = false,
                    ["save_output"] = false,
                },
                ["_meta"] = new JsonObject { ["title"] = "Clip preview (FlipPix)" },
            };
        }

        /// <summary>The clip the chain has just started, from Current Clip's <c>h3_chain_active_scene</c> UI output.</summary>
        public static (int Index, int Count)? ActiveClip(string? outputJson)
        {
            if (string.IsNullOrEmpty(outputJson) || !outputJson.Contains("h3_chain_active_scene")) return null;
            try
            {
                var scene = JsonNode.Parse(outputJson)?["h3_chain_active_scene"]?[0];
                var index = scene?["clip_index"]?.GetValue<int>();
                var count = scene?["clip_count"]?.GetValue<int>();
                return index is > 0 && count is > 0 ? (index.Value, count.Value) : null;
            }
            catch (Exception) { return null; }
        }

        // ── Find people ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Node whose text output holds the describer's list of people.</summary>
        public const string PeopleOutput = "people_list";
        public const int PeopleFrames = 20;

        private const string PeopleOnce =
            " The frames come from different shots, so the same person can look different between them (lighting, angle, " +
            "close-up or wide, holding a prop, a costume change). Tell people apart by face, build, hair and skin, not by what " +
            "they are doing in one shot, and list each person exactly once: never list one person twice under two roles. " +
            "Leave out people who are only blurred, tiny or in the background crowd. In the look, give gender, age, build, hair and " +
            "face first, then clothing.";

        /// <summary>
        /// A small graph that only asks the describer who is in the video, using the Advanced export's own
        /// "Who is in the video" prompt and model so the roles it returns are the wording the swap matches
        /// tracks against. Takes <see cref="PeopleFrames"/> frames spread over the chosen part of the video
        /// (the swap's own describer samples one every 2 s from the start, so it only sees the first 40 s),
        /// and loads only those frames. Takes seconds, not minutes: no SAM, no H3.
        /// </summary>
        public static JsonObject BuildFindPeople(JsonObject advancedSource, string video, string describer,
                                                 double startSeconds, double lengthSeconds, double videoSeconds)
        {
            var videoId = NodeByTitle(advancedSource, AdvancedVideoTitle);
            var whoId = NodeByTitle(advancedSource, "Who is in the video");
            // Each picture has its own "Shrink for the describer"; take the one feeding this node.
            if (!IsLink(Inputs(advancedSource, whoId)["image"], out var shrinkId, out _))
                throw new InvalidOperationException("\"Who is in the video\" has no image input.");
            var clipId = NodeByTitle(advancedSource, "Describer: Qwen3-VL 8B");

            var graph = new JsonObject();
            foreach (var id in new[] { videoId, shrinkId, whoId, clipId })
                graph[id] = advancedSource[id]!.DeepClone();

            var loader = Inputs(graph, videoId);
            loader["video"] = video;
            SetSection(graph, videoId, startSeconds, lengthSeconds);
            var span = lengthSeconds > 0 ? lengthSeconds : Math.Max(0, videoSeconds - Math.Max(0, startSeconds));
            loader["select_every_nth"] = span > 0 ? Math.Max(1, (int)(span * 24 / PeopleFrames)) : 48;
            loader["frame_load_cap"] = PeopleFrames;

            Inputs(graph, shrinkId)["image"] = Link(videoId, 0);
            var who = Inputs(graph, whoId);
            who["image"] = Link(shrinkId, 0);
            // Frames spread over a long video cross many shots, and the describer then lists one person
            // twice under different roles ("the woman" and "the fighter"), which makes the picker a guess.
            who["prompt"] = (who["prompt"]?.GetValue<string>() ?? string.Empty) + PeopleOnce;
            who["clip"] = Link(clipId, 0);
            Inputs(graph, clipId)["clip_name"] = describer;

            graph[PeopleOutput] = new JsonObject
            {
                ["class_type"] = "PreviewAny",
                ["inputs"] = new JsonObject { ["source"] = Link(whoId, 0) },
                ["_meta"] = new JsonObject { ["title"] = "People in the video" },
            };
            AssertNoDanglingLinks(graph);
            return graph;
        }

        private static readonly Regex PersonLine = new(@"^\s*(\d+)\s*[.)]\s*(.+?)\s*$");

        /// <summary>Reads the describer's "1. the guitarist - tall man with a grey beard" lines.</summary>
        public static List<SwapPerson> ParsePeople(string? text)
        {
            var people = new List<SwapPerson>();
            foreach (var raw in (text ?? string.Empty).Split('\n'))
            {
                var m = PersonLine.Match(raw);
                if (!m.Success) continue;
                var body = m.Groups[2].Value.Trim().Trim('*').Trim();
                var dash = body.IndexOf(" - ", StringComparison.Ordinal);
                var role = (dash > 0 ? body[..dash] : body).Trim();
                var look = dash > 0 ? body[(dash + 3)..].Trim() : string.Empty;
                if (role.Length == 0) continue;
                // The describer sometimes lists one person again from a later shot under the same role; one
                // entry per role keeps the picker to the people actually there (first description wins).
                if (people.Any(p => string.Equals(p.Role, role, StringComparison.OrdinalIgnoreCase))) continue;
                people.Add(new SwapPerson(people.Count + 1, role, look));
            }
            return people;
        }

        // ── Failure hints ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What to change after a run that failed because the edit mask came out empty (H3's edit-latent
        /// node refuses with "The mask is empty on every frame, so nothing would change."), or null for
        /// any other failure.
        /// <para>Advanced mode builds the mask from SAM's person tracks that Qwen matches to a role; with
        /// the role left blank, Qwen names the person itself ("the wrestler") and can then fail to match
        /// anyone in a clip. Simple mode and Check mask track whatever the "what to replace" text names.
        /// Either way one clip with nobody matching ends the whole run, so a clip where the person is
        /// absent fails it too.</para>
        /// </summary>
        public static string? EmptyMaskHint(string? error, bool advanced, IReadOnlyList<string> roles, string target, bool wholeVideo)
        {
            if (error == null || !error.Contains("mask is empty", StringComparison.OrdinalIgnoreCase)) return null;

            string hint;
            if (advanced)
            {
                hint = roles.All(string.IsNullOrWhiteSpace)
                    ? "Nobody in this clip matched the person to replace. With \"Who do they replace?\" left blank, " +
                      "the describer names them itself and may not recognise them again. Fill it in under each picture, " +
                      "by role plus how they look after ' - ', e.g. \"the woman walking forward - woman in a black dress\"."
                    : "Nobody in this clip matched the role(s) under the pictures. Make the role easier to spot " +
                      "(add how they look after ' - ', e.g. \"the singer - woman with a red scarf\").";
            }
            else
            {
                var what = string.IsNullOrWhiteSpace(target) ? DefaultTarget : target.Trim();
                hint = $"SAM found no \"{what}\" to replace. Change what to replace, or use Check mask to see what it picks up.";
            }

            return hint + (wholeVideo
                ? " One clip without them stops the whole run: if they are not in every part of the video, set the section to where they appear."
                : " If they are not in this section, set it to where they appear.");
        }

        // ── Advanced ─────────────────────────────────────────────────────────────────────────────────

        private static readonly Regex Hat = new(@"\bhats?\b", RegexOptions.IgnoreCase);
        private static readonly Regex PictureTitle = new(@"^Picture \d$");

        public static int PictureCount(JsonObject graph) =>
            graph.Count(kv => kv.Value?["class_type"]?.GetValue<string>() == "LoadImage" && PictureTitle.IsMatch(TitleOf(kv.Value)));

        private static string Ordinal(int i, int count) =>
            count == 1 ? "the main person" : $"the person with the {new[] { "", "second ", "third ", "fourth " }[i]}most screen time";

        /// <summary>Advanced motion transfer (pruneAdvanced): 1–4 characters, each person matched to a role per shot.</summary>
        public static JsonObject PruneAdvanced(JsonObject source, int pictures, SwapSettings s)
        {
            var graph = (JsonObject)source.DeepClone();
            var available = PictureCount(graph);
            if (pictures < 1 || pictures > available) throw new InvalidOperationException($"This mode takes 1-{available} character photos.");

            var removed = new HashSet<string>();
            for (var k = pictures + 1; k <= available; k++)
            {
                var load = NodeByTitle(graph, $"Picture {k}");
                var shrink = graph.FirstOrDefault(kv => kv.Value?["class_type"]?.GetValue<string>() == "ImageScaleToMaxDimension"
                                                        && IsLink(kv.Value?["inputs"]?["image"], out var src, out _) && src == load).Key;
                removed.Add(load);
                if (shrink != null) removed.Add(shrink);
                removed.Add(NodeByTitle(graph, $"Describe picture {k}"));
            }

            var bypass = new Dictionary<string, JsonNode?>();
            if (!s.HatFix)
                foreach (var (id, node) in graph)
                {
                    if (!Hat.IsMatch(TitleOf(node))) continue;
                    removed.Add(id);
                    if (node?["class_type"]?.GetValue<string>() == "MaskComposite" && node["inputs"]?["operation"]?.GetValue<string>() == "add")
                        bypass[id] = node["inputs"]?["destination"]?.DeepClone();
                }

            foreach (var id in removed) graph.Remove(id);
            foreach (var (id, inputs) in AllInputs(graph))
                foreach (var name in inputs.Select(kv => kv.Key).ToList())
                {
                    if (!IsLink(inputs[name], out var src, out _) || !removed.Contains(src)) continue;
                    if (bypass.TryGetValue(src, out var through)) inputs[name] = through?.DeepClone();
                    else if (Regex.IsMatch(name, @"^picture_\d") || name.StartsWith("ref_images.", StringComparison.Ordinal)) inputs.Remove(name);
                    else throw new InvalidOperationException($"Node {id}.{name} still links to removed node {src}.");
                }

            if (!s.NoLipSync) DropNoLipSync(graph);
            SetMotionSize(graph, s.MotionSize);

            foreach (var (_, node) in graph)
                if (node?["class_type"]?.GetValue<string>() == "GrowMaskWithBlur" && TitleOf(node).Contains(" grow mask "))
                    node["inputs"]!["expand"] = MarginPixels(s.Margin);

            var plan = Inputs(graph, NodeByTitle(graph, AdvancedPlanTitle));
            var roles = Enumerable.Range(0, pictures).Select(i => Collapse(i < s.Roles.Count ? s.Roles[i] : string.Empty)).ToList();
            plan["resolution"] = s.Resolution;
            plan["seed"] = s.Seed;
            plan["clip_seconds"] = s.ClipSeconds;
            plan["roles"] = roles.Any(r => r.Length > 0)
                ? string.Join("\n", roles.Select((r, i) => r.Length > 0 ? r : Ordinal(i, pictures)))
                : string.Empty;
            return graph;
        }

        // ── Model components (applyModel) ────────────────────────────────────────────────────────────

        /// <summary>Base model, LoRA chain, steps, text encoder, VAEs and describer.</summary>
        public static void ApplyModel(JsonObject graph, SwapModel m)
        {
            Inputs(graph, NodeByTitle(graph, "Describer: Qwen3-VL 8B"))["clip_name"] = m.Describer;
            Inputs(graph, NodeByTitle(graph, "H3 Text Encoder"))["clip_name"] = m.TextEncoder;
            Inputs(graph, NodeByTitle(graph, "Video VAE"))["vae_name"] = m.VideoVae;
            Inputs(graph, NodeByTitle(graph, "Audio VAE"))["vae_name"] = m.AudioVae;
            Inputs(graph, NodeByTitle(graph, "SAM 3.1"))["ckpt_name"] = m.Sam;

            var plans = graph.Where(kv => kv.Value?["class_type"]?.GetValue<string>() is "H3OneClickSwapPlan" or "H3AdvancedSwapPlan").ToList();
            if (plans.Count != 1) throw new InvalidOperationException($"Expected one Swap Plan node in the workflow, found {plans.Count}.");
            plans[0].Value!["inputs"]!["steps"] = m.Steps;

            var unet = NodeByTitle(graph, "H3 Diffusion Model");
            var lora = NodeByTitle(graph, "DMAD LoRA");
            Inputs(graph, unet)["unet_name"] = m.Model;

            var template = Node(graph, lora);
            graph.Remove(lora);
            var last = (id: unet, slot: 0);
            var chain = m.Loras.Where(l => !string.IsNullOrWhiteSpace(l.Name) && l.Strength != 0).ToList();
            for (var i = 0; i < chain.Count; i++)
            {
                var id = i == 0 ? lora : $"{lora}_lora{i + 1}";
                var node = (JsonObject)template.DeepClone();
                var inputs = (JsonObject)node["inputs"]!;
                inputs["lora_name"] = chain[i].Name;
                inputs["strength_model"] = chain[i].Strength;
                inputs["model"] = Link(last.id, last.slot);
                node["_meta"] = new JsonObject { ["title"] = $"LoRA {i + 1}" };
                graph[id] = node;
                last = (id, 0);
            }
            foreach (var (id, inputs) in AllInputs(graph))
            {
                if (id == lora || id.StartsWith($"{lora}_lora", StringComparison.Ordinal)) continue;
                foreach (var name in inputs.Select(kv => kv.Key).ToList())
                    if (IsLink(inputs[name], out var src, out _) && src == lora) inputs[name] = Link(last.id, last.slot);
            }

            // Separate saved clips when the model components differ from upstream's defaults, so a rerun
            // never resumes from clips another model made.
            var key = string.Join("|", new[] { m.Model, m.TextEncoder, m.VideoVae, m.AudioVae, m.Describer }
                .Concat(chain.Select(l => $"{l.Name}:{l.Strength:0.###}")));
            var defaultKey = string.Join("|", new[] { DefaultModel, DefaultTextEncoder, DefaultVideoVae, DefaultAudioVae, DefaultDescriber, $"{DefaultLora}:1" });
            if (key != defaultKey && graph.Any(kv => kv.Value?["class_type"]?.GetValue<string>() == "MiniMaxH3ChainPlanModern"))
            {
                ulong hash = 14695981039346656037UL;
                foreach (var ch in key) unchecked { hash = (hash ^ ch) * 1099511628211UL; }
                SuffixRunName(graph, "model_run_name", $"_model{hash:x}", "Run name for these model components");
            }
        }

        /// <summary>Picks the installed file for a wanted name: exact, then the same file in a subfolder, then the first fallback that matches.</summary>
        public static string ResolveFile(string wanted, IReadOnlyList<string> installed, params string[] fallbackPatterns)
        {
            if (installed.Count == 0) return wanted;
            if (installed.Contains(wanted)) return wanted;
            var bySubfolder = installed.FirstOrDefault(f => f.Replace('\\', '/').EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase));
            if (bySubfolder != null) return bySubfolder;
            foreach (var pattern in fallbackPatterns)
            {
                var hit = installed.FirstOrDefault(f => Regex.IsMatch(f.Replace('\\', '/'), pattern, RegexOptions.IgnoreCase));
                if (hit != null) return hit;
            }
            return wanted;
        }

        // ── Clip estimate (lib/clips.ts) ─────────────────────────────────────────────────────────────

        /// <summary>
        /// About how many H3 clips a run takes: exact for a video without hard cuts. The pack also ends a
        /// clip on a nearby cut, which the page cannot see before a run.
        /// </summary>
        public static int ClipCount(double durationSeconds, double clipSeconds)
        {
            const int fps = 24, overlap = 39;
            static int GridFloor(int frames) => (int)Math.Floor((frames - 5) / 17.0) * 17 + 5;
            var total = (int)Math.Floor(durationSeconds * fps);
            var longest = GridFloor((int)Math.Floor(clipSeconds * fps));
            if (total < 5 || longest <= overlap) return 0;
            int count = 0, end = 0, context = 0;
            while (true)
            {
                var start = end - context;
                var length = GridFloor(Math.Min(longest, total - start));
                if (count > 0 && length - context < 17) return count;
                count++;
                end = start + length;
                context = Math.Min(overlap, GridFloor(length - context));
                if (count > 1000) return count;
            }
        }
    }

    public enum SwapMargin { Tight, Normal, Loose }

    /// <summary>One person the describer saw in the source video: a role ("the dancer") and how they look.</summary>
    public sealed record SwapPerson(int Number, string Role, string Look)
    {
        /// <summary>The form the Advanced role field takes: role, then the look after " - " to help matching.</summary>
        public string Line => Look.Length > 0 ? $"{Role} - {Look}" : Role;
        public string Display => $"{Number}. {Line}";
        public override string ToString() => Display;
    }

    public sealed class SwapSettings
    {
        public List<string> Roles { get; init; } = new();
        public string Resolution { get; init; } = "736p";
        public int MotionSize { get; init; } = 512;
        public bool HatFix { get; init; }
        public bool NoLipSync { get; init; }
        public SwapMargin Margin { get; init; } = SwapMargin.Normal;
        public string Target { get; init; } = CharacterSwapGraph.DefaultTarget;
        public long Seed { get; init; }
        public double ClipSeconds { get; init; } = CharacterSwapGraph.DefaultClipSeconds;
    }

    public sealed record SwapLora(string Name, double Strength);

    public sealed class SwapModel
    {
        public string Model { get; init; } = CharacterSwapGraph.DefaultModel;
        public List<SwapLora> Loras { get; init; } = new() { new(CharacterSwapGraph.DefaultLora, 1) };
        public int Steps { get; init; } = CharacterSwapGraph.DefaultSteps;
        public string Describer { get; init; } = CharacterSwapGraph.DefaultDescriber;
        public string TextEncoder { get; init; } = CharacterSwapGraph.DefaultTextEncoder;
        public string VideoVae { get; init; } = CharacterSwapGraph.DefaultVideoVae;
        public string AudioVae { get; init; } = CharacterSwapGraph.DefaultAudioVae;
        public string Sam { get; init; } = "sam3.1_multiplex_fp16.safetensors";
    }
}
