using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ⚡ H3 Express's fourth stack: 🐰 <b>BUNNY</b> — <c>workflow/video/h3-minimax/h3-bunny.json</c>, built
    /// from the author's "MiniMax H3 12GB VRAM Universal FL2VA" render by <c>tools/build_h3_bunny.py</c>.
    ///
    /// <para><b>What it is: a sigma split, not a second seed.</b> One schedule is built once — eight steps
    /// of <c>simple</c>, with three extra steps woven in between sigma 0.65 and 0.28 where the motion is
    /// decided — and then cut at 75% by <c>SplitSigmasDenoise</c>. The first three quarters are sampled with
    /// the clip's own noise and the Combat LoRA at full strength: that pass is the choreography, and the
    /// author's tuning note is not to touch it, because a fixed seed's motion trajectory is what it
    /// produces. The last quarter is sampled by a second <c>SamplerCustomAdvanced</c> reading
    /// <c>DisableNoise</c> — it does not re-noise, it simply runs the schedule out — on the same Combat LoRA
    /// at 0.65, which is what takes the noise, ghosting and texture smear off the tail.</para>
    ///
    /// <para><b>One seed, and the upscale is automatic.</b> Both stages belong to one clip and one
    /// <c>RandomNoise</c>; nothing here asks a question. The authored graph stops at the sampler's own
    /// canvas, so this build grafts on the finish the other latent-upscale stacks use — the relayed latent
    /// lifted to the Quality canvas by <c>MinimaxH3LatentUpscaler3D</c> and re-sampled by the short
    /// fixed-sigma pass — which is why <see cref="H3ErosViewModel.BuildFinish"/> needs no override at all:
    /// the id the tab knows as "the sampler that made the take" <i>is</i> this stack's stage-2 sampler.</para>
    ///
    /// <para><b>What it does not change.</b> Everything a story is: the folder loop, the cast and the cards,
    /// the wardrobe, the sheets, the clip writer and the saved prompts, the @char tags and the reference
    /// panels, the per-clip prompt editor, the seed each clip is given, the join, and the optional LoRA —
    /// spliced into the same <c>Power Lora Loader</c> seat as on the other stacks, above the split, so it
    /// reaches both stages.</para>
    ///
    /// <para><b>What it is for.</b> The graph is built around <c>H3/H3_Combat_V2</c> and its author's prompt
    /// template is a fight beat sheet. It is the stack to pick for stories that are mostly physical action;
    /// on a quiet story the Combat LoRA is still on the wire at full strength through the pass that decides
    /// the motion.</para>
    /// </summary>
    public partial class H3ExpressViewModel
    {
        /// <summary>The author's BUNNY graph, flattened to API format and given the tab's finish by
        /// <c>tools/build_h3_bunny.py</c>. Its ids are <c>h3-eros.json</c>'s wherever
        /// <see cref="H3ErosViewModel.ApplyCommonInputs"/> or <see cref="H3ErosViewModel.BuildFinish"/>
        /// writes to one.</summary>
        private const string BunnyWorkflow = "workflow/video/h3-minimax/h3-bunny.json";

        /// <summary>
        /// The checkpoint this stack loads: the <b>fl2va/ref2va hybrid</b>, which is what the author's own
        /// note node prescribes for the all-reference mode this tab is always in ("全能参考版本默认使用该
        /// Hybrid 模型" — blocks 25–49 carry Ref2VA's conditional modulation weights over an FL2VA body).
        ///
        /// <para>The export itself is left on <c>minimax_h3_fastvideo_vsa_datafree_1300step_4step_int8_convrot</c>
        /// — the note says the FL2VA edition keeps whatever FL2VA model was last picked — and that is the
        /// first thing to try from the Model dropdown if the hybrid drifts. It is a first/last-frame build
        /// fed through <c>MiniMaxH3ReferenceToVideo</c> though, which is exactly the pairing
        /// <see cref="H3ErosViewModel.DiffusionModelSummary"/> warns about.</para>
        /// </summary>
        public const string BunnyModel = "h3-minimax/minimax_h3_hybrid_fl2va_ref2va_b25-49-int8.safetensors";

        /// <summary>The whole schedule, both stages — eight, as authored, before
        /// <c>ExtendIntermediateSigmas</c> weaves three more into the middle of it. Written into
        /// <c>22:8</c>, which the one <c>BasicScheduler</c> both stages are cut out of reads.</summary>
        private const int BunnySteps = 8;

        /// <summary>Where the schedule is cut: stage 2 runs the last quarter of it. Display only — the
        /// number that matters is <c>SplitSigmasDenoise</c>'s in the graph, and this is read off the same
        /// authored value.</summary>
        private const double BunnySplit = 0.25;

        private bool _useBunny;

        /// <summary>
        /// Reads the remembered stack and, if it is this one, moves the model dropdown onto its checkpoint.
        /// Written the way <see cref="InitTaoMate"/> is, and for the same reason: by the time this class
        /// exists the base constructors have already chosen a checkpoint and seeded the dropdown, so the
        /// move is only made when the dropdown is still sitting on a default.
        /// </summary>
        private void InitBunny()
        {
            _useBunny = _settingsService.Settings?.H3ExpressUseBunny ?? false;
            if (!_useBunny) return;

            // The stacks are one choice. Settings holding more than one — an older file, or a hand edit —
            // is resolved here rather than at every read, and the correction is written back so the file
            // stops holding two. 🍥 is initialised first and wins.
            if (UseTaoMate)
            {
                _useBunny = true;   // so ClearBunny does the clearing, including the save
                ClearBunny();
                return;
            }
            ClearSingularity();

            var stored = (RecallDiffusionModel(_settingsService.Settings) ?? string.Empty)
                .Trim().Replace('\\', '/');
            OfferShippedModel();
            if (stored.Length == 0 || stored == base.ShippedModel)
                SelectedDiffusionModel = BunnyModel;

            AddLog($"  🐰 BUNNY is ON: every clip is sampled on h3-bunny.json — one {BunnySteps}-step " +
                   $"schedule extended through the mid sigmas and split at {1 - BunnySplit:0%}, the Combat " +
                   "LoRA at 1.00 for the action and 0.65 for the cleanup, then the usual latent upscale.");
        }

        // ── The switch ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether every clip is sampled on the BUNNY stack instead of one of the other three.
        ///
        /// <para>Exclusive with them — see <see cref="Stack"/>, which is what the page's radio group binds.
        /// Persisted, and refused mid-run for the reason <see cref="H3BatchViewModel.CanChangeWorkflow"/>
        /// gives: the workflow file and the step count are read live as each clip's graph is built, so a
        /// folder switched halfway is a folder of films that do not match each other.</para>
        /// </summary>
        public bool UseBunny
        {
            get => _useBunny;
            set
            {
                if (_useBunny == value) return;
                if (value) { ClearTaoMate(); ClearSingularity(); }
                _useBunny = value;
                OnPropertyChanged();
                RaiseStackState();

                // The checkpoint is the stack, exactly as it is for ✴️ and 🍥: the dropdown moves with the
                // choice and says so, because the next thing the user does may be to move it back.
                OfferShippedModel();
                SelectedDiffusionModel = ShippedModel;

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.H3ExpressUseBunny = value;
                    _settingsService.SaveSettings(settings);
                }

                AddLog(value
                    ? $"{TabDisplayName}: 🐰 BUNNY ON — every clip is sampled on h3-bunny.json " +
                      $"({LabelFor(BunnyModel)}, res_multistep/simple at {BunnySteps} steps with three more " +
                      $"woven into the mid sigmas, split at {1 - BunnySplit:0%} so the last quarter runs out " +
                      "as a cleanup that does not re-noise, the Combat LoRA at 1.00 then 0.65). The draft " +
                      "canvas, the latent upscale, the cast, the clips and the join are unchanged."
                    : $"{TabDisplayName}: BUNNY off — every clip is sampled on the H3 Eros stack again.");

                if (value)
                    AddLog("  Note: H3/H3_Combat_V2 is on the wire at full strength through the pass that " +
                           "decides the motion. That is what this stack is — pick another for a story that " +
                           "is not mostly action.");
            }
        }

        /// <summary>Turns 🐰 off <b>quietly</b> — no log line, and the model dropdown left where it is — for
        /// another stack that is taking the render over and is about to say so itself. The counterpart of
        /// <see cref="H3BatchViewModel.ClearSingularity"/>.</summary>
        private void ClearBunny()
        {
            if (!_useBunny) return;
            _useBunny = false;
            OnPropertyChanged(nameof(UseBunny));

            var settings = _settingsService.Settings;
            if (settings != null)
            {
                settings.H3ExpressUseBunny = false;
                _settingsService.SaveSettings(settings);
            }
        }
    }
}
