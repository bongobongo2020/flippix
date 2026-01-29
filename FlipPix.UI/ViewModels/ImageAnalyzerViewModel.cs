using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Services;
using FlipPix.UI.Models;
using Microsoft.Win32;
using YamlDotNet.Serialization;
using ComfyUIService = FlipPix.ComfyUI.Services.ComfyUIService;

namespace FlipPix.UI.ViewModels
{
    public class StyleInfo
    {
        public string Name { get; set; } = string.Empty;
        public string PromptTemplate { get; set; } = string.Empty;
        public string WorkflowFile { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
    }

    public class ImageAnalyzerViewModel : INotifyPropertyChanged
    {
        private readonly ComfyUIService _comfyUIService;
        private readonly LMStudioService _lmStudioService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;

        private string _sourceImagePath = string.Empty;
        private BitmapImage? _sourceImageSource;
        private bool _hasSourceImage = false;
        private string _analysisText = "Analysis will appear here after you upload and analyze an image...";
        private bool _isAnalyzing = false;
        private bool _isGenerating = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private int _aspectRatioIndex = 2; // Default to 9:16 portrait
        private int _steps = 9;
        private double _cfg = 1.0;
        private long _seed = 0;
        private string _comfyUIServer = "127.0.0.1";
        private string _comfyUIPort = "8188";
        private List<LMStudioModel> _availableModels = new List<LMStudioModel>();
        private string _statusBarMessage = "Ready to analyze images";
        private bool _hasResultImage = false;
        private string _resultImagePath = string.Empty;
        private BitmapImage? _resultImageSource;
        private string _imageInfo = string.Empty;
        private System.Threading.CancellationTokenSource? _cancellationTokenSource;

        // Workflow parameters for amazing-z-image workflows
        private string _negativePrompt = "";
        private int _width = 944;
        private int _height = 1408;
        private double _denoise = 1.0;
        private int _selectedStyleIndex = 0;
        private TextGeneratorWorkflow _selectedWorkflow = TextGeneratorWorkflow.Zimage;

        // Unified style list from both workflows
        private List<StyleInfo> _allStyles = new List<StyleInfo>();

        // LORA list - using ObservableCollection for UI binding
        private ObservableCollection<string> _availableLoras = new();
        private string _selectedLora = string.Empty;
        private bool _loraEnabled = false;

        // Queue system
        private ObservableCollection<ImageAnalyzerQueueItem> _queueItems = new();
        private bool _isProcessingQueue = false;
        private ImageAnalyzerQueueItem? _currentQueueItem;
        private int _queueProgress = 0;
        private int _queueTotal = 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ImageAnalyzerViewModel(ComfyUIService comfyUIService, LMStudioService lmStudioService, IAppLogger logger, FlipPix.Core.Services.SettingsService settingsService)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            // Initialize commands
            BrowseImageCommand = new RelayCommand(BrowseImage, () => !IsAnalyzing && !IsGenerating);
            AnalyzeImageCommand = new RelayCommand(async () => await AnalyzeImageAsync(), () => HasSourceImage && !IsAnalyzing && !IsGenerating);
            GenerateImageCommand = new RelayCommand(async () => await GenerateImageAsync(), () => CanGenerate);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResultImage);
            TestLMStudioConnectionCommand = new RelayCommand(async () => await TestLMStudioConnectionAsync(), () => !IsAnalyzing && !IsGenerating);
            RefreshModelsCommand = new RelayCommand(async () => await RefreshModelsAsync(), () => !IsAnalyzing && !IsGenerating);
            RefreshLorasCommand = new RelayCommand(RefreshLoras, () => !IsAnalyzing && !IsGenerating);

            // Load ComfyUI settings
            if (_settingsService.Settings != null)
            {
                var uri = new Uri(_settingsService.Settings.BaseUrl);
                ComfyUIServer = uri.Host;
                ComfyUIPort = uri.Port.ToString();
            }

            // Initialize LM Studio
            InitializeLMStudio();

            // Load workflows and extract styles
            LoadWorkflowsAndStyles();

            _logger.LogInfo("Image Analyzer initialized");
        }

        // Properties
        public string SourceImagePath
        {
            get => _sourceImagePath;
            set
            {
                _sourceImagePath = value;
                OnPropertyChanged();
            }
        }

        public BitmapImage? SourceImageSource
        {
            get => _sourceImageSource;
            set
            {
                _sourceImageSource = value;
                OnPropertyChanged();
            }
        }

