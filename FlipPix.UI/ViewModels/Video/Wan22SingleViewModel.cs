using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    /// ViewModel for Wan 2.2 Remix single image-to-video generation.
    /// Analyzes the image via llamaserver (qwen-4-vl) using wan-system-single.md,
    /// then automatically queues and processes the result.
    /// </summary>
    public partial class Wan22SingleViewModel : VideoProcessingBaseViewModel
    {
        private string _imagePath = string.Empty;
        private BitmapImage? _imagePreview;
        private string _imageInfo = string.Empty;
        private string _prompt = string.Empty;
        private bool _isAnalyzing = false;
        private string _analysisResult = string.Empty;
        private bool _isProcessingQueue = false;
        private string _queueStatus = string.Empty;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private readonly ObservableCollection<QueueItem> _queue = new();

        public Wan22SingleViewModel(
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
            AnalyzeImageCommand = new RelayCommand(async () => await AnalyzeImageAsync(), () => CanAnalyzeImage);
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

            AddLog("Wan 2.2 Remix Single Video Generator initialized");
        }

        #region Commands

        public ICommand SelectImageCommand { get; }
        public RelayCommand AnalyzeImageCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand<QueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }

        #endregion

        #region Properties

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
                    OnCanExecuteChanged();
                }
            }
        }

        public bool HasImage => !string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath);
        public bool HasAnalysis => !string.IsNullOrWhiteSpace(AnalysisResult);
        public bool CanAnalyzeImage => HasImage && !IsAnalyzing;
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

        #region Image Analysis

        private async Task AnalyzeImageAsync()
        {
            if (!CanAnalyzeImage) return;
            try
            {
                IsAnalyzing = true;
                AddLog("=== Analyzing image for Wan 2.2 video prompt ===");

                var models = await _lmStudioService.GetAvailableModelsAsync();
                var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(selectedModel))
                {
                    if (models.Count > 0)
                        selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;
                    else
                        throw new Exception("No models available in llamaserver. Please load a vision model.");
                }

                AddLog($"Using model: {selectedModel}");

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "wan-system-single.md");
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"Prompt file not found: {promptFilePath}");

                var systemPrompt = await File.ReadAllTextAsync(promptFilePath);
                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel, ImagePath,
                    "Analyze this image and generate a cinematic video prompt.",
                    systemPrompt);

                AnalysisResult = result;
                Prompt = result;
                AddLog($"Analysis complete ({result.Length} chars) — auto-adding to queue");

                if (CanAddToQueue)
                    AddToQueueAndProcess();
                else
                    AddLog("Warning: Cannot add to queue — check image path");
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

        #endregion

        #region Queue Management

        public void AddToQueueAndProcess()
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
                        await GenerateWan22VideoAsync(item);
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

        private async Task GenerateWan22VideoAsync(QueueItem item)
        {
            try
            {
                AddLog($"=== Generating Wan 2.2 Remix video: {Path.GetFileName(item.ImagePath)} ===");
                IsProcessing = true;
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";

                ProcessingStatus = "Checking ComfyUI status...";
                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddLog($"[Auto-Restart] {status}"));

                if (!comfyUIOk)
                    throw new Exception("ComfyUI is not running. Please start it manually.");

                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                    AddLog("Connected to ComfyUI");
                }

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "workflow", "Wan2_2_RemixAPI.json");

                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow not found: {workflowPath}");

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                ProcessingStatus = "Uploading image...";
                ProcessingProgress = 10;

                var uploadedImageName = await _comfyUIService.UploadImageAsync(item.ImagePath);
                if (string.IsNullOrEmpty(uploadedImageName))
                    throw new Exception("Image upload to ComfyUI failed.");

                AddLog($"Image uploaded: {uploadedImageName}");

                var rawJson = workflow.GetRawText();
                var seed = (long)new Random().Next(1, int.MaxValue);
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "258", "image", uploadedImageName);
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "6", "text", item.Prompt);
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "57", "noise_seed", seed);
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "58", "noise_seed", seed);
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "304", "filename_prefix",
                    $"{DateTime.Now:yyyyMMdd_HHmmss}_Wan2.2");

                var updatedWorkflow = JsonSerializer.Deserialize<JsonElement>(rawJson);

                var generationStart = DateTime.Now.AddSeconds(-2);

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

                var outputVideo = await WaitForVideoByTimestampAsync(
                    generationStart, TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(5));

                if (outputVideo == null || !File.Exists(outputVideo))
                    throw new Exception("No output video was produced within the timeout.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "Wan22Single");
                Directory.CreateDirectory(outputDir);

                var finalPath = Path.Combine(outputDir, $"Wan22_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                File.Copy(outputVideo, finalPath, true);
                AddLog($"Video saved: {finalPath}");

                ResultVideoPath = finalPath;
                await LocalCopyService.CopyVideoAsync(finalPath);
                HasResult = true;

                var fileInfo = new FileInfo(finalPath);
                ResultVideoInfo = $"Wan 2.2 Remix • {fileInfo.Length / 1024.0 / 1024.0:F1}MB";
                item.OutputImagePath = finalPath;

                ProcessingProgress = 100;
                ProcessingStatus = "Complete!";
                AddLog("=== Video generation complete ===");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private async Task<string?> WaitForVideoByTimestampAsync(
            DateTime after, TimeSpan maxWait, TimeSpan checkInterval)
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

                var candidate = Directory.GetFiles(outputFolder, "*.mp4", SearchOption.AllDirectories)
                    .Where(f => File.GetLastWriteTime(f) > after)
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .FirstOrDefault();

                if (candidate != null)
                {
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
        }
    }
}
