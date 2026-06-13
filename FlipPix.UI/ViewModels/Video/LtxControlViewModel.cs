using System;
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
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    public partial class LtxControlViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/ltx/LTX-2.3_ICLoRA_Union_Control_Distilled.json";
        private const string OutputSubfolder = "ltx_control";

        private string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "ltx_control_queue.json");

        // ── Input fields ───────────────────────────────────────────────────────
        private string _refImagePath = string.Empty;
        private BitmapImage? _refImagePreview;
        private string _refImageInfo = string.Empty;

        private string _refVideoPath = string.Empty;
        private string _refVideoInfo = string.Empty;
        private string? _refVideoFileUri;

        private string _prompt = string.Empty;
        private string _negativePrompt = "ugly, distorted, low quality, blurry, artifacts, watermark, text, cartoon";
        private long _seed = -1;
        private bool _isAnalyzing;
        private bool _isProcessingQueue;
        private string _queueStatus = string.Empty;

        // ── State ──────────────────────────────────────────────────────────────
        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private readonly ObservableCollection<LtxControlQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;
        private CancellationTokenSource? _analyzeCts;

        public LtxControlViewModel(
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

            SelectRefImageCommand = new RelayCommand(SelectRefImage);
            SelectRefVideoCommand = new RelayCommand(SelectRefVideo);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            GenerateVideoCommand = new RelayCommand(AddToQueue, () => CanAddToQueue);
            RemoveQueueItemCommand = new RelayCommand<LtxControlQueueItem>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => _queue.Any());
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            StartQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => HasQueueItems && !IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(async () => await ReprocessAllFailedAsync(), () => HasFailedItems);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);
            RandomSeedCommand = new RelayCommand(() => Seed = new Random().NextInt64(0, long.MaxValue));

            _queue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
                OnCanExecuteChanged();
            };

            AddLog("LTX Control initialized");
            LoadQueueFromFile();
        }

        #region Commands

        public ICommand SelectRefImageCommand { get; }
        public ICommand SelectRefVideoCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand<LtxControlQueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand StartQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }
        public RelayCommand RandomSeedCommand { get; }

        public bool HasFailedItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        #endregion

        #region Input Properties

        public string RefImagePath
        {
            get => _refImagePath;
            set
            {
                if (_refImagePath != value)
                {
                    _refImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasRefImage));
                    OnPropertyChanged(nameof(CanAddToQueue));
                    OnPropertyChanged(nameof(CanAnalyze));
                    LoadRefImagePreview();
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? RefImagePreview
        {
            get => _refImagePreview;
            set { _refImagePreview = value; OnPropertyChanged(); }
        }

        public string RefImageInfo
        {
            get => _refImageInfo;
            set { if (_refImageInfo != value) { _refImageInfo = value; OnPropertyChanged(); } }
        }

        public string RefVideoPath
        {
            get => _refVideoPath;
            set
            {
                if (_refVideoPath != value)
                {
                    _refVideoPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasRefVideo));
                    OnPropertyChanged(nameof(CanAddToQueue));
                    OnPropertyChanged(nameof(CanAnalyze));
                    LoadRefVideoInfo();
                    OnCanExecuteChanged();
                }
            }
        }

        public string RefVideoInfo
        {
            get => _refVideoInfo;
            set { if (_refVideoInfo != value) { _refVideoInfo = value; OnPropertyChanged(); } }
        }

        public string? RefVideoFileUri
        {
            get => _refVideoFileUri;
            private set { if (_refVideoFileUri != value) { _refVideoFileUri = value; OnPropertyChanged(); } }
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

        public long Seed
        {
            get => _seed;
            set { if (_seed != value) { _seed = value; OnPropertyChanged(); } }
        }

        public bool HasRefImage => !string.IsNullOrEmpty(RefImagePath) && File.Exists(RefImagePath);
        public bool HasRefVideo => !string.IsNullOrEmpty(RefVideoPath) && File.Exists(RefVideoPath);
        public bool CanAddToQueue => HasRefImage && HasRefVideo;
        public bool CanAnalyze => HasRefImage && !IsAnalyzing && !IsProcessing;

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (_isAnalyzing != value)
                {
                    _isAnalyzing = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanAnalyze));
                    OnCanExecuteChanged();
                }
            }
        }

        #endregion

        #region Queue Properties

        public ObservableCollection<LtxControlQueueItem> Queue => _queue;

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

        private async void SelectRefImage()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: "ltxcontrol.image");

            if (path != null)
            {
                RefImagePath = path;
                AddLog($"Reference image: {Path.GetFileName(path)}");
            }
        }

        private async void SelectRefVideo()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Video",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.webm|All Files|*.*",
                initialDir,
                persistKey: "ltxcontrol.video");

            if (path != null)
            {
                RefVideoPath = path;
                AddLog($"Reference video: {Path.GetFileName(path)}");
            }
        }

        private void LoadRefImagePreview()
        {
            if (!HasRefImage)
            {
                RefImagePreview = null;
                RefImageInfo = string.Empty;
                return;
            }
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(RefImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                RefImagePreview = bitmap;
                var fi = new FileInfo(RefImagePath);
                RefImageInfo = $"{bitmap.PixelWidth}×{bitmap.PixelHeight} • {fi.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                RefImageInfo = "Error loading image";
            }
        }

        private void LoadRefVideoInfo()
        {
            if (!HasRefVideo)
            {
                RefVideoInfo = string.Empty;
                RefVideoFileUri = null;
                return;
            }
            var fi = new FileInfo(RefVideoPath);
            RefVideoInfo = $"{fi.Name} • {fi.Length / 1024 / 1024.0:F1}MB";
            RefVideoFileUri = RefVideoPath;
        }

        #endregion

        #region Analysis

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
                var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(selectedModel) && models.Count > 0)
                    selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;

                if (string.IsNullOrEmpty(selectedModel))
                {
                    MessageBox.Show("No LM Studio model available. Please ensure LM Studio is running and a model is loaded.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AddLog($"Analyzing reference image with model: {selectedModel}");

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "ltx-control.md");
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"System prompt not found: {promptFilePath}");

                var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel,
                    RefImagePath,
                    "Analyze this reference image and generate an LTX IC-LoRA video prompt.",
                    systemPrompt,
                    maxTokens: 4000,
                    cancellationToken: token);

                var cleaned = CleanOutput(result);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    Prompt = cleaned;
                    AddLog($"Prompt generated ({cleaned.Length} chars)");
                }
                else
                {
                    AddLog("WARNING: Analysis returned empty result");
                }
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

        private static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = text.Replace("**", "").Trim();
            var lower = text.ToLowerInvariant();
            if (lower.StartsWith("prompt:") || lower.StartsWith("prompt :"))
                text = text.Substring(text.IndexOf(':') + 1).Trim();
            return text;
        }

        #endregion

        #region Queue Management

        private void AddToQueue()
        {
            if (!CanAddToQueue) return;

            var effectivePrompt = string.IsNullOrWhiteSpace(Prompt)
                ? "cinematic footage, photorealistic, high quality, detailed"
                : Prompt;

            var item = new LtxControlQueueItem
            {
                RefImagePath = RefImagePath,
                RefVideoPath = RefVideoPath,
                Prompt = effectivePrompt,
                NegativePrompt = NegativePrompt,
                Seed = Seed,
                ItemStatus = QueueItemStatus.Pending
            };

            _queue.Add(item);
            SaveQueueToFile();
            AddLog($"Added to queue: {item.DisplayText}");
            UpdateQueueStatus();

            if (!IsProcessingQueue)
                _ = ProcessQueueAsync();
        }

        private void RemoveQueueItem(LtxControlQueueItem? item)
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

        private void StopQueue() => _queueCts?.Cancel();

        private async Task ReprocessAllFailedAsync()
        {
            var failed = _queue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (!failed.Any()) return;
            foreach (var item in failed)
                item.ItemStatus = QueueItemStatus.Pending;
            UpdateQueueStatus();
            SaveQueueToFile();
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

            WorkflowQueueCoordinator.WorkflowLease lease;
            try
            {
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("LtxControl", token);
            }
            catch (OperationCanceledException)
            {
                IsProcessingQueue = false;
                OnCanExecuteChanged();
                return;
            }

            AddLog("Starting LTX Control queue...");
            using (lease)
            try
            {
                LtxControlQueueItem? item;
                while (!token.IsCancellationRequested &&
                       (item = _queue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                {
                    item.ItemStatus = QueueItemStatus.Processing;
                    UpdateQueueStatus();
                    SaveQueueToFile();
                    try
                    {
                        await GenerateSingleVideoAsync(item, token);
                        item.ItemStatus = QueueItemStatus.Completed;
                        AddLog($"Completed: {item.DisplayText}");
                    }
                    catch (OperationCanceledException)
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
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
                            AddLog($"FAILED: {ex.Message}");
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
                var items = JsonSerializer.Deserialize<System.Collections.Generic.List<LtxControlQueueItem>>(
                    File.ReadAllText(QueueFilePath));
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
                var pending = _queue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
                if (pending > 0)
                {
                    AddLog($"Auto-resuming: {pending} pending item(s)");
                    _ = ProcessQueueAsync();
                }
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        #endregion

        #region Video Generation

        private async Task GenerateSingleVideoAsync(LtxControlQueueItem item, CancellationToken token)
        {
            try
            {
                IsProcessing = true;
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing LTX Control workflow...";

                AddLog($"=== LTX Control: {item.DisplayText} ===");

                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    s => AddLog($"[Auto-Restart] {s}"));

                if (!comfyOk)
                    throw new Exception("ComfyUI is not running.");

                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFileName);
                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow file not found: {workflowPath}");

                var workflowJson = await File.ReadAllTextAsync(workflowPath, token);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                ProcessingStatus = "Uploading assets...";
                ProcessingProgress = 10;

                AddLog("Uploading reference image...");
                var uploadedImage = await _comfyUIService.UploadImageAsync(item.RefImagePath);
                if (string.IsNullOrEmpty(uploadedImage))
                    throw new Exception("Failed to upload reference image.");
                AddLog($"Image uploaded: {uploadedImage}");

                AddLog("Uploading reference video...");
                var uploadedVideo = await _comfyUIService.UploadVideoAsync(item.RefVideoPath);
                if (string.IsNullOrEmpty(uploadedVideo))
                    throw new Exception("Failed to upload reference video.");
                AddLog($"Video uploaded: {uploadedVideo}");

                var runSeed = item.Seed >= 0 ? item.Seed : (long)(new Random().NextDouble() * long.MaxValue);
                var updatedWorkflow = UpdateWorkflowNodes(workflow, uploadedImage, uploadedVideo,
                    item.Prompt, item.NegativePrompt, runSeed, workflowJson);

                ProcessingProgress = 20;
                ProcessingStatus = "Generating video...";

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 20 + pct * 0.75;
                            ProcessingStatus = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                        });
                    }
                });

                var existingFiles = GetExistingVideoFiles("*.mp4", OutputSubfolder);
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                AddLog($"Workflow submitted, ID: {promptId}");

                ProcessingProgress = 85;
                ProcessingStatus = "Waiting for output...";

                var outputVideo = await TryGetVideoFromHistoryAsync(promptId);
                if (outputVideo == null)
                {
                    AddLog("Falling back to filesystem polling...");
                    outputVideo = await WaitForNewVideoAsync(
                        existingFiles, "*.mp4",
                        TimeSpan.FromMinutes(30),
                        TimeSpan.FromSeconds(5),
                        OutputSubfolder);
                }

                if (outputVideo == null || !File.Exists(outputVideo))
                    throw new Exception("No output video was generated.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(),
                    "LtxControl");
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"LtxControl_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                File.Copy(outputVideo, finalPath, true);

                item.OutputVideoPath = finalPath;
                ResultVideoPath = finalPath;
                await LocalCopyService.CopyVideoAsync(finalPath);
                HasResult = true;
                var fi = new FileInfo(finalPath);
                ResultVideoInfo = $"LTX Control • {fi.Length / 1024 / 1024.0:F1}MB";
                ProcessingProgress = 100;
                ProcessingStatus = "LTX Control Complete!";
                AddLog($"=== Complete: {finalPath} ===");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                ProcessingStatus = "Error";
                throw;
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private JsonElement UpdateWorkflowNodes(
            JsonElement workflow,
            string imageName,
            string videoName,
            string prompt,
            string negativePrompt,
            long seed,
            string originalJson)
        {
            var json = originalJson;
            AddLog($"Updating workflow nodes (seed={seed})");

            // Node 2004: LoadImage - reference image
            WorkflowNodeUpdater.UpdateNodeInput(ref json, "2004", "image", imageName);

            // Node 5001: LoadVideo - reference video
            WorkflowNodeUpdater.UpdateNodeInput(ref json, "5001", "file", videoName);

            // Node 2483: Positive prompt
            WorkflowNodeUpdater.UpdateNodeInput(ref json, "2483", "text", prompt);

            // Node 2612: Negative prompt
            WorkflowNodeUpdater.UpdateNodeInput(ref json, "2612", "text", negativePrompt);

            // Node 4832: Seed
            WorkflowNodeUpdater.UpdateNodeInput(ref json, "4832", "noise_seed", seed);

            AddLog("✓ Workflow nodes updated");
            return JsonSerializer.Deserialize<JsonElement>(json);
        }

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            AnalyzeCommand.NotifyCanExecuteChanged();
            GenerateVideoCommand.NotifyCanExecuteChanged();
            RemoveQueueItemCommand.NotifyCanExecuteChanged();
            ClearQueueCommand.NotifyCanExecuteChanged();
            StopQueueCommand.NotifyCanExecuteChanged();
            StartQueueCommand.NotifyCanExecuteChanged();
            ReprocessAllFailedCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            SendToEditCameraCommand.NotifyCanExecuteChanged();
        }
    }
}
