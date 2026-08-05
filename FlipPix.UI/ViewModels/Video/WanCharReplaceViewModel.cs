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
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    public partial class WanCharReplaceViewModel : VideoProcessingBaseViewModel
    {
        private const int FramesPerChunk = 77;
        private const string WorkflowFileName = "video/wan/wan22_animate_preprocess_MDMZ_071025charreplacement.json";
        private const string OutputSubfolder = "video/wan_char_replace";
        private const string TmpOutputSubfolder = "video/wan_char_replace_tmp";

        private string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "wan_char_replace_queue.json");

        // ── Input fields ──────────────────────────────────────────────────────
        private string _characterImagePath = string.Empty;
        private BitmapImage? _characterImagePreview;
        private string _characterImageInfo = string.Empty;

        private string _inputVideoPath = string.Empty;
        private string _inputVideoInfo = string.Empty;

        private string _prompt = string.Empty;
        private string _negativePrompt = "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";
        private int _fps = 16;
        private int _steps = 4;
        private long _seed = -1;
        private bool _useRtxUpscale = true;
        private int _totalFrames;
        private bool _isAnalyzing;
        private bool _isProcessingQueue;
        private string _queueStatus = string.Empty;
        private bool _isAnalyzingAll;

        // ── Video editor / chunk timeline ─────────────────────────────────────
        private double _nativeFps;
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
        private readonly ObservableCollection<WanCharReplaceQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;
        private CancellationTokenSource? _analyzeCts;

        public event EventHandler<TimeSpan>? SeekRequested;

        public WanCharReplaceViewModel(
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
            RemoveQueueItemCommand = new RelayCommand<WanCharReplaceQueueItem>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => _queue.Any());
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            StartQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => HasQueueItems && !IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(async () => await ReprocessAllFailedAsync(), () => HasFailedItems);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);
            AnalyzeImageCommand = new RelayCommand(async () => await AnalyzeImageAsync(), () => CanAnalyzeImage);
            AnalyzeAllChunksCommand = new RelayCommand(async () => await AnalyzeImageAsync(), () => CanAnalyzeAllChunks);
            RandomSeedCommand = new RelayCommand(() => Seed = new Random().NextInt64(0, long.MaxValue));
            ToggleRtxUpscaleCommand = new RelayCommand(() => UseRtxUpscale = !UseRtxUpscale);

            _queue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
                OnCanExecuteChanged();
            };

            AddLog("WAN Character Replace initialized");
            LoadQueueFromFile();
        }

        #region Commands

        public ICommand SelectCharacterImageCommand { get; }
        public ICommand SelectVideoCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand ProcessSelectedChunkCommand { get; }
        public RelayCommand<WanScailChunkItem> SelectChunkCommand { get; }
        public RelayCommand<WanCharReplaceQueueItem> RemoveQueueItemCommand { get; }
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
        public RelayCommand ToggleRtxUpscaleCommand { get; }

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
                    TryReconstructPromptsFromCache();
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

        public int Steps
        {
            get => _steps;
            set { if (_steps != value) { _steps = Math.Max(1, value); OnPropertyChanged(); } }
        }

        public long Seed
        {
            get => _seed;
            set { if (_seed != value) { _seed = value; OnPropertyChanged(); } }
        }

        public bool UseRtxUpscale
        {
            get => _useRtxUpscale;
            set { if (_useRtxUpscale != value) { _useRtxUpscale = value; OnPropertyChanged(); } }
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

        public ObservableCollection<WanCharReplaceQueueItem> Queue => _queue;

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
                "Select Replacement Character Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDirectory,
                persistKey: "wancharreplace.image");

            if (filePath != null)
            {
                CharacterImagePath = filePath;
                AddLog($"CharReplace: Selected character image: {Path.GetFileName(filePath)}");
            }
        }

        private async void SelectVideo()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Video (source character + motion)",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.webm|All Files|*.*",
                initialDirectory,
                persistKey: "wancharreplace.video");

            if (filePath != null)
            {
                InputVideoPath = filePath;
                AddLog($"CharReplace: Selected video: {Path.GetFileName(filePath)}");
            }
        }

        #endregion

        #region Video Info + Chunk Timeline

        private async void LoadVideoInfoAsync()
        {
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

            var path = InputVideoPath;
            var duration = await Task.Run(() => GetVideoDuration(path));
            // Use native frame count from ffprobe — skip_first_frames in VHS_LoadVideo is in
            // native video frames, so chunk math must use native frames, not duration * outputFps.
            var frameCount = await Task.Run(() => GetVideoFrameCount(path));
            if (frameCount <= 0 && duration > 0)
                frameCount = (int)Math.Floor(duration * Math.Max(Fps, 24));

            if (path != InputVideoPath) return;

            TotalFrames = frameCount > 0 ? frameCount : 0;

            _nativeFps = duration > 0 && TotalFrames > 0 ? TotalFrames / duration : (double)Fps;
            VideoDuration = duration > 0 ? $"{duration:F1}s" : "—";
            VideoFpsDisplay = $"{_nativeFps:F1}";
            VideoFrameCountDisplay = TotalFrames > 0 ? TotalFrames.ToString("N0") : "—";
            VideoChunksDisplay = TotalChunks > 0 ? $"{TotalChunks} chunk{(TotalChunks != 1 ? "s" : "")}" : "—";
            HasVideoInfo = TotalFrames > 0;

            BuildChunkTimeline();
            OnPropertyChanged(nameof(CanAddToQueue));
            OnCanExecuteChanged();

            AddLog($"Video analyzed: {TotalFrames} native frames @ {_nativeFps:F1}fps → {TotalChunks} chunks of {FramesPerChunk}");
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

            // Load motion cache (video-specific, image-independent) — this is the primary cache.
            var cachedMotion = _promptCache.GetAllMotion(InputVideoPath);
            var cachedAppearance = HasCharacterImage ? _promptCache.GetAppearance(CharacterImagePath) : null;

            // Reconstruct combined prompts from motion + current appearance.
            foreach (var (k, motion) in cachedMotion)
            {
                _chunkPrompts[k] = string.IsNullOrWhiteSpace(cachedAppearance)
                    ? motion
                    : $"{cachedAppearance}, {motion}";
            }

            // Also load any legacy combined prompts for chunks not yet in motion cache.
            var legacyCombined = _promptCache.GetAllPrompts(InputVideoPath);
            foreach (var (k, combined) in legacyCombined)
            {
                if (!_chunkPrompts.ContainsKey(k))
                    _chunkPrompts[k] = combined;
            }

            int motionCount = cachedMotion.Count;
            if (motionCount > 0 || legacyCombined.Count > 0)
            {
                var appearanceNote = string.IsNullOrWhiteSpace(cachedAppearance) ? ", no appearance cached yet" : "";
                AddLog($"Loaded {_chunkPrompts.Count} chunk prompt(s) ({motionCount} from motion cache{appearanceNote})");
            }

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
                    // Motion cached = prompt is ready regardless of which image is loaded.
                    HasCachedPrompt = cachedMotion.ContainsKey(i) || legacyCombined.ContainsKey(i),
                });
            }

            _selectedChunkIndex = 0;
            UpdateChunkSelectionInfo();
            OnPropertyChanged(nameof(CanAnalyzeImage));
            OnPropertyChanged(nameof(CanAnalyzeAllChunks));
            OnCanExecuteChanged();
        }

        // Recombines cached motion with the newly selected character image's appearance.
        // Called when CharacterImagePath changes so prompts update without re-running analysis.
        private void TryReconstructPromptsFromCache()
        {
            if (!HasCharacterImage || !HasInputVideo || _chunkItems.Count == 0) return;

            var cachedMotion = _promptCache.GetAllMotion(InputVideoPath);
            if (cachedMotion.Count == 0) return;

            var cachedAppearance = _promptCache.GetAppearance(CharacterImagePath);

            bool anyUpdated = false;
            foreach (var (chunkIdx, motion) in cachedMotion)
            {
                var chunk = _chunkItems.FirstOrDefault(c => c.Index == chunkIdx);
                if (chunk == null) continue;

                var prompt = string.IsNullOrWhiteSpace(cachedAppearance)
                    ? motion
                    : $"{cachedAppearance}, {motion}";

                _chunkPrompts[chunkIdx] = prompt;
                chunk.HasCachedPrompt = true;
                anyUpdated = true;
            }

            if (anyUpdated)
            {
                if (_chunkPrompts.TryGetValue(_selectedChunkIndex, out var p))
                    Prompt = p;

                var note = string.IsNullOrWhiteSpace(cachedAppearance)
                    ? " (new image not yet analyzed — run Analyze to update)"
                    : " (combined with cached appearance)";
                AddLog($"Reconstructed {cachedMotion.Count} prompt(s) from motion cache{note}");
            }
        }

        private void OnChunkSelected(WanScailChunkItem? chunk)
        {
            if (chunk == null) return;
            var index = chunk.Index;

            foreach (var c in _chunkItems)
                c.IsSelected = c.Index == index;

            _selectedChunkIndex = index;
            UpdateChunkSelectionInfo();

            if (_chunkPrompts.TryGetValue(index, out var cachedPrompt) && !string.IsNullOrWhiteSpace(cachedPrompt))
                Prompt = cachedPrompt;

            if (_nativeFps > 0 && TotalFrames > 0)
                SeekRequested?.Invoke(this, TimeSpan.FromSeconds((index * FramesPerChunk) / _nativeFps));
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
            var t = _nativeFps > 0 ? (sf / _nativeFps).ToString("F2") : "—";
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
                    chunk.IsSelected = chunk.Index == _selectedChunkIndex;
                }
            });
        }

        #endregion

        #region Image Analysis

        private async Task AnalyzeImageAsync()
        {
            if (!CanAnalyzeImage) return;

            // Check what's already cached.
            var cachedMotion = _promptCache.GetAllMotion(InputVideoPath);
            bool allMotionCached = cachedMotion.Count >= _chunkItems.Count;
            var cachedAppearance = _promptCache.GetAppearance(CharacterImagePath);
            bool appearanceCached = !string.IsNullOrWhiteSpace(cachedAppearance);

            // Only ask before re-doing work when everything is already cached.
            if (allMotionCached && appearanceCached)
            {
                var answer = MessageBox.Show(
                    $"All {_chunkItems.Count} chunk(s) and the reference image are already fully analyzed.\n\nRedo full analysis from scratch?",
                    "Already Analyzed",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes)
                {
                    // Just reconstruct and return — nothing to do.
                    TryReconstructPromptsFromCache();
                    return;
                }

                // Redo everything — clear cached values so nothing is skipped below.
                cachedMotion = new Dictionary<int, string>();
                cachedAppearance = null;
                appearanceCached = false;
                allMotionCached = false;
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
                    MessageBox.Show("No LM Studio model available. Please ensure LM Studio is running.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int totalChunks = _chunkItems.Count;
                int chunksNeedingMotion = totalChunks - cachedMotion.Count;

                if (allMotionCached)
                    AddLog($"=== CharReplace: video motion fully cached ({totalChunks} chunks) — analyzing image only ===");
                else
                    AddLog($"=== CharReplace analysis started — sending to {_lmStudioService.DescribeTarget(selectedModel)} ({chunksNeedingMotion}/{totalChunks} chunks need motion) ===");

                // ── Step 1: Appearance (image-specific) ──────────────────────
                string appearanceDescription;
                if (appearanceCached)
                {
                    appearanceDescription = cachedAppearance!;
                    AddLog($"Appearance loaded from cache: {Truncate(appearanceDescription, 120)}");
                }
                else
                {
                    const string appearanceSystemPrompt =
                        "Describe only what the person is wearing and their physical appearance in this image. " +
                        "Include clothing (style and colors), hairstyle, and body type. " +
                        "Do not describe what they are doing, their pose, or any action. " +
                        "Write one or two sentences. Output only the description as plain text — no labels, no headers, no markdown.";

                    AnalyzeAllChunksStatus = "Analyzing reference image…";
                    OnPropertyChanged(nameof(AnalyzeAllChunksStatus));
                    AddLog("Step 1: Extracting replacement character appearance…");

                    var appearanceRaw = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                        selectedModel, CharacterImagePath, "Describe this character's appearance.",
                        appearanceSystemPrompt, maxTokens: 2000, cancellationToken: token);

                    appearanceDescription = CleanLLMOutput(appearanceRaw);
                    if (string.IsNullOrWhiteSpace(appearanceDescription))
                    {
                        AddLog("ERROR: Could not extract character appearance");
                        return;
                    }

                    _promptCache.SaveAppearance(CharacterImagePath, appearanceDescription);
                    AddLog($"Appearance: {Truncate(appearanceDescription, 120)} (cached for future use)");
                }

                // ── Step 2: Motion per chunk (video-specific) ────────────────
                const string motionSystemPrompt =
                    "Describe only the body movement and actions visible in these sequential video frames. " +
                    "Focus on what the person is doing: their poses, gestures, and direction of motion. " +
                    "Do not mention clothing, hair, or appearance. " +
                    "Write one or two sentences. Output only the movement description as plain text — no labels, no headers, no markdown.";

                int done = 0;

                for (int i = 0; i < totalChunks; i++)
                {
                    if (token.IsCancellationRequested) break;

                    string motionDescription;

                    if (cachedMotion.TryGetValue(i, out var existingMotion))
                    {
                        // Motion already cached — just (re)combine with the new appearance.
                        motionDescription = existingMotion;
                    }
                    else
                    {
                        int motionDone = i - cachedMotion.Count(kv => kv.Key < i);
                        AnalyzeAllChunksStatus = $"Analyzing chunk {motionDone + 1}/{chunksNeedingMotion}…";
                        OnPropertyChanged(nameof(AnalyzeAllChunksStatus));
                        AddLog($"Step 2 — Chunk {i + 1}/{totalChunks}: extracting motion…");

                        List<string> framePaths = new();
                        try
                        {
                            framePaths = await ExtractChunkFramesAsync(InputVideoPath, i, token);
                            if (framePaths.Count == 0) { AddLog($"Chunk {i + 1}: no frames extracted, skipping"); continue; }

                            var motionRaw = await _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                                selectedModel, framePaths, "Describe the movement in these video frames.",
                                motionSystemPrompt, maxTokens: 2000, cancellationToken: token);

                            motionDescription = CleanLLMOutput(motionRaw);
                            if (string.IsNullOrWhiteSpace(motionDescription)) { AddLog($"Chunk {i + 1}: empty motion, skipping"); continue; }

                            // Save motion separately so it survives image changes.
                            _promptCache.SaveMotion(InputVideoPath, i, motionDescription);
                            AddLog($"Chunk {i + 1} motion: {Truncate(motionDescription, 100)} (cached)");
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex) { AddLog($"Chunk {i + 1} failed: {ex.Message}"); continue; }
                        finally { foreach (var f in framePaths) try { File.Delete(f); } catch { } }
                    }

                    var combinedPrompt = $"{appearanceDescription}, {motionDescription}";
                    _chunkPrompts[i] = combinedPrompt;
                    _promptCache.SavePrompt(InputVideoPath, i, combinedPrompt);
                    done++;

                    var chunkIdx = i;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var tile = _chunkItems.FirstOrDefault(c => c.Index == chunkIdx);
                        if (tile != null) tile.HasCachedPrompt = true;
                        if (chunkIdx == _selectedChunkIndex)
                            Prompt = combinedPrompt;
                    });
                }

                AnalyzeAllChunksStatus = token.IsCancellationRequested
                    ? $"Cancelled — {done}/{totalChunks} chunks ready"
                    : $"Done — {done}/{totalChunks} chunks ready";
                OnPropertyChanged(nameof(AnalyzeAllChunksStatus));
                AddLog($"=== CharReplace analysis complete: {done}/{totalChunks} chunks ===");
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

        private static string Truncate(string s, int max) =>
            s.Length > max ? s[..max] + "…" : s;

        private async Task<List<string>> ExtractChunkFramesAsync(string videoPath, int chunkIndex, CancellationToken token)
        {
            var frames = new List<string>();
            var ffmpegPath = FindFFmpeg();
            if (ffmpegPath == null) return frames;

            var fps = _nativeFps > 0 ? _nativeFps : (Fps > 0 ? (double)Fps : 16.0);
            var startFrame = chunkIndex * FramesPerChunk;
            var endFrame = Math.Min(startFrame + FramesPerChunk - 1, TotalFrames - 1);
            int numFrames = Math.Min(4, endFrame - startFrame + 1);

            for (int f = 0; f < numFrames; f++)
            {
                if (token.IsCancellationRequested) break;
                var frameIdx = numFrames == 1 ? startFrame
                    : startFrame + (int)Math.Round((double)(endFrame - startFrame) * f / (numFrames - 1));
                var timeSec = frameIdx / fps;
                var tempFile = Path.Combine(Path.GetTempPath(), $"wancharreplace_frame_{Guid.NewGuid():N}.jpg");

                await Task.Run(() =>
                {
                    var si = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = $"-loglevel quiet -nostats -ss {timeSec:F3} -i \"{videoPath}\" " +
                                    $"-frames:v 1 -q:v 3 -vf \"scale=512:512:force_original_aspect_ratio=decrease\" \"{tempFile}\" -y",
                        UseShellExecute = false, CreateNoWindow = true,
                    };
                    using var proc = Process.Start(si);
                    if (proc != null && !proc.WaitForExit(30000)) try { proc.Kill(); } catch { }
                }, token);

                if (File.Exists(tempFile)) frames.Add(tempFile);
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
                var bmp = LoadBitmapFrozen(CharacterImagePath);
                CharacterImagePreview = bmp;
                var fi = new FileInfo(CharacterImagePath);
                CharacterImageInfo = $"{bmp.PixelWidth}x{bmp.PixelHeight} • {fi.Length / 1024}KB";
                AddLog($"Character image loaded: {bmp.PixelWidth}x{bmp.PixelHeight}");
            }
            catch (Exception ex) { AddLog($"Error loading character image: {ex.Message}"); CharacterImageInfo = "Error loading image"; }
        }

        private static BitmapImage LoadBitmapFrozen(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
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
                ? "character motion, smooth animation, high quality"
                : Prompt;
            var item = new WanCharReplaceQueueItem
            {
                CharacterImagePath = CharacterImagePath,
                InputVideoPath = InputVideoPath,
                Prompt = effectivePrompt,
                NegativePrompt = NegativePrompt,
                Fps = Fps,
                Steps = Steps,
                Seed = Seed,
                SingleChunkIndex = singleChunkIndex,
                ItemStatus = QueueItemStatus.Pending,
            };

            _queue.Add(item);
            SaveQueueToFile();
            var desc = singleChunkIndex.HasValue ? $"chunk {singleChunkIndex.Value + 1}" : "all chunks";
            AddLog($"Added to CharReplace queue ({desc}): {item.DisplayText}");
            UpdateQueueStatus();

            if (!IsProcessingQueue) _ = ProcessQueueAsync();
        }

        private void RemoveQueueItem(WanCharReplaceQueueItem? item)
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
            QueueStatus = total == 0 ? string.Empty : $"{pending} pending • {completed} done • {failed} failed";
            OnPropertyChanged(nameof(HasFailedItems));
            OnCanExecuteChanged();
        }

        private void ClearQueue()
        {
            _queueCts?.Cancel();
            foreach (var item in _queue.ToList()) _queue.Remove(item);
            SaveQueueToFile();
            UpdateQueueStatus();
            AddLog("CharReplace queue cleared");
        }

        private void StopQueue() { _queueCts?.Cancel(); AddLog("CharReplace queue stop requested"); }

        private async Task ReprocessAllFailedAsync()
        {
            var failed = _queue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (!failed.Any()) return;
            foreach (var item in failed) item.ItemStatus = QueueItemStatus.Pending;
            UpdateQueueStatus();
            SaveQueueToFile();
            AddLog($"Reprocessing {failed.Count} failed CharReplace item(s)...");
            if (!IsProcessingQueue) await ProcessQueueAsync();
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
                lease = await _workflowCoordinator.AcquireAsync("WanCharReplace", token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue cancelled while waiting");
                IsProcessingQueue = false;
                OnCanExecuteChanged();
                return;
            }

            AddLog("Starting CharReplace queue processing...");
            using (lease)
            try
            {
                WanCharReplaceQueueItem? item;
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
                        AddLog("Queue item cancelled — reset to Pending");
                        break;
                    }
                    catch (Exception ex)
                    {
                        var shouldRetry = await TryHandleCrashAndRetryAsync(item, ex);
                        if (shouldRetry) { item.ItemStatus = QueueItemStatus.Pending; AddLog("Item reset to Pending — will retry"); }
                        else { item.ItemStatus = QueueItemStatus.Failed; item.ErrorMessage = ex.Message; AddLog($"Queue item FAILED: {ex.Message}"); }
                    }
                    UpdateQueueStatus();
                    SaveQueueToFile();
                }
            }
            finally
            {
                IsProcessingQueue = false;
                AddLog("CharReplace queue processing finished.");
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
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(QueueFilePath, JsonSerializer.Serialize(_queue.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AddLog($"Error saving queue: {ex.Message}"); }
        }

        private void LoadQueueFromFile()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;
                var items = JsonSerializer.Deserialize<List<WanCharReplaceQueueItem>>(File.ReadAllText(QueueFilePath));
                if (items?.Any() != true) return;
                _queue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Processing) item.ItemStatus = QueueItemStatus.Pending;
                    _queue.Add(item);
                }
                UpdateQueueStatus();
                AddLog($"CharReplace queue loaded: {_queue.Count} items");
                var pending = _queue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
                if (pending > 0 && !IsProcessingQueue) _ = ProcessQueueAsync();
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        #endregion

        #region Video Generation

        private async Task GenerateSingleVideoAsync(WanCharReplaceQueueItem item)
        {
            try
            {
                AddLog($"=== CharReplace generation: {item.DisplayText} ===");
                IsProcessing = true;
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing Character Replace workflow...";

                var videoFrameCount = TotalFrames > 0 ? TotalFrames : GetVideoFrameCount(item.InputVideoPath);
                if (videoFrameCount <= 0) videoFrameCount = FramesPerChunk;
                TotalFrames = videoFrameCount;

                int startChunk = item.SingleChunkIndex ?? 0;
                int endChunk = item.SingleChunkIndex.HasValue ? item.SingleChunkIndex.Value + 1 : TotalChunks;

                AddLog($"Processing chunks {startChunk + 1}–{endChunk} of {TotalChunks}");

                ProcessingStatus = "Checking ComfyUI...";
                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyUIOk)
                {
                    AddLog("ERROR: ComfyUI is not running");
                    MessageBox.Show("ComfyUI is not running. Please start ComfyUI and try again.", "ComfyUI Not Running", MessageBoxButton.OK, MessageBoxImage.Warning);
                    throw new Exception("ComfyUI is not running.");
                }

                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                    AddLog("Connected to ComfyUI");
                }

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", WorkflowFileName);
                if (!File.Exists(workflowPath)) throw new FileNotFoundException($"CharReplace workflow not found: {workflowPath}");

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                ProcessingStatus = "Uploading assets...";
                ProcessingProgress = 10;

                var (videoW, videoH) = GetVideoDimensions(item.InputVideoPath);
                int outputWidth = 640, outputHeight = 640;
                if (videoW > 0 && videoH > 0)
                {
                    (outputWidth, outputHeight) = ComputeOutputResolution(videoW, videoH);
                    AddLog($"Output resolution: {outputWidth}x{outputHeight}");
                }

                string? croppedTemp = null;
                string imageToUpload = item.CharacterImagePath;
                if (outputWidth > 0 && outputHeight > 0)
                {
                    croppedTemp = CropImageToAspectRatio(item.CharacterImagePath, outputWidth, outputHeight);
                    if (croppedTemp != null) imageToUpload = croppedTemp;
                }

                AddLog("Uploading character image...");
                var uploadedImageName = await _comfyUIService.UploadImageAsync(imageToUpload);
                if (!string.IsNullOrEmpty(croppedTemp)) try { File.Delete(croppedTemp); } catch { }
                if (string.IsNullOrEmpty(uploadedImageName)) throw new Exception("Failed to upload character image.");
                AddLog($"Character image uploaded: {uploadedImageName}");

                AddLog("Uploading reference video...");
                var uploadedVideoName = await _comfyUIService.UploadVideoAsync(item.InputVideoPath);
                if (string.IsNullOrEmpty(uploadedVideoName)) throw new Exception("Failed to upload reference video.");
                AddLog($"Reference video uploaded: {uploadedVideoName}");

                var numChunks = endChunk - startChunk;
                var chunkFiles = new List<string>();
                var runSeed = item.Seed >= 0 ? item.Seed : (long)(new Random().NextDouble() * long.MaxValue);

                for (int chunkIndex = startChunk; chunkIndex < endChunk; chunkIndex++)
                {
                    SetChunkStatus(chunkIndex, WanScailChunkStatus.Processing);
                    try
                    {
                        var relativeChunk = chunkIndex - startChunk + 1;
                        AddLog($"=== Chunk {chunkIndex + 1} (job {relativeChunk}/{numChunks}): skip={chunkIndex * FramesPerChunk} frames ===");
                        ProcessingStatus = $"Processing chunk {chunkIndex + 1} ({relativeChunk}/{numChunks})";
                        var baseProgress = 20.0 + (relativeChunk - 1) * 60.0 / numChunks;

                        if (chunkIndex > startChunk && !_comfyUIService.IsConnected)
                        {
                            AddLog("Reconnecting to ComfyUI...");
                            await _comfyUIService.ConnectAsync();
                        }

                        var chunkPrompt = _chunkPrompts.TryGetValue(chunkIndex, out var cp) && !string.IsNullOrWhiteSpace(cp)
                            ? cp : item.Prompt;

                        var updatedWorkflow = BuildWorkflow(workflow, uploadedImageName, uploadedVideoName,
                            chunkIndex, chunkPrompt, item.NegativePrompt, item.Fps, item.Steps,
                            outputWidth, outputHeight, runSeed + chunkIndex);

                        var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                        {
                            if (msg.Data?.Value != null && msg.Data?.Max != null)
                            {
                                var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    ProcessingProgress = baseProgress + pct * 0.6 / numChunks;
                                    ProcessingStatus = $"Chunk {chunkIndex + 1} ({relativeChunk}/{numChunks}): {msg.Data.Value}/{msg.Data.Max}";
                                });
                            }
                        });

                        var existingFiles = GetExistingVideoFiles("*.mp4", OutputSubfolder);
                        var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                        AddLog($"Chunk {chunkIndex + 1} submitted, prompt ID: {promptId}");

                        var outputVideo = await TryGetVideoFromHistoryAsync(promptId);
                        if (outputVideo == null)
                        {
                            AddLog("History API returned no result, polling filesystem...");
                            outputVideo = await WaitForNewVideoAsync(existingFiles, "*.mp4",
                                TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5), OutputSubfolder);
                        }

                        if (outputVideo != null && File.Exists(outputVideo))
                        {
                            // Trim to source chunk duration so the merged video matches the
                            // original length (WAN always generates FramesPerChunk frames at
                            // output fps, which is longer than the source chunk when native fps
                            // is higher than output fps).
                            int actualFrames = Math.Min(FramesPerChunk, TotalFrames - chunkIndex * FramesPerChunk);
                            var videoToUse = TrimChunkToSourceDuration(outputVideo, actualFrames, chunkIndex);
                            var chunkFile = Path.Combine(Path.GetTempPath(),
                                $"wancharreplace_chunk_{chunkIndex:D3}_{Path.GetFileName(outputVideo)}");
                            File.Copy(videoToUse, chunkFile, true);
                            if (videoToUse != outputVideo) try { File.Delete(videoToUse); } catch { }
                            chunkFiles.Add(chunkFile);
                            SetChunkStatus(chunkIndex, WanScailChunkStatus.Done);
                            AddLog($"Chunk {chunkIndex + 1} complete: {Path.GetFileName(chunkFile)}");
                        }
                        else
                        {
                            SetChunkStatus(chunkIndex, WanScailChunkStatus.Failed);
                            AddLog($"ERROR: No output for chunk {chunkIndex + 1} — aborting");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        SetChunkStatus(chunkIndex, WanScailChunkStatus.Failed);
                        AddLog($"ERROR chunk {chunkIndex + 1}: {ex.Message}");
                        break;
                    }
                }

                ProcessingProgress = 85;
                ProcessingStatus = "Merging output chunks...";

                if (chunkFiles.Count > 0)
                {
                    var outputDir = Path.Combine(
                        _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "CharReplace");
                    Directory.CreateDirectory(outputDir);
                    var finalPath = Path.Combine(outputDir, $"CharReplace_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                    if (chunkFiles.Count == 1) File.Copy(chunkFiles[0], finalPath, true);
                    else MergeVideoChunksWithFFmpeg(chunkFiles, finalPath);

                    foreach (var f in chunkFiles) try { File.Delete(f); } catch { }

                    item.OutputVideoPath = finalPath;
                    ResultVideoPath = finalPath;
                    await LocalCopyService.CopyVideoAsync(finalPath);
                    HasResult = true;
                    ResultVideoInfo = $"Character Replace Video • {new FileInfo(finalPath).Length / 1024 / 1024:F1}MB";
                    ProcessingProgress = 100;
                    ProcessingStatus = "Character Replace Complete!";
                    AddLog($"=== CharReplace complete: {finalPath} ===");
                }
                else
                {
                    AddLog("ERROR: No chunks generated");
                    ProcessingStatus = "No output generated";
                    throw new Exception("No video chunks were generated.");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                ProcessingStatus = "Error occurred";
                throw;
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private static (int Width, int Height) ComputeOutputResolution(int videoW, int videoH)
        {
            const int shortEdge = 480;
            const int longEdge = 832;
            const int alignment = 32;
            if (videoH > videoW)
            {
                int h = (int)(Math.Round((double)shortEdge * videoH / videoW / alignment) * alignment);
                return (shortEdge, Math.Min(h, longEdge));
            }
            else
            {
                int w = (int)(Math.Round((double)shortEdge * videoW / videoH / alignment) * alignment);
                return (Math.Min(w, longEdge), shortEdge);
            }
        }

        private JsonElement BuildWorkflow(
            JsonElement baseWorkflow,
            string imageName, string videoName,
            int chunkIndex, string prompt, string negativePrompt,
            int fps, int steps, int width, int height, long seed)
        {
            var wfJson = baseWorkflow.GetRawText();
            int skipFrames = chunkIndex * FramesPerChunk;

            // Node 57: replacement character image
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "57", "image", imageName);

            // Node 63: reference video with chunk offset via skip_first_frames
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref wfJson, "63", new Dictionary<string, object>
            {
                { "video", videoName },
                { "skip_first_frames", skipFrames },
                { "frame_load_cap", FramesPerChunk },
                { "custom_width", width },
                { "custom_height", height },
            });

            // Node 65: text prompts
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref wfJson, "65", new Dictionary<string, object>
            {
                { "positive_prompt", prompt },
                { "negative_prompt", negativePrompt },
            });

            // Nodes 150/151: output dimensions
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "150", "value", width);
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "151", "value", height);

            // Node 27: sampler seed and steps
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "27", "seed", seed);
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "27", "steps", steps);

            // Optional RTX 2x upscale: insert RTXVideoSuperResolution between decode (node 28)
            // and GetImageSizeAndCount (node 42) so the clean output (186) is upscaled.
            // Node 42 already passes images through to 186 — we redirect 42's input to use RTX output.
            if (UseRtxUpscale)
            {
                WorkflowNodeUpdater.AddNode(ref wfJson, "cr_rtx", new
                {
                    inputs = new Dictionary<string, object>
                    {
                        { "scale", 2 },
                        { "quality", "ULTRA" },
                        { "deblur", "OFF" },
                        { "images", new object[] { "28", 0 } },
                    },
                    class_type = "RTXVideoSuperResolution",
                    _meta = new { title = "RTX Video Super Resolution 2x" }
                });
                // Route node 1860 (CreateVideo) images through the RTX upscaler instead of node 42
                WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "1860", "images", new object[] { "cr_rtx", 0 });
                AddLog("RTX 2x upscale enabled — node cr_rtx inserted");
            }

            // Node 186 (SaveVideo): main output — set filename prefix
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "186", "filename_prefix", $"{OutputSubfolder}/CharReplace");
            // Node 1860 (CreateVideo): set fps for main output
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "1860", "fps", fps);

            // Node 30 (SaveVideo): comparison/debug output — set filename prefix
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "30", "filename_prefix", $"{TmpOutputSubfolder}/CharReplace_cmp");
            // Node 300 (CreateVideo): set fps for comparison output
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "300", "fps", fps);

            AddLog($"Workflow: skip={skipFrames}, res={width}x{height}, fps={fps}, steps={steps}, seed={seed}, rtx={UseRtxUpscale}");
            return JsonSerializer.Deserialize<JsonElement>(wfJson);
        }

        // Trims a WAN-generated chunk to match the source chunk's duration.
        // WAN always generates FramesPerChunk frames at output fps; when the source
        // fps is higher than output fps the WAN clip is proportionally longer than the
        // source segment it represents.  We use ffmpeg -c copy (no re-encode) to cut.
        private string TrimChunkToSourceDuration(string inputPath, int actualSourceFrames, int chunkIndex)
        {
            double effectiveFps = _nativeFps > 0 ? _nativeFps : Fps;
            double targetSec = actualSourceFrames / effectiveFps;
            double wanOutputSec = (double)FramesPerChunk / Fps;

            // Nothing to trim when source duration ≥ WAN output (e.g. source is same fps
            // or slower, meaning the WAN output is already the right length or shorter).
            if (targetSec >= wanOutputSec - 0.05)
                return inputPath;

            var ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
                return inputPath;

            var trimmed = Path.Combine(Path.GetTempPath(), $"wancharreplace_trim_{Guid.NewGuid():N}.mp4");
            var si = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-i \"{inputPath}\" -t {targetSec:F3} -c copy \"{trimmed}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(si);
            if (proc == null) return inputPath;
            proc.WaitForExit(30000);

            if (File.Exists(trimmed))
            {
                AddLog($"Chunk {chunkIndex + 1}: trimmed {wanOutputSec:F2}s → {targetSec:F2}s ({effectiveFps:F1}fps source, {Fps}fps output)");
                return trimmed;
            }
            return inputPath;
        }

        private void MergeVideoChunksWithFFmpeg(List<string> chunkFiles, string outputPath)
            => MergeVideoChunks(chunkFiles, outputPath, "wancharreplace");

        #endregion

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
