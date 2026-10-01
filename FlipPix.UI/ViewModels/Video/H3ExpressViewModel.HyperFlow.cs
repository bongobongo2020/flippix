using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ⚡ H3 Express's sixth stack: 🌊 <b>HyperFlow</b> — <c>workflow/video/h3-minimax/h3-hyperflow.json</c>,
    /// built by <c>tools/build_h3_hyperflow.py</c> from the author's HyperFlow export with the action recipe
    /// laid over it.
    ///
    /// <para><b>What it is.</b> Video Rebirth's HyperFlow adapter for MiniMax H3 — an 8-step LoRA plus a
    /// two-time <c>(t, r)</c> conditioning only the <c>ApplyHyperFlowH3</c> node can apply — on the
    /// <b>Singularity</b> ref2va pruned checkpoint, in <c>bypass</c> mode with the curve refit on. The node
    /// hands back its trained nine-point sigma grid and the three drafts sample <i>that</i> with euler, so
    /// <see cref="FirstPassSteps"/> is written into <c>22:8</c> and read by nothing. <c>H3SLAAttention</c>
    /// at 0.90 sits last on the wire.</para>
    ///
    /// <para><b>The upscale pass is not HyperFlow.</b> It forks off the attention backend and samples on the
    /// TaoMate 3-step LoRA instead — "replace engine LoRA" — for exactly two steps. More steps softened it in
    /// the tool the recipe came from, so the pass carries its own schedule and <see cref="FinishSigmasNode"/>
    /// keeps the ⬆ steps dial away from it.</para>
    ///
    /// <para><b>Why the LoRA seat sits below the adapter.</b> The curve refit is bound to the exact
    /// checkpoint file and to an unmodified model, so a user LoRA above <c>ApplyHyperFlowH3</c> would switch
    /// it off without a word. The tab's LoRAs therefore reach the drafts only, not the TaoMate pass.</para>
    /// </summary>
    public partial class H3ExpressViewModel
    {
        private const string HyperFlowWorkflow = "workflow/video/h3-minimax/h3-hyperflow.json";

        /// <summary>The recipe's checkpoint: Singularity ref2va pruned v1.3, which has a bundled curve fit.</summary>
        public const string HyperFlowModel = "h3-minimax/Minimax-h3_Singularity_ref2va_Pruned_v1.3_int8.safetensors";

        /// <summary>The length of HyperFlow's trained grid. Display only — the grid is in the weights file.</summary>
        private const int HyperFlowSteps = 8;

        /// <summary>The adapter weights the graph loads, from <c>models/hyperflow/</c>. Named here for the log.</summary>
        private const string HyperFlowWeights = "custom_node_hyperflow_8step_v1.0_comfyui_pruned.safetensors";

        /// <summary>The upscale pass's own two-step schedule in h3-hyperflow.json.</summary>
        private const string NodeHyperFlowPass2Sigmas = "hf:p2sigmas";

        private bool _useHyperFlow;

        /// <summary>The recipe's 720p, set when 🌊 is picked: draft 0.2 MP (608×352), finish 0.9 MP (~1280×736).</summary>
        public const double HyperFlowDraftMegapixels = 0.2;

        /// <inheritdoc cref="HyperFlowDraftMegapixels"/>
        public const double HyperFlowMegapixels = 0.9;

        /// <summary>
        /// Reads the remembered stack and, if it is this one, moves the model dropdown onto its checkpoint.
        /// Initialised after the other four, so anything already holding the render keeps it and this clears
        /// itself — the same rule <see cref="InitParasyte"/> follows.
        /// </summary>
        private void InitHyperFlow()
        {
            _useHyperFlow = _settingsService.Settings?.H3ExpressUseHyperFlow ?? false;
            if (!_useHyperFlow) return;

            if (UseTaoMate || UseBunny || UseParasyte)
            {
                ClearHyperFlow();
                return;
            }
            ClearSingularity();

            var stored = (RecallDiffusionModel(_settingsService.Settings) ?? string.Empty)
                .Trim().Replace('\\', '/');
            OfferShippedModel();
            if (stored.Length == 0 || stored == base.ShippedModel)
                SelectedDiffusionModel = HyperFlowModel;

            // The canvas is not persisted, so a restart on 🌊 starts from the recipe's 720p again.
            PreviewMegapixels = HyperFlowDraftMegapixels;
            Megapixels = HyperFlowMegapixels;

            AddLog($"  🌊 HyperFlow is ON: every clip is sampled on h3-hyperflow.json — Singularity, euler on " +
                   $"the adapter's own {HyperFlowSteps}-step grid, SLA 0.90, then a two-step TaoMate upscale pass.");
        }

        // ── The switch ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether every clip is sampled on the HyperFlow stack instead of one of the other five. Exclusive
        /// with them — see <see cref="Stack"/> — persisted, and refused mid-run for the reason
        /// <see cref="H3BatchViewModel.CanChangeWorkflow"/> gives.
        /// </summary>
        public bool UseHyperFlow
        {
            get => _useHyperFlow;
            set
            {
                if (_useHyperFlow == value) return;
                if (value) { ClearTaoMate(); ClearBunny(); ClearParasyte(); ClearSingularity(); }
                _useHyperFlow = value;
                OnPropertyChanged();
                RaiseStackState();

                OfferShippedModel();
                SelectedDiffusionModel = ShippedModel;

                // The recipe's 720p: a 608×352 draft lifted to ~1280×736. Set once, on the switch — the user
                // can pick 1080p (0.6 draft → 2.0 MP) or anything else afterwards.
                if (value)
                {
                    PreviewMegapixels = HyperFlowDraftMegapixels;
                    Megapixels = HyperFlowMegapixels;
                }

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.H3ExpressUseHyperFlow = value;
                    _settingsService.SaveSettings(settings);
                }

                AddLog(value
                    ? $"{TabDisplayName}: 🌊 HyperFlow ON — every clip is sampled on h3-hyperflow.json " +
                      $"({LabelFor(HyperFlowModel)}, euler on the adapter's own {HyperFlowSteps}-step sigma grid, " +
                      "bypass, curve refit on, SLA 0.90), then upscaled by a two-step pass on the TaoMate 3-step " +
                      "LoRA in place of HyperFlow. The cast, the clips and the join are unchanged."
                    : $"{TabDisplayName}: HyperFlow off — every clip is sampled on the H3 Eros stack again.");

                if (value)
                    AddLog($"  Note: the steps and ⬆ upscale-steps dials do nothing on this stack. Leave the LoRA " +
                           $"stack empty — it only reaches the drafts. Needs the ComfyUI-HyperFlow-H3 pack and " +
                           $"{HyperFlowWeights} in models/hyperflow.");
            }
        }

        /// <summary>Turns 🌊 off <b>quietly</b> for another stack that is taking the render over and is about
        /// to say so itself. The counterpart of <see cref="ClearParasyte"/>.</summary>
        private void ClearHyperFlow()
        {
            if (!_useHyperFlow) return;
            _useHyperFlow = false;
            OnPropertyChanged(nameof(UseHyperFlow));

            var settings = _settingsService.Settings;
            if (settings != null)
            {
                settings.H3ExpressUseHyperFlow = false;
                _settingsService.SaveSettings(settings);
            }
        }

        /// <summary>🌊's upscale pass keeps its own two steps; every other stack takes the dial's schedule.</summary>
        protected override string FinishSigmasNode(H3CastQueueItem item) =>
            UseHyperFlow ? NodeHyperFlowPass2Sigmas : base.FinishSigmasNode(item);
    }
}
