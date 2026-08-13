using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "H3 Chain" tab — MiniMax H3 driven as an <b>autoregressive chain</b> rather than a single pass.
    ///
    /// <para>Every other MiniMax tab renders one clip per submission and, when it needs a longer video,
    /// queues N jobs and FFmpeg-concatenates the results. That produces N independent clips with hard
    /// cuts between them. This tab runs <c>h3-chain-ref2va.json</c>, whose <c>MiniMaxH3Chain*</c> nodes
    /// loop <i>inside</i> ComfyUI: one submission renders every segment, carries the tail of each into
    /// the head of the next as motion context, trims the repeated overlap back off, checkpoints each
    /// finished segment to disk, and finally joins and muxes them into one continuous take. The result
    /// is one moving camera take of arbitrary length, not a slideshow of clips.</para>
    ///
    /// <para><b>What the tab feeds it.</b> Two reference images and a soundtrack:</para>
    /// <list type="bullet">
    /// <item><b>Reference 1</b> → <c>ref_images.ref_image_0</c> / <c>&lt;Picture 1&gt;</c> — facial identity.</item>
    /// <item><b>Reference 2</b> (optional) → <c>ref_image_1</c> / <c>&lt;Picture 2&gt;</c> — body, wardrobe,
    /// proportions. Left empty, <see cref="RemoveSecondReference"/> strips the slot and the node itself.</item>
    /// <item><b>Audio</b> → <c>LoadAudio</c>, wired into Loop Start, Current Shot and Assemble. The chain
    /// slices the exact window of the track for each segment, hands that slice to H3 as
    /// <c>ref_audio_0</c> (<c>&lt;Audio 1&gt;</c>) so the performance is generated <i>against</i> the music,
    /// and muxes the full track back over the finished take.</item>
    /// </list>
    ///
    /// <para><b>Length is the user's, not the model's.</b> H3 tops out at ~15 s per pass;
    /// <see cref="TotalSeconds"/> (up to 30 minutes) is divided by <see cref="SegmentSeconds"/> to give
    /// <see cref="PlannedSegmentCount"/> segments, and all of them go into a single
    /// <c>MiniMaxH3ChainPlan</c>. <see cref="LoopAudio"/> loops the soundtrack with FFmpeg to cover a
    /// running time longer than the song.</para>
    ///
    /// <para><b>Resumable.</b> <c>MiniMaxH3ChainSegmentSave</c> checkpoints every accepted segment under
    /// <c>output/h3_chain/{run_name}/</c>. The run name is frozen on the queue item, so
    /// <see cref="ResumeCommand"/> can count what survived a crash and restart the chain at the next
    /// unrendered segment instead of paying for the whole take again.</para>
    ///
    /// <para><b>Progress.</b> A chain is one prompt to ComfyUI, so the WebSocket only reports sampler
    /// steps within whichever segment is running. <see cref="WatchCheckpointsAsync"/> watches the
    /// checkpoint folder alongside it and reports real segment-level progress.</para>
    /// </summary>
    public partial class H3ChainViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/h3-minimax/h3-chain-ref2va.json";
        private const string SystemPromptFile = "h3chain.md";

        /// <summary>
        /// Where <c>MiniMaxH3ChainPlan</c> keeps every chain, relative to ComfyUI's output directory.
        /// Fixed by the node, not configurable. Under <c>{ChainRootFolder}/{run_name}/</c> it writes
        /// <c>segments/</c>, <c>checkpoints/</c> and <c>final/</c>.
        /// <para>Taken from what the nodes actually report in /history, not from their tooltips, which
        /// still say the singular "h3_chain".</para>
        /// </summary>
        private const string ChainRootFolder = "h3_chains";

        private const string SegmentsFolder = "segments";
        private const string FinalFolder = "final";

        // ── Workflow node ids (locked from h3-minimax/h3-chain-ref2va.json) ──────────────────
        private const string NodeReference1 = "910";   // LoadImage → ref_image_0
        private const string NodeReference2 = "911";   // LoadImage → ref_image_1
        private const string NodeAudio = "940";        // LoadAudio → chain source track
        private const string NodeRefToVideo = "110";   // MiniMaxH3ReferenceToVideo
        private const string NodePlan = "1700";        // MiniMaxH3ChainPlan
        private const string NodeLoopStart = "1701";   // MiniMaxH3ChainLoopStart
        /// <summary>MiniMaxH3ChainLoopEnd. Not written to — its id is the nesting prefix ComfyUI stamps on
        /// every cloned loop-body node, which is how <see cref="SegmentFromNodeId"/> counts segments.</summary>
        private const string NodeLoopEnd = "1705";
        private const string NodeAssemble = "1706";    // MiniMaxH3ChainAssemble — the final MP4
        private const string NodeTrim = "132";         // MiniMaxH3LoopTrim (fps)

        /// <summary>H3 renders at 24 fps and every duration/frame conversion here assumes it. Written on
        /// every submit so an export at another rate cannot silently desync the plan from the file.</summary>
        private const int OutputFrameRate = 24;

        /// <summary>Ceiling on <c>MiniMaxH3ChainLoopStart.start_clip</c>, and therefore on how many
        /// segments one chain can hold.</summary>
        private const int MaxSegments = 128;

        // ── Input state ────────────────────────────────────────────────────────
        private string _reference1Path = string.Empty;
        private BitmapImage? _reference1Preview;
        private string _reference1Info = string.Empty;

        private string _reference2Path = string.Empty;
        private BitmapImage? _reference2Preview;
        private string _reference2Info = string.Empty;

        private string _audioPath = string.Empty;
        private string _audioInfo = string.Empty;
        private double _audioDurationSeconds;

        private string _prompt = string.Empty;
        private int _promptSegmentCount;
        private string _storyGuidance = string.Empty;
        private string _lyrics = string.Empty;

        private double _totalSeconds = 60;
        private double _segmentSeconds = 15;
        private string _selectedAspectRatio = MiniMaxH3ViewModel.AutoAspect;
        private double _megapixels = 0.52;
        private int _contextLength = 22;
        private int _steps = 5;
        private long _baseSeed = -1;
        private bool _loopAudio = true;
        private string _audioMode = "source_track";

        private bool _isAnalyzing;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _analyzeCts;

        // ── Queue ──────────────────────────────────────────────────────────────
        private readonly ObservableCollection<H3ChainQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;
        private bool _isProcessingQueue;
        private string _queueStatus = string.Empty;

        private static string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h3chain_queue.json");

        public H3ChainViewModel(
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

            SelectReference1Command = new RelayCommand(async () => await SelectReference1Async());
            SelectReference2Command = new RelayCommand(async () => await SelectReference2Async());
            ClearReference2Command = new RelayCommand(() => Reference2Path = string.Empty);
            SelectAudioCommand = new RelayCommand(async () => await SelectAudioAsync());
            MatchSongLengthCommand = new RelayCommand(MatchSongLength, () => HasAudioDuration);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            GenerateCommand = new RelayCommand(AddToQueue, () => CanGenerate);
            CancelCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            RemoveQueueItemCommand = new RelayCommand<H3ChainQueueItem>(RemoveQueueItem);
            ResumeQueueItemCommand = new RelayCommand<H3ChainQueueItem>(item => _ = ResumeItemAsync(item));
            ClearQueueCommand = new RelayCommand(ClearQueue, () => HasQueueItems);
            StartQueueCommand = new RelayCommand(() => _ = ProcessQueueAsync(), () => HasPendingItems && !IsProcessingQueue);
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(ReprocessAllFailed, () => HasFailedItems);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            RandomSeedCommand = new RelayCommand(() => BaseSeed = System.Random.Shared.NextInt64(0, int.MaxValue));

            _queue.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
            };

            AddLog("H3 Chain initialized");
            ScheduleQueueLoad();
        }

        #region Commands

        public ICommand SelectReference1Command { get; }
        public ICommand SelectReference2Command { get; }
        public ICommand ClearReference2Command { get; }
        public ICommand SelectAudioCommand { get; }
        public RelayCommand MatchSongLengthCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        /// <summary>Named for the button it drives; it enqueues the chain rather than running it inline.</summary>
        public RelayCommand GenerateCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand<H3ChainQueueItem> RemoveQueueItemCommand { get; }
        /// <summary>Restarts an interrupted chain at its first unrendered segment — see <see cref="ResumeItemAsync"/>.</summary>
        public RelayCommand<H3ChainQueueItem> ResumeQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StartQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand RandomSeedCommand { get; }

        #endregion

        #region Inputs

        /// <summary>Reference 1 — <c>ref_image_0</c> / <c>&lt;Picture 1&gt;</c>. The face the chain holds.</summary>
        public string Reference1Path
        {
            get => _reference1Path;
            set
            {
                if (_reference1Path == value) return;
                _reference1Path = value;
                _reference1Preview = LoadImagePreview(value, out _reference1Info);
                OnPropertyChanged();
                OnPropertyChanged(nameof(Reference1Preview));
                OnPropertyChanged(nameof(Reference1Info));
                OnPropertyChanged(nameof(HasReference1));
                OnPropertyChanged(nameof(ResolvedAspectRatio));
                OnPropertyChanged(nameof(CanvasSummary));
                OnCanExecuteChanged();
            }
        }

        public BitmapImage? Reference1Preview => _reference1Preview;
        public string Reference1Info => _reference1Info;
        public bool HasReference1 => !string.IsNullOrEmpty(Reference1Path) && File.Exists(Reference1Path);

        /// <summary>Reference 2 — <c>ref_image_1</c> / <c>&lt;Picture 2&gt;</c>. Optional; body and wardrobe.</summary>
        public string Reference2Path
        {
            get => _reference2Path;
            set
            {
                if (_reference2Path == value) return;
                _reference2Path = value;
                _reference2Preview = LoadImagePreview(value, out _reference2Info);
                OnPropertyChanged();
                OnPropertyChanged(nameof(Reference2Preview));
                OnPropertyChanged(nameof(Reference2Info));
                OnPropertyChanged(nameof(HasReference2));
                OnPropertyChanged(nameof(ReferenceSummary));
                OnCanExecuteChanged();
            }
        }

        public BitmapImage? Reference2Preview => _reference2Preview;
        public string Reference2Info => _reference2Info;
        public bool HasReference2 => !string.IsNullOrEmpty(Reference2Path) && File.Exists(Reference2Path);

        public string ReferenceSummary => HasReference2
            ? "Two references — <Picture 1> fixes the face, <Picture 2> the body and wardrobe."
            : "One reference — <Picture 1> alone. Add a second image to pin the body and wardrobe separately.";

        /// <summary>
        /// The soundtrack. The chain slices it per segment, hands each slice to H3 as
        /// <c>&lt;Audio 1&gt;</c>, and muxes the whole track back over the assembled take.
        /// </summary>
        public string AudioPath
        {
            get => _audioPath;
            set
            {
                if (_audioPath == value) return;
                _audioPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAudio));
                // Probing spawns ffprobe — keep it off the UI thread, and off the setter's caller.
                _ = ProbeAudioAsync(value);
                OnCanExecuteChanged();
            }
        }

        public bool HasAudio => !string.IsNullOrEmpty(AudioPath) && File.Exists(AudioPath);

        public string AudioInfo
        {
            get => _audioInfo;
            private set { if (_audioInfo != value) { _audioInfo = value; OnPropertyChanged(); } }
        }

        /// <summary>Length of the loaded track, or 0 while it is still being probed / if FFmpeg is absent.</summary>
        public double AudioDurationSeconds
        {
            get => _audioDurationSeconds;
            private set
            {
                if (Math.Abs(_audioDurationSeconds - value) < 0.001) return;
                _audioDurationSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAudioDuration));
                OnPropertyChanged(nameof(AudioCoverageSummary));
                MatchSongLengthCommand.NotifyCanExecuteChanged();
            }
        }

        public bool HasAudioDuration => AudioDurationSeconds > 0.5;

        /// <summary>Says, in words, what the track will actually do over the requested running time —
        /// covered, looped to fill, or trimmed.</summary>
        public string AudioCoverageSummary
        {
            get
            {
                if (!HasAudio) return "No soundtrack loaded — the chain needs one.";
                if (!HasAudioDuration) return "Track length unknown (FFmpeg not found) — it is used as-is.";

                var total = TotalSeconds;
                if (AudioDurationSeconds >= total - 0.5)
                    return LoopAudio
                        ? $"Track is {Fmt(AudioDurationSeconds)}; the first {Fmt(total)} is used and the rest is trimmed."
                        : $"Track is {Fmt(AudioDurationSeconds)} — longer than the video, so the tail is unused.";

                if (!LoopAudio)
                    return $"Track is only {Fmt(AudioDurationSeconds)} of {Fmt(total)} — the rest of the video runs " +
                           "against silence. Turn on Loop to fill it.";

                var times = total / AudioDurationSeconds;
                return $"Track is {Fmt(AudioDurationSeconds)} — looped ×{times:0.0} to cover {Fmt(total)}.";
            }
        }

        private static string Fmt(double seconds) =>
            seconds >= 60
                ? $"{(int)(seconds / 60)}m {seconds % 60:0}s"
                : $"{seconds:0.#}s";

        private Task SelectReference1Async() => PickImageAsync("Select Reference 1 (face / identity)", "h3chain.ref1",
            path => { Reference1Path = path; AddLog($"Reference 1: {Path.GetFileName(path)}"); });

        private Task SelectReference2Async() => PickImageAsync("Select Reference 2 (body / wardrobe sheet)", "h3chain.ref2",
            path => { Reference2Path = path; AddLog($"Reference 2: {Path.GetFileName(path)}"); });

        private async Task PickImageAsync(string title, string persistKey, Action<string> apply)
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                title,
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: persistKey);

            if (path != null) apply(path);
        }

        private async Task SelectAudioAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select the soundtrack",
                "Audio Files|*.mp3;*.wav;*.flac;*.m4a;*.aac;*.ogg;*.opus|All Files|*.*",
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                persistKey: "h3chain.audio");

            if (path != null)
            {
                AudioPath = path;
                AddLog($"Soundtrack: {Path.GetFileName(path)}");
            }
        }

        /// <summary>
        /// Reads the track's length so the duration planner can say whether it covers the video. Runs on a
        /// worker thread — <see cref="VideoProcessingBaseViewModel.GetVideoDuration"/> shells out to ffprobe.
        /// </summary>
        private async Task ProbeAudioAsync(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                AudioDurationSeconds = 0;
                AudioInfo = string.Empty;
                return;
            }

            AudioInfo = "Reading track…";
            var sizeKb = new FileInfo(path).Length / 1024;
            var duration = await Task.Run(() => GetVideoDuration(path));

            AudioDurationSeconds = duration;
            AudioInfo = duration > 0
                ? $"{Fmt(duration)} • {sizeKb:N0}KB"
                : $"{sizeKb:N0}KB";
        }

        private BitmapImage? LoadImagePreview(string path, out string info)
        {
            info = string.Empty;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                var fi = new FileInfo(path);
                info = $"{bitmap.PixelWidth}×{bitmap.PixelHeight} • {fi.Length / 1024}KB";
                return bitmap;
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                info = "Error loading image";
                return null;
            }
        }

        #endregion

        #region Plan settings

        /// <summary>
        /// The chain's segment plans, one per segment, separated by <c>=== SEGMENT n of N ===</c> headers.
        /// This box is what Generate actually sends — hand-edits, deletions and added headers are honoured.
        /// </summary>
        public string Prompt
        {
            get => _prompt;
            set
            {
                if (_prompt == value) return;
                _prompt = value;
                // Cached: the box updates on every keystroke and a long chain is tens of kilobytes.
                _promptSegmentCount = SplitSegments(_prompt).Count;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PromptSegmentCount));
                OnPropertyChanged(nameof(PromptSegmentSummary));
                OnPropertyChanged(nameof(HasPromptMismatch));
                OnCanExecuteChanged();
            }
        }

        /// <summary>Free-text description of what the take shows, start to finish. Optional.</summary>
        public string StoryGuidance
        {
            get => _storyGuidance;
            set { if (_storyGuidance != value) { _storyGuidance = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// The song's words, pasted in. The LLM cannot hear the track, so this is the only way it can
        /// write real lyrics into the <c>&lt;d&gt;[English] …&lt;/d&gt;</c> tags and distribute them across
        /// the segments in order. Left empty, the segments describe the performance and quote nothing.
        /// </summary>
        public string Lyrics
        {
            get => _lyrics;
            set { if (_lyrics != value) { _lyrics = value; OnPropertyChanged(); } }
        }

        /// <summary>Target running time of the finished take, 15 s – 30 min.</summary>
        public double TotalSeconds
        {
            get => _totalSeconds;
            set
            {
                var snapped = Math.Clamp(Math.Round(value / 5.0) * 5.0, 15, 1800);
                if (Math.Abs(_totalSeconds - snapped) < 0.0001) return;
                _totalSeconds = snapped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalSecondsDisplay));
                RaisePlanChanged();
            }
        }

        public string TotalSecondsDisplay => Fmt(TotalSeconds);

        /// <summary>Length of one segment — one H3 pass. The model's own supported range is 4–15 s.</summary>
        public double SegmentSeconds
        {
            get => _segmentSeconds;
            set
            {
                var clamped = Math.Clamp(Math.Round(value), 4, 15);
                if (Math.Abs(_segmentSeconds - clamped) < 0.0001) return;
                _segmentSeconds = clamped;
                OnPropertyChanged();
                RaisePlanChanged();
            }
        }

        /// <summary>How many segments the chain will hold: the target running time divided by the segment
        /// length, rounded up and capped at the loop node's own limit.</summary>
        public int PlannedSegmentCount => Math.Clamp(
            (int)Math.Ceiling(TotalSeconds / SegmentSeconds - 0.0001), 1, MaxSegments);

        public string PlanSummary
        {
            get
            {
                var n = PlannedSegmentCount;
                var actual = n * SegmentSeconds;
                var capped = n >= MaxSegments && TotalSeconds > actual
                    ? $" (capped at the chain's {MaxSegments}-segment limit)"
                    : string.Empty;
                return $"{n} segment{(n == 1 ? "" : "s")} × {SegmentSeconds:0}s → {Fmt(actual)} " +
                       $"of one continuous take{capped}. Each segment is {FramesForSeconds(SegmentSeconds)} frames @ 24 fps.";
            }
        }

        /// <summary>How many segments the prompt box actually holds — what Generate will submit.</summary>
        public int PromptSegmentCount => _promptSegmentCount;

        /// <summary>True when the written chain no longer matches the planner, so the sliders are lying
        /// about what Generate will do.</summary>
        public bool HasPromptMismatch => PromptSegmentCount > 0 && PromptSegmentCount != PlannedSegmentCount;

        public string PromptSegmentSummary
        {
            get
            {
                if (PromptSegmentCount == 0) return string.Empty;
                var written = $"The prompt box holds {PromptSegmentCount} segment{(PromptSegmentCount == 1 ? "" : "s")} " +
                              $"→ {Fmt(PromptSegmentCount * SegmentSeconds)} of video.";
                return HasPromptMismatch
                    ? written + $" The planner is set to {PlannedSegmentCount} — the box wins."
                    : written;
            }
        }

        public IReadOnlyList<string> AspectRatioOptions { get; } =
            new[] { MiniMaxH3ViewModel.AutoAspect }
                .Concat(MiniMaxH3ViewModel.AspectRatios.Select(a => a.Option)).ToList();

        public string SelectedAspectRatio
        {
            get => _selectedAspectRatio;
            set
            {
                if (_selectedAspectRatio == value) return;
                _selectedAspectRatio = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResolvedAspectRatio));
                OnPropertyChanged(nameof(CanvasSummary));
            }
        }

        /// <summary>The aspect actually planned — the picked one, or Reference 1's closest match.</summary>
        public string ResolvedAspectRatio =>
            SelectedAspectRatio == MiniMaxH3ViewModel.AutoAspect
                ? ClosestAspectRatio(Reference1Path)
                : SelectedAspectRatio;

        /// <summary>
        /// Canvas presets. A chain multiplies its canvas cost by the segment count, so the default is
        /// deliberately the workflow's own 960×544 rather than the single-clip tabs' 1.0 MP.
        /// </summary>
        public IReadOnlyList<MegapixelOption> MegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.35, "0.35 MP — draft (≈800×448)"),
            new MegapixelOption(0.52, "0.52 MP — chain default (≈960×544)"),
            new MegapixelOption(0.75, "0.75 MP — sharper (≈1152×640)"),
            new MegapixelOption(1.00, "1.0 MP — full quality (≈1344×768)"),
        };

        public double Megapixels
        {
            get => _megapixels;
            set
            {
                if (Math.Abs(_megapixels - value) <= 0.0001) return;
                _megapixels = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanvasSummary));
            }
        }

        public string CanvasSummary
        {
            get
            {
                var (w, h) = CanvasSize(ResolvedAspectRatio, Megapixels);
                return $"Every segment renders {w}×{h}. The chain node holds this fixed for the whole take.";
            }
        }

        /// <summary>The overlap lengths <c>MiniMaxH3ChainPlan</c> accepts, no more and no fewer.</summary>
        public IReadOnlyList<ContextOption> ContextLengthOptions { get; } = new[]
        {
            new ContextOption(1,  "1 frame — cheapest, weakest continuity"),
            new ContextOption(5,  "5 frames — light"),
            new ContextOption(22, "22 frames — recommended"),
            new ContextOption(39, "39 frames — strongest, slowest"),
        };

        /// <summary>
        /// How many frames of the previous segment are regenerated at the head of the next one to carry
        /// motion across the seam. They are trimmed back off afterwards, so this costs render time rather
        /// than running time.
        /// </summary>
        public int ContextLength
        {
            get => _contextLength;
            set
            {
                if (_contextLength == value) return;
                _contextLength = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Audio modes that are safe here. Both keep the external track wired, which the graph's
        /// <c>ref_audio_0</c> slice depends on.</summary>
        public IReadOnlyList<AudioModeOption> AudioModeOptions { get; } = new[]
        {
            new AudioModeOption("source_track", "Song only — each segment gets its slice of the track"),
            new AudioModeOption("source_plus_timeline", "Song + generated tail — also carries H3's own audio across the seam"),
        };

        public string AudioMode
        {
            get => _audioMode;
            set { if (_audioMode != value) { _audioMode = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Sampler steps per segment. The workflow ships the 4-step lightx2v turbo LoRA, so 5 is the
        /// intended figure — raising it costs render time linearly across every segment.
        /// </summary>
        public int Steps
        {
            get => _steps;
            set
            {
                var clamped = Math.Clamp(value, 1, 40);
                if (_steps == clamped) return;
                _steps = clamped;
                OnPropertyChanged();
            }
        }

        /// <summary>Base for the chain's per-segment seeds. -1 draws a fresh one at run time.</summary>
        public long BaseSeed
        {
            get => _baseSeed;
            set { if (_baseSeed != value) { _baseSeed = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Loop (and trim) the soundtrack with FFmpeg so it covers the requested running time. Off, a
        /// track shorter than the video leaves the rest of the take running against silence.
        /// </summary>
        public bool LoopAudio
        {
            get => _loopAudio;
            set
            {
                if (_loopAudio == value) return;
                _loopAudio = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AudioCoverageSummary));
            }
        }

        /// <summary>Sets the running time to the loaded track's length, rounded to the 5-second grid.</summary>
        private void MatchSongLength()
        {
            if (!HasAudioDuration) return;
            TotalSeconds = AudioDurationSeconds;
            AddLog($"Running time set to the song's length: {Fmt(TotalSeconds)} " +
                   $"({PlannedSegmentCount} × {SegmentSeconds:0}s).");
        }

        private void RaisePlanChanged()
        {
            OnPropertyChanged(nameof(PlannedSegmentCount));
            OnPropertyChanged(nameof(PlanSummary));
            OnPropertyChanged(nameof(PromptSegmentSummary));
            OnPropertyChanged(nameof(HasPromptMismatch));
            OnPropertyChanged(nameof(AudioCoverageSummary));
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
        /// Analyze needs Reference 1 — it is what the LLM reads the subject off. Deliberately not gated on
        /// <see cref="VideoProcessingBaseViewModel.IsProcessing"/>: it talks to the llama-server, so the
        /// next chain can be written while one is on the GPU.
        /// </summary>
        public bool CanAnalyze => HasReference1 && !IsAnalyzing;

        /// <summary>Queueing needs the segment plans, the face and the soundtrack. A render in flight does
        /// not block it; an in-flight analysis does, since it is about to overwrite the prompt box.</summary>
        public bool CanGenerate =>
            PromptSegmentCount > 0 && HasReference1 && HasAudio && !IsAnalyzing;

        private string ClosestAspectRatio(string path)
        {
            int w = 0, h = 0;
            if (Reference1Preview is { } preview && string.Equals(path, Reference1Path, StringComparison.OrdinalIgnoreCase))
            {
                w = preview.PixelWidth; h = preview.PixelHeight;
            }
            if ((w <= 0 || h <= 0) && !string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    w = frame.PixelWidth; h = frame.PixelHeight;
                }
                catch { /* fall through to the 16:9 default */ }
            }
            return MiniMaxH3ViewModel.ClosestAspectRatio(w, h);
        }

        /// <summary>
        /// The canvas written onto <c>MiniMaxH3ChainPlan</c>. Unlike the single-clip tabs there is no
        /// ResolutionSelector in this graph — the plan node is the single source of the size for every
        /// segment — so the maths lives here: the aspect's area at this megapixel count, the width snapped
        /// to a multiple of 32, and the height derived from the snapped width.
        /// </summary>
        private static (int Width, int Height) CanvasSize(string aspectOption, double megapixels)
        {
            var ratio = MiniMaxH3ViewModel.AspectRatios
                .FirstOrDefault(a => a.Option == aspectOption).Ratio;
            if (ratio <= 0) ratio = 16.0 / 9.0;

            var area = Math.Max(0.1, megapixels) * 1_000_000.0;
            var w = RoundTo32(Math.Sqrt(area * ratio));
            return (w, RoundTo32(w / ratio));

            static int RoundTo32(double v) => Math.Max(32, (int)Math.Round(v / 32.0) * 32);
        }

        /// <summary>H3's frame grid: 17k+5 at 24 fps. The plan node rounds each segment up onto it, and
        /// this mirrors that so the tab can report the real per-segment length.</summary>
        private static int FramesForSeconds(double seconds)
        {
            var frames = Math.Max(5, (int)Math.Round(seconds * OutputFrameRate));
            return frames + (5 - frames % 17 + 17) % 17;
        }

        #endregion

        #region Segment chain text

        private const string SegmentHeaderFormat = "=== SEGMENT {0} of {1} ===";

        /// <summary>
        /// Matches a segment header on a line of its own. Loose about the decoration around it
        /// (<c>===</c>, <c>##</c>, <c>[SEGMENT 3]</c>, <c>Segment 3:</c> — small models produce all of
        /// them) and accepts <c>CLIP</c> as a synonym, since the H3 system prompts elsewhere use that
        /// word and models mix them up. Capped at 60 characters so a line of plan body that happens to
        /// start with the word cannot be mistaken for a header.
        /// </summary>
        private static readonly Regex SegmentHeaderRegex = new(
            @"^[ \t]*[=#*\-–—\[]{0,6}[ \t]*(?:SEGMENT|CLIP)[ \t]+(\d+)\b[^\r\n]{0,60}$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Splits the chain text into its per-segment plans, headers removed. Text with no headers is one
        /// segment; empty text yields an empty list.
        /// </summary>
        private static List<string> SplitSegments(string? text)
        {
            // Normalized first: `$` in a .NET multiline match sits *before* the \n, so a CRLF header line
            // would never match — and the prompt box hands back CRLF the moment it is edited by hand.
            var t = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (t.Length == 0) return new List<string>();

            var headers = SegmentHeaderRegex.Matches(t);
            if (headers.Count == 0) return new List<string> { t };

            var segments = new List<string>();
            // Anything ahead of the first header is a preamble the model added; it belongs to segment 1.
            var preamble = t[..headers[0].Index].Trim();

            for (var i = 0; i < headers.Count; i++)
            {
                var start = headers[i].Index + headers[i].Length;
                var end = i + 1 < headers.Count ? headers[i + 1].Index : t.Length;
                var body = t[start..end].Trim();

                if (i == 0 && preamble.Length > 0)
                    body = body.Length > 0 ? $"{preamble}\n\n{body}" : preamble;

                if (body.Length > 0) segments.Add(body);
            }

            return segments.Count > 0 ? segments : new List<string> { t };
        }

        /// <summary>Reassembles segment plans into one chain, headers and all. Unlike the clip chains in
        /// the sibling tabs a single segment still gets its header, because the header is what makes the
        /// count in the box unambiguous.</summary>
        private static string JoinSegments(IReadOnlyList<string> segments)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < segments.Count; i++)
            {
                if (i > 0) sb.Append("\n\n");
                sb.AppendFormat(CultureInfo.InvariantCulture, SegmentHeaderFormat, i + 1, segments.Count);
                sb.Append("\n\n").Append(segments[i].Trim());
            }
            return sb.ToString();
        }

        #endregion

        #region Analysis (references → segment plans)

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
                    MessageBox.Show("No LM Studio / llama-server model available. Ensure the server is running and a model is loaded.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var count = PlannedSegmentCount;
                AddLog($"Writing a {count}-segment chain plan ({count} × {SegmentSeconds:0}s = {Fmt(count * SegmentSeconds)}) " +
                       $"— sending to {_lmStudioService.DescribeTarget(model)}");

                var systemPrompt = await ReadSystemPromptAsync(SystemPromptFile, token);

                // Both references go to the LLM here, unlike the 🎭👥 tab. The chain's plan format opens
                // with a subject_definitions block that has to spell the wardrobe and distinctive features
                // out in words — the tags alone do not carry them across a segment boundary — so the model
                // has to be able to see what it is describing.
                var images = new List<string> { Reference1Path };
                if (HasReference2) images.Add(Reference2Path);

                var refBlock = HasReference2
                    ? "You are shown TWO reference images. The first is <Picture 1> — the face. The second is " +
                      "<Picture 2> — the full body, wardrobe and proportions. Both are also given to the video " +
                      "generator, so define <Subject 1> as being fixed jointly by them, and read the wardrobe and " +
                      "distinctive features off <Picture 2>."
                    : "You are shown ONE reference image, <Picture 1>, which is also given to the video generator. " +
                      "Define <Subject 1> as being fixed by it, and read the wardrobe and distinctive features off it. " +
                      "There is no <Picture 2> — never mention that tag.";

                var story = string.IsNullOrWhiteSpace(StoryGuidance)
                    ? "(none — invent a performance that suits the subject and carry it from beginning to end)"
                    : StoryGuidance.Trim();

                var lyrics = string.IsNullOrWhiteSpace(Lyrics)
                    ? "(none supplied — do NOT invent lyrics. Write the performance, the mouth movement and the " +
                      "delivery, and quote no words at all.)"
                    : Lyrics.Trim();

                // The prompt box doubles as the user's draft, unless it already holds a chain — far too
                // long to feed back in as a "draft".
                var draft = PromptSegmentCount > 1
                    ? "(the prompt box holds a previous chain — ignore it and write a fresh one)"
                    : string.IsNullOrWhiteSpace(Prompt)
                        ? "(none)"
                        : Prompt.Trim();

                var userMessage =
                    $"{refBlock}\n" +
                    $"Chain: {count} segment{(count == 1 ? "" : "s")}, each {SegmentSeconds:0} seconds long, " +
                    $"{Fmt(count * SegmentSeconds)} in total. Write ONE continuous take split across those " +
                    $"{count} segments.\n" +
                    $"Soundtrack: the generator hears the matching slice of the song in every segment as <Audio 1>. " +
                    $"You do not hear it.\n" +
                    $"Song lyrics:\n{lyrics}\n" +
                    $"Story the take must tell:\n{story}\n" +
                    $"Draft idea from the user:\n{draft}";

                // A chain needs headroom for N plans. One plan runs ~900 tokens; the floor covers the
                // format itself and the total is capped so the request cannot exceed a modest local
                // context window.
                var maxTokens = Math.Min(48000, 4000 + 1800 * Math.Max(1, count));

                var result = await _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                    model, images, userMessage, systemPrompt, maxTokens: maxTokens, cancellationToken: token);

                var cleaned = CleanOutput(result);
                var segments = SplitSegments(cleaned);
                if (segments.Count == 0)
                {
                    AddLog("WARNING: Analysis returned empty result");
                    return;
                }

                // Re-joined rather than used raw, so the headers are renumbered to what was actually
                // written — the model routinely emits "of N" with the wrong N.
                Prompt = JoinSegments(segments);
                AddLog($"Chain plan written ({segments.Count} segments, {Prompt.Length} chars)");

                if (segments.Count != count)
                    AddLog($"WARNING: asked for {count} segment(s) but the model returned {segments.Count}. " +
                           "Generate submits what is in the prompt box — re-run Analyze, or edit the headers by hand.");

                var drift = DescribeDefinitionDrift(segments);
                if (drift != null)
                    AddLog($"WARNING: {drift} The subject definition is supposed to be identical in every " +
                           "segment — differing wording is a costume or hair change mid-take. Harmonise those " +
                           "lines in the prompt box before generating.");
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
            }
        }

        private static async Task<string> ReadSystemPromptAsync(string fileName, CancellationToken token)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"System prompt not found: {path}");
            return await File.ReadAllTextAsync(path, token);
        }

        /// <summary>
        /// Strips the wrappers small models like to add (code fences, bold markers, a leading "prompt:"
        /// label, surrounding quotes) without touching the plan's field structure.
        /// </summary>
        private static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Replace("**", "").Trim();

            if (text.StartsWith("```"))
            {
                var firstBreak = text.IndexOf('\n');
                if (firstBreak > 0) text = text[(firstBreak + 1)..];
                var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0) text = text[..lastFence];
                text = text.Trim();
            }

            if (text.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase))
                text = text[7..].TrimStart();
            if (text.Length > 1 && text[0] == '"' && text[^1] == '"')
                text = text[1..^1].Trim();

            return text.Trim();
        }

        /// <summary>
        /// Flags a chain whose <c>subject_definitions</c> block is not byte-identical across segments.
        /// Returns null when there is nothing to report.
        ///
        /// <para>That block is what holds the wardrobe, hair and distinctive features. The segments are
        /// encoded separately, so a re-worded definition in segment 7 <i>is</i> a costume change in
        /// segment 7 — and it only becomes visible after minutes of GPU time per segment, which is why it
        /// is worth catching the moment the plan is written.</para>
        /// </summary>
        private static string? DescribeDefinitionDrift(IReadOnlyList<string> segments)
        {
            if (segments.Count < 2) return null;

            var definitions = segments.Select(ExtractDefinitions).ToList();

            var missing = definitions.Select((d, i) => (Text: d, Index: i + 1))
                .Where(x => string.IsNullOrEmpty(x.Text))
                .Select(x => x.Index)
                .ToList();
            if (missing.Count > 0)
                return $"segment{(missing.Count == 1 ? "" : "s")} {string.Join(", ", missing)} " +
                       $"{(missing.Count == 1 ? "has" : "have")} no subject_definitions block.";

            var distinct = definitions.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinct <= 1) return null;

            var odd = definitions.Select((d, i) => (d, i))
                .Where(x => !string.Equals(x.d, definitions[0], StringComparison.OrdinalIgnoreCase))
                .Select(x => x.i + 1)
                .ToList();
            var shown = string.Join(", ", odd.Take(8));
            var more = odd.Count > 8 ? $" (+{odd.Count - 8} more)" : string.Empty;
            return $"segment{(odd.Count == 1 ? "" : "s")} {shown}{more} define the subject differently from segment 1.";
        }

        /// <summary>The <c>subject_definitions</c> block, whitespace-collapsed, or empty if absent.</summary>
        private static string ExtractDefinitions(string plan)
        {
            var start = plan.IndexOf("subject_definitions:", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;
            start += "subject_definitions:".Length;

            var end = plan.IndexOf("summary:", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) end = plan.Length;

            return Regex.Replace(plan[start..end], @"\s+", " ").Trim();
        }

        #endregion

        #region Queue

        public ObservableCollection<H3ChainQueueItem> Queue => _queue;

        public bool HasQueueItems => _queue.Count > 0;
        public bool HasPendingItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Pending);
        public bool HasFailedItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

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
        /// Freezes the whole form into <b>one</b> queue item — a chain is a single submission, so unlike
        /// the sibling tabs the segments do not become separate jobs. The prompt box, not the duration
        /// slider, decides how many segments the chain holds.
        /// </summary>
        private void AddToQueue()
        {
            if (!CanGenerate) return;

            var segments = SplitSegments(Prompt);
            if (segments.Count == 0) return;
            if (segments.Count > MaxSegments)
            {
                AddLog($"The prompt box holds {segments.Count} segments; the chain node stops at {MaxSegments}. " +
                       $"Only the first {MaxSegments} are queued.");
                segments = segments.Take(MaxSegments).ToList();
            }

            var (w, h) = CanvasSize(ResolvedAspectRatio, Megapixels);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            var item = new H3ChainQueueItem
            {
                Reference1Path = Reference1Path,
                Reference2Path = HasReference2 ? Reference2Path : string.Empty,
                AudioPath = AudioPath,
                SegmentPrompts = segments,
                SegmentSeconds = SegmentSeconds,
                Width = w,
                Height = h,
                Steps = Steps,
                ContextLength = ContextLength,
                BaseSeed = BaseSeed,
                // Frozen now and never regenerated: it is the checkpoint identity that makes Resume work.
                RunName = $"chain_{stamp}",
                GenerationFingerprint = Fingerprint(w, h, HasReference2, AudioMode, ContextLength),
                StartSegment = 1,
                ItemStatus = QueueItemStatus.Pending,
            };

            _queue.Add(item);
            AddLog($"Queued: {item.DisplayText}");
            AddLog($"Checkpoints: {ChainRootFolder}/{item.RunName}/ — the chain resumes from there if it is interrupted.");

            SaveQueueToFile();
            UpdateQueueStatus();

            if (!IsProcessingQueue) _ = ProcessQueueAsync();
        }

        /// <summary>
        /// Tags the configuration a chain's checkpoints were rendered under. <c>MiniMaxH3ChainPlan</c>
        /// enforces it on resume, so changing the canvas, the cast or the audio handling correctly refuses
        /// to graft new segments onto old ones.
        /// </summary>
        private static string Fingerprint(int width, int height, bool twoRefs, string audioMode, int contextLength) =>
            $"{width}x{height}-{(twoRefs ? "2ref" : "1ref")}-{audioMode}-ctx{contextLength}";

        private void RemoveQueueItem(H3ChainQueueItem? item)
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

        /// <summary>
        /// Restarts an interrupted chain at its first unrendered segment, turning a crashed 40-minute
        /// chain into however much of it was left.
        ///
        /// <para>Two independent sources agree on how far it got, because neither is always available:
        /// the <c>segments/</c> folder is authoritative but only when the ComfyUI host's output directory
        /// is visible from this machine, and <see cref="H3ChainQueueItem.LastReportedSegment"/> — read off
        /// the executing node id while the chain ran and persisted with the item — needs no share at all
        /// but only knows which segment <i>started</i>. The higher count wins, minus one for the segment
        /// that was in flight when it died.</para>
        /// </summary>
        private async Task ResumeItemAsync(H3ChainQueueItem? item)
        {
            if (item == null || item.ItemStatus == QueueItemStatus.Processing) return;

            var onDisk = await Task.Run(() => CountCheckpointedSegments(item.RunName));
            // The segment that was mid-render when the run died is not finished, so it is redone.
            var reported = Math.Max(0, item.LastReportedSegment - 1);
            var done = Math.Max(onDisk, reported);

            if (onDisk < 0)
                AddLog($"The chain folder {ChainRootFolder}/{item.RunName}/{SegmentsFolder}/ is not visible " +
                       "from this machine, so the count comes from what the run reported before it stopped.");

            if (done <= 0)
            {
                AddLog($"Nothing to resume for {item.RunName} — the chain restarts from segment 1. " +
                       "If segments do exist on the ComfyUI host, they are reused automatically anyway.");
                item.StartSegment = 1;
            }
            else if (done >= item.SegmentCount)
            {
                AddLog($"All {item.SegmentCount} segments are already checkpointed — re-running only assembles them.");
                item.StartSegment = item.SegmentCount;
            }
            else
            {
                item.StartSegment = done + 1;
                AddLog($"Resuming at segment {item.StartSegment} of {item.SegmentCount} — " +
                       $"{done} already rendered ({Fmt(done * item.SegmentSeconds)} of video kept).");
            }

            item.ItemStatus = QueueItemStatus.Pending;
            item.ErrorMessage = null;
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

            OnPropertyChanged(nameof(HasPendingItems));
            OnPropertyChanged(nameof(HasFailedItems));
            OnCanExecuteChanged();
        }

        /// <summary>
        /// Drains pending chains one at a time. The coordinator lease is taken per item rather than around
        /// the loop, so a queue of chains does not lock every other tab out of ComfyUI for hours.
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;

            IsProcessingQueue = true;
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;

            AddLog("Starting H3 Chain queue...");
            try
            {
                H3ChainQueueItem? item;
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
                    }
                    catch (OperationCanceledException)
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
                        AddLog("Queue stopped — the chain is back to Pending. " +
                               "Use ⏩ Resume to pick it up from its last checkpointed segment.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (await TryHandleCrashAndRetryAsync(item, ex))
                        {
                            item.ItemStatus = QueueItemStatus.Pending;
                            AddLog("Chain reset to Pending — will retry after ComfyUI restart");
                        }
                        else
                        {
                            item.ItemStatus = QueueItemStatus.Failed;
                            item.ErrorMessage = ex.Message;
                            AddLog($"FAILED: {ex.Message}");
                            AddLog("Rendered segments are checkpointed — ⏩ Resume restarts from the first one missing.");
                        }
                    }

                    UpdateQueueStatus();
                    SaveQueueToFile();
                }
            }
            finally
            {
                IsProcessingQueue = false;
                ProcessingStatus = token.IsCancellationRequested ? "Queue stopped" : "Queue finished";
                AddLog("Queue processing finished.");
                OnCanExecuteChanged();
            }
        }

        #endregion

        #region Queue persistence

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
        /// Defers the persisted queue read to Background dispatcher priority, with the file I/O itself on a
        /// worker thread — this view model is built during app startup and must not do disk work in its
        /// constructor.
        /// </summary>
        private void ScheduleQueueLoad()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                _ = LoadQueueFromFileAsync();
                return;
            }

            dispatcher.InvokeAsync(async () => await LoadQueueFromFileAsync(),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private async Task LoadQueueFromFileAsync()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;

                var items = await Task.Run(() =>
                    JsonSerializer.Deserialize<List<H3ChainQueueItem>>(File.ReadAllText(QueueFilePath)));
                if (items == null || items.Count == 0) return;

                _queue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Completed) continue;
                    // Anything left mid-flight by a crash or a close is unfinished work, not a running job.
                    if (item.ItemStatus == QueueItemStatus.Processing) item.ItemStatus = QueueItemStatus.Pending;
                    _queue.Add(item);
                }

                UpdateQueueStatus();
                // Deliberately not auto-started: a leftover chain is hours of GPU time and should not seize
                // the card the moment the app opens.
                if (HasPendingItems)
                    AddLog($"Queue restored: {_queue.Count} chain(s) — press ▶ Start to run one, " +
                           "or ⏩ Resume to continue an interrupted one from its checkpoints.");
                else if (_queue.Count > 0)
                    AddLog($"Queue restored: {_queue.Count} chain(s)");
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        #endregion

        #region Generation

        private async Task GenerateItemAsync(H3ChainQueueItem item, CancellationToken token)
        {
            IsProcessing = true;
            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = "Preparing H3 Chain workflow...";

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            var tempAudio = string.Empty;

            try
            {
                AddLog($"=== H3 Chain: {item.SegmentCount} segments × {item.SegmentSeconds:0}s " +
                       $"= {Fmt(item.TotalSeconds)} ({(item.HasReference2 ? "2 references" : "1 reference")}) ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("H3Chain", token);

                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFileName);
                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
                var json = await File.ReadAllTextAsync(workflowPath, token);

                // ── References ───────────────────────────────────────────────
                ProcessingStatus = "Uploading references...";
                ProcessingProgress = 2;
                if (!File.Exists(item.Reference1Path))
                    throw new FileNotFoundException($"Reference 1 is gone: {item.Reference1Path}");
                var ref1 = await _comfyUIService.UploadImageAsync(item.Reference1Path);
                if (string.IsNullOrEmpty(ref1)) throw new Exception("Failed to upload Reference 1.");
                AddLog($"Reference 1 uploaded: {ref1}");
                SetInput(ref json, NodeReference1, "image", ref1);

                if (item.HasReference2)
                {
                    if (!File.Exists(item.Reference2Path))
                        throw new FileNotFoundException($"Reference 2 is gone: {item.Reference2Path}");
                    var ref2 = await _comfyUIService.UploadImageAsync(item.Reference2Path);
                    if (string.IsNullOrEmpty(ref2)) throw new Exception("Failed to upload Reference 2.");
                    AddLog($"Reference 2 uploaded: {ref2}");
                    SetInput(ref json, NodeReference2, "image", ref2);
                }
                else
                {
                    json = RemoveSecondReference(json);
                    AddLog("Single-reference chain: ref_image_1 and its loader are removed from the graph.");
                }

                // ── Soundtrack ───────────────────────────────────────────────
                ProcessingStatus = "Preparing the soundtrack...";
                ProcessingProgress = 5;
                if (!File.Exists(item.AudioPath))
                    throw new FileNotFoundException($"The soundtrack is gone: {item.AudioPath}");

                var (audioForUpload, audioIsTemp, looped) = PrepareAudio(item);
                if (audioIsTemp) tempAudio = audioForUpload;
                item.AudioLooped = looped;

                var audioName = await _comfyUIService.UploadAudioAsync(audioForUpload);
                if (string.IsNullOrEmpty(audioName)) throw new Exception("Failed to upload the soundtrack.");
                // Audio upload (unlike video) doesn't self-verify — confirm the bytes actually persisted so
                // a phantom 2xx doesn't surface later as a cryptic LoadAudio decode error.
                await _comfyUIService.HttpClient.VerifyInputFileExistsAsync(audioName, "", token);
                AddLog($"Soundtrack uploaded: {audioName}");
                SetInput(ref json, NodeAudio, "audio", audioName);

                // ── Plan ─────────────────────────────────────────────────────
                var baseSeed = item.BaseSeed >= 0 ? item.BaseSeed : System.Random.Shared.NextInt64(0, int.MaxValue);
                var runToken = item.RunName;
                var finalName = $"{runToken}_final";

                SetInput(ref json, NodePlan, "plan_json", BuildPlanJson(item, baseSeed));
                SetInput(ref json, NodePlan, "run_name", runToken);
                SetInput(ref json, NodePlan, "generation_fingerprint", item.GenerationFingerprint);
                SetInput(ref json, NodePlan, "width", item.Width);
                SetInput(ref json, NodePlan, "height", item.Height);
                SetInput(ref json, NodePlan, "context_length", item.ContextLength);
                SetInput(ref json, NodePlan, "audio_mode", AudioMode);
                SetInput(ref json, NodePlan, "default_duration_seconds", item.SegmentSeconds);
                SetInput(ref json, NodePlan, "default_steps", item.Steps);
                SetInput(ref json, NodePlan, "base_seed", baseSeed);

                SetInput(ref json, NodeLoopStart, "start_clip", Math.Clamp(item.StartSegment, 1, Math.Max(1, item.SegmentCount)));
                SetInput(ref json, NodeLoopStart, "scene_range", string.Empty);

                SetInput(ref json, NodeAssemble, "filename", finalName);
                SetInput(ref json, NodeTrim, "fps", (double)OutputFrameRate);

                // ── Submit ───────────────────────────────────────────────────
                AddLog($"Plan: {item.SegmentCount} segments, {item.Width}×{item.Height}, {item.Steps} steps, " +
                       $"context {item.ContextLength} frames, audio {AudioMode}, base seed {baseSeed}.");
                AddLog($"Chain folder on the ComfyUI host: {ChainRootFolder}/{runToken}/");
                if (item.StartSegment > 1)
                    AddLog($"Resuming at segment {item.StartSegment} — earlier segments are read from the checkpoint.");

                _currentSegment = Math.Clamp(item.StartSegment, 1, Math.Max(1, item.SegmentCount));
                ProcessingProgress = 8;
                ProcessingStatus = $"Rendering segment {_currentSegment} of {item.SegmentCount}...";

                // Every segment is a full H3 pass. The default 30-minute ceiling would abort a chain of any
                // real length mid-render, so the budget is scaled to the plan with a generous floor.
                var remaining = Math.Max(1, item.SegmentCount - item.StartSegment + 1);
                var budget = TimeSpan.FromMinutes(Math.Min(24 * 60, 30 + 20 * remaining));
                AddLog($"Execution budget: {budget.TotalHours:0.#}h for {remaining} segment(s).");

                var promptId = await SubmitAsync(json, item, 8, 92, budget, token);

                ProcessingStatus = "Locating the assembled take...";
                var local = await ResolveFinalVideoAsync(promptId, runToken, finalName, token);
                if (local == null || !File.Exists(local))
                    throw new Exception(
                        "The chain finished but the assembled MP4 could not be retrieved. Look for " +
                        $"{ChainRootFolder}/{runToken}/{FinalFolder}/{finalName}.mp4 on the ComfyUI host.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "H3Chain");
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"H3Chain_{runToken}.mp4");
                if (!string.Equals(local, finalPath, StringComparison.OrdinalIgnoreCase))
                    File.Copy(local, finalPath, true);
                await LocalCopyService.CopyVideoAsync(finalPath);

                var fi = new FileInfo(finalPath);
                var measured = GetVideoDuration(finalPath);
                var length = measured > 0 ? Fmt(measured) : Fmt(item.TotalSeconds);
                var cast = item.HasReference2 ? "2 refs" : "1 ref";
                var audio = item.AudioLooped ? "looped track" : "source track";

                item.OutputVideoPath = finalPath;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResultVideoPath = finalPath;
                    ResultVideoInfo = $"H3 Chain • {item.SegmentCount} segments • {length} • {cast} • " +
                                      $"{item.Width}×{item.Height} • {audio} • {fi.Length / 1024 / 1024.0:F1}MB";
                    HasResult = true;
                    OnCanExecuteChanged();
                });
                ProcessingProgress = 100;
                ProcessingStatus = "Complete!";
                AddLog($"=== Complete: {finalPath} ===");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The queue loop decides whether this is a retry or a failure; it just needs the reason.
                AddLog($"ERROR: {ex.Message}");
                ProcessingStatus = $"Error: {ex.Message}";
                throw;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempAudio))
                {
                    try { File.Delete(tempAudio); } catch { /* temp file: best effort */ }
                }
                lease?.Dispose();
                IsProcessing = false;
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Builds <c>MiniMaxH3ChainPlan.plan_json</c>: shared defaults plus one entry per segment carrying
        /// its id, its plan text and its seed.
        ///
        /// <para>Seeds are derived here rather than left to the node's own <c>base_seed</c> derivation so
        /// that they are written into the plan itself — which means a resumed chain reuses exactly the
        /// seeds its earlier segments were rendered with, and the plan the user can read in the log is the
        /// plan that ran. They are emitted as strings, matching the node's own exports, because a 15-digit
        /// seed does not survive a round trip through a JSON double.</para>
        /// </summary>
        private static string BuildPlanJson(H3ChainQueueItem item, long baseSeed)
        {
            var shots = new JsonArray();
            for (var i = 0; i < item.SegmentPrompts.Count; i++)
            {
                // Cheap, stable and order-dependent: the same base seed always yields the same per-segment
                // seeds, so a resume lands on the sequence the first run was rendering.
                var seed = unchecked((long)((ulong)baseSeed * 6364136223846793005UL + (ulong)(i + 1) * 1442695040888963407UL));
                seed &= 0x7FFFFFFFFFFFL;

                shots.Add(new JsonObject
                {
                    ["id"] = string.Format(CultureInfo.InvariantCulture, "clip_{0:00}", i + 1),
                    ["prompt"] = item.SegmentPrompts[i],
                    ["seed"] = seed.ToString(CultureInfo.InvariantCulture),
                });
            }

            var plan = new JsonObject
            {
                ["defaults"] = new JsonObject
                {
                    ["duration_seconds"] = item.SegmentSeconds,
                    ["steps"] = item.Steps,
                },
                ["shots"] = shots,
            };

            return plan.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// Normalizes the soundtrack for ComfyUI's <c>LoadAudio</c>, and loops or trims it to the chain's
        /// running time when <see cref="LoopAudio"/> is on.
        ///
        /// <para>The normalization is the same defence the 🪪 tab uses: LoadAudio decodes with PyAV, which
        /// chokes on some MP3/M4A headers, and a clean PCM WAV sidesteps it. The looping is what lets a
        /// three-minute song carry a ten-minute take — without it the chain slices past the end of the
        /// track and the rest of the video runs against silence.</para>
        ///
        /// <para>Falls back to the original file whenever FFmpeg is unavailable or produces nothing, so a
        /// missing FFmpeg costs the loop, not the run.</para>
        /// </summary>
        private (string Path, bool IsTemp, bool Looped) PrepareAudio(H3ChainQueueItem item)
        {
            var ffmpeg = FindFFmpeg();
            if (ffmpeg == null)
            {
                AddLog("FFmpeg not found — uploading the soundtrack as-is (no looping, no normalization).");
                return (item.AudioPath, false, false);
            }

            // A little past the nominal length: every segment is rounded up onto H3's frame grid, so the
            // rendered take is slightly longer than segments × seconds and the last slice must still land
            // inside the track.
            var target = item.TotalSeconds + item.SegmentSeconds;
            var wantLoop = LoopAudio;

            try
            {
                var outPath = Path.Combine(Path.GetTempPath(), $"h3chain_audio_{Guid.NewGuid():N}.wav");
                var args = new List<string> { "-y" };
                // -stream_loop precedes -i: it applies to the input, and -t then cuts the looped stream.
                if (wantLoop) args.AddRange(new[] { "-stream_loop", "-1" });
                args.AddRange(new[] { "-i", item.AudioPath });
                if (wantLoop) args.AddRange(new[] { "-t", target.ToString("0.###", CultureInfo.InvariantCulture) });
                args.AddRange(new[] { "-vn", "-ac", "2", "-ar", "44100", "-c:a", "pcm_s16le", outPath });

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return (item.AudioPath, false, false);
                // stderr is drained before the wait: FFmpeg logs everything there and blocks once the pipe
                // buffer fills, which would otherwise hang the run.
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(300000);

                if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                {
                    var mb = new FileInfo(outPath).Length / 1024.0 / 1024.0;
                    // Looping a track that already covers the take is just a trim; say which happened.
                    var sourceLength = AudioDurationSeconds;
                    var reallyLooped = wantLoop && sourceLength > 0.5 && sourceLength < target - 0.5;
                    AddLog(wantLoop
                        ? $"Soundtrack {(reallyLooped ? "looped" : "trimmed")} to {Fmt(target)} and normalized to WAV ({mb:F1}MB)."
                        : $"Soundtrack normalized to WAV ({mb:F1}MB) — used at its own length.");
                    return (outPath, true, reallyLooped);
                }

                var tail = string.IsNullOrEmpty(err) ? string.Empty : err[Math.Max(0, err.Length - 300)..];
                AddLog($"Audio transcode produced no file; uploading the original. {tail}");
                return (item.AudioPath, false, false);
            }
            catch (Exception ex)
            {
                AddLog($"Audio transcode failed: {ex.Message}; uploading the original.");
                return (item.AudioPath, false, false);
            }
        }

        /// <summary>
        /// Drops the second reference. The graph is authored for two, so rather than leaving a loader
        /// pointed at a placeholder filename (which fails validation), the <c>ref_image_1</c> slot and node
        /// 911 are both removed and the chain runs on <c>&lt;Picture 1&gt;</c> alone.
        /// </summary>
        private static string RemoveSecondReference(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeRefToVideo, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodeReference2, "LoadImage");

            if (root[NodeRefToVideo]?["inputs"] is not JsonObject refInputs)
                throw new Exception($"Workflow node '{NodeRefToVideo}' has no inputs — the workflow file no longer matches this tab.");

            refInputs.Remove("ref_images.ref_image_1");
            root.Remove(NodeReference2);
            return root.ToJsonString();
        }

        /// <summary>
        /// Wrapper around <see cref="WorkflowNodeUpdater.UpdateNodeInput"/> that fails loudly on a node id
        /// or input that is no longer in the graph. The updater silently no-ops instead, which here would
        /// mean shipping the template's placeholder plan and reference filenames to the GPU.
        /// </summary>
        private static void SetInput(ref string json, string nodeId, string input, object value)
        {
            if (WorkflowNodeUpdater.GetNodeInput(json, nodeId, input) == null)
                throw new Exception($"Workflow node '{nodeId}' has no input '{input}' — the workflow file no longer matches this tab.");
            WorkflowNodeUpdater.UpdateNodeInput(ref json, nodeId, input, value);
        }

        /// <summary>Fails loudly when a node the patches rewire is missing or is no longer the class they
        /// assume — both would otherwise produce a graph that only fails on the server, or worse, silently
        /// renders the wrong thing.</summary>
        private static void RequireClass(JsonObject root, string nodeId, string expected)
        {
            if (root[nodeId] is not JsonObject node)
                throw new Exception($"Workflow node '{nodeId}' is not in the graph — the workflow file no longer matches this tab.");
            var actual = node["class_type"]?.GetValue<string>();
            if (actual != expected)
                throw new Exception($"Workflow node '{nodeId}' is a {actual ?? "(none)"}, expected {expected} — the workflow file no longer matches this tab.");
        }

        private async Task<string> SubmitAsync(string json, H3ChainQueueItem item,
            double progressFrom, double progressTo, TimeSpan budget, CancellationToken token)
        {
            var workflow = JsonSerializer.Deserialize<JsonElement>(json);
            var total = Math.Max(1, item.SegmentCount);

            // A chain is one prompt, so sampler progress restarts at every segment and on its own says
            // nothing about how far the take has got. The executing node id does: the loop body is cloned
            // per segment and gains one nesting prefix each time (see SegmentFromNodeId), so it is read
            // here and the sampler's own percentage is mapped into that segment's slice of the bar.
            var progress = new Progress<ProgressMessage>(msg =>
            {
                var data = msg.Data;
                if (data == null || data.Max <= 0) return;

                var segment = SegmentFromNodeId(data.Node, total);
                var pct = (double)data.Value / data.Max;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    // Monotonic: nodes outside the loop body carry no nesting prefix and so read as
                    // segment 1 — including Assemble, which runs *after* the last segment. Letting the
                    // count fall back would walk the bar and the status text backwards at the very end.
                    if (segment > _currentSegment)
                    {
                        _currentSegment = segment;
                        item.LastReportedSegment = segment;
                        ProcessingStatus = $"Rendering segment {segment} of {total}...";
                        AddLog($"Segment {segment} of {total} started.");
                    }

                    var index = Math.Max(1, _currentSegment);
                    var from = progressFrom + (progressTo - progressFrom) * (index - 1) / total;
                    var to = progressFrom + (progressTo - progressFrom) * index / total;
                    ProcessingProgress = from + pct * (to - from);
                });
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token, budget);
            AddLog($"Workflow submitted, ID: {promptId}");
            return promptId;
        }

        /// <summary>Which segment the chain is on, as last reported by the executing node id.</summary>
        private int _currentSegment;

        /// <summary>
        /// Counts occurrences of the Loop End node's id prefix in an executing node id, which is how far
        /// into the chain the running segment is.
        ///
        /// <para>ComfyUI's recursive loop clones the body for each segment and reports the clone's id with
        /// one nesting prefix per iteration. Segment Save came back from a four-segment run as:</para>
        /// <code>
        /// 1704
        /// 1705.0.0.1704
        /// 1705.0.0.Recurse.0.0.1705.0.0.1704
        /// 1705.0.0.Recurse.0.0.Recurse.0.0.1705.0.0.Recurse.0.0.1705.0.0.1704
        /// </code>
        /// <para>The <c>Recurse</c> markers are not linear, but the count of <c>1705.</c> is exactly the
        /// zero-based segment index — and it holds for every node in the body, not just Segment Save, so
        /// it can be read off the sampler's own progress messages.</para>
        ///
        /// <para>Returns 0 for an id this cannot read, which leaves the caller on the segment it already
        /// knew about rather than jumping the bar around.</para>
        /// </summary>
        private static int SegmentFromNodeId(string? nodeId, int total)
        {
            if (string.IsNullOrEmpty(nodeId)) return 0;

            var depth = 0;
            var needle = NodeLoopEnd + ".";
            var at = nodeId.IndexOf(needle, StringComparison.Ordinal);
            while (at >= 0)
            {
                depth++;
                at = nodeId.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
            }

            return Math.Clamp(depth + 1, 1, Math.Max(1, total));
        }

        /// <summary>
        /// ComfyUI's output folder as this machine sees it — the remote path when ComfyUI is on another
        /// host, since that is the share the outputs actually land on.
        /// </summary>
        private string OutputRoot()
        {
            var settings = _settingsService.Settings;
            if (settings == null) return string.Empty;
            var isRemote = IsComfyUIRemote(new Uri(GetComfyUIBaseUrl()).Host);
            return settings.ResolveOutputFolder(isRemote);
        }

        /// <summary>This run's <c>segments/</c> folder as this machine sees it, or empty when the output
        /// root is not configured.</summary>
        private string SegmentsDir(string runName)
        {
            var root = OutputRoot();
            return string.IsNullOrEmpty(root)
                ? string.Empty
                : Path.Combine(root, ChainRootFolder, runName, SegmentsFolder);
        }

        /// <summary>
        /// How many segments of a chain are checkpointed, counted from the <c>segments/</c> folder.
        /// Returns -1 when that folder cannot be seen from this machine at all, which is a different
        /// answer from "none rendered" and is what stops <see cref="ResumeItemAsync"/> from silently
        /// restarting a chain that in fact got most of the way through.
        ///
        /// <para>The host's output directory is not necessarily the folder in settings, so this is
        /// genuinely optional information — <see cref="H3ChainQueueItem.LastReportedSegment"/> is the
        /// fallback and needs no share at all.</para>
        /// </summary>
        private int CountCheckpointedSegments(string runName)
        {
            try
            {
                var dir = SegmentsDir(runName);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return -1;

                // Segments are written as clip_0001.<hash>.mp4; nothing else lands in this folder.
                return Directory.EnumerateFiles(dir, "*.mp4", SearchOption.TopDirectoryOnly).Count();
            }
            catch { return -1; }
        }

        /// <summary>
        /// Finds the assembled take.
        ///
        /// <para><c>MiniMaxH3ChainAssemble</c> is an OUTPUT_NODE that reports no media at all — its only
        /// output is a <b>text</b> line naming the file it wrote, as an absolute path on the ComfyUI
        /// host:</para>
        /// <code>assembled 4 generated clips with ffmpeg -> /mnt/output/h3_chains/{run}/final/{name}.mp4</code>
        /// <para>So that line is the authority, and it is read first. The server path is then turned into
        /// a ComfyUI-relative <c>subfolder/filename</c> by cutting it at the chain root, which is what
        /// makes the file retrievable no matter where the host's output directory lives.</para>
        ///
        /// <para>The local share is only an optimization, and deliberately not the primary route: the
        /// host's output directory is not necessarily the folder in settings — on this setup ComfyUI's
        /// <c>/mnt/output</c> serves the chain folder happily over /view while the mapped drive does not
        /// show it at all.</para>
        /// </summary>
        private async Task<string?> ResolveFinalVideoAsync(string promptId, string runName,
            string finalName, CancellationToken token)
        {
            var reported = await ReadAssembledPathAsync(promptId, token);
            if (reported != null)
                AddLog($"Assemble reported: {reported}");

            // Ordered by reliability: what the node said, then where the node's own layout puts it.
            var relatives = new List<string>();
            if (reported != null && ToChainRelative(reported) is { } fromReport) relatives.Add(fromReport);
            relatives.Add($"{ChainRootFolder}/{runName}/{FinalFolder}/{finalName}.mp4");

            foreach (var relative in relatives.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var local = await ResolveChainFileAsync(relative);
                if (local != null) return local;
            }

            // Last resort: the assembled name is unique to this run, so a scan cannot pick up someone
            // else's file even if the chain folder is somewhere unexpected.
            var root = OutputRoot();
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                try
                {
                    var hit = Directory.EnumerateFiles(root, $"*{finalName}*.mp4", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTime)
                        .FirstOrDefault();
                    if (hit != null)
                    {
                        await WaitForFileStableAsync(hit);
                        AddLog($"Assembled take found by scan: {hit}");
                        return hit;
                    }
                }
                catch (Exception ex) { AddLog($"Output scan failed: {ex.Message}"); }
            }

            return null;
        }

        /// <summary>
        /// The absolute path <c>MiniMaxH3ChainAssemble</c> says it wrote, read out of its text output.
        /// Null when the node reported nothing recognizable.
        /// </summary>
        private async Task<string?> ReadAssembledPathAsync(string promptId, CancellationToken token)
        {
            try
            {
                var texts = await _comfyUIService.HttpClient.GetTextOutputsByNodeAsync(promptId, token);
                if (texts.Count == 0) return null;

                // Keyed by the plain node id: unlike the loop body, whose ids gain a nesting prefix per
                // segment ("1705.0.0.Recurse.0.0.1705.0.0.1704"), Assemble runs once and keeps its own.
                var lines = texts.TryGetValue(NodeAssemble, out var mine)
                    ? mine
                    : texts.Values.SelectMany(v => v).ToList();

                foreach (var line in lines)
                {
                    var m = Regex.Match(line, @"->\s*(?<path>\S.*\.mp4)\s*$");
                    if (m.Success) return m.Groups["path"].Value.Trim();
                }
            }
            catch (Exception ex) { AddLog($"Could not read the Assemble node's output: {ex.Message}"); }
            return null;
        }

        /// <summary>
        /// Rewrites a path reported by the chain nodes — absolute, and on the ComfyUI host's filesystem —
        /// as a path relative to ComfyUI's output directory, by cutting it at the chain root folder.
        /// Null when the path does not sit under one.
        /// </summary>
        private static string? ToChainRelative(string reportedPath)
        {
            var normalized = reportedPath.Replace('\\', '/');
            var idx = normalized.IndexOf($"/{ChainRootFolder}/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return normalized[(idx + 1)..];
            return normalized.StartsWith($"{ChainRootFolder}/", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : null;
        }

        /// <summary>
        /// Resolves one ComfyUI-relative output path to a file on this machine: the local share if it
        /// happens to expose it, otherwise a download through ComfyUI's own /view endpoint.
        /// </summary>
        private async Task<string?> ResolveChainFileAsync(string relative)
        {
            var outputFolder = OutputRoot();
            if (!string.IsNullOrEmpty(outputFolder))
            {
                var localPath = Path.Combine(outputFolder, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(localPath))
                {
                    await WaitForFileStableAsync(localPath);
                    AddLog($"Assembled take (local): {localPath}");
                    return localPath;
                }
            }

            var parts = relative.Split('/');
            var filename = parts[^1];
            var subfolder = parts.Length > 1 ? string.Join("/", parts[..^1]) : string.Empty;

            try
            {
                var bytes = await _comfyUIService.HttpClient.DownloadViewFileAsync(filename, subfolder, "output");
                if (bytes is { Length: > 0 })
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"h3chain_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tempPath, bytes);
                    AddLog($"Assembled take downloaded from ComfyUI: {subfolder}/{filename} " +
                           $"({bytes.Length / 1024 / 1024.0:F1}MB)");
                    return tempPath;
                }
            }
            catch (Exception ex) { AddLog($"Download of {relative} failed: {ex.Message}"); }

            return null;
        }

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(CanAnalyze));
            OnPropertyChanged(nameof(CanGenerate));
            AnalyzeCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            MatchSongLengthCommand.NotifyCanExecuteChanged();
            RemoveQueueItemCommand.NotifyCanExecuteChanged();
            ResumeQueueItemCommand.NotifyCanExecuteChanged();
            ClearQueueCommand.NotifyCanExecuteChanged();
            StartQueueCommand.NotifyCanExecuteChanged();
            StopQueueCommand.NotifyCanExecuteChanged();
            ReprocessAllFailedCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>One of the four overlap lengths <c>MiniMaxH3ChainPlan</c> accepts.</summary>
    public record ContextOption(int Value, string Label);

    /// <summary>One of the chain's audio modes, paired with what it actually does.</summary>
    public record AudioModeOption(string Value, string Label);
}
