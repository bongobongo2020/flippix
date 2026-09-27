using System.Text.Json.Nodes;
using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ⚡ H3 Express's fifth stack: 🦠 <b>Parasyte</b> — <c>workflow/video/h3-minimax/h3-parasyte.json</c>,
    /// built from PlagueKind's MiniMax H3 Sparse Attention render by <c>tools/build_h3_parasyte.py</c>.
    ///
    /// <para><b>What it is.</b> The current-generation PlagueKind build, and it differs from the other four
    /// in three ways. <c>H3SLAAttention</c> — block-sparse attention — sits <b>last on the model wire</b>,
    /// feeding the guiders and schedulers directly; that placement is the whole reason the patch does
    /// anything. The sampler is <c>res_multistep</c>/<c>simple</c> at thirteen steps on
    /// <c>H3/H3-PK-Parasyte-Turbo</c> at 1.00. And the finish is one node: <c>MMH3UltimateUpscale</c> does
    /// the latent upscale <i>and</i> the second pass together, handing back a single AV latent that both
    /// decodes read — so unlike every other stack here there is no <c>LTXVConcatAVLatent</c> and no separate
    /// upscale sampler.</para>
    ///
    /// <para><b>What SLA buys, and what it does not.</b> Measured on the 4090: 1.42× at sparsity 0.85 and
    /// 1.63× at 0.90. It is a <i>speed</i> patch — it saves no VRAM and will never fix an OOM. It also fails
    /// <b>silently</b>: a missing Triton or an incompatible GPU is caught and the model passes through
    /// unpatched, which is indistinguishable from "no speedup". And below the node's <c>min_seq_len</c> the
    /// kernel falls back to dense, so a short clip at a small draft canvas is expected to show no gain at
    /// all. See <see cref="ParasyteSparsity"/> for why this ships 0.85 rather than the export's 0.90.</para>
    ///
    /// <para><b>Why the fps checkbox is different here.</b> The other stacks double frames with
    /// <c>RIFEInterpolation</c>; the authored graph interpolates with FILM. Both live at the shared id
    /// <c>165</c> so the sink wiring, the chain trim and the frame rate are written once —
    /// <see cref="WireInterpolation"/> is the seam that writes whichever node the stack actually ships.</para>
    ///
    /// <para><b>What it does not change.</b> Everything a story is: the folder loop, the cast and the cards,
    /// the wardrobe, the sheets, the clip writer and the saved prompts, the @char tags and the reference
    /// panels, the per-clip prompt editor, the seed each clip is given, the join, and the optional LoRA —
    /// spliced into the same <c>Power Lora Loader</c> seat, which here sits above the Parasyte LoRA and the
    /// SLA patch so it reaches the whole wire.</para>
    /// </summary>
    public partial class H3ExpressViewModel
    {
        /// <summary>PlagueKind's sparse-attention graph, flattened to API format and given the tab's three
        /// hunt branches by <c>tools/build_h3_parasyte.py</c>. Its ids are <c>h3-eros.json</c>'s wherever
        /// <see cref="H3ErosViewModel.ApplyCommonInputs"/> writes to one, which is what keeps the prompt,
        /// the references, the canvas, the length, the steps and the LoRA splice shared with every other
        /// stack.</summary>
        private const string ParasyteWorkflow = "workflow/video/h3-minimax/h3-parasyte.json";

        /// <summary>The checkpoint the authored graph loads: the <b>fl2va/ref2va hybrid</b>, the same one
        /// 🐰 BUNNY ships. H3 Express is always all-reference — every clip conditions on cast panels wired
        /// into <c>MiniMaxH3ReferenceToVideo</c>, never on a first or last frame — and the hybrid is what
        /// the author prescribes for that mode.</summary>
        public const string ParasyteModel = "h3-minimax/minimax_h3_hybrid_fl2va_ref2va_b25-49-int8.safetensors";

        /// <summary>The authored schedule length, written into <c>22:8</c>.</summary>
        private const int ParasyteSteps = 13;

        /// <summary>
        /// The sparsity the graph ships. The author's export is at 0.90; this is 0.85, which is what every
        /// other <c>H3SLAAttention</c> call site in this app pins, what lightx2v ships, and what the SLA
        /// turbo LoRA was distilled against.
        ///
        /// <para>The difference is not free either way: 0.90 is ~13% quicker, but the one measurement taken
        /// here had the soundtrack come back ~11 dB hotter in RMS at the same peak with ~18% less of the
        /// 300–3400 Hz speech band. That was n=1, so indicative rather than settled — but 0.85 is the
        /// conservative end of a trade the user cannot see happening.</para>
        /// </summary>
        private const double ParasyteSparsity = 0.85;

        private const string NodeParasyteSla = "pk:sla";      // H3SLAAttention — last on the model wire
        private const string NodeParasyteUpscale = "pk:ult";  // MMH3UltimateUpscale — upscale + 2nd pass
        private const string NodeParasyteFilm = "pk:film";    // FrameInterpolationModelLoader — FILM weights

        private bool _useParasyte;

        /// <summary>
        /// Reads the remembered stack and, if it is this one, moves the model dropdown onto its checkpoint.
        /// Written the way <see cref="InitBunny"/> is, and for the same reason: by the time this class
        /// exists the base constructors have already chosen a checkpoint and seeded the dropdown, so the
        /// move is only made when the dropdown is still sitting on a default.
        /// </summary>
        private void InitParasyte()
        {
            _useParasyte = _settingsService.Settings?.H3ExpressUseParasyte ?? false;
            if (!_useParasyte) return;

            // The stacks are one choice. Settings holding more than one — an older file, or a hand edit —
            // is resolved here rather than at every read. This one initialises last, so anything already
            // holding the render keeps it and this clears itself.
            if (UseTaoMate || UseBunny)
            {
                _useParasyte = true;   // so ClearParasyte does the clearing, including the save
                ClearParasyte();
                return;
            }
            ClearSingularity();

            var stored = (RecallDiffusionModel(_settingsService.Settings) ?? string.Empty)
                .Trim().Replace('\\', '/');
            OfferShippedModel();
            if (stored.Length == 0 || stored == base.ShippedModel)
                SelectedDiffusionModel = ParasyteModel;

            AddLog($"  🦠 Parasyte is ON: every clip is sampled on h3-parasyte.json — res_multistep/simple " +
                   $"at {ParasyteSteps} steps on the Parasyte turbo LoRA, sparse attention at " +
                   $"{ParasyteSparsity:0.00}, finished by MMH3UltimateUpscale and FILM.");
        }

        // ── The switch ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether every clip is sampled on the Parasyte stack instead of one of the other four.
        ///
        /// <para>Exclusive with them — see <see cref="Stack"/>, which is what the page's radio group binds.
        /// Persisted, and refused mid-run for the reason <see cref="H3BatchViewModel.CanChangeWorkflow"/>
        /// gives: the workflow file and the step count are read live as each clip's graph is built, so a
        /// folder switched halfway is a folder of films that do not match each other.</para>
        /// </summary>
        public bool UseParasyte
        {
            get => _useParasyte;
            set
            {
                if (_useParasyte == value) return;
                if (value) { ClearTaoMate(); ClearBunny(); ClearSingularity(); }
                _useParasyte = value;
                OnPropertyChanged();
                RaiseStackState();

                // The checkpoint is the stack, exactly as it is for the others: the dropdown moves with the
                // choice and says so, because the next thing the user does may be to move it back.
                OfferShippedModel();
                SelectedDiffusionModel = ShippedModel;

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.H3ExpressUseParasyte = value;
                    _settingsService.SaveSettings(settings);
                }

                AddLog(value
                    ? $"{TabDisplayName}: 🦠 Parasyte ON — every clip is sampled on h3-parasyte.json " +
                      $"({LabelFor(ParasyteModel)}, res_multistep/simple at {ParasyteSteps} steps on " +
                      $"{LabelFor(ParasyteTurboLora)} at 1.00, sparse attention at {ParasyteSparsity:0.00}). " +
                      "The draft canvas stays, but the upscale and the second pass are one " +
                      "MMH3UltimateUpscale node, and the fps checkbox doubles frames with FILM, not RIFE."
                    : $"{TabDisplayName}: Parasyte off — every clip is sampled on the H3 Eros stack again.");

                if (value)
                    AddLog("  Note: sparse attention is a speed patch, not a memory one — it will not fix " +
                           "an OOM, and it fails silently (a missing Triton or a short clip at a small " +
                           "draft canvas both read as 'no speedup'). Expect ~1.4× on a full-length clip.");
            }
        }

        /// <summary>The LoRA the stack is built around — named here only so the log line can label it.
        /// The graph carries it; nothing in C# writes it.</summary>
        private const string ParasyteTurboLora = "H3/H3-PK-Parasyte-Turbo.safetensors";

        /// <summary>Turns 🦠 off <b>quietly</b> — no log line, and the model dropdown left where it is — for
        /// another stack that is taking the render over and is about to say so itself. The counterpart of
        /// <see cref="ClearBunny"/>.</summary>
        private void ClearParasyte()
        {
            if (!_useParasyte) return;
            _useParasyte = false;
            OnPropertyChanged(nameof(UseParasyte));

            var settings = _settingsService.Settings;
            if (settings != null)
            {
                settings.H3ExpressUseParasyte = false;
                _settingsService.SaveSettings(settings);
            }
        }

        // ── The finish ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The Parasyte finish. The same shape as the base's — the picked branch's seed written back, the
        /// finished canvas set, the fixed sigmas linked, the sink named and the graph pruned — but against
        /// one node instead of three: <c>MMH3UltimateUpscale</c> takes the picked latent, the noise, the
        /// sampler and the sigmas and hands back an AV latent, so there is no latent split to point at it
        /// and no concat to put the audio back.
        /// </summary>
        /// <remarks>Not an <c>override</c> — see <see cref="BuildTaoMateFinish"/>; the one override is in
        /// H3ExpressViewModel.Stacks.cs and dispatches here.</remarks>
        private FinishSubmission BuildParasyteFinish(
            JsonObject root, ErosHuntClip row, H3CastQueueItem item, int chosen, long seed, string runToken)
        {
            RequireClass(root, NodeParasyteUpscale, "MMH3UltimateUpscale");
            RequireClass(root, NodeParasyteSla, "H3SLAAttention");

            var (fw, fh) = H3Canvas.Resolve(item.AspectRatio, item.Megapixels, 32);
            var (sampler, _, noise) = SampleBranches[chosen - 1];
            SetInput(root, noise, "noise_seed", seed);

            // The picked branch's denoised latent is what gets upscaled — the AV latent whole, not a
            // separated video one, which is what this node takes.
            Link(root, NodeParasyteUpscale, "latent", sampler, DenoisedSlot);

            // The graph's "skip the upscale" decodes. Nothing consumes them, so the prune below removes
            // them; they are repointed anyway so the graph never carries a link into a deleted sampler.
            Link(root, NodeSinglePassVideo, "samples", sampler, DenoisedSlot);
            Link(root, NodeSinglePassAudio, "samples", sampler, DenoisedSlot);

            // The finished canvas, and the pass that paints it. Its noise is fresh every finish, the way
            // the other stacks' upscale pass is — re-rolling it is how the same picked composition is
            // offered again slightly differently.
            SetInput(root, NodeParasyteUpscaleModel, "width", fw);
            SetInput(root, NodeParasyteUpscaleModel, "height", fh);
            SetInput(root, NodeUpscaleNoise, "noise_seed", System.Random.Shared.NextInt64(0, long.MaxValue));
            Link(root, NodeParasyteUpscale, "sigmas",
                 SigmaSchedules.TryGetValue(item.UpscaleSteps, out var sigmas) ? sigmas : SigmaSchedules[4], 0);

            SetInput(root, NodeParasyteSla, "sparsity_ratio", ParasyteSparsity);

            WireSink(root, item, NodeUpscaledVideo, NodeUpscaledAudio, runToken);

            var json = PruneToOutputs(root.ToJsonString(), FinishOutputs(item), out var pruned);
            AddLog($"{row.Title}: finishing take {chosen} (seed {seed}, {item.UpscaleSteps} fixed sigmas " +
                   $"at {fw}×{fh} through MMH3UltimateUpscale, sparse attention {ParasyteSparsity:0.00}, " +
                   $"{(item.UseRife ? $"FILM → {DraftFrameRate * 2}fps" : $"{DraftFrameRate}fps")}). " +
                   $"Finish graph: the picked branch kept, {pruned} node(s) removed.");
            return new FinishSubmission(json, NodeFinalSave, fw, fh);
        }

        /// <summary>MMH3LatentUpscaleWithModelParams — the finished canvas, as a param object the upscale
        /// node reads rather than two widgets on the node itself.</summary>
        private const string NodeParasyteUpscaleModel = "pk:upmodel";

        /// <summary>
        /// Where 🔗 pins the finish pass on this stack. The other latent-upscale stacks split the finish
        /// into <c>MinimaxH3LatentUpscaler3D</c> plus a second <c>SamplerCustomAdvanced</c>, so the chain
        /// intercepts that sampler's <c>BasicGuider</c> and reads its <c>latent_image</c>. Here the finish
        /// is the single <c>MMH3UltimateUpscale</c> node: it takes its conditioning directly — there is no
        /// guider. Its output 0 is the finished AV latent, which is what the next clip is laid against.
        /// </summary>
        private static ChainFinishPin ParasyteFinishPin => new(
            NodeParasyteUpscale, "conditioning", NodeParasyteUpscale);

        private const string NodeParasyteFinishShape = "h3ctx:fshape";

        /// <summary>
        /// A stand-in for the latent 🦠's finish samples, which never exists on a wire:
        /// <c>MMH3UltimateUpscale</c> takes the draft and upscales it inside. The Motion Context only reads
        /// the latent's shape, so an empty AV latent at the finished canvas, as long as the reference node,
        /// is exactly what it needs. The pinned keyframes then arrive at the size the node samples at —
        /// it resizes any other size bilinearly, so a draft-size pin would have been blurred as well.
        /// </summary>
        private JsonArray ParasyteFinishShape(JsonObject root, H3CastQueueItem item)
        {
            var (fw, fh) = H3Canvas.Resolve(item.AspectRatio, item.Megapixels, 32);
            root[NodeParasyteFinishShape] = new JsonObject
            {
                ["inputs"] = new JsonObject
                {
                    ["width"] = fw,
                    ["height"] = fh,
                    ["length"] = root[NodeRef2V]?["inputs"]?["length"]?.DeepClone()
                                 ?? throw new Exception($"Workflow node '{NodeRef2V}' has no length."),
                },
                ["class_type"] = "EmptyMiniMaxH3LatentAV",
                ["_meta"] = new JsonObject { ["title"] = "H3 Motion Context — finish canvas (shape only)" }
            };
            return new JsonArray(NodeParasyteFinishShape, 0);
        }

        /// <summary>
        /// 🦠's frame doubler is FILM, not RIFE. <c>FrameInterpolate</c> takes a loaded model and a
        /// multiplier where <c>RIFEInterpolation</c> takes a source and a target rate, so the shared id
        /// <c>165</c> is written differently here; everything below it — the trim, the mux, the doubled
        /// frame rate — is the base's.
        /// </summary>
        protected override void WireInterpolation(JsonObject root, string pictureFrom)
        {
            if (!UseParasyte) { base.WireInterpolation(root, pictureFrom); return; }

            RequireClass(root, NodeRife, "FrameInterpolate");
            RequireClass(root, NodeParasyteFilm, "FrameInterpolationModelLoader");
            SetInput(root, NodeRife, "multiplier", 2);
            Link(root, NodeRife, "images", pictureFrom, 0);
        }
    }
}
