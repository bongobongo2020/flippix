using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Core.Models;
using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// The PRESET dropdown's <b>🌹 ErosMax Turbo 2-Pass (preview → refine)</b> entry: clips render through 🌀 MiniMax
    /// I2V's 2-Pass graph (h3-erosmax-2pass.json — "H3 Turbo 2-Pass ErosMax b7, RefMods v3"), in two stages the
    /// user steers between.
    ///
    /// <para><b>1 · Previews.</b> Generate (and Story Mode's Analyze) renders every beat's <i>first pass only</i> —
    /// the 10Eros b7 Turbo checkpoint at the small first-pass canvas — and shows each on the timeline as it lands.
    /// A beat that is not right is edited in the clip panel on the right and re-previewed on its own: ↻ New take
    /// rolls a new seed, ↻ Re-preview edit keeps the seed so only the edit changes the take.</para>
    ///
    /// <para><b>2 · Refinement.</b> Nothing heavier runs until "Happy — proceed with final refinement". Each beat is
    /// then rendered again in full — the same prompt, the same seed and the same guide, so the first pass is the
    /// approved one, then the learned latent upscale, the refining pass, RTX and the encode. A story run waits at
    /// that button, then joins the refined beats.</para>
    ///
    /// <para><b>Chained beats</b> (Chain clips on) use the graph's own Continue mode: the previous beat's
    /// <i>preview</i> guides the first frames of this one, and only the new segment is exported (the overlap cut
    /// off picture and sound), so each beat stays one clip. The final refinement is guided by that same preview,
    /// which is what makes it reproduce the approved first pass. Re-previewing a beat marks the chained beats after
    /// it RE-JOIN until they are previewed again.</para>
    ///
    /// <para>The card is the I2V tab's (Controls/TwoPassModelCard, TwoPassTuningCards), on its own copy of the
    /// options (<see cref="H3VideoEditorSettings.TwoPass"/>). The canvas aspect is the editor's header, lengths
    /// come from the timeline, and FrameGen stays off so every beat joins at 24 fps.</para>
    /// </summary>
    public partial class H3VideoEditorViewModel
    {
        /// <summary>The 2-Pass card. The same object settings persist, so an edit is saved as it is made.</summary>
        public MiniMaxI2VTwoPassOptions TwoPass { get; private set; } = new();

        /// <summary>Whether the picked preset is the 2-Pass one.</summary>
        public bool IsTwoPass => SelectedPreset?.IsTwoPass == true;

        public ObservableCollection<MiniMaxI2VLoraSlot> TwoPassLoras { get; } = new();
        public ObservableCollection<MiniMaxI2VRefModSlot> TwoPassRefMods { get; } = new();
        public ObservableCollection<string> RefModOptions { get; } = new() { MiniMaxI2VRefModSlot.None };
        public ObservableCollection<DiffusionModelOption> TwoPassDiffusionModelOptions { get; } = new();
        public ObservableCollection<DiffusionModelOption> TwoPassLoraOptions { get; } = new() { NoTwoPassLora };

        private static readonly DiffusionModelOption NoTwoPassLora = new(string.Empty, "None");

        public RelayCommand ResetTwoPassCommand { get; private set; } = null!;
        public RelayCommand AddTwoPassLoraCommand { get; private set; } = null!;
        public RelayCommand<MiniMaxI2VLoraSlot> RemoveTwoPassLoraCommand { get; private set; } = null!;
        public RelayCommand AddRefModCommand { get; private set; } = null!;
        public RelayCommand<MiniMaxI2VRefModSlot> RemoveRefModCommand { get; private set; } = null!;
        public RelayCommand RefreshRefModsCommand { get; private set; } = null!;
        public RelayCommand RefreshTwoPassModelsCommand { get; private set; } = null!;
        public RelayCommand RefreshTwoPassLorasCommand { get; private set; } = null!;

        /// <summary>"Happy — proceed with final refinement".</summary>
        public RelayCommand ProceedRefineCommand { get; private set; } = null!;

        /// <summary>Re-previews one beat with a new seed — a new take of the same prompt.</summary>
        public RelayCommand<H3TimelineClip> NewTakePreviewCommand { get; private set; } = null!;

        /// <summary>Re-previews one beat with the seed it has — so only the prompt edit changes the take.</summary>
        public RelayCommand<H3TimelineClip> RepreviewSameSeedCommand { get; private set; } = null!;

        private bool _isLoadingTwoPassLists;
        private bool _rebuildingTwoPassLoras;
        private bool _twoPassListsLoaded;

        /// <summary>Set while a story run waits for the user to approve the previews.</summary>
        private TaskCompletionSource<bool>? _approval;

        private void InitializeTwoPass()
        {
            var settings = _settingsService.Settings?.H3VideoEditor;
            TwoPass = settings?.TwoPass ?? new MiniMaxI2VTwoPassOptions();
            if (settings != null) settings.TwoPass = TwoPass;
            TwoPass.PropertyChanged += OnTwoPassChanged;

            foreach (var lora in TwoPass.Loras.Take(MiniMaxI2VTwoPassOptions.MaxLoras))
            {
                OfferTwoPassLora(lora.Name);
                AttachTwoPassLora(new MiniMaxI2VLoraSlot(TwoPassLoras.Count + 1, lora.Name, lora.Strength));
            }
            foreach (var mod in TwoPass.RefMods.Take(MiniMaxI2VTwoPassOptions.MaxRefMods))
            {
                if (!RefModOptions.Contains(mod.Mod)) RefModOptions.Add(mod.Mod);
                AttachRefMod(mod);
            }
            TwoPassLoras.CollectionChanged += (_, _) => SyncTwoPassLoras();
            TwoPassRefMods.CollectionChanged += OnRefModsCollectionChanged;

            ResetTwoPassCommand = new RelayCommand(ResetTwoPass);
            AddTwoPassLoraCommand = new RelayCommand(
                () => AttachTwoPassLora(new MiniMaxI2VLoraSlot(TwoPassLoras.Count + 1)),
                () => TwoPassLoras.Count < MiniMaxI2VTwoPassOptions.MaxLoras);
            RemoveTwoPassLoraCommand = new RelayCommand<MiniMaxI2VLoraSlot>(RemoveTwoPassLora);
            AddRefModCommand = new RelayCommand(
                () => AttachRefMod(new MiniMaxI2VRefModSlot { Components = TwoPassRefMods.Count == 0 ? "Visual" : "All" }),
                () => TwoPassRefMods.Count < MiniMaxI2VTwoPassOptions.MaxRefMods);
            RemoveRefModCommand = new RelayCommand<MiniMaxI2VRefModSlot>(m => { if (m != null) TwoPassRefMods.Remove(m); });
            RefreshRefModsCommand = new RelayCommand(() => _ = LoadTwoPassListsAsync(), () => !_isLoadingTwoPassLists);
            RefreshTwoPassModelsCommand = RefreshRefModsCommand;
            RefreshTwoPassLorasCommand = RefreshRefModsCommand;

            ProceedRefineCommand = new RelayCommand(ProceedRefine, () => CanProceedRefine);
            NewTakePreviewCommand = new RelayCommand<H3TimelineClip>(
                c => _ = RegeneratePreviewAsync(c, newSeed: true), c => c != null && IsTwoPass && !IsGenerating);
            RepreviewSameSeedCommand = new RelayCommand<H3TimelineClip>(
                c => _ = RegeneratePreviewAsync(c, newSeed: false), c => c != null && IsTwoPass && !IsGenerating && c.Seed != 0);

            TwoPassDiffusionModelOptions.Add(new DiffusionModelOption(
                MiniMaxI2VTwoPassOptions.AuthoredDiffusionModel,
                MiniMaxI2VViewModel.LabelFor(MiniMaxI2VTwoPassOptions.AuthoredDiffusionModel) + " (workflow)"));
            OfferTwoPassModel();

            TimelineClips.CollectionChanged += OnTwoPassTimelineChanged;
            _comfyUIService.LivePreviewReceived += OnLivePreviewReceived;
        }

        // ── Live sampler preview ────────────────────────────────────────────────────────────────────
        //
        // The 2-Pass graph's ModelPreviewOverrideKJ streams the whole clip, tiny-VAE decoded, after every sampler
        // step. Shown over the player while a beat renders, so its motion is visible within seconds of the
        // sampler starting rather than when the run ends.

        private readonly LivePreviewFeed _livePreview = new("live_preview_h3ve");
        private string _livePreviewPath = string.Empty;
        private string _livePreviewCaption = string.Empty;
        private string _liveBeatLabel = string.Empty;

        public string LivePreviewPath
        {
            get => _livePreviewPath;
            private set
            {
                if (_livePreviewPath == value) return;
                _livePreviewPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLivePreview));
            }
        }

        public bool HasLivePreview => _livePreviewPath.Length > 0;

        /// <summary>"Beat 3 · preview · step 2/8".</summary>
        public string LivePreviewCaption
        {
            get => _livePreviewCaption;
            private set { if (_livePreviewCaption != value) { _livePreviewCaption = value; OnPropertyChanged(); } }
        }

        /// <summary>Starts (with a label) or ends (null) the live preview of one beat's render.</summary>
        private void SetLiveBeat(string? label) => OnUi(() =>
        {
            _livePreview.Reset();
            _liveBeatLabel = label ?? string.Empty;
            LivePreviewPath = string.Empty;
            LivePreviewCaption = label == null ? string.Empty : $"{label} · encoding the prompt…";
        });

        private void OnLivePreviewReceived(object? sender, string raw)
        {
            if (!IsGenerating || !IsTwoPass || _liveBeatLabel.Length == 0) return;
            if (_livePreview.Write(raw) is not { } frame) return;
            var pass = frame.Pass > 1 ? " · refining" : string.Empty;
            var caption = frame.Step == 0
                ? $"{_liveBeatLabel}{pass} · starting noise"
                : $"{_liveBeatLabel}{pass} · step {frame.Step}/{frame.Total}";
            OnUi(() =>
            {
                if (_liveBeatLabel.Length == 0) return;
                LivePreviewPath = frame.Path;
                LivePreviewCaption = caption;
            });
        }

        // ── The card ────────────────────────────────────────────────────────────────────────────────

        public IReadOnlyList<DiffusionModelOption> TwoPassTextEncoderOptions => MiniMaxI2VViewModel.TwoPassTextEncoders;
        public IReadOnlyList<DiffusionModelOption> TwoPassVideoVaeOptions => MiniMaxI2VViewModel.TwoPassVideoVaes;
        public IReadOnlyList<string> TwoPassSamplerOptions => MiniMaxI2VViewModel.TwoPassSamplers;
        public IReadOnlyList<string> TwoPassSchedulerOptions => MiniMaxI2VViewModel.TwoPassSchedulers;
        public IReadOnlyList<string> TwoPassUpscalerOptions => MiniMaxI2VViewModel.TwoPassUpscalers;
        public IReadOnlyList<string> TwoPassAttentionOptions => MiniMaxI2VViewModel.UltraAttentions;
        public IReadOnlyList<string> TwoPassSparseMethods => MiniMaxI2VViewModel.TwoPassSparseMethodList;
        public IReadOnlyList<string> TwoPassSinkOptions => MiniMaxI2VViewModel.TwoPassSinks;
        public IReadOnlyList<string> TwoPassRtxQualityOptions => MiniMaxI2VViewModel.TwoPassRtxQualities;
        public IReadOnlyList<string> TwoPassFrameGenModes => MiniMaxI2VViewModel.TwoPassFrameGenModeList;
        public IReadOnlyList<string> TwoPassEncoderPresets => MiniMaxI2VViewModel.TwoPassEncoderPresetList;
        public IReadOnlyList<string> TwoPassQuantizedLayerOptions => MiniMaxI2VViewModel.TwoPassQuantizedLayers;
        public IReadOnlyList<string> TwoPassAdalnOptions => MiniMaxI2VViewModel.TwoPassAdalnPorts;
        public IReadOnlyList<int> TwoPassGuideWindowOptions => MiniMaxI2VViewModel.TwoPassGuideWindows;
        public IReadOnlyList<string> RefModComponentOptions => MiniMaxI2VViewModel.RefModComponents;
        public IReadOnlyList<string> RefModCurveDirections => MiniMaxI2VViewModel.RefModCurveDirectionList;
        public IReadOnlyList<string> RefModCurveShapes => MiniMaxI2VViewModel.RefModCurveShapeList;
        public IReadOnlyList<MegapixelOption> TwoPassFirstPassMegapixels => MiniMaxI2VViewModel.TwoPassFirstPassSizes;
        public IReadOnlyList<MegapixelOption> TwoPassFinalMegapixels => MiniMaxI2VViewModel.TwoPassFinalSizes;

        /// <summary>Beats are joined into one file at one frame rate, so FrameGen is not offered here.</summary>
        public bool TwoPassFrameGenAvailable => false;

        public string TwoPassFrameGenNote => string.Empty;

        public bool TwoPassUsesTau => TwoPass.SparseMethod == "sol-attn";
        public bool TwoPassUsesKeepPercent => !TwoPassUsesTau;
        public bool TwoPassUsesScheduler2 => !TwoPass.UseManualSigmas;

        public string TwoPassAuthoredBadge => TwoPass.IsAuthored
            ? "✓ Every dial is on the workflow's authored value."
            : "Changed from the workflow's authored values — ↺ puts them back.";

        public string TwoPassCanvasSummary
        {
            get
            {
                var (w1, h1) = H3Canvas.Resolve(SelectedAspectRatio, TwoPass.FirstPassMegapixels, MiniMaxI2VViewModel.TwoPassResolutionMultiple);
                var (w2, h2) = H3Canvas.Resolve(SelectedAspectRatio, TwoPass.FinalMegapixels, MiniMaxI2VViewModel.TwoPassResolutionMultiple);
                var rtx = TwoPass.UseRtxUpscale ? $" → RTX ×{TwoPass.RtxScale:0.0#}" : string.Empty;
                return $"→ {SelectedAspectRatio} (header) · previews at {w1}×{h1}{rtx}; the refinement lifts them to " +
                       $"≈ {w2}×{h2}{rtx}. Clip lengths come from the timeline.";
            }
        }

        public string TwoPassSamplingSummary => MiniMaxI2VViewModel.TwoPassSamplingSummaryFor(TwoPass);

        public string TwoPassOutputSummary => MiniMaxI2VViewModel.TwoPassOutputSummaryFor(TwoPass, frameGen: false);

        public string TwoPassChainSummary => ChainClips
            ? $"Chain clips is on: each beat continues the previous beat's preview — its last {TwoPass.GuideFrames} frames " +
              $"(≈ {TwoPass.GuideFrames / 24.0:0.00}s) guide the start and are cut off again."
            : "Chain clips is off: every beat starts on its own.";

        private void OnTwoPassChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Every edit also announces IsAuthored; one save per edit is enough.
            if (e.PropertyName == nameof(MiniMaxI2VTwoPassOptions.IsAuthored)) return;
            SaveSettings();
            RaiseTwoPassState();
        }

        private void RaiseTwoPassState()
        {
            OnPropertyChanged(nameof(TwoPassCanvasSummary));
            OnPropertyChanged(nameof(TwoPassSamplingSummary));
            OnPropertyChanged(nameof(TwoPassOutputSummary));
            OnPropertyChanged(nameof(TwoPassChainSummary));
            OnPropertyChanged(nameof(TwoPassAuthoredBadge));
            OnPropertyChanged(nameof(TwoPassUsesTau));
            OnPropertyChanged(nameof(TwoPassUsesKeepPercent));
            OnPropertyChanged(nameof(TwoPassUsesScheduler2));
        }

        private void ResetTwoPass()
        {
            TwoPass.Reset();
            TwoPassLoras.Clear();
            foreach (var mod in TwoPassRefMods) mod.PropertyChanged -= OnRefModSlotChanged;
            TwoPassRefMods.Clear();
            OfferTwoPassModel();
            AddLog("🌹 2-Pass: every dial back on the authored workflow's value.");
        }

        private void OfferTwoPassModel()
        {
            var name = (TwoPass.DiffusionModel ?? string.Empty).Trim().Replace('\\', '/');
            if (name.Length == 0) return;
            if (TwoPassDiffusionModelOptions.Any(o => string.Equals(o.Value, name, StringComparison.OrdinalIgnoreCase)))
                return;
            TwoPassDiffusionModelOptions.Add(new DiffusionModelOption(name, MiniMaxI2VViewModel.LabelFor(name)));
        }

        private void OfferTwoPassLora(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (TwoPassLoraOptions.Any(o => string.Equals(o.Value, name, StringComparison.OrdinalIgnoreCase))) return;
            TwoPassLoraOptions.Add(new DiffusionModelOption(name, MiniMaxI2VViewModel.LabelFor(name)));
        }

        // ── LoRA rows and RefMods (as on the I2V tab) ───────────────────────────────────────────────

        private void AttachTwoPassLora(MiniMaxI2VLoraSlot slot)
        {
            slot.Changed += (_, _) => SyncTwoPassLoras();
            TwoPassLoras.Add(slot);
            AddTwoPassLoraCommand?.NotifyCanExecuteChanged();
        }

        private void RemoveTwoPassLora(MiniMaxI2VLoraSlot? slot)
        {
            if (slot == null) return;
            TwoPassLoras.Remove(slot);
            for (var i = 0; i < TwoPassLoras.Count; i++) TwoPassLoras[i].Index = i + 1;
            AddTwoPassLoraCommand.NotifyCanExecuteChanged();
        }

        private void SyncTwoPassLoras()
        {
            // While the list is rebuilt its ComboBoxes push empty names through for a moment.
            if (_rebuildingTwoPassLoras) return;
            TwoPass.Loras = TwoPassLoras.Select(l => new MiniMaxI2VLora { Name = l.Name, Strength = l.Strength }).ToList();
            TwoPass.Refresh(nameof(MiniMaxI2VTwoPassOptions.Loras));
        }

        private void AttachRefMod(MiniMaxI2VRefModSlot slot)
        {
            slot.PropertyChanged += OnRefModSlotChanged;
            TwoPassRefMods.Add(slot);
        }

        private void OnRefModSlotChanged(object? sender, PropertyChangedEventArgs e) =>
            TwoPass.Refresh(nameof(MiniMaxI2VTwoPassOptions.RefMods));

        private void OnRefModsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (MiniMaxI2VRefModSlot old in e.OldItems) old.PropertyChanged -= OnRefModSlotChanged;
            TwoPass.RefMods = TwoPassRefMods.ToList();
            TwoPass.Refresh(nameof(MiniMaxI2VTwoPassOptions.RefMods));
            AddRefModCommand?.NotifyCanExecuteChanged();
        }

        /// <summary>Reads the server's h3-minimax checkpoints, loras/H3 and models/refmods for the card. One
        /// refresh button reloads all three.</summary>
        private async Task LoadTwoPassListsAsync()
        {
            if (_isLoadingTwoPassLists) return;
            _isLoadingTwoPassLists = true;
            OnUi(() => RefreshRefModsCommand.NotifyCanExecuteChanged());
            try
            {
                var http = _comfyUIService.HttpClient;
                var models = (await http.GetUnetFilenamesAsync())
                    .Select(n => n.Replace('\\', '/'))
                    .Where(n => n.StartsWith("h3-minimax/", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var loras = (await http.GetLoraFilenamesAsync())
                    .Select(n => n.Replace('\\', '/'))
                    .Where(n => n.StartsWith("H3/", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var mods = await http.GetModelFilenamesAsync("MiniMaxH3RefModsLoader", "mod_1");

                OnUi(() =>
                {
                    if (models.Count > 0)
                    {
                        var keep = TwoPass.DiffusionModel;
                        TwoPassDiffusionModelOptions.Clear();
                        foreach (var n in models)
                            TwoPassDiffusionModelOptions.Add(new DiffusionModelOption(n, MiniMaxI2VViewModel.LabelFor(n) +
                                (n == MiniMaxI2VTwoPassOptions.AuthoredDiffusionModel ? " (workflow)" : string.Empty)));
                        if (!models.Any(n => string.Equals(n, keep, StringComparison.OrdinalIgnoreCase)))
                            TwoPassDiffusionModelOptions.Add(new DiffusionModelOption(keep, MiniMaxI2VViewModel.LabelFor(keep) + " (not on server)"));
                        TwoPass.Refresh(nameof(MiniMaxI2VTwoPassOptions.DiffusionModel));
                    }

                    _rebuildingTwoPassLoras = true;
                    try
                    {
                        var held = TwoPassLoras.Select(l => l.Name).ToList();
                        TwoPassLoraOptions.Clear();
                        TwoPassLoraOptions.Add(NoTwoPassLora);
                        foreach (var n in loras) TwoPassLoraOptions.Add(new DiffusionModelOption(n, MiniMaxI2VViewModel.LabelFor(n)));
                        foreach (var name in held.Where(h => h.Length > 0 &&
                                     !loras.Any(n => string.Equals(n, h, StringComparison.OrdinalIgnoreCase))))
                            TwoPassLoraOptions.Add(new DiffusionModelOption(name, MiniMaxI2VViewModel.LabelFor(name) + " (not on server)"));
                        for (var i = 0; i < TwoPassLoras.Count; i++)
                            TwoPassLoras[i].Name = loras.FirstOrDefault(n => string.Equals(n, held[i], StringComparison.OrdinalIgnoreCase)) ?? held[i];
                    }
                    finally { _rebuildingTwoPassLoras = false; }
                    SyncTwoPassLoras();

                    if (mods.Count > 0)
                    {
                        var chosen = TwoPassRefMods.Select(m => m.Mod).ToList();
                        RefModOptions.Clear();
                        foreach (var name in mods) RefModOptions.Add(name);
                        if (!RefModOptions.Contains(MiniMaxI2VRefModSlot.None)) RefModOptions.Insert(0, MiniMaxI2VRefModSlot.None);
                        foreach (var name in chosen.Where(c => !RefModOptions.Contains(c))) RefModOptions.Add(name);
                        for (var i = 0; i < TwoPassRefMods.Count; i++) TwoPassRefMods[i].Mod = chosen[i];
                    }
                });
                _twoPassListsLoaded = true;
                AddLog($"🌹 2-Pass lists: {models.Count} model(s), {loras.Count} LoRA(s) in loras/H3, " +
                       (mods.Count == 0 ? "no RefMods loader on the server." : $"{mods.Count(n => n != MiniMaxI2VRefModSlot.None)} RefMod(s)."));
            }
            catch (Exception ex)
            {
                AddLog($"🌹 2-Pass lists could not be read ({ex.Message}).");
            }
            finally
            {
                _isLoadingTwoPassLists = false;
                OnUi(() => RefreshRefModsCommand.NotifyCanExecuteChanged());
            }
        }

        private static void OnUi(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

        /// <summary>Called when the preset becomes 2-Pass: the lists are read once per session.</summary>
        private void OnTwoPassSelected()
        {
            OfferTwoPassModel();
            if (!_twoPassListsLoaded) _ = LoadTwoPassListsAsync();
            RefreshTwoPassGuides();
            RaiseReviewState();
        }

        // ── Review state ────────────────────────────────────────────────────────────────────────────

        /// <summary>Whether a story run is waiting at "Happy — proceed".</summary>
        public bool IsAwaitingApproval => _approval != null;

        private bool AllPreviewsCurrent =>
            TimelineClips.Count > 0 && TimelineClips.All(c => c.IsPreviewCurrent);

        /// <summary>Enabled once every beat has an up-to-date preview and at least one still needs refining.</summary>
        public bool CanProceedRefine =>
            IsTwoPass && !IsGenerating && AllPreviewsCurrent && TimelineClips.Any(c => !c.IsRendered);

        /// <summary>The line beside the action bar: how far the review has got and what is in the way.</summary>
        public string TwoPassReviewSummary
        {
            get
            {
                if (!IsTwoPass || TimelineClips.Count == 0) return string.Empty;
                var total = TimelineClips.Count;
                var current = TimelineClips.Count(c => c.IsPreviewCurrent);
                var finals = TimelineClips.Count(c => c.IsRendered);
                var edited = TimelineClips.Where(c => c.HasPreview && c.PreviewedPrompt != c.Prompt).Select(c => c.DisplayIndex).ToList();
                var rejoin = TimelineClips.Where(c => c.HasPreview && c.PreviewedPrompt == c.Prompt && c.GuideChanged).Select(c => c.DisplayIndex).ToList();
                var missing = TimelineClips.Where(c => !c.HasPreview).Select(c => c.DisplayIndex).ToList();

                var parts = new List<string> { $"{current}/{total} previews ready" };
                if (finals > 0) parts.Add($"{finals} refined");
                if (edited.Count > 0) parts.Add($"edited since preview: {string.Join(", ", edited)}");
                if (rejoin.Count > 0) parts.Add($"re-join after the beat before changed: {string.Join(", ", rejoin)}");
                if (missing.Count > 0 && missing.Count < total) parts.Add($"not previewed: {string.Join(", ", missing)}");
                var line = string.Join(" · ", parts);
                if (finals == total) return line + " — every beat is refined.";
                return AllPreviewsCurrent
                    ? line + " — happy with them? Proceed runs the final refinement."
                    : line + " — Generate previews renders what is missing.";
            }
        }

        public string GenerateLabel => IsTwoPass ? "Generate previews" : "Generate";

        private void RaiseReviewState()
        {
            OnUi(() =>
            {
                OnPropertyChanged(nameof(TwoPassReviewSummary));
                OnPropertyChanged(nameof(CanProceedRefine));
                OnPropertyChanged(nameof(IsAwaitingApproval));
                OnPropertyChanged(nameof(AnalyzeBlockedHint));
                OnPropertyChanged(nameof(GenerateLabel));
                ProceedRefineCommand?.NotifyCanExecuteChanged();
                NewTakePreviewCommand?.NotifyCanExecuteChanged();
                RepreviewSameSeedCommand?.NotifyCanExecuteChanged();
                CancelCommand?.NotifyCanExecuteChanged();
            });
        }

        private void OnTwoPassTimelineChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (H3TimelineClip c in e.OldItems) c.PropertyChanged -= OnTwoPassClipChanged;
            if (e.NewItems != null)
                foreach (H3TimelineClip c in e.NewItems) { c.PropertyChanged -= OnTwoPassClipChanged; c.PropertyChanged += OnTwoPassClipChanged; }
            if (e.Action == NotifyCollectionChangedAction.Reset)
                foreach (var c in TimelineClips) { c.PropertyChanged -= OnTwoPassClipChanged; c.PropertyChanged += OnTwoPassClipChanged; }
            RaiseReviewState();
        }

        private void OnTwoPassClipChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(H3TimelineClip.TwoPassBadge) or nameof(H3TimelineClip.IsPreviewCurrent))
                RaiseReviewState();
        }

        // ── Chained beats ───────────────────────────────────────────────────────────────────────────

        /// <summary>The preview a beat continues when it is previewed now: the beat before's, when chaining.</summary>
        private string ExpectedGuideFor(H3TimelineClip clip)
        {
            if (!ChainClips || !clip.UseMotionContext) return string.Empty;
            var idx = TimelineClips.IndexOf(clip);
            if (idx <= 0) return string.Empty;
            var prev = TimelineClips[idx - 1];
            return prev.HasPreview ? prev.PreviewPath : string.Empty;
        }

        /// <summary>Flags each previewed beat whose predecessor's preview is no longer the one it continued.</summary>
        private void RefreshTwoPassGuides()
        {
            foreach (var clip in TimelineClips)
                clip.GuideChanged = clip.HasPreview && IsTwoPass &&
                                    !string.Equals(ExpectedGuideFor(clip), clip.PreviewGuidePath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The length to render so a continued beat is still <paramref name="seconds"/> long once the
        /// guide window is cut off its head.</summary>
        internal static double TwoPassChainedSeconds(double seconds, int guideFrames) =>
            (Math.Round(seconds * 24) + guideFrames) / 24.0;

        // ── Stage 1: previews ───────────────────────────────────────────────────────────────────────

        /// <summary>The beats Generate previews renders: anything without an up-to-date preview. A beat already
        /// refined from a preview that is still current is left alone.</summary>
        private List<H3TimelineClip> ClipsNeedingPreview()
        {
            RefreshTwoPassGuides();
            return TimelineClips.Where(c => !c.IsPreviewCurrent).ToList();
        }

        /// <summary>Gives a beat a seed of its own before its first preview; re-previews keep it unless asked.</summary>
        private static void EnsureSeed(H3TimelineClip clip, bool fresh)
        {
            if (fresh || clip.Seed <= 0) clip.Seed = Random.Shared.NextInt64(1, 999_999_999_999_999L);
        }

        /// <summary>↻ on one beat: new take (new seed) or the edited prompt on the same seed.</summary>
        private async Task RegeneratePreviewAsync(H3TimelineClip? clip, bool newSeed)
        {
            if (clip == null || IsGenerating || !IsTwoPass) return;

            IsLogOpen = true;
            IsGenerating = true;
            Progress = 0;
            StatusText = $"Previewing beat {clip.DisplayIndex}...";
            _renderCts = new CancellationTokenSource();
            try
            {
                using var lease = await _workflowCoordinator.AcquireAsync("h3_video_editor", _renderCts.Token);
                await RenderPreviewAsync(clip, newSeed, _renderCts.Token);
                StatusText = "Ready";
            }
            catch (OperationCanceledException)
            {
                clip.State = clip.HasPreview ? H3ClipState.Previewed : H3ClipState.Pending;
                StatusText = "Cancelled";
            }
            catch (Exception ex)
            {
                clip.State = H3ClipState.Failed;
                AddLog($"Preview of beat {clip.DisplayIndex} failed: {ex.Message}");
                StatusText = "Preview failed — see log";
            }
            finally
            {
                IsGenerating = false;
                _renderCts?.Dispose();
                _renderCts = null;
                AutoSaveTimeline();
                RaiseReviewState();
            }
        }

        /// <summary>One beat's first-pass preview. The caller holds the lease and sets IsGenerating.</summary>
        private async Task RenderPreviewAsync(H3TimelineClip clip, bool newSeed, CancellationToken ct, int batchIndex = 0, int batchTotal = 1)
        {
            EnsureSeed(clip, newSeed);
            // A new preview means the old final (if any) is no longer the approved take.
            clip.OutputPath = string.Empty;
            clip.State = H3ClipState.Rendering;
            AddLog($"👁 Beat {clip.DisplayIndex}: preview, seed {clip.Seed}");
            SetLiveBeat($"Beat {clip.DisplayIndex} · preview");
            try
            {
                await GenerateSingleClipAsync(clip, ct, batchIndex, batchTotal, twoPassPreview: true);
            }
            finally { SetLiveBeat(null); }
            clip.State = H3ClipState.Previewed;
            RefreshTwoPassGuides();

            // Show it straight away, so the review can start while the rest render — unless the user has gone to
            // another beat since the last one landed, which is left on screen.
            if (clip.HasPreview)
                OnUi(() =>
                {
                    if (SelectedClip != null && SelectedClip != _followedClip && SelectedClip != clip) return;
                    SelectedClip = clip;
                    PreviewVideoPath = clip.PreviewPath;
                    _followedClip = clip;
                });

            var stale = TimelineClips.Where(c => c.GuideChanged).Select(c => c.DisplayIndex).ToList();
            if (stale.Count > 0 && !IsBatchPreviewing)
                AddLog($"⚠ Beat(s) {string.Join(", ", stale)} continued the take this replaces — re-preview them " +
                       "(Generate previews does all of them) or the joins won't match.");
        }

        private bool IsBatchPreviewing { get; set; }

        /// <summary>The beat the player last moved to on its own as previews landed.</summary>
        private H3TimelineClip? _followedClip;

        /// <summary>Generate in 2-Pass mode: previews every beat that needs one, in timeline order (a chained
        /// beat continues the preview made just before it).</summary>
        private async Task GeneratePreviewsAsync()
        {
            var clips = ClipsNeedingPreview();
            IsLogOpen = true;
            if (clips.Count == 0)
            {
                StatusText = "Every beat has a current preview";
                AddLog("🌹 Every beat already has an up-to-date preview. Edit a beat and ↻ it, or proceed with the final refinement.");
                RaiseReviewState();
                return;
            }

            IsGenerating = true;
            IsBatchPreviewing = true;
            _followedClip = SelectedClip; // the player follows the previews from here until the user picks a beat
            Progress = 0;
            _renderCts = new CancellationTokenSource();
            var total = clips.Count;
            int done = 0, failed = 0;
            AddLog($"👁 Previewing {total} beat(s) — first pass only ({TwoPass.FirstPassSteps} steps at " +
                   $"{TwoPass.FirstPassMegapixels:0.0#} MP). The refinement waits for your go-ahead.");
            try
            {
                foreach (var clip in clips) clip.State = H3ClipState.Queued;
                var busyWith = _workflowCoordinator.CurrentWorkflowType;
                StatusText = busyWith != null ? $"Waiting for {busyWith} to finish..." : "Starting...";
                using var lease = await _workflowCoordinator.AcquireAsync("h3_video_editor", _renderCts.Token);

                for (var i = 0; i < clips.Count; i++)
                {
                    if (_renderCts.Token.IsCancellationRequested) break;
                    var clip = clips[i];
                    StatusText = $"Previewing beat {clip.DisplayIndex} ({i + 1}/{total})...";
                    Progress = (double)i / total * 100;
                    try
                    {
                        // An edited beat keeps its seed, so its new preview differs by the edit alone.
                        await RenderPreviewAsync(clip, newSeed: false, _renderCts.Token, i, total);
                        done++;
                        AutoSaveTimeline();
                    }
                    catch (OperationCanceledException) when (_renderCts.Token.IsCancellationRequested)
                    {
                        clip.State = clip.HasPreview ? H3ClipState.Previewed : H3ClipState.Pending;
                        break;
                    }
                    catch (Exception ex)
                    {
                        clip.State = H3ClipState.Failed;
                        failed++;
                        AddLog($"Preview of beat {clip.DisplayIndex} failed: {ex.Message}");
                        AutoSaveTimeline();
                    }
                }

                Progress = 100;
                StatusText = failed > 0
                    ? $"Previewed {done}/{total} ({failed} failed — ↻ them)"
                    : $"Previewed {done}/{total} — review, then proceed";
                AddLog(AllPreviewsCurrent
                    ? "✅ Every beat is previewed. Play them, edit and ↻ any beat you are not happy with, then click " +
                      "\"Happy — proceed with final refinement\"."
                    : "Some beats still need a preview — ↻ them or Generate previews again.");
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled";
                AddLog("Previews cancelled");
            }
            catch (Exception ex)
            {
                StatusText = "Previews failed — see log";
                AddLog($"ERROR: {ex.Message}");
            }
            finally
            {
                foreach (var clip in clips.Where(c => c.State is H3ClipState.Queued or H3ClipState.Rendering))
                    clip.State = clip.HasPreview ? H3ClipState.Previewed : H3ClipState.Pending;
                IsBatchPreviewing = false;
                IsGenerating = false;
                _renderCts?.Dispose();
                _renderCts = null;
                GenerateClipsCommand.NotifyCanExecuteChanged();
                JoinClipsCommand.NotifyCanExecuteChanged();
                RaiseReviewState();
            }
        }

        // ── The approval gate ───────────────────────────────────────────────────────────────────────

        private void ProceedRefine()
        {
            if (!CanProceedRefine) return;
            if (_approval != null)
            {
                AddLog("✅ Previews approved — the story run continues with the final refinement.");
                _approval.TrySetResult(true);
                return;
            }
            AddLog("✅ Previews approved.");
            _ = RefineClipsAsync();
        }

        /// <summary>A story run waits here after its previews, until Proceed (true) or Cancel (false).</summary>
        private async Task<bool> AwaitApprovalAsync()
        {
            _approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            StoryStatus = "Previews ready — review them, ↻ any beat, then click \"Happy — proceed\"";
            RaiseReviewState();
            try
            {
                return await _approval.Task;
            }
            finally
            {
                _approval = null;
                RaiseReviewState();
            }
        }

        /// <summary>Cancel releases a waiting story run without refining.</summary>
        private void CancelApproval()
        {
            if (_approval == null) return;
            AddLog("Story run stopped at the review — nothing was refined. The previews stay on the timeline.");
            _approval.TrySetResult(false);
        }

        // ── Stage 2: the final refinement ───────────────────────────────────────────────────────────

        /// <summary>Re-renders every approved beat in full: same prompt, seed and guide, both passes.</summary>
        private async Task RefineClipsAsync()
        {
            var clips = TimelineClips.Where(c => c.IsPreviewCurrent && !c.IsRendered).ToList();
            if (clips.Count == 0 || IsGenerating) return;

            IsLogOpen = true;
            IsGenerating = true;
            Progress = 0;
            _renderCts = new CancellationTokenSource();
            var total = clips.Count;
            int done = 0, failed = 0;
            AddLog($"✨ Final refinement of {total} beat(s): {TwoPassSamplingSummary}");
            try
            {
                foreach (var clip in clips) clip.State = H3ClipState.Queued;
                using var lease = await _workflowCoordinator.AcquireAsync("h3_video_editor", _renderCts.Token);
                for (var i = 0; i < clips.Count; i++)
                {
                    if (_renderCts.Token.IsCancellationRequested) break;
                    var clip = clips[i];
                    clip.State = H3ClipState.Rendering;
                    StatusText = $"Refining beat {clip.DisplayIndex} ({i + 1}/{total})...";
                    Progress = (double)i / total * 100;
                    try
                    {
                        SetLiveBeat($"Beat {clip.DisplayIndex} · final");
                        try
                        {
                            await GenerateSingleClipAsync(clip, _renderCts.Token, i, total, twoPassPreview: false);
                        }
                        finally { SetLiveBeat(null); }
                        clip.State = clip.IsRendered ? H3ClipState.Rendered : H3ClipState.Failed;
                        if (clip.IsRendered) done++; else failed++;
                        AutoSaveTimeline();
                    }
                    catch (OperationCanceledException) when (_renderCts.Token.IsCancellationRequested)
                    {
                        clip.State = H3ClipState.Previewed;
                        break;
                    }
                    catch (Exception ex)
                    {
                        clip.State = H3ClipState.Failed;
                        failed++;
                        AddLog($"Refinement of beat {clip.DisplayIndex} failed: {ex.Message}");
                        AutoSaveTimeline();
                    }
                }
                Progress = 100;
                StatusText = failed > 0
                    ? $"Refined {done}/{total} ({failed} failed — proceed again to retry)"
                    : $"Refined {done}/{total} beats";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled";
                AddLog("Refinement cancelled");
            }
            catch (Exception ex)
            {
                StatusText = "Refinement failed — see log";
                AddLog($"ERROR: {ex.Message}");
            }
            finally
            {
                foreach (var clip in clips.Where(c => c.State is H3ClipState.Queued or H3ClipState.Rendering or H3ClipState.Failed))
                    clip.State = clip.IsRendered ? H3ClipState.Rendered : H3ClipState.Previewed;
                IsGenerating = false;
                _renderCts?.Dispose();
                _renderCts = null;
                GenerateClipsCommand.NotifyCanExecuteChanged();
                JoinClipsCommand.NotifyCanExecuteChanged();
                RaiseReviewState();
            }
        }

        // ── The clip graph ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One beat through h3-erosmax-2pass.json via the I2V tab's builder: the card's dials, then this beat's
        /// prompt, length, seed and canvas. A preview stops after pass 1; a chained beat continues
        /// <paramref name="guide"/> (the previous beat's preview, uploaded here) and exports its new segment only.
        /// </summary>
        private async Task<(JsonObject workflow, string saveNodeId, string thumbnailNodeId, string? contextFolder)>
            BuildTwoPassClipWorkflowAsync(H3TimelineClip clip, string?[] uploadedImages, bool preview, string guide, CancellationToken ct)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, MiniMaxI2VViewModel.TwoPassWorkflowFileName);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow file not found: {MiniMaxI2VViewModel.TwoPassWorkflowFileName}");

            var pictures = OrderedPictures(uploadedImages).Take(MiniMaxI2VViewModel.MaxTwoPassPictures).ToList();
            var o = TwoPass.Clone();
            o.DiffusionModel = o.DiffusionModel.Trim().Replace('\\', '/');
            o.PreviewOnly = preview;
            o.FrameGenMultiplier = 1;   // one frame rate for the whole join
            if (preview)
            {
                // A preview is for judging the take: RTX upscale and film grain only add time. Both come after
                // the decode, so the first pass the final re-runs is the same either way.
                o.UseRtxUpscale = false;
                o.GrainStrength = 0;
            }
            o.DurationSeconds = clip.DurationSeconds;

            string? source = null;
            if (guide.Length > 0)
            {
                if (!File.Exists(guide))
                    throw new FileNotFoundException($"The preview beat {clip.DisplayIndex} continues is gone: {guide}");
                source = await _comfyUIService.UploadVideoAsync(guide, ct);
                if (string.IsNullOrEmpty(source)) throw new Exception("Failed to upload the previous beat's preview.");
                o.ContinueMode = true;
                o.DurationSeconds = TwoPassChainedSeconds(clip.DurationSeconds, o.GuideFrames);
                AddLog($"🔗 Beat {clip.DisplayIndex} continues beat {clip.DisplayIndex - 1}'s preview: its last {o.GuideFrames} frames " +
                       $"guide the start, rendered {o.DurationSeconds:0.##}s → ≈{clip.DurationSeconds:0.#}s after the cut");
            }

            if (o.UseTurboLora)
            {
                var all = await _comfyUIService.HttpClient.GetLoraFilenamesAsync(ct);
                var resolved = MiniMaxI2VViewModel.ResolveLoraName(all, o.TurboLora);
                if (resolved == null)
                    AddLog($"⚠ Turbo LoRA {o.TurboLora} is not in the server's loras/ — the run will fail to load it.");
                else o.TurboLora = resolved;
            }

            var item = new MiniMaxI2VQueueItem
            {
                Workflow = I2VWorkflow.TwoPass,
                TwoPass = o,
                Prompt = clip.Prompt,
                AspectRatio = SelectedAspectRatio,
                Megapixels = o.FinalMegapixels,
            };
            var prefix = TwoPassOutputPrefix(clip, preview);
            var built = MiniMaxI2VViewModel.BuildTwoPassGraph(
                await File.ReadAllTextAsync(workflowPath, ct), item, pictures,
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<bool>(), source,
                clip.Seed, prefix, segmentOnly: true, out var pruned);
            var workflow = JsonNode.Parse(built)?.AsObject() ?? throw new Exception("2-Pass workflow could not be parsed.");

            var (w, h) = H3Canvas.Resolve(SelectedAspectRatio, preview ? o.FirstPassMegapixels : o.FinalMegapixels,
                                          MiniMaxI2VViewModel.TwoPassResolutionMultiple);
            AddLog(preview
                ? $"Engine: 🌹 2-Pass preview — {Path.GetFileName(o.DiffusionModel)}, pass 1 only, {o.FirstPassSteps} steps at {w}×{h}, {pruned} node(s) pruned"
                : $"Engine: 🌹 2-Pass final — {MiniMaxI2VViewModel.TwoPassSamplingSummaryFor(o)}, ≈{w}×{h}, {pruned} node(s) pruned");
            AddLog(pictures.Count > 0 ? $"References: {pictures.Count} picture(s)" : "No reference pictures — text-to-video");

            // The thumbnail is the first frame the beat keeps: whatever the encoder reads.
            var frames = workflow[MiniMaxI2VViewModel.TpLokkenNew]?["inputs"]?["images"] as JsonArray;
            AddThumbnailNodes(workflow, clip, frames?[0]?.GetValue<string>() ?? MiniMaxI2VViewModel.TpDecode);
            return (workflow, MiniMaxI2VViewModel.TpSink, ThumbnailSaveNodeId, null);
        }

        /// <summary>Previews and finals land in their own files: video/&lt;project&gt;/preview/clip_N and clip_N.</summary>
        private string TwoPassOutputPrefix(H3TimelineClip clip, bool preview) =>
            preview ? $"video/{ProjectFolder}/preview/clip_{clip.DisplayIndex:D5}" : $"video/{ProjectFolder}/clip_{clip.DisplayIndex:D5}";
    }
}
