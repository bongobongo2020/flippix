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
using WinForms = System.Windows.Forms;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using YamlDotNet.Serialization;

namespace FlipPix.UI.ViewModels
{
    public class StoryImageGeneratorViewModel : INotifyPropertyChanged
    {
        private readonly FlipPix.ComfyUI.Services.ComfyUIService _comfyUIService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly WorkflowQueueCoordinator _workflowCoordinator;

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
        private System.Threading.CancellationTokenSource? _cancellationTokenSource;
        private bool _isQueuePaused = false;
        private readonly ManualResetEventSlim _pauseEvent = new(true);

        // Generation settings
        private int _steps = 8;
        private double _cfg = 1.5;
        private double _denoise = 0.98;
        private double _denoise2 = 0.85;
        private string _negativePrompt = "";

        // Style workflows (loaded from ZStyles folder)
        private List<StyleInfo> _allStyles = new List<StyleInfo>();
        private int _selectedStyleIndex = 0;

        // LoRA settings
        private ObservableCollection<string> _availableLoras = new();
        private string _selectedLora = string.Empty;
        private bool _loraEnabled = false;
        private double _loraStrengthModel = 1.0;
        private double _loraStrengthClip = 1.0;

        // Photo Style settings
        private ObservableCollection<string> _availableStyles = new();
        private string _selectedStyle = "Phone Photo";
        private bool _spicyContentEnabled = false;
        private string _customStyleTemplate = "";

        // Resolution/Orientation settings
        private ObservableCollection<string> _availableOrientations = new();
        private string _selectedOrientation = "Portrait (944x1408)";

        // Upscale settings
        private bool _upscaleEnabled = true;
        private ObservableCollection<string> _upscaleMethods = new();
        private string _upscaleMethod = "Photo";

        public StoryImageGeneratorViewModel(
            FlipPix.ComfyUI.Services.ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            WorkflowQueueCoordinator workflowCoordinator)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _workflowCoordinator = workflowCoordinator ?? throw new ArgumentNullException(nameof(workflowCoordinator));

