using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ViewModel for LTX 2.3 basic image-to-video generation.
    /// Supports single reference image, AI analysis, prompt enhancement, and a simple auto-processing queue.
    /// </summary>
    public partial class LTX23BasicViewModel : VideoProcessingBaseViewModel
    {
        private string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "ltx23basic_queue.json");

        private string _imagePath = string.Empty;
        private BitmapImage? _imagePreview;
        private string _imageInfo = string.Empty;
        private int _width;
        private int _height;
        private string _prompt = string.Empty;
        private bool _isAnalyzing = false;
        private string _analysisResult = string.Empty;
        private bool _showVideoPrompt = false;
        private bool _isProcessingQueue = false;
        private string _queueStatus = string.Empty;
        private int _frameCount = 240;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private readonly ObservableCollection<QueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;

        public LTX23BasicViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            LMStudioService lmStudioService,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            SelectImageCommand = new RelayCommand(SelectImage);
            AnalyzeImageCommand = new RelayCommand(async () => await AnalyzeImageWithLMStudioAsync(), () => CanAnalyzeImage);
            EnhancePromptCommand = new RelayCommand(async () => await EnhancePromptWithLMStudioAsync(), () => CanEnhancePrompt);
            GenerateVideoCommand = new RelayCommand(AddToQueueAndProcess, () => CanAddToQueue);
            RemoveQueueItemCommand = new RelayCommand<QueueItem>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => _queue.Any());
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(async () => await ReprocessAllFailedAsync(), () => HasFailedItems);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);

            _frameCount = settingsService.Settings?.Ltx23FrameCount ?? 240;

            _queue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
                OnCanExecuteChanged();
            };

            AddLog("LTX 2.3 Basic Video Generator initialized");
            LoadQueueFromFile();
        }

        #region Commands

        public ICommand SelectImageCommand { get; }
        public RelayCommand AnalyzeImageCommand { get; }
        public RelayCommand EnhancePromptCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand<QueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }

        public bool HasFailedItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        #endregion

        #region Input Properties

        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (_imagePath != value)
                {
                    _imagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasImage));
                    OnPropertyChanged(nameof(CanAnalyzeImage));
                    OnPropertyChanged(nameof(CanAddToQueue));
                    LoadImagePreview();
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? ImagePreview
        {
            get => _imagePreview;
            set { _imagePreview = value; OnPropertyChanged(); }
        }

        public string ImageInfo
        {
            get => _imageInfo;
            set { if (_imageInfo != value) { _imageInfo = value; OnPropertyChanged(); } }
        }

        public int Width
        {
            get => _width;
            set
            {
                if (_width != value)
                {
                    _width = value;
                    OnPropertyChanged();
                }
            }
        }

        public int Height
        {
            get => _height;
            set
            {
                if (_height != value)
                {
                    _height = value;
                    OnPropertyChanged();
                }
            }
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

        public int FrameCount
        {
            get => _frameCount;
            set
            {
                var clamped = Math.Max(49, Math.Min(481, value));
                if (_frameCount != clamped)
                {
                    _frameCount = clamped;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FrameCountHint));
                    if (_settingsService.Settings != null)
                    {
                        _settingsService.Settings.Ltx23FrameCount = clamped;
                        _settingsService.SaveSettings(_settingsService.Settings);
                    }
                }
            }
        }

        public string FrameCountHint => $"~{_frameCount / 24.0:F1}s at 24fps";

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
                    OnPropertyChanged(nameof(CanEnhancePrompt));
                    OnCanExecuteChanged();
                }
            }
        }

        public string AnalysisResult
        {
            get => _analysisResult;
            set
            {
                if (_analysisResult != value)
                {
                    _analysisResult = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasAnalysis));
                    OnPropertyChanged(nameof(CanEnhancePrompt));
                    OnCanExecuteChanged();
                }
            }
        }

        public bool ShowVideoPrompt
        {
            get => _showVideoPrompt;
            private set { if (_showVideoPrompt != value) { _showVideoPrompt = value; OnPropertyChanged(); } }
        }

        public bool HasImage => !string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath);
        public bool HasAnalysis => !string.IsNullOrWhiteSpace(AnalysisResult);
        public bool CanAnalyzeImage => HasImage && !IsAnalyzing;
        public bool CanEnhancePrompt => HasAnalysis && !IsAnalyzing;
        public bool CanAddToQueue => HasImage && !string.IsNullOrWhiteSpace(Prompt);

        #endregion

        #region Queue Properties

        public ObservableCollection<QueueItem> Queue => _queue;

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

        private async void SelectImage()
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
                ImagePath = filePath;
                AddLog($"Selected image: {Path.GetFileName(ImagePath)}");
            }
        }

        private void LoadImagePreview()
        {
            if (string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath))
            {
                ImagePreview = null;
                ImageInfo = string.Empty;
                return;
            }
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(ImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                ImagePreview = bitmap;
                Width = bitmap.PixelWidth;
                Height = bitmap.PixelHeight;
                var fi = new FileInfo(ImagePath);
                ImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fi.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                ImageInfo = "Error loading image";
            }
        }

        #endregion

        #region LMStudio Analysis

        private async Task AnalyzeImageWithLMStudioAsync()
        {
            if (!CanAnalyzeImage) return;
            try
            {
                IsAnalyzing = true;

                // Step 1: Analyze image
                AddLog("=== Analyzing image with LMStudio ===");

                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);

                var models = await _lmStudioService.GetAvailableModelsAsync();
                var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(selectedModel))
                {
                    if (models.Count > 0)
                        selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;
                    else
                        throw new Exception("No models available in LM Studio. Please load a vision model.");
                }

                AddLog($"Using model: {selectedModel}");

                var analysisPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "image-analysis-prompt.md");
                if (!File.Exists(analysisPromptPath))
                    throw new FileNotFoundException($"Prompt file not found: {analysisPromptPath}");

                var analysisSystemPrompt = await File.ReadAllTextAsync(analysisPromptPath);
                var analysisResult = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel, ImagePath, "Analyze this image.", analysisSystemPrompt);

                AnalysisResult = analysisResult;
                AddLog($"Image analysis complete ({analysisResult.Length} chars)");

                // Step 2: Enhance prompt
                AddLog("=== Enhancing prompt with LMStudio (LTX 2.3) ===");

                var enhancePromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "ltx-audio-video.md");
                if (!File.Exists(enhancePromptPath))
                    throw new FileNotFoundException($"Prompt file not found: {enhancePromptPath}");

                var enhanceSystemPrompt = await File.ReadAllTextAsync(enhancePromptPath);
                var enhanced = await _lmStudioService.SendTextChatAsync(
                    selectedModel, enhanceSystemPrompt, analysisResult, maxTokens: 6000);

                Prompt = enhanced;
                ShowVideoPrompt = true;
                AddLog($"Prompt enhanced ({Prompt.Length} chars)");

                // Step 3: Auto-queue and process
                if (CanAddToQueue)
                {
                    AddLog("Auto-adding to queue and generating...");
                    AddToQueueAndProcess();
                }
                else
                {
                    AddLog("Warning: Cannot add to queue - check image and prompt");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                System.Windows.MessageBox.Show($"Analyze & enhance failed:\n{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
            }
        }

        private async Task EnhancePromptWithLMStudioAsync()
        {
            if (!CanEnhancePrompt) return;
            try
            {
                IsAnalyzing = true;
                AddLog("=== Enhancing prompt with LMStudio (LTX 2.3) ===");

                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);

                var models = await _lmStudioService.GetAvailableModelsAsync();
                var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(selectedModel))
                {
                    if (models.Count > 0)
                        selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;
                    else
                        throw new Exception("No models available in LM Studio.");
                }

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "ltx-audio-video.md");
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"Prompt file not found: {promptFilePath}");

                var systemPrompt = await File.ReadAllTextAsync(promptFilePath);
                var enhanced = await _lmStudioService.SendTextChatAsync(
                    selectedModel, systemPrompt, AnalysisResult, maxTokens: 6000);

                Prompt = enhanced;
                ShowVideoPrompt = true;
                AddLog($"Prompt enhanced ({Prompt.Length} chars)");

                if (CanAddToQueue)
                {
                    AddLog("Auto-adding to queue and generating...");
                    AddToQueueAndProcess();
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR enhancing prompt: {ex.Message}");
                System.Windows.MessageBox.Show($"Prompt enhancement failed:\n{ex.Message}",
                    "Enhancement Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
            }
        }

        #endregion

        #region Queue Management

        private void AddToQueueAndProcess()
        {
            if (!CanAddToQueue) return;

            var item = new QueueItem
            {
                ImagePath = ImagePath,
                Prompt = Prompt,
                FrameCount = FrameCount,
                ItemStatus = QueueItemStatus.Pending
            };

            _queue.Add(item);
            SaveQueueToFile();
            AddLog($"Added to queue: {Path.GetFileName(ImagePath)}");
            UpdateQueueStatus();

            // Auto-start if idle
            if (!IsProcessingQueue)
                _ = ProcessQueueAsync();
        }

        private void RemoveQueueItem(QueueItem? item)
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
            AddLog("Queue cleared");
        }

        private void StopQueue()
        {
            _queueCts?.Cancel();
            AddLog("Queue stop requested");
        }

        private async Task ReprocessAllFailedAsync()
        {
            var failed = _queue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (!failed.Any()) return;

            foreach (var item in failed)
                item.ItemStatus = QueueItemStatus.Pending;

            UpdateQueueStatus();
            SaveQueueToFile();
            AddLog($"Reprocessing {failed.Count} failed item(s)...");

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
                lease = await _workflowCoordinator.AcquireAsync("LTX23Basic", token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue processing cancelled while waiting");
                IsProcessingQueue = false;
                OnCanExecuteChanged();
                return;
            }

            AddLog("Starting queue processing...");
            using (lease)
            try
            {
                QueueItem? item;
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
                        AddLog($"Queue item completed: {Path.GetFileName(item.ImagePath)}");
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
                AddLog("Queue processing finished.");
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
                var items = JsonSerializer.Deserialize<List<QueueItem>>(File.ReadAllText(QueueFilePath));
                if (items?.Any() != true) return;
                _queue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Processing)
                        item.ItemStatus = QueueItemStatus.Pending;
                    _queue.Add(item);
                }
                UpdateQueueStatus();
                AddLog($"Queue loaded: {_queue.Count} items");
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        #endregion

        #region Video Generation

        private async Task GenerateSingleVideoAsync(QueueItem item)
        {
            try
            {
                AddLog($"=== Generating LTX 2.3 video for: {Path.GetFileName(item.ImagePath)} ===");
                IsProcessing = true;
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";

                // Detect image orientation and set output latent dimensions
                int itemWidth = 960, itemHeight = 544; // default landscape
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(item.ImagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    var pixelWidth = bitmap.PixelWidth;
                    var pixelHeight = bitmap.PixelHeight;
                    bool isPortrait = pixelHeight > pixelWidth;

                    // LTX 2.3 Two-Stage output latent dimensions
                    if (isPortrait)
                    {
                        itemWidth = 544;
                        itemHeight = 960;
                    }
                    else
                    {
                        itemWidth = 960;
                        itemHeight = 544;
                    }

                    AddLog($"Detected image: {pixelWidth}x{pixelHeight} ({(isPortrait ? "portrait" : "landscape")})");
                    AddLog($"Using output dimensions: {itemWidth}x{itemHeight}");
                }
                catch (Exception ex)
                {
                    AddLog($"Warning: Could not detect image orientation, using defaults: {ex.Message}");
                }

                // Check / restart ComfyUI
                ProcessingStatus = "Checking ComfyUI status...";
                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddLog($"[Auto-Restart] {status}"));

                if (!comfyUIOk)
                {
                    AddLog("ERROR: ComfyUI not running");
                    throw new Exception("ComfyUI is not running. Please start it manually.");
                }

                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                    AddLog("Connected to ComfyUI");
                }

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "workflow", "LTX-2.3_T2V_I2V_Two_Stage_Distilled(1).json");

                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow not found: {workflowPath}");

                AddLog("Loading workflow...");
                var rawJson = await File.ReadAllTextAsync(workflowPath);

                ProcessingStatus = "Uploading image...";
                ProcessingProgress = 10;

                var uploadedImageName = await _comfyUIService.UploadImageAsync(item.ImagePath);
                if (string.IsNullOrEmpty(uploadedImageName))
                    throw new Exception("Image upload to ComfyUI failed.");

                AddLog($"Image uploaded: {uploadedImageName}");

                // Patch workflow nodes
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "2004", "image", uploadedImageName);
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "2483", "text", item.Prompt);
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "4988", "value", item.FrameCount);
                // Output latent dimensions based on image orientation
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref rawJson, "3059",
                    new Dictionary<string, object> { { "width", itemWidth }, { "height", itemHeight } });
                // Randomise seeds for both two-stage samplers
                var rng = new Random();
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "4832", "noise_seed", (long)(rng.NextDouble() * long.MaxValue));
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "4967", "noise_seed", (long)(rng.NextDouble() * long.MaxValue));
                var updatedWorkflow = JsonSerializer.Deserialize<JsonElement>(rawJson);

                // Record start time for output detection
                var generationStart = DateTime.Now.AddSeconds(-2); // small buffer

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 20 + pct * 0.75;
                            ProcessingStatus = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                        });
                    }
                });

                ProcessingStatus = "Executing workflow...";
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                AddLog($"Workflow submitted (prompt ID: {promptId})");

                ProcessingProgress = 95;
                ProcessingStatus = "Waiting for output video...";

                var outputVideo = await WaitForVideoByTimestampAsync(generationStart, "output_*.mp4",
                    TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5));

                if (outputVideo == null || !File.Exists(outputVideo))
                    throw new Exception("No output video was produced within the timeout.");

                // Save copy to output folder
                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "LTX23Basic");
                Directory.CreateDirectory(outputDir);

                var outName = $"LTX23_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
                var finalPath = Path.Combine(outputDir, outName);
                File.Copy(outputVideo, finalPath, true);
                AddLog($"Video saved: {finalPath}");

                ResultVideoPath = finalPath;
                await LocalCopyService.CopyVideoAsync(finalPath);
                HasResult = true;

                var fileInfo = new FileInfo(finalPath);
                ResultVideoInfo = $"LTX 2.3 • {fileInfo.Length / 1024.0 / 1024.0:F1}MB";
                item.OutputImagePath = finalPath; // store path on item for reference

                ProcessingProgress = 100;
                ProcessingStatus = "Complete!";
                AddLog("=== Video generation complete ===");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        /// <summary>
        /// Waits for a new MP4 file whose last-write time is after <paramref name="after"/>.
        /// This avoids false-negatives when a file was already in the output folder from a prior run.
        /// </summary>
        private async Task<string?> WaitForVideoByTimestampAsync(
            DateTime after,
            string filePattern,
            TimeSpan maxWait,
            TimeSpan checkInterval)
        {
            var settings = _settingsService.Settings;
            if (settings == null) { AddLog("ERROR: Settings not available"); return null; }

            var baseUrl = GetComfyUIBaseUrl();
            var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
            var outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;

            if (string.IsNullOrEmpty(outputFolder))
            {
                AddLog("ERROR: Output folder not configured in settings");
                return null;
            }

            AddLog($"Monitoring: {outputFolder}  (files newer than {after:HH:mm:ss})");

            var deadline = DateTime.Now + maxWait;
            while (DateTime.Now < deadline)
            {
                await Task.Delay(checkInterval);

                if (!Directory.Exists(outputFolder)) continue;

                var candidate = Directory.GetFiles(outputFolder, filePattern)
                    .Where(f => File.GetLastWriteTime(f) > after)
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .FirstOrDefault();

                if (candidate != null)
                {
                    // Give ComfyUI a moment to finish writing
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    var fi = new FileInfo(candidate);
                    AddLog($"Found: {fi.Name} ({fi.Length / 1024.0 / 1024.0:F2} MB)");
                    return candidate;
                }

                var remaining = (int)(deadline - DateTime.Now).TotalSeconds;
                AddLog($"Waiting for video... ({remaining}s remaining)");
            }

            AddLog("ERROR: Timeout waiting for output video");
            return null;
        }

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            GenerateVideoCommand.NotifyCanExecuteChanged();
            ClearQueueCommand.NotifyCanExecuteChanged();
            StopQueueCommand.NotifyCanExecuteChanged();
            ReprocessAllFailedCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            SendToEditCameraCommand.NotifyCanExecuteChanged();
            AnalyzeImageCommand.NotifyCanExecuteChanged();
            EnhancePromptCommand.NotifyCanExecuteChanged();
        }
    }
}
