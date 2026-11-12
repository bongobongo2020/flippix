using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FlipPix.UI.Services
{
    /// <summary>What a Remake changes: people and characters (swap), or objects, setting, weather and light (edit).</summary>
    public enum RemakeMode { Swap, Edit }

    /// <summary>One character on the 🎞 Remake page, as the prompt writer sees it.</summary>
    public sealed record RemakeCast(string Replaces, string Name, string Look);

    /// <summary>One painted frame: where it sits in the clip (seconds from the clip's start) and whether it is pinned.</summary>
    public sealed record RemakeFrame(double Seconds, bool Pinned);

    /// <summary>What the Qwen3-VL pass said about the clip, and per picture.</summary>
    public sealed record RemakeNotes(string Scene, string Style, string Timeline, IReadOnlyList<string> Looks);

    /// <summary>Everything one render needs besides the prompt.</summary>
    public sealed class RemakeRender
    {
        public RemakeMode Mode { get; init; }
        public string Video { get; init; } = string.Empty;
        public bool HasAudio { get; init; } = true;
        public double StartSeconds { get; init; }
        public int Frames { get; init; } = RemakeGraph.DefaultFrames;
        public int ShortEdge { get; init; } = 480;
        public IReadOnlyList<string> Pictures { get; init; } = Array.Empty<string>();
        /// <summary>One output frame per picture, or empty for references only.</summary>
        public IReadOnlyList<int> Pins { get; init; } = Array.Empty<int>();
        public string Prompt { get; init; } = string.Empty;
        public long Seed { get; init; }
        public double KeyframeUntil { get; init; } = RemakeGraph.DefaultKeyframeUntil;
        /// <summary>A Remake transformer with the mode's LoRA already merged (int8 / GGUF), or the base fl2va model when <see cref="Lora"/> is set.</summary>
        public string Model { get; init; } = string.Empty;
        /// <summary>The mode's Remake LoRA on top of a base fl2va <see cref="Model"/>; null for a merged file.</summary>
        public string? Lora { get; init; }
        public string TextEncoder { get; init; } = string.Empty;
        public string VideoVae { get; init; } = string.Empty;
        public string AudioVae { get; init; } = string.Empty;
        public string OutputName { get; init; } = "remake";
        /// <summary>Lift the sampled clip ×2 in latent space and refine it there (<see cref="RemakeGraph.AddLatentUpscale"/>).</summary>
        public bool LatentUpscale { get; init; }
        /// <summary>The latent upscaler checkpoint in <c>latent_upscale_models</c>.</summary>
        public string Upscaler { get; init; } = RemakeGraph.DefaultUpscaler;
    }

    /// <summary>
    /// Viggle Remake (huggingface.co/Viggle/Viggle-Remake, Apache-2.0 code, MiniMax H3 Community License weights)
    /// on this ComfyUI. A Remake re-renders a clip keeping its motion, camera, timing and soundtrack while the
    /// painted frames decide who or what is in it.
    ///
    /// <para>The render graphs in <c>workflow/video/viggle-remake</c> are upstream's API exports, unchanged; every
    /// request is filled in here. The other graphs (frame grab, painting with Qwen Image 2.1 Edit, the Qwen3-VL
    /// prompt notes) are built in code. The six-section prompt itself is assembled here from those notes, in the
    /// shape PROMPTING.md and the MCP's short guide ask for, so a weak describer can only make a sentence vaguer,
    /// never break the format the model was trained on.</para>
    /// </summary>
    public static class RemakeGraph
    {
        public const string SwapFile = "remake_swap_api.json";
        public const string EditFile = "remake_edit_api.json";
        public const int Fps = 24;
        public const int MinFrames = 90, MaxFrames = 345, DefaultFrames = 124;
        public const double DefaultKeyframeUntil = 0.9;
        public const int MaxPictures = 4, MaxCast = 4;

        /// <summary>On a 24 GB card the int8 file renders 1280×736 only up to 124 frames (COMFYUI.md's card table).</summary>
        public const int MaxFramesAt736 = 124;

        public static readonly int[] ShortEdges = { 480, 736 };

        /// <summary>The MiniMax H3 3D latent upscaler the I2V tabs use; ×2 per side.</summary>
        public const string DefaultUpscaler = "minimax_h3_latent_upscaler_3d_fp16.safetensors";
        public const double UpscaleFactor = 2.0;
        /// <summary>The I2V tabs' three-step finish. Every sigma is under 1.0, so the finish model's own
        /// Keyframe Until (1.0) always drops the painted keyframes: they are encoded at the draft canvas and
        /// could not be laid over the doubled one. The source and the pictures stay as references.</summary>
        public const string FinishSigmas = "0.9035, 0.6316, 0.3158, 0.0";
        /// <summary>The finished short edge: the ×2 finish only runs off a 480 draft (960 out). A 736 draft
        /// doubled is 1472, far past what a 24 GB card holds.</summary>
        public const int UpscaleDraftEdge = 480;

        public const string SwapSigmas = "1.0, 0.990476, 0.965243, 0.923077, 0.8, 0.631579, 0.0";
        public const string EditSigmas = "1.0, 0.983607, 0.96, 0.923077, 0.857143, 0.705882, 0.0";

        public const string MergedSwapModel = "viggle_remake_swap_pruned_int8_convrot.safetensors";
        public const string MergedEditModel = "viggle_remake_edit_pruned_int8_convrot.safetensors";
        public const string SwapLora = "viggle_remake_swap_comfyui.safetensors";
        public const string EditLora = "viggle_remake_edit_comfyui.safetensors";
        public const string DefaultTextEncoder = "qwen3vl_32b_minimax_h3_bf16.safetensors";
        public const string DefaultVideoVae = "minimax_h3_video_vae_fp16.safetensors";
        public const string DefaultAudioVae = "minimax_h3_audio_vae_fp32.safetensors";
        public const string DefaultDescriber = "qwen3vl_8b_int8_convrot.safetensors";

        /// <summary>Node ids upstream pins in both render graphs.</summary>
        public static class Nodes
        {
            public const string Unet = "1", Lora = "2", KeyframeUntil = "3", Clip = "4", VideoVae = "5", AudioVae = "6",
                                Video = "7", Components = "8", Ref1 = "9", Ref2 = "10", Conditioning = "11", Noise = "12",
                                Sampler = "14", Sigmas = "15", Sample = "16", DecodeVideo = "17", Save = "20";

            /// <summary>The ×2 finish block <see cref="AddLatentUpscale"/> adds.</summary>
            public const string UpSplit = "up_split", Upscaler = "up_x2", UpJoin = "up_join", UpModel = "up_model",
                                UpGuider = "up_guider", UpNoise = "up_noise", UpSigmas = "up_sigmas", UpSample = "up_sample";
        }

        // ── Lengths ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>Every length the model takes: 17n+5 frames from 90 to 345.</summary>
        public static IReadOnlyList<int> FrameOptions { get; } =
            Enumerable.Range(0, 100).Select(n => 17 * n + 5).Where(f => f >= MinFrames && f <= MaxFrames).ToList();

        public static bool IsValidFrames(int frames) => frames >= MinFrames && frames <= MaxFrames && (frames - 5) % 17 == 0;

        /// <summary>The longest valid length that fits in <paramref name="seconds"/> of source (one frame spare), or 0.</summary>
        public static int LongestFit(double seconds)
        {
            var available = (int)Math.Floor(seconds * Fps) - 1;
            return FrameOptions.Where(f => f <= available).DefaultIfEmpty(0).Max();
        }

        /// <summary>The valid length nearest <paramref name="frames"/> that is no longer than <paramref name="cap"/> (0 = no cap).</summary>
        public static int Snap(int frames, int cap = 0)
        {
            var options = FrameOptions.Where(f => cap <= 0 || f <= cap).ToList();
            if (options.Count == 0) return 0;
            return options.OrderBy(f => Math.Abs(f - frames)).ThenBy(f => f).First();
        }

        /// <summary>A painted frame's place in the output: its frame index from the clip's start.</summary>
        public static int PinFor(double secondsIntoClip, int frames) =>
            Math.Clamp((int)Math.Round(secondsIntoClip * Fps), 0, Math.Max(0, frames - 1));

        public static string Stamp(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var ms = (long)Math.Round(seconds * 1000);
            return $"{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}";
        }

        // ── Shared helpers ───────────────────────────────────────────────────────────────────────────

        private static JsonArray Link(string id, int slot) => new() { id, slot };

        private static JsonObject Inputs(JsonObject graph, string id) => CharacterSwapGraph.Inputs(graph, id);

        private static JsonObject NodeOf(string classType, string title, JsonObject inputs) => new()
        {
            ["class_type"] = classType,
            ["inputs"] = inputs,
            ["_meta"] = new JsonObject { ["title"] = title },
        };

        /// <summary>Loads the source at 24 fps from <paramref name="startSeconds"/>, <paramref name="frames"/> frames (0 = to the end).</summary>
        private static JsonObject LoadVideoNode(string video, double startSeconds, int frames, int everyNth = 1) =>
            NodeOf("VHS_LoadVideo", "Source clip (24 fps)", new JsonObject
            {
                ["video"] = video,
                ["force_rate"] = Fps,
                ["custom_width"] = 0,
                ["custom_height"] = 0,
                ["frame_load_cap"] = frames,
                ["skip_first_frames"] = Math.Max(0, (int)Math.Round(startSeconds * Fps)),
                ["select_every_nth"] = everyNth,
                ["format"] = "None",
            });

        // ── Render ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Fills upstream's render graph for one request. The source comes in through VHS at 24 fps from the chosen
        /// start (upstream's LoadVideo takes the whole file at its own rate), so the window and the frame rate are
        /// the clip's, as <c>sample.py</c> converts them. A source with no audio stream leaves the soundtrack input
        /// unlinked, which the conditioning node reads as silence (VHS throws as soon as anything reads a missing
        /// soundtrack). A merged int8/GGUF file replaces the base model and the LoRA loader, as COMFYUI.md says.
        /// </summary>
        public static JsonObject BuildRender(JsonObject template, RemakeRender r)
        {
            if (r.Pictures.Count is < 1 or > MaxPictures)
                throw new ArgumentException($"A Remake takes 1 to {MaxPictures} painted frames, got {r.Pictures.Count}.");
            if (!IsValidFrames(r.Frames))
                throw new ArgumentException($"Frames must be 17n+5 from {MinFrames} to {MaxFrames}, got {r.Frames}.");
            if (r.Pins.Count != 0 && r.Pins.Count != r.Pictures.Count)
                throw new ArgumentException("Give one pin per painted frame, or none.");
            if (r.Pins.Any(p => p < 0 || p >= r.Frames))
                throw new ArgumentException($"Pins must be inside the clip (0 to {r.Frames - 1}).");

            var graph = (JsonObject)template.DeepClone();

            // Model: merged Remake file straight into Keyframe Until, or base + bypass LoRA.
            Inputs(graph, Nodes.Unet)["unet_name"] = r.Model;
            if (string.IsNullOrEmpty(r.Lora))
            {
                graph.Remove(Nodes.Lora);
                Inputs(graph, Nodes.KeyframeUntil)["model"] = Link(Nodes.Unet, 0);
                graph[Nodes.Unet]!["_meta"]!["title"] = "Load Viggle Remake transformer (LoRA merged)";
            }
            else
            {
                Inputs(graph, Nodes.Lora)["lora_name"] = r.Lora;
            }
            Inputs(graph, Nodes.KeyframeUntil)["keyframe_until"] = r.KeyframeUntil;
            Inputs(graph, Nodes.Clip)["clip_name"] = r.TextEncoder;
            Inputs(graph, Nodes.VideoVae)["vae_name"] = r.VideoVae;
            Inputs(graph, Nodes.AudioVae)["vae_name"] = r.AudioVae;

            // Source window through VHS.
            graph.Remove(Nodes.Components);
            graph[Nodes.Video] = LoadVideoNode(r.Video, r.StartSeconds, r.Frames);
            var cond = Inputs(graph, Nodes.Conditioning);
            cond["source"] = Link(Nodes.Video, 0);
            if (r.HasAudio) cond["audio"] = Link(Nodes.Video, 2);
            else cond.Remove("audio");

            // Painted frames.
            graph.Remove(Nodes.Ref1);
            graph.Remove(Nodes.Ref2);
            for (var i = 1; i <= MaxPictures; i++) cond.Remove($"ref_{i}");
            for (var i = 0; i < r.Pictures.Count; i++)
            {
                var id = $"ref{i + 1}";
                graph[id] = NodeOf("LoadImage", $"Painted frame {i + 1}", new JsonObject { ["image"] = r.Pictures[i] });
                cond[$"ref_{i + 1}"] = Link(id, 0);
            }

            cond["prompt"] = r.Prompt;
            cond["pins"] = string.Join(", ", r.Pins);
            cond["frames"] = r.Frames;
            cond["short_edge"] = r.ShortEdge;

            Inputs(graph, Nodes.Noise)["seed"] = r.Seed;
            Inputs(graph, Nodes.Sigmas)["sigmas"] = r.Mode == RemakeMode.Swap ? SwapSigmas : EditSigmas;
            Inputs(graph, Nodes.Save)["filename_prefix"] = $"viggle_remake/{SafeName(r.OutputName)}";

            if (r.LatentUpscale) AddLatentUpscale(graph, r);

            CharacterSwapGraph.AssertNoDanglingLinks(graph);
            return graph;
        }

        /// <summary>
        /// The I2V tabs' draft → ×2 → finish scheme on a Remake: the sampled video latent is split from its
        /// audio, doubled by MiniMax H3's 3D latent upscaler, joined back and refined in three steps with the
        /// same Remake conditioning, so the source clip and the painted frames still steer the detail. Only the
        /// picture is taken from the finish; the soundtrack is decoded from the draft, where it was held clean.
        /// Measured on the 4090: 124 frames 480 → 1664×960 in about 2:30, 260 frames in about 7:20.
        /// </summary>
        public static void AddLatentUpscale(JsonObject graph, RemakeRender r)
        {
            if (r.ShortEdge != UpscaleDraftEdge)
                throw new ArgumentException($"The ×2 finish runs off a {UpscaleDraftEdge} draft, not {r.ShortEdge}.");

            graph[Nodes.UpSplit] = NodeOf("LTXVSeparateAVLatent", "Split video and audio", new JsonObject
            {
                ["av_latent"] = Link(Nodes.Sample, 0),
            });
            graph[Nodes.Upscaler] = NodeOf("MinimaxH3LatentUpscaler3D", "Latent upscale ×2", new JsonObject
            {
                ["latent"] = Link(Nodes.UpSplit, 0),
                ["model_name"] = r.Upscaler,
                ["mode"] = "scale by multiplier",
                ["mode.scale"] = UpscaleFactor,
                ["align"] = 32,
                ["enable_temporal_chunking"] = true,
                ["force_unload"] = true,
                ["device"] = "cuda",
                ["precision"] = "fp16",
            });
            graph[Nodes.UpJoin] = NodeOf("LTXVConcatAVLatent", "Join video and audio", new JsonObject
            {
                ["video_latent"] = Link(Nodes.Upscaler, 0),
                ["audio_latent"] = Link(Nodes.UpSplit, 1),
            });
            // The same transformer the draft used (whatever feeds Keyframe Until), with keyframes always off.
            graph[Nodes.UpModel] = NodeOf("RemakeKeyframeUntil", "Finish: references only", new JsonObject
            {
                ["model"] = Inputs(graph, Nodes.KeyframeUntil)["model"]!.DeepClone(),
                ["keyframe_until"] = 1.0,
            });
            graph[Nodes.UpGuider] = NodeOf("BasicGuider", "Finish guider", new JsonObject
            {
                ["model"] = Link(Nodes.UpModel, 0),
                ["conditioning"] = Link(Nodes.Conditioning, 0),
            });
            graph[Nodes.UpNoise] = NodeOf("RandomNoise", "Finish noise", new JsonObject { ["noise_seed"] = r.Seed });
            graph[Nodes.UpSigmas] = NodeOf("ManualSigmas", "Finish sigmas (3 steps)", new JsonObject { ["sigmas"] = FinishSigmas });
            graph[Nodes.UpSample] = NodeOf("SamplerCustomAdvanced", "Finish ×2", new JsonObject
            {
                ["noise"] = Link(Nodes.UpNoise, 0),
                ["guider"] = Link(Nodes.UpGuider, 0),
                ["sampler"] = Link(Nodes.Sampler, 0),
                ["sigmas"] = Link(Nodes.UpSigmas, 0),
                ["latent_image"] = Link(Nodes.UpJoin, 0),
            });
            Inputs(graph, Nodes.DecodeVideo)["samples"] = Link(Nodes.UpSample, 0);
        }

        public static string SafeName(string name)
        {
            var cleaned = Regex.Replace(name ?? string.Empty, @"[^A-Za-z0-9_\-]+", "_").Trim('_');
            if (cleaned.Length > 48) cleaned = cleaned[..48];
            return cleaned.Length == 0 ? "remake" : cleaned;
        }

        // ── Frame grab ───────────────────────────────────────────────────────────────────────────────

        public const string GrabSave = "2";

        /// <summary>One frame of the source at <paramref name="seconds"/>, at 24 fps (the frame the render will see there).</summary>
        public static JsonObject BuildGrabFrame(string video, double seconds) => new()
        {
            ["1"] = LoadVideoNode(video, seconds, 1),
            [GrabSave] = NodeOf("SaveImage", "Frame", new JsonObject
            {
                ["filename_prefix"] = "viggle_remake/frames/frame",
                ["images"] = Link("1", 0),
            }),
        };

        // ── Paint (Qwen Image 2.1 Edit) ──────────────────────────────────────────────────────────────

        /// <summary>Node ids in <c>workflow/image/qwen-edit/qwen21-edit-charswapAPI.json</c>.</summary>
        public static class PaintNodes
        {
            public const string Frame = "470", Character = "475", Encode = "474", Sampler = "458", Save = "461";
        }

        public const string PaintWorkflow = "qwen21-edit-charswapAPI.json";

        /// <summary>
        /// Paints a source frame with Qwen Image 2.1 Edit: <c>&lt;image1&gt;</c> is the frame, then one picture per
        /// character photo. With no photos (edit mode, or a character described in words) only the frame goes in.
        /// </summary>
        public static JsonObject BuildPaint(JsonObject template, string frame, IReadOnlyList<string> photos, string instruction,
                                            long seed, int resolution = 0)
        {
            var graph = (JsonObject)template.DeepClone();
            Inputs(graph, PaintNodes.Frame)["image"] = frame;
            var encode = Inputs(graph, PaintNodes.Encode);
            graph.Remove(PaintNodes.Character);
            foreach (var key in encode.Select(kv => kv.Key).Where(k => k.StartsWith("images.", StringComparison.Ordinal)).ToList())
                encode.Remove(key);
            encode["images.image_1"] = Link(PaintNodes.Frame, 0);
            for (var i = 0; i < photos.Count; i++)
            {
                var id = $"photo{i + 1}";
                graph[id] = NodeOf("LoadImage", $"Character {i + 1}", new JsonObject { ["image"] = photos[i] });
                encode[$"images.image_{i + 2}"] = Link(id, 0);
            }
            encode["prompt"] = instruction;
            encode["resolution"] = resolution;
            Inputs(graph, PaintNodes.Sampler)["seed"] = seed;
            Inputs(graph, PaintNodes.Save)["filename_prefix"] = "viggle_remake/paint";
            CharacterSwapGraph.AssertNoDanglingLinks(graph);
            return graph;
        }

        private const string KeepFrame =
            "Keep everything else in <image1> exactly as it is: the camera angle, framing, composition, background, lighting, " +
            "the other people and objects, and every pose and position. Photorealistic, sharp, the same image quality as <image1>.";

        /// <summary>
        /// The edit instruction for one frame. Swap: each character replaces the person named, from its photo
        /// (<c>&lt;image2&gt;</c>, …) or from its description. Edit: the frame shows the target description.
        /// </summary>
        public static string PaintInstruction(RemakeMode mode, IReadOnlyList<(RemakeCast Cast, bool HasPhoto)> cast, string change)
        {
            if (mode == RemakeMode.Edit)
            {
                var target = Sentence(change);
                return $"Edit <image1> so that it shows {Lower(target.TrimEnd('.'))}. {KeepFrame}";
            }

            var parts = new List<string>();
            var image = 2;
            foreach (var (c, hasPhoto) in cast)
            {
                var who = Who(c.Replaces);
                var look = string.IsNullOrWhiteSpace(c.Look) ? string.Empty : $" ({c.Look.Trim().TrimEnd('.')})";
                parts.Add(hasPhoto
                    ? $"replace {who} in <image1> with the character from <image{image++}>{look}, with the same pose, body position, size and expression"
                    : $"replace {who} in <image1> with {(string.IsNullOrWhiteSpace(c.Look) ? (string.IsNullOrWhiteSpace(c.Name) ? "a new character" : c.Name.Trim()) : c.Look.Trim().TrimEnd('.'))}, with the same pose, body position, size and expression");
            }
            var extra = string.IsNullOrWhiteSpace(change) ? string.Empty : $" {Sentence(change)}";
            return $"{Capitalize(string.Join("; ", parts))}.{extra} {KeepFrame}";
        }

        private static string Who(string replaces) =>
            string.IsNullOrWhiteSpace(replaces) ? "the main person" : replaces.Trim().TrimEnd('.');

        // ── Notes (Qwen3-VL) ─────────────────────────────────────────────────────────────────────────

        public const string SceneOutput = "out_scene", TimelineOutput = "out_timeline";
        public static string LookOutput(int i) => $"out_look{i + 1}";

        /// <summary>
        /// One pass of Qwen3-VL over the clip (core TextGenerate samples a 24 fps batch at 1 fps) and over each
        /// picture it should describe. Each answer is a small, separate question, which an 8B describer gets right
        /// far more often than a whole six-section prompt.
        /// </summary>
        /// <param name="lookImages">Per cast member (swap) or the first painted frame (edit): the image to describe, or null to skip.</param>
        public static JsonObject BuildNotes(string video, double startSeconds, int frames, string describer, RemakeMode mode,
                                            IReadOnlyList<RemakeCast> cast, IReadOnlyList<string?> lookImages, string change = "")
        {
            var seconds = frames / (double)Fps;
            var (nth, count) = NotesSampling(frames);
            var graph = new JsonObject
            {
                ["clip"] = NodeOf("CLIPLoader", "Describer: Qwen3-VL", new JsonObject
                {
                    ["clip_name"] = describer, ["type"] = "stable_diffusion", ["device"] = "default",
                }),
                ["video"] = LoadVideoNode(video, startSeconds, count, nth),
                ["small"] = NodeOf("ImageScaleToMaxDimension", "Shrink for the describer", new JsonObject
                {
                    ["upscale_method"] = "area", ["largest_size"] = 512, ["image"] = Link("video", 0),
                }),
            };

            void Ask(string id, string output, string prompt, string? imageNode, bool video, int maxLength)
            {
                var inputs = new JsonObject
                {
                    ["clip"] = Link("clip", 0),
                    ["prompt"] = prompt,
                    ["max_length"] = maxLength,
                    ["sampling_mode"] = "off",
                    ["thinking"] = false,
                    ["use_default_template"] = true,
                };
                // The frames go in as a picture batch with their times spelled out. Through TextGenerate's video
                // input (resampled to 1 fps) the 8B describer lost track of time and invented a whole other video.
                if (video) inputs["image"] = Link("small", 0);
                if (imageNode != null) inputs["image"] = Link(imageNode, 0);
                graph[id] = NodeOf("TextGenerate", id, inputs);
                graph[output] = NodeOf("PreviewAny", output, new JsonObject { ["source"] = Link(id, 0) });
            }

            var times = FrameTimes(frames, nth, count);
            Ask("ask_scene", SceneOutput, ScenePrompt(seconds, times), null, true, 200);
            if (mode == RemakeMode.Swap)
                Ask("ask_timeline", TimelineOutput, TimelinePrompt(mode, cast, seconds, times, change), null, true, 450);
            for (var i = 0; i < lookImages.Count; i++)
            {
                if (lookImages[i] is not { } image) continue;
                var load = $"look_img{i + 1}";
                graph[load] = NodeOf("LoadImage", $"Picture to describe {i + 1}", new JsonObject { ["image"] = image });
                graph[$"look_small{i + 1}"] = NodeOf("ImageScaleToMaxDimension", "Shrink", new JsonObject
                {
                    ["upscale_method"] = "area", ["largest_size"] = 768, ["image"] = Link(load, 0),
                });
                var prompt = mode == RemakeMode.Swap ? LookPrompt(i < cast.Count ? cast[i] : null) : SettingPrompt();
                Ask($"ask_look{i + 1}", LookOutput(i), prompt, $"look_small{i + 1}", false, 220);
            }
            CharacterSwapGraph.AssertNoDanglingLinks(graph);
            return graph;
        }

        /// <summary>About eight frames spread over the clip: every <c>nth</c> frame, <c>count</c> of them.</summary>
        public static (int Nth, int Count) NotesSampling(int frames)
        {
            var nth = Math.Max(1, (int)Math.Ceiling(frames / 8.0));
            return (nth, (frames + nth - 1) / nth);
        }

        public static IReadOnlyList<double> FrameTimes(int frames, int nth, int count) =>
            Enumerable.Range(0, count).Select(i => Math.Min(frames - 1, i * nth) / (double)Fps).ToList();

        private static string Frames(IReadOnlyList<double> times) =>
            string.Join(", ", times.Select((t, i) => $"picture {i + 1} at {Stamp(t)}"));

        public static string ScenePrompt(double seconds, IReadOnlyList<double> times) =>
            $"These {times.Count} pictures are frames of one {seconds:0.#} second video, in time order ({Frames(times)}). " +
            "Describe only what you can see in them. Answer in exactly two lines. " +
            "Line 1 starts with \"Scene:\" and says in one sentence what the video shows: the place, the camera (static, handheld, " +
            "panning, zooming) and who or what is in it. " +
            "Line 2 starts with \"Style:\" and names the look in a few words: realism or animation, the lighting, the colour, the camera style. " +
            "Never write the word dim.";

        public static string TimelinePrompt(RemakeMode mode, IReadOnlyList<RemakeCast> cast, double seconds, IReadOnlyList<double> times, string change = "")
        {
            var sb = new StringBuilder();
            sb.Append($"These {times.Count} pictures are frames of one {seconds:0.#} second video, in time order: {Frames(times)}. ");
            sb.Append($"The video ends at {Stamp(seconds)}; nothing happens after that. Describe only what you can see in the pictures. ");
            if (mode == RemakeMode.Swap && cast.Count > 0)
            {
                sb.Append("In this video, ");
                sb.Append(string.Join("; ", cast.Select((c, i) => $"{Who(c.Replaces)} is called <Subject {i + 1}>")));
                sb.Append(". Always call them that, and never describe their face, hair or clothes. ");
            }
            sb.Append($"Write what happens, in time order, as 3 to {Math.Clamp(times.Count, 3, 8)} short present-tense sentences, one per line. ");
            sb.Append("Start every line with its time as \"At MM:SS.mmm\" (for example \"At 00:02.500\"). ");
            sb.Append("Say where each person or thing is in the frame, what they do and how the camera moves. ");
            sb.Append("When the video cuts to a different shot, start that line with \"CUT\" before the time. ");
            sb.Append("Do not invent spoken words. Do not write anything except these lines.");
            if (mode == RemakeMode.Edit)
            {
                sb.Append(" Describe the motion and the camera only, not the colours, weather or light.");
                if (!string.IsNullOrWhiteSpace(change))
                    sb.Append($" In the finished video the scene is changed to: {change.Trim().TrimEnd('.')}. Never name anything this change removes or replaces.");
            }
            return sb.ToString();
        }

        public static string LookPrompt(RemakeCast? cast) =>
            "Describe the main character in this picture in one or two sentences, specific enough to tell them apart from anyone else: " +
            "what they are (person, animal, creature, mascot), then face, hair, body, clothes, colours and anything they carry. " +
            "Describe only what is visible. Start with the noun, for example \"a woman with ...\" or \"a plush dinosaur mascot with ...\"." +
            (cast != null && !string.IsNullOrWhiteSpace(cast.Name) ? $" The character is called {cast.Name.Trim()}; do not repeat the name." : string.Empty);

        public static string SettingPrompt() =>
            "Describe this picture in two sentences: the place, the sky and weather, the light, and the main objects, with their colours. " +
            "Describe only what is visible, as it is now. Never write the word dim.";

        /// <summary>Reads the describer's "Scene:" / "Style:" answer.</summary>
        public static (string Scene, string Style) ParseScene(string? text)
        {
            string scene = string.Empty, style = string.Empty;
            foreach (var raw in (text ?? string.Empty).Split('\n'))
            {
                var line = raw.Trim().Trim('*').Trim();
                if (line.StartsWith("Scene:", StringComparison.OrdinalIgnoreCase)) scene = line[6..].Trim();
                else if (line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase)) style = line[6..].Trim();
                else if (scene.Length == 0 && line.Length > 0) scene = line;
            }
            return (CleanSentence(scene), CleanSentence(style));
        }

        /// <summary>One sentence of the timeline: its time and whether a cut opens it.</summary>
        public sealed record Beat(double Seconds, bool Cut, string Text);

        private static readonly Regex BeatLine = new(
            @"^\s*[-*\d.)\s]*(?<cut>CUT\b[\s:,\-–]*)?(?:(?:At|From)\s+)?(?<m>\d{1,2}):(?<s>\d{2})(?:[.,](?<f>\d{1,3}))?(?:\s*(?:-|–|to)\s*\d{1,2}:\d{2}(?:[.,]\d{1,3})?)?\s*[,:\-–]?\s*(?<text>.+)$",
            RegexOptions.IgnoreCase);

        /// <summary>Reads the describer's "At MM:SS.mmm …" lines, clamped to the clip and in time order.</summary>
        public static List<Beat> ParseTimeline(string? text, double clipSeconds)
        {
            var beats = new List<Beat>();
            foreach (var raw in (text ?? string.Empty).Split('\n'))
            {
                var m = BeatLine.Match(raw.Trim());
                if (!m.Success) continue;
                var seconds = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) * 60
                              + int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture)
                              + (m.Groups["f"].Success ? double.Parse("0." + m.Groups["f"].Value, CultureInfo.InvariantCulture) : 0);
                var body = CleanSentence(m.Groups["text"].Value);
                if (body.Length == 0) continue;
                // A beat past the end is the describer guessing; drop it rather than pile it on the last frame.
                if (seconds > clipSeconds + 0.25) continue;
                beats.Add(new Beat(Math.Clamp(seconds, 0, Math.Max(0, clipSeconds - 1.0 / Fps)), m.Groups["cut"].Success, body));
            }
            beats.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
            if (beats.Count > 0 && beats[0].Cut) beats[0] = beats[0] with { Cut = false };
            return beats;
        }

        // ── The six-section prompt ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// The six-section prompt for one render, in the shape of the examples upstream ships (PROMPTING.md, the
        /// MCP's short guide): it describes the target video, never gives an instruction, places every picture at its
        /// time and shot, and says in <c>retention_analysis</c> what changes — that section is obeyed literally.
        /// </summary>
        /// <param name="frames">The clip as painted frames (seconds into the clip), in picture order.</param>
        /// <param name="change">Edit: what the result shows. Swap: an optional extra sentence about the result.</param>
        public static string ComposePrompt(RemakeMode mode, int frameCount, IReadOnlyList<RemakeFrame> frames,
                                           IReadOnlyList<RemakeCast> cast, string change, RemakeNotes notes)
        {
            var seconds = frameCount / (double)Fps;
            var end = Stamp((frameCount - 1) / (double)Fps);
            // Edit: no beat-by-beat timeline. The describer sees the source, so its beats name the very things the
            // edit removes ("approaching the nearest wind turbine"), and the model obeys them. Upstream's edit prompts
            // describe the target over the whole shot instead, with the motion taken from <Video 1>.
            var beats = mode == RemakeMode.Swap ? ParseTimeline(notes.Timeline, seconds) : new List<Beat>();
            var cuts = beats.Where(b => b.Cut).Select(b => b.Seconds).ToList();
            int ShotAt(double t) => 1 + cuts.Count(c => c <= t + 1e-6);
            var shots = 1 + cuts.Count;
            var pics = frames.Select((f, i) => (Label: $"<Picture {i + 1}>", f.Seconds, f.Pinned, Shot: ShotAt(f.Seconds))).ToList();
            var allPics = JoinAnd(pics.Select(p => p.Label));
            var scene = notes.Scene.Length > 0 ? Lower(notes.Scene.TrimEnd('.')) : "a short video clip";
            var style = notes.Style.Length > 0 ? notes.Style.TrimEnd('.') : "realistic, natural lighting and colour, the camera as in <Video 1>";
            var shotList = shots == 1 ? "the single continuous shot" : $"the {shots} shots and the cuts at {JoinAnd(cuts.Select(Stamp))}";

            var sb = new StringBuilder();

            // subject_definitions
            sb.AppendLine("subject_definitions:");
            sb.AppendLine(mode == RemakeMode.Swap
                ? $"<Video 1> is the source video for the editing task: {scene}, {frameCount} frames at 24 fps."
                : $"<Video 1> is the source video for the target video edit: {scene}.");
            sb.AppendLine(PicturesLine(mode, pics.Select(p => (p.Label, p.Seconds, p.Pinned, p.Shot)).ToList()));
            if (mode == RemakeMode.Swap)
            {
                for (var i = 0; i < cast.Count; i++)
                {
                    var c = cast[i];
                    var look = LookOf(c, notes, i);
                    var name = string.IsNullOrWhiteSpace(c.Name) ? string.Empty : $"{c.Name.Trim()}, ";
                    sb.AppendLine($"<Subject {i + 1}> is {name}{look}, as in {allPics}. This character replaces {Who(c.Replaces)} in <Video 1>.");
                }
            }
            else
            {
                var target = Lower(Sentence(change).TrimEnd('.'));
                var painted = notes.Looks.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) is { } p ? $" {Sentence(p)}" : string.Empty;
                sb.AppendLine($"<Subject 1> is the setting from <Video 1>, now {target}.{painted}");
            }
            sb.AppendLine();

            // summary
            sb.AppendLine("summary:");
            if (mode == RemakeMode.Swap)
            {
                var becomes = JoinAnd(cast.Select((c, i) =>
                    $"{Who(c.Replaces)} becomes <Subject {i + 1}>{(string.IsNullOrWhiteSpace(c.Name) ? "" : $", {c.Name.Trim()}")}"));
                var extra = string.IsNullOrWhiteSpace(change) ? string.Empty : $" {Sentence(change)}";
                sb.AppendLine($"[video editing + character replacement] The target video is an edited version of <Video 1> in which {becomes}, " +
                              $"looking exactly as in {allPics}.{extra} All other people, the place and the soundtrack are unchanged; " +
                              $"{shotList}, the camera, the motion and the timing of <Video 1> are kept.");
            }
            else
            {
                var target = Lower(Sentence(change).TrimEnd('.'));
                sb.AppendLine("[video editing] The target video is an edited version of <Video 1> with exactly the same framing, camera, motion " +
                              $"and timing, but from the very first frame to the last it shows {target}.");
            }
            sb.AppendLine();

            // retention_analysis
            sb.AppendLine("retention_analysis:");
            if (mode == RemakeMode.Swap)
            {
                sb.AppendLine($"<Video 1> (source video editing): fully_preserved - {shotList}, the camera framing and movement, the location, " +
                              "the lighting, the background, the unchanged people, and the motion and timing of every performance are copied frame for frame.");
                foreach (var p in pics)
                    sb.AppendLine($"{p.Label} (appears in [Shot {p.Shot}]): reference - a frame of the target video at {Stamp(p.Seconds)}. " +
                                  $"The characters in it, their faces, hair and clothing, and the background, framing and lighting are taken from {p.Label}." +
                                  (pics.Count > 1 && p == pics[^1] ? " A character shown in several pictures is the same character." : string.Empty));
                for (var i = 0; i < cast.Count; i++)
                {
                    var appears = string.Join(", ", Enumerable.Range(1, shots).Select(s => $"[Shot {s}]"));
                    sb.AppendLine($"<Subject {i + 1}> (appears in {appears}): fully_preserved - {LookOf(cast[i], notes, i)} stay identical in every frame, " +
                                  "also in motion, in wide shots and close to the camera.");
                }
            }
            else
            {
                sb.AppendLine($"<Video 1> (source video editing): fully_preserved - {shotList}, the framing and camera movement, the motion and timing " +
                              "of everything that moves, and the shape and position of everything the edit does not touch are copied frame for frame.");
                foreach (var p in pics)
                    sb.AppendLine($"{p.Label} (target keyframe): fully_preserved - the target video passes through this exact frame at {Stamp(p.Seconds)}.");
                sb.AppendLine($"<Subject 1> (appears in [Shot 1]): changed - everything keeps its place and shape; only what the edit describes changes, " +
                              $"in every frame and in every part of the frame, including the edges: {Lower(Sentence(change).TrimEnd('.'))}, as in {allPics}.");
            }
            sb.AppendLine();

            // detailed_description
            sb.AppendLine("detailed_description:");
            sb.AppendLine($"The look of the target video: {LowerFirst(style)}. No added captions, titles or logos.");
            var events = beats.Select(b => (b.Seconds, Order: 0, Text: b.Text, b.Cut)).ToList();
            foreach (var p in pics)
                events.Add((p.Seconds, 1, $"At {Stamp(p.Seconds)} the frame matches {p.Label}.", false));
            events = events.OrderBy(e => e.Seconds).ThenBy(e => e.Order).ToList();

            var shot = 1;
            var line = new StringBuilder("[Shot 1] ");
            if (mode == RemakeMode.Edit)
                line.Append($"From 00:00.000 to {end} the video shows {Lower(Sentence(change).TrimEnd('.'))}. ");
            if (beats.Count == 0)
                line.Append("The camera, every movement and the timing are exactly as in <Video 1>. ");
            foreach (var e in events)
            {
                if (e.Cut)
                {
                    sb.AppendLine(line.ToString().TrimEnd());
                    shot++;
                    line = new StringBuilder($"[Shot {shot}] At {Stamp(e.Seconds)}, the shot cuts to {Lower(StripTime(e.Text))} ");
                    continue;
                }
                line.Append(e.Order == 0 ? $"At {Stamp(e.Seconds)} {Lower(StripTime(e.Text))} " : $"{e.Text} ");
            }
            if (mode == RemakeMode.Swap && cast.Count > 0)
            {
                var keeps = JoinAnd(cast.Select((c, i) => $"<Subject {i + 1}>"));
                line.Append($"In every frame, from the first to the last, {keeps} {(cast.Count == 1 ? "keeps" : "keep")} exactly the look in {allPics}; " +
                            $"only {(cast.Count == 1 ? "this character changes" : "these characters change")}, and everything else stays as in <Video 1>.");
            }
            else if (mode == RemakeMode.Edit)
            {
                line.Append("The change stays the same to the end; everything else moves exactly as in <Video 1>.");
            }
            sb.AppendLine(line.ToString().TrimEnd());
            sb.AppendLine();

            sb.AppendLine("overall_soundscape:");
            sb.AppendLine("The source soundtrack of <Video 1> continues throughout, unchanged.");
            sb.AppendLine();
            sb.AppendLine("non_diegetic_music:");
            sb.AppendLine("N/A");
            return ToAscii(sb.ToString());
        }

        private static string PicturesLine(RemakeMode mode, List<(string Label, double Seconds, bool Pinned, int Shot)> pics)
        {
            if (mode == RemakeMode.Edit)
                return pics.Count == 1
                    ? $"{pics[0].Label} is a frame of the target video, exactly as the target video shows it at {Stamp(pics[0].Seconds)}."
                    : $"{JoinAnd(pics.Select(p => p.Label))} are frames of the target video, exactly as the target video shows them, " +
                      $"{JoinAnd(pics.Select(p => $"{p.Label} at {Stamp(p.Seconds)}"))}.";
            return pics.Count == 1
                ? $"{pics[0].Label} is a frame of the target video, from [Shot {pics[0].Shot}] at {Stamp(pics[0].Seconds)}."
                : $"{JoinAnd(pics.Select(p => p.Label))} are frames of the target video, " +
                  $"{JoinAnd(pics.Select(p => $"{p.Label} from [Shot {p.Shot}] at {Stamp(p.Seconds)}"))}.";
        }

        private static string LookOf(RemakeCast c, RemakeNotes notes, int i)
        {
            if (!string.IsNullOrWhiteSpace(c.Look)) return Lower(c.Look.Trim().TrimEnd('.'));
            if (i < notes.Looks.Count && !string.IsNullOrWhiteSpace(notes.Looks[i])) return Lower(notes.Looks[i].Trim().TrimEnd('.'));
            return "the character exactly as painted";
        }

        // ── Text helpers ─────────────────────────────────────────────────────────────────────────────

        private static readonly Regex LeadingTime = new(@"^(?:At|From)\s+\d{1,2}:\d{2}(?:[.,]\d{1,3})?\s*[,:\-–]?\s*", RegexOptions.IgnoreCase);
        private static string StripTime(string text) => LeadingTime.Replace(text, string.Empty);

        public static string CleanSentence(string text)
        {
            var t = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim().Trim('"', '*').Trim();
            t = Regex.Replace(t, @"\bdimly lit\b", "softly lit", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\bdim\b", "soft", RegexOptions.IgnoreCase);
            return t;
        }

        private static string Sentence(string text)
        {
            var t = CleanSentence(text);
            if (t.Length == 0) return t;
            return char.IsPunctuation(t[^1]) ? t : t + ".";
        }

        private static string Lower(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // "The"/"A" at the start becomes lower case; a name or "<Subject 1>" stays.
            var first = text.Split(' ')[0];
            return first is "The" or "A" or "An" or "Some" or "Two" or "Three" or "It" or "There" or "In" or "On" or "He" or "She" or "They"
                ? char.ToLowerInvariant(text[0]) + text[1..]
                : text;
        }

        private static string LowerFirst(string text) =>
            string.IsNullOrEmpty(text) || (text.Length > 1 && char.IsUpper(text[1])) ? text : char.ToLowerInvariant(text[0]) + text[1..];

        private static string Capitalize(string text) =>
            string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];

        private static string Article(string phrase)
        {
            var t = phrase.TrimStart();
            return t.Length > 0 && "aeiouAEIOU".Contains(t[0]) ? "an" : "a";
        }

        private static string JoinAnd(IEnumerable<string> items)
        {
            var list = items.ToList();
            return list.Count switch
            {
                0 => string.Empty,
                1 => list[0],
                2 => $"{list[0]} and {list[1]}",
                _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
            };
        }

        /// <summary>Plain ASCII punctuation, as upstream's prompt writer uses.</summary>
        public static string ToAscii(string text) => text
            .Replace('—', '-').Replace('–', '-').Replace('‑', '-').Replace('−', '-')
            .Replace('‘', '\'').Replace('’', '\'').Replace('“', '"').Replace('”', '"').Replace("…", "...");

        public static readonly string[] Sections =
            { "subject_definitions:", "summary:", "retention_analysis:", "detailed_description:", "overall_soundscape:", "non_diegetic_music:" };

        /// <summary>Problems with a hand-edited prompt: missing or out-of-order sections, pictures it never mentions.</summary>
        public static List<string> CheckPrompt(string prompt, int pictures)
        {
            var problems = new List<string>();
            var at = -1;
            foreach (var s in Sections)
            {
                var i = prompt.IndexOf(s, StringComparison.Ordinal);
                if (i < 0) problems.Add($"missing \"{s}\"");
                else if (i < at) problems.Add($"\"{s}\" is out of order");
                else at = i;
            }
            for (var p = 1; p <= pictures; p++)
                if (!prompt.Contains($"<Picture {p}>", StringComparison.Ordinal)) problems.Add($"never mentions <Picture {p}>");
            if (pictures < MaxPictures && prompt.Contains($"<Picture {pictures + 1}>", StringComparison.Ordinal))
                problems.Add($"mentions <Picture {pictures + 1}> but there are only {pictures} painted frames");
            return problems;
        }
    }
}