            // Initialize commands
            SelectPromptJsonCommand = new RelayCommand(SelectPromptJson);
            SelectInputImageCommand = new RelayCommand(SelectInputImage);
            LoadPromptsCommand = new RelayCommand(async () => await LoadPromptsAsync(), () => CanLoadPrompts);
            ProcessQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => CanProcessQueue);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => QueueItems.Any());
            OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
            CancelProcessingCommand = new RelayCommand(CancelProcessing, () => IsProcessing);
            RefreshLorasCommand = new RelayCommand(RefreshLoras);
            PauseQueueCommand = new RelayCommand(PauseQueue, () => IsProcessingQueue && !IsQueuePaused);
            ResumeQueueCommand = new RelayCommand(ResumeQueue, () => IsProcessingQueue && IsQueuePaused);

            // Load available Loras
            LoadAvailableLoras();

            // Load workflows and styles from ZStyles folder
            LoadWorkflowsAndStyles();

            // Initialize available styles (legacy - for backward compatibility)
            InitializeAvailableStyles();

            // Initialize orientations
            InitializeOrientations();

            // Initialize upscale methods
            InitializeUpscaleMethods();

            LoadQueueFromFile();

            AddLog("Story Image Generator initialized");
        }

        private void InitializeAvailableStyles()
        {
            AvailableStyles.Clear();
            AvailableStyles.Add("Phone Photo");
            AvailableStyles.Add("Oil Painting");
            AvailableStyles.Add("Watercolor");
            AvailableStyles.Add("Vintage Film");
            AvailableStyles.Add("Cinematic");
            AvailableStyles.Add("Pencil Sketch");
            AvailableStyles.Add("Anime");
            AvailableStyles.Add("3D Render");
            AvailableStyles.Add("Digital Art");
            AvailableStyles.Add("Pop Art");
        }

        private void InitializeOrientations()
        {
            AvailableOrientations.Clear();
            AvailableOrientations.Add("Portrait (944x1408)");
            AvailableOrientations.Add("Landscape (1408x944)");
            AvailableOrientations.Add("Square (1088x1088)");
        }

        private void InitializeUpscaleMethods()
        {
            UpscaleMethods.Clear();
            UpscaleMethods.Add("Photo");
        }

        private void LoadWorkflowsAndStyles()
        {
            try
            {
                // Clear previous styles
                _allStyles.Clear();

                var workflowDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "ZStyles");

                if (!Directory.Exists(workflowDir))
                {
                    AddLog($"ZStyles workflow directory not found at {workflowDir}");
                    return;
                }

                // Load all workflow JSON files from ZStyles folder
                var workflowFiles = Directory.GetFiles(workflowDir, "*.json");
                AddLog($"Found {workflowFiles.Length} workflow files in {workflowDir}");

                foreach (var workflowFile in workflowFiles)
                {
                    try
                    {
                        // Extract style name from filename (e.g., "Z3drender.json" -> "3drender")
                        var fileName = Path.GetFileNameWithoutExtension(workflowFile);
                        var styleName = fileName.StartsWith("Z") ? fileName.Substring(1) : fileName;

                        // Add style info for this workflow file
                        _allStyles.Add(new StyleInfo
                        {
                            Name = styleName,
                            PromptTemplate = "",  // Will be filled from prompt text
                            WorkflowFile = workflowFile,
                            NodeId = ""  // These are complete workflows, no single style node
                        });

                        AddLog($"Loaded style: {styleName} from {Path.GetFileName(workflowFile)}");
                    }
                    catch (Exception ex)
                    {
                        AddLog($"Error loading workflow file {workflowFile}: {ex.Message}");
                    }
                }

                // Sort styles alphabetically
                _allStyles = _allStyles.OrderBy(s => s.Name).ToList();

                AddLog($"Loaded {_allStyles.Count} total styles from ZStyles workflows");
                OnPropertyChanged(nameof(StyleNames));
            }
            catch (Exception ex)
            {
                AddLog($"Error loading workflows: {ex.Message}");
            }
        }

        // Properties
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

        public ICommand PauseQueueCommand { get; }
        public ICommand ResumeQueueCommand { get; }

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

        public string QueueProgressText => QueueItems.Count > 0 ? $"{CompletedCount}/{QueueItems.Count} ({QueuedCount} remaining)" : "0/0";

        public bool CanLoadPrompts => !string.IsNullOrEmpty(PromptJsonFilePath) &&
                                      File.Exists(PromptJsonFilePath);

        public bool CanProcessQueue => QueueItems.Any(item => item.Status == "Queued") &&
                                       !IsProcessingQueue;

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

        // Settings Properties
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

        // LoRA Properties
        public ObservableCollection<string> AvailableLoras
        {
            get => _availableLoras;
            set
            {
                if (_availableLoras != value)
                {
                    _availableLoras = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SelectedLora
        {
            get => _selectedLora;
            set
            {
                if (_selectedLora != value)
                {
                    _selectedLora = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool LoraEnabled
        {
            get => _loraEnabled;
            set
            {
                if (_loraEnabled != value)
                {
                    _loraEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public double LoraStrengthModel
        {
            get => _loraStrengthModel;
            set
            {
                if (_loraStrengthModel != value)
                {
                    _loraStrengthModel = value;
                    OnPropertyChanged();
                }
            }
        }

        public double LoraStrengthClip
        {
            get => _loraStrengthClip;
            set
            {
                if (_loraStrengthClip != value)
                {
                    _loraStrengthClip = value;
                    OnPropertyChanged();
                }
            }
        }

        // Workflow Style Properties (from ZStyles)
        public int SelectedStyleIndex
        {
            get => _selectedStyleIndex;
            set
            {
                if (_selectedStyleIndex != value)
                {
                    _selectedStyleIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        public string[] StyleNames => _allStyles.Select(s => s.Name).ToArray();

        public StyleInfo? SelectedWorkflowStyle => _allStyles.Count > 0 ? _allStyles[Math.Min(SelectedStyleIndex, _allStyles.Count - 1)] : null;

        // Style Properties (Legacy - kept for compatibility)
        public ObservableCollection<string> AvailableStyles
        {
            get => _availableStyles;
            set
            {
                if (_availableStyles != value)
                {
                    _availableStyles = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SelectedStyle
        {
            get => _selectedStyle;
            set
            {
                if (_selectedStyle != value)
                {
                    _selectedStyle = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool SpicyContentEnabled
        {
            get => _spicyContentEnabled;
            set
            {
                if (_spicyContentEnabled != value)
                {
                    _spicyContentEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CustomStyleTemplate
        {
            get => _customStyleTemplate;
            set
            {
                if (_customStyleTemplate != value)
                {
                    _customStyleTemplate = value;
                    OnPropertyChanged();
                }
            }
        }

        // Resolution/Orientation Properties
        public ObservableCollection<string> AvailableOrientations
        {
            get => _availableOrientations;
            set
            {
                if (_availableOrientations != value)
                {
                    _availableOrientations = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SelectedOrientation
        {
            get => _selectedOrientation;
            set
            {
                if (_selectedOrientation != value)
                {
                    _selectedOrientation = value;
                    OnPropertyChanged();
                }
            }
        }

        // Upscale Properties
        public bool UpscaleEnabled
        {
            get => _upscaleEnabled;
            set
            {
                if (_upscaleEnabled != value)
                {
                    _upscaleEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public string UpscaleMethod
        {
            get => _upscaleMethod;
            set
            {
                if (_upscaleMethod != value)
                {
                    _upscaleMethod = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<string> UpscaleMethods
        {
            get => _upscaleMethods;
            set
            {
                if (_upscaleMethods != value)
                {
                    _upscaleMethods = value;
                    OnPropertyChanged();
                }
            }
        }

        public double Denoise2
        {
            get => _denoise2;
            set
            {
                if (_denoise2 != value)
                {
                    _denoise2 = value;
                    OnPropertyChanged();
                }
            }
        }

        // Commands
        public ICommand SelectPromptJsonCommand { get; }
        public ICommand SelectInputImageCommand { get; }
        public ICommand LoadPromptsCommand { get; }
        public ICommand ProcessQueueCommand { get; }
        public ICommand ClearQueueCommand { get; }
        public ICommand OpenOutputFolderCommand { get; }
        public ICommand CancelProcessingCommand { get; }
        public ICommand RefreshLorasCommand { get; }

        // Methods
        private void SelectPromptJson()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                Title = "Select Story Prompts JSON File"
            };

            // Use the last used folder if available
            var lastFolder = _settingsService.Settings?.StoryImageGeneratorPromptJsonFolder;
            if (!string.IsNullOrEmpty(lastFolder) && Directory.Exists(lastFolder))
            {
                dialog.InitialDirectory = lastFolder;
            }
            else
            {
                dialog.InitialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts");
            }

            if (dialog.ShowDialog() == true)
            {
                PromptJsonFilePath = dialog.FileName;

                // Save the folder for next time
                var folder = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folder) && _settingsService.Settings != null)
                {
                    _settingsService.Settings.StoryImageGeneratorPromptJsonFolder = folder;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }

                AddLog($"Selected prompt file: {Path.GetFileName(PromptJsonFilePath)}");
            }
        }

        private void SelectInputImage()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All Files (*.*)|*.*",
                Title = "Select Input Image"
            };

            if (dialog.ShowDialog() == true)
            {
                InputImagePath = dialog.FileName;
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

        private async Task LoadPromptsAsync()
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

                // Append to existing queue (calculate next index from existing items)
                var startIndex = QueueItems.Any() ? QueueItems.Max(q => q.Index) + 1 : 1;

                // Create queue items for each prompt
                for (int i = 0; i < storyData.Prompts.Count; i++)
                {
                    QueueItems.Add(new StoryPromptItem
                    {
                        Index = startIndex + i,
                        Prompt = storyData.Prompts[i],
                        InputImagePath = InputImagePath, // All items use the same input image
                        Status = "Queued",
                        // Snapshot current settings
                        StyleName = SelectedWorkflowStyle?.Name ?? "",
                        StyleWorkflowFile = SelectedWorkflowStyle?.WorkflowFile ?? "",
                        LoraEnabled = LoraEnabled,
                        SelectedLora = SelectedLora,
                        LoraStrengthModel = LoraStrengthModel,
                        LoraStrengthClip = LoraStrengthClip,
                        SelectedStyle = SelectedStyle,
                        SpicyContentEnabled = SpicyContentEnabled,
                        NegativePrompt = NegativePrompt,
                        SelectedOrientation = SelectedOrientation,
                    });
                }

                UpdateQueueCountNotifications();
                SaveQueueToFile();
                AddLog($"Added {storyData.Prompts.Count} prompts to queue (total: {QueueItems.Count})");
                System.Windows.MessageBox.Show($"Added {storyData.Prompts.Count} prompts to the queue.\nTotal queue items: {QueueItems.Count}", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading prompts: {ex.Message}");
                _logger.LogError($"Error loading prompts: {ex}");
                System.Windows.MessageBox.Show($"Error loading prompts:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ProcessQueueAsync()
        {
            if (!CanProcessQueue) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new System.Threading.CancellationTokenSource();

            AddLog("Waiting for other workflows to finish...");

            try
            {
                await _workflowCoordinator.AcquireAsync("StoryImageZ", _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue processing cancelled while waiting");
                return;
            }

            try
            {
                IsProcessingQueue = true;
                QueueTotal = QueueItems.Count;
                QueueProgress = 0;

                // Create output folder for this queue processing session (named after JSON file)
                var jsonFileName = Path.GetFileNameWithoutExtension(PromptJsonFilePath);
                var baseOutputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "story-generator");
                Directory.CreateDirectory(baseOutputDir);

                // Create sequential folder if it already exists
                var sessionOutputDir = GetUniqueFolderPath(baseOutputDir, jsonFileName);
                Directory.CreateDirectory(sessionOutputDir);

                AddLog($"=== Starting story queue processing ({QueuedCount} images) ===");
                AddLog($"Output folder: {sessionOutputDir}");

                while (true)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        AddLog("Queue processing cancelled");
                        break;
                    }

                    // Wait if paused
                    _pauseEvent.Wait(_cancellationTokenSource.Token);

                    var item = QueueItems.FirstOrDefault(i => i.Status == "Queued");
                    if (item == null) break;

                    QueueTotal = QueueItems.Count;

                    // Check ComfyUI status before processing each item
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
                        continue; // Try next item instead of breaking
                    }

                    // Ensure ComfyUI is connected
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

                    CurrentQueueItem = item;
                    item.Status = "Processing";
                    item.StartedAt = DateTime.Now;
                    item.InputImagePath = InputImagePath; // Always use the original input image
                    SaveQueueToFile();

                    AddLog($"Processing story image {QueueProgress + 1}/{QueueTotal}");

                    try
                    {
                        // Process the current queue item using the original input image and shared output folder
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

                        // Check if this might be a ComfyUI crash and try to detect it
                        AddLog("Checking if ComfyUI crashed after error...");
                        await _comfyUIService.DetectAndRestartIfCrashedAsync(
                            status => AddLog($"[Post-Error Check] {status}"),
                            _cancellationTokenSource.Token);
                    }
                    finally
                    {
                        QueueProgress++;
                        UpdateQueueCountNotifications();
                    }
                }

                AddLog($"=== Story queue processing completed ({CompletedCount} successful, {FailedCount} failed) ===");
                System.Windows.MessageBox.Show($"Story generation completed!\n\nSuccessful: {CompletedCount}\nFailed: {FailedCount}",
                    "Processing Complete", MessageBoxButton.OK, MessageBoxImage.Information);
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
                _workflowCoordinator.Release();
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

        private void UpdateQueueCountNotifications()
        {
            OnPropertyChanged(nameof(QueuedCount));
            OnPropertyChanged(nameof(CompletedCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(QueueProgressText));
        }

        private async Task<string> ProcessQueueItemAsync(StoryPromptItem item, string inputImagePath, string outputDir, string jsonFileName, System.Threading.CancellationToken cancellationToken)
        {
            // Ensure ComfyUI is connected
            if (!_comfyUIService.IsConnected)
            {
                await _comfyUIService.ConnectAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Get style from item snapshot
            if (string.IsNullOrEmpty(item.StyleWorkflowFile))
            {
                throw new InvalidOperationException("No style selected. Please select a style from the ZStyles workflows.");
            }

            AddLog($"Using style: {item.StyleName} from workflow: {Path.GetFileName(item.StyleWorkflowFile)}");

            // Load workflow from item's snapshotted style
            if (!File.Exists(item.StyleWorkflowFile))
            {
                throw new FileNotFoundException($"Workflow file not found: {item.StyleWorkflowFile}");
            }

            var workflowJson = await File.ReadAllTextAsync(item.StyleWorkflowFile, cancellationToken);
            var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

            cancellationToken.ThrowIfCancellationRequested();

            // Update workflow parameters (text-to-image, no input image needed)
            var updatedWorkflow = UpdateWorkflowParameters(workflow, item);

            // Execute workflow with progress reporting
            var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
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

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, cancellationToken);

            // Get output images
            var outputImages = await GetOutputImagesFromComfyUI(promptId);
            if (!outputImages.Any())
            {
                throw new InvalidOperationException("No output images were generated");
            }

            var outputImage = outputImages.First();

            // Generate sequential filename: jsonfilename-1.png, jsonfilename-2.png, etc.
            var outputPath = Path.Combine(outputDir, $"{jsonFileName}-{item.Index}.png");

            await File.WriteAllBytesAsync(outputPath, outputImage);
            AddLog($"Story image #{item.Index} saved: {outputPath} ({outputImage.Length} bytes)");
            return outputPath;
        }

        private string SanitizeFolderName(string prompt)
        {
            // Remove invalid filename characters and limit length
            var invalidChars = Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).ToArray();
            var sanitized = string.Join("_", prompt.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

            // Take first 50 characters to avoid path length issues
            if (sanitized.Length > 50)
            {
                sanitized = sanitized.Substring(0, 50);
            }

            // Remove leading/trailing spaces and dots
            sanitized = sanitized.Trim().Trim('.');

            // If empty after sanitization, use a default name
            if (string.IsNullOrEmpty(sanitized))
            {
                sanitized = "unnamed";
            }

            return sanitized;
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, StoryPromptItem item)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            // 1. Update user prompt (node 385 - StringTrim)
            if (workflowDict.ContainsKey("385"))
            {
                var node385 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["385"].GetRawText());
                if (node385 != null && node385.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node385["inputs"]));
                    if (inputs != null)
                    {
                        inputs["string"] = item.Prompt;
                        node385["inputs"] = inputs;
                        workflowDict["385"] = JsonSerializer.SerializeToElement(node385);
                    }
                }
            }

            // 2. Update negative prompt (node 60)
            if (workflowDict.ContainsKey("60"))
            {
                var node60 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["60"].GetRawText());
                if (node60 != null && node60.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node60["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = item.NegativePrompt;
                        node60["inputs"] = inputs;
                        workflowDict["60"] = JsonSerializer.SerializeToElement(node60);
                    }
                }
            }

            // 3. Update seed (node 307)
            if (workflowDict.ContainsKey("307"))
            {
                var node307 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["307"].GetRawText());
                if (node307 != null && node307.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node307["inputs"]));
                    if (inputs != null)
                    {
                        // Generate random seed
                        var random = new Random();
                        int seed = random.Next(1, int.MaxValue);
                        inputs["value"] = seed;
                        node307["inputs"] = inputs;
                        workflowDict["307"] = JsonSerializer.SerializeToElement(node307);
                    }
                }
            }

            // 4. Update style template (node 125)
            if (workflowDict.ContainsKey("125"))
            {
                var node125 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["125"].GetRawText());
                if (node125 != null && node125.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node125["inputs"]));
                    if (inputs != null)
                    {
                        // Build the style template
                        var styleTemplate = GetStyleTemplateForWorkflow(item.SelectedStyle, item.SpicyContentEnabled);
                        inputs["value"] = styleTemplate;
                        node125["inputs"] = inputs;
                        workflowDict["125"] = JsonSerializer.SerializeToElement(node125);
                    }
                }
            }

            // 5. Update LoRA settings - always process to disable hardcoded loras when not enabled
            AddLog($"Processing LoRA nodes - LoRA Enabled: {item.LoraEnabled}, Selected LoRA: {item.SelectedLora}");

            // Iterate through all nodes to find Power Lora Loader nodes
            foreach (var kvp in workflowDict)
            {
                var nodeElement = kvp.Value;
                if (nodeElement.TryGetProperty("class_type", out var classTypeElement))
                {
                    var classTypeStr = classTypeElement.GetString();
                    if (classTypeStr == "Power Lora Loader (rgthree)" && nodeElement.TryGetProperty("inputs", out var loraInputsProp))
                    {
                        AddLog($"Found Power Lora Loader node {kvp.Key}");

                        var nodeDict = JsonSerializer.Deserialize<Dictionary<string, object>>(nodeElement.GetRawText());
                        if (nodeDict != null && nodeDict.ContainsKey("inputs"))
                        {
                            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                JsonSerializer.Serialize(nodeDict["inputs"]));
                            if (inputs != null)
                            {
                                // Power Lora Loader uses lora_1, lora_2, etc. with structure:
                                // { "on": true, "lora": "path/to/lora.safetensors", "strength": 1.0 }

                                if (item.LoraEnabled)
                                {
                                    // Enable and update with selected LoRA
                                    bool loraUpdated = false;
                                    for (int i = 1; i <= 10; i++)
                                    {
                                        string loraKey = $"lora_{i}";
                                        if (inputs.ContainsKey(loraKey))
                                        {
                                            var loraEntryJson = JsonSerializer.Serialize(inputs[loraKey]);
                                            var loraEntry = JsonSerializer.Deserialize<Dictionary<string, object>>(loraEntryJson);

                                            if (loraEntry != null && loraEntry.ContainsKey("on"))
                                            {
                                                var onValue = loraEntry["on"];
                                                bool isOn = onValue is bool b && b;

                                                if (isOn)
                                                {
                                                    // Update this lora entry with proper path format
                                                    loraEntry["lora"] = $"zimage\\{item.SelectedLora}.safetensors";
                                                    loraEntry["strength"] = item.LoraStrengthModel;
                                                    inputs[loraKey] = loraEntry;
                                                    loraUpdated = true;
                                                    AddLog($"Updated {loraKey} with LoRA: {item.SelectedLora}.safetensors (Strength: {item.LoraStrengthModel})");
                                                    break; // Only update the first enabled lora
                                                }
                                            }
                                        }
                                    }

                                    // If no lora was enabled, enable lora_1
                                    if (!loraUpdated && inputs.ContainsKey("lora_1"))
                                    {
                                        var loraEntryJson = JsonSerializer.Serialize(inputs["lora_1"]);
                                        var loraEntry = JsonSerializer.Deserialize<Dictionary<string, object>>(loraEntryJson);

                                        if (loraEntry != null)
                                        {
                                            loraEntry["on"] = true;
                                            loraEntry["lora"] = $"zimage\\{item.SelectedLora}.safetensors";
                                            loraEntry["strength"] = item.LoraStrengthModel;
                                            inputs["lora_1"] = loraEntry;
                                            AddLog($"Enabled and updated lora_1 with LoRA: {item.SelectedLora}.safetensors (Strength: {item.LoraStrengthModel})");
                                        }
                                    }
                                }
                                else
                                {
                                    // Disable all loras
                                    for (int i = 1; i <= 10; i++)
                                    {
                                        string loraKey = $"lora_{i}";
                                        if (inputs.ContainsKey(loraKey))
                                        {
                                            var loraEntryJson = JsonSerializer.Serialize(inputs[loraKey]);
                                            var loraEntry = JsonSerializer.Deserialize<Dictionary<string, object>>(loraEntryJson);

                                            if (loraEntry != null && loraEntry.ContainsKey("on"))
                                            {
                                                loraEntry["on"] = false;
                                                inputs[loraKey] = loraEntry;
                                                AddLog($"Disabled {loraKey} (LoRA not enabled in settings)");
                                            }
                                        }
                                    }
                                    AddLog("All LoRAs disabled in workflow");
                                }

                                nodeDict["inputs"] = inputs;
                                workflowDict[kvp.Key] = JsonSerializer.SerializeToElement(nodeDict);
                            }
                        }
                    }
                }
            }

            // 6. Update output filename prefix with timestamp (node 9)
            if (workflowDict.ContainsKey("9"))
            {
                var node9 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["9"].GetRawText());
                if (node9 != null && node9.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node9["inputs"]));
                    if (inputs != null)
                    {
                        var timestamp = DateTime.Now.ToString("yyyy_MM_dd");
                        inputs["filename_prefix"] = $"ZImage/{timestamp}/ZI";
                        node9["inputs"] = inputs;
                        workflowDict["9"] = JsonSerializer.SerializeToElement(node9);
                    }
                }
            }

            // 7. Update resolution/orientation (nodes 56, 243, 248)
            UpdateResolution(workflowDict, item.SelectedOrientation);

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private void UpdateResolution(Dictionary<string, JsonElement> workflowDict, string selectedOrientation)
        {
            // Parse orientation and set dimensions
            int width = 944;
            int height = 1408;

            switch (selectedOrientation)
            {
                case "Portrait (944x1408)":
                    width = 944;
                    height = 1408;
                    break;
                case "Landscape (1408x944)":
                    width = 1408;
                    height = 944;
                    break;
                case "Square (1088x1088)":
                    width = 1088;
                    height = 1088;
                    break;
            }

            // Update EmptyLatentImage (node 56) - This is used by the KSamplerAdvanced
            if (workflowDict.ContainsKey("56"))
            {
                var node56 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["56"].GetRawText());
                if (node56 != null && node56.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node56["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = width;
                        inputs["height"] = height;
                        node56["inputs"] = inputs;
                        workflowDict["56"] = JsonSerializer.SerializeToElement(node56);
                    }
                }
            }

            // Update EmptySD3LatentImage (node 244) - This is used for the main generation
            // It gets width/height from switches, so we need to update the source values
            // Node 244 gets width from node 536, which gets from node 534, which gets from node 243 (short side)
            // Node 244 gets height from node 537, which gets from node 249, which gets from node 248 (long side)

            // For portrait: width should be short (1088), height should be long (1600)
            // For landscape: width should be long (1600), height should be short (1088)
            // For square: both should be 1088

            int shortSide = 1088;
            int longSide = 1600;
            int widthSD3 = width;
            int heightSD3 = height;

            // For SD3, we need to map the actual width/height to short/long sides
            if (selectedOrientation == "Portrait (944x1408)")
            {
                widthSD3 = shortSide;  // 1088
                heightSD3 = longSide;  // 1600
            }
            else if (selectedOrientation == "Landscape (1408x944)")
            {
                widthSD3 = longSide;   // 1600
                heightSD3 = shortSide;  // 1088
            }
            else // Square
            {
                widthSD3 = 1088;
                heightSD3 = 1088;
            }

            // Update Short Side (node 243) - This feeds into width for SD3
            if (workflowDict.ContainsKey("243"))
            {
                var node243 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["243"].GetRawText());
                if (node243 != null && node243.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node243["inputs"]));
                    if (inputs != null)
                    {
                        inputs["value"] = widthSD3;
                        node243["inputs"] = inputs;
                        workflowDict["243"] = JsonSerializer.SerializeToElement(node243);
                    }
                }
            }

            // Update Long Side (node 248) - This feeds into height for SD3
            if (workflowDict.ContainsKey("248"))
            {
                var node248 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["248"].GetRawText());
                if (node248 != null && node248.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node248["inputs"]));
                    if (inputs != null)
                    {
                        inputs["value"] = heightSD3;
                        node248["inputs"] = inputs;
                        workflowDict["248"] = JsonSerializer.SerializeToElement(node248);
                    }
                }
            }

            AddLog($"Orientation: {selectedOrientation} -> Node56: {width}x{height}, SD3: {widthSD3}x{heightSD3}");
        }

        private string GetStyleTemplate(string selectedStyle)
        {
            return selectedStyle switch
            {
                "Phone Photo" => "YOUR CONTEXT:\nYour photographs has android phone cam-quality.\nYour photographs exhibit {$spicy-content-with} surprising compositions, sharp complex backgrounds, natural lighting, and candid moments that feel immediate and authentic.\nYour photographs are actual gritty candid photographic background.\nYOUR PHOTO:\n{$@}",

                "Oil Painting" => "YOUR CONTEXT:\nYour artwork is a masterful oil painting on canvas.\nYour artwork exhibits {$spicy-content-with} rich brushstrokes, vibrant colors, dramatic lighting, classical composition, and museum-quality technique.\nYour artwork has visible texture, depth, and the expressive quality of traditional oil paintings.\nYOUR ARTWORK:\n{$@}",

                "Watercolor" => "YOUR CONTEXT:\nYour artwork is a delicate watercolor painting.\nYour artwork exhibits {$spicy-content-with} soft washes, transparent colors, fluid blends, paper texture showing through, and ethereal atmospheric effects.\nYour artwork has the spontaneous and expressive quality of traditional watercolor paintings.\nYOUR ARTWORK:\n{$@}",

                "Vintage Film" => "YOUR CONTEXT:\nYour photographs have vintage film camera quality from the 1970s-80s.\nYour photographs exhibit {$spicy-content-with} film grain, warm color grading, soft contrast, light leaks, and authentic nostalgic atmosphere.\nYour photographs have the soulful and timeless quality of vintage film photography.\nYOUR PHOTO:\n{$@}",

                "Cinematic" => "YOUR CONTEXT:\nYour photographs are cinematic film stills from a high-budget movie.\nYour photographs exhibit {$spicy-content-with} dramatic lighting, anamorphic lens bokeh, rich color grading, deep depth of field, and theatrical composition.\nYour photographs have the polished and atmospheric quality of professional cinematography.\nYOUR PHOTO:\n{$@}",

                "Pencil Sketch" => "YOUR CONTEXT:\nYour artwork is a detailed pencil sketch on paper.\nYour artwork exhibits {$spicy-content-with} precise linework, shading through hatching and cross-hatching, subtle graphite texture, and classical drawing technique.\nYour artwork has the expressive and intimate quality of hand-drawn pencil sketches.\nYOUR ARTWORK:\n{$@}",

                "Anime" => "YOUR CONTEXT:\nYour artwork is in the style of high-quality Japanese anime and manga art.\nYour artwork exhibits {$spicy-content-with} clean lines, vibrant cel-shaded colors, expressive eyes, dynamic poses, and polished anime aesthetic.\nYour artwork has the distinctive and appealing style of professional anime illustration.\nYOUR ARTWORK:\n{$@}",

                "3D Render" => "YOUR CONTEXT:\nYour artwork is a photorealistic 3D render using modern rendering techniques.\nYour artwork exhibits {$spicy-content-with} perfect lighting, subsurface scattering, realistic materials, global illumination, and high-end 3D quality.\nYour artwork has the polished and hyper-realistic quality of professional 3D rendering.\nYOUR ARTWORK:\n{$@}",

                "Digital Art" => "YOUR CONTEXT:\nYour artwork is high-quality digital art.\nYour artwork exhibits {$spicy-content-with} clean digital painting technique, vibrant colors, smooth gradients, perfect composition, and contemporary digital aesthetic.\nYour artwork has the polished and professional quality of modern digital illustration.\nYOUR ARTWORK:\n{$@}",

                "Pop Art" => "YOUR CONTEXT:\nYour artwork is in the style of Pop Art, inspired by artists like Andy Warhol and Roy Lichtenstein.\nYour artwork exhibits {$spicy-content-with} bold colors, halftone dots, comic book style, high contrast, and vibrant graphic design elements.\nYour artwork has the eye-catching and iconic quality of Pop Art movement.\nYOUR ARTWORK:\n{$@}",

                _ => "YOUR CONTEXT:\nYour photographs has android phone cam-quality.\nYour photographs exhibit {$spicy-content-with} surprising compositions, sharp complex backgrounds, natural lighting, and candid moments that feel immediate and authentic.\nYOUR PHOTO:\n{$@}"
            };
        }

        private string GetStyleTemplateForWorkflow(string selectedStyle, bool spicyContentEnabled)
        {
            var baseTemplate = GetStyleTemplate(selectedStyle);

            // Add spicy content modifier if enabled
            if (spicyContentEnabled)
            {
                return baseTemplate.Replace("{$spicy-content-with}", "erotic, sensual,");
            }
            else
            {
                return baseTemplate.Replace("{$spicy-content-with}", "");
            }
        }

        private async Task<List<byte[]>> GetOutputImagesFromComfyUI(string promptId)
        {
            var images = new List<byte[]>();

            try
            {
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;

                bool isRemoteComfyUI = IsComfyUIRemote(actualServer);

                AddLog($"ComfyUI server: {actualServer}");
                AddLog($"Is remote ComfyUI: {isRemoteComfyUI}");

                // Retry image retrieval with delays to give ComfyUI time to write the file
                int retryCount = 0;
                int maxRetries = 20; // Wait up to 100 seconds (20 retries × 5s)

                while (retryCount < maxRetries && !images.Any())
                {
                    if (retryCount > 0)
                    {
                        AddLog($"Retry {retryCount}/{maxRetries} - waiting 5 seconds before checking again...");
                        await Task.Delay(5000);
                    }

                    if (isRemoteComfyUI)
                    {
                        AddLog("Detected remote ComfyUI server, downloading generated image...");

                        var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
                        AddLog($"Found {outputFiles.Count} potential output files");

                        // Look for recent PNG files (story images)
                        var imageFiles = outputFiles.Where(f =>
                            f.EndsWith(".png") &&
                            !f.StartsWith("z-image_") &&
                            !f.StartsWith("temp_"))
                            .ToList();

                        if (imageFiles.Any())
                        {
                            var filename = imageFiles.Last();
                            AddLog($"Downloading generated image: {filename}");

                            var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filename);
                            if (imageData != null)
                            {
                                images.Add(imageData);
                                AddLog($"Successfully downloaded image ({imageData.Length} bytes)");
                            }
                        }
                        else
                        {
                            var fallbackImage = await _comfyUIService.HttpClient.TryDownloadRecentOutputAsync(promptId);
                            if (fallbackImage != null)
                            {
                                images.Add(fallbackImage);
                                AddLog($"Successfully downloaded image via fallback method ({fallbackImage.Length} bytes)");
                            }
                        }
                    }
                    else
                    {
                        var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                        if (string.IsNullOrEmpty(comfyUIOutputDir))
                        {
                            AddLog("ERROR: ComfyUI output folder not configured");
                            return images;
                        }

                        if (!Directory.Exists(comfyUIOutputDir))
                        {
                            AddLog($"ERROR: ComfyUI output folder not found: {comfyUIOutputDir}");
                            return images;
                        }

                        // Search in multiple locations:
                        // 1. Root output directory
                        // 2. ZImage subfolder (where the workflow saves)
                        // 3. ZImage/Date subfolders

                        var searchDirs = new List<string> { comfyUIOutputDir };

                        // Add ZImage subdirectories
                        var zimageDir = Path.Combine(comfyUIOutputDir, "ZImage");
                        if (Directory.Exists(zimageDir))
                        {
                            searchDirs.Add(zimageDir);
                            // Add date-named subfolders in ZImage
                            try
                            {
                                var dateDirs = Directory.GetDirectories(zimageDir)
                                    .OrderByDescending(d => Directory.GetLastWriteTime(d))
                                    .Take(3); // Check last 3 date folders
                                foreach (var dateDir in dateDirs)
                                {
                                    searchDirs.Add(dateDir);
                                }
                            }
                            catch { }
                        }

                        AddLog($"Searching in {searchDirs.Count} directories for output images");

                        foreach (var searchDir in searchDirs)
                        {
                            // Look for recently created images (within last 2 minutes)
                            var recentFiles = Directory.GetFiles(searchDir, "*.png")
                                .Select(f => new FileInfo(f))
                                .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 2)
                                .OrderByDescending(f => f.LastWriteTime)
                                .ToList();

                            if (recentFiles.Any())
                            {
                                AddLog($"Found {recentFiles.Count} recent PNG files in: {Path.GetFileName(searchDir)}");
                                var latestFile = recentFiles.First();
                                AddLog($"Using latest file: {latestFile.Name} (modified: {latestFile.LastWriteTime})");
                                images.Add(await File.ReadAllBytesAsync(latestFile.FullName));
                                break; // Found images, stop searching
                            }
                        }

                        if (!images.Any())
                        {
                            AddLog($"No recent images found in retry {retryCount + 1}");

                            // Fallback: look for ANY PNG file modified in the last 10 minutes in all search dirs
                            if (retryCount >= 5) // After 5 retries, look for older files too
                            {
                                foreach (var searchDir in searchDirs)
                                {
                                    var olderFiles = Directory.GetFiles(searchDir, "*.png")
                                        .Select(f => new FileInfo(f))
                                        .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 10)
                                        .OrderByDescending(f => f.LastWriteTime)
                                        .ToList();

                                    if (olderFiles.Any())
                                    {
                                        AddLog($"Fallback: Found {olderFiles.Count} PNG files in last 10 minutes in: {Path.GetFileName(searchDir)}");
                                        var latestFile = olderFiles.First();
                                        AddLog($"Using fallback file: {latestFile.Name} (modified: {latestFile.LastWriteTime})");
                                        images.Add(await File.ReadAllBytesAsync(latestFile.FullName));
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    retryCount++;
                }

                if (!images.Any())
                {
                    AddLog("WARNING: No output images received after all retries");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR retrieving output images: {ex.Message}");
            }

            return images;
        }

        private bool IsComfyUIRemote(string serverAddress)
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

        private void RefreshLoras()
        {
            LoadAvailableLoras();
            AddLog("Refreshed LoRA list");
        }

        private string? GetLoraModelPath()
        {
            try
            {
                var comfyUIPath = _settingsService.Settings?.ComfyUIFolderPath;
                if (string.IsNullOrEmpty(comfyUIPath))
                {
                    AddLog("ComfyUI installation path not configured");
                    return null;
                }

                // First try to get path from extra_model_paths.yaml
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

                            // Check for "comfyui" section (most common format)
                            if (yamlData.ContainsKey("comfyui"))
                            {
                                AddLog("Found 'comfyui' section in YAML");
                                var comfyuiSectionObject = yamlData["comfyui"];
                                var comfyuiSection = comfyuiSectionObject as Dictionary<object, object>;

                                if (comfyuiSection != null)
                                {
                                    // Convert to Dictionary<string, object> for easier use
                                    var comfyuiStringDict = new Dictionary<string, object>();
                                    foreach (var kvp in comfyuiSection)
                                    {
                                        if (kvp.Key != null)
                                        {
                                            comfyuiStringDict[kvp.Key.ToString() ?? string.Empty] = kvp.Value;
                                        }
                                    }

                                    AddLog($"ComfyUI section keys: {string.Join(", ", comfyuiStringDict.Keys)}");

                                    // Get base_path if it exists
                                    if (comfyuiStringDict.ContainsKey("base_path"))
                                    {
                                        basePath = comfyuiStringDict["base_path"]?.ToString() ?? string.Empty;
                                        AddLog($"Found base_path: {basePath}");
                                    }

                                    // Get loras path if it exists
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

                                // Fallback to direct "loras" key
                                if (yamlData.ContainsKey("loras"))
                                {
                                    lorasRelativePath = yamlData["loras"]?.ToString() ?? string.Empty;
                                    AddLog($"Found direct loras path: {lorasRelativePath}");
                                }
                            }

                            // Construct full path
                            if (!string.IsNullOrEmpty(lorasRelativePath))
                            {
                                string fullLoraPath;
                                if (!string.IsNullOrEmpty(basePath))
                                {
                                    // Combine base_path with loras relative path
                                    fullLoraPath = Path.Combine(basePath, lorasRelativePath);
                                    AddLog($"Combined base_path and loras: {basePath} + {lorasRelativePath} = {fullLoraPath}");
                                }
                                else
                                {
                                    // Use just the loras path (might be absolute)
                                    fullLoraPath = lorasRelativePath;
                                    AddLog($"Using loras path directly: {fullLoraPath}");
                                }

                                // Normalize path separators
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

                // Fallback to default ComfyUI models directory
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

        private void LoadAvailableLoras()
        {
            try
            {
                // Priority 1: Get LoRA path from ComfyUI extra_model_paths.yaml or default location
                var loraBasePath = GetLoraModelPath();
                if (!string.IsNullOrEmpty(loraBasePath))
                {
                    // Look for zimage subfolder
                    var zimageLoraPath = Path.Combine(loraBasePath, "zimage");
                    if (Directory.Exists(zimageLoraPath))
                    {
                        LoadLorasFromDirectory(zimageLoraPath, "ComfyUI LoRA directory");
                        return;
                    }
                    else
                    {
                        // If zimage subfolder doesn't exist, use the base LoRA directory
                        LoadLorasFromDirectory(loraBasePath, "ComfyUI LoRA directory");
                        return;
                    }
                }

                // Priority 2: Fallback to local directory
                var localLoraPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "loras", "zimage");
                LoadLorasFromDirectory(localLoraPath, "local directory");
            }
            catch (Exception ex)
            {
                AddLog($"Error loading LoRAs: {ex.Message}");
                AvailableLoras.Clear();
                AvailableLoras.Add("Error loading LoRAs");
            }
        }

        private void LoadLorasFromDirectory(string loraPath, string pathDescription)
        {
            AddLog($"Looking for LoRAs in {pathDescription}: {loraPath}");

            if (!Directory.Exists(loraPath))
            {
                AddLog($"LoRA directory not found: {loraPath}");
                AvailableLoras.Clear();
                AvailableLoras.Add("No LoRAs available");
                return;
            }

            var loraFiles = Directory.GetFiles(loraPath, "*.safetensors")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name))
                .OrderBy(name => name)
                .ToList();

            AvailableLoras.Clear();

            if (loraFiles.Any())
            {
                foreach (var lora in loraFiles)
                {
                    if (!string.IsNullOrEmpty(lora))
                        AvailableLoras.Add(lora);
                }

                if (string.IsNullOrEmpty(SelectedLora) && AvailableLoras.Any())
                {
                    SelectedLora = AvailableLoras.First();
                }

                AddLog($"Loaded {AvailableLoras.Count} LoRAs from {loraPath}");
            }
            else
            {
                AvailableLoras.Add("No LoRAs available");
                AddLog($"No LoRA files found in {pathDescription}");
            }
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
                var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "story-generator");
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

        private string QueueFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "queue", "story_image_queue.json");

        private void SaveQueueToFile()
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

        private void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogOutput += $"[{timestamp}] {message}\n";
            _logger.LogInfo(message);
        }

        private string GetUniqueFolderPath(string baseDir, string folderName)
        {
            var folderPath = Path.Combine(baseDir, folderName);

            // If the folder doesn't exist, return it as-is
            if (!Directory.Exists(folderPath))
            {
                return folderPath;
            }

            // If it exists, find the next available sequential number
            int counter = 2;
            string newFolderPath;
            do
            {
                newFolderPath = Path.Combine(baseDir, $"{folderName} ({counter})");
                counter++;
            } while (Directory.Exists(newFolderPath));

            return newFolderPath;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // RelayCommand class
        public class RelayCommand : ICommand
        {
            private readonly Action _execute;
            private readonly Func<bool>? _canExecute;

            public RelayCommand(Action execute, Func<bool>? canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public event EventHandler? CanExecuteChanged
            {
                add => CommandManager.RequerySuggested += value;
                remove => CommandManager.RequerySuggested -= value;
            }

            public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

            public void Execute(object? parameter) => _execute();
        }
    }
}
