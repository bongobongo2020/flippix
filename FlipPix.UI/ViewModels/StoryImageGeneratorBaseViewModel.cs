using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Commands;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using YamlDotNet.Serialization;

namespace FlipPix.UI.ViewModels
{
    public abstract class StoryImageGeneratorBaseViewModel : INotifyPropertyChanged
    {
        protected readonly ComfyUIService _comfyUIService;
        protected readonly IAppLogger _logger;
        protected readonly FlipPix.Core.Services.SettingsService _settingsService;
        protected readonly WorkflowQueueCoordinator _workflowCoordinator;

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
            WorkflowQueueCoordinator workflowCoordinator)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _workflowCoordinator = workflowCoordinator ?? throw new ArgumentNullException(nameof(workflowCoordinator));

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
                if (_promptJsonFilePath != value)
                {
                    _promptJsonFilePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanLoadPrompts));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string InputImagePath
        {
            get => _inputImagePath;
            set
            {
                if (_inputImagePath != value)
                {
                    _inputImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanLoadPrompts));
                    LoadInputImagePreview();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public BitmapImage? InputImagePreview
        {
            get => _inputImagePreview;
            set
            {
                if (_inputImagePreview != value)
                {
                    _inputImagePreview = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<StoryPromptItem> QueueItems
        {
            get => _queueItems;
            set
            {
                if (_queueItems != value)
                {
                    _queueItems = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            set
            {
                if (_isProcessingQueue != value)
                {
                    _isProcessingQueue = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanProcessQueue));
                    OnPropertyChanged(nameof(CanLoadPrompts));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsQueuePaused
        {
            get => _isQueuePaused;
            set
            {
                if (_isQueuePaused != value)
                {
                    _isQueuePaused = value;
                    OnPropertyChanged();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public StoryPromptItem? CurrentQueueItem
        {
            get => _currentQueueItem;
            set
            {
                if (_currentQueueItem != value)
                {
                    _currentQueueItem = value;
                    OnPropertyChanged();
                }
            }
        }

        public int QueueProgress
        {
            get => _queueProgress;
            set
            {
                if (_queueProgress != value)
                {
                    _queueProgress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(QueueProgressText));
                }
            }
        }

        public int QueueTotal
        {
            get => _queueTotal;
            set
            {
                if (_queueTotal != value)
                {
                    _queueTotal = value;
                    OnPropertyChanged();
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
            set
            {
                if (_logOutput != value)
                {
                    _logOutput = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (_isProcessing != value)
                {
                    _isProcessing = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ProcessingStatus
        {
            get => _processingStatus;
            set
            {
                if (_processingStatus != value)
                {
                    _processingStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public double ProcessingProgress
        {
            get => _processingProgress;
            set
            {
                if (_processingProgress != value)
                {
                    _processingProgress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ProgressPercentage));
                }
            }
        }

        public string ProgressPercentage => $"{ProcessingProgress:F0}%";

        public int Steps
        {
            get => _steps;
            set
            {
                if (_steps != value)
                {
                    _steps = value;
                    OnPropertyChanged();
                }
            }
        }

        public double Cfg
        {
            get => _cfg;
            set
            {
                if (_cfg != value)
                {
                    _cfg = value;
                    OnPropertyChanged();
                }
            }
        }

        public double Denoise
        {
            get => _denoise;
            set
            {
                if (_denoise != value)
                {
                    _denoise = value;
                    OnPropertyChanged();
                }
            }
        }

        public string NegativePrompt
        {
            get => _negativePrompt;
            set
            {
                if (_negativePrompt != value)
                {
                    _negativePrompt = value;
                    OnPropertyChanged();
                }
            }
        }

        // --- Commands ---

        public ICommand SelectPromptJsonCommand { get; }
        public ICommand SelectInputImageCommand { get; }
        public ICommand LoadPromptsCommand { get; }
        public ICommand ProcessQueueCommand { get; }
        public ICommand ClearQueueCommand { get; }
        public ICommand OpenOutputFolderCommand { get; }
        public ICommand CancelProcessingCommand { get; }
        public ICommand PauseQueueCommand { get; }
        public ICommand ResumeQueueCommand { get; }

        // --- Shared methods ---

        private void SelectPromptJson()
        {
            var initialDirectory = GetPromptJsonInitialDirectory();

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts");
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                Title = "Select Story Prompts JSON File",
                InitialDirectory = initialDirectory
            };

            if (dialog.ShowDialog() == true)
            {
                PromptJsonFilePath = dialog.FileName;

                var folderPath = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    SavePromptJsonFolder(folderPath);
                }

                AddLog($"Selected prompt file: {Path.GetFileName(PromptJsonFilePath)}");
            }
        }

        private void SelectInputImage()
        {
            var initialDirectory = GetInputImageInitialDirectory();

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All Files (*.*)|*.*",
                Title = "Select Input Image",
                InitialDirectory = initialDirectory
            };

            if (dialog.ShowDialog() == true)
            {
                InputImagePath = dialog.FileName;

                var folderPath = Path.GetDirectoryName(dialog.FileName);
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

                // Auto-start processing if enabled and not already processing
                if (AutoStartProcessing && CanProcessQueue)
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
                    item.InputImagePath = InputImagePath;
                    SaveQueueToFile();

                    AddLog($"Processing story image {QueueProgress + 1}/{QueueTotal}");

                    try
                    {
                        var outputPath = await ProcessQueueItemAsync(item, InputImagePath, sessionOutputDir, jsonFileName, _cancellationTokenSource.Token);
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

        protected bool IsComfyUIRemote(string serverAddress)
        {
            try
            {
                if (serverAddress.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    serverAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    serverAddress.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (System.Net.IPAddress.TryParse(serverAddress, out var ip))
                {
                    var bytes = ip.GetAddressBytes();
                    if (bytes.Length == 4)
                    {
                        if (bytes[0] == 192 && bytes[1] == 168) return true;
                        if (bytes[0] == 10) return true;
                        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                    }
                }

                return !string.IsNullOrEmpty(serverAddress) && serverAddress != ".";
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Shared LoRA model path resolution from ComfyUI's extra_model_paths.yaml.
        /// Used by Z and Amateur variants.
        /// </summary>
        protected string? GetLoraModelPath()
        {
            try
            {
                var comfyUIPath = _settingsService.Settings?.ComfyUIFolderPath;
                if (string.IsNullOrEmpty(comfyUIPath))
                {
                    AddLog("ComfyUI installation path not configured");
                    return null;
                }

                var extraModelPathsFile = Path.Combine(comfyUIPath, "extra_model_paths.yaml");
                AddLog($"Looking for extra_model_paths.yaml at: {extraModelPathsFile}");

                if (File.Exists(extraModelPathsFile))
                {
                    try
                    {
                        AddLog("Found extra_model_paths.yaml, reading content...");
                        var yamlContent = File.ReadAllText(extraModelPathsFile);
                        var deserializer = new DeserializerBuilder().Build();
                        var yamlData = deserializer.Deserialize<Dictionary<string, object>>(yamlContent);

                        AddLog($"YAML parsed successfully. Keys found: {string.Join(", ", yamlData.Keys)}");

                        if (yamlData != null)
                        {
                            string basePath = string.Empty;
                            string lorasRelativePath = string.Empty;

                            if (yamlData.ContainsKey("comfyui"))
                            {
                                AddLog("Found 'comfyui' section in YAML");
                                var comfyuiSectionObject = yamlData["comfyui"];
                                var comfyuiSection = comfyuiSectionObject as Dictionary<object, object>;

                                if (comfyuiSection != null)
                                {
                                    var comfyuiStringDict = new Dictionary<string, object>();
                                    foreach (var kvp in comfyuiSection)
                                    {
                                        if (kvp.Key != null)
                                        {
                                            comfyuiStringDict[kvp.Key.ToString() ?? string.Empty] = kvp.Value;
                                        }
                                    }

                                    AddLog($"ComfyUI section keys: {string.Join(", ", comfyuiStringDict.Keys)}");

                                    if (comfyuiStringDict.ContainsKey("base_path"))
                                    {
                                        basePath = comfyuiStringDict["base_path"]?.ToString() ?? string.Empty;
                                        AddLog($"Found base_path: {basePath}");
                                    }

                                    if (comfyuiStringDict.ContainsKey("loras"))
                                    {
                                        lorasRelativePath = comfyuiStringDict["loras"]?.ToString() ?? string.Empty;
                                        AddLog($"Found loras path: {lorasRelativePath}");
                                    }
                                    else
                                    {
                                        AddLog("No 'loras' key found in comfyui section");
                                    }
                                }
                            }
                            else
                            {
                                AddLog("No 'comfyui' section found in YAML");

                                if (yamlData.ContainsKey("loras"))
                                {
                                    lorasRelativePath = yamlData["loras"]?.ToString() ?? string.Empty;
                                    AddLog($"Found direct loras path: {lorasRelativePath}");
                                }
                            }

                            if (!string.IsNullOrEmpty(lorasRelativePath))
                            {
                                string fullLoraPath;
                                if (!string.IsNullOrEmpty(basePath))
                                {
                                    fullLoraPath = Path.Combine(basePath, lorasRelativePath);
                                    AddLog($"Combined base_path and loras: {basePath} + {lorasRelativePath} = {fullLoraPath}");
                                }
                                else
                                {
                                    fullLoraPath = lorasRelativePath;
                                    AddLog($"Using loras path directly: {fullLoraPath}");
                                }

                                fullLoraPath = fullLoraPath.Replace('/', Path.DirectorySeparatorChar);

                                AddLog($"Final LoRA path: {fullLoraPath}");

                                if (Directory.Exists(fullLoraPath))
                                {
                                    AddLog($"SUCCESS: LoRA directory exists: {fullLoraPath}");
                                    return fullLoraPath;
                                }
                                else
                                {
                                    AddLog($"ERROR: LoRA path from extra_model_paths.yaml exists but directory not found: {fullLoraPath}");
                                }
                            }
                            else
                            {
                                AddLog("ERROR: No loras path found in YAML configuration");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"ERROR reading extra_model_paths.yaml: {ex.Message}");
                    }
                }
                else
                {
                    AddLog($"ERROR: extra_model_paths.yaml not found in ComfyUI directory: {extraModelPathsFile}");
                }

                var defaultLoraPath = Path.Combine(comfyUIPath, "models", "loras");
                if (Directory.Exists(defaultLoraPath))
                {
                    AddLog($"Using default ComfyUI LoRA path: {defaultLoraPath}");
                    return defaultLoraPath;
                }

                AddLog($"No LoRA directory found in: {comfyUIPath}");
                return null;
            }
            catch (Exception ex)
            {
                AddLog($"Error getting LoRA model path: {ex.Message}");
                return null;
            }
        }

        // --- INotifyPropertyChanged ---

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
