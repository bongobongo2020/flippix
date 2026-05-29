using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using FlipPix.UI.Linux.Models;
using FlipPix.UI.Linux.Services;

namespace FlipPix.UI.Linux.ViewModels.Video
{
    /// <summary>
    /// ViewModel for WanAnimate video generation.
    /// Uses the "Wan Animate + Steady Dancer + OneToAll Animation + SCAIL.json" workflow.
    /// Accepts a reference image, face image, and input video.
    /// Processes the video in 81-frame chunks using ffmpeg to calculate total frame count,
    /// then concatenates all chunks into a single final video.
    /// </summary>
    public partial class WanAnimateViewModel : VideoProcessingBaseViewModel
    {
        private const int FramesPerChunk = 81;
        private const string WorkflowFileName = "Wan Animate + Steady Dancer + OneToAll Animation + SCAIL.json";
        private const string OutputSubfolder = "wan_animate";

        private string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "wan_animate_queue.json");

        // Input fields
        private string _referenceImagePath = string.Empty;
        private BitmapImage? _referenceImagePreview;
        private string _referenceImageInfo = string.Empty;

        private string _faceImagePath = string.Empty;
        private BitmapImage? _faceImagePreview;
        private string _faceImageInfo = string.Empty;

        private string _inputVideoPath = string.Empty;
        private string _inputVideoInfo = string.Empty;

        private string _prompt = string.Empty;
        private string _negativePrompt = "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";
        private int _outputWidth = 640;
        private int _outputHeight = 1024;
        private int _totalFrames;
        private bool _isAnalyzing;
        private bool _isProcessingQueue;
        private string _queueStatus = string.Empty;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private readonly ObservableCollection<WanAnimateQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;

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

