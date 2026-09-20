using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.Core.Models;
using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ⚡ H3 Express's 🔗 <b>chained clips</b> — the clips of one story rendered as one continuous shot
    /// instead of N separate ones glued together. Five 5-second clips come out as a seamless ~25 seconds:
    /// every clip after the first is generated <i>continuing</i> the one before it.
    ///
    /// <para><b>How.</b> It is NikoDemon80's ComfyUI-H3-Motion-Context, wired the way its
    /// <c>MiniMax H3 - fl2va - ref2va</c> example wires the Ref2VA graph — the reference node's conditioning
    /// goes through <c>MiniMaxH3MotionContext</c> on its way to the sampler, and the previous clip's
    /// <i>latent</i> (picture and sound both, no decode and re-encode) is pinned to the head of this one.
    /// The pack's Chain node is a set of front-end buttons that walk two indices up a slot list; here the
    /// app is the button. Clip N loads slot N-1 and saves slot N, the story's own id names the folder, and
    /// the clips are already rendered in order, so nothing else is needed.</para>
    ///
    /// <para><b>Both passes.</b> The first pass is pinned to the previous clip's draft-canvas latent, and
    /// the finish (upscale) pass to the previous clip's <i>finished</i> latent — two save slots per clip.
    /// The finish re-samples from sigma 0.9, hard enough that a pin on the draft alone would be undone by
    /// it. (<see cref="ChainPinFinish"/> turns the second one off; 🍥 TaoMate has no finish pass and is
    /// pinned on both of its legs instead.)</para>
    ///
    /// <para><b>Trim.</b> The pinned frames come back at the start of the new clip. They are cut — picture
    /// and sound together, ahead of RIFE — so the clips join with a plain butt join, which is what the
    /// existing FFmpeg concat already does. To keep the delivered clip the length that was asked for, the
    /// clip is generated that much longer.</para>
    ///
    /// <para><b>Not chained</b> when the story is a single clip, when the ComfyUI server does not have the
    /// pack, or for a clip whose predecessor did not render (the chain restarts there and says so).</para>
    /// </summary>
    public partial class H3ExpressViewModel
    {
        // ── Nodes this adds to the graph ────────────────────────────────────────────────────────────
        private const string NodeChainLoad = "h3ctx:load";
        private const string NodeChainCtx = "h3ctx:ctx";
        private const string NodeChainSave = "h3ctx:save";
        private const string NodeChainFinishLoad = "h3ctx:fload";
        private const string NodeChainFinishCtx = "h3ctx:fctx";
        private const string NodeChainFinishSave = "h3ctx:fsave";
        private const string NodeChainTrim = "h3ctx:trim";

        private const string ChainRoot = "h3_express_ctx";

        /// <summary>The pack's own tested setting, and the only one that leaves a beat of headroom: 22
        /// frames is 0.92 s of pinned picture. The dropdown offers 5, 22, 39 or 56 — the only lengths
        /// the model's video latent can hold exactly.</summary>
        private const int ChainContextFrames = 22;

        /// <summary>Exactly one second of sound, and on the audio grid (any multiple of 3 is).</summary>
        private const int ChainAudioFrames = 24;

        private bool _chainClips = true;
        private bool _chainPinFinish = true;
        private bool? _chainNodesOk;

        private void InitChain()
        {
            _chainClips = _settingsService.Settings?.H3ExpressChainClips ?? true;
            _chainPinFinish = _settingsService.Settings?.H3ExpressChainPinFinish ?? true;
        }

        /// <summary>
        /// Whether the clips of a story are rendered as one continuous shot. Read live as each clip's graph is
        /// built, and frozen while anything renders (<see cref="H3BatchViewModel.CanChangeWorkflow"/>), so a
        /// story is never half of one and half of the other.
        /// </summary>
        public bool ChainClips
        {
            get => _chainClips;
            set
            {
                if (_chainClips == value) return;
                _chainClips = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ChainSummary));
                OnPropertyChanged(nameof(ChainPinFinishEnabled));

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.H3ExpressChainClips = value;
                    _settingsService.SaveSettings(settings);
                }
                AddLog(value
                    ? "🔗 Chained clips ON — each clip after the first continues from the tail of the one before it " +
                      "(H3 Motion Context), and the story plays as one seamless shot. Needs the " +
                      "ComfyUI-H3-Motion-Context node pack on the ComfyUI server."
                    : "🔗 Chained clips off — every clip is rendered on its own again.");
                if (value)
                    AddLog("  Tip: clip prompts written before this was on do not open on the previous clip's " +
                           "last moment. Untick ♻ Reuse saved prompts (or Analyze again) to have them written for a chain.");
            }
        }

        /// <summary>Also pin the finish (upscale) pass to the previous clip's finished tail. On by default;
        /// off leaves the finish as the unchained render — the fallback if that pass misbehaves.</summary>
        public bool ChainPinFinish
        {
            get => _chainPinFinish;
            set
            {
                if (_chainPinFinish == value) return;
                _chainPinFinish = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ChainSummary));

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.H3ExpressChainPinFinish = value;
                    _settingsService.SaveSettings(settings);
                }
            }
        }

        public bool ChainPinFinishEnabled => _chainClips;

        public string ChainSummary =>
            !_chainClips
                ? "Off — every clip is rendered on its own and the film is the clips butted together."
                : "On — clip 2 onward continues from the last second of the clip before it, picture and sound, and the " +
                  "pinned head is cut so the clips join with no seam. Each clip after the first is generated " +
                  "0.9 s longer to make up for it. " +
                  (_chainPinFinish
                      ? "The upscale pass is pinned too."
                      : "The upscale pass is not pinned — the seam is only as good as the draft's.");

        /// <summary>
        /// Asks the server, once per run, whether the Motion Context pack is installed. A server that cannot
        /// be asked is given the benefit of the doubt; one that answers "no" turns chaining off for this run
        /// with a line saying why, rather than failing every clip on an unknown node class.
        /// </summary>
        private async Task EnsureChainNodesAsync(CancellationToken token)
        {
            _chainNodesOk = null;
            if (!_chainClips) return;

            var found = await _comfyUIService.HttpClient.NodeClassExistsAsync("MiniMaxH3MotionContext", token);
            _chainNodesOk = found;
            if (found == false)
                AddLog("⚠ 🔗 Chained clips: the ComfyUI server has no MiniMaxH3MotionContext node. Install " +
                       "ComfyUI-H3-Motion-Context (github.com/NikoDemon80/ComfyUI-H3-Motion-Context) and restart " +
                       "ComfyUI — until then this run renders every clip on its own.");
        }

        // ── The plan for one clip ───────────────────────────────────────────────────────────────────

        /// <summary>What chaining does to one clip. Worked out again by each step that needs it, from the
        /// item and the queue alone, so the steps agree without carrying state between them.</summary>
        private sealed record ChainPlan(
            bool Continues, int LoadIndex, int SaveIndex, bool Save, bool FinishPinned,
            string Folder, string FinishFolder);

        private ChainPlan? PlanChain(H3CastQueueItem item)
        {
            if (!_chainClips || _chainNodesOk == false) return null;
            if (!item.IsStoryClip || string.IsNullOrEmpty(item.StoryId)) return null;

            var save = item.ClipIndex < item.ClipCount;

            var continues = false;
            if (item.ClipIndex > 1)
            {
                var previous = Queue.FirstOrDefault(q => q.StoryId == item.StoryId && q.ClipIndex == item.ClipIndex - 1);
                continues = previous is { ItemStatus: QueueItemStatus.Completed };
            }

            // Nothing to continue from and nothing after it to save for: an ordinary clip.
            if (!continues && !save) return null;

            var id = Regex.Replace(item.StoryId, "[^A-Za-z0-9_-]", "_");
            return new ChainPlan(
                Continues: continues,
                LoadIndex: item.ClipIndex - 1,
                SaveIndex: item.ClipIndex,
                Save: save,
                FinishPinned: _chainPinFinish && !UseTaoMate,
                Folder: $"{ChainRoot}/{id}",
                FinishFolder: $"{ChainRoot}/{id}_hi");
        }

        /// <summary>The length to ask the graph for so the clip that is delivered — after the pinned head is
        /// cut — is the length that was asked for. The graph's own rounding lands on 17k+5 frames, and the
        /// head is 22 = 17+5 of them, so this picks the nearest whole step rather than let it round up a full
        /// 0.7 s.</summary>
        private static double ChainedSeconds(double seconds)
        {
            var delivered = (int)Math.Round(seconds * DraftFrameRate);
            var steps = Math.Max(2, (int)Math.Round((delivered + ChainContextFrames - 5) / 17.0));
            return (5 + 17 * steps) / (double)DraftFrameRate;
        }

        // ── The prompt ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What the clip writer is told about a chained clip. The pinned frames are not a suggestion — they
        /// are re-injected at every sampling step and the model cannot render anything else in that span — so
        /// a clip that opens on something other than where the last one ended does not choose between the two,
        /// it renders both (the pack's own note: three people in the frame). The prompt has to open by
        /// describing how the previous clip ended, hold that for a beat past the pin, and only then move on.
        /// </summary>
        protected override string ChainedOpening(int clipIndex)
        {
            if (!_chainClips || _chainNodesOk == false || clipIndex <= 0) return string.Empty;

            return "CHAINED CLIP — this clip is rendered as the CONTINUATION of the one before it, not cut from it. " +
                   "The last second of that clip is pinned into the first second of this one as real frames and " +
                   "sound, and the video model cannot render anything else there. So [Shot 1] must open on " +
                   "exactly how the clip before ended — the same people, the same clothes, the same place, the same " +
                   "framing, the same action and the same light — and hold that framing for at least the first " +
                   "2 seconds: no cut and no new angle inside it (this overrides the shot plan's timing for " +
                   "[Shot 1] only). Give the hold something small to do — a breath, a weight shift, an eyeline " +
                   "change — so it does not read as a frozen frame. Only after that does this clip's own action begin. " +
                   "Anything else in the opening seconds is rendered as well as the pinned moment, not instead of it.\n\n";
        }

        // ── The graph ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Wires the chain into the graph <see cref="ApplyCommonInputs"/> just filled in: the Motion Context
        /// between the reference node and each pass that samples the clip, a Load for the clip before it, a
        /// Save for this one, and — through <see cref="InsertTrim"/>, later — the trim.
        /// </summary>
        private void ApplyChain(JsonObject root, H3CastQueueItem item, double lengthSeconds)
        {
            var plan = PlanChain(item);
            if (plan == null) return;

            var vae = LinkFrom(root, NodeRef2V, "vae");
            var audioVae = LinkFrom(root, NodeRef2V, "audio_vae");

            // Where the first pass is sampled — and so where its conditioning is intercepted, and which
            // latent is saved. The TaoMate relay is two samplers reading the reference node directly.
            string firstPass;
            var conditioned = new List<(string Node, string Input)>();
            if (UseTaoMate)
            {
                firstPass = NodeTaoPass2;
                conditioned.Add((NodeTaoPass1, "positive"));
                conditioned.Add((NodeTaoPass2, "positive"));
            }
            else
            {
                firstPass = SampleBranches[RenderSlot - 1].Sampler;
                conditioned.Add((GuiderOf(root, firstPass), "conditioning"));
            }

            if (plan.Continues)
            {
                root[NodeChainLoad] = LoadNode(plan.Folder, plan.LoadIndex, "H3 Motion Context — previous clip (draft)");
                root[NodeChainCtx] = ContextNode(
                    conditioning: new JsonArray(NodeRef2V, 0), vae, audioVae,
                    latent: new JsonArray(NodeRef2V, 1), load: NodeChainLoad, "H3 Motion Context (draft pass)");
                foreach (var (node, input) in conditioned)
                    SetLink(root, node, input, NodeChainCtx, 0);

                // The finish is sampled off the latent upscaler's output; that is the shape and size the
                // finished tail of the previous clip has, and what the pin is laid against.
                if (plan.FinishPinned)
                {
                    var finishGuider = GuiderOf(root, NodeUpscaleSampler);
                    var finishLatent = LinkFrom(root, NodeUpscaleSampler, "latent_image");
                    root[NodeChainFinishLoad] = LoadNode(plan.FinishFolder, plan.LoadIndex,
                                                         "H3 Motion Context — previous clip (finish)");
                    root[NodeChainFinishCtx] = ContextNode(
                        conditioning: new JsonArray(NodeRef2V, 0), vae, audioVae,
                        latent: finishLatent, load: NodeChainFinishLoad, "H3 Motion Context (finish pass)");
                    SetLink(root, finishGuider, "conditioning", NodeChainFinishCtx, 0);
                }

                SetInput(root, NodeSeconds, "value", ChainedSeconds(lengthSeconds));
            }

            if (plan.Save)
            {
                root[NodeChainSave] = SaveNode(firstPass, plan.Folder, plan.SaveIndex, "H3 Motion Context — save (draft)");
                if (plan.FinishPinned)
                    root[NodeChainFinishSave] = SaveNode(NodeUpscaleSampler, plan.FinishFolder, plan.SaveIndex,
                                                         "H3 Motion Context — save (finish)");
            }

            var later = Queue.Any(q => q.StoryId == item.StoryId && q.ClipIndex > item.ClipIndex &&
                                       q.ItemStatus == QueueItemStatus.Completed);
            AddLog(plan.Continues
                ? $"  🔗 Clip {item.ClipIndex}/{item.ClipCount} continues from clip {plan.LoadIndex}: {ChainContextFrames} " +
                  $"frames + {ChainAudioFrames} of sound pinned, rendered {ChainedSeconds(lengthSeconds):0.##}s, " +
                  $"{ChainContextFrames} frames cut → ≈{lengthSeconds:0.#}s delivered" +
                  (plan.FinishPinned ? ", finish pass pinned too." : ".")
                : $"  🔗 Clip {item.ClipIndex}/{item.ClipCount} starts the chain" +
                  (item.ClipIndex > 1 ? " (the clip before it did not render, so it cannot continue from it)." : "."));
            if (later)
                AddLog("  ⚠ Clips after this one are already rendered and continue from the take it replaces — " +
                       "the join into the next clip will not match until those are regenerated too.");
        }

        /// <summary>Cuts the pinned head off the finished clip — picture and sound together — before RIFE and
        /// the mux. A clip with nothing pinned still passes through, at 0 frames: the node also squares the
        /// audio's length to the frame count, which is what stops a few milliseconds a join adding up.</summary>
        protected override string? InsertTrim(JsonObject root, H3CastQueueItem item, string video, string audio)
        {
            var plan = PlanChain(item);
            if (plan == null) return null;

            root[NodeChainTrim] = new JsonObject
            {
                ["inputs"] = new JsonObject
                {
                    ["images"] = new JsonArray(video, 0),
                    ["trim_frames"] = plan.Continues ? new JsonArray(NodeChainCtx, 1) : (JsonNode)JsonValue.Create(0),
                    ["audio"] = new JsonArray(audio, 0),
                    ["fps"] = (double)DraftFrameRate,
                    ["match_tail"] = true
                },
                ["class_type"] = "MiniMaxH3MotionContextTrim",
                ["_meta"] = new JsonObject { ["title"] = "H3 Motion Context Trim" }
            };
            return NodeChainTrim;
        }

        /// <summary>The mux, plus the saves — an output node runs only when something keeps it.</summary>
        protected override IReadOnlyList<string> FinishOutputs(H3CastQueueItem item)
        {
            var outputs = new List<string>(base.FinishOutputs(item));
            if (PlanChain(item) is { Save: true } plan)
            {
                outputs.Add(NodeChainSave);
                if (plan.FinishPinned) outputs.Add(NodeChainFinishSave);
            }
            return outputs;
        }

        // ── Node builders ───────────────────────────────────────────────────────────────────────────

        private static JsonObject LoadNode(string folder, int index, string title) => new()
        {
            ["inputs"] = new JsonObject { ["latent_path"] = folder, ["clip_index"] = index },
            ["class_type"] = "MiniMaxH3MotionContextLoadLatent",
            ["_meta"] = new JsonObject { ["title"] = title }
        };

        private static JsonObject SaveNode(string sampler, string folder, int index, string title) => new()
        {
            ["inputs"] = new JsonObject
            {
                ["latent"] = new JsonArray(sampler, 0),
                ["filename_prefix"] = $"{folder}/clip",
                ["clip_index"] = index
            },
            ["class_type"] = "MiniMaxH3MotionContextSaveLatent",
            ["_meta"] = new JsonObject { ["title"] = title }
        };

        private static JsonObject ContextNode(
            JsonArray conditioning, JsonArray vae, JsonArray audioVae, JsonArray latent, string load, string title) => new()
        {
            ["inputs"] = new JsonObject
            {
                ["conditioning"] = conditioning.DeepClone(),
                ["vae"] = vae.DeepClone(),
                ["latent"] = latent.DeepClone(),
                ["context_length"] = ChainContextFrames.ToString(),
                ["audio_context_length"] = ChainAudioFrames,
                ["context_latent"] = new JsonArray(load, 0),
                ["audio_vae"] = audioVae.DeepClone()
            },
            ["class_type"] = "MiniMaxH3MotionContext",
            ["_meta"] = new JsonObject { ["title"] = title }
        };

        /// <summary>A copy of the link one node's input holds — the graph's own wiring, read rather than
        /// assumed, so a stack whose VAE loaders have other ids still chains.</summary>
        private static JsonArray LinkFrom(JsonObject root, string nodeId, string input) =>
            root[nodeId]?["inputs"]?[input] is JsonArray link && link.Count == 2
                ? (JsonArray)link.DeepClone()
                : throw new Exception($"Workflow node '{nodeId}' has no linked '{input}' — the workflow file no longer " +
                                      "matches what 🔗 chained clips wires into.");

        private static string GuiderOf(JsonObject root, string samplerId) =>
            LinkFrom(root, samplerId, "guider")[0]?.ToString()
            ?? throw new Exception($"Sampler '{samplerId}' has no guider.");

        private static void SetLink(JsonObject root, string nodeId, string input, string sourceId, int slot)
        {
            if (root[nodeId]?["inputs"] is not JsonObject inputs)
                throw new Exception($"Workflow node '{nodeId}' is missing — the workflow file no longer matches this tab.");
            inputs[input] = new JsonArray(sourceId, slot);
        }
    }
}
