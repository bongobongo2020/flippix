using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    public partial class StoryImageGeneratorViewModel : StoryImageGeneratorBaseViewModel
    {
        // Upscale settings
        private double _denoise2 = 0.85;
        private bool _upscaleEnabled = true;
        private ObservableCollection<string> _upscaleMethods = new();
        private string _upscaleMethod = "Photo";

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
        private string _selectedOrientation = "Landscape (1408x944)";

        // Analysis
        private readonly LMStudioService _lmStudioService;
        private bool _isAnalyzingImage = false;
        private string _analysisStatus = string.Empty;

        public StoryImageGeneratorViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService,
            LoraManager loraManager,
            ComfyUIImageRetriever imageRetriever,
            LMStudioService lmStudioService)
            : base(comfyUIService, logger, settingsService, workflowCoordinator, fileDialogService, loraManager, imageRetriever)
        {
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));

            // Re-evaluate AnalyzeImageWithQwenVLCommand when InputImagePath changes
            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(InputImagePath))
                {
                    (AnalyzeImageWithQwenVLCommand as CommunityToolkit.Mvvm.Input.RelayCommand)?.NotifyCanExecuteChanged();
                }
            };
        }

        // --- Abstract member implementations ---

        protected override string VariantDisplayName => "Story Image Generator Z";
        protected override string WorkflowTypeName => "StoryImageZ";
        protected override string QueuePersistenceFileName => "story_image_queue.json";
        protected override string OutputFolderName => "story-generator";
        protected override int DefaultSteps => 8;
        protected override double DefaultCfg => 1.5;
        protected override double DefaultDenoise => 0.98;

        // Z variant does NOT require input image (text-to-image)
        protected override bool RequiresInputImage => false;
        protected override bool UseSessionOutputFolder => true;
        protected override bool UseComfyUICrashDetection => true;
        protected override bool ShowCompletionMessageBox => false;
        protected override bool AutoStartProcessing => true;

        // --- Variant-specific initialization ---

        protected override void InitializeVariant()
        {
            RefreshLorasCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(RefreshLoras);
            AnalyzeImageWithQwenVLCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(
                async () => await AnalyzeImageWithQwenVLAsync(),
                () => !string.IsNullOrEmpty(InputImagePath) && File.Exists(InputImagePath) && !IsAnalyzingImage);

            LoadAvailableLoras();
            LoadWorkflowsAndStyles();
            InitializeAvailableStyles();
            InitializeOrientations();
            InitializeUpscaleMethods();
        }

        // --- Overrides for folder saving ---

        protected override string GetPromptJsonInitialDirectory()
        {
            var folder = _settingsService.Settings?.StoryImageGeneratorPromptJsonFolder;
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder)
                ? folder
                : base.GetPromptJsonInitialDirectory();
        }

        protected override void SavePromptJsonFolder(string folderPath)
        {
            if (_settingsService.Settings != null)
            {
                _settingsService.Settings.StoryImageGeneratorPromptJsonFolder = folderPath;
                _settingsService.SaveSettings(_settingsService.Settings);
            }
        }

        // --- Override CreateQueueItem to snapshot Z-specific settings ---

        protected override StoryPromptItem CreateQueueItem(int index, string prompt, string inputImagePath)
        {
            return new StoryPromptItem
            {
                Index = index,
                Prompt = prompt,
                InputImagePath = inputImagePath,
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
            };
        }

        // --- Analysis properties ---

        public ICommand AnalyzeImageWithQwenVLCommand { get; private set; } = null!;

        public bool IsAnalyzingImage
        {
            get => _isAnalyzingImage;
            set
            {
                if (SetProperty(ref _isAnalyzingImage, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string AnalysisStatus
        {
            get => _analysisStatus;
            set => SetProperty(ref _analysisStatus, value);
        }

        // --- Z-specific properties ---

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

        public StyleInfo? SelectedWorkflowStyle => _allStyles.Count > 0
            ? _allStyles[Math.Min(SelectedStyleIndex, _allStyles.Count - 1)]
            : null;

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

        // Commands
        public ICommand RefreshLorasCommand { get; private set; } = null!;

        // --- Initialization methods ---

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
                _allStyles.Clear();

                var workflowDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "ZStyles");

                if (!Directory.Exists(workflowDir))
                {
                    AddLog($"ZStyles workflow directory not found at {workflowDir}");
                    return;
                }

                var workflowFiles = Directory.GetFiles(workflowDir, "*.json");
                AddLog($"Found {workflowFiles.Length} workflow files in {workflowDir}");

                foreach (var workflowFile in workflowFiles)
                {
                    try
                    {
                        var fileName = Path.GetFileNameWithoutExtension(workflowFile);
                        var styleName = fileName.StartsWith("Z") ? fileName.Substring(1) : fileName;

                        _allStyles.Add(new StyleInfo
                        {
                            Name = styleName,
                            PromptTemplate = "",
                            WorkflowFile = workflowFile,
                            NodeId = ""
                        });

                        AddLog($"Loaded style: {styleName} from {Path.GetFileName(workflowFile)}");
                    }
                    catch (Exception ex)
                    {
                        AddLog($"Error loading workflow file {workflowFile}: {ex.Message}");
                    }
                }

                _allStyles = _allStyles.OrderBy(s => s.Name).ToList();

                AddLog($"Loaded {_allStyles.Count} total styles from ZStyles workflows");
                OnPropertyChanged(nameof(StyleNames));
            }
            catch (Exception ex)
            {
                AddLog($"Error loading workflows: {ex.Message}");
            }
        }

        // --- LoRA methods ---

        private void RefreshLoras()
        {
            LoadAvailableLoras();
            AddLog("Refreshed LoRA list");
        }

        private void LoadAvailableLoras()
        {
            try
            {
                // Check RemoteLoraFolderPath first (same priority as ImageGeneratorViewModel)
                var overridePath = _settingsService.Settings?.RemoteLoraFolderPath;
                if (!string.IsNullOrEmpty(overridePath) && Directory.Exists(overridePath))
                {
                    AddLog($"Using configured LoRA folder: {overridePath}");
                    LoadLorasFromDirectory(overridePath, "configured LoRA folder");
                    return;
                }

                var loraBasePath = GetLoraModelPath();
                if (!string.IsNullOrEmpty(loraBasePath))
                {
                    AddLog($"LoRA base path: {loraBasePath}");
                    var zimageLoraPath = Path.Combine(loraBasePath, "zimage");
                    if (Directory.Exists(zimageLoraPath))
                    {
                        LoadLorasFromDirectory(zimageLoraPath, "ComfyUI zimage LoRA directory");
                        return;
                    }
                    else
                    {
                        AddLog($"No 'zimage' subfolder found, searching base path directly");
                        LoadLorasFromDirectory(loraBasePath, "ComfyUI LoRA directory");
                        return;
                    }
                }

                AddLog("WARNING: LoRA base path not found. Check ComfyUI folder path or LoRA folder in settings.");
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

            var loraFiles = Directory.GetFiles(loraPath, "*.safetensors", SearchOption.AllDirectories)
                .Select(f => Path.ChangeExtension(Path.GetRelativePath(loraPath, f), null).Replace('/', '\\'))
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

        // --- Qwen VL Analysis ---

        private async Task AnalyzeImageWithQwenVLAsync()
        {
            try
            {
                IsAnalyzingImage = true;
                AnalysisStatus = "Analyzing image with Qwen VL...";
                AddLog("Starting image analysis with Qwen VL...");

                var systemPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", "klien-story-10.md");
                if (!File.Exists(systemPromptPath))
                {
                    throw new FileNotFoundException($"System prompt file not found: {systemPromptPath}");
                }
                var systemPrompt = await File.ReadAllTextAsync(systemPromptPath);

                var modelName = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(modelName))
                {
                    System.Windows.MessageBox.Show(
                        "No LM Studio model selected. Please configure LM Studio in the Image Analyzer tab first.",
                        "Model Not Configured", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                var userPrompt = "Analyze this character image and generate 10 sequential story prompts following the template in the system instructions.";

                var analysisResult = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    modelName,
                    InputImagePath,
                    userPrompt,
                    systemPrompt,
                    maxTokens: 36000,
                    CancellationToken.None);

                if (string.IsNullOrEmpty(analysisResult))
                {
                    AddLog("ERROR: No response from Qwen VL");
                    System.Windows.MessageBox.Show("No response received from Qwen VL.", "Error",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                AddLog($"Received Qwen VL response ({analysisResult.Length} chars)");

                var prompts = ParsePromptsFromAnalysis(analysisResult);

                if (prompts.Count == 0)
                {
                    AddLog("ERROR: Could not parse any prompts from Qwen VL response");
                    AddLog($"Raw response preview: {analysisResult.Substring(0, Math.Min(500, analysisResult.Length))}");
                    System.Windows.MessageBox.Show(
                        "Could not parse prompts from Qwen VL response. Check the activity log for details.",
                        "Parse Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                var startIndex = QueueItems.Any() ? QueueItems.Max(q => q.Index) + 1 : 1;
                for (int i = 0; i < prompts.Count; i++)
                {
                    QueueItems.Add(CreateQueueItem(startIndex + i, prompts[i], InputImagePath));
                }

                if (string.IsNullOrEmpty(PromptJsonFilePath))
                {
                    var imageName = Path.GetFileNameWithoutExtension(InputImagePath);
                    PromptJsonFilePath = Path.Combine(
                        Path.GetDirectoryName(InputImagePath) ?? AppDomain.CurrentDomain.BaseDirectory,
                        $"{imageName}-zvl.json");
                }

                UpdateQueueCountNotifications();
                SaveQueueToFile();
                CommandManager.InvalidateRequerySuggested();
                AnalysisStatus = $"Added {prompts.Count} prompts to queue";
                AddLog($"Added {prompts.Count} prompts from Qwen VL analysis to queue (total: {QueueItems.Count})");

                if (CanProcessQueue)
                {
                    AddLog("Auto-starting queue processing...");
                    _ = ProcessQueueAsync();
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR analyzing image: {ex.Message}");
                _logger.LogError($"Error analyzing image with Qwen VL: {ex}");
                AnalysisStatus = "Analysis failed";

                var lmStudioUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                System.Windows.MessageBox.Show(
                    $"Error analyzing image:\n\n{ex.Message}\n\nPlease ensure LM Studio is running at {lmStudioUrl} with a Qwen VL model loaded.",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzingImage = false;
            }
        }

        private List<string> ParsePromptsFromAnalysis(string analysisText)
        {
            var prompts = new List<string>();

            // Strategy 1: Split by "Prompt #N:" or "Prompt N:" pattern
            var pattern = @"Prompt\s*#?\s*(\d+)\s*:\s*";
            var matches = Regex.Matches(analysisText, pattern, RegexOptions.IgnoreCase);

            for (int i = 0; i < matches.Count; i++)
            {
                var startPos = matches[i].Index + matches[i].Length;
                var endPos = (i + 1 < matches.Count) ? matches[i + 1].Index : analysisText.Length;
                var promptText = analysisText.Substring(startPos, endPos - startPos).Trim();
                if (!string.IsNullOrWhiteSpace(promptText))
                    prompts.Add(promptText);
            }

            // Strategy 2 (Fallback): Split by "Subject:" occurrences
            if (prompts.Count == 0)
            {
                AddLog("No 'Prompt #N:' labels found, falling back to 'Subject:' delimiter parsing...");
                var subjectPattern = @"(?=Subject\s*:)";
                var segments = Regex.Split(analysisText, subjectPattern, RegexOptions.IgnoreCase);
                foreach (var segment in segments)
                {
                    var trimmed = segment.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.StartsWith("Subject", StringComparison.OrdinalIgnoreCase))
                        prompts.Add(trimmed);
                }
            }

            // Strategy 3 (Last resort): Use PromptParser.ExtractPrompts
            if (prompts.Count == 0)
            {
                AddLog("Fallback: Using generic PromptParser.ExtractPrompts...");
                prompts = PromptParser.ExtractPrompts(analysisText);
            }

            AddLog($"Parsed {prompts.Count} prompts from Qwen VL response");
            return prompts;
        }

        // --- Core processing logic ---

        protected override async Task<string> ProcessQueueItemAsync(
            StoryPromptItem item,
            string inputImagePath,
            string? sessionOutputDir,
            string jsonFileName,
            CancellationToken cancellationToken)
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

            // If LoRA is enabled but the snapshot has no LoRA selected, fall back to current selection
            if (item.LoraEnabled && string.IsNullOrEmpty(item.SelectedLora) && !string.IsNullOrEmpty(SelectedLora))
            {
                AddLog($"LoRA snapshot was empty — using current selection: {SelectedLora}");
                item.SelectedLora = SelectedLora;
                item.LoraStrengthModel = LoraStrengthModel;
                item.LoraStrengthClip = LoraStrengthClip;
            }

            // Update workflow parameters (text-to-image, no input image needed)
            var updatedWorkflow = UpdateWorkflowParameters(workflow, item);

            // Execute workflow with progress reporting
            var progress = CreateProgressReporter(item);
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, cancellationToken);

            // Get output images
            var outputImages = await _imageRetriever.GetOutputImagesAsync(
                _comfyUIService.HttpClient,
                _settingsService,
                _logger,
                AddLog,
                specificFolder: "ZImage",
                promptId: promptId,
                ct: cancellationToken);
            if (!outputImages.Any())
            {
                throw new InvalidOperationException("No output images were generated");
            }

            var outputImage = outputImages.First();

            // Generate sequential filename: jsonfilename-1.png, jsonfilename-2.png, etc.
            var outputPath = Path.Combine(sessionOutputDir!, $"{jsonFileName}-{item.Index}.png");

            await File.WriteAllBytesAsync(outputPath, outputImage);
            await LocalCopyService.CopyImageAsync(outputPath);
            AddLog($"Story image #{item.Index} saved: {outputPath} ({outputImage.Length} bytes)");
            return outputPath;
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, StoryPromptItem item)
        {
            var workflowJson = workflow.GetRawText();

            // 1. Update user prompt (node 385 - StringTrim)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "385", "string", item.Prompt);

            // 2. Update negative prompt (node 60)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "60", "text", item.NegativePrompt);

            // 3. Update seed (node 307)
            var random = new Random();
            int seed = random.Next(1, int.MaxValue);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "307", "value", seed);

            // 4. Update style template (node 125)
            var styleTemplate = GetStyleTemplateForWorkflow(item.SelectedStyle, item.SpicyContentEnabled);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "125", "value", styleTemplate);

            // 5. Update LoRA settings - always process to disable hardcoded loras when not enabled
            AddLog($"Processing LoRA nodes - LoRA Enabled: {item.LoraEnabled}, Selected LoRA: {item.SelectedLora}");
            workflowJson = UpdateLoraSettings(workflowJson, item);

            // 6. Update output filename prefix with timestamp (node 9)
            var timestamp = DateTime.Now.ToString("yyyy_MM_dd");
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "9", "filename_prefix", $"ZImage/{timestamp}/ZI");

            // 7. Update resolution/orientation (nodes 56, 243, 248)
            workflowJson = UpdateResolution(workflowJson, item.SelectedOrientation);

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        private string UpdateLoraSettings(string workflowJson, StoryPromptItem item)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson);
            if (workflowDict == null) return workflowJson;

            foreach (var kvp in workflowDict)
            {
                var nodeElement = kvp.Value;
                if (nodeElement.TryGetProperty("class_type", out var classTypeElement))
                {
                    var classTypeStr = classTypeElement.GetString();
                    if (classTypeStr == "Power Lora Loader (rgthree)" && nodeElement.TryGetProperty("inputs", out var _))
                    {
                        AddLog($"Found Power Lora Loader node {kvp.Key}");

                        var nodeDict = JsonSerializer.Deserialize<Dictionary<string, object>>(nodeElement.GetRawText());
                        if (nodeDict != null && nodeDict.ContainsKey("inputs"))
                        {
                            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                JsonSerializer.Serialize(nodeDict["inputs"]));
                            if (inputs != null)
                            {
                                if (item.LoraEnabled)
                                {
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
                                                    loraEntry["lora"] = $"zimage\\{item.SelectedLora}.safetensors";
                                                    loraEntry["strength"] = item.LoraStrengthModel;
                                                    inputs[loraKey] = loraEntry;
                                                    loraUpdated = true;
                                                    AddLog($"Updated {loraKey} with LoRA: {item.SelectedLora}.safetensors (Strength: {item.LoraStrengthModel})");
                                                    break;
                                                }
                                            }
                                        }
                                    }

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

            return JsonSerializer.Serialize(workflowDict);
        }

        private string UpdateResolution(string workflowJson, string selectedOrientation)
        {
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

            // Update EmptyLatentImage (node 56)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "56", new Dictionary<string, object>
            {
                { "width", width },
                { "height", height }
            });

            int shortSide = 1088;
            int longSide = 1600;
            int widthSD3 = width;
            int heightSD3 = height;

            if (selectedOrientation == "Portrait (944x1408)")
            {
                widthSD3 = shortSide;
                heightSD3 = longSide;
            }
            else if (selectedOrientation == "Landscape (1408x944)")
            {
                widthSD3 = longSide;
                heightSD3 = shortSide;
            }
            else
            {
                widthSD3 = 1088;
                heightSD3 = 1088;
            }

            // Update Short Side (node 243)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "243", "value", widthSD3);

            // Update Long Side (node 248)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "248", "value", heightSD3);

            // Directly set EmptySD3LatentImage (node 244) dimensions, bypassing Any Switch routing
            // (nodes 536/537 have no sel input connected so default routing is always landscape)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "244", new Dictionary<string, object>
            {
                { "width", widthSD3 },
                { "height", heightSD3 }
            });

            AddLog($"Orientation: {selectedOrientation} -> Node56: {width}x{height}, SD3: {widthSD3}x{heightSD3}");

            return workflowJson;
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

            if (spicyContentEnabled)
            {
                return baseTemplate.Replace("{$spicy-content-with}", "erotic, sensual,");
            }
            else
            {
                return baseTemplate.Replace("{$spicy-content-with}", "");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                // Clear additional collections
                AvailableLoras?.Clear();
                AvailableStyles?.Clear();
                AvailableOrientations?.Clear();
                UpscaleMethods?.Clear();
                _allStyles?.Clear();

                // Clear additional string properties
                _selectedLora = string.Empty;
                _selectedStyle = string.Empty;
                _customStyleTemplate = string.Empty;
                _selectedOrientation = string.Empty;
                _upscaleMethod = string.Empty;

                _disposed = true;
            }
            base.Dispose(disposing);
        }

        private bool _disposed = false;
    }
}
