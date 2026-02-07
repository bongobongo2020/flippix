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
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using YamlDotNet.Serialization;

namespace FlipPix.UI.ViewModels
{
    public class AmateurGeneratorViewModel : BasePromptViewModel
    {
        private readonly FlipPix.ComfyUI.Services.ComfyUIService _comfyUIService;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;

        private string _additionalPrompt = string.Empty;
        private int _orientationIndex = 0; // 0 = Landscape, 1 = Portrait
        private int _styleIndex = 0;
        private int _steps = 9;
        private double _cfg = 1.0;
        private long _seed = 0;
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;
        private bool _hasResultImage = false;
        private string _resultImagePath = string.Empty;
        private BitmapImage? _resultImageSource;
        private string _imageInfo = string.Empty;
        private System.Threading.CancellationTokenSource? _cancellationTokenSource;

        // Amateur LoRA is always enabled
        private const string AmateurLoraName = "amateur_photography_zimage_v1.safetensors";
        private const double AmateurLoraStrength1 = 0.4; // Node 105
        private const double AmateurLoraStrength2 = 0.9; // Node 752

        // Additional LoRA settings (optional)
        private ObservableCollection<string> _availableLoras = new();
        private string _selectedLora = string.Empty;
        private bool _loraEnabled = false;
        private double _loraStrength = 0.8;

        // Orientation and Style options
        private ObservableCollection<string> _orientations = new(new[] { "Landscape", "Portrait" });
        private ObservableCollection<string> _styles = new(new[] { "Natural", "Cinematic", "Dramatic", "Vintage", "Modern" });

        public AmateurGeneratorViewModel(
            FlipPix.ComfyUI.Services.ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IPromptService? promptService = null)
            : base(promptService ?? new PromptService(logger), logger, "AmateurGenerator")
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            // Initialize commands
            GenerateImageCommand = new RelayCommand(async () => await GenerateImageAsync(), () => CanGenerate);
            CancelGenerationCommand = new RelayCommand(CancelGeneration, () => IsProcessing);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResultImage);
            OpenResultImageCommand = new RelayCommand(OpenResultImage, () => HasResultImage);
            RefreshLorasCommand = new RelayCommand(RefreshLoras);
            PasteFromClipboardCommand = new RelayCommand(PasteFromClipboard);

            // Load available Loras
            LoadAvailableLoras();

