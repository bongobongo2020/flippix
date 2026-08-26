using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Linux.Models;
using FlipPix.UI.Linux.Services;
// MessageBox is fully qualified below: MsBox.Avalonia contributes a root
// namespace of the same name, so a using-alias would be a CS0576 conflict.
using Application = System.Windows.Application;

namespace FlipPix.UI.Linux.ViewModels.Video
{
    /// <summary>
    /// "MiniMax I2V" tab. Drives <c>h3-minimax-i2v.json</c> — MiniMax H3 in Ref2VA mode, where the
    /// uploaded pictures are <em>references</em> rather than the frame at 0.00s. Up to four go in
    /// (<c>&lt;Picture 1&gt;</c>…<c>&lt;Picture 4&gt;</c>); Analyze turns them plus a draft idea into the
    /// six-field Ref2VA prompt; one submission renders video with synchronized audio.
    ///
    /// <para>H3 renders at most ~15s per pass, so length past that is bought with <b>continuations</b>: a
    /// for-loop inside the same submission takes the tail of the previous pass as a reference clip, adds
    /// it as a guide at frame 0, samples the next pass against its own prompt and seed, and blends the two
    /// back together over an overlap window. The loop's per-iteration prompt and duration are indexed off
    /// the loop counter, which is why there are exactly three continuation slots.</para>
    ///
    /// <para>The graph as authored refined the prompt with an in-graph LLM node pointed at a hardcoded
    /// local server. That is stripped from the copy this tab ships: FlipPix writes the prompt itself
    /// through <see cref="LMStudioService"/> so the user can read and edit it before spending a render,
    /// and the reference-to-video nodes read the prompt primitives directly.</para>
    ///
    /// <para>Every pass renders in three moves rather than one: four sampler steps at a quarter of the
    /// canvas to fix the composition, the MiniMax H3 3D latent upscaler doubling that latent, then three
    /// fixed-sigma steps at full size to put the detail on. The expensive steps are the only ones that
    /// ever see the full canvas, which is what makes it both quicker and sharper than sampling eight
    /// steps at full size and refining afterwards.</para>
    ///
    /// <para>Both halves of the graph — the base pass and the continuation loop — end in their own
    /// VHS_VideoCombine sink, and an OUTPUT_NODE runs whether or not anything downstream wants it. So the
    /// run picks its sink and <see cref="PruneToOutputs"/> deletes everything the sink does not reach:
    /// with no continuations the loop never enters the submitted graph at all.</para>
    /// </summary>
    public partial class MiniMaxI2VViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/h3-minimax/h3-minimax-i2v.json";
        private const string OutputSubfolder = "minimax_i2v";
        private const string SystemPromptFile = "h3-r2va.md";

        /// <summary>What a continuation pass costs against the base pass of the same length, measured.</summary>
        private const double ContinuationFactor = 1.55;

        /// <summary>
        /// The multiple ResolutionSelector rounds the <i>draft</i> canvas to. 32, not 64: the finish pass
        /// scales the draft by exactly <see cref="LatentUpscaleFactor"/>, so a 32-aligned draft doubles to
        /// a 64-aligned finish and the upscaler's own alignment can never disagree with the loop's
        /// <c>round(a * b / 32) * 32</c>. That disagreement — and the token-count mismatch it killed the
        /// loop with — was what forced 64 back when the factor was 1.5.
        /// </summary>
        private const int ResolutionMultiple = 32;

        /// <summary>
        /// How much bigger the finished canvas is than the sampled draft, per side. Must stay an integer:
        /// the loop derives the finish canvas twice — once inside MinimaxH3LatentUpscaler3D and once as
        /// <c>round(draft * factor / 32) * 32</c> for the conditioning latent — and only an integer factor
        /// guarantees the two land on the same number for every aspect ratio.
        /// </summary>
        private const double LatentUpscaleFactor = 2.0;

        /// <summary>Continuation slots the loop's indexed prompt/duration switches can address.</summary>
        public const int MaxContinuations = 3;

        /// <summary>Reference pictures MiniMaxH3ReferenceToVideo is given on this tab.</summary>
        public const int MaxReferences = 4;

        // ── Workflow node ids (locked to h3-minimax-i2v.json) ─────────────────────────────────
        private const string NodeReference0 = "10";            // LoadImage → ref_image_0
        private const string NodeBaseRef2V = "4145:174";       // MiniMaxH3ReferenceToVideo (base pass)
        private const string NodeLoopRef2V = "4146:175";       // MiniMaxH3ReferenceToVideo (continuation)
        private const string NodeBasePrompt = "56";            // PrimitiveStringMultiline
        private const string NodeBaseSeconds = "4145:147";     // easy int → frame-count expression
        private const string NodeBaseSeed = "4145:149";        // RandomNoise
        private const string NodeResolution = "60";            // ResolutionSelector
        private const string NodeLoopStart = "4146:126";       // easy forLoopStart (total = continuations)
        private const string NodeOverlap = "4174:4171";        // CustomCombo → overlap frame count
        private const string NodeSaveSingle = "49";            // VHS_VideoCombine, base pass only
        private const string NodeSaveJoined = "52";            // VHS_VideoCombine, base + continuations

        // Latent upscale: the 2x re-sample that turns the draft into the finished frames. The loop needs
        // the same answer twice — once for its own pass, once to know whether the frames handed to it are
        // already at the finished size.
        private const string NodeBaseDetail = "4145:4220";
        private static readonly string[] NodeLoopDetail = { "4146:4241", "4146:4321" };

        // The two samplers of each pass, and the sigma sources they can be pointed at. With the latent
        // upscale on, the draft runs the first four steps of an unshifted 8-step schedule (1.0 -> 0.5) and
        // the finish runs three fixed sigmas; with it off there is no finish pass at all, so the draft
        // sampler takes the full 8-step shifted schedule and denoises to zero on its own.
        private const string NodeBaseSampler = "4145:140";
        private const string NodeLoopSampler = "4146:92";
        private const string NodeDraftSigmas = "draft_split";      // SplitSigmas.high_sigmas
        private const string NodeBaseFullSigmas = "4145:148";      // BasicScheduler, 8 steps, shifted
        private const string NodeLoopFullSigmas = "4146:104";

        // The 2x factor written into the graph in the three places that have to agree about it.
        private const string NodeBaseUpscaler = "4145:4318";
        private const string NodeLoopUpscaler = "4146:4319";
        private static readonly string[] NodeLoopFinishDims = { "4146:4315", "4146:4316" };

        // SLA: block-sparse attention matching the inference path the lightx2v SLA turbo LoRA was
        // distilled against. It has to sit last on the MODEL wire, feeding the guiders and schedulers
        // directly, so there is one per branch - the base pass and the loop never share a sampler.
        private static readonly string[] NodeSla = { "sla_base", "sla_loop" };

        // Sol-Attn, the same author's earlier general-purpose sparse attention. SLA supersedes it for
        // H3, so this stays off: stacking two approximations bought nothing measurable.
        private const string NodeSparseAttention = "55:3706";

        // The RTX super-resolution and audio-enhancement switches exist on both halves, but only the half
        // that owns the saved sink may run them: on the base pass they would feed the loop upscaled frames
        // and twice-enhanced audio.
        private const string NodeBaseUpscale = "4145:139";
        private const string NodeLoopUpscale = "4146:70";
        private const string NodeBaseAudio = "4145:143";
        private const string NodeLoopAudio = "4146:73";

        private static readonly string[] NodeContinuationPrompts = { "57", "58", "46" };
        private static readonly string[] NodeContinuationSeconds = { "7", "8", "6" };
        private static readonly string[] NodeContinuationSeeds = { "4146:97", "4146:96", "4146:94" };

        /// <summary>Marker the Analyze pass puts between per-continuation prompt blocks.</summary>
        private const string SegmentMarker = "=== SEGMENT";

        // ── Input state ────────────────────────────────────────────────────────
        private string _prompt = string.Empty;
        private string _selectedAspectRatio = H3Canvas.AutoAspect;
        private double _megapixels = 0.7;
        private int _lengthSeconds = 10;
        private long _seed = -1;
        private OverlapOption _overlap;
        private bool _useSla = true;
        // 0.85 rather than the faster 0.90: at 0.90 the benchmark soundtrack came back ~11 dB hotter
        // in RMS at the same peak, i.e. the quiet gaps between hits filled in, and the 300-3400 Hz
        // speech band lost ~18% of its share. 0.85 is also the value lightx2v ships and the one the
        // SLA turbo LoRA was distilled against. 0.90 is one dropdown click away and ~13% faster.
        private double _slaSparsity = 0.85;
        private bool _useSparseAttention;
        private bool _useLatentUpscale = true;
        private bool _useRtxUpscale;
        private bool _useAudioEnhancement = true;
        private bool _maxFidelityReferences;
        private bool _isAnalyzing;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _analyzeCts;

        /// <summary>This tab's own prompt store. Deliberately not the Character tab's: a take here is a
        /// base pass plus segments written against a numbered picture order, which is not what that
        /// tab's entries are.</summary>
        private readonly ScenePromptLibrary _promptLibrary;
        private readonly SemaphoreSlim _promptLibraryLock = new(1, 1);
        private List<ScenePrompt>? _savedPrompts;
        private int _savedPromptCount;

        private readonly ObservableCollection<MiniMaxI2VQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;
        private bool _isProcessingQueue;
        private string _queueStatus = string.Empty;

        private readonly GenerationProgressTracker _progressTracker;
        private string _generationTimer = string.Empty;

        /// <summary>
        /// Seconds this box takes per Gpx of one pass, learned from the runs it has already done.
        /// Only the first minute of a run leans on it — from the first sampler step the estimate is
        /// re-derived from the run itself — but it is what makes the very first ETA a number rather than
        /// a shrug.
        ///
        /// <para>Derived, not measured: the 513 s/Gpx this tab learned under the old scheme was against a
        /// Gpx that counted the 1.5x detail tensor and 17 full-canvas step-equivalents of sampling. The
        /// draft-then-finish scheme does about 4, against a Gpx that is now the finished canvas — which
        /// lands near 270. The first real run replaces it.</para>
        /// </summary>
        private static double _secondsPerGpx = 270;

