using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "MiniMax FFLF" tab. Drives <c>h3-minimax-fflf.json</c> — MiniMax H3 in FL2VA mode, where the two
    /// supplied pictures are the literal first and last frames of the clip and the prompt describes the
    /// path between them.
    ///
    /// <para>The tab is built around a <b>keyframe chain</b>: an opening frame plus up to four stills the
    /// take has to pass through. Clip 1 runs from the opening frame to keyframe 2; every later clip is a
    /// further sampling pass inside the same submission, conditioned on the tail of the clip before it and
    /// aimed at its own keyframe. The loop indexes its prompt, duration and end frame off the loop
    /// counter, which is why there are exactly three continuing clips.</para>
    ///
    /// <para>Analyze walks that chain pair by pair — (opening, keyframe 2), (keyframe 2, keyframe 3), … —
    /// because that is exactly the shape the FL2VA system prompt is written for, and it is also what each
    /// pass really sees. The graph as authored refined the prompt with an in-graph LLM node pointed at a
    /// hardcoded local server; that is stripped from the copy this tab ships, so the prompt can be read
    /// and edited before a render is spent.</para>
    ///
    /// <para>Every clip renders in three moves rather than one: four sampler steps at a quarter of the
    /// canvas to settle the composition, the MiniMax H3 3D latent upscaler doubling that latent, then
    /// three fixed-sigma steps at the finished size to put the detail on. Only those last three steps
    /// ever see the whole canvas, which is what makes it both quicker and sharper than eight full-size
    /// steps and a 1.5× refine afterwards.</para>
    ///
    /// <para>Both halves of the graph — the base pass and the continuation loop — end in their own
    /// VHS_VideoCombine sink, and an OUTPUT_NODE runs whether or not anything downstream wants it. So the
    /// run picks its sink and <see cref="PruneToOutputs"/> deletes everything the sink does not reach:
    /// with a single clip the loop never enters the submitted graph at all.</para>
    ///
    /// <para>The clips of one take are therefore joined <i>inside ComfyUI</i>, by the loop's
    /// <c>ImageBatchExtendWithOverlap</c> and <c>AudioConcat</c> — a blended overlap, not a cut. A folder
    /// too long for one chain is walked as several takes, one submission each, and those are joined here
    /// with FFmpeg once the last one lands: see <see cref="CompleteFolderRunAsync"/>.</para>
    /// </summary>
    public partial class MiniMaxFflfViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/h3-minimax/h3-minimax-fflf.json";
        private const string OutputSubfolder = "minimax_fflf";
        private const string SystemPromptFile = "h3minimax-fflf.md";

        /// <summary>What a continuing clip costs against the base pass of the same length, measured.</summary>
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
        /// <c>round(draft * factor / 32) * 32</c> for the conditioning latent — and only an integer
        /// factor guarantees the two land on the same number for every aspect ratio.
        /// </summary>
        private const double LatentUpscaleFactor = 2.0;

        /// <summary>Clips in the chain: the base pass plus the three slots the loop's switches can address.</summary>
        public const int MaxClips = 4;

        // ── Workflow node ids (locked to h3-minimax-fflf.json) ────────────────────────────────
        private const string NodeOpeningFrame = "462";        // LoadImage → first_frame of the base pass
        private const string NodeBaseEndFrame = "463";        // LoadImage → last_frame of the base pass
        private const string NodeBaseFl2v = "16:382";         // MiniMaxH3ImageToVideo (base pass)
        private const string NodeLoopFl2v = "521:515";        // MiniMaxH3ImageToVideo (continuing clip)
        private const string NodeBasePrompt = "3371";         // PrimitiveStringMultiline
        private const string NodeBaseSeconds = "16:12";       // easy int → frame-count expression
        private const string NodeBaseSeed = "16:483";         // RandomNoise
        private const string NodeResolution = "17";           // ResolutionSelector
        private const string NodeLoopStart = "521:523";       // easy forLoopStart (total = continuing clips)
        private const string NodeOverlap = "4177:4171";       // CustomCombo → overlap frame count
        private const string NodeSaveSingle = "381";          // VHS_VideoCombine, base pass only
        private const string NodeSaveJoined = "526";          // VHS_VideoCombine, whole chain

        // The loop's indexed slots, in loop order. Note the end-frame loaders are not in id order:
        // the switch's value0/1/2 are 3478, 3477, 3479 as the graph was authored.
        private static readonly string[] NodeClipPrompts = { "3372", "3373", "3374" };
        private static readonly string[] NodeClipSeconds = { "3377", "3378", "3379" };
        private static readonly string[] NodeClipEndFrames = { "3478", "3477", "3479" };
        private static readonly string[] NodeClipSeeds = { "521:3447", "521:3448", "521:3450" };

        /// <summary>Picks the per-iteration seed switch over one seed for the whole loop.</summary>
        private const string NodeLoopSeedSwitch = "521:3451";

        // Latent upscale: the 2x re-sample that turns the draft into the finished frames. The loop needs
        // the same answer twice — once for its own pass, once to know whether the frames handed to it
        // are already at the finished size.
        private const string NodeBaseDetail = "16:4194";
        private static readonly string[] NodeLoopDetail = { "521:4209", "521:4214" };

        // The two samplers of each half, and the sigma sources they can be pointed at. With the latent
        // upscale on, the draft runs the first four steps of an unshifted 8-step schedule (1.0 -> 0.5) and
        // the finish runs three fixed sigmas; with it off there is no finish pass at all, so the draft
        // sampler takes the full 8-step shifted schedule and denoises to zero on its own.
        private const string NodeBaseSampler = "16:487";
        private const string NodeLoopSampler = "521:519";
        private const string NodeDraftSigmas = "draft_split";      // SplitSigmas.high_sigmas
        private const string NodeBaseFullSigmas = "16:485";        // BasicScheduler, 8 steps, shifted
        private const string NodeLoopFullSigmas = "521:510";

        // The 2x factor written into the graph in the three places that have to agree about it.
        private const string NodeBaseUpscaler = "16:4187";
        private const string NodeLoopUpscaler = "521:4202";
        private static readonly string[] NodeLoopFinishDims = { "521:4212", "521:4213" };

        /// <summary>Sol-Attn sparse attention, on the one MODEL wire both halves share.</summary>
        private const string NodeSparseAttention = "478:473";

        // The RTX super-resolution and audio-enhancement switches exist on both halves, but only the half
        // that owns the saved sink may run them: on the base pass they would feed the loop upscaled frames
        // and twice-enhanced audio.
        private const string NodeBaseUpscale = "16:3391";
        private const string NodeLoopUpscale = "521:3393";
        private const string NodeBaseAudio = "16:3442";
        private const string NodeLoopAudio = "521:3444";

        // ── Input state ────────────────────────────────────────────────────────
        private string _draftIdea = string.Empty;
        private string _selectedAspectRatio = H3Canvas.AutoAspect;
        private double _megapixels = 0.7;
        private long _seed = -1;
        private OverlapOption _overlap;
        private bool _useSparseAttention;
        private bool _useLatentUpscale = true;
        private bool _useRtxUpscale;
        private bool _useAudioEnhancement = true;
        private bool _isAnalyzing;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _analyzeCts;

        private readonly ObservableCollection<MiniMaxFflfQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;
        private bool _isProcessingQueue;
        private string _queueStatus = string.Empty;

        private readonly GenerationProgressTracker _progressTracker;
        private string _generationTimer = string.Empty;

        // A loaded folder of keyframes, and where in it the form currently sits. A chain holds five
        // keyframes at most, so a longer folder is walked as a series of takes.
        private readonly List<string> _folderImages = new();
        private string _folderPath = string.Empty;
        private int _folderOffset;
        private int _takeNumber;
        private int _clipsPerTake = 3;
        private bool _isQueueingFolder;
        private bool _folderMemoryConfirmed;

        /// <summary>
        /// Stamped onto every take queued off the currently loaded folder, so the drain loop can tell one
        /// folder's takes apart from anything else in the queue and join them when the last lands. Reset
        /// by <see cref="LoadFolder"/>, so re-loading a folder starts a new run rather than joining the
        /// new takes onto the old ones.
        /// </summary>
        private string _folderRunId = string.Empty;

        /// <summary>
        /// Seconds this box takes per Gpx of one pass, learned from the runs it has already done.
        ///
        /// <para>Re-measured on 25 Aug under the draft-then-finish scheme, because the old 604 was against
        /// a Gpx that counted the 1.5× detail tensor and 17 full-canvas step-equivalents of sampling: two
        /// 10s clips finishing at 1152×640 (0.482 weighted Gpx) rendered in 3m22s with the models already
        /// resident, which is 336 s/Gpx — above the I2V tab's 270, because an FL2VA pass carries a second
        /// conditioning image and this graph resizes the carried context on top. Only the first minute of
        /// a run leans on it; from the first sampler step the estimate is re-derived from the run
        /// itself.</para>
        /// </summary>
        private static double _secondsPerGpx = 336;

        private static string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "minimax_fflf_queue.json");

        public MiniMaxFflfViewModel(
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
            _overlap = OverlapOptions[1];

            _progressTracker = new GenerationProgressTracker(
                p => OnUiThread(() => ProcessingProgress = p),
                s => OnUiThread(() => ProcessingStatus = s),
                t => OnUiThread(() => GenerationTimer = t));

            OpeningFrame = new MiniMaxFflfFrame("Opening frame");
            OpeningFrame.Changed += (_, _) => OnOpeningFrameChanged();

            BrowseFrameCommand = new RelayCommand<MiniMaxFflfFrame>(async f => await BrowseFrameAsync(f));
            ClearFrameCommand = new RelayCommand<MiniMaxFflfFrame>(f => f?.Clear());
            LoadFolderCommand = new RelayCommand(async () => await BrowseFolderAsync());
            QueueFolderCommand = new RelayCommand(async () => await QueueWholeFolderAsync(),
                                                  () => HasFolder && !_isQueueingFolder && !IsAnalyzing);
            NextTakeCommand = new RelayCommand(() => ApplyTake(_folderOffset + Clips.Count, _takeNumber + 1),
                                               () => CanAdvanceFolder);
            PreviousTakeCommand = new RelayCommand(() => ApplyTake(Math.Max(0, _folderOffset - ClipsPerTake), _takeNumber - 1),
                                                   () => CanRewindFolder);
            AddClipCommand = new RelayCommand(AddClip, () => Clips.Count < MaxClips);
            RemoveClipCommand = new RelayCommand<MiniMaxFflfClip>(RemoveClip);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(onlyEmpty: false), () => CanAnalyze);
            GenerateCommand = new RelayCommand(AddToQueue, () => CanGenerate);
            CancelCommand = new RelayCommand(StopQueue, () => IsProcessing || IsProcessingQueue);
            RemoveQueueItemCommand = new RelayCommand<MiniMaxFflfQueueItem>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => HasQueueItems);
            StartQueueCommand = new RelayCommand(() => _ = ProcessQueueAsync(),
                                                 () => HasPendingItems && !IsProcessingQueue);
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(ReprocessAllFailed, () => HasFailedItems);

            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            RandomSeedCommand = new RelayCommand(() => Seed = System.Random.Shared.NextInt64(0, int.MaxValue));

            // Both of these notify commands, so neither may run before the commands exist: clip 1 is
            // the base pass and always exists, and restoring a queue re-raises every CanExecute.
            AddClip();
            _queue.CollectionChanged += (_, _) => UpdateQueueStatus();
            LoadQueueFromFile();

            AddLog("MiniMax FFLF initialized");
        }

        #region Commands

        public RelayCommand<MiniMaxFflfFrame> BrowseFrameCommand { get; }
        public RelayCommand<MiniMaxFflfFrame> ClearFrameCommand { get; }
        public RelayCommand LoadFolderCommand { get; }
        public RelayCommand QueueFolderCommand { get; }
        public RelayCommand NextTakeCommand { get; }
        public RelayCommand PreviousTakeCommand { get; }
        public RelayCommand AddClipCommand { get; }
        public RelayCommand<MiniMaxFflfClip> RemoveClipCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand RandomSeedCommand { get; }
        public RelayCommand<MiniMaxFflfQueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StartQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }

        #endregion

        #region Keyframe chain

        /// <summary>The picture at 0.00s — the first frame of clip 1.</summary>
        public MiniMaxFflfFrame OpeningFrame { get; }

        /// <summary>The chain, in order. Clip 1 is the base pass; the rest are the loop's iterations.</summary>
        public ObservableCollection<MiniMaxFflfClip> Clips { get; } = new();

        /// <summary>Every keyframe in take order — the opening frame, then each clip's end frame.</summary>
        public IReadOnlyList<MiniMaxFflfFrame> ChainFrames =>
            new[] { OpeningFrame }.Concat(Clips.Select(c => c.EndFrame)).ToList();

        public bool HasOpeningFrame => OpeningFrame.HasImage;

        /// <summary>
        /// Lets the Video Generator hand a picture straight over from the Image Generator: it lands on
        /// the opening frame, the one the take is built out of.
        /// </summary>
        public string PrimaryReferencePath
        {
            get => OpeningFrame.Path;
            set => OpeningFrame.Path = value;
        }

        private void OnOpeningFrameChanged()
        {
            OnPropertyChanged(nameof(HasOpeningFrame));
            OnPropertyChanged(nameof(ChainFrames));
            OnPropertyChanged(nameof(ResolvedAspectRatio));
            OnPropertyChanged(nameof(ChainSummary));
            OnPropertyChanged(nameof(PrimaryReferencePath));
            OnCanExecuteChanged();
        }

        private void AddClip()
        {
            if (Clips.Count >= MaxClips) return;

            var clip = new MiniMaxFflfClip(Clips.Count + 1);
            clip.Changed += (_, _) =>
            {
                OnPropertyChanged(nameof(TotalLengthSummary));
                OnPropertyChanged(nameof(ChainFrames));
                OnPropertyChanged(nameof(ChainSummary));
                RaiseMemoryEstimate();
                OnCanExecuteChanged();
            };
            // Changed fires for the length and the end frame. Without this, typing a prompt satisfied
            // CanGenerate but never re-raised it, so the button stayed greyed out on a complete form.
            clip.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MiniMaxFflfClip.Prompt)) OnCanExecuteChanged();
            };
            Clips.Add(clip);
            AfterChainChanged();
        }

        private void RemoveClip(MiniMaxFflfClip? clip)
        {
            // Clip 1 is the base pass — the take cannot exist without it.
            if (clip == null || clip.Index == 1 || Clips.Count <= 1) return;
            if (!Clips.Remove(clip)) return;
            for (var i = 0; i < Clips.Count; i++) Clips[i].Index = i + 1;
            AfterChainChanged();
        }

        private void AfterChainChanged()
        {
            OnPropertyChanged(nameof(ChainFrames));
            OnPropertyChanged(nameof(ChainSummary));
            OnPropertyChanged(nameof(HasExtensions));
            OnPropertyChanged(nameof(TotalLengthSummary));
            RaiseMemoryEstimate();
            // How far the next take starts from depends on how many clips this one holds.
            RaiseFolderState();
            AddClipCommand.NotifyCanExecuteChanged();
            OnCanExecuteChanged();
        }

        /// <summary>Continuing clips — everything after the base pass.</summary>
        public bool HasExtensions => Clips.Count > 1;

        public string ChainSummary
        {
            get
            {
                var filled = ChainFrames.Count(f => f.HasImage);
                var total = ChainFrames.Count;
                return filled == total
                    ? $"{total} keyframes · {Clips.Count} clip{(Clips.Count == 1 ? "" : "s")}"
                    : $"{filled} of {total} keyframes set";
            }
        }

        /// <summary>
        /// Frames one clip renders. H3 snaps length to the 17k+5 grid at 24 fps, and denoises the whole
        /// clip jointly — which is why seconds, not resolution, is usually what runs the card out of
        /// memory.
        /// </summary>
        private static int FrameCount(int seconds)
        {
            var f = Math.Max(5, seconds * 24);
            return f + (5 - (f % 17)) % 17;
        }

        /// <summary>
        /// Rough size of the largest tensor a single pass has to hold, in billions of pixel-positions:
        /// finished canvas area × frames. The finish sampler is what sets it — the draft runs at a
        /// quarter of the area and is never the clip's peak. Clips run one after another, so the peak is
        /// set by the largest clip, not by the total.
        /// </summary>
        public double EstimatedPeakGpx
        {
            get
            {
                var area = SampledArea(ResolvedAspectRatio, Megapixels, UseLatentUpscale);
                var peak = 0;
                for (var i = 0; i < Clips.Count; i++)
                    peak = Math.Max(peak, FrameCount(Clips[i].Seconds) + (i == 0 ? 0 : Overlap.Frames));
                return area * peak / 1e9;
            }
        }

        /// <summary>
        /// Decoded frames the continuation loop holds in <b>system</b> RAM, in GB. A second, independent
        /// limit from <see cref="EstimatedPeakGpx"/>: the loop concatenates every clip's frames into one
        /// growing batch, so this scales with the take's <i>total</i> length. VRAM can be comfortable
        /// while this is fatal — a big enough batch gets ComfyUI killed by the kernel, not just the job.
        /// </summary>
        public double EstimatedFrameRamGb
        {
            get
            {
                var area = SampledArea(ResolvedAspectRatio, Megapixels, UseLatentUpscale);
                var totalFrames = 0;
                for (var i = 0; i < Clips.Count; i++)
                    totalFrames += FrameCount(Clips[i].Seconds) + (i == 0 ? 0 : Overlap.Frames);
                return area * totalFrames * 12 / 1e9;
            }
        }

        /// <summary>The worse of the two limits, named. Calibrated on real outcomes on this box.</summary>
        public string MemoryWarning
        {
            get
            {
                var gpx = EstimatedPeakGpx;
                var ram = EstimatedFrameRamGb;
                var head = $"~{gpx:0.00} Gpx/pass · ~{ram:0.0} GB frames";

                if (ram > FrameRamOverLimitGb)
                    return $"⚠ {head} — bigger than anything that has finished on this box. Fewer clips " +
                           "per take is the biggest lever; then shorter clips, or lower quality.";
                if (gpx > VramOverLimitGpx)
                    return $"⚠ {head} — this has run out of VRAM on 24 GB.";
                if (ram > FrameRamRiskyGb)
                    return $"{head} — needs a server with room: a take this size has both completed and "
                           + "OOM-killed ComfyUI, depending on what was already resident. Pre-flight "
                           + "unloads models before it starts.";
                if (gpx > VramRiskyGpx)
                    return $"{head} — close to the VRAM limit on 24 GB.";
                return head;
            }
        }

        /// <summary>Where the form starts warning, and where it calls it over the line — the 24 GB card's
        /// measured limit (see <see cref="GpxPerVramGb"/>), less a margin.</summary>
        private const double VramRiskyGpx = 0.40;
        private const double VramOverLimitGpx = 0.435;

        /// <summary>
        /// Where the concatenated frame batch starts to threaten the host, in GB.
        ///
        /// <para>Advisory, because the size alone does not decide it — what the server already has
        /// resident does. Measured on 21 Aug, both halves on the same 94 GB box and the same take (four
        /// clips, 40s, 0.4 MP, a <b>12.4 GB</b> frame estimate): with 62.4 GB free at idle it killed the
        /// ComfyUI process outright, in <c>ImageBatchExtendWithOverlap</c>, the node that blends the
        /// growing batch — and then rendered in 15m27s on the restarted server. The difference is the
        /// ~32 GB of weights and cache already staged in the same pool the frames need. Which is why the
        /// real gate is <see cref="PreflightAsync"/>, reading live free RAM and unloading models before
        /// it refuses anything.</para>
        /// </summary>
        private const double FrameRamRiskyGb = 10.0;
        private const double FrameRamOverLimitGb = 16.0;

        /// <summary>
        /// What to multiply the frame estimate by before comparing it against free host RAM. Six, not the
        /// two-and-a-half this started at: the blend step holds source, new and result at once, the loop
        /// keeps its own carried copy, this graph resizes the clip once more on the way in — and on top of
        /// all of that the staged model weights land in the same pool. See <see cref="FrameRamRiskyGb"/>
        /// for the run that set this number.
        /// </summary>
        private const double PreflightPeakFactor = 6.0;

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
                var total = Clips.Sum(c => c.Seconds);
                return Clips.Count == 1
                    ? $"≈ {total}s in one pass"
                    : $"≈ {total}s over {Clips.Count} clips";
            }
        }

        private async Task BrowseFrameAsync(MiniMaxFflfFrame? frame)
        {
            if (frame == null) return;

            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                $"Select {frame.Label.ToLowerInvariant()}",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: "minimaxfflf.keyframe");

            if (path == null) return;
            frame.Path = path;
            AddLog($"{frame.Label}: {Path.GetFileName(path)}");
        }

        #endregion

        #region Folder of keyframes

        private async Task BrowseFolderAsync()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var folder = await _fileDialogService.OpenFolderDialogAsync(
                "Select a folder of keyframes", initialDir, persistKey: "minimaxfflf.folder");
            if (folder == null) return;
            LoadFolder(folder);
        }

        /// <summary>
        /// Loads a folder of stills as the keyframe chain — what Story Image Q's keyframe runs produce.
        /// Images are ordered by creation time (then filename) and laid straight onto the chain: the
        /// first becomes the opening frame and each one after it becomes the keyframe a clip has to
        /// arrive at, so N images give N-1 clips.
        ///
        /// <para>A chain holds five keyframes at most, so a longer folder is walked as a series of
        /// <b>takes</b>. Each take starts on the keyframe the one before it ended on, which is what makes
        /// the takes join: the last picture of take 1 is the first frame of take 2. They are also joined
        /// literally — every take queued off one loaded folder carries the same
        /// <see cref="MiniMaxFflfQueueItem.FolderRunId"/>, and the drain loop FFmpeg-concatenates them
        /// into one video when the last of them completes.</para>
        ///
        /// <para>Must be called on the UI thread — the Image Generator calls it directly.</para>
        /// </summary>
        public void LoadFolder(string folder)
        {
            if (IsAnalyzing) return;   // do not pull the frames out from under a running Analyze
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
            var images = Directory.EnumerateFiles(folder)
                .Where(f => exts.Contains(Path.GetExtension(f)))
                .OrderBy(ImageOrderKey)
                .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (images.Count < 2)
            {
                MessageBox.Show(
                    $"Found {images.Count} image(s) in:\n{folder}\n\nNeed at least 2 — an opening frame "
                    + "and the keyframe the first clip has to reach.",
                    "Not enough images", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _folderImages.Clear();
            _folderImages.AddRange(images);
            _folderPath = folder;
            _folderRunId = $"{Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))}_" +
                           DateTime.Now.ToString("yyyyMMdd_HHmmss");

            ApplyTake(0, 1);
            AddLog($"Folder loaded: {folder} — {images.Count} keyframes → {images.Count - 1} clip(s) "
                   + $"over {TakeCount} take(s)");
        }

        /// <summary>
        /// Points the chain at one window of the loaded folder: <paramref name="offset"/> is the opening
        /// frame, and the next four images (at most) become the clips' end keyframes.
        /// </summary>
        private void ApplyTake(int offset, int takeNumber)
        {
            if (_folderImages.Count < 2) return;

            _folderOffset = Math.Clamp(offset, 0, _folderImages.Count - 2);
            _takeNumber = Math.Max(1, takeNumber);
            var endFrames = _folderImages.Skip(_folderOffset + 1).Take(ClipsPerTake).ToList();

            SetClipCount(endFrames.Count);
            OpeningFrame.Path = _folderImages[_folderOffset];
            for (var i = 0; i < endFrames.Count; i++)
            {
                Clips[i].EndFrame.Path = endFrames[i];
                // The two frames are what a prompt describes the path between, so one written for the
                // previous take says nothing true about this one.
                Clips[i].Prompt = string.Empty;
            }

            RaiseFolderState();
            AddLog($"{FolderStatus} — press Analyze to write this take's prompts");
        }

        /// <summary>Grows or shrinks the chain to <paramref name="count"/> clips, renumbering as it goes.</summary>
        private void SetClipCount(int count)
        {
            count = Math.Clamp(count, 1, MaxClips);
            while (Clips.Count > count) Clips.RemoveAt(Clips.Count - 1);
            while (Clips.Count < count) AddClip();
            for (var i = 0; i < Clips.Count; i++) Clips[i].Index = i + 1;
            AfterChainChanged();
        }

        /// <summary>A copied or moved file can carry a creation time newer than its real write time, so
        /// the earlier of the two is what actually orders a rendered sequence.</summary>
        private static DateTime ImageOrderKey(string path)
        {
            try
            {
                var created = File.GetCreationTime(path);
                var written = File.GetLastWriteTime(path);
                return created <= written ? created : written;
            }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// Analyzes and queues every remaining take of the loaded folder in one go: apply take, write
        /// the prompts it is missing, enqueue, move on. One queue item per take, and the queue drains
        /// them one at a time — so a ten-keyframe folder becomes one finished video, joined by
        /// <see cref="CompleteFolderRunAsync"/> when the last take lands, without standing over it.
        /// </summary>
        private async Task QueueWholeFolderAsync()
        {
            if (!HasFolder || _isQueueingFolder || IsAnalyzing) return;

            _isQueueingFolder = true;
            _folderMemoryConfirmed = false;
            OnCanExecuteChanged();
            var queued = 0;
            try
            {
                while (true)
                {
                    var offsetBefore = _folderOffset;

                    // Only the clips that have nothing yet: a take the user already analyzed (or wrote
                    // by hand) keeps its prompts.
                    if (Clips.Any(c => string.IsNullOrWhiteSpace(c.Prompt)))
                        await AnalyzeAsync(onlyEmpty: true);

                    if (!CanGenerate)
                    {
                        AddLog($"Folder run stopped at take {_takeNumber}: {GenerateBlockedReason}");
                        break;
                    }

                    if (!TryAddToQueue())   // advances to the next take on its own while one is left
                    {
                        AddLog($"Folder run stopped at take {_takeNumber}.");
                        break;
                    }
                    queued++;

                    if (_folderOffset == offsetBefore) break;   // that was the last take
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR queueing the folder: {ex.Message}");
            }
            finally
            {
                _isQueueingFolder = false;
                OnCanExecuteChanged();
                AddLog($"Folder run: {queued} take(s) queued");
            }
        }

        public bool HasFolder => _folderImages.Count > 0;

        /// <summary>
        /// Keyframes a single take spans, minus one. Four clips is what the graph's loop can address, and
        /// four does render — 40s at 0.4 MP took 15m27s on 21 Aug — but every clip's frames stay in host
        /// RAM until the take is written out, and that same take killed ComfyUI earlier the same evening
        /// on a server that already had 32 GB resident. Three is the default because it leaves room for
        /// whatever else the box is holding; raise it when the server is fresh.
        /// </summary>
        public IReadOnlyList<int> ClipsPerTakeOptions { get; } = new[] { 1, 2, 3, 4 };

        public int ClipsPerTake
        {
            get => _clipsPerTake;
            set
            {
                var clamped = Math.Clamp(value, 1, MaxClips);
                if (_clipsPerTake == clamped) return;
                _clipsPerTake = clamped;
                OnPropertyChanged();
                // Re-cut the chain around the take the form is already on.
                if (HasFolder) ApplyTake(_folderOffset, _takeNumber);
                else RaiseFolderState();
            }
        }

        /// <summary>Takes the folder is worth, at five keyframes each.</summary>
        public int TakeCount => _folderImages.Count < 2
            ? 0
            : (int)Math.Ceiling((_folderImages.Count - 1) / (double)ClipsPerTake);

        public int CurrentTake => HasFolder ? _takeNumber : 0;

        /// <summary>There are keyframes past this take's last one.</summary>
        public bool CanAdvanceFolder => HasFolder && _folderOffset + Clips.Count < _folderImages.Count - 1;

        public bool CanRewindFolder => HasFolder && _folderOffset > 0;

        public string FolderStatus
        {
            get
            {
                if (!HasFolder) return string.Empty;
                var last = _folderOffset + Clips.Count + 1;
                return $"📂 {Path.GetFileName(_folderPath.TrimEnd(Path.DirectorySeparatorChar))} · "
                       + $"take {CurrentTake} of {TakeCount} · keyframes {_folderOffset + 1}–{last} "
                       + $"of {_folderImages.Count}";
            }
        }

        private void RaiseFolderState()
        {
            OnPropertyChanged(nameof(HasFolder));
            OnPropertyChanged(nameof(FolderStatus));
            OnPropertyChanged(nameof(CurrentTake));
            OnPropertyChanged(nameof(TakeCount));
            OnPropertyChanged(nameof(CanAdvanceFolder));
            OnPropertyChanged(nameof(CanRewindFolder));
            NextTakeCommand.NotifyCanExecuteChanged();
            PreviousTakeCommand.NotifyCanExecuteChanged();
        }

        #endregion

        #region Settings

        /// <summary>The idea Analyze paces across the chain. Each clip's own prompt is written from it.</summary>
        public string DraftIdea
        {
            get => _draftIdea;
            set { if (_draftIdea != value) { _draftIdea = value; OnPropertyChanged(); } }
        }

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
                RaiseMemoryEstimate();
            }
        }

        /// <summary>
        /// The aspect actually sent to ComfyUI — the picked one, or the opening frame's closest match.
        /// Worth getting right on this tab in particular: the keyframes are the literal first and last
        /// frames, so a canvas that does not match their proportions squeezes them.
        /// </summary>
        public string ResolvedAspectRatio
        {
            get
            {
                if (SelectedAspectRatio != H3Canvas.AutoAspect) return SelectedAspectRatio;
                var source = ChainFrames.FirstOrDefault(f => f.HasImage);
                if (source == null) return "16:9 (Widescreen)";
                var (w, h) = MiniMaxFflfFrame.MeasurePixels(source.Path);
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
            // Sizes are what this graph really produces at 16:9 — what the draft ResolutionSelector
            // returns for a quarter of the target, doubled — not the megapixel target rounded by eye.
            // Other aspects land within a few percent of the same pixel count, except 1:1, ~5% smaller.
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

        public long Seed
        {
            get => _seed;
            set { if (_seed != value) { _seed = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// How many frames of the previous clip a continuation re-generates and blends over. The options
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
            set
            {
                if (_overlap == value || value == null) return;
                _overlap = value;
                OnPropertyChanged();
                RaiseMemoryEstimate();
            }
        }

        /// <summary>
        /// Routes the model through Sol-Attn's sparse attention instead of the dense backend. H3 denoises
        /// the whole clip in one pass, so attention cost grows with the square of frames × canvas area.
        /// The patch skips the first 20% and last 10% of steps, where the layout and the final detail are
        /// decided, and engages only past 4096 tokens.
        /// </summary>
        public bool UseSparseAttention
        {
            get => _useSparseAttention;
            set { if (_useSparseAttention != value) { _useSparseAttention = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// The draft-then-finish scheme: four sampler steps at half the width and half the height, a 2×
        /// pass through the MiniMax H3 3D latent upscaler, then three fixed-sigma steps at the finished
        /// size. Off, the clip instead denoises the full canvas over eight steps in one go — slower, and
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

        /// <summary>Every keyframe in the chain is set, so every clip has both of its ends.</summary>
        public bool ChainIsComplete => OpeningFrame.HasImage && Clips.All(c => c.HasEndFrame);

        /// <summary>
        /// Deliberately not gated on <see cref="VideoProcessingBaseViewModel.IsProcessing"/>. Analyze
        /// talks to the llama-server, which is a different machine from ComfyUI, so writing the next
        /// prompt costs the running render nothing.
        /// </summary>
        public bool CanAnalyze => ChainIsComplete && !IsAnalyzing;

        /// <summary>
        /// Whether the form describes a runnable job — deliberately not whether the GPU is free. Enqueuing
        /// is a local operation, so a render already in flight must not block it.
        /// </summary>
        public bool CanGenerate =>
            ChainIsComplete && !IsAnalyzing && Clips.All(c => !string.IsNullOrWhiteSpace(c.Prompt));

        /// <summary>Why Add to Queue is disabled, or empty when it is not.</summary>
        public string GenerateBlockedReason
        {
            get
            {
                if (!OpeningFrame.HasImage) return "Set the opening frame — the picture at 0.00s.";

                var missingFrames = Clips.Where(c => !c.HasEndFrame).Select(c => c.Index.ToString()).ToList();
                if (missingFrames.Count > 0)
                    return missingFrames.Count == 1
                        ? $"Clip {missingFrames[0]} has no end keyframe. FL2VA needs both ends of every clip."
                        : $"Clips {string.Join(", ", missingFrames)} have no end keyframe.";

                if (IsAnalyzing) return "Waiting for Analyze to finish…";

                var missingPrompts = Clips.Where(c => string.IsNullOrWhiteSpace(c.Prompt))
                                          .Select(c => c.Index.ToString()).ToList();
                if (missingPrompts.Count > 0)
                    return missingPrompts.Count == 1
                        ? $"Clip {missingPrompts[0]} needs a prompt — press Analyze, or write one."
                        : $"Clips {string.Join(", ", missingPrompts)} need prompts — press Analyze, or write them.";

                return string.Empty;
            }
        }

        #endregion

        #region Analysis (keyframe pairs → FL2VA prompts)

        /// <summary>
        /// Writes one FL2VA prompt per clip, walking the chain pair by pair.
        ///
        /// <para>One call per clip rather than one call for the whole take: the FL2VA system prompt is
        /// written for exactly two images — "this is the frame at 0.00s, this is the frame at S.SS" — and
        /// a pair is also what each pass really gets. The previous clip's prompt rides along as context so
        /// the chain reads as one take rather than a set of unrelated shots.</para>
        /// </summary>
        private async Task AnalyzeAsync(bool onlyEmpty = false)
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
                    MessageBox.Show("No LM Studio / llama-server model available. Ensure the server is running and a model is loaded.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", SystemPromptFile);
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"System prompt not found: {promptFilePath}");
                var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

                var draft = DraftIdea.Trim();
                AddLog($"Writing {Clips.Count} FL2VA prompt(s) → {_lmStudioService.DescribeTarget(model)}");

                // Sequential, and it has to be: clip 3's context is clip 2's prompt, which may have just
                // been written by the call before it.
                foreach (var clip in Clips.ToList())
                {
                    token.ThrowIfCancellationRequested();
                    if (onlyEmpty && !string.IsNullOrWhiteSpace(clip.Prompt)) continue;

                    var startPath = StartFrameFor(clip);
                    if (string.IsNullOrEmpty(startPath) || !clip.HasEndFrame)
                    {
                        AddLog($"WARNING: clip {clip.Index} is missing one of its frames — skipped");
                        continue;
                    }

                    var previous = clip.Index <= 1 ? string.Empty : Clips[clip.Index - 2].Prompt;
                    var reply = await _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                        model,
                        new List<string> { startPath, clip.EndFrame.Path },
                        BuildAnalyzeRequest(clip, previous, draft),
                        systemPrompt,
                        maxTokens: 3000,
                        cancellationToken: token);

                    var text = CleanLLMOutput(reply)?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        AddLog($"WARNING: clip {clip.Index} came back empty");
                        continue;
                    }

                    clip.Prompt = text;
                    AddLog($"Clip {clip.Index} prompt written ({text.Length} chars)");
                    OnCanExecuteChanged();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR during analysis: {ex.Message}");
                MessageBox.Show($"Analysis failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
                OnCanExecuteChanged();
            }
        }

        /// <summary>The picture a clip starts on: the opening frame, or the keyframe the clip before it
        /// had to arrive at — which is what its first frames really look like.</summary>
        private string StartFrameFor(MiniMaxFflfClip clip) =>
            clip.Index <= 1 ? OpeningFrame.Path : Clips[clip.Index - 2].EndFrame.Path;

        /// <summary>
        /// The user message for one clip: which image is which end, how long the clip runs, the draft
        /// idea, and — past clip 1 — the prompt of the clip this one continues out of.
        /// </summary>
        private string BuildAnalyzeRequest(MiniMaxFflfClip clip, string previous, string draft)
        {
            var startLabel = clip.Index <= 1 ? "the opening frame" : $"keyframe {clip.Index}";
            var lines = new List<string>
            {
                $"Picture 1 is {startLabel} — the frame at 0.00s of this clip.",
                $"Picture 2 is keyframe {clip.Index + 1} — the frame at {clip.Seconds:0.00}s, where this clip ends.",
                $"Target duration: {clip.Seconds} seconds (write {clip.Seconds:0.00} in the alignment line).",
                string.Empty
            };

            if (Clips.Count > 1)
            {
                lines.Add($"This is clip {clip.Index} of {Clips.Count} in one continuous take running "
                          + $"{string.Join("s + ", Clips.Select(c => c.Seconds))}s.");
                lines.Add("Write ONLY this clip: the part of the idea that falls between these two pictures, "
                          + "and nothing from any other clip. Its timestamps are local to this clip and start "
                          + "at 0.00s.");
                lines.Add(string.Empty);
            }

            lines.Add("Idea from the user for the whole take:");
            lines.Add(string.IsNullOrWhiteSpace(draft)
                ? "(none — work out the single transformation that separates the two pictures and write that, "
                  + "adding nothing beyond it)"
                : draft);

            if (!string.IsNullOrWhiteSpace(previous))
            {
                lines.Add(string.Empty);
                lines.Add("The clip immediately before yours is below. It is CONTEXT ONLY — your clip starts "
                          + "where it ends and must carry the action forward. Do not repeat its events and do "
                          + "not copy its description; keep wardrobe, setting and lighting identical so nothing "
                          + "drifts across the join:");
                lines.Add(previous.Trim());
            }

            return string.Join("\n", lines);
        }

        #endregion

        #region Queue

        public ObservableCollection<MiniMaxFflfQueueItem> Queue => _queue;

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

        /// <summary>"⏱ 04:12 · ~10:18 left" while a render runs, then "✓ 14:51" once it lands.</summary>
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
        /// Every setting is copied, not referenced: the form stays editable while jobs drain.
        /// </summary>
        private void AddToQueue() => TryAddToQueue();

        /// <summary>Enqueues the form; false when the user declined the memory warning.</summary>
        private bool TryAddToQueue()
        {
            if (!CanGenerate) return false;

            // A host-RAM blowout does not fail one job — the kernel kills ComfyUI, which loses whatever
            // was rendering and every item queued behind it. Worth a confirm rather than a log line.
            // Asked once per folder run rather than once per take, so a batch is not a wall of dialogs.
            if (IsMemoryOverLimit && !(_isQueueingFolder && _folderMemoryConfirmed))
            {
                var proceed = MessageBox.Show(
                    $"{MemoryWarning}\n\n" +
                    "The continuation loop keeps every clip's frames in system RAM at once, and the " +
                    "model weights are staged in the same pool — so a long take can exhaust the server " +
                    "rather than just this job, and that takes the whole queue down with it. It has " +
                    "already happened on this box.\n\n" +
                    "Fewer clips per take is the biggest lever; then shorter clips or lower " +
                    "quality.\n\n" +
                    (_isQueueingFolder ? "Queue this and every remaining take anyway?" : "Queue it anyway?"),
                    "Likely to run out of memory",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (proceed != MessageBoxResult.Yes) return false;
                if (_isQueueingFolder) _folderMemoryConfirmed = true;
            }

            var item = new MiniMaxFflfQueueItem
            {
                OpeningFramePath = OpeningFrame.Path,
                EndFramePaths = Clips.Select(c => c.EndFrame.Path).ToList(),
                Prompts = Clips.Select(c => c.Prompt.Trim()).ToList(),
                Seconds = Clips.Select(c => c.Seconds).ToList(),
                AspectRatio = ResolvedAspectRatio,
                Megapixels = Megapixels,
                Seed = Seed,
                OverlapFrames = Overlap.Frames,
                UseSparseAttention = UseSparseAttention,
                UseLatentUpscale = UseLatentUpscale,
                UseRtxUpscale = UseRtxUpscale,
                UseAudioEnhancement = UseAudioEnhancement,

                // Takes of one folder are consecutive windows of the same chain — take 2 opens on the
                // keyframe take 1 closed on — so they are one video cut into queue-sized pieces. Stamped
                // whether the takes were queued by ▶ Queue folder or added one at a time, because both
                // walk the same folder in the same order.
                FolderRunId = HasFolder ? _folderRunId : null,
                FolderName = HasFolder
                    ? Path.GetFileName(_folderPath.TrimEnd(Path.DirectorySeparatorChar))
                    : string.Empty,
                TakeNumber = HasFolder ? _takeNumber : 0,
                TakeCount = HasFolder ? TakeCount : 0,
            };

            _queue.Add(item);
            AddLog($"Queued: {item.DisplayText}");
            UpdateQueueStatus();
            SaveQueueToFile();

            // Queueing stages the job; it does not start it. Add to Queue and Generate are separate
            // buttons so a run can be built up prompt by prompt and then rendered in one pass — the
            // GPU is only claimed when ▶ Generate (StartQueueCommand) is pressed.
            AddLog(IsProcessingQueue
                ? "Added to the queue — the queue is already running, so this is picked up when the item " +
                  "on the GPU finishes."
                : "Added to the queue — nothing is rendering yet. Press ▶ Generate to start.");

            // Walking a folder, the obvious next thing is the next take, and the item just queued has
            // its own frozen copy of everything — so nothing is lost by moving the form on.
            if (CanAdvanceFolder) ApplyTake(_folderOffset + Clips.Count, _takeNumber + 1);
            return true;
        }

        private void RemoveQueueItem(MiniMaxFflfQueueItem? item)
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
        /// Asks ComfyUI to unload models and release cached memory, then reports the new headroom. Most of
        /// the host RAM in use is staged model weights, reloaded on demand — about a minute of load time
        /// against a run that would otherwise take the whole server down. Only ever called with the
        /// workflow-coordinator lease held.
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
        /// The host-RAM check carries a 2.5× factor: the estimate is the finished batch, but the blend step
        /// holds source, new and result at once, and getting this wrong kills ComfyUI rather than the job.
        /// </summary>
        private async Task<string?> PreflightAsync(MiniMaxFflfQueueItem item, CancellationToken token)
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

            if (frameRamGb * PreflightPeakFactor > ramFree)
            {
                AddLog($"Pre-flight: {ramFree:0.0} GB free is not enough — unloading models to reclaim RAM…");
                var freed = await FreeServerMemoryAsync(token);
                if (freed != null)
                {
                    ramFree = freed.Value.RamFreeGb;
                    AddLog($"Pre-flight: {ramFree:0.0} GB RAM free after unloading "
                           + "(models will reload on demand, roughly a minute)");
                }

                if (frameRamGb * PreflightPeakFactor > ramFree)
                    return $"Not enough host RAM even after unloading models: the take needs roughly "
                           + $"{frameRamGb * PreflightPeakFactor:0.0} GB at peak ({frameRamGb:0.0} GB of "
                           + $"frames, plus the blend copies and the model weights staged beside them) and "
                           + $"the server has {ramFree:0.0} GB free. Fewer clips per take is the biggest "
                           + "lever; then shorter clips or lower quality.";
            }

            var gpxCeiling = vramTotal * GpxPerVramGb;
            if (gpx > gpxCeiling)
            {
                var (w, h) = ResolveCanvas(item.AspectRatio, item.Megapixels, item.UseLatentUpscale);
                var fits = Math.Floor(item.Megapixels * (gpxCeiling / gpx) * 20) / 20;
                var (fw, fh) = ResolveCanvas(item.AspectRatio, fits, item.UseLatentUpscale);
                return $"Too large for the GPU: {item.Megapixels:0.0} MP at {item.AspectRatio} resolves to "
                       + $"{w}×{h}, and the biggest clip then needs ~{gpx:0.00} Gpx against a limit of about "
                       + $"{gpxCeiling:0.00} Gpx on a {vramTotal:0.0} GB card. Drop the quality to "
                       + $"{fits:0.00} MP ({fw}×{fh}), or shorten the longest clip.";
            }

            if (gpx > gpxCeiling * 0.95)
                AddLog($"Pre-flight: ~{gpx:0.00} Gpx is within 5% of this card's ~{gpxCeiling:0.00} Gpx "
                       + "limit — it should run, but there is nothing spare.");

            return null;
        }

        /// <summary>Gpx one pass may reach per GB of VRAM — measured on the I2V tab, which runs the same
        /// samplers on the same model: 0.430 Gpx completed on a 23.5 GB card, 0.440 Gpx did not.</summary>
        private const double GpxPerVramGb = 0.0185;

        /// <summary>Host-RAM estimate for an already-queued item — the arithmetic behind
        /// <see cref="EstimatedFrameRamGb"/>, which reads the live form instead.</summary>
        private static double EstimateFrameRamGb(MiniMaxFflfQueueItem item)
        {
            var area = SampledArea(item.AspectRatio, item.Megapixels, item.UseLatentUpscale);
            var totalFrames = 0;
            for (var pass = 0; pass < item.PassCount; pass++) totalFrames += PassFrames(item, pass);
            return area * totalFrames * 12 / 1e9;
        }

        /// <summary>
        /// True when ComfyUI itself went away mid-run rather than the job failing. The kernel OOM killer
        /// leaves exactly this trace: the prompt is in neither the queue nor the history.
        /// </summary>
        private static bool IsServerDeath(Exception ex) =>
            ex.Message.IndexOf("no longer knows about this job", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("restarted or was killed mid-run", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Drains pending items one at a time. The workflow-coordinator lease is taken <b>per item</b>
        /// rather than around the loop, so a long queue does not lock every other tab out of ComfyUI.
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;

            IsProcessingQueue = true;
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;

            AddLog("Starting MiniMax FFLF queue...");
            try
            {
                MiniMaxFflfQueueItem? item;
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

                        // Exception-free by contract, cancellation included: this sits inside the same
                        // try as the render, and a join failure escaping it would be caught below as a
                        // render failure and push an already-rendered take back to Pending.
                        await CompleteFolderRunAsync(item, token);
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
                        item.ItemStatus = QueueItemStatus.Failed;
                        item.ErrorMessage = "ComfyUI was killed mid-run — almost always the host running "
                                          + "out of memory. Queue stopped.";
                        AddLog("=== QUEUE STOPPED ===");
                        AddLog("ComfyUI died mid-run. That is the host running out of RAM, not this job "
                               + "failing — the remaining items are untouched and still Pending.");
                        AddLog("Shorten the clips, drop one, or lower the quality, then press Start.");
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
        /// Pending — the ComfyUI run it belonged to did not survive the app closing. Never auto-starts.
        /// </summary>
        private void LoadQueueFromFile()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;
                var items = JsonSerializer.Deserialize<List<MiniMaxFflfQueueItem>>(File.ReadAllText(QueueFilePath));
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

        #region Folder-run join

        /// <summary>
        /// Runs after every completed take: when the take belonged to a folder run and it was the last of
        /// that run still outstanding, FFmpeg-joins the run's videos, in take order, into one file.
        ///
        /// <para>A single take is already joined inside ComfyUI — the continuation loop accumulates its
        /// clips with <c>ImageBatchExtendWithOverlap</c> and writes one video. What this joins is the
        /// takes: a folder of more keyframes than one chain holds is walked as several submissions, and
        /// without this the folder comes out as a set of separate files.</para>
        ///
        /// <para>Deliberately exception-free, cancellation included. It is called from the drain loop
        /// inside the same try as the render, whose catch would otherwise read a join failure as a render
        /// failure and push an already-rendered take back to Pending.</para>
        /// </summary>
        private async Task CompleteFolderRunAsync(MiniMaxFflfQueueItem finished, CancellationToken token)
        {
            try
            {
                if (string.IsNullOrEmpty(finished.FolderRunId)) return;

                // Take order, not queue order: a take re-queued after a failure sits at the end of the
                // queue but belongs where its keyframes are. Position breaks ties, so re-rendering one
                // take does not reorder the rest.
                var siblings = _queue
                    .Select((item, index) => (item, index))
                    .Where(x => x.item.FolderRunId == finished.FolderRunId)
                    .OrderBy(x => x.item.TakeNumber)
                    .ThenBy(x => x.index)
                    .Select(x => x.item)
                    .ToList();

                if (siblings.Count < 2) return;   // one take is already one video

                if (siblings.Any(x => x.ItemStatus != QueueItemStatus.Completed))
                {
                    // Nothing of the run left running means it is stuck on a failure rather than still
                    // rendering — say so, or the missing joined file looks like a silent bug.
                    var failed = siblings.Count(x => x.ItemStatus == QueueItemStatus.Failed);
                    if (failed > 0 && !siblings.Any(x => x.ItemStatus is QueueItemStatus.Pending
                                                                      or QueueItemStatus.Processing))
                        AddLog($"Folder run not joined: {failed} of {siblings.Count} take(s) failed. " +
                               "Retry them and the join runs when the last one lands.");
                    return;
                }

                var clips = siblings.Sum(x => x.PassCount);
                var seconds = siblings.Sum(x => x.TotalSeconds);
                // Queueing can stop short of the folder's end, so say what the join actually covers
                // rather than implying it is the whole folder.
                var expected = siblings.Max(x => x.TakeCount);
                var scope = expected > siblings.Count ? $"{siblings.Count} of {expected} take(s)"
                                                      : $"{siblings.Count} take(s)";
                AddLog($"=== Folder run complete: {scope}, {clips} clip(s), {seconds}s total ===");
                foreach (var take in siblings)
                    AddLog($"  take {take.TakeNumber}: {take.OutputImagePath}");

                await JoinFolderRunAsync(finished, siblings, token);
            }
            catch (Exception ex)
            {
                // Includes cancellation: every take is already on disk either way.
                AddLog($"Folder-run join skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Concatenates a finished folder run's videos into one MP4 beside them, and makes it the tab's
        /// current result so ▶ Play opens the whole thing rather than its last take. Best-effort — the
        /// individual takes are untouched and remain usable if the join cannot run.
        /// </summary>
        private async Task JoinFolderRunAsync(MiniMaxFflfQueueItem finished,
            IReadOnlyList<MiniMaxFflfQueueItem> takes, CancellationToken token)
        {
            var paths = takes.Select(t => t.OutputImagePath)
                             .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                             .Select(p => p!)
                             .ToList();

            if (paths.Count < takes.Count)
                AddLog($"Join: {takes.Count - paths.Count} take file(s) are missing from disk and are "
                       + "left out.");
            if (paths.Count < 2)
            {
                AddLog("Join skipped: fewer than two take files are available.");
                return;
            }

            var ffmpeg = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpeg))
            {
                AddLog("Join skipped: FFmpeg not found. The takes are separate files, in playback order.");
                return;
            }

            // Beside the takes, which already share this folder — the joined file sorts with them.
            var outputDir = Path.GetDirectoryName(paths[0])
                            ?? Path.Combine(_settingsService.Settings?.OutputFolderPath
                                            ?? Path.GetTempPath(), "MiniMaxFflf");
            Directory.CreateDirectory(outputDir);

            // The run id already carries the folder name and the time it was loaded, so re-joining after
            // retrying a take overwrites the same file: a refresh, not a loss.
            var joinedPath = Path.Combine(outputDir,
                                          $"MiniMaxFFLF_{Sanitize(finished.FolderRunId!)}_joined.mp4");
            var seconds = takes.Sum(t => t.TotalSeconds);

            ProcessingStatus = $"Joining {paths.Count} takes...";
            AddLog($"Joining {paths.Count} takes with FFmpeg → {Path.GetFileName(joinedPath)}");
            await ConcatTakesAsync(ffmpeg, paths, joinedPath, token);

            if (!File.Exists(joinedPath) || new FileInfo(joinedPath).Length == 0)
            {
                AddLog("Join produced no file — the individual takes are unaffected.");
                return;
            }

            await LocalCopyService.CopyVideoAsync(joinedPath);

            var fi = new FileInfo(joinedPath);
            var clips = takes.Sum(t => t.PassCount);
            Application.Current.Dispatcher.Invoke(() =>
            {
                ResultVideoPath = joinedPath;
                ResultVideoInfo = $"MiniMax FFLF • joined folder • {paths.Count} takes • {clips} clips • "
                                + $"{seconds}s • {fi.Length / 1024 / 1024.0:F1}MB";
                HasResult = true;
                OnCanExecuteChanged();
            });
            ProcessingStatus = "Takes joined!";
            AddLog($"=== Joined video complete: {joinedPath} ===");
        }

        /// <summary>
        /// FFmpeg concat-demuxer join. Every take of a run comes out of the same graph at the same
        /// resolution and frame rate, but it is re-encoded rather than stream-copied for the same reason
        /// the other H3 tabs do it: H3 writes an audio track per take, and a copy-mode concat of
        /// separately encoded H3 outputs is where the timestamp and codec-parameter edge cases live.
        /// veryfast/CRF 18 is visually lossless and costs seconds against a render measured in minutes.
        ///
        /// <para>The seams are hard cuts. Consecutive takes share a keyframe — take 2 opens on the still
        /// take 1 closed on — so the joined video holds that picture for two frames at each seam, 1/12s
        /// at 24fps. Trimming it would mean re-cutting every input through the concat <i>filter</i>, and
        /// the seam is a cut between separately sampled takes either way.</para>
        /// </summary>
        private async Task ConcatTakesAsync(string ffmpeg, IReadOnlyList<string> takes, string outPath,
            CancellationToken token)
        {
            var listPath = Path.Combine(Path.GetTempPath(), $"mmfflf_concat_{Guid.NewGuid():N}.txt");
            var sb = new StringBuilder();
            foreach (var take in takes)
            {
                // The concat demuxer reads a backslash as an escape and a single quote as the delimiter.
                sb.AppendLine($"file '{take.Replace("\\", "/").Replace("'", @"'\''")}'");
            }
            await File.WriteAllTextAsync(listPath, sb.ToString(), token);

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in new[]
                {
                    "-y", "-f", "concat", "-safe", "0", "-i", listPath,
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-c:a", "aac", "-b:a", "192k", "-pix_fmt", "yuv420p", outPath
                }) psi.ArgumentList.Add(a);

                using var p = System.Diagnostics.Process.Start(psi)
                              ?? throw new Exception("Failed to start FFmpeg.");
                // stderr is drained before the wait: FFmpeg logs everything there and blocks once the pipe
                // buffer fills, which would otherwise hang the join.
                var stderr = await p.StandardError.ReadToEndAsync(token);
                await p.WaitForExitAsync(token);
                if (p.ExitCode != 0)
                {
                    var tail = stderr.Length <= 600 ? stderr : stderr[^600..];
                    throw new Exception($"FFmpeg exited {p.ExitCode}: {tail}");
                }
            }
            finally
            {
                try { File.Delete(listPath); } catch { /* temp file: best effort */ }
            }
        }

        /// <summary>Strips whatever a folder name may hold that a filename may not.</summary>
        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        #endregion

        #region Generation

        private async Task GenerateItemAsync(MiniMaxFflfQueueItem item, CancellationToken outerToken)
        {
            IsProcessing = true;
            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = "Preparing MiniMax FFLF workflow...";
            GenerationTimer = string.Empty;

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                AddLog("=== MiniMax FFLF (H3 FL2VA) ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("MiniMaxFflf", outerToken);

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

                ProcessingStatus = "Uploading keyframes...";
                ProcessingProgress = 4;
                var opening = await UploadFrameAsync(item.OpeningFramePath, "Opening frame");
                var endFrames = new List<string>();
                for (var i = 0; i < item.EndFramePaths.Count; i++)
                    endFrames.Add(await UploadFrameAsync(item.EndFramePaths[i], $"Keyframe {i + 2}"));

                var runSeed = item.Seed >= 0 ? item.Seed : System.Random.Shared.NextInt64(0, int.MaxValue);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var runToken = $"mmfflf_{ts}";
                var sink = item.ExtensionCount > 0 ? NodeSaveJoined : NodeSaveSingle;

                json = BuildWorkflow(json, item, opening, endFrames, runSeed, runToken, sink, out var pruned);
                AddLog($"Graph prepared: {item.PassCount} clip(s), {pruned} node(s) pruned");

                ProcessingProgress = 8;
                AddLog($"Generating (seed {runSeed}, ~{item.TotalSeconds}s over {item.PassCount} clip(s), " +
                       $"{item.AspectRatio}, {item.Megapixels:0.0} MP" +
                       $"{(item.UseSparseAttention ? ", Sol-Attn" : "")}" +
                       $"{(item.UseLatentUpscale ? ", latent 2x" : "")}" +
                       $"{(item.UseRtxUpscale ? ", RTX 2x" : "")}, ~{EstimateGpx(item):0.00} Gpx/pass)...");

                // The bar and the clock are handed the shape of the run before it starts, so the very
                // first tick can already say roughly how long this will take.
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
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "MiniMaxFflf");
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"MiniMaxFFLF_{ts}.mp4");
                File.Copy(local, finalPath, true);
                await LocalCopyService.CopyVideoAsync(finalPath);

                var fi = new FileInfo(finalPath);
                item.OutputImagePath = finalPath;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResultVideoPath = finalPath;
                    ResultVideoInfo =
                        $"MiniMax FFLF • {item.AspectRatio} • {item.TotalSeconds}s over {item.PassCount} " +
                        $"clip{(item.PassCount == 1 ? "" : "s")} • {endFrames.Count + 1} keyframes • " +
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

        private async Task<string> UploadFrameAsync(string path, string label)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException($"{label} is gone: {path}");
            var name = await _comfyUIService.UploadImageAsync(path);
            if (string.IsNullOrEmpty(name)) throw new Exception($"Failed to upload {label.ToLowerInvariant()}.");
            AddLog($"{label} uploaded: {name}");
            return name!;
        }

        /// <summary>
        /// Describes the run to the progress tracker: every sampler bar ComfyUI will report, in order,
        /// what each of their steps is worth, and the unreported work between them.
        ///
        /// <para>Everything below is measured in one <em>finish</em> sampler step — the only steps that
        /// see the whole canvas. The figures carry over from the run measured under the previous scheme,
        /// rebased on that unit: a draft step costs about a quarter of a finish step because it samples a
        /// quarter of the pixels, the latent upscale between the two is worth ~2.2, VAE decode and
        /// stitching after them ~3.8, and a continuing clip ~1.55× the clip it extends. They only have to
        /// be roughly right — they set the <em>shape</em> of the run, and the pace is re-measured from the
        /// run itself as it goes.</para>
        /// </summary>
        private (List<ProgressStage> Stages, double LeadUnits, double EstimatedSeconds) BuildProgressPlan(
            MiniMaxFflfQueueItem item)
        {
            const double draftStepUnits = 0.25;   // a quarter-canvas step against a finish step
            const double afterMainUnits = 2.2;    // latent upscale, between the two samplers of a clip
            const double afterPassUnits = 3.8;    // VAE decode and stitching at the end of a clip
            const double rtxUnits = 1.3;          // RTX 2x, on the saved pass only
            const double leadUnits = 1.3;         // loading the model, encoding the keyframes

            var stages = new List<ProgressStage>();
            for (var pass = 0; pass < item.PassCount; pass++)
            {
                var label = item.PassCount == 1 ? "Rendering" : $"Clip {pass + 1}/{item.PassCount}";
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

        /// <summary>What one clip costs against the base pass. A continuing clip re-samples its own length
        /// <i>and</i> the overlap it has to blend into the clip before it, measured at ~1.55×.</summary>
        private static double PassWeight(MiniMaxFflfQueueItem item, int passIndex)
        {
            if (passIndex == 0) return 1.0;
            var seconds = ClipSeconds(item, passIndex);

            // Length only: the 1.55 already carries the overlap window, so taking the ratio from
            // PassFrames as well would charge for it twice.
            var ratio = FrameCount(seconds) / (double)Math.Max(1, FrameCount(ClipSeconds(item, 0)));
            return ContinuationFactor * ratio;
        }

        /// <summary>Total cost of the run in base-pass Gpx — what the seconds-per-Gpx figure multiplies.</summary>
        private static double WeightedGpx(MiniMaxFflfQueueItem item)
        {
            var total = 0.0;
            for (var pass = 0; pass < item.PassCount; pass++)
                total += PassGpx(item, pass) * (pass == 0 ? 1.0 : ContinuationFactor);
            return total;
        }

        /// <summary>Peak tensor size of one clip, in billions of pixel-positions.</summary>
        private static double PassGpx(MiniMaxFflfQueueItem item, int passIndex) =>
            SampledArea(item.AspectRatio, item.Megapixels, item.UseLatentUpscale)
            * PassFrames(item, passIndex) / 1e9;

        /// <summary>
        /// Frames one clip actually samples. A continuing clip re-generates the overlap window on top of
        /// its own length — the loop's frame-count expression is literally <c>frames(seconds) + overlap</c>
        /// — so it is always bigger than the base pass at the same number of seconds.
        /// </summary>
        private static int PassFrames(MiniMaxFflfQueueItem item, int passIndex) =>
            FrameCount(ClipSeconds(item, passIndex)) + (passIndex == 0 ? 0 : item.OverlapFrames);

        private static int ClipSeconds(MiniMaxFflfQueueItem item, int passIndex)
        {
            var seconds = item.Seconds.ElementAtOrDefault(passIndex);
            return seconds > 0 ? seconds : 10;
        }

        /// <summary>Pixels in one finished frame: the canvas ComfyUI will really produce. Both memory
        /// limits are built on this figure, and deriving it from the megapixel target instead understates
        /// 16:9, 4:3, 3:4, 9:16 and 21:9 by ~5%, which is the entire margin on a 24 GB card.</summary>
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
        /// Folds a finished run's real duration back into the seconds-per-Gpx figure. Half-weighted, and
        /// ignored outside a sane range, so one stalled run cannot poison the number.
        /// </summary>
        private void RecalibrateFromRun(MiniMaxFflfQueueItem item, TimeSpan elapsed)
        {
            var gpx = WeightedGpx(item);
            if (gpx <= 0 || elapsed.TotalSeconds < 60) return;

            var measured = (elapsed.TotalSeconds - 40) / gpx;
            if (measured is < 100 or > 3000) return;

            // A run whose server died and was resubmitted mid-flight measures the outage and the redone
            // work as well: 21 Aug's 15m27s render "measured" 32m39s that way, 1307 s/Gpx against a real
            // 604. Capping how far one run may move the figure keeps a crash from poisoning every ETA
            // for the rest of the session, while a genuine change still arrives within a couple of jobs.
            var capped = Math.Clamp(measured, _secondsPerGpx / 1.5, _secondsPerGpx * 1.5);
            if (Math.Abs(capped - measured) > 1)
                AddLog($"Measured {measured:0} s/Gpx — further from {_secondsPerGpx:0} than one run should "
                       + $"move it (a lost server counts its dead time), so taking {capped:0}");
            _secondsPerGpx = 0.5 * _secondsPerGpx + 0.5 * capped;
            AddLog($"Took {elapsed:hh\\:mm\\:ss} for {gpx:0.00} Gpx — {measured:0} s/Gpx " +
                   $"(estimate for the next item: {_secondsPerGpx:0} s/Gpx)");
        }

        /// <summary>Peak-clip estimate for an already-queued item.</summary>
        private static double EstimateGpx(MiniMaxFflfQueueItem item)
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
            string json, MiniMaxFflfQueueItem item, string opening, IReadOnlyList<string> endFrames,
            long runSeed, string runToken, string sink, out int pruned)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeBaseFl2v, "MiniMaxH3ImageToVideo");
            RequireClass(root, NodeLoopFl2v, "MiniMaxH3ImageToVideo");
            RequireClass(root, NodeLoopStart, "easy forLoopStart");
            RequireClass(root, sink, "VHS_VideoCombine");

            if (endFrames.Count == 0) throw new Exception("This item has no keyframes.");
            var extending = item.ExtensionCount > 0;

            // ── The chain ─────────────────────────────────────────────────────
            SetInput(root, NodeOpeningFrame, "image", opening);
            SetInput(root, NodeBaseEndFrame, "image", endFrames[0]);
            SetInput(root, NodeBasePrompt, "value", item.Prompts[0]);
            SetInput(root, NodeBaseSeconds, "value", ClipSeconds(item, 0));
            SetInput(root, NodeBaseSeed, "noise_seed", runSeed);

            // The loop's end-frame/prompt/duration/seed switches are indexed off the loop counter, so
            // every slot has to hold something valid even when fewer clips are queued than there are
            // slots. The unused ones repeat the last real clip rather than staying empty.
            for (var i = 0; i < MaxClips - 1; i++)
            {
                // Slot i drives clip i+2; slots past the queued chain fall back to its last clip.
                var source = Math.Max(0, Math.Min(i + 1, item.PassCount - 1));

                SetInput(root, NodeClipEndFrames[i], "image", endFrames[source]);
                SetInput(root, NodeClipPrompts[i], "value", item.Prompts[source]);
                SetInput(root, NodeClipSeconds[i], "value", ClipSeconds(item, source));
                SetInput(root, NodeClipSeeds[i], "noise_seed", runSeed + i + 1);
            }
            SetInput(root, NodeLoopSeedSwitch, "switch", true);
            SetInput(root, NodeLoopStart, "total", Math.Max(1, item.ExtensionCount));
            SetInput(root, NodeOverlap, "choice", item.OverlapFrames.ToString());

            // ── Canvas ────────────────────────────────────────────────────────
            // ResolutionSelector sizes the *sampled* canvas, which with the latent upscale on is the
            // draft — a quarter of the megapixel target, doubled afterwards. The dropdown names the
            // finished size, so the division happens here rather than in the user's head.
            SetInput(root, NodeResolution, "aspect_ratio", item.AspectRatio);
            SetInput(root, NodeResolution, "megapixels",
                     item.UseLatentUpscale ? DraftMegapixels(item.Megapixels) : item.Megapixels);
            SetInput(root, NodeResolution, "multiple", ResolutionMultiple);

            // ── Attention and sampling scheme ────────────────────────────────
            SetInput(root, NodeSparseAttention, "switch", item.UseSparseAttention);

            // The 2x has to be written into all three places that derive the finished canvas: the two
            // latent upscalers, and the loop's own width/height expressions for the conditioning latent
            // its finish sampler is built against. If those disagree the loop dies on a token mismatch.
            SetInput(root, NodeBaseUpscaler, "mode.scale", LatentUpscaleFactor);
            SetInput(root, NodeLoopUpscaler, "mode.scale", LatentUpscaleFactor);
            foreach (var id in NodeLoopFinishDims) SetInput(root, id, "values.b", LatentUpscaleFactor);

            SetInput(root, NodeBaseDetail, "switch", item.UseLatentUpscale);
            foreach (var id in NodeLoopDetail) SetInput(root, id, "switch", item.UseLatentUpscale);

            // With the upscale on, the first sampler is only a draft and stops half-denoised at sigma 0.5
            // — the finish sampler picks it up from there. With it off there is no finish sampler, so
            // the same node has to run the full shifted schedule down to zero instead.
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
        /// anything ending in an OUTPUT_NODE runs whether or not something downstream consumes it.
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
            // like "521:515"), but plain integer ids show up in other exports of the same nodes.
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

            // A four-clip chain at this quality runs well past the 30-minute default, and being cut off at
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
                    var tempPath = Path.Combine(Path.GetTempPath(), $"mmfflf_{Guid.NewGuid():N}_{filename}");
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
            OnPropertyChanged(nameof(ChainIsComplete));
            OnPropertyChanged(nameof(GenerateBlockedReason));
            AnalyzeCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            AddClipCommand.NotifyCanExecuteChanged();
            QueueFolderCommand.NotifyCanExecuteChanged();
            ClearQueueCommand.NotifyCanExecuteChanged();
            StartQueueCommand.NotifyCanExecuteChanged();
            StopQueueCommand.NotifyCanExecuteChanged();
            ReprocessAllFailedCommand.NotifyCanExecuteChanged();
        }
    }
}
