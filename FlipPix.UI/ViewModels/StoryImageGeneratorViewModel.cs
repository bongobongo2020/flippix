using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Commands;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    public class StoryImageGeneratorViewModel : StoryImageGeneratorBaseViewModel
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
        private string _selectedOrientation = "Portrait (944x1408)";

        public StoryImageGeneratorViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            WorkflowQueueCoordinator workflowCoordinator)
            : base(comfyUIService, logger, settingsService, workflowCoordinator)
        {
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
            RefreshLorasCommand = new RelayCommand(RefreshLoras);

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
                var loraBasePath = GetLoraModelPath();
                if (!string.IsNullOrEmpty(loraBasePath))
                {
                    var zimageLoraPath = Path.Combine(loraBasePath, "zimage");
                    if (Directory.Exists(zimageLoraPath))
                    {
                        LoadLorasFromDirectory(zimageLoraPath, "ComfyUI LoRA directory");
                        return;
                    }
                    else
                    {
                        LoadLorasFromDirectory(loraBasePath, "ComfyUI LoRA directory");
                        return;
                    }
                }

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

            // Update workflow parameters (text-to-image, no input image needed)
            var updatedWorkflow = UpdateWorkflowParameters(workflow, item);

            // Execute workflow with progress reporting
            var progress = CreateProgressReporter(item);
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, cancellationToken);

            // Get output images
            var outputImages = await GetOutputImagesFromComfyUI(promptId);
            if (!outputImages.Any())
            {
                throw new InvalidOperationException("No output images were generated");
            }

            var outputImage = outputImages.First();

            // Generate sequential filename: jsonfilename-1.png, jsonfilename-2.png, etc.
            var outputPath = Path.Combine(sessionOutputDir!, $"{jsonFileName}-{item.Index}.png");

            await File.WriteAllBytesAsync(outputPath, outputImage);
            AddLog($"Story image #{item.Index} saved: {outputPath} ({outputImage.Length} bytes)");
            return outputPath;
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
                        var styleTemplate = GetStyleTemplateForWorkflow(item.SelectedStyle, item.SpicyContentEnabled);
                        inputs["value"] = styleTemplate;
                        node125["inputs"] = inputs;
                        workflowDict["125"] = JsonSerializer.SerializeToElement(node125);
                    }
                }
            }

            // 5. Update LoRA settings - always process to disable hardcoded loras when not enabled
            AddLog($"Processing LoRA nodes - LoRA Enabled: {item.LoraEnabled}, Selected LoRA: {item.SelectedLora}");

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

            // Update Long Side (node 248)
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

                int retryCount = 0;
                int maxRetries = 20;

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

                        var searchDirs = new List<string> { comfyUIOutputDir };

                        var zimageDir = Path.Combine(comfyUIOutputDir, "ZImage");
                        if (Directory.Exists(zimageDir))
                        {
                            searchDirs.Add(zimageDir);
                            try
                            {
                                var dateDirs = Directory.GetDirectories(zimageDir)
                                    .OrderByDescending(d => Directory.GetLastWriteTime(d))
                                    .Take(3);
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
                                break;
                            }
                        }

                        if (!images.Any())
                        {
                            AddLog($"No recent images found in retry {retryCount + 1}");

                            if (retryCount >= 5)
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
    }
}
