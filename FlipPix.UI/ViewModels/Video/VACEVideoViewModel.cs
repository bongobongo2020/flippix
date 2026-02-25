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
    /// Handles a reference image and input video, processed in 81-frame chunks.
    /// Uses Wan-VACE_V2V_MasterAPI.json workflow.
    /// </summary>
    public partial class VACEVideoViewModel : VideoProcessingBaseViewModel
    {
        private const int FramesPerChunk = 81;
        private const string OutputSubfolder = "wan_vace";

        private string _prompt = string.Empty;
        private string _foregroundImagePath = string.Empty;
        private BitmapImage? _foregroundImagePreview;
        private string _foregroundImageInfo = string.Empty;
        private string _inputVideoPath = string.Empty;
        private string _inputVideoInfo = string.Empty;
        private int _totalFrames;
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
            SelectForegroundImageCommand = new RelayCommand(SelectForegroundImage);
            SelectVideoCommand = new RelayCommand(SelectVideo);
            GenerateVideoCommand = new RelayCommand(async () => await GenerateVideoAsync(), () => CanGenerateVideo);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);

            AddLog("VACE Video Generator initialized");
        }

        #region Commands

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
            set { _foregroundImagePreview = value; OnPropertyChanged(); }
        }

        public string ForegroundImageInfo
        {
            get => _foregroundImageInfo;
            set { if (_foregroundImageInfo != value) { _foregroundImageInfo = value; OnPropertyChanged(); } }
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
            set { if (_inputVideoInfo != value) { _inputVideoInfo = value; OnPropertyChanged(); } }
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

        public bool HasForegroundImage => !string.IsNullOrEmpty(ForegroundImagePath) && File.Exists(ForegroundImagePath);
        public bool HasInputVideo => !string.IsNullOrEmpty(InputVideoPath) && File.Exists(InputVideoPath);
        public bool CanGenerateVideo => HasForegroundImage && HasInputVideo && !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing;

        #endregion

        #region File Selection

        private async void SelectForegroundImage()
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
                ForegroundImagePath = filePath;
                AddLog($"VACE: Selected reference image: {Path.GetFileName(ForegroundImagePath)}");
            }
        }

        private async void SelectVideo()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

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

        #region Preview Loading

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
                AddLog($"Error loading image preview: {ex.Message}");
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

                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing VACE workflow...";

                AddLog($"Reference image: {Path.GetFileName(ForegroundImagePath)}");
                AddLog($"Input video: {Path.GetFileName(InputVideoPath)}");
                AddLog($"Prompt: {Prompt}");

                // Get frame count
                ProcessingStatus = "Analysing input video...";
                TotalFrames = GetVideoFrameCount(InputVideoPath);
                if (TotalFrames <= 0)
                {
                    AddLog("WARNING: Could not determine frame count; defaulting to 1 chunk");
                    TotalFrames = FramesPerChunk;
                }
                AddLog($"Total frames: {TotalFrames} → {TotalChunks} chunk(s) of {FramesPerChunk}");

                // ComfyUI health check
                ProcessingStatus = "Checking ComfyUI status...";
                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddLog($"[Auto-Restart] {status}"));

                if (!comfyUIOk)
                {
                    AddLog("ERROR: ComfyUI is not running");
                    System.Windows.MessageBox.Show(
                        "ComfyUI is not running. Please start ComfyUI manually or configure auto-restart in settings.",
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
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "Wan-VACE_V2V_MasterAPI.json");
                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    System.Windows.MessageBox.Show($"VACE workflow file not found:\n{workflowPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Upload assets
                ProcessingStatus = "Uploading assets to ComfyUI...";
                ProcessingProgress = 10;

                AddLog("Uploading reference image...");
                var uploadedImageName = await _comfyUIService.UploadImageAsync(ForegroundImagePath);
                if (string.IsNullOrEmpty(uploadedImageName))
                {
                    AddLog("ERROR: Reference image upload failed");
                    System.Windows.MessageBox.Show("Failed to upload reference image to ComfyUI.", "Upload Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                AddLog($"Reference image uploaded: {uploadedImageName}");

                AddLog("Uploading video...");
                var uploadedVideoName = await _comfyUIService.UploadVideoAsync(InputVideoPath);
                if (string.IsNullOrEmpty(uploadedVideoName))
                {
                    AddLog("ERROR: Video upload failed");
                    System.Windows.MessageBox.Show("Failed to upload video to ComfyUI.", "Upload Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                AddLog($"Video uploaded: {uploadedVideoName}");

                // Calculate output dimensions from reference image
                int outputWidth = 576, outputHeight = 1024;
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(ForegroundImagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    double ar = (double)bitmap.PixelWidth / bitmap.PixelHeight;
                    if (ar > 1.2) { outputWidth = 1024; outputHeight = 576; }
                    else if (ar >= 0.85) { outputWidth = 720; outputHeight = 720; }
                    else { outputWidth = 576; outputHeight = 1024; }
                    AddLog($"Output dimensions: {outputWidth}x{outputHeight} (AR: {ar:F2})");
                }
                catch (Exception ex)
                {
                    AddLog($"Warning: Could not read image dimensions, using defaults: {ex.Message}");
                }

                // Chunk loop
                var totalChunks = TotalChunks;
                var chunkFiles = new List<string>();
                AddLog($"=== Processing {totalChunks} chunk(s) of {FramesPerChunk} frames ===");

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    try
                    {
                        var startFrame = chunkIndex * FramesPerChunk;
                        var framesInChunk = Math.Min(FramesPerChunk, TotalFrames - startFrame);

                        AddLog($"=== Chunk {chunkIndex + 1}/{totalChunks}: frames {startFrame}–{startFrame + framesInChunk - 1} ===");
                        ProcessingStatus = $"Processing chunk {chunkIndex + 1}/{totalChunks}";
                        var baseProgress = 20.0 + chunkIndex * 60.0 / totalChunks;

                        if (chunkIndex > 0 && !_comfyUIService.IsConnected)
                        {
                            AddLog("Reconnecting to ComfyUI...");
                            await _comfyUIService.ConnectAsync();
                        }

                        var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedImageName, uploadedVideoName,
                            startFrame, framesInChunk, outputWidth, outputHeight);

                        var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                        {
                            if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                            {
                                var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                {
                                    ProcessingProgress = baseProgress + percent * 0.6 / totalChunks;
                                    ProcessingStatus = $"Chunk {chunkIndex + 1}/{totalChunks}: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                                });
                            }
                        });

                        var existingFiles = GetExistingVideoFiles("*.mp4", OutputSubfolder);
                        var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                        AddLog($"Chunk {chunkIndex + 1} completed, prompt ID: {promptId}");

                        var outputVideo = await WaitForNewVideoAsync(
                            existingFiles, "*.mp4",
                            TimeSpan.FromMinutes(15),
                            TimeSpan.FromSeconds(5),
                            OutputSubfolder);

                        if (outputVideo != null && File.Exists(outputVideo))
                        {
                            var chunkFile = Path.Combine(Path.GetTempPath(), $"vace_chunk_{chunkIndex:D3}_{Path.GetFileName(outputVideo)}");
                            File.Copy(outputVideo, chunkFile, true);
                            chunkFiles.Add(chunkFile);
                            AddLog($"Chunk {chunkIndex + 1}/{totalChunks} saved: {Path.GetFileName(chunkFile)}");
                        }
                        else
                        {
                            AddLog($"WARNING: No output video for chunk {chunkIndex + 1}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"ERROR processing chunk {chunkIndex + 1}: {ex.Message}");
                    }
                }

                // Merge / finalise
                ProcessingProgress = 85;
                ProcessingStatus = "Merging video chunks...";
                AddLog("=== Merging chunks ===");

                if (chunkFiles.Count > 0)
                {
                    var outputDir = Path.Combine(
                        _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(),
                        "VACE");
                    Directory.CreateDirectory(outputDir);

                    var finalPath = Path.Combine(outputDir, $"VACE_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                    if (chunkFiles.Count == 1)
                    {
                        File.Copy(chunkFiles[0], finalPath, true);
                        AddLog($"Single chunk copied to: {finalPath}");
                    }
                    else
                    {
                        MergeVideoChunksWithFFmpeg(chunkFiles, finalPath);
                    }

                    foreach (var f in chunkFiles)
                        try { File.Delete(f); } catch { }

                    ResultVideoPath = finalPath;
                    await LocalCopyService.CopyVideoAsync(finalPath);
                    HasResult = true;

                    var fi = new FileInfo(finalPath);
                    ResultVideoInfo = $"VACE Video • {fi.Length / 1024 / 1024:F1}MB";
                    ProcessingProgress = 100;
                    ProcessingStatus = "VACE Complete!";
                    AddLog($"=== VACE generation complete: {finalPath} ===");
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

        private JsonElement UpdateWorkflowParameters(
            JsonElement workflow,
            string imageName,
            string videoName,
            int startFrame,
            int framesInChunk,
            int outputWidth,
            int outputHeight)
        {
            var workflowJson = workflow.GetRawText();
            AddLog($"Updating workflow: start={startFrame}, frames={framesInChunk}, size={outputWidth}x{outputHeight}");

            // Node 10: video input — override frame_load_cap and skip_first_frames
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "10", new Dictionary<string, object>
            {
                { "video", videoName },
                { "frame_load_cap", framesInChunk },
                { "skip_first_frames", startFrame }
            });

            // Node 148: reference image
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "148", "image", imageName);

            // Node 31: prompt
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "31", "string", Prompt);

            // Nodes 19/20/21: frames / height / width (INTConstant)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "19", "value", framesInChunk);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "20", "value", outputHeight);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "21", "value", outputWidth);

            AddLog($"✓ Nodes updated");
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

            var listFile = Path.Combine(Path.GetTempPath(), $"ffmpeg_vace_{Guid.NewGuid()}.txt");
            using (var writer = new StreamWriter(listFile))
            {
                foreach (var f in chunkFiles)
                    writer.WriteLine($"file '{f.Replace("\\", "/")}'");
            }

            AddLog($"Merging {chunkFiles.Count} chunks with ffmpeg...");

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-f concat -safe 0 -i \"{listFile}\" -c copy \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException("Failed to start ffmpeg.");

            process.WaitForExit(120000);
            try { File.Delete(listFile); } catch { }

            if (!File.Exists(outputPath))
                throw new InvalidOperationException($"ffmpeg merge failed. Output not found: {outputPath}");

            AddLog($"Merge complete: {Path.GetFileName(outputPath)}");
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
