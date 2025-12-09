using System;
using System.Collections.Generic;
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
using Microsoft.Win32;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;

namespace FlipPix.UI.ViewModels
{
    public class VideoGeneratorViewModel : INotifyPropertyChanged
    {
        private readonly ComfyUIService _comfyUIService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IServiceProvider? _serviceProvider;

        private string _imageFilePath = string.Empty;
        private BitmapImage? _imagePreviewSource;
        private string _imageInfo = string.Empty;
        private string _videoPrompt = "The subject stands still, eyes full of determination and strength. The camera slowly moves closer or circles around, highlighting the powerful presence and heroic spirit of the character.";
        private string _negativePrompt = "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;
        private string _comfyUIServer = "127.0.0.1";
        private string _comfyUIPort = "8188";
        private string _statusBarMessage = "Ready";
        private bool _hasResultVideo = false;
        private string _resultVideoPath = string.Empty;
        private string _videoInfo = string.Empty;

        // Video settings
        private int _videoLength = 81;
        private int _fps = 16;
        private int _steps = 4;
        private double _cfg = 1.0;
        private long _seed = 0;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? PlayRequested;

        public VideoGeneratorViewModel(ComfyUIService comfyUIService, IAppLogger logger, FlipPix.Core.Services.SettingsService settingsService, IServiceProvider? serviceProvider = null)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _serviceProvider = serviceProvider;

