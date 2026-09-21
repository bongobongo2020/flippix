using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Core.Models;
using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// 🌀 MiniMax I2V's <b>render card</b> — the dials ⚡ H3 Express has, on this tab's own graph: which
    /// sampling stack every pass runs, the checkpoint, a stack of up to five LoRAs, the step count, the
    /// finishing sigmas and RIFE.
    ///
    /// <para><b>A stack here is a patch, not a file.</b> On Express a stack <i>is</i> a workflow — the four
    /// graphs share <c>h3-eros.json</c>'s node ids, so switching one is a change of filename. That trick
    /// does not reach this tab: none of those files has a continuation loop, a four-reference
    /// <c>MiniMaxH3ReferenceToVideo</c>, or the draft/finish switches the canvas arithmetic is built on.
    /// Authoring four i2v editions of them would be four more graphs to keep in step with a fifth.</para>
    ///
    /// <para>So the stacks are ported as what actually distinguishes them. Held up side by side the four
    /// Express graphs differ in six things and nothing else — the checkpoint, whether a
    /// <c>MiniMaxH3SigmaShift</c> is on the wire and at what ratio, the sampler, the scheduler, the authored
    /// step count, and the LoRAs the stack is built around (plus, on 🍥 and 🐰, a second sampler fed by the
    /// first). Every one of those has a seat in <c>h3-minimax-i2v.json</c> already. MiniMaxI2VViewModel.Stacks.cs
    /// writes them; this file is the card that chooses them.</para>
    ///
    /// <para><b>What that buys and what it costs.</b> The references, the Ref2VA prompt, the continuation
    /// loop, the overlap blend, SLA, the canvas arithmetic and the prune are identical on all five stacks —
    /// which is the point, because those are what this tab is. What it does not give you is a byte-identical
    /// copy of an Express render: the turbo LoRA, the preview overrides and the loop's own switches are this
    /// graph's, not h3-eros.json's. A stack picked here samples <i>the way</i> that stack samples.</para>
    ///
    /// <para><b>Frozen mid-run.</b> Everything on the card is read as each item's graph is built and frozen
    /// onto the queue item at Add to Queue, so a queue drained after the card moved still renders what was
    /// queued. <see cref="CanChangeRender"/> locks the card itself while anything is on the GPU.</para>
    /// </summary>
    public partial class MiniMaxI2VViewModel
    {
        // ── Checkpoints ─────────────────────────────────────────────────────────────────────────────

        /// <summary>The folder the model dropdown lists, as ComfyUI names it under <c>diffusion_models/</c>.</summary>
        private const string ModelFolder = "h3-minimax/";

        /// <summary>Where the LoRA dropdowns look, relative to the loras root.</summary>
        private const string LoraFolder = "H3/";

        /// <summary>The checkpoint <c>h3-minimax-i2v.json</c> ships — the ref2va build the tab was written
        /// against, and the only one the turbo LoRA on its wire was distilled for.</summary>
        public const string ShippedI2VModel = "h3-minimax/minimax_h3_ref2va_pruned_int8_convrot.safetensors";

        /// <summary>🌹 h3-eros.json's checkpoint: the 10Eros TURBO hybrid.</summary>
        public const string ErosModel = "h3-minimax/10Eros_Max_h3_TURBO-hybrid_beta4_int8_convrot.safetensors";

        /// <summary>✴️ h3-singularity.json's checkpoint.</summary>
        public const string SingularityModel = "h3-minimax/Minimax-h3_Singularity_ref2va_Pruned_v1.3_int8.safetensors";

        /// <summary>🍥 h3-taomate.json's checkpoint: the fl2va base the relay hands off from.</summary>
        public const string TaoMateModel = "h3-minimax/minimax_h3_fl2va_pruned_int8_convrot.safetensors";

        /// <summary>🐰 h3-bunny.json's checkpoint: the fl2va/ref2va hybrid its author prescribes for the
        /// all-reference mode this tab is always in.</summary>
        public const string BunnyModel = "h3-minimax/minimax_h3_hybrid_fl2va_ref2va_b25-49-int8.safetensors";

        /// <summary>The checkpoint a stack is authored on. Picking a stack moves the dropdown here; moving
        /// the dropdown afterwards sticks, so a stack can be sampled on another checkpoint on purpose.</summary>
        public static string ShippedModelFor(I2VStack stack) => stack switch
        {
            I2VStack.Eros => ErosModel,
            I2VStack.Singularity => SingularityModel,
            I2VStack.TaoMate => TaoMateModel,
            I2VStack.Bunny => BunnyModel,
            _ => ShippedI2VModel,
        };

        // ── Steps ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>Below about four these checkpoints do not resolve a frame at all.</summary>
        public const int MinSteps = 4;

        /// <summary>Past the twenties a clip costs proportionally more for nothing visible on a turbo or
        /// 3-step-LoRA build sampled at cfg 1.</summary>
        public const int MaxSteps = 30;

        /// <summary>🍥's floor: its first leg spends <c>steps_to_run</c> of the schedule before the TaoMate
        /// LoRA resamples the rest, so six or fewer leaves that LoRA nothing to do.</summary>
        private const int TaoMateMinSteps = 7;

        /// <summary>What each stack is <i>authored</i> at. Not what the render uses — the slider stores a
        /// count per checkpoint over the top of this.</summary>
        public static int AuthoredStepsFor(I2VStack stack, bool erSde) => stack switch
        {
            I2VStack.Eros => 12,
            I2VStack.Singularity => erSde ? 12 : 10,
            I2VStack.TaoMate => 10,
            I2VStack.Bunny => 8,
            _ => 8,           // the graph's own BasicScheduler
        };

        // ── State ───────────────────────────────────────────────────────────────────────────────────

        private I2VStack _stack = I2VStack.Shipped;
        private bool _singularityErSde;
        private string _selectedDiffusionModel = string.Empty;
        private bool _isLoadingDiffusionModels;
        private bool _isLoadingLoras;
        private bool _rebuildingLoras;
        private int? _stepsOverride;
        private string _stepsModel = string.Empty;
        private int _upscaleSteps = 3;
        private bool _useRife;
        private bool _suspendLoraSave;

        /// <summary>The graph's five switched <c>LoraLoaderModelOnly</c> seats — the most rows the card offers.</summary>
        public const int MaxLoraSlots = 5;

        private static readonly DiffusionModelOption NoLora = new(string.Empty, "None");

        /// <summary>
        /// The fixed sigmas each finishing-pass length runs, keyed by the number of steps they describe.
        /// The strings are h3-eros.json's nodes 222/221/220 verbatim, which is where this tab's own
        /// <c>finish_sigmas</c> value came from.
        /// </summary>
        private static readonly Dictionary<int, string> FinishSigmas = new()
        {
            [3] = "0.9035, 0.6316, 0.3158, 0.0000",
            [4] = "0.9035, 0.8000, 0.6316, 0.3158, 0.0000",
            [5] = "0.9231, 0.8780, 0.8000, 0.6316, 0.3158, 0.0000",
        };

        /// <summary>
        /// Reads the card back out of settings and seeds both dropdowns so the tab is usable before — and
        /// if — the server answers. Enumerating either folder is a network round trip, so both happen off
        /// the constructor's thread: this tab is on the window's startup path, and a ctor that waits on
        /// ComfyUI is the slow-open bug this codebase keeps re-learning.
        /// </summary>
        private void InitRender()
        {
            var settings = _settingsService.Settings;

            _stack = Enum.TryParse<I2VStack>(settings?.MiniMaxI2VStack, ignoreCase: true, out var stored)
                ? stored
                : I2VStack.Shipped;
            _singularityErSde = settings?.MiniMaxI2VSingularityErSde ?? false;
            _upscaleSteps = FinishSigmas.ContainsKey(settings?.MiniMaxI2VUpscaleSteps ?? 3)
                ? settings!.MiniMaxI2VUpscaleSteps
                : 3;
            _useRife = settings?.MiniMaxI2VUseRife ?? false;

            // 🍥 paints at the Quality canvas and upscales the decoded frames, so there is no draft to halve
            // and nothing for the latent upscaler to lift. Applied here as well as in the setter: the stack
            // is restored from settings, and the flag it implies has to come back with it.
            if (_stack == I2VStack.TaoMate) _useLatentUpscale = false;

            _selectedDiffusionModel = NormalizeName(settings?.MiniMaxI2VDiffusionModel);
            if (_selectedDiffusionModel.Length == 0) _selectedDiffusionModel = ShippedModelFor(_stack);
            DiffusionModelOptions.Add(new DiffusionModelOption(ShippedModelFor(_stack),
                                                               LabelFor(ShippedModelFor(_stack)) + " (shipped)"));
            if (_selectedDiffusionModel != ShippedModelFor(_stack))
                DiffusionModelOptions.Add(new DiffusionModelOption(_selectedDiffusionModel, LabelFor(_selectedDiffusionModel)));

            _stepsModel = StepsKey(_selectedDiffusionModel);
            _stepsOverride = RecallSteps(_stepsModel);

            LoraOptions.Add(NoLora);
            _suspendLoraSave = true;
            try
            {
                foreach (var saved in (settings?.MiniMaxI2VLoras ?? new List<MiniMaxI2VLora>()).Take(MaxLoraSlots))
                {
                    var name = NormalizeName(saved?.Name);
                    if (name.Length == 0) continue;
                    if (LoraOptions.All(o => !string.Equals(o.Value, name, StringComparison.OrdinalIgnoreCase)))
                        LoraOptions.Add(new DiffusionModelOption(name, LabelFor(name)));
                    AttachLoraSlot(new MiniMaxI2VLoraSlot(Loras.Count + 1, name, saved!.Strength));
                }
            }
            finally { _suspendLoraSave = false; }

            RefreshDiffusionModelsCommand = new RelayCommand(() => _ = LoadDiffusionModelsAsync(),
                                                             () => !_isLoadingDiffusionModels);
            RefreshLorasCommand = new RelayCommand(() => _ = LoadLorasAsync(), () => !_isLoadingLoras);
            AddLoraCommand = new RelayCommand(AddLoraSlot, () => CanAddLora);
            RemoveLoraCommand = new RelayCommand<MiniMaxI2VLoraSlot>(RemoveLoraSlot);
            ResetStepsCommand = new RelayCommand(ResetSteps, () => HasStepsOverride && CanChangeRender);

            Loras.CollectionChanged += OnLorasCollectionChanged;

            _ = LoadDiffusionModelsAsync();
            _ = LoadLorasAsync();

            if (_stack != I2VStack.Shipped)
                AddLog($"Render: {StackName(_stack)} — {StackDetail(_stack)}");
        }

        // ── The stack ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Which way every pass samples. Setting it moves the model dropdown onto that stack's checkpoint,
        /// brings that checkpoint's own step count back, and persists — the same contract Express's radio
        /// group has, minus the reconciliation: one stored name cannot say two things at once.
        /// </summary>
        public I2VStack Stack
        {
            get => _stack;
            set
            {
                if (_stack == value || !CanChangeRender) return;
                _stack = value;
                OnPropertyChanged();
                RaiseStackState();

                // The checkpoint is the stack. The dropdown moves with the choice and says so, because the
                // next thing the user does may well be to move it back.
                OfferShippedModel();
                SelectedDiffusionModel = ShippedModelFor(value);

                // 🍥 has no draft canvas: it paints at the Quality size and upscales the decoded frames.
                if (value == I2VStack.TaoMate)
                {
                    UseLatentUpscale = false;
                    UseRtxUpscale = true;
                }

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.MiniMaxI2VStack = value.ToString();
                    _settingsService.SaveSettings(settings);
                }

                AddLog($"Render: {StackName(value)} — {StackDetail(value)}");
                if (value == I2VStack.Bunny)
                    AddLog("  Note: H3/H3_Combat_V2 is on the wire at full strength through the pass that " +
                           "decides the motion. That is what this stack is — pick another for a take that " +
                           "is not mostly action.");
            }
        }

        /// <summary>The five stacks as one radio group. A radio only ever asks to be turned <i>on</i>;
        /// <see cref="Stack"/>'s setter is what turns the others off.</summary>
        public bool StackIsShipped
        {
            get => _stack == I2VStack.Shipped;
            set { if (value) Stack = I2VStack.Shipped; }
        }

        /// <inheritdoc cref="StackIsShipped"/>
        public bool StackIsEros
        {
            get => _stack == I2VStack.Eros;
            set { if (value) Stack = I2VStack.Eros; }
        }

        /// <inheritdoc cref="StackIsShipped"/>
        public bool StackIsSingularity
        {
            get => _stack == I2VStack.Singularity;
            set { if (value) Stack = I2VStack.Singularity; }
        }

        /// <inheritdoc cref="StackIsShipped"/>
        public bool StackIsTaoMate
        {
            get => _stack == I2VStack.TaoMate;
            set { if (value) Stack = I2VStack.TaoMate; }
        }

        /// <inheritdoc cref="StackIsShipped"/>
        public bool StackIsBunny
        {
            get => _stack == I2VStack.Bunny;
            set { if (value) Stack = I2VStack.Bunny; }
        }

        /// <summary>✴️'s sub-option: keep the Singularity graph's checkpoint and its 12/3 shift, but sample
        /// it the H3 Eros way — er_sde/beta at 12 instead of euler/simple at 10.</summary>
        public bool SingularityErSde
        {
            get => _singularityErSde;
            set
            {
                if (_singularityErSde == value || !CanChangeRender) return;
                _singularityErSde = value;
                OnPropertyChanged();
                RaiseStackState();
                RaiseStepsState();

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.MiniMaxI2VSingularityErSde = value;
                    _settingsService.SaveSettings(settings);
                }
                AddLog(value
                    ? "✴️ Singularity: er_sde/beta at 12 steps — the Eros sampler over the Singularity graph. " +
                      "Untested on that checkpoint; euler/simple at 10 is the author's setting."
                    : "✴️ Singularity: euler/simple at 10 steps, as the graph is authored.");
            }
        }

        /// <summary>Whether the chosen stack composes at a draft canvas and lifts the latent to the finished
        /// one. True of every stack but 🍥 TaoMate — so the page hides the dials that would otherwise sit
        /// there doing nothing rather than leaving them to lie.</summary>
        public bool UsesDraftCanvas => _stack != I2VStack.TaoMate;

        /// <summary>Whether the card can be moved at all: not while anything is on the GPU. The stack, the
        /// checkpoint and the step count are read as each item's graph is built, so a queue switched
        /// halfway is a queue of takes that do not match each other.</summary>
        public bool CanChangeRender => !IsProcessing && !IsProcessingQueue;

        private static string StackName(I2VStack stack) => stack switch
        {
            I2VStack.Eros => "🌹 H3 Eros",
            I2VStack.Singularity => "✴️ Singularity",
            I2VStack.TaoMate => "🍥 TaoMate relay",
            I2VStack.Bunny => "🐰 BUNNY (action)",
            _ => "🌀 Shipped",
        };

        private string StackDetail(I2VStack stack) => stack switch
        {
            I2VStack.Eros =>
                $"the 10Eros TURBO hybrid sampled er_sde/beta at {AuthoredStepsFor(stack, false)}, with no sigma " +
                "shift and the lightx2v turbo LoRA off the wire — h3-eros.json's sampling on this tab's graph.",
            I2VStack.Singularity => _singularityErSde
                ? "the Singularity ref2va build and its 12/3 shift, sampled er_sde/beta at 12 — the Eros sampler " +
                  "over the Singularity graph."
                : "the Singularity ref2va build, euler/simple at 10 with the 12/3 shift, as its author measured it.",
            I2VStack.TaoMate =>
                "the fl2va weights and the TaoMate 3-step LoRA sharing one linear/euler beta57 schedule — six " +
                "steps on the base, the rest resampled on the LoRA at 0.65, with a 12/6 shift. No draft canvas: " +
                "every pass is painted at the Quality size and the frames are doubled by RTX Video Super " +
                "Resolution instead of the latent being lifted, so the file lands at twice Quality in each " +
                "direction and costs more per second.",
            I2VStack.Bunny =>
                "res_multistep/simple on the fl2va/ref2va hybrid: three extra steps woven between sigma 0.65 and " +
                "0.28 where the motion is decided, the schedule cut at 75%, the first three quarters sampled on " +
                "the Combat LoRA at 1.00 and the tail run out by a second sampler that does not re-noise, on the " +
                "same LoRA at 0.65.",
            _ =>
                "the graph as authored: the ref2va checkpoint on the lightx2v 4-step turbo LoRA, euler/simple at " +
                "8 with the 12/3 shift.",
        };

        /// <summary>The line under the radio group.</summary>
        public string StackSummary
        {
            get
            {
                var body = $"{StackName(_stack)} — {StackDetail(_stack)}";
                if (_stack == I2VStack.Shipped) return body;

                return body + " The references, the Ref2VA prompt, the continuation loop, the overlap blend and " +
                       "the canvas are unchanged — a stack changes how this graph samples, not what it renders.";
            }
        }

        private void RaiseStackState()
        {
            OnPropertyChanged(nameof(Stack));
            OnPropertyChanged(nameof(StackIsShipped));
            OnPropertyChanged(nameof(StackIsEros));
            OnPropertyChanged(nameof(StackIsSingularity));
            OnPropertyChanged(nameof(StackIsTaoMate));
            OnPropertyChanged(nameof(StackIsBunny));
            OnPropertyChanged(nameof(StackSummary));
            OnPropertyChanged(nameof(UsesDraftCanvas));
            OnPropertyChanged(nameof(RenderSummary));
            RaiseStepsState();
        }

        /// <summary>Raised from <see cref="OnCanExecuteChanged"/>: the whole card greys out while the GPU
        /// is busy, and comes back when it is not.</summary>
        private void RaiseRenderGate()
        {
            OnPropertyChanged(nameof(CanChangeRender));
            OnPropertyChanged(nameof(CanAddLora));
            AddLoraCommand?.NotifyCanExecuteChanged();
            ResetStepsCommand?.NotifyCanExecuteChanged();
        }

        // ── The checkpoint ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The checkpoint every pass loads, as ComfyUI names it, so it drops straight into
        /// <c>DiffusionModelLoaderKJ.model_name</c>. Persisted the moment it changes, and frozen onto every
        /// queue item at Add to Queue.
        /// </summary>
        public string SelectedDiffusionModel
        {
            get => _selectedDiffusionModel;
            set
            {
                var name = NormalizeName(value);
                if (name.Length == 0 || _selectedDiffusionModel == name) return;
                _selectedDiffusionModel = name;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DiffusionModelSummary));
                OnPropertyChanged(nameof(RenderSummary));
                OnStepsModelChanged();

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.MiniMaxI2VDiffusionModel = name;
                    _settingsService.SaveSettings(settings);
                }
                AddLog($"Model: {LabelFor(name)} — every pass rendered from now on samples with this.");
            }
        }

        /// <summary>Every model the connected ComfyUI reports under <see cref="ModelFolder"/>, plus the one
        /// the chosen stack ships and whatever was picked last run, so the list is never empty.</summary>
        public ObservableCollection<DiffusionModelOption> DiffusionModelOptions { get; } = new();

        public RelayCommand RefreshDiffusionModelsCommand { get; private set; } = new(() => { });

        /// <summary>The line under the dropdown: what is loaded, and the one caveat that bites.</summary>
        public string DiffusionModelSummary =>
            _isLoadingDiffusionModels
                ? "Reading diffusion_models/h3-minimax from ComfyUI…"
                : _selectedDiffusionModel.Contains("ref2v", StringComparison.OrdinalIgnoreCase) ||
                  _selectedDiffusionModel.Contains("hybrid", StringComparison.OrdinalIgnoreCase)
                    ? $"{DiffusionModelOptions.Count} model(s) in diffusion_models/h3-minimax."
                    : "This graph feeds MiniMaxH3ReferenceToVideo — a model trained only for first/last-frame " +
                      "work may render noise through it. Render one short pass before queueing a long take.";

        /// <summary>Puts the current stack's own checkpoint in the list, for the setter that is about to
        /// select it. Only needed when the server has not answered (or has never had that file).</summary>
        private void OfferShippedModel()
        {
            var name = ShippedModelFor(_stack);
            if (DiffusionModelOptions.Any(o => string.Equals(o.Value, name, StringComparison.OrdinalIgnoreCase)))
                return;
            DiffusionModelOptions.Add(new DiffusionModelOption(name, LabelFor(name) + " (shipped)"));
        }

        /// <summary>
        /// Fills <see cref="DiffusionModelOptions"/> from /object_info/UNETLoader, keeping only what lives in
        /// <see cref="ModelFolder"/>. Asking the server rather than reading the disk is what makes this work
        /// against a remote ComfyUI, where the models folder is not a path this machine has. A server that
        /// cannot be reached leaves the list exactly as <see cref="InitRender"/> seeded it.
        /// </summary>
        private async Task LoadDiffusionModelsAsync()
        {
            if (_isLoadingDiffusionModels) return;
            _isLoadingDiffusionModels = true;
            OnUiThread(() =>
            {
                RefreshDiffusionModelsCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(DiffusionModelSummary));
            });
            try
            {
                var all = await _comfyUIService.HttpClient.GetUnetFilenamesAsync();
                var found = all
                    .Select(NormalizeName)
                    .Where(n => n.StartsWith(ModelFolder, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (found.Count == 0)
                {
                    AddLog("Model list: ComfyUI reported nothing under diffusion_models/h3-minimax — " +
                           "keeping the model the workflow ships.");
                    return;
                }

                var keep = _selectedDiffusionModel;
                OnUiThread(() =>
                {
                    DiffusionModelOptions.Clear();
                    var shipped = ShippedModelFor(_stack);
                    foreach (var n in found)
                        DiffusionModelOptions.Add(new DiffusionModelOption(
                            n, LabelFor(n) + (string.Equals(n, shipped, StringComparison.OrdinalIgnoreCase)
                                                  ? " (shipped)" : string.Empty)));

                    // The server's own spelling wins where it differs only in case, so what is written into
                    // model_name is byte-for-byte what ComfyUI offered.
                    var match = found.FirstOrDefault(n => string.Equals(n, keep, StringComparison.OrdinalIgnoreCase));
                    if (match == null && keep.Length > 0)
                        DiffusionModelOptions.Add(new DiffusionModelOption(keep, LabelFor(keep) + " (not on server)"));

                    // Written through the field, not the property: Clear() has already pushed null back
                    // through the ComboBox's two-way SelectedValue binding, so the notification below is what
                    // puts the selection back on screen — and the setter, seeing no change, would never raise
                    // it. Going round the setter also skips a settings write on every startup.
                    _selectedDiffusionModel = match ?? keep;
                    OnPropertyChanged(nameof(SelectedDiffusionModel));
                    OnPropertyChanged(nameof(DiffusionModelSummary));
                    OnStepsModelChanged();
                });
                AddLog($"Model list: {found.Count} model(s) in diffusion_models/h3-minimax.");
            }
            catch (Exception ex)
            {
                AddLog($"Model list could not be read ({ex.Message}) — keeping {LabelFor(_selectedDiffusionModel)}.");
            }
            finally
            {
                _isLoadingDiffusionModels = false;
                OnUiThread(() =>
                {
                    RefreshDiffusionModelsCommand.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(DiffusionModelSummary));
                });
            }
        }

        // ── The LoRA stack ──────────────────────────────────────────────────────────────────────────

        /// <summary>Up to five LoRAs, applied to the checkpoint in list order. Express offers one; this
        /// graph ships five switched seats, so all five are on the card.</summary>
        public ObservableCollection<MiniMaxI2VLoraSlot> Loras { get; } = new();

        /// <summary>None, then every LoRA the server reports under loras/H3. Shared by every row.</summary>
        public ObservableCollection<DiffusionModelOption> LoraOptions { get; } = new();

        public RelayCommand RefreshLorasCommand { get; private set; } = new(() => { });

        public RelayCommand AddLoraCommand { get; private set; } = new(() => { });

        public RelayCommand<MiniMaxI2VLoraSlot> RemoveLoraCommand { get; private set; } = new(_ => { });

        public bool CanAddLora => Loras.Count < MaxLoraSlots && CanChangeRender;

        /// <summary>The line under the list: what is on the wire, on top of what.</summary>
        public string LoraSummary
        {
            get
            {
                if (_isLoadingLoras) return "Reading loras/H3 from ComfyUI…";

                var active = Loras.Where(l => l.IsActive).ToList();
                var stackOwn = StackLoras(_stack).ToList();
                var theirs = stackOwn.Count == 0
                    ? string.Empty
                    : $" The stack loads {string.Join(" and ", stackOwn.Select(l => $"{LabelFor(l.Name)} at {l.Strength:0.00}"))} " +
                      "of its own, ahead of these.";

                if (active.Count == 0)
                    return $"No LoRA of yours. {Math.Max(0, LoraOptions.Count - 1)} available in loras/H3.{theirs}";

                var named = string.Join(", ", active.Select(l => $"{LabelFor(l.Name)} at {l.Strength:0.00}"));
                var dropped = Loras.Count - active.Count;
                var skipped = dropped == 0 ? string.Empty
                    : $" {dropped} row(s) at 0 or unset are left out of the graph.";
                return $"{named} — stacked on the checkpoint in that order.{skipped}{theirs}";
            }
        }

        private void AddLoraSlot()
        {
            if (!CanAddLora) return;
            AttachLoraSlot(new MiniMaxI2VLoraSlot(Loras.Count + 1));
        }

        private void AttachLoraSlot(MiniMaxI2VLoraSlot slot)
        {
            slot.Changed += OnLoraSlotChanged;
            Loras.Add(slot);
        }

        private void RemoveLoraSlot(MiniMaxI2VLoraSlot? slot)
        {
            if (slot == null || !CanChangeRender) return;
            slot.Changed -= OnLoraSlotChanged;
            Loras.Remove(slot);
            for (var i = 0; i < Loras.Count; i++) Loras[i].Index = i + 1;
        }

        private void OnLoraSlotChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(LoraSummary));
            OnPropertyChanged(nameof(RenderSummary));
            SaveLoras();
        }

        private void OnLorasCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(CanAddLora));
            OnPropertyChanged(nameof(HasLoras));
            OnPropertyChanged(nameof(LoraSummary));
            OnPropertyChanged(nameof(RenderSummary));
            AddLoraCommand?.NotifyCanExecuteChanged();
            SaveLoras();
        }

        public bool HasLoras => Loras.Count > 0;

        private void SaveLoras()
        {
            // A rebuild's Clear() pushes null back through every row's two-way binding before the new list
            // lands; persisting in the middle of that would write the blanks out.
            if (_suspendLoraSave || _rebuildingLoras) return;

            var settings = _settingsService.Settings;
            if (settings == null) return;
            settings.MiniMaxI2VLoras = Loras
                .Where(l => l.HasLora)
                .Select(l => new MiniMaxI2VLora { Name = l.Name, Strength = l.Strength })
                .ToList();
            _settingsService.SaveSettings(settings);
        }

        /// <summary>
        /// Fills <see cref="LoraOptions"/> from /object_info/LoraLoader, keeping only what lives in
        /// <see cref="LoraFolder"/>. A LoRA a row is already set to that the server no longer has stays in
        /// the list, labelled, rather than silently dropping that row to none.
        /// </summary>
        private async Task LoadLorasAsync()
        {
            if (_isLoadingLoras) return;
            _isLoadingLoras = true;
            OnUiThread(() =>
            {
                RefreshLorasCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(LoraSummary));
            });
            try
            {
                var all = await _comfyUIService.HttpClient.GetLoraFilenamesAsync();
                var found = all
                    .Select(NormalizeName)
                    .Where(n => n.StartsWith(LoraFolder, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var chosen = Loras.Where(l => l.HasLora).Select(l => l.Name).ToList();
                OnUiThread(() =>
                {
                    _rebuildingLoras = true;
                    try
                    {
                        // Each row's SelectedValue is re-resolved against the rebuilt list, so the name is
                        // captured before the Clear() and written back after it.
                        var held = Loras.Select(l => l.Name).ToList();

                        LoraOptions.Clear();
                        LoraOptions.Add(NoLora);
                        foreach (var n in found) LoraOptions.Add(new DiffusionModelOption(n, LabelFor(n)));
                        foreach (var name in chosen.Where(c =>
                                     !found.Any(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase))))
                            LoraOptions.Add(new DiffusionModelOption(name, LabelFor(name) + " (not on server)"));

                        for (var i = 0; i < Loras.Count; i++)
                        {
                            // The server's own spelling wins where it differs only in case.
                            var match = found.FirstOrDefault(n =>
                                string.Equals(n, held[i], StringComparison.OrdinalIgnoreCase));
                            Loras[i].Name = match ?? held[i];
                        }
                    }
                    finally { _rebuildingLoras = false; }

                    OnPropertyChanged(nameof(LoraSummary));
                });
                AddLog($"LoRA list: {found.Count} in loras/H3.");
            }
            catch (Exception ex)
            {
                AddLog($"LoRA list could not be read ({ex.Message}).");
            }
            finally
            {
                _isLoadingLoras = false;
                OnUiThread(() =>
                {
                    RefreshLorasCommand.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(LoraSummary));
                });
            }
        }

        /// <summary>The LoRAs a stack is built around, loaded ahead of the user's own. These are the stack,
        /// not a preference — 🐰 without the Combat LoRA is not BUNNY.</summary>
        private static IEnumerable<MiniMaxI2VLora> StackLoras(I2VStack stack)
        {
            switch (stack)
            {
                case I2VStack.TaoMate:
                    yield return new MiniMaxI2VLora { Name = "H3/taomate_h3_3step_comfy.safetensors", Strength = 0.65 };
                    break;
                case I2VStack.Bunny:
                    yield return new MiniMaxI2VLora { Name = "H3/H3_Combat_V2.safetensors", Strength = 1.0 };
                    break;
            }
        }

        // ── Sampling steps ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Steps on the pass that paints the clip, as the slider reads and writes it: this checkpoint's
        /// stored count, or its stack's authored one while it has none. Setting it stores the count against
        /// the current model and saves at once; ↺ is the way back out.
        /// </summary>
        public int FirstPassStepCount
        {
            get => ClampSteps(_stepsOverride ?? AuthoredSteps);
            set
            {
                if (!CanChangeRender) return;
                var clamped = ClampSteps(value);

                // Landing on the authored count clears the entry rather than writing one: "nothing stored"
                // already means exactly that. It also makes the one write a Slider can make on its own —
                // coercing a value against bounds it has not been given yet — a no-op.
                if (clamped == AuthoredSteps)
                {
                    if (_stepsOverride != null) ResetSteps();
                    return;
                }

                if (_stepsOverride == clamped) return;
                _stepsOverride = clamped;
                StoreSteps(_stepsModel, clamped);
                RaiseStepsState();
                AddLog($"Steps: {clamped} on {LabelFor(_selectedDiffusionModel)} (authored: {AuthoredSteps}) — " +
                       "every pass rendered from now on, and remembered for that checkpoint alone.");
            }
        }

        /// <summary>What the chosen stack is authored at, for the checkpoint currently loaded.</summary>
        public int AuthoredSteps => AuthoredStepsFor(_stack, _singularityErSde);

        /// <summary>The slider's floor: seven on 🍥, which spends six of the schedule before the LoRA leg
        /// starts, and <see cref="MinSteps"/> everywhere else.</summary>
        public int MinStepsForStack => _stack == I2VStack.TaoMate ? TaoMateMinSteps : MinSteps;

        /// <summary>Whether this checkpoint renders at a count chosen here rather than its stack's. What ↺
        /// is enabled by.</summary>
        public bool HasStepsOverride => _stepsOverride.HasValue && _stepsOverride.Value != AuthoredSteps;

        public RelayCommand ResetStepsCommand { get; private set; } = new(() => { });

        private void ResetSteps()
        {
            if (_stepsOverride == null) return;
            _stepsOverride = null;
            ForgetSteps(_stepsModel);
            RaiseStepsState();
            AddLog($"Steps: back to {FirstPassStepCount} on {LabelFor(_selectedDiffusionModel)} — the count its " +
                   "stack is authored at.");
        }

        /// <summary>The line under the slider: what the count is, where it came from, and what moving it costs.</summary>
        public string StepsSummary
        {
            get
            {
                var authored = AuthoredSteps;
                var steps = FirstPassStepCount;
                var scheme = _stack switch
                {
                    I2VStack.TaoMate =>
                        $"the relay's whole schedule — {TaoMateMinSteps - 1} steps on the fl2va weights, the rest " +
                        "resampled on the TaoMate LoRA",
                    I2VStack.Bunny =>
                        "the schedule BUNNY extends and splits — three more steps are woven into the mid sigmas " +
                        "and the last 25% of what comes out is the cleanup pass",
                    I2VStack.Singularity => _singularityErSde
                        ? "er_sde/beta over the Singularity graph"
                        : "euler/simple, as the Singularity graph is authored",
                    I2VStack.Eros => "er_sde/beta on the 10Eros hybrid",
                    _ => "euler/simple on the turbo LoRA, as this graph is authored",
                };

                var draft = UsesDraftCanvas && UseLatentUpscale
                    ? " With the latent upscale on, the first half of this schedule is the draft and the finish " +
                      "runs its own fixed sigmas."
                    : string.Empty;

                var where = steps == authored
                    ? $"the authored count for this stack ({scheme})."
                    : $"your count for this checkpoint — authored is {authored} ({scheme}). " +
                      (steps > authored
                          ? "More steps cost proportionally more per pass; on a turbo build they mostly firm up motion."
                          : "Fewer is quicker and tends to come out softer.");

                return $"{steps} steps — {where}{draft} Saved against {LabelFor(_selectedDiffusionModel)} alone, " +
                       "so each checkpoint keeps its own.";
            }
        }

        /// <summary>One spelling for a model name, so a count written on one run is found by the next
        /// whatever the server's casing or slashes were.</summary>
        private static string StepsKey(string? model) =>
            (model ?? string.Empty).Trim().Replace('\\', '/').ToLowerInvariant();

        private int? RecallSteps(string key)
        {
            if (key.Length == 0) return null;
            var map = _settingsService.Settings?.MiniMaxI2VStepsByModel;
            return map != null && map.TryGetValue(key, out var steps) && steps > 0 ? ClampSteps(steps) : null;
        }

        private void StoreSteps(string key, int steps)
        {
            var settings = _settingsService.Settings;
            if (settings == null || key.Length == 0) return;

            var map = settings.MiniMaxI2VStepsByModel ??= new Dictionary<string, int>();
            map[key] = steps;
            _settingsService.SaveSettings(settings);
        }

        private void ForgetSteps(string key)
        {
            var settings = _settingsService.Settings;
            var map = settings?.MiniMaxI2VStepsByModel;
            if (settings == null || map == null || key.Length == 0) return;
            if (map.Remove(key)) _settingsService.SaveSettings(settings);
        }

        /// <summary>Called when the model dropdown moves — including when a stack change moves it, and when
        /// the server's list is rebuilt under it — to bring that checkpoint's own count back.</summary>
        private void OnStepsModelChanged()
        {
            var key = StepsKey(_selectedDiffusionModel);
            if (key == _stepsModel)
            {
                // The same checkpoint, but the stack around it may have changed what "authored" means.
                RaiseStepsState();
                return;
            }

            _stepsModel = key;
            var had = _stepsOverride;
            _stepsOverride = RecallSteps(key);
            RaiseStepsState();

            if (_stepsOverride.HasValue)
                AddLog($"Steps: {FirstPassStepCount} — the count saved for {LabelFor(_selectedDiffusionModel)} " +
                       $"(authored: {AuthoredSteps}).");
            else if (had.HasValue)
                AddLog($"Steps: {FirstPassStepCount} — {LabelFor(_selectedDiffusionModel)} has no count of its " +
                       "own, so its stack's authored one stands.");
        }

        private int ClampSteps(int steps) => Math.Clamp(steps, MinStepsForStack, MaxSteps);

        private void RaiseStepsState()
        {
            OnPropertyChanged(nameof(FirstPassStepCount));
            OnPropertyChanged(nameof(AuthoredSteps));
            OnPropertyChanged(nameof(MinStepsForStack));
            OnPropertyChanged(nameof(HasStepsOverride));
            OnPropertyChanged(nameof(StepsSummary));
            OnPropertyChanged(nameof(RenderSummary));
            ResetStepsCommand?.NotifyCanExecuteChanged();
        }

        // ── The finish ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Fixed sigmas on the pass that finishes the upscaled latent: 3, 4 or 5. Three is what
        /// this graph ships; the strings are h3-eros.json's own.</summary>
        public int UpscaleSteps
        {
            get => _upscaleSteps;
            set
            {
                var clamped = FinishSigmas.ContainsKey(value) ? value : 3;
                if (_upscaleSteps == clamped) return;
                _upscaleSteps = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RenderSummary));

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.MiniMaxI2VUpscaleSteps = clamped;
                    _settingsService.SaveSettings(settings);
                }
            }
        }

        public IReadOnlyList<int> UpscaleStepOptions { get; } = new[] { 3, 4, 5 };

        /// <summary>
        /// RIFE on the finished frames, 24 → 48 fps. Off by default and a genuine addition rather than a
        /// setting: unlike the Express graphs, <c>h3-minimax-i2v.json</c> has no <c>RIFEInterpolation</c>
        /// node — the submit path grafts one in front of the chosen sink when this is on.
        /// </summary>
        public bool UseRife
        {
            get => _useRife;
            set
            {
                if (_useRife == value) return;
                _useRife = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RenderSummary));

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.MiniMaxI2VUseRife = value;
                    _settingsService.SaveSettings(settings);
                }
            }
        }

        /// <summary>One line naming the whole card, for the panel that has no room for five.</summary>
        public string RenderSummary
        {
            get
            {
                var loras = Loras.Count(l => l.IsActive) + StackLoras(_stack).Count();
                var lora = loras == 0 ? string.Empty : $" · {loras} LoRA{(loras == 1 ? string.Empty : "s")}";
                var finish = UsesDraftCanvas
                    ? (UseLatentUpscale ? $" · latent ×2, {UpscaleSteps}-step finish" : " · one pass, no upscale")
                    : " · RTX ×2 frames";
                var rife = UseRife ? " · RIFE 48 fps" : string.Empty;
                return $"{StackName(_stack)} · {LabelFor(_selectedDiffusionModel)} · {FirstPassStepCount} steps" +
                       $"{lora}{finish}{rife}";
            }
        }

        // ── Shared helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>Slashes one way and no stray whitespace, so a name from settings, from the server and
        /// from the workflow file all compare equal.</summary>
        private static string NormalizeName(string? name) =>
            (name ?? string.Empty).Trim().Replace('\\', '/');

        /// <summary>What a dropdown shows: the filename, without the folder or the extension.</summary>
        private static string LabelFor(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "(workflow default)";
            var file = name.Replace('\\', '/');
            var slash = file.LastIndexOf('/');
            if (slash >= 0) file = file[(slash + 1)..];
            var dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }
    }
}
