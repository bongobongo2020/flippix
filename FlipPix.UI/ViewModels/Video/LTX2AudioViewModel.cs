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
    /// ViewModel for LTX2 Audio-synchronized video generation.
    /// Handles image input, audio file, and generates video in chunks synced to audio.
    /// </summary>
    public partial class LTX2AudioViewModel : VideoProcessingBaseViewModel
    {
        // LTX2Audio-specific properties
        private string _imagePath = string.Empty;
        private BitmapImage? _imagePreview;
        private string _imageInfo = string.Empty;
        private string _audioPath = string.Empty;
        private string _audioInfo = string.Empty;
        private string _prompt = string.Empty;
        private int _width = 1152;
        private int _height = 768;
        private double _audioDuration = 0;
        private int _totalFrames = 0;
        private readonly IFileDialogService _fileDialogService;

        public LTX2AudioViewModel(
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
            SelectImageCommand = new RelayCommand(SelectImage);
            SelectAudioCommand = new RelayCommand(SelectAudio);
            GenerateVideoCommand = new RelayCommand(async () => await GenerateVideoAsync(), () => CanGenerateVideo);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);

            AddLog("LTX2 Audio Video Generator initialized");
        }

        #region Commands

        public ICommand SelectImageCommand { get; }
        public ICommand SelectAudioCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }

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

        public string EstimatedDuration => AudioDuration > 0
            ? $"{AudioDuration:F1} seconds ({TotalFrames} frames at 24 FPS)"
            : "No audio loaded";

        public bool HasImage => !string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath);
        public bool HasAudio => !string.IsNullOrEmpty(AudioPath) && File.Exists(AudioPath);

        public bool CanGenerateVideo => HasImage && HasAudio &&
                                        !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing;

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
                initialDirectory);

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
                initialDirectory);

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
            const int fps = 24;
            TotalFrames = (int)(AudioDuration * fps);
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
                System.Windows.MessageBox.Show($"An error occurred during LTX2 Audio video generation:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task GenerateVideoAsyncInternal()
        {
            try
            {
                AddLog("=== Starting LTX2 Audio video generation ===");
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
                AddLog($"Total frames: {TotalFrames} ({AudioDuration:F1} seconds at 24 FPS)");

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

                // Load LTX2 Audio workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "LTX2-AudioSync-i2v-Ver2-GGUF (2)(1).json");

                AddLog($"Loading LTX2 Audio workflow");

                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    System.Windows.MessageBox.Show($"LTX2 Audio workflow file not found:\n{workflowPath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Upload image and audio
                ProcessingStatus = "Uploading assets to ComfyUI...";
                ProcessingProgress = 10;
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

                // Calculate chunks
                const int chunkDurationSeconds = 20;
                var totalChunks = (int)Math.Ceiling(AudioDuration / chunkDurationSeconds);
                AddLog($"Total duration: {AudioDuration:F1}s, will generate in {totalChunks} chunks of {chunkDurationSeconds}s each");

                var chunkFiles = new List<string>();
                var currentStartIndex = 0.0;

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    try
                    {
                        var chunkDuration = Math.Min(chunkDurationSeconds, AudioDuration - currentStartIndex);
                        var chunkFrames = (int)(chunkDuration * 24); // 24 FPS

                        AddLog($"=== Processing chunk {chunkIndex + 1}/{totalChunks} ({chunkDuration:F1}s, {chunkFrames} frames) ===");

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
                        var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedImageName, uploadedAudioName, currentStartIndex, chunkDuration, chunkFrames);

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
                        var existingFiles = GetExistingVideoFiles("LTX_*.mp4");
                        var outputVideo = await WaitForNewVideoAsync(
                            existingFiles,
                            "LTX_*.mp4",
                            TimeSpan.FromMinutes(15),
                            TimeSpan.FromSeconds(5));

                        if (outputVideo != null && File.Exists(outputVideo))
                        {
                            var chunkFileName = Path.Combine(Path.GetTempPath(), $"ltx2_chunk_{chunkIndex:D3}_{Path.GetFileName(outputVideo)}");
                            File.Copy(outputVideo, chunkFileName, true);
                            chunkFiles.Add(chunkFileName);
                            AddLog($"Chunk {chunkIndex + 1}/{totalChunks} saved: {Path.GetFileName(chunkFileName)}");
                        }
                        else
                        {
                            AddLog($"WARNING: No output video found for chunk {chunkIndex + 1}");
                        }

                        currentStartIndex += chunkDuration;
                    }
                    catch (Exception ex)
                    {
                        AddLog($"ERROR processing chunk {chunkIndex + 1}: {ex.Message}");
                        currentStartIndex += Math.Min(chunkDurationSeconds, AudioDuration - currentStartIndex);
                    }
                }

                // Merge chunks
                ProcessingProgress = 85;
                ProcessingStatus = "Merging video chunks...";
                AddLog("=== Merging video chunks ===");

                if (chunkFiles.Count > 0)
                {
                    var outputPath = Path.Combine(_settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "LTX2Audio");
                    Directory.CreateDirectory(outputPath);

                    var outputFileName = $"LTX2Audio_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
                    var finalOutputPath = Path.Combine(outputPath, outputFileName);

                    if (chunkFiles.Count == 1)
                    {
                        File.Copy(chunkFiles[0], finalOutputPath, true);
                        AddLog($"Single chunk copied to: {finalOutputPath}");
                    }
                    else
                    {
                        MergeVideoChunksWithFFmpeg(chunkFiles, finalOutputPath, AudioPath);
                    }

                    // Clean up chunk files
                    foreach (var chunkFile in chunkFiles)
                    {
                        try { File.Delete(chunkFile); } catch { }
                    }

                    ResultVideoPath = finalOutputPath;
                    await LocalCopyService.CopyVideoAsync(finalOutputPath);
                    HasResult = true;

                    var fileInfo = new FileInfo(finalOutputPath);
                    ResultVideoInfo = $"LTX2 Audio Video • {fileInfo.Length / 1024 / 1024:F1}MB";

                    ProcessingProgress = 100;
                    ProcessingStatus = "Complete!";

                    AddLog($"=== LTX2 Audio video generation completed ===");
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

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string imageName, string audioName, double startIndex, double duration, int frames)
        {
            var workflowJson = workflow.GetRawText();
            AddLog($"Updating workflow: Start {startIndex:F2}s, Duration {duration:F2}s, Frames {frames}");

            // Update image (node 110)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "110", "image", imageName);

            // Update audio (node 12)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "12", "audio", audioName);

            // Update prompt (node 85)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "85", "text", Prompt);

            // Update video length/frames (node 81)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "81", "value", frames);

            // Update width and height (node 68)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "68", new Dictionary<string, object>
            {
                { "width", Width },
                { "height", Height }
            });

            // Update audio start index (node 101)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "101", "value", startIndex);

            // Update audio duration (node 102)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "102", "value", duration);

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        private void MergeVideoChunksWithFFmpeg(List<string> chunkFiles, string outputPath, string originalAudioPath)
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

            // Concatenate chunks
            RunFFmpegCommand(ffmpegPath, $"-f concat -safe 0 -i \"{listFile}\" -c copy \"{outputPath}\"");

            try { File.Delete(listFile); } catch { }

            // Replace audio with original for perfect sync
            AddLog("Replacing audio with original for perfect sync...");
            var tempOutput = outputPath + ".temp.mp4";

            RunFFmpegCommand(ffmpegPath, $"-i \"{outputPath}\" -i \"{originalAudioPath}\" -c:v copy -c:a aac -map 0:v:0 -map 1:a:0 -shortest \"{tempOutput}\"");

            File.Delete(outputPath);
            File.Move(tempOutput, outputPath);

            AddLog($"Video merged successfully: {outputPath}");
        }

        private void RunFFmpegCommand(string ffmpegPath, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
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
