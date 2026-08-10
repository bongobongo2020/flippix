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
            _selectedStoryPromptTemplate = _storyPromptTemplates[0];

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

        /// <summary>Krea2 two-reference identity edit mode (workflow/image/krea/krea2_edit_two_ref.json).</summary>
        public const string Krea2EditMode = "krea2-edit";

        public static readonly IReadOnlyList<string> WorkflowModes = new[] { "Qwen", "Klein", "FireRed", "StoryImageZ", "Krea2", "krea2-edit" };

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
                    else if (value == "Krea2")
                        Steps = 8; // Krea2 Turbo
                    else if (value == Krea2EditMode)
                        Steps = 12; // Krea2 Edit (turbo LoRA + identity edit LoRA)
                    else
                        Steps = DefaultSteps;

                    OnPropertyChanged(nameof(ShowLoRAOption));
                    OnPropertyChanged(nameof(ShowKreaLoraOption));
                    OnPropertyChanged(nameof(ShowKrea2EditOption));
                    OnPropertyChanged(nameof(ShowZOptions));
                    OnPropertyChanged(nameof(CanLoadPrompts));

                    if (value == "StoryImageZ" && _zAllStyles.Count == 0)
                        LoadZWorkflowsAndStyles();
                    if (value == "StoryImageZ" && !_zAvailableLoras.Any())
                        LoadZAvailableLoras();
                    if (value == "Krea2" && !_kreaLoras.Any())
                        LoadKreaLoras();
                }
            }
        }

        // --- Story prompt template (system prompt sent to Qwen VL for "Analyze Image") ---

        private readonly ObservableCollection<SeedHuntPromptTemplate> _storyPromptTemplates = new()
        {
            new SeedHuntPromptTemplate("📖 Story (10 scenes)", "story-prompt.md", string.Empty),
            new SeedHuntPromptTemplate("🎬 FFLF Continuous Shot — Image (10 stills, 5s)", "fflf-story-image.md", string.Empty),
            new SeedHuntPromptTemplate("🎬 FFLF Continuous Shot — Video (10 keyframes, 5s)", "fflf-story.md", string.Empty),
        };

        public ObservableCollection<SeedHuntPromptTemplate> StoryPromptTemplates => _storyPromptTemplates;

        private SeedHuntPromptTemplate _selectedStoryPromptTemplate;
        public SeedHuntPromptTemplate SelectedStoryPromptTemplate
        {
            get => _selectedStoryPromptTemplate;
            set
            {
                if (value != null && _selectedStoryPromptTemplate != value)
                {
                    _selectedStoryPromptTemplate = value;
                    OnPropertyChanged();
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

        public bool ShowKreaLoraOption => SelectedWorkflowMode == "Krea2";

        public bool ShowZOptions => SelectedWorkflowMode == "StoryImageZ";

        public bool ShowKrea2EditOption => SelectedWorkflowMode == Krea2EditMode;

        // --- Krea2 Edit fields ---
        // Image A (the scene being edited) is the queue's normal input image, per story item.
        // Image B is a single identity/subject reference reused for every prompt in the run,
        // which is what keeps the same character across all the story keyframes.

        private string _krea2EditRefImagePath = string.Empty;
        public string Krea2EditRefImagePath
        {
            get => _krea2EditRefImagePath;
            set
            {
                if (SetProperty(ref _krea2EditRefImagePath, value))
                    LoadKrea2EditRefImagePreview();
            }
        }

        private System.Windows.Media.Imaging.BitmapImage? _krea2EditRefImagePreview;
        public System.Windows.Media.Imaging.BitmapImage? Krea2EditRefImagePreview
        {
            get => _krea2EditRefImagePreview;
            set => SetProperty(ref _krea2EditRefImagePreview, value);
        }

        public ICommand SelectKrea2EditRefImageCommand { get; private set; } = null!;

        private async void SelectKrea2EditRefImage()
        {
            var selectedFile = await _fileDialogService.OpenFileDialogAsync(
                "Select Krea2 Edit Reference Image (image B)",
                "Image Files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All Files (*.*)|*.*",
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                persistKey: "storyimage.krea2-edit-ref-image");

            if (!string.IsNullOrEmpty(selectedFile))
            {
                Krea2EditRefImagePath = selectedFile;
                AddLog($"Selected Krea2 Edit reference image: {Path.GetFileName(selectedFile)}");
            }
        }

        private void LoadKrea2EditRefImagePreview()
        {
            if (string.IsNullOrEmpty(Krea2EditRefImagePath) || !File.Exists(Krea2EditRefImagePath))
            {
                Krea2EditRefImagePreview = null;
                return;
            }

            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(Krea2EditRefImagePath, UriKind.Absolute);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                Krea2EditRefImagePreview = bitmap;
            }
            catch (Exception ex)
            {
                AddLog($"Error loading Krea2 Edit reference preview: {ex.Message}");
                Krea2EditRefImagePreview = null;
            }
        }

        // --- Krea2 LoRA fields (loaded from the <loras>/krea2 subfolder) ---
        private ObservableCollection<string> _kreaLoras = new();
        private ObservableCollection<KreaLoraSelection> _selectedKreaLoras = new();
        private string _kreaLoraSubfolder = "krea2";

        public ObservableCollection<string> KreaLoras
        {
            get => _kreaLoras;
            set { if (_kreaLoras != value) { _kreaLoras = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Krea2 LoRAs to apply, in order, to Power Lora Loader (node 17) slots lora_1, lora_2, …
        /// </summary>
        public ObservableCollection<KreaLoraSelection> SelectedKreaLoras
        {
            get => _selectedKreaLoras;
            set { if (_selectedKreaLoras != value) { _selectedKreaLoras = value; OnPropertyChanged(); } }
        }

        public ICommand RefreshKreaLorasCommand { get; private set; } = null!;
        public ICommand AddKreaLoraCommand { get; private set; } = null!;
        public ICommand RemoveKreaLoraCommand { get; private set; } = null!;

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
        // StoryImageZ and Krea2 are text-to-image (no reference image needed); the rest are image edits.
        protected override bool RequiresInputImage => SelectedWorkflowMode != "StoryImageZ" && SelectedWorkflowMode != "Krea2";

        /// <summary>
        /// Local folder where this session's generated keyframes are saved
        /// ({BaseDir}/output/story-generator-q/{session}). These are the ordered images that the
        /// FFLF Seed Hunter folder/batch mode chains into overlapping pairs. Empty if no session yet.
        /// </summary>
        public string KeyframeOutputFolder
        {
            get
            {
                var jsonFileName = Path.GetFileNameWithoutExtension(PromptJsonFilePath);
                if (string.IsNullOrEmpty(jsonFileName)) return string.Empty;
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", OutputFolderName, jsonFileName);
            }
        }

        /// <summary>True when the keyframe output folder exists and holds at least 2 images (a pair).</summary>
        public bool HasKeyframeOutput
        {
            get
            {
                var folder = KeyframeOutputFolder;
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
                return Directory.EnumerateFiles(folder, "*.png").Take(2).Count() >= 2;
            }
        }

        // --- Variant-specific initialization ---

        protected override void InitializeVariant()
        {
            ToggleSettingsVisibilityCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ToggleSettingsVisibility);
            AnalyzeImageWithQwenVLCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(
                async () => await AnalyzeImageWithQwenVLAsync(),
                () => !string.IsNullOrEmpty(InputImagePath) && File.Exists(InputImagePath) && !IsAnalyzingImage);
            RefreshZLorasCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(RefreshZLoras);
            RefreshKreaLorasCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(RefreshKreaLoras);
            AddKreaLoraCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(AddKreaLora);
            RemoveKreaLoraCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<KreaLoraSelection>(RemoveKreaLora, (item) => item != null);
            SelectKrea2EditRefImageCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(SelectKrea2EditRefImage);
            LoadZWorkflowsAndStyles();
            LoadZAvailableLoras();
            LoadKreaLoras();
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

        private string _characterName = string.Empty;
        /// <summary>
        /// Optional character name. When set, the generated scenes refer to the active
        /// character by this name (e.g. "Tara") instead of a generic description ("a woman").
        /// </summary>
        public string CharacterName
        {
            get => _characterName;
            set => SetProperty(ref _characterName, value);
        }

        private string _characterClothing = string.Empty;
        /// <summary>
        /// Optional character clothing/outfit description. When set, the generated scenes
        /// keep the active character dressed in this same outfit across every scene, so the
        /// character's clothing stays consistent throughout the 10 generated images.
        /// </summary>
        public string CharacterClothing
        {
            get => _characterClothing;
            set => SetProperty(ref _characterClothing, value);
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
                var workflowDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "zimage");

                if (!Directory.Exists(workflowDir))
                {
                    AddLog($"ZStyles workflow directory not found at {workflowDir}");
                    OnPropertyChanged(nameof(ZStyleNames));
                    return;
                }

                // Recurse so styles organized into subfolders (4k-upscale/, simple/, base/) are found.
                foreach (var workflowFile in Directory.GetFiles(workflowDir, "*.json", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileNameWithoutExtension(workflowFile);

                    // Skip full workflows that aren't selectable style presets.
                    if (StyleInfo.IsNonStyleWorkflow(fileName))
                        continue;

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

        private void RefreshKreaLoras()
        {
            LoadKreaLoras();
            AddLog("Refreshed Krea2 LoRA list");
        }

        /// <summary>
        /// Applies the user's Krea2 LoRA selection(s) to the Power Lora Loader (rgthree) node 17
        /// in the krea2 realism workflow. Each enabled row becomes a numbered slot (lora_1, lora_2, …);
        /// the LoRA reference is the <loras>/krea2 relative path ComfyUI expects.
        /// </summary>
        private void ApplyKrea2Lora(JsonObject workflow)
        {
            if (workflow["17"] is not JsonObject node) return;
            if (node["inputs"] is not JsonObject inputs) return;

            // Preserve the slot's current lora name so the disabled fallback keeps a valid reference.
            var existingLoraName = $"{_kreaLoraSubfolder}/Krea2-realism-V1.safetensors";
            if (inputs["lora_1"] is JsonObject existingSlot && existingSlot["lora"] != null)
                existingLoraName = existingSlot["lora"]!.GetValue<string>();

            // Remove any pre-existing numbered slots so stale entries don't linger.
            foreach (var key in inputs.Select(kv => kv.Key).Where(k => k.StartsWith("lora_")).ToList())
                inputs.Remove(key);

            var valid = SelectedKreaLoras
                .Where(s => !string.IsNullOrEmpty(s.LoraName)
                            && s.LoraName != "No LoRAs available"
                            && s.LoraName != "Error loading LoRAs")
                .ToList();

            if (valid.Count == 0)
            {
                inputs["lora_1"] = new JsonObject { ["on"] = false, ["lora"] = existingLoraName, ["strength"] = 0.0 };
                AddLog("Krea2 LoRA: none");
                return;
            }

            for (int i = 0; i < valid.Count; i++)
            {
                inputs[$"lora_{i + 1}"] = new JsonObject
                {
                    ["on"] = true,
                    ["lora"] = $"{_kreaLoraSubfolder}/{valid[i].LoraName}.safetensors",
                    ["strength"] = valid[i].Strength
                };
            }

            AddLog($"Krea2 LoRAs: {string.Join(", ", valid.Select(v => $"{v.LoraName}@{v.Strength:0.##}"))}");
        }

        private string DefaultKreaLoraName()
        {
            var realism = KreaLoras.FirstOrDefault(l => l.IndexOf("realism", StringComparison.OrdinalIgnoreCase) >= 0);
            return realism ?? KreaLoras.FirstOrDefault(l => l != "No LoRAs available" && l != "Error loading LoRAs") ?? string.Empty;
        }

        private void AddKreaLora()
        {
            SelectedKreaLoras.Add(new KreaLoraSelection(DefaultKreaLoraName()));
        }

        private void RemoveKreaLora(KreaLoraSelection? item)
        {
            if (item != null)
                SelectedKreaLoras.Remove(item);
        }

        /// <summary>
        /// Resolves the folder that holds the Krea2 LoRAs. Prefers the explicit
        /// Settings → Krea2 LoRA Folder path (which points straight at the krea2 folder),
        /// then falls back to a "krea2"/"Krea2" subfolder of the general LoRA directory.
        /// </summary>
        private string? ResolveKreaLoraFolder()
        {
            var configured = _settingsService.Settings?.KreaLoraFolderPath;
            if (!string.IsNullOrEmpty(configured))
            {
                if (Directory.Exists(configured))
                {
                    AddLog($"Using configured Krea2 LoRA folder: {configured}");
                    return configured;
                }
                AddLog($"Configured Krea2 LoRA folder not accessible: {configured}");
            }

            var loraBasePath = GetLoraModelPath();
            if (!string.IsNullOrEmpty(loraBasePath))
            {
                foreach (var name in new[] { "krea2", "Krea2" })
                {
                    var candidate = Path.Combine(loraBasePath, name);
                    if (Directory.Exists(candidate)) return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Loads the Krea2 LoRAs from the configured/derived krea2 folder.
        /// These feed the Power Lora Loader (node 17) in the Krea2 realism workflow.
        /// </summary>
        private void LoadKreaLoras()
        {
            try
            {
                var kreaPath = ResolveKreaLoraFolder();

                _kreaLoras.Clear();

                if (kreaPath == null)
                {
                    AddLog("Krea2 LoRA folder not found (set it in Settings → Krea2 LoRA Folder, or place a krea2 subfolder in the LoRA directory)");
                    _kreaLoras.Add("No LoRAs available");
                    return;
                }

                _kreaLoraSubfolder = new DirectoryInfo(kreaPath).Name;

                var loraFiles = Directory.GetFiles(kreaPath, "*.safetensors")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .OrderBy(name => name)
                    .ToList();

                if (loraFiles.Any())
                {
                    foreach (var lora in loraFiles)
                        _kreaLoras.Add(lora!);

                    // Seed one default row on first load so the picker is never empty.
                    if (SelectedKreaLoras.Count == 0)
                        SelectedKreaLoras.Add(new KreaLoraSelection(DefaultKreaLoraName()));

                    AddLog($"Loaded {_kreaLoras.Count} Krea2 LoRAs from {kreaPath}");
                }
                else
                {
                    _kreaLoras.Add("No LoRAs available");
                    AddLog($"No Krea2 LoRA files found in {kreaPath}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error loading Krea2 LoRAs: {ex.Message}");
                _kreaLoras.Clear();
                _kreaLoras.Add("Error loading LoRAs");
            }
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

        /// <summary>
        /// Krea2 mode: text-to-image with the Krea2 Turbo workflow (no reference image).
        /// Mirrors the Klein/FireRed/Qwen output flow but skips the image upload.
        /// </summary>
        private async Task<string> ProcessKrea2QueueItemAsync(StoryPromptItem item, string jsonFileName, CancellationToken cancellationToken)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "krea", "krea2RealismV1_krea2RealismV1WF.json");
            AddLog("Using Krea2 workflow (Turbo, text-to-image)");

            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow file not found: {workflowPath}");

            var workflowJson = await File.ReadAllTextAsync(workflowPath, cancellationToken);

            // Node 6 - CLIPTextEncode (positive prompt)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "6", "text", item.Prompt);
            // Node 27 - ClownsharKSampler_Beta (seed only; turbo steps/cfg fixed in workflow)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "27", "seed", Random.Shared.NextInt64(0, long.MaxValue));
            // Node 10 - EmptyLatentImage: pin a fixed portrait size, overriding the workflow's
            // FluxResolutionNode link (which defaults to a slow 2.5 MP latent).
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "10", "width", 1024);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "10", "height", 1280);

            // Replace SaveImageKJ (node 23) with a standard SaveImage fed from the RTX-upscaled
            // image (node 28 - RTXVideoSuperResolution, 2×), drop the PreviewImage (node 5), and
            // apply the selected Krea2 LoRA(s) to the Power Lora Loader (node 17). SaveImageKJ does
            // not register its result in /history, so remote retrieval never finds it; standard
            // SaveImage does. Saves into the per-session subfolder like the other modes.
            var root = JsonNode.Parse(workflowJson);
            var obj = root?.AsObject();
            if (obj != null)
            {
                obj.Remove("5");
                obj["23"] = new JsonObject
                {
                    ["inputs"] = new JsonObject
                    {
                        ["filename_prefix"] = $"{jsonFileName}/{jsonFileName}-{item.Index}",
                        ["images"] = new JsonArray("28", 0)
                    },
                    ["class_type"] = "SaveImage",
                    ["_meta"] = new JsonObject { ["title"] = "Save Image (FlipPix)" }
                };

                ApplyKrea2Lora(obj);
            }
            var workflow = JsonSerializer.Deserialize<JsonElement>(obj?.ToJsonString() ?? workflowJson);

            cancellationToken.ThrowIfCancellationRequested();

            var progress = CreateProgressReporter(item);
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, cancellationToken);

            var outputImages = await GetOutputImagesFromComfyUI(promptId, jsonFileName, item.Index);
            if (!outputImages.Any())
                throw new InvalidOperationException("No output images were generated");

            var baseOutputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", OutputFolderName, jsonFileName);
            Directory.CreateDirectory(baseOutputDir);
            var outputPath = Path.Combine(baseOutputDir, $"{jsonFileName}-{item.Index}.png");

            await File.WriteAllBytesAsync(outputPath, outputImages.First());
            await LocalCopyService.CopyImageAsync(outputPath);
            AddLog($"Story Q (Krea2 mode) image #{item.Index} saved: {outputPath}");
            return outputPath;
        }

        /// <summary>
        /// krea2-edit mode: Krea2 two-reference identity edit. Image A (node 72) is this story
        /// item's input image — the scene being edited — and image B (node 86) is the single
        /// reference subject reused for the whole run, which keeps the character consistent
        /// across every keyframe. The workflow already carries its own SaveImage (node 29).
        /// </summary>
        private async Task<string> ProcessKrea2EditQueueItemAsync(StoryPromptItem item, string inputImagePath, string jsonFileName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(Krea2EditRefImagePath) || !File.Exists(Krea2EditRefImagePath))
                throw new InvalidOperationException("krea2-edit needs a reference image (image B). Pick one in the Krea2 Edit panel.");

            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "krea", "krea2_edit_two_ref.json");
            AddLog("Using Krea2 Edit workflow (two-reference identity edit)");

            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow file not found: {workflowPath}");

            var workflowJson = await File.ReadAllTextAsync(workflowPath, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var uploadedImageA = await _comfyUIService.UploadImageAsync(inputImagePath);
            var uploadedImageB = await _comfyUIService.UploadImageAsync(Krea2EditRefImagePath);

            // Node 72 - LoadImage (image A: scene to edit)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "72", "image", uploadedImageA);
            // Node 86 - LoadImage (image B: subject / identity reference)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "86", "image", uploadedImageB);
            // Node 84 - Krea2EditGroundedEncode (positive)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "84", "prompt", item.Prompt);
            // Node 85 - Krea2EditGroundedEncode (negative)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "85", "prompt", NegativePrompt ?? string.Empty);
            // Node 53 - KSampler (cfg/denoise stay at the turbo LoRA's fixed values)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "53", new Dictionary<string, object>
            {
                { "seed", Random.Shared.NextInt64(0, long.MaxValue) },
                { "steps", Steps }
            });
            // Node 29 - SaveImage: write into the per-session subfolder like the other modes
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "29", "filename_prefix", $"{jsonFileName}/{jsonFileName}-{item.Index}");

            var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

            var progress = CreateProgressReporter(item);
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, cancellationToken);

            var outputImages = await GetOutputImagesFromComfyUI(promptId, jsonFileName, item.Index);
            if (!outputImages.Any())
                throw new InvalidOperationException("No output images were generated");

            var baseOutputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", OutputFolderName, jsonFileName);
            Directory.CreateDirectory(baseOutputDir);
            var outputPath = Path.Combine(baseOutputDir, $"{jsonFileName}-{item.Index}.png");

            await File.WriteAllBytesAsync(outputPath, outputImages.First());
            await LocalCopyService.CopyImageAsync(outputPath);
            AddLog($"Story Q (krea2-edit mode) image #{item.Index} saved: {outputPath}");
            return outputPath;
        }

        private JsonElement UpdateZWorkflowParameters(JsonElement workflow, StoryPromptItem item)
        {
            // Detect workflow variant by checking for characteristic node IDs
            bool isZ4k = workflow.TryGetProperty("92", out var node92) &&
                         node92.TryGetProperty("class_type", out var ct92) &&
                         ct92.GetString() == "PrimitiveStringMultiline";

            if (isZ4k)
                return UpdateZ4kWorkflowParameters(workflow, item);

            // Standard ZStyle workflow (nodes 385, 60, 307, 9, 56)
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

        /// <summary>
        /// Z4k uses a two-pass pipeline (KSampler → PiD upscaling to 4096×4096)
        /// with completely different node IDs than the standard ZStyle template.
        /// </summary>
        private JsonElement UpdateZ4kWorkflowParameters(JsonElement workflow, StoryPromptItem item)
        {
            var workflowJson = workflow.GetRawText();

            // Node 92 - PrimitiveStringMultiline (prompt)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "92", "value", item.Prompt);

            // Resolution — Z4k has two hardcoded square latents that must follow the
            // selected orientation: node 68 (EmptySD3LatentImage, base pass) and node 84
            // (EmptyChromaRadianceLatentImage, PiD 4K pass that produces the saved image).
            var (baseW, baseH, k4W, k4H) = GetZ4kResolution(item.SelectedOrientation);
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "68", new Dictionary<string, object>
            {
                { "width", baseW }, { "height", baseH }
            });
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "84", new Dictionary<string, object>
            {
                { "width", k4W }, { "height", k4H }
            });
            AddLog($"Z4k resolution: base {baseW}x{baseH}, 4K {k4W}x{k4H} ({item.SelectedOrientation})");

            // Node 73 - CLIPTextEncode (negative prompt, has good default)
            if (!string.IsNullOrEmpty(item.NegativePrompt))
                WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "73", "text", item.NegativePrompt);

            // Seeds — two-pass pipeline needs both randomized
            var seed = Random.Shared.NextInt64(1, long.MaxValue);
            // Node 70 - KSampler seed (first pass: base generation)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "70", "seed", seed);
            // Node 75 - SamplerCustom noise_seed (second pass: PiD upscale)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "75", "noise_seed", seed);

            // Node 100 - SaveImage (output filename)
            var timestamp = DateTime.Now.ToString("yyyy_MM_dd");
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "100", "filename_prefix", $"ZImage/{timestamp}/ZI");

            // LoRA — Z4k uses LoraLoaderModelOnly, not Power Lora Loader
            workflowJson = UpdateZ4kLoraSettings(workflowJson, item);

            AddLog($"Z4k workflow: seed={seed}, LoRA enabled={item.LoraEnabled}");

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        /// <summary>
        /// Maps the selected orientation to Z4k's base-pass and 4K-pass dimensions.
        /// The PiD upscaler (node 82 + the fixed ManualSigmas schedule) is tuned for an
        /// exactly 4x linear upscale from a ~1 MP base, so every orientation keeps the
        /// 4K dims = 4x base and a ~1024 base long edge (square is the original 1024→4096).
        /// Changing the ratio or pushing the base higher softens the result.
        /// </summary>
        private static (int baseW, int baseH, int k4W, int k4H) GetZ4kResolution(string orientation) => orientation switch
        {
            "Landscape (1408x944)" => (1024, 688, 4096, 2752),
            "Square (1088x1088)" => (1024, 1024, 4096, 4096),
            _ => (688, 1024, 2752, 4096),   // Portrait (default)
        };

        private string UpdateZ4kLoraSettings(string workflowJson, StoryPromptItem item)
        {
            var root = JsonNode.Parse(workflowJson);
            if (root == null) return workflowJson;

            // Find LoraLoaderModelOnly node (node 99 in Z4k)
            foreach (var kvp in root.AsObject())
            {
                var node = kvp.Value?.AsObject();
                if (node == null) continue;
                if (node["class_type"]?.GetValue<string>() != "LoraLoaderModelOnly") continue;

                var inputs = node["inputs"]?.AsObject();
                if (inputs == null) break;

                if (item.LoraEnabled && !string.IsNullOrEmpty(item.SelectedLora))
                {
                    inputs["lora_name"] = JsonValue.Create($"zimage/{item.SelectedLora.Replace('\\', '/')}.safetensors");
                    inputs["strength_model"] = JsonValue.Create(item.LoraStrengthModel);
                    AddLog($"Z4k LoRA: {item.SelectedLora} (strength: {item.LoraStrengthModel:F2})");
                }
                else
                {
                    // Disable LoRA by setting strength to 0
                    inputs["strength_model"] = JsonValue.Create(0.0);
                    AddLog("Z4k LoRA: disabled (strength=0)");
                }
                break;
            }

            return root.ToJsonString();
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
                AnalysisStatus = $"Sending image to {_lmStudioService.DescribeTarget()}...";
                AddLog($"Starting image analysis — sending to {_lmStudioService.DescribeTarget()}");

                // Read system prompt from the selected template file
                var promptFileName = SelectedStoryPromptTemplate?.FileName ?? "story-prompt.md";
                var systemPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", promptFileName);
                if (!File.Exists(systemPromptPath))
                {
                    throw new FileNotFoundException($"System prompt file not found: {systemPromptPath}");
                }
                AddLog($"Using story prompt template: {SelectedStoryPromptTemplate?.DisplayName ?? promptFileName} ({promptFileName})");
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

                // Pass story concept + optional character name as the user message.
                // When a character name is supplied, instruct the model to refer to the active
                // character by that name in every scene instead of a generic description.
                var userParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(StoryConcept))
                    userParts.Add($"Story concept: {StoryConcept.Trim()}");
                if (!string.IsNullOrWhiteSpace(CharacterName))
                    userParts.Add($"The main character's name is \"{CharacterName.Trim()}\". " +
                        $"In every scene, refer to this character by name (\"{CharacterName.Trim()}\") " +
                        "instead of a generic description such as \"a woman\", \"the woman\", \"a man\", or \"the man\".");
                if (!string.IsNullOrWhiteSpace(CharacterClothing))
                    userParts.Add($"CLOTHING OVERRIDE: The main character wears \"{CharacterClothing.Trim()}\". " +
                        "Bake this exact outfit into the locked Character DNA string, replacing whatever clothing the image shows. " +
                        $"Write out \"{CharacterClothing.Trim()}\" (or a near-verbatim version of it) in FULL inside EVERY single scene prompt — " +
                        "all 8–10 scenes, not just the first. Do not summarize it as \"the same outfit\", do not drop it, and do not " +
                        "let the clothing drift or change between scenes unless the story explicitly requires a costume change.");
                var userPrompt = string.Join("\n\n", userParts);

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

            // Strategy 2: Split by "Prompt #N:" pattern. Tolerate markdown bold (**Prompt #1:**)
            // and an optional timestamp before the colon (e.g. "Prompt #1 (0s):").
            var promptPattern = @"\**\s*Prompt\s*#?\s*(\d+)\s*(?:\([^)]*\))?\s*:\s*\**";
            var promptMatches = Regex.Matches(analysisText, promptPattern, RegexOptions.IgnoreCase);

            for (int i = 0; i < promptMatches.Count; i++)
            {
                var startPos = promptMatches[i].Index + promptMatches[i].Length;
                var endPos = (i + 1 < promptMatches.Count) ? promptMatches[i + 1].Index : analysisText.Length;

                var promptText = analysisText.Substring(startPos, endPos - startPos).Trim();
                // Strip surrounding quotes and stray markdown bold the model may wrap content in
                promptText = promptText.Trim().Trim('*').Trim().Trim('"').Trim();

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

            if (SelectedWorkflowMode == "Krea2")
                return await ProcessKrea2QueueItemAsync(item, jsonFileName, cancellationToken);

            if (SelectedWorkflowMode == Krea2EditMode)
                return await ProcessKrea2EditQueueItemAsync(item, inputImagePath, jsonFileName, cancellationToken);

            string workflowPath;
            if (SelectedWorkflowMode == "Klein")
            {
                workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "klein", "V2-Edit-with-LCS-example-workflowAPI.json");
                AddLog("Using Klein workflow (Flux2 + LCS)");
            }
            else if (SelectedWorkflowMode == "FireRed")
            {
                workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "firered-image-edit-1.1API.json");
                AddLog("Using FireRed workflow");
            }
            else
            {
                workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "qwen-edit", "Qwen_Edit_2511_INT8_Convrot_WF.json");
                AddLog("Using Qwen workflow (Qwen-Image-Edit 2511 INT8 ConvRot)");
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

            // Qwen_Edit_2511_INT8_Convrot_WF.json node map. The "115:" ids are literal keys in
            // the API export (the graph was authored inside a subgraph), not a path expression.
            // Node 78 - LoadImage (the scene being edited)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "78", "image", inputImageName);
            // Node 115:111 - TextEncodeQwenImageEditPlus (positive)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "115:111", "prompt", promptText);
            // Node 115:110 - TextEncodeQwenImageEditPlus (negative)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "115:110", "prompt", NegativePrompt);
            // Node 115:3 - KSampler (8-step lightning LoRA is baked into the graph)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "115:3", new Dictionary<string, object>
            {
                { "seed", Random.Shared.NextInt64(0, long.MaxValue) },
                { "steps", Steps },
                { "cfg", Cfg },
                { "denoise", Denoise }
            });
            // Node 60 - SaveImage (fed from the 2x RTX upscale, node 115:124)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "60", "filename_prefix", $"{jsonFileName}/{jsonFileName}-{imageIndex}");

            // Drop the PreviewImage (115:116) and the unused EmptySD3LatentImage (115:112).
            // PreviewImage is an OUTPUT_NODE, so its temp file lands in /history next to the
            // saved image and the "any png from this prompt" fallback in
            // GetOutputImagesFromComfyUI would try to fetch it from the output folder.
            var root = JsonNode.Parse(workflowJson)?.AsObject();
            if (root != null)
            {
                root.Remove("115:116");
                root.Remove("115:112");
                workflowJson = root.ToJsonString();
            }

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
