using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    public partial class StoryImageGeneratorQViewModel : StoryImageGeneratorBaseViewModel
    {
        private readonly LMStudioService _lmStudioService;
        private bool _settingsVisible = false;

        public StoryImageGeneratorQViewModel(
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

        // --- Workflow mode ---

        public static readonly IReadOnlyList<string> WorkflowModes = new[] { "Qwen", "Klein", "FireRed", "StoryImageZ" };

        private string _selectedWorkflowMode = "Qwen";
        public string SelectedWorkflowMode
        {
            get => _selectedWorkflowMode;
            set
            {
                if (SetProperty(ref _selectedWorkflowMode, value))
                {
                    if (value == "Klein")
                        Steps = 20;
                    else if (value == "FireRed")
                        Steps = 8;
                    else
                        Steps = DefaultSteps;

                    OnPropertyChanged(nameof(ShowLoRAOption));
                    OnPropertyChanged(nameof(ShowZOptions));
                    OnPropertyChanged(nameof(CanLoadPrompts));

                    if (value == "StoryImageZ" && _zAllStyles.Count == 0)
                        LoadZWorkflowsAndStyles();
                    if (value == "StoryImageZ" && !_zAvailableLoras.Any())
                        LoadZAvailableLoras();
                }
            }
        }

        private bool _useLoRA = true;
        public bool UseLoRA
        {
            get => _useLoRA;
            set => SetProperty(ref _useLoRA, value);
        }

        public bool ShowLoRAOption => SelectedWorkflowMode == "FireRed";

        public bool ShowZOptions => SelectedWorkflowMode == "StoryImageZ";

        // --- Z workflow fields ---

        private List<StyleInfo> _zAllStyles = new();
        private int _zSelectedStyleIndex = 0;
        private ObservableCollection<string> _zAvailableLoras = new();
        private string _zSelectedLora = string.Empty;
        private bool _zLoraEnabled = false;
        private double _zLoraStrengthModel = 1.0;
        private double _zLoraStrengthClip = 1.0;
        private string _zSelectedOrientation = "Portrait (944x1408)";

        public static readonly IReadOnlyList<string> ZOrientations = new[]
        {
            "Portrait (944x1408)",
            "Landscape (1408x944)",
            "Square (1088x1088)"
        };

        public string ZSelectedOrientation
        {
            get => _zSelectedOrientation;
            set { if (_zSelectedOrientation != value) { _zSelectedOrientation = value; OnPropertyChanged(); } }
        }

        public string[] ZStyleNames => _zAllStyles.Select(s => s.Name).ToArray();

        public int ZSelectedStyleIndex
        {
            get => _zSelectedStyleIndex;
            set { if (_zSelectedStyleIndex != value) { _zSelectedStyleIndex = value; OnPropertyChanged(); } }
        }

        public StyleInfo? ZSelectedWorkflowStyle => _zAllStyles.Count > 0
            ? _zAllStyles[Math.Min(_zSelectedStyleIndex, _zAllStyles.Count - 1)]
            : null;

        public ObservableCollection<string> ZAvailableLoras
        {
            get => _zAvailableLoras;
            set { if (_zAvailableLoras != value) { _zAvailableLoras = value; OnPropertyChanged(); } }
        }

        public string ZSelectedLora
        {
            get => _zSelectedLora;
            set
            {
                if (_zSelectedLora != value)
                {
                    _zSelectedLora = value;
                    OnPropertyChanged();
                    SaveZLoraSettings();
                }
            }
        }

        public bool ZLoraEnabled
        {
            get => _zLoraEnabled;
            set
            {
                if (_zLoraEnabled != value)
                {
                    _zLoraEnabled = value;
                    OnPropertyChanged();
                    SaveZLoraSettings();
                }
            }
        }

        public double ZLoraStrengthModel
        {
            get => _zLoraStrengthModel;
            set
            {
                if (_zLoraStrengthModel != value)
                {
                    _zLoraStrengthModel = value;
                    OnPropertyChanged();
                    SaveZLoraSettings();
                }
            }
        }

        public double ZLoraStrengthClip
        {
            get => _zLoraStrengthClip;
            set { if (_zLoraStrengthClip != value) { _zLoraStrengthClip = value; OnPropertyChanged(); } }
        }

        public ICommand RefreshZLorasCommand { get; private set; } = null!;

        // --- Abstract member implementations ---

        protected override string VariantDisplayName => "Story Image Generator Q";
        protected override string WorkflowTypeName => "StoryImageQ";
        protected override string QueuePersistenceFileName => "story_image_q_queue.json";
        protected override string OutputFolderName => "story-generator-q";
        protected override int DefaultSteps => 8;
        protected override double DefaultCfg => 1.0;
        protected override double DefaultDenoise => 0.98;
        protected override bool RequiresInputImage => SelectedWorkflowMode != "StoryImageZ";

        // --- Variant-specific initialization ---

        protected override void InitializeVariant()
        {
            ToggleSettingsVisibilityCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ToggleSettingsVisibility);
            AnalyzeImageWithQwenVLCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(
                async () => await AnalyzeImageWithQwenVLAsync(),
                () => !string.IsNullOrEmpty(InputImagePath) && File.Exists(InputImagePath) && !IsAnalyzingImage);
            RefreshZLorasCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(RefreshZLoras);
            LoadZWorkflowsAndStyles();
            LoadZAvailableLoras();
            RestoreZLoraSettings();
        }

        // --- Overrides for folder saving ---

        protected override string GetPromptJsonInitialDirectory()
        {
            var folder = _settingsService.Settings?.StoryGeneratorPromptJsonFolder;
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder)
                ? folder
                : base.GetPromptJsonInitialDirectory();
        }

        protected override void SavePromptJsonFolder(string folderPath)
        {
            if (_settingsService.Settings != null)
            {
                _settingsService.Settings.StoryGeneratorPromptJsonFolder = folderPath;
                _settingsService.SaveSettings(_settingsService.Settings);
            }
        }

        protected override string GetInputImageInitialDirectory()
        {
            var folder = _settingsService.Settings?.StoryGeneratorInputImageFolder;
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder)
                ? folder
                : base.GetInputImageInitialDirectory();
        }

        protected override void SaveInputImageFolder(string folderPath)
        {
            if (_settingsService.Settings != null)
            {
                _settingsService.Settings.StoryGeneratorInputImageFolder = folderPath;
                _settingsService.SaveSettings(_settingsService.Settings);
            }
        }

        // --- Variant-specific properties ---

        public bool SettingsVisible
        {
            get => _settingsVisible;
            set
            {
                if (_settingsVisible != value)
                {
                    _settingsVisible = value;
                    OnPropertyChanged();
                }
            }
        }

        public ICommand ToggleSettingsVisibilityCommand { get; private set; } = null!;
        public ICommand AnalyzeImageWithQwenVLCommand { get; private set; } = null!;

        private bool _isAnalyzingImage = false;
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

        private string _analysisStatus = string.Empty;
        public string AnalysisStatus
        {
            get => _analysisStatus;
            set => SetProperty(ref _analysisStatus, value);
        }

        private string _storyConcept = string.Empty;
        public string StoryConcept
        {
            get => _storyConcept;
            set => SetProperty(ref _storyConcept, value);
        }

        private void ToggleSettingsVisibility()
        {
            SettingsVisible = !SettingsVisible;
        }

        // --- CreateQueueItem override for Z mode ---

        protected override StoryPromptItem CreateQueueItem(int index, string prompt, string inputImagePath)
        {
            if (SelectedWorkflowMode == "StoryImageZ")
            {
                return new StoryPromptItem
                {
                    Index = index,
                    Prompt = prompt,
                    InputImagePath = inputImagePath,
                    Status = "Queued",
                    StyleName = ZSelectedWorkflowStyle?.Name ?? "",
                    StyleWorkflowFile = ZSelectedWorkflowStyle?.WorkflowFile ?? "",
                    LoraEnabled = ZLoraEnabled,
                    SelectedLora = ZSelectedLora,
                    LoraStrengthModel = ZLoraStrengthModel,
                    LoraStrengthClip = ZLoraStrengthClip,
                    SelectedStyle = "Phone Photo",
                    SpicyContentEnabled = false,
                    NegativePrompt = NegativePrompt,
                    SelectedOrientation = ZSelectedOrientation,
                };
            }
            return base.CreateQueueItem(index, prompt, inputImagePath);
        }

        // --- Z workflow: style and LoRA loading ---

        private void LoadZWorkflowsAndStyles()
        {
            try
            {
                _zAllStyles.Clear();
                var workflowDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "ZStyles");

                if (!Directory.Exists(workflowDir))
                {
                    AddLog($"ZStyles workflow directory not found at {workflowDir}");
                    OnPropertyChanged(nameof(ZStyleNames));
                    return;
                }

                foreach (var workflowFile in Directory.GetFiles(workflowDir, "*.json"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(workflowFile);
                    var styleName = fileName.StartsWith("Z") ? fileName.Substring(1) : fileName;
                    _zAllStyles.Add(new StyleInfo
                    {
                        Name = styleName,
                        PromptTemplate = "",
                        WorkflowFile = workflowFile,
                        NodeId = ""
                    });
                }

                _zAllStyles = _zAllStyles.OrderBy(s => s.Name).ToList();
                AddLog($"Loaded {_zAllStyles.Count} ZStyles workflows for StoryImageZ");
                OnPropertyChanged(nameof(ZStyleNames));
                OnPropertyChanged(nameof(ZSelectedWorkflowStyle));
            }
            catch (Exception ex)
            {
                AddLog($"Error loading ZStyles: {ex.Message}");
            }
        }

        private void LoadZAvailableLoras()
        {
            try
            {
                var overridePath = _settingsService.Settings?.RemoteLoraFolderPath;
                if (!string.IsNullOrEmpty(overridePath) && Directory.Exists(overridePath))
                {
                    LoadZLorasFromDirectory(overridePath);
                    return;
                }

                var loraBasePath = GetLoraModelPath();
                if (!string.IsNullOrEmpty(loraBasePath))
                {
                    var zimageLoraPath = Path.Combine(loraBasePath, "zimage");
                    LoadZLorasFromDirectory(Directory.Exists(zimageLoraPath) ? zimageLoraPath : loraBasePath);
                    return;
                }

                LoadZLorasFromDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "loras", "zimage"));
            }
            catch (Exception ex)
            {
                AddLog($"Error loading Z LoRAs: {ex.Message}");
                _zAvailableLoras.Clear();
                _zAvailableLoras.Add("Error loading LoRAs");
            }
        }

        private void LoadZLorasFromDirectory(string loraPath)
        {
            _zAvailableLoras.Clear();
            if (!Directory.Exists(loraPath))
            {
                _zAvailableLoras.Add("No LoRAs available");
                return;
            }

            var loraFiles = Directory.GetFiles(loraPath, "*.safetensors", SearchOption.AllDirectories)
                .Select(f => Path.ChangeExtension(Path.GetRelativePath(loraPath, f), null).Replace('/', '\\'))
                .Where(name => !string.IsNullOrEmpty(name))
                .OrderBy(name => name)
                .ToList();

            if (loraFiles.Any())
            {
                foreach (var lora in loraFiles)
                    _zAvailableLoras.Add(lora);
                if (string.IsNullOrEmpty(ZSelectedLora))
                    ZSelectedLora = _zAvailableLoras.First();
                AddLog($"Loaded {_zAvailableLoras.Count} LoRAs for StoryImageZ");
            }
            else
            {
                _zAvailableLoras.Add("No LoRAs available");
            }
        }

        private void RefreshZLoras()
        {
            LoadZAvailableLoras();
            RestoreZLoraSettings();
            AddLog("Refreshed Z LoRA list");
        }

        private void RestoreZLoraSettings()
        {
            var s = _settingsService.Settings;
            if (s == null) return;

            _zLoraEnabled = s.StoryImageQZLoraEnabled;
            OnPropertyChanged(nameof(ZLoraEnabled));

            _zLoraStrengthModel = s.StoryImageQZLoraStrengthModel > 0 ? s.StoryImageQZLoraStrengthModel : 1.0;
            OnPropertyChanged(nameof(ZLoraStrengthModel));

            if (!string.IsNullOrEmpty(s.StoryImageQZSelectedLora) && _zAvailableLoras.Contains(s.StoryImageQZSelectedLora))
            {
                _zSelectedLora = s.StoryImageQZSelectedLora;
                OnPropertyChanged(nameof(ZSelectedLora));
            }
        }

        private void SaveZLoraSettings()
        {
            var s = _settingsService.Settings;
            if (s == null) return;
            s.StoryImageQZLoraEnabled = _zLoraEnabled;
            s.StoryImageQZSelectedLora = _zSelectedLora;
            s.StoryImageQZLoraStrengthModel = _zLoraStrengthModel;
            _settingsService.SaveSettings(s);
        }

        // --- Z workflow processing ---

        private async Task<string> ProcessZQueueItemAsync(StoryPromptItem item, string jsonFileName, CancellationToken cancellationToken)
        {
            if (!_comfyUIService.IsConnected)
                await _comfyUIService.ConnectAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // Sync style from current ViewModel if item doesn't have one (e.g. created before style was selected, or loaded from old queue)
            if (string.IsNullOrEmpty(item.StyleWorkflowFile) && ZSelectedWorkflowStyle != null)
            {
                item.StyleName = ZSelectedWorkflowStyle.Name;
                item.StyleWorkflowFile = ZSelectedWorkflowStyle.WorkflowFile;
            }

            if (string.IsNullOrEmpty(item.StyleWorkflowFile))
                throw new InvalidOperationException("No ZStyle selected. Please select a style workflow.");

            if (!File.Exists(item.StyleWorkflowFile))
                throw new FileNotFoundException($"Workflow file not found: {item.StyleWorkflowFile}");

            AddLog($"Using ZStyle: {item.StyleName}");

            var workflowJson = await File.ReadAllTextAsync(item.StyleWorkflowFile, cancellationToken);
            var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

            cancellationToken.ThrowIfCancellationRequested();

            // Always sync current ViewModel settings — item may have been created before these were changed
            item.SelectedOrientation = ZSelectedOrientation;
            item.LoraEnabled = ZLoraEnabled;
            item.SelectedLora = ZSelectedLora;
            item.LoraStrengthModel = ZLoraStrengthModel;
            item.LoraStrengthClip = ZLoraStrengthClip;
            AddLog($"Orientation: {item.SelectedOrientation}, LoRA: enabled={item.LoraEnabled}, lora='{item.SelectedLora}', strength={item.LoraStrengthModel:F2}");

            var updatedWorkflow = UpdateZWorkflowParameters(workflow, item);
            var progress = CreateProgressReporter(item);
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, cancellationToken);

            var outputImages = await _imageRetriever.GetOutputImagesAsync(
                _comfyUIService.HttpClient,
                _settingsService,
                _logger,
                AddLog,
                specificFolder: "ZImage",
                promptId: promptId,
                ct: cancellationToken);

            if (!outputImages.Any())
                throw new InvalidOperationException("No output images were generated");

            var baseOutputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", OutputFolderName, jsonFileName);
            Directory.CreateDirectory(baseOutputDir);
            var outputPath = Path.Combine(baseOutputDir, $"{jsonFileName}-{item.Index}.png");

            await File.WriteAllBytesAsync(outputPath, outputImages.First());
            await LocalCopyService.CopyImageAsync(outputPath);
            AddLog($"Story Q (Z mode) image #{item.Index} saved: {outputPath}");
            return outputPath;
        }

        private JsonElement UpdateZWorkflowParameters(JsonElement workflow, StoryPromptItem item)
        {
            var workflowJson = workflow.GetRawText();

            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "385", "string", item.Prompt);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "60", "text", item.NegativePrompt);

            var seed = new Random().Next(1, int.MaxValue);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "307", "value", seed);

            var styleTemplate = GetZStyleTemplate(item.SelectedStyle, item.SpicyContentEnabled);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "125", "value", styleTemplate);

            workflowJson = UpdateZLoraSettings(workflowJson, item);

            var timestamp = DateTime.Now.ToString("yyyy_MM_dd");
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "9", "filename_prefix", $"ZImage/{timestamp}/ZI");

            workflowJson = UpdateZResolution(workflowJson, item.SelectedOrientation);

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        private string UpdateZLoraSettings(string workflowJson, StoryPromptItem item)
        {
            var root = JsonNode.Parse(workflowJson);
            if (root == null) return workflowJson;

            JsonObject? loraInputs = null;
            string? foundNodeId = null;

            foreach (var kvp in root.AsObject())
            {
                var node = kvp.Value?.AsObject();
                if (node == null) continue;
                if (node["class_type"]?.GetValue<string>() != "Power Lora Loader (rgthree)") continue;
                loraInputs = node["inputs"]?.AsObject();
                foundNodeId = kvp.Key;
                break;
            }

            if (loraInputs == null)
            {
                AddLog("WARNING: No 'Power Lora Loader (rgthree)' node found in workflow — LoRA cannot be applied");
                return workflowJson;
            }

            AddLog($"Found Power Lora Loader node {foundNodeId}: LoRA enabled={item.LoraEnabled}, lora='{item.SelectedLora}'");

            if (item.LoraEnabled)
            {
                bool updated = false;
                for (int i = 1; i <= 10 && !updated; i++)
                {
                    var entry = loraInputs[$"lora_{i}"]?.AsObject();
                    if (entry == null) continue;
                    if (entry["on"]?.GetValue<bool>() != true) continue;
                    entry["lora"] = JsonValue.Create($"zimage/{item.SelectedLora.Replace('\\', '/')}.safetensors");
                    entry["strength"] = JsonValue.Create(item.LoraStrengthModel);
                    updated = true;
                    AddLog($"Updated lora_{i} with LoRA: {item.SelectedLora} (Strength: {item.LoraStrengthModel:F2})");
                }

                if (!updated)
                {
                    var lora1 = loraInputs["lora_1"]?.AsObject();
                    if (lora1 != null)
                    {
                        lora1["on"] = JsonValue.Create(true);
                        lora1["lora"] = JsonValue.Create($"zimage/{item.SelectedLora.Replace('\\', '/')}.safetensors");
                        lora1["strength"] = JsonValue.Create(item.LoraStrengthModel);
                        AddLog($"Enabled lora_1 with LoRA: {item.SelectedLora} (Strength: {item.LoraStrengthModel:F2})");
                    }
                }
            }
            else
            {
                for (int i = 1; i <= 10; i++)
                {
                    var entry = loraInputs[$"lora_{i}"]?.AsObject();
                    if (entry != null)
                        entry["on"] = JsonValue.Create(false);
                }
                AddLog("All LoRAs disabled in workflow");
            }

            return root.ToJsonString();
        }

        private string UpdateZResolution(string workflowJson, string orientation)
        {
            int width, height;
            switch (orientation)
            {
                case "Landscape (1408x944)": width = 1408; height = 944; break;
                case "Square (1088x1088)": width = 1088; height = 1088; break;
                default: width = 944; height = 1408; break;
            }

            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "56", new Dictionary<string, object>
            {
                { "width", width }, { "height", height }
            });

            int wSD3 = width == 1408 ? 1600 : (width == 944 ? 1088 : 1088);
            int hSD3 = height == 1408 ? 1600 : (height == 944 ? 1088 : 1088);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "243", "value", wSD3);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "248", "value", hSD3);
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "244", new Dictionary<string, object>
            {
                { "width", wSD3 }, { "height", hSD3 }
            });

            return workflowJson;
        }

        private string GetZStyleTemplate(string selectedStyle, bool spicy)
        {
            var template = selectedStyle switch
            {
                "Oil Painting" => "YOUR CONTEXT:\nYour artwork is a masterful oil painting on canvas.\nYour artwork exhibits {$spicy-content-with} rich brushstrokes, vibrant colors, dramatic lighting, classical composition, and museum-quality technique.\nYOUR ARTWORK:\n{$@}",
                "Watercolor" => "YOUR CONTEXT:\nYour artwork is a delicate watercolor painting.\nYour artwork exhibits {$spicy-content-with} soft washes, transparent colors, fluid blends, and ethereal atmospheric effects.\nYOUR ARTWORK:\n{$@}",
                "Vintage Film" => "YOUR CONTEXT:\nYour photographs have vintage film camera quality from the 1970s-80s.\nYour photographs exhibit {$spicy-content-with} film grain, warm color grading, and authentic nostalgic atmosphere.\nYOUR PHOTO:\n{$@}",
                "Cinematic" => "YOUR CONTEXT:\nYour photographs are cinematic film stills from a high-budget movie.\nYour photographs exhibit {$spicy-content-with} dramatic lighting, rich color grading, and theatrical composition.\nYOUR PHOTO:\n{$@}",
                "Anime" => "YOUR CONTEXT:\nYour artwork is in the style of high-quality Japanese anime.\nYour artwork exhibits {$spicy-content-with} clean lines, vibrant cel-shaded colors, and polished anime aesthetic.\nYOUR ARTWORK:\n{$@}",
                _ => "YOUR CONTEXT:\nYour photographs has android phone cam-quality.\nYour photographs exhibit {$spicy-content-with} surprising compositions, natural lighting, and candid moments that feel immediate and authentic.\nYOUR PHOTO:\n{$@}"
            };
            return spicy
                ? template.Replace("{$spicy-content-with}", "erotic, sensual,")
                : template.Replace("{$spicy-content-with}", "");
        }

        // --- Qwen VL Analysis ---

        private async Task AnalyzeImageWithQwenVLAsync()
        {
            try
            {
                IsAnalyzingImage = true;
                AnalysisStatus = "Analyzing image with Qwen VL...";
                AddLog("Starting image analysis with Qwen VL...");

                // Read system prompt from file
                var systemPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", "story-prompt.md");
                if (!File.Exists(systemPromptPath))
                {
                    throw new FileNotFoundException($"System prompt file not found: {systemPromptPath}");
                }
                var systemPrompt = await File.ReadAllTextAsync(systemPromptPath);

                // Get model name from settings
                var modelName = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(modelName))
                {
                    System.Windows.MessageBox.Show(
                        "No LM Studio model selected. Please configure LM Studio in the Image Analyzer tab first.",
                        "Model Not Configured", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Pass story concept as user message if provided
                var userPrompt = string.IsNullOrWhiteSpace(StoryConcept)
                    ? string.Empty
                    : $"Story concept: {StoryConcept.Trim()}";

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
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                AddLog($"Received Qwen VL response ({analysisResult.Length} chars)");

                // Parse the 10 prompts from the response
                var prompts = ParsePromptsFromAnalysis(analysisResult);

                if (prompts.Count == 0)
                {
                    AddLog("ERROR: Could not parse any prompts from Qwen VL response");
                    AddLog($"Raw response preview: {analysisResult.Substring(0, Math.Min(500, analysisResult.Length))}");
                    System.Windows.MessageBox.Show(
                        "Could not parse prompts from Qwen VL response. Check the activity log for details.",
                        "Parse Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Add prompts to queue (same as LoadPromptsAsync)
                var startIndex = QueueItems.Any() ? QueueItems.Max(q => q.Index) + 1 : 1;
                for (int i = 0; i < prompts.Count; i++)
                {
                    QueueItems.Add(CreateQueueItem(startIndex + i, prompts[i], InputImagePath));
                }

                // Set a default PromptJsonFilePath-based name for output folders/filenames
                // when prompts come from Qwen VL analysis instead of a JSON file
                if (string.IsNullOrEmpty(PromptJsonFilePath))
                {
                    // Use input image filename as the session name (e.g. "character1" from "character1.png")
                    var imageName = Path.GetFileNameWithoutExtension(InputImagePath);
                    PromptJsonFilePath = Path.Combine(
                        Path.GetDirectoryName(InputImagePath) ?? AppDomain.CurrentDomain.BaseDirectory,
                        $"{imageName}-qwenvl.json");
                }

                UpdateQueueCountNotifications();
                SaveQueueToFile();
                CommandManager.InvalidateRequerySuggested();
                AnalysisStatus = $"Added {prompts.Count} prompts to queue";
                AddLog($"Added {prompts.Count} prompts from Qwen VL analysis to queue (total: {QueueItems.Count})");

                // Auto-start processing if not already processing (same as LoadPromptsAsync)
                if (CanProcessQueue)
                {
                    AddLog("Auto-starting queue processing...");
                    _ = ProcessQueueAsync(); // Fire and forget
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
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzingImage = false;
            }
        }

        private List<string> ParsePromptsFromAnalysis(string analysisText)
        {
            var prompts = new List<string>();

            // Strategy 1: "Scene N — Title:" format (VERA/huihui story prompt output)
            var scenePattern = @"Scene\s+\d+\s*[—–\-][^\n:]*:";
            var sceneMatches = Regex.Matches(analysisText, scenePattern, RegexOptions.IgnoreCase);
            if (sceneMatches.Count > 0)
            {
                for (int i = 0; i < sceneMatches.Count; i++)
                {
                    var startPos = sceneMatches[i].Index + sceneMatches[i].Length;
                    var endPos = (i + 1 < sceneMatches.Count) ? sceneMatches[i + 1].Index : analysisText.Length;

                    var promptText = analysisText.Substring(startPos, endPos - startPos).Trim();
                    // Strip surrounding quotes if present (VERA wraps prompts in "...")
                    promptText = promptText.Trim('"').Trim();

                    if (!string.IsNullOrWhiteSpace(promptText))
                        prompts.Add(promptText);
                }
                AddLog($"Strategy 1 (Scene N — Title): parsed {prompts.Count} prompts");
                AddLog($"Parsed {prompts.Count} prompts from Qwen VL response");
                return prompts;
            }

            // Strategy 2: Split by "Prompt #N:" or "Prompt N:" pattern
            var promptPattern = @"Prompt\s*#?\s*(\d+)\s*:\s*";
            var promptMatches = Regex.Matches(analysisText, promptPattern, RegexOptions.IgnoreCase);

            for (int i = 0; i < promptMatches.Count; i++)
            {
                var startPos = promptMatches[i].Index + promptMatches[i].Length;
                var endPos = (i + 1 < promptMatches.Count) ? promptMatches[i + 1].Index : analysisText.Length;

                var promptText = analysisText.Substring(startPos, endPos - startPos).Trim();

                if (!string.IsNullOrWhiteSpace(promptText))
                    prompts.Add(promptText);
            }

            if (prompts.Count > 0)
            {
                AddLog($"Strategy 2 (Prompt #N): parsed {prompts.Count} prompts");
                AddLog($"Parsed {prompts.Count} prompts from Qwen VL response");
                return prompts;
            }

            // Strategy 3: Split by "Subject:" occurrences
            AddLog("No scene/prompt labels found, falling back to 'Subject:' delimiter parsing...");
            var subjectPattern = @"(?=Subject\s*:)";
            var segments = Regex.Split(analysisText, subjectPattern, RegexOptions.IgnoreCase);

            foreach (var segment in segments)
            {
                var trimmed = segment.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.StartsWith("Subject", StringComparison.OrdinalIgnoreCase))
                    prompts.Add(trimmed);
            }

            if (prompts.Count > 0)
            {
                AddLog($"Strategy 3 (Subject:): parsed {prompts.Count} prompts");
                AddLog($"Parsed {prompts.Count} prompts from Qwen VL response");
                return prompts;
            }

            // Strategy 4 (Last resort): generic parsing
            AddLog("Fallback: Using generic PromptParser.ExtractPrompts...");
            prompts = PromptParser.ExtractPrompts(analysisText);

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
            if (!_comfyUIService.IsConnected)
                await _comfyUIService.ConnectAsync(cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (SelectedWorkflowMode == "StoryImageZ")
                return await ProcessZQueueItemAsync(item, jsonFileName, cancellationToken);

            string workflowPath;
            if (SelectedWorkflowMode == "Klein")
            {
                workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "klein", "V2-Edit-with-LCS-example-workflowAPI.json");
                AddLog("Using Klein workflow (Flux2 + LCS)");
            }
            else if (SelectedWorkflowMode == "FireRed")
            {
                workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "firered-image-edit-1.1API.json");
                AddLog("Using FireRed workflow");
            }
            else
            {
                workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "RapidEditAIO-API.json");
                AddLog("Using Qwen workflow (RapidEditAIO)");
            }

            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow file not found: {workflowPath}");

            var workflowJson = await File.ReadAllTextAsync(workflowPath, cancellationToken);
            var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

            cancellationToken.ThrowIfCancellationRequested();

            var uploadedImageName = await _comfyUIService.UploadImageAsync(inputImagePath);

            JsonElement updatedWorkflow;
            if (SelectedWorkflowMode == "Klein")
                updatedWorkflow = UpdateKleinWorkflowParameters(workflow, uploadedImageName, item.Prompt, item.Index, jsonFileName);
            else if (SelectedWorkflowMode == "FireRed")
                updatedWorkflow = UpdateFireRedWorkflowParameters(workflow, uploadedImageName, item.Prompt, item.Index, jsonFileName);
            else
                updatedWorkflow = UpdateQwenWorkflowParameters(workflow, uploadedImageName, item.Prompt, item.Index, jsonFileName);

            var progress = CreateProgressReporter(item);
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, cancellationToken);

            var outputImages = await GetOutputImagesFromComfyUI(promptId, jsonFileName, item.Index);
            if (!outputImages.Any())
                throw new InvalidOperationException("No output images were generated");

            var outputImage = outputImages.First();

            var baseOutputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", OutputFolderName, jsonFileName);
            Directory.CreateDirectory(baseOutputDir);

            var outputPath = Path.Combine(baseOutputDir, $"{jsonFileName}-{item.Index}.png");

            await File.WriteAllBytesAsync(outputPath, outputImage);
            await LocalCopyService.CopyImageAsync(outputPath);
            AddLog($"Story Q image #{item.Index} saved: {outputPath} ({outputImage.Length} bytes)");
            return outputPath;
        }

        private JsonElement UpdateQwenWorkflowParameters(JsonElement workflow, string inputImageName, string promptText, int imageIndex, string jsonFileName)
        {
            var workflowJson = workflow.GetRawText();

            // Node 213 - LoadImage
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "213", "image", inputImageName);
            // Node 153 - TextEncodeQwenImageEditPlus (positive)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "153", "prompt", promptText);
            // Node 154 - TextEncodeQwenImageEditPlus (negative)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "154", "prompt", NegativePrompt);
            // Node 3 - KSampler
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "3", new Dictionary<string, object>
            {
                { "seed", Random.Shared.NextInt64(0, long.MaxValue) },
                { "steps", Steps },
                { "cfg", Cfg },
                { "denoise", Denoise }
            });
            // Node 145 - ModelSamplingAuraFlow
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "145", "shift", 3.1);
            // Node 218 - SaveImage
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "218", "filename_prefix", $"{jsonFileName}/{jsonFileName}-{imageIndex}");

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        private JsonElement UpdateKleinWorkflowParameters(JsonElement workflow, string inputImageName, string promptText, int imageIndex, string jsonFileName)
        {
            var workflowJson = workflow.GetRawText();

            // Node 385 - LoadImage (input reference image)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "385", "image", inputImageName);
            // Node 407 - CLIPTextEncode (positive prompt)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "407", "text", promptText);
            // Nodes 386 and 443 - Flux2Scheduler (steps)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "386", "steps", Steps);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "443", "steps", Steps);
            // Nodes 371 and 435 - CFGGuider (cfg)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "371", "cfg", Cfg);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "435", "cfg", Cfg);
            // Randomize seed for LCS pipeline (node 439 - RandomNoise; node 387 uses node 160 which is already -1/random)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "439", "noise_seed", Random.Shared.NextInt64(0, long.MaxValue));

            // Inject SaveImage node connected to LCS VAEDecode output (node 433)
            // The Klein workflow only has a PreviewImage; we add SaveImage with a unique high node ID
            WorkflowNodeUpdater.AddNode(ref workflowJson, "9000", new
            {
                inputs = new
                {
                    filename_prefix = $"{jsonFileName}/{jsonFileName}-{imageIndex}",
                    images = new object[] { "433", 0 }
                },
                class_type = "SaveImage",
                _meta = new { title = "Save Image (FlipPix)" }
            });

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        private JsonElement UpdateFireRedWorkflowParameters(JsonElement workflow, string inputImageName, string promptText, int imageIndex, string jsonFileName)
        {
            var workflowJson = workflow.GetRawText();

            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "143", "image", inputImageName);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "118", "prompt", promptText);
            if (!string.IsNullOrEmpty(NegativePrompt))
                WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "117", "prompt", NegativePrompt);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "153", "value", UseLoRA);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "155", "value", Steps);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "156", "value", Steps);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "130", "denoise", Denoise);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "130", "seed", Random.Shared.Next());
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "9", "filename_prefix", $"{jsonFileName}/{jsonFileName}-{imageIndex}");

            AddLog($"FireRed workflow: LoRA={UseLoRA}, Steps={Steps}, Denoise={Denoise:F2}");

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        private async Task<List<byte[]>> GetOutputImagesFromComfyUI(string promptId, string jsonFileName, int imageIndex)
        {
            var images = new List<byte[]>();

            try
            {
                var baseUrl = _settingsService.Settings?.BaseUrl;
                if (string.IsNullOrEmpty(baseUrl))
                {
                    _logger.LogWarning("Settings BaseUrl is null or empty, reloading settings");
                    baseUrl = _settingsService.LoadSettings().BaseUrl;
                    if (string.IsNullOrEmpty(baseUrl))
                    {
                        _logger.LogWarning("Failed to load BaseUrl from settings, using default");
                        baseUrl = "http://127.0.0.1:8188";
                    }
                }
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;

                bool isRemoteComfyUI = _imageRetriever.IsComfyUIRemote(_settingsService);

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

                        List<string> imageFiles = new();
                        var expectedPattern = $"{jsonFileName}-{imageIndex}_";

                        // Strategy 1: Use prompt-specific history lookup (most reliable)
                        var promptOutputFiles = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                        imageFiles = promptOutputFiles.Where(f =>
                            f.EndsWith(".png") &&
                            (f.Contains(expectedPattern) || f.Contains($"{jsonFileName}/{expectedPattern}")))
                            .ToList();

                        if (imageFiles.Any())
                        {
                            AddLog($"Found {imageFiles.Count} output file(s) for prompt {promptId} matching pattern {expectedPattern}");
                        }
                        else
                        {
                            // Strategy 2: Try prompt-specific history without pattern filter (just get any png output from this prompt)
                            imageFiles = promptOutputFiles.Where(f => f.EndsWith(".png")).ToList();
                            if (imageFiles.Any())
                            {
                                AddLog($"Found {imageFiles.Count} output file(s) for prompt {promptId} (no pattern filter)");
                            }
                            else
                            {
                                // Strategy 3: Fall back to scanning recent history with pattern matching
                                AddLog($"No output files in history for prompt {promptId}, trying general pattern match...");

                                var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
                                AddLog($"Found {outputFiles.Count} potential output files in recent history");

                                imageFiles = outputFiles.Where(f =>
                                    f.EndsWith(".png") &&
                                    (f.Contains(expectedPattern) || f.Contains($"{jsonFileName}/{expectedPattern}")))
                                    .ToList();

                                AddLog($"Looking for pattern: {expectedPattern} (with or without subfolder prefix)");

                                if (!imageFiles.Any())
                                {
                                    AddLog($"No matching files found. Available files: {string.Join(", ", outputFiles.Take(5))}");
                                }
                            }
                        }

                        // Download the image
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

                        // Search in the single folder named after the JSON file
                        var subfolderPath = Path.Combine(comfyUIOutputDir, jsonFileName);

                        AddLog($"Searching for images in folder: {subfolderPath}");

                        if (Directory.Exists(subfolderPath))
                        {
                            // Look for files matching the pattern: jsonfilename-index_00001.png
                            var pattern = $"{jsonFileName}-{imageIndex}_*.png";
                            var matchingFiles = Directory.GetFiles(subfolderPath, pattern)
                                .Select(f => new FileInfo(f))
                                .OrderByDescending(f => f.LastWriteTime)
                                .ToList();

                            if (matchingFiles.Any())
                            {
                                var latestFile = matchingFiles.First();
                                AddLog($"Found matching file: {latestFile.Name} (modified: {latestFile.LastWriteTime})");
                                images.Add(await File.ReadAllBytesAsync(latestFile.FullName));
                            }
                            else
                            {
                                AddLog($"No files found matching pattern: {pattern}");
                                // List all files in the subfolder for debugging
                                var allFiles = Directory.GetFiles(subfolderPath, "*.png")
                                    .Select(Path.GetFileName)
                                    .ToList();
                                AddLog($"Files in subfolder: {string.Join(", ", allFiles)}");
                            }
                        }
                        else
                        {
                            AddLog($"Subfolder does not exist: {subfolderPath}");

                            // Fallback: search in the root output directory
                            var recentFiles = Directory.GetFiles(comfyUIOutputDir, "*.png")
                                .Select(f => new FileInfo(f))
                                .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 2)
                                .OrderByDescending(f => f.LastWriteTime)
                                .ToList();

                            if (recentFiles.Any())
                            {
                                AddLog($"Fallback: Found {recentFiles.Count} recent PNG files in root output directory");
                                var latestFile = recentFiles.First();
                                AddLog($"Using fallback file: {latestFile.Name}");
                                images.Add(await File.ReadAllBytesAsync(latestFile.FullName));
                            }
                        }

                        if (!images.Any())
                        {
                            AddLog($"No images found in retry {retryCount + 1}");
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

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
            }
            base.Dispose(disposing);
        }

        private bool _disposed = false;
    }
}
