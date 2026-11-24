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
    /// <param name="Seconds">The beat's own length from a leading <c>[7 s]</c> tag (⏱ dynamic beat time); 0 when it
    /// has none and renders at the tab's 'Seconds / beat'.</param>
    public sealed record FflfBeatText(string Title, string Text, double Seconds = 0);

    /// <summary>Everything one FFLF beat render needs, already uploaded to the server.</summary>
    public sealed record FflfBeatRender
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
        /// <summary><c>ref_image_size: max</c> — the references at full size instead of scaled to the (small)
        /// first-pass canvas. The H3 likeness bake-off's largest single effect (median ArcFace 0.68 vs 0.50).</summary>
        public bool MaxFidelityReferences { get; init; } = true;
        /// <summary>SaveVideo prefix; the last frame is saved beside it as &lt;prefix&gt;_last.</summary>
        public string OutputPrefix { get; init; } = "video/fflf/beat";

        // ── 🥋 Action tab ────────────────────────────────────────────────────────────────────────────

        /// <summary>LoRAs stacked after <see cref="Lora"/> — the Action tab's combat mix.</summary>
        public IReadOnlyList<MiniMaxI2VLora> ExtraLoras { get; init; } = Array.Empty<MiniMaxI2VLora>();
        /// <summary>What <see cref="ExtraLoras"/> become on the refine pass (a LoRA left out is dropped there);
        /// null runs the refine on the first-pass mix. See <see cref="FflfGraph.AddRefineLoraMix"/>.</summary>
        public IReadOnlyList<MiniMaxI2VLora>? RefineLoras { get; init; }
        /// <summary>Photos of the cast, sent as the pictures after the start frame and the opening image.</summary>
        public IReadOnlyList<string> CastImages { get; init; } = Array.Empty<string>();
        /// <summary>Sampler and first-pass scheduler; empty keeps the graph's own (er_sde · sgm_uniform).</summary>
        public string Sampler { get; init; } = string.Empty;
        public string FirstPassScheduler { get; init; } = string.Empty;

        /// <summary>The beat's dialogue (an uploaded audio file), or null. The soundtrack is locked to it and H3
        /// draws only the picture, so the speaker's lips follow the words — see <see cref="FflfGraph.AddDialogueLock"/>.</summary>
        public string? DialogueAudio { get; init; }
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
        private const string Pass1Sampler = "151:21";                              // SamplerCustomAdvanced
        private const string Pass2Sampler = "151:16";                              // SamplerCustomAdvanced
        private const string FrameCount = "151:19";                                // seconds → frames (17k+5), output 1
        private const string AudioVae = "4";                                       // VAELoader, audio
        private const string AudioDecode = "151:14";                               // VAEDecodeAudio → the export

        public const string DialogueLoadNode = "fflf_dlg_load";
        public const string DialogueLockPass1Node = "fflf_dlg_lock1";
        public const string DialogueLockPass2Node = "fflf_dlg_lock2";

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
                var seconds = TakeSecondsTag(ref raw);
                if (raw.Length == 0) return;
                var m = Heading.Match(raw);
                if (m.Success && m.Groups["t"].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8)
                {
                    // "Title: [7 s] text" as well as "[7 s] Title: text".
                    var body = m.Groups["b"].Value.Trim();
                    if (seconds == 0) seconds = TakeSecondsTag(ref body);
                    beats.Add(new FflfBeatText(m.Groups["t"].Value.Trim(), body, seconds));
                }
                else
                    beats.Add(new FflfBeatText(string.Empty, raw, seconds));
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

        /// <summary>Shortest and longest length a ⏱ dynamic beat may ask for — the 'Seconds / beat' slider's range.</summary>
        public const double MinTaggedSeconds = 2, MaxTaggedSeconds = 15;

        // "[7 s]", "[7s]", "[7.5 sec]", "(7 seconds)" at the very start of a beat.
        private static readonly Regex SecondsTag = new(
            @"^[\[(]\s*(?<s>\d{1,2}(?:[.,]\d)?)\s*(?:s|sec|secs|second|seconds)\s*[\])]\s*[—–:\-]?\s*",
            RegexOptions.IgnoreCase);

        /// <summary>Removes a leading <c>[7 s]</c> length tag from a beat and returns its seconds (clamped to
        /// <see cref="MinTaggedSeconds"/>–<see cref="MaxTaggedSeconds"/>), or 0 when there is none.</summary>
        public static double TakeSecondsTag(ref string text)
        {
            var m = SecondsTag.Match(text);
            if (!m.Success) return 0;
            if (!double.TryParse(m.Groups["s"].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out var s) || s <= 0) return 0;
            text = text[m.Length..].Trim();
            return Math.Clamp(s, MinTaggedSeconds, MaxTaggedSeconds);
        }

        /// <summary>The tag <see cref="ParseBeats"/> reads back: "[7 s] " (with its trailing space), or empty for 0.</summary>
        public static string SecondsTagFor(double seconds) =>
            seconds > 0 ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[{seconds:0.#} s] ") : string.Empty;

        /// <summary>
        /// The beat's prompt: who and what the pictures are, the optional style line, then the beat itself.
        /// <c>&lt;Picture 1&gt;</c> is always the frame the beat starts on; <c>&lt;Picture 2&gt;</c>, when sent, is
        /// the initial image as the look of the characters.
        /// </summary>
        /// <summary>The style, setting and wardrobe boxes as the one block written in front of every beat: the
        /// style (master direction) first, then the setting and the wardrobe, each under its own heading so a style
        /// that says "see Setting / see Wardrobe" points at something. Empty boxes are left out.</summary>
        public static string ComposeLook(string? style, string? setting, string? wardrobe)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(style)) parts.Add(style.Trim());
            if (!string.IsNullOrWhiteSpace(setting)) parts.Add("SETTING: " + setting.Trim());
            if (!string.IsNullOrWhiteSpace(wardrobe)) parts.Add("WARDROBE (keep identical in every shot): " + wardrobe.Trim());
            return string.Join("\n\n", parts);
        }

        /// <param name="sheets">Who each character sheet sent after the start frame (and the identity image) shows —
        /// "A, on the left" — in picture order.</param>
        public static string ComposePrompt(string beat, string? style, bool hasIdentityImage, bool isFirstBeat,
                                           IReadOnlyList<string>? sheets = null)
        {
            var sb = new StringBuilder();
            sb.Append(isFirstBeat
                ? "<Picture 1> is the first frame of this shot: the video starts exactly on it and the action moves on from there. "
                : "<Picture 1> is the first frame of this shot — the last moment of the shot before it: the video starts exactly on it and the action carries straight on. ");
            sb.Append("Keep the same people with the same faces, hair, build and clothing, the same place, light and camera style throughout.");
            if (hasIdentityImage)
                sb.Append(" <Picture 2> shows how the characters look; keep them identical to it.");
            var picture = hasIdentityImage ? 3 : 2;
            foreach (var who in sheets ?? Array.Empty<string>())
                sb.Append($" <Picture {picture++}> is the character sheet of {who} (front, three-quarter, side, back and face): " +
                          "keep that character's face, hair, build and clothes identical to it from every angle.");
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
                MaxFidelityReferences = r.MaxFidelityReferences,
            };
            if (!string.IsNullOrWhiteSpace(r.Lora) && r.LoraStrength > 0)
                o.Loras = new List<MiniMaxI2VLora> { new() { Name = r.Lora.Trim().Replace('\\', '/'), Strength = r.LoraStrength } };
            foreach (var extra in r.ExtraLoras.Where(l => !string.IsNullOrWhiteSpace(l.Name) && l.Strength > 0))
                o.Loras.Add(new MiniMaxI2VLora { Name = extra.Name.Trim().Replace('\\', '/'), Strength = extra.Strength });
            if (!string.IsNullOrWhiteSpace(r.Sampler)) o.Sampler = r.Sampler.Trim();
            if (!string.IsNullOrWhiteSpace(r.FirstPassScheduler)) o.FirstPassScheduler = r.FirstPassScheduler.Trim();
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
            foreach (var cast in r.CastImages.Where(c => !string.IsNullOrEmpty(c)))
                if (pictures.Count < MiniMaxI2VViewModel.MaxTwoPassPictures) pictures.Add(cast);

            var built = MiniMaxI2VViewModel.BuildTwoPassGraph(
                twoPassJson, item, pictures, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<bool>(),
                continueSource: null, r.Seed, r.OutputPrefix, segmentOnly: true, out pruned);
            var root = JsonNode.Parse(built)?.AsObject() ?? throw new InvalidOperationException("The 2-Pass graph could not be parsed.");

            if (r.LockFirstFrame) AddFirstFrameGuide(root, r.StartImage);
            AddLastFrameSave(root, r.OutputPrefix + "_last");
            if (r.RefineLoras != null) AddRefineLoraMix(root, r.ExtraLoras, r.RefineLoras);
            // Last: it wraps whatever model each guider ends up reading, refine LoRA mix included.
            if (!string.IsNullOrEmpty(r.DialogueAudio)) AddDialogueLock(root, r.DialogueAudio!);
            return root;
        }

        /// <summary>What the prompt adds when a beat has dialogue: the soundtrack is speech, and someone in frame says it.</summary>
        public const string DialogueDirection =
            "The soundtrack is this shot's dialogue: the character who is speaking says every word of it on camera, " +
            "mouth, lips, jaw and face moving in exact sync with the voice — clear, natural talking, with the mouth visible.";

        public static string WithDialogueDirection(string prompt) =>
            string.IsNullOrWhiteSpace(prompt) ? DialogueDirection : prompt.TrimEnd() + "\n\n" + DialogueDirection;

        /// <summary>
        /// Locks the beat's soundtrack to <paramref name="audio"/> (an uploaded input file) so the character speaks it
        /// with lip sync. H3 samples one audio-video latent; <c>MiniMaxH3NativeAudioLock</c> encodes the exact audio
        /// into that latent's audio half, holds it at its clean timestep and masks sampling so only the video is
        /// denoised — the picture is drawn attending to the words, which is what moves the mouth in time.
        ///
        /// <para>The dialogue is first padded with silence and cut to the clip's length (frames / 24), so a short
        /// line ends in quiet rather than in whatever the model would have made up. Both passes are locked (the
        /// refine pass would otherwise redraw the audio, and the mouths with it), and the export takes the lock's
        /// exact audio rather than a VAE round trip of it.</para>
        /// </summary>
        public static void AddDialogueLock(JsonObject root, string audio)
        {
            static JsonObject Node(string cls, string title, JsonObject inputs) => new()
            {
                ["class_type"] = cls,
                ["_meta"] = new JsonObject { ["title"] = title },
                ["inputs"] = inputs,
            };
            static JsonArray L(string id, int slot = 0) => new() { id, slot };
            JsonObject Inputs(string id) => root[id]?["inputs"] as JsonObject
                ?? throw new InvalidOperationException($"Workflow node '{id}' is missing — h3-erosmax-2pass.json no longer matches the FFLF tab's dialogue lock.");

            var pass1Guider = Inputs(Pass1Guider);
            var pass1Sampler = Inputs(Pass1Sampler);
            Inputs(FrameCount);

            root[DialogueLoadNode] = Node("LoadAudio", "FFLF: dialogue", new JsonObject { ["audio"] = audio });
            root["fflf_dlg_dur"] = Node("ComfyMathExpression", "FFLF: clip duration = frames / 24",
                new JsonObject { ["expression"] = "a / 24", ["values.a"] = L(FrameCount, 1) });
            root["fflf_dlg_silence"] = Node("EmptyAudio", "FFLF: silence to pad the dialogue",
                new JsonObject { ["duration"] = L("fflf_dlg_dur"), ["sample_rate"] = 32000, ["channels"] = 2 });
            root["fflf_dlg_pad"] = Node("AudioConcat", "FFLF: dialogue + silence",
                new JsonObject { ["audio1"] = L(DialogueLoadNode), ["audio2"] = L("fflf_dlg_silence"), ["direction"] = "after" });
            root["fflf_dlg_fit"] = Node("TrimAudioDuration", "FFLF: dialogue fitted to the clip",
                new JsonObject { ["audio"] = L("fflf_dlg_pad"), ["start_index"] = 0, ["duration"] = L("fflf_dlg_dur") });

            // Pass 1: the lock wraps the guider's model and replaces the sampler's starting latent.
            root[DialogueLockPass1Node] = Node("MiniMaxH3NativeAudioLock", "FFLF: dialogue lock, pass 1", new JsonObject
            {
                ["model"] = pass1Guider["model"]!.DeepClone(),
                ["av_latent"] = pass1Sampler["latent_image"]!.DeepClone(),
                ["audio_vae"] = L(AudioVae),
                ["audio"] = L("fflf_dlg_fit"),
            });
            pass1Guider["model"] = L(DialogueLockPass1Node, 0);
            pass1Sampler["latent_image"] = L(DialogueLockPass1Node, 1);

            // Pass 2 (a draft has none).
            if (root[Pass2Guider]?["inputs"] is JsonObject pass2Guider && root[Pass2Sampler]?["inputs"] is JsonObject pass2Sampler)
            {
                root[DialogueLockPass2Node] = Node("MiniMaxH3NativeAudioLock", "FFLF: dialogue lock, pass 2", new JsonObject
                {
                    ["model"] = pass2Guider["model"]!.DeepClone(),
                    ["av_latent"] = pass2Sampler["latent_image"]!.DeepClone(),
                    ["audio_vae"] = L(AudioVae),
                    ["audio"] = L("fflf_dlg_fit"),
                });
                pass2Guider["model"] = L(DialogueLockPass2Node, 0);
                pass2Sampler["latent_image"] = L(DialogueLockPass2Node, 1);
            }

            // The export: the exact dialogue, not the decoded latent.
            foreach (var (id, node) in root.ToList())
            {
                if (id.StartsWith("fflf_dlg_", StringComparison.Ordinal) || node?["inputs"] is not JsonObject inputs) continue;
                foreach (var key in inputs.Select(kv => kv.Key).ToList())
                {
                    if (inputs[key] is JsonArray link && link.Count == 2 && link[0]?.ToString() == AudioDecode && link[1]?.ToString() == "0")
                        inputs[key] = L(DialogueLockPass1Node, 2);
                }
            }
        }

        public const string RefineNodePrefix = "fflf_refine_";

        /// <summary>
        /// Gives the refine pass its own LoRA mix. The authored 2-Pass graph runs both passes on one model wire
        /// (loader → H3PowerLoraStack → shift → sparse attention → backend); the Action Director workflow runs
        /// its second pass on a separate, weaker stack, because motion LoRAs at full strength on a low-denoise
        /// pass redraw limbs the first pass already placed. So the wire from the stack down to the pass-2 guider
        /// is copied, the copy's stack carries <paramref name="refine"/> in place of <paramref name="firstPass"/>
        /// (every other row — the tab's own LoRA — unchanged), and the pass-2 guider and scheduler read the copy.
        /// A draft has no second pass and is left alone. Returns whether the graph was changed.
        /// </summary>
        public static bool AddRefineLoraMix(JsonObject root, IReadOnlyList<MiniMaxI2VLora> firstPass, IReadOnlyList<MiniMaxI2VLora> refine)
        {
            static string Norm(string name) => name.Trim().Replace('\\', '/');
            static string? ModelSource(JsonObject root, string id) =>
                root[id]?["inputs"]?["model"] is JsonArray link && link.Count == 2 ? link[0]?.ToString() : null;

            var swapped = firstPass.Where(l => !string.IsNullOrWhiteSpace(l.Name) && l.Strength > 0).Select(l => Norm(l.Name)).ToList();
            var wanted = refine.Where(l => !string.IsNullOrWhiteSpace(l.Name) && l.Strength > 0).ToList();
            var same = swapped.Count == wanted.Count && wanted.All(w =>
                firstPass.Any(f => Norm(f.Name).Equals(Norm(w.Name), StringComparison.OrdinalIgnoreCase) && Math.Abs(f.Strength - w.Strength) < 1e-6));
            if (same || !root.ContainsKey(Pass2Guider)) return false;

            // The wire, from the pass-2 guider up to the stack.
            var path = new List<string>();
            for (var id = ModelSource(root, Pass2Guider); id != null && path.Count < 12; id = ModelSource(root, id))
            {
                path.Add(id);
                if (id == MiniMaxI2VViewModel.TpLoraStack) break;
            }
            if (path.Count == 0 || path[^1] != MiniMaxI2VViewModel.TpLoraStack) return false;

            // The stack's copy: the rows that are not the first-pass mix, then the refine mix.
            var stack = root[MiniMaxI2VViewModel.TpLoraStack]!.DeepClone().AsObject();
            var inputs = stack["inputs"]!.AsObject();
            var kept = inputs.Where(kv => kv.Key.StartsWith("lora_", StringComparison.Ordinal))
                             .Select(kv => kv.Value!.DeepClone().AsObject())
                             .Where(row => !swapped.Contains(Norm(row["lora"]?.GetValue<string>() ?? string.Empty), StringComparer.OrdinalIgnoreCase))
                             .ToList();
            foreach (var key in inputs.Select(kv => kv.Key).Where(k => k.StartsWith("lora_", StringComparison.Ordinal)).ToList())
                inputs.Remove(key);
            foreach (var l in wanted)
                kept.Add(new JsonObject { ["on"] = true, ["lora"] = Norm(l.Name), ["strength"] = l.Strength });
            for (var i = 0; i < kept.Count; i++) inputs[$"lora_{i + 1}"] = kept[i];
            stack["_meta"] = new JsonObject { ["title"] = "H3 Power LoRA Stack (refine pass)" };

            // With no row left the refine reads the wire above the stack.
            JsonNode upstream = kept.Count > 0
                ? new JsonArray { RefineNodePrefix + MiniMaxI2VViewModel.TpLoraStack, 0 }
                : inputs["model"]!.DeepClone();
            if (kept.Count > 0) root[RefineNodePrefix + MiniMaxI2VViewModel.TpLoraStack] = stack;

            // Every node between the stack and the guider, copied bottom-up. The pass-2 live preview node
            // already belongs to pass 2 alone, so it is rewired rather than copied.
            var copies = new Dictionary<string, string>();
            for (var i = path.Count - 2; i >= 0; i--)
            {
                var id = path[i];
                var own = id == MiniMaxI2VViewModel.TpPreviewPass2;
                var copyId = own ? id : RefineNodePrefix + id.Replace(':', '_');
                if (!own) root[copyId] = root[id]!.DeepClone();
                root[copyId]!["inputs"]!["model"] = upstream.DeepClone();
                upstream = new JsonArray { copyId, 0 };
                copies[id] = copyId;
            }
            root[Pass2Guider]!["inputs"]!["model"] = upstream.DeepClone();

            // The pass-2 scheduler builds its sigmas from the model it is given.
            if (ModelSource(root, MiniMaxI2VViewModel.TpScheduler2) is { } scheduled)
            {
                if (copies.TryGetValue(scheduled, out var copy)) root[MiniMaxI2VViewModel.TpScheduler2]!["inputs"]!["model"] = new JsonArray { copy, 0 };
                else if (scheduled == MiniMaxI2VViewModel.TpLoraStack) root[MiniMaxI2VViewModel.TpScheduler2]!["inputs"]!["model"] = upstream.DeepClone();
            }
            return true;
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

        // ── Face repair on the handoff frame ────────────────────────────────────────────────────────

        public const string FaceRepairWorkflowFileName = "workflow/image/qwen-edit/Qwen_Edit_2511_INT8_Convrot_WF.json";
        public const double DefaultFaceRepairStrength = 1.0;
        /// <summary>The two strengths that differ: with 8 steps ComfyUI samples int(8 / denoise) steps, so 0.9–1.0 all run
        /// the same schedule. 8/9 is the next one down.</summary>
        public const double GentleFaceRepairStrength = 0.89;

        // Qwen_Edit_2511_INT8_Convrot_WF.json
        private const string QwLoad = "78";           // LoadImage — the handoff frame
        private const string QwScale = "115:93";      // ImageScaleToTotalPixels → encoder + latent
        private const string QwPositive = "115:111";  // TextEncodeQwenImageEditPlus
        private const string QwNegative = "115:110";  // TextEncodeQwenImageEditPlus (empty prompt)
        private const string QwSampler = "115:3";     // KSampler
        private const string QwDecode = "115:8";      // VAEDecode
        private const string QwRtx = "115:124";       // RTXVideoSuperResolution — not wanted on a crop
        private const string QwPreview = "115:116";   // PreviewImage
        public const string FaceRepairSaveNode = "60"; // SaveImage

        private const string FxReference = "fflf_fx_ref";
        private const string FxReferenceCrop = "fflf_fx_refcrop";
        private const string FxCrop = "fflf_fx_crop";
        private const string FxCropSize = "fflf_fx_size";
        private const string FxResize = "fflf_fx_resize";
        private const string FxSam = "fflf_fx_sam";
        private const string FxMask = "fflf_fx_mask";
        private const string FxStitch = "fflf_fx_stitch";

        /// <summary>What Qwen Edit is asked to do to the face crop. Expression and camera are kept; identity
        /// is taken from the opening image's face.</summary>
        public const string FaceRepairPrompt =
            "Picture 1 is a close-up crop of a face from a video frame. Picture 2 is a close-up of the same person's real face. " +
            "Redraw the face in Picture 1 so it is unmistakably the person in Picture 2: the same face shape, jawline, cheeks, " +
            "eyes, eyebrows, nose, lips, skin and age. Keep Picture 1's expression, open or closed mouth, gaze, head angle, " +
            "lighting, colour, sharpness and camera distortion. Change nothing else: hair, framing and background stay as they are.";

        /// <summary>
        /// The graph that puts the opening image's face back on a handoff frame before the next beat starts on it.
        ///
        /// <para>Why: each beat is pinned at frame 0 to the last frame of the beat before, so whatever the face
        /// lost in one beat is the starting point of the next. The error compounds — the chain is a Markov chain
        /// with no memory of beat 1 except <c>&lt;Picture 2&gt;</c>, and the pinned frame outweighs a reference.
        /// Repairing the handoff frame re-anchors every beat to the original face instead of to the drifted one.</para>
        ///
        /// <para>How: H3FaceTrackCrop crops the face that best matches the opening image (ArcFace via insightface;
        /// the largest face if none clears the threshold), and the opening image's largest face, so the reference is a
        /// close-up too; Qwen Edit 2511 redraws the crop against it; H3FaceStitch pastes back only the face
        /// through a SAM mask, colour-matched — hair, body, background and blur stay the original pixels. A frame
        /// with no detectable face (turned away) comes back unchanged.</para>
        ///
        /// <para>Measured on a 10-beat run (ArcFace similarity to the opening image): the plain chain fell
        /// 0.49 → 0.41 → 0.27 → 0.16 → 0.10 by beat 10; repairing beat 8's and 9's frames lifted them from 0.16 and
        /// 0.30 to 0.27 and 0.49.</para>
        ///
        /// <para>Strength is the KSampler denoise. With 8 steps only 1.0 and 8/9 differ (int(steps / denoise));
        /// below ~0.85 the Lightning LoRA leaves blotchy skin. 1.0 moves the face furthest but may change the
        /// expression.</para>
        /// </summary>
        public static JsonObject BuildFaceRepair(string qwenJson, string handoffImage, string identityImage,
                                                 long seed, double strength, string outputPrefix)
        {
            var root = JsonNode.Parse(qwenJson)?.AsObject() ?? throw new InvalidOperationException("The Qwen Edit graph could not be parsed.");
            foreach (var (id, cls) in new[]
                     {
                         (QwLoad, "LoadImage"), (QwScale, "ImageScaleToTotalPixels"), (QwPositive, "TextEncodeQwenImageEditPlus"),
                         (QwNegative, "TextEncodeQwenImageEditPlus"), (QwSampler, "KSampler"), (QwDecode, "VAEDecode"),
                         (FaceRepairSaveNode, "SaveImage"),
                     })
            {
                if (root[id]?["class_type"]?.GetValue<string>() != cls)
                    throw new InvalidOperationException($"Workflow node '{id}' is not {cls} — {FaceRepairWorkflowFileName} no longer matches the FFLF tab.");
            }

            static JsonObject Node(string cls, string title, JsonObject inputs) => new()
            {
                ["class_type"] = cls,
                ["_meta"] = new JsonObject { ["title"] = title },
                ["inputs"] = inputs,
            };
            static JsonArray L(string id, int slot = 0) => new() { id, slot };
            static JsonObject Crop(JsonArray images, JsonArray? identity = null)
            {
                var c = CropInputs(images);
                if (identity == null) return c;
                c["identity_track"] = true;
                c["identity_reference"] = identity;
                c["identity_model"] = "insightface";
                c["identity_threshold"] = 0.28;
                return c;
            }
            static JsonObject CropInputs(JsonArray images) => new()
            {
                ["images"] = images,
                ["detector"] = "bbox/face_yolov8m.pt",
                ["confidence"] = 0.15,
                ["crop_factor"] = 2.2,
                ["canvas_width"] = 1024,
                ["canvas_height"] = 1024,
                ["canvas_mode"] = "manual",
                ["smooth_window"] = 1,
                ["size_smooth_window"] = 1,
                ["smooth_method"] = "gaussian",
                ["size_mode"] = "per_frame",
                ["identity_track"] = false,
                ["select"] = "largest_face",
                ["fallback_detector"] = "none",
                ["fallback_head_frac"] = 0.5,
            };

            root[QwLoad]!["inputs"]!["image"] = handoffImage;
            root[FxReference] = Node("LoadImage", "FFLF: opening image", new JsonObject { ["image"] = identityImage });
            root[FxReferenceCrop] = Node("H3FaceTrackCrop", "FFLF: opening face", Crop(L(FxReference)));
            root[FxCrop] = Node("H3FaceTrackCrop", "FFLF: handoff face", Crop(L(QwLoad), L(FxReference)));

            root[QwScale]!["inputs"]!["image"] = L(FxCrop);
            foreach (var enc in new[] { QwPositive, QwNegative })
                root[enc]!["inputs"]!["image2"] = L(FxReferenceCrop);
            root[QwPositive]!["inputs"]!["prompt"] = FaceRepairPrompt;
            root[QwSampler]!["inputs"]!["seed"] = seed;
            root[QwSampler]!["inputs"]!["denoise"] = Math.Clamp(strength, 0.5, 1.0);

            root.Remove(QwRtx);
            root.Remove(QwPreview);
            root[FxCropSize] = Node("GetImageSize", "FFLF: crop size", new JsonObject { ["image"] = L(FxCrop) });
            root[FxResize] = Node("ImageScale", "FFLF: back to crop size", new JsonObject
            {
                ["image"] = L(QwDecode),
                ["upscale_method"] = "lanczos",
                ["width"] = L(FxCropSize, 0),
                ["height"] = L(FxCropSize, 1),
                ["crop"] = "disabled",
            });
            root[FxSam] = Node("SAMLoader", "FFLF: SAM", new JsonObject { ["model_name"] = "sam_vit_b_01ec64.pth", ["device_mode"] = "AUTO" });
            root[FxMask] = Node("H3FaceMaskSAM", "FFLF: face mask", new JsonObject
            {
                ["crops"] = L(FxCrop),
                ["sam_model"] = L(FxSam),
                ["transform"] = L(FxCrop, 1),
                ["threshold"] = 0.93,
                ["dilation"] = 0,
                ["temporal_smooth"] = 1,
            });
            root[FxStitch] = Node("H3FaceStitch", "FFLF: face back into the frame", new JsonObject
            {
                ["base_images"] = L(QwLoad),
                ["refined_crops"] = L(FxResize),
                ["transform"] = L(FxCrop, 1),
                ["paste_region"] = "face_only",
                ["mask_dilation"] = 24,
                ["feather"] = 8,
                ["colour_match"] = 1.0,
                ["blend"] = 1.0,
                ["undetected_frames"] = "skip",
                ["masks"] = L(FxMask),
            });
            root[FaceRepairSaveNode]!["inputs"]!["images"] = L(FxStitch);
            root[FaceRepairSaveNode]!["inputs"]!["filename_prefix"] = outputPrefix;
            return root;
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
