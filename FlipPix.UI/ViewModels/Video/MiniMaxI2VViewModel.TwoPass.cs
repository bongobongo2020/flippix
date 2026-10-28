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
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// 🌀 MiniMax I2V's third workflow: <b>🌹 ErosMax Turbo 2-Pass</b> — "H3 Turbo 2-Pass ErosMax b7
    /// (experimental, RefMods v3)", chosen from the dropdown at the top of the tab.
    ///
    /// <para><b>A workflow, not a stack.</b> It ships as its own file, <c>h3-erosmax-2pass.json</c>, flattened
    /// from the authored graph and its four subgraphs by tools/convert_h3_erosmax_2pass.py. Node ids are the
    /// authored ones; subgraph nodes carry ComfyUI's own "&lt;instance&gt;:&lt;inner&gt;" ids.</para>
    ///
    /// <para><b>What it does.</b> One MiniMaxH3ReferenceToVideo at a small first-pass canvas (0.3 MP 4:3), with
    /// up to four pictures, three videos with their soundtracks and three audio clips — or none, which makes
    /// it text-to-video. The 10Eros Max b7 8-step Turbo checkpoint samples it (er_sde on 8 sgm_uniform steps),
    /// the learned latent upscaler lifts it to the final megapixels and a second pass refines it (3 beta steps
    /// at denoise 0.4). RTX super resolution ×1.2 and, optionally, RTX frame generation follow; Lokken encodes
    /// it with optional film grain. RefMods add saved references from models/refmods to the conditioning both
    /// passes read.</para>
    ///
    /// <para><b>Continue mode</b> is the graph's own: the tail of an existing video (22, 39 or 56 frames, and
    /// optionally its soundtrack and its last frame as a picture) guides both passes, and the export trims
    /// that overlap off the new clip and appends the rest to every frame of the source. It is a different
    /// thing from the Ref2VA workflow's continuations, which are further passes of one new take.</para>
    ///
    /// <para><b>Every dial opens on the authored value</b>, and ↺ puts them all back. See
    /// <see cref="MiniMaxI2VTwoPassOptions"/>.</para>
    /// </summary>
    public partial class MiniMaxI2VViewModel
    {
        internal const string TwoPassWorkflowFileName = "workflow/video/h3-minimax/h3-erosmax-2pass.json";

        public const int MaxTwoPassPictures = 4;

        // ── Node ids (locked to h3-erosmax-2pass.json — the authored graph's own) ─────────────────────
        internal const string TpUnet = "30";                 // UNETLoader
        internal const string TpClip = "15";                 // CLIPLoader
        internal const string TpVideoVae = "3";              // VAELoader, video
        internal const string TpTurboLora = "36";            // LoraLoaderModelOnly — authored bypassed
        internal const string TpLoraStack = "101";           // H3PowerLoraStack
        internal const string TpShift = "38";                // MiniMaxH3SigmaShift
        internal const string TpSparse = "100";              // BlockSparseAttention
        internal const string TpAttention = "37";            // ModelAttentionBackend
        internal const string TpPrompt = "2";                // PrimitiveStringMultiline
        internal const string TpDuration = "17";             // PrimitiveFloat, seconds
        internal const string TpResolution = "20";           // ResolutionSelector, first pass
        internal const string TpFinalMegapixels = "23";      // PrimitiveFloat, second pass
        internal const string TpUseFirstPassAudio = "87";    // PrimitiveBoolean
        internal const string TpPreview = "94";              // PrimitiveBoolean
        internal const string TpContinue = "103";            // PrimitiveBoolean
        internal const string TpPrevHasAudio = "117";        // PrimitiveBoolean
        internal const string TpLastFrameRef = "131";        // PrimitiveBoolean
        internal const string TpAudioGuide = "144";          // PrimitiveBoolean
        internal const string TpExtensionFolder = "147";     // PrimitiveString
        internal const string TpPrefixFull = "73";           // PrimitiveString
        internal const string TpPrefixPreview = "74";        // PrimitiveString
        internal const string TpGuideIndex = "153";          // PrimitiveInt, 0/1/2 → 22/39/56 frames
        internal const string TpFrameGenMultiplier = "173";  // INTConstant
        internal const string TpGrain = "174";               // PrimitiveFloat
        internal const string TpNoise = "31";                // RandomNoise
        internal const string TpSampler = "18";              // KSamplerSelect
        internal const string TpScheduler1 = "24";           // BasicScheduler, pass 1
        internal const string TpScheduler2 = "102";          // BasicScheduler, pass 2
        internal const string TpManualSigmas = "185";        // ManualSigmas, pass 2 alternative
        internal const string TpRefModsLoader = "181";       // MiniMaxH3RefModsLoader
        internal const string TpRtxSr = "39";                // RTXVideoSuperResolution
        internal const string TpFrameGen = "155";            // RTXVideoFrameGeneration
        internal const string TpFrameGenSwitch = "156";      // ComfySwitchNode: false = FrameGen, true = skip
        internal const string TpSink = "40";                 // SaveVideo
        internal const string TpRef2V = "151:11";            // MiniMaxH3ReferenceToVideo
        internal const string TpRefModApply = "151:182";     // MiniMaxH3RefModApply
        internal const string TpUpscaler = "151:26";         // MinimaxH3LatentUpscaler3D
        internal const string TpSampler2 = "151:16";         // SamplerCustomAdvanced, pass 2
        internal const string TpSampler2Guider = "151:32";   // BasicGuider, pass 2
        internal const string TpDecode = "151:25";           // VAEDecode
        internal const string TpSourceVideo = "151:105";     // VHS_LoadVideo, the video Continue extends
        internal const string TpSourceFrame = "151:134";     // ComfySwitchNode, the source's last frame
        internal const string TpLokkenNew = "152:135";       // LokkenVideoPostProcessing, the new clip
        internal const string TpLokkenSource = "152:136";    // LokkenVideoPostProcessing, the source
        internal const string TpPreviewPass1 = "12";         // ModelPreviewOverrideKJ on the pass 1 model
        internal const string TpPreviewPass2 = "tp_preview2"; // its copy on pass 2 — added, not authored
        internal const string TpPreviewVideo = "151:95";     // ComfySwitchNode: pass 2 / pass 1 video (94)
        internal const string TpPreviewAudio = "151:88";     // ComfySwitchNode: pass 2 / pass 1 audio (87 OR 94)
        internal const string TpNormalExport = "152:137";    // ConcatenateVideo, the Normal container
        internal const string TpContainer = "152:138";       // ComfySwitchNode: Normal / Continue container
        internal const string TpOutputFolder = "152:150";    // ComfySwitchNode: Normal / extension folder
        internal const string TpTrimmedAudio = "152:123";    // TrimAudioDuration, new audio minus the overlap

        /// <summary>The switches Normal mode collapses onto their false side, so no continuation node reaches
        /// the server: (switch, the input whose source replaces it).</summary>
        private static readonly (string Switch, string Keep)[] TpNormalSwitches =
        {
            ("151:110", "on_false"),  // pass 1 conditioning
            ("151:111", "on_false"),  // pass 2 conditioning
            ("152:115", "on_false"),  // full clip, not the trimmed segment
            ("152:138", "on_false"),  // the Normal container
            ("152:150", "on_false"),  // the Normal output folder
        };

        /// <summary>ResolutionSelector's multiple in the authored graph.</summary>
        internal const int TwoPassResolutionMultiple = 32;

        internal static readonly int[] TwoPassGuideWindows = { 22, 39, 56 };

        /// <summary>Seconds per Gpx of the final canvas. A guess until the first run replaces it.</summary>
        private static double _twoPassSecondsPerGpx = 220;

        /// <summary>The 2-Pass card. The same object settings persist, so an edit is saved as it is made.</summary>
        public MiniMaxI2VTwoPassOptions TwoPass { get; private set; } = new();

        /// <summary>H3PowerLoraStack rows, mirrored onto <see cref="MiniMaxI2VTwoPassOptions.Loras"/>.</summary>
        public ObservableCollection<MiniMaxI2VLoraSlot> TwoPassLoras { get; } = new();

        /// <summary>RefMod slots — the same objects as <see cref="MiniMaxI2VTwoPassOptions.RefMods"/>.</summary>
        public ObservableCollection<MiniMaxI2VRefModSlot> TwoPassRefMods { get; } = new();

        /// <summary>What models/refmods holds on the server, "(none)" first.</summary>
        public ObservableCollection<string> RefModOptions { get; } = new() { MiniMaxI2VRefModSlot.None };

        private string _twoPassContinueSource = string.Empty;
        private bool _isLoadingRefMods;

        public RelayCommand ResetTwoPassCommand { get; private set; } = new(() => { });
        public RelayCommand AddTwoPassLoraCommand { get; private set; } = new(() => { });
        public RelayCommand<MiniMaxI2VLoraSlot> RemoveTwoPassLoraCommand { get; private set; } = new(_ => { });
        public RelayCommand AddRefModCommand { get; private set; } = new(() => { });
        public RelayCommand<MiniMaxI2VRefModSlot> RemoveRefModCommand { get; private set; } = new(_ => { });
        public RelayCommand RefreshRefModsCommand { get; private set; } = new(() => { });
        public RelayCommand BrowseContinueSourceCommand { get; private set; } = new(() => { });
        public RelayCommand UseLastResultAsSourceCommand { get; private set; } = new(() => { });
        public RelayCommand ClearContinueSourceCommand { get; private set; } = new(() => { });

        private void InitTwoPass()
        {
            var settings = _settingsService.Settings;
            TwoPass = settings?.MiniMaxI2VTwoPass ?? new MiniMaxI2VTwoPassOptions();
            if (settings != null) settings.MiniMaxI2VTwoPass = TwoPass;
            TwoPass.PropertyChanged += OnTwoPassChanged;

            foreach (var lora in TwoPass.Loras.Take(MiniMaxI2VTwoPassOptions.MaxLoras))
                AttachTwoPassLora(new MiniMaxI2VLoraSlot(TwoPassLoras.Count + 1, lora.Name, lora.Strength));
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
            RefreshRefModsCommand = new RelayCommand(() => _ = LoadRefModsAsync(), () => !_isLoadingRefMods);
            BrowseContinueSourceCommand = new RelayCommand(async () => await BrowseContinueSourceAsync());
            UseLastResultAsSourceCommand = new RelayCommand(
                () => TwoPassContinueSource = ResultVideoPath,
                () => !string.IsNullOrEmpty(ResultVideoPath) && File.Exists(ResultVideoPath));
            ClearContinueSourceCommand = new RelayCommand(() => TwoPassContinueSource = string.Empty);

            OfferTwoPassModel();
            if (_workflow == I2VWorkflow.TwoPass)
            {
                AddLog($"Workflow: {WorkflowName(_workflow)}");
                _ = LoadRefModsAsync();
            }
        }

        // ── Workflow state ──────────────────────────────────────────────────────────────────────────

        public bool IsTwoPass => _workflow == I2VWorkflow.TwoPass;

        /// <summary>The workflows that take audio and video references and render one graph per item.</summary>
        public bool IsMediaWorkflow => IsUltra || IsTwoPass;

        private void RaiseTwoPassState()
        {
            OnPropertyChanged(nameof(IsTwoPass));
            OnPropertyChanged(nameof(IsMediaWorkflow));
            OnPropertyChanged(nameof(TwoPassCanvasSummary));
            OnPropertyChanged(nameof(TwoPassFrameSummary));
            OnPropertyChanged(nameof(TwoPassSamplingSummary));
            OnPropertyChanged(nameof(TwoPassOutputSummary));
            OnPropertyChanged(nameof(TwoPassContinueSummary));
            OnPropertyChanged(nameof(TwoPassMediaSummary));
            OnPropertyChanged(nameof(TwoPassAuthoredBadge));
            OnPropertyChanged(nameof(TwoPassResolvedAspect));
            OnPropertyChanged(nameof(TwoPassUsesTau));
            OnPropertyChanged(nameof(TwoPassUsesKeepPercent));
            OnPropertyChanged(nameof(TwoPassUsesScheduler2));
            OnPropertyChanged(nameof(TwoPassFrameGenNote));
        }

        private void OnTwoPassChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Every edit also announces IsAuthored; one save per edit is enough.
            if (e.PropertyName == nameof(MiniMaxI2VTwoPassOptions.IsAuthored)) return;

            var settings = _settingsService.Settings;
            if (settings != null) _settingsService.SaveSettings(settings);
            RaiseTwoPassState();
            if (e.PropertyName == nameof(MiniMaxI2VTwoPassOptions.ContinueMode)) OnCanExecuteChanged();
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

        /// <summary>Puts the card's checkpoint in the shared model list, for when the server has not answered
        /// (or has never had that file).</summary>
        private void OfferTwoPassModel()
        {
            var name = NormalizeName(TwoPass.DiffusionModel);
            if (name.Length == 0) return;
            if (DiffusionModelOptions.Any(o => string.Equals(o.Value, name, StringComparison.OrdinalIgnoreCase)))
                return;
            DiffusionModelOptions.Add(new DiffusionModelOption(name, LabelFor(name) + " (2-Pass)"));
        }

        public string TwoPassAuthoredBadge => TwoPass.IsAuthored
            ? "✓ Every dial is on the workflow's authored value."
            : "Changed from the workflow's authored values — ↺ puts them back.";

        // ── Lists ───────────────────────────────────────────────────────────────────────────────────

        // Static so 🎬 H3 Video Editor's 🌹 preset offers the same lists. The instance properties below are what
        // the shared cards (Controls/TwoPassModelCard, TwoPassTuningCards) bind to, on either view model.

        internal static readonly IReadOnlyList<DiffusionModelOption> TwoPassTextEncoders = new[]
        {
            new DiffusionModelOption(MiniMaxI2VTwoPassOptions.AuthoredTextEncoder, "qwen3vl 32b w4a8 convrot (workflow)"),
            new DiffusionModelOption(MiniMaxI2VUltraOptions.AuthoredTextEncoder, "qwen3vl 32b nvfp4 awq"),
            new DiffusionModelOption(MiniMaxI2VUltraOptions.AlternateTextEncoder, "qwen3vl 32b ultra uncensored heretic int8"),
        };

        internal static readonly IReadOnlyList<DiffusionModelOption> TwoPassVideoVaes = new[]
        {
            new DiffusionModelOption(MiniMaxI2VTwoPassOptions.AuthoredVideoVae, "int8 convrot (workflow)"),
            new DiffusionModelOption("minimax_h3_video_vae_fp16.safetensors", "fp16"),
        };

        internal static readonly IReadOnlyList<string> TwoPassSamplers =
            new[] { "er_sde", "euler", "euler_ancestral", "res_multistep", "dpmpp_2m", "dpmpp_sde", "lcm" };

        internal static readonly IReadOnlyList<string> TwoPassSchedulers =
            new[] { "sgm_uniform", "beta", "simple", "normal", "karras", "exponential", "linear_quadratic", "kl_optimal", "beta57" };

        internal static readonly IReadOnlyList<string> TwoPassUpscalers = new[]
        {
            MiniMaxI2VTwoPassOptions.AuthoredLatentUpscaler,
            "minimax_h3_latent_upscaler_3d_bf16.safetensors",
            "minimax_h3_latent_upscaler_3d_conv_v1_fp16.safetensors",
            "h3_upscaler_lms_v0.1.safetensors",
        };

        internal static readonly IReadOnlyList<string> TwoPassSparseMethodList = new[] { "vsa", "sla", "sol-attn" };
        internal static readonly IReadOnlyList<string> TwoPassSinks = new[] { "exact_kv_and_rows", "exact_kv", "off" };
        internal static readonly IReadOnlyList<string> TwoPassRtxQualities = new[] { "ULTRA", "HIGH", "MEDIUM", "LOW", "BICUBIC" };
        internal static readonly IReadOnlyList<string> TwoPassFrameGenModeList = new[] { "HIGH", "MEDIUM", "LOW" };
        internal static readonly IReadOnlyList<string> TwoPassEncoderPresetList =
            new[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };
        internal static readonly IReadOnlyList<string> TwoPassQuantizedLayers = new[] { "auto", "branch", "merge" };
        internal static readonly IReadOnlyList<string> TwoPassAdalnPorts = new[] { "auto", "strip", "off" };
        internal static readonly IReadOnlyList<string> RefModComponents = new[] { "All", "Visual", "Audio" };
        internal static readonly IReadOnlyList<string> RefModCurveDirectionList =
            new[] { "constant", "concept_at_start", "concept_at_middle", "concept_at_end", "concept_at_ends" };
        internal static readonly IReadOnlyList<string> RefModCurveShapeList =
            new[] { "linear", "ease", "sigmoid", "tanh", "quadratic", "cubic", "exponential", "stair", "elastic", "bump", "dip" };

        /// <summary>The first-pass sizes, from the workflow's own size note (multiple 32, 4:3 column).</summary>
        internal static readonly IReadOnlyList<MegapixelOption> TwoPassFirstPassSizes = new[]
        {
            new MegapixelOption(0.2, "0.2 MP — 544×384"),
            new MegapixelOption(0.3, "0.3 MP — 640×480 (workflow)"),
            new MegapixelOption(0.4, "0.4 MP — 736×576"),
            new MegapixelOption(0.5, "0.5 MP — 832×640"),
            new MegapixelOption(0.6, "0.6 MP — 928×672"),
        };

        /// <summary>The second-pass target, before RTX.</summary>
        internal static readonly IReadOnlyList<MegapixelOption> TwoPassFinalSizes = new[]
        {
            new MegapixelOption(0.7, "0.7 MP"),
            new MegapixelOption(0.9, "0.9 MP"),
            new MegapixelOption(1.0, "1.0 MP (workflow)"),
            new MegapixelOption(1.2, "1.2 MP"),
            new MegapixelOption(1.5, "1.5 MP"),
            new MegapixelOption(1.8, "1.8 MP"),
            new MegapixelOption(2.0, "2.0 MP"),
        };

        public IReadOnlyList<DiffusionModelOption> TwoPassTextEncoderOptions => TwoPassTextEncoders;
        public IReadOnlyList<DiffusionModelOption> TwoPassVideoVaeOptions => TwoPassVideoVaes;
        public IReadOnlyList<string> TwoPassSamplerOptions => TwoPassSamplers;
        public IReadOnlyList<string> TwoPassSchedulerOptions => TwoPassSchedulers;
        public IReadOnlyList<string> TwoPassUpscalerOptions => TwoPassUpscalers;
        public IReadOnlyList<string> TwoPassAttentionOptions => UltraAttentions;
        public IReadOnlyList<string> TwoPassSparseMethods => TwoPassSparseMethodList;
        public IReadOnlyList<string> TwoPassSinkOptions => TwoPassSinks;
        public IReadOnlyList<string> TwoPassRtxQualityOptions => TwoPassRtxQualities;
        public IReadOnlyList<string> TwoPassFrameGenModes => TwoPassFrameGenModeList;
        public IReadOnlyList<string> TwoPassEncoderPresets => TwoPassEncoderPresetList;
        public IReadOnlyList<string> TwoPassQuantizedLayerOptions => TwoPassQuantizedLayers;
        public IReadOnlyList<string> TwoPassAdalnOptions => TwoPassAdalnPorts;
        public IReadOnlyList<int> TwoPassGuideWindowOptions => TwoPassGuideWindows;
        public IReadOnlyList<string> RefModComponentOptions => RefModComponents;
        public IReadOnlyList<string> RefModCurveDirections => RefModCurveDirectionList;
        public IReadOnlyList<string> RefModCurveShapes => RefModCurveShapeList;
        public IReadOnlyList<MegapixelOption> TwoPassFirstPassMegapixels => TwoPassFirstPassSizes;
        public IReadOnlyList<MegapixelOption> TwoPassFinalMegapixels => TwoPassFinalSizes;

        // The names the shared cards bind to, for the lists this tab shares with its other workflows.
        public ObservableCollection<DiffusionModelOption> TwoPassDiffusionModelOptions => DiffusionModelOptions;
        public RelayCommand RefreshTwoPassModelsCommand => RefreshDiffusionModelsCommand;
        public ObservableCollection<DiffusionModelOption> TwoPassLoraOptions => LoraOptions;
        public RelayCommand RefreshTwoPassLorasCommand => RefreshLorasCommand;

        /// <summary>The OUTPUT card's FrameGen rows. The editor hides them: its clips are joined at one frame rate.</summary>
        public bool TwoPassFrameGenAvailable => true;

        // ── Summaries ───────────────────────────────────────────────────────────────────────────────

        /// <summary>The aspect the 2-Pass renders at — the picked one, or Auto resolved off picture 1.</summary>
        public string TwoPassResolvedAspect =>
            TwoPass.AspectRatio == H3Canvas.AutoAspect ? ResolvedAspectRatio : TwoPass.AspectRatio;

        public string TwoPassCanvasSummary => TwoPassCanvasSummaryFor(TwoPassResolvedAspect, TwoPass);

        internal static string TwoPassCanvasSummaryFor(string aspect, MiniMaxI2VTwoPassOptions o)
        {
            var (w1, h1) = H3Canvas.Resolve(aspect, o.FirstPassMegapixels, TwoPassResolutionMultiple);
            var (w2, h2) = H3Canvas.Resolve(aspect, o.FinalMegapixels, TwoPassResolutionMultiple);
            var rtx = o.UseRtxUpscale ? $" → RTX ×{o.RtxScale:0.0#} ≈ {(int)(w2 * o.RtxScale)}×{(int)(h2 * o.RtxScale)}" : string.Empty;
            return o.PreviewOnly
                ? $"→ {aspect} · pass 1 only at {w1}×{h1}{(o.UseRtxUpscale ? $" → RTX ×{o.RtxScale:0.0#}" : string.Empty)} (preview)"
                : $"→ {aspect} · pass 1 at {w1}×{h1} → latent upscale ≈ {w2}×{h2}{rtx}";
        }

        public string TwoPassFrameSummary =>
            $"{UltraFrameCount(TwoPass.DurationSeconds)} frames at 24 fps (≈ {UltraFrameCount(TwoPass.DurationSeconds) / 24.0:0.0}s)" +
            (TwoPass.ContinueMode ? $" — includes the {TwoPass.GuideFrames}-frame guide window" : string.Empty);

        public string TwoPassSamplingSummary => TwoPassSamplingSummaryFor(TwoPass);

        internal static string TwoPassSamplingSummaryFor(MiniMaxI2VTwoPassOptions o)
        {
            var pass2 = o.PreviewOnly
                ? "pass 2 skipped (preview)"
                : o.UseManualSigmas
                    ? $"pass 2 on manual sigmas [{o.ManualSigmas}]"
                    : $"pass 2 {o.SecondPassScheduler} {o.SecondPassSteps} steps @ {o.SecondPassDenoise:0.00}";
            var sparse = !o.UseSparseAttention
                ? "dense"
                : o.SparseMethod == "sol-attn" ? $"sol-attn τ {o.SparseTau:0.00}" : $"{o.SparseMethod} {o.SparseKeepPercent:0.#}%";
            return $"{o.Sampler} · pass 1 {o.FirstPassScheduler} {o.FirstPassSteps} steps · {pass2} · " +
                   $"shift {o.ShiftVideo:0.##}/{o.ShiftAudio:0.##} · {sparse}";
        }

        public string TwoPassOutputSummary => TwoPassOutputSummaryFor(TwoPass);

        internal static string TwoPassOutputSummaryFor(MiniMaxI2VTwoPassOptions o, bool frameGen = true)
        {
            var fps = !frameGen || o.ContinueMode || o.FrameGenMultiplier <= 1
                ? "24 fps"
                : $"FrameGen ×{o.FrameGenMultiplier} → {24 * o.FrameGenMultiplier} fps";
            var grain = o.GrainStrength > 0 ? $"grain {o.GrainStrength:0.000}" : "no grain";
            return $"{fps} · {grain} · H.264 CRF {o.Crf}, {o.EncoderPreset}";
        }

        public string TwoPassFrameGenNote => TwoPass.ContinueMode && TwoPass.FrameGenMultiplier > 1
            ? "⚠ Continue mode skips FrameGen and stays at 24 fps."
            : "1× = off. 2×–5× save 48–120 fps (Normal mode only).";

        public bool TwoPassUsesTau => TwoPass.SparseMethod == "sol-attn";

        public bool TwoPassUsesKeepPercent => !TwoPassUsesTau;

        /// <summary>The second-pass scheduler dials, greyed out while manual sigmas replace them.</summary>
        public bool TwoPassUsesScheduler2 => !TwoPass.UseManualSigmas;

        public string TwoPassMediaSummary
        {
            get
            {
                var pictures = FilledReferences.Count;
                var audios = UltraAudioPaths.Count;
                var videos = UltraVideoPaths.Count;
                var parts = new List<string>
                {
                    pictures == 0 ? "no picture (text-to-video)" : $"{pictures} picture{(pictures == 1 ? "" : "s")}",
                };
                if (videos > 0) parts.Add($"{videos} video");
                if (audios > 0) parts.Add($"{audios} audio");
                var silent = VideoReferences.Count(v => v.HasFile && !v.HasSoundtrack);
                var line = string.Join(" · ", parts);
                if (silent > 0) line += $" · {silent} video without soundtrack";
                return line + " · in the prompt: <Picture N>, <Video N>, <Audio N> — video soundtracks are " +
                       "numbered before the standalone audio.";
            }
        }

        // ── Continue mode ───────────────────────────────────────────────────────────────────────────

        /// <summary>The video Continue mode extends — a local file, uploaded when the item runs.</summary>
        public string TwoPassContinueSource
        {
            get => _twoPassContinueSource;
            set
            {
                value ??= string.Empty;
                if (_twoPassContinueSource == value) return;
                _twoPassContinueSource = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TwoPassContinueSourceName));
                OnPropertyChanged(nameof(TwoPassContinueSummary));
                if (value.Length > 0)
                {
                    TwoPass.ContinueMode = true;
                    AddLog($"⏩ Continue from: {Path.GetFileName(value)}");
                }
                OnCanExecuteChanged();
            }
        }

        public string TwoPassContinueSourceName => File.Exists(_twoPassContinueSource)
            ? Path.GetFileName(_twoPassContinueSource)
            : "no video chosen";

        private bool HasContinueSource => File.Exists(_twoPassContinueSource);

        public string TwoPassContinueSummary
        {
            get
            {
                if (!TwoPass.ContinueMode) return "Off — a new video. Turn on to extend an existing one.";
                if (!HasContinueSource) return "⚠ Choose the video to continue (or use the last result).";
                var seconds = TwoPass.GuideFrames / 24.0;
                return $"Extends {Path.GetFileName(_twoPassContinueSource)}: its last {TwoPass.GuideFrames} frames " +
                       $"(≈ {seconds:0.00}s) guide both passes" +
                       (TwoPass.UseSourceAudioGuide && TwoPass.PreviousHasAudio ? " with its soundtrack" : string.Empty) +
                       (TwoPass.UseLastFrameReference ? ", and its last frame is the final <Picture N>" : string.Empty) +
                       ". The overlap is trimmed and the rest appended to the whole source, at 24 fps.";
            }
        }

        private async Task BrowseContinueSourceAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select the video to continue",
                "Video Files|*.mp4;*.mov;*.webm;*.mkv|All Files|*.*",
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                persistKey: "minimaxi2v.twopass.continue");
            if (path != null) TwoPassContinueSource = path;
        }

        // ── LoRA rows ───────────────────────────────────────────────────────────────────────────────

        private void AttachTwoPassLora(MiniMaxI2VLoraSlot slot)
        {
            slot.Changed += (_, _) => SyncTwoPassLoras();
            TwoPassLoras.Add(slot);
            AddTwoPassLoraCommand.NotifyCanExecuteChanged();
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
            // The shared LoRA list is being rebuilt: its ComboBoxes push empty names through for a moment,
            // and LoadLorasAsync writes the real ones back straight after.
            if (_rebuildingLoras) return;
            TwoPass.Loras = TwoPassLoras.Select(l => new MiniMaxI2VLora { Name = l.Name, Strength = l.Strength }).ToList();
            TwoPass.Refresh(nameof(MiniMaxI2VTwoPassOptions.Loras));
        }

        // ── RefMods ─────────────────────────────────────────────────────────────────────────────────

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
            AddRefModCommand.NotifyCanExecuteChanged();
        }

        /// <summary>Reads models/refmods from the loader's own mod_1 list, so a remote server's files show.</summary>
        private async Task LoadRefModsAsync()
        {
            if (_isLoadingRefMods) return;
            _isLoadingRefMods = true;
            OnUiThread(() => RefreshRefModsCommand.NotifyCanExecuteChanged());
            try
            {
                var found = await _comfyUIService.HttpClient.GetModelFilenamesAsync("MiniMaxH3RefModsLoader", "mod_1");
                if (found.Count == 0)
                {
                    AddLog("RefMods: the server has no MiniMaxH3RefModsLoader (ComfyUI-MiniMaxH3Mod) — RefMods are off.");
                    return;
                }
                OnUiThread(() =>
                {
                    var chosen = TwoPassRefMods.Select(m => m.Mod).ToList();
                    RefModOptions.Clear();
                    foreach (var name in found) RefModOptions.Add(name);
                    if (!RefModOptions.Contains(MiniMaxI2VRefModSlot.None)) RefModOptions.Insert(0, MiniMaxI2VRefModSlot.None);
                    foreach (var name in chosen.Where(c => !RefModOptions.Contains(c))) RefModOptions.Add(name);
                    for (var i = 0; i < TwoPassRefMods.Count; i++) TwoPassRefMods[i].Mod = chosen[i];
                });
                AddLog($"RefMods: {found.Count(n => n != MiniMaxI2VRefModSlot.None)} saved reference(s) in models/refmods.");
            }
            finally
            {
                _isLoadingRefMods = false;
                OnUiThread(() => RefreshRefModsCommand.NotifyCanExecuteChanged());
            }
        }

        /// <summary>
        /// What Analyze tells the prompt writer about 2-Pass's clips and Continue mode. The numbering is the
        /// graph's: a video's soundtrack is an audio reference numbered before the standalone audio clips.
        /// </summary>
        private List<string> TwoPassMediaInstructions()
        {
            var lines = new List<string>();
            var videos = VideoReferences.Where(v => v.HasFile).ToList();
            var audios = UltraAudioPaths;
            if (videos.Count > 0 || audios.Count > 0)
            {
                lines.Add("This take also has reference clips the pictures above do not show. Bind them in "
                          + "subject_definitions and retention_analysis the same way as the pictures:");
                for (var i = 0; i < videos.Count; i++)
                    lines.Add($"  <Video {i + 1}> — {Path.GetFileNameWithoutExtension(videos[i].Path)} (reference video: "
                              + "appearance and motion reference)");
                var audio = 0;
                foreach (var v in videos.Where(v => v.HasSoundtrack))
                    lines.Add($"  <Audio {++audio}> — the soundtrack of {Path.GetFileNameWithoutExtension(v.Path)} (voice "
                              + "timbre and accent; generate new words, never copy the recording)");
                foreach (var a in audios)
                    lines.Add($"  <Audio {++audio}> — {Path.GetFileNameWithoutExtension(a)} (reference audio: the voice "
                              + "timbre and accent of whichever subject the draft idea gives it; generate new words, "
                              + "never copy the recording)");
                lines.Add(string.Empty);
            }
            if (FilledReferences.Count == 0 && videos.Count == 0)
            {
                lines.Add("There are no reference pictures: this is text-to-video. Describe every subject and the set "
                          + "in words, and do not refer to any <Picture N>.");
                lines.Add(string.Empty);
            }
            if (TwoPass.ContinueMode && HasContinueSource)
            {
                lines.Add($"This clip CONTINUES an existing video ({Path.GetFileNameWithoutExtension(_twoPassContinueSource)}). "
                          + $"Its first ~{TwoPass.GuideFrames / 24.0:0.0}s re-trace the end of that video and are cut away; "
                          + "keep the same scene, subjects, lighting and voices, and describe what happens NEXT.");
                if (TwoPass.UseLastFrameReference)
                    lines.Add($"  <Picture {FilledReferences.Count + 1}> is the last frame of that video.");
                lines.Add(string.Empty);
            }
            return lines;
        }

        // ── Validation ──────────────────────────────────────────────────────────────────────────────

        /// <summary>2-Pass needs only a prompt: with no picture it is text-to-video, as authored.</summary>
        private string TwoPassBlockedReason()
        {
            if (string.IsNullOrWhiteSpace(Prompt)) return "Write a prompt — press Analyze, or write one.";
            if (IsAnalyzing) return "Waiting for Analyze to finish…";
            if (TwoPass.ContinueMode && !HasContinueSource)
                return "Continue mode is on — choose the video to continue, or turn Continue off.";
            if (TwoPass.ContinueMode && TwoPass.DurationSeconds * 24 <= TwoPass.GuideFrames)
                return $"The duration has to be longer than the {TwoPass.GuideFrames}-frame guide window it includes.";
            return string.Empty;
        }

        // ── Building the graph ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes one 2-Pass item into h3-erosmax-2pass.json. Everything not set here stays exactly as the
        /// authored graph has it. <paramref name="videoSoundtracks"/> is index-matched to
        /// <paramref name="videos"/>; <paramref name="continueSource"/> is the uploaded source video, or null
        /// for a Normal take.
        /// </summary>
        internal static string BuildTwoPassWorkflow(
            string json, MiniMaxI2VQueueItem item,
            IReadOnlyList<string> pictures, IReadOnlyList<string> audios, IReadOnlyList<string> videos,
            IReadOnlyList<bool> videoSoundtracks, string? continueSource,
            long runSeed, string runToken, out int pruned) =>
            BuildTwoPassGraph(json, item, pictures, audios, videos, videoSoundtracks, continueSource,
                              runSeed, $"{OutputSubfolder}/{runToken}", segmentOnly: false, out pruned);

        /// <summary>
        /// <see cref="BuildTwoPassWorkflow"/> with the two choices 🎬 H3 Video Editor needs:
        /// <paramref name="outputPrefix"/> is the SaveVideo prefix (both the full and the preview one), and
        /// <paramref name="segmentOnly"/> makes a Continue take export only its new segment — the overlap cut
        /// off picture and sound — instead of the source with the segment appended, so each beat stays its
        /// own clip on the timeline.
        /// </summary>
        internal static string BuildTwoPassGraph(
            string json, MiniMaxI2VQueueItem item,
            IReadOnlyList<string> pictures, IReadOnlyList<string> audios, IReadOnlyList<string> videos,
            IReadOnlyList<bool> videoSoundtracks, string? continueSource,
            long runSeed, string outputPrefix, bool segmentOnly, out int pruned)
        {
            var o = item.TwoPass ?? throw new Exception("This item has no 2-Pass settings.");
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, TpUnet, "UNETLoader");
            RequireClass(root, TpRef2V, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, TpSampler2, "SamplerCustomAdvanced");
            RequireClass(root, TpSink, "SaveVideo");

            var isContinue = !string.IsNullOrEmpty(continueSource);

            // ── Models ────────────────────────────────────────────────────────
            if (o.DiffusionModel.Length > 0) SetInput(root, TpUnet, "unet_name", NormalizeName(o.DiffusionModel));
            SetInput(root, TpClip, "clip_name", o.TextEncoder);
            SetInput(root, TpVideoVae, "vae_name", o.VideoVae);

            // ── The model wire, from the loader outwards ──────────────────────
            // A node the card switches off is stepped over and the prune takes it.
            var wire = TpUnet;
            if (o.UseTurboLora && o.TurboLoraStrength > 0)
            {
                Link(root, TpTurboLora, "model", wire, 0);
                SetInput(root, TpTurboLora, "lora_name", o.TurboLora);
                SetInput(root, TpTurboLora, "strength_model", o.TurboLoraStrength);
                wire = TpTurboLora;
            }

            var loras = o.Loras.Where(l => !string.IsNullOrWhiteSpace(l.Name) && l.Strength > 0)
                               .Take(MiniMaxI2VTwoPassOptions.MaxLoras).ToList();
            if (loras.Count > 0)
            {
                var stack = root[TpLoraStack]!["inputs"]!.AsObject();
                foreach (var key in stack.Select(kv => kv.Key).Where(k => k.StartsWith("lora_", StringComparison.Ordinal)).ToList())
                    stack.Remove(key);
                Link(root, TpLoraStack, "model", wire, 0);
                SetInput(root, TpLoraStack, "quantized_layers", o.QuantizedLayers);
                SetInput(root, TpLoraStack, "adaln_port", o.AdalnPort);
                for (var i = 0; i < loras.Count; i++)
                    stack[$"lora_{i + 1}"] = new JsonObject
                    {
                        ["on"] = true,
                        ["lora"] = NormalizeName(loras[i].Name),
                        ["strength"] = loras[i].Strength,
                    };
                wire = TpLoraStack;
            }

            Link(root, TpShift, "model", wire, 0);
            SetInput(root, TpShift, "shift_video", o.ShiftVideo);
            SetInput(root, TpShift, "shift_audio", o.ShiftAudio);
            wire = TpShift;

            if (o.UseSparseAttention)
            {
                var sparse = root[TpSparse]!["inputs"]!.AsObject();
                sparse.Remove("selection.keep_percent");
                sparse.Remove("selection.tau");
                Link(root, TpSparse, "model", wire, 0);
                SetInput(root, TpSparse, "selection", o.SparseMethod);
                if (o.SparseMethod == "sol-attn") SetInput(root, TpSparse, "selection.tau", o.SparseTau);
                else SetInput(root, TpSparse, "selection.keep_percent", o.SparseKeepPercent);
                SetInput(root, TpSparse, "start_percent", o.SparseStart);
                SetInput(root, TpSparse, "end_percent", o.SparseEnd);
                SetInput(root, TpSparse, "dense_blocks", o.DenseBlocks);
                SetInput(root, TpSparse, "sink_conditioning", o.SinkConditioning);
                wire = TpSparse;
            }

            Link(root, TpAttention, "model", wire, 0);
            SetInput(root, TpAttention, "attention", o.AttentionBackend);

            // ── Prompt, length, canvas ────────────────────────────────────────
            SetInput(root, TpPrompt, "value", item.Prompt);
            SetInput(root, TpDuration, "value", o.DurationSeconds);
            SetInput(root, TpFinalMegapixels, "value", o.FinalMegapixels);
            if (H3Canvas.RequiresLiteralCanvas(item.AspectRatio))
            {
                // The stereo aspects are not on ResolutionSelector's COMBO — resolved here by its arithmetic.
                var (cw, ch) = H3Canvas.Resolve(item.AspectRatio, o.FirstPassMegapixels, TwoPassResolutionMultiple);
                root[NodeCanvasWidth] = IntNode(cw, "Canvas width (first pass)");
                root[NodeCanvasHeight] = IntNode(ch, "Canvas height (first pass)");
                Retarget(root, TpResolution, 0, NodeCanvasWidth);
                Retarget(root, TpResolution, 1, NodeCanvasHeight);
            }
            else
            {
                SetInput(root, TpResolution, "aspect_ratio", item.AspectRatio);
                SetInput(root, TpResolution, "megapixels", o.FirstPassMegapixels);
                SetInput(root, TpResolution, "multiple", TwoPassResolutionMultiple);
            }

            // ── References ────────────────────────────────────────────────────
            SetInput(root, TpRef2V, "ref_image_size", o.MaxFidelityReferences ? "max" : "match");
            var refInputs = root[TpRef2V]!["inputs"]!.AsObject();
            foreach (var key in refInputs.Select(kv => kv.Key).Where(k => k.StartsWith("ref_", StringComparison.Ordinal) &&
                                                                         k.Contains('.')).ToList())
                refInputs.Remove(key);

            var pictureCount = Math.Min(pictures.Count, MaxTwoPassPictures);
            for (var i = 0; i < pictureCount; i++)
            {
                var id = $"tp_img_{i}";
                root[id] = Node("LoadImage", $"Picture {i + 1}", new JsonObject { ["image"] = pictures[i] });
                Link(root, TpRef2V, $"ref_images.ref_image_{i}", id, 0);
            }
            // The source's last frame comes after every enabled picture — <Picture N+1>.
            if (isContinue && o.UseLastFrameReference)
                Link(root, TpRef2V, $"ref_images.ref_image_{pictureCount}", TpSourceFrame, 0);

            for (var i = 0; i < Math.Min(videos.Count, MaxUltraVideos); i++)
            {
                // The authored loaders: 24 fps, native size, every frame, no format preset.
                var id = $"tp_vid_{i}";
                root[id] = Node("VHS_LoadVideo", $"Video {i + 1} + soundtrack", new JsonObject
                {
                    ["video"] = videos[i],
                    ["force_rate"] = 24,
                    ["custom_width"] = 0,
                    ["custom_height"] = 0,
                    ["frame_load_cap"] = 0,
                    ["skip_first_frames"] = 0,
                    ["select_every_nth"] = 1,
                    ["format"] = "None",
                });
                Link(root, TpRef2V, $"ref_videos.ref_video_{i}", id, 0);
                if (i >= videoSoundtracks.Count || videoSoundtracks[i])
                    Link(root, TpRef2V, $"ref_video_audios.ref_video_audio_{i}", id, 2);
            }
            for (var i = 0; i < Math.Min(audios.Count, MaxUltraAudios); i++)
            {
                var id = $"tp_aud_{i}";
                root[id] = Node("LoadAudio", $"Audio {i + 1}", new JsonObject { ["audio"] = audios[i] });
                Link(root, TpRef2V, $"ref_audios.ref_audio_{i}", id, 0);
            }

            // ── Normal / Continue ─────────────────────────────────────────────
            SetInput(root, TpContinue, "value", isContinue);
            if (isContinue)
            {
                SetInput(root, TpSourceVideo, "video", continueSource!);
                SetInput(root, TpPrevHasAudio, "value", o.PreviousHasAudio);
                SetInput(root, TpLastFrameRef, "value", o.UseLastFrameReference);
                SetInput(root, TpAudioGuide, "value", o.UseSourceAudioGuide);
                SetInput(root, TpGuideIndex, "value", Math.Max(0, Array.IndexOf(TwoPassGuideWindows, o.GuideFrames)));
                // The stitched file lands beside every other take, so the tab finds it.
                SetInput(root, TpExtensionFolder, "value", OutputSubfolder);

                if (segmentOnly)
                {
                    // The Normal container already encodes the trimmed segment (152:115 is on its Continue side);
                    // it only needs the trimmed soundtrack to match, and the take's own folder.
                    Link(root, TpNormalExport, "complete_audio", TpTrimmedAudio, 0);
                    Collapse(root, TpContainer, "on_false");
                    Collapse(root, TpOutputFolder, "on_false");
                }
            }
            else
            {
                // Each switch's false side, wired straight through: nothing of the continuation branch is
                // left for the server to validate or load.
                foreach (var (sw, keep) in TpNormalSwitches) Collapse(root, sw, keep);
            }

            // ── RefMods ───────────────────────────────────────────────────────
            var mods = o.ActiveRefMods;
            if (mods.Count > 0)
            {
                SetInput(root, TpRefModsLoader, "show_info", false);
                SetInput(root, TpRefModsLoader, "max_total_tokens", o.RefModMaxTokens);
                for (var k = 1; k <= MiniMaxI2VTwoPassOptions.MaxRefMods; k++)
                {
                    var m = k <= mods.Count ? mods[k - 1] : new MiniMaxI2VRefModSlot();
                    SetInput(root, TpRefModsLoader, $"mod_{k}", m.Mod);
                    SetInput(root, TpRefModsLoader, $"strength_{k}", m.Strength);
                    SetInput(root, TpRefModsLoader, $"copies_{k}", m.Copies);
                    SetInput(root, TpRefModsLoader, $"components_{k}", m.Components);
                    SetInput(root, TpRefModsLoader, $"visual_strength_{k}", m.VisualStrength);
                    SetInput(root, TpRefModsLoader, $"audio_strength_{k}", m.AudioStrength);
                }
                SetInput(root, TpRefModApply, "retention", o.RefModRetention);
                SetInput(root, TpRefModApply, "curve_direction", o.RefModCurveDirection);
                SetInput(root, TpRefModApply, "curve_shape", o.RefModCurveShape);
                SetInput(root, TpRefModApply, "curve_value", o.RefModCurveValue);
                SetInput(root, TpRefModApply, "scramble_seed", o.RefModScrambleSeed);
                SetInput(root, TpRefModApply, "max_total_tokens", o.RefModMaxTokens);
            }
            else
            {
                // No RefMod chosen: the conditioning goes straight on, and a server without
                // ComfyUI-MiniMaxH3Mod is never asked for its nodes.
                Retarget(root, TpRefModApply, 0, TpRef2V, 0);
            }

            // ── Sampling ──────────────────────────────────────────────────────
            SetInput(root, TpNoise, "noise_seed", runSeed);
            SetInput(root, TpSampler, "sampler_name", o.Sampler);
            SetInput(root, TpScheduler1, "scheduler", o.FirstPassScheduler);
            SetInput(root, TpScheduler1, "steps", Math.Clamp(o.FirstPassSteps, 1, 60));
            SetInput(root, TpScheduler2, "scheduler", o.SecondPassScheduler);
            SetInput(root, TpScheduler2, "steps", Math.Clamp(o.SecondPassSteps, 1, 30));
            SetInput(root, TpScheduler2, "denoise", Math.Clamp(o.SecondPassDenoise, 0.01, 1.0));
            if (o.UseManualSigmas)
            {
                SetInput(root, TpManualSigmas, "sigmas", o.ManualSigmas);
                Link(root, TpSampler2, "sigmas", TpManualSigmas, 0);
            }
            SetInput(root, TpUpscaler, "model_name", o.LatentUpscaler);

            // The authored graph previews pass 1 only. A copy of its preview override on pass 2's model lets
            // the refinement be watched too; it changes nothing the sampler computes.
            if (o.LivePreviewRefiningPass && !o.PreviewOnly)
            {
                var preview = root[TpPreviewPass1]!.DeepClone().AsObject();
                preview["_meta"] = new JsonObject { ["title"] = "Model Preview Override (pass 2)" };
                root[TpPreviewPass2] = preview;
                Link(root, TpSampler2Guider, "model", TpPreviewPass2, 0);
            }
            SetInput(root, TpPreview, "value", o.PreviewOnly);
            SetInput(root, TpUseFirstPassAudio, "value", o.UseFirstPassAudio);
            if (o.PreviewOnly)
            {
                // A preview decodes pass 1's video and sound: the switches are wired to that side, so the
                // upscaler and the second pass are pruned rather than left for the server to validate and load.
                Collapse(root, TpPreviewVideo, "on_true");
                Collapse(root, TpPreviewAudio, "on_true");
            }

            // ── RTX, encode, sink ─────────────────────────────────────────────
            if (o.UseRtxUpscale)
            {
                SetInput(root, TpRtxSr, "resize_type.scale", o.RtxScale);
                SetInput(root, TpRtxSr, "quality", o.RtxQuality);
            }
            else
            {
                Retarget(root, TpRtxSr, 0, TpDecode, 0);
            }

            var multiplier = Math.Clamp(o.FrameGenMultiplier, 1, 5);
            SetInput(root, TpFrameGenMultiplier, "value", multiplier);
            SetInput(root, TpFrameGen, "mode", o.FrameGenMode);
            // FrameGen off (1×, or any Continue take): its switch's skip side, wired straight through.
            if (multiplier <= 1 || isContinue) Collapse(root, TpFrameGenSwitch, "on_true");

            SetInput(root, TpGrain, "value", Math.Max(0, o.GrainStrength));
            foreach (var lokken in new[] { TpLokkenNew, TpLokkenSource })
            {
                if (root[lokken] == null) continue;
                SetInput(root, lokken, "crf", Math.Clamp(o.Crf, 0, 51));
                SetInput(root, lokken, "encoder_preset", o.EncoderPreset);
            }

            SetInput(root, TpPrefixFull, "value", outputPrefix);
            SetInput(root, TpPrefixPreview, "value", outputPrefix);

            return PruneToOutputs(root.ToJsonString(), new[] { TpSink }, out pruned);
        }

        /// <summary>Replaces a ComfySwitchNode by the source of one of its inputs: everything that read the
        /// switch reads that source instead, and the prune takes the switch and its other side.</summary>
        private static void Collapse(JsonObject root, string switchId, string keepInput)
        {
            if (root[switchId]?["inputs"]?[keepInput] is not JsonArray link || link.Count != 2)
                throw new Exception($"Workflow node '{switchId}' has no '{keepInput}' — the workflow file no longer matches this tab.");
            Retarget(root, switchId, 0, link[0]!.GetValue<string>(), link[1]!.GetValue<int>());
        }

        // ── Progress, pre-flight, queue ─────────────────────────────────────────────────────────────

        /// <summary>The 2-Pass run, described to the progress tracker: pass 1, the latent upscale, pass 2,
        /// then decode, RTX and the encode.</summary>
        private static (List<ProgressStage> Stages, double LeadUnits, double EstimatedSeconds)
            BuildTwoPassProgressPlan(MiniMaxI2VQueueItem item)
        {
            var o = item.TwoPass!;
            var ratio = o.FirstPassMegapixels / Math.Max(0.1, o.FinalMegapixels);
            var stages = new List<ProgressStage>();
            if (o.PreviewOnly)
            {
                stages.Add(new("Pass 1 (preview)", o.FirstPassSteps, ratio, 3.0));
            }
            else
            {
                stages.Add(new("Pass 1", o.FirstPassSteps, ratio, 2.0));
                stages.Add(new("Pass 2 (refine)", o.UseManualSigmas ? 0 : o.SecondPassSteps, 1.0,
                               3.0 + (o.FrameGenMultiplier > 1 && !item.IsTwoPassContinue ? 2.0 : 0)));
            }
            return (stages, 1.5, 60 + _twoPassSecondsPerGpx * TwoPassGpx(item));
        }

        /// <summary>Final canvas × frames, in billions — what the 2-Pass run time is scaled by.</summary>
        private static double TwoPassGpx(MiniMaxI2VQueueItem item)
        {
            var o = item.TwoPass!;
            var mp = o.PreviewOnly ? o.FirstPassMegapixels : o.FinalMegapixels;
            var (w, h) = H3Canvas.Resolve(item.AspectRatio, mp, TwoPassResolutionMultiple);
            return (double)w * h * UltraFrameCount(o.DurationSeconds) / 1e9;
        }

        private void RecalibrateTwoPassFromRun(MiniMaxI2VQueueItem item, TimeSpan elapsed)
        {
            var gpx = TwoPassGpx(item);
            if (gpx <= 0 || elapsed.TotalSeconds < 60) return;
            var measured = (elapsed.TotalSeconds - 60) / gpx;
            if (measured is < 20 or > 6000) return;
            _twoPassSecondsPerGpx = 0.5 * _twoPassSecondsPerGpx + 0.5 * measured;
            AddLog($"Took {elapsed:hh\\:mm\\:ss} for {gpx:0.00} Gpx — {measured:0} s/Gpx " +
                   $"(2-Pass estimate for the next item: {_twoPassSecondsPerGpx:0} s/Gpx)");
        }

        /// <summary>Decoded frames in GB of float32 RGB host RAM, after RTX and FrameGen. A Continue take also
        /// holds the whole source.</summary>
        private static double TwoPassFrameRamGb(MiniMaxI2VQueueItem item)
        {
            var o = item.TwoPass!;
            var mp = o.PreviewOnly ? o.FirstPassMegapixels : o.FinalMegapixels;
            var (w, h) = H3Canvas.Resolve(item.AspectRatio, mp, TwoPassResolutionMultiple);
            var scale = o.UseRtxUpscale ? o.RtxScale * o.RtxScale : 1.0;
            var frames = (double)UltraFrameCount(o.DurationSeconds);
            if (!item.IsTwoPassContinue) frames *= Math.Clamp(o.FrameGenMultiplier, 1, 5);
            return w * h * scale * frames * 12 / 1e9;
        }

        /// <summary>🌹 2-Pass pre-flight: host RAM for the decoded, upscaled, interpolated frames.</summary>
        private async Task<string?> PreflightTwoPassAsync(MiniMaxI2VQueueItem item, CancellationToken token)
        {
            var frameRamGb = TwoPassFrameRamGb(item);
            var head = await FetchHeadroomAsync(token);
            if (head == null)
            {
                AddLog("Pre-flight: could not read /system_stats — going ahead");
                return null;
            }

            var (ramFree, _, vramFree, vramTotal) = head.Value;
            AddLog($"Pre-flight: server has {ramFree:0.0} GB RAM and {vramFree:0.0} GB of {vramTotal:0.0} GB VRAM " +
                   $"free; this 2-Pass take holds ~{frameRamGb:0.0} GB of frames" +
                   (item.IsTwoPassContinue ? " plus the source video" : string.Empty));

            if (frameRamGb * 2 > ramFree)
            {
                AddLog("Pre-flight: tight on host RAM — unloading models to reclaim it…");
                var freed = await FreeServerMemoryAsync(token);
                if (freed != null) ramFree = freed.Value.RamFreeGb;
                if (frameRamGb * 2 > ramFree)
                    return $"Not enough host RAM: this take needs roughly {frameRamGb * 2:0.0} GB for its frames and the " +
                           $"server has {ramFree:0.0} GB free even after unloading models. Shorten it, lower the final " +
                           "megapixels, or turn RTX upscale / FrameGen down.";
            }
            return null;
        }

        /// <summary>Freezes the 2-Pass half of the form into a queue item.</summary>
        private MiniMaxI2VQueueItem BuildTwoPassQueueItem()
        {
            var options = TwoPass.Clone();
            options.DiffusionModel = NormalizeName(options.DiffusionModel);
            var continueSource = options.ContinueMode && HasContinueSource ? _twoPassContinueSource : null;
            var videos = VideoReferences.Where(v => v.HasFile).ToList();
            return new MiniMaxI2VQueueItem
            {
                Workflow = I2VWorkflow.TwoPass,
                TwoPass = options,
                ReferencePaths = FilledReferences.Take(MaxTwoPassPictures).Select(r => r.Path).ToList(),
                AudioPaths = UltraAudioPaths.ToList(),
                VideoPaths = videos.Select(v => v.Path).ToList(),
                VideoSoundtracks = videos.Select(v => v.HasSoundtrack).ToList(),
                ContinueSourcePath = continueSource,
                Prompt = Prompt.Trim(),
                AspectRatio = TwoPassResolvedAspect,
                Megapixels = options.FinalMegapixels,
                LengthSeconds = (int)Math.Round(options.DurationSeconds),
                Seed = Seed,
                MaxFidelityReferences = options.MaxFidelityReferences,
                // The Ref2VA graph's finishing switches: none of them exist in this one.
                UseLatentUpscale = false,
                UseRtxUpscale = false,
                UseAudioEnhancement = false,
                UseSla = false,
                DiffusionModel = options.DiffusionModel,
                FirstPassSteps = options.FirstPassSteps,
            };
        }

        /// <summary>
        /// Runs one 2-Pass item: uploads its media (and the video it continues), resolves the Turbo LoRA against
        /// the server's loras/, builds the graph and waits for the SaveVideo sink.
        /// </summary>
        private async Task RunTwoPassItemAsync(
            MiniMaxI2VQueueItem item, string json, List<string> uploaded, long runSeed, string runToken, string ts,
            CancellationToken token)
        {
            var o = item.TwoPass!;
            var audios = await UploadMediaAsync(item.AudioPaths, isVideo: false, token);
            var videos = await UploadMediaAsync(item.VideoPaths, isVideo: true, token);

            string? source = null;
            if (item.IsTwoPassContinue)
            {
                if (!File.Exists(item.ContinueSourcePath))
                    throw new FileNotFoundException($"The video to continue is gone: {item.ContinueSourcePath}");
                ProcessingStatus = "Uploading the video to continue...";
                source = await _comfyUIService.UploadVideoAsync(item.ContinueSourcePath!, token);
                if (string.IsNullOrEmpty(source)) throw new Exception("Failed to upload the video to continue.");
                AddLog($"⏩ Continue source uploaded: {source}");
            }

            if (o.UseTurboLora)
            {
                var all = await _comfyUIService.HttpClient.GetLoraFilenamesAsync(token);
                var resolved = ResolveLoraName(all, o.TurboLora);
                if (resolved == null)
                    AddLog($"⚠ Turbo LoRA {o.TurboLora} is not in the server's loras/ — the run will fail to load it.");
                else o.TurboLora = resolved;
            }

            json = BuildTwoPassWorkflow(json, item, uploaded, audios, videos, item.VideoSoundtracks, source,
                                        runSeed, runToken, out var pruned);
            var (w1, h1) = H3Canvas.Resolve(item.AspectRatio, o.FirstPassMegapixels, TwoPassResolutionMultiple);
            AddLog($"Graph prepared: 🌹 ErosMax 2-Pass{(source != null ? " (Continue)" : string.Empty)}, {pruned} node(s) pruned");
            AddLog($"Generating (seed {runSeed}, {o.DurationSeconds:0.#}s = {UltraFrameCount(o.DurationSeconds)} frames, " +
                   $"pass 1 at {w1}×{h1}, {uploaded.Count} picture(s), {videos.Count} video, {audios.Count} audio, " +
                   $"{o.ActiveRefMods.Count} RefMod(s); {TwoPassSamplingSummaryFor(o)})...");

            ProcessingProgress = 8;
            var plan = BuildTwoPassProgressPlan(item);
            _progressTracker.Begin(plan.Stages, plan.LeadUnits, 8, 97, plan.EstimatedSeconds, phase: string.Empty);
            AddLog($"Estimated run time ~{TimeSpan.FromSeconds(plan.EstimatedSeconds):hh\\:mm\\:ss} " +
                   $"({_twoPassSecondsPerGpx:0} s/Gpx learned so far)");

            var local = await SubmitAndRetrieveAsync(json, runToken, TpSink, plan.EstimatedSeconds, token);
            if (local == null || !File.Exists(local))
                throw new Exception("No output video was generated.");

            RecalibrateTwoPassFromRun(item, _progressTracker.Elapsed);
            _progressTracker.Finish(true);
            await PublishResultAsync(item, local, ts,
                $"🌹 2-Pass • {item.AspectRatio} • {o.DurationSeconds:0.#}s • {uploaded.Count} ref" +
                (videos.Count > 0 ? $" + {videos.Count} video" : string.Empty) +
                (audios.Count > 0 ? $" + {audios.Count} audio" : string.Empty) +
                (source != null ? " • continued" : string.Empty));
        }

        /// <summary>The server's spelling of a LoRA asked for by file name: an exact match, or the one file in
        /// any folder with that name.</summary>
        internal static string? ResolveLoraName(IReadOnlyList<string> available, string wanted)
        {
            var w = NormalizeName(wanted);
            var exact = available.FirstOrDefault(a => string.Equals(NormalizeName(a), w, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var file = w.Contains('/') ? w[(w.LastIndexOf('/') + 1)..] : w;
            var matches = available.Where(a =>
            {
                var n = NormalizeName(a);
                return string.Equals(n, file, StringComparison.OrdinalIgnoreCase) ||
                       n.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase);
            }).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }
    }
}
