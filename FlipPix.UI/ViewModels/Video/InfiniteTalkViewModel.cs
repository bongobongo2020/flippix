using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ViewModel for Wan InfiniteTalk video generation.
    /// Handles image input, audio file, and generates video in 81-frame chunks synced to audio.
    /// </summary>
    public partial class InfiniteTalkViewModel : VideoProcessingBaseViewModel
    {
        // InfiniteTalk-specific properties
        private string _imagePath = string.Empty;
        private BitmapImage? _imagePreview;
        private string _imageInfo = string.Empty;
        private string _audioPath = string.Empty;
        private string _audioInfo = string.Empty;
        private string _prompt = string.Empty;
        private int _width = 640;
        private int _height = 640;
        private double _audioDuration = 0;
        private int _totalFrames = 0;
        private int _totalChunks = 0;
        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private bool _isAnalyzing = false;
        private string _analysisResult = string.Empty;
        private const int CHUNK_FRAMES = 81;
        private const int FPS = 25;

        public InfiniteTalkViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            LMStudioService lmStudioService,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            // Initialize commands
            SelectImageCommand = new RelayCommand(SelectImage);
            SelectAudioCommand = new RelayCommand(SelectAudio);
            GenerateVideoCommand = new RelayCommand(async () => await GenerateVideoAsync(), () => CanGenerateVideo);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);
            AnalyzeImageCommand  = new RelayCommand(async () => await AnalyzeImageWithLMStudioAsync(),  () => CanAnalyzeImage);
            EnhancePromptCommand = new RelayCommand(async () => await EnhancePromptWithLMStudioAsync(), () => CanEnhancePrompt);
            AddLog("InfiniteTalk Video Generator initialized");
        }

        #region Commands

        public ICommand SelectImageCommand { get; }
        public ICommand SelectAudioCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }
        public RelayCommand AnalyzeImageCommand  { get; }
        public RelayCommand EnhancePromptCommand { get; }

        #endregion

        #region Properties

        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (_imagePath != value)
                {
                    _imagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasImage));
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    OnPropertyChanged(nameof(CanAnalyzeImage));
                    LoadImagePreview();
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? ImagePreview
        {
            get => _imagePreview;
            set
            {
                _imagePreview = value;
                OnPropertyChanged();
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

        public string AudioPath
        {
            get => _audioPath;
            set
            {
                if (_audioPath != value)
                {
                    _audioPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasAudio));
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    LoadAudioInfo();
                    OnCanExecuteChanged();
                }
            }
        }

        public string AudioInfo
        {
            get => _audioInfo;
            set
            {
                if (_audioInfo != value)
                {
                    _audioInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Prompt
        {
            get => _prompt;
            set
            {
                if (_prompt != value)
                {
                    _prompt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    OnCanExecuteChanged();
                }
            }
        }

        public int Width
        {
            get => _width;
            set
            {
                if (_width != value)
                {
                    _width = value;
                    OnPropertyChanged();
                }
            }
        }

        public int Height
        {
            get => _height;
            set
            {
                if (_height != value)
                {
                    _height = value;
                    OnPropertyChanged();
                }
            }
        }

        public double AudioDuration
        {
            get => _audioDuration;
            set
            {
                if (Math.Abs(_audioDuration - value) > 0.01)
                {
                    _audioDuration = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(EstimatedDuration));
                    CalculateTotalFrames();
                }
            }
        }

        public int TotalFrames
        {
            get => _totalFrames;
            set
            {
                if (_totalFrames != value)
                {
                    _totalFrames = value;
                    OnPropertyChanged();
                }
            }
        }

        public int TotalChunks
        {
            get => _totalChunks;
            set
            {
                if (_totalChunks != value)
                {
                    _totalChunks = value;
                    OnPropertyChanged();
                }
            }
        }

        public string EstimatedDuration => AudioDuration > 0
            ? $"{AudioDuration:F1} seconds ({TotalFrames} frames at {FPS} FPS)"
            : "No audio loaded";

        public bool HasImage => !string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath);
        public bool HasAudio => !string.IsNullOrEmpty(AudioPath) && File.Exists(AudioPath);

        public bool CanGenerateVideo => HasImage && HasAudio &&
                                        !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing;

        #endregion

        #region LMStudio Analysis Properties

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (_isAnalyzing != value)
                {
                    _isAnalyzing = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanAnalyzeImage));
                    OnPropertyChanged(nameof(CanEnhancePrompt));
                    OnCanExecuteChanged();
                }
            }
        }

        public string AnalysisResult
        {
            get => _analysisResult;
            set
            {
                if (_analysisResult != value)
                {
                    _analysisResult = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasAnalysis));
                    OnPropertyChanged(nameof(CanEnhancePrompt));
                    OnCanExecuteChanged();
                }
            }
        }

        public bool HasAnalysis    => !string.IsNullOrWhiteSpace(AnalysisResult);
        public bool CanAnalyzeImage => HasImage && !IsAnalyzing && !IsProcessing;
        public bool CanEnhancePrompt => HasAnalysis && !IsAnalyzing;
        public bool ShowVideoPrompt { get; private set; } = false;

        #endregion

        #region File Selection Methods

        private async void SelectImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Source Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                initialDirectory,
                persistKey: "infinitetalk.image");

            if (filePath != null)
            {
                ImagePath = filePath;
                AddLog($"Selected image: {Path.GetFileName(ImagePath)}");
            }
        }

        private async void SelectAudio()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            }

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Audio File",
                "Audio Files|*.mp3;*.wav;*.ogg;*.flac;*.m4a|All Files|*.*",
                initialDirectory,
                persistKey: "infinitetalk.audio");

            if (filePath != null)
            {
                AudioPath = filePath;
                AddLog($"Selected audio: {Path.GetFileName(AudioPath)}");
            }
        }

        #endregion

        #region Preview/Info Loading Methods

        private void LoadImagePreview()
        {
            if (string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath))
            {
                ImagePreview = null;
                ImageInfo = string.Empty;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(ImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                ImagePreview = bitmap;

                var fileInfo = new FileInfo(ImagePath);
                ImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fileInfo.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                ImageInfo = "Error loading image";
            }
        }

        private void LoadAudioInfo()
        {
            if (string.IsNullOrEmpty(AudioPath) || !File.Exists(AudioPath))
            {
                AudioInfo = string.Empty;
                AudioDuration = 0;
                return;
            }

            try
            {
                var fileInfo = new FileInfo(AudioPath);
                AudioInfo = $"{fileInfo.Name} • {fileInfo.Length / 1024 / 1024:F1}MB";

                // Get audio duration using ffmpeg
                GetAudioDuration(AudioPath);
            }
            catch (Exception ex)
            {
                AddLog($"Error loading audio info: {ex.Message}");
                AudioInfo = "Error loading audio info";
            }
        }

        private void GetAudioDuration(string audioPath)
        {
            try
            {
                var ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    AddLog("ERROR: ffmpeg not found. Please install ffmpeg to use this feature.");
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-i \"{audioPath}\" -f null -",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return;
                    var errorOutput = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    // Parse duration from ffmpeg output
                    var match = Regex.Match(errorOutput, @"Duration: (\d+):(\d+):(\d+\.\d+)");
                    if (match.Success)
                    {
                        var hours = double.Parse(match.Groups[1].Value);
                        var minutes = double.Parse(match.Groups[2].Value);
                        var seconds = double.Parse(match.Groups[3].Value);
                        AudioDuration = hours * 3600 + minutes * 60 + seconds;
                        AddLog($"Audio duration: {AudioDuration:F2} seconds");
                    }
                    else
                    {
                        AddLog("Could not determine audio duration");
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error getting audio duration: {ex.Message}");
            }
        }

        private void CalculateTotalFrames()
        {
            TotalFrames = (int)(AudioDuration * FPS);
            TotalChunks = (int)Math.Ceiling((double)TotalFrames / CHUNK_FRAMES);
            AddLog($"Total frames: {TotalFrames}, Chunks: {TotalChunks}");
        }

        #endregion

        #region Video Generation

        private async Task GenerateVideoAsync()
        {
            if (!CanGenerateVideo) return;

            try
            {
                await GenerateVideoAsyncInternal();
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                System.Windows.MessageBox.Show($"An error occurred during InfiniteTalk video generation:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task GenerateVideoAsyncInternal()
        {
            try
            {
                AddLog("=== Starting InfiniteTalk video generation ===");
                IsProcessing = true;

                // Clear previous result
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"Source image: {Path.GetFileName(ImagePath)}");
                AddLog($"Audio file: {Path.GetFileName(AudioPath)}");
                AddLog($"Prompt: {Prompt}");
                AddLog($"Total frames: {TotalFrames} ({AudioDuration:F1} seconds at {FPS} FPS)");
                AddLog($"InfiniteTalk mode will process in {CHUNK_FRAMES}-frame windows internally");

                // Check if ComfyUI has crashed and restart if needed
                ProcessingStatus = "Checking ComfyUI status...";
                AddLog("Checking if ComfyUI is running...");

                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddLog($"[Auto-Restart] {status}"));

                if (!comfyUIOk)
                {
                    AddLog("ERROR: ComfyUI is not running and auto-restart failed or is disabled");
                    System.Windows.MessageBox.Show(
                        "ComfyUI is not running. Please start ComfyUI manually or configure auto-restart in settings.",
                        "ComfyUI Not Running",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                AddLog("ComfyUI is running and responsive");

                // Ensure ComfyUI is connected
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    AddLog("Connecting to ComfyUI WebSocket...");
                    await _comfyUIService.ConnectAsync();
                    AddLog("Connected to ComfyUI");
                }

                // Load InfiniteTalk workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "wanvideo_2_1_14B_I2V_InfiniteTalk_example_03API.json");

                AddLog($"Loading InfiniteTalk workflow");

                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    System.Windows.MessageBox.Show($"InfiniteTalk workflow file not found:\n{workflowPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Upload image and audio
                ProcessingStatus = "Uploading assets to ComfyUI...";
                ProcessingProgress = 5;
                AddLog("Uploading image to ComfyUI...");
                var uploadedImageName = await _comfyUIService.UploadImageAsync(ImagePath);
                if (string.IsNullOrEmpty(uploadedImageName))
                {
                    AddLog("ERROR: Image upload failed");
                    System.Windows.MessageBox.Show("Failed to upload image to ComfyUI.", "Upload Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                AddLog($"Image uploaded: {uploadedImageName}");

                AddLog("Uploading audio to ComfyUI...");
                var uploadedAudioName = await _comfyUIService.UploadAudioAsync(AudioPath);
                if (string.IsNullOrEmpty(uploadedAudioName))
                {
                    AddLog("ERROR: Audio upload failed");
                    System.Windows.MessageBox.Show("Failed to upload audio to ComfyUI.", "Upload Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                AddLog($"Audio uploaded: {uploadedAudioName}");

                // Update workflow with total frames (InfiniteTalk handles chunking internally)
                AddLog($"=== Generating video with {TotalFrames} total frames ===");
                ProcessingStatus = "Generating video...";
                ProcessingProgress = 10;

                var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedImageName, uploadedAudioName, TotalFrames);

                // Execute workflow
                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 10 + (percent * 0.85);
                            ProcessingStatus = $"Processing: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                AddLog($"Workflow completed, prompt ID: {promptId}");

                // Wait for output video
                ProcessingProgress = 95;
                ProcessingStatus = "Waiting for output video...";
                var existingFiles = GetExistingVideoFiles("WanVideo2_1_InfiniteTalk_*.mp4");
                var outputVideo = await WaitForNewVideoAsync(
                    existingFiles,
                    "WanVideo2_1_InfiniteTalk_*.mp4",
                    TimeSpan.FromMinutes(30),
                    TimeSpan.FromSeconds(5));

                if (outputVideo != null && File.Exists(outputVideo))
                {
                    // Save the output video
                    var outputPath = Path.Combine(_settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "InfiniteTalk");
                    Directory.CreateDirectory(outputPath);

                    var outputFileName = $"InfiniteTalk_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
                    var finalOutputPath = Path.Combine(outputPath, outputFileName);

                    File.Copy(outputVideo, finalOutputPath, true);
                    AddLog($"Video saved to: {finalOutputPath}");

                    ResultVideoPath = finalOutputPath;
                    await LocalCopyService.CopyVideoAsync(finalOutputPath);
                    HasResult = true;

                    var fileInfo = new FileInfo(finalOutputPath);
                    ResultVideoInfo = $"InfiniteTalk Video • {fileInfo.Length / 1024 / 1024:F1}MB";

                    ProcessingProgress = 100;
                    ProcessingStatus = "Complete!";

                    AddLog($"=== InfiniteTalk video generation completed ===");
                }
                else
                {
                    AddLog("ERROR: No video was generated");
                    ProcessingStatus = "No output generated";
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                AddLog($"Stack trace: {ex.StackTrace}");
                ProcessingStatus = "Error occurred";
                throw;
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string imageName, string audioName, int totalFrames)
        {
            var workflowJson = workflow.GetRawText();
            AddLog($"Updating workflow: Total frames {totalFrames}");

            // Update image (node 284)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "284", "image", imageName);

            // Update audio (node 125)
            try
            {
                var workflowObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson);
                if (workflowObj != null && workflowObj.ContainsKey("125"))
                {
                    var loadAudioNode = new
                    {
                        inputs = new
                        {
                            audio = audioName,
                            audioUI = ""
                        },
                        class_type = "LoadAudio",
                        _meta = new
                        {
                            title = "Load Audio"
                        }
                    };

                    workflowObj["125"] = JsonSerializer.SerializeToElement(loadAudioNode);
                    workflowJson = JsonSerializer.Serialize(workflowObj);
                    AddLog($"Node 125 (LoadAudio) updated: {audioName}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR updating node 125: {ex.Message}");
            }

            // Update prompt (node 241)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "241", "positive_prompt", Prompt);

            // Update max frames (node 270) - InfiniteTalk mode handles chunking internally with frame_window_size
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "270", "value", totalFrames);
            AddLog($"Node 270 (Max frames) set to: {totalFrames}");

            // Update width (node 245)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "245", "value", Width);

            // Update height (node 246)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "246", "value", Height);

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        #endregion

        #region LMStudio Analysis Methods

        private async Task AnalyzeImageWithLMStudioAsync()
        {
            if (!CanAnalyzeImage) return;
            try
            {
                IsAnalyzing = true;
                AddLog("=== Analyzing image with LMStudio ===");

                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);

                var models = await _lmStudioService.GetAvailableModelsAsync();
                var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(selectedModel))
                {
                    if (models.Count > 0)
                        selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;
                    else
                        throw new Exception("No models available in LM Studio. Please load a vision model.");
                }

                AddLog($"Using model: {selectedModel}");

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "image-analysis-prompt.md");
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"Prompt file not found: {promptFilePath}");

                var systemPromptContent = await File.ReadAllTextAsync(promptFilePath);

                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel,
                    ImagePath,
                    "Analyze this image.",
                    systemPromptContent);

                AnalysisResult = result;
                AddLog($"Image analysis complete ({result.Length} chars)");
                AddLog($"Preview: {(result.Length > 200 ? result.Substring(0, 200) + "..." : result)}");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR analyzing image: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"Image analysis failed:\n{ex.Message}",
                    "Analysis Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
            }
        }

        private async Task EnhancePromptWithLMStudioAsync()
        {
            if (!CanEnhancePrompt) return;
            try
            {
                IsAnalyzing = true;
                AddLog("=== Enhancing prompt with LMStudio (InfiniteTalk) ===");

                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);

                var models = await _lmStudioService.GetAvailableModelsAsync();
                var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(selectedModel))
                {
                    if (models.Count > 0)
                        selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;
                    else
                        throw new Exception("No models available in LM Studio.");
                }

                AddLog($"Using model: {selectedModel}");

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "ltx-audio-video.md");
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"Prompt file not found: {promptFilePath}");

                var systemPromptContent = await File.ReadAllTextAsync(promptFilePath);

                var enhancedPrompt = await _lmStudioService.SendTextChatAsync(
                    selectedModel,
                    systemPromptContent,
                    AnalysisResult,
                    maxTokens: 2000);

                Prompt = enhancedPrompt;
                ShowVideoPrompt = true;
                OnPropertyChanged(nameof(ShowVideoPrompt));
                AddLog($"Prompt enhanced ({enhancedPrompt.Length} chars)");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR enhancing prompt: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"Prompt enhancement failed:\n{ex.Message}",
                    "Enhancement Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
            }
        }

        #endregion

        private void NotifyCommandsCanExecuteChanged()
        {
            GenerateVideoCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            SendToEditCameraCommand.NotifyCanExecuteChanged();
            AnalyzeImageCommand.NotifyCanExecuteChanged();
            EnhancePromptCommand.NotifyCanExecuteChanged();
        }

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            NotifyCommandsCanExecuteChanged();
        }
    }
}
