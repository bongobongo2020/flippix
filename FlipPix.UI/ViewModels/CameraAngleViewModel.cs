using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.ViewModels
{
    public partial class CameraAngleViewModel : ObservableObject, IDisposable
    {
        private readonly FlipPix.ComfyUI.Services.ComfyUIService _comfyUIService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IFileDialogService _fileDialogService;
        private readonly ComfyUIImageRetriever _imageRetriever;
        private bool _disposed = false;

        private string _inputImagePath = string.Empty;
        private BitmapImage? _inputImagePreview;
        private ObservableCollection<string> _outputImages = new();
        private ObservableCollection<CameraAngleOutputItem> _outputItems = new();
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;
        private System.Threading.CancellationTokenSource? _cancellationTokenSource;
        private const string SelectedModel = "Flux2-Klein-9B"; // Hardcoded to 9B Klein model

        public CameraAngleViewModel(
            FlipPix.ComfyUI.Services.ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IFileDialogService fileDialogService,
            ComfyUIImageRetriever? imageRetriever = null)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            _imageRetriever = imageRetriever ?? new ComfyUIImageRetriever();

            AddLog("Camera Angle Generator initialized");
        }

        // Properties
        public string InputImagePath
        {
            get => _inputImagePath;
            set
            {
                if (SetProperty(ref _inputImagePath, value))
                {
                    OnPropertyChanged(nameof(CanGenerate));
                    LoadInputImagePreview();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public BitmapImage? InputImagePreview
        {
            get => _inputImagePreview;
            set => SetProperty(ref _inputImagePreview, value);
        }

        public ObservableCollection<string> OutputImages
        {
            get => _outputImages;
            set => SetProperty(ref _outputImages, value);
        }

        public ObservableCollection<CameraAngleOutputItem> OutputItems
        {
            get => _outputItems;
            set => SetProperty(ref _outputItems, value);
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (SetProperty(ref _isProcessing, value))
                {
                    OnPropertyChanged(nameof(CanGenerate));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string ProcessingStatus
        {
            get => _processingStatus;
            set => SetProperty(ref _processingStatus, value);
        }

        public double ProcessingProgress
        {
            get => _processingProgress;
            set
            {
                if (SetProperty(ref _processingProgress, value))
                {
                    OnPropertyChanged(nameof(ProgressPercentage));
                }
            }
        }

        public string ProgressPercentage => $"{ProcessingProgress:F0}%";

        public string LogOutput
        {
            get => _logOutput;
            set => SetProperty(ref _logOutput, value);
        }

        public bool CanGenerate => !string.IsNullOrEmpty(InputImagePath) &&
                                   File.Exists(InputImagePath) &&
                                   !IsProcessing;

        // Methods
        [RelayCommand]
        private async Task SelectInputImageAsync()
        {
            var initialDirectory = _settingsService.Settings?.StoryGeneratorInputImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Input Image for Camera Angle Generation",
                "Image Files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All Files (*.*)|*.*",
                initialDirectory);

            if (filePath != null)
            {
                InputImagePath = filePath;

                // Save the folder location for next time
                var folderPath = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(folderPath) && _settingsService.Settings != null)
                {
                    _settingsService.Settings.StoryGeneratorInputImageFolder = folderPath;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }

                AddLog($"Selected input image: {Path.GetFileName(InputImagePath)}");
            }
        }

        private void LoadInputImagePreview()
        {
            if (!string.IsNullOrEmpty(InputImagePath) && File.Exists(InputImagePath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(InputImagePath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    InputImagePreview = bitmap;
                }
                catch (Exception ex)
                {
                    AddLog($"Error loading image preview: {ex.Message}");
                    InputImagePreview = null;
                }
            }
            else
            {
                InputImagePreview = null;
            }
        }

        private BitmapImage? LoadThumbnail(string imagePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 200; // Limit size for thumbnail
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                AddLog($"Error loading thumbnail for {Path.GetFileName(imagePath)}: {ex.Message}");
                return null;
            }
        }

        [RelayCommand]
        private async Task GenerateCameraAnglesAsync()
        {
            // If no image selected or file doesn't exist, auto-open file dialog
            if (string.IsNullOrEmpty(InputImagePath) || !File.Exists(InputImagePath))
            {
                await SelectInputImageAsync();
                // If user cancelled the dialog, abort
                if (string.IsNullOrEmpty(InputImagePath) || !File.Exists(InputImagePath))
                    return;
            }

            // If already processing, don't start another
            if (IsProcessing) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                IsProcessing = true;
                OutputImages.Clear();
                OutputItems.Clear();

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"=== Starting camera angle generation ===");
                AddLog($"Input image: {Path.GetFileName(InputImagePath)}");
                AddLog($"Model: {SelectedModel}");

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
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "FLUX2-DEV-KLEIN_4_and_9B_1_click_multiple_character_angles-v1.0.json");
                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    System.Windows.MessageBox.Show($"Workflow file not found: {workflowPath}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                AddLog($"Loading workflow: {workflowPath}");
                var workflowJson = await File.ReadAllTextAsync(workflowPath, _cancellationTokenSource.Token);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Upload input image
                ProcessingStatus = "Uploading input image...";
                ProcessingProgress = 10;
                AddLog("Uploading input image to ComfyUI...");
                var uploadedImageName = await _comfyUIService.UploadImageAsync(InputImagePath);
                AddLog($"Image uploaded as: {uploadedImageName}");

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Update workflow parameters
                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 20;
                var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedImageName);

                // Execute workflow
                ProcessingStatus = "Generating camera angles...";
                ProcessingProgress = 30;
                AddLog("Executing workflow in ComfyUI...");

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 30 + (percent * 0.6);
                            ProcessingStatus = $"Generating: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, _cancellationTokenSource.Token);

                // Force progress update after workflow completes
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    ProcessingStatus = "Workflow completed, retrieving outputs...";
                });

                AddLog($"Workflow execution completed with prompt ID: {promptId}");

                // Get output images
                ProcessingStatus = "Retrieving output images...";
                ProcessingProgress = 95;
                AddLog("Looking for generated images...");

                var outputImages = await GetOutputImagesFromComfyUI(promptId);

                if (outputImages.Any())
                {
                    // Get input image filename without extension for the subfolder
                    var inputImageFileName = Path.GetFileNameWithoutExtension(InputImagePath);
                    // Sanitize the filename to remove invalid characters
                    var subfolderName = string.Join("_", inputImageFileName.Split(Path.GetInvalidFileNameChars()));

                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "camera-angles", subfolderName);
                    Directory.CreateDirectory(outputDir);

                    AddLog($"Output directory: {outputDir}");

                    foreach (var (outputImage, index) in outputImages.Select((img, i) => (img, i)))
                    {
                        var outputPath = Path.Combine(outputDir, $"camera-angle_{index + 1}.png");
                        await File.WriteAllBytesAsync(outputPath, outputImage);
                        await LocalCopyService.CopyImageAsync(outputPath);
                        OutputImages.Add(outputPath);
                        AddLog($"Output saved: {outputPath}");

                        // Create thumbnail item
                        var thumbnail = LoadThumbnail(outputPath);
                        OutputItems.Add(new CameraAngleOutputItem
                        {
                            FilePath = outputPath,
                            Thumbnail = thumbnail,
                            Index = index + 1
                        });
                    }

                    ProcessingProgress = 100;
                    ProcessingStatus = $"Complete! Generated {OutputImages.Count} camera angles";
                    AddLog($"=== Camera angle generation completed ({OutputImages.Count} images) ===");

                    System.Windows.MessageBox.Show(
                        $"Successfully generated {OutputImages.Count} camera angles!\n\nOutput folder: {outputDir}",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    AddLog("WARNING: No output images received after all retries");
                    ProcessingStatus = "No output generated";
                    System.Windows.MessageBox.Show(
                        "No output images were generated. Please check the ComfyUI console for errors.",
                        "Warning",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("Camera angle generation cancelled by user");
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
                _logger.LogError($"Error generating camera angles: {ex}");

                ProcessingStatus = "Error occurred";
                ProcessingProgress = 0;

                System.Windows.MessageBox.Show(
                    $"Error generating camera angles:\n\n{ex.Message}\n\nCheck the log for more details.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string inputImageName)
        {
            var workflowJson = workflow.GetRawText();

            // Get the input image filename without extension for the subfolder
            var inputImageFileName = Path.GetFileNameWithoutExtension(InputImagePath);
            // Sanitize the filename to remove invalid characters
            var subfolderName = string.Join("_", inputImageFileName.Split(Path.GetInvalidFileNameChars()));

            // Update the input image (node 76 - LoadImage)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "76", "image", inputImageName);
            AddLog("Updated input image in workflow");

            // Update save nodes for models
            // The workflow has 3 save nodes: 112 (Flux2-Dev), 9 (Flux2-Klein-9B), 94 (Flux2-Klein-4B)
            // Only update the save node for the selected model

            var modelNodeId = SelectedModel switch
            {
                "Flux2-Dev" => "112",
                "Flux2-Klein-9B" => "9",
                "Flux2-Klein-4B" => "94",
                _ => "112"
            };

            AddLog($"Selected model: {SelectedModel}, using save node: {modelNodeId}");

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var filenamePrefix = $"{SelectedModel.Replace("-", "")}-camera-angles-{timestamp}";

            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, modelNodeId, new Dictionary<string, object>
            {
                { "filename_prefix", filenamePrefix },
                { "subfolder", subfolderName }
            });

            AddLog($"Node {modelNodeId}: subfolder='{subfolderName}', prefix='{filenamePrefix}'");
            AddLog($"Updated workflow for model: {SelectedModel} (subfolder: {subfolderName})");
            AddLog($"Workflow JSON length: {workflowJson.Length} characters");

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
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

                // Retry image retrieval with delays
                int retryCount = 0;
                int maxRetries = 20;

                while (retryCount < maxRetries && !images.Any())
                {
                    if (retryCount > 0)
                    {
                        AddLog($"Retry {retryCount}/{maxRetries} - waiting 5 seconds...");
                        await Task.Delay(5000);
                    }

                    _cancellationTokenSource?.Token.ThrowIfCancellationRequested();

                    if (isRemoteComfyUI)
                    {
                        AddLog("Detected remote ComfyUI server, downloading generated images...");

                        List<string> imageFiles = new();

                        // Strategy 1: Use prompt-specific history lookup (most reliable)
                        var promptOutputFiles = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                        imageFiles = promptOutputFiles.Where(f => f.EndsWith(".png")).ToList();

                        if (imageFiles.Any())
                        {
                            AddLog($"Found {imageFiles.Count} output file(s) for prompt {promptId}");
                        }
                        else
                        {
                            // Strategy 2: Fall back to scanning recent history with pattern matching
                            AddLog($"No output files in history for prompt {promptId} yet, trying pattern match...");

                            var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
                            AddLog($"Found {outputFiles.Count} potential output files in recent history");

                            // Match both "camera-angles" and "character-angles" naming conventions
                            imageFiles = outputFiles
                                .Where(f => f.EndsWith(".png") && (f.Contains("camera-angles") || f.Contains("character-angles")))
                                .ToList();

                            if (!imageFiles.Any())
                            {
                                // Also try subfolder name match
                                var inputImageFileName = Path.GetFileNameWithoutExtension(InputImagePath);
                                var subfolderName = string.Join("_", inputImageFileName.Split(Path.GetInvalidFileNameChars()));

                                imageFiles = outputFiles
                                    .Where(f => f.EndsWith(".png") && f.Contains(subfolderName))
                                    .ToList();
                            }

                            if (!imageFiles.Any())
                            {
                                AddLog($"No matching files found. Available files: {string.Join(", ", outputFiles.Take(10))}");
                            }
                        }

                        // Download ALL matching images
                        if (imageFiles.Any())
                        {
                            AddLog($"Downloading {imageFiles.Count} image(s)...");
                            foreach (var filename in imageFiles)
                            {
                                AddLog($"Downloading: {filename}");
                                var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filename);
                                if (imageData != null)
                                {
                                    images.Add(imageData);
                                }
                            }
                            AddLog($"Successfully downloaded {images.Count}/{imageFiles.Count} images");
                        }
                        else
                        {
                            AddLog("No camera-angle files found, trying fallback...");
                            var fallbackImage = await _comfyUIService.HttpClient.TryDownloadRecentOutputAsync(promptId);
                            if (fallbackImage != null)
                            {
                                images.Add(fallbackImage);
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

                        AddLog($"ComfyUI output folder: {comfyUIOutputDir}");

                        // Get the subfolder name based on input image filename
                        var inputImageFileName = Path.GetFileNameWithoutExtension(InputImagePath);
                        var subfolderName = string.Join("_", inputImageFileName.Split(Path.GetInvalidFileNameChars()));
                        var searchDir = Path.Combine(comfyUIOutputDir, subfolderName);

                        AddLog($"Input image: {inputImageFileName}");
                        AddLog($"Subfolder name: {subfolderName}");
                        AddLog($"Searching for images in: {searchDir}");

                        // List all subdirectories in the ComfyUI output folder for debugging
                        try
                        {
                            var allSubdirs = Directory.GetDirectories(comfyUIOutputDir);
                            AddLog($"All subdirectories in ComfyUI output ({allSubdirs.Length}):");
                            foreach (var dir in allSubdirs.Take(10))
                            {
                                AddLog($"  - {Path.GetFileName(dir)}");
                            }
                        }
                        catch (Exception ex)
                        {
                            AddLog($"Error listing subdirectories: {ex.Message}");
                        }

                        if (!Directory.Exists(searchDir))
                        {
                            AddLog($"Subfolder not found: {searchDir}");
                            // Fall back to searching in the main output directory, but only for recent files
                            searchDir = comfyUIOutputDir;
                            AddLog($"Falling back to: {searchDir} (will only load recent files)");
                        }

                        if (!Directory.Exists(searchDir))
                        {
                            AddLog($"ERROR: Directory not found: {searchDir}");
                            return images;
                        }

                        // Only get files created within the last 5 minutes to avoid picking up old files
                        var fiveMinutesAgo = DateTime.Now.AddMinutes(-5);
                        var cameraAngleFiles = Directory.GetFiles(searchDir, "*.png")
                            .Where(f => File.GetCreationTime(f) > fiveMinutesAgo || File.GetLastWriteTime(f) > fiveMinutesAgo)
                            .OrderByDescending(f => File.GetLastWriteTime(f))
                            .ToList();

                        AddLog($"Found {cameraAngleFiles.Count} recent PNG files (created within last 5 minutes)");

                        // List first few files for debugging
                        foreach (var file in cameraAngleFiles.Take(5))
                        {
                            AddLog($"  - {Path.GetFileName(file)} ({File.GetLastWriteTime(file):yyyy-MM-dd HH:mm:ss})");
                        }

                        if (cameraAngleFiles.Any())
                        {
                            foreach (var file in cameraAngleFiles)
                            {
                                images.Add(await File.ReadAllBytesAsync(file));
                            }
                        }
                        else
                        {
                            AddLog($"No recent PNG files found in retry {retryCount + 1}");
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

        [RelayCommand(CanExecute = nameof(IsProcessing))]
        private void CancelGeneration()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                AddLog("Cancellation requested by user");
                _cancellationTokenSource.Cancel();
                ProcessingStatus = "Cancelling...";
            }
        }

        [RelayCommand]
        private void OpenOutputFolder()
        {
            try
            {
                var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "camera-angles");
                if (Directory.Exists(outputDir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = outputDir,
                        UseShellExecute = true
                    });
                }
                else
                {
                    System.Windows.MessageBox.Show("Output folder does not exist yet. Generate some images first.", "Info",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR opening output folder: {ex.Message}");
            }
        }

        [RelayCommand(CanExecute = nameof(HasOutputImages))]
        private void ClearOutput()
        {
            OutputImages.Clear();
            OutputItems.Clear();
            AddLog("Cleared output images");
        }

        private bool HasOutputImages() => OutputImages.Any();

        private void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogOutput += $"[{timestamp}] {message}\n";
            _logger.LogInfo(message);
        }

        public class CameraAngleOutputItem
        {
            public string FilePath { get; set; } = string.Empty;
            public BitmapImage? Thumbnail { get; set; }
            public int Index { get; set; }
            public string DisplayName => $"Angle {Index}";
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

                // Clear collections
                OutputImages.Clear();
                OutputItems.Clear();

                // Clear string properties
                _inputImagePath = string.Empty;
                _processingStatus = string.Empty;
                _logOutput = string.Empty;

                _disposed = true;
            }
        }
    }
}