        public bool HasSourceImage
        {
            get => _hasSourceImage;
            set
            {
                _hasSourceImage = value;
                OnPropertyChanged();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public string AnalysisText
        {
            get => _analysisText;
            set
            {
                _analysisText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                _isAnalyzing = value;
                OnPropertyChanged();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsGenerating
        {
            get => _isGenerating;
            set
            {
                _isGenerating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
                OnPropertyChanged(nameof(GenerateButtonText));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public string GenerateButtonText => IsGenerating ? $"Generating... {ProgressPercentage}" : "Process & Generate Image";

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
                OnPropertyChanged(nameof(GenerateButtonText));
            }
        }

        public string ProgressPercentage => $"{ProcessingProgress:F0}%";

        public int AspectRatioIndex
        {
            get => _aspectRatioIndex;
            set
            {
                _aspectRatioIndex = value;
                OnPropertyChanged();
            }
        }

        public int Steps
        {
            get => _steps;
            set
            {
                _steps = value;
                OnPropertyChanged();
            }
        }

        public double Cfg
        {
            get => _cfg;
            set
            {
                _cfg = value;
                OnPropertyChanged();
            }
        }

        public long Seed
        {
            get => _seed;
            set
            {
                _seed = value;
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

        public string LMStudioServer
        {
            get
            {
                var uri = new Uri(_settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://localhost:1234");
                return uri.Host;
            }
            set
            {
                if (_settingsService.Settings?.LMStudioSettings != null && !string.IsNullOrEmpty(value))
                {
                    var currentUri = new Uri(_settingsService.Settings.LMStudioSettings.BaseUrl);
                    var newUri = new UriBuilder(currentUri) { Host = value }.Uri;
                    _settingsService.Settings.LMStudioSettings.BaseUrl = newUri.ToString();
                    OnPropertyChanged();

                    // Save settings when server changes
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            _settingsService.SaveSettings(_settingsService.Settings);
                            _logger.LogInfo($"Saved LM Studio server: {value}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error saving LM Studio settings: {ex.Message}");
                        }
                    });
                }
            }
        }

        public string LMStudioPort
        {
            get
            {
                var uri = new Uri(_settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://localhost:1234");
                return uri.Port.ToString();
            }
            set
            {
                if (_settingsService.Settings?.LMStudioSettings != null && !string.IsNullOrEmpty(value) && int.TryParse(value, out var port))
                {
                    var currentUri = new Uri(_settingsService.Settings.LMStudioSettings.BaseUrl);
                    var newUri = new UriBuilder(currentUri) { Port = port }.Uri;
                    _settingsService.Settings.LMStudioSettings.BaseUrl = newUri.ToString();
                    OnPropertyChanged();

                    // Save settings when port changes
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            _settingsService.SaveSettings(_settingsService.Settings);
                            _logger.LogInfo($"Saved LM Studio port: {value}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error saving LM Studio settings: {ex.Message}");
                        }
                    });
                }
            }
        }

        public string SelectedModel
        {
            get => _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
            set
            {
                if (_settingsService.Settings?.LMStudioSettings != null)
                {
                    _settingsService.Settings.LMStudioSettings.SelectedModel = value;
                    OnPropertyChanged();

                    // Save settings when model changes
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            _settingsService.SaveSettings(_settingsService.Settings);
                            _logger.LogInfo($"Saved LM Studio model selection: {value}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Error saving LM Studio settings: {ex.Message}");
                        }
                    });
                }
            }
        }

        public List<LMStudioModel> AvailableModels
        {
            get => _availableModels;
            set
            {
                _availableModels = value;
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
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
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

        public bool CanGenerate => HasSourceImage && !string.IsNullOrWhiteSpace(AnalysisText) && !IsAnalyzing && !IsGenerating;

        public TextGeneratorWorkflow SelectedWorkflow
        {
            get => _selectedWorkflow;
            set
            {
                _selectedWorkflow = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowLoraOptions));
                OnPropertyChanged(nameof(ShowStyleOptions));
            }
        }

        public bool ShowLoraOptions => SelectedWorkflow == TextGeneratorWorkflow.Zimage;

        public bool ShowStyleOptions => SelectedWorkflow == TextGeneratorWorkflow.Zimage;

        public int SelectedStyleIndex
        {
            get => _selectedStyleIndex;
            set
            {
                _selectedStyleIndex = value;
                OnPropertyChanged();
            }
        }

        public string[] StyleNames => _allStyles.Select(s => s.Name).ToArray();

        public StyleInfo? SelectedStyle => _allStyles.Count > 0 ? _allStyles[Math.Min(SelectedStyleIndex, _allStyles.Count - 1)] : null;

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

        public string NegativePrompt
        {
            get => _negativePrompt;
            set
            {
                _negativePrompt = value;
                OnPropertyChanged();
            }
        }

        public double Denoise
        {
            get => _denoise;
            set
            {
                _denoise = value;
                OnPropertyChanged();
            }
        }

        // Queue properties
        public ObservableCollection<ImageAnalyzerQueueItem> QueueItems
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
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public ImageAnalyzerQueueItem? CurrentQueueItem
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

        public string QueueProgressText => QueueTotal > 0 ? $"{QueueProgress}/{QueueTotal}" : "0/0";

        public int QueuedCount => QueueItems.Count(item => item.Status == "Queued");
        public int CompletedCount => QueueItems.Count(item => item.Status == "Completed");
        public int FailedCount => QueueItems.Count(item => item.Status == "Failed");

        // Commands
        public ICommand BrowseImageCommand { get; }
        public ICommand AnalyzeImageCommand { get; }
        public ICommand GenerateImageCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand TestLMStudioConnectionCommand { get; }
        public ICommand RefreshModelsCommand { get; }
        public ICommand RefreshLorasCommand { get; }

        // Methods
        private async void InitializeLMStudio()
        {
            try
            {
                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://localhost:1234";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);

                _logger.LogInfo($"LM Studio configured for {baseUrl}");

                // Try to load available models
                try
                {
                    var models = await _lmStudioService.GetAvailableModelsAsync();
                    AvailableModels = models;

                    // Select previously saved model or find a good default
                    var savedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel;

                    if (!string.IsNullOrEmpty(savedModel))
                    {
                        // Try to find the saved model
                        var savedModelObj = models.FirstOrDefault(m =>
                            m.Name == savedModel || m.Id == savedModel);
                        if (savedModelObj != null)
                        {
                            SelectedModel = savedModelObj.Name;
                            _logger.LogInfo($"Using saved model: {savedModelObj.Name}");
                            return;
                        }
                    }

                    // Try to find qwen-vl model as default
                    var qwenModel = models.FirstOrDefault(m =>
                        m.Name.ToLower().Contains("qwen") && m.Name.ToLower().Contains("vl"));

                    if (qwenModel != null)
                    {
                        SelectedModel = qwenModel.Name;
                        _logger.LogInfo($"Found and selected Qwen VL model: {qwenModel.Name}");
                    }
                    else if (models.Any())
                    {
                        // Use first available model
                        SelectedModel = models.First().Name;
                        _logger.LogInfo($"Using first available model: {models.First().Name}");
                    }
                    else
                    {
                        _logger.LogWarning("No models available in LM Studio");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Could not load LM Studio models: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error initializing LM Studio: {ex.Message}");
            }
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
                    _logger.LogWarning($"ZStyles workflow directory not found at {workflowDir}");
                    return;
                }

                // Load all workflow JSON files from ZStyles folder
                var workflowFiles = Directory.GetFiles(workflowDir, "*.json");
                _logger.LogInfo($"Found {workflowFiles.Length} workflow files in {workflowDir}");

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
                            PromptTemplate = "",  // Will be filled from analysis text
                            WorkflowFile = workflowFile,
                            NodeId = ""  // These are complete workflows, no single style node
                        });

                        _logger.LogInfo($"Loaded style: {styleName} from {Path.GetFileName(workflowFile)}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error loading workflow file {workflowFile}: {ex.Message}");
                    }
                }

                // Sort styles alphabetically
                _allStyles = _allStyles.OrderBy(s => s.Name).ToList();

                _logger.LogInfo($"Loaded {_allStyles.Count} total styles from ZStyles workflows");
                OnPropertyChanged(nameof(StyleNames));

                // Load LORAs
                LoadAvailableLoras();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading workflows: {ex.Message}");
            }
        }

        private void RefreshLoras()
        {
            LoadAvailableLoras();
            _logger.LogInfo("Refreshed LoRA list");
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
                        _logger.LogWarning("Remote output path not configured in settings - cannot derive LoRA path");
                        return null;
                    }

                    // Also check if RemoteLoraFolderPath is explicitly set (for custom paths)
                    var explicitLoraPath = _settingsService.Settings?.RemoteLoraFolderPath;
                    if (!string.IsNullOrEmpty(explicitLoraPath))
                    {
                        loraBasePath = explicitLoraPath;
                        _logger.LogInfo($"Using explicitly configured remote LoRA path: {loraBasePath}");
                    }
                    else
                    {
                        // Derive LoRA path from output path
                        // Expected: \\server\ComfyUI\output -> \\server\ComfyUI\models\loras
                        var comfyUIRoot = Path.GetDirectoryName(remoteOutputPath);
                        if (string.IsNullOrEmpty(comfyUIRoot))
                        {
                            _logger.LogWarning($"Could not derive ComfyUI root from output path: {remoteOutputPath}");
                            return null;
                        }

                        loraBasePath = Path.Combine(comfyUIRoot, "models", "loras");
                        _logger.LogInfo($"Derived remote LoRA path from output path: {loraBasePath}");
                    }

                    // For remote paths, check if directory exists directly
                    if (Directory.Exists(loraBasePath))
                    {
                        _logger.LogInfo($"Remote LoRA directory exists: {loraBasePath}");
                        return loraBasePath;
                    }
                    else
                    {
                        _logger.LogWarning($"Remote LoRA directory not found: {loraBasePath}");
                        return null;
                    }
                }
                else
                {
                    // Use local ComfyUI path
                    loraBasePath = _settingsService.Settings?.ComfyUIFolderPath;
                    if (string.IsNullOrEmpty(loraBasePath))
                    {
                        _logger.LogWarning("ComfyUI installation path not configured");
                        return null;
                    }
                }

                // First try to get path from extra_model_paths.yaml (local only)
                var extraModelPathsFile = Path.Combine(loraBasePath, "extra_model_paths.yaml");

                if (File.Exists(extraModelPathsFile))
                {
                    try
                    {
                        var yamlContent = File.ReadAllText(extraModelPathsFile);
                        var deserializer = new DeserializerBuilder().Build();
                        var yamlData = deserializer.Deserialize<Dictionary<string, object>>(yamlContent);

                        if (yamlData != null)
                        {
                            string basePath = string.Empty;
                            string lorasRelativePath = string.Empty;

                            // Check for "comfyui" section (most common format)
                            if (yamlData.ContainsKey("comfyui"))
                            {
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

                                    // Get base_path if it exists
                                    if (comfyuiStringDict.ContainsKey("base_path"))
                                    {
                                        basePath = comfyuiStringDict["base_path"]?.ToString() ?? string.Empty;
                                    }

                                    // Get loras path if it exists
                                    if (comfyuiStringDict.ContainsKey("loras"))
                                    {
                                        lorasRelativePath = comfyuiStringDict["loras"]?.ToString() ?? string.Empty;
                                    }
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
                                }
                                else
                                {
                                    // Use just the loras path (might be absolute)
                                    fullLoraPath = lorasRelativePath;
                                }

                                // Normalize path separators
                                fullLoraPath = fullLoraPath.Replace('/', Path.DirectorySeparatorChar);

                                if (Directory.Exists(fullLoraPath))
                                {
                                    return fullLoraPath;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Error reading extra_model_paths.yaml: {ex.Message}");
                    }
                }

                // Fallback to default ComfyUI models directory
                var defaultLoraPath = Path.Combine(loraBasePath, "models", "loras");
                if (Directory.Exists(defaultLoraPath))
                {
                    return defaultLoraPath;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting LoRA model path: {ex.Message}");
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
                _logger.LogError($"Error loading LoRAs: {ex.Message}");
                AvailableLoras.Clear();
                AvailableLoras.Add("Error loading LoRAs");
            }
        }

        private void LoadLorasFromDirectory(string loraPath, string pathDescription)
        {
            _logger.LogInfo($"Looking for LoRAs in {pathDescription}: {loraPath}");

            if (!Directory.Exists(loraPath))
            {
                _logger.LogWarning($"LoRA directory not found: {loraPath}");
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

                _logger.LogInfo($"Loaded {AvailableLoras.Count} LoRAs from {loraPath}");
            }
            else
            {
                AvailableLoras.Add("No LoRAs available");
                _logger.LogInfo($"No LoRA files found in {pathDescription}");
            }
        }

        private void ExtractStylesFromWorkflowNew(string workflowJson, string workflowPath, string workflowLabel)
        {
            try
            {
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // New format has "nodes" array
                if (workflow.TryGetProperty("nodes", out var nodes))
                {
                    foreach (var node in nodes.EnumerateArray())
                    {
                        // Title is directly on the node, not under _meta
                        if (node.TryGetProperty("title", out var title))
                        {
                            var titleStr = title.GetString() ?? "";
                            if (titleStr.StartsWith("STYLE: "))
                            {
                                string styleName = titleStr.Substring(7); // Remove "STYLE: " prefix
                                string promptTemplate = "";
                                string nodeId = "";

                                // Get node ID
                                if (node.TryGetProperty("id", out var id))
                                {
                                    nodeId = id.GetUInt32().ToString();
                                }

                                // Try to extract from widgets_values (new format)
                                if (node.TryGetProperty("widgets_values", out var widgetsValues))
                                {
                                    if (widgetsValues.ValueKind == JsonValueKind.Array &&
                                        widgetsValues.GetArrayLength() > 0)
                                    {
                                        promptTemplate = widgetsValues[0].GetString() ?? "";
                                    }
                                }

                                // Add to styles list
                                _allStyles.Add(new StyleInfo
                                {
                                    Name = styleName,
                                    PromptTemplate = promptTemplate,
                                    WorkflowFile = workflowPath,
                                    NodeId = nodeId
                                });

                                _logger.LogInfo($"[{workflowLabel}] Found style: {styleName} (node {nodeId})");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error extracting styles from {workflowPath}: {ex.Message}");
            }
        }

        private void ExtractStylesFromApiWorkflow(string workflowJson, string workflowPath)
        {
            try
            {
                // Clear previous styles
                _allStyles.Clear();

                var workflow = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson);

                if (workflow == null)
                {
                    _logger.LogWarning("Workflow is null or empty");
                    return;
                }

                _logger.LogInfo($"Parsing workflow with {workflow.Count} nodes from: {workflowPath}");

                // API format is a dictionary with node IDs as keys
                foreach (var kvp in workflow)
                {
                    var nodeId = kvp.Key;
                    var node = kvp.Value;

                    // Check if this node has _meta with title starting with "STYLE: "
                    if (node.TryGetProperty("_meta", out var meta))
                    {
                        if (meta.TryGetProperty("title", out var title))
                        {
                            var titleStr = title.GetString() ?? "";
                            if (titleStr.StartsWith("STYLE: "))
                            {
                                string styleName = titleStr.Substring(7); // Remove "STYLE: " prefix
                                string promptTemplate = "";

                                // Get the value from inputs
                                if (node.TryGetProperty("inputs", out var inputs))
                                {
                                    if (inputs.TryGetProperty("value", out var value))
                                    {
                                        promptTemplate = value.GetString() ?? "";
                                    }
                                }

                                // Add to styles list
                                _allStyles.Add(new StyleInfo
                                {
                                    Name = styleName,
                                    PromptTemplate = promptTemplate,
                                    WorkflowFile = workflowPath,
                                    NodeId = nodeId
                                });

                                _logger.LogInfo($"Found style: {styleName} (node {nodeId})");
                            }
                        }
                    }
                }

                _logger.LogInfo($"Total styles extracted: {_allStyles.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error extracting styles from API workflow {workflowPath}: {ex.Message}");
            }
        }

        private void BrowseImage()
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All Files|*.*",
                Title = "Select an Image to Analyze"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                SourceImagePath = openFileDialog.FileName;
                LoadSourceImagePreview(openFileDialog.FileName);
                HasSourceImage = true;
                StatusBarMessage = $"Loaded: {Path.GetFileName(openFileDialog.FileName)}";
                _logger.LogInfo($"Image selected: {openFileDialog.FileName}");
            }
        }

        private void LoadSourceImagePreview(string imagePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                SourceImageSource = bitmap;
                _logger.LogInfo("Source image preview loaded");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading source image preview: {ex.Message}");
                System.Windows.MessageBox.Show($"Error loading image:\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private async Task AnalyzeImageAsync()
        {
            if (!HasSourceImage) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new System.Threading.CancellationTokenSource();

            try
            {
                _logger.LogInfo("=== Starting image analysis with LM Studio QwenVL ===");
                IsAnalyzing = true;
                StatusBarMessage = "Analyzing image...";
                AnalysisText = "Analyzing image with LM Studio QwenVL AI...";

                // Use LM Studio for image analysis
                var analysisPrompt = "Describe this image in detail, including colors, objects, composition, and mood.";

                var analysisResult = await _lmStudioService.AnalyzeImageAsync(
                    SelectedModel,
                    SourceImagePath,
                    analysisPrompt,
                    maxTokens: 500,
                    _cancellationTokenSource.Token);

                if (!string.IsNullOrEmpty(analysisResult))
                {
                    AnalysisText = analysisResult;
                    StatusBarMessage = "Analysis complete - ready to generate";
                    _logger.LogInfo("Analysis completed successfully with LM Studio");
                }
                else
                {
                    AnalysisText = "Analysis completed, but no text was returned from LM Studio.";
                    StatusBarMessage = "Analysis complete (no output detected)";
                    _logger.LogWarning("Analysis completed but no text output was detected");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Analysis cancelled by user");
                AnalysisText = "Analysis cancelled";
                StatusBarMessage = "Analysis cancelled";
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error analyzing image: {ex.Message}");
                AnalysisText = $"Error analyzing image:\n{ex.Message}";
                StatusBarMessage = "Analysis failed";
                System.Windows.MessageBox.Show($"Error analyzing image:\n\n{ex.Message}\n\nPlease ensure LM Studio is running on {LMStudioServer}:{LMStudioPort} and the QwenVL model is loaded.", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                _logger.LogInfo("=== Image analysis ended ===");
            }
        }

        private async Task<string> TryGetAnalysisOutputAsync(string promptId)
        {
            try
            {
                // Wait a bit for the output to be available
                await Task.Delay(3000);

                // Try to get output from history API using HttpClient directly
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                var historyUrl = $"{baseUrl}/history/{promptId}";

                _logger.LogInfo($"Fetching analysis output from: {historyUrl}");

                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var response = await httpClient.GetAsync(historyUrl);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    _logger.LogInfo($"History response received: {responseContent.Substring(0, Math.Min(500, responseContent.Length))}...");

                    // Parse the response to extract text output from node 60 (ShowText)
                    var historyData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseContent);

                    if (historyData != null && historyData.ContainsKey(promptId))
                    {
                        var promptData = historyData[promptId];
                        _logger.LogInfo("Found prompt data in history");

                        if (promptData.TryGetProperty("outputs", out var outputs))
                        {
                            _logger.LogInfo("Found outputs in prompt data");

                            // Node 60 is the ShowText node that displays QwenVL output
                            if (outputs.TryGetProperty("60", out var node60))
                            {
                                _logger.LogInfo("Found node 60 (ShowText) in outputs");

                                if (node60.TryGetProperty("text", out var textArray))
                                {
                                    if (textArray.ValueKind == JsonValueKind.Array && textArray.GetArrayLength() > 0)
                                    {
                                        var textOutput = textArray[0].GetString() ?? string.Empty;
                                        _logger.LogInfo($"Retrieved text output: {textOutput.Substring(0, Math.Min(100, textOutput.Length))}...");
                                        return textOutput;
                                    }
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Node 60 not found in outputs. Available nodes: " + string.Join(", ", outputs.EnumerateObject().Select(p => p.Name)));
                            }
                        }
                        else
                        {
                            _logger.LogWarning("No 'outputs' property found in prompt data");
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Prompt ID {promptId} not found in history response");
                    }
                }
                else
                {
                    _logger.LogError($"History request failed with status: {response.StatusCode}");
                }

                _logger.LogWarning("Could not find text output in history response");
                return "Analysis completed. Edit this text and click 'Process & Generate Image' to create your image.";
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error retrieving analysis output: {ex.Message}\n{ex.StackTrace}");
                return "Analysis completed. Edit this text and click 'Process & Generate Image' to create your image.";
            }
        }

        private Task GenerateImageAsync()
        {
            if (!CanGenerate) return Task.CompletedTask;

            // Set IsGenerating immediately so the UI updates right away
            IsGenerating = true;
            ProcessingProgress = 0;
            ProcessingStatus = "Starting generation...";
            StatusBarMessage = "Preparing to generate image...";

            // Create a new queue item from current settings
            var queueItem = new ImageAnalyzerQueueItem
            {
                SourceImagePath = SourceImagePath,
                Prompt = AnalysisText,
                SelectedWorkflow = SelectedWorkflow,
                SelectedStyleIndex = SelectedStyleIndex,
                StyleName = SelectedStyle?.Name ?? "Unknown",
                AspectRatioIndex = AspectRatioIndex,
                Steps = Steps,
                Cfg = Cfg,
                Seed = Seed,
                Denoise = Denoise,
                LoraEnabled = LoraEnabled,
                SelectedLora = SelectedLora,
                NegativePrompt = NegativePrompt,
                Width = _width,
                Height = _height,
                Status = "Queued"
            };

            QueueItems.Add(queueItem);
            _logger.LogInfo($"Added item to queue: {queueItem.StyleName} - {queueItem.DisplayPrompt}");

            // Start processing if not already processing
            if (!IsProcessingQueue)
            {
                _ = Task.Run(() => ProcessQueueAsync());
            }

            return Task.CompletedTask;
        }

        private async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new System.Threading.CancellationTokenSource();

            try
            {
                IsProcessingQueue = true;
                var queuedItems = QueueItems.Where(item => item.Status == "Queued").ToList();
                QueueTotal = queuedItems.Count;
                QueueProgress = 0;

                _logger.LogInfo($"=== Starting queue processing ({QueueTotal} items) ===");

                foreach (var item in queuedItems)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        _logger.LogInfo("Queue processing cancelled");
                        break;
                    }

                    CurrentQueueItem = item;
                    item.Status = "Processing";
                    item.StartedAt = DateTime.Now;

                    _logger.LogInfo($"Processing queue item {QueueProgress + 1}/{QueueTotal}: {item.StyleName}");

                    try
                    {
                        await ProcessQueueItemAsync(item, _cancellationTokenSource.Token);
                        item.Status = "Completed";
                        item.CompletedAt = DateTime.Now;
                        item.Progress = 100;

                        _logger.LogInfo($"Completed queue item {QueueProgress + 1}/{QueueTotal}: {item.StyleName}");
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = "Cancelled";
                        item.ErrorMessage = "Cancelled by user";
                        _logger.LogInfo($"Queue item cancelled: {item.StyleName}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        item.Status = "Failed";
                        item.ErrorMessage = ex.Message;
                        item.Progress = 0;
                        _logger.LogError($"Queue item failed: {item.StyleName} - {ex.Message}");
                    }
                    finally
                    {
                        QueueProgress++;
                    }
                }

                _logger.LogInfo($"=== Queue processing completed ({CompletedCount} successful, {FailedCount} failed) ===");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error processing queue: {ex}");
            }
            finally
            {
                IsProcessingQueue = false;
                CurrentQueueItem = null;
                QueueProgress = 0;
                QueueTotal = 0;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task ProcessQueueItemAsync(ImageAnalyzerQueueItem item, System.Threading.CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInfo($"=== Starting image generation with {item.SelectedWorkflow} ===");

                // Update UI on dispatcher thread
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    IsGenerating = true;
                    HasResultImage = false;
                    ResultImageSource = null;
                });
                GC.Collect();
                GC.WaitForPendingFinalizers();

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 0;
                    ProcessingStatus = "Preparing workflow...";
                    StatusBarMessage = $"Generating image ({QueueProgress + 1}/{QueueTotal})...";
                });
                _logger.LogInfo($"Using prompt: {item.Prompt}");
                _logger.LogInfo($"Selected workflow: {item.SelectedWorkflow}");

                // Ensure ComfyUI is connected
                if (!_comfyUIService.IsConnected)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProcessingStatus = "Connecting to ComfyUI...";
                    });
                    await _comfyUIService.ConnectAsync(cancellationToken);
                }

                // Load the appropriate workflow based on selected workflow
                string workflowPath;
                StyleInfo? selectedStyle = null;

                switch (item.SelectedWorkflow)
                {
                    case TextGeneratorWorkflow.Qwen2512:
                        workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "qwen2512API-text.json");
                        _logger.LogInfo($"Using Qwen2512 workflow");
                        break;

                    case TextGeneratorWorkflow.Klien:
                        workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "Klien-Text-API.json");
                        _logger.LogInfo($"Using Klien workflow");
                        break;

                    case TextGeneratorWorkflow.Zimage:
                    default:
                        // Get the workflow for the item's style
                        selectedStyle = _allStyles.FirstOrDefault(s => s.Name == item.StyleName);
                        if (selectedStyle == null)
                        {
                            _logger.LogError($"Style not found: {item.StyleName}");
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                StatusBarMessage = "Error: Style not found";
                            });
                            return;
                        }
                        workflowPath = selectedStyle.WorkflowFile;
                        _logger.LogInfo($"Using Zimage workflow with style: {selectedStyle.Name}");
                        break;
                }

                if (!File.Exists(workflowPath))
                {
                    _logger.LogError($"Workflow file not found: {workflowPath}");
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        StatusBarMessage = "Error: Workflow file not found";
                    });
                    return;
                }

                // Load the workflow file
                var workflowJson = await File.ReadAllTextAsync(workflowPath, cancellationToken);
                _logger.LogInfo($"Loaded workflow file: {workflowPath}, JSON length: {workflowJson.Length}");

                // For ZStyles workflows, directly parse to Dictionary to preserve nested structures
                var workflow = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowJson);

                if (workflow == null)
                {
                    _logger.LogError("Failed to parse workflow JSON");
                    return;
                }

                _logger.LogInfo($"Workflow deserialized successfully, node count: {workflow.Count}");

                // Update workflow with generation parameters
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingStatus = "Configuring generation settings...";
                    ProcessingProgress = 10;
                    item.Progress = 10;
                });

                // Temporarily set the view model properties for the workflow update
                var originalAnalysisText = AnalysisText;
                var originalAspectRatioIndex = AspectRatioIndex;
                var originalSteps = Steps;
                var originalCfg = Cfg;
                var originalSeed = Seed;
                var originalLoraEnabled = LoraEnabled;
                var originalSelectedLora = SelectedLora;
                var originalSelectedStyleIndex = SelectedStyleIndex;

                AnalysisText = item.Prompt;
                AspectRatioIndex = item.AspectRatioIndex;
                Steps = item.Steps;
                Cfg = item.Cfg;
                Seed = item.Seed;
                _width = item.Width;
                _height = item.Height;
                _negativePrompt = item.NegativePrompt;
                _selectedStyleIndex = item.SelectedStyleIndex;

                // Only set LORA properties if using Zimage workflow
                if (item.SelectedWorkflow == TextGeneratorWorkflow.Zimage)
                {
                    LoraEnabled = item.LoraEnabled;
                    SelectedLora = item.SelectedLora;
                }
                else
                {
                    LoraEnabled = false;
                    SelectedLora = string.Empty;
                }

                var updatedWorkflow = UpdateWorkflowForGenerationSimple(workflow, item.SelectedWorkflow);

                // Restore original properties
                AnalysisText = originalAnalysisText;
                AspectRatioIndex = originalAspectRatioIndex;
                Steps = originalSteps;
                Cfg = originalCfg;
                Seed = originalSeed;
                LoraEnabled = originalLoraEnabled;
                SelectedLora = originalSelectedLora;
                _selectedStyleIndex = originalSelectedStyleIndex;

                // Execute workflow
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingStatus = $"Generating image {QueueProgress + 1}/{QueueTotal}...";
                    ProcessingProgress = 30;
                    item.Progress = 30;
                });
                _logger.LogInfo($"Executing {item.SelectedWorkflow} generation workflow...");

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 30 + (percent * 0.6); // Scale to 30-90%
                            item.Progress = ProcessingProgress;
                            ProcessingStatus = $"Generating {QueueProgress + 1}/{QueueTotal}: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, cancellationToken);

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    item.Progress = 90;
                    ProcessingStatus = "Workflow completed, retrieving output...";
                });

                _logger.LogInfo($"Workflow execution completed with prompt ID: {promptId}");

                // Retrieve output image
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingStatus = "Retrieving generated image...";
                    ProcessingProgress = 95;
                    item.Progress = 95;
                });

                // Give ComfyUI time to save the image
                _logger.LogInfo("Waiting for image to be saved...");
                await Task.Delay(3000, cancellationToken);

                // First, try to get the specific output file from this prompt's history
                _logger.LogInfo($"Getting output file for prompt ID: {promptId}");
                List<byte[]> outputImages = new();
                int retryCount = 0;
                int maxRetries = 8;

                while (retryCount < maxRetries && !outputImages.Any())
                {
                    if (retryCount > 0)
                    {
                        _logger.LogInfo($"Retry {retryCount}/{maxRetries} - waiting 2 seconds...");
                        await Task.Delay(2000, cancellationToken);
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // Try to get the specific file from this prompt's history first
                    outputImages = await GetOutputImageFromPromptHistory(promptId, item.SelectedWorkflow);

                    // Fallback to scanning output folder if history lookup fails
                    if (!outputImages.Any())
                    {
                        _logger.LogInfo("History lookup failed, falling back to folder scan...");
                        outputImages = await GetMostRecentImageFromOutput(item.SelectedWorkflow);
                    }

                    retryCount++;

                    if (!outputImages.Any())
                    {
                        _logger.LogInfo($"No new images found on attempt {retryCount}");
                    }
                }

                if (outputImages.Any())
                {
                    var outputImage = outputImages.First();
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "image-analyzer");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var prefix = item.SelectedWorkflow switch
                    {
                        TextGeneratorWorkflow.Qwen2512 => "qwen2512",
                        TextGeneratorWorkflow.Klien => "flux2-klein",
                        _ => "z-image"
                    };
                    var outputPath = Path.Combine(outputDir, $"{prefix}_{timestamp}.png");

                    await File.WriteAllBytesAsync(outputPath, outputImage);
                    _logger.LogInfo($"Output saved: {outputPath}");

                    item.OutputImagePath = outputPath;

                    // Only update the main result preview if this is the most recent item
                    if (item == CurrentQueueItem)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ResultImagePath = outputPath;
                            LoadResultPreview(outputPath);
                            HasResultImage = true;
                        });
                    }

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProcessingProgress = 100;
                        item.Progress = 100;
                        ProcessingStatus = "Complete!";
                        StatusBarMessage = $"Image generated - {Path.GetFileName(outputPath)}";
                    });
                }
                else
                {
                    _logger.LogWarning("No output images received after all retries");
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProcessingStatus = "No output generated";
                        StatusBarMessage = "Generation failed - no output";
                    });
                    throw new InvalidOperationException("No output images were generated");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Image generation cancelled by user");
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingStatus = "Cancelled";
                    ProcessingProgress = 0;
                    StatusBarMessage = "Generation cancelled";
                });
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error generating image: {ex}");
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingStatus = "Error occurred";
                    ProcessingProgress = 0;
                    StatusBarMessage = "Generation failed";
                });
                throw;
            }
            finally
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    IsGenerating = false;
                });
                _logger.LogInfo("=== Image generation ended ===");
            }
        }

        private object UpdateWorkflowForGeneration(JsonElement workflow)
        {
            var selectedStyle = SelectedStyle;

            _logger.LogInfo($"Using style: {selectedStyle?.Name ?? "None"}");

            // Parse the workflow
            var workflowJson = workflow.GetRawText();
            var workflowRoot = System.Text.Json.Nodes.JsonNode.Parse(workflowJson);

            if (workflowRoot == null)
            {
                _logger.LogError("Failed to parse workflow JSON");
                return workflow;
            }

            // Check if new format (has "nodes" array) - need to convert to API format
            if (workflowRoot["nodes"] is System.Text.Json.Nodes.JsonArray nodesArray)
            {
                // Build ComfyUI API prompt format (dictionary with node IDs as keys)
                var apiPrompt = new Dictionary<string, object>();

                foreach (var node in nodesArray)
                {
                    if (node == null) continue;

                    var nodeObj = node.AsObject();
                    var nodeId = nodeObj["id"]?.GetValue<uint>();
                    var nodeTypeStr = nodeId?.ToString() ?? "0";
                    var nodeType = nodeObj["type"]?.GetValue<string>();

                    // Create the node inputs dictionary
                    var inputs = new Dictionary<string, object>();

                    // Convert widgets_values to inputs
                    if (nodeObj["widgets_values"] is System.Text.Json.Nodes.JsonArray widgets)
                    {
                        // Handle specific node types
                        if (nodeType == "CLIPTextEncode")
                        {
                            if (widgets.Count > 0)
                            {
                                // Update with analysis text
                                inputs["text"] = AnalysisText;
                                _logger.LogInfo($"Updated CLIPTextEncode node {nodeId} with analysis text");
                            }
                            // Add clip input reference
                            AddInputReferencesToDict(nodeObj, inputs);
                        }
                        else if (nodeType == "EmptyLatentImage" || nodeType == "EmptySD3LatentImage")
                        {
                            if (widgets.Count >= 3)
                            {
                                var dimensions = GetDimensionsForAspectRatio(AspectRatioIndex);
                                inputs["width"] = dimensions.Item1;
                                inputs["height"] = dimensions.Item2;
                                if (widgets[2] != null)
                                {
                                    inputs["batch_size"] = GetWidgetValue(widgets[2]);
                                }
                                _logger.LogInfo($"Updated {nodeType} node {nodeId} dimensions: {dimensions.Item1}x{dimensions.Item2}");
                            }
                            AddInputReferencesToDict(nodeObj, inputs);
                        }
                        else if (nodeType == "KSamplerAdvanced")
                        {
                            // widgets_values: ['enable', seed, 'fixed', steps, cfg, sampler_name, scheduler, start_at_step, end_at_step, return_with_leftover_noise]
                            if (widgets.Count >= 10)
                            {
                                var actualSeed = Seed == 0 ? (long)new Random().NextInt64(0, 999999999999999) : (long)Seed;
                                inputs["add_noise"] = GetWidgetValue(widgets[0]);
                                inputs["noise_seed"] = actualSeed;
                                inputs["control_after_generate"] = GetWidgetValue(widgets[2]);
                                inputs["steps"] = GetWidgetValue(widgets[3]);
                                inputs["cfg"] = GetWidgetValue(widgets[4]);
                                inputs["sampler_name"] = GetWidgetValue(widgets[5]);
                                inputs["scheduler"] = GetWidgetValue(widgets[6]);
                                inputs["start_at_step"] = GetWidgetValue(widgets[7]);
                                inputs["end_at_step"] = GetWidgetValue(widgets[8]);
                                inputs["return_with_leftover_noise"] = GetWidgetValue(widgets[9]);
                                _logger.LogInfo($"Updated KSamplerAdvanced node {nodeId} seed: {actualSeed}");
                            }
                            AddInputReferencesToDict(nodeObj, inputs);
                        }
                        else if (nodeType == "SaveImage")
                        {
                            if (widgets.Count > 0)
                            {
                                var timestamp = DateTime.Now.ToString("yyyy_MM_dd");
                                inputs["filename_prefix"] = $"ZImage/{timestamp}/ZI";
                                _logger.LogInfo($"Updated SaveImage node {nodeId} filename prefix");
                            }
                            AddInputReferencesToDict(nodeObj, inputs);
                        }
                        else
                        {
                            // For other nodes, add input references
                            AddInputReferencesToDict(nodeObj, inputs);
                        }
                    }
                    else
                    {
                        // No widgets_values, just add input references
                        AddInputReferencesToDict(nodeObj, inputs);
                    }

                    var nodeData = new Dictionary<string, object>
                    {
                        ["class_type"] = nodeType ?? "",
                        ["inputs"] = inputs
                    };

                    apiPrompt[nodeTypeStr] = nodeData;
                }

                return apiPrompt;
            }

            // API format workflow - update the nodes directly
            // Check if it's already in API format (dictionary with node IDs)
            if (workflowRoot.AsObject().Count > 0 && workflowRoot["nodes"] == null)
            {
                var apiPrompt = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson)!;

                // Build a complete resolution map for all editor-only nodes
                var resolutionMap = new Dictionary<string, (string targetNode, int outputIndex)>();
                var editorOnlyNodes = new HashSet<string>();

                // First pass: identify all editor-only nodes and build resolution map
                foreach (var kvp in apiPrompt)
                {
                    var nodeId = kvp.Key;
                    var nodeData = kvp.Value;
                    if (nodeData.TryGetProperty("class_type", out var classTypeElem))
                    {
                        var classType = classTypeElem.GetString() ?? "";

                        // Identify editor-only node types
                        bool isEditorOnly = classType == "Reroute" ||
                                           classType == "Reroute (rgthree)" ||
                                           classType == "Node Collector (rgthree)" ||
                                           classType == "Display Any (rgthree)" ||
                                           classType == "Note" ||
                                           classType == "Fast Muter (rgthree)" ||
                                           classType == "Mute / Bypass Repeater (rgthree)" ||
                                           classType.Contains("Reroute");

                        if (isEditorOnly)
                        {
                            editorOnlyNodes.Add(nodeId);

                            // Build resolution map for reroutes
                            if (classType.Contains("Reroute"))
                            {
                                var inputs = nodeData.GetProperty("inputs");
                                if (inputs.ValueKind == JsonValueKind.Object && inputs.EnumerateObject().Any())
                                {
                                    var inputProp = inputs.EnumerateObject().First();
                                    if (inputProp.Value.ValueKind == JsonValueKind.Array)
                                    {
                                        var arr = inputProp.Value.EnumerateArray().ToArray();
                                        if (arr.Length >= 2)
                                        {
                                            var targetNode = arr[0].GetString() ?? "";
                                            var outputIndex = arr[1].GetInt32();
                                            resolutionMap[nodeId] = (targetNode, outputIndex);
                                        }
                                    }
                                }
                            }
                            // For Node Collectors, map each numbered output to its input
                            else if (classType == "Node Collector (rgthree)")
                            {
                                var inputs = nodeData.GetProperty("inputs");
                                if (inputs.ValueKind == JsonValueKind.Object)
                                {
                                    var inputNum = 0;
                                    foreach (var inputProp in inputs.EnumerateObject().OrderBy(x => x.Name))
                                    {
                                        if (inputProp.Value.ValueKind == JsonValueKind.Array)
                                        {
                                            var arr = inputProp.Value.EnumerateArray().ToArray();
                                            if (arr.Length >= 2)
                                            {
                                                var targetNode = arr[0].GetString() ?? "";
                                                var outputIndex = arr[1].GetInt32();
                                                // Node collector outputs are accessed as "nodeId:outputNum"
                                                resolutionMap[$"{nodeId}:{inputNum}"] = (targetNode, outputIndex);
                                            }
                                        }
                                        inputNum++;
                                    }
                                }
                            }
                        }
                    }
                }

                _logger.LogInfo($"Identified {editorOnlyNodes.Count} editor-only nodes, built {resolutionMap.Count} resolution mappings");

                // Clone the workflow and resolve all references
                var updatedPrompt = new Dictionary<string, object>();

                // Determine which style is selected
                string? selectedSwitchNode = null;
                string? selectedInputName = null;
                if (selectedStyle != null && int.TryParse(selectedStyle.NodeId, out int styleNodeId))
                {
                    if (styleNodeId < 1000)
                    {
                        selectedSwitchNode = "87";
                        var styleMap = new Dictionary<string, string>
                        {
                            ["125"] = "any_02", ["101"] = "any_03", ["117"] = "any_04", ["63"] = "any_05",
                            ["47"] = "any_06", ["38"] = "any_07", ["92"] = "any_08", ["37"] = "any_09",
                            ["41"] = "any_10", ["122"] = "any_11", ["43"] = "any_12", ["93"] = "any_13",
                            ["130"] = "any_14", ["45"] = "any_15", ["124"] = "any_16", ["459"] = "any_17",
                            ["460"] = "any_18", ["461"] = "any_19"
                        };
                        styleMap.TryGetValue(selectedStyle.NodeId, out selectedInputName);
                    }
                    else if (styleNodeId >= 5000)
                    {
                        selectedSwitchNode = "5087";
                        var styleMap = new Dictionary<string, string>
                        {
                            ["5125"] = "any_02", ["5101"] = "any_03", ["5117"] = "any_04", ["5063"] = "any_05",
                            ["5047"] = "any_06", ["5038"] = "any_07", ["5092"] = "any_08", ["5037"] = "any_09",
                            ["5041"] = "any_10", ["5122"] = "any_11", ["5043"] = "any_12", ["5093"] = "any_13",
                            ["5130"] = "any_14", ["5045"] = "any_15", ["5124"] = "any_16", ["5459"] = "any_17",
                            ["5460"] = "any_18", ["5461"] = "any_19"
                        };
                        styleMap.TryGetValue(selectedStyle.NodeId, out selectedInputName);
                    }
                    if (!string.IsNullOrEmpty(selectedInputName))
                    {
                        _logger.LogInfo($"Style '{selectedStyle.Name}' (node {selectedStyle.NodeId}) -> {selectedSwitchNode} input {selectedInputName}");
                    }
                }

                // Process each node
                foreach (var kvp in apiPrompt)
                {
                    var nodeId = kvp.Key;
                    var nodeData = kvp.Value;
                    var nodeObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(nodeData.GetRawText());

                    if (nodeObj == null) continue;

                    var classType = nodeData.TryGetProperty("class_type", out var ct) ? (ct.GetString() ?? "") : "";
                    var inputs = nodeData.TryGetProperty("inputs", out var inp) ? inp : default;
                    var meta = nodeObj.ContainsKey("_meta") ? nodeObj["_meta"] : default;

                    // Skip editor-only nodes
                    if (editorOnlyNodes.Contains(nodeId))
                    {
                        continue;
                    }

                    var newInputs = new Dictionary<string, object>();

                    // Copy and resolve inputs
                    if (inputs.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var input in inputs.EnumerateObject())
                        {
                            var inputName = input.Name;

                            if (input.Value.ValueKind == JsonValueKind.Array)
                            {
                                var arr = input.Value.EnumerateArray().ToArray();
                                if (arr.Length >= 2)
                                {
                                    var refNodeId = arr[0].GetString() ?? "";
                                    var refIndex = arr[1].GetInt32();

                                    // Resolve through editor-only nodes
                                    var resolvedNodeId = refNodeId;
                                    var resolvedIndex = refIndex;
                                    var depth = 0;
                                    const int maxDepth = 20;

                                    // First, try direct resolution
                                    while (resolutionMap.ContainsKey(resolvedNodeId) && depth < maxDepth)
                                    {
                                        (resolvedNodeId, resolvedIndex) = resolutionMap[resolvedNodeId];
                                        depth++;
                                    }

                                    // If still pointing to editor-only node, try numbered output format
                                    if (editorOnlyNodes.Contains(resolvedNodeId) && depth < maxDepth)
                                    {
                                        // Some nodes reference collector outputs as "nodeId:outputNum"
                                        for (int i = 0; i < 20; i++)
                                        {
                                            var collectorKey = $"{resolvedNodeId}:{i}";
                                            if (resolutionMap.ContainsKey(collectorKey))
                                            {
                                                (resolvedNodeId, resolvedIndex) = resolutionMap[collectorKey];
                                                break;
                                            }
                                        }
                                    }

                                    // Skip if still pointing to editor-only node
                                    if (editorOnlyNodes.Contains(resolvedNodeId))
                                    {
                                        _logger.LogWarning($"Skipping input {inputName} of node {nodeId}: references editor-only node {resolvedNodeId}");
                                        continue;
                                    }

                                    newInputs[inputName] = new object[] { resolvedNodeId, resolvedIndex };
                                }
                            }
                            else
                            {
                                // Copy primitive value
                                newInputs[inputName] = input.Value.ValueKind switch
                                {
                                    JsonValueKind.String => input.Value.GetString() ?? "",
                                    JsonValueKind.Number when input.Value.TryGetInt32(out var intVal) => intVal,
                                    JsonValueKind.Number when input.Value.TryGetInt64(out var longVal) => longVal,
                                    JsonValueKind.Number when input.Value.TryGetDouble(out var doubleVal) => doubleVal,
                                    JsonValueKind.True => true,
                                    JsonValueKind.False => false,
                                    _ => input.Value.ToString()
                                };
                            }
                        }
                    }

                    // Update specific nodes
                    if (classType == "PrimitiveNode" && nodeObj.ContainsKey("_meta") &&
                        meta.ValueKind == JsonValueKind.Object &&
                        meta.TryGetProperty("title", out var title) &&
                        title.GetString() == "PROMPT")
                    {
                        newInputs["value"] = AnalysisText;
                        _logger.LogInfo($"Updated PROMPT node {nodeId}");
                    }
                    else if (classType == "Any Switch (rgthree)" && nodeId == selectedSwitchNode && !string.IsNullOrEmpty(selectedInputName))
                    {
                        var inputNumStr = selectedInputName.Replace("any_", "");
                        if (int.TryParse(inputNumStr, out int inputNum))
                        {
                            newInputs["select"] = inputNum;
                            _logger.LogInfo($"Set {nodeId} select to {inputNum}");
                        }
                    }
                    else if (classType == "EmptySD3LatentImage")
                    {
                        var dimensions = GetDimensionsForAspectRatio(AspectRatioIndex);
                        newInputs["width"] = dimensions.Item1;
                        newInputs["height"] = dimensions.Item2;
                    }
                    else if (classType == "PrimitiveInt" && nodeObj.ContainsKey("_meta") &&
                             meta.ValueKind == JsonValueKind.Object &&
                             meta.TryGetProperty("title", out var seedTitle) &&
                             seedTitle.GetString() == "SEED")
                    {
                        var actualSeed = Seed == 0 ? (long)new Random().NextInt64(0, 999999999999999) : (long)Seed;
                        newInputs["value"] = (int)actualSeed;
                    }
                    else if (classType == "SaveImage")
                    {
                        var timestamp = DateTime.Now.ToString("yyyy_MM_dd");
                        newInputs["filename_prefix"] = $"ZImage/{timestamp}/ZI";
                    }

                    updatedPrompt[nodeId] = new Dictionary<string, object>
                    {
                        ["class_type"] = classType,
                        ["inputs"] = newInputs
                    };
                }

                _logger.LogInfo($"Built workflow with {updatedPrompt.Count} nodes (skipped {editorOnlyNodes.Count} editor-only nodes)");
                return updatedPrompt;
            }

            // Old format - return as is
            return JsonSerializer.Deserialize<Dictionary<string, object>>(workflow.GetRawText())!;
        }

        private object UpdateWorkflowForGenerationSimple(Dictionary<string, object> workflow, TextGeneratorWorkflow selectedWorkflow)
        {
            try
            {
                _logger.LogInfo($"=== UpdateWorkflowForGenerationSimple START ===");
                _logger.LogInfo($"Selected workflow: {selectedWorkflow}");
                _logger.LogInfo($"Analysis text length: {AnalysisText?.Length ?? 0}");
                _logger.LogInfo($"Analysis text preview: {(AnalysisText?.Length > 0 ? AnalysisText.Substring(0, Math.Min(100, AnalysisText.Length)) : "EMPTY")}");
                _logger.LogInfo($"LORA Enabled: {LoraEnabled}, Selected LORA: {SelectedLora}");
                _logger.LogInfo($"Total nodes in workflow: {workflow.Count}");

                // Handle different workflows with their specific node IDs
                string promptNodeId = selectedWorkflow switch
                {
                    TextGeneratorWorkflow.Qwen2512 => "71",
                    TextGeneratorWorkflow.Klien => "76",
                    _ => ""  // Empty for Zimage (will use generic search)
                };

                // Determine the input key for the prompt (text vs value)
                string promptInputKey = selectedWorkflow == TextGeneratorWorkflow.Klien ? "value" : "text";

                if (!string.IsNullOrEmpty(promptNodeId) && workflow.ContainsKey(promptNodeId))
                {
                    _logger.LogInfo($"Using specific node {promptNodeId} for {selectedWorkflow} workflow");

                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(workflow[promptNodeId]));
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null && inputs.ContainsKey(promptInputKey))
                        {
                            inputs[promptInputKey] = AnalysisText ?? "";
                            node["inputs"] = inputs;
                            workflow[promptNodeId] = node;
                            _logger.LogInfo($"✓ Updated node {promptNodeId} ({promptInputKey}) with analysis text (length: {AnalysisText?.Length ?? 0})");
                        }
                    }

                    // Also update aspect ratio for non-Zimage workflows
                    if (selectedWorkflow != TextGeneratorWorkflow.Zimage)
                    {
                        if (selectedWorkflow == TextGeneratorWorkflow.Qwen2512)
                        {
                            string emptyLatentNodeId = "51";

                            if (workflow.ContainsKey(emptyLatentNodeId))
                            {
                                var resolutions = new[]
                                {
                                    (1024, 1024), // 1:1
                                    (896, 1152),  // 3:4
                                    (768, 1344),  // 9:16
                                    (1152, 896),  // 4:3
                                    (1344, 768)   // 16:9
                                };
                                var (width, height) = resolutions[Math.Min(AspectRatioIndex, resolutions.Length - 1)];

                                var latentNode = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(workflow[emptyLatentNodeId]));
                                if (latentNode != null && latentNode.ContainsKey("inputs"))
                                {
                                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(latentNode["inputs"]));
                                    if (inputs != null)
                                    {
                                        inputs["width"] = width;
                                        inputs["height"] = height;
                                        latentNode["inputs"] = inputs;
                                        workflow[emptyLatentNodeId] = latentNode;
                                        _logger.LogInfo($"✓ Updated node {emptyLatentNodeId} with resolution: {width}x{height}");
                                    }
                                }
                            }
                        }
                        else if (selectedWorkflow == TextGeneratorWorkflow.Klien)
                        {
                            // Klien uses separate PrimitiveInt nodes for width and height
                            string widthNodeId = "75:68";
                            string heightNodeId = "75:69";

                            var resolutions = new[]
                            {
                                (1024, 1024), // 1:1
                                (896, 1152),  // 3:4
                                (768, 1344),  // 9:16
                                (1152, 896),  // 4:3
                                (1344, 768)   // 16:9
                            };
                            var (width, height) = resolutions[Math.Min(AspectRatioIndex, resolutions.Length - 1)];

                            // Update width node
                            if (workflow.ContainsKey(widthNodeId))
                            {
                                var widthNode = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(workflow[widthNodeId]));
                                if (widthNode != null && widthNode.ContainsKey("inputs"))
                                {
                                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(widthNode["inputs"]));
                                    if (inputs != null && inputs.ContainsKey("value"))
                                    {
                                        inputs["value"] = width;
                                        widthNode["inputs"] = inputs;
                                        workflow[widthNodeId] = widthNode;
                                        _logger.LogInfo($"✓ Updated node {widthNodeId} with width: {width}");
                                    }
                                }
                            }

                            // Update height node
                            if (workflow.ContainsKey(heightNodeId))
                            {
                                var heightNode = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(workflow[heightNodeId]));
                                if (heightNode != null && heightNode.ContainsKey("inputs"))
                                {
                                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(heightNode["inputs"]));
                                    if (inputs != null && inputs.ContainsKey("value"))
                                    {
                                        inputs["value"] = height;
                                        heightNode["inputs"] = inputs;
                                        workflow[heightNodeId] = heightNode;
                                        _logger.LogInfo($"✓ Updated node {heightNodeId} with height: {height}");
                                    }
                                }
                            }
                        }
                    }

                    return workflow;
                }

                // For Zimage workflow, use the generic search approach

                int updatedNodes = 0;
                var modifiedWorkflow = new Dictionary<string, object>();

                // Process each node in the workflow
                foreach (var kvp in workflow)
                {
                    var nodeValue = kvp.Value;

                    // Convert to JsonElement for easier inspection
                    JsonElement nodeElement;
                    if (nodeValue is JsonElement je)
                    {
                        nodeElement = je;
                    }
                    else
                    {
                        var json = JsonSerializer.Serialize(nodeValue);
                        nodeElement = JsonSerializer.Deserialize<JsonElement>(json);
                    }

                    if (nodeElement.ValueKind == JsonValueKind.Undefined)
                    {
                        modifiedWorkflow[kvp.Key] = nodeValue;
                        continue;
                    }

                    // Get class_type
                    string classType = "";
                    if (nodeElement.TryGetProperty("class_type", out var classTypeProp))
                    {
                        classType = classTypeProp.GetString() ?? "";
                    }

                    // Get _meta.title
                    string title = "";
                    if (nodeElement.TryGetProperty("_meta", out var metaProp))
                    {
                        if (metaProp.ValueKind == JsonValueKind.Object && metaProp.TryGetProperty("title", out var titleProp))
                        {
                            title = titleProp.GetString() ?? "";
                        }
                    }

                    // Check if this is a node we need to update with analysis text
                    bool shouldUpdate = false;
                    string textPropertyName = "";  // Will be "text" for CLIPTextEncode, "string" for StringTrim/Primitive

                    if (nodeElement.TryGetProperty("inputs", out var inputsProp))
                    {
                        if (inputsProp.ValueKind == JsonValueKind.Object)
                        {
                            // Case 1: CLIPTextEncode with "text" property
                            if (classType == "CLIPTextEncode" && inputsProp.TryGetProperty("text", out var textProp))
                            {
                                bool isStringText = textProp.ValueKind == JsonValueKind.String;
                                bool isPositivePrompt = !string.IsNullOrEmpty(title) && (title.Contains("Positive") || title.Contains("positive"));

                                _logger.LogInfo($"Node {kvp.Key}: class_type={classType}, title=\"{title}\", isStringText={isStringText}, isPositivePrompt={isPositivePrompt}");

                                shouldUpdate = isStringText && isPositivePrompt;
                                textPropertyName = "text";
                            }
                            // Case 2: StringTrim or Primitive with "string" property (these contain the actual prompt text)
                            else if ((classType == "StringTrim" || classType == "PrimitiveNode") && inputsProp.TryGetProperty("string", out var stringProp))
                            {
                                bool isStringText = stringProp.ValueKind == JsonValueKind.String;

                                _logger.LogInfo($"Node {kvp.Key}: class_type={classType}, title=\"{title}\", isStringText={isStringText}");

                                shouldUpdate = isStringText;
                                textPropertyName = "string";
                            }
                        }
                    }

                    if (shouldUpdate)
                    {
                        _logger.LogInfo($"→ Attempting to update node {kvp.Key} (property: {textPropertyName})...");
                        // Deserialize the entire node to Dictionary so we can modify it
                        var nodeDict = JsonSerializer.Deserialize<Dictionary<string, object>>(nodeElement.GetRawText());
                        _logger.LogInfo($"  nodeDict deserialized: {nodeDict != null}");
                        if (nodeDict != null)
                        {
                            _logger.LogInfo($"  nodeDict keys: {string.Join(", ", nodeDict.Keys)}");
                            _logger.LogInfo($"  nodeDict.TryGetValue('inputs'): {nodeDict.TryGetValue("inputs", out var inputsObj)}");
                            if (inputsObj != null)
                            {
                                _logger.LogInfo($"  inputsObj type: {inputsObj.GetType().Name}");
                                _logger.LogInfo($"  inputsObj is Dictionary<string, object>: {inputsObj is Dictionary<string, object>}");
                            }
                        }

                        // Handle both Dictionary<string, object> and JsonElement cases
                        if (nodeDict != null && nodeDict.TryGetValue("inputs", out var inputsObj2))
                        {
                            Dictionary<string, object>? inputs = null;

                            // Case 1: inputs is already a Dictionary<string, object>
                            if (inputsObj2 is Dictionary<string, object> dictInputs)
                            {
                                inputs = dictInputs;
                            }
                            // Case 2: inputs is a JsonElement (common with System.Text.Json)
                            else if (inputsObj2 is JsonElement elementInputs && elementInputs.ValueKind == JsonValueKind.Object)
                            {
                                inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(elementInputs.GetRawText());
                                _logger.LogInfo($"  Deserialized JsonElement inputs to Dictionary: {inputs != null}");
                            }

                            if (inputs != null)
                            {
                                // Use the appropriate property name based on node type
                                inputs[textPropertyName] = AnalysisText ?? "";
                                // Update the inputs in nodeDict (handle both cases)
                                nodeDict["inputs"] = inputs;
                                modifiedWorkflow[kvp.Key] = nodeDict;
                                updatedNodes++;
                                _logger.LogInfo($"✓ Updated {classType} node {kvp.Key} ({textPropertyName}) with analysis text (length: {AnalysisText?.Length ?? 0})");
                            }
                            else
                            {
                                _logger.LogInfo($"✗ Failed to deserialize inputs for node {kvp.Key}");
                                modifiedWorkflow[kvp.Key] = nodeValue;
                            }
                        }
                        else
                        {
                            _logger.LogInfo($"✗ Failed to update node {kvp.Key} - conditions not met");
                            modifiedWorkflow[kvp.Key] = nodeValue;
                        }
                    }
                    else if (LoraEnabled && classType == "Power Lora Loader (rgthree)" && nodeElement.TryGetProperty("inputs", out var loraInputsProp))
                    {
                        // Update LORA node with selected LORA
                        _logger.LogInfo($"→ Found Power Lora Loader node {kvp.Key}, updating with selected LORA: {SelectedLora}");

                        var nodeDict = JsonSerializer.Deserialize<Dictionary<string, object>>(nodeElement.GetRawText());
                        if (nodeDict != null && nodeDict.TryGetValue("inputs", out var loraInputsObj))
                        {
                            Dictionary<string, object>? loraInputs = null;

                            // Handle both Dictionary and JsonElement cases
                            if (loraInputsObj is Dictionary<string, object> dictLoraInputs)
                            {
                                loraInputs = dictLoraInputs;
                            }
                            else if (loraInputsObj is JsonElement loraElementInputs && loraElementInputs.ValueKind == JsonValueKind.Object)
                            {
                                loraInputs = JsonSerializer.Deserialize<Dictionary<string, object>>(loraElementInputs.GetRawText());
                            }

                            if (loraInputs != null)
                            {
                                // Update lora_1 object
                                if (loraInputs.TryGetValue("lora_1", out var lora1Obj))
                                {
                                    Dictionary<string, object>? lora1Dict = null;

                                    if (lora1Obj is Dictionary<string, object> dictLora1)
                                    {
                                        lora1Dict = dictLora1;
                                    }
                                    else if (lora1Obj is JsonElement lora1Element && lora1Element.ValueKind == JsonValueKind.Object)
                                    {
                                        lora1Dict = JsonSerializer.Deserialize<Dictionary<string, object>>(lora1Element.GetRawText());
                                    }

                                    if (lora1Dict != null)
                                    {
                                        // Update lora filename
                                        lora1Dict["lora"] = $"zimage\\{SelectedLora}.safetensors";
                                        lora1Dict["on"] = true;

                                        loraInputs["lora_1"] = lora1Dict;
                                        nodeDict["inputs"] = loraInputs;
                                        modifiedWorkflow[kvp.Key] = nodeDict;
                                        updatedNodes++;
                                        _logger.LogInfo($"✓ Updated LORA node {kvp.Key} with {SelectedLora}.safetensors");
                                    }
                                }
                            }
                        }

                        if (!modifiedWorkflow.ContainsKey(kvp.Key))
                        {
                            modifiedWorkflow[kvp.Key] = nodeValue;
                        }
                    }
                    else
                    {
                        // Keep original node
                        modifiedWorkflow[kvp.Key] = nodeValue;
                    }
                }

                _logger.LogInfo($"=== Workflow update complete. Updated {updatedNodes} nodes ===");
                return modifiedWorkflow;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error updating workflow: {ex.Message}\n{ex.StackTrace}");
                return workflow; // Return original workflow if update fails
            }
        }

        private void AddInputReferencesToDict(System.Text.Json.Nodes.JsonObject nodeObj, Dictionary<string, object> inputs)
        {
            if (nodeObj["inputs"] is System.Text.Json.Nodes.JsonArray nodeInputs)
            {
                foreach (var input in nodeInputs)
                {
                    if (input is System.Text.Json.Nodes.JsonObject inputObj)
                    {
                        var inputName = inputObj["name"]?.GetValue<string>();
                        if (inputName != null && inputObj["link"] != null)
                        {
                            var linkNode = inputObj["link"];
                            if (linkNode != null)
                            {
                                var linkValue = linkNode.AsValue();
                                var linkKind = linkValue.GetValueKind();
                                if (linkKind == System.Text.Json.JsonValueKind.Number)
                                {
                                    var linkNum = linkValue.GetValue<uint>();
                                    // ComfyUI API format: [nodeId, outputIndex]
                                    inputs[inputName] = new object[] { linkNum.ToString(), 0 };
                                }
                            }
                        }
                    }
                }
            }
        }

        private object GetWidgetValue(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node == null) return "";

            var value = node.AsValue();
            var kind = value.GetValueKind();

            if (kind == System.Text.Json.JsonValueKind.String)
            {
                return node.ToString();
            }
            else if (kind == System.Text.Json.JsonValueKind.Number)
            {
                // Try to get as int first, then long, then double
                var element = JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
                if (element.ValueKind == JsonValueKind.Number)
                {
                    if (element.TryGetInt32(out var intVal))
                        return intVal;
                    if (element.TryGetInt64(out var longVal))
                        return longVal;
                    if (element.TryGetDouble(out var doubleVal))
                        return doubleVal;
                }
                return 0;
            }
            else if (kind == System.Text.Json.JsonValueKind.True)
            {
                return true;
            }
            else if (kind == System.Text.Json.JsonValueKind.False)
            {
                return false;
            }
            else if (kind == System.Text.Json.JsonValueKind.Null)
            {
                return null!;
            }

            return node.ToString();
        }

        private (int, int) GetDimensionsForAspectRatio(int aspectRatioIndex)
        {
            // Z-Image recommended dimensions based on aspect ratios
            var dimensions = new[]
            {
                (1024, 1024),  // 0: 1:1 square
                (896, 1152),   // 1: 3:4 portrait
                (768, 1344),   // 2: 9:16 portrait
                (1152, 896),   // 3: 4:3 landscape
                (1344, 768)    // 4: 16:9 landscape
            };

            return dimensions[Math.Min(aspectRatioIndex, dimensions.Length - 1)];
        }

        private async Task<List<byte[]>> GetOutputImageFromPromptHistory(string promptId, TextGeneratorWorkflow workflow)
        {
            var images = new List<byte[]>();

            try
            {
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                _logger.LogInfo($"Querying history for prompt ID: {promptId}");

                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var historyUrl = $"{baseUrl}/history/{promptId}";
                var response = await httpClient.GetAsync(historyUrl);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"History request failed with status: {response.StatusCode}");
                    return images;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogInfo($"History response received ({responseContent.Length} chars)");

                var historyData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseContent);

                if (historyData == null || !historyData.ContainsKey(promptId))
                {
                    _logger.LogWarning($"Prompt ID {promptId} not found in history response");
                    return images;
                }

                var promptData = historyData[promptId];
                _logger.LogInfo($"Found prompt data for ID: {promptId}");

                if (!promptData.TryGetProperty("outputs", out var outputs))
                {
                    _logger.LogWarning("Prompt data found but no 'outputs' property");
                    return images;
                }

                _logger.LogInfo("Found 'outputs' property in prompt data");

                // Search through all output nodes for images
                foreach (var outputNode in outputs.EnumerateObject())
                {
                    if (outputNode.Value.TryGetProperty("images", out var imagesArray))
                    {
                        _logger.LogInfo($"Found 'images' array in node {outputNode.Name} with {imagesArray.GetArrayLength()} items");

                        foreach (var imageInfo in imagesArray.EnumerateArray())
                        {
                            if (imageInfo.TryGetProperty("filename", out var filenameProp))
                            {
                                var filename = filenameProp.GetString() ?? string.Empty;
                                var subfolder = "";

                                if (imageInfo.TryGetProperty("subfolder", out var subfolderProp))
                                {
                                    subfolder = subfolderProp.GetString() ?? "";
                                }

                                _logger.LogInfo($"Found image from history: filename='{filename}', subfolder='{subfolder}'");

                                if (!string.IsNullOrEmpty(filename))
                                {
                                    var uri = new Uri(baseUrl);
                                    bool isRemoteComfyUI = IsComfyUIRemote(uri.Host);

                                    if (isRemoteComfyUI)
                                    {
                                        // Download via HTTP
                                        var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filename, subfolder);
                                        if (imageData != null)
                                        {
                                            images.Add(imageData);
                                            _logger.LogInfo($"Successfully downloaded image from prompt history ({imageData.Length} bytes)");
                                            return images; // Return immediately with the specific image
                                        }
                                    }
                                    else
                                    {
                                        // Read from local folder
                                        var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                                        if (!string.IsNullOrEmpty(comfyUIOutputDir))
                                        {
                                            var fullPath = string.IsNullOrEmpty(subfolder)
                                                ? Path.Combine(comfyUIOutputDir, filename)
                                                : Path.Combine(comfyUIOutputDir, subfolder, filename);

                                            _logger.LogInfo($"Looking for local file: {fullPath}");

                                            if (File.Exists(fullPath))
                                            {
                                                var imageData = await File.ReadAllBytesAsync(fullPath);
                                                images.Add(imageData);
                                                _logger.LogInfo($"Successfully loaded image from local path ({imageData.Length} bytes)");
                                                return images;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                _logger.LogWarning("No images found in prompt history outputs");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting output from prompt history: {ex.Message}");
            }

            return images;
        }

        private async Task<List<byte[]>> GetMostRecentImageFromOutput(TextGeneratorWorkflow workflow)
        {
            var images = new List<byte[]>();

            try
            {
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;

                bool isRemoteComfyUI = IsComfyUIRemote(actualServer);
                _logger.LogInfo($"Getting most recent image from {(isRemoteComfyUI ? "remote" : "local")} ComfyUI");
                _logger.LogInfo($"Selected workflow: {workflow}");

                // Determine file prefix based on workflow
                var filePrefix = workflow switch
                {
                    TextGeneratorWorkflow.Qwen2512 => "qwen2512_",
                    TextGeneratorWorkflow.Klien => "Flux2-Klein_",
                    _ => ""  // Empty for Zimage (will use ZI/z-image pattern)
                };

                _logger.LogInfo($"Using file prefix pattern: '{filePrefix}'");

                if (isRemoteComfyUI)
                {
                    _logger.LogInfo("Fetching file list from remote ComfyUI...");
                    var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
                    _logger.LogInfo($"Found {outputFiles.Count} total output files");

                    // Filter files based on workflow
                    IEnumerable<string> matchingFiles;
                    if (workflow == TextGeneratorWorkflow.Zimage)
                    {
                        // Get ZI files (new format) or z-image files (old format)
                        matchingFiles = outputFiles.Where(f => (f.Contains("ZI") || f.Contains("z-image")) && f.EndsWith(".png"));
                    }
                    else
                    {
                        // Get files with the specific prefix
                        matchingFiles = outputFiles.Where(f => f.Contains(filePrefix) && f.EndsWith(".png"));
                    }

                    var filteredFiles = matchingFiles.ToList();

                    if (filteredFiles.Any())
                    {
                        // Get the last one (they're typically already sorted by name which includes number)
                        var newestFile = filteredFiles.Last();
                        _logger.LogInfo($"✓ Selected newest file: {newestFile}");

                        // Parse the path to extract subfolder and filename
                        var lastSlash = newestFile.LastIndexOf('/');
                        var subfolder = lastSlash > 0 ? newestFile.Substring(0, lastSlash) : "";
                        var filename = lastSlash > 0 ? newestFile.Substring(lastSlash + 1) : newestFile;
                        _logger.LogInfo($"  Subfolder: '{subfolder}', Filename: '{filename}'");

                        var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filename, subfolder);
                        if (imageData != null)
                        {
                            images.Add(imageData);
                            _logger.LogInfo($"✓ Downloaded image ({imageData.Length} bytes)");
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"No matching files found in remote output for prefix: {filePrefix}");
                    }
                }
                else
                {
                    // Local ComfyUI - get newest file by creation time
                    var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                    _logger.LogInfo($"Checking local output folder: {comfyUIOutputDir}");

                    if (string.IsNullOrEmpty(comfyUIOutputDir) || !Directory.Exists(comfyUIOutputDir))
                    {
                        _logger.LogError($"Output folder not found: {comfyUIOutputDir}");
                        return images;
                    }

                    // Get files based on workflow
                    string[] allFiles;
                    if (workflow == TextGeneratorWorkflow.Zimage)
                    {
                        // Get all ZI*.png files recursively (new format with ZImage subfolder)
                        var allZiFiles = Directory.GetFiles(comfyUIOutputDir, "ZI*.png", SearchOption.AllDirectories);
                        // Also get old z-image*.png files for backward compatibility
                        var oldZiFiles = Directory.GetFiles(comfyUIOutputDir, "z-image*.png", SearchOption.TopDirectoryOnly);
                        allFiles = allZiFiles.Concat(oldZiFiles).ToArray();
                    }
                    else
                    {
                        // Get files with the specific prefix
                        allFiles = Directory.GetFiles(comfyUIOutputDir, $"{filePrefix}*.png", SearchOption.AllDirectories);
                    }

                    _logger.LogInfo($"Found {allFiles.Length} total matching files");

                    if (allFiles.Length == 0)
                    {
                        // Try without the pattern to see what's in the folder
                        var anyPng = Directory.GetFiles(comfyUIOutputDir, "*.png", SearchOption.AllDirectories);
                        _logger.LogWarning($"No ZI files found, but found {anyPng.Length} total PNG files");
                        if (anyPng.Length > 0)
                        {
                            _logger.LogInfo($"Sample files: {string.Join(", ", anyPng.Take(5).Select(Path.GetFileName))}");
                        }
                    }

                    var imageFiles = allFiles
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTime)
                        .ToList();

                    if (imageFiles.Any())
                    {
                        var newestFile = imageFiles.First();
                        var fileAge = DateTime.Now - newestFile.LastWriteTime;

                        _logger.LogInfo($"✓ Newest ZI file found:");
                        _logger.LogInfo($"  Name: {newestFile.Name}");
                        _logger.LogInfo($"  Full Path: {newestFile.FullName}");
                        _logger.LogInfo($"  Last Modified: {newestFile.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                        _logger.LogInfo($"  Age: {fileAge.TotalSeconds:F1} seconds");
                        _logger.LogInfo($"  Size: {newestFile.Length} bytes");

                        // Take the newest file regardless of age (it should be the right one)
                        _logger.LogInfo($"✓✓ Using newest file: {newestFile.Name}");
                        var imageData = await File.ReadAllBytesAsync(newestFile.FullName);
                        images.Add(imageData);
                        _logger.LogInfo($"✓✓✓ Successfully loaded {imageData.Length} bytes");
                    }
                    else
                    {
                        _logger.LogError($"ERROR: No ZI files found in {comfyUIOutputDir}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting most recent image: {ex.Message}");
            }

            return images;
        }

        private async Task<string> TryGetImageFilenameFromHistory(string promptId)
        {
            // Retry a few times in case history isn't updated yet
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        _logger.LogInfo($"History query attempt {attempt + 1}/3...");
                        await Task.Delay(2000); // Wait 2 seconds between retries
                    }

                    var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                    var historyUrl = $"{baseUrl}/history/{promptId}";

                    using var httpClient = new System.Net.Http.HttpClient();
                    httpClient.Timeout = TimeSpan.FromSeconds(30);

                    var response = await httpClient.GetAsync(historyUrl);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = await response.Content.ReadAsStringAsync();
                        _logger.LogInfo($"History response received ({responseContent.Length} chars)");

                        var historyData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseContent);

                        if (historyData != null && historyData.ContainsKey(promptId))
                        {
                            var promptData = historyData[promptId];
                            _logger.LogInfo($"Found prompt data for ID: {promptId}");

                            if (promptData.TryGetProperty("outputs", out var outputs))
                            {
                                _logger.LogInfo("✓ Found 'outputs' property in prompt data");

                                // Node 9 is the SaveImage node
                                if (outputs.TryGetProperty("9", out var node9))
                                {
                                    _logger.LogInfo("✓ Found node 9 (SaveImage) in outputs");

                                    if (node9.TryGetProperty("images", out var images))
                                    {
                                        _logger.LogInfo($"✓ Found 'images' array with {images.GetArrayLength()} items");

                                        if (images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
                                        {
                                            var firstImage = images[0];
                                            if (firstImage.TryGetProperty("filename", out var filename))
                                            {
                                                var filenameStr = filename.GetString() ?? string.Empty;
                                                _logger.LogInfo($"✓✓✓ SUCCESS! Image filename from history: {filenameStr}");
                                                return filenameStr;
                                            }
                                            else
                                            {
                                                _logger.LogWarning("Image object found but no 'filename' property");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogWarning("Node 9 found but no 'images' property");
                                    }
                                }
                                else
                                {
                                    // Log all available node IDs
                                    var nodeIds = outputs.EnumerateObject().Select(p => p.Name).ToList();
                                    _logger.LogWarning($"Node 9 not found in outputs. Available nodes: {string.Join(", ", nodeIds)}");
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Prompt data found but no 'outputs' property");
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"Prompt ID {promptId} not found in history response (attempt {attempt + 1})");
                        }
                    }
                    else
                    {
                        _logger.LogError($"History request failed with status: {response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error getting filename from history (attempt {attempt + 1}): {ex.Message}");
                }
            }

            _logger.LogError("Failed to get image filename from history after all retries");
            return string.Empty;
        }

        private async Task<List<byte[]>> GetOutputImagesFromComfyUI(string promptId, string specificFilename = "")
        {
            var images = new List<byte[]>();

            try
            {
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;

                bool isRemoteComfyUI = IsComfyUIRemote(actualServer);
                _logger.LogInfo($"ComfyUI server: {actualServer}, Is remote: {isRemoteComfyUI}");

                if (isRemoteComfyUI)
                {
                    _logger.LogInfo("Detected remote ComfyUI, downloading via HTTP...");

                    // If we have a specific filename from history, try that first
                    if (!string.IsNullOrEmpty(specificFilename))
                    {
                        _logger.LogInfo($"Trying to download specific file from history: {specificFilename}");
                        // Parse the path to extract subfolder and filename
                        var lastSlash = specificFilename.LastIndexOf('/');
                        var subfolderSpec = lastSlash > 0 ? specificFilename.Substring(0, lastSlash) : "";
                        var filenameSpec = lastSlash > 0 ? specificFilename.Substring(lastSlash + 1) : specificFilename;
                        var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filenameSpec, subfolderSpec);
                        if (imageData != null)
                        {
                            images.Add(imageData);
                            _logger.LogInfo($"Successfully downloaded specific image ({imageData.Length} bytes)");
                            return images;
                        }
                        else
                        {
                            _logger.LogWarning($"Failed to download specific file: {specificFilename}");
                        }
                    }

                    var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
                    _logger.LogInfo($"Found {outputFiles.Count} total output files");

                    // Look for z-image files (path may include subfolder like ZImage/2026_01_15/ZI_00001.png)
                    var zImageFiles = outputFiles.Where(f => (f.Contains("z-image") || f.Contains("ZI")) && f.EndsWith(".png")).ToList();
                    _logger.LogInfo($"Found {zImageFiles.Count} z-image files: {string.Join(", ", zImageFiles.Take(5))}");

                    if (zImageFiles.Any())
                    {
                        var fullPath = zImageFiles.Last();
                        _logger.LogInfo($"Downloading most recent: {fullPath}");
                        // Parse the path to extract subfolder and filename
                        var lastSlash = fullPath.LastIndexOf('/');
                        var subfolder = lastSlash > 0 ? fullPath.Substring(0, lastSlash) : "";
                        var filename = lastSlash > 0 ? fullPath.Substring(lastSlash + 1) : fullPath;
                        _logger.LogInfo($"  Subfolder: '{subfolder}', Filename: '{filename}'");
                        var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filename, subfolder);
                        if (imageData != null)
                        {
                            images.Add(imageData);
                            _logger.LogInfo($"Successfully downloaded image ({imageData.Length} bytes)");
                        }
                        else
                        {
                            _logger.LogWarning($"Download returned null for {fullPath}");
                        }
                    }
                    else
                    {
                        // If no z-image files found, try to get ANY recent .png file
                        var pngFiles = outputFiles.Where(f => f.EndsWith(".png")).ToList();
                        _logger.LogInfo($"No z-image files found. Found {pngFiles.Count} total PNG files");

                        if (pngFiles.Any())
                        {
                            var fullPath = pngFiles.Last();
                            _logger.LogInfo($"Trying to download most recent PNG: {fullPath}");
                            // Parse the path to extract subfolder and filename
                            var lastSlash = fullPath.LastIndexOf('/');
                            var subfolder = lastSlash > 0 ? fullPath.Substring(0, lastSlash) : "";
                            var filename = lastSlash > 0 ? fullPath.Substring(lastSlash + 1) : fullPath;
                            var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filename, subfolder);
                            if (imageData != null)
                            {
                                images.Add(imageData);
                                _logger.LogInfo($"Successfully downloaded image ({imageData.Length} bytes)");
                            }
                        }
                    }
                }
                else
                {
                    // Local ComfyUI
                    var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                    _logger.LogInfo($"Checking local output folder: {comfyUIOutputDir}");

                    if (string.IsNullOrEmpty(comfyUIOutputDir) || !Directory.Exists(comfyUIOutputDir))
                    {
                        _logger.LogError($"ComfyUI output folder not found or not configured: {comfyUIOutputDir}");
                        return images;
                    }

                    // If we have a specific filename from history, try that first
                    if (!string.IsNullOrEmpty(specificFilename))
                    {
                        var specificPath = Path.Combine(comfyUIOutputDir, specificFilename);
                        _logger.LogInfo($"Looking for specific file: {specificPath}");

                        if (File.Exists(specificPath))
                        {
                            _logger.LogInfo($"Found specific file from history!");
                            var imageData = await File.ReadAllBytesAsync(specificPath);
                            images.Add(imageData);
                            return images;
                        }
                        else
                        {
                            _logger.LogWarning($"Specific file not found at: {specificPath}");
                        }
                    }

                    // Try to find z-image files first
                    var imageFiles = Directory.GetFiles(comfyUIOutputDir, "z-image*.png")
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .ToList();

                    _logger.LogInfo($"Found {imageFiles.Count} z-image files in output folder");

                    if (!imageFiles.Any())
                    {
                        // If no z-image files, try ANY recent .png file
                        imageFiles = Directory.GetFiles(comfyUIOutputDir, "*.png")
                            .OrderByDescending(f => File.GetLastWriteTime(f))
                            .ToList();
                        _logger.LogInfo($"No z-image files found. Found {imageFiles.Count} total PNG files");
                    }

                    if (imageFiles.Any())
                    {
                        var latestFile = imageFiles.First();
                        var fileAge = DateTime.Now - File.GetLastWriteTime(latestFile);

                        _logger.LogInfo($"Latest file: {Path.GetFileName(latestFile)}, Age: {fileAge.TotalSeconds:F1} seconds");

                        if (fileAge.TotalSeconds < 120) // Increased from 60 to 120 seconds
                        {
                            _logger.LogInfo($"Using output image: {Path.GetFileName(latestFile)}");
                            var imageData = await File.ReadAllBytesAsync(latestFile);
                            images.Add(imageData);
                        }
                        else
                        {
                            _logger.LogWarning($"Latest file is too old ({fileAge.TotalSeconds:F0} seconds)");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("No PNG files found in output folder");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error retrieving output images: {ex.Message}\n{ex.StackTrace}");
            }

            return images;
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

                _logger.LogInfo("Result image preview loaded");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading result preview: {ex.Message}");
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
                _logger.LogError($"Error opening result folder: {ex.Message}");
            }
        }

        private bool IsComfyUIRemote(string serverAddress)
        {
            if (serverAddress.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                serverAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                serverAddress.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !string.IsNullOrEmpty(serverAddress);
        }

        private async Task TestLMStudioConnectionAsync()
        {
            try
            {
                StatusBarMessage = "Testing LM Studio connection...";
                _logger.LogInfo("Testing LM Studio connection...");

                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://localhost:1234";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);

                var isRunning = await _lmStudioService.IsRunningAsync();
                if (isRunning)
                {
                    StatusBarMessage = "LM Studio connection successful";
                    _logger.LogInfo("LM Studio connection test successful");
                    System.Windows.MessageBox.Show("Successfully connected to LM Studio!", "Connection Test",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
                else
                {
                    StatusBarMessage = "LM Studio connection failed";
                    _logger.LogError("LM Studio connection test failed - service not responding");
                    System.Windows.MessageBox.Show("Failed to connect to LM Studio. Please ensure LM Studio is running and accessible.", "Connection Test Failed",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                StatusBarMessage = "LM Studio connection error";
                _logger.LogError($"Error testing LM Studio connection: {ex.Message}");
                System.Windows.MessageBox.Show($"Error connecting to LM Studio: {ex.Message}", "Connection Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private async Task RefreshModelsAsync()
        {
            try
            {
                StatusBarMessage = "Refreshing LM Studio models...";
                _logger.LogInfo("Refreshing LM Studio models...");

                var models = await _lmStudioService.GetAvailableModelsAsync();
                AvailableModels = models;

                StatusBarMessage = $"Found {models.Count} models";
                _logger.LogInfo($"Model refresh completed: {models.Count} models found");

                if (!models.Any())
                {
                    System.Windows.MessageBox.Show("No models found in LM Studio. Please load a model in LM Studio.", "No Models Found",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                StatusBarMessage = "Error refreshing models";
                _logger.LogError($"Error refreshing models: {ex.Message}");
                System.Windows.MessageBox.Show($"Error refreshing models: {ex.Message}", "Refresh Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
    }
}
