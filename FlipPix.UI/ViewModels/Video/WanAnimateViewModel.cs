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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// One interactive point placed on the SAM2 segmentation editor canvas.
    /// Coordinates are stored in both 640×640 workflow-space (Wx/Wy)
    /// and display-space (Cx/Cy) so the XAML ItemsControl can position each dot.
    /// </summary>
    public class PointMarker : ObservableObject
    {
        public int Wx { get; set; }
        public int Wy { get; set; }
        public bool IsPositive { get; set; }

        private double _cx;
        public double Cx { get => _cx; set => SetProperty(ref _cx, value); }

        private double _cy;
        public double Cy { get => _cy; set => SetProperty(ref _cy, value); }

        public string FillColor => IsPositive ? "#22C55E" : "#EF4444";
        public string Label => IsPositive ? "+" : "−";
    }

    public partial class WanAnimateViewModel : VideoProcessingBaseViewModel
    {
        // Each workflow run has 2 internal WanAnimateToVideo passes of 77 frames each = 154 frames per run.
        // FramesPerChunk must match the actual output so TotalChunks is calculated correctly.
        private const int FramesPerChunk = 154;
        private const string WorkflowFileName = "video/wan/video_wan2_2_14B_animateAPI.json";
        private const string OutputSubfolder = "wan_animate";
        private const string TmpOutputSubfolder = "wan_animate_tmp";

        private string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "wan_animate_queue.json");

        // ── Input fields ──────────────────────────────────────────────────────
        private string _characterImagePath = string.Empty;
        private BitmapImage? _characterImagePreview;
        private string _characterImageInfo = string.Empty;

        private string _faceImagePath = string.Empty;
        private BitmapImage? _faceImagePreview;
        private string _faceImageInfo = string.Empty;

        private string _inputVideoPath = string.Empty;
        private string _inputVideoInfo = string.Empty;

        private string _prompt = string.Empty;
        private string _negativePrompt = "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";
        private int _fps = 16;
        private int _steps = 6;
        private long _seed = -1;
        private int _totalFrames;
        private bool _isAnalyzing;
        private bool _isProcessingQueue;
        private string _queueStatus = string.Empty;
        private bool _isAnalyzingAll;

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

        // ── Points Editor ─────────────────────────────────────────────────────
        private string? _editorFramePath;
        private readonly ObservableCollection<PointMarker> _editorPoints = new();

        // ── State ──────────────────────────────────────────────────────────────
        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private readonly ChunkPromptCacheService _promptCache = new();
        private readonly Dictionary<int, string> _chunkPrompts = new();
        private readonly ObservableCollection<WanAnimateQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;
        private CancellationTokenSource? _analyzeCts;

        public event EventHandler<TimeSpan>? SeekRequested;

        public WanAnimateViewModel(
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
            SelectFaceImageCommand = new RelayCommand(SelectFaceImage);
            SelectVideoCommand = new RelayCommand(SelectVideo);
            GenerateVideoCommand = new RelayCommand(AddAllChunksToQueue, () => CanAddToQueue);
            ProcessSelectedChunkCommand = new RelayCommand(AddSelectedChunkToQueue, () => CanAddToQueue && _chunkItems.Any());
            SelectChunkCommand = new RelayCommand<WanScailChunkItem>(OnChunkSelected);
            RemoveQueueItemCommand = new RelayCommand<WanAnimateQueueItem>(RemoveQueueItem);
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
            ClearPointsCommand = new RelayCommand(ClearPoints);

            _queue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
                OnCanExecuteChanged();
            };

            AddLog("WAN Animate initialized");
            LoadQueueFromFile();
        }

        #region Commands

        public ICommand SelectCharacterImageCommand { get; }
        public ICommand SelectFaceImageCommand { get; }
        public ICommand SelectVideoCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand ProcessSelectedChunkCommand { get; }
        public RelayCommand<WanScailChunkItem> SelectChunkCommand { get; }
        public RelayCommand<WanAnimateQueueItem> RemoveQueueItemCommand { get; }
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
        public RelayCommand ClearPointsCommand { get; }

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

        public string FaceImagePath
        {
            get => _faceImagePath;
            set
            {
                if (_faceImagePath != value)
                {
                    _faceImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasFaceImage));
                    LoadFaceImagePreview();
                }
            }
        }

        public BitmapImage? FaceImagePreview
        {
            get => _faceImagePreview;
            set { _faceImagePreview = value; OnPropertyChanged(); }
        }

        public string FaceImageInfo
        {
            get => _faceImageInfo;
            set { if (_faceImageInfo != value) { _faceImageInfo = value; OnPropertyChanged(); } }
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
        public bool HasFaceImage => !string.IsNullOrEmpty(FaceImagePath) && File.Exists(FaceImagePath);
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

        #region Points Editor Properties

        public string? EditorFramePath
        {
            get => _editorFramePath;
            private set { if (_editorFramePath != value) { _editorFramePath = value; OnPropertyChanged(); } }
        }

        public ObservableCollection<PointMarker> EditorPoints => _editorPoints;

        public string PointsJson => BuildPointsJson();

        private string BuildPointsJson()
        {
            var pos = _editorPoints.Where(p => p.IsPositive).Select(p => new { x = p.Wx, y = p.Wy }).ToList();
            var neg = _editorPoints.Where(p => !p.IsPositive).Select(p => new { x = p.Wx, y = p.Wy }).ToList();
            if (!pos.Any()) pos.Add(new { x = 256, y = 256 });
            if (!neg.Any()) neg.Add(new { x = 0, y = 0 });
            return JsonSerializer.Serialize(new { positive = pos, negative = neg });
        }

        public void AddPoint(double canvasX, double canvasY, double canvasW, double canvasH, bool isPositive)
        {
            if (canvasW <= 0 || canvasH <= 0) return;
            int wx = (int)Math.Clamp(canvasX / canvasW * 640, 0, 639);
            int wy = (int)Math.Clamp(canvasY / canvasH * 640, 0, 639);
            var marker = new PointMarker
            {
                Wx = wx, Wy = wy,
                IsPositive = isPositive,
                Cx = canvasX - 6,
                Cy = canvasY - 6,
            };
            _editorPoints.Add(marker);
            OnPropertyChanged(nameof(PointsJson));
        }

        public void RemoveNearestPoint(double canvasX, double canvasY, double canvasW, double canvasH)
        {
            if (!_editorPoints.Any() || canvasW <= 0) return;
            int wx = (int)Math.Clamp(canvasX / canvasW * 640, 0, 639);
            int wy = (int)Math.Clamp(canvasY / canvasH * 640, 0, 639);
            var nearest = _editorPoints.OrderBy(p => Math.Pow(p.Wx - wx, 2) + Math.Pow(p.Wy - wy, 2)).First();
            // Only remove if within 20 px in workflow space
            if (Math.Sqrt(Math.Pow(nearest.Wx - wx, 2) + Math.Pow(nearest.Wy - wy, 2)) < 20)
            {
                _editorPoints.Remove(nearest);
                OnPropertyChanged(nameof(PointsJson));
            }
        }

        public void UpdatePointDisplayPositions(double canvasW, double canvasH)
        {
            if (canvasW <= 0 || canvasH <= 0) return;
            foreach (var p in _editorPoints)
            {
                p.Cx = p.Wx * canvasW / 640.0 - 6;
                p.Cy = p.Wy * canvasH / 640.0 - 6;
            }
        }

        private void ClearPoints()
        {
            _editorPoints.Clear();
            OnPropertyChanged(nameof(PointsJson));
        }

        #endregion

        #region Queue Properties

        public ObservableCollection<WanAnimateQueueItem> Queue => _queue;

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
                initialDirectory);

            if (filePath != null)
            {
                CharacterImagePath = filePath;
                AddLog($"WAN Animate: Selected character image: {Path.GetFileName(filePath)}");
            }
        }

        private async void SelectFaceImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Face Image (optional — overrides Character Image as the reference)",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                FaceImagePath = filePath;
                AddLog($"WAN Animate: Selected face image: {Path.GetFileName(filePath)}");
            }
        }

        private async void SelectVideo()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Video (provides motion)",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.webm|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                InputVideoPath = filePath;
                AddLog($"WAN Animate: Selected video: {Path.GetFileName(filePath)}");
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
                EditorFramePath = null;
                return;
            }

            var fi = new FileInfo(InputVideoPath);
            InputVideoInfo = $"{fi.Name} • {fi.Length / 1024 / 1024:F1}MB";
            VideoFileUri = InputVideoPath;
            ChunkSelectionInfo = "Analyzing video…";
            HasVideoInfo = false;

            var path = InputVideoPath;
            var duration = await Task.Run(() => GetVideoDuration(path));
            var frameCount = duration > 0 && Fps > 0
                ? (int)Math.Floor(duration * Fps)
                : await Task.Run(() => GetVideoFrameCount(path));

            if (path != InputVideoPath) return;

            TotalFrames = frameCount > 0 ? frameCount : 0;

            var estimatedDuration = duration > 0 ? duration : (Fps > 0 && TotalFrames > 0 ? (double)TotalFrames / Fps : 0);
            VideoDuration = estimatedDuration > 0 ? $"{estimatedDuration:F1}s" : "—";
            VideoFpsDisplay = Fps.ToString();
            VideoFrameCountDisplay = TotalFrames > 0 ? TotalFrames.ToString("N0") : "—";
            VideoChunksDisplay = TotalChunks > 0 ? $"{TotalChunks} chunk{(TotalChunks != 1 ? "s" : "")}" : "—";
            HasVideoInfo = TotalFrames > 0;

            BuildChunkTimeline();
            OnPropertyChanged(nameof(CanAddToQueue));
            OnCanExecuteChanged();

            AddLog($"Video analyzed: {TotalFrames} frames → {TotalChunks} chunks of {FramesPerChunk}");

            // Extract first frame for the points editor
            _ = ExtractEditorFrameAsync(InputVideoPath, 0);
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
                AddLog($"Loaded {cached.Count} cached chunk prompt(s)");

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

            if (Fps > 0 && TotalFrames > 0)
                SeekRequested?.Invoke(this, TimeSpan.FromSeconds((double)(index * FramesPerChunk) / Fps));

            // Update the points editor background to this chunk's start frame
            if (!string.IsNullOrEmpty(InputVideoPath))
                _ = ExtractEditorFrameAsync(InputVideoPath, index * FramesPerChunk);
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
                    chunk.IsSelected = chunk.Index == _selectedChunkIndex;
                }
            });
        }

        #endregion

        #region Points Editor Frame Extraction

        private async Task ExtractEditorFrameAsync(string videoPath, int frameIndex)
        {
            var ffmpegPath = FindFFmpeg();
            if (ffmpegPath == null) return;

            var fps = Fps > 0 ? (double)Fps : 16.0;
            var timeSec = frameIndex / fps;
            var tempFile = Path.Combine(Path.GetTempPath(), $"wananimate_editor_{Guid.NewGuid():N}.jpg");

            try
            {
                await Task.Run(() =>
                {
                    var si = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = $"-loglevel quiet -nostats -ss {timeSec:F3} -i \"{videoPath}\" " +
                                    $"-frames:v 1 -q:v 2 -vf \"scale=640:640:force_original_aspect_ratio=decrease,pad=640:640:(ow-iw)/2:(oh-ih)/2\" " +
                                    $"\"{tempFile}\" -y",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var proc = Process.Start(si);
                    proc?.WaitForExit(15000);
                });

                if (File.Exists(tempFile))
                {
                    // Clean up previous temp frame
                    var prev = EditorFramePath;
                    EditorFramePath = tempFile;
                    if (!string.IsNullOrEmpty(prev) && prev != tempFile)
                        try { File.Delete(prev); } catch { }
                }
            }
            catch
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
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
                    MessageBox.Show("No LM Studio model available. Please ensure LM Studio is running.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AddLog($"=== WAN Animate analysis started (model: {selectedModel}) ===");

                const string appearanceSystemPrompt =
                    "Describe only what the person is wearing and their physical appearance in this image. " +
                    "Include clothing (style and colors), hairstyle, and body type. " +
                    "Do not describe what they are doing, their pose, or any action. " +
                    "Write one or two sentences. Output only the description as plain text — no labels, no headers, no markdown.";

                var imageForAnalysis = HasFaceImage ? FaceImagePath : CharacterImagePath;
                AddLog("Step 1: Extracting character appearance…");
                var appearanceRaw = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel, imageForAnalysis, "Describe this character's appearance.",
                    appearanceSystemPrompt, maxTokens: 2000, cancellationToken: token);

                var appearanceDescription = CleanLLMOutput(appearanceRaw);
                if (string.IsNullOrWhiteSpace(appearanceDescription))
                {
                    AddLog("ERROR: Could not extract character appearance");
                    return;
                }
                AddLog($"Appearance: {(appearanceDescription.Length > 120 ? appearanceDescription[..120] + "…" : appearanceDescription)}");

                const string motionSystemPrompt =
                    "Describe only the body movement and actions visible in these sequential video frames. " +
                    "Focus on what the person is doing: their poses, gestures, and direction of motion. " +
                    "Do not mention clothing, hair, or appearance. " +
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
                        if (framePaths.Count == 0) { AddLog($"Chunk {i + 1}: no frames extracted, skipping"); continue; }

                        var motionRaw = await _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                            selectedModel, framePaths, "Describe the movement in these video frames.",
                            motionSystemPrompt, maxTokens: 2000, cancellationToken: token);

                        var motionDescription = CleanLLMOutput(motionRaw);
                        if (string.IsNullOrWhiteSpace(motionDescription)) { AddLog($"Chunk {i + 1}: empty motion, skipping"); continue; }

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

                        AddLog($"Chunk {i + 1} motion: {(motionDescription.Length > 100 ? motionDescription[..100] + "…" : motionDescription)}");
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { AddLog($"Chunk {i + 1} failed: {ex.Message}"); }
                    finally { foreach (var f in framePaths) try { File.Delete(f); } catch { } }
                }

                AnalyzeAllChunksStatus = token.IsCancellationRequested
                    ? $"Cancelled — {done}/{totalChunks} chunks analyzed"
                    : $"Done — {done}/{totalChunks} chunks analyzed";
                OnPropertyChanged(nameof(AnalyzeAllChunksStatus));
                AddLog($"=== WAN Animate analysis complete: {done}/{totalChunks} chunks ===");
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

            var fps = Fps > 0 ? (double)Fps : 16.0;
            var startFrame = chunkIndex * FramesPerChunk;
            var endFrame = Math.Min(startFrame + FramesPerChunk - 1, TotalFrames - 1);
            int numFrames = Math.Min(4, endFrame - startFrame + 1);

            for (int f = 0; f < numFrames; f++)
            {
                if (token.IsCancellationRequested) break;
                var frameIdx = numFrames == 1 ? startFrame
                    : startFrame + (int)Math.Round((double)(endFrame - startFrame) * f / (numFrames - 1));
                var timeSec = frameIdx / fps;
                var tempFile = Path.Combine(Path.GetTempPath(), $"wananimate_frame_{Guid.NewGuid():N}.jpg");

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

        private void LoadFaceImagePreview()
        {
            if (string.IsNullOrEmpty(FaceImagePath) || !File.Exists(FaceImagePath))
            {
                FaceImagePreview = null;
                FaceImageInfo = string.Empty;
                return;
            }
            try
            {
                var bmp = LoadBitmapFrozen(FaceImagePath);
                FaceImagePreview = bmp;
                var fi = new FileInfo(FaceImagePath);
                FaceImageInfo = $"{bmp.PixelWidth}x{bmp.PixelHeight} • {fi.Length / 1024}KB";
                AddLog($"Face image loaded: {bmp.PixelWidth}x{bmp.PixelHeight}");
            }
            catch (Exception ex) { AddLog($"Error loading face image: {ex.Message}"); FaceImageInfo = "Error loading image"; }
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
            var item = new WanAnimateQueueItem
            {
                CharacterImagePath = CharacterImagePath,
                FaceImagePath = FaceImagePath,
                InputVideoPath = InputVideoPath,
                Prompt = effectivePrompt,
                NegativePrompt = NegativePrompt,
                Fps = Fps,
                Steps = Steps,
                Seed = Seed,
                SingleChunkIndex = singleChunkIndex,
                PointsJson = PointsJson,
                ItemStatus = QueueItemStatus.Pending,
            };

            _queue.Add(item);
            SaveQueueToFile();
            var desc = singleChunkIndex.HasValue ? $"chunk {singleChunkIndex.Value + 1}" : "all chunks";
            AddLog($"Added to WAN Animate queue ({desc}): {item.DisplayText}");
            UpdateQueueStatus();

            if (!IsProcessingQueue) _ = ProcessQueueAsync();
        }

        private void RemoveQueueItem(WanAnimateQueueItem? item)
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
            AddLog("WAN Animate queue cleared");
        }

        private void StopQueue() { _queueCts?.Cancel(); AddLog("WAN Animate queue stop requested"); }

        private async Task ReprocessAllFailedAsync()
        {
            var failed = _queue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (!failed.Any()) return;
            foreach (var item in failed) item.ItemStatus = QueueItemStatus.Pending;
            UpdateQueueStatus();
            SaveQueueToFile();
            AddLog($"Reprocessing {failed.Count} failed WAN Animate item(s)...");
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
                lease = await _workflowCoordinator.AcquireAsync("WanAnimate", token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue cancelled while waiting");
                IsProcessingQueue = false;
                OnCanExecuteChanged();
                return;
            }

            AddLog("Starting WAN Animate queue processing...");
            using (lease)
            try
            {
                WanAnimateQueueItem? item;
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
                AddLog("WAN Animate queue processing finished.");
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
                var items = JsonSerializer.Deserialize<List<WanAnimateQueueItem>>(File.ReadAllText(QueueFilePath));
                if (items?.Any() != true) return;
                _queue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Processing) item.ItemStatus = QueueItemStatus.Pending;
                    _queue.Add(item);
                }
                UpdateQueueStatus();
                AddLog($"WAN Animate queue loaded: {_queue.Count} items");
                var pending = _queue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
                if (pending > 0 && !IsProcessingQueue) _ = ProcessQueueAsync();
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        #endregion

        #region Video Generation

        private async Task GenerateSingleVideoAsync(WanAnimateQueueItem item)
        {
            try
            {
                AddLog($"=== WAN Animate generation: {item.DisplayText} ===");
                IsProcessing = true;
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing WAN Animate workflow...";

                // Determine chunk range
                var videoFrameCount = TotalFrames > 0 ? TotalFrames : GetVideoFrameCount(item.InputVideoPath);
                if (videoFrameCount <= 0) videoFrameCount = FramesPerChunk;
                TotalFrames = videoFrameCount;

                int startChunk = item.SingleChunkIndex ?? 0;
                int endChunk = item.SingleChunkIndex.HasValue ? item.SingleChunkIndex.Value + 1 : TotalChunks;

                AddLog($"Processing chunks {startChunk + 1}–{endChunk} of {TotalChunks}");

                // ComfyUI connection
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
                if (!File.Exists(workflowPath)) throw new FileNotFoundException($"WAN Animate workflow not found: {workflowPath}");

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                ProcessingStatus = "Uploading assets...";
                ProcessingProgress = 10;

                // Compute aspect-ratio-correct resolution (480p short-edge for RTX 2x upscale)
                var (videoW, videoH) = GetVideoDimensions(item.InputVideoPath);
                int outputWidth = 640, outputHeight = 640;
                if (videoW > 0 && videoH > 0)
                {
                    (outputWidth, outputHeight) = ComputeOutputResolution(videoW, videoH);
                    AddLog($"Output resolution: {outputWidth}x{outputHeight}");
                }

                // Determine which image to use as the reference (face override takes priority)
                string referenceImageToUpload = !string.IsNullOrEmpty(item.FaceImagePath) && File.Exists(item.FaceImagePath)
                    ? item.FaceImagePath
                    : item.CharacterImagePath;

                // Crop reference image to match output aspect ratio
                string imageToUpload = referenceImageToUpload;
                string? croppedTemp = null;
                if (outputWidth > 0 && outputHeight > 0)
                {
                    croppedTemp = CropImageToAspectRatio(referenceImageToUpload, outputWidth, outputHeight);
                    if (croppedTemp != null) imageToUpload = croppedTemp;
                }

                AddLog("Uploading reference image...");
                var uploadedImageName = await _comfyUIService.UploadImageAsync(imageToUpload);
                if (!string.IsNullOrEmpty(croppedTemp)) try { File.Delete(croppedTemp); } catch { }
                if (string.IsNullOrEmpty(uploadedImageName)) throw new Exception("Failed to upload reference image.");
                AddLog($"Reference image uploaded: {uploadedImageName}");

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
                        var startFrame = chunkIndex * FramesPerChunk;
                        var relativeChunk = chunkIndex - startChunk + 1;
                        AddLog($"=== Chunk {chunkIndex + 1} (job {relativeChunk}/{numChunks}): frame offset {startFrame} ===");
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
                            startFrame, chunkPrompt, item.NegativePrompt, item.Fps, item.Steps,
                            outputWidth, outputHeight, runSeed + chunkIndex, item.PointsJson);

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
                            var chunkFile = Path.Combine(Path.GetTempPath(),
                                $"wananimate_chunk_{chunkIndex:D3}_{Path.GetFileName(outputVideo)}");
                            File.Copy(outputVideo, chunkFile, true);
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
                        _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "WanAnimate");
                    Directory.CreateDirectory(outputDir);
                    var finalPath = Path.Combine(outputDir, $"WanAnimate_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                    if (chunkFiles.Count == 1) File.Copy(chunkFiles[0], finalPath, true);
                    else MergeVideoChunksWithFFmpeg(chunkFiles, finalPath);

                    foreach (var f in chunkFiles) try { File.Delete(f); } catch { }

                    item.OutputVideoPath = finalPath;
                    ResultVideoPath = finalPath;
                    await LocalCopyService.CopyVideoAsync(finalPath);
                    HasResult = true;
                    ResultVideoInfo = $"WAN Animate Video • {new FileInfo(finalPath).Length / 1024 / 1024:F1}MB";
                    ProcessingProgress = 100;
                    ProcessingStatus = "WAN Animate Complete!";
                    AddLog($"=== WAN Animate complete: {finalPath} ===");
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
            // 480p short-edge — RTX VSR doubles to 960p
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
            int videoFrameOffset, string prompt, string negativePrompt,
            int fps, int steps, int width, int height, long seed, string pointsJson)
        {
            var wfJson = baseWorkflow.GetRawText();

            // Parse point data from the JSON string
            string coordinates = "[{\"x\":256,\"y\":256}]";
            string negCoordinates = "[{\"x\":0,\"y\":0}]";
            try
            {
                var doc = JsonDocument.Parse(pointsJson);
                var posArr = doc.RootElement.GetProperty("positive");
                var negArr = doc.RootElement.GetProperty("negative");
                coordinates = JsonSerializer.Serialize(posArr);
                negCoordinates = JsonSerializer.Serialize(negArr);
            }
            catch { /* use defaults */ }

            // Node 10: reference image
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "10", "image", imageName);

            // Node 145: reference video
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "145", "file", videoName);

            // Node 21: positive prompt
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "21", "text", prompt);

            // Node 1: negative prompt
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "1", "text", negativePrompt);

            // Nodes 159/160: output width/height
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "159", "value", width);
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "160", "value", height);

            // Node 229: PointsEditor — SAM2 segmentation points.
            // Coordinates are always in 640×640 workflow space (see AddPoint), so keep
            // width/height at 640 so the PointsEditor interprets them correctly.
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref wfJson, "229", new Dictionary<string, object>
            {
                { "points_store", pointsJson },
                { "coordinates", coordinates },
                { "neg_coordinates", negCoordinates },
                { "width", 640 },
                { "height", 640 },
            });

            // Node 301: trim the full frame batch to just this chunk's 154 frames (77×2 passes)
            // so DWPose, SAM2 and DrawMask only process the frames they actually need.
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "301", "batch_index", videoFrameOffset);

            // Node 232:62: video is pre-trimmed to start at frame 0
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "232:62", "video_frame_offset", 0);

            // Node 232:63: KSampler seed + steps
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "232:63", "seed", seed);
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "232:63", "steps", steps);

            // Node 242:91: continuation KSampler seed + steps
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "242:91", "seed", seed + 1);
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "242:91", "steps", steps);

            // Nodes 232:15 / 242:88: CreateVideo — set FPS from user setting
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "232:15", "fps", fps);
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "242:88", "fps", fps);

            // Node 243: final SaveVideo — route to our output subfolder
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "243", "filename_prefix", $"video/{OutputSubfolder}/WanAnimate");

            // Node 19: intermediate segment — route to temp subfolder
            WorkflowNodeUpdater.UpdateNodeInput(ref wfJson, "19", "filename_prefix", $"video/{TmpOutputSubfolder}/Seg1");

            AddLog($"Workflow updated: offset={videoFrameOffset}, res={width}x{height}, fps={fps}, steps={steps}, seed={seed}");
            return JsonSerializer.Deserialize<JsonElement>(wfJson);
        }

        private void MergeVideoChunksWithFFmpeg(List<string> chunkFiles, string outputPath)
        {
            var ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
                throw new InvalidOperationException("ffmpeg is required to merge video chunks but was not found.");

            var listFile = Path.Combine(Path.GetTempPath(), $"ffmpeg_wananimate_{Guid.NewGuid()}.txt");
            using (var writer = new StreamWriter(listFile))
                foreach (var f in chunkFiles)
                    writer.WriteLine($"file '{f.Replace("\\", "/")}'");

            AddLog($"Merging {chunkFiles.Count} chunks with ffmpeg...");
            var si = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-f concat -safe 0 -i \"{listFile}\" -c copy \"{outputPath}\"",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var proc = Process.Start(si);
            if (proc == null) throw new InvalidOperationException("Failed to start ffmpeg.");
            proc.WaitForExit(120000);
            try { File.Delete(listFile); } catch { }
            if (!File.Exists(outputPath)) throw new InvalidOperationException($"ffmpeg merge failed. Output not found: {outputPath}");
            AddLog($"Merge complete: {Path.GetFileName(outputPath)}");
        }

        #endregion

        private static string CleanLLMOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = text.Replace("**", "");
            var trimmed = text.TrimStart();
            var lower = trimmed.ToLowerInvariant();
            if (lower.StartsWith("prompt:") || lower.StartsWith("prompt :"))
                trimmed = trimmed[(trimmed.IndexOf(':') + 1)..];
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
