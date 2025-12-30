using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using Microsoft.Win32;

namespace FlipPix.UI.ViewModels
{
    public class StoryVideoViewModel : INotifyPropertyChanged
    {
        private readonly ComfyUIService _comfyUIService;
        private readonly FlipPix.UI.Services.LMStudioService _lmStudioService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;

        private string _selectedImagePath = string.Empty;
        private BitmapImage? _selectedImageSource;
        private bool _hasSelectedImage = false;
        private string _customPrompt = "Act as a world-class fight director and choreographer. Analyze the uploaded image and create ten distinct, highly detailed prompts for 5-second action videos inspired by the scene. Each prompt must be descriptive, focusing on dynamic combat mechanics, cinematography, lighting, and atmosphere. Format: Return each prompt on a new line starting with 'Prompt #N:' followed by the full detailed prompt text in quotes. Example: Prompt #1: \"A dynamic close-up shot of...\" Prompt #2: \"Slow-motion capture of...\"";
        private string _generatedPrompt1 = string.Empty;
        private string _generatedPrompt2 = string.Empty;
        private string _generatedPrompt3 = string.Empty;
        private string _generatedPrompt4 = string.Empty;
        private string _generatedPrompt5 = string.Empty;
        private string _generatedPrompt6 = string.Empty;
        private string _generatedPrompt7 = string.Empty;
        private string _generatedPrompt8 = string.Empty;
        private string _generatedPrompt9 = string.Empty;
        private string _generatedPrompt10 = string.Empty;
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;
        private string _statusBarMessage = "Ready";
        private bool _hasGeneratedPrompts = false;
        private bool _hasResultVideo = false;
        private string _resultVideoPath = string.Empty;
        private System.Threading.CancellationTokenSource? _cancellationTokenSource;

        public event PropertyChangedEventHandler? PropertyChanged;

        public StoryVideoViewModel(ComfyUIService comfyUIService, FlipPix.UI.Services.LMStudioService lmStudioService, IAppLogger logger, FlipPix.Core.Services.SettingsService settingsService)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            // Initialize commands
            SelectImageCommand = new RelayCommand(SelectImage);
            AnalyzeImageCommand = new RelayCommand(async () => await AnalyzeImageAsync(), () => HasSelectedImage && !IsProcessing);
            GeneratePromptsCommand = new RelayCommand(async () => await GeneratePromptsAsync(), () => CanGeneratePrompts);
            GenerateVideoCommand = new RelayCommand(async () => await GenerateVideoAsync(), () => CanGenerateVideo);
            CancelGenerationCommand = new RelayCommand(CancelGeneration, () => IsProcessing);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResultVideo);
            SavePromptsCommand = new RelayCommand(SavePrompts);
            LoadPromptsCommand = new RelayCommand(LoadPrompts);

