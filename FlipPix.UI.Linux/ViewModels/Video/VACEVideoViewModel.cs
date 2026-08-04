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
    /// ViewModel for VACE (Video-to-Video with Control) video generation.
    /// Handles a reference image and input video, processed in 81-frame chunks.
    /// Uses Wan-VACE_V2V_MasterAPI.json workflow. Supports a multi-item queue.
    /// </summary>
    public partial class VACEVideoViewModel : VideoProcessingBaseViewModel
    {
        private const int FramesPerChunk = 81;
        private const string OutputSubfolder = "wan_vace";

        private string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "vace_queue.json");

        private string _prompt = string.Empty;
        private string _foregroundImagePath = string.Empty;
        private BitmapImage? _foregroundImagePreview;
        private string _foregroundImageInfo = string.Empty;
        private string _inputVideoPath = string.Empty;
        private string _inputVideoInfo = string.Empty;
        private int _totalFrames;
        private bool _isAnalyzing = false;
        private bool _isProcessingQueue = false;
        private string _queueStatus = string.Empty;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private readonly ObservableCollection<VaceQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;

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
            GenerateVideoCommand = new RelayCommand(AddToQueueAndProcess, () => CanAddToQueue);
            RemoveQueueItemCommand = new RelayCommand<VaceQueueItem>(RemoveQueueItem);
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

            AddLog("VACE Video Generator initialized");
            LoadQueueFromFile();
        }

        #region Commands

        public ICommand SelectForegroundImageCommand { get; }
        public ICommand SelectVideoCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand<VaceQueueItem> RemoveQueueItemCommand { get; }
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
        public bool CanAddToQueue => HasForegroundImage && HasInputVideo && !string.IsNullOrWhiteSpace(Prompt);

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

        public bool CanAnalyzeImage => HasForegroundImage && !IsAnalyzing && !IsProcessing;

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
                initialDirectory);

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
                initialDirectory);

            if (filePath != null)
            {
                InputVideoPath = filePath;
                AddLog($"VACE: Selected video: {Path.GetFileName(InputVideoPath)}");
            }
        }

        private async Task AnalyzeImageAsync()
        {
            if (!CanAnalyzeImage) return;
            try
            {
                IsAnalyzing = true;
                AddLog("=== Analyzing reference image with LM Studio ===");

                var systemPromptPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "vace-system-prompt.md");

                if (!File.Exists(systemPromptPath))
                {
                    AddLog($"ERROR: System prompt file not found: {systemPromptPath}");
                    System.Windows.MessageBox.Show(
                        $"VACE system prompt file not found:\n{systemPromptPath}",
                        "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                var systemPrompt = await File.ReadAllTextAsync(systemPromptPath);
                AddLog($"System prompt loaded ({systemPrompt.Length} chars)");

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
                        "LM Studio Unavailable", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                AddLog($"Sending image to LM Studio (model: {selectedModel})...");
                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel,
                    ForegroundImagePath,
                    "Analyze this image.",
                    systemPrompt);

                Prompt = result;
                AddLog("Image analysis complete. VACE prompt updated.");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR during image analysis: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"Image analysis failed:\n{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
            }
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
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                ForegroundImageInfo = "Error loading image";
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
                var fileInfo = new FileInfo(InputVideoPath);
                InputVideoInfo = $"{fileInfo.Name} • {fileInfo.Length / 1024 / 1024:F1}MB";
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

            var item = new VaceQueueItem
            {
                ForegroundImagePath = ForegroundImagePath,
                InputVideoPath = InputVideoPath,
                Prompt = Prompt,
                ItemStatus = QueueItemStatus.Pending
            };

            _queue.Add(item);
            SaveQueueToFile();
            AddLog($"Added to queue: {item.DisplayText}");
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

                // Get frame count
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
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "Wan-VACE_V2V_MasterAPI.json");
                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    throw new FileNotFoundException($"VACE workflow file not found: {workflowPath}");
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Upload assets
                ProcessingStatus = "Uploading assets to ComfyUI...";
                ProcessingProgress = 10;

                AddLog("Uploading reference image...");
                var uploadedImageName = await _comfyUIService.UploadImageAsync(item.ForegroundImagePath);
                if (string.IsNullOrEmpty(uploadedImageName))
                {
                    AddLog("ERROR: Reference image upload failed");
                    throw new Exception("Failed to upload reference image to ComfyUI.");
                }
                AddLog($"Reference image uploaded: {uploadedImageName}");

                AddLog("Uploading video...");
                var uploadedVideoName = await _comfyUIService.UploadVideoAsync(item.InputVideoPath);
                if (string.IsNullOrEmpty(uploadedVideoName))
                {
                    AddLog("ERROR: Video upload failed");
                    throw new Exception("Failed to upload video to ComfyUI.");
                }
                AddLog($"Video uploaded: {uploadedVideoName}");

                // Calculate output dimensions from reference image
                int outputWidth = 576, outputHeight = 1024;
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(item.ForegroundImagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    double ar = (double)bitmap.PixelWidth / bitmap.PixelHeight;
                    if (ar > 1.2) { outputWidth = 832; outputHeight = 480; }
                    else if (ar >= 0.85) { outputWidth = 704; outputHeight = 704; }
                    else { outputWidth = 480; outputHeight = 832; }
                    AddLog($"Output dimensions: {outputWidth}x{outputHeight} (AR: {ar:F2})");
                }
                catch (Exception ex)
                {
                    AddLog($"Warning: Could not read image dimensions, using defaults: {ex.Message}");
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

                        var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedImageName, uploadedVideoName,
                            startFrame, framesInChunk, outputWidth, outputHeight, item.Prompt);

                        var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                        {
                            if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                            {
                                var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                {
                                    ProcessingProgress = baseProgress + percent * 0.6 / totalChunks;
                                    ProcessingStatus = $"Chunk {chunkIndex + 1}/{totalChunks}: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                                });
                            }
                        });

                        var existingFiles = GetExistingVideoFiles("*.mp4", OutputSubfolder);
                        var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                        AddLog($"Chunk {chunkIndex + 1} completed, prompt ID: {promptId}");

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

            // Node 10: video input — override frame_load_cap and skip_first_frames
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "10", new Dictionary<string, object>
            {
                { "video", videoName },
                { "frame_load_cap", framesInChunk },
                { "skip_first_frames", startFrame }
            });

            // Node 148: reference image
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "148", "image", imageName);

            // Node 31: prompt
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "31", "string", prompt);

            // Nodes 19/20/21: frames / height / width (INTConstant)
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
