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
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;

namespace FlipPix.UI.ViewModels
{
    public enum TextGeneratorWorkflow
    {
        Zimage,
        Qwen2512,
        Klien
    }

    public class ImageGeneratorViewModel : BasePromptViewModel, IDisposable
    {
        private readonly FlipPix.ComfyUI.Services.ComfyUIService _comfyUIService;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IServiceProvider? _serviceProvider;
        private readonly WorkflowQueueCoordinator _workflowCoordinator;
        private bool _disposed = false;

        private string _imagePrompt = string.Empty;
        private int _aspectRatioIndex = 0;
        private int _steps = 9;
        private double _cfg = 1.5;
        private long _seed = 0;
        private double _denoise = 1.0;
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;
        private string _comfyUIServer = "127.0.0.1";
        private string _comfyUIPort = "8188";
        private string _statusBarMessage = "Ready";
        private bool _hasResultImage = false;
        private string _resultImagePath = string.Empty;
        private BitmapImage? _resultImageSource;
        private string _imageInfo = string.Empty;
        private System.Threading.CancellationTokenSource? _cancellationTokenSource;
        private ObservableCollection<string> _availableLoras = new();
        private string _selectedLora = string.Empty;
        private bool _loraEnabled = false;
        private TextGeneratorWorkflow _selectedWorkflow = TextGeneratorWorkflow.Zimage;

        // Queue fields
        private ObservableCollection<ImagePromptQueueItem> _promptQueue = new();
        private ImagePromptQueueItem? _selectedQueueItem;
        private bool _isProcessingQueue = false;
        private bool _isQueuePaused = false;
        private readonly ManualResetEventSlim _pauseEvent = new(true);

        // Nested ViewModels for tabs
        private ImageAnalyzerViewModel _analyzer;
        private FlipPixViewModel _cameraEdit;
        private StoryImageGeneratorViewModel _storyGenerator;
        private StoryImageGeneratorQViewModel _storyGeneratorQ;
        private StoryImageGeneratorFViewModel _storyGeneratorF;
        private StoryImageGeneratorAmateurViewModel _storyGeneratorAmateur;
        private AmateurGeneratorViewModel _amateurGenerator;
        private CameraAngleViewModel _cameraAngle;

        
        public ImageGeneratorViewModel(FlipPix.ComfyUI.Services.ComfyUIService comfyUIService, IAppLogger logger, FlipPix.Core.Services.SettingsService settingsService, IServiceProvider? serviceProvider = null, IPromptService? promptService = null)
            : base(promptService ?? new PromptService(logger), logger, "ImageGenerator")
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _serviceProvider = serviceProvider;
            _workflowCoordinator = serviceProvider?.GetRequiredService<WorkflowQueueCoordinator>() ?? throw new InvalidOperationException("WorkflowQueueCoordinator is required");

            // Load default prompt from settings
            _imagePrompt = settingsService.Settings.DefaultImagePrompt;

            // Get IFileDialogService from service provider
            var fileDialogService = serviceProvider?.GetRequiredService<IFileDialogService>() ?? throw new InvalidOperationException("IFileDialogService is required");

            // Initialize nested ViewModels
            var lmStudioService = serviceProvider?.GetRequiredService<LMStudioService>();
            _analyzer = new ImageAnalyzerViewModel(comfyUIService, lmStudioService ?? throw new InvalidOperationException("LMStudioService is required"), logger, settingsService, _workflowCoordinator, fileDialogService);
            _cameraEdit = new FlipPixViewModel(comfyUIService, logger, settingsService, serviceProvider, promptService, fileDialogService);
            _storyGenerator = new StoryImageGeneratorViewModel(comfyUIService, logger, settingsService, _workflowCoordinator, fileDialogService);
            _storyGeneratorQ = new StoryImageGeneratorQViewModel(comfyUIService, logger, settingsService, _workflowCoordinator, fileDialogService);
            _storyGeneratorF = new StoryImageGeneratorFViewModel(comfyUIService, logger, settingsService, _workflowCoordinator, fileDialogService);
            _storyGeneratorAmateur = new StoryImageGeneratorAmateurViewModel(comfyUIService, logger, settingsService, _workflowCoordinator, fileDialogService);
            _amateurGenerator = new AmateurGeneratorViewModel(comfyUIService, logger, settingsService, promptService);
            _cameraAngle = new CameraAngleViewModel(comfyUIService, logger, settingsService, fileDialogService);

            // Initialize commands
            GenerateImageCommand = new RelayCommand(async () => await GenerateImageAsync(), () => CanGenerate);
            CancelGenerationCommand = new RelayCommand(CancelGeneration, () => IsProcessing);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResultImage);
            OpenResultImageCommand = new RelayCommand(OpenResultImage, () => HasResultImage);
            SendToCameraEditCommand = new RelayCommand(SendToCameraEdit, () => HasResultImage);
            SendToVideoGeneratorCommand = new RelayCommand(SendToVideoGenerator, () => HasResultImage);
            NavigateToCameraEditCommand = new RelayCommand(NavigateToCameraEdit);
            NavigateToImageAnalyzerCommand = new RelayCommand(NavigateToImageAnalyzer);
            NavigateToVideoGeneratorCommand = new RelayCommand(NavigateToVideoGenerator);
                NavigateToStoryVideoCommand = new RelayCommand(NavigateToStoryVideo);
            RefreshLorasCommand = new RelayCommand(RefreshLoras);