            AddLog("Story Video Generator initialized");
        }

        // Properties
        public string SelectedImagePath
        {
            get => _selectedImagePath;
            set
            {
                _selectedImagePath = value;
                OnPropertyChanged();
            }
        }

        public BitmapImage? SelectedImageSource
        {
            get => _selectedImageSource;
            set
            {
                _selectedImageSource = value;
                OnPropertyChanged();
            }
        }

        public bool HasSelectedImage
        {
            get => _hasSelectedImage;
            set
            {
                _hasSelectedImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGeneratePrompts));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public string CustomPrompt
        {
            get => _customPrompt;
            set
            {
                _customPrompt = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt1
        {
            get => _generatedPrompt1;
            set
            {
                _generatedPrompt1 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt2
        {
            get => _generatedPrompt2;
            set
            {
                _generatedPrompt2 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt3
        {
            get => _generatedPrompt3;
            set
            {
                _generatedPrompt3 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt4
        {
            get => _generatedPrompt4;
            set
            {
                _generatedPrompt4 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt5
        {
            get => _generatedPrompt5;
            set
            {
                _generatedPrompt5 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt6
        {
            get => _generatedPrompt6;
            set
            {
                _generatedPrompt6 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt7
        {
            get => _generatedPrompt7;
            set
            {
                _generatedPrompt7 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt8
        {
            get => _generatedPrompt8;
            set
            {
                _generatedPrompt8 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt9
        {
            get => _generatedPrompt9;
            set
            {
                _generatedPrompt9 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt10
        {
            get => _generatedPrompt10;
            set
            {
                _generatedPrompt10 = value;
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
                OnPropertyChanged(nameof(CanGeneratePrompts));
                OnPropertyChanged(nameof(CanGenerateVideo));
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

        public string LogOutput
        {
            get => _logOutput;
            set
            {
                _logOutput = value;
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

        public bool HasGeneratedPrompts
        {
            get => _hasGeneratedPrompts;
            set
            {
                _hasGeneratedPrompts = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerateVideo));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool HasResultVideo
        {
            get => _hasResultVideo;
            set
            {
                _hasResultVideo = value;
                OnPropertyChanged();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public string ResultVideoPath
        {
            get => _resultVideoPath;
            set
            {
                _resultVideoPath = value;
                OnPropertyChanged();
            }
        }

        // Image analysis properties
        private bool _isAnalyzing = false;
        private string _analysisStatus = string.Empty;
        private double _analysisProgress = 0;
        private string _imageAnalysis = string.Empty;

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

        public string AnalysisStatus
        {
            get => _analysisStatus;
            set
            {
                _analysisStatus = value;
                OnPropertyChanged();
            }
        }

        public double AnalysisProgress
        {
            get => _analysisProgress;
            set
            {
                _analysisProgress = value;
                OnPropertyChanged();
            }
        }

        public string ImageAnalysis
        {
            get => _imageAnalysis;
            set
            {
                _imageAnalysis = value;
                OnPropertyChanged();
            }
        }

        public bool HasAnalysis => !string.IsNullOrWhiteSpace(ImageAnalysis);

        public bool CanGeneratePrompts => HasSelectedImage && !IsProcessing;
        public bool CanGenerateVideo => HasGeneratedPrompts && !IsProcessing;

        // Commands
        public ICommand SelectImageCommand { get; }
        public ICommand AnalyzeImageCommand { get; }
        public ICommand GeneratePromptsCommand { get; }
        public ICommand GenerateVideoCommand { get; }
        public ICommand CancelGenerationCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand SavePromptsCommand { get; }
        public ICommand LoadPromptsCommand { get; }

        // Methods
        private void SelectImage()
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                Title = "Select an image"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                SelectedImagePath = openFileDialog.FileName;
                LoadImagePreview(openFileDialog.FileName);
                HasSelectedImage = true;
                AddLog($"Selected image: {Path.GetFileName(openFileDialog.FileName)}");
            }
        }

        private void LoadImagePreview(string imagePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                SelectedImageSource = bitmap;
                AddLog("Image preview loaded");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading image preview: {ex.Message}");
            }
        }

        private async Task AnalyzeImageAsync()
        {
            if (!HasSelectedImage)
            {
                AddLog("Cannot analyze: No image loaded");
                return;
            }

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new System.Threading.CancellationTokenSource();

            try
            {
                IsAnalyzing = true;
                AnalysisStatus = "Analyzing image with LM Studio Qwen-VL...";
                AnalysisProgress = 0;
                ImageAnalysis = "Analyzing image with LM Studio Qwen-VL AI...";

                AddLog("=== Starting image analysis with LM Studio Qwen-VL ===");

                // Get the selected model from settings
                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://localhost:1234";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);
                AddLog($"Using LM Studio at: {baseUrl}");

                // Get the selected model or try to find a qwen-vl model
                var models = await _lmStudioService.GetAvailableModelsAsync(_cancellationTokenSource.Token);
                string selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;

                if (string.IsNullOrEmpty(selectedModel))
                {
                    // Try to find qwen-vl model
                    var qwenModel = models.FirstOrDefault(m =>
                        m.Name.ToLower().Contains("qwen") && m.Name.ToLower().Contains("vl"));

                    if (qwenModel != null)
                    {
                        selectedModel = qwenModel.Name;
                        AddLog($"Auto-selected Qwen VL model: {selectedModel}");
                    }
                    else if (models.Any())
                    {
                        selectedModel = models.First().Name;
                        AddLog($"Using first available model: {selectedModel}");
                    }
                    else
                    {
                        throw new Exception("No models available in LM Studio. Please load a vision model like Qwen-VL.");
                    }
                }
                else
                {
                    AddLog($"Using configured model: {selectedModel}");
                }

                AnalysisStatus = "Analyzing with LM Studio Qwen-VL...";
                AnalysisProgress = 30;

                // Use LM Studio for image analysis
                var analysisPrompt = "Analyze this image in detail for story video generation. Describe the scene, characters, mood, atmosphere, and any key elements that would be useful for creating a compelling video story.";

                var analysisResult = await _lmStudioService.AnalyzeImageAsync(
                    selectedModel,
                    SelectedImagePath,
                    analysisPrompt,
                    maxTokens: 500,
                    _cancellationTokenSource.Token);

                AnalysisProgress = 90;
                AddLog("Analysis received from LM Studio");

                if (!string.IsNullOrEmpty(analysisResult))
                {
                    ImageAnalysis = analysisResult;
                    AnalysisStatus = "Analysis complete";
                    AnalysisProgress = 100;
                    AddLog("Image analysis completed successfully");
                    StatusBarMessage = "Image analysis complete - you can use this for story prompts";
                }
                else
                {
                    ImageAnalysis = "Analysis completed but no text was returned from LM Studio.";
                    AnalysisStatus = "Analysis complete (no output)";
                    AddLog("Analysis completed but no text output was detected");
                }
            }
            catch (OperationCanceledException)
            {
                IsAnalyzing = false;
                AnalysisStatus = "Cancelled";
                AddLog("Image analysis cancelled by user");
            }
            catch (Exception ex)
            {
                IsAnalyzing = false;
                AnalysisStatus = "Error";
                ImageAnalysis = $"Error analyzing image: {ex.Message}";
                AddLog($"ERROR analyzing image: {ex.Message}");
                System.Windows.MessageBox.Show($"Error analyzing image:\n\n{ex.Message}\n\nPlease ensure LM Studio is running and the Qwen-VL model is loaded.", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task GeneratePromptsAsync()
        {
            if (!CanGeneratePrompts) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new System.Threading.CancellationTokenSource();

            try
            {
                AddLog("=== Starting prompt generation with LM Studio ===");
                IsProcessing = true;
                ProcessingProgress = 0;
                ProcessingStatus = "Connecting to LM Studio...";

                // Get the selected model from settings
                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://localhost:1234";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);
                AddLog($"Using LM Studio at: {baseUrl}");

                ProcessingStatus = "Loading models...";
                ProcessingProgress = 10;

                // Get the selected model or try to find a qwen-vl model
                var models = await _lmStudioService.GetAvailableModelsAsync(_cancellationTokenSource.Token);
                string selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;

                if (string.IsNullOrEmpty(selectedModel))
                {
                    // Try to find qwen-vl model
                    var qwenModel = models.FirstOrDefault(m =>
                        m.Name.ToLower().Contains("qwen") && m.Name.ToLower().Contains("vl"));

                    if (qwenModel != null)
                    {
                        selectedModel = qwenModel.Name;
                        AddLog($"Auto-selected Qwen VL model: {selectedModel}");
                    }
                    else if (models.Any())
                    {
                        selectedModel = models.First().Name;
                        AddLog($"Using first available model: {selectedModel}");
                    }
                    else
                    {
                        throw new Exception("No models available in LM Studio. Please load a vision model like Qwen-VL.");
                    }
                }
                else
                {
                    AddLog($"Using configured model: {selectedModel}");
                }

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                ProcessingStatus = "Analyzing image and generating prompts...";
                ProcessingProgress = 20;
                AddLog("Sending image to LM Studio for prompt generation...");

                // Use LM Studio for prompt generation with higher token limit
                var generatedText = await _lmStudioService.AnalyzeImageAsync(
                    selectedModel,
                    SelectedImagePath,
                    CustomPrompt,
                    maxTokens: 2000,  // Increased token limit for 10 prompts
                    _cancellationTokenSource.Token);

                ProcessingProgress = 90;
                AddLog($"Generated text received ({generatedText.Length} characters)");

                if (!string.IsNullOrEmpty(generatedText))
                {
                    // Parse the generated text to extract 10 prompts
                    var prompts = ExtractPromptsFromText(generatedText);

                    if (prompts.Count >= 10)
                    {
                        GeneratedPrompt1 = prompts[0];
                        GeneratedPrompt2 = prompts[1];
                        GeneratedPrompt3 = prompts[2];
                        GeneratedPrompt4 = prompts[3];
                        GeneratedPrompt5 = prompts[4];
                        GeneratedPrompt6 = prompts[5];
                        GeneratedPrompt7 = prompts[6];
                        GeneratedPrompt8 = prompts[7];
                        GeneratedPrompt9 = prompts[8];
                        GeneratedPrompt10 = prompts[9];

                        HasGeneratedPrompts = true;
                        ProcessingProgress = 100;
                        ProcessingStatus = "Prompts generated successfully!";
                        StatusBarMessage = "10 prompts generated successfully";

                        AddLog("\n*** PROMPT GENERATION COMPLETE ***");
                        AddLog($"Successfully extracted {prompts.Count} prompts:");
                        for (int i = 0; i < prompts.Count; i++)
                        {
                            AddLog($"  {i + 1}. {prompts[i].Substring(0, Math.Min(60, prompts[i].Length))}...");
                        }
                        AddLog("*** Ready for video generation ***\n");
                    }
                    else
                    {
                        AddLog($"WARNING: Only extracted {prompts.Count} prompts, expected 10");
                        System.Windows.MessageBox.Show($"Only {prompts.Count} prompts were extracted. The model returned:\n\n{generatedText}\n\nPlease try again or adjust your custom prompt.", "Warning", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    }
                }
                else
                {
                    AddLog("WARNING: No generated text received from LM Studio");
                    System.Windows.MessageBox.Show("No text was generated. Please check LM Studio for errors.", "Warning", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("Prompt generation cancelled by user");
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

                _logger.LogError($"Error generating prompts: {ex}");
                ProcessingStatus = "Error occurred";
                ProcessingProgress = 0;

                System.Windows.MessageBox.Show(
                    $"Error generating prompts:\n\n{ex.Message}\n\nPlease ensure LM Studio is running and a Qwen-VL model is loaded.\n\nCheck the log for more details.",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
                AddLog("=== Prompt generation ended ===");
            }
        }

        private async Task GenerateVideoAsync()
        {
            if (!CanGenerateVideo) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new System.Threading.CancellationTokenSource();

            try
            {
                AddLog("=== Starting video generation ===");
                IsProcessing = true;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing video workflow...";

                // Check if ComfyUI has crashed and restart if needed
                ProcessingStatus = "Checking ComfyUI status...";
                AddLog("Checking if ComfyUI is running...");

                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddLog($"[Auto-Restart] {status}"),
                    _cancellationTokenSource.Token);

                if (!comfyUIOk)
                {
                    AddLog("ERROR: ComfyUI is not running and auto-restart failed or is disabled");
                    System.Windows.MessageBox.Show(
                        "ComfyUI is not running. Please start ComfyUI manually or configure auto-restart in settings.",
                        "ComfyUI Not Running",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }

                AddLog("ComfyUI is running and responsive");

                // Ensure ComfyUI is connected
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    AddLog("Connecting to ComfyUI WebSocket...");
                    await _comfyUIService.ConnectAsync(_cancellationTokenSource.Token);
                    AddLog("Connected to ComfyUI");
                }

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Load workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "SVI-Wan22-1207API.json");
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

                // Upload image to ComfyUI
                ProcessingStatus = "Uploading image...";
                ProcessingProgress = 5;
                AddLog("Uploading image to ComfyUI...");

                var imageFileName = await _comfyUIService.HttpClient.UploadImageAsync(SelectedImagePath);
                AddLog($"Image uploaded as: {imageFileName}");

                // Update workflow with parameters
                ProcessingStatus = "Updating video workflow with prompts...";
                ProcessingProgress = 10;

                var updatedWorkflow = UpdateVideoWorkflowParameters(workflow, imageFileName,
                    GeneratedPrompt1, GeneratedPrompt2, GeneratedPrompt3, GeneratedPrompt4, GeneratedPrompt5,
                    GeneratedPrompt6, GeneratedPrompt7, GeneratedPrompt8, GeneratedPrompt9, GeneratedPrompt10);

                // Execute workflow
                ProcessingStatus = "Generating video (this may take several minutes)...";
                ProcessingProgress = 20;
                AddLog("Executing video workflow in ComfyUI...");
                AddLog("NOTE: Video generation can take 10-30 minutes depending on your hardware");

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 20 + (percent * 0.7);
                            ProcessingStatus = $"Generating video: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, _cancellationTokenSource.Token);

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    ProcessingStatus = "Workflow completed, retrieving video...";
                });

                AddLog($"Workflow execution completed with prompt ID: {promptId}");

                // Get output video
                ProcessingStatus = "Retrieving output video...";
                ProcessingProgress = 95;
                AddLog("Looking for generated video...");

                // Wait for video to be written
                await Task.Delay(5000, _cancellationTokenSource.Token);

                var videoPath = GetOutputVideoFromComfyUI();
                if (!string.IsNullOrEmpty(videoPath))
                {
                    ResultVideoPath = videoPath;
                    HasResultVideo = true;
                    ProcessingProgress = 100;
                    ProcessingStatus = "Video generation complete!";
                    StatusBarMessage = $"Video generated - {Path.GetFileName(videoPath)}";
                    AddLog($"Output video saved: {videoPath}");
                }
                else
                {
                    AddLog("WARNING: No output video found");
                    System.Windows.MessageBox.Show("No output video was generated. Please check the ComfyUI console for errors.", "Warning", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("Video generation cancelled by user");
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

                _logger.LogError($"Error generating video: {ex}");
                ProcessingStatus = "Error occurred";
                ProcessingProgress = 0;

                System.Windows.MessageBox.Show(
                    $"Error generating video:\n\n{ex.Message}\n\nCheck the log for more details.",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
                AddLog("=== Video generation ended ===");
            }
        }

        private JsonElement UpdatePromptWorkflowParameters(JsonElement workflow, string imageFileName, string customPrompt)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());
            if (workflowDict == null) return workflow;

            // Update LoadImage node (node 1)
            if (workflowDict.ContainsKey("1"))
            {
                var node1 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["1"].GetRawText());
                if (node1 != null && node1.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node1["inputs"]));
                    if (inputs != null)
                    {
                        inputs["image"] = imageFileName;
                        node1["inputs"] = inputs;
                        workflowDict["1"] = JsonSerializer.SerializeToElement(node1);
                    }
                }
            }

            // Update QwenVL node (node 2) with custom prompt
            if (workflowDict.ContainsKey("2"))
            {
                var node2 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["2"].GetRawText());
                if (node2 != null && node2.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node2["inputs"]));
                    if (inputs != null)
                    {
                        inputs["custom_prompt"] = customPrompt;
                        node2["inputs"] = inputs;
                        workflowDict["2"] = JsonSerializer.SerializeToElement(node2);
                    }
                }
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private JsonElement UpdateVideoWorkflowParameters(JsonElement workflow, string imageFileName,
            string prompt1, string prompt2, string prompt3, string prompt4, string prompt5,
            string prompt6, string prompt7, string prompt8, string prompt9, string prompt10)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());
            if (workflowDict == null) return workflow;

            var negativePrompt = "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";

            // Debug: Log the prompts we're about to use
            AddLog("\n*** VIDEO GENERATION: PROMPT UPDATE START ***");
            AddLog($"Total prompts received: {new[] { prompt1, prompt2, prompt3, prompt4, prompt5, prompt6, prompt7, prompt8, prompt9, prompt10 }.Count(p => !string.IsNullOrEmpty(p))}");
            AddLog($"Prompt 1: {prompt1.Substring(0, Math.Min(80, prompt1.Length))}...");
            AddLog($"Prompt 2: {prompt2.Substring(0, Math.Min(80, prompt2.Length))}...");
            AddLog($"Prompt 3: {prompt3.Substring(0, Math.Min(80, prompt3.Length))}...");
            AddLog($"Prompt 4: {prompt4.Substring(0, Math.Min(80, prompt4.Length))}...");
            AddLog($"Prompt 5: {prompt5.Substring(0, Math.Min(80, prompt5.Length))}...");
            AddLog($"Prompt 6: {prompt6.Substring(0, Math.Min(80, prompt6.Length))}...");
            AddLog($"Prompt 7: {prompt7.Substring(0, Math.Min(80, prompt7.Length))}...");
            AddLog($"Prompt 8: {prompt8.Substring(0, Math.Min(80, prompt8.Length))}...");
            AddLog($"Prompt 9: {prompt9.Substring(0, Math.Min(80, prompt9.Length))}...");
            AddLog($"Prompt 10: {prompt10.Substring(0, Math.Min(80, prompt10.Length))}...");

            // Update LoadImage node (node 67)
            if (workflowDict.ContainsKey("67"))
            {
                var node67 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["67"].GetRawText());
                if (node67 != null && node67.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node67["inputs"]));
                    if (inputs != null)
                    {
                        inputs["image"] = imageFileName;
                        node67["inputs"] = inputs;
                        workflowDict["67"] = JsonSerializer.SerializeToElement(node67);
                    }
                }
            }

            AddLog("\n*** UPDATING WORKFLOW NODES ***");

            // Update the 10 WanVideoTextEncode nodes with our generated prompts
            AddLog("→ Updating Node 16 (Prompt 1)...");
            UpdateTextEncodeNode(workflowDict, "16", prompt1, negativePrompt);    // Node 16 - First prompt

            AddLog("→ Updating Node 140 (Prompt 2)...");
            UpdateTextEncodeNode(workflowDict, "140", prompt2, negativePrompt);   // Node 140 - Second prompt

            AddLog("→ Updating Node 184 (Prompt 3)...");
            UpdateTextEncodeNode(workflowDict, "184", prompt3, negativePrompt);   // Node 184 - Third prompt

            AddLog("→ Updating Node 211 (Prompt 4)...");
            UpdateTextEncodeNode(workflowDict, "211", prompt4, negativePrompt);   // Node 211 - Fourth prompt

            AddLog("→ Updating Node 231 (Prompt 5)...");
            UpdateTextEncodeNode(workflowDict, "231", prompt5, negativePrompt);   // Node 231 - Fifth prompt

            AddLog("→ Updating Node 257 (Prompt 6)...");
            UpdateTextEncodeNode(workflowDict, "257", prompt6, negativePrompt);   // Node 257 - Sixth prompt

            AddLog("→ Updating Node 281 (Prompt 7)...");
            UpdateTextEncodeNode(workflowDict, "281", prompt7, negativePrompt);   // Node 281 - Seventh prompt

            AddLog("→ Updating Node 303 (Prompt 8)...");
            UpdateTextEncodeNode(workflowDict, "303", prompt8, negativePrompt);   // Node 303 - Eighth prompt

            AddLog("→ Updating Node 329 (Prompt 9)...");
            UpdateTextEncodeNode(workflowDict, "329", prompt9, negativePrompt);   // Node 329 - Ninth prompt

            AddLog("→ Updating Node 353 (Prompt 10)...");
            UpdateTextEncodeNode(workflowDict, "353", prompt10, negativePrompt);  // Node 353 - Tenth prompt

            AddLog("*** ALL 10 NODES UPDATED SUCCESSFULLY ***\n");

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private void UpdateTextEncodeNode(Dictionary<string, JsonElement> workflowDict, string nodeId, string positivePrompt, string negativePrompt)
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
                        // Store the original prompt for comparison
                        var originalPrompt = inputs.ContainsKey("positive_prompt") ? inputs["positive_prompt"].ToString() : "[NOT SET]";

                        inputs["positive_prompt"] = positivePrompt;
                        inputs["negative_prompt"] = negativePrompt;
                        node["inputs"] = inputs;
                        workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);

                        // Only log if the prompt actually changed
                        if (originalPrompt != positivePrompt)
                        {
                            AddLog($"  ✓ Node {nodeId} - Prompt updated successfully");
                        }
                        else
                        {
                            AddLog($"  ⚠ Node {nodeId} - Prompt unchanged (may already be set)");
                        }
                    }
                }
                else
                {
                    AddLog($"  ✗ Node {nodeId} - ERROR: Invalid node structure");
                }
            }
            else
            {
                AddLog($"  ✗ Node {nodeId} - ERROR: Node not found in workflow!");
            }
        }

        private async Task<string> GetGeneratedTextFromHistory(string promptId)
        {
            try
            {
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                var historyUrl = $"{baseUrl}/history/{promptId}";

                AddLog($"Fetching history from: {historyUrl}");

                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var response = await httpClient.GetAsync(historyUrl);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var historyData = JsonSerializer.Deserialize<JsonElement>(content);

                    // Navigate through the JSON structure to find the ShowText output
                    if (historyData.TryGetProperty(promptId, out var promptData) &&
                        promptData.TryGetProperty("outputs", out var outputs))
                    {
                        // Look for node 3 (ShowText)
                        if (outputs.TryGetProperty("3", out var node3Output) &&
                            node3Output.TryGetProperty("text", out var textArray) &&
                            textArray.GetArrayLength() > 0)
                        {
                            return textArray[0].GetString() ?? string.Empty;
                        }
                    }
                }

                AddLog("Could not find generated text in history response");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR retrieving generated text: {ex.Message}");
            }

            return string.Empty;
        }

        private List<string> ExtractPromptsFromText(string generatedText)
        {
            var prompts = new List<string>();

            try
            {
                AddLog("\n*** PROMPT EXTRACTION START ***");
                AddLog($"Full text length: {generatedText.Length} characters");

                // Log the first 800 characters for debugging
                var preview = generatedText.Substring(0, Math.Min(800, generatedText.Length));
                AddLog($"Text preview: {preview}...");

                // Strategy 1: Split by "Prompt #N:" markers and extract content between them
                var promptSections = Regex.Split(generatedText, @"Prompt\s*#?\d+:\s*", RegexOptions.IgnoreCase)
                    .Skip(1) // Skip empty first element
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                if (promptSections.Count >= 5)
                {
                    AddLog($"Strategy 1: Found {promptSections.Count} 'Prompt #N:' sections");

                    foreach (var section in promptSections.Take(10))
                    {
                        // Extract quoted text if present
                        var quotedMatch = Regex.Match(section, "^\"([^\"]*(?:\"[^\"]*)*)\"");
                        string promptText;

                        if (quotedMatch.Success)
                        {
                            promptText = quotedMatch.Groups[1].Value;
                        }
                        else
                        {
                            // Take everything until the next newline or end, but clean it
                            var lines = section.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                            promptText = lines.FirstOrDefault()?.Trim() ?? section.Trim();
                        }

                        var cleaned = CleanPrompt(promptText);

                        if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Length > 20)
                        {
                            prompts.Add(cleaned);
                            AddLog($"  → Extracted prompt {prompts.Count}: {cleaned.Substring(0, Math.Min(60, cleaned.Length))}...");
                        }
                    }
                }

                // Strategy 2: Look for "Video Clip #N:" followed by description in parentheses
                if (prompts.Count < 10)
                {
                    var videoClipPattern = "Video Clip #(\\d+):\\s*\"([^\"]+)\"\\s*\\([^)]+\\)\\s*(.*?)(?=Video Clip #\\d+:|$)";
                    var matches = Regex.Matches(generatedText, videoClipPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);

                    if (matches.Count >= 5)
                    {
                        AddLog($"Strategy 2: Found {matches.Count} 'Video Clip' sections");

                        foreach (Match match in matches.Take(10 - prompts.Count))
                        {
                            var title = CleanPrompt(match.Groups[2].Value.Trim());
                            var description = CleanPrompt(match.Groups[3].Value.Trim());

                            var fullPrompt = string.IsNullOrWhiteSpace(description) ? title : $"{title}. {description}";

                            if (!string.IsNullOrWhiteSpace(fullPrompt) && fullPrompt.Length > 20)
                            {
                                prompts.Add(fullPrompt);
                                AddLog($"  → Extracted prompt {prompts.Count}: {fullPrompt.Substring(0, Math.Min(60, fullPrompt.Length))}...");
                            }
                        }
                    }
                }

                // Strategy 3: Look for lines starting with "Video Clip #" without quotes
                if (prompts.Count < 10)
                {
                    var lines = generatedText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => line.Trim())
                        .Where(line => line.StartsWith("Video Clip #", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (lines.Count > 0)
                    {
                        AddLog($"Strategy 3: Found {lines.Count} Video Clip lines");
                        foreach (var line in lines.Take(10 - prompts.Count))
                        {
                            // Extract the content after "Video Clip #N: " and before any time indication
                            var content = Regex.Replace(line, @"^Video Clip #\d+:\s*", "", RegexOptions.IgnoreCase);
                            content = Regex.Replace(content, @"\(.*?\d+s.*?\)", "").Trim();

                            if (!string.IsNullOrWhiteSpace(content) && content.Length > 20)
                            {
                                var cleaned = CleanPrompt(content);
                                prompts.Add(cleaned);
                                AddLog($"  → Line prompt {prompts.Count}: {cleaned.Substring(0, Math.Min(60, cleaned.Length))}...");
                            }
                        }
                    }
                }

                // Strategy 4: Extract numbered items with descriptions
                if (prompts.Count < 10)
                {
                    var numberedPattern = "^\\s*\\d+\\.\\s*(.*?)(?=^\\s*\\d+\\.|$)";
                    var matches = Regex.Matches(generatedText, numberedPattern, RegexOptions.Multiline | RegexOptions.Singleline);

                    if (matches.Count > 0)
                    {
                        AddLog($"Strategy 4: Found {matches.Count} numbered items");
                        foreach (Match match in matches.Take(10 - prompts.Count))
                        {
                            var content = CleanPrompt(match.Groups[1].Value.Trim());
                            if (!string.IsNullOrWhiteSpace(content) && content.Length > 30)
                            {
                                prompts.Add(content);
                                AddLog($"  → Numbered prompt {prompts.Count}: {content.Substring(0, Math.Min(60, content.Length))}...");
                            }
                        }
                    }
                }

                // Strategy 5: Extract descriptive sentences
                if (prompts.Count < 10)
                {
                    AddLog($"Strategy 5: Extracting sentences (need {10 - prompts.Count} more)");

                    var sentences = Regex.Split(generatedText, @"(?<=[.!?])\s+(?=[A-Z])")
                        .Where(s => !string.IsNullOrWhiteSpace(s) && s.Length > 50)
                        .Where(s => !s.StartsWith("Here") && !s.StartsWith("Each") && !s.StartsWith("Perfect"))
                        .Where(s => !s.Contains("perfect for") && !s.Contains("social media"))
                        .Take(15)
                        .ToList();

                    foreach (var sentence in sentences)
                    {
                        if (prompts.Count >= 10) break;

                        var cleaned = CleanPrompt(sentence);
                        if (cleaned.Length > 40)
                        {
                            prompts.Add(cleaned);
                            AddLog($"  → Sentence prompt {prompts.Count}: {cleaned.Substring(0, Math.Min(60, cleaned.Length))}...");
                        }
                    }
                }

                // Strategy 6: Fill remaining slots with variations
                if (prompts.Count > 0 && prompts.Count < 10)
                {
                    AddLog($"Strategy 6: Filling {10 - prompts.Count} missing slots");
                    var basePrompts = prompts.ToList();

                    for (int i = prompts.Count; i < 10; i++)
                    {
                        var basePrompt = basePrompts[i % basePrompts.Count];
                        var variation = $"{basePrompt} (Scene {i + 1})";
                        prompts.Add(variation);
                        AddLog($"  → Created variation {i + 1}");
                    }
                }

                AddLog($"\n*** EXTRACTION COMPLETE: {prompts.Count} prompts ***");
                for (int i = 0; i < Math.Min(prompts.Count, 10); i++)
                {
                    AddLog($"  {i + 1}. {prompts[i].Substring(0, Math.Min(80, prompts[i].Length))}...");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR extracting prompts: {ex.Message}");
            }

            return prompts;
        }

        private string CleanPrompt(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return string.Empty;

            // Remove asterisks and markdown formatting
            prompt = Regex.Replace(prompt, @"\*+", "");

            // Remove markdown headers
            prompt = Regex.Replace(prompt, @"^#{1,6}\s*", "", RegexOptions.Multiline);

            // Remove time indicators like "(0-5s)"
            prompt = Regex.Replace(prompt, @"\(?\d+[-–]?\d*\s*s\)?", "");
            prompt = Regex.Replace(prompt, @"\*\d+\s+seconds?\*", "");

            // Clean up dashes
            prompt = prompt.Replace("—", " - ").Replace("–", " - ");

            // Remove common prefixes - Add "Prompt #N:" pattern
            prompt = Regex.Replace(prompt, @"^Prompt\s*#?\d+:\s*", "", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            prompt = Regex.Replace(prompt, @"^Video Clip #\d+:\s*", "", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            prompt = Regex.Replace(prompt, @"^\d+\.\s*", "", RegexOptions.Multiline);
            prompt = Regex.Replace(prompt, @"^[🎬📖]\s*", "", RegexOptions.Multiline);

            // Remove quotes
            prompt = prompt.Replace("\"", "").Replace("'", "");

            // Remove sound/camera indicators
            prompt = Regex.Replace(prompt, @"SFX:[^.\n]*\.?", "");
            prompt = Regex.Replace(prompt, @"\*[^*]*Sound[^*]*\*", "");
            prompt = Regex.Replace(prompt, @"\*[^*]*Camera[^*]*\*", "");

            // Remove meta-commentary
            prompt = Regex.Replace(prompt, @"Absolutely - here are[^:]*:\s*", "", RegexOptions.IgnoreCase);
            prompt = Regex.Replace(prompt, @"Here are[^:]*:\s*", "", RegexOptions.IgnoreCase);
            prompt = Regex.Replace(prompt, @"Of course[^:]*:\s*", "", RegexOptions.IgnoreCase);

            // Clean up whitespace
            prompt = Regex.Replace(prompt, @"\s+", " ");
            prompt = prompt.Trim();

            // Remove trailing punctuation
            if (prompt.EndsWith(":") || prompt.EndsWith("."))
                prompt = prompt.TrimEnd(".:".ToCharArray());

            return prompt;
        }

        private string GetOutputVideoFromComfyUI()
        {
            try
            {
                var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                if (string.IsNullOrEmpty(comfyUIOutputDir))
                {
                    AddLog("ERROR: ComfyUI output folder not configured");
                    return string.Empty;
                }

                if (!Directory.Exists(comfyUIOutputDir))
                {
                    AddLog($"ERROR: ComfyUI output folder not found: {comfyUIOutputDir}");
                    return string.Empty;
                }

                // Look for WanVideo output files
                var videoFiles = Directory.GetFiles(comfyUIOutputDir, "WanVideo2_2_I2V_*.mp4")
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();

                if (videoFiles.Any())
                {
                    var latestFile = videoFiles.First();
                    var fileAge = DateTime.Now - File.GetLastWriteTime(latestFile);

                    // Only use files created in the last 10 minutes
                    if (fileAge.TotalMinutes < 10)
                    {
                        AddLog($"Found output video: {Path.GetFileName(latestFile)}");

                        // Copy to our output folder
                        var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "story-video");
                        Directory.CreateDirectory(outputDir);

                        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        var outputPath = Path.Combine(outputDir, $"story-video_{timestamp}.mp4");

                        File.Copy(latestFile, outputPath, true);
                        AddLog($"Video copied to: {outputPath}");

                        return outputPath;
                    }
                    else
                    {
                        AddLog($"Latest file is too old ({fileAge.TotalMinutes:F1} minutes), no new video found");
                    }
                }
                else
                {
                    AddLog("No WanVideo output files found");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR retrieving output video: {ex.Message}");
            }

            return string.Empty;
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
                var folder = Path.GetDirectoryName(ResultVideoPath);
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

        private void SavePrompts()
        {
            try
            {
                var saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "JSON Files|*.json|All Files|*.*",
                    Title = "Save Generated Prompts",
                    FileName = $"story-prompts_{DateTime.Now:yyyyMMdd_HHmmss}.json"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    var promptsData = new
                    {
                        CustomPrompt = CustomPrompt,
                        Prompts = new[]
                        {
                            GeneratedPrompt1, GeneratedPrompt2, GeneratedPrompt3, GeneratedPrompt4, GeneratedPrompt5,
                            GeneratedPrompt6, GeneratedPrompt7, GeneratedPrompt8, GeneratedPrompt9, GeneratedPrompt10
                        },
                        SavedAt = DateTime.Now,
                        Version = "1.0"
                    };

                    var json = JsonSerializer.Serialize(promptsData, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    File.WriteAllText(saveFileDialog.FileName, json);
                    AddLog($"Prompts saved to: {Path.GetFileName(saveFileDialog.FileName)}");
                    StatusBarMessage = "Prompts saved successfully";
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR saving prompts: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"Error saving prompts: {ex.Message}",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private void LoadPrompts()
        {
            try
            {
                var openFileDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "JSON Files|*.json|All Files|*.*",
                    Title = "Load Generated Prompts"
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    var json = File.ReadAllText(openFileDialog.FileName);
                    var data = JsonSerializer.Deserialize<JsonElement>(json);

                    // Load custom prompt if available
                    if (data.TryGetProperty("CustomPrompt", out var customPromptProp))
                    {
                        CustomPrompt = customPromptProp.GetString() ?? CustomPrompt;
                    }

                    // Load the 10 prompts
                    if (data.TryGetProperty("Prompts", out var promptsProp) && promptsProp.ValueKind == JsonValueKind.Array)
                    {
                        var prompts = promptsProp.EnumerateArray().ToList();

                        if (prompts.Count >= 10)
                        {
                            GeneratedPrompt1 = prompts[0].GetString() ?? string.Empty;
                            GeneratedPrompt2 = prompts[1].GetString() ?? string.Empty;
                            GeneratedPrompt3 = prompts[2].GetString() ?? string.Empty;
                            GeneratedPrompt4 = prompts[3].GetString() ?? string.Empty;
                            GeneratedPrompt5 = prompts[4].GetString() ?? string.Empty;
                            GeneratedPrompt6 = prompts[5].GetString() ?? string.Empty;
                            GeneratedPrompt7 = prompts[6].GetString() ?? string.Empty;
                            GeneratedPrompt8 = prompts[7].GetString() ?? string.Empty;
                            GeneratedPrompt9 = prompts[8].GetString() ?? string.Empty;
                            GeneratedPrompt10 = prompts[9].GetString() ?? string.Empty;

                            HasGeneratedPrompts = true;
                            AddLog($"Loaded 10 prompts from: {Path.GetFileName(openFileDialog.FileName)}");
                            StatusBarMessage = "10 prompts loaded successfully";

                            // Log the loaded prompts for verification
                            AddLog("\n*** LOADED PROMPTS VERIFICATION ***");
                            for (int i = 0; i < 10; i++)
                            {
                                var prompt = prompts[i].GetString() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(prompt))
                                {
                                    AddLog($"  {i + 1}. {prompt.Substring(0, Math.Min(60, prompt.Length))}...");
                                }
                                else
                                {
                                    AddLog($"  {i + 1}. [EMPTY PROMPT]");
                                }
                            }
                            AddLog("*** LOAD COMPLETE ***\n");
                        }
                        else
                        {
                            AddLog($"ERROR: Expected 10 prompts, but found {prompts.Count}");
                            System.Windows.MessageBox.Show(
                                $"Expected 10 prompts, but found {prompts.Count} in the file.",
                                "Error",
                                System.Windows.MessageBoxButton.OK,
                                System.Windows.MessageBoxImage.Error);
                        }
                    }
                    else
                    {
                        AddLog("ERROR: Invalid prompts file format - missing Prompts array");
                        System.Windows.MessageBox.Show(
                            "Invalid file format. Expected a JSON file with a 'Prompts' array.",
                            "Error",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading prompts: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"Error loading prompts: {ex.Message}",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogOutput += $"[{timestamp}] {message}\n";
            _logger.LogInfo(message);
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