            AddLog("Amateur Generator initialized");
        }

        // Properties
        public string AdditionalPrompt
        {
            get => _additionalPrompt;
            set
            {
                if (_additionalPrompt != value)
                {
                    _additionalPrompt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanGenerate));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public int OrientationIndex
        {
            get => _orientationIndex;
            set
            {
                if (_orientationIndex != value)
                {
                    _orientationIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        public int StyleIndex
        {
            get => _styleIndex;
            set
            {
                if (_styleIndex != value)
                {
                    _styleIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<string> Orientations => _orientations;
        public ObservableCollection<string> Styles => _styles;

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (_isProcessing != value)
                {
                    _isProcessing = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanGenerate));
                    OnPropertyChanged(nameof(CanCancel));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool CanCancel => IsProcessing;
        public bool CanGenerate => !IsProcessing;

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

        public bool HasResultImage
        {
            get => _hasResultImage;
            set
            {
                if (_hasResultImage != value)
                {
                    _hasResultImage = value;
                    OnPropertyChanged();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string ResultImagePath
        {
            get => _resultImagePath;
            set
            {
                if (_resultImagePath != value)
                {
                    _resultImagePath = value;
                    OnPropertyChanged();
                }
            }
        }

        public BitmapImage? ResultImageSource
        {
            get => _resultImageSource;
            set
            {
                if (_resultImageSource != value)
                {
                    _resultImageSource = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ImageInfo
        {
            get => _imageInfo;
            set
            {
                if (_imageInfo != value)
                {
                    _imageInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        // Additional LoRA Properties
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

        public double LoraStrength
        {
            get => _loraStrength;
            set
            {
                if (_loraStrength != value)
                {
                    _loraStrength = value;
                    OnPropertyChanged();
                }
            }
        }

        // Implementation of abstract BasePromptViewModel properties
        public override string CurrentPromptText => AdditionalPrompt;

        public override int AspectRatioIndex
        {
            get => OrientationIndex;
            set => OrientationIndex = value;
        }

        public override int Steps
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

        public override double Cfg
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

        public override long Seed
        {
            get => _seed;
            set
            {
                if (_seed != value)
                {
                    _seed = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _denoise = 1.0;
        public override double Denoise
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

        // Override base class methods
        protected override void OnPromptSaved(string promptName)
        {
            AddLog($"Prompt saved: {promptName}");
        }

        protected override void OnPromptDeleted(string promptName)
        {
            AddLog($"Prompt deleted: {promptName}");
        }

        protected override void OnPromptLoaded(SavedPrompt savedPrompt)
        {
            AdditionalPrompt = savedPrompt.Prompt;
            OrientationIndex = savedPrompt.AspectRatioIndex;
            Steps = savedPrompt.Steps;
            Cfg = savedPrompt.Cfg;
            Seed = savedPrompt.Seed;
            Denoise = savedPrompt.Denoise;

            // Load additional data if available
            if (savedPrompt.AdditionalData != null && savedPrompt.AdditionalData is Dictionary<string, object> additionalData)
            {
                if (additionalData.TryGetValue("StyleIndex", out var styleIndexObj) && styleIndexObj is int styleIndex)
                {
                    StyleIndex = styleIndex;
                }

                if (additionalData.TryGetValue("LoraEnabled", out var loraEnabledObj) && loraEnabledObj is bool loraEnabled)
                {
                    LoraEnabled = loraEnabled;
                }

                if (additionalData.TryGetValue("SelectedLora", out var selectedLoraObj) && selectedLoraObj is string selectedLora)
                {
                    SelectedLora = selectedLora;
                }

                if (additionalData.TryGetValue("LoraStrength", out var loraStrengthObj) && loraStrengthObj is double loraStrength)
                {
                    LoraStrength = loraStrength;
                }
            }

            AddLog($"Prompt loaded: {savedPrompt.Name}");
        }

        protected override void OnPromptError(string error)
        {
            AddLog($"ERROR: {error}");
        }

        public override Dictionary<string, object> GetAdditionalPromptData()
        {
            return new Dictionary<string, object>
            {
                { "StyleIndex", StyleIndex },
                { "LoraEnabled", LoraEnabled },
                { "SelectedLora", SelectedLora },
                { "LoraStrength", LoraStrength }
            };
        }

        // Commands
        public ICommand GenerateImageCommand { get; }
        public ICommand CancelGenerationCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand OpenResultImageCommand { get; }
        public ICommand RefreshLorasCommand { get; }
        public ICommand PasteFromClipboardCommand { get; }

        // Methods
        private async Task GenerateImageAsync()
        {
            if (!CanGenerate) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                AddLog("=== Starting Amateur image generation ===");
                IsProcessing = true;

                // Clear previous result
                HasResultImage = false;
                ResultImageSource = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"Additional Prompt: {AdditionalPrompt}");

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

                // Load workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "amateurZimageAPI.json");
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

                // Retry image retrieval with delays
                List<byte[]> outputImages = new();
                int retryCount = 0;
                int maxRetries = 20; // Wait up to 100 seconds

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
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "amateur-generator");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var outputPath = Path.Combine(outputDir, $"amateur_{timestamp}.png");

                    await File.WriteAllBytesAsync(outputPath, outputImage);
                    AddLog($"Output saved: {outputPath}");

                    ResultImagePath = outputPath;
                    LoadResultPreview(outputPath);
                    HasResultImage = true;

                    ProcessingProgress = 100;
                    ProcessingStatus = "Complete!";
                    AddLog("Image generation complete!");
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
                AddLog("=== Amateur image generation ended ===");
            }
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            // Build the full prompt with photographer prefix (duplicated as requested)
            const string photographerPrefix = "A photo taken by the photographer Deedeemegadoodo, raw, unedited, ";
            string styleSuffix = GetStyleSuffix();
            string fullPrompt = photographerPrefix + photographerPrefix + AdditionalPrompt + styleSuffix;

            // 1. Update positive prompt (node 6)
            if (workflowDict.ContainsKey("6"))
            {
                var node6 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["6"].GetRawText());
                if (node6 != null && node6.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node6["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = fullPrompt;
                        node6["inputs"] = inputs;
                        workflowDict["6"] = JsonSerializer.SerializeToElement(node6);
                    }
                }
            }

            // 2. Update negative prompt (node 7)
            if (workflowDict.ContainsKey("7"))
            {
                var node7 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["7"].GetRawText());
                if (node7 != null && node7.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node7["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = "";
                        node7["inputs"] = inputs;
                        workflowDict["7"] = JsonSerializer.SerializeToElement(node7);
                    }
                }
            }

            // 3. Update seed (node 28) - max value is 2^50 (1125899906842624)
            if (workflowDict.ContainsKey("28"))
            {
                var node28 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["28"].GetRawText());
                if (node28 != null && node28.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node28["inputs"]));
                    if (inputs != null)
                    {
                        long maxSeed = 1125899906842624;
                        var actualSeed = Seed == 0 ? new Random().NextInt64(0, maxSeed) : Seed;
                        inputs["seed"] = actualSeed;
                        node28["inputs"] = inputs;
                        workflowDict["28"] = JsonSerializer.SerializeToElement(node28);
                    }
                }
            }

            // 4. Update ClownsharKSampler settings (node 582)
            if (workflowDict.ContainsKey("582"))
            {
                var node582 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["582"].GetRawText());
                if (node582 != null && node582.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node582["inputs"]));
                    if (inputs != null)
                    {
                        inputs["denoise"] = 0.5;
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        node582["inputs"] = inputs;
                        workflowDict["582"] = JsonSerializer.SerializeToElement(node582);
                    }
                }
            }

            // 5. Update second ClownsharKSampler settings (node 620)
            if (workflowDict.ContainsKey("620"))
            {
                var node620 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["620"].GetRawText());
                if (node620 != null && node620.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node620["inputs"]));
                    if (inputs != null)
                    {
                        inputs["denoise"] = 0.3;
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        node620["inputs"] = inputs;
                        workflowDict["620"] = JsonSerializer.SerializeToElement(node620);
                    }
                }
            }

            // 6. Update KSampler settings (node 754)
            if (workflowDict.ContainsKey("754"))
            {
                var node754 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["754"].GetRawText());
                if (node754 != null && node754.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node754["inputs"]));
                    if (inputs != null)
                    {
                        inputs["denoise"] = 0.9;
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        node754["inputs"] = inputs;
                        workflowDict["754"] = JsonSerializer.SerializeToElement(node754);
                    }
                }
            }

            // 7. Update KSampler settings (node 768)
            if (workflowDict.ContainsKey("768"))
            {
                var node768 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["768"].GetRawText());
                if (node768 != null && node768.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node768["inputs"]));
                    if (inputs != null)
                    {
                        inputs["denoise"] = 1.0;
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        node768["inputs"] = inputs;
                        workflowDict["768"] = JsonSerializer.SerializeToElement(node768);
                    }
                }
            }

            // 8. Update Amateur LoRA strengths (always applied)
            UpdateLoraStrength(workflowDict, "105", AmateurLoraStrength1);
            UpdateLoraStrength(workflowDict, "752", AmateurLoraStrength2);

            // 9. Update character LoRA if enabled (node 760)
            if (LoraEnabled && !string.IsNullOrEmpty(SelectedLora) && SelectedLora != "No LoRAs available")
            {
                UpdateCharacterLora(workflowDict, "760", SelectedLora, LoraStrength);
            }

            // 10. Update latent image dimensions based on orientation
            bool isPortrait = OrientationIndex == 1;
            if (isPortrait)
            {
                // Portrait mode
                UpdateLatentDimensions(workflowDict, "46", 416, 576);
                UpdateLatentDimensions(workflowDict, "693", 208, 288);
                UpdateLatentDimensions(workflowDict, "758", 288, 208);
                UpdateLatentDimensions(workflowDict, "772", 1248, 1728);
            }
            else
            {
                // Landscape mode
                UpdateLatentDimensions(workflowDict, "46", 576, 416);
                UpdateLatentDimensions(workflowDict, "693", 208, 288);
                UpdateLatentDimensions(workflowDict, "758", 416, 576);
                UpdateLatentDimensions(workflowDict, "772", 1728, 1248);
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private string GetStyleSuffix()
        {
            return StyleIndex switch
            {
                0 => "", // Natural
                1 => ", cinematic lighting, dramatic shadows, professional photography",
                2 => ", dramatic lighting, high contrast, moody atmosphere",
                3 => ", vintage film look, grain, faded colors, nostalgic",
                4 => ", modern aesthetic, clean lines, vibrant colors",
                _ => ""
            };
        }

        private void UpdateLoraStrength(Dictionary<string, JsonElement> workflowDict, string nodeId, double strength)
        {
            if (workflowDict.ContainsKey(nodeId))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null && inputs.ContainsKey("strength_model"))
                    {
                        inputs["strength_model"] = strength;
                        node["inputs"] = inputs;
                        workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }
        }

        private void UpdateCharacterLora(Dictionary<string, JsonElement> workflowDict, string nodeId, string loraName, double strength)
        {
            if (workflowDict.ContainsKey(nodeId))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["lora_name"] = $"zimage\\{loraName}.safetensors";
                        inputs["strength_model"] = strength;
                        node["inputs"] = inputs;
                        workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }
        }

        private void UpdateLatentDimensions(Dictionary<string, JsonElement> workflowDict, string nodeId, int width, int height)
        {
            if (workflowDict.ContainsKey(nodeId))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = width;
                        inputs["height"] = height;
                        node["inputs"] = inputs;
                        workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                    }
                }
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

        private void PasteFromClipboard()
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    var clipboardText = System.Windows.Clipboard.GetText();
                    if (!string.IsNullOrEmpty(clipboardText))
                    {
                        AdditionalPrompt = clipboardText;
                        AddLog("Pasted content from clipboard");
                    }
                }
                else
                {
                    AddLog("Clipboard is empty or does not contain text");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR pasting from clipboard: {ex.Message}");
            }
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

            // Filter out amateur photography LoRA since it's always applied
            var loraFiles = Directory.GetFiles(loraPath, "*.safetensors")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name) &&
                               !name.Equals("amateur_photography_zimage_v1", StringComparison.OrdinalIgnoreCase))
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

        private void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogOutput += $"[{timestamp}] {message}\n";
            _logger.LogInfo(message);
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
