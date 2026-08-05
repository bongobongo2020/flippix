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
    public partial class VACEVideoViewModel : VideoProcessingBaseViewModel
    {
        private const int FramesPerChunk = 81;
        private const string OutputSubfolder = "wan_vace";

        private string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "vace_queue.json");

        // ── Input fields ──────────────────────────────────────────────────────
        private string _prompt = string.Empty;
        private string _foregroundImagePath = string.Empty;
        private BitmapImage? _foregroundImagePreview;
        private string _foregroundImageInfo = string.Empty;
        private string _inputVideoPath = string.Empty;
        private string _inputVideoInfo = string.Empty;
        private int _totalFrames;
        private bool _isAnalyzing;
        private bool _isProcessingQueue;
        private string _queueStatus = string.Empty;
        private int _fps = 24;

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
        private readonly ObservableCollection<VaceQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;
        private CancellationTokenSource? _analyzeCts;
        private bool _isAnalyzingAll;

        public event EventHandler<TimeSpan>? SeekRequested;

        public VACEVideoViewModel(
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

            SelectForegroundImageCommand = new RelayCommand(SelectForegroundImage);
            SelectVideoCommand = new RelayCommand(SelectVideo);
            GenerateVideoCommand = new RelayCommand(AddAllChunksToQueue, () => CanAddToQueue);
            ProcessSelectedChunkCommand = new RelayCommand(AddSelectedChunkToQueue, () => CanAddToQueue && _chunkItems.Any());
            SelectChunkCommand = new RelayCommand<WanScailChunkItem>(OnChunkSelected);
            RemoveQueueItemCommand = new RelayCommand<VaceQueueItem>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => _queue.Any());
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            StartQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => HasQueueItems && !IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(async () => await ReprocessAllFailedAsync(), () => HasFailedItems);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);
            AnalyzeImageCommand = new RelayCommand(async () => await AnalyzeImageAsync(), () => CanAnalyzeImage);
            AnalyzeAllChunksCommand = new RelayCommand(async () => await AnalyzeImageAsync(), () => CanAnalyzeAllChunks);

            _queue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
                OnCanExecuteChanged();
            };

            AddLog("VACE Video Generator initialized");
            LoadQueueFromFile();
        }

        #region Commands

        public ICommand SelectForegroundImageCommand { get; }
        public ICommand SelectVideoCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand ProcessSelectedChunkCommand { get; }
        public RelayCommand<WanScailChunkItem> SelectChunkCommand { get; }
        public RelayCommand<VaceQueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand StartQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }
        public RelayCommand AnalyzeImageCommand { get; }
        public RelayCommand AnalyzeAllChunksCommand { get; }

        public bool HasFailedItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        #endregion

        #region Input Properties

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
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    OnCanExecuteChanged();
                }
            }
        }

        public string ForegroundImagePath
        {
            get => _foregroundImagePath;
            set
            {
                if (_foregroundImagePath != value)
                {
                    _foregroundImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasForegroundImage));
                    OnPropertyChanged(nameof(CanAddToQueue));
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    OnPropertyChanged(nameof(CanAnalyzeImage));
                    LoadForegroundImagePreview();
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? ForegroundImagePreview
        {
            get => _foregroundImagePreview;
            set { _foregroundImagePreview = value; OnPropertyChanged(); }
        }

        public string ForegroundImageInfo
        {
            get => _foregroundImageInfo;
            set { if (_foregroundImageInfo != value) { _foregroundImageInfo = value; OnPropertyChanged(); } }
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
                    OnPropertyChanged(nameof(CanGenerateVideo));
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

        public bool HasForegroundImage => !string.IsNullOrEmpty(ForegroundImagePath) && File.Exists(ForegroundImagePath);
        public bool HasInputVideo => !string.IsNullOrEmpty(InputVideoPath) && File.Exists(InputVideoPath);
        public bool CanGenerateVideo => HasForegroundImage && HasInputVideo && !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing;
        public bool CanAddToQueue => HasForegroundImage && HasInputVideo;

        public bool CanAnalyzeImage => HasForegroundImage && HasInputVideo && _chunkItems.Any() && !IsAnalyzing && !IsAnalyzingAll && !IsProcessing;
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

        public ObservableCollection<VaceQueueItem> Queue => _queue;

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

        private async void SelectForegroundImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                initialDirectory,
                persistKey: "vace.image");

            if (filePath != null)
            {
                ForegroundImagePath = filePath;
                AddLog($"VACE: Selected reference image: {Path.GetFileName(ForegroundImagePath)}");
            }
        }

        private async void SelectVideo()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Input Video",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv|All Files|*.*",
                initialDirectory,
                persistKey: "vace.video");

            if (filePath != null)
            {
                InputVideoPath = filePath;
                AddLog($"VACE: Selected video: {Path.GetFileName(InputVideoPath)}");
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
            var frameCount = duration > 0 && _fps > 0
                ? (int)Math.Floor(duration * _fps)
                : await Task.Run(() => GetVideoFrameCount(path));

            if (path != InputVideoPath) return;

            TotalFrames = frameCount > 0 ? frameCount : 0;

            var estimatedDuration = duration > 0 ? duration : (_fps > 0 && TotalFrames > 0 ? (double)TotalFrames / _fps : 0);
            VideoDuration = estimatedDuration > 0 ? $"{estimatedDuration:F1}s" : "—";
            VideoFpsDisplay = _fps.ToString();
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

            if (_chunkPrompts.TryGetValue(index, out var cachedPrompt) && !string.IsNullOrWhiteSpace(cachedPrompt))
                Prompt = cachedPrompt;

            if (_fps > 0 && TotalFrames > 0)
            {
                var seekTime = TimeSpan.FromSeconds((double)(index * FramesPerChunk) / _fps);
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
            var t = _fps > 0 ? (sf / (double)_fps).ToString("F2") : "—";
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

        #region Image + Motion Analysis

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

                AddLog($"=== VACE analysis started — sending to {_lmStudioService.DescribeTarget(selectedModel)} ===");

                // Step 1: Extract character appearance from reference image (once)
                const string appearanceSystemPrompt =
                    "Describe only what the person is wearing and their physical appearance in this image. " +
                    "Include clothing (style and colors), hairstyle, and body type. " +
                    "Do not describe what they are doing, their pose, their expression, or any action. " +
                    "Write one or two sentences. Output only the description as plain text — no labels, no headers, no markdown.";

                AddLog("Step 1: Extracting character appearance from reference image…");
                var appearanceRaw = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel,
                    ForegroundImagePath,
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
                var apPreview = appearanceDescription.Length > 120 ? appearanceDescription[..120] + "…" : appearanceDescription;
                AddLog($"Appearance: {apPreview}");

                // Step 2: Per-chunk motion extraction
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

                        var motPreview = motionDescription.Length > 100 ? motionDescription[..100] + "…" : motionDescription;
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
                AddLog($"=== VACE analysis complete: {done}/{totalChunks} chunks ===");
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

        private async Task<List<string>> ExtractChunkFramesAsync(string videoPath, int chunkIndex, CancellationToken token)
        {
            var frames = new List<string>();
            var ffmpegPath = FindFFmpeg();
            if (ffmpegPath == null) return frames;

            var fps = _fps > 0 ? (double)_fps : 24.0;
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
                var tempFile = Path.Combine(Path.GetTempPath(), $"vace_frame_{Guid.NewGuid():N}.jpg");

                await Task.Run(() =>
                {
                    var si = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
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

        private void LoadForegroundImagePreview()
        {
            if (string.IsNullOrEmpty(ForegroundImagePath) || !File.Exists(ForegroundImagePath))
            {
                ForegroundImagePreview = null;
                ForegroundImageInfo = string.Empty;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(ForegroundImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                ForegroundImagePreview = bitmap;
                var fileInfo = new FileInfo(ForegroundImagePath);
                ForegroundImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fileInfo.Length / 1024}KB";
                AddLog($"Reference image loaded: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                ForegroundImageInfo = "Error loading image";
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

            var item = new VaceQueueItem
            {
                ForegroundImagePath = ForegroundImagePath,
                InputVideoPath = InputVideoPath,
                Prompt = effectivePrompt,
                SingleChunkIndex = singleChunkIndex,
                ItemStatus = QueueItemStatus.Pending
            };

            _queue.Add(item);
            SaveQueueToFile();
            var desc = singleChunkIndex.HasValue ? $"chunk {singleChunkIndex.Value + 1}" : "all chunks";
            AddLog($"Added to VACE queue ({desc}): {item.DisplayText}");
            UpdateQueueStatus();

            if (!IsProcessingQueue)
                _ = ProcessQueueAsync();
        }

        private void RemoveQueueItem(VaceQueueItem? item)
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
            AddLog("VACE queue cleared");
        }

        private void StopQueue()
        {
            _queueCts?.Cancel();
            AddLog("VACE queue stop requested");
        }

        private async Task ReprocessAllFailedAsync()
        {
            var failed = _queue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (!failed.Any()) return;

            foreach (var item in failed)
                item.ItemStatus = QueueItemStatus.Pending;

            UpdateQueueStatus();
            SaveQueueToFile();
            AddLog($"Reprocessing {failed.Count} failed VACE item(s)...");

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
                lease = await _workflowCoordinator.AcquireAsync("VACE", token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue processing cancelled while waiting");
                IsProcessingQueue = false;
                OnCanExecuteChanged();
                return;
            }

            AddLog("Starting VACE queue processing...");
            using (lease)
            try
            {
                VaceQueueItem? item;
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
                        AddLog("VACE queue item cancelled — reset to Pending");
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
                AddLog("VACE queue processing finished.");
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
                var items = JsonSerializer.Deserialize<List<VaceQueueItem>>(File.ReadAllText(QueueFilePath));
                if (items?.Any() != true) return;
                _queue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Processing)
                        item.ItemStatus = QueueItemStatus.Pending;
                    _queue.Add(item);
                }
                UpdateQueueStatus();
                AddLog($"VACE queue loaded: {_queue.Count} items");

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

        private async Task GenerateSingleVideoAsync(VaceQueueItem item)
        {
            try
            {
                AddLog($"=== Starting VACE video generation: {item.DisplayText} ===");
                IsProcessing = true;

                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing VACE workflow...";

                AddLog($"Reference image: {Path.GetFileName(item.ForegroundImagePath)}");
                AddLog($"Input video: {Path.GetFileName(item.InputVideoPath)}");
                AddLog($"Prompt: {item.Prompt}");

                ProcessingStatus = "Analysing input video...";
                var videoFrameCount = TotalFrames > 0 ? TotalFrames : GetVideoFrameCount(item.InputVideoPath);
                if (videoFrameCount <= 0)
                {
                    AddLog("WARNING: Could not determine frame count; defaulting to 1 chunk");
                    videoFrameCount = FramesPerChunk;
                }
                TotalFrames = videoFrameCount;
                AddLog($"Total frames: {TotalFrames} → {TotalChunks} chunk(s) of {FramesPerChunk}");

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

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "Wan-VACE_V2V_MasterAPI.json");
                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    throw new FileNotFoundException($"VACE workflow file not found: {workflowPath}");
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                ProcessingStatus = "Uploading assets to ComfyUI...";
                ProcessingProgress = 10;

                AddLog("Uploading reference image...");
                var uploadedImageName = await _comfyUIService.UploadImageAsync(item.ForegroundImagePath);
                if (string.IsNullOrEmpty(uploadedImageName))
                    throw new Exception("Failed to upload reference image to ComfyUI.");
                AddLog($"Reference image uploaded: {uploadedImageName}");

                AddLog("Uploading video...");
                var uploadedVideoName = await _comfyUIService.UploadVideoAsync(item.InputVideoPath);
                if (string.IsNullOrEmpty(uploadedVideoName))
                    throw new Exception("Failed to upload video to ComfyUI.");
                AddLog($"Video uploaded: {uploadedVideoName}");

                int outputWidth = 480, outputHeight = 832;
                var (videoW, videoH) = GetVideoDimensions(item.InputVideoPath);
                if (videoW > 0 && videoH > 0)
                {
                    double ar = (double)videoW / videoH;
                    if (ar > 1.2) { outputWidth = 832; outputHeight = 480; }
                    else if (ar >= 0.85) { outputWidth = 704; outputHeight = 704; }
                    else { outputWidth = 480; outputHeight = 832; }
                    AddLog($"Output dimensions: {outputWidth}x{outputHeight} (video AR: {ar:F2})");
                }
                else
                {
                    AddLog("Warning: Could not read video dimensions, defaulting to portrait 480x832");
                }

                var numChunks = endChunk - startChunk;
                var chunkFiles = new List<string>();
                AddLog($"=== Processing {numChunks} chunk(s) of {FramesPerChunk} frames ===");

                for (int chunkIndex = startChunk; chunkIndex < endChunk; chunkIndex++)
                {
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

                        var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedImageName, uploadedVideoName,
                            startFrame, framesInChunk, outputWidth, outputHeight, chunkPrompt);

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
                        var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                        AddLog($"Chunk {chunkIndex + 1} submitted, prompt ID: {promptId}");

                        var outputVideo = await TryGetVideoFromHistoryAsync(promptId);

                        if (outputVideo == null)
                        {
                            AddLog("History API returned no result, falling back to filesystem polling...");
                            outputVideo = await WaitForNewVideoAsync(
                                existingFiles, "*.mp4",
                                TimeSpan.FromMinutes(15),
                                TimeSpan.FromSeconds(5),
                                OutputSubfolder);
                        }

                        if (outputVideo != null && File.Exists(outputVideo))
                        {
                            var chunkFile = Path.Combine(Path.GetTempPath(), $"vace_chunk_{chunkIndex:D3}_{Path.GetFileName(outputVideo)}");
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
                        "VACE");
                    Directory.CreateDirectory(outputDir);

                    var finalPath = Path.Combine(outputDir, $"VACE_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

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
                    ResultVideoInfo = $"VACE Video • {fi.Length / 1024 / 1024:F1}MB";
                    ProcessingProgress = 100;
                    ProcessingStatus = "VACE Complete!";
                    AddLog($"=== VACE generation complete: {finalPath} ===");
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
                IsProcessing = false;
            }
        }

        private JsonElement UpdateWorkflowParameters(
            JsonElement workflow,
            string imageName,
            string videoName,
            int startFrame,
            int framesInChunk,
            int outputWidth,
            int outputHeight,
            string prompt)
        {
            var workflowJson = workflow.GetRawText();
            AddLog($"Updating workflow: start={startFrame}, frames={framesInChunk}, size={outputWidth}x{outputHeight}");

            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "10", new Dictionary<string, object>
            {
                { "video", videoName },
                { "frame_load_cap", framesInChunk },
                { "skip_first_frames", startFrame }
            });

            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "148", "image", imageName);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "31", "string", prompt);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "19", "value", framesInChunk);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "20", "value", outputHeight);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "21", "value", outputWidth);

            AddLog($"✓ Nodes updated");
            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        private void MergeVideoChunksWithFFmpeg(List<string> chunkFiles, string outputPath)
            => MergeVideoChunks(chunkFiles, outputPath, "vace");

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