        private static string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "minimax_i2v_queue.json");

        public MiniMaxI2VViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            _promptLibrary = new ScenePromptLibrary(AddLog, ScenePromptLibrary.FolderFor("minimax-i2v"));
            _overlap = OverlapOptions[1];

            _progressTracker = new GenerationProgressTracker(
                p => OnUiThread(() => ProcessingProgress = p),
                s => OnUiThread(() => ProcessingStatus = s),
                t => OnUiThread(() => GenerationTimer = t));

            for (var slot = 1; slot <= MaxReferences; slot++)
            {
                var reference = new MiniMaxI2VReference(slot);
                reference.Changed += OnReferenceChanged;
                References.Add(reference);
            }

            BrowseReferenceCommand = new RelayCommand<MiniMaxI2VReference>(async r => await BrowseReferenceAsync(r));
            ClearReferenceCommand = new RelayCommand<MiniMaxI2VReference>(r => r?.Clear());
            AddContinuationCommand = new RelayCommand(AddContinuation, () => Continuations.Count < MaxContinuations);
            RemoveContinuationCommand = new RelayCommand<MiniMaxI2VSegment>(RemoveContinuation);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            GenerateCommand = new RelayCommand(AddToQueue, () => CanGenerate);
            CancelCommand = new RelayCommand(StopQueue, () => IsProcessing || IsProcessingQueue);
            RemoveQueueItemCommand = new RelayCommand<MiniMaxI2VQueueItem>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => HasQueueItems);
            StartQueueCommand = new RelayCommand(() => _ = ProcessQueueAsync(),
                                                 () => HasPendingItems && !IsProcessingQueue);
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(ReprocessAllFailed, () => HasFailedItems);

            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            RandomSeedCommand = new RelayCommand(() => Seed = System.Random.Shared.NextInt64(0, int.MaxValue));
            OpenPromptLibraryCommand = new RelayCommand(async () => await OpenPromptLibraryAsync());
            SavePromptCommand = new RelayCommand(async () => await SaveCurrentPromptAsync(manual: true),
                () => !string.IsNullOrWhiteSpace(Prompt));

            // Only once every command exists: restoring a queue notifies all of them.
            _queue.CollectionChanged += (_, _) => UpdateQueueStatus();
            LoadQueueFromFile();

            // Reads the index off the UI thread; the button caption picks up the count when it lands.
            _ = PrimePromptLibraryAsync();

            AddLog("MiniMax I2V initialized");
        }

        #region Commands

        public RelayCommand<MiniMaxI2VReference> BrowseReferenceCommand { get; }
        public RelayCommand<MiniMaxI2VReference> ClearReferenceCommand { get; }
        public RelayCommand AddContinuationCommand { get; }
        public RelayCommand<MiniMaxI2VSegment> RemoveContinuationCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand RandomSeedCommand { get; }
        public RelayCommand OpenPromptLibraryCommand { get; }
        public RelayCommand SavePromptCommand { get; }
        public RelayCommand<MiniMaxI2VQueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StartQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }

        #endregion

        #region References

        public ObservableCollection<MiniMaxI2VReference> References { get; } = new();

        /// <summary>Filled slots in slot order — the &lt;Picture N&gt; numbering the prompt refers to.</summary>
        public IReadOnlyList<MiniMaxI2VReference> FilledReferences =>
            References.Where(r => r.HasImage).ToList();

        public bool HasReference => References.Count > 0 && References[0].HasImage;

        /// <summary>
        /// Lets the Video Generator hand a picture straight over from the Image Generator: it lands in
        /// slot 1, the slot the shot is built around.
        /// </summary>
        public string PrimaryReferencePath
        {
            get => References.Count > 0 ? References[0].Path : string.Empty;
            set { if (References.Count > 0) References[0].Path = value; }
        }

        private void OnReferenceChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(FilledReferences));
            OnPropertyChanged(nameof(HasReference));
            OnPropertyChanged(nameof(ResolvedAspectRatio));
            OnPropertyChanged(nameof(ReferenceSummary));
            OnPropertyChanged(nameof(PrimaryReferencePath));
            OnCanExecuteChanged();
        }

        public string ReferenceSummary
        {
            get
            {
                var count = FilledReferences.Count;
                return count switch
                {
                    0 => "No reference picture yet",
                    1 => "1 reference · <Picture 1>",
                    _ => $"{count} references · <Picture 1>–<Picture {count}>"
                };
            }
        }

        private async Task BrowseReferenceAsync(MiniMaxI2VReference? reference)
        {
            if (reference == null) return;

            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                $"Select reference picture {reference.Slot}",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: "minimaxi2v.reference");

            if (path == null) return;
            reference.Path = path;
            AddLog($"<Picture {reference.Slot}>: {Path.GetFileName(path)}");
        }

        #endregion

        #region Prompt and continuations

        /// <summary>The base pass's Ref2VA prompt — the first ~5–15 seconds of the take.</summary>
        public string Prompt
        {
            get => _prompt;
            set { if (_prompt != value) { _prompt = value; OnPropertyChanged(); OnCanExecuteChanged(); } }
        }

        public ObservableCollection<MiniMaxI2VSegment> Continuations { get; } = new();

        private void AddContinuation()
        {
            if (Continuations.Count >= MaxContinuations) return;
            var segment = new MiniMaxI2VSegment(Continuations.Count + 1) { Seconds = 10 };
            segment.Changed += (_, _) => { OnPropertyChanged(nameof(TotalLengthSummary)); RaiseMemoryEstimate(); };
            // Changed only fires for Seconds. Without this, typing a continuation prompt satisfied
            // CanGenerate but never re-raised it, so the button stayed greyed out on a complete form.
            segment.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MiniMaxI2VSegment.Prompt)) OnCanExecuteChanged();
            };
            Continuations.Add(segment);
            AfterContinuationsChanged();
        }

        private void RemoveContinuation(MiniMaxI2VSegment? segment)
        {
            if (segment == null || !Continuations.Remove(segment)) return;
            for (var i = 0; i < Continuations.Count; i++) Continuations[i].Index = i + 1;
            AfterContinuationsChanged();
        }

        private void AfterContinuationsChanged()
        {
            OnPropertyChanged(nameof(HasContinuations));
            OnPropertyChanged(nameof(TotalLengthSummary));
            RaiseMemoryEstimate();
            AddContinuationCommand.NotifyCanExecuteChanged();
            OnCanExecuteChanged();
        }

        public bool HasContinuations => Continuations.Count > 0;

        /// <summary>
        /// Frames the base pass renders. H3 snaps length to the 17k+5 grid at 24 fps, and denoises the
        /// whole clip jointly — which is why seconds, not resolution, is usually what runs the card out
        /// of memory.
        /// </summary>
        private static int FrameCount(int seconds)
        {
            var f = Math.Max(5, seconds * 24);
            return f + (5 - (f % 17)) % 17;
        }

        /// <summary>
        /// Rough size of the largest tensor a single pass has to hold, in billions of pixel-positions:
        /// finished canvas area × frames. The finish sampler is what sets it — the draft runs at a quarter
        /// of the area and is never the pass's peak.
        ///
        /// <para>Passes do not add up: continuations run one after another, so the peak is set by the
        /// single largest pass, not by the total.</para>
        /// </summary>
        public double EstimatedPeakGpx
        {
            get
            {
                var area = SampledArea(ResolvedAspectRatio, Megapixels, UseLatentUpscale);
                var peak = FrameCount(LengthSeconds);
                foreach (var continuation in Continuations)
                    peak = Math.Max(peak, FrameCount(continuation.Seconds) + Overlap.Frames);
                return area * peak / 1e9;
            }
        }

        /// <summary>
        /// Decoded frames the continuation loop holds in <b>system</b> RAM, in GB.
        ///
        /// <para>A second, independent limit from <see cref="EstimatedPeakGpx"/>, and the one that actually
        /// killed the server: the loop concatenates every pass's frames into one growing batch, so this
        /// scales with the take's <i>total</i> length, not the longest pass. VRAM can be comfortable while
        /// this is fatal.</para>
        ///
        /// <para>Frames are float32 RGB — 12 bytes a pixel — at the finished canvas. The blend step holds
        /// source, new and result at once, so the true peak runs well above this figure, and roughly 40 GB
        /// of staged model weights sit underneath it.</para>
        /// </summary>
        public double EstimatedFrameRamGb
        {
            get
            {
                var area = SampledArea(ResolvedAspectRatio, Megapixels, UseLatentUpscale);
                var totalFrames = FrameCount(LengthSeconds)
                                  + Continuations.Sum(c => FrameCount(c.Seconds) + Overlap.Frames);
                return area * totalFrames * 12 / 1e9;
            }
        }

        /// <summary>
        /// The worse of the two limits, named. Calibrated on real outcomes on this box:
        /// VRAM — 0.40 Gpx completed, 0.57 Gpx raised a CUDA OOM.
        /// Host RAM — an 8.1 GB batch completed; a 18.4 GB batch took the whole ComfyUI process
        /// out via the kernel OOM killer at 95 GB RSS, losing the run and everything queued behind it.
        /// </summary>
        public string MemoryWarning
        {
            get
            {
                var gpx = EstimatedPeakGpx;
                var ram = EstimatedFrameRamGb;
                var head = $"~{gpx:0.00} Gpx/pass · ~{ram:0.0} GB frames";

                if (ram > FrameRamOverLimitGb)
                    return $"⚠ {head} — this size has OOM-killed the ComfyUI server. Shorten the take, " +
                           $"drop continuations, or drop the quality.";
                if (gpx > VramOverLimitGpx)
                    return $"⚠ {head} — this has run out of VRAM on 24 GB.";
                if (ram > FrameRamRiskyGb)
                    return $"{head} — host RAM is getting close; the whole server dies if it tips over.";
                if (gpx > VramRiskyGpx)
                    return $"{head} — close to the VRAM limit on 24 GB.";
                return head;
            }
        }

        /// <summary>Where the form starts warning, and where it calls it over the line. Both are the
        /// 24 GB card's measured limit (see <see cref="GpxPerVramGb"/>), less a margin.</summary>
        private const double VramRiskyGpx = 0.40;
        private const double VramOverLimitGpx = 0.435;

        /// <summary>
        /// Where the concatenated frame batch starts to threaten the host, in GB, on the same corrected
        /// basis as <see cref="EstimatedFrameRamGb"/>. Measured on 21 Aug: a 3-pass 3:2 take (15.0 GB)
        /// completed, while a 4-pass 1:1 take (19.4 GB) had the kernel OOM killer take the whole ComfyUI
        /// process out mid-run, losing that job and everything queued behind it.
        /// </summary>
        private const double FrameRamRiskyGb = 13.0;
        private const double FrameRamOverLimitGb = 17.5;

        public bool IsMemoryRisky => EstimatedPeakGpx > VramRiskyGpx || EstimatedFrameRamGb > FrameRamRiskyGb;
        public bool IsMemoryOverLimit => EstimatedPeakGpx > VramOverLimitGpx
                                         || EstimatedFrameRamGb > FrameRamOverLimitGb;

        private void RaiseMemoryEstimate()
        {
            OnPropertyChanged(nameof(EstimatedPeakGpx));
            OnPropertyChanged(nameof(EstimatedFrameRamGb));
            OnPropertyChanged(nameof(MemoryWarning));
            OnPropertyChanged(nameof(IsMemoryRisky));
            OnPropertyChanged(nameof(IsMemoryOverLimit));
        }

        public string TotalLengthSummary
        {
            get
            {
                var total = LengthSeconds + Continuations.Sum(c => c.Seconds);
                if (Continuations.Count == 0) return $"≈ {total}s in one pass";
                var passes = Continuations.Count + 1;
                return $"≈ {total}s over {passes} passes";
            }
        }

        #endregion

        #region Settings

        public IReadOnlyList<string> AspectRatioOptions { get; } =
            new[] { H3Canvas.AutoAspect }.Concat(H3Canvas.AspectRatios.Select(a => a.Option)).ToList();

        public string SelectedAspectRatio
        {
            get => _selectedAspectRatio;
            set
            {
                if (_selectedAspectRatio == value) return;
                _selectedAspectRatio = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResolvedAspectRatio));
            }
        }

        /// <summary>The aspect actually sent to ComfyUI — the picked one, or slot 1's closest match.</summary>
        public string ResolvedAspectRatio
        {
            get
            {
                if (SelectedAspectRatio != H3Canvas.AutoAspect) return SelectedAspectRatio;
                var primary = References.FirstOrDefault(r => r.HasImage);
                if (primary == null) return "16:9 (Widescreen)";
                var (w, h) = MiniMaxI2VReference.MeasurePixels(primary.Path);
                return H3Canvas.ClosestAspectRatio(w, h);
            }
        }

        /// <summary>
        /// The size of the <b>finished</b> frames, not of the sampling. With the latent upscale on, the
        /// draft is sampled at a quarter of this and doubled; H3's native canvas is a 768px short edge, so
        /// 0.7 MP stays the balanced default and 1.5 MP is reachable now that only three steps ever run at
        /// full size.
        /// </summary>
        public IReadOnlyList<MegapixelOption> MegapixelOptions { get; } = new[]
        {
            // Sizes are what this graph really produces at 16:9 — the draft ResolutionSelector returns for
            // a quarter of the target, doubled — not the megapixel target rounded by eye. Other aspects
            // land within a few percent of the same pixel count, except 1:1, which comes out ~5% smaller.
            new MegapixelOption(0.4, "0.4 MP — fast draft (832×512)"),
            new MegapixelOption(0.7, "0.7 MP — balanced (1152×640)"),
            new MegapixelOption(1.0, "1.0 MP — full quality (1344×768)"),
            new MegapixelOption(1.5, "1.5 MP — high (1664×960)"),
        };

        public double Megapixels
        {
            get => _megapixels;
            set
            {
                if (Math.Abs(_megapixels - value) <= 0.0001) return;
                _megapixels = value;
                OnPropertyChanged();
                RaiseMemoryEstimate();
            }
        }

        /// <summary>Base-pass length in whole seconds. H3's trained range is ~124–362 frames at 24 fps.</summary>
        public int LengthSeconds
        {
            get => _lengthSeconds;
            set
            {
                var clamped = Math.Clamp(value, 5, 15);
                if (_lengthSeconds == clamped) return;
                _lengthSeconds = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalLengthSummary));
                RaiseMemoryEstimate();
            }
        }

        public long Seed
        {
            get => _seed;
            set { if (_seed != value) { _seed = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// How many frames of the previous pass a continuation re-generates and blends over. The options
        /// are H3's own valid clip lengths (17k + 5), because the overlap is handed to the model as a
        /// reference clip: more overlap costs render time and buys a less visible join.
        /// </summary>
        public IReadOnlyList<OverlapOption> OverlapOptions { get; } = new[]
        {
            new OverlapOption(5,  "5 frames — hard cut (0.2s)"),
            new OverlapOption(22, "22 frames — default blend (0.9s)"),
            new OverlapOption(39, "39 frames — smooth (1.6s)"),
            new OverlapOption(56, "56 frames — longest (2.3s)"),
        };

        public OverlapOption Overlap
        {
            get => _overlap;
            set { if (_overlap != value && value != null) { _overlap = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Block-sparse attention for H3. Each query block is scored against every key block with one
        /// pooled matmul and only the top slice is actually attended, which is the inference path the
        /// lightx2v SLA turbo LoRA was distilled for. Long sequences benefit most; anything under the
        /// kernel's minimum sequence length falls back to dense on its own.
        /// </summary>
        public bool UseSla
        {
            get => _useSla;
            set { if (_useSla != value) { _useSla = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Fraction of key blocks skipped. 0.85 is what lightx2v ships and what the turbo LoRA was
        /// distilled against; 0.90 is the author's validated value. Below ~0.60 the kernel is slower
        /// than dense attention, so a low setting is a loss rather than a safe fallback.
        /// </summary>
        public IReadOnlyList<SlaSparsityOption> SlaSparsityOptions { get; } = new[]
        {
            new SlaSparsityOption(0.80, "0.80 - conservative"),
            new SlaSparsityOption(0.85, "0.85 - lightx2v default"),
            new SlaSparsityOption(0.90, "0.90 - validated, ~15% faster"),
            new SlaSparsityOption(0.95, "0.95 - maximum"),
        };

        public double SlaSparsity
        {
            get => _slaSparsity;
            set { if (Math.Abs(_slaSparsity - value) > 0.0001) { _slaSparsity = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Routes the model through Sol-Attn's sparse attention instead of the dense backend. H3 denoises
        /// the whole clip in one pass, so attention cost grows with the square of frames × canvas area —
        /// which is why a 10s clip costs far more than twice a 5s one. The patch skips the first 20% and
        /// last 10% of steps, where the layout and the final detail are decided.
        /// </summary>
        public bool UseSparseAttention
        {
            get => _useSparseAttention;
            set { if (_useSparseAttention != value) { _useSparseAttention = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// The draft-then-finish scheme: four sampler steps at half the width and half the height, a 2×
        /// pass through the MiniMax H3 3D latent upscaler, then three fixed-sigma steps at the finished
        /// size. Off, the pass instead denoises the full canvas over eight steps in one go — slower, and
        /// softer, but it is the fallback if the upscaler ever misbehaves.
        /// </summary>
        public bool UseLatentUpscale
        {
            get => _useLatentUpscale;
            set
            {
                if (_useLatentUpscale == value) return;
                _useLatentUpscale = value;
                OnPropertyChanged();
                RaiseMemoryEstimate();
            }
        }

        /// <summary>RTX Video Super Resolution 2× on the finished frames — driver-side, not a sampling pass.</summary>
        public bool UseRtxUpscale
        {
            get => _useRtxUpscale;
            set { if (_useRtxUpscale != value) { _useRtxUpscale = value; OnPropertyChanged(); } }
        }

        public bool UseAudioEnhancement
        {
            get => _useAudioEnhancement;
            set { if (_useAudioEnhancement != value) { _useAudioEnhancement = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Switches the reference pipeline from 'match' (references scaled to the generation's pixel area)
        /// to 'max' (2048px short edge). Reference tokens ride through every sampling step, so this is
        /// several times slower — it buys identity fidelity, nothing else.
        /// </summary>
        public bool MaxFidelityReferences
        {
            get => _maxFidelityReferences;
            set { if (_maxFidelityReferences != value) { _maxFidelityReferences = value; OnPropertyChanged(); } }
        }

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (_isAnalyzing == value) return;
                _isAnalyzing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanAnalyze));
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Deliberately not gated on <see cref="VideoProcessingBaseViewModel.IsProcessing"/>. Analyze
        /// talks to the llama-server, which is a different machine from ComfyUI, so writing the next
        /// prompt costs the running render nothing. The in-flight job already has its prompt baked into
        /// the submitted graph, so changing this one cannot disturb it either.
        /// </summary>
        public bool CanAnalyze => HasReference && !IsAnalyzing;

        /// <summary>
        /// Whether the form describes a runnable job — deliberately not whether the GPU is free. Enqueuing
        /// is a local operation, so a render already in flight must not block it; that is the whole point
        /// of the queue.
        /// </summary>
        public bool CanGenerate =>
            HasReference && !string.IsNullOrWhiteSpace(Prompt) && !IsAnalyzing &&
            Continuations.All(c => !string.IsNullOrWhiteSpace(c.Prompt));

        /// <summary>
        /// Why Add to Queue is disabled, or empty when it is not. A greyed-out button with four possible
        /// causes and no stated reason is a dead end for the user — an empty continuation box in
        /// particular looks like a complete form.
        /// </summary>
        public string GenerateBlockedReason
        {
            get
            {
                if (!HasReference) return "Add a picture to slot 1 (Picture 1) to enable this.";
                if (string.IsNullOrWhiteSpace(Prompt)) return "Segment 1 needs a prompt — press Analyze, or write one.";
                if (IsAnalyzing) return "Waiting for Analyze to finish…";
                var empty = Continuations
                    .Select((c, i) => (c, i))
                    .Where(t => string.IsNullOrWhiteSpace(t.c.Prompt))
                    .Select(t => (t.i + 1).ToString())
                    .ToList();
                if (empty.Count > 0)
                    return empty.Count == 1
                        ? $"Continuation {empty[0]} has no prompt. Analyze writes one per segment only when the reply carries the segment markers — fill it in or remove it."
                        : $"Continuations {string.Join(", ", empty)} have no prompt — fill them in or remove them.";
                return string.Empty;
            }
        }

        #endregion

        #region Analysis (references → Ref2VA prompt)

        private async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;

            try
            {
                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);

                var models = await _lmStudioService.GetAvailableModelsAsync(token);
                var model = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(model) && models.Count > 0)
                    model = models[0].Id ?? models[0].Name ?? string.Empty;
                if (string.IsNullOrEmpty(model))
                {
                    System.Windows.MessageBox.Show("No LM Studio / llama-server model available. Ensure the server is running and a model is loaded.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", SystemPromptFile);
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"System prompt not found: {promptFilePath}");
                var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

                var pictures = FilledReferences;
                AddLog($"Writing the Ref2VA prompt — {pictures.Count} picture(s) → {_lmStudioService.DescribeTarget(model)}");

                // Captured before the reply lands: ApplyAnalyzedPrompt overwrites the box with segment 1,
                // and the repair pass needs the user's original idea to have anything to advance toward.
                var draft = Prompt.Trim();

                var result = await _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                    model,
                    pictures.Select(p => p.Path).ToList(),
                    BuildAnalyzeRequest(pictures),
                    systemPrompt,
                    maxTokens: 4000,
                    cancellationToken: token);

                ApplyAnalyzedPrompt(CleanLLMOutput(result));
                await RepairMissingSegmentsAsync(model, systemPrompt, pictures, draft, token);

                // Filed as soon as it exists, not only when it renders: a prompt worth keeping is often
                // one the user reads, edits and never queues.
                await SaveCurrentPromptAsync(manual: false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR during analysis: {ex.Message}");
                System.Windows.MessageBox.Show($"Analysis failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
            }
        }

        /// <summary>
        /// The user message: what each picture is, the draft idea, and — when the take is continued — the
        /// per-segment durations the model has to pace its beats against.
        /// </summary>
        private string BuildAnalyzeRequest(IReadOnlyList<MiniMaxI2VReference> pictures)
        {
            var lines = new List<string>
            {
                $"You are given {pictures.Count} reference picture(s), in order:"
            };
            for (var i = 0; i < pictures.Count; i++)
                lines.Add($"  <Picture {i + 1}> — {Path.GetFileNameWithoutExtension(pictures[i].Path)}");

            lines.Add(string.Empty);
            if (Continuations.Count == 0)
            {
                lines.Add($"Write ONE segment. Target duration: {LengthSeconds} seconds.");
            }
            else
            {
                lines.Add($"Write {Continuations.Count + 1} segments, in order, separated by the segment markers:");
                lines.Add($"  Segment 1 — {LengthSeconds} seconds.");
                for (var i = 0; i < Continuations.Count; i++)
                    lines.Add($"  Segment {i + 2} — {Continuations[i].Seconds} seconds, continuing directly out of segment {i + 1}.");
            }

            lines.Add(string.Empty);
            lines.Add("Draft idea from the user:");
            lines.Add(string.IsNullOrWhiteSpace(Prompt)
                ? "(none — build a single natural beat out of what the pictures show, and add nothing beyond it)"
                : Prompt.Trim());

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Splits the reply on the segment markers and drops the blocks into the base prompt and the
        /// continuation slots. A reply with no markers is treated as the base prompt alone, so a model
        /// that ignored the segment instruction never silently blanks a continuation.
        /// </summary>
        private void ApplyAnalyzedPrompt(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply))
            {
                AddLog("WARNING: Analysis returned an empty result");
                return;
            }

            var blocks = SplitSegments(reply);
            Prompt = blocks[0];
            AddLog($"Segment 1 prompt written ({blocks[0].Length} chars)");

            for (var i = 0; i < Continuations.Count; i++)
            {
                if (i + 1 >= blocks.Count)
                {
                    AddLog($"WARNING: no segment {i + 2} in the reply — continuation {i + 1} is still empty");
                    continue;
                }
                Continuations[i].Prompt = blocks[i + 1];
                AddLog($"Segment {i + 2} prompt written ({blocks[i + 1].Length} chars)");
            }

            OnCanExecuteChanged();
        }

        /// <summary>
        /// Re-asks for any continuation the first reply left empty, one at a time.
        ///
        /// <para>A single reply carrying every segment is cheaper and keeps the take coherent, so it stays
        /// the first attempt — but whether the model emits the <c>=== SEGMENT n ===</c> markers is sampling
        /// luck, and the same prompt and model produce them on one run and not the next. Rather than hope,
        /// each missing segment is requested on its own, with the segment before it supplied as context so
        /// the continuation still picks up where its predecessor ended.</para>
        ///
        /// <para>Deliberately sequential: segment 3's context is segment 2, which may itself have just been
        /// repaired. Running these in parallel would hand segment 3 an empty predecessor.</para>
        /// </summary>
        private async Task RepairMissingSegmentsAsync(
            string model, string systemPrompt, IReadOnlyList<MiniMaxI2VReference> pictures,
            string draft, CancellationToken token)
        {
            if (Continuations.All(c => !string.IsNullOrWhiteSpace(c.Prompt))) return;

            var missing = Continuations.Count(c => string.IsNullOrWhiteSpace(c.Prompt));
            AddLog($"{missing} continuation(s) came back without a segment marker — asking for them one at a time");

            foreach (var segment in Continuations)
            {
                if (!string.IsNullOrWhiteSpace(segment.Prompt)) continue;
                token.ThrowIfCancellationRequested();

                var previous = segment.Index <= 1 ? Prompt : Continuations[segment.Index - 2].Prompt;
                if (string.IsNullOrWhiteSpace(previous))
                {
                    AddLog($"WARNING: continuation {segment.Index} has no preceding segment to continue from — skipped");
                    continue;
                }

                var reply = await _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                    model,
                    pictures.Select(p => p.Path).ToList(),
                    BuildRepairRequest(pictures, segment, previous, draft),
                    systemPrompt,
                    maxTokens: 2500,
                    cancellationToken: token);

                // The reply should be one segment, but a model that ignored "no marker" would wrap it —
                // take the first block either way.
                var text = SplitSegments(CleanLLMOutput(reply)).FirstOrDefault() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    AddLog($"WARNING: continuation {segment.Index} still came back empty");
                    continue;
                }

                segment.Prompt = text;
                AddLog($"Continuation {segment.Index} written on retry ({text.Length} chars)");
            }

            OnCanExecuteChanged();
        }

        /// <summary>
        /// Asks for exactly one continuation.
        ///
        /// <para>The user's draft is supplied alongside the preceding segment, and that is not optional.
        /// The Ref2VA system prompt forbids inventing action, so a request carrying only the previous
        /// segment leaves the model nothing it is permitted to write — and it answers by restating that
        /// segment almost verbatim. The draft is what makes advancing the action a compliant answer.</para>
        /// </summary>
        private string BuildRepairRequest(
            IReadOnlyList<MiniMaxI2VReference> pictures, MiniMaxI2VSegment segment,
            string previous, string draft)
        {
            var lines = new List<string>
            {
                $"You are given {pictures.Count} reference picture(s), in order:"
            };
            for (var i = 0; i < pictures.Count; i++)
                lines.Add($"  <Picture {i + 1}> — {Path.GetFileNameWithoutExtension(pictures[i].Path)}");

            lines.Add(string.Empty);
            lines.Add("The user's idea for the WHOLE take:");
            lines.Add(string.IsNullOrWhiteSpace(draft)
                ? "(none given — carry the action forward naturally from the segment below, one new beat only)"
                : draft);

            lines.Add(string.Empty);
            lines.Add($"The take runs as {Continuations.Count + 1} segments of "
                      + $"{LengthSeconds}s + {string.Join("s + ", Continuations.Select(c => c.Seconds))}s.");
            lines.Add($"Write ONLY segment {segment.Index + 1}, lasting {segment.Seconds} seconds — the part of "
                      + "the user's idea that falls in that stretch, and nothing from any other segment.");
            lines.Add("Output the six Ref2VA fields exactly once. Do NOT write a segment marker line.");
            lines.Add("Its first interval restarts at 0.0s — timestamps are local to this segment, not cumulative.");
            lines.Add("Re-establish the subjects by their <Subject N> bindings, and keep wardrobe, setting and "
                      + "lighting identical so nothing drifts across the join.");

            lines.Add(string.Empty);
            lines.Add("The segment immediately before yours is below. It is CONTEXT ONLY — your segment starts "
                      + "where it ends and must carry the action forward. Do not repeat its events, do not "
                      + "re-describe its beat, and do not copy its detailed_description:");
            lines.Add(previous.Trim());

            return string.Join("\n", lines);
        }

        /// <summary>Splits on any line whose first non-space run is the segment marker.</summary>
        internal static List<string> SplitSegments(string reply)
        {
            var blocks = new List<string>();
            var current = new List<string>();

            foreach (var line in reply.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.TrimStart().StartsWith(SegmentMarker, StringComparison.OrdinalIgnoreCase))
                {
                    blocks.Add(string.Join("\n", current).Trim());
                    current.Clear();
                    continue;
                }
                current.Add(line);
            }
            blocks.Add(string.Join("\n", current).Trim());

            return blocks.Where(b => b.Length > 0).DefaultIfEmpty(reply.Trim()).ToList();
        }

        #endregion

        #region Queue

        public ObservableCollection<MiniMaxI2VQueueItem> Queue => _queue;

        public bool HasQueueItems => _queue.Count > 0;
        public bool HasPendingItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Pending);
        public bool HasFailedItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        /// <summary>True while the drain loop is alive — one ComfyUI submission at a time.</summary>
        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            private set
            {
                if (_isProcessingQueue == value) return;
                _isProcessingQueue = value;
                OnPropertyChanged();
                OnCanExecuteChanged();
            }
        }

        public string QueueStatus
        {
            get => _queueStatus;
            private set { if (_queueStatus != value) { _queueStatus = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// "⏱ 04:12 · ~10:18 left" while a render runs, then "✓ 14:51" once it lands. Sits beside the
        /// percentage, because on a job this long the percentage alone says nothing about when to come back.
        /// </summary>
        public string GenerationTimer
        {
            get => _generationTimer;
            private set { if (_generationTimer != value) { _generationTimer = value; OnPropertyChanged(); } }
        }

        private static void OnUiThread(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

        /// <summary>
        /// Freezes the form into one queue item and starts the drain loop if it is not already running.
        ///
        /// <para>Every setting is copied, not referenced. That is the whole reason the queue exists: the
        /// form stays editable while jobs drain, so a queued item that re-read the sliders at submit time
        /// would silently become a different job than the one that was queued.</para>
        /// </summary>
        private void AddToQueue()
        {
            if (!CanGenerate) return;

            // A host-RAM blowout does not fail one job — the kernel kills ComfyUI, which loses whatever
            // was rendering and every item queued behind it. Worth a confirm rather than a log line.
            if (IsMemoryOverLimit)
            {
                var proceed = System.Windows.MessageBox.Show(
                    $"{MemoryWarning}\n\n" +
                    "The continuation loop keeps every pass's frames in system RAM at once, so a long " +
                    "take can exhaust the server rather than just this job — and that takes down the " +
                    "whole queue with it.\n\n" +
                    "Shorten the take, remove continuations, lower the quality, or turn off the detail " +
                    "pass.\n\nQueue it anyway?",
                    "Likely to run out of memory",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (proceed != MessageBoxResult.Yes) return;
            }

            var item = new MiniMaxI2VQueueItem
            {
                ReferencePaths = FilledReferences.Select(r => r.Path).ToList(),
                Prompt = Prompt.Trim(),
                ContinuationPrompts = Continuations.Select(c => c.Prompt.Trim()).ToList(),
                ContinuationSeconds = Continuations.Select(c => c.Seconds).ToList(),
                AspectRatio = ResolvedAspectRatio,
                Megapixels = Megapixels,
                LengthSeconds = LengthSeconds,
                Seed = Seed,
                OverlapFrames = Overlap.Frames,
                UseSla = UseSla,
                SlaSparsity = SlaSparsity,
                UseSparseAttention = UseSparseAttention,
                UseLatentUpscale = UseLatentUpscale,
                UseRtxUpscale = UseRtxUpscale,
                UseAudioEnhancement = UseAudioEnhancement,
                MaxFidelityReferences = MaxFidelityReferences,
            };

            _queue.Add(item);
            AddLog($"Queued: {item.DisplayText}");
            UpdateQueueStatus();
            SaveQueueToFile();

            if (!IsProcessingQueue) _ = ProcessQueueAsync();
        }

        private void RemoveQueueItem(MiniMaxI2VQueueItem? item)
        {
            // A Processing item is mid-submission; removing it would orphan the run, not stop it.
            if (item == null || item.ItemStatus == QueueItemStatus.Processing) return;
            _queue.Remove(item);
            SaveQueueToFile();
            UpdateQueueStatus();
        }

        private void ClearQueue()
        {
            _queueCts?.Cancel();
            _queue.Clear();
            SaveQueueToFile();
            UpdateQueueStatus();
            AddLog("Queue cleared");
        }

        private void StopQueue() => _queueCts?.Cancel();

        private void ReprocessAllFailed()
        {
            var failed = _queue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (failed.Count == 0) return;
            foreach (var item in failed)
            {
                item.ItemStatus = QueueItemStatus.Pending;
                item.ErrorMessage = null;
            }
            UpdateQueueStatus();
            SaveQueueToFile();
            if (!IsProcessingQueue) _ = ProcessQueueAsync();
        }

        private void UpdateQueueStatus()
        {
            var pending = _queue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
            var running = _queue.Count(x => x.ItemStatus == QueueItemStatus.Processing);
            var done = _queue.Count(x => x.ItemStatus == QueueItemStatus.Completed);
            var failed = _queue.Count(x => x.ItemStatus == QueueItemStatus.Failed);
            QueueStatus = _queue.Count == 0
                ? string.Empty
                : $"{pending} pending • {running} running • {done} done • {failed} failed";

            OnPropertyChanged(nameof(HasQueueItems));
            OnPropertyChanged(nameof(HasPendingItems));
            OnPropertyChanged(nameof(HasFailedItems));
            OnCanExecuteChanged();
        }

        /// <summary>What the server has free right now, in GB. Null when it could not be read.</summary>
        private async Task<(double RamFreeGb, double VramFreeGb, double VramTotalGb)?> FetchHeadroomAsync(
            CancellationToken token)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var json = await http.GetStringAsync($"{GetComfyUIBaseUrl().TrimEnd('/')}/system_stats", token);
                var root = JsonNode.Parse(json)?.AsObject();
                if (root == null) return null;

                var ram = root["system"]?["ram_free"]?.GetValue<double>() ?? 0;
                double vramFree = 0, vramTotal = 0;
                if (root["devices"] is JsonArray devices && devices.Count > 0)
                {
                    // The largest device — the same choice App.xaml.cs makes when it detects the tier.
                    foreach (var d in devices)
                    {
                        var total = d?["vram_total"]?.GetValue<double>() ?? 0;
                        if (total <= vramTotal) continue;
                        vramTotal = total;
                        vramFree = d?["vram_free"]?.GetValue<double>() ?? 0;
                    }
                }
                const double gb = 1024 * 1024 * 1024;
                return (ram / gb, vramFree / gb, vramTotal / gb);
            }
            catch { return null; }
        }

        /// <summary>
        /// Asks ComfyUI to unload models and release cached memory, then reports the new headroom.
        ///
        /// <para>Most of the host RAM in use is staged model weights — a 15 GB text encoder and a 20 GB
        /// diffusion model kept warm for fast swapping. They are reloaded on demand, so dropping them
        /// costs about a minute of load time and buys back tens of gigabytes. That is a good trade
        /// against a run that would otherwise take the whole server down half an hour in.</para>
        ///
        /// <para>Only ever called with the workflow-coordinator lease held: unloading models underneath
        /// another tab's running job would break it.</para>
        /// </summary>
        private async Task<(double RamFreeGb, double VramFreeGb, double VramTotalGb)?> FreeServerMemoryAsync(
            CancellationToken token)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                var body = new StringContent("{\"unload_models\":true,\"free_memory\":true}",
                                             System.Text.Encoding.UTF8, "application/json");
                var response = await http.PostAsync($"{GetComfyUIBaseUrl().TrimEnd('/')}/free", body, token);
                if (!response.IsSuccessStatusCode)
                {
                    AddLog($"Could not free server memory (/free returned {(int)response.StatusCode})");
                    return await FetchHeadroomAsync(token);
                }
            }
            catch (Exception ex)
            {
                AddLog($"Could not free server memory: {ex.Message}");
                return await FetchHeadroomAsync(token);
            }

            // Releasing pinned host memory is not instant; give the allocator a moment before measuring.
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            return await FetchHeadroomAsync(token);
        }

        /// <summary>
        /// Refuses an item the server demonstrably cannot hold, and says why. Returns null when it fits.
        ///
        /// <para>Checked against live <c>/system_stats</c> rather than a hardcoded ceiling, because the
        /// budget genuinely moves: what is resident, what another tab left staged, and which card is
        /// installed all change it between runs.</para>
        ///
        /// <para>The host-RAM check carries a 2.5× factor. The estimate is the finished batch, but the
        /// blend step holds source, new and result at once and the loop keeps its carried values alongside
        /// — the peak is well above the steady-state figure. Getting this wrong does not fail one job: the
        /// kernel kills ComfyUI and takes the rest of the queue with it.</para>
        /// </summary>
        private async Task<string?> PreflightAsync(MiniMaxI2VQueueItem item, CancellationToken token)
        {
            var frameRamGb = EstimateFrameRamGb(item);
            var gpx = EstimateGpx(item);

            var head = await FetchHeadroomAsync(token);
            if (head == null)
            {
                AddLog("Pre-flight: could not read /system_stats — falling back to the calibrated limits");
                if (frameRamGb > FrameRamOverLimitGb)
                    return $"~{frameRamGb:0.0} GB of frames — past the size that has OOM-killed the server.";
                return null;
            }

            var (ramFree, vramFree, vramTotal) = head.Value;
            AddLog($"Pre-flight: server has {ramFree:0.0} GB RAM and {vramFree:0.0} GB of "
                   + $"{vramTotal:0.0} GB VRAM free; this item wants ~{frameRamGb:0.0} GB of frames "
                   + $"and ~{gpx:0.00} Gpx per pass");

            if (frameRamGb * 2.5 > ramFree)
            {
                // Most of what is "in use" is staged weights, not live work — reclaim it and re-measure
                // before refusing. The lease is already held, so nothing else is mid-run.
                AddLog($"Pre-flight: {ramFree:0.0} GB free is not enough — unloading models to reclaim RAM…");
                var freed = await FreeServerMemoryAsync(token);
                if (freed != null)
                {
                    ramFree = freed.Value.RamFreeGb;
                    AddLog($"Pre-flight: {ramFree:0.0} GB RAM free after unloading "
                           + $"(models will reload on demand, roughly a minute)");
                }

                if (frameRamGb * 2.5 > ramFree)
                    return $"Not enough host RAM even after unloading models: the take needs roughly "
                           + $"{frameRamGb * 2.5:0.0} GB at peak ({frameRamGb:0.0} GB of frames plus blend "
                           + $"copies) and the server has {ramFree:0.0} GB free. Shorten the take, drop "
                           + "continuations, or lower the quality.";
            }

            var gpxCeiling = vramTotal * GpxPerVramGb;
            if (gpx > gpxCeiling)
            {
                var (w, h) = ResolveCanvas(item.AspectRatio, item.Megapixels, item.UseLatentUpscale);
                var fits = Math.Floor(item.Megapixels * (gpxCeiling / gpx) * 20) / 20;
                var (fw, fh) = ResolveCanvas(item.AspectRatio, fits, item.UseLatentUpscale);
                return $"Too large for the GPU: {item.Megapixels:0.0} MP at {item.AspectRatio} resolves to "
                       + $"{w}×{h}, and the biggest pass then needs ~{gpx:0.00} Gpx against a limit of about "
                       + $"{gpxCeiling:0.00} Gpx on a {vramTotal:0.0} GB card. Drop the quality to "
                       + $"{fits:0.00} MP ({fw}×{fh}), or shorten the longest pass.";
            }

            if (gpx > gpxCeiling * 0.95)
                AddLog($"Pre-flight: ~{gpx:0.00} Gpx is within 5% of this card's ~{gpxCeiling:0.00} Gpx "
                       + "limit — it should run, but there is nothing spare.");

            return null;
        }

        /// <summary>
        /// Gpx one pass may reach per GB of VRAM. Measured on this box on 21 Aug, all four runs 3-pass,
        /// 0.7 MP, on a 23.5 GB card — and it is the <i>continuation</i> pass's finish sampler that
        /// decides it, since that is the largest tensor the graph ever holds:
        /// 1:1 (832×832, 0.413 Gpx) completed, 3:2 (1024×704, 0.430 Gpx) completed,
        /// 16:9 and 3:4 (both 1152×640, 0.440 Gpx) both died with a CUDA OOM on node 4146:4240.
        /// 0.0185 puts the line at ~0.435 Gpx here: between the largest that worked and the smallest
        /// that did not. It is a narrow band, so being near it is worth saying out loud.
        /// </summary>
        private const double GpxPerVramGb = 0.0185;

        /// <summary>Host-RAM estimate for an already-queued item — the arithmetic behind
        /// <see cref="EstimatedFrameRamGb"/>, which reads the live form instead.</summary>
        private static double EstimateFrameRamGb(MiniMaxI2VQueueItem item)
        {
            var area = SampledArea(item.AspectRatio, item.Megapixels, item.UseLatentUpscale);
            var totalFrames = 0;
            for (var pass = 0; pass < item.PassCount; pass++) totalFrames += PassFrames(item, pass);
            return area * totalFrames * 12 / 1e9;
        }

        /// <summary>
        /// True when ComfyUI itself went away mid-run rather than the job failing. The kernel OOM killer
        /// leaves exactly this trace: the prompt is in neither the queue nor the history, because the
        /// process that held both was killed.
        /// </summary>
        private static bool IsServerDeath(Exception ex) =>
            ex.Message.IndexOf("no longer knows about this job", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("restarted or was killed mid-run", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Drains pending items one at a time. The workflow-coordinator lease is taken <b>per item</b>
        /// rather than around the loop, so a long queue does not lock every other tab out of ComfyUI for
        /// its whole run — and items added mid-drain are picked up on the next pass.
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;

            IsProcessingQueue = true;
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;

            AddLog("Starting MiniMax I2V queue...");
            try
            {
                MiniMaxI2VQueueItem? item;
                while (!token.IsCancellationRequested &&
                       (item = _queue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                {
                    item.ItemStatus = QueueItemStatus.Processing;
                    item.StartedAt = DateTime.Now;
                    UpdateQueueStatus();
                    SaveQueueToFile();

                    try
                    {
                        await GenerateItemAsync(item, token);
                        item.ItemStatus = QueueItemStatus.Completed;
                        item.CompletedAt = DateTime.Now;
                        AddLog($"Completed: {item.DisplayText}");

                        // From the item, not the form: the tab may already be showing the next take. This
                        // is also the point at which the queue file drops the item, so without it a prompt
                        // that rendered would be the one prompt the app forgets.
                        await SavePromptAsync(item.Prompt, item.ContinuationPrompts, item.ContinuationSeconds,
                            item.ReferencePaths, item.LengthSeconds, item.AspectRatio, manual: false);
                    }
                    catch (OperationCanceledException)
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
                        AddLog("Queue stopped — the current item is back to Pending.");
                        break;
                    }
                    catch (InvalidOperationException ex)
                    {
                        // Pre-flight said no. The shape is the problem, so a retry would only repeat it.
                        item.ItemStatus = QueueItemStatus.Failed;
                        item.ErrorMessage = ex.Message;
                        AddLog($"SKIPPED (would not fit): {ex.Message}");
                        UpdateQueueStatus();
                        SaveQueueToFile();
                        continue;
                    }
                    catch (Exception ex) when (IsServerDeath(ex))
                    {
                        // Every remaining item is the same shape and would kill it again. Leave them
                        // Pending so the settings can be fixed and the queue resumed, rather than
                        // burning the whole list against the same wall.
                        item.ItemStatus = QueueItemStatus.Failed;
                        item.ErrorMessage = "ComfyUI was killed mid-run — almost always the host running "
                                          + "out of memory. Queue stopped.";
                        AddLog("=== QUEUE STOPPED ===");
                        AddLog("ComfyUI died mid-run. That is the host running out of RAM, not this job "
                               + "failing — the remaining items are untouched and still Pending.");
                        AddLog("Shorten the take, remove continuations or lower the quality, "
                               + "then press Start.");
                        UpdateQueueStatus();
                        SaveQueueToFile();
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (await TryHandleCrashAndRetryAsync(item, ex))
                        {
                            item.ItemStatus = QueueItemStatus.Pending;
                            AddLog("Item reset to Pending — will retry after ComfyUI restart");
                        }
                        else
                        {
                            item.ItemStatus = QueueItemStatus.Failed;
                            item.ErrorMessage = ex.Message;
                            AddLog($"FAILED: {ex.Message}");
                        }
                    }

                    UpdateQueueStatus();
                    SaveQueueToFile();
                }
            }
            finally
            {
                IsProcessingQueue = false;
                IsProcessing = false;
                ProcessingStatus = token.IsCancellationRequested ? "Queue stopped" : "Queue finished";
                AddLog("Queue processing finished.");
                OnCanExecuteChanged();
            }
        }

        #region Prompt library

        /// <summary>
        /// How many takes are saved. Drives the button caption, so the library advertises itself without
        /// needing a panel of its own.
        /// </summary>
        public int SavedPromptCount
        {
            get => _savedPromptCount;
            private set
            {
                if (_savedPromptCount == value) return;
                _savedPromptCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PromptLibraryLabel));
            }
        }

        public string PromptLibraryLabel =>
            SavedPromptCount > 0 ? $"📚 Prompt Library ({SavedPromptCount})" : "📚 Prompt Library";

        /// <summary>Reads the index in the background so the button can show a count from the first paint.</summary>
        private async Task PrimePromptLibraryAsync()
        {
            try
            {
                await EnsurePromptsLoadedAsync();
            }
            catch (Exception ex)
            {
                AddLog($"Prompt library unavailable: {ex.Message}");
            }
        }

        private async Task EnsurePromptsLoadedAsync()
        {
            if (_savedPrompts != null) return;
            await _promptLibraryLock.WaitAsync();
            try
            {
                if (_savedPrompts != null) return;
                _savedPrompts = await _promptLibrary.LoadAsync();
                SavedPromptCount = _savedPrompts.Count;
            }
            finally
            {
                _promptLibraryLock.Release();
            }
        }

        /// <summary>
        /// Files whatever is in the boxes right now. Called automatically after every Analyze, and by the
        /// Save button — which is the only caller that reports back when nothing was new.
        /// </summary>
        private Task SaveCurrentPromptAsync(bool manual) =>
            SavePromptAsync(Prompt,
                Continuations.Select(c => c.Prompt).ToList(),
                Continuations.Select(c => c.Seconds).ToList(),
                FilledReferences.Select(r => r.Path).ToList(),
                LengthSeconds, ResolvedAspectRatio, manual);

        /// <summary>
        /// Files one take. The queue calls this with the finished item's own snapshot rather than the live
        /// form, because by the time a job completes the boxes may already hold the next take.
        ///
        /// <para>The base pass and the continuations are stored apart, so recalling the entry puts each
        /// segment back in the box it came out of instead of leaving the user to split a wall of text.</para>
        /// </summary>
        private async Task SavePromptAsync(
            string prompt, IReadOnlyList<string> continuationPrompts, IReadOnlyList<int> continuationSeconds,
            IReadOnlyList<string> referencePaths, int lengthSeconds, string aspectRatio, bool manual)
        {
            var body = (prompt ?? string.Empty).Trim();
            if (body.Length == 0)
            {
                if (manual) AddLog("Nothing to save — segment 1 is empty.");
                return;
            }

            // Trailing empties are half-filled form state, not part of the take. A blank in the middle is
            // kept: dropping it would silently renumber the segments after it.
            var segments = continuationPrompts.Select(p => (p ?? string.Empty).Trim()).ToList();
            var seconds = continuationSeconds.ToList();
            while (segments.Count > 0 && segments[^1].Length == 0)
            {
                segments.RemoveAt(segments.Count - 1);
                if (seconds.Count > segments.Count) seconds.RemoveAt(seconds.Count - 1);
            }

            var held = false;
            try
            {
                await EnsurePromptsLoadedAsync();
                await _promptLibraryLock.WaitAsync();
                held = true;

                var saved = _savedPrompts!;
                var pictures = referencePaths.Where(p => !string.IsNullOrEmpty(p)).ToList();
                var primary = pictures.FirstOrDefault(File.Exists) ?? string.Empty;

                var draft = new ScenePrompt
                {
                    Name = ScenePromptLibrary.SuggestName(primary, body, saved),
                    Prompt = body,
                    ContinuationPrompts = segments,
                    ContinuationSeconds = seconds,
                    ReferenceImagePaths = pictures,
                    // The thumbnail is rendered from this one; the rest are recorded for the recall log.
                    SceneImagePath = primary,
                    AspectRatio = aspectRatio,
                    LengthSeconds = lengthSeconds,
                };

                // Thumbnail encoding runs inside AddOrRefresh — keep the whole thing off the UI thread.
                var (entry, isNew) = await Task.Run(() => _promptLibrary.AddOrRefresh(saved, draft));
                await _promptLibrary.SaveAsync(saved);
                SavedPromptCount = saved.Count;

                AddLog(isNew
                    ? $"Saved to the prompt library as \"{entry.Name}\" ({SavedPromptCount} takes)."
                    : $"Already in the prompt library as \"{entry.Name}\" — timestamp refreshed.");
            }
            catch (Exception ex)
            {
                // Never let a library problem fail the Analyze or Generate that triggered it.
                AddLog($"Could not save to the prompt library: {ex.Message}");
            }
            finally
            {
                if (held) _promptLibraryLock.Release();
            }
        }

        /// <summary>
        /// Opens the picker and, on a pick, puts the take back in the boxes: segment 1, one continuation
        /// per saved segment, and the length and aspect it was written for.
        ///
        /// <para>The reference pictures are deliberately <b>not</b> restored — reusing a prompt against new
        /// pictures is the point of the library. But the prompt names its pictures by number, so recalling
        /// a three-picture take onto one loaded slot leaves it referring to a &lt;Picture 3&gt; that is not
        /// there; the log says so rather than letting it fail at render time.</para>
        /// </summary>
        /// <summary>
        /// Avalonia's ShowDialog always needs an owner. With no active window (only possible during
        /// teardown) fall back to showing the picker as a top-level window and awaiting its close.
        /// </summary>
        private static async Task<Models.ScenePrompt?> ShowOwnerlessAsync(
            FlipPix.UI.Linux.Windows.ScenePromptLibraryWindow window)
        {
            var completion = new TaskCompletionSource<Models.ScenePrompt?>();
            window.Closed += (_, _) => completion.TrySetResult(null);
            window.Show();
            return await completion.Task;
        }

        private async Task OpenPromptLibraryAsync()
        {
            try
            {
                await EnsurePromptsLoadedAsync();

                var window = new FlipPix.UI.Linux.Windows.ScenePromptLibraryWindow(
                    _promptLibrary, _savedPrompts!, new FlipPix.UI.Linux.Windows.ScenePromptLibraryChrome
                {
                    WindowTitle = "Prompt Library — MiniMax I2V",
                    SearchWatermark = "Search saved takes by name or prompt text",
                    PromptNote = "Saved as separate segments; the headers above are only how the preview "
                               + "shows them. The reference pictures are not restored — load your own, and "
                               + "keep the <Picture N> order the prompt expects.",
                    EmptyText = "No saved takes yet.\nAnalyze or render a prompt and it lands here.",
                    NoMatchText = "No take matches that search.",
                    UseButtonText = "Use Take",
                    LibraryNoun = "prompt library",
                    DeleteDialogTitle = "Delete Take",
                });

                // Avalonia's ShowDialog returns the value the window closed with, so the picked take
                // comes back directly instead of through WPF's DialogResult/SelectedScene pair.
                var owner = (Avalonia.Application.Current?.ApplicationLifetime
                        as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                    ?.Windows.FirstOrDefault(w => w.IsActive)
                    ?? (Avalonia.Application.Current?.ApplicationLifetime
                        as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                    ?.MainWindow;

                // CenterOwner with no owner lands the window in the top-left corner.
                if (owner == null)
                    window.WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen;

                var picked = owner != null
                    ? await window.ShowDialog<Models.ScenePrompt?>(owner)
                    : await ShowOwnerlessAsync(window);
                SavedPromptCount = _savedPrompts!.Count;
                if (picked == null) return;

                Prompt = picked.Prompt;
                RestoreContinuations(picked.ContinuationPrompts, picked.ContinuationSeconds);

                if (picked.LengthSeconds > 0) LengthSeconds = (int)Math.Round(picked.LengthSeconds);
                if (!string.IsNullOrEmpty(picked.AspectRatio) && AspectRatioOptions.Contains(picked.AspectRatio))
                    SelectedAspectRatio = picked.AspectRatio;

                var passes = picked.ContinuationPrompts.Count + 1;
                AddLog($"Loaded \"{picked.Name}\" from the prompt library "
                     + $"({passes} pass{(passes == 1 ? string.Empty : "es")}, {LengthSeconds}s base, {ResolvedAspectRatio}).");

                var wanted = picked.ReferenceImagePaths.Count;
                var loaded = FilledReferences.Count;
                if (wanted > 0 && wanted != loaded)
                    AddLog($"NOTE: this prompt was written against {wanted} picture(s) and {loaded} are loaded — "
                         + "check that the <Picture N> references still match what is in the slots.");
            }
            catch (Exception ex)
            {
                AddLog($"Prompt library failed to open: {ex.Message}");
                System.Windows.MessageBox.Show($"Could not open the prompt library:\n{ex.Message}",
                    "Prompt Library", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Rebuilds the continuation slots to match a recalled take, dropping any beyond the three
        /// the graph's indexed switches can address.</summary>
        private void RestoreContinuations(IReadOnlyList<string> prompts, IReadOnlyList<int> seconds)
        {
            while (Continuations.Count > 0) RemoveContinuation(Continuations[^1]);

            var count = Math.Min(prompts.Count, MaxContinuations);
            for (var i = 0; i < count; i++)
            {
                AddContinuation();
                var segment = Continuations[^1];
                segment.Prompt = prompts[i];
                if (i < seconds.Count && seconds[i] > 0) segment.Seconds = seconds[i];
            }

            if (prompts.Count > count)
                AddLog($"WARNING: the saved take has {prompts.Count} continuations and this tab holds "
                     + $"{MaxContinuations} — the last {prompts.Count - count} were dropped.");
        }

        #endregion

        private void SaveQueueToFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(QueueFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // Completed items are session history, not pending work — keeping them out stops the queue
                // file (and therefore startup) from growing without bound.
                var pending = _queue.Where(q => q.ItemStatus != QueueItemStatus.Completed).ToList();
                File.WriteAllText(QueueFilePath,
                    JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AddLog($"Error saving queue: {ex.Message}"); }
        }

        /// <summary>
        /// Restores unfinished work from a previous session. Anything left mid-flight is put back to
        /// Pending — the ComfyUI run it belonged to did not survive the app closing. Never auto-starts;
        /// the user presses Start.
        /// </summary>
        private void LoadQueueFromFile()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;
                var items = JsonSerializer.Deserialize<List<MiniMaxI2VQueueItem>>(File.ReadAllText(QueueFilePath));
                if (items == null) return;
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Processing) item.ItemStatus = QueueItemStatus.Pending;
                    _queue.Add(item);
                }
                if (_queue.Count > 0) AddLog($"Restored {_queue.Count} queued item(s)");
                UpdateQueueStatus();
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        #endregion

        #region Generation

        private async Task GenerateItemAsync(MiniMaxI2VQueueItem item, CancellationToken outerToken)
        {
            IsProcessing = true;
            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = "Preparing MiniMax I2V workflow...";
            GenerationTimer = string.Empty;

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                AddLog("=== MiniMax I2V (H3 Ref2VA) ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("MiniMaxI2V", outerToken);

                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                // With the lease held, so freeing memory cannot pull models out from under another tab.
                ProcessingStatus = "Checking the server has room...";
                var blocked = await PreflightAsync(item, outerToken);
                if (blocked != null) throw new InvalidOperationException(blocked);

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFileName);
                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
                var json = await File.ReadAllTextAsync(workflowPath, outerToken);

                ProcessingStatus = "Uploading reference pictures...";
                ProcessingProgress = 4;
                var uploaded = new List<string>();
                foreach (var path in item.ReferencePaths)
                {
                    if (!File.Exists(path))
                        throw new FileNotFoundException($"Reference picture is gone: {path}");
                    var name = await _comfyUIService.UploadImageAsync(path);
                    if (string.IsNullOrEmpty(name))
                        throw new Exception($"Failed to upload reference picture {uploaded.Count + 1}.");
                    uploaded.Add(name!);
                    AddLog($"<Picture {uploaded.Count}> uploaded: {name}");
                }
                if (uploaded.Count == 0) throw new Exception("This item has no reference pictures.");

                var runSeed = item.Seed >= 0 ? item.Seed : System.Random.Shared.NextInt64(0, int.MaxValue);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var runToken = $"mmi2v_{ts}";
                var sink = item.ContinuationPrompts.Count > 0 ? NodeSaveJoined : NodeSaveSingle;

                json = BuildWorkflow(json, item, uploaded, runSeed, runToken, sink, out var pruned);
                AddLog($"Graph prepared: {item.PassCount} pass(es), {pruned} node(s) pruned");

                ProcessingProgress = 8;
                AddLog($"Generating (seed {runSeed}, ~{item.TotalSeconds}s over {item.PassCount} pass(es), " +
                       $"{item.AspectRatio}, {item.Megapixels:0.0} MP" +
                       $"{(item.UseSla ? $", SLA {item.SlaSparsity:0.00}" : "")}" +
                       $"{(item.UseSparseAttention ? ", Sol-Attn" : "")}" +
                       $"{(item.UseLatentUpscale ? ", latent 2x" : "")}" +
                       $"{(item.UseRtxUpscale ? ", RTX 2x" : "")}, ~{EstimateGpx(item):0.00} Gpx/pass)...");

                // The bar and the clock are handed the shape of the run before it starts, so the very
                // first tick can already say roughly how long this will take. From the first sampler
                // step onwards the estimate is re-derived from the run's own pace.
                var plan = BuildProgressPlan(item);
                _progressTracker.Begin(plan.Stages, plan.LeadUnits, 8, 97, plan.EstimatedSeconds,
                                       phase: string.Empty);
                AddLog($"Estimated run time ~{TimeSpan.FromSeconds(plan.EstimatedSeconds):hh\\:mm\\:ss} " +
                       $"({_secondsPerGpx:0} s/Gpx learned so far)");

                var local = await SubmitAndRetrieveAsync(json, runToken, sink, plan.EstimatedSeconds, outerToken);
                if (local == null || !File.Exists(local))
                    throw new Exception("No output video was generated.");

                RecalibrateFromRun(item, _progressTracker.Elapsed);
                _progressTracker.Finish(true);

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "MiniMaxI2V");
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"MiniMaxI2V_{ts}.mp4");
                File.Copy(local, finalPath, true);
                await LocalCopyService.CopyVideoAsync(finalPath);

                var fi = new FileInfo(finalPath);
                item.OutputImagePath = finalPath;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResultVideoPath = finalPath;
                    ResultVideoInfo =
                        $"MiniMax I2V • {item.AspectRatio} • {item.TotalSeconds}s over {item.PassCount} " +
                        $"pass{(item.PassCount == 1 ? "" : "es")} • {uploaded.Count} ref • " +
                        $"{fi.Length / 1024 / 1024.0:F1}MB";
                    HasResult = true;
                    OnCanExecuteChanged();
                });
                ProcessingProgress = 100;
                ProcessingStatus = "Complete!";
                AddLog($"=== Complete: {finalPath} ===");
            }
            finally
            {
                // Cancelled or failed: stop the clock where it stopped, rather than leaving a ticking
                // ETA against a run that is no longer happening.
                if (_progressTracker.IsRunning) _progressTracker.Finish(false);
                lease?.Dispose();
                IsProcessing = false;
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Describes the run to the progress tracker: every sampler bar ComfyUI will report, in order,
        /// what each of their steps is worth, and the unreported work between them.
        ///
        /// <para>Everything below is measured in one <em>finish</em> sampler step — the only steps that
        /// see the whole canvas. The figures carry over from the run that was measured under the previous
        /// scheme, rebased on that unit: a draft step costs about a quarter of a finish step because it
        /// samples a quarter of the pixels, the latent upscale between the two is worth ~2.2, VAE decode
        /// and stitching after them ~3.8, and a continuation pass ~1.55× the base pass it extends. They
        /// only have to be roughly right — they set the <em>shape</em> of the run, and the pace is
        /// re-measured from the run itself as it goes.</para>
        /// </summary>
        private (List<ProgressStage> Stages, double LeadUnits, double EstimatedSeconds) BuildProgressPlan(
            MiniMaxI2VQueueItem item)
        {
            const double draftStepUnits = 0.25;   // a quarter-canvas step against a finish step
            const double afterMainUnits = 2.2;    // latent upscale, between the two samplers of a pass
            const double afterPassUnits = 3.8;    // VAE decode and stitching at the end of a pass
            const double rtxUnits = 1.3;          // RTX 2×, on the saved pass only
            const double leadUnits = 1.3;         // loading the model, encoding the references

            var stages = new List<ProgressStage>();
            for (var pass = 0; pass < item.PassCount; pass++)
            {
                var label = item.PassCount == 1 ? "Rendering" : $"Pass {pass + 1}/{item.PassCount}";
                var weight = PassWeight(item, pass);
                var last = pass == item.PassCount - 1;
                var tail = afterPassUnits * weight + (last && item.UseRtxUpscale ? rtxUnits * weight : 0);

                // Latent upscale on: 4 draft steps then 3 finish steps. Off: one 8-step pass at full
                // size, where every step is worth a finish step because it is already the final canvas.
                if (item.UseLatentUpscale)
                {
                    stages.Add(new ProgressStage($"{label} draft", 4, draftStepUnits * weight,
                                                 afterMainUnits * weight));
                    stages.Add(new ProgressStage(label, 3, weight, tail));
                }
                else
                {
                    stages.Add(new ProgressStage(label, 8, weight, tail));
                }
            }

            // The joined output's frame-by-frame encode. It reports one step per frame and runs at ~90
            // frames a second, so hundreds of its steps are worth barely one finish step.
            stages.Add(new ProgressStage("Writing the video", 0, 0.0054, 0.27));

            return (stages, leadUnits, 40 + _secondsPerGpx * WeightedGpx(item));
        }

        /// <summary>
        /// What one pass costs against the base pass. A continuation re-samples its own clip <i>and</i> the
        /// overlap it has to blend into the pass before it, which measured out at ~1.55× the base pass of
        /// the same size — on top of any difference in length between them.
        /// </summary>
        private static double PassWeight(MiniMaxI2VQueueItem item, int passIndex)
        {
            if (passIndex == 0) return 1.0;
            var seconds = item.ContinuationSeconds.ElementAtOrDefault(passIndex - 1);
            if (seconds <= 0) seconds = item.LengthSeconds;

            // Length only: the 1.55 already carries the overlap window a continuation re-samples, so
            // taking the ratio from PassFrames as well would charge for it twice.
            var ratio = FrameCount(seconds) / (double)Math.Max(1, FrameCount(item.LengthSeconds));
            return ContinuationFactor * ratio;
        }

        /// <summary>Total cost of the run in base-pass Gpx — what the seconds-per-Gpx figure multiplies.</summary>
        private static double WeightedGpx(MiniMaxI2VQueueItem item)
        {
            var total = 0.0;
            for (var pass = 0; pass < item.PassCount; pass++)
                total += PassGpx(item, pass) * (pass == 0 ? 1.0 : ContinuationFactor);
            return total;
        }

        /// <summary>Peak tensor size of one pass, in billions of pixel-positions — the cost driver the
        /// run-time estimate is scaled by.</summary>
        private static double PassGpx(MiniMaxI2VQueueItem item, int passIndex) =>
            SampledArea(item.AspectRatio, item.Megapixels, item.UseLatentUpscale)
            * PassFrames(item, passIndex) / 1e9;

        /// <summary>
        /// Frames one pass actually samples. A continuation re-generates the overlap window on top of its
        /// own length — the loop's frame-count expression is literally <c>frames(seconds) + overlap</c> —
        /// so a continuation is always bigger than the base pass, even at the same number of seconds.
        /// </summary>
        private static int PassFrames(MiniMaxI2VQueueItem item, int passIndex)
        {
            if (passIndex == 0) return FrameCount(item.LengthSeconds);
            var seconds = item.ContinuationSeconds.ElementAtOrDefault(passIndex - 1);
            if (seconds <= 0) seconds = item.LengthSeconds;
            return FrameCount(seconds) + item.OverlapFrames;
        }

        /// <summary>
        /// Pixels in one finished frame: the canvas ComfyUI will really produce. Both memory limits are
        /// built on this figure, and deriving it from the megapixel target instead — as this tab used to
        /// — understates 16:9, 4:3, 3:4, 9:16 and 21:9 by ~5%, which is the entire margin on a 24 GB card.
        /// </summary>
        private static double SampledArea(string aspectRatio, double megapixels, bool latentUpscale)
        {
            var (w, h) = ResolveCanvas(aspectRatio, megapixels, latentUpscale);
            return (double)w * h;
        }

        /// <summary>
        /// Width and height of the finished frames. With the latent upscale on, ResolutionSelector is
        /// handed a quarter of the megapixel target and its answer is doubled, so this has to fold the
        /// same arithmetic <see cref="BuildWorkflow"/> writes into the graph — taking the size straight
        /// from the megapixel target would miss the draft's own rounding.
        /// </summary>
        private static (int Width, int Height) ResolveCanvas(
            string aspectRatio, double megapixels, bool latentUpscale)
        {
            if (!latentUpscale) return H3Canvas.Resolve(aspectRatio, megapixels, ResolutionMultiple);

            var (w, h) = H3Canvas.Resolve(aspectRatio, DraftMegapixels(megapixels), ResolutionMultiple);
            return ((int)(w * LatentUpscaleFactor), (int)(h * LatentUpscaleFactor));
        }

        /// <summary>The megapixel target the draft is sampled at in order to finish at
        /// <paramref name="megapixels"/>.</summary>
        private static double DraftMegapixels(double megapixels) =>
            megapixels / (LatentUpscaleFactor * LatentUpscaleFactor);

        /// <summary>
        /// Folds a finished run's real duration back into the seconds-per-Gpx figure, so the next item in
        /// the queue opens with an estimate from this machine rather than from the one this was written on.
        /// Half-weighted, and ignored outside a sane range, so one stalled run cannot poison the number.
        /// </summary>
        private void RecalibrateFromRun(MiniMaxI2VQueueItem item, TimeSpan elapsed)
        {
            var gpx = WeightedGpx(item);
            if (gpx <= 0 || elapsed.TotalSeconds < 60) return;

            var measured = (elapsed.TotalSeconds - 40) / gpx;
            if (measured is < 100 or > 3000) return;

            _secondsPerGpx = 0.5 * _secondsPerGpx + 0.5 * measured;
            AddLog($"Took {elapsed:hh\\:mm\\:ss} for {gpx:0.00} Gpx — {measured:0} s/Gpx " +
                   $"(estimate for the next item: {_secondsPerGpx:0} s/Gpx)");
        }

        /// <summary>Peak-pass estimate for an already-queued item — same arithmetic as
        /// <see cref="EstimatedPeakGpx"/>, which reads the live form instead.</summary>
        private static double EstimateGpx(MiniMaxI2VQueueItem item)
        {
            var peak = 0.0;
            for (var pass = 0; pass < item.PassCount; pass++) peak = Math.Max(peak, PassGpx(item, pass));
            return peak;
        }

        #endregion

        #region Workflow patching

        /// <summary>
        /// Writes one queued item into the graph and cuts it down to the chosen sink. Everything the run
        /// does not use is removed rather than unhooked, because the half that is not chosen still ends in
        /// its own VHS_VideoCombine and would render regardless.
        /// </summary>
        private string BuildWorkflow(
            string json, MiniMaxI2VQueueItem item, IReadOnlyList<string> uploaded,
            long runSeed, string runToken, string sink, out int pruned)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeBaseRef2V, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodeLoopRef2V, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodeLoopStart, "easy forLoopStart");
            RequireClass(root, sink, "VHS_VideoCombine");

            var extending = item.ContinuationPrompts.Count > 0;

            // ── References ────────────────────────────────────────────────────
            SetInput(root, NodeReference0, "image", uploaded[0]);
            var loaders = new List<string> { NodeReference0 };
            for (var i = 1; i < uploaded.Count; i++)
            {
                var id = $"i2v_ref_{i}";
                root[id] = new JsonObject
                {
                    ["inputs"] = new JsonObject { ["image"] = uploaded[i] },
                    ["class_type"] = "LoadImage",
                    ["_meta"] = new JsonObject { ["title"] = $"Picture {i + 1}" }
                };
                loaders.Add(id);
            }
            AttachReferences(root, NodeBaseRef2V, loaders);
            AttachReferences(root, NodeLoopRef2V, loaders);

            var refSize = item.MaxFidelityReferences ? "max" : "match";
            SetInput(root, NodeBaseRef2V, "ref_image_size", refSize);
            SetInput(root, NodeLoopRef2V, "ref_image_size", refSize);

            // ── Prompts, lengths and seeds ────────────────────────────────────
            SetInput(root, NodeBasePrompt, "value", item.Prompt);
            SetInput(root, NodeBaseSeconds, "value", item.LengthSeconds);
            SetInput(root, NodeBaseSeed, "noise_seed", runSeed);

            // The loop's prompt/duration/seed switches are indexed off the loop counter, so every slot has
            // to hold something valid even when fewer continuations are queued than there are slots. The
            // unused ones repeat the last real continuation rather than staying empty.
            for (var i = 0; i < MaxContinuations; i++)
            {
                var hasOwn = i < item.ContinuationPrompts.Count;
                var lastIndex = item.ContinuationPrompts.Count - 1;
                var promptText = hasOwn ? item.ContinuationPrompts[i]
                    : lastIndex >= 0 ? item.ContinuationPrompts[lastIndex] : item.Prompt;
                var seconds = i < item.ContinuationSeconds.Count ? item.ContinuationSeconds[i]
                    : item.ContinuationSeconds.Count > 0 ? item.ContinuationSeconds[^1] : item.LengthSeconds;

                SetInput(root, NodeContinuationPrompts[i], "value", promptText);
                SetInput(root, NodeContinuationSeconds[i], "value", seconds);
                SetInput(root, NodeContinuationSeeds[i], "noise_seed", runSeed + i + 1);
            }
            SetInput(root, NodeLoopStart, "total", Math.Max(1, item.ContinuationPrompts.Count));
            SetInput(root, NodeOverlap, "choice", item.OverlapFrames.ToString());

            // ── Canvas ────────────────────────────────────────────────────────
            // ResolutionSelector sizes the *sampled* canvas, which with the latent upscale on is the
            // draft — a quarter of the megapixel target, doubled afterwards. The dropdown names the
            // finished size, so the division happens here rather than in the user's head.
            SetInput(root, NodeResolution, "aspect_ratio", item.AspectRatio);
            SetInput(root, NodeResolution, "megapixels",
                     item.UseLatentUpscale ? DraftMegapixels(item.Megapixels) : item.Megapixels);
            SetInput(root, NodeResolution, "multiple", ResolutionMultiple);

            // ── Attention ─────────────────────────────────────────────────────
            foreach (var id in NodeSla)
            {
                SetInput(root, id, "enabled", item.UseSla);
                SetInput(root, id, "sparsity_ratio", item.SlaSparsity);
                // 64 always: H3 packs audio at 80 rows per second, so a 128-row block forces 1.6s of
                // audio through one attention pattern and speech comes out robotic. Every clip this
                // tab renders has a soundtrack, so the wider block is never the right trade here.
                SetInput(root, id, "block_size", "64");
            }
            SetInput(root, NodeSparseAttention, "switch", item.UseSparseAttention);

            // ── Sampling scheme ───────────────────────────────────────────────
            // The 2x has to be written into all three places that derive the finished canvas: the two
            // latent upscalers, and the loop's own width/height expressions for the conditioning latent
            // its finish sampler is built against. If those disagree the loop dies on a token mismatch.
            SetInput(root, NodeBaseUpscaler, "mode.scale", LatentUpscaleFactor);
            SetInput(root, NodeLoopUpscaler, "mode.scale", LatentUpscaleFactor);
            foreach (var id in NodeLoopFinishDims) SetInput(root, id, "values.b", LatentUpscaleFactor);

            SetInput(root, NodeBaseDetail, "switch", item.UseLatentUpscale);
            foreach (var id in NodeLoopDetail) SetInput(root, id, "switch", item.UseLatentUpscale);

            // With the upscale on, the first sampler is only a draft and stops half-denoised at sigma 0.5
            // — the finish sampler picks it up from there. With it off there is no finish sampler, so the
            // same node has to run the full shifted schedule down to zero instead.
            Link(root, NodeBaseSampler, "sigmas",
                 item.UseLatentUpscale ? NodeDraftSigmas : NodeBaseFullSigmas, 0);
            Link(root, NodeLoopSampler, "sigmas",
                 item.UseLatentUpscale ? NodeDraftSigmas : NodeLoopFullSigmas, 0);

            // Only the half that owns the saved sink finishes the frames and the audio: on the base pass
            // these would hand the loop 2x frames and already-enhanced audio to enhance a second time.
            SetInput(root, NodeBaseUpscale, "switch", !extending && item.UseRtxUpscale);
            SetInput(root, NodeLoopUpscale, "switch", item.UseRtxUpscale);
            SetInput(root, NodeBaseAudio, "switch", !extending && item.UseAudioEnhancement);
            SetInput(root, NodeLoopAudio, "switch", item.UseAudioEnhancement);

            // ── Sink ──────────────────────────────────────────────────────────
            SetInput(root, sink, "filename_prefix", $"{OutputSubfolder}/{runToken}");
            SetInput(root, sink, "save_output", true);

            return PruneToOutputs(root.ToJsonString(), new[] { sink }, out pruned);
        }


        /// <summary>Replaces a reference-to-video node's autogrow ref_image slots with this run's loaders.</summary>
        private static void AttachReferences(JsonObject root, string nodeId, IReadOnlyList<string> loaders)
        {
            if (root[nodeId]?["inputs"] is not JsonObject inputs) return;

            foreach (var key in inputs.Select(kv => kv.Key)
                                      .Where(k => k.StartsWith("ref_images.ref_image_", StringComparison.Ordinal))
                                      .ToList())
                inputs.Remove(key);

            for (var i = 0; i < loaders.Count; i++)
                inputs[$"ref_images.ref_image_{i}"] = new JsonArray(loaders[i], 0);
        }

        /// <summary>Points one node's input at another node's output.</summary>
        private static void Link(JsonObject root, string nodeId, string input, string sourceId, int slot)
        {
            if (root[nodeId]?["inputs"] is not JsonObject inputs)
                throw new Exception($"Workflow node '{nodeId}' is missing — the workflow file no longer matches this tab.");
            if (root[sourceId] == null)
                throw new Exception($"Workflow node '{sourceId}' is missing — the workflow file no longer matches this tab.");

            inputs[input] = new JsonArray(sourceId, slot);
        }

        private static void SetInput(JsonObject root, string nodeId, string input, object value)
        {
            if (root[nodeId]?["inputs"] is not JsonObject inputs)
                throw new Exception($"Workflow node '{nodeId}' is missing — the workflow file no longer matches this tab.");

            inputs[input] = value switch
            {
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(value.ToString())
            };
        }

        private static void RequireClass(JsonObject root, string nodeId, string expected)
        {
            var actual = root[nodeId]?["class_type"]?.GetValue<string>();
            if (actual != expected)
                throw new Exception($"Workflow node '{nodeId}' is a {actual ?? "(none)"}, expected {expected} — the workflow file no longer matches this tab.");
        }

        /// <summary>
        /// Cuts the graph down to the output nodes we want plus everything they depend on, and deletes
        /// every other node outright. Pruning by reachability is the only reliable way to drop a branch:
        /// anything ending in an OUTPUT_NODE runs whether or not something downstream consumes it, so
        /// unhooking a sink is not enough on its own.
        /// </summary>
        private static string PruneToOutputs(string json, IEnumerable<string> keepOutputs, out int removed)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(keepOutputs);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!reachable.Add(id)) continue;
                if (root[id]?["inputs"] is not JsonObject inputs) continue;

                foreach (var input in inputs)
                {
                    // A link is ["<source node id>", <output index>]; widget values are anything else.
                    if (input.Value is JsonArray link && link.Count == 2 && LinkSource(link[0]) is { } src)
                        stack.Push(src);
                }
            }

            removed = 0;
            foreach (var id in root.Select(kv => kv.Key).ToList())
            {
                if (reachable.Contains(id)) continue;
                root.Remove(id);
                removed++;
            }

            return root.ToJsonString();

            // Node ids are strings here (this graph was exported with subgraphs flattened, so they look
            // like "4145:174"), but plain integer ids show up in other exports of the same nodes.
            static string? LinkSource(JsonNode? node)
            {
                if (node is not JsonValue value) return null;
                if (value.TryGetValue<string>(out var s)) return s;
                if (value.TryGetValue<long>(out var i)) return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return null;
            }
        }

        #endregion

        #region Submission

        /// <summary>Submits the workflow, waits for completion, and resolves the chosen video sink's output
        /// to a local file — first via /history node outputs, then a disk scan for the run token.</summary>
        private async Task<string?> SubmitAndRetrieveAsync(
            string json, string runToken, string outputNode, double estimatedSeconds, CancellationToken token)
        {
            var existing = GetExistingVideoFiles("*.mp4", OutputSubfolder);
            var promptId = await SubmitAsync(json, estimatedSeconds, token);

            _progressTracker.SetPhase("Fetching the finished video...");
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
            if (byNode.TryGetValue(outputNode, out var outs) && outs.Count > 0)
            {
                var pick = outs.FirstOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? outs[0];
                var local = await ResolveOutputToLocalAsync(pick);
                if (local != null) return local;
            }

            // Fallback: wait for a new mp4 carrying this run's token in the output subfolder.
            var found = await WaitForNewVideoAsync(existing, "*.mp4",
                TimeSpan.FromMinutes(90), TimeSpan.FromSeconds(4), OutputSubfolder);
            if (found != null && Path.GetFileName(found).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0)
                return found;
            return found ?? FindTokenFileOnDisk(runToken);
        }

        /// <summary>
        /// Submits and waits. ComfyUI counts steps per node, so every sampler in the graph reports 1..8
        /// or 1..4 of its own — the tracker is what turns that into one number for the whole run.
        /// </summary>
        private async Task<string> SubmitAsync(string json, double estimatedSeconds, CancellationToken token)
        {
            var workflow = JsonSerializer.Deserialize<JsonElement>(json);
            var progress = new Progress<ProgressMessage>(msg =>
            {
                var data = msg.Data;
                if (data != null && data.Max > 0) _progressTracker.Report(data.Value, data.Max);
            });

            // A four-pass job at this quality runs well past the 30-minute default, and being cut off at
            // the ceiling looks exactly like a failure. Three times the estimate, floor 45 minutes.
            var timeout = TimeSpan.FromSeconds(Math.Clamp(estimatedSeconds * 3, 45 * 60, 6 * 60 * 60));

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token, timeout);
            AddLog($"Workflow submitted, ID: {promptId}");
            return promptId;
        }

        private async Task<string?> ResolveOutputToLocalAsync(string videoFile)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    var baseUrl = GetComfyUIBaseUrl();
                    bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                    string outputFolder = settings.ResolveOutputFolder(isRemote);
                    if (!string.IsNullOrEmpty(outputFolder))
                    {
                        var localPath = Path.Combine(outputFolder, videoFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(localPath))
                        {
                            await WaitForFileStableAsync(localPath);
                            return localPath;
                        }
                    }
                }

                var parts = videoFile.Split('/');
                var filename = parts.Last();
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : "";
                var bytes = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, subfolder);
                if (bytes is { Length: > 0 })
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"mmi2v_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tempPath, bytes);
                    return tempPath;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Resolve output failed: {ex.Message}");
            }
            return null;
        }

        private string? FindTokenFileOnDisk(string runToken)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;
                var baseUrl = GetComfyUIBaseUrl();
                bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                var outputFolder = settings.ResolveOutputFolder(isRemote);
                if (string.IsNullOrEmpty(outputFolder)) return null;

                var candidates = new List<string>();
                foreach (var folder in new[] { outputFolder, Path.Combine(outputFolder, OutputSubfolder) })
                {
                    if (Directory.Exists(folder))
                        candidates.AddRange(Directory.GetFiles(folder, "*.mp4", SearchOption.AllDirectories)
                            .Where(f => Path.GetFileName(f).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                return candidates.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            }
            catch (Exception ex)
            {
                AddLog($"Disk scan failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(CanAnalyze));
            OnPropertyChanged(nameof(CanGenerate));
            OnPropertyChanged(nameof(GenerateBlockedReason));
            AnalyzeCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            AddContinuationCommand.NotifyCanExecuteChanged();
            ClearQueueCommand.NotifyCanExecuteChanged();
            StartQueueCommand.NotifyCanExecuteChanged();
            StopQueueCommand.NotifyCanExecuteChanged();
            ReprocessAllFailedCommand.NotifyCanExecuteChanged();
            SavePromptCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>How many frames a continuation re-generates and blends over the pass before it.</summary>
    public record OverlapOption(int Frames, string Label);

    /// <summary>An SLA sparsity preset - the fraction of key blocks the kernel skips.</summary>
    public record SlaSparsityOption(double Value, string Label);
}
