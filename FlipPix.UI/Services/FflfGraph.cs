using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.UI.Services
{
    /// <summary>One story beat from the FFLF tab's text box.</summary>
    public sealed record FflfBeatText(string Title, string Text);

    /// <summary>Everything one FFLF beat render needs, already uploaded to the server.</summary>
    public sealed class FflfBeatRender
    {
        /// <summary>The image the beat starts on: the initial image for beat 1, the previous beat's last frame after.</summary>
        public string StartImage { get; init; } = string.Empty;
        /// <summary>The initial image again as a character reference (beats 2+), or null.</summary>
        public string? IdentityImage { get; init; }
        public string Prompt { get; init; } = string.Empty;
        public double Seconds { get; init; } = 5;
        public long Seed { get; init; }
        public string AspectRatio { get; init; } = "16:9 (Widescreen)";
        public double FirstPassMegapixels { get; init; } = 0.3;
        public double FinalMegapixels { get; init; } = 1.0;
        public int FirstPassSteps { get; init; } = 8;
        public bool PreviewOnly { get; init; }
        public string DiffusionModel { get; init; } = H3FflfSettings.DefaultModel;
        public string Lora { get; init; } = H3FflfSettings.DefaultLora;
        public double LoraStrength { get; init; } = 0.25;
        public bool LockFirstFrame { get; init; } = true;
        /// <summary>SaveVideo prefix; the last frame is saved beside it as &lt;prefix&gt;_last.</summary>
        public string OutputPrefix { get; init; } = "video/fflf/beat";
    }

    /// <summary>
    /// The ⛓ FFLF tab's graph: one story beat through 🌀 MiniMax I2V's ErosMax 2-Pass workflow
    /// (h3-erosmax-2pass.json, built by <see cref="MiniMaxI2VViewModel.BuildTwoPassGraph"/>), with two additions:
    ///
    /// <list type="bullet">
    /// <item><b>First frame.</b> The image the beat starts on is anchored at frame 0 of both passes with
    /// MiniMaxH3AddGuide — the node the graph's own Continue mode uses for its guide window, here fed one still — and
    /// is also reference <c>&lt;Picture 1&gt;</c>.</item>
    /// <item><b>Last frame.</b> The final frame the encoder receives is saved as a PNG, so the tab can hand it to
    /// the next beat.</item>
    /// </list>
    /// </summary>
    public static class FflfGraph
    {
        public const string StartImageNode = "fflf_start";
        public const string GuidePass1Node = "fflf_guide1";
        public const string GuidePass2Node = "fflf_guide2";
        public const string LastFrameNode = "fflf_last";
        public const string LastFrameSaveNode = "fflf_last_save";
        public const string VideoSaveNode = MiniMaxI2VViewModel.TpSink;

        private const string Pass1Guider = "151:29";
        private const string Pass2Guider = MiniMaxI2VViewModel.TpSampler2Guider;   // 151:32
        private const string Pass1Latent = MiniMaxI2VViewModel.TpRef2V;             // 151:11, output 1
        private const string Pass2Latent = "151:10";                               // LTXVConcatAVLatent

        // ── Beats ───────────────────────────────────────────────────────────────────────────────────

        private static readonly Regex Numbered = new(@"^\s*(?:(?:beat|shot|scene)\s*)?\d{1,3}\s*[.):\-–]\s*", RegexOptions.IgnoreCase);
        private static readonly Regex Bullet = new(@"^\s*[-*•]\s+");
        private static readonly Regex Heading = new(@"^(?<t>[^:.!?\n]{2,60}):\s+(?<b>\S.*)$", RegexOptions.Singleline);

        /// <summary>
        /// Splits pasted story beats into beats: a blank line, a numbered line ("1.", "2)", "Beat 3:") or a bullet
        /// starts the next one; any other line carries on the beat above it. A short "Title: " in front of a beat
        /// becomes its title, and the numbering is dropped.
        /// </summary>
        public static List<FflfBeatText> ParseBeats(string? text)
        {
            var beats = new List<FflfBeatText>();
            if (string.IsNullOrWhiteSpace(text)) return beats;

            var current = new StringBuilder();
            void Flush()
            {
                var raw = Regex.Replace(current.ToString(), @"\s+", " ").Trim();
                current.Clear();
                if (raw.Length == 0) return;
                var m = Heading.Match(raw);
                if (m.Success && m.Groups["t"].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8)
                    beats.Add(new FflfBeatText(m.Groups["t"].Value.Trim(), m.Groups["b"].Value.Trim()));
                else
                    beats.Add(new FflfBeatText(string.Empty, raw));
            }

            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }
                var stripped = line;
                if (Numbered.IsMatch(line)) { Flush(); stripped = Numbered.Replace(line, string.Empty, 1); }
                else if (Bullet.IsMatch(line)) { Flush(); stripped = Bullet.Replace(line, string.Empty, 1); }
                current.Append(stripped).Append(' ');
            }
            Flush();
            return beats;
        }

        /// <summary>
        /// The beat's prompt: who and what the pictures are, the optional style line, then the beat itself.
        /// <c>&lt;Picture 1&gt;</c> is always the frame the beat starts on; <c>&lt;Picture 2&gt;</c>, when sent, is
        /// the initial image as the look of the characters.
        /// </summary>
        public static string ComposePrompt(string beat, string? style, bool hasIdentityImage, bool isFirstBeat)
        {
            var sb = new StringBuilder();
            sb.Append(isFirstBeat
                ? "<Picture 1> is the first frame of this shot: the video starts exactly on it and the action moves on from there. "
                : "<Picture 1> is the first frame of this shot — the last moment of the shot before it: the video starts exactly on it and the action carries straight on. ");
            sb.Append("Keep the same people with the same faces, hair, build and clothing, the same place, light and camera style throughout.");
            if (hasIdentityImage)
                sb.Append(" <Picture 2> shows how the characters look; keep them identical to it.");
            sb.Append("\n\n");
            if (!string.IsNullOrWhiteSpace(style)) sb.Append(style.Trim()).Append("\n\n");
            sb.Append(beat.Trim());
            return sb.ToString();
        }

        // ── The graph ───────────────────────────────────────────────────────────────────────────────

        /// <summary>The 2-Pass options one beat renders with: the authored graph, with this tab's model, LoRA,
        /// canvas, length and steps.</summary>
        public static MiniMaxI2VTwoPassOptions OptionsFor(FflfBeatRender r)
        {
            var o = new MiniMaxI2VTwoPassOptions
            {
                DiffusionModel = r.DiffusionModel.Trim().Replace('\\', '/'),
                DurationSeconds = r.Seconds,
                FirstPassMegapixels = r.FirstPassMegapixels,
                FinalMegapixels = r.FinalMegapixels,
                FirstPassSteps = Math.Clamp(r.FirstPassSteps, 1, 60),
                PreviewOnly = r.PreviewOnly,
                FrameGenMultiplier = 1,   // one frame rate for the join
                AspectRatio = r.AspectRatio,
            };
            if (!string.IsNullOrWhiteSpace(r.Lora) && r.LoraStrength > 0)
                o.Loras = new List<MiniMaxI2VLora> { new() { Name = r.Lora.Trim().Replace('\\', '/'), Strength = r.LoraStrength } };
            if (r.PreviewOnly)
            {
                // A draft is for judging the action: RTX and grain only add time.
                o.UseRtxUpscale = false;
                o.GrainStrength = 0;
            }
            return o;
        }

        /// <summary>Builds one beat's API graph from the h3-erosmax-2pass.json text.</summary>
        public static JsonObject Build(string twoPassJson, FflfBeatRender r, out int pruned)
        {
            var o = OptionsFor(r);
            var item = new MiniMaxI2VQueueItem
            {
                Workflow = I2VWorkflow.TwoPass,
                TwoPass = o,
                Prompt = r.Prompt,
                AspectRatio = r.AspectRatio,
                Megapixels = o.FinalMegapixels,
            };
            var pictures = new List<string> { r.StartImage };
            if (!string.IsNullOrEmpty(r.IdentityImage)) pictures.Add(r.IdentityImage!);

            var built = MiniMaxI2VViewModel.BuildTwoPassGraph(
                twoPassJson, item, pictures, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<bool>(),
                continueSource: null, r.Seed, r.OutputPrefix, segmentOnly: true, out pruned);
            var root = JsonNode.Parse(built)?.AsObject() ?? throw new InvalidOperationException("The 2-Pass graph could not be parsed.");

            if (r.LockFirstFrame) AddFirstFrameGuide(root, r.StartImage);
            AddLastFrameSave(root, r.OutputPrefix + "_last");
            return root;
        }

        /// <summary>Anchors <paramref name="image"/> (an uploaded input file) at frame 0 of each pass the graph runs.</summary>
        public static void AddFirstFrameGuide(JsonObject root, string image)
        {
            root[StartImageNode] = new JsonObject
            {
                ["class_type"] = "LoadImage",
                ["_meta"] = new JsonObject { ["title"] = "FFLF: first frame" },
                ["inputs"] = new JsonObject { ["image"] = image },
            };
            Guide(root, Pass1Guider, GuidePass1Node, Pass1Latent, 1, "FFLF: first frame, pass 1");
            // A draft stops after pass 1; its second pass is already pruned.
            if (root.ContainsKey(Pass2Guider) && root.ContainsKey(Pass2Latent))
                Guide(root, Pass2Guider, GuidePass2Node, Pass2Latent, 0, "FFLF: first frame, pass 2");
        }

        private static void Guide(JsonObject root, string guider, string id, string latentNode, int latentSlot, string title)
        {
            var inputs = root[guider]?["inputs"] as JsonObject
                         ?? throw new InvalidOperationException($"Workflow node '{guider}' is missing — h3-erosmax-2pass.json no longer matches the FFLF tab.");
            var conditioning = inputs["conditioning"]?.DeepClone()
                               ?? throw new InvalidOperationException($"Workflow node '{guider}' has no conditioning input.");
            root[id] = new JsonObject
            {
                ["class_type"] = "MiniMaxH3AddGuide",
                ["_meta"] = new JsonObject { ["title"] = title },
                ["inputs"] = new JsonObject
                {
                    ["positive"] = conditioning,
                    ["latent"] = new JsonArray { latentNode, latentSlot },
                    ["vae"] = new JsonArray { MiniMaxI2VViewModel.TpVideoVae, 0 },
                    ["image"] = new JsonArray { StartImageNode, 0 },
                    ["frame_idx"] = 0,
                },
            };
            inputs["conditioning"] = new JsonArray { id, 0 };
        }

        /// <summary>Saves the last frame the encoder receives — the frame the next beat starts on.</summary>
        public static void AddLastFrameSave(JsonObject root, string prefix)
        {
            var frames = root[MiniMaxI2VViewModel.TpLokkenNew]?["inputs"]?["images"]?.DeepClone()
                         ?? new JsonArray { MiniMaxI2VViewModel.TpDecode, 0 };
            root[LastFrameNode] = new JsonObject
            {
                ["class_type"] = "ImageFromBatch",
                ["_meta"] = new JsonObject { ["title"] = "FFLF: last frame" },
                ["inputs"] = new JsonObject { ["image"] = frames, ["batch_index"] = -1, ["length"] = 1 },
            };
            root[LastFrameSaveNode] = new JsonObject
            {
                ["class_type"] = "SaveImage",
                ["_meta"] = new JsonObject { ["title"] = "FFLF: save last frame" },
                ["inputs"] = new JsonObject
                {
                    ["images"] = new JsonArray { LastFrameNode, 0 },
                    ["filename_prefix"] = prefix,
                },
            };
        }

        // ── The join ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// FFmpeg arguments that join the beats in order. When each beat starts on the frame the one before ended
        /// on, that frame would show twice, so beats 2+ lose their first frame (and 1/24 s of sound).
        /// </summary>
        public static string JoinArguments(IReadOnlyList<string> clips, string output, bool dropFirstFrame, bool withAudio, int crf = 18)
        {
            var sb = new StringBuilder("-y ");
            foreach (var c in clips) sb.Append("-i \"").Append(c).Append("\" ");
            var filter = new StringBuilder();
            var concatIn = new StringBuilder();
            for (var i = 0; i < clips.Count; i++)
            {
                var trimV = dropFirstFrame && i > 0 ? "trim=start_frame=1," : string.Empty;
                filter.Append($"[{i}:v]{trimV}setpts=PTS-STARTPTS,setsar=1[v{i}];");
                concatIn.Append($"[v{i}]");
                if (withAudio)
                {
                    var trimA = dropFirstFrame && i > 0 ? "atrim=start=0.0416667," : string.Empty;
                    filter.Append($"[{i}:a]{trimA}asetpts=PTS-STARTPTS[a{i}];");
                    concatIn.Append($"[a{i}]");
                }
            }
            filter.Append(concatIn).Append($"concat=n={clips.Count}:v=1:a={(withAudio ? 1 : 0)}[v]");
            if (withAudio) filter.Append("[a]");
            sb.Append("-filter_complex \"").Append(filter).Append("\" -map \"[v]\" ");
            if (withAudio) sb.Append("-map \"[a]\" -c:a aac -b:a 192k ");
            sb.Append($"-c:v libx264 -crf {crf} -preset medium -pix_fmt yuv420p -movflags +faststart \"{output}\"");
            return sb.ToString();
        }
    }
}
