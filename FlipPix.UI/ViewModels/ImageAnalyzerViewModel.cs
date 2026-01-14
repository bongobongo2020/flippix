using System;
using System.Collections.Generic;
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
using ComfyUIService = FlipPix.ComfyUI.Services.ComfyUIService;

namespace FlipPix.UI.ViewModels
{
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

        // Workflow parameters for amazing-z-image-a_GGUFAPI.json
        private string _negativePrompt = "";
        private int _width = 944;
        private int _height = 1408;
        private double _denoise = 1.0;
        private string _samplerName = "euler";
        private string _scheduler = "simple";
        private string _modelName = "z_image_turbo-Q8_0.gguf";
        private int _selectedPresetSizeIndex = 0;
        private int _selectedStyleIndex = 0;

        // Style presets for Z-Image workflow
        private Dictionary<string, string> _stylePresets = new Dictionary<string, string>
        {
            ["None"] = "{$@}",
            ["Phone Photo"] = "YOUR CONTEXT:\nYour photographs has android phone cam-quality.\nYour photographs exhibit {$spicy-content-with} surprising compositions, sharp complex backgrounds, natural lighting, and candid moments that feel immediate and authentic.\nYour photographs are actual gritty candid photographic background.\nYOUR PHOTO:\n{$@}",
            ["Cinematic"] = "Cinematic shot, professional photography, dramatic lighting, shallow depth of field, film grain, color grading, anamorphic lens flare, professional composition: {$@}",
            ["Anime"] = "Anime style, manga art, cel shading, vibrant colors, clean lines, studio Ghibli inspired, detailed illustration: {$@}",
            ["Oil Painting"] = "Oil painting style, classical art, brush strokes visible, rich textures, Renaissance inspired, museum quality: {$@}",
            ["3D Render"] = "3D render, Octane render, ray tracing, volumetric lighting, photorealistic CGI, unreal engine 5: {$@}",
            ["Watercolor"] = "Watercolor painting, soft edges, pastel colors, artistic flow, paper texture, traditional art medium: {$@}",
            ["Digital Art"] = "Digital art, concept art, trending on ArtStation, highly detailed, sharp focus, vibrant colors: {$@}",
            ["Vintage Photo"] = "Vintage photograph, film photography, kodachrome, grainy texture, aged paper, nostalgic atmosphere, 1970s style: {@}",
            ["Cyberpunk"] = "Cyberpunk aesthetic, neon lights, futuristic, sci-fi, high contrast, blade runner style, dystopian atmosphere: {$@}"
        };

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

            // Load ComfyUI settings
            if (_settingsService.Settings != null)
            {
                var uri = new Uri(_settingsService.Settings.BaseUrl);
                ComfyUIServer = uri.Host;
                ComfyUIPort = uri.Port.ToString();
            }

            // Initialize LM Studio
            InitializeLMStudio();

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
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

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

        public bool CanGenerate => HasSourceImage && !string.IsNullOrWhiteSpace(AnalysisText) && !IsGenerating && !IsAnalyzing;

        public int SelectedStyleIndex
        {
            get => _selectedStyleIndex;
            set
            {
                _selectedStyleIndex = value;
                OnPropertyChanged();
            }
        }

        public string[] StyleNames => _stylePresets.Keys.ToArray();

        // Commands
        public ICommand BrowseImageCommand { get; }
        public ICommand AnalyzeImageCommand { get; }
        public ICommand GenerateImageCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand TestLMStudioConnectionCommand { get; }
        public ICommand RefreshModelsCommand { get; }

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

        private async Task GenerateImageAsync()
        {
            if (!CanGenerate) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new System.Threading.CancellationTokenSource();

            try
            {
                _logger.LogInfo("=== Starting image generation with Z-Image ===");
                IsGenerating = true;

                // Clear previous result
                HasResultImage = false;
                ResultImageSource = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                StatusBarMessage = "Generating image...";
                _logger.LogInfo($"Using prompt: {AnalysisText}");

                // Ensure ComfyUI is connected
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync(_cancellationTokenSource.Token);
                }

                // Load workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "amazing-z-image-a_GGUFAPI.json");
                var workflowJson = await File.ReadAllTextAsync(workflowPath, _cancellationTokenSource.Token);
                var workflow = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson);

                if (workflow == null)
                {
                    _logger.LogError("Failed to parse workflow JSON");
                    return;
                }

                // Update workflow with generation parameters
                ProcessingStatus = "Configuring generation settings...";
                ProcessingProgress = 10;

