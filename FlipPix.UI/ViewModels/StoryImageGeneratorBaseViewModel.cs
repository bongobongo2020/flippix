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
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    public abstract class StoryImageGeneratorBaseViewModel : ObservableObject, IDisposable
    {
        protected readonly ComfyUIService _comfyUIService;
        protected readonly IAppLogger _logger;
        protected readonly FlipPix.Core.Services.SettingsService _settingsService;
        protected readonly WorkflowQueueCoordinator _workflowCoordinator;
        protected readonly IFileDialogService _fileDialogService;
        protected readonly LoraManager _loraManager;
        protected readonly ComfyUIImageRetriever _imageRetriever;
        private bool _disposed = false;

        private string _promptJsonFilePath = string.Empty;
        private string _inputImagePath = string.Empty;
        private BitmapImage? _inputImagePreview;
        private ObservableCollection<StoryPromptItem> _queueItems = new();
        private bool _isProcessingQueue = false;
        private StoryPromptItem? _currentQueueItem;
        private int _queueProgress = 0;
        private int _queueTotal = 0;
        private string _logOutput = string.Empty;
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        protected CancellationTokenSource? _cancellationTokenSource;
        private bool _isQueuePaused = false;
        protected readonly ManualResetEventSlim _pauseEvent = new(true);

        // Generation settings
        private int _steps;
        private double _cfg;
        private double _denoise;
        private string _negativePrompt = "";

        protected StoryImageGeneratorBaseViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService,
            LoraManager loraManager,
            ComfyUIImageRetriever imageRetriever)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _workflowCoordinator = workflowCoordinator ?? throw new ArgumentNullException(nameof(workflowCoordinator));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            _loraManager = loraManager ?? throw new ArgumentNullException(nameof(loraManager));
            _imageRetriever = imageRetriever ?? throw new ArgumentNullException(nameof(imageRetriever));

            // Set defaults from subclass
            _steps = DefaultSteps;
            _cfg = DefaultCfg;
            _denoise = DefaultDenoise;

            // Initialize commands
            SelectPromptJsonCommand = new RelayCommand(SelectPromptJson);
            SelectInputImageCommand = new RelayCommand(SelectInputImage);
            LoadPromptsCommand = new RelayCommand(async () => await LoadPromptsAsync(), () => CanLoadPrompts);
            ProcessQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => CanProcessQueue);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => QueueItems.Any());
            OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
            CancelProcessingCommand = new RelayCommand(CancelProcessing, () => IsProcessing);
            PauseQueueCommand = new RelayCommand(PauseQueue, () => IsProcessingQueue && !IsQueuePaused);
            ResumeQueueCommand = new RelayCommand(ResumeQueue, () => IsProcessingQueue && IsQueuePaused);

            // Let subclass do variant-specific init
            InitializeVariant();

            LoadQueueFromFile();

            AddLog($"{VariantDisplayName} initialized");
        }

        // --- Abstract members that each variant must provide ---

        /// <summary>Display name for logging (e.g. "Story Image Generator Q").</summary>
        protected abstract string VariantDisplayName { get; }

        /// <summary>Workflow type name for the coordinator (e.g. "StoryImageQ").</summary>
        protected abstract string WorkflowTypeName { get; }

        /// <summary>Queue persistence filename (e.g. "story_image_q_queue.json").</summary>
        protected abstract string QueuePersistenceFileName { get; }

        /// <summary>Output subfolder name (e.g. "story-generator-q").</summary>
        protected abstract string OutputFolderName { get; }

        /// <summary>Default value for Steps.</summary>
        protected abstract int DefaultSteps { get; }

        /// <summary>Default value for Cfg.</summary>
        protected abstract double DefaultCfg { get; }

        /// <summary>Default value for Denoise.</summary>
        protected abstract double DefaultDenoise { get; }

        /// <summary>
        /// Process a single queue item. Each variant implements its own workflow execution logic.
        /// </summary>
        /// <param name="item">The queue item to process.</param>
        /// <param name="inputImagePath">Path to the input image.</param>
        /// <param name="sessionOutputDir">Session output directory (null if not using session folders).</param>
        /// <param name="jsonFileName">JSON filename without extension.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Path to the output image.</returns>
        protected abstract Task<string> ProcessQueueItemAsync(
            StoryPromptItem item,
            string inputImagePath,
            string? sessionOutputDir,
            string jsonFileName,
            CancellationToken cancellationToken);

        // --- Virtual members that variants can override ---

        /// <summary>Whether this variant requires an input image to load prompts. Default: true.</summary>
        protected virtual bool RequiresInputImage => true;

        /// <summary>Whether to create a session output folder per queue run. Default: false.</summary>
        protected virtual bool UseSessionOutputFolder => false;

        /// <summary>Whether to check/restart ComfyUI before each item. Default: false.</summary>
        protected virtual bool UseComfyUICrashDetection => false;

        /// <summary>Whether to show a MessageBox when queue processing completes. Default: false.</summary>
        protected virtual bool ShowCompletionMessageBox => false;

        /// <summary>Whether to automatically start processing when items are added to queue. Default: false.</summary>
        protected virtual bool AutoStartProcessing => false;

        /// <summary>Called during constructor for variant-specific initialization.</summary>
        protected virtual void InitializeVariant() { }

        /// <summary>Creates a StoryPromptItem for LoadPromptsAsync. Override to snapshot settings.</summary>
        protected virtual StoryPromptItem CreateQueueItem(int index, string prompt, string inputImagePath)
        {
            return new StoryPromptItem
            {
                Index = index,
                Prompt = prompt,
                InputImagePath = inputImagePath,
                Status = "Queued"
            };
        }

        /// <summary>Returns the initial directory for the prompt JSON dialog.</summary>
        protected virtual string GetPromptJsonInitialDirectory()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts");
        }

        /// <summary>Optionally saves the selected prompt JSON folder. Default: no-op.</summary>
        protected virtual void SavePromptJsonFolder(string folderPath) { }

        /// <summary>Returns the initial directory for the input image dialog.</summary>
        protected virtual string GetInputImageInitialDirectory()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        }

        /// <summary>Optionally saves the selected input image folder. Default: no-op.</summary>
        protected virtual void SaveInputImageFolder(string folderPath) { }

        // --- Properties ---

        public string PromptJsonFilePath
        {
            get => _promptJsonFilePath;
            set
            {
                if (SetProperty(ref _promptJsonFilePath, value))
                {
                    OnPropertyChanged(nameof(CanLoadPrompts));
                    LoadPromptsCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string InputImagePath
        {
            get => _inputImagePath;
            set
            {
                if (SetProperty(ref _inputImagePath, value))
                {
                    OnPropertyChanged(nameof(CanLoadPrompts));
                    LoadInputImagePreview();
                    LoadPromptsCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public BitmapImage? InputImagePreview
        {
            get => _inputImagePreview;
            set => SetProperty(ref _inputImagePreview, value);
        }

        public ObservableCollection<StoryPromptItem> QueueItems
        {
            get => _queueItems;
            set => SetProperty(ref _queueItems, value);
        }

        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            set
            {
                if (SetProperty(ref _isProcessingQueue, value))
                {
                    OnPropertyChanged(nameof(CanProcessQueue));
                    OnPropertyChanged(nameof(CanLoadPrompts));
                    LoadPromptsCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool IsQueuePaused
        {
            get => _isQueuePaused;
            set
            {
                if (SetProperty(ref _isQueuePaused, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public StoryPromptItem? CurrentQueueItem
        {
            get => _currentQueueItem;
            set => SetProperty(ref _currentQueueItem, value);
        }

        public int QueueProgress
        {
            get => _queueProgress;
            set
            {
                if (SetProperty(ref _queueProgress, value))
                {
                    OnPropertyChanged(nameof(QueueProgressText));
                }
            }
        }

        public int QueueTotal
        {
            get => _queueTotal;
            set
            {
                if (SetProperty(ref _queueTotal, value))
                {
                    OnPropertyChanged(nameof(QueueProgressText));
                }
            }
        }

        public string QueueProgressText => QueueItems.Count > 0
            ? $"{CompletedCount}/{QueueItems.Count} ({QueuedCount} remaining)"
            : "0/0";

        public virtual bool CanLoadPrompts
        {
            get
            {
                var hasPromptFile = !string.IsNullOrEmpty(PromptJsonFilePath) && File.Exists(PromptJsonFilePath);
                if (!RequiresInputImage)
                    return hasPromptFile;
                return hasPromptFile && !string.IsNullOrEmpty(InputImagePath) && File.Exists(InputImagePath);
            }
        }

        public bool CanProcessQueue => QueueItems.Any(item => item.Status == "Queued") && !IsProcessingQueue;

        public int QueuedCount => QueueItems.Count(item => item.Status == "Queued");

        public int CompletedCount => QueueItems.Count(item => item.Status == "Completed");

        public int FailedCount => QueueItems.Count(item => item.Status == "Failed");

        public string LogOutput
        {
            get => _logOutput;
            set => SetProperty(ref _logOutput, value);
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set => SetProperty(ref _isProcessing, value);
        }

        public string ProcessingStatus
        {
            get => _processingStatus;
            set => SetProperty(ref _processingStatus, value);
        }

        public double ProcessingProgress
        {
            get => _processingProgress;
            set
            {
                if (SetProperty(ref _processingProgress, value))
                {
                    OnPropertyChanged(nameof(ProgressPercentage));
                }
            }
        }

        public string ProgressPercentage => $"{ProcessingProgress:F0}%";

        public int Steps
        {
            get => _steps;
            set => SetProperty(ref _steps, value);
        }

        public double Cfg
        {
            get => _cfg;
            set => SetProperty(ref _cfg, value);
        }

        public double Denoise
        {
            get => _denoise;
            set => SetProperty(ref _denoise, value);
        }

        public string NegativePrompt
        {
            get => _negativePrompt;
            set => SetProperty(ref _negativePrompt, value);
        }

        // --- Commands ---

        public ICommand SelectPromptJsonCommand { get; }
        public ICommand SelectInputImageCommand { get; }
        public CommunityToolkit.Mvvm.Input.RelayCommand LoadPromptsCommand { get; }
        public ICommand ProcessQueueCommand { get; }
        public ICommand ClearQueueCommand { get; }
        public ICommand OpenOutputFolderCommand { get; }
        public ICommand CancelProcessingCommand { get; }
        public ICommand PauseQueueCommand { get; }
        public ICommand ResumeQueueCommand { get; }

        // --- Shared methods ---

        private async void SelectPromptJson()
        {
            var initialDirectory = GetPromptJsonInitialDirectory();

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts");
            }

            var selectedFile = await _fileDialogService.OpenFileDialogAsync(
                "Select Story Prompts JSON File",
                "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                initialDirectory);

            if (!string.IsNullOrEmpty(selectedFile))
            {
                PromptJsonFilePath = selectedFile;

                var folderPath = Path.GetDirectoryName(selectedFile);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    SavePromptJsonFolder(folderPath);
                }

                AddLog($"Selected prompt file: {Path.GetFileName(PromptJsonFilePath)}");
            }
        }

        private async void SelectInputImage()
        {
            var initialDirectory = GetInputImageInitialDirectory();

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var selectedFile = await _fileDialogService.OpenFileDialogAsync(
                "Select Input Image",
                "Image Files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All Files (*.*)|*.*",
                initialDirectory);

            if (!string.IsNullOrEmpty(selectedFile))
            {
                InputImagePath = selectedFile;

                var folderPath = Path.GetDirectoryName(selectedFile);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    SaveInputImageFolder(folderPath);
                }

                AddLog($"Selected input image: {Path.GetFileName(InputImagePath)}");
            }
        }

        private void LoadInputImagePreview()
        {
            if (!string.IsNullOrEmpty(InputImagePath) && File.Exists(InputImagePath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(InputImagePath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    InputImagePreview = bitmap;
                }
                catch (Exception ex)
                {
                    AddLog($"Error loading image preview: {ex.Message}");
                    InputImagePreview = null;
                }
            }
            else
            {
                InputImagePreview = null;
            }
        }

        protected async Task LoadPromptsAsync()
        {
            if (!CanLoadPrompts) return;

            try
            {
                AddLog("Loading prompts from JSON file...");
                var jsonContent = await File.ReadAllTextAsync(PromptJsonFilePath);
                var storyData = JsonSerializer.Deserialize<StoryPromptData>(jsonContent);

                if (storyData?.Prompts == null || !storyData.Prompts.Any())
                {
                    AddLog("ERROR: No prompts found in JSON file");
                    System.Windows.MessageBox.Show("No prompts found in the JSON file.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var startIndex = QueueItems.Any() ? QueueItems.Max(q => q.Index) + 1 : 1;

                for (int i = 0; i < storyData.Prompts.Count; i++)
                {
                    QueueItems.Add(CreateQueueItem(startIndex + i, storyData.Prompts[i], InputImagePath));
                }

                UpdateQueueCountNotifications();
                SaveQueueToFile();
                AddLog($"Added {storyData.Prompts.Count} prompts to queue (total: {QueueItems.Count})");

                // Auto-start processing if not already processing
                if (CanProcessQueue)
                {
                    AddLog("Auto-starting queue processing...");
                    _ = ProcessQueueAsync(); // Fire and forget - don't await
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading prompts: {ex.Message}");
                _logger.LogError($"Error loading prompts: {ex}");
                System.Windows.MessageBox.Show($"Error loading prompts:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected async Task ProcessQueueAsync()
        {
            if (!CanProcessQueue) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            AddLog("Waiting for other workflows to finish...");

            WorkflowQueueCoordinator.WorkflowLease lease;
            try
            {
                lease = await _workflowCoordinator.AcquireAsync(WorkflowTypeName, _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue processing cancelled while waiting");
                return;
            }

            using (lease)
            try
            {
                IsProcessingQueue = true;
                QueueTotal = QueueItems.Count;
                QueueProgress = 0;

                var jsonFileName = Path.GetFileNameWithoutExtension(PromptJsonFilePath);
                string? sessionOutputDir = null;

                if (UseSessionOutputFolder)
                {
                    var baseOutputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", OutputFolderName);
                    Directory.CreateDirectory(baseOutputDir);
                    sessionOutputDir = GetUniqueFolderPath(baseOutputDir, jsonFileName);
                    Directory.CreateDirectory(sessionOutputDir);
                    AddLog($"Output folder: {sessionOutputDir}");
                }

                AddLog($"=== Starting story queue processing ({QueuedCount} images) ===");

                while (true)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        AddLog("Queue processing cancelled");
                        break;
                    }

                    _pauseEvent.Wait(_cancellationTokenSource.Token);

                    var item = QueueItems.FirstOrDefault(i => i.Status == "Queued");
                    if (item == null) break;

                    QueueTotal = QueueItems.Count;

                    // ComfyUI crash detection (if enabled by variant)
                    if (UseComfyUICrashDetection)
                    {
                        AddLog("Checking ComfyUI status before processing...");
                        var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                            status => AddLog($"[Crash Detection] {status}"),
                            _cancellationTokenSource.Token);

                        if (!comfyUIOk)
                        {
                            AddLog("ERROR: ComfyUI is not running and auto-restart failed");
                            item.Status = "Failed";
                            item.ErrorMessage = "ComfyUI is not available";
                            QueueProgress++;
                            continue;
                        }

                        if (!_comfyUIService.IsConnected)
                        {
                            AddLog("Reconnecting to ComfyUI WebSocket...");
                            try
                            {
                                await _comfyUIService.ConnectAsync(_cancellationTokenSource.Token);
                                AddLog("Reconnected to ComfyUI");
                            }
                            catch (Exception ex)
                            {
                                AddLog($"ERROR: Failed to reconnect to ComfyUI: {ex.Message}");
                                item.Status = "Failed";
                                item.ErrorMessage = $"Failed to reconnect: {ex.Message}";
                                QueueProgress++;
                                continue;
                            }
                        }
                    }

                    CurrentQueueItem = item;
                    item.Status = "Processing";
                    item.StartedAt = DateTime.Now;
                    SaveQueueToFile();

                    AddLog($"Processing story image {QueueProgress + 1}/{QueueTotal}");

                    try
                    {
                        var outputPath = await ProcessQueueItemAsync(item, item.InputImagePath, sessionOutputDir, jsonFileName, _cancellationTokenSource.Token);
                        item.OutputImagePath = outputPath;
                        item.Status = "Completed";
                        item.CompletedAt = DateTime.Now;
                        item.Progress = 100;
                        SaveQueueToFile();

                        AddLog($"Completed story image {QueueProgress + 1}/{QueueTotal}: {Path.GetFileName(outputPath)}");
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = "Cancelled";
                        item.ErrorMessage = "Cancelled by user";
                        SaveQueueToFile();
                        AddLog($"Queue item cancelled: Prompt #{item.Index}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        item.Status = "Failed";
                        item.ErrorMessage = ex.Message;
                        item.Progress = 0;
                        SaveQueueToFile();
                        AddLog($"Queue item failed: Prompt #{item.Index} - {ex.Message}");
                        _logger.LogError($"Error processing queue item {item.Id}: {ex}");

                        if (UseComfyUICrashDetection)
                        {
                            AddLog("Checking if ComfyUI crashed after error...");
                            await _comfyUIService.DetectAndRestartIfCrashedAsync(
                                status => AddLog($"[Post-Error Check] {status}"),
                                _cancellationTokenSource.Token);
                        }
                    }
                    finally
                    {
                        QueueProgress++;
                        UpdateQueueCountNotifications();
                    }
                }

                AddLog($"=== Story queue processing completed ({CompletedCount} successful, {FailedCount} failed) ===");

                if (ShowCompletionMessageBox)
                {
                    System.Windows.MessageBox.Show($"Story generation completed!\n\nSuccessful: {CompletedCount}\nFailed: {FailedCount}",
                        "Processing Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: Queue processing failed: {ex.Message}");
                _logger.LogError($"Error processing queue: {ex}");
                System.Windows.MessageBox.Show($"Queue processing failed:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsProcessingQueue = false;
                IsQueuePaused = false;
                _pauseEvent.Set();
                CurrentQueueItem = null;
                QueueProgress = 0;
                QueueTotal = 0;
                UpdateQueueCountNotifications();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        protected void UpdateQueueCountNotifications()
        {
            OnPropertyChanged(nameof(QueuedCount));
            OnPropertyChanged(nameof(CompletedCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(QueueProgressText));
        }

        /// <summary>
        /// Creates a progress reporter that updates the item's Progress on the UI thread.
        /// </summary>
        protected IProgress<FlipPix.ComfyUI.Models.ProgressMessage> CreateProgressReporter(StoryPromptItem item)
        {
            return new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
            {
                if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                {
                    var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        item.Progress = percent;
                    });
                }
            });
        }

        private void ClearQueue()
        {
            if (!QueueItems.Any()) return;

            var result = System.Windows.MessageBox.Show(
                $"Are you sure you want to clear all {QueueItems.Count} items from the queue?",
                "Clear Queue",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                QueueItems.Clear();
                SaveQueueToFile();
                AddLog("Queue cleared");
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void OpenOutputFolder()
        {
            try
            {
                var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", OutputFolderName);
                if (Directory.Exists(outputDir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = outputDir,
                        UseShellExecute = true
                    });
                }
                else
                {
                    System.Windows.MessageBox.Show("Output folder does not exist yet. Generate some images first.", "Info",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR opening output folder: {ex.Message}");
            }
        }

        private void CancelProcessing()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                AddLog("Cancellation requested by user");
                _cancellationTokenSource.Cancel();
                ProcessingStatus = "Cancelling...";
            }
        }

        private void PauseQueue()
        {
            IsQueuePaused = true;
            _pauseEvent.Reset();
            AddLog("Queue paused");
        }

        private void ResumeQueue()
        {
            IsQueuePaused = false;
            _pauseEvent.Set();
            AddLog("Queue resumed");
        }

        private string QueueFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "queue", QueuePersistenceFileName);

        protected void SaveQueueToFile()
        {
            try
            {
                var queueDir = Path.GetDirectoryName(QueueFilePath);
                if (!string.IsNullOrEmpty(queueDir) && !Directory.Exists(queueDir))
                {
                    Directory.CreateDirectory(queueDir);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(QueueItems.ToList(), options);
                File.WriteAllText(QueueFilePath, json);
            }
            catch (Exception ex)
            {
                AddLog($"Error saving queue to file: {ex.Message}");
            }
        }

        private void LoadQueueFromFile()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;

                var json = File.ReadAllText(QueueFilePath);
                var savedItems = JsonSerializer.Deserialize<List<StoryPromptItem>>(json);

                if (savedItems != null && savedItems.Any())
                {
                    _queueItems.Clear();
                    foreach (var item in savedItems)
                    {
                        if (item.Status == "Processing")
                        {
                            item.Status = "Failed";
                            item.ErrorMessage = "Interrupted by crash or app restart";
                        }
                        _queueItems.Add(item);
                    }
                    UpdateQueueCountNotifications();
                    AddLog($"Queue loaded from file: {_queueItems.Count} items");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error loading queue from file: {ex.Message}");
            }
        }

        protected void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogOutput += $"[{timestamp}] {message}\n";
            _logger.LogInfo(message);
        }

        protected static string GetUniqueFolderPath(string baseDir, string folderName)
        {
            var folderPath = Path.Combine(baseDir, folderName);

            if (!Directory.Exists(folderPath))
            {
                return folderPath;
            }

            int counter = 2;
            string newFolderPath;
            do
            {
                newFolderPath = Path.Combine(baseDir, $"{folderName} ({counter})");
                counter++;
            } while (Directory.Exists(newFolderPath));

            return newFolderPath;
        }

        /// <summary>
        /// Shared LoRA model path resolution from ComfyUI's extra_model_paths.yaml.
        /// Used by Z and Amateur variants.
        /// </summary>
        protected string? GetLoraModelPath()
        {
            return _loraManager.ResolveLoraPath();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
                _pauseEvent?.Dispose();

                // Clear collections
                QueueItems.Clear();

                // Clear string properties
                _promptJsonFilePath = string.Empty;
                _inputImagePath = string.Empty;
                _logOutput = string.Empty;
                _processingStatus = string.Empty;
                _negativePrompt = string.Empty;

                _disposed = true;
            }
        }
    }
}