            // Initialize commands
            SelectImageCommand = new RelayCommand(SelectImage);
            GenerateVideoCommand = new RelayCommand(async () => await GenerateVideoAsync(), () => CanGenerateVideo);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResultVideo);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResultVideo);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResultVideo);
            NavigateToImageGeneratorCommand = new RelayCommand(NavigateToImageGenerator);
            NavigateToCameraEditCommand = new RelayCommand(NavigateToCameraEdit);

            AddLog("Video Generator initialized");

            // Load ComfyUI settings
            var settings = _settingsService.LoadSettings();
            if (settings != null)
            {
                var uri = new Uri(settings.BaseUrl);
                ComfyUIServer = uri.Host;
                ComfyUIPort = uri.Port.ToString();
            }
        }

        // Properties
        public string ImageFilePath
        {
            get => _imageFilePath;
            set
            {
                if (_imageFilePath != value)
                {
                    _imageFilePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    LoadImagePreview();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public BitmapImage? ImagePreviewSource
        {
            get => _imagePreviewSource;
            set
            {
                _imagePreviewSource = value;
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

        public string VideoPrompt
        {
            get => _videoPrompt;
            set
            {
                _videoPrompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerateVideo));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string NegativePrompt
        {
            get => _negativePrompt;
            set
            {
                _negativePrompt = value;
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
                OnPropertyChanged(nameof(CanGenerateVideo));
                CommandManager.InvalidateRequerySuggested();
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

        public string StatusBarMessage
        {
            get => _statusBarMessage;
            set
            {
                _statusBarMessage = value;
                OnPropertyChanged();
            }
        }

        public bool HasResultVideo
        {
            get => _hasResultVideo;
            set
            {
                _hasResultVideo = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
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

        public string VideoInfo
        {
            get => _videoInfo;
            set
            {
                _videoInfo = value;
                OnPropertyChanged();
            }
        }

        // Video Settings
        public int VideoLength
        {
            get => _videoLength;
            set
            {
                _videoLength = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VideoLengthSeconds));
            }
        }

        public string VideoLengthSeconds => $"≈ {(double)VideoLength / Fps:F1} seconds at {Fps} FPS";

        public int Fps
        {
            get => _fps;
            set
            {
                _fps = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VideoLengthSeconds));
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

        public bool CanGenerateVideo => !string.IsNullOrEmpty(ImageFilePath) &&
                                        File.Exists(ImageFilePath) &&
                                        !string.IsNullOrWhiteSpace(VideoPrompt) &&
                                        !IsProcessing;

        // Commands
        public ICommand SelectImageCommand { get; }
        public ICommand GenerateVideoCommand { get; }
        public ICommand PlayVideoCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand SendToEditCameraCommand { get; }
        public ICommand NavigateToImageGeneratorCommand { get; }
        public ICommand NavigateToCameraEditCommand { get; }

        // Methods
        private void SelectImage()
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Input Image",
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                CheckFileExists = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                ImageFilePath = openFileDialog.FileName;
                AddLog($"Selected image: {Path.GetFileName(ImageFilePath)}");
            }
        }

        public void SetImagePath(string imagePath)
        {
            if (File.Exists(imagePath))
            {
                ImageFilePath = imagePath;
                AddLog($"Image loaded from edit camera: {Path.GetFileName(ImageFilePath)}");
            }
        }

        private void LoadImagePreview()
        {
            if (string.IsNullOrEmpty(ImageFilePath) || !File.Exists(ImageFilePath))
            {
                ImagePreviewSource = null;
                ImageInfo = string.Empty;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(ImageFilePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                ImagePreviewSource = bitmap;

                var fileInfo = new FileInfo(ImageFilePath);
                ImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fileInfo.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                ImageInfo = "Error loading image";
            }
        }

        private async Task GenerateVideoAsync()
        {
            if (!CanGenerateVideo) return;

            try
            {
                AddLog("=== Starting video generation ===");
                IsProcessing = true;

                // Clear previous result
                HasResultVideo = false;
                ResultVideoPath = string.Empty;
                VideoInfo = string.Empty;

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"Input image: {Path.GetFileName(ImageFilePath)}");
                AddLog($"Prompt: {VideoPrompt}");
                AddLog($"Video settings: {VideoLength} frames @ {Fps} FPS");

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

                // Load workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "video_wan2_2_14B_i2vAPI.json");
                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    System.Windows.MessageBox.Show($"Workflow file not found: {workflowPath}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                AddLog($"Loading workflow: {workflowPath}");
                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Upload input image
                ProcessingStatus = "Uploading input image...";
                ProcessingProgress = 10;
                AddLog("Uploading input image to ComfyUI...");

                var uploadedImageName = await _comfyUIService.UploadImageAsync(ImageFilePath);
                AddLog($"Image uploaded: {uploadedImageName}");

                // Update workflow parameters
                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 20;
                var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedImageName);

                // Execute workflow
                ProcessingStatus = "Generating video...";
                ProcessingProgress = 30;
                AddLog("Executing video generation workflow...");

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

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);

                // Force progress update after workflow completes
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    ProcessingStatus = "Workflow completed, retrieving video...";
                });

                AddLog($"Workflow execution completed with prompt ID: {promptId}");

                // Wait and retrieve the output video
                ProcessingStatus = "Retrieving output video...";
                ProcessingProgress = 95;
                AddLog("Looking for generated video...");

                // Wait for the video to be saved
                await Task.Delay(3000);

                // Get the video from ComfyUI output folder
                var outputVideo = await GetOutputVideoFromComfyUI(promptId);

                if (outputVideo != null && File.Exists(outputVideo))
                {
                    ResultVideoPath = outputVideo;
                    HasResultVideo = true;

                    var fileInfo = new FileInfo(outputVideo);
                    VideoInfo = $"Video: {VideoLength} frames @ {Fps} FPS • {fileInfo.Length / 1024}KB";

                    ProcessingProgress = 100;
                    ProcessingStatus = "Complete!";
                    StatusBarMessage = $"Video generation complete - {Path.GetFileName(outputVideo)}";

                    AddLog("=== Video generation completed successfully ===");
                }
                else
                {
                    AddLog("WARNING: No output video found");
                    ProcessingStatus = "No output generated";
                    System.Windows.MessageBox.Show("No output video was generated. Please check the ComfyUI console for errors.", "Warning", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                AddLog($"Stack trace: {ex.StackTrace}");
                ProcessingStatus = "Error occurred";
                StatusBarMessage = "Error during video generation";
                System.Windows.MessageBox.Show($"An error occurred during video generation:\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string inputImageName)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            // Update image input (node 97)
            if (workflowDict.ContainsKey("97"))
            {
                var node97 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["97"].GetRawText());
                if (node97 != null && node97.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node97["inputs"]));
                    if (inputs != null)
                    {
                        inputs["image"] = inputImageName;
                        node97["inputs"] = inputs;
                        workflowDict["97"] = JsonSerializer.SerializeToElement(node97);
                    }
                }
            }

            // Update positive prompt (node 93)
            if (workflowDict.ContainsKey("93"))
            {
                var node93 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["93"].GetRawText());
                if (node93 != null && node93.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node93["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = VideoPrompt;
                        node93["inputs"] = inputs;
                        workflowDict["93"] = JsonSerializer.SerializeToElement(node93);
                    }
                }
            }

            // Update negative prompt (node 89)
            if (workflowDict.ContainsKey("89"))
            {
                var node89 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["89"].GetRawText());
                if (node89 != null && node89.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node89["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = NegativePrompt;
                        node89["inputs"] = inputs;
                        workflowDict["89"] = JsonSerializer.SerializeToElement(node89);
                    }
                }
            }

            // Update WanImageToVideo parameters (node 98)
            if (workflowDict.ContainsKey("98"))
            {
                var node98 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["98"].GetRawText());
                if (node98 != null && node98.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node98["inputs"]));
                    if (inputs != null)
                    {
                        inputs["length"] = VideoLength;
                        node98["inputs"] = inputs;
                        workflowDict["98"] = JsonSerializer.SerializeToElement(node98);
                    }
                }
            }

            // Update FPS (node 94)
            if (workflowDict.ContainsKey("94"))
            {
                var node94 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["94"].GetRawText());
                if (node94 != null && node94.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node94["inputs"]));
                    if (inputs != null)
                    {
                        inputs["fps"] = Fps;
                        node94["inputs"] = inputs;
                        workflowDict["94"] = JsonSerializer.SerializeToElement(node94);
                    }
                }
            }

            // Update steps and CFG for both KSampler nodes (85 and 86)
            foreach (var nodeId in new[] { "85", "86" })
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
                            inputs["steps"] = Steps;
                            inputs["cfg"] = Cfg;
                            if (Seed > 0)
                            {
                                inputs["noise_seed"] = Seed;
                            }
                            node["inputs"] = inputs;
                            workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                        }
                    }
                }
            }

            var updatedWorkflow = JsonSerializer.SerializeToElement(workflowDict);
            AddLog("Workflow parameters updated successfully");
            return updatedWorkflow;
        }

        private async Task<string?> GetOutputVideoFromComfyUI(string promptId)
        {
            try
            {
                AddLog("=== GetOutputVideoFromComfyUI START ===");

                // Get the actual ComfyUI server settings
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                AddLog($"BaseUrl from settings: {baseUrl}");

                // Parse the URL to get server and port
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;
                var actualPort = uri.Port.ToString();

                // Check if ComfyUI is running locally or remotely
                bool isRemoteComfyUI = IsComfyUIRemote(actualServer);

                AddLog($"Parsed server: {actualServer}:{actualPort}");
                AddLog($"Is remote ComfyUI: {isRemoteComfyUI}");

                if (isRemoteComfyUI)
                {
                    AddLog("Detected remote ComfyUI server, accessing generated video...");

                    // For remote ComfyUI, require a network path to the output folder
                    var remoteOutputPath = _settingsService.Settings?.RemoteOutputFolderPath;

                    // Check if we have a valid remote output path
                    if (!string.IsNullOrEmpty(remoteOutputPath) && Directory.Exists(remoteOutputPath))
                    {
                        AddLog($"Using remote output folder: {remoteOutputPath}");
                        return await CopyVideoFromRemoteFolder(remoteOutputPath, promptId);
                    }

                    // If we don't have a valid remote output path, require user to configure it
                    if (string.IsNullOrEmpty(remoteOutputPath))
                    {
                        AddLog("Remote output folder not configured for remote ComfyUI server.");

                        var result = System.Windows.MessageBox.Show(
                            "ComfyUI is running on a remote server.\n\n" +
                            "To retrieve generated videos, you must configure the network path to the remote ComfyUI output folder.\n\n" +
                            "Would you like to configure it now?",
                            "Remote Output Folder Required",
                            System.Windows.MessageBoxButton.OKCancel,
                            System.Windows.MessageBoxImage.Warning);

                        if (result == System.Windows.MessageBoxResult.OK)
                        {
                            ShowRemoteOutputFolderSetup();
                            // After setup, check again if the folder was configured
                            remoteOutputPath = _settingsService.Settings?.RemoteOutputFolderPath;
                            if (!string.IsNullOrEmpty(remoteOutputPath) && Directory.Exists(remoteOutputPath))
                            {
                                AddLog($"Remote output folder configured: {remoteOutputPath}");
                                return await CopyVideoFromRemoteFolder(remoteOutputPath, promptId);
                            }
                        }
                    }
                    else
                    {
                        // Remote output path is configured but not accessible
                        AddLog($"Remote output folder not accessible: {remoteOutputPath}");

                        var result = System.Windows.MessageBox.Show(
                            "The configured remote output folder is not accessible.\n\n" +
                            "Please check the network path and permissions.\n\n" +
                            "Would you like to reconfigure it?",
                            "Remote Output Folder Not Accessible",
                            System.Windows.MessageBoxButton.OKCancel,
                            System.Windows.MessageBoxImage.Error);

                        if (result == System.Windows.MessageBoxResult.OK)
                        {
                            ShowRemoteOutputFolderSetup();
                            // After setup, check again if the folder was configured
                            remoteOutputPath = _settingsService.Settings?.RemoteOutputFolderPath;
                            if (!string.IsNullOrEmpty(remoteOutputPath) && Directory.Exists(remoteOutputPath))
                            {
                                AddLog($"Remote output folder reconfigured: {remoteOutputPath}");
                                return await CopyVideoFromRemoteFolder(remoteOutputPath, promptId);
                            }
                        }
                    }

                    // If we get here, no valid remote output folder is available
                    AddLog("ERROR: Remote output folder is required for remote ComfyUI server access.");
                    AddLog("Video retrieval failed - please configure the remote output folder and try again.");
                    return null;
                }
                else
                {
                    // Local ComfyUI - check the output folder directly
                    var settings = _settingsService.Settings;
                    if (settings == null || string.IsNullOrEmpty(settings.OutputFolderPath))
                    {
                        AddLog("ERROR: ComfyUI output path not configured");
                        return null;
                    }

                    var outputFolder = Path.Combine(settings.OutputFolderPath, "video");
                    if (!Directory.Exists(outputFolder))
                    {
                        AddLog($"ERROR: Output folder not found: {outputFolder}");
                        return null;
                    }

                    // Get the most recent video file
                    var videoFiles = Directory.GetFiles(outputFolder, "*.mp4")
                        .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                        .ToList();

                    if (videoFiles.Any())
                    {
                        var latestVideo = videoFiles.First();
                        AddLog($"Found output video: {Path.GetFileName(latestVideo)}");
                        return latestVideo;
                    }

                    AddLog("No video files found in output folder");
                }

                return null;
            }
            catch (Exception ex)
            {
                AddLog($"ERROR getting output video: {ex.Message}");
                return null;
            }
        }

        private void PlayVideo()
        {
            PlayRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OpenResultFolder()
        {
            if (!string.IsNullOrEmpty(ResultVideoPath) && File.Exists(ResultVideoPath))
            {
                var folderPath = Path.GetDirectoryName(ResultVideoPath);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    Process.Start("explorer.exe", folderPath);
                    AddLog($"Opened folder: {folderPath}");
                }
            }
        }

        private void SendToEditCamera()
        {
            // This will be implemented to open FlipPixWindow with the first frame of the video
            System.Windows.MessageBox.Show("This feature will extract the first frame of the video and send it to the Edit Camera page.", "Info", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            AddLog("Send to Edit Camera requested");
        }

        private void NavigateToImageGenerator()
        {
            if (_serviceProvider == null) return;

            try
            {
                var imageGeneratorWindow = _serviceProvider.GetService(typeof(ImageGeneratorWindow)) as ImageGeneratorWindow;
                imageGeneratorWindow?.Show();
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Image Generator: {ex.Message}");
            }
        }

        private void NavigateToCameraEdit()
        {
            if (_serviceProvider == null) return;

            try
            {
                var cameraEditWindow = _serviceProvider.GetService(typeof(FlipPixWindow)) as FlipPixWindow;
                cameraEditWindow?.Show();
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Camera Edit: {ex.Message}");
            }
        }

        private void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var logEntry = $"[{timestamp}] {message}\n";
            LogOutput += logEntry;
            _logger.LogInfo(message);
        }

        private async Task<string?> CopyVideoFromRemoteFolder(string remoteOutputPath, string promptId)
    {
        try
        {
            AddLog("=== CopyVideoFromRemoteFolder START ===");
            AddLog($"Remote output path: {remoteOutputPath}");

            // Wait a moment for files to be written
            await Task.Delay(2000);

            // Look for recent video files in the remote output folder
            var videoFiles = Directory.GetFiles(remoteOutputPath, "*.mp4", SearchOption.AllDirectories)
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .ToList();

            AddLog($"Found {videoFiles.Count} MP4 files in remote folder");

            // Also check for subfolders that might contain videos
            var subfolders = new[] { "output", "videos", "temp", "input" };
            foreach (var subfolder in subfolders)
            {
                var subfolderPath = Path.Combine(remoteOutputPath, subfolder);
                if (Directory.Exists(subfolderPath))
                {
                    var subfolderVideos = Directory.GetFiles(subfolderPath, "*.mp4")
                        .OrderByDescending(f => new FileInfo(f).LastWriteTime);
                    videoFiles.AddRange(subfolderVideos);
                }
            }

            videoFiles = videoFiles.Distinct().OrderByDescending(f => new FileInfo(f).LastWriteTime).ToList();
            AddLog($"Total unique MP4 files found: {videoFiles.Count}");

            // Filter for files created in the last 10 minutes (more generous timeframe)
            var recentFiles = videoFiles.Where(f =>
            {
                var fileInfo = new FileInfo(f);
                var age = DateTime.Now - fileInfo.LastWriteTime;
                return age.TotalMinutes <= 10;
            }).ToList();

            AddLog($"Found {recentFiles.Count} recent video files (within last 10 minutes)");

            if (recentFiles.Any())
            {
                var latestVideo = recentFiles.First();
                var fileInfo = new FileInfo(latestVideo);
                AddLog($"Most recent video: {fileInfo.Name} (Modified: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss})");

                // Create local output directory
                var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "video-generation");
                Directory.CreateDirectory(outputDir);

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var localFileName = $"video_{timestamp}.mp4";
                var outputPath = Path.Combine(outputDir, localFileName);

                AddLog($"Copying video to: {outputPath}");

                // Copy the file
                File.Copy(latestVideo, outputPath, true);

                var copiedFileInfo = new FileInfo(outputPath);
                AddLog($"Video copied successfully: {copiedFileInfo.Name} ({copiedFileInfo.Length / 1024}KB)");
                AddLog("=== CopyVideoFromRemoteFolder END ===");

                return outputPath;
            }
            else
            {
                // If no recent files found, show all files for debugging
                if (videoFiles.Any())
                {
                    AddLog("All video files found (showing last 10):");
                    foreach (var file in videoFiles.Take(10))
                    {
                        var info = new FileInfo(file);
                        var age = DateTime.Now - info.LastWriteTime;
                        AddLog($"  - {info.Name} ({age.TotalMinutes:F1} minutes old)");
                    }
                }
                else
                {
                    AddLog("No MP4 files found in remote output folder or subfolders");
                }

                AddLog("=== CopyVideoFromRemoteFolder END (NO FILES) ===");
                return null;
            }
        }
        catch (Exception ex)
        {
            AddLog($"ERROR accessing remote folder: {ex.Message}");
            AddLog($"Stack trace: {ex.StackTrace}");
            return null;
        }
    }

    private async Task<string?> TryHttpDownloadFallback(string promptId)
    {
        try
        {
            AddLog("=== HTTP Download Fallback START ===");

            // First try the history API approach
            var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
            AddLog($"Found {outputFiles.Count} potential output files");

            // Look for video files in the output
            var videoFiles = outputFiles.Where(f => f.EndsWith(".mp4") || f.EndsWith(".webm") || f.EndsWith(".mov")).ToList();

            if (videoFiles.Any())
            {
                // Download the most recent video file
                var filename = videoFiles.Last(); // Get the last/most recent
                AddLog($"Downloading generated video: {filename}");

                // Try downloading with different subfolder approaches
                var videoData = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename);

                // If direct download fails, try common subfolders
                if (videoData == null)
                {
                    AddLog("Direct download failed, trying with 'output' subfolder...");
                    videoData = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, "output");
                }

                if (videoData == null)
                {
                    AddLog("Trying with 'videos' subfolder...");
                    videoData = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, "videos");
                }

                if (videoData != null)
                {
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "video-generation");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var outputPath = Path.Combine(outputDir, $"video_{timestamp}.mp4");

                    await File.WriteAllBytesAsync(outputPath, videoData);
                    AddLog($"Video downloaded and saved: {outputPath}");
                    return outputPath;
                }
                else
                {
                    AddLog($"Failed to download video: {filename}");
                }
            }
            else
            {
                AddLog("No video files found in history, trying alternative approach...");

                // Try the fallback approach
                var fallbackVideo = await _comfyUIService.HttpClient.TryDownloadRecentVideoAsync(promptId);
                if (fallbackVideo != null)
                {
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "video-generation");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var outputPath = Path.Combine(outputDir, $"video_{timestamp}.mp4");

                    await File.WriteAllBytesAsync(outputPath, fallbackVideo);
                    AddLog($"Video downloaded and saved via fallback method: {outputPath}");
                    return outputPath;
                }
                else
                {
                    AddLog("Failed to download video using all available methods");
                    AddLog("This might be due to:");
                    AddLog("- ComfyUI output folder not being accessible via HTTP");
                    AddLog("- Different filename pattern than expected");
                    AddLog("- ComfyUI server configuration preventing file access");
                }
            }

            // Debug info about what files we found
            if (outputFiles.Any())
            {
                AddLog("All files found in history:");
                foreach (var file in outputFiles.Take(5))
                {
                    AddLog($"  - {file}");
                }
            }

            AddLog("=== HTTP Download Fallback END ===");
            return null;
        }
        catch (Exception ex)
        {
            AddLog($"ERROR in HTTP download fallback: {ex.Message}");
            return null;
        }
    }

    private void ShowRemoteOutputFolderSetup()
        {
            try
            {
                // Use a simple folder browser dialog to select the remote output folder
                using (var folderDialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    folderDialog.Description = "Select the network path to the remote ComfyUI output folder";
                    folderDialog.ShowNewFolderButton = false;

                    // Try to use previously configured path as starting point
                    var currentPath = _settingsService.Settings?.RemoteOutputFolderPath;
                    if (!string.IsNullOrEmpty(currentPath) && System.IO.Directory.Exists(currentPath))
                    {
                        folderDialog.SelectedPath = currentPath;
                    }

                    if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        var selectedPath = folderDialog.SelectedPath;

                        // Validate that the path is accessible
                        if (System.IO.Directory.Exists(selectedPath))
                        {
                            // Save the remote output folder path
                            var settings = _settingsService.Settings;
                            if (settings != null)
                            {
                                settings.RemoteOutputFolderPath = selectedPath;
                                _settingsService.SaveSettings(settings);
                            }

                            AddLog($"Remote output folder configured: {selectedPath}");
                        }
                        else
                        {
                            System.Windows.MessageBox.Show(
                                "The selected folder is not accessible. Please check the network path and permissions.",
                                "Folder Not Accessible",
                                System.Windows.MessageBoxButton.OK,
                                System.Windows.MessageBoxImage.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error configuring remote output folder: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"Error configuring remote output folder: {ex.Message}",
                    "Configuration Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private void ShowComfyUIFolderSetup()
        {
            try
            {
                // Create the ViewModel
                var setupViewModel = new ComfyUIFolderSetupViewModel(_settingsService);

                // Create and show the ComfyUI folder setup window
                var setupWindow = new ComfyUIFolderSetupWindow(setupViewModel);

                // Show the window as a dialog
                bool? result = setupWindow.ShowDialog();

                if (result == true)
                {
                    AddLog("ComfyUI settings updated successfully");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error opening ComfyUI setup: {ex.Message}");
            }
        }

        private bool IsComfyUIRemote(string serverAddress)
        {
            try
            {
                // Check if it's a local address
                if (serverAddress.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    serverAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    serverAddress.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Check if it's a local network IP (192.168.x.x, 10.x.x.x, 172.16-31.x.x)
                if (System.Net.IPAddress.TryParse(serverAddress, out var ip))
                {
                    var bytes = ip.GetAddressBytes();
                    if (bytes.Length == 4)
                    {
                        // 192.168.x.x
                        if (bytes[0] == 192 && bytes[1] == 168)
                        {
                            return true; // This is a LAN IP
                        }
                        // 10.x.x.x
                        if (bytes[0] == 10)
                        {
                            return true; // This is a LAN IP
                        }
                        // 172.16-31.x.x
                        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                        {
                            return true; // This is a LAN IP
                        }
                    }
                }

                // If we get here, assume it's remote
                return !string.IsNullOrEmpty(serverAddress) && serverAddress != ".";
            }
            catch
            {
                // If we can't determine, assume it's remote to be safe
                return true;
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