            SelectReferenceImageCommand = new RelayCommand(SelectReferenceImage);
            SelectFaceImageCommand = new RelayCommand(SelectFaceImage);
            SelectVideoCommand = new RelayCommand(SelectVideo);
            GenerateVideoCommand = new RelayCommand(AddToQueueAndProcess, () => CanAddToQueue);
            RemoveQueueItemCommand = new RelayCommand<WanAnimateQueueItem>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => _queue.Any());
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(async () => await ReprocessAllFailedAsync(), () => HasFailedItems);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);
            AnalyzeImageCommand = new RelayCommand(async () => await AnalyzeImageAsync(), () => CanAnalyzeImage);

            _queue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
                OnCanExecuteChanged();
            };

            AddLog("WanAnimate Video Generator initialized");
            LoadQueueFromFile();
        }

        #region Commands

        public ICommand SelectReferenceImageCommand { get; }
        public ICommand SelectFaceImageCommand { get; }
        public ICommand SelectVideoCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand<WanAnimateQueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }
        public RelayCommand AnalyzeImageCommand { get; }

        public bool HasFailedItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        #endregion

        #region Input Properties

        public string ReferenceImagePath
        {
            get => _referenceImagePath;
            set
            {
                if (_referenceImagePath != value)
                {
                    _referenceImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasReferenceImage));
                    OnPropertyChanged(nameof(CanAddToQueue));
                    OnPropertyChanged(nameof(CanAnalyzeImage));
                    LoadReferenceImagePreview();
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? ReferenceImagePreview
        {
            get => _referenceImagePreview;
            set { _referenceImagePreview = value; OnPropertyChanged(); }
        }

        public string ReferenceImageInfo
        {
            get => _referenceImageInfo;
            set { if (_referenceImageInfo != value) { _referenceImageInfo = value; OnPropertyChanged(); } }
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
                    OnPropertyChanged(nameof(CanAddToQueue));
                    LoadFaceImagePreview();
                    OnCanExecuteChanged();
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
                    LoadVideoInfo();
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

        public int OutputWidth
        {
            get => _outputWidth;
            set { if (_outputWidth != value) { _outputWidth = value; OnPropertyChanged(); } }
        }

        public int OutputHeight
        {
            get => _outputHeight;
            set { if (_outputHeight != value) { _outputHeight = value; OnPropertyChanged(); } }
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

        public bool HasReferenceImage => !string.IsNullOrEmpty(ReferenceImagePath) && File.Exists(ReferenceImagePath);
        public bool HasFaceImage => !string.IsNullOrEmpty(FaceImagePath) && File.Exists(FaceImagePath);
        public bool HasInputVideo => !string.IsNullOrEmpty(InputVideoPath) && File.Exists(InputVideoPath);

        public bool CanAddToQueue => HasReferenceImage && HasFaceImage && HasInputVideo && !string.IsNullOrWhiteSpace(Prompt);
        public bool CanAnalyzeImage => HasReferenceImage && !IsAnalyzing && !IsProcessing;

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
                    OnCanExecuteChanged();
                }
            }
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

        private async void SelectReferenceImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                ReferenceImagePath = filePath;
                AddLog($"WanAnimate: Selected reference image: {Path.GetFileName(filePath)}");
            }
        }

        private async void SelectFaceImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Face Reference Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                FaceImagePath = filePath;
                AddLog($"WanAnimate: Selected face image: {Path.GetFileName(filePath)}");
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
                initialDirectory);

            if (filePath != null)
            {
                InputVideoPath = filePath;
                AddLog($"WanAnimate: Selected video: {Path.GetFileName(filePath)}");
            }
        }

        private async Task AnalyzeImageAsync()
        {
            if (!CanAnalyzeImage) return;
            try
            {
                IsAnalyzing = true;
                AddLog("=== Analyzing reference image with LM Studio ===");

                var models = await _lmStudioService.GetAvailableModelsAsync();
                string selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;

                if (string.IsNullOrEmpty(selectedModel) && models.Count > 0)
                {
                    var m = models.First();
                    selectedModel = !string.IsNullOrEmpty(m.Name) ? m.Name : m.Id;
                }

                if (string.IsNullOrEmpty(selectedModel))
                {
                    AddLog("ERROR: No LM Studio model available");
                    System.Windows.MessageBox.Show(
                        "No LM Studio model available. Please ensure LM Studio is running and a model is loaded.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AddLog($"Sending image to LM Studio (model: {selectedModel})...");
                var systemPrompt = "Describe the person, their pose, the scene, and the atmosphere in detail. Focus on describing the movement and action. Output a prompt suitable for WanAnimate video generation, describing how the person should move and animate.";

                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel,
                    ReferenceImagePath,
                    "Analyze this image and generate a motion description prompt.",
                    systemPrompt);

                Prompt = result;
                AddLog("Image analysis complete. Prompt updated.");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR during image analysis: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"Image analysis failed:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
            }
        }

        #endregion

        #region Preview Loading

        private void LoadReferenceImagePreview()
        {
            if (string.IsNullOrEmpty(ReferenceImagePath) || !File.Exists(ReferenceImagePath))
            {
                ReferenceImagePreview = null;
                ReferenceImageInfo = string.Empty;
                return;
            }
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(ReferenceImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                ReferenceImagePreview = bitmap;
                var fi = new FileInfo(ReferenceImagePath);
                ReferenceImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fi.Length / 1024}KB";

                // Auto-detect output dimensions from reference image aspect ratio
                double ar = (double)bitmap.PixelWidth / bitmap.PixelHeight;
                if (ar > 1.2) { OutputWidth = 832; OutputHeight = 480; }
                else if (ar >= 0.85) { OutputWidth = 704; OutputHeight = 704; }
                else { OutputWidth = 640; OutputHeight = 1024; }
                AddLog($"Auto-set output dimensions: {OutputWidth}x{OutputHeight} (AR: {ar:F2})");
            }
            catch (Exception ex)
            {
                AddLog($"Error loading reference image preview: {ex.Message}");
                ReferenceImageInfo = "Error loading image";
            }
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
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(FaceImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                FaceImagePreview = bitmap;
                var fi = new FileInfo(FaceImagePath);
                FaceImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fi.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading face image preview: {ex.Message}");
                FaceImageInfo = "Error loading image";
            }
        }

        private void LoadVideoInfo()
        {
            if (string.IsNullOrEmpty(InputVideoPath) || !File.Exists(InputVideoPath))
            {
                InputVideoInfo = string.Empty;
                return;
            }
            try
            {
                var fi = new FileInfo(InputVideoPath);
                InputVideoInfo = $"{fi.Name} • {fi.Length / 1024 / 1024:F1}MB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading video info: {ex.Message}");
                InputVideoInfo = "Error loading video info";
            }
        }

        #endregion

        #region Queue Management

        private void AddToQueueAndProcess()
        {
            if (!CanAddToQueue) return;

            var item = new WanAnimateQueueItem
            {
                ReferenceImagePath = ReferenceImagePath,
                FaceImagePath = FaceImagePath,
                InputVideoPath = InputVideoPath,
                Prompt = Prompt,
                NegativePrompt = NegativePrompt,
                ItemStatus = QueueItemStatus.Pending
            };

            _queue.Add(item);
            SaveQueueToFile();
            AddLog($"Added to WanAnimate queue: {item.DisplayText}");
            UpdateQueueStatus();

            if (!IsProcessingQueue)
                _ = ProcessQueueAsync();
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
            AddLog("WanAnimate queue cleared");
        }

        private void StopQueue()
        {
            _queueCts?.Cancel();
            AddLog("WanAnimate queue stop requested");
        }

        private async Task ReprocessAllFailedAsync()
        {
            var failed = _queue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (!failed.Any()) return;

            foreach (var item in failed)
                item.ItemStatus = QueueItemStatus.Pending;

            UpdateQueueStatus();
            SaveQueueToFile();
            AddLog($"Reprocessing {failed.Count} failed WanAnimate item(s)...");

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
                lease = await _workflowCoordinator.AcquireAsync("WanAnimate", token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue processing cancelled while waiting");
                IsProcessingQueue = false;
                OnCanExecuteChanged();
                return;
            }

            AddLog("Starting WanAnimate queue processing...");
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
                        AddLog("WanAnimate queue item cancelled — reset to Pending");
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
                AddLog("WanAnimate queue processing finished.");
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
                var items = JsonSerializer.Deserialize<List<WanAnimateQueueItem>>(File.ReadAllText(QueueFilePath));
                if (items?.Any() != true) return;
                _queue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Processing)
                        item.ItemStatus = QueueItemStatus.Pending;
                    _queue.Add(item);
                }
                UpdateQueueStatus();
                AddLog($"WanAnimate queue loaded: {_queue.Count} items");
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        #endregion

        #region Video Generation

        private async Task GenerateSingleVideoAsync(WanAnimateQueueItem item)
        {
            try
            {
                AddLog($"=== Starting WanAnimate generation: {item.DisplayText} ===");
                IsProcessing = true;

                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing WanAnimate workflow...";

                AddLog($"Reference image: {Path.GetFileName(item.ReferenceImagePath)}");
                AddLog($"Face image: {Path.GetFileName(item.FaceImagePath)}");
                AddLog($"Input video: {Path.GetFileName(item.InputVideoPath)}");
                AddLog($"Prompt: {item.Prompt}");

                // Calculate total frames via ffmpeg
                ProcessingStatus = "Analysing input video...";
                TotalFrames = GetVideoFrameCount(item.InputVideoPath);
                if (TotalFrames <= 0)
                {
                    AddLog("WARNING: Could not determine frame count; defaulting to 1 chunk");
                    TotalFrames = FramesPerChunk;
                }
                AddLog($"Total frames: {TotalFrames} → {TotalChunks} chunk(s) of {FramesPerChunk}");

                // ComfyUI health check
                ProcessingStatus = "Checking ComfyUI status...";
                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddLog($"[Auto-Restart] {status}"));

                if (!comfyUIOk)
                {
                    AddLog("ERROR: ComfyUI is not running");
                    System.Windows.MessageBox.Show(
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

                // Load workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", WorkflowFileName);
                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    throw new FileNotFoundException($"WanAnimate workflow file not found: {workflowPath}");
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Upload assets
                ProcessingStatus = "Uploading assets to ComfyUI...";
                ProcessingProgress = 10;

                AddLog("Uploading reference image...");
                var uploadedRefImageName = await _comfyUIService.UploadImageAsync(item.ReferenceImagePath);
                if (string.IsNullOrEmpty(uploadedRefImageName))
                    throw new Exception("Failed to upload reference image to ComfyUI.");
                AddLog($"Reference image uploaded: {uploadedRefImageName}");

                AddLog("Uploading face image...");
                var uploadedFaceImageName = await _comfyUIService.UploadImageAsync(item.FaceImagePath);
                if (string.IsNullOrEmpty(uploadedFaceImageName))
                    throw new Exception("Failed to upload face image to ComfyUI.");
                AddLog($"Face image uploaded: {uploadedFaceImageName}");

                AddLog("Uploading video...");
                var uploadedVideoName = await _comfyUIService.UploadVideoAsync(item.InputVideoPath);
                if (string.IsNullOrEmpty(uploadedVideoName))
                    throw new Exception("Failed to upload video to ComfyUI.");
                AddLog($"Video uploaded: {uploadedVideoName}");

                // Determine output dimensions from reference image
                int outputWidth = OutputWidth;
                int outputHeight = OutputHeight;
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(item.ReferenceImagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    double ar = (double)bitmap.PixelWidth / bitmap.PixelHeight;
                    if (ar > 1.2) { outputWidth = 832; outputHeight = 480; }
                    else if (ar >= 0.85) { outputWidth = 704; outputHeight = 704; }
                    else { outputWidth = 640; outputHeight = 1024; }
                    AddLog($"Output dimensions: {outputWidth}x{outputHeight} (AR: {ar:F2})");
                }
                catch (Exception ex)
                {
                    AddLog($"Warning: Could not read image dimensions, using {outputWidth}x{outputHeight}: {ex.Message}");
                }

                // Chunk loop
                var totalChunks = TotalChunks;
                var chunkFiles = new List<string>();
                AddLog($"=== Processing {totalChunks} chunk(s) of {FramesPerChunk} frames ===");

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    try
                    {
                        var startFrame = chunkIndex * FramesPerChunk;
                        var framesInChunk = Math.Min(FramesPerChunk, TotalFrames - startFrame);

                        AddLog($"=== Chunk {chunkIndex + 1}/{totalChunks}: frames {startFrame}–{startFrame + framesInChunk - 1} ===");
                        ProcessingStatus = $"Processing chunk {chunkIndex + 1}/{totalChunks}";
                        var baseProgress = 20.0 + chunkIndex * 60.0 / totalChunks;

                        if (chunkIndex > 0 && !_comfyUIService.IsConnected)
                        {
                            AddLog("Reconnecting to ComfyUI...");
                            await _comfyUIService.ConnectAsync();
                        }

                        var updatedWorkflow = UpdateWorkflowParameters(
                            workflow,
                            uploadedRefImageName,
                            uploadedFaceImageName,
                            uploadedVideoName,
                            startFrame,
                            framesInChunk,
                            outputWidth,
                            outputHeight,
                            item.Prompt,
                            item.NegativePrompt);

                        var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                        {
                            if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                            {
                                var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    ProcessingProgress = baseProgress + percent * 0.6 / totalChunks;
                                    ProcessingStatus = $"Chunk {chunkIndex + 1}/{totalChunks}: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
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
                                TimeSpan.FromMinutes(20),
                                TimeSpan.FromSeconds(5),
                                OutputSubfolder);
                        }

                        if (outputVideo != null && File.Exists(outputVideo))
                        {
                            var chunkFile = Path.Combine(Path.GetTempPath(), $"wananimate_chunk_{chunkIndex:D3}_{Path.GetFileName(outputVideo)}");
                            File.Copy(outputVideo, chunkFile, true);
                            chunkFiles.Add(chunkFile);
                            AddLog($"Chunk {chunkIndex + 1}/{totalChunks} saved: {Path.GetFileName(chunkFile)}");
                        }
                        else
                        {
                            AddLog($"ERROR: No output video for chunk {chunkIndex + 1} — aborting remaining chunks");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"ERROR processing chunk {chunkIndex + 1}: {ex.Message} — aborting remaining chunks");
                        break;
                    }
                }

                // Merge / finalise
                ProcessingProgress = 85;
                ProcessingStatus = "Merging video chunks...";
                AddLog("=== Merging chunks ===");

                if (chunkFiles.Count > 0)
                {
                    var outputDir = Path.Combine(
                        _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(),
                        "WanAnimate");
                    Directory.CreateDirectory(outputDir);

                    var finalPath = Path.Combine(outputDir, $"WanAnimate_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

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
                    ResultVideoInfo = $"WanAnimate Video • {fi.Length / 1024 / 1024:F1}MB";
                    ProcessingProgress = 100;
                    ProcessingStatus = "WanAnimate Complete!";
                    AddLog($"=== WanAnimate generation complete: {finalPath} ===");
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
            string refImageName,
            string faceImageName,
            string videoName,
            int startFrame,
            int framesInChunk,
            int outputWidth,
            int outputHeight,
            string prompt,
            string negativePrompt)
        {
            var workflowJson = workflow.GetRawText();
            AddLog($"Updating workflow: start={startFrame}, frames={framesInChunk}, size={outputWidth}x{outputHeight}");

            // Node 57: Reference identity image (LoadImage "🌐 REFERENCE SECTION")
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "57", "image", refImageName);

            // Node 258: Face reference image (LoadImage "👨‍🦰 REFERENCE SECTION FACE")
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "258", "image", faceImageName);

            // Node 63: Input video — set video name, skip_first_frames, and override frame_load_cap link with direct integer
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "63", new Dictionary<string, object>
            {
                { "video", videoName },
                { "skip_first_frames", startFrame },
                { "frame_load_cap", framesInChunk }
            });

            // Node 314: Number of frames Int node (drives frame_load_cap reference) — keep consistent
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "314", "Number", framesInChunk.ToString());

            // Node 290: Output resolution (SetImageSize)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "290", new Dictionary<string, object>
            {
                { "width", outputWidth },
                { "height", outputHeight }
            });

            // Node 213:666: WanVideo TextEncode — positive and negative prompt
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "213:666", new Dictionary<string, object>
            {
                { "positive_prompt", prompt },
                { "negative_prompt", negativePrompt }
            });

            // Node 213:664: WanVideo Sampler — randomise seed per run
            var seed = (long)(new Random().NextDouble() * 1125899906842624L);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "213:664", "seed", seed);

            AddLog("✓ Workflow nodes updated");
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

            var listFile = Path.Combine(Path.GetTempPath(), $"ffmpeg_wananimate_{Guid.NewGuid()}.txt");
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

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            GenerateVideoCommand.NotifyCanExecuteChanged();
            RemoveQueueItemCommand.NotifyCanExecuteChanged();
            ClearQueueCommand.NotifyCanExecuteChanged();
            StopQueueCommand.NotifyCanExecuteChanged();
            ReprocessAllFailedCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            SendToEditCameraCommand.NotifyCanExecuteChanged();
            AnalyzeImageCommand.NotifyCanExecuteChanged();
        }
    }
}
