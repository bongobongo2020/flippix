using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// 🌀 MiniMax I2V's <b>stack patches</b> — what each of the five stacks writes into
    /// <c>h3-minimax-i2v.json</c> as a pass is built.
    ///
    /// <para><b>The shape of the graph this writes into.</b> One model wire runs the length of it:
    /// <c>55:3701</c> the checkpoint → <c>55:3703</c> the attention backend → <c>55:3706</c> the Sol-Attn
    /// switch → <c>55:3690</c> the lightx2v turbo LoRA → <c>55:3704</c> the sigma shift → <c>55:3705</c>
    /// Spectrum (shipped disabled, a pass-through) → five switched LoRA seats → <c>39</c>/<c>51</c> the
    /// preview overrides → <c>sla_base</c>/<c>sla_loop</c> → the guiders and schedulers. Everything below is
    /// a change to that wire and to the two samplers hanging off it — the base pass's and the continuation
    /// loop's. Nothing here touches the references, the prompt, the canvas, the overlap or the sinks.</para>
    ///
    /// <para><b>Why relink rather than switch.</b> Several of those nodes are unconditional in the file —
    /// the turbo LoRA and the sigma shift are simply on the wire — and two of the stacks want neither. They
    /// are taken out by pointing the node below them at the node above, which leaves them orphaned;
    /// <see cref="PruneToOutputs"/> then deletes them along with everything else the sink cannot reach. The
    /// five shipped LoRA seats go the same way: the chain is rebuilt from <c>55:3705</c> out of nodes this
    /// method adds, and the seats fall out unreferenced rather than being left switched off with a
    /// <c>lora_name</c> ComfyUI would still have to validate.</para>
    ///
    /// <para><b>The two relays.</b> 🍥 and 🐰 are not one sampler with different widgets — each is two
    /// samplers, the second reading the first. Both are grafted per branch, because the base pass and the
    /// continuation loop never share a sampler: whatever is done to one has to be done to the other, or a
    /// take comes out with its opening on one scheme and its continuations on another.</para>
    /// </summary>
    public partial class MiniMaxI2VViewModel
    {
        // ── The model wire ──────────────────────────────────────────────────────────────────────────
        private const string NodeModelLoader = "55:3701";   // DiffusionModelLoaderKJ
        private const string NodeTurboLora = "55:3690";     // LoraLoaderModelOnly, lightx2v 4-step turbo
        private const string NodeSigmaShift = "55:3704";    // MiniMaxH3SigmaShift
        private const string NodeSpectrum = "55:3705";      // SpectrumApplyMiniMaxH3 (disabled pass-through)
        private static readonly string[] NodePreview = { "39", "51" };   // ModelPreviewOverrideKJ → the SLA nodes

        // ── Schedules and samplers ──────────────────────────────────────────────────────────────────
        private const string NodeDraftSched = "draft_sched";    // BasicScheduler feeding draft_split
        private const string NodeFinishSigmas = "finish_sigmas"; // ManualSigmas on the upscale finish
        private const string NodeBaseKSampler = "4145:145";
        private const string NodeLoopKSampler = "4146:71";

        // ── Conditioning the relays need to build their own guiders from ────────────────────────────
        private const string NodeLoopCond = "4146:121";     // MiniMaxH3AddGuide — the loop's positive
        private const string NodeBaseZeroOut = "4145:135";  // ConditioningZeroOut — the base pass's negative
        private const string NodeLoopZeroOut = "4146:67";
        private const string NodeLoopCounter = "4146:126";  // easy forLoopStart; output 1 is the index

        // The guiders the latent-upscale finish reads. 🐰 points these at its cleanup wire, the way
        // h3-bunny.json's own finish sampler reads the Combat LoRA at 0.65 rather than at full strength.
        private static readonly string[] NodeBaseFinishGuiders = { "4145:4223", "4145:4215" };
        private static readonly string[] NodeLoopFinishGuiders = { "4146:4236", "4146:4237" };

        /// <summary>
        /// One branch of the graph, named by the nodes a stack patch has to reach. The base pass and the
        /// continuation loop are the same shape twice over, so every graft below is written once and run
        /// for each.
        /// </summary>
        private readonly record struct Branch(
            string Tag,
            string Sampler,          // the SamplerCustomAdvanced that paints the pass
            string FullSigmas,       // BasicScheduler holding the whole schedule
            string KSampler,         // KSamplerSelect
            string Sla,              // H3SLAAttention — the end of the model wire
            string Positive,         // the conditioning the pass is sampled against
            string Negative,         // its zeroed counterpart
            string[] FinishGuiders);

        private static Branch BaseBranch => new(
            "base", NodeBaseSampler, NodeBaseFullSigmas, NodeBaseKSampler, "sla_base",
            NodeBaseRef2V, NodeBaseZeroOut, NodeBaseFinishGuiders);

        private static Branch LoopBranch => new(
            "loop", NodeLoopSampler, NodeLoopFullSigmas, NodeLoopKSampler, "sla_loop",
            NodeLoopCond, NodeLoopZeroOut, NodeLoopFinishGuiders);

        /// <summary>What a stack does to the shared wire. The relays carry their sampler and scheduler on
        /// their own nodes, so those two fields are what the non-relay stacks sample with.</summary>
        private readonly record struct StackSpec(
            string Sampler,
            string Scheduler,
            bool KeepTurboLora,
            double ShiftVideo,
            double ShiftAudio);

        private static StackSpec SpecFor(I2VStack stack, bool erSde) => stack switch
        {
            // h3-eros.json: er_sde/beta on the 10Eros hybrid, no MiniMaxH3SigmaShift anywhere in the file.
            I2VStack.Eros => new StackSpec("er_sde", "beta", false, 0, 0),

            // h3-singularity.json: euler/simple and the author's 12/3 shift — or the Eros sampler over it.
            I2VStack.Singularity => erSde
                ? new StackSpec("er_sde", "beta", false, 12, 3)
                : new StackSpec("euler", "simple", false, 12, 3),

            // h3-taomate.json: both legs are ClownsharKSampler_Beta, so the sampler and scheduler here are
            // only what the (pruned) KSamplerSelect would have said. The 12/6 shift is on the first leg.
            I2VStack.TaoMate => new StackSpec("euler", "simple", false, 12, 6),

            // h3-bunny.json: res_multistep/simple on the hybrid, and no sigma shift.
            I2VStack.Bunny => new StackSpec("res_multistep", "simple", false, 0, 0),

            // h3-parasyte.json: res_multistep/simple on the hybrid, no sigma shift, and the Parasyte
            // turbo LoRA in place of the shipped lightx2v one. The graph's own H3SLAAttention nodes are
            // already last on each branch's wire, so this stack adds no patch of its own.
            I2VStack.Parasyte => new StackSpec("res_multistep", "simple", false, 0, 0),

            // The graph as authored.
            _ => new StackSpec("euler", "simple", true, 12, 3),
        };

        /// <summary>The LoRAs a stack puts on the <i>shared</i> wire — the one every sampler reads. 🍥's
        /// belongs to its second leg alone and 🐰's cleanup strength to its second sampler alone, so those
        /// are grafted by the relays rather than listed here.</summary>
        private static IEnumerable<(string Name, double Strength)> MainWireLoras(I2VStack stack) => stack switch
        {
            I2VStack.Bunny => new[] { (BunnyCombatLora, 1.0) },
            I2VStack.Parasyte => new[] { (ParasyteTurboLora, 1.0) },
            _ => Array.Empty<(string, double)>(),
        };

        /// <summary>The LoRA 🦠 is built around, at the strength the authored graph leaves it on. It goes on
        /// the shared wire above the SLA patch, so both the base pass and the continuation loop sample
        /// against it.</summary>
        private const string ParasyteTurboLora = "H3/H3-PK-Parasyte-Turbo.safetensors";

        private const string BunnyCombatLora = "H3/H3_Combat_V2.safetensors";
        private const double BunnyCleanupStrength = 0.65;

        /// <summary>Where BUNNY cuts the schedule: the last quarter is the cleanup that does not re-noise.</summary>
        private const double BunnySplitDenoise = 0.25;

        private const string TaoMateLora = "H3/taomate_h3_3step_comfy.safetensors";
        private const double TaoMateLoraStrength = 0.65;

        /// <summary>Steps of the relay's schedule spent on the base weights before the TaoMate LoRA
        /// resamples the rest. The authored <c>steps_to_run</c>, and why the slider's floor is seven.</summary>
        private const int TaoMateFirstLegSteps = 6;

        /// <summary>
        /// Writes the queued item's render card into the graph: the checkpoint, the model wire, the LoRA
        /// stack, the step count, the sampler and scheduler, the finishing sigmas, whichever relay the stack
        /// needs, and RIFE.
        ///
        /// <para>Called from <see cref="BuildWorkflow"/> after the canvas and attention are set and
        /// <i>before</i> the sampling-scheme section, which owns the draft/finish sigma wiring and may
        /// override what a relay set here — see the comment at the call site.</para>
        /// </summary>
        private static void ApplyRenderStack(JsonObject root, MiniMaxI2VQueueItem item, string sink, long runSeed)
        {
            var spec = SpecFor(item.Stack, item.SingularityErSde);

            // ── The checkpoint ────────────────────────────────────────────────
            if (item.DiffusionModel.Length > 0)
            {
                RequireClass(root, NodeModelLoader, "DiffusionModelLoaderKJ");
                SetInput(root, NodeModelLoader, "model_name", item.DiffusionModel);
            }

            // ── The model wire ────────────────────────────────────────────────
            // Built from the Sol-Attn switch outwards. Nodes a stack does not want are not disabled, they
            // are stepped over: the node below is pointed at the node above, and the prune takes them.
            var wire = NodeSparseAttention;     // 55:3706 — the switch the attention backend feeds

            if (spec.KeepTurboLora)
            {
                Link(root, NodeTurboLora, "model", wire, 0);
                wire = NodeTurboLora;
            }

            if (spec.ShiftVideo > 0)
            {
                SetInput(root, NodeSigmaShift, "shift_video", spec.ShiftVideo);
                SetInput(root, NodeSigmaShift, "shift_audio", spec.ShiftAudio);
                Link(root, NodeSigmaShift, "model", wire, 0);
                wire = NodeSigmaShift;
            }

            Link(root, NodeSpectrum, "model", wire, 0);
            wire = NodeSpectrum;

            // The user's own LoRAs first, then whatever the stack is built around, so a stack's identity
            // sits closest to the sampler. The five shipped seats are left unreferenced and pruned.
            var userWire = BuildLoraChain(root, wire, item.Loras
                .Where(l => l.IsActive)
                .Select(l => (l.Name, l.Strength)), "i2v_lora_");

            var mainWire = BuildLoraChain(root, userWire, MainWireLoras(item.Stack), "i2v_stacklora_");

            foreach (var preview in NodePreview) Link(root, preview, "model", mainWire, 0);

            // ── Steps, sampler and scheduler ──────────────────────────────────
            var steps = item.FirstPassSteps > 0
                ? Math.Clamp(item.FirstPassSteps, MinStepsFor(item.Stack), MaxSteps)
                : AuthoredStepsFor(item.Stack, item.SingularityErSde);

            foreach (var id in new[] { NodeDraftSched, NodeBaseFullSigmas, NodeLoopFullSigmas })
            {
                SetInput(root, id, "steps", steps);
                SetInput(root, id, "scheduler", spec.Scheduler);
            }

            // draft_split cuts the unshifted schedule in half: the draft runs the top of it and the finish
            // pass takes over at the upscaled canvas. The cut has to move with the step count or a longer
            // schedule would hand the draft proportionally more of the denoising than it was tuned for.
            SetInput(root, NodeDraftSigmas, "step", Math.Max(1, steps / 2));

            SetInput(root, NodeBaseKSampler, "sampler_name", spec.Sampler);
            SetInput(root, NodeLoopKSampler, "sampler_name", spec.Sampler);

            // ── The finishing sigmas ──────────────────────────────────────────
            if (FinishSigmas.TryGetValue(item.UpscaleSteps, out var sigmas))
                SetInput(root, NodeFinishSigmas, "sigmas", sigmas);

            // ── Whichever relay the stack is ──────────────────────────────────
            switch (item.Stack)
            {
                case I2VStack.Bunny:
                    ApplyBunnyRelay(root, BaseBranch, userWire);
                    ApplyBunnyRelay(root, LoopBranch, userWire);
                    break;

                case I2VStack.TaoMate:
                    ApplyTaoMateRelay(root, BaseBranch, userWire, steps, runSeed, item);
                    ApplyTaoMateRelay(root, LoopBranch, userWire, steps, runSeed, item);
                    break;
            }

            // ── RIFE ──────────────────────────────────────────────────────────
            if (item.UseRife) GraftRife(root, sink);
        }

        /// <summary>
        /// 🐰 <b>BUNNY</b> on one branch. The whole schedule is built once, three steps are woven into the
        /// mid sigmas where the motion is decided, and the result is cut at 75%: the pass already in the
        /// graph samples the first three quarters with the clip's own noise on the Combat LoRA at full
        /// strength, and a second sampler added here runs the tail out on <c>DisableNoise</c> — it does not
        /// re-noise, it simply finishes the schedule — against the same LoRA at 0.65.
        ///
        /// <para>The sigmas are the full schedule rather than <c>draft_split</c>'s half: BUNNY denoises to
        /// zero at the sampling canvas and the tab's latent upscale is the finish grafted on top, which is
        /// exactly how h3-bunny.json is put together.</para>
        /// </summary>
        private static void ApplyBunnyRelay(JsonObject root, Branch branch, string userWire)
        {
            var ext = $"bn_ext_{branch.Tag}";
            var split = $"bn_split_{branch.Tag}";
            var cleanupLora = $"bn_cleanup_{branch.Tag}";
            var cleanupSla = $"bn_sla_{branch.Tag}";
            var cleanupGuider = $"bn_guide_{branch.Tag}";
            var stage2 = $"bn_stage2_{branch.Tag}";
            const string noNoise = "bn_nonoise";

            root[ext] = Node("ExtendIntermediateSigmas", $"BUNNY mid sigmas ({branch.Tag})", new JsonObject
            {
                ["sigmas"] = new JsonArray(branch.FullSigmas, 0),
                ["steps"] = 3,
                ["start_at_sigma"] = 0.65,
                ["end_at_sigma"] = 0.28,
                ["spacing"] = "sine",
            });

            root[split] = Node("SplitSigmasDenoise", $"BUNNY split at {1 - BunnySplitDenoise:0%} ({branch.Tag})",
                new JsonObject
                {
                    ["sigmas"] = new JsonArray(ext, 0),
                    ["denoise"] = BunnySplitDenoise,
                });

            root[noNoise] = Node("DisableNoise", "BUNNY cleanup takes no fresh noise", new JsonObject());

            // The cleanup wire: the user's LoRAs, then the Combat LoRA at 0.65 — its own SLA node, because
            // SLA has to sit last on a model wire and this one forks before the shared node.
            root[cleanupLora] = Node("LoraLoaderModelOnly", $"Combat LoRA {BunnyCleanupStrength:0.00} ({branch.Tag})",
                new JsonObject
                {
                    ["lora_name"] = BunnyCombatLora,
                    ["strength_model"] = BunnyCleanupStrength,
                    ["model"] = new JsonArray(userWire, 0),
                });

            root[cleanupSla] = CloneNode(root, branch.Sla, new JsonObject { ["model"] = new JsonArray(cleanupLora, 0) });

            root[cleanupGuider] = Node("BasicGuider", $"BUNNY cleanup guider ({branch.Tag})", new JsonObject
            {
                ["model"] = new JsonArray(cleanupSla, 0),
                ["conditioning"] = new JsonArray(branch.Positive, 0),
            });

            // Stage 1 is the sampler already in the graph, moved onto the top of the split schedule.
            Link(root, branch.Sampler, "sigmas", split, 0);

            // Everything that read stage 1 now reads stage 2 — including the LTXVSeparateAVLatent that
            // takes the denoised output off slot 1. Done before stage 2 exists, so its own latent_image is
            // not rewritten to point at itself.
            Retarget(root, branch.Sampler, 0, stage2);
            Retarget(root, branch.Sampler, 1, stage2, 1);

            root[stage2] = Node("SamplerCustomAdvanced", $"BUNNY cleanup ({branch.Tag})", new JsonObject
            {
                ["noise"] = new JsonArray(noNoise, 0),
                ["guider"] = new JsonArray(cleanupGuider, 0),
                ["sampler"] = new JsonArray(branch.KSampler, 0),
                ["sigmas"] = new JsonArray(split, 1),
                ["latent_image"] = new JsonArray(branch.Sampler, 0),
            });

            // h3-bunny.json's own finish sampler reads the Combat LoRA at 0.65, not at full strength.
            foreach (var guider in branch.FinishGuiders) Link(root, guider, "model", cleanupSla, 0);
        }

        /// <summary>
        /// 🍥 <b>TaoMate</b> on one branch. One linear/euler beta57 schedule is shared by two
        /// <c>ClownsharKSampler_Beta</c> legs: the first runs <see cref="TaoMateFirstLegSteps"/> of it on the
        /// base weights with the 12/6 shift, and the second resamples the rest on the TaoMate 3-step LoRA at
        /// 0.65 — which, as the authored graph has it, reads the checkpoint without the attention backend
        /// and without the shift.
        ///
        /// <para>The pair replaces the branch's <c>SamplerCustomAdvanced</c> outright, so the
        /// <c>KSamplerSelect</c> and <c>BasicScheduler</c> that fed it fall out in the prune. There is no
        /// draft canvas on this stack: the pass is painted at the Quality size and the decoded frames are
        /// doubled by RTX Video Super Resolution instead of the latent being lifted.</para>
        /// </summary>
        private static void ApplyTaoMateRelay(
            JsonObject root, Branch branch, string userWire, int steps, long runSeed,
            MiniMaxI2VQueueItem item)
        {
            var relayLora = $"tm_lora_{branch.Tag}";
            var relaySla = $"tm_sla_{branch.Tag}";
            var leg1 = $"tm_leg1_{branch.Tag}";
            var leg2 = $"tm_leg2_{branch.Tag}";

            // The LoRA leg's wire: the raw checkpoint, the user's LoRAs, then TaoMate — no attention backend
            // and no sigma shift, which is how h3-taomate.json wires it. Rebuilt from the loader rather than
            // forked off the shared wire, because the shared wire has both of those on it by then.
            var legWire = userWire == NodeSpectrum
                ? NodeModelLoader
                : RebuildLoraChain(root, NodeModelLoader, item.Loras.Where(l => l.IsActive), $"tm_user_{branch.Tag}_");

            root[relayLora] = Node("LoraLoaderModelOnly", $"TaoMate 3-step LoRA ({branch.Tag})", new JsonObject
            {
                ["lora_name"] = TaoMateLora,
                ["strength_model"] = TaoMateLoraStrength,
                ["model"] = new JsonArray(legWire, 0),
            });

            root[relaySla] = CloneNode(root, branch.Sla, new JsonObject { ["model"] = new JsonArray(relayLora, 0) });

            // The latent the branch's own sampler was given — the guide/noise-mask switch, not the raw
            // reference latent, so a continuation still starts from its blended tail.
            var latent = LinkOf(root, branch.Sampler, "latent_image")
                         ?? throw new Exception($"Workflow node '{branch.Sampler}' has no latent_image link — " +
                                                "the workflow file no longer matches this tab.");

            var seed = SeedSourceFor(root, branch, runSeed);

            root[leg1] = Node("ClownsharKSampler_Beta", $"TaoMate leg 1 ({branch.Tag})", new JsonObject
            {
                ["model"] = new JsonArray(branch.Sla, 0),
                ["positive"] = new JsonArray(branch.Positive, 0),
                ["negative"] = new JsonArray(branch.Negative, 0),
                ["latent_image"] = latent.DeepClone(),
                ["eta"] = 0.5,
                ["sampler_name"] = "linear/euler",
                ["scheduler"] = "beta57",
                ["steps"] = steps,
                ["steps_to_run"] = TaoMateFirstLegSteps,
                ["denoise"] = 1.0,
                ["cfg"] = 1.0,
                ["seed"] = seed.DeepClone(),
                ["sampler_mode"] = "standard",
                ["bongmath"] = true,
            });

            // Retarget before leg 2 exists, for the reason the BUNNY graft gives. Clownshar hands back one
            // latent, so the readers that took the branch's denoised output off slot 1 are moved to slot 0.
            Retarget(root, branch.Sampler, 0, leg2);
            Retarget(root, branch.Sampler, 1, leg2);

            root[leg2] = Node("ClownsharKSampler_Beta", $"TaoMate leg 2 ({branch.Tag})", new JsonObject
            {
                ["model"] = new JsonArray(relaySla, 0),
                ["positive"] = new JsonArray(branch.Positive, 0),
                ["negative"] = new JsonArray(branch.Negative, 0),
                ["latent_image"] = new JsonArray(leg1, 0),
                ["eta"] = 0.5,
                ["sampler_name"] = "linear/euler",
                ["scheduler"] = "beta57",
                ["steps"] = steps,
                ["steps_to_run"] = -1,
                ["denoise"] = 1.0,
                ["cfg"] = 1.0,
                ["seed"] = seed.DeepClone(),
                ["sampler_mode"] = "resample",
                ["bongmath"] = true,
            });
        }

        /// <summary>
        /// The seed a Clownshar leg takes. The base pass has one number. The loop does not: its seed is
        /// chosen per iteration by an index switch over three <c>RandomNoise</c> nodes, and Clownshar takes
        /// an INT rather than a NOISE — so this builds the same switch over three literals, indexed off the
        /// same loop counter, and the take still reproduces from one number.
        /// </summary>
        private static JsonNode SeedSourceFor(JsonObject root, Branch branch, long runSeed)
        {
            if (branch.Tag != "loop") return JsonValue.Create(runSeed)!;

            const string switchId = "tm_loop_seed";
            if (root[switchId] == null)
            {
                var values = new JsonObject
                {
                    ["index"] = new JsonArray(NodeLoopCounter, 1),
                };
                for (var i = 0; i < MaxContinuations; i++)
                {
                    var id = $"tm_loop_seed_{i}";
                    root[id] = Node("PrimitiveInt", $"Continuation {i + 1} seed", new JsonObject
                    {
                        ["value"] = runSeed + i + 1,
                    });
                    values[$"value{i}"] = new JsonArray(id, 0);
                }
                root[switchId] = Node("easy anythingIndexSwitch", "TaoMate continuation seed", values);
            }

            return new JsonArray(switchId, 0);
        }

        /// <summary>
        /// Puts RIFE between the chosen sink and whatever was feeding it, and doubles the sink's frame rate
        /// to match. Unlike the Express graphs this one is not authored with RIFE at all, so the node is
        /// added rather than switched — and only in front of the sink the run actually saves, which is why
        /// this happens after the sink is chosen.
        /// </summary>
        private static void GraftRife(JsonObject root, string sink)
        {
            var images = LinkOf(root, sink, "images");
            if (images == null) return;

            const string rife = "i2v_rife";
            root[rife] = Node("RIFEInterpolation", "RIFE 24 → 48 fps", new JsonObject
            {
                ["images"] = images.DeepClone(),
                ["source_fps"] = 24.0,
                ["target_fps"] = 48.0,
                ["scale"] = 1,
                ["model_name"] = "flownet.pkl",
                ["batch_size"] = 8,
                ["use_fp16"] = true,
            });

            Link(root, sink, "images", rife, 0);
            SetInput(root, sink, "frame_rate", 48);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>Chains LoRA loaders onto a model wire and hands back the end of it. A row with nothing
        /// chosen, or at strength 0, is skipped rather than loaded as a no-op.</summary>
        private static string BuildLoraChain(
            JsonObject root, string wire, IEnumerable<(string Name, double Strength)> loras, string idPrefix)
        {
            var slot = 0;
            foreach (var (name, strength) in loras)
            {
                if (string.IsNullOrWhiteSpace(name) || strength <= 0.0) continue;
                var id = $"{idPrefix}{slot}";
                root[id] = Node("LoraLoaderModelOnly", $"LoRA {slot + 1} — {strength:0.00}", new JsonObject
                {
                    ["lora_name"] = name.Trim().Replace('\\', '/'),
                    ["strength_model"] = strength,
                    ["model"] = new JsonArray(wire, 0),
                });
                wire = id;
                slot++;
            }
            return wire;
        }

        /// <summary>The same chain again on a different wire, for a relay leg that has to start from the
        /// bare checkpoint rather than fork off the shared one.</summary>
        private static string RebuildLoraChain(
            JsonObject root, string wire, IEnumerable<MiniMaxI2VLoraChoice> loras, string idPrefix) =>
            BuildLoraChain(root, wire, loras.Select(l => (l.Name, l.Strength)), idPrefix);

        /// <summary>A node in ComfyUI's API shape.</summary>
        private static JsonObject Node(string classType, string title, JsonObject inputs) => new()
        {
            ["inputs"] = inputs,
            ["class_type"] = classType,
            ["_meta"] = new JsonObject { ["title"] = title },
        };

        /// <summary>
        /// A copy of an existing node with some of its inputs replaced — used to give a forked model wire
        /// its own H3SLAAttention without restating the widgets the run has already set on the original.
        /// </summary>
        private static JsonObject CloneNode(JsonObject root, string sourceId, JsonObject overrides)
        {
            if (root[sourceId] is not JsonObject source)
                throw new Exception($"Workflow node '{sourceId}' is missing — the workflow file no longer " +
                                    "matches this tab.");

            var clone = source.DeepClone().AsObject();
            if (clone["inputs"] is not JsonObject inputs) return clone;
            foreach (var kv in overrides) inputs[kv.Key] = kv.Value?.DeepClone();
            return clone;
        }

        /// <summary>The link on one of a node's inputs, or null when that input is a widget value.</summary>
        private static JsonNode? LinkOf(JsonObject root, string nodeId, string input) =>
            root[nodeId]?["inputs"] is JsonObject inputs &&
            inputs[input] is JsonArray link && link.Count == 2
                ? link
                : null;

        /// <summary>The slider's floor for a stack, without needing the live card — a queued item carries
        /// its own stack, and a queue restored from an older file may carry a count from another one.</summary>
        private static int MinStepsFor(I2VStack stack) =>
            stack == I2VStack.TaoMate ? TaoMateMinSteps : MinSteps;
    }
}
