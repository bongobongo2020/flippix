using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    public class AmateurGeneratorViewModel : BasePromptViewModel, IDisposable
    {
        private bool _disposed = false;
        private readonly FlipPix.ComfyUI.Services.ComfyUIService _comfyUIService;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly LoraManager _loraManager;
        private readonly ComfyUIImageRetriever _imageRetriever;

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

        // Static random for seed generation (better than creating new Random() each time)
        private static readonly Random _random = new Random();

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
            IPromptService? promptService = null,
            LoraManager? loraManager = null,
            ComfyUIImageRetriever? imageRetriever = null)
            : base(promptService ?? new PromptService(logger), logger, "AmateurGenerator")
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _loraManager = loraManager ?? new LoraManager(_settingsService, logger);
            _imageRetriever = imageRetriever ?? new ComfyUIImageRetriever();

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
                    await LocalCopyService.CopyImageAsync(outputPath);
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
            var workflowJson = workflow.GetRawText();

            // Build the full prompt with photographer prefix (duplicated as requested)
            const string photographerPrefix = "A photo taken by the photographer Deedeemegadoodo, raw, unedited, ";
            string styleSuffix = GetStyleSuffix();
            string fullPrompt = photographerPrefix + photographerPrefix + AdditionalPrompt + styleSuffix;

            // 1. Update positive prompt (node 6)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "6", "text", fullPrompt);

            // 2. Update negative prompt (node 7)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "7", "text", "");

            // 3. Update seed (node 28) - max value is 2^50 (1125899906842624)
            long maxSeed = 1125899906842624;
            var actualSeed = Seed == 0 ? _random.NextInt64(0, maxSeed) : Seed;
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "28", "seed", actualSeed);

            // Update the Seed property so user can see what seed was actually used (for reproducibility)
            if (Seed == 0)
            {
                Seed = actualSeed;
            }

            AddLog($"Using seed: {actualSeed}");

            // 4. Update ClownsharKSampler settings (node 582)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "582", new Dictionary<string, object>
            {
                { "denoise", 0.5 },
                { "steps", Steps },
                { "cfg", Cfg }
            });

            // 5. Update second ClownsharKSampler settings (node 620)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "620", new Dictionary<string, object>
            {
                { "denoise", 0.3 },
                { "steps", Steps },
                { "cfg", Cfg }
            });

            // 6. Update KSampler settings (node 754)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "754", new Dictionary<string, object>
            {
                { "denoise", 0.9 },
                { "steps", Steps },
                { "cfg", Cfg }
            });

            // 7. Update KSampler settings (node 768)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "768", new Dictionary<string, object>
            {
                { "denoise", 1.0 },
                { "steps", Steps },
                { "cfg", Cfg }
            });

            // 8. Update Amateur LoRA strengths (always applied)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "105", "strength_model", AmateurLoraStrength1);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "752", "strength_model", AmateurLoraStrength2);

            // 9. Update character LoRA (node 760) - always set a valid LoRA to avoid validation errors
            if (LoraEnabled && !string.IsNullOrEmpty(SelectedLora) && SelectedLora != "No LoRAs available")
            {
                var loraName = $"zimage\\{SelectedLora}.safetensors";
                AddLog($"Setting character LoRA: {loraName} with strength {LoraStrength}");
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "760", new Dictionary<string, object>
                {
                    { "lora_name", loraName },
                    { "strength_model", LoraStrength }
                });
            }
            else
            {
                // Use amateur LoRA with minimal strength as fallback (prevents invalid LoRA errors)
                AddLog($"Using fallback LoRA: zimage\\{AmateurLoraName} with strength 0.0");
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "760", new Dictionary<string, object>
                {
                    { "lora_name", $"zimage\\{AmateurLoraName}" },
                    { "strength_model", 0.0 }
                });
            }

            // 10. Remove problematic metadata/watermark nodes that reference non-existent image (nodes 107, 109, 747, 748, 749, 751)
            // These nodes cause file loading errors and aren't essential for image generation
            AddLog("Removing metadata and watermark nodes to prevent file loading errors");
            RemoveNodesFromWorkflow(ref workflowJson, new[] { "107", "109", "747", "748", "749", "751" });

            // 11. Update latent image dimensions based on aspect ratio orientation
            // Node 46 is the primary latent image used by KSampler node 754
            // Nodes 693, 758, 772 are for other purposes but should also match the aspect ratio
            int orientationIndex = OrientationIndex; // 0=Landscape, 1=Portrait, 2=Square
            AddLog($"Setting aspect ratio mode: {orientationIndex} (0=Land, 1=Port, 2=Square)");

            if (orientationIndex == 1)  // Portrait mode
            {
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "46", new Dictionary<string, object> { { "width", 416 }, { "height", 576 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "693", new Dictionary<string, object> { { "width", 208 }, { "height", 288 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "758", new Dictionary<string, object> { { "width", 288 }, { "height", 208 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "772", new Dictionary<string, object> { { "width", 1248 }, { "height", 1728 } });
                AddLog("Portrait dimensions: 416x576");
            }
            else if (orientationIndex == 2)  // Square mode
            {
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "46", new Dictionary<string, object> { { "width", 512 }, { "height", 512 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "693", new Dictionary<string, object> { { "width", 256 }, { "height", 256 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "758", new Dictionary<string, object> { { "width", 256 }, { "height", 256 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "772", new Dictionary<string, object> { { "width", 1536 }, { "height", 1536 } });
                AddLog("Square dimensions: 512x512");
            }
            else  // Landscape mode (default, index 0)
            {
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "46", new Dictionary<string, object> { { "width", 576 }, { "height", 416 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "693", new Dictionary<string, object> { { "width", 288 }, { "height", 208 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "758", new Dictionary<string, object> { { "width", 416 }, { "height", 576 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "772", new Dictionary<string, object> { { "width", 1728 }, { "height", 1248 } });
                AddLog("Landscape dimensions: 576x416");
            }

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
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

        private async Task<List<byte[]>> GetOutputImagesFromComfyUI(string promptId)
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

                        // The workflow saves to ZImage folder with "AmateurImage" prefix
                        var searchDirs = new List<string>();

                        // Prioritize ZImage folder
                        var zimageDir = Path.Combine(comfyUIOutputDir, "ZImage");
                        if (Directory.Exists(zimageDir))
                        {
                            searchDirs.Add(zimageDir);
                            AddLog("Added ZImage folder to search directories");
                        }

                        // Also check main output folder as fallback
                        searchDirs.Add(comfyUIOutputDir);

                        // Also check date folders as fallback
                        try
                        {
                            var dateFolders = Directory.GetDirectories(comfyUIOutputDir)
                                .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d{4}-\d{2}-\d{2}$"))
                                .OrderByDescending(d => Directory.GetLastWriteTime(d))
                                .Take(3);

                            foreach (var dateDir in dateFolders)
                            {
                                searchDirs.Add(dateDir);
                            }
                        }
                        catch { }

                        AddLog($"Searching in {searchDirs.Count} directories for output images");

                        foreach (var searchDir in searchDirs)
                        {
                            var dirName = Path.GetFileName(searchDir);
                            // Look for AmateurImage pattern in ZImage folder, or any recent PNG elsewhere
                            var pattern = dirName.Equals("ZImage", StringComparison.OrdinalIgnoreCase) ? "AmateurImage*.png" : "*.png";

                            var recentFiles = Directory.GetFiles(searchDir, pattern)
                                .Select(f => new FileInfo(f))
                                .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 2)
                                .OrderByDescending(f => f.LastWriteTime)
                                .ToList();

                            if (recentFiles.Any())
                            {
                                AddLog($"Found {recentFiles.Count} recent PNG files in: {dirName}");
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
                                    var dirName = Path.GetFileName(searchDir);
                                    var pattern = dirName.Equals("ZImage", StringComparison.OrdinalIgnoreCase) ? "AmateurImage*.png" : "*.png";

                                    var olderFiles = Directory.GetFiles(searchDir, pattern)
                                        .Select(f => new FileInfo(f))
                                        .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 10)
                                        .OrderByDescending(f => f.LastWriteTime)
                                        .ToList();

                                    if (olderFiles.Any())
                                    {
                                        AddLog($"Fallback: Found {olderFiles.Count} PNG files in last 10 minutes in: {dirName}");
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

        private void RefreshLoras()
        {
            LoadAvailableLoras();
            AddLog("Refreshed LoRA list");
        }

        private void RemoveNodesFromWorkflow(ref string workflowJson, string[] nodeIds)
        {
            try
            {
                var workflow = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson);
                if (workflow != null)
                {
                    foreach (var nodeId in nodeIds)
                    {
                        if (workflow.ContainsKey(nodeId))
                        {
                            workflow.Remove(nodeId);
                            AddLog($"Removed node {nodeId} from workflow");
                        }
                    }
                    workflowJson = JsonSerializer.Serialize(workflow, new JsonSerializerOptions { WriteIndented = false });
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR removing nodes from workflow: {ex.Message}");
            }
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
            return _loraManager.ResolveLoraPath();
        }

        private void LoadAvailableLoras()
        {
            try
            {
                // Get available LoRAs from LoraManager
                var allLoras = _loraManager.GetAvailableLoras();

                // Filter out amateur photography LoRA since it's always applied
                var filteredLoras = allLoras
                    .Where(name => !string.IsNullOrEmpty(name) &&
                                   !name.Equals("amateur_photography_zimage_v1", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(name => name)
                    .ToList();

                AvailableLoras.Clear();

                if (filteredLoras.Any())
                {
                    foreach (var lora in filteredLoras)
                    {
                        if (!string.IsNullOrEmpty(lora))
                            AvailableLoras.Add(lora);
                    }

                    if (string.IsNullOrEmpty(SelectedLora) && AvailableLoras.Any())
                    {
                        SelectedLora = AvailableLoras.First();
                    }

                    AddLog($"Loaded {AvailableLoras.Count} LoRAs");
                }
                else
                {
                    AvailableLoras.Add("No LoRAs available");
                    AddLog("No LoRA files found");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error loading LoRAs: {ex.Message}");
                AvailableLoras.Clear();
                AvailableLoras.Add("Error loading LoRAs");
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

                AvailableLoras.Clear();
                Orientations.Clear();
                Styles.Clear();

                _additionalPrompt = string.Empty;
                _processingStatus = string.Empty;
                _logOutput = string.Empty;
                _resultImagePath = string.Empty;
                _imageInfo = string.Empty;

                _disposed = true;
            }
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
