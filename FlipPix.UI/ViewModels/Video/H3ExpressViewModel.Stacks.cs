using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ⚡ H3 Express's <b>sampling stack</b> — which of the four graphs every clip is rendered on, and the
    /// three things that choice changes.
    ///
    /// <para><b>Four graphs, one contract.</b> Every one of them is written on <c>h3-eros.json</c>'s node
    /// ids wherever the shared render path writes: <c>22:11</c> the prompt, <c>22:23</c>/<c>22:24</c> the
    /// length, <c>22:8</c> the steps, <c>22:9</c> the draft canvas, <c>5</c> the reference node,
    /// <c>171:4</c> the UNet, <c>21</c> the Power Lora Loader seat, <c>34</c> the sink. So a stack is a
    /// change of <see cref="H3ErosViewModel.WorkflowFileName"/>, <see cref="H3ErosViewModel.ShippedModel"/>
    /// and <see cref="AuthoredFirstPassSteps"/> and nothing else — one render path, four stacks:</para>
    /// <list type="bullet">
    /// <item>🌹 <b>H3 Eros</b> — <c>h3-eros.json</c>, the 10Eros hybrid at er_sde/beta.</item>
    /// <item>✴️ <b>Singularity</b> — <c>h3-singularity.json</c>; see <see cref="H3BatchViewModel"/>.</item>
    /// <item>🍥 <b>TaoMate</b> — <c>h3-taomate.json</c>, the two-model relay with an RTX frame-space
    /// finish; the one stack with no draft canvas. See H3ExpressViewModel.TaoMate.cs.</item>
    /// <item>🐰 <b>BUNNY</b> — <c>h3-bunny.json</c>, the sigma split on the Combat LoRA. See
    /// H3ExpressViewModel.Bunny.cs.</item>
    /// </list>
    ///
    /// <para><b>Why the radio group binds computed properties.</b> The choice is stored as one flag per
    /// stack — each one persisted in its own settings slot, each one logging and moving the model dropdown
    /// when it changes — so "which stack" is a derived question with exactly one answer, asked here rather
    /// than reconciled at every read. A radio only ever asks to be turned <i>on</i>, and
    /// <see cref="Stack"/>'s setter is what turns the others off.</para>
    /// </summary>
    public partial class H3ExpressViewModel
    {
        /// <summary>
        /// Which stack every clip is rendered on. Setting it routes through the individual switches, so each
        /// still logs what it did, persists itself and moves the model dropdown onto the checkpoint that
        /// ends up chosen.
        /// </summary>
        public ExpressStack Stack
        {
            get => UseTaoMate ? ExpressStack.TaoMate
                 : UseBunny ? ExpressStack.Bunny
                 : UseSingularity ? ExpressStack.Singularity
                 : ExpressStack.Eros;
            set
            {
                if (Stack == value) return;

                // Whichever stack is being turned ON goes first: its own setter clears the others quietly,
                // so the dropdown is moved once, by the winner, rather than parked on the Eros checkpoint
                // for the one statement it takes the new stack to move it again.
                switch (value)
                {
                    case ExpressStack.TaoMate:
                        UseTaoMate = true;
                        break;
                    case ExpressStack.Bunny:
                        UseBunny = true;
                        break;
                    case ExpressStack.Singularity:
                        UseTaoMate = false;
                        UseBunny = false;
                        UseSingularity = true;
                        break;
                    default:
                        UseTaoMate = false;
                        UseBunny = false;
                        UseSingularity = false;
                        break;
                }
                RaiseStackState();
            }
        }

        /// <summary>The four stacks as one radio group. The getters are computed off <see cref="Stack"/>, so
        /// there is no fifth state to keep in step; a radio asking to be turned off is ignored, because the
        /// one that was turned on has already said so.</summary>
        public bool StackIsEros
        {
            get => Stack == ExpressStack.Eros;
            set { if (value) Stack = ExpressStack.Eros; }
        }

        /// <inheritdoc cref="StackIsEros"/>
        public bool StackIsSingularity
        {
            get => Stack == ExpressStack.Singularity;
            set { if (value) Stack = ExpressStack.Singularity; }
        }

        /// <inheritdoc cref="StackIsEros"/>
        public bool StackIsTaoMate
        {
            get => Stack == ExpressStack.TaoMate;
            set { if (value) Stack = ExpressStack.TaoMate; }
        }

        /// <inheritdoc cref="StackIsEros"/>
        public bool StackIsBunny
        {
            get => Stack == ExpressStack.Bunny;
            set { if (value) Stack = ExpressStack.Bunny; }
        }

        private void RaiseStackState()
        {
            OnPropertyChanged(nameof(Stack));
            OnPropertyChanged(nameof(StackIsEros));
            OnPropertyChanged(nameof(StackIsSingularity));
            OnPropertyChanged(nameof(StackIsTaoMate));
            OnPropertyChanged(nameof(StackIsBunny));
            OnPropertyChanged(nameof(StackSummary));
            OnPropertyChanged(nameof(HuntSummary));
            OnPropertyChanged(nameof(UsesDraftCanvas));
            // The frame stack is four times the pixels on 🍥, so the warning changes with the stack.
            OnPropertyChanged(nameof(LoadSummary));
            OnPropertyChanged(nameof(HasLoadWarning));
        }

        /// <summary>Whether the chosen stack composes at a draft canvas and lifts the latent to the finished
        /// one. True of every stack but 🍥 TaoMate, which paints at the quality canvas and upscales the
        /// decoded frames — so the page hides the two dials (the composition canvas and the finishing sigmas)
        /// that would otherwise sit there doing nothing.</summary>
        public bool UsesDraftCanvas => !UseTaoMate;

        // ── Identity: what the choice actually changes ───────────────────────────────────────────────

        protected override string WorkflowFileName => Stack switch
        {
            ExpressStack.TaoMate => TaoMateWorkflow,
            ExpressStack.Bunny => BunnyWorkflow,
            _ => base.WorkflowFileName          // Singularity's own file, or Eros's, from H3BatchViewModel
        };

        protected override string ShippedModel => Stack switch
        {
            ExpressStack.TaoMate => TaoMateModel,
            ExpressStack.Bunny => BunnyModel,
            _ => base.ShippedModel
        };

        /// <summary>
        /// The step count each stack is <i>authored</i> at: the base's twelve is the Eros hybrid's, ✴️'s ten
        /// is the Singularity checkpoint's, 🍥's ten is the length of the schedule its relay splits, and 🐰's
        /// eight is what the BUNNY scheduler builds before three more are woven into the mid sigmas.
        ///
        /// <para>Not what the render uses: the steps slider stores a count per checkpoint over the top of
        /// this, and <see cref="FirstPassSteps"/> — overridden in H3ExpressViewModel.Steps.cs — is what the
        /// graph is actually written with.</para>
        /// </summary>
        protected int AuthoredFirstPassSteps => Stack switch
        {
            ExpressStack.TaoMate => TaoMateSteps,
            ExpressStack.Bunny => BunnySteps,
            _ => base.FirstPassSteps
        };
    }
}