            // Queue commands
            AddToQueueCommand = new RelayCommand(AddToQueue, () => CanAddToQueue);
            RemoveFromQueueCommand = new RelayCommand<ImagePromptQueueItem>(RemoveFromQueue, (item) => item != null);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => CanClearQueue);
            ProcessQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => CanProcessQueue);
            PauseQueueCommand = new RelayCommand(PauseQueue, () => IsProcessingQueue && !IsQueuePaused);
            ResumeQueueCommand = new RelayCommand(ResumeQueue, () => IsProcessingQueue && IsQueuePaused);

            // Load available Loras
            LoadAvailableLoras();

            LoadQueueFromFile();

            AddLog("Image Generator initialized");
        }

        // Properties
        public string ImagePrompt
        {
            get => _imagePrompt;
            set
            {
                _imagePrompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public override int Steps
        {
            get => _steps;
            set
            {
                _steps = value;
                OnPropertyChanged();
            }
        }

        public override double Cfg
        {
            get => _cfg;
            set
            {
                _cfg = value;
                OnPropertyChanged();
            }
        }

        public override double Denoise
        {
            get => _denoise;
            set
            {
                _denoise = value;
                OnPropertyChanged();
            }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                _isProcessing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
                OnPropertyChanged(nameof(CanCancel));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool CanCancel => IsProcessing;

        // Nested ViewModel properties
        public ImageAnalyzerViewModel Analyzer => _analyzer;
        public FlipPixViewModel CameraEdit => _cameraEdit;
        public StoryImageGeneratorViewModel StoryGenerator => _storyGenerator;
        public StoryImageGeneratorQViewModel StoryGeneratorQ => _storyGeneratorQ;
        public StoryImageGeneratorFViewModel StoryGeneratorF => _storyGeneratorF;
        public StoryImageGeneratorAmateurViewModel StoryGeneratorAmateur => _storyGeneratorAmateur;
        public AmateurGeneratorViewModel AmateurGenerator => _amateurGenerator;
        public CameraAngleViewModel CameraAngle => _cameraAngle;

        public string ProcessingStatus
        {
            get => _processingStatus;
            set
            {
                _processingStatus = value;
                OnPropertyChanged();
            }
        }

        public double ProcessingProgress
        {
            get => _processingProgress;
            set
            {
                _processingProgress = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercentage));
            }
        }

        public string ProgressPercentage => $"{ProcessingProgress:F0}%";

        public string LogOutput
        {
            get => _logOutput;
            set
            {
                _logOutput = value;
                OnPropertyChanged();
            }
        }

        public string ComfyUIServer
        {
            get => _comfyUIServer;
            set
            {
                _comfyUIServer = value;
                OnPropertyChanged();
            }
        }

        public string ComfyUIPort
        {
            get => _comfyUIPort;
            set
            {
                _comfyUIPort = value;
                OnPropertyChanged();
            }
        }

        public string StatusBarMessage
        {
            get => _statusBarMessage;
            set
            {
                _statusBarMessage = value;
                OnPropertyChanged();
            }
        }

        public bool HasResultImage
        {
            get => _hasResultImage;
            set
            {
                _hasResultImage = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string ResultImagePath
        {
            get => _resultImagePath;
            set
            {
                _resultImagePath = value;
                OnPropertyChanged();
            }
        }

        public BitmapImage? ResultImageSource
        {
            get => _resultImageSource;
            set
            {
                _resultImageSource = value;
                OnPropertyChanged();
            }
        }

        public string ImageInfo
        {
            get => _imageInfo;
            set
            {
                _imageInfo = value;
                OnPropertyChanged();
            }
        }

        public bool CanGenerate => !string.IsNullOrEmpty(ImagePrompt);

        // Lora Properties
        public ObservableCollection<string> AvailableLoras
        {
            get => _availableLoras;
            set
            {
                _availableLoras = value;
                OnPropertyChanged();
            }
        }

        public string SelectedLora
        {
            get => _selectedLora;
            set
            {
                _selectedLora = value;
                OnPropertyChanged();
            }
        }

        public bool LoraEnabled
        {
            get => _loraEnabled;
            set
            {
                _loraEnabled = value;
                OnPropertyChanged();
            }
        }

        // Workflow Properties
        public TextGeneratorWorkflow SelectedWorkflow
        {
            get => _selectedWorkflow;
            set
            {
                if (_selectedWorkflow != value)
                {
                    _selectedWorkflow = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowLoraOptions));
                }
            }
        }

        public bool ShowLoraOptions => SelectedWorkflow == TextGeneratorWorkflow.Zimage;

        // Commands
        public ICommand GenerateImageCommand { get; }
        public ICommand CancelGenerationCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand OpenResultImageCommand { get; }
        public ICommand SendToCameraEditCommand { get; }
        public ICommand SendToVideoGeneratorCommand { get; }
        public ICommand NavigateToCameraEditCommand { get; }
        public ICommand NavigateToImageAnalyzerCommand { get; }
        public ICommand NavigateToVideoGeneratorCommand { get; }
              public ICommand NavigateToStoryVideoCommand { get; }
        public ICommand RefreshLorasCommand { get; }

        // Queue commands
        public ICommand AddToQueueCommand { get; }
        public ICommand RemoveFromQueueCommand { get; }
        public ICommand ClearQueueCommand { get; }
        public ICommand ProcessQueueCommand { get; }

        // Queue properties
        public ObservableCollection<ImagePromptQueueItem> PromptQueue
        {
            get => _promptQueue;
            set
            {
                _promptQueue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasQueueItems));
                OnPropertyChanged(nameof(QueueCount));
            }
        }

        public ImagePromptQueueItem? SelectedQueueItem
        {
            get => _selectedQueueItem;
            set
            {
                _selectedQueueItem = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            set
            {
                _isProcessingQueue = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
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

        public bool HasQueueItems => _promptQueue.Any();
        public int QueueCount => _promptQueue.Count;
        public int PendingQueueCount => _promptQueue.Count(q => q.Status == "Pending");
        public int CompletedQueueCount => _promptQueue.Count(q => q.Status == "Completed");

        public bool CanAddToQueue => !string.IsNullOrEmpty(ImagePrompt);
        public bool CanRemoveFromQueue => SelectedQueueItem != null;
        public bool CanClearQueue => _promptQueue.Any();
        public bool CanProcessQueue => _promptQueue.Any(q => q.Status == "Pending") && !IsProcessingQueue;

        // Navigation properties
        private int _selectedTabIndex = 0; // Default to Text Generation tab

        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (_selectedTabIndex != value)
                {
                    _selectedTabIndex = value;
                    OnPropertyChanged();
                }
            }
        }


        // Methods
        private async Task GenerateImageAsync()
        {
            // If already processing, add to queue instead
            if (IsProcessing)
            {
                AddToQueue();
                // Auto-start queue processing if not already processing queue
                if (!IsProcessingQueue && PromptQueue.Any(q => q.Status == "Pending"))
                {
                    _ = Task.Run(async () => await ProcessQueueAsync());
                }
                return;
            }

            if (!CanGenerate) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                AddLog("=== Starting image generation ===");
                IsProcessing = true;

                // Clear previous result
                HasResultImage = false;
                ResultImageSource = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"Prompt: {ImagePrompt}");

                // Ensure ComfyUI is connected
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    AddLog("Connecting to ComfyUI WebSocket...");
                    await _comfyUIService.ConnectAsync(_cancellationTokenSource.Token);
                    AddLog("Connected to ComfyUI");
                }
                else
                {
                    AddLog("ComfyUI already connected");
                }

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Load workflow based on selected workflow
                var workflowFileName = SelectedWorkflow switch
                {
                    TextGeneratorWorkflow.Qwen2512 => "qwen2512API-text.json",
                    TextGeneratorWorkflow.Klien => "Klien-Text-API.json",
                    _ => "Zib-Zit.json"
                };
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", workflowFileName);
                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    System.Windows.MessageBox.Show($"Workflow file not found: {workflowPath}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                AddLog($"Loading workflow: {workflowPath}");
                var workflowJson = await File.ReadAllTextAsync(workflowPath, _cancellationTokenSource.Token);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Update workflow with parameters
                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 10;

                var updatedWorkflow = UpdateWorkflowParameters(workflow);

                // Execute workflow
                ProcessingStatus = "Generating image...";
                ProcessingProgress = 30;
                AddLog("Executing workflow in ComfyUI...");

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 30 + (percent * 0.6); // Scale to 30-90%
                            ProcessingStatus = $"Generating: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, _cancellationTokenSource.Token);

                // Force progress update after workflow completes
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    ProcessingStatus = "Workflow completed, retrieving output...";
                });

                AddLog($"Workflow execution completed with prompt ID: {promptId}");

                // Get output images from ComfyUI output folder
                ProcessingStatus = "Retrieving output image...";
                ProcessingProgress = 95;
                AddLog("Looking for generated image...");

                // Add debug info about what we're doing
                AddLog("=== DEBUG: About to call GetOutputImagesFromComfyUI ===");

                // Retry image retrieval with delays to give ComfyUI time to write the file
                List<byte[]> outputImages = new();
                int retryCount = 0;
                int maxRetries = 20; // Wait up to 100 seconds (20 retries × 5s)

                while (retryCount < maxRetries && !outputImages.Any())
                {
                    if (retryCount > 0)
                    {
                        AddLog($"Retry {retryCount}/{maxRetries} - waiting 5 seconds before checking again...");
                        await Task.Delay(5000, _cancellationTokenSource.Token);
                    }

                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    outputImages = await GetOutputImagesFromComfyUI(promptId);
                    retryCount++;
                }

                if (outputImages.Any())
                {
                    var outputImage = outputImages.First();
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "image-generator");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var prefix = SelectedWorkflow switch
                    {
                        TextGeneratorWorkflow.Qwen2512 => "qwen2512",
                        TextGeneratorWorkflow.Klien => "flux2-klein",
                        _ => "z-image"
                    };
                    var outputPath = Path.Combine(outputDir, $"{prefix}_{timestamp}.png");

                    await File.WriteAllBytesAsync(outputPath, outputImage);
                    AddLog($"Output saved: {outputPath}");

                    ResultImagePath = outputPath;
                    LoadResultPreview(outputPath);
                    HasResultImage = true;

                    ProcessingProgress = 100;
                    ProcessingStatus = "Complete!";
                    StatusBarMessage = $"Image generation complete - {Path.GetFileName(outputPath)}";
                }
                else
                {
                    AddLog("WARNING: No output images received after all retries");
                    ProcessingStatus = "No output generated";
                    System.Windows.MessageBox.Show("No output images were generated. Please check the ComfyUI console for errors.", "Warning", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("Image generation cancelled by user");
                ProcessingStatus = "Cancelled";
                ProcessingProgress = 0;
                StatusBarMessage = "Generation cancelled";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AddLog($"Inner Exception: {ex.InnerException.Message}");
                }
                AddLog($"Stack Trace: {ex.StackTrace}");

                _logger.LogError($"Error generating image: {ex}");

                ProcessingStatus = "Error occurred";
                ProcessingProgress = 0;

                System.Windows.MessageBox.Show(
                    $"Error generating image:\n\n{ex.Message}\n\nCheck the log for more details.",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
                AddLog("=== Image generation ended ===");
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
                // Check if we're connecting to a remote ComfyUI server
                var baseUrl = _settingsService.Settings?.BaseUrl ?? string.Empty;
                bool isRemoteServer = IsRemoteUrl(baseUrl);

                string? loraBasePath;

                if (isRemoteServer)
                {
                    // For remote servers, derive LoRA path from RemoteOutputFolderPath
                    // RemoteOutputFolderPath is usually something like \\server\ComfyUI\output
                    // We need to get \\server\ComfyUI\models\loras
                    var remoteOutputPath = _settingsService.Settings?.RemoteOutputFolderPath;

                    if (string.IsNullOrEmpty(remoteOutputPath))
                    {
                        AddLog("Remote output path not configured in settings - cannot derive LoRA path");
                        return null;
                    }

                    // Also check if RemoteLoraFolderPath is explicitly set (for custom paths)
                    var explicitLoraPath = _settingsService.Settings?.RemoteLoraFolderPath;
                    if (!string.IsNullOrEmpty(explicitLoraPath))
                    {
                        loraBasePath = explicitLoraPath;
                        AddLog($"Using explicitly configured remote LoRA path: {loraBasePath}");
                    }
                    else
                    {
                        // Derive LoRA path from output path
                        // Expected: \\server\ComfyUI\output -> \\server\ComfyUI\models\loras
                        var comfyUIRoot = Path.GetDirectoryName(remoteOutputPath);
                        if (string.IsNullOrEmpty(comfyUIRoot))
                        {
                            AddLog($"Could not derive ComfyUI root from output path: {remoteOutputPath}");
                            return null;
                        }

                        loraBasePath = Path.Combine(comfyUIRoot, "models", "loras");
                        AddLog($"Derived remote LoRA path from output path: {loraBasePath}");
                    }

                    // For remote paths, check if directory exists directly
                    if (Directory.Exists(loraBasePath))
                    {
                        AddLog($"Remote LoRA directory exists: {loraBasePath}");
                        return loraBasePath;
                    }
                    else
                    {
                        AddLog($"Remote LoRA directory not found: {loraBasePath}");
                        return null;
                    }
                }
                else
                {
                    // Use local ComfyUI path
                    loraBasePath = _settingsService.Settings?.ComfyUIFolderPath;
                    if (string.IsNullOrEmpty(loraBasePath))
                    {
                        AddLog("ComfyUI installation path not configured");
                        return null;
                    }
                }

                // First try to get path from extra_model_paths.yaml (local only)
                var extraModelPathsFile = Path.Combine(loraBasePath, "extra_model_paths.yaml");
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
                        AddLog($"Stack trace: {ex.StackTrace}");
                    }
                }
                else
                {
                    AddLog($"ERROR: extra_model_paths.yaml not found in ComfyUI directory: {extraModelPathsFile}");
                }

                // Fallback to default ComfyUI models directory
                var defaultLoraPath = Path.Combine(loraBasePath, "models", "loras");
                if (Directory.Exists(defaultLoraPath))
                {
                    AddLog($"Using default ComfyUI LoRA path: {defaultLoraPath}");
                    return defaultLoraPath;
                }

                AddLog($"No LoRA directory found in: {loraBasePath}");
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

        private JsonElement UpdateWorkflowParameters(JsonElement workflow)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            switch (SelectedWorkflow)
            {
                case TextGeneratorWorkflow.Zimage:
                    return UpdateZimageWorkflow(workflowDict);
                case TextGeneratorWorkflow.Qwen2512:
                    return UpdateQwen2512Workflow(workflowDict);
                case TextGeneratorWorkflow.Klien:
                    return UpdateKlienWorkflow(workflowDict);
                default:
                    return workflow;
            }
        }

        private JsonElement UpdateZimageWorkflow(Dictionary<string, JsonElement> workflowDict)
        {
            // Zib-Zit workflow uses Power Lora Loader (node 583)
            // Handle LoRA: enable/disable lora_1 slot in the Power Lora Loader
            if (workflowDict.ContainsKey("583"))
            {
                // Power Lora Loader exists - Zib-Zit workflow
                var node583 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["583"].GetRawText());
                if (node583 != null && node583.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node583["inputs"]));
                    if (inputs != null)
                    {
                        if (LoraEnabled && !string.IsNullOrEmpty(SelectedLora) && SelectedLora != "No Loras available")
                        {
                            // Enable lora_1 with the selected lora
                            var lora1Config = new
                            {
                                on = true,
                                lora = $"{SelectedLora}.safetensors",
                                strength = 1.0
                            };
                            inputs["lora_1"] = JsonSerializer.Deserialize<object>(
                                JsonSerializer.Serialize(lora1Config));
                            AddLog($"LoRA enabled: {SelectedLora}.safetensors");
                        }
                        else
                        {
                            // Disable lora_1
                            var lora1Config = new
                            {
                                on = false,
                                lora = "",
                                strength = 0.0
                            };
                            inputs["lora_1"] = JsonSerializer.Deserialize<object>(
                                JsonSerializer.Serialize(lora1Config));
                            AddLog("LoRA disabled");
                        }

                        node583["inputs"] = inputs;
                        workflowDict["583"] = JsonSerializer.SerializeToElement(node583);
                    }
                }
            }
            else
            {
                // Legacy workflow - use old LoRA handling
                if (LoraEnabled && !string.IsNullOrEmpty(SelectedLora) && SelectedLora != "No Loras available")
                {
                    workflowDict = AddLoraToWorkflow(workflowDict, SelectedLora);
                }
                else
                {
                    // LoRA disabled: bypass the existing LoRA node (58) by connecting directly to model/clip loaders
                    // Update ModelSamplingAuraFlow (node 47) to connect directly to UNETLoader (node 46)
                    if (workflowDict.ContainsKey("47"))
                    {
                        var node47 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["47"].GetRawText());
                        if (node47 != null && node47.ContainsKey("inputs"))
                        {
                            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                JsonSerializer.Serialize(node47["inputs"]));
                            if (inputs != null)
                            {
                                inputs["model"] = new object[] { "46", 0 }; // Connect directly to UNETLoader
                                node47["inputs"] = inputs;
                                workflowDict["47"] = JsonSerializer.SerializeToElement(node47);
                            }
                        }
                    }

                    // Update CLIPTextEncode (node 45) to connect directly to CLIPLoader (node 39)
                    if (workflowDict.ContainsKey("45"))
                    {
                        var node45 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["45"].GetRawText());
                        if (node45 != null && node45.ContainsKey("inputs"))
                        {
                            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                JsonSerializer.Serialize(node45["inputs"]));
                            if (inputs != null)
                            {
                                inputs["clip"] = new object[] { "39", 0 }; // Connect directly to CLIPLoader
                                node45["inputs"] = inputs;
                                workflowDict["45"] = JsonSerializer.SerializeToElement(node45);
                            }
                        }
                    }

                    // Remove the orphaned LoRA node (58) from the workflow
                    workflowDict.Remove("58");
                    AddLog("LoRA disabled: bypassing built-in LoRA node");
                }
            }

            // Zib-Zit workflow uses different node IDs:
            // Node 443: Textbox - Positive Prompt
            // Node 445: Textbox - Negative Prompt
            // Node 569: Seed String
            // Node 639: KSamplerAdvanced - Z-image (steps, cfg, denoise)
            // Node 176: CR Aspect Ratio

            // Update positive prompt (node 443 - Textbox)
            if (workflowDict.ContainsKey("443"))
            {
                var node443 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["443"].GetRawText());
                if (node443 != null && node443.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node443["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = ImagePrompt;
                        node443["inputs"] = inputs;
                        workflowDict["443"] = JsonSerializer.SerializeToElement(node443);
                        AddLog($"Updated positive prompt (node 443)");
                    }
                }
            }

            // Update seed (node 569 - Seed String)
            if (workflowDict.ContainsKey("569"))
            {
                var node569 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["569"].GetRawText());
                if (node569 != null && node569.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node569["inputs"]));
                    if (inputs != null)
                    {
                        var actualSeed = Seed == 0 ? new Random().NextInt64(0, 999999999999999) : Seed;
                        inputs["seed"] = (long)actualSeed;
                        node569["inputs"] = inputs;
                        workflowDict["569"] = JsonSerializer.SerializeToElement(node569);
                        AddLog($"Updated seed: {actualSeed}");
                    }
                }
            }

            // Update Z-image sampler settings (node 639 - KSamplerAdvanced)
            if (workflowDict.ContainsKey("639"))
            {
                var node639 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["639"].GetRawText());
                if (node639 != null && node639.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node639["inputs"]));
                    if (inputs != null)
                    {
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        inputs["denoise"] = Denoise;
                        node639["inputs"] = inputs;
                        workflowDict["639"] = JsonSerializer.SerializeToElement(node639);
                        AddLog($"Updated Z-image sampler: steps={Steps}, cfg={Cfg}, denoise={Denoise}");
                    }
                }
            }

            // Update aspect ratio (node 176 - CR Aspect Ratio)
            if (workflowDict.ContainsKey("176"))
            {
                var aspectRatios = new[]
                {
                    "SDXL - 9:16 portrait 1088x1600",
                    "SDXL - 16:9 landscape 1600x1088",
                    "SDXL - 1:1 square 1600x1600"
                };

                var selectedRatio = aspectRatios[Math.Min(AspectRatioIndex, aspectRatios.Length - 1)];

                var node176 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["176"].GetRawText());
                if (node176 != null && node176.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node176["inputs"]));
                    if (inputs != null)
                    {
                        inputs["aspect_ratio"] = selectedRatio;
                        // Turn off swap_dimensions to prevent width/height inversion
                        inputs["swap_dimensions"] = "Off";
                        node176["inputs"] = inputs;
                        workflowDict["176"] = JsonSerializer.SerializeToElement(node176);
                        AddLog($"Updated aspect ratio: {selectedRatio}");
                    }
                }
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private JsonElement UpdateQwen2512Workflow(Dictionary<string, JsonElement> workflowDict)
        {
            // Get resolution from aspect ratio index
            var resolutions = new[]
            {
                (1600, 1088), // Landscape
                (1088, 1600), // Portrait
                (1600, 1600), // Square
            };
            var (width, height) = resolutions[Math.Min(AspectRatioIndex, resolutions.Length - 1)];

            // Update prompt (node 71 - CLIPTextEncode)
            if (workflowDict.ContainsKey("71"))
            {
                var node71 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["71"].GetRawText());
                if (node71 != null && node71.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node71["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = ImagePrompt;
                        node71["inputs"] = inputs;
                        workflowDict["71"] = JsonSerializer.SerializeToElement(node71);
                    }
                }
            }

            // Update seed (node 120 - Seed)
            if (workflowDict.ContainsKey("120"))
            {
                var node120 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["120"].GetRawText());
                if (node120 != null && node120.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node120["inputs"]));
                    if (inputs != null)
                    {
                        var actualSeed = Seed == 0 ? -1 : Seed;
                        inputs["seed"] = actualSeed;
                        node120["inputs"] = inputs;
                        workflowDict["120"] = JsonSerializer.SerializeToElement(node120);
                    }
                }
            }

            // Update sampler settings (node 74 - KSampler)
            if (workflowDict.ContainsKey("74"))
            {
                var node74 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["74"].GetRawText());
                if (node74 != null && node74.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node74["inputs"]));
                    if (inputs != null)
                    {
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        inputs["denoise"] = Denoise;
                        node74["inputs"] = inputs;
                        workflowDict["74"] = JsonSerializer.SerializeToElement(node74);
                    }
                }
            }

            // Update resolution (node 51 - EmptyLatentImage)
            if (workflowDict.ContainsKey("51"))
            {
                var node51 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["51"].GetRawText());
                if (node51 != null && node51.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node51["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = width;
                        inputs["height"] = height;
                        node51["inputs"] = inputs;
                        workflowDict["51"] = JsonSerializer.SerializeToElement(node51);
                    }
                }
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private JsonElement UpdateKlienWorkflow(Dictionary<string, JsonElement> workflowDict)
        {
            // Get resolution from aspect ratio index
            var resolutions = new[]
            {
                (1600, 1088), // Landscape
                (1088, 1600), // Portrait
                (1600, 1600), // Square
            };
            var (width, height) = resolutions[Math.Min(AspectRatioIndex, resolutions.Length - 1)];

            // Update prompt (node 76 - PrimitiveStringMultiline)
            if (workflowDict.ContainsKey("76"))
            {
                var node76 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["76"].GetRawText());
                if (node76 != null && node76.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node76["inputs"]));
                    if (inputs != null)
                    {
                        inputs["value"] = ImagePrompt;
                        node76["inputs"] = inputs;
                        workflowDict["76"] = JsonSerializer.SerializeToElement(node76);
                    }
                }
            }

            // Update seed (node 75:73 - RandomNoise)
            if (workflowDict.ContainsKey("75:73"))
            {
                var node75_73 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["75:73"].GetRawText());
                if (node75_73 != null && node75_73.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node75_73["inputs"]));
                    if (inputs != null)
                    {
                        var actualSeed = Seed == 0 ? new Random().NextInt64(0, 999999999999999) : Seed;
                        inputs["noise_seed"] = actualSeed;
                        node75_73["inputs"] = inputs;
                        workflowDict["75:73"] = JsonSerializer.SerializeToElement(node75_73);
                    }
                }
            }

            // Update CFG (node 75:63 - CFGGuider)
            if (workflowDict.ContainsKey("75:63"))
            {
                var node75_63 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["75:63"].GetRawText());
                if (node75_63 != null && node75_63.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node75_63["inputs"]));
                    if (inputs != null)
                    {
                        inputs["cfg"] = Cfg;
                        node75_63["inputs"] = inputs;
                        workflowDict["75:63"] = JsonSerializer.SerializeToElement(node75_63);
                    }
                }
            }

            // Update steps (node 75:62 - Flux2Scheduler)
            if (workflowDict.ContainsKey("75:62"))
            {
                var node75_62 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["75:62"].GetRawText());
                if (node75_62 != null && node75_62.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node75_62["inputs"]));
                    if (inputs != null)
                    {
                        inputs["steps"] = Steps;
                        node75_62["inputs"] = inputs;
                        workflowDict["75:62"] = JsonSerializer.SerializeToElement(node75_62);
                    }
                }
            }

            // Update resolution (nodes 75:68 and 75:69 - Width/Height)
            if (workflowDict.ContainsKey("75:68"))
            {
                var node75_68 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["75:68"].GetRawText());
                if (node75_68 != null && node75_68.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node75_68["inputs"]));
                    if (inputs != null)
                    {
                        inputs["value"] = width;
                        node75_68["inputs"] = inputs;
                        workflowDict["75:68"] = JsonSerializer.SerializeToElement(node75_68);
                    }
                }
            }

            if (workflowDict.ContainsKey("75:69"))
            {
                var node75_69 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["75:69"].GetRawText());
                if (node75_69 != null && node75_69.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node75_69["inputs"]));
                    if (inputs != null)
                    {
                        inputs["value"] = height;
                        node75_69["inputs"] = inputs;
                        workflowDict["75:69"] = JsonSerializer.SerializeToElement(node75_69);
                    }
                }
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private Dictionary<string, JsonElement> AddLoraToWorkflow(Dictionary<string, JsonElement> workflowDict, string loraName)
        {
            try
            {
                AddLog($"Applying Lora: {loraName}");

                // Check if this is the Zib-Zit workflow with Power Lora Loader (node 583)
                if (workflowDict.ContainsKey("583"))
                {
                    // Zib-Zit workflow uses Power Lora Loader (rgthree) node
                    AddLog("Detected Power Lora Loader (Zib-Zit workflow)");

                    var node583 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["583"].GetRawText());
                    if (node583 != null && node583.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node583["inputs"]));
                        if (inputs != null)
                        {
                            // Update lora_1 slot to enable it and set the selected lora
                            // The lora structure in Power Lora Loader is: { on: bool, lora: string, strength: float }
                            var lora1Config = new
                            {
                                on = true,
                                lora = $"{loraName}.safetensors",
                                strength = 1.0
                            };

                            inputs["lora_1"] = JsonSerializer.Deserialize<object>(
                                JsonSerializer.Serialize(lora1Config));

                            node583["inputs"] = inputs;
                            workflowDict["583"] = JsonSerializer.SerializeToElement(node583);
                            AddLog($"Successfully enabled lora_1 with: {loraName}.safetensors");
                        }
                    }
                }
                else
                {
                    // Legacy workflow: Create LoraLoader node (using a high node number to avoid conflicts)
                    AddLog("Using legacy LoraLoader node");

                    var loraNodeNumber = "100";
                    var loraNode = new
                    {
                        inputs = new
                        {
                            lora_name = $"zimage\\{loraName}.safetensors",
                            strength_model = 1.0,
                            strength_clip = 1.0,
                            model = new object[] { "46", 0 }, // Connect to UNETLoader (node 46)
                            clip = new object[] { "39", 0 }   // Connect to CLIPLoader (node 39)
                        },
                        class_type = "LoraLoader",
                        _meta = new
                        {
                            title = "Load LoRA"
                        }
                    };

                    workflowDict[loraNodeNumber] = JsonSerializer.SerializeToElement(loraNode);

                    // Update nodes that use the model to use the Lora-enhanced model instead
                    // Update ModelSamplingAuraFlow (node 47) to use Lora output
                    if (workflowDict.ContainsKey("47"))
                    {
                        var node47 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["47"].GetRawText());
                        if (node47 != null && node47.ContainsKey("inputs"))
                        {
                            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                JsonSerializer.Serialize(node47["inputs"]));
                            if (inputs != null)
                            {
                                inputs["model"] = new object[] { loraNodeNumber, 0 }; // Use Lora-enhanced model
                                node47["inputs"] = inputs;
                                workflowDict["47"] = JsonSerializer.SerializeToElement(node47);
                            }
                        }
                    }

                    // Update CLIPTextEncode (node 45) to use Lora-enhanced CLIP
                    if (workflowDict.ContainsKey("45"))
                    {
                        var node45 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["45"].GetRawText());
                        if (node45 != null && node45.ContainsKey("inputs"))
                        {
                            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                JsonSerializer.Serialize(node45["inputs"]));
                            if (inputs != null && inputs.ContainsKey("clip"))
                            {
                                inputs["clip"] = new object[] { loraNodeNumber, 1 }; // Use Lora-enhanced CLIP (output 1)
                                node45["inputs"] = inputs;
                                workflowDict["45"] = JsonSerializer.SerializeToElement(node45);
                            }
                        }
                    }

                    AddLog($"Successfully added Lora node {loraNodeNumber} for {loraName}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error adding Lora to workflow: {ex.Message}");
            }

            return workflowDict;
        }

        private async Task<List<byte[]>> GetOutputImagesFromComfyUI(string promptId)
        {
            var images = new List<byte[]>();

            try
            {
                // Get the actual ComfyUI server settings
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";

                // Parse the URL to get server and port
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;
                var actualPort = uri.Port.ToString();

                // Check if ComfyUI is running locally or remotely
                bool isRemoteComfyUI = IsComfyUIRemote(actualServer);

                AddLog($"ComfyUI server: {actualServer}:{actualPort}");
                AddLog($"Is remote ComfyUI: {isRemoteComfyUI}");

                if (isRemoteComfyUI)
                {
                    AddLog("Detected remote ComfyUI server, downloading generated image...");

                    // Get output files for this specific prompt ID
                    var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                    AddLog($"Found {outputFiles.Count} output files for prompt {promptId}");

                    if (outputFiles.Any())
                    {
                        // Get the first image file (there should typically be just one)
                        var imageFile = outputFiles.FirstOrDefault(f => f.EndsWith(".png") || f.EndsWith(".jpg") || f.EndsWith(".jpeg"));

                        if (!string.IsNullOrEmpty(imageFile))
                        {
                            AddLog($"Downloading generated image: {imageFile}");

                            var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(imageFile);
                            if (imageData != null)
                            {
                                images.Add(imageData);
                                AddLog($"Successfully downloaded image ({imageData.Length} bytes)");
                            }
                            else
                            {
                                AddLog($"Failed to download image: {imageFile}");
                            }
                        }
                        else
                        {
                            AddLog("No image files found in prompt output");
                            foreach (var file in outputFiles)
                            {
                                AddLog($"  - {file}");
                            }
                        }
                    }
                    else
                    {
                        AddLog("No output files found for this prompt, trying fallback approach...");

                        // Try the fallback approach
                        var fallbackImage = await _comfyUIService.HttpClient.TryDownloadRecentOutputAsync(promptId);
                        if (fallbackImage != null)
                        {
                            images.Add(fallbackImage);
                            AddLog($"Successfully downloaded image via fallback method ({fallbackImage.Length} bytes)");
                        }
                        else
                        {
                            AddLog("Failed to download image using all available methods");
                            AddLog("This might be due to:");
                            AddLog("- ComfyUI output folder not being accessible via HTTP");
                            AddLog("- Different filename pattern than expected");
                            AddLog("- ComfyUI server configuration preventing file access");
                        }
                    }
                }
                else
                {
                    // Local ComfyUI - check the output folder directly
                    var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                    if (string.IsNullOrEmpty(comfyUIOutputDir))
                    {
                        AddLog("ERROR: ComfyUI output folder not configured");
                        AddLog("Please restart the application and configure the ComfyUI folder path");
                        return images;
                    }

                    // For Zimage (Zib-Zit workflow), the output is in a subdirectory: Jib_Mix_Z-Image/%date/
                    string searchDirectory;
                    if (SelectedWorkflow == TextGeneratorWorkflow.Zimage)
                    {
                        // Look in Jib_Mix_Z-Image subdirectory with today's date
                        var dateSubdir = DateTime.Now.ToString("yyyy-MM-dd");
                        searchDirectory = Path.Combine(comfyUIOutputDir, "Jib_Mix_Z-Image", dateSubdir);
                        AddLog($"Zimage workflow: searching in {searchDirectory}");
                    }
                    else
                    {
                        searchDirectory = comfyUIOutputDir;
                    }

                    if (!Directory.Exists(comfyUIOutputDir))
                    {
                        AddLog($"ERROR: ComfyUI output folder not found: {comfyUIOutputDir}");
                        AddLog("Please check the ComfyUI folder configuration in settings");
                        return images;
                    }

                    // Look for files based on the selected workflow
                    List<string> imageFiles;
                    if (SelectedWorkflow == TextGeneratorWorkflow.Zimage)
                    {
                        // Zib-Zit workflow: files are named like "False__0_blur_02.png"
                        // Just look for all PNG files in the subdirectory and sort by modification time
                        if (Directory.Exists(searchDirectory))
                        {
                            imageFiles = Directory.GetFiles(searchDirectory, "*.png")
                                .OrderByDescending(f => File.GetLastWriteTime(f))
                                .ToList();
                        }
                        else
                        {
                            AddLog($"Zimage output directory not found: {searchDirectory}");
                            // Try to find the Jib_Mix_Z-Image directory and its subdirectories
                            var zimageBaseDir = Path.Combine(comfyUIOutputDir, "Jib_Mix_Z-Image");
                            if (Directory.Exists(zimageBaseDir))
                            {
                                AddLog($"Found Jib_Mix_Z-Image directory, searching for recent files...");
                                imageFiles = Directory.GetFiles(zimageBaseDir, "*.png", SearchOption.AllDirectories)
                                    .OrderByDescending(f => File.GetLastWriteTime(f))
                                    .Take(20)
                                    .ToList();
                                AddLog($"Found {imageFiles.Count} files in Jib_Mix_Z-Image directory tree");
                            }
                            else
                            {
                                AddLog($"Jib_Mix_Z-Image directory not found at: {zimageBaseDir}");
                                imageFiles = new List<string>();
                            }
                        }
                    }
                    else
                    {
                        // Other workflows use prefix-based file naming
                        var prefix = SelectedWorkflow switch
                        {
                            TextGeneratorWorkflow.Qwen2512 => "qwen2512_",
                            TextGeneratorWorkflow.Klien => "Flux2-Klein_",  // Note: Klien uses this prefix format
                            _ => "z-image_"
                        };
                        imageFiles = Directory.GetFiles(comfyUIOutputDir, $"{prefix}*.png")
                            .OrderByDescending(f => ExtractFileNumber(f)) // Sort by extracted number for proper numeric ordering
                            .ToList();
                    }

                    AddLog($"Output directory path: {comfyUIOutputDir}");
                    AddLog($"Search directory: {searchDirectory}");
                    AddLog($"Directory exists: {Directory.Exists(searchDirectory)}");

                    if (!Directory.Exists(searchDirectory))
                    {
                        AddLog($"ERROR: Output directory does not exist: {searchDirectory}");
                        return images;
                    }

                    // Debug: List ALL files in the directory to understand what's there
                    try
                    {
                        var allFiles = Directory.GetFiles(searchDirectory, "*.png")
                            .OrderByDescending(f => File.GetLastWriteTime(f))
                            .Take(10)
                            .Select(f => $"{Path.GetFileName(f)} ({(DateTime.Now - File.GetLastWriteTime(f)).TotalSeconds:F0}s old)");

                        AddLog("All PNG files in directory (first 10 by time):");
                        foreach (var file in allFiles)
                        {
                            AddLog($"  - {file}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"Error listing files: {ex.Message}");
                    }

                    var workflowName = SelectedWorkflow.ToString();
                    AddLog($"Found {imageFiles.Count} {workflowName} PNG files in output directory");

                    if (imageFiles.Any())
                    {
                        var latestFile = imageFiles.First();
                        var fileAge = DateTime.Now - File.GetLastWriteTime(latestFile);

                        AddLog($"Latest {workflowName} file: {Path.GetFileName(latestFile)}");
                        AddLog($"File modification time: {File.GetLastWriteTime(latestFile):yyyy-MM-dd HH:mm:ss}");
                        AddLog($"Current time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        AddLog($"File age: {fileAge.TotalSeconds:F0} seconds");

                        // For Zimage, use the most recently modified file
                        // For other workflows, use the highest numbered file
                        AddLog($"Using latest {workflowName} file: {Path.GetFileName(latestFile)}");
                        var imageData = await File.ReadAllBytesAsync(latestFile);
                        images.Add(imageData);
                    }
                    else
                    {
                        AddLog($"No {workflowName} files found, looking for any other PNG files...");

                        // Fallback to any PNG files in the search directory
                        var allImageFiles = Directory.GetFiles(searchDirectory, "*.png")
                            .Where(f => !Path.GetFileName(f).StartsWith("temp_")) // Exclude temporary files
                            .OrderByDescending(f => File.GetLastWriteTime(f))
                            .ToList();

                        // If still no files, try the base output directory
                        if (!allImageFiles.Any() && searchDirectory != comfyUIOutputDir)
                        {
                            AddLog($"No files in subdirectory, checking base output directory...");
                            allImageFiles = Directory.GetFiles(comfyUIOutputDir, "*.png", SearchOption.AllDirectories)
                                .Where(f => !Path.GetFileName(f).StartsWith("temp_"))
                                .OrderByDescending(f => File.GetLastWriteTime(f))
                                .Take(50)
                                .ToList();
                        }

                        AddLog($"Found {allImageFiles.Count} other PNG files");

                        if (allImageFiles.Any())
                        {
                            var latestFile = allImageFiles.First();
                            AddLog($"Using latest file as fallback: {Path.GetFileName(latestFile)}");
                            var imageData = await File.ReadAllBytesAsync(latestFile);
                            images.Add(imageData);
                        }
                        else
                        {
                            AddLog("No PNG output files found in directory...");

                            // Try to list what files are actually there
                            try
                            {
                                var allFiles = Directory.GetFiles(comfyUIOutputDir)
                                    .OrderByDescending(f => File.GetLastWriteTime(f))
                                    .Take(10)
                                    .Select(f => $"{Path.GetFileName(f)} ({(DateTime.Now - File.GetLastWriteTime(f)).TotalSeconds:F0}s old)");

                                AddLog("Files in output directory (first 10):");
                                foreach (var file in allFiles)
                                {
                                    AddLog($"  - {file}");
                                }
                            }
                            catch (Exception ex)
                            {
                                AddLog($"Could not list directory contents: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR retrieving output images: {ex.Message}");
            }

            return images;
        }

        private int ExtractFileNumber(string filePath)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);

            // Extract number based on the selected workflow
            // Zimage: "z-image_12345_" pattern
            // Qwen: "qwen2512_00001_" pattern (5-digit zero-padded)
            // Klien: "Flux2-Klein_00001_" pattern (5-digit zero-padded)
            var patterns = SelectedWorkflow switch
            {
                TextGeneratorWorkflow.Qwen2512 => new[] { @"qwen2512_(\d+)_", @"qwen2512_(\d+)$" },
                TextGeneratorWorkflow.Klien => new[] { @"Flux2-Klein_(\d+)_", @"Flux2-Klein_(\d+)$" },
                _ => new[] { @"z-image_(\d+)_", @"z-image_(\d+)$" }
            };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(fileName, pattern);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var number))
                {
                    return number;
                }
            }

            // Fallback: return 0 if we can't extract the number
            return 0;
        }

        private void LoadResultPreview(string imagePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                ResultImageSource = bitmap;

                var fileInfo = new FileInfo(imagePath);
                ImageInfo = $"Size: {fileInfo.Length / 1024}KB | {bitmap.PixelWidth}x{bitmap.PixelHeight}";

                AddLog("Result image preview loaded");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading result preview: {ex.Message}");
            }
        }

        private void CancelGeneration()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                AddLog("Cancellation requested by user");
                _cancellationTokenSource.Cancel();
                ProcessingStatus = "Cancelling...";
            }
        }

        private void OpenResultFolder()
        {
            try
            {
                var folder = Path.GetDirectoryName(ResultImagePath);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = folder,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR opening result folder: {ex.Message}");
            }
        }

        private void OpenResultImage()
        {
            try
            {
                if (!string.IsNullOrEmpty(ResultImagePath) && File.Exists(ResultImagePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = ResultImagePath,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR opening result image: {ex.Message}");
            }
        }

        private void SendToCameraEdit()
        {
            if (!HasResultImage) return;

            try
            {
                // Set the image path on the embedded CameraEdit tab
                _cameraEdit.SetImagePath(ResultImagePath);

                // Navigate to the Camera Edit tab
                SelectedTabIndex = 2;

                AddLog($"Sent image to Camera Edit tab: {Path.GetFileName(ResultImagePath)}");
                StatusBarMessage = $"Image sent to Camera Edit tab: {Path.GetFileName(ResultImagePath)}";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR sending to Camera Edit: {ex.Message}");
                _logger.LogError($"Error sending to Camera Edit: {ex}");
                System.Windows.MessageBox.Show($"Error sending image to Camera Edit tab:\n\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void SendToVideoGenerator()
        {
            if (!HasResultImage || _serviceProvider == null) return;

            try
            {
                var videoWindow = _serviceProvider.GetService(typeof(VideoGeneratorWindow)) as VideoGeneratorWindow;
                if (videoWindow != null)
                {
                    videoWindow.Show();

                    if (videoWindow.DataContext is VideoGeneratorViewModel viewModel)
                    {
                        viewModel.SetImagePath(ResultImagePath);
                    }

                    AddLog($"Sent image to Video Generator: {Path.GetFileName(ResultImagePath)}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR sending to Video Generator: {ex.Message}");
                System.Windows.MessageBox.Show($"Error opening Video Generator window:\n\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void NavigateToCameraEdit()
        {
            if (_serviceProvider == null)
            {
                AddLog("ERROR: Service provider is null");
                return;
            }

            try
            {
                var cameraEditWindow = _serviceProvider.GetService(typeof(FlipPixWindow)) as FlipPixWindow;

                if (cameraEditWindow == null)
                {
                    AddLog("ERROR: Failed to create FlipPixWindow - GetService returned null");
                    return;
                }

                cameraEditWindow.Show();
                AddLog("Successfully opened Camera Edit window");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Camera Edit: {ex.Message}");
            }
        }

        private void NavigateToVideoGenerator()
        {
            if (_serviceProvider == null) return;

            try
            {
                var videoWindow = _serviceProvider.GetService(typeof(VideoGeneratorWindow)) as VideoGeneratorWindow;
                videoWindow?.Show();
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Video Generator: {ex.Message}");
            }
        }

        private void NavigateToImageAnalyzer()
        {
            if (_serviceProvider == null) return;

            try
            {
                var imageAnalyzerWindow = _serviceProvider.GetService(typeof(ImageAnalyzerWindow)) as ImageAnalyzerWindow;
                if (imageAnalyzerWindow != null)
                {
                    // Ensure window appears on screen
                    var screenWidth = SystemParameters.PrimaryScreenWidth;
                    var screenHeight = SystemParameters.PrimaryScreenHeight;
                    var windowWidth = imageAnalyzerWindow.Width;
                    var windowHeight = imageAnalyzerWindow.Height;

                    // Use conservative positioning
                    imageAnalyzerWindow.Left = 150;
                    imageAnalyzerWindow.Top = 150;

                    // Ensure window is fully visible on screen
                    if (imageAnalyzerWindow.Left + windowWidth > screenWidth)
                        imageAnalyzerWindow.Left = Math.Max(50, screenWidth - windowWidth - 50);
                    if (imageAnalyzerWindow.Top + windowHeight > screenHeight)
                        imageAnalyzerWindow.Top = Math.Max(50, screenHeight - windowHeight - 50);

                    imageAnalyzerWindow.Show();
                    AddLog("Opened Image Analyzer window");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Image Analyzer: {ex.Message}");
            }
        }

  
        private void NavigateToStoryVideo()
        {
            if (_serviceProvider == null) return;

            try
            {
                var storyVideoWindow = _serviceProvider.GetService(typeof(StoryVideoWindow)) as StoryVideoWindow;
                if (storyVideoWindow != null)
                {
                    // Ensure window appears on screen
                    var screenWidth = SystemParameters.PrimaryScreenWidth;
                    var screenHeight = SystemParameters.PrimaryScreenHeight;
                    var windowWidth = storyVideoWindow.Width;
                    var windowHeight = storyVideoWindow.Height;

                    // Use conservative positioning
                    storyVideoWindow.Left = 200;
                    storyVideoWindow.Top = 200;

                    // Ensure window is fully visible on screen
                    if (storyVideoWindow.Left + windowWidth > screenWidth)
                        storyVideoWindow.Left = Math.Max(50, screenWidth - windowWidth - 50);
                    if (storyVideoWindow.Top + windowHeight > screenHeight)
                        storyVideoWindow.Top = Math.Max(50, screenHeight - windowHeight - 50);

                    storyVideoWindow.Show();
                    AddLog("Opened Story Video window");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Story Video: {ex.Message}");
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

        private string QueueFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "queue", "image_generator_queue.json");

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
                var json = JsonSerializer.Serialize(PromptQueue.ToList(), options);
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
                var savedItems = JsonSerializer.Deserialize<List<ImagePromptQueueItem>>(json);

                if (savedItems != null && savedItems.Any())
                {
                    _promptQueue.Clear();
                    foreach (var item in savedItems)
                    {
                        if (item.Status == "Processing")
                        {
                            item.Status = "Failed";
                            item.ErrorMessage = "Interrupted by crash or app restart";
                        }
                        _promptQueue.Add(item);
                    }
                    OnPropertyChanged(nameof(HasQueueItems));
                    OnPropertyChanged(nameof(QueueCount));
                    OnPropertyChanged(nameof(PendingQueueCount));
                    AddLog($"Queue loaded from file: {_promptQueue.Count} items");
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

        private bool IsComfyUIRemote(string serverAddress)
        {
            try
            {
                // Check if it's a local address
                if (serverAddress.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    serverAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    serverAddress.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Check if it's a local network IP (192.168.x.x, 10.x.x.x, 172.16-31.x.x)
                if (System.Net.IPAddress.TryParse(serverAddress, out var ip))
                {
                    var bytes = ip.GetAddressBytes();
                    if (bytes.Length == 4)
                    {
                        // 192.168.x.x
                        if (bytes[0] == 192 && bytes[1] == 168)
                        {
                            return true; // This is a LAN IP
                        }
                        // 10.x.x.x
                        if (bytes[0] == 10)
                        {
                            return true; // This is a LAN IP
                        }
                        // 172.16-31.x.x
                        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                        {
                            return true; // This is a LAN IP
                        }
                    }
                }

                // If we get here, assume it's remote
                return !string.IsNullOrEmpty(serverAddress) && serverAddress != ".";
            }
            catch
            {
                // If we can't determine, assume it's remote to be safe
                return true;
            }
        }

        // Implementation of abstract base class properties
        public override string CurrentPromptText => ImagePrompt;

        public override int AspectRatioIndex
        {
            get => _aspectRatioIndex;
            set
            {
                _aspectRatioIndex = value;
                OnPropertyChanged();
            }
        }

        public override long Seed
        {
            get => _seed;
            set
            {
                _seed = value;
                OnPropertyChanged();
            }
        }

        // Override base class methods
        protected override void OnPromptSaved(string promptName)
        {
            AddLog($"Prompt saved: {promptName}");
            StatusBarMessage = $"Prompt saved: {promptName}";
        }

        protected override void OnPromptDeleted(string promptName)
        {
            AddLog($"Prompt deleted: {promptName}");
            StatusBarMessage = $"Prompt deleted: {promptName}";
        }

        protected override void OnPromptLoaded(SavedPrompt savedPrompt)
        {
            ImagePrompt = savedPrompt.Prompt;
            AspectRatioIndex = savedPrompt.AspectRatioIndex;
            Steps = savedPrompt.Steps;
            Cfg = savedPrompt.Cfg;
            Seed = savedPrompt.Seed;
            Denoise = savedPrompt.Denoise;

            // Load additional settings if they exist in the additional data
            if (savedPrompt.AdditionalData != null && savedPrompt.AdditionalData is Dictionary<string, object> additionalData)
            {
                if (additionalData.TryGetValue("SelectedWorkflow", out var workflowObj) && workflowObj is int workflowInt)
                {
                    SelectedWorkflow = (TextGeneratorWorkflow)workflowInt;
                }

                if (additionalData.TryGetValue("LoraEnabled", out var loraEnabledObj) && loraEnabledObj is bool loraEnabled)
                {
                    LoraEnabled = loraEnabled;
                }

                if (additionalData.TryGetValue("SelectedLora", out var selectedLoraObj) && selectedLoraObj is string selectedLora)
                {
                    SelectedLora = selectedLora;
                }
            }

            AddLog($"Prompt loaded: {savedPrompt.Name}");
            StatusBarMessage = $"Prompt loaded: {savedPrompt.Name}";
        }

        protected override void OnPromptError(string error)
        {
            AddLog($"ERROR: {error}");
            StatusBarMessage = error;
        }

        public override Dictionary<string, object> GetAdditionalPromptData()
        {
            var additionalData = new Dictionary<string, object>
            {
                { "SelectedWorkflow", (int)SelectedWorkflow },
                { "LoraEnabled", LoraEnabled },
                { "SelectedLora", SelectedLora }
            };
            return additionalData;
        }

        // Queue Management Methods

        private void AddToQueue()
        {
            if (!CanAddToQueue) return;

            var queueItem = new ImagePromptQueueItem
            {
                Prompt = ImagePrompt,
                AspectRatioIndex = AspectRatioIndex,
                Steps = Steps,
                Cfg = Cfg,
                Seed = Seed,
                Denoise = Denoise,
                LoraEnabled = LoraEnabled,
                SelectedLora = SelectedLora,
                SelectedWorkflow = SelectedWorkflow
            };

            PromptQueue.Add(queueItem);
            SaveQueueToFile();
            AddLog($"Added prompt to queue: {queueItem.DisplayPrompt}");
            StatusBarMessage = $"Added to queue ({PromptQueue.Count} items)";

            OnPropertyChanged(nameof(HasQueueItems));
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(PendingQueueCount));
            CommandManager.InvalidateRequerySuggested();

            // Auto-start queue processing if not already processing queue and not processing single image
            if (!IsProcessingQueue && !IsProcessing && PromptQueue.Any(q => q.Status == "Pending"))
            {
                _ = Task.Run(async () => await ProcessQueueAsync());
            }
        }

        private void RemoveFromQueue(ImagePromptQueueItem? item)
        {
            if (item == null) return;

            PromptQueue.Remove(item);
            SaveQueueToFile();
            AddLog($"Removed prompt from queue: {item.DisplayPrompt}");
            StatusBarMessage = $"Removed from queue ({PromptQueue.Count} items)";

            OnPropertyChanged(nameof(HasQueueItems));
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(PendingQueueCount));
            CommandManager.InvalidateRequerySuggested();
        }

        private void ClearQueue()
        {
            if (!PromptQueue.Any()) return;

            var count = PromptQueue.Count;
            PromptQueue.Clear();
            SaveQueueToFile();
            AddLog($"Cleared {count} items from queue");
            StatusBarMessage = "Queue cleared";

            OnPropertyChanged(nameof(HasQueueItems));
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(PendingQueueCount));
            CommandManager.InvalidateRequerySuggested();
        }

        private async Task ProcessQueueAsync()
        {
            if (!CanProcessQueue) return;

            IsProcessingQueue = true;
            AddLog("Waiting for other workflows to finish...");

            WorkflowQueueCoordinator.WorkflowLease lease;
            try
            {
                lease = await _workflowCoordinator.AcquireAsync("ImageGenerator", _cancellationTokenSource?.Token ?? CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue processing cancelled while waiting");
                IsProcessingQueue = false;
                return;
            }

            AddLog("=== Starting queue processing ===");

            using (lease)
            try
            {
                var pendingItems = PromptQueue.Where(q => q.Status == "Pending").ToList();

                foreach (var queueItem in pendingItems)
                {
                    if (queueItem.Status == "Failed") continue;

                    // Wait if paused
                    _pauseEvent.Wait(_cancellationTokenSource?.Token ?? CancellationToken.None);

                    try
                    {
                        queueItem.Status = "Processing";
                        queueItem.StartedAt = DateTime.Now;
                        queueItem.Progress = 0;
                        SaveQueueToFile();
                        OnPropertyChanged(nameof(PendingQueueCount));

                        AddLog($"Processing queue item: {queueItem.DisplayPrompt}");

                        ImagePrompt = queueItem.Prompt;
                        AspectRatioIndex = queueItem.AspectRatioIndex;
                        Steps = queueItem.Steps;
                        Cfg = queueItem.Cfg;
                        Seed = queueItem.Seed;
                        Denoise = queueItem.Denoise;
                        LoraEnabled = queueItem.LoraEnabled;
                        SelectedLora = queueItem.SelectedLora;
                        SelectedWorkflow = queueItem.SelectedWorkflow;

                        await ProcessQueueItemAsync(queueItem);

                        queueItem.Status = "Completed";
                        queueItem.CompletedAt = DateTime.Now;
                        queueItem.Progress = 100;
                        SaveQueueToFile();
                        AddLog($"Completed queue item: {queueItem.DisplayPrompt}");
                    }
                    catch (OperationCanceledException)
                    {
                        queueItem.Status = "Failed";
                        queueItem.ErrorMessage = "Cancelled";
                        SaveQueueToFile();
                        AddLog($"Queue item cancelled: {queueItem.DisplayPrompt}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        queueItem.Status = "Failed";
                        queueItem.ErrorMessage = ex.Message;
                        SaveQueueToFile();
                        AddLog($"ERROR processing queue item: {ex.Message}");
                    }
                    finally
                    {
                        OnPropertyChanged(nameof(CompletedQueueCount));
                    }
                }

                StatusBarMessage = $"Queue processing complete. {CompletedQueueCount}/{QueueCount} items completed.";
                AddLog("=== Queue processing ended ===");
            }
            finally
            {
                IsProcessingQueue = false;
                IsQueuePaused = false;
                _pauseEvent.Set();
            }
        }

        private async Task ProcessQueueItemAsync(ImagePromptQueueItem queueItem)
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                IsProcessing = true;

                // Clear previous result
                HasResultImage = false;
                ResultImageSource = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"Prompt: {queueItem.Prompt}");

                // Ensure ComfyUI is connected
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    AddLog("Connecting to ComfyUI WebSocket...");
                    await _comfyUIService.ConnectAsync(_cancellationTokenSource.Token);
                    AddLog("Connected to ComfyUI");
                }
                else
                {
                    AddLog("ComfyUI already connected");
                }

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Load workflow based on selected workflow
                var workflowFileName = SelectedWorkflow switch
                {
                    TextGeneratorWorkflow.Qwen2512 => "qwen2512API-text.json",
                    TextGeneratorWorkflow.Klien => "Klien-Text-API.json",
                    _ => "Zib-Zit.json"
                };
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", workflowFileName);
                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
                }

                AddLog($"Loading workflow: {workflowPath}");
                var workflowJson = await File.ReadAllTextAsync(workflowPath, _cancellationTokenSource.Token);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Update workflow with parameters
                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 10;
                queueItem.Progress = 10;

                var updatedWorkflow = UpdateWorkflowParameters(workflow);

                // Execute workflow
                ProcessingStatus = "Generating image...";
                ProcessingProgress = 30;
                queueItem.Progress = 30;
                AddLog("Executing workflow in ComfyUI...");

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 30 + (percent * 0.6);
                            queueItem.Progress = 30 + (percent * 0.6);
                            ProcessingStatus = $"Generating: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, _cancellationTokenSource.Token);

                // Force progress update after workflow completes
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    queueItem.Progress = 90;
                    ProcessingStatus = "Workflow completed, retrieving output...";
                });

                AddLog($"Workflow execution completed with prompt ID: {promptId}");

                // Get output images from ComfyUI output folder
                ProcessingStatus = "Retrieving output image...";
                ProcessingProgress = 95;
                AddLog("Looking for generated image...");

                List<byte[]> outputImages = new();
                int retryCount = 0;
                int maxRetries = 20;

                while (retryCount < maxRetries && !outputImages.Any())
                {
                    if (retryCount > 0)
                    {
                        AddLog($"Retry {retryCount}/{maxRetries} - waiting 5 seconds before checking again...");
                        await Task.Delay(5000, _cancellationTokenSource.Token);
                    }

                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    outputImages = await GetOutputImagesFromComfyUI(promptId);
                    retryCount++;
                }

                if (outputImages.Any())
                {
                    var outputImage = outputImages.First();
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "image-generator");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var prefix = SelectedWorkflow switch
                    {
                        TextGeneratorWorkflow.Qwen2512 => "qwen2512",
                        TextGeneratorWorkflow.Klien => "flux2-klein",
                        _ => "z-image"
                    };
                    var outputPath = Path.Combine(outputDir, $"{prefix}_{timestamp}.png");

                    await File.WriteAllBytesAsync(outputPath, outputImage);
                    AddLog($"Output saved: {outputPath}");

                    queueItem.OutputImagePath = outputPath;
                    ResultImagePath = outputPath;
                    LoadResultPreview(outputPath);
                    HasResultImage = true;

                    ProcessingProgress = 100;
                    queueItem.Progress = 100;
                    ProcessingStatus = "Complete!";
                    StatusBarMessage = $"Image generation complete - {Path.GetFileName(outputPath)}";
                }
                else
                {
                    AddLog("WARNING: No output images received after all retries");
                    throw new Exception("No output images were generated. Please check the ComfyUI console for errors.");
                }
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private bool IsRemoteUrl(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url))
                    return false;

                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();

                // Check if it's not a local address
                return !host.Equals("localhost") &&
                       !host.Equals("127.0.0.1") &&
                       !host.Equals("0.0.0.0") &&
                       !host.Equals("::1");
            }
            catch
            {
                return false;
            }
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

                // Dispose nested ViewModels
                _analyzer?.Dispose();
                _cameraEdit?.Dispose();
                _storyGenerator?.Dispose();
                _storyGeneratorQ?.Dispose();
                _storyGeneratorF?.Dispose();
                _storyGeneratorAmateur?.Dispose();
                _amateurGenerator?.Dispose();
                _cameraAngle?.Dispose();

                // Clear collections
                _availableLoras?.Clear();
                _promptQueue?.Clear();

                // Clear string properties
                _imagePrompt = string.Empty;
                _processingStatus = string.Empty;
                _logOutput = string.Empty;
                _comfyUIServer = string.Empty;
                _comfyUIPort = string.Empty;
                _statusBarMessage = string.Empty;
                _resultImagePath = string.Empty;
                _imageInfo = string.Empty;
                _selectedLora = string.Empty;

                _disposed = true;
            }
        }
    }
}
