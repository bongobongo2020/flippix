using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ViewModel for SCAIL motion-transfer video generation.
    /// Uses the scail_4090_optimizedAPI.json workflow — no chunk processing.
    /// Supports analyzing source video frames + reference image via Qwen VL (llamaserver)
    /// to generate a synthesized motion prompt.
    /// </summary>
    public partial class MochaVideoViewModel : VideoProcessingBaseViewModel
    {
        // SCAIL properties
        private string _videoPath = string.Empty;
        private string _sourceVideoInfo = string.Empty;
        private string _imagePath = string.Empty;
        private BitmapImage? _imagePreview;
        private string _imageInfo = string.Empty;
        private string _prompt = string.Empty;
        private int _totalFrames = 0;
        private bool _isAnalyzing = false;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;

        public MochaVideoViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService,
            LMStudioService lmStudioService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));

            SelectVideoCommand = new RelayCommand(SelectVideo);
            SelectImageCommand = new RelayCommand(SelectImage);
            GenerateVideoCommand = new RelayCommand(async () => await GenerateVideoAsync(), () => CanGenerateVideo);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);
            AnalyzeImageCommand = new RelayCommand(async () => await AnalyzeImageAndVideoAsync(), () => CanAnalyzeImage);

            AddLog("SCAIL Video Generator initialized");
        }

        #region Commands

        public ICommand SelectVideoCommand { get; }
        public ICommand SelectImageCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }
        public RelayCommand AnalyzeImageCommand { get; }

        #endregion

        #region Properties

        public string VideoPath
        {
            get => _videoPath;
            set
            {
                if (_videoPath != value)
                {
                    _videoPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasVideo));
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    OnPropertyChanged(nameof(CanAnalyzeImage));
                    LoadVideoInfo();
                    OnCanExecuteChanged();
                }
            }
        }

        public string SourceVideoInfo
        {
            get => _sourceVideoInfo;
            set
            {
                if (_sourceVideoInfo != value) { _sourceVideoInfo = value; OnPropertyChanged(); }
            }
        }

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
            set { _imagePreview = value; OnPropertyChanged(); }
        }

        public string ImageInfo
        {
            get => _imageInfo;
            set
            {
                if (_imageInfo != value) { _imageInfo = value; OnPropertyChanged(); }
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
                    OnCanExecuteChanged();
                }
            }
        }

        public int TotalFrames
        {
            get => _totalFrames;
            set
            {
                if (_totalFrames != value) { _totalFrames = value; OnPropertyChanged(); }
            }
        }

        // Kept for XAML backward compat (binding MochaTotalChunks); always 0 since SCAIL doesn't chunk
        public int TotalChunks => 0;

        public bool HasVideo => !string.IsNullOrEmpty(VideoPath) && File.Exists(VideoPath);
        public bool HasImage => !string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath);
        public bool CanGenerateVideo => HasVideo && HasImage && !IsProcessing;

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
                    OnCanExecuteChanged();
                }
            }
        }

        public bool CanAnalyzeImage => HasVideo && HasImage && !IsAnalyzing && !IsProcessing;

        #endregion

        #region File Selection

        private async void SelectVideo()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Source Video",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.webm|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                VideoPath = filePath;

                var folderPath = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(folderPath) && _settingsService.Settings != null)
                {
                    _settingsService.Settings.VideoGeneratorImageFolder = folderPath;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }

                AddLog($"Selected source video: {Path.GetFileName(VideoPath)}");
            }
        }

        private async void SelectImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                ImagePath = filePath;
                AddLog($"Selected reference image: {Path.GetFileName(ImagePath)}");
            }
        }

        #endregion

        #region Preview / Info Loading

        private void LoadVideoInfo()
        {
            if (string.IsNullOrEmpty(VideoPath) || !File.Exists(VideoPath))
            {
                SourceVideoInfo = string.Empty;
                TotalFrames = 0;
                return;
            }

            try
            {
                var fileInfo = new FileInfo(VideoPath);
                var duration = GetVideoDuration(VideoPath);
                TotalFrames = GetVideoFrameCount(VideoPath);
                SourceVideoInfo = $"{fileInfo.Name} • {fileInfo.Length / 1024 / 1024:F1}MB • {duration:F1}s • {TotalFrames} frames";
                AddLog($"Video: {SourceVideoInfo}");
            }
            catch (Exception ex)
            {
                AddLog($"Error loading video info: {ex.Message}");
                SourceVideoInfo = "Error loading video info";
            }
        }

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

        #endregion

        #region Image / Video Analysis

        private async Task AnalyzeImageAndVideoAsync()
        {
            if (!CanAnalyzeImage) return;

            var tempFrameDir = string.Empty;

            try
            {
                IsAnalyzing = true;
                AddLog("=== Analyzing video motion + reference image with Qwen VL ===");

                var systemPromptPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "scail-prompt.md");

                if (!File.Exists(systemPromptPath))
                {
                    AddLog($"ERROR: SCAIL system prompt not found: {systemPromptPath}");
                    MessageBox.Show(
                        $"SCAIL system prompt not found:\n{systemPromptPath}",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var systemPrompt = await File.ReadAllTextAsync(systemPromptPath);
                AddLog($"System prompt loaded ({systemPrompt.Length} chars)");

                // Extract video frames
                var (frames, extractedDir) = await ExtractVideoFramesAsync(VideoPath, 6);
                tempFrameDir = extractedDir;
                AddLog($"Extracted {frames.Count} frame(s) from video");

                // Build image list: video frames first, reference image last
                var allImages = new List<string>(frames) { ImagePath };

                // Resolve model
                var models = await _lmStudioService.GetAvailableModelsAsync();
                var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(selectedModel) && models.Count > 0)
                {
                    var m = models.First();
                    selectedModel = !string.IsNullOrEmpty(m.Name) ? m.Name : m.Id;
                }

                if (string.IsNullOrEmpty(selectedModel))
                {
                    AddLog("ERROR: No LM Studio model available");
                    MessageBox.Show(
                        "No model available. Please ensure llamaserver is running and a model is loaded.",
                        "Model Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AddLog($"Sending {allImages.Count} image(s) to {selectedModel}...");

                var result = await _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                    selectedModel,
                    allImages,
                    "The first images are sequential frames from a source video (showing the motion). The last image is the reference image (showing the subject/scene to transfer the motion to). Analyze the motion and generate a synthesized video description.",
                    systemPrompt);

                Prompt = result;
                AddLog("Analysis complete. Prompt updated.");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR during analysis: {ex.Message}");
                MessageBox.Show(
                    $"Analysis failed:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;

                // Clean up temp frames
                if (!string.IsNullOrEmpty(tempFrameDir) && Directory.Exists(tempFrameDir))
                    try { Directory.Delete(tempFrameDir, recursive: true); } catch { }
            }
        }

        private async Task<(List<string> Frames, string TempDir)> ExtractVideoFramesAsync(string videoPath, int numFrames)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"scail_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            var frames = new List<string>();
            var ffmpegPath = FindFFmpeg();

            if (string.IsNullOrEmpty(ffmpegPath))
            {
                AddLog("WARNING: FFmpeg not found — sending reference image only (no video frames)");
                return (frames, tempDir);
            }

            var duration = GetVideoDuration(videoPath);
            if (duration <= 0) duration = 5;

            var interval = duration / (numFrames + 1);

            for (int i = 1; i <= numFrames; i++)
            {
                var timestamp = interval * i;
                var framePath = Path.Combine(tempDir, $"frame_{i:D3}.jpg");

                await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        // -y: auto-overwrite  -nostdin: don't prompt
                        Arguments = $"-y -nostdin -ss {timestamp:F3} -i \"{videoPath}\" -vframes 1 -q:v 3 \"{framePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                        // Do NOT redirect stdio — unread buffers cause FFmpeg to block
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(30000);
                });

                if (File.Exists(framePath))
                    frames.Add(framePath);
            }

            AddLog($"Extracted {frames.Count}/{numFrames} frames from video");
            return (frames, tempDir);
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
                MessageBox.Show(
                    $"An error occurred during SCAIL video generation:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task GenerateVideoAsyncInternal()
        {
            try
            {
                AddLog("=== Starting SCAIL video generation ===");
                IsProcessing = true;

                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing SCAIL workflow...";

                AddLog($"Source video: {Path.GetFileName(VideoPath)}");
                AddLog($"Reference image: {Path.GetFileName(ImagePath)}");
                if (!string.IsNullOrWhiteSpace(Prompt))
                    AddLog($"Prompt: {Prompt}");

                // Check ComfyUI
                ProcessingStatus = "Checking ComfyUI status...";
                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddLog($"[Auto-Restart] {status}"));

                if (!comfyUIOk)
                {
                    AddLog("ERROR: ComfyUI is not running");
                    MessageBox.Show(
                        "ComfyUI is not running. Please start ComfyUI or configure auto-restart in settings.",
                        "ComfyUI Not Running", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                    AddLog("Connected to ComfyUI");
                }

                // Load workflow
                var workflowPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "workflow", "scail_4090_optimizedAPI.json");

                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow not found: {workflowPath}");
                    MessageBox.Show(
                        $"SCAIL workflow file not found:\n{workflowPath}",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Upload assets
                ProcessingStatus = "Uploading video to ComfyUI...";
                ProcessingProgress = 10;
                AddLog("Uploading source video...");
                var uploadedVideoName = await _comfyUIService.UploadVideoAsync(VideoPath);
                if (string.IsNullOrEmpty(uploadedVideoName))
                {
                    AddLog("ERROR: Video upload failed");
                    MessageBox.Show("Failed to upload video to ComfyUI.", "Upload Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                AddLog($"Video uploaded: {uploadedVideoName}");

                ProcessingStatus = "Uploading reference image to ComfyUI...";
                ProcessingProgress = 15;
                AddLog("Uploading reference image...");
                var uploadedImageName = await _comfyUIService.UploadImageAsync(ImagePath);
                if (string.IsNullOrEmpty(uploadedImageName))
                {
                    AddLog("ERROR: Image upload failed");
                    MessageBox.Show("Failed to upload image to ComfyUI.", "Upload Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                AddLog($"Image uploaded: {uploadedImageName}");

                // Update and execute workflow
                var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedVideoName, uploadedImageName, Prompt);

                ProcessingStatus = "Executing SCAIL workflow...";
                ProcessingProgress = 20;

                var existingFiles = GetExistingVideoFiles("*.mp4");

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 20 + percent * 0.75;
                            ProcessingStatus = $"Generating: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                AddLog($"Workflow submitted, prompt ID: {promptId}");

                // Retrieve output
                ProcessingProgress = 95;
                ProcessingStatus = "Retrieving output video...";

                var outputVideo = await TryGetVideoFromHistoryAsync(promptId);
                if (outputVideo == null)
                {
                    AddLog("History API returned no result, polling filesystem...");
                    outputVideo = await WaitForNewVideoAsync(
                        existingFiles, "*.mp4",
                        TimeSpan.FromMinutes(15),
                        TimeSpan.FromSeconds(5));
                }

                if (outputVideo != null && File.Exists(outputVideo))
                {
                    var outputDir = Path.Combine(
                        _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(),
                        "SCAIL");
                    Directory.CreateDirectory(outputDir);

                    var finalPath = Path.Combine(outputDir, $"SCAIL_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                    File.Copy(outputVideo, finalPath, true);

                    ResultVideoPath = finalPath;
                    await LocalCopyService.CopyVideoAsync(finalPath);
                    HasResult = true;

                    var fi = new FileInfo(finalPath);
                    ResultVideoInfo = $"SCAIL Video • {fi.Length / 1024 / 1024:F1}MB";
                    ProcessingProgress = 100;
                    ProcessingStatus = "Complete!";
                    AddLog($"=== SCAIL generation complete: {finalPath} ===");
                }
                else
                {
                    AddLog("ERROR: No output video found");
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

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string videoName, string imageName, string prompt)
        {
            var workflowJson = workflow.GetRawText();

            // Node 47: VHS_LoadVideo — source video
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "47", "video", videoName);

            // Node 12: LoadImage — reference image
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "12", "image", imageName);

            // Node 6: CLIPTextEncode — prompt (only if provided)
            if (!string.IsNullOrWhiteSpace(prompt))
                WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "6", "text", prompt);

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        #endregion

        private void NotifyCommandsCanExecuteChanged()
        {
            GenerateVideoCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            SendToEditCameraCommand.NotifyCanExecuteChanged();
            AnalyzeImageCommand.NotifyCanExecuteChanged();
        }

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            NotifyCommandsCanExecuteChanged();
        }
    }
}
