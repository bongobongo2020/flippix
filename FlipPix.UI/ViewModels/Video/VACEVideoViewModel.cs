using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
    /// ViewModel for VACE (Video-to-Video with Control) video generation.
    /// Handles background/foreground images, input video, and VACE prompt.
    /// </summary>
    public partial class VACEVideoViewModel : VideoProcessingBaseViewModel
    {
        // VACE-specific properties
        private string _prompt = string.Empty;
        private string _backgroundImagePath = string.Empty;
        private BitmapImage? _backgroundImagePreview;
        private string _backgroundImageInfo = string.Empty;
        private string _foregroundImagePath = string.Empty;
        private BitmapImage? _foregroundImagePreview;
        private string _foregroundImageInfo = string.Empty;
        private string _inputVideoPath = string.Empty;
        private string _inputVideoInfo = string.Empty;
        private readonly IFileDialogService _fileDialogService;

        public VACEVideoViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            // Initialize commands
            SelectBackgroundImageCommand = new RelayCommand(SelectBackgroundImage);
            SelectForegroundImageCommand = new RelayCommand(SelectForegroundImage);
            SelectVideoCommand = new RelayCommand(SelectVideo);
            GenerateVideoCommand = new RelayCommand(async () => await GenerateVideoAsync(), () => CanGenerateVideo);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);

            AddLog("VACE Video Generator initialized");
        }

        #region Commands

        public ICommand SelectBackgroundImageCommand { get; }
        public ICommand SelectForegroundImageCommand { get; }
        public ICommand SelectVideoCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }

        #endregion

        #region Properties

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

        public string BackgroundImagePath
        {
            get => _backgroundImagePath;
            set
            {
                if (_backgroundImagePath != value)
                {
                    _backgroundImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasBackgroundImage));
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    LoadBackgroundImagePreview();
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? BackgroundImagePreview
        {
            get => _backgroundImagePreview;
            set
            {
                _backgroundImagePreview = value;
                OnPropertyChanged();
            }
        }

        public string BackgroundImageInfo
        {
            get => _backgroundImageInfo;
            set
            {
                if (_backgroundImageInfo != value)
                {
                    _backgroundImageInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ForegroundImagePath
        {
            get => _foregroundImagePath;
            set
            {
                if (_foregroundImagePath != value)
                {
                    _foregroundImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasForegroundImage));
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    LoadForegroundImagePreview();
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? ForegroundImagePreview
        {
            get => _foregroundImagePreview;
            set
            {
                _foregroundImagePreview = value;
                OnPropertyChanged();
            }
        }

        public string ForegroundImageInfo
        {
            get => _foregroundImageInfo;
            set
            {
                if (_foregroundImageInfo != value)
                {
                    _foregroundImageInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public string InputVideoPath
        {
            get => _inputVideoPath;
            set
            {
                if (_inputVideoPath != value)
                {
                    _inputVideoPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasInputVideo));
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    LoadVideoInfo();
                    OnCanExecuteChanged();
                }
            }
        }

        public string InputVideoInfo
        {
            get => _inputVideoInfo;
            set
            {
                if (_inputVideoInfo != value)
                {
                    _inputVideoInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasBackgroundImage => !string.IsNullOrEmpty(BackgroundImagePath) && File.Exists(BackgroundImagePath);
        public bool HasForegroundImage => !string.IsNullOrEmpty(ForegroundImagePath) && File.Exists(ForegroundImagePath);
        public bool HasInputVideo => !string.IsNullOrEmpty(InputVideoPath) && File.Exists(InputVideoPath);

        public bool CanGenerateVideo => HasBackgroundImage && HasForegroundImage && HasInputVideo &&
                                        !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing;

        #endregion

        #region File Selection Methods

        private async void SelectBackgroundImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Background Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                BackgroundImagePath = filePath;
                AddLog($"VACE: Selected background image: {Path.GetFileName(BackgroundImagePath)}");
            }
        }

        private async void SelectForegroundImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Foreground Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                ForegroundImagePath = filePath;
                AddLog($"VACE: Selected foreground image: {Path.GetFileName(ForegroundImagePath)}");
            }
        }

        private async void SelectVideo()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            }

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Input Video",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                InputVideoPath = filePath;
                AddLog($"VACE: Selected video: {Path.GetFileName(InputVideoPath)}");
            }
        }

        #endregion

        #region Preview Loading Methods

        private void LoadBackgroundImagePreview()
        {
            if (string.IsNullOrEmpty(BackgroundImagePath) || !File.Exists(BackgroundImagePath))
            {
                BackgroundImagePreview = null;
                BackgroundImageInfo = string.Empty;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(BackgroundImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                BackgroundImagePreview = bitmap;

                var fileInfo = new FileInfo(BackgroundImagePath);
                BackgroundImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fileInfo.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading background image preview: {ex.Message}");
                BackgroundImageInfo = "Error loading image";
            }
        }

        private void LoadForegroundImagePreview()
        {
            if (string.IsNullOrEmpty(ForegroundImagePath) || !File.Exists(ForegroundImagePath))
            {
                ForegroundImagePreview = null;
                ForegroundImageInfo = string.Empty;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(ForegroundImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                ForegroundImagePreview = bitmap;

                var fileInfo = new FileInfo(ForegroundImagePath);
                ForegroundImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fileInfo.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading foreground image preview: {ex.Message}");
                ForegroundImageInfo = "Error loading image";
            }
        }

        private void LoadVideoInfo()
        {
            if (string.IsNullOrEmpty(InputVideoPath) || !File.Exists(InputVideoPath))
            {
                InputVideoInfo = string.Empty;
                return;
            }

            try
            {
                var fileInfo = new FileInfo(InputVideoPath);
                InputVideoInfo = $"{fileInfo.Name} • {fileInfo.Length / 1024 / 1024:F1}MB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading video info: {ex.Message}");
                InputVideoInfo = "Error loading video info";
            }
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
                System.Windows.MessageBox.Show($"An error occurred during VACE video generation:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task GenerateVideoAsyncInternal()
        {
            try
            {
                AddLog("=== Starting VACE video generation ===");
                IsProcessing = true;

                // Clear previous result
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing VACE workflow...";
                AddLog($"Background image: {Path.GetFileName(BackgroundImagePath)}");
                AddLog($"Foreground image: {Path.GetFileName(ForegroundImagePath)}");
                AddLog($"Input video: {Path.GetFileName(InputVideoPath)}");
                AddLog($"Prompt: {Prompt}");

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
                else
                {
                    AddLog("ComfyUI already connected");
                }

                // Load VACE workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "step1-chunkcreatorAPI.json");

                AddLog($"Loading VACE workflow: step1-chunkcreatorAPI.json");

                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    System.Windows.MessageBox.Show($"VACE workflow file not found:\n{workflowPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Upload images and video
                ProcessingStatus = "Uploading assets to ComfyUI...";
                ProcessingProgress = 10;
                AddLog("Uploading background image to ComfyUI...");
                var uploadedBgImageName = await _comfyUIService.UploadImageAsync(BackgroundImagePath);
                if (string.IsNullOrEmpty(uploadedBgImageName))
                {
                    AddLog("ERROR: Background image upload failed");
                    System.Windows.MessageBox.Show("Failed to upload background image to ComfyUI.", "Upload Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                AddLog($"Background image uploaded: {uploadedBgImageName}");

                AddLog("Uploading foreground image to ComfyUI...");
                var uploadedFgImageName = await _comfyUIService.UploadImageAsync(ForegroundImagePath);
                if (string.IsNullOrEmpty(uploadedFgImageName))
                {
                    AddLog("ERROR: Foreground image upload failed");
                    System.Windows.MessageBox.Show("Failed to upload foreground image to ComfyUI.", "Upload Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                AddLog($"Foreground image uploaded: {uploadedFgImageName}");

                AddLog("Uploading video to ComfyUI...");
                var uploadedVideoName = await _comfyUIService.UploadVideoAsync(InputVideoPath);
                if (string.IsNullOrEmpty(uploadedVideoName))
                {
                    AddLog("ERROR: Video upload failed");
                    System.Windows.MessageBox.Show("Failed to upload video to ComfyUI.", "Upload Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                AddLog($"Video uploaded: {uploadedVideoName}");

                // Update workflow parameters
                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 20;
                var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedBgImageName, uploadedFgImageName, uploadedVideoName);

                // Execute workflow
                ProcessingStatus = "Generating VACE video...";
                ProcessingProgress = 30;
                AddLog("Executing VACE video generation workflow...");

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 30 + (percent * 0.6);
                            ProcessingStatus = $"Generating VACE video: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    ProcessingStatus = "VACE workflow completed, retrieving video...";
                });

                AddLog($"VACE workflow execution completed with prompt ID: {promptId}");

                // Wait and retrieve the output video
                ProcessingStatus = "Retrieving output video...";
                ProcessingProgress = 95;
                AddLog("Looking for generated VACE video...");

                var existingFiles = GetExistingVideoFiles();
                var outputVideo = await WaitForNewVideoAsync(existingFiles, "*.mp4", TimeSpan.FromSeconds(120));

                if (outputVideo != null && File.Exists(outputVideo))
                {
                    ResultVideoPath = outputVideo;
                    await LocalCopyService.CopyVideoAsync(outputVideo);
                    HasResult = true;

                    var fileInfo = new FileInfo(outputVideo);
                    ResultVideoInfo = $"VACE Video • {fileInfo.Length / 1024}KB";

                    ProcessingProgress = 100;
                    ProcessingStatus = "VACE Complete!";

                    AddLog($"=== VACE video generation completed successfully ===");
                    AddLog($"Video saved to: {outputVideo}");
                }
                else
                {
                    AddLog("WARNING: No output video found after VACE generation");
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

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string backgroundImageName, string foregroundImageName, string videoName)
        {
            var workflowJson = workflow.GetRawText();
            AddLog("=== Updating VACE workflow parameters ===");

            // Calculate video dimensions based on foreground image aspect ratio
            int videoWidth = 832;
            int videoHeight = 480;
            int imageWidth = 480;
            int imageHeight = 832;

            try
            {
                var imagePath = ForegroundImagePath;
                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    int originalWidth = bitmap.PixelWidth;
                    int originalHeight = bitmap.PixelHeight;
                    double aspectRatio = (double)originalWidth / originalHeight;

                    AddLog($"Image dimensions: {originalWidth}x{originalHeight} (AR: {aspectRatio:F2})");

                    const int maxDimension = 832;
                    const int minDimension = 480;

                    if (aspectRatio > 1) // Landscape
                    {
                        videoWidth = maxDimension;
                        videoHeight = (int)(maxDimension / aspectRatio);
                        imageWidth = minDimension;
                        imageHeight = (int)(minDimension / aspectRatio);
                    }
                    else // Portrait or square
                    {
                        videoWidth = (int)(minDimension * aspectRatio);
                        videoHeight = minDimension;
                        imageWidth = (int)(minDimension * aspectRatio);
                        imageHeight = maxDimension;
                    }

                    // Ensure even numbers
                    videoWidth = videoWidth % 2 == 0 ? videoWidth : videoWidth + 1;
                    videoHeight = videoHeight % 2 == 0 ? videoHeight : videoHeight + 1;
                    imageWidth = imageWidth % 2 == 0 ? imageWidth : imageWidth + 1;
                    imageHeight = imageHeight % 2 == 0 ? imageHeight : imageHeight + 1;

                    AddLog($"Calculated video dimensions: {videoWidth}x{videoHeight}");
                    AddLog($"Calculated image dimensions: {imageWidth}x{imageHeight}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Warning: Could not read image dimensions, using defaults: {ex.Message}");
            }

            // Update background image (node 25)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "25", "image", backgroundImageName);
            AddLog($"✓ Node 25 (LoadImage - Background image): image updated");

            // Update foreground image (node 24)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "24", "image", foregroundImageName);
            AddLog($"✓ Node 24 (LoadImage - Foreground image): image updated");

            // Update video input (node 14)
            var comfyUIInputPath = Path.Combine(_settingsService.Settings?.ComfyUIFolderPath ?? "", "input", videoName);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "14", "video", comfyUIInputPath);
            AddLog($"✓ Node 14 (LoadVideo - Video): video updated");

            // Update positive prompt (node 26)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "26", "string", Prompt);
            AddLog($"✓ Node 26 (StringConstantMultiline - Prompt): string updated");

            // Update image resize dimensions (node 22)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "22", new Dictionary<string, object>
            {
                { "width", imageWidth },
                { "height", imageHeight }
            });
            AddLog($"✓ Node 22 (ImageResizeKJv2): {imageWidth}x{imageHeight}");

            // Update VACE encode dimensions (node 38)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "38", new Dictionary<string, object>
            {
                { "width", videoWidth },
                { "height", videoHeight }
            });
            AddLog($"✓ Node 38 (WanVideoVACEEncode): {videoWidth}x{videoHeight}");

            // Update VACE encode dimensions (node 48)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "48", new Dictionary<string, object>
            {
                { "width", videoWidth },
                { "height", videoHeight }
            });
            AddLog($"✓ Node 48 (WanVideoVACEEncode): {videoWidth}x{videoHeight}");

            AddLog("=== VACE workflow parameters updated successfully ===");

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        #endregion

        private void NotifyCommandsCanExecuteChanged()
        {
            GenerateVideoCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            SendToEditCameraCommand.NotifyCanExecuteChanged();
        }

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            NotifyCommandsCanExecuteChanged();
        }
    }
}
