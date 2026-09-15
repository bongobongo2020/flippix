using System;
using System.Text.Json.Nodes;
using FlipPix.Core.Models;
using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ⚡ H3 Express's third stack: 🍥 <b>TaoMate</b> — <c>workflow/video/h3-minimax/h3-taomate.json</c>,
    /// built from the author's own TaoMate render by <c>tools/build_h3_taomate.py</c>.
    ///
    /// <para><b>What it is.</b> A two-model relay. One ten-step <c>linear/euler</c>/<c>beta57</c> schedule is
    /// split across two <c>ClownsharKSampler_Beta</c> nodes: the first six steps run on the base checkpoint
    /// with comfy-kitchen attention and the 12/6 sigma shift, then RES4LYF hands the schedule's position on
    /// in the latent and the second sampler <i>resamples</i> the rest of it on the same checkpoint plus
    /// <c>H3/taomate_h3_3step_comfy</c> at 0.65. Both legs sample eta 0.5 at cfg 1 with bongmath on.</para>
    ///
    /// <para><b>Why it is a different finish.</b> The other two stacks compose a clip at a draft canvas and
    /// lift the picked latent to the finished one with <c>MinimaxH3LatentUpscaler3D</c>. This one samples at
    /// the real canvas and upscales the <i>decoded frames</i> with <c>RTXVideoSuperResolution</c> ×2 — so
    /// there is no draft size, no second latent pass and no sigma schedules to pick between. The Quality
    /// dropdown therefore names the canvas the model paints, and the file that lands is twice that in each
    /// direction; see <see cref="SampledMegapixels"/> and <see cref="BuildFinish"/>.</para>
    ///
    /// <para><b>What it does not change.</b> Everything a story is: the folder loop, the cast and the cards,
    /// the wardrobe, the sheets, the clip writer and the saved prompts, the @char tags and the reference
    /// panels, the per-clip prompt editor, the seed each clip is given, the join, and the optional LoRA —
    /// which is spliced into the same <c>Power Lora Loader</c> seat as on the other stacks and, here, sits
    /// above the relay's split so it reaches both legs.</para>
    /// </summary>
    public partial class H3ExpressViewModel
    {
        /// <summary>The author's TaoMate relay, flattened to API format by
        /// <c>tools/build_h3_taomate.py</c>. Its ids are <c>h3-eros.json</c>'s wherever
        /// <see cref="H3ErosViewModel.ApplyCommonInputs"/> writes to one, which is what keeps the prompt,
        /// the references, the canvas, the length, the steps and the LoRA splice shared with every other
        /// stack.</summary>
        private const string TaoMateWorkflow = "workflow/video/h3-minimax/h3-taomate.json";

        /// <summary>The checkpoint the authored graph relays on — loaded once here and read by both legs.
        /// It is the <b>fl2va</b> pruned build, which is what the author paired with the TaoMate LoRA; the
        /// conditioning is still <c>MiniMaxH3ReferenceToVideo</c> with the cast's panels in it, and
        /// <c>minimax_h3_ref2va_pruned_int8_convrot</c> is the sibling to try from the Model dropdown if
        /// faces drift.</summary>
        public const string TaoMateModel = "h3-minimax/minimax_h3_fl2va_pruned_int8_convrot.safetensors";

        /// <summary>The whole schedule, both legs. Written into <c>22:8</c>, which both samplers' step
        /// counts are linked to — a resample that continues a schedule of a different length is not a
        /// continuation of the one the first leg stopped in the middle of.</summary>
        private const int TaoMateSteps = 10;

        /// <summary>RTXVideoSuperResolution's multiplier, as authored. Each dimension doubles, so the file
        /// is four times the pixels the model painted.</summary>
        private const double TaoMateUpscale = 2.0;

        private const string NodeTaoPass1 = "tm:pass1";  // ClownsharKSampler_Beta — the base weights
        private const string NodeTaoPass2 = "tm:pass2";  // ClownsharKSampler_Beta — the TaoMate resample
        private const string NodeTaoRtx = "tm:rtx";      // RTXVideoSuperResolution — the frame-space finish

        private bool _useTaoMate;

        /// <summary>
        /// Reads the remembered stack and, if it is this one, moves the model dropdown onto its checkpoint.
        ///
        /// <para>Read into the field for the reason <see cref="H3BatchViewModel"/> reads ✴️ into its own: the
        /// base constructors have already chosen a checkpoint and seeded the dropdown by the time this class
        /// exists. The move is only made when the dropdown is still sitting on a default, never over a
        /// checkpoint that was picked on purpose.</para>
        /// </summary>
        private void InitTaoMate()
        {
            _useTaoMate = _settingsService.Settings?.H3ExpressUseTaoMate ?? false;
            if (!_useTaoMate) return;

            // The two stacks are one choice. Settings holding both — an older file, or a hand edit — is
            // resolved here rather than at every read.
            ClearSingularity();

            var stored = (RecallDiffusionModel(_settingsService.Settings) ?? string.Empty)
                .Trim().Replace('\\', '/');
            OfferShippedModel();
            if (stored.Length == 0 || stored == base.ShippedModel)
                SelectedDiffusionModel = TaoMateModel;

            AddLog($"  🍥 TaoMate is ON: every clip is relayed on h3-taomate.json — {TaoMateSteps} steps, " +
                   "the first six on the base weights and the rest resampled on the TaoMate 3-step LoRA, " +
                   "then upscaled ×2 in frame space by RTX rather than in the latent.");
        }

        // ── The switch ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether every clip is relayed on the TaoMate stack instead of the Singularity or H3 Eros one.
        ///
        /// <para>Exclusive with <see cref="H3BatchViewModel.UseSingularity"/> — three stacks, one choice —
        /// which is why the page binds the radio group below rather than this. Persisted, and refused
        /// mid-run for the reason <see cref="H3BatchViewModel.CanChangeWorkflow"/> gives: the workflow file
        /// and the step count are read live by the render, so a folder switched halfway is a folder of films
        /// that do not match each other.</para>
        /// </summary>
        public bool UseTaoMate
        {
            get => _useTaoMate;
            set
            {
                if (_useTaoMate == value) return;
                if (value) ClearSingularity();
                _useTaoMate = value;
                OnPropertyChanged();
                RaiseStackState();

                // The checkpoint is the stack, exactly as it is for ✴️: the dropdown moves with the choice
                // and says so, because the next thing the user does may be to move it back.
                OfferShippedModel();
                SelectedDiffusionModel = ShippedModel;

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.H3ExpressUseTaoMate = value;
                    _settingsService.SaveSettings(settings);
                }

                AddLog(value
                    ? $"{TabDisplayName}: 🍥 TaoMate ON — every clip is relayed on h3-taomate.json " +
                      $"({LabelFor(TaoMateModel)}, linear/euler/beta57, {TaoMateSteps} steps handed to the " +
                      "TaoMate 3-step LoRA after six), and finished by an RTX ×2 frame upscale instead of " +
                      "the latent one. The cast, the sheets, the clips and the join are unchanged."
                    : $"{TabDisplayName}: TaoMate off — every clip is sampled on the H3 Eros stack again, " +
                      "draft canvas and latent upscale included.");

                // The one thing this stack is expensive at, said when it is actually a problem rather than
                // every time: there is no draft canvas to keep the tensors small, and RTX plus RIFE put four
                // times the pixels and twice the frames through the mux.
                if (value && HasLoadWarning)
                    AddLog($"  ⚠ At {Megapixels:0.##} MP{(UseRife ? " with RIFE on" : string.Empty)} that is " +
                           $"{LoadSummary} Drop the Quality or turn RIFE off in More options if ComfyUI " +
                           "falls over part-way through a clip.");
            }
        }

        /// <summary>The three stacks as one radio group. A radio only ever asks to be turned <i>on</i>; the
        /// group turns the others off, and the getters are computed, so there is no fourth state to keep in
        /// step.</summary>
        public bool StackIsEros
        {
            get => !UseTaoMate && !UseSingularity;
            set { if (value) SelectStack(taoMate: false, singularity: false); }
        }

        /// <inheritdoc cref="StackIsEros"/>
        public bool StackIsSingularity
        {
            get => !UseTaoMate && UseSingularity;
            set { if (value) SelectStack(taoMate: false, singularity: true); }
        }

        /// <inheritdoc cref="StackIsEros"/>
        public bool StackIsTaoMate
        {
            get => UseTaoMate;
            set { if (value) SelectStack(taoMate: true, singularity: false); }
        }

        private void SelectStack(bool taoMate, bool singularity)
        {
            if (taoMate == UseTaoMate && singularity == UseSingularity) return;

            // Through the properties, so each one logs what it did, persists itself and moves the model
            // dropdown onto the stack that ends up chosen. TaoMate goes first when it is being turned off,
            // so ✴️ has the last word on the checkpoint.
            if (taoMate)
            {
                UseTaoMate = true;
            }
            else
            {
                UseTaoMate = false;
                UseSingularity = singularity;
            }
            RaiseStackState();
        }

        private void RaiseStackState()
        {
            OnPropertyChanged(nameof(StackIsEros));
            OnPropertyChanged(nameof(StackIsSingularity));
            OnPropertyChanged(nameof(StackIsTaoMate));
            OnPropertyChanged(nameof(StackSummary));
            OnPropertyChanged(nameof(HuntSummary));
            OnPropertyChanged(nameof(UsesDraftCanvas));
            // The frame stack is four times the pixels on this stack, so the warning changes with it.
            OnPropertyChanged(nameof(LoadSummary));
            OnPropertyChanged(nameof(HasLoadWarning));
        }

        /// <summary>Whether the chosen stack composes at a draft canvas and lifts the latent to the finished
        /// one. False on TaoMate, which paints at the quality canvas and upscales the decoded frames — so the
        /// page hides the two dials (the composition canvas and the finishing sigmas) that would otherwise
        /// sit there doing nothing.</summary>
        public bool UsesDraftCanvas => !UseTaoMate;

        // ── Identity: what the switch actually changes ───────────────────────────────────────────────

        protected override string WorkflowFileName => UseTaoMate ? TaoMateWorkflow : base.WorkflowFileName;

        protected override string ShippedModel => UseTaoMate ? TaoMateModel : base.ShippedModel;

        /// <summary>Ten, both legs. The base's twelve is the Eros hybrid's and ✴️'s ten is the Singularity
        /// checkpoint's; this one is the length of the schedule the relay splits.</summary>
        protected override int FirstPassSteps => UseTaoMate ? TaoMateSteps : base.FirstPassSteps;

        /// <summary>
        /// On this stack the model paints at the Quality dropdown's canvas, not at the draft one: the finish
        /// is a frame-space upscale of what came out of the sampler, so there is nothing for a 0.15 MP draft
        /// to be lifted from and a draft-sized render would simply be a small film upscaled ×2.
        /// </summary>
        protected override double SampledMegapixels(H3CastQueueItem item) =>
            UseTaoMate ? item.Megapixels : base.SampledMegapixels(item);

        // ── What it costs ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The frame-stack warning, told the truth about this stack. It is the one place where TaoMate is
        /// dramatically more expensive than the other two and the difference has to be on screen: the
        /// latent upscalers hand the mux frames at the quality canvas, whereas RTX hands it frames at
        /// <b>twice that in each direction</b> — four times the pixels — and RIFE then doubles how many
        /// there are. At 0.8 MP and seven seconds that is a ~12 GB tensor, which is the size that takes
        /// ComfyUI down mid-render rather than merely being slow.
        /// </summary>
        public override string LoadSummary
        {
            get
            {
                if (!UseTaoMate) return base.LoadSummary;

                var frames = FramesForSeconds(ClampLength(LengthSeconds));
                var (sw, sh) = H3Canvas.Resolve(ResolvedAspectRatio, Megapixels, 32);
                var (fw, fh) = ((int)(sw * TaoMateUpscale), (int)(sh * TaoMateUpscale));
                var sampled = FrameStackGb(frames, sw, sh);
                var upscaled = FrameStackGb(frames, fw, fh);
                var muxed = UseRife ? FrameStackGb(frames * 2, fw, fh) : upscaled;

                var text = $"{frames} frames: ≈{sampled:0.#} GB as sampled at {sw}×{sh}, " +
                           $"≈{upscaled:0.#} GB after RTX takes them to {fw}×{fh}" +
                           (UseRife ? $", ≈{muxed:0.#} GB once RIFE doubles them." : ".");
                return muxed >= HeavyFrameStackGb
                    ? text + " ⚠ That is the size that takes ComfyUI down mid-render — shorten the clip, " +
                             "drop the Quality (RTX doubles whatever it is), or turn RIFE off."
                    : text;
            }
        }

        /// <inheritdoc cref="LoadSummary"/>
        public override bool HasLoadWarning
        {
            get
            {
                if (!UseTaoMate) return base.HasLoadWarning;

                var frames = FramesForSeconds(ClampLength(LengthSeconds));
                var (sw, sh) = H3Canvas.Resolve(ResolvedAspectRatio, Megapixels, 32);
                return FrameStackGb(UseRife ? frames * 2 : frames,
                                    (int)(sw * TaoMateUpscale), (int)(sh * TaoMateUpscale))
                       >= HeavyFrameStackGb;
            }
        }

        // ── The finish ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The TaoMate finish: the clip's seed into the relay's first leg, a derived one into the second,
        /// the RTX upscale confirmed at its authored factor, RIFE in or out, and the graph pruned to the
        /// sink. No latent split, no latent upscaler, no sigma schedule — this stack has none of them.
        /// </summary>
        protected override FinishSubmission BuildFinish(
            JsonObject root, ErosHuntClip row, H3CastQueueItem item, int chosen, long seed, string runToken)
        {
            if (!UseTaoMate) return base.BuildFinish(root, row, item, chosen, seed, runToken);

            RequireClass(root, NodeTaoPass1, "ClownsharKSampler_Beta");
            RequireClass(root, NodeTaoPass2, "ClownsharKSampler_Beta");
            RequireClass(root, NodeTaoRtx, "RTXVideoSuperResolution");

            var (sw, sh) = H3Canvas.Resolve(item.AspectRatio, item.Megapixels, 32);
            var fw = (int)Math.Round(sw * TaoMateUpscale);
            var fh = (int)Math.Round(sh * TaoMateUpscale);

            SetInput(root, NodeTaoPass1, "seed", seed);
            // The second leg needs a seed of its own — eta 0.5 keeps adding noise while it runs the rest of
            // the schedule out. Derived from the clip's rather than rolled, so a regenerate on the same seed
            // is the same clip, which is what "the prompt shown is the prompt that made the file" rests on.
            SetInput(root, NodeTaoPass2, "seed", SecondLegSeed(seed));

            // Written rather than assumed: the factor is what the finished size below is calculated from,
            // and a re-export of the authored graph could change it.
            SetInput(root, NodeTaoRtx, "resize_type", "scale by multiplier");
            SetInput(root, NodeTaoRtx, "resize_type.scale", TaoMateUpscale);

            // The RTX upscale is what feeds the mux — or RIFE, which reads it in turn.
            WireSink(root, item, NodeTaoRtx, NodeUpscaledAudio, runToken);

            var json = PruneToOutputs(root.ToJsonString(), new[] { NodeFinalSave }, out var pruned);
            AddLog($"{row.Title}: rendering the TaoMate relay (seed {seed}, {TaoMateSteps} steps, six on " +
                   $"the base weights then resampled on the TaoMate LoRA) at {sw}×{sh}, RTX ×{TaoMateUpscale:0.#} " +
                   $"to {fw}×{fh}, {(item.UseRife ? $"RIFE → {DraftFrameRate * 2}fps" : $"{DraftFrameRate}fps")}. " +
                   $"Finish graph: {pruned} node(s) removed.");
            return new FinishSubmission(json, NodeFinalSave, fw, fh);
        }

        /// <summary>The second leg's noise, folded out of the first's. Deterministic and non-negative, so
        /// the same clip seed always renders the same clip.</summary>
        private static long SecondLegSeed(long seed) => (seed ^ 0x2545F4914F6CDD1DL) & long.MaxValue;
    }
}
