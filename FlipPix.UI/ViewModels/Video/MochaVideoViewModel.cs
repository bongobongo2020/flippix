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
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ViewModel for Mocha motion capture video generation.
    /// Handles source video, reference image, and generates video in 81-frame chunks.
    /// </summary>
    public partial class MochaVideoViewModel : VideoProcessingBaseViewModel
    {
        private const int FramesPerChunk = 81;

        // Mocha-specific properties
        private string _videoPath = string.Empty;
        private string _sourceVideoInfo = string.Empty;
        private string _imagePath = string.Empty;
        private BitmapImage? _imagePreview;
        private string _imageInfo = string.Empty;
        private string _prompt = string.Empty;
        private int _totalFrames = 0;
        private readonly IFileDialogService _fileDialogService;

        public MochaVideoViewModel(
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
            SelectVideoCommand = new RelayCommand(SelectVideo);
            SelectImageCommand = new RelayCommand(SelectImage);
            GenerateVideoCommand = new RelayCommand(async () => await GenerateVideoAsync(), () => CanGenerateVideo);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);

            AddLog("Mocha Video Generator initialized");
        }

        #region Commands

        public ICommand SelectVideoCommand { get; }
        public ICommand SelectImageCommand { get; }
        public ICommand GenerateVideoCommand { get; }
        public ICommand PlayVideoCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand SendToEditCameraCommand { get; }

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
                if (_sourceVideoInfo != value)
                {
                    _sourceVideoInfo = value;
                    OnPropertyChanged();
                }
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

        public int TotalFrames
        {
            get => _totalFrames;
            set
            {
                if (_totalFrames != value)
                {
                    _totalFrames = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TotalChunks));
                }
            }
        }

        public int TotalChunks => TotalFrames > 0 ? (int)Math.Ceiling((double)TotalFrames / FramesPerChunk) : 0;

        public bool HasVideo => !string.IsNullOrEmpty(VideoPath) && File.Exists(VideoPath);
        public bool HasImage => !string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath);

        public bool CanGenerateVideo => HasVideo && HasImage &&
                                        !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing;

        #endregion

        #region File Selection Methods

        private async void SelectVideo()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            }

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Input Video",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.webm|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                VideoPath = filePath;

                // Save folder for next time
                var folderPath = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(folderPath) && _settingsService.Settings != null)
                {
                    _settingsService.Settings.VideoGeneratorImageFolder = folderPath;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }

                AddLog($"Selected Mocha video: {Path.GetFileName(VideoPath)}");
            }
        }

        private async void SelectImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                ImagePath = filePath;
                AddLog($"Selected Mocha image: {Path.GetFileName(ImagePath)}");
            }
        }

        #endregion

        #region Preview/Info Loading Methods

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
                AddLog($"Video info: {SourceVideoInfo}");
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
                System.Windows.MessageBox.Show($"An error occurred during Mocha video generation:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task GenerateVideoAsyncInternal()
        {
            try
            {
                AddLog("=== Starting Mocha video generation ===");
                IsProcessing = true;

                // Clear previous result
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"Source video: {Path.GetFileName(VideoPath)} ({TotalFrames} frames)");
                AddLog($"Source image: {Path.GetFileName(ImagePath)}");
                AddLog($"Total chunks: {TotalChunks} ({FramesPerChunk} frames each)");

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

                // Load Mocha workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "wanvideo_2_1_14B_MoCha_replace_subject_KJ_02(1).json");

                AddLog($"Loading Mocha workflow");

                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    System.Windows.MessageBox.Show($"Mocha workflow file not found:\n{workflowPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Upload video and image
                ProcessingStatus = "Uploading assets to ComfyUI...";
                ProcessingProgress = 10;
                AddLog("Uploading video to ComfyUI...");
                var uploadedVideoName = await _comfyUIService.UploadVideoAsync(VideoPath);
                if (string.IsNullOrEmpty(uploadedVideoName))
                {
                    AddLog("ERROR: Video upload failed");
                    System.Windows.MessageBox.Show("Failed to upload video to ComfyUI.", "Upload Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                AddLog($"Video uploaded: {uploadedVideoName}");

                AddLog("Uploading image to ComfyUI...");
                var uploadedImageName = await _comfyUIService.UploadImageAsync(ImagePath);
                if (string.IsNullOrEmpty(uploadedImageName))
                {
                    AddLog("ERROR: Image upload failed");
                    System.Windows.MessageBox.Show("Failed to upload image to ComfyUI.", "Upload Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                AddLog($"Image uploaded: {uploadedImageName}");

                // Process chunks
                var chunkFiles = new List<string>();
                var totalChunks = TotalChunks;

                AddLog($"=== Will process {totalChunks} chunks of {FramesPerChunk} frames each ===");

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    try
                    {
                        var startFrame = chunkIndex * FramesPerChunk;
                        var framesInChunk = Math.Min(FramesPerChunk, TotalFrames - startFrame);

                        AddLog($"=== Processing chunk {chunkIndex + 1}/{totalChunks} (frames {startFrame}-{startFrame + framesInChunk - 1}) ===");

                        ProcessingStatus = $"Processing chunk {chunkIndex + 1}/{totalChunks}";
                        var baseProgress = 20 + (chunkIndex * 60.0 / totalChunks);

                        // Check and reconnect if needed between chunks
                        if (chunkIndex > 0 && !_comfyUIService.IsConnected)
                        {
                            AddLog("Reconnecting to ComfyUI...");
                            await _comfyUIService.ConnectAsync();
                            AddLog("Reconnected to ComfyUI");
                        }

                        // Update workflow parameters for this chunk
                        var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedVideoName, uploadedImageName, startFrame, framesInChunk);

                        // Execute workflow
                        var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                        {
                            if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                            {
                                var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                {
                                    ProcessingProgress = baseProgress + (percent * 0.6 / totalChunks);
                                    ProcessingStatus = $"Chunk {chunkIndex + 1}/{totalChunks}: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                                });
                            }
                        });

                        var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                        AddLog($"Chunk {chunkIndex + 1} workflow completed, prompt ID: {promptId}");

                        // Wait for output video
                        var existingFiles = GetExistingVideoFiles("*.mp4");
                        var outputVideo = await WaitForNewVideoAsync(
                            existingFiles,
                            "*.mp4",
                            TimeSpan.FromMinutes(15),
                            TimeSpan.FromSeconds(5));

                        if (outputVideo != null && File.Exists(outputVideo))
                        {
                            var chunkFileName = Path.Combine(Path.GetTempPath(), $"mocha_chunk_{chunkIndex:D3}_{Path.GetFileName(outputVideo)}");
                            File.Copy(outputVideo, chunkFileName, true);
                            chunkFiles.Add(chunkFileName);
                            AddLog($"Chunk {chunkIndex + 1}/{totalChunks} saved: {Path.GetFileName(chunkFileName)}");
                        }
                        else
                        {
                            AddLog($"WARNING: No output video found for chunk {chunkIndex + 1}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"ERROR processing chunk {chunkIndex + 1}: {ex.Message}");
                    }
                }

                // Merge chunks
                ProcessingProgress = 85;
                ProcessingStatus = "Merging video chunks...";
                AddLog("=== Merging video chunks ===");

                if (chunkFiles.Count > 0)
                {
                    var outputPath = Path.Combine(_settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "Mocha");
                    Directory.CreateDirectory(outputPath);

                    var outputFileName = $"Mocha_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
                    var finalOutputPath = Path.Combine(outputPath, outputFileName);

                    if (chunkFiles.Count == 1)
                    {
                        File.Copy(chunkFiles[0], finalOutputPath, true);
                        AddLog($"Single chunk copied to: {finalOutputPath}");
                    }
                    else
                    {
                        MergeVideoChunksWithFFmpeg(chunkFiles, finalOutputPath);
                    }

                    // Clean up chunk files
                    foreach (var chunkFile in chunkFiles)
                    {
                        try { File.Delete(chunkFile); } catch { }
                    }

                    ResultVideoPath = finalOutputPath;
                    HasResult = true;

                    var fileInfo = new FileInfo(finalOutputPath);
                    ResultVideoInfo = $"Mocha Video • {fileInfo.Length / 1024 / 1024:F1}MB";

                    ProcessingProgress = 100;
                    ProcessingStatus = "Complete!";

                    AddLog($"=== Mocha video generation completed ===");
                    AddLog($"Video saved to: {finalOutputPath}");
                }
                else
                {
                    AddLog("ERROR: No video chunks were generated");
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

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string videoName, string imageName, int startFrame, int frameCount)
        {
            var workflowJson = workflow.GetRawText();
            AddLog($"Updating workflow: Start frame {startFrame}, Frame count {frameCount}");

            // Update video (node 128)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "128", new Dictionary<string, object>
            {
                { "video", videoName },
                { "frame_load_cap", frameCount },
                { "skip_first_frames", startFrame }
            });

            // Update image (node 212)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "212", "image", imageName);

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        private void MergeVideoChunksWithFFmpeg(List<string> chunkFiles, string outputPath)
        {
            var ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                AddLog("ERROR: ffmpeg not found. Cannot merge video chunks.");
                throw new InvalidOperationException("ffmpeg is required to merge video chunks.");
            }

            // Create a temporary file list for ffmpeg
            var listFile = Path.Combine(Path.GetTempPath(), $"ffmpeg_list_{Guid.NewGuid()}.txt");
            using (var writer = new StreamWriter(listFile))
            {
                foreach (var chunkFile in chunkFiles)
                {
                    writer.WriteLine($"file '{chunkFile.Replace("\\", "/")}'");
                }
            }

            AddLog($"Merging {chunkFiles.Count} video chunks...");

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-f concat -safe 0 -i \"{listFile}\" -c copy \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
            }

            try { File.Delete(listFile); } catch { }

            AddLog($"Video merged successfully: {outputPath}");
        }

        #endregion
    }
}
