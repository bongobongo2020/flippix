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

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private readonly ObservableCollection<QueueItem> _queue = new();

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
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);

            _queue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
                OnCanExecuteChanged();
            };

            AddLog("LTX 2.3 Basic Video Generator initialized");
        }

        #region Commands

        public ICommand SelectImageCommand { get; }
        public RelayCommand AnalyzeImageCommand { get; }
        public RelayCommand EnhancePromptCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand<QueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }

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

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "image-analysis-prompt.md");
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"Prompt file not found: {promptFilePath}");

                var systemPrompt = await File.ReadAllTextAsync(promptFilePath);
                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel, ImagePath, "Analyze this image.", systemPrompt);

                AnalysisResult = result;
                AddLog($"Image analysis complete ({result.Length} chars)");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR analyzing image: {ex.Message}");
                System.Windows.MessageBox.Show($"Image analysis failed:\n{ex.Message}",
                    "Analysis Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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

                AddLog($"Using model: {selectedModel}");

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "ltx-audio-video.md");
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"Prompt file not found: {promptFilePath}");

                var systemPrompt = await File.ReadAllTextAsync(promptFilePath);
                var enhanced = await _lmStudioService.SendTextChatAsync(
                    selectedModel, systemPrompt, AnalysisResult, maxTokens: 2000);

                Prompt = enhanced;
                ShowVideoPrompt = true;
                AddLog($"Prompt enhanced ({enhanced.Length} chars)");

                // Auto-trigger queue and generate after enhancement
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
                ItemStatus = QueueItemStatus.Pending
            };

            _queue.Add(item);
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
        }

        private async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;

            IsProcessingQueue = true;
            AddLog("Starting queue processing...");

            try
            {
                QueueItem? item;
                while ((item = _queue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                {
                    item.ItemStatus = QueueItemStatus.Processing;
                    UpdateQueueStatus();

                    try
                    {
                        await GenerateSingleVideoAsync(item);
                        item.ItemStatus = QueueItemStatus.Completed;
                        AddLog($"Queue item completed: {Path.GetFileName(item.ImagePath)}");
                    }
                    catch (Exception ex)
                    {
                        item.ItemStatus = QueueItemStatus.Failed;
                        item.ErrorMessage = ex.Message;
                        AddLog($"Queue item FAILED: {ex.Message}");
                    }

                    UpdateQueueStatus();
                }
            }
            finally
            {
                IsProcessingQueue = false;
                AddLog("Queue processing finished.");
            }
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

                // Detect image orientation and set constrained dimensions
                int itemWidth = 320, itemHeight = 224; // default landscape
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

                    // LTX 2.3 constrained dimensions based on orientation
                    if (isPortrait)
                    {
                        itemWidth = 224;
                        itemHeight = 320;
                    }
                    else
                    {
                        itemWidth = 320;
                        itemHeight = 224;
                    }

                    AddLog($"Detected image: {pixelWidth}x{pixelHeight} ({(isPortrait ? "portrait" : "landscape")})");
                    AddLog($"Using constrained dimensions: {itemWidth}x{itemHeight}");
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
                    "workflow", "LTX2.3-I2VAPI.json");

                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow not found: {workflowPath}");

                AddLog("Loading workflow...");
                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                ProcessingStatus = "Uploading image...";
                ProcessingProgress = 10;

                var uploadedImageName = await _comfyUIService.UploadImageAsync(item.ImagePath);
                if (string.IsNullOrEmpty(uploadedImageName))
                    throw new Exception("Image upload to ComfyUI failed.");

                AddLog($"Image uploaded: {uploadedImageName}");

                // Patch workflow nodes
                var rawJson = workflow.GetRawText();
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "5016:2004", "image", uploadedImageName);
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "5026:5018", "text", item.Prompt);
                // Update width/height to match uploaded image aspect ratio
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref rawJson, "5013:3059",
                    new Dictionary<string, object> { { "width", itemWidth }, { "height", itemHeight } });
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
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            SendToEditCameraCommand.NotifyCanExecuteChanged();
            AnalyzeImageCommand.NotifyCanExecuteChanged();
            EnhancePromptCommand.NotifyCanExecuteChanged();
        }
    }
}
