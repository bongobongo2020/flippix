using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    public partial class WanScailViewModel : VideoProcessingBaseViewModel
    {
        // Number of frames per externally-sliced chunk. Subclasses whose workflow loops
        // over the whole video internally override this to force a single-shot run.
        protected virtual int FramesPerChunk => 121;
        protected virtual string WorkflowFileName => Path.Combine("video", "wan", "SCAIL+Video+Multi-Character+Motion+Transfer+V1API.json");

        // Client-side ceiling for a single ComfyUI prompt. The SCAIL II "simple" workflow runs
        // the whole video in one prompt (segments loop inside SCAIL2SimpleVideo), so it can far
        // exceed the default 30 min on longer clips. Real completion is detected via /history,
        // so this is just a safety net — keep it generous.
        protected virtual TimeSpan ExecutionTimeout => TimeSpan.FromHours(3);
        private const string OutputSubfolder = "wan_scail";

        private string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "wan_scail_queue.json");

        // ── Input fields ──────────────────────────────────────────────────────
        private string _characterImagePath = string.Empty;
        private BitmapImage? _characterImagePreview;
        private string _characterImageInfo = string.Empty;

        private string _inputVideoPath = string.Empty;
        private string _inputVideoInfo = string.Empty;

        private string _prompt = string.Empty;
        private string _negativePrompt = "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";
        private int _fps = 24;
        private int _maxEdge = 1280;
        private long _seed = -1;
        private int _totalFrames;
        private bool _isAnalyzing;
        private bool _isProcessingQueue;
        private string _queueStatus = string.Empty;

        // ── Video editor / chunk timeline ─────────────────────────────────────
        private string? _videoFileUri;
        private bool _hasVideoInfo;
        private string _videoDuration = "—";
        private string _videoFpsDisplay = "—";
        private string _videoFrameCountDisplay = "—";
        private string _videoChunksDisplay = "—";
        private string _chunkSelectionInfo = "Load a video to see chunks";
        private int _selectedChunkIndex;

        private readonly ObservableCollection<WanScailChunkItem> _chunkItems = new();

        // ── State ──────────────────────────────────────────────────────────────
        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private readonly ChunkPromptCacheService _promptCache = new();
        private readonly Dictionary<int, string> _chunkPrompts = new();
        private readonly ObservableCollection<WanScailQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;
        private CancellationTokenSource? _analyzeCts;
        private bool _isAnalyzingAll;

        public event EventHandler<TimeSpan>? SeekRequested;

        // ── Generation wall-clock timer (for A/B comparing settings like the VRAM toggle) ──
        private readonly Stopwatch _genStopwatch = new();
        private System.Windows.Threading.DispatcherTimer? _genTimer;
        private string _generationTimer = "—";
        /// <summary>
        /// Live "mm:ss" wall-clock elapsed while a generation runs, then a final
        /// "✓ Done in mm:ss" / "✗ Stopped at mm:ss" once it finishes. Lets the VRAM toggle's
        /// effect on speed be compared at a glance.
        /// </summary>
        public string GenerationTimer
        {
            get => _generationTimer;
            private set { if (_generationTimer != value) { _generationTimer = value; OnPropertyChanged(); } }
        }

        private static string FormatElapsed(TimeSpan t) =>
            t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";

        private void StartGenerationTimer() => Application.Current?.Dispatcher.Invoke(() =>
        {
            _genStopwatch.Restart();
            GenerationTimer = "⏱ 00:00";
            if (_genTimer == null)
            {
                _genTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _genTimer.Tick += (_, _) => GenerationTimer = $"⏱ {FormatElapsed(_genStopwatch.Elapsed)}";
            }
            _genTimer.Start();
        });

        private void StopGenerationTimer(bool success) => Application.Current?.Dispatcher.Invoke(() =>
        {
            _genTimer?.Stop();
            _genStopwatch.Stop();
            GenerationTimer = $"{(success ? "✓ Done in" : "✗ Stopped at")} {FormatElapsed(_genStopwatch.Elapsed)}";
        });

        // ── Trim / scrub / ETA (let the user shorten the clip before processing) ──────────
        // Seconds-per-frame estimate for the ETA. Seeded from an ~81-frame window taking
        // ~4.5 min (≈3.3 s/frame), then recalibrated from the actual elapsed time after each
        // run so the estimate converges to this machine + resolution.
        private static double _secondsPerFrame = 270.0 / 81.0;

        private double _videoDurationSeconds;
        public double VideoDurationSeconds
        {
            get => _videoDurationSeconds;
            private set { if (Math.Abs(_videoDurationSeconds - value) > 0.001) { _videoDurationSeconds = value; OnPropertyChanged(); RaiseTrimDerived(); } }
        }

        private double _trimInSeconds;
        /// <summary>Start of the kept range, in seconds. Clamped to [0, TrimOut].</summary>
        public double TrimInSeconds
        {
            get => _trimInSeconds;
            set
            {
                var v = Math.Max(0, value);
                if (_trimOutSeconds > 0) v = Math.Min(v, _trimOutSeconds);
                if (Math.Abs(_trimInSeconds - v) > 0.001) { _trimInSeconds = v; OnPropertyChanged(); RaiseTrimDerived(); }
            }
        }

        private double _trimOutSeconds;
        /// <summary>End of the kept range, in seconds. 0/duration means "to the end".</summary>
        public double TrimOutSeconds
        {
            get => _trimOutSeconds;
            set
            {
                var v = value <= 0 ? VideoDurationSeconds : Math.Min(value, VideoDurationSeconds);
                if (v < _trimInSeconds) v = _trimInSeconds;
                if (Math.Abs(_trimOutSeconds - v) > 0.001) { _trimOutSeconds = v; OnPropertyChanged(); RaiseTrimDerived(); }
            }
        }

        private double _playbackPositionSeconds;
        /// <summary>Two-way bound to the scrub slider / media element position.</summary>
        public double PlaybackPositionSeconds
        {
            get => _playbackPositionSeconds;
            set { if (Math.Abs(_playbackPositionSeconds - value) > 0.001) { _playbackPositionSeconds = value; OnPropertyChanged(); } }
        }

        public double TrimmedDurationSeconds =>
            Math.Max(0, (TrimOutSeconds > 0 ? TrimOutSeconds : VideoDurationSeconds) - TrimInSeconds);

        /// <summary>Frame count of the kept range at the target FPS — what actually gets processed.</summary>
        public int TrimmedFrames => Fps > 0 ? (int)Math.Round(TrimmedDurationSeconds * Fps) : 0;

        public bool IsTrimmed =>
            TrimInSeconds > 0.05 || (VideoDurationSeconds > 0 && TrimOutSeconds > 0 && TrimOutSeconds < VideoDurationSeconds - 0.05);

        public string TrimInfo =>
            VideoDurationSeconds <= 0
                ? "Load a video to set in/out"
                : $"In {TrimInSeconds:F1}s · Out {(TrimOutSeconds > 0 ? TrimOutSeconds : VideoDurationSeconds):F1}s  →  {TrimmedDurationSeconds:F1}s / {TrimmedFrames:N0} frames";

        public string EstimatedTime
        {
            get
            {
                if (TrimmedFrames <= 0) return "—";
                var t = TimeSpan.FromSeconds(TrimmedFrames * _secondsPerFrame);
                return t.TotalHours >= 1 ? $"≈ {(int)t.TotalHours}h {t.Minutes:D2}m" : $"≈ {t.Minutes}m {t.Seconds:D2}s";
            }
        }

        private void RaiseTrimDerived()
        {
            OnPropertyChanged(nameof(TrimmedDurationSeconds));
            OnPropertyChanged(nameof(TrimmedFrames));
            OnPropertyChanged(nameof(IsTrimmed));
            OnPropertyChanged(nameof(TrimInfo));
            OnPropertyChanged(nameof(EstimatedTime));
        }

        private void MarkIn() => TrimInSeconds = PlaybackPositionSeconds;
        private void MarkOut() => TrimOutSeconds = PlaybackPositionSeconds;
        private void ResetTrim() { _trimInSeconds = 0; _trimOutSeconds = VideoDurationSeconds; OnPropertyChanged(nameof(TrimInSeconds)); OnPropertyChanged(nameof(TrimOutSeconds)); RaiseTrimDerived(); }

        // Skip / cap (in target-FPS frames) for VHS_LoadVideo, derived from the trim range.
        protected int TrimSkipFrames => Fps > 0 ? (int)Math.Round(TrimInSeconds * Fps) : 0;
        protected int TrimFrameCap => IsTrimmed ? TrimmedFrames : 0; // 0 = load all

        private void RecalibrateEta(int framesProcessed)
        {
            if (framesProcessed > 0 && _genStopwatch.Elapsed.TotalSeconds > 10)
            {
                _secondsPerFrame = _genStopwatch.Elapsed.TotalSeconds / framesProcessed;
                Application.Current?.Dispatcher.Invoke(() => OnPropertyChanged(nameof(EstimatedTime)));
            }
        }

        public WanScailViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));

            SelectCharacterImageCommand = new RelayCommand(SelectCharacterImage);
            SelectVideoCommand = new RelayCommand(SelectVideo);
            GenerateVideoCommand = new RelayCommand(AddAllChunksToQueue, () => CanAddToQueue);
            ProcessSelectedChunkCommand = new RelayCommand(AddSelectedChunkToQueue, () => CanAddToQueue && _chunkItems.Any());
            SelectChunkCommand = new RelayCommand<WanScailChunkItem>(OnChunkSelected);
            RemoveQueueItemCommand = new RelayCommand<WanScailQueueItem>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => _queue.Any());
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            StartQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => HasQueueItems && !IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(async () => await ReprocessAllFailedAsync(), () => HasFailedItems);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);
            AnalyzeImageCommand = new RelayCommand(async () => await AnalyzeImageAsync(), () => CanAnalyzeImage);
            AnalyzeAllChunksCommand = new RelayCommand(async () => await AnalyzeAllChunksAsync(), () => CanAnalyzeAllChunks);
            RandomSeedCommand = new RelayCommand(() => Seed = new Random().NextInt64(0, long.MaxValue));
            MarkInCommand = new RelayCommand(MarkIn);
            MarkOutCommand = new RelayCommand(MarkOut);
            ResetTrimCommand = new RelayCommand(ResetTrim);

            _queue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
                OnCanExecuteChanged();
            };

            AddLog("WAN SCAIL Motion Transfer initialized");
            LoadQueueFromFile();
        }

        #region Commands

        public ICommand SelectCharacterImageCommand { get; }
        public ICommand SelectVideoCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand ProcessSelectedChunkCommand { get; }
        public RelayCommand<WanScailChunkItem> SelectChunkCommand { get; }
        public RelayCommand<WanScailQueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand StartQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }
        public RelayCommand AnalyzeImageCommand { get; }
        public RelayCommand AnalyzeAllChunksCommand { get; }
        public RelayCommand RandomSeedCommand { get; }
        public RelayCommand MarkInCommand { get; }
        public RelayCommand MarkOutCommand { get; }
        public RelayCommand ResetTrimCommand { get; }

        public bool HasFailedItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        #endregion

        #region Input Properties

        public string CharacterImagePath
        {
            get => _characterImagePath;
            set
            {
                if (_characterImagePath != value)
                {
                    _characterImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasCharacterImage));
                    OnPropertyChanged(nameof(CanAddToQueue));
                    OnPropertyChanged(nameof(CanAnalyzeImage));
                    LoadCharacterImagePreview();
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? CharacterImagePreview
        {
            get => _characterImagePreview;
            set { _characterImagePreview = value; OnPropertyChanged(); }
        }

        public string CharacterImageInfo
        {
            get => _characterImageInfo;
            set { if (_characterImageInfo != value) { _characterImageInfo = value; OnPropertyChanged(); } }
        }

        public string InputVideoPath
        {
            get => _inputVideoPath;
            set
            {
                if (_inputVideoPath != value)
                {
                    _inputVideoPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasInputVideo));
                    OnPropertyChanged(nameof(CanAddToQueue));
                    OnPropertyChanged(nameof(CanAnalyzeImage));
                    OnPropertyChanged(nameof(CanAnalyzeAllChunks));
                    LoadVideoInfoAsync();
                    OnCanExecuteChanged();
                }
            }
        }

        public string InputVideoInfo
        {
            get => _inputVideoInfo;
            set { if (_inputVideoInfo != value) { _inputVideoInfo = value; OnPropertyChanged(); } }
        }

        public string Prompt
        {
            get => _prompt;
            set
            {
                if (_prompt != value)
                {
                    _prompt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanAddToQueue));
                    OnCanExecuteChanged();
                }
            }
        }

        public string NegativePrompt
        {
            get => _negativePrompt;
            set { if (_negativePrompt != value) { _negativePrompt = value; OnPropertyChanged(); } }
        }

        public int Fps
        {
            get => _fps;
            set { if (_fps != value) { _fps = value; OnPropertyChanged(); } }
        }

        public int MaxEdge
        {
            get => _maxEdge;
            set { if (_maxEdge != value) { _maxEdge = value; OnPropertyChanged(); } }
        }

        public long Seed
        {
            get => _seed;
            set { if (_seed != value) { _seed = value; OnPropertyChanged(); } }
        }

        public int TotalFrames
        {
            get => _totalFrames;
            set
            {
                if (_totalFrames != value)
                {
                    _totalFrames = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TotalChunks));
                }
            }
        }

        public int TotalChunks => TotalFrames > 0 ? (int)Math.Ceiling((double)TotalFrames / FramesPerChunk) : 0;

        public bool HasCharacterImage => !string.IsNullOrEmpty(CharacterImagePath) && File.Exists(CharacterImagePath);
        public bool HasInputVideo => !string.IsNullOrEmpty(InputVideoPath) && File.Exists(InputVideoPath);

        public bool CanAddToQueue => HasCharacterImage && HasInputVideo;
        public bool CanAnalyzeImage => HasCharacterImage && HasInputVideo && _chunkItems.Any() && !IsAnalyzing && !IsAnalyzingAll && !IsProcessing;
        public bool CanAnalyzeAllChunks => CanAnalyzeImage && !IsProcessingQueue;

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (_isAnalyzing != value)
                {
                    _isAnalyzing = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanAnalyzeImage));
                    OnPropertyChanged(nameof(CanAnalyzeAllChunks));
                    OnCanExecuteChanged();
                }
            }
        }

        public bool IsAnalyzingAll
        {
            get => _isAnalyzingAll;
            private set
            {
                if (_isAnalyzingAll != value)
                {
                    _isAnalyzingAll = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanAnalyzeImage));
                    OnPropertyChanged(nameof(CanAnalyzeAllChunks));
                    OnCanExecuteChanged();
                }
            }
        }

        public string AnalyzeAllChunksStatus { get; private set; } = string.Empty;

        #endregion

        #region Video Editor Properties

        public string? VideoFileUri
        {
            get => _videoFileUri;
            private set { if (_videoFileUri != value) { _videoFileUri = value; OnPropertyChanged(); } }
        }

        public bool HasVideoInfo
        {
            get => _hasVideoInfo;
            private set { if (_hasVideoInfo != value) { _hasVideoInfo = value; OnPropertyChanged(); } }
        }

        public string VideoDuration
        {
            get => _videoDuration;
            private set { if (_videoDuration != value) { _videoDuration = value; OnPropertyChanged(); } }
        }

        public string VideoFpsDisplay
        {
            get => _videoFpsDisplay;
            private set { if (_videoFpsDisplay != value) { _videoFpsDisplay = value; OnPropertyChanged(); } }
        }

        public string VideoFrameCountDisplay
        {
            get => _videoFrameCountDisplay;
            private set { if (_videoFrameCountDisplay != value) { _videoFrameCountDisplay = value; OnPropertyChanged(); } }
        }

        public string VideoChunksDisplay
        {
            get => _videoChunksDisplay;
            private set { if (_videoChunksDisplay != value) { _videoChunksDisplay = value; OnPropertyChanged(); } }
        }

        public string ChunkSelectionInfo
        {
            get => _chunkSelectionInfo;
            private set { if (_chunkSelectionInfo != value) { _chunkSelectionInfo = value; OnPropertyChanged(); } }
        }

        public int SelectedChunkIndex
        {
            get => _selectedChunkIndex;
            private set { if (_selectedChunkIndex != value) { _selectedChunkIndex = value; OnPropertyChanged(); } }
        }

        public ObservableCollection<WanScailChunkItem> ChunkItems => _chunkItems;

        #endregion

        #region Queue Properties

        public ObservableCollection<WanScailQueueItem> Queue => _queue;

        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            private set
            {
                if (_isProcessingQueue != value)
                {
                    _isProcessingQueue = value;
                    OnPropertyChanged();
                    OnCanExecuteChanged();
                }
            }
        }

        public string QueueStatus
        {
            get => _queueStatus;
            private set { if (_queueStatus != value) { _queueStatus = value; OnPropertyChanged(); } }
        }

        public bool HasQueueItems => _queue.Any();

        #endregion

        #region File Selection

        private async void SelectCharacterImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Character Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDirectory,
                persistKey: "wanscail.image");

            if (filePath != null)
            {
                CharacterImagePath = filePath;
                AddLog($"WAN SCAIL: Selected character image: {Path.GetFileName(filePath)}");
            }
        }

        private async void SelectVideo()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Video",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.webm|All Files|*.*",
                initialDirectory,
                persistKey: "wanscail.video");

            if (filePath != null)
            {
                InputVideoPath = filePath;
                AddLog($"WAN SCAIL: Selected video: {Path.GetFileName(filePath)}");
            }
        }

        #endregion

        #region Video Info + Chunk Timeline

        private async void LoadVideoInfoAsync()
        {
            // Clear state when no video
            if (string.IsNullOrEmpty(InputVideoPath) || !File.Exists(InputVideoPath))
            {
                InputVideoInfo = string.Empty;
                VideoFileUri = null;
                HasVideoInfo = false;
                TotalFrames = 0;
                _chunkItems.Clear();
                ChunkSelectionInfo = "Load a video to see chunks";
                return;
            }

            var fi = new FileInfo(InputVideoPath);
            InputVideoInfo = $"{fi.Name} • {fi.Length / 1024 / 1024:F1}MB";
            VideoFileUri = InputVideoPath;

            ChunkSelectionInfo = "Analyzing video…";
            HasVideoInfo = false;

            var path = InputVideoPath; // capture before await
            // Use duration × target FPS so chunk boundaries match what the workflow sees after force_rate
            var duration = await Task.Run(() => GetVideoDuration(path));
            var frameCount = duration > 0 && Fps > 0
                ? (int)Math.Floor(duration * Fps)
                : await Task.Run(() => GetVideoFrameCount(path));

            // Guard: if video changed while we were analyzing, discard result
            if (path != InputVideoPath) return;

            TotalFrames = frameCount > 0 ? frameCount : 0;

            var estimatedDuration = duration > 0 ? duration : (Fps > 0 && TotalFrames > 0 ? (double)TotalFrames / Fps : 0);
            // Seed the trim range to the whole clip so ETA/scrub work immediately.
            VideoDurationSeconds = estimatedDuration;
            _trimInSeconds = 0;
            _trimOutSeconds = estimatedDuration;
            OnPropertyChanged(nameof(TrimInSeconds));
            OnPropertyChanged(nameof(TrimOutSeconds));
            RaiseTrimDerived();
            VideoDuration = estimatedDuration > 0 ? $"{estimatedDuration:F1}s" : "—";
            VideoFpsDisplay = Fps.ToString();
            VideoFrameCountDisplay = TotalFrames > 0 ? TotalFrames.ToString("N0") : "—";
            VideoChunksDisplay = TotalChunks > 0 ? $"{TotalChunks} chunk{(TotalChunks != 1 ? "s" : "")}" : "—";
            HasVideoInfo = TotalFrames > 0;

            BuildChunkTimeline();
            OnPropertyChanged(nameof(CanAddToQueue));
            OnCanExecuteChanged();

            AddLog($"Video analyzed: {TotalFrames} frames → {TotalChunks} chunks of {FramesPerChunk}");
        }

        private void BuildChunkTimeline()
        {
            _chunkItems.Clear();
            _chunkPrompts.Clear();
            if (TotalChunks <= 0)
            {
                ChunkSelectionInfo = "No frames detected — ffmpeg may not be installed";
                OnPropertyChanged(nameof(CanAnalyzeAllChunks));
                OnCanExecuteChanged();
                return;
            }

            // Load cached prompts for this video from SQLite
            var cached = _promptCache.GetAllPrompts(InputVideoPath);
            foreach (var (k, v) in cached)
                _chunkPrompts[k] = v;
            if (cached.Count > 0)
                AddLog($"Loaded {cached.Count} cached chunk prompt(s) from database");

            for (int i = 0; i < TotalChunks; i++)
            {
                var start = i * FramesPerChunk;
                var end = Math.Min(start + FramesPerChunk - 1, TotalFrames - 1);
                _chunkItems.Add(new WanScailChunkItem
                {
                    Index = i,
                    StartFrame = start,
                    EndFrame = end,
                    IsSelected = i == 0,
                    Status = WanScailChunkStatus.Idle,
                    HasCachedPrompt = cached.ContainsKey(i),
                });
            }

            _selectedChunkIndex = 0;
            UpdateChunkSelectionInfo();
            OnPropertyChanged(nameof(CanAnalyzeImage));
            OnPropertyChanged(nameof(CanAnalyzeAllChunks));
            OnCanExecuteChanged();
        }

        private void OnChunkSelected(WanScailChunkItem? chunk)
        {
            if (chunk == null) return;
            var index = chunk.Index;

            foreach (var c in _chunkItems)
                c.IsSelected = c.Index == index;

            _selectedChunkIndex = index;
            UpdateChunkSelectionInfo();

            // Load this chunk's cached prompt if available
            if (_chunkPrompts.TryGetValue(index, out var cachedPrompt) && !string.IsNullOrWhiteSpace(cachedPrompt))
                Prompt = cachedPrompt;

            // Seek video to this chunk's start frame
            if (Fps > 0 && TotalFrames > 0)
            {
                var seekTime = TimeSpan.FromSeconds((double)(index * FramesPerChunk) / Fps);
                SeekRequested?.Invoke(this, seekTime);
            }
        }

        private void UpdateChunkSelectionInfo()
        {
            if (_chunkItems.Count == 0)
            {
                ChunkSelectionInfo = "Upload a video to see chunks";
                return;
            }
            var idx = _selectedChunkIndex;
            var sf = idx * FramesPerChunk;
            var ef = Math.Min(sf + FramesPerChunk - 1, Math.Max(0, TotalFrames - 1));
            var t = Fps > 0 ? (sf / (double)Fps).ToString("F2") : "—";
            ChunkSelectionInfo = $"Chunk {idx + 1} of {_chunkItems.Count}  ·  frames {sf}–{ef}  ·  t={t}s";
        }

        private void SetChunkStatus(int index, WanScailChunkStatus status)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var chunk = _chunkItems.FirstOrDefault(c => c.Index == index);
                if (chunk != null)
                {
                    chunk.Status = status;
                    // Keep IsSelected for the currently selected chunk
                    chunk.IsSelected = chunk.Index == _selectedChunkIndex;
                }
            });
        }

        #endregion

        #region Image Analysis

        private async Task AnalyzeImageAsync()
        {
            if (!CanAnalyzeImage) return;

            if (_chunkPrompts.Count > 0)
            {
                var answer = MessageBox.Show(
                    $"{_chunkPrompts.Count} of {_chunkItems.Count} chunk(s) already have prompts.\n\nOverwrite all with fresh analysis?",
                    "Overwrite Cached Prompts?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
            }

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;

            try
            {
                var models = await _lmStudioService.GetAvailableModelsAsync(token);
                var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(selectedModel) && models.Count > 0)
                    selectedModel = models.First().Name.Length > 0 ? models.First().Name : models.First().Id;

                if (string.IsNullOrEmpty(selectedModel))
                {
                    AddLog("ERROR: No LM Studio model available");
                    MessageBox.Show("No LM Studio model available. Please ensure LM Studio is running and a model is loaded.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AddLog($"=== WAN SCAIL analysis started (model: {selectedModel}) ===");

                // Step 1: Extract character appearance from the reference image (once, reused for every chunk)
                const string appearanceSystemPrompt =
                    "Describe only what the person is wearing and their physical appearance in this image. " +
                    "Include clothing (style and colors), hairstyle, and body type. " +
                    "Do not describe what they are doing, their pose, their expression, or any action. " +
                    "Write one or two sentences. Output only the description as plain text — no labels, no headers, no markdown.";

                AddLog("Step 1: Extracting character appearance from reference image…");
                var appearanceRaw = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel,
                    CharacterImagePath,
                    "Describe this character's appearance.",
                    appearanceSystemPrompt,
                    maxTokens: 2000,
                    cancellationToken: token);

                var appearanceDescription = CleanLLMOutput(appearanceRaw);
                if (string.IsNullOrWhiteSpace(appearanceDescription))
                {
                    AddLog("ERROR: Could not extract character appearance from reference image");
                    return;
                }
                var apPreview = appearanceDescription.Length > 120 ? appearanceDescription.Substring(0, 120) + "…" : appearanceDescription;
                AddLog($"Appearance: {apPreview}");

                // Step 2: For each chunk, extract motion from frames and combine with appearance
                const string motionSystemPrompt =
                    "Describe only the body movement and actions visible in these sequential video frames. " +
                    "Focus on what the person is doing: their poses, gestures, and direction of motion from frame to frame. " +
                    "Do not mention clothing, hair, skin color, or any aspect of appearance. " +
                    "Write one or two sentences. Output only the movement description as plain text — no labels, no headers, no markdown.";

                int totalChunks = _chunkItems.Count;
                int done = 0;

                for (int i = 0; i < totalChunks; i++)
                {
                    if (token.IsCancellationRequested) break;

                    AnalyzeAllChunksStatus = $"Analyzing chunk {i + 1}/{totalChunks}…";
                    OnPropertyChanged(nameof(AnalyzeAllChunksStatus));
                    AddLog($"Step 2 — Chunk {i + 1}/{totalChunks}: extracting motion…");

                    List<string> framePaths = new();
                    try
                    {
                        framePaths = await ExtractChunkFramesAsync(InputVideoPath, i, token);
                        AddLog($"Chunk {i + 1}: extracted {framePaths.Count} frame(s)");
                        if (framePaths.Count == 0)
                        {
                            AddLog($"Chunk {i + 1}: no frames extracted, skipping");
                            continue;
                        }

                        var motionRaw = await _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                            selectedModel,
                            framePaths,
                            "Describe the movement in these video frames.",
                            motionSystemPrompt,
                            maxTokens: 2000,
                            cancellationToken: token);

                        var motionDescription = CleanLLMOutput(motionRaw);
                        if (string.IsNullOrWhiteSpace(motionDescription))
                        {
                            AddLog($"Chunk {i + 1}: motion description was empty, skipping");
                            continue;
                        }

                        // Combine: character appearance (constant) + motion (per chunk)
                        var combinedPrompt = $"{appearanceDescription}, {motionDescription}";

                        _chunkPrompts[i] = combinedPrompt;
                        _promptCache.SavePrompt(InputVideoPath, i, combinedPrompt);
                        done++;

                        var chunkIdx = i; // capture for lambda
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var tile = _chunkItems.FirstOrDefault(c => c.Index == chunkIdx);
                            if (tile != null) tile.HasCachedPrompt = true;
                            if (chunkIdx == _selectedChunkIndex)
                                Prompt = combinedPrompt;
                        });

                        var motPreview = motionDescription.Length > 100 ? motionDescription.Substring(0, 100) + "…" : motionDescription;
                        AddLog($"Chunk {i + 1} motion: {motPreview}");
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        AddLog($"Chunk {i + 1} failed: {ex.Message}");
                    }
                    finally
                    {
                        foreach (var f in framePaths)
                            try { File.Delete(f); } catch { }
                    }
                }

                AnalyzeAllChunksStatus = token.IsCancellationRequested
                    ? $"Cancelled — {done}/{totalChunks} chunks analyzed"
                    : $"Done — {done}/{totalChunks} chunks analyzed";
                OnPropertyChanged(nameof(AnalyzeAllChunksStatus));
                AddLog($"=== WAN SCAIL analysis complete: {done}/{totalChunks} chunks ===");
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

        #endregion

        #region Analyze All Chunks

        private async Task AnalyzeAllChunksAsync() => await AnalyzeImageAsync();

        private async Task<List<string>> ExtractChunkFramesAsync(string videoPath, int chunkIndex, CancellationToken token)
        {
            var frames = new List<string>();
            var ffmpegPath = FindFFmpeg();
            if (ffmpegPath == null) return frames;

            var fps = Fps > 0 ? (double)Fps : 24.0;
            var startFrame = chunkIndex * FramesPerChunk;
            var endFrame = Math.Min(startFrame + FramesPerChunk - 1, TotalFrames - 1);
            int numFrames = Math.Min(4, endFrame - startFrame + 1);

            for (int f = 0; f < numFrames; f++)
            {
                if (token.IsCancellationRequested) break;

                var frameIdx = numFrames == 1
                    ? startFrame
                    : startFrame + (int)Math.Round((double)(endFrame - startFrame) * f / (numFrames - 1));
                var timeSec = frameIdx / fps;
                var tempFile = Path.Combine(Path.GetTempPath(), $"wanscail_frame_{Guid.NewGuid():N}.jpg");

                await Task.Run(() =>
                {
                    var si = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        // -loglevel quiet suppresses all stderr output; -nostats removes progress lines.
                        // Not redirecting stderr avoids the pipe-buffer deadlock where ffmpeg blocks
                        // waiting for the consumer to drain the pipe before our WaitForExit returns.
                        Arguments = $"-loglevel quiet -nostats -ss {timeSec:F3} -i \"{videoPath}\" " +
                                    $"-frames:v 1 -q:v 3 " +
                                    $"-vf \"scale=512:512:force_original_aspect_ratio=decrease\" \"{tempFile}\" -y",
                        UseShellExecute = false,
                        RedirectStandardError = false,
                        CreateNoWindow = true,
                    };
                    using var proc = Process.Start(si);
                    if (proc != null && !proc.WaitForExit(30000))
                        try { proc.Kill(); } catch { }
                }, token);

                if (File.Exists(tempFile))
                    frames.Add(tempFile);
            }

            return frames;
        }

        #endregion

        #region Preview Loading

        private void LoadCharacterImagePreview()
        {
            if (string.IsNullOrEmpty(CharacterImagePath) || !File.Exists(CharacterImagePath))
            {
                CharacterImagePreview = null;
                CharacterImageInfo = string.Empty;
                return;
            }
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(CharacterImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                CharacterImagePreview = bitmap;
                var fi = new FileInfo(CharacterImagePath);
                CharacterImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fi.Length / 1024}KB";
                AddLog($"Character image loaded: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
            }
            catch (Exception ex)
            {
                AddLog($"Error loading character image preview: {ex.Message}");
                CharacterImageInfo = "Error loading image";
            }
        }

        #endregion

        #region Queue Management

        private void AddAllChunksToQueue()
        {
            if (!CanAddToQueue) return;
            EnqueueItem(singleChunkIndex: null);
        }

        private void AddSelectedChunkToQueue()
        {
            if (!CanAddToQueue || !_chunkItems.Any()) return;
            EnqueueItem(singleChunkIndex: _selectedChunkIndex);
        }

        private void EnqueueItem(int? singleChunkIndex)
        {
            var effectivePrompt = string.IsNullOrWhiteSpace(Prompt)
                ? "character motion transfer, smooth movement, high quality"
                : Prompt;
            var item = new WanScailQueueItem
            {
                CharacterImagePath = CharacterImagePath,
                InputVideoPath = InputVideoPath,
                Prompt = effectivePrompt,
                NegativePrompt = NegativePrompt,
                Fps = Fps,
                MaxEdge = MaxEdge,
                Seed = Seed,
                SingleChunkIndex = singleChunkIndex,
                ItemStatus = QueueItemStatus.Pending
            };

            // Let subclasses stamp workflow-specific fields (e.g. SCAIL II subject / replacement mode)
            OnEnqueue(item);

            _queue.Add(item);
            SaveQueueToFile();
            var desc = singleChunkIndex.HasValue ? $"chunk {singleChunkIndex.Value + 1}" : "all chunks";
            AddLog($"Added to WAN SCAIL queue ({desc}): {item.DisplayText}");
            UpdateQueueStatus();

            if (!IsProcessingQueue)
                _ = ProcessQueueAsync();
        }

        /// <summary>
        /// Hook for subclasses to stamp workflow-specific fields onto a freshly built
        /// queue item before it is enqueued. Base implementation does nothing.
        /// </summary>
        protected virtual void OnEnqueue(WanScailQueueItem item) { }

        private void RemoveQueueItem(WanScailQueueItem? item)
        {
            if (item != null && item.ItemStatus != QueueItemStatus.Processing)
            {
                _queue.Remove(item);
                UpdateQueueStatus();
            }
        }

        private void UpdateQueueStatus()
        {
            var pending = _queue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
            var completed = _queue.Count(x => x.ItemStatus == QueueItemStatus.Completed);
            var failed = _queue.Count(x => x.ItemStatus == QueueItemStatus.Failed);
            var total = _queue.Count;

            QueueStatus = total == 0
                ? string.Empty
                : $"{pending} pending • {completed} done • {failed} failed";

            OnPropertyChanged(nameof(HasFailedItems));
            OnCanExecuteChanged();
        }

        private void ClearQueue()
        {
            _queueCts?.Cancel();
            foreach (var item in _queue.ToList())
                _queue.Remove(item);
            SaveQueueToFile();
            UpdateQueueStatus();
            AddLog("WAN SCAIL queue cleared");
        }

        private void StopQueue()
        {
            _queueCts?.Cancel();
            AddLog("WAN SCAIL queue stop requested");
        }

        private async Task ReprocessAllFailedAsync()
        {
            var failed = _queue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (!failed.Any()) return;

            foreach (var item in failed)
                item.ItemStatus = QueueItemStatus.Pending;

            UpdateQueueStatus();
            SaveQueueToFile();
            AddLog($"Reprocessing {failed.Count} failed WAN SCAIL item(s)...");

            if (!IsProcessingQueue)
                await ProcessQueueAsync();
        }

        private async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;

            IsProcessingQueue = true;
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;
            AddLog("Waiting for other workflows to finish...");
            OnCanExecuteChanged();

            WorkflowQueueCoordinator.WorkflowLease lease;
            try
            {
                lease = await _workflowCoordinator.AcquireAsync("WanScail", token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue processing cancelled while waiting");
                IsProcessingQueue = false;
                OnCanExecuteChanged();
                return;
            }

            AddLog("Starting WAN SCAIL queue processing...");
            using (lease)
            try
            {
                WanScailQueueItem? item;
                while (!token.IsCancellationRequested &&
                       (item = _queue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                {
                    item.ItemStatus = QueueItemStatus.Processing;
                    UpdateQueueStatus();
                    SaveQueueToFile();
                    try
                    {
                        await GenerateSingleVideoAsync(item);
                        item.ItemStatus = QueueItemStatus.Completed;
                        AddLog($"Queue item completed: {item.DisplayText}");
                    }
                    catch (OperationCanceledException)
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
                        AddLog("WAN SCAIL queue item cancelled — reset to Pending");
                        break;
                    }
                    catch (Exception ex)
                    {
                        var shouldRetry = await TryHandleCrashAndRetryAsync(item, ex);
                        if (shouldRetry)
                        {
                            item.ItemStatus = QueueItemStatus.Pending;
                            AddLog("Item reset to Pending — will retry after ComfyUI restart");
                        }
                        else
                        {
                            item.ItemStatus = QueueItemStatus.Failed;
                            item.ErrorMessage = ex.Message;
                            AddLog($"Queue item FAILED: {ex.Message}");
                        }
                    }
                    UpdateQueueStatus();
                    SaveQueueToFile();
                }
            }
            finally
            {
                IsProcessingQueue = false;
                AddLog("WAN SCAIL queue processing finished.");
                OnCanExecuteChanged();
            }
        }

        #endregion

        #region Queue Persistence

        private void SaveQueueToFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(QueueFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(QueueFilePath,
                    JsonSerializer.Serialize(_queue.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AddLog($"Error saving queue: {ex.Message}"); }
        }

        private void LoadQueueFromFile()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;
                var items = JsonSerializer.Deserialize<List<WanScailQueueItem>>(File.ReadAllText(QueueFilePath));
                if (items?.Any() != true) return;
                _queue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Processing)
                        item.ItemStatus = QueueItemStatus.Pending;
                    _queue.Add(item);
                }
                UpdateQueueStatus();
                AddLog($"WAN SCAIL queue loaded: {_queue.Count} items");

                var pending = _queue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
                if (pending > 0 && !IsProcessingQueue)
                {
                    AddLog($"Auto-resuming queue: {pending} pending item(s) from previous session");
                    _ = ProcessQueueAsync();
                }
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        #endregion

        #region Video Generation

        private async Task GenerateSingleVideoAsync(WanScailQueueItem item)
        {
            bool success = false;
            StartGenerationTimer();
            try
            {
                AddLog($"=== Starting WAN SCAIL generation: {item.DisplayText} ===");
                IsProcessing = true;

                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing WAN SCAIL workflow...";

                AddLog($"Character image: {Path.GetFileName(item.CharacterImagePath)}");
                AddLog($"Input video: {Path.GetFileName(item.InputVideoPath)}");
                AddLog($"Prompt: {item.Prompt}");
                AddLog($"Settings: FPS={item.Fps}, MaxEdge={item.MaxEdge}, Seed={item.Seed}");

                ProcessingStatus = "Analysing input video...";
                // Use already-computed TotalFrames if available; otherwise re-analyze
                var videoFrameCount = TotalFrames > 0 ? TotalFrames : GetVideoFrameCount(item.InputVideoPath);
                if (videoFrameCount <= 0)
                {
                    AddLog("WARNING: Could not determine frame count; defaulting to 1 chunk");
                    videoFrameCount = FramesPerChunk;
                }
                TotalFrames = videoFrameCount;
                AddLog($"Total frames: {TotalFrames} → {TotalChunks} chunk(s) of {FramesPerChunk}");

                // Determine chunk range (single chunk or all chunks)
                int startChunk = item.SingleChunkIndex ?? 0;
                int endChunk = item.SingleChunkIndex.HasValue ? item.SingleChunkIndex.Value + 1 : TotalChunks;
                AddLog($"Processing chunks {startChunk + 1}–{endChunk} of {TotalChunks}");

                ProcessingStatus = "Checking ComfyUI status...";
                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddLog($"[Auto-Restart] {status}"));

                if (!comfyUIOk)
                {
                    AddLog("ERROR: ComfyUI is not running");
                    MessageBox.Show(
                        "ComfyUI is not running. Please start ComfyUI manually or configure auto-restart in settings.",
                        "ComfyUI Not Running", MessageBoxButton.OK, MessageBoxImage.Warning);
                    throw new Exception("ComfyUI is not running.");
                }

                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                    AddLog("Connected to ComfyUI");
                }

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", WorkflowFileName);
                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    throw new FileNotFoundException($"WAN SCAIL workflow file not found: {workflowPath}");
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                ProcessingStatus = "Uploading assets to ComfyUI...";
                ProcessingProgress = 10;

                // Get video dimensions — used for both cropping and output resolution
                var (videoW, videoH) = GetVideoDimensions(item.InputVideoPath);

                int outputWidth = 0, outputHeight = 0;
                if (videoW > 0 && videoH > 0)
                {
                    (outputWidth, outputHeight) = ComputeOutputResolution(videoW, videoH, item.MaxEdge);
                    AddLog($"Output resolution: {outputWidth}x{outputHeight}");
                }

                // Crop character image to match output aspect ratio before uploading
                string imageToUpload = item.CharacterImagePath;
                string? croppedImageTemp = null;
                if (outputWidth > 0 && outputHeight > 0)
                {
                    croppedImageTemp = CropImageToAspectRatio(item.CharacterImagePath, outputWidth, outputHeight);
                    if (croppedImageTemp != null)
                    {
                        imageToUpload = croppedImageTemp;
                        AddLog($"Character image cropped to {outputWidth}:{outputHeight} aspect ratio");
                    }
                }

                AddLog("Uploading character image...");
                var uploadedImageName = await _comfyUIService.UploadImageAsync(imageToUpload);
                if (!string.IsNullOrEmpty(croppedImageTemp))
                    try { File.Delete(croppedImageTemp); } catch { }
                if (string.IsNullOrEmpty(uploadedImageName))
                    throw new Exception("Failed to upload character image to ComfyUI.");
                AddLog($"Character image uploaded: {uploadedImageName}");

                AddLog("Uploading reference video...");
                var uploadedVideoName = await _comfyUIService.UploadVideoAsync(item.InputVideoPath);
                if (string.IsNullOrEmpty(uploadedVideoName))
                    throw new Exception("Failed to upload video to ComfyUI.");
                AddLog($"Video uploaded: {uploadedVideoName}");

                var numChunks = endChunk - startChunk;
                var chunkFiles = new List<string>();
                AddLog($"=== Processing {numChunks} chunk(s) of {FramesPerChunk} frames ===");

                // Per-run seed: if -1, generate random once for the whole job
                var runSeed = item.Seed >= 0 ? item.Seed : (long)(new Random().NextDouble() * long.MaxValue);

                for (int chunkIndex = startChunk; chunkIndex < endChunk; chunkIndex++)
                {
                    // Mark chunk as processing in UI
                    SetChunkStatus(chunkIndex, WanScailChunkStatus.Processing);

                    try
                    {
                        var startFrame = chunkIndex * FramesPerChunk;
                        var framesInChunk = Math.Min(FramesPerChunk, TotalFrames - startFrame);
                        var relativeChunk = chunkIndex - startChunk + 1;

                        AddLog($"=== Chunk {chunkIndex + 1} (job {relativeChunk}/{numChunks}): frames {startFrame}–{startFrame + framesInChunk - 1} ===");
                        ProcessingStatus = $"Processing chunk {chunkIndex + 1} ({relativeChunk}/{numChunks})";
                        var baseProgress = 20.0 + (relativeChunk - 1) * 60.0 / numChunks;

                        if (chunkIndex > startChunk && !_comfyUIService.IsConnected)
                        {
                            AddLog("Reconnecting to ComfyUI...");
                            await _comfyUIService.ConnectAsync();
                        }

                        // Use per-chunk cached prompt if available, fall back to queue-item prompt
                        var chunkPrompt = _chunkPrompts.TryGetValue(chunkIndex, out var cp) && !string.IsNullOrWhiteSpace(cp)
                            ? cp : item.Prompt;
                        if (_chunkPrompts.ContainsKey(chunkIndex))
                            AddLog($"Chunk {chunkIndex + 1}: using cached per-chunk prompt");

                        var updatedWorkflow = UpdateWorkflowParameters(
                            workflow,
                            uploadedImageName,
                            uploadedVideoName,
                            startFrame,
                            framesInChunk,
                            chunkPrompt,
                            item.NegativePrompt,
                            item.Fps,
                            item.MaxEdge,
                            runSeed + chunkIndex,
                            outputWidth,
                            outputHeight,
                            item);

                        var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                        {
                            if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                            {
                                var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    ProcessingProgress = baseProgress + percent * 0.6 / numChunks;
                                    ProcessingStatus = $"Chunk {chunkIndex + 1} ({relativeChunk}/{numChunks}): {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                                });
                            }
                        });

                        var existingFiles = GetExistingVideoFiles("*.mp4", OutputSubfolder);
                        var promptId = await _comfyUIService.ExecuteWorkflowAsync(
                            updatedWorkflow, progress, executionTimeout: ExecutionTimeout);
                        AddLog($"Chunk {chunkIndex + 1} submitted, prompt ID: {promptId}");

                        var outputVideo = await TryGetVideoFromHistoryAsync(promptId);

                        if (outputVideo == null)
                        {
                            AddLog("History API returned no result, falling back to filesystem polling...");
                            outputVideo = await WaitForNewVideoAsync(
                                existingFiles, "*.mp4",
                                ExecutionTimeout,
                                TimeSpan.FromSeconds(5),
                                OutputSubfolder);
                        }

                        if (outputVideo != null && File.Exists(outputVideo))
                        {
                            var chunkFile = Path.Combine(Path.GetTempPath(), $"wanscail_chunk_{chunkIndex:D3}_{Path.GetFileName(outputVideo)}");
                            File.Copy(outputVideo, chunkFile, true);
                            chunkFiles.Add(chunkFile);
                            SetChunkStatus(chunkIndex, WanScailChunkStatus.Done);
                            AddLog($"Chunk {chunkIndex + 1} complete: {Path.GetFileName(chunkFile)}");
                        }
                        else
                        {
                            SetChunkStatus(chunkIndex, WanScailChunkStatus.Failed);
                            AddLog($"ERROR: No output video for chunk {chunkIndex + 1} — aborting remaining chunks");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        SetChunkStatus(chunkIndex, WanScailChunkStatus.Failed);
                        AddLog($"ERROR processing chunk {chunkIndex + 1}: {ex.Message} — aborting remaining chunks");
                        break;
                    }
                }

                ProcessingProgress = 85;
                ProcessingStatus = "Merging video chunks...";
                AddLog("=== Merging chunks ===");

                if (chunkFiles.Count > 0)
                {
                    var outputDir = Path.Combine(
                        _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(),
                        "WanScail");
                    Directory.CreateDirectory(outputDir);

                    var finalPath = Path.Combine(outputDir, $"WanScail_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                    if (chunkFiles.Count == 1)
                    {
                        File.Copy(chunkFiles[0], finalPath, true);
                        AddLog($"Single chunk copied to: {finalPath}");
                    }
                    else
                    {
                        MergeVideoChunksWithFFmpeg(chunkFiles, finalPath);
                    }

                    foreach (var f in chunkFiles)
                        try { File.Delete(f); } catch { }

                    item.OutputVideoPath = finalPath;
                    ResultVideoPath = finalPath;
                    await LocalCopyService.CopyVideoAsync(finalPath);
                    HasResult = true;

                    var fi = new FileInfo(finalPath);
                    ResultVideoInfo = $"WAN SCAIL Video • {fi.Length / 1024 / 1024:F1}MB";
                    ProcessingProgress = 100;
                    ProcessingStatus = "WAN SCAIL Complete!";
                    success = true;
                    RecalibrateEta(TrimmedFrames > 0 ? TrimmedFrames : TotalFrames);
                    AddLog($"=== WAN SCAIL generation complete: {finalPath} ===");
                }
                else
                {
                    AddLog("ERROR: No video chunks were generated");
                    ProcessingStatus = "No output generated";
                    throw new Exception("No video chunks were generated.");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                AddLog($"Stack trace: {ex.StackTrace}");
                ProcessingStatus = "Error occurred";
                throw;
            }
            finally
            {
                StopGenerationTimer(success);
                IsProcessing = false;
            }
        }

        // Subclasses override this to fix a specific output resolution (e.g. GGUF always uses 832×480).
        protected virtual (int Width, int Height) ComputeOutputResolution(int videoW, int videoH, int maxEdge)
        {
            // SCAIL 480p: short edge = 480, long edge scaled from video AR, both divisible by 32
            const int shortEdge = 480;
            const int alignment = 32;
            if (videoW <= videoH)
                return (shortEdge, (int)(Math.Round((double)shortEdge * videoH / videoW / alignment) * alignment));
            else
                return ((int)(Math.Round((double)shortEdge * videoW / videoH / alignment) * alignment), shortEdge);
        }

        protected virtual JsonElement UpdateWorkflowParameters(
            JsonElement workflow,
            string characterImageName,
            string videoName,
            int startFrame,
            int framesInChunk,
            string prompt,
            string negativePrompt,
            int fps,
            int maxEdge,
            long seed,
            int outputWidth = 0,
            int outputHeight = 0,
            WanScailQueueItem? item = null)
        {
            var workflowJson = workflow.GetRawText();
            AddLog($"Updating workflow: start={startFrame}, frames={framesInChunk}, fps={fps}, maxEdge={maxEdge}");

            // Node 52: Character reference image (LoadImage)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "52", "image", characterImageName);

            // Node 65: Input video — video name, skip_first_frames, frame_load_cap
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "65", new Dictionary<string, object>
            {
                { "video", videoName },
                { "skip_first_frames", startFrame },
                { "frame_load_cap", framesInChunk }
            });

            // Node 112: Positive prompt
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "112", "prompt", prompt);

            // Node 55: Negative prompt
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "55", "negative_prompt", negativePrompt);

            // Node 135: Frame rate
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "135", "Number", fps.ToString());

            // Node 144: Output resolution (max edge)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "144", "Number", maxEdge.ToString());

            // Node 152: Seed
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "152", "value", seed);

            AddLog("✓ WAN SCAIL workflow nodes updated");
            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        private void MergeVideoChunksWithFFmpeg(List<string> chunkFiles, string outputPath)
        {
            var ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                AddLog("ERROR: ffmpeg not found. Cannot merge video chunks.");
                throw new InvalidOperationException("ffmpeg is required to merge video chunks.");
            }

            var listFile = Path.Combine(Path.GetTempPath(), $"ffmpeg_wanscail_{Guid.NewGuid()}.txt");
            using (var writer = new StreamWriter(listFile))
            {
                foreach (var f in chunkFiles)
                    writer.WriteLine($"file '{f.Replace("\\", "/")}'");
            }

            AddLog($"Merging {chunkFiles.Count} chunks with ffmpeg...");

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-f concat -safe 0 -i \"{listFile}\" -c copy \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException("Failed to start ffmpeg.");
            process.WaitForExit(120000);
            try { File.Delete(listFile); } catch { }

            if (!File.Exists(outputPath))
                throw new InvalidOperationException($"ffmpeg merge failed. Output not found: {outputPath}");

            AddLog($"Merge complete: {Path.GetFileName(outputPath)}");
        }

        #endregion

        protected static string CleanLLMOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = text.Replace("**", "");
            var trimmed = text.TrimStart();
            var lower = trimmed.ToLowerInvariant();
            if (lower.StartsWith("prompt:") || lower.StartsWith("prompt :"))
                trimmed = trimmed.Substring(trimmed.IndexOf(':') + 1);
            return trimmed.Trim();
        }

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            GenerateVideoCommand.NotifyCanExecuteChanged();
            ProcessSelectedChunkCommand.NotifyCanExecuteChanged();
            RemoveQueueItemCommand.NotifyCanExecuteChanged();
            ClearQueueCommand.NotifyCanExecuteChanged();
            StopQueueCommand.NotifyCanExecuteChanged();
            StartQueueCommand.NotifyCanExecuteChanged();
            ReprocessAllFailedCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            SendToEditCameraCommand.NotifyCanExecuteChanged();
            AnalyzeImageCommand.NotifyCanExecuteChanged();
            AnalyzeAllChunksCommand.NotifyCanExecuteChanged();
        }
    }
}