                var updatedWorkflow = UpdateWorkflowForGeneration(workflow);

                // Execute workflow
                ProcessingStatus = "Generating image with Z-Image...";
                ProcessingProgress = 30;
                _logger.LogInfo("Executing Z-Image generation workflow...");

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

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    ProcessingStatus = "Workflow completed, retrieving output...";
                });

                _logger.LogInfo($"Workflow execution completed with prompt ID: {promptId}");

                // Retrieve output image
                ProcessingStatus = "Retrieving generated image...";
                ProcessingProgress = 95;

                // Give ComfyUI time to save the image
                _logger.LogInfo("Waiting for image to be saved...");
                await Task.Delay(3000, _cancellationTokenSource.Token);

                // Get the most recent image file directly (simpler and more reliable)
                _logger.LogInfo("Looking for most recently created image file...");
                List<byte[]> outputImages = new();
                int retryCount = 0;
                int maxRetries = 8;

                while (retryCount < maxRetries && !outputImages.Any())
                {
                    if (retryCount > 0)
                    {
                        _logger.LogInfo($"Retry {retryCount}/{maxRetries} - waiting 2 seconds...");
                        await Task.Delay(2000, _cancellationTokenSource.Token);
                    }

                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    outputImages = await GetMostRecentImageFromOutput();
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
                    var outputPath = Path.Combine(outputDir, $"z-image_{timestamp}.png");

                    await File.WriteAllBytesAsync(outputPath, outputImage);
                    _logger.LogInfo($"Output saved: {outputPath}");

                    ResultImagePath = outputPath;
                    LoadResultPreview(outputPath);
                    HasResultImage = true;

                    ProcessingProgress = 100;
                    ProcessingStatus = "Complete!";
                    StatusBarMessage = $"Image generated - {Path.GetFileName(outputPath)}";
                }
                else
                {
                    _logger.LogWarning("No output images received after all retries");
                    ProcessingStatus = "No output generated";
                    StatusBarMessage = "Generation failed - no output";
                    System.Windows.MessageBox.Show("No output images were generated. Please check the ComfyUI console for errors.", "Warning", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Image generation cancelled by user");
                ProcessingStatus = "Cancelled";
                ProcessingProgress = 0;
                StatusBarMessage = "Generation cancelled";
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error generating image: {ex}");
                ProcessingStatus = "Error occurred";
                ProcessingProgress = 0;
                StatusBarMessage = "Generation failed";
                System.Windows.MessageBox.Show($"Error generating image:\n\n{ex.Message}\n\nCheck the log for more details.", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsGenerating = false;
                _logger.LogInfo("=== Image generation ended ===");
            }
        }

        private Dictionary<string, object> UpdateWorkflowForGeneration(Dictionary<string, JsonElement> workflow)
        {
            var workflowDict = new Dictionary<string, object>();
            var selectedStyleName = StyleNames[Math.Min(SelectedStyleIndex, StyleNames.Length - 1)];
            var styleTemplate = _stylePresets[selectedStyleName];

            // Apply style template to the analysis text
            var styledPrompt = styleTemplate.Replace("{$@}", AnalysisText).Replace("{$spicy-content-with}", "");
            // Fix double spaces
            styledPrompt = System.Text.RegularExpressions.Regex.Replace(styledPrompt, @" +", " ");

            _logger.LogInfo($"Using style: {selectedStyleName}");
            _logger.LogInfo($"Styled prompt: {styledPrompt.Substring(0, Math.Min(200, styledPrompt.Length))}...");

            foreach (var kvp in workflow)
            {
                var nodeDict = JsonSerializer.Deserialize<Dictionary<string, object>>(kvp.Value.GetRawText());
                if (nodeDict != null)
                {
                    // Update node 6 (CLIPTextEncode) - directly set the styled prompt text
                    // This overrides the connection from node 166
                    if (kvp.Key == "6" && nodeDict.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(nodeDict["inputs"]));
                        if (inputs != null)
                        {
                            // Replace the connection with the actual text
                            inputs["text"] = styledPrompt;
                            nodeDict["inputs"] = inputs;
                            _logger.LogInfo($"Updated node 6 (CLIPTextEncode) with styled prompt");
                        }
                    }

                    // Update node 307 (SEED)
                    if (kvp.Key == "307" && nodeDict.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(nodeDict["inputs"]));
                        if (inputs != null)
                        {
                            var actualSeed = Seed == 0 ? new Random().NextInt64(0, 999999999999999) : Seed;
                            inputs["value"] = actualSeed;
                            nodeDict["inputs"] = inputs;
                            _logger.LogInfo($"Updated seed: {actualSeed}");
                        }
                    }

                    // Update node 244 (EmptySD3LatentImage) - width/height based on aspect ratio
                    if (kvp.Key == "244" && nodeDict.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(nodeDict["inputs"]));
                        if (inputs != null)
                        {
                            // Calculate dimensions based on aspect ratio
                            var dimensions = GetDimensionsForAspectRatio(AspectRatioIndex);
                            inputs["width"] = dimensions.Item1;
                            inputs["height"] = dimensions.Item2;
                            nodeDict["inputs"] = inputs;
                            _logger.LogInfo($"Updated dimensions: {dimensions.Item1}x{dimensions.Item2}");
                        }
                    }

                    // Update node 9 (SaveImage) filename_prefix with timestamp
                    if (kvp.Key == "9" && nodeDict.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(nodeDict["inputs"]));
                        if (inputs != null)
                        {
                            var timestamp = DateTime.Now.ToString("yyyy_MM_dd");
                            inputs["filename_prefix"] = $"ZImage/{timestamp}/ZI";
                            nodeDict["inputs"] = inputs;
                            _logger.LogInfo($"Updated output filename prefix");
                        }
                    }

                    workflowDict[kvp.Key] = nodeDict;
                }
            }

            return workflowDict;
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

        private async Task<List<byte[]>> GetMostRecentImageFromOutput()
        {
            var images = new List<byte[]>();

            try
            {
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;

                bool isRemoteComfyUI = IsComfyUIRemote(actualServer);
                _logger.LogInfo($"Getting most recent image from {(isRemoteComfyUI ? "remote" : "local")} ComfyUI");

                if (isRemoteComfyUI)
                {
                    _logger.LogInfo("Fetching file list from remote ComfyUI...");
                    var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
                    _logger.LogInfo($"Found {outputFiles.Count} total output files");

                    // Get ZI files (new format) or z-image files (old format)
                    var ziFiles = outputFiles.Where(f => (f.Contains("ZI") || f.Contains("z-image")) && f.EndsWith(".png")).ToList();

                    if (ziFiles.Any())
                    {
                        // Get the last one (they're typically already sorted by name which includes number)
                        var newestFile = ziFiles.Last();
                        _logger.LogInfo($"✓ Selected newest ZI file: {newestFile}");

                        var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(newestFile);
                        if (imageData != null)
                        {
                            images.Add(imageData);
                            _logger.LogInfo($"✓ Downloaded image ({imageData.Length} bytes)");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("No ZI files found in remote output");
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

                    // Get all ZI*.png files recursively (new format with ZImage subfolder)
                    var allZiFiles = Directory.GetFiles(comfyUIOutputDir, "ZI*.png", SearchOption.AllDirectories);
                    // Also get old z-image*.png files for backward compatibility
                    var oldZiFiles = Directory.GetFiles(comfyUIOutputDir, "z-image*.png", SearchOption.TopDirectoryOnly);
                    var allFiles = allZiFiles.Concat(oldZiFiles).ToArray();

                    _logger.LogInfo($"Found {allFiles.Length} total ZI/z-image files");

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
                        var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(specificFilename);
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

                    // Look for z-image files
                    var zImageFiles = outputFiles.Where(f => f.Contains("z-image") && f.EndsWith(".png")).ToList();
                    _logger.LogInfo($"Found {zImageFiles.Count} z-image files: {string.Join(", ", zImageFiles.Take(5))}");

                    if (zImageFiles.Any())
                    {
                        var filename = zImageFiles.Last();
                        _logger.LogInfo($"Downloading most recent: {filename}");
                        var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filename);
                        if (imageData != null)
                        {
                            images.Add(imageData);
                            _logger.LogInfo($"Successfully downloaded image ({imageData.Length} bytes)");
                        }
                        else
                        {
                            _logger.LogWarning($"Download returned null for {filename}");
                        }
                    }
                    else
                    {
                        // If no z-image files found, try to get ANY recent .png file
                        var pngFiles = outputFiles.Where(f => f.EndsWith(".png")).ToList();
                        _logger.LogInfo($"No z-image files found. Found {pngFiles.Count} total PNG files");

                        if (pngFiles.Any())
                        {
                            var filename = pngFiles.Last();
                            _logger.LogInfo($"Trying to download most recent PNG: {filename}");
                            var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filename);
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
    }
}
