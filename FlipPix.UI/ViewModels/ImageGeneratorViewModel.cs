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

namespace FlipPix.UI.ViewModels
{
    public class ImageGeneratorViewModel : INotifyPropertyChanged
    {
        private readonly ComfyUIService _comfyUIService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IServiceProvider? _serviceProvider;

        private string _imagePrompt = "Latina female with thick wavy hair, harbor boats and pastel houses behind. Breezy seaside light, warm tones, cinematic close-up.";
        private int _aspectRatioIndex = 0;
        private int _steps = 9;
        private double _cfg = 1.0;
        private long _seed = 0;
        private double _denoise = 1.0;
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;
        private string _comfyUIServer = "127.0.0.1";
        private string _comfyUIPort = "8188";
        private string _statusBarMessage = "Ready";
        private bool _hasResultImage = false;
        private string _resultImagePath = string.Empty;
        private BitmapImage? _resultImageSource;
        private string _imageInfo = string.Empty;
        private System.Threading.CancellationTokenSource? _cancellationTokenSource;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ImageGeneratorViewModel(ComfyUIService comfyUIService, IAppLogger logger, FlipPix.Core.Services.SettingsService settingsService, IServiceProvider? serviceProvider = null)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _serviceProvider = serviceProvider;

            // Initialize commands
            GenerateImageCommand = new RelayCommand(async () => await GenerateImageAsync(), () => CanGenerate);
            CancelGenerationCommand = new RelayCommand(CancelGeneration, () => IsProcessing);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResultImage);
            SendToCameraEditCommand = new RelayCommand(SendToCameraEdit, () => HasResultImage);
            SendToVideoGeneratorCommand = new RelayCommand(SendToVideoGenerator, () => HasResultImage);
            NavigateToCameraEditCommand = new RelayCommand(NavigateToCameraEdit);
            NavigateToVideoGeneratorCommand = new RelayCommand(NavigateToVideoGenerator);

            AddLog("Image Generator initialized");
        }

        // Properties
        public string ImagePrompt
        {
            get => _imagePrompt;
            set
            {
                _imagePrompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
                CommandManager.InvalidateRequerySuggested();
            }
        }

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

        public double Denoise
        {
            get => _denoise;
            set
            {
                _denoise = value;
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
                OnPropertyChanged(nameof(CanGenerate));
                OnPropertyChanged(nameof(CanCancel));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool CanCancel => IsProcessing;

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

        public bool HasResultImage
        {
            get => _hasResultImage;
            set
            {
                _hasResultImage = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
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

        public bool CanGenerate => !string.IsNullOrEmpty(ImagePrompt) && !IsProcessing;

        // Commands
        public ICommand GenerateImageCommand { get; }
        public ICommand CancelGenerationCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand SendToCameraEditCommand { get; }
        public ICommand SendToVideoGeneratorCommand { get; }
        public ICommand NavigateToCameraEditCommand { get; }
        public ICommand NavigateToVideoGeneratorCommand { get; }

        // Methods
        private async Task GenerateImageAsync()
        {
            if (!CanGenerate) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new System.Threading.CancellationTokenSource();

            try
            {
                AddLog("=== Starting image generation ===");
                IsProcessing = true;

                // Clear previous result
                HasResultImage = false;
                ResultImageSource = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"Prompt: {ImagePrompt}");

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
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image_z_image-TEXTAPI.json");
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

                // Update workflow with parameters
                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 10;

                var updatedWorkflow = UpdateWorkflowParameters(workflow);

                // Execute workflow
                ProcessingStatus = "Generating image...";
                ProcessingProgress = 30;
                AddLog("Executing workflow in ComfyUI...");

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

                // Force progress update after workflow completes
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    ProcessingStatus = "Workflow completed, retrieving output...";
                });

                AddLog($"Workflow execution completed with prompt ID: {promptId}");

                // Get output images from ComfyUI output folder
                ProcessingStatus = "Retrieving output image...";
                ProcessingProgress = 95;
                AddLog("Looking for generated image...");

                // Add debug info about what we're doing
                AddLog("=== DEBUG: About to call GetOutputImagesFromComfyUI ===");

                // Retry image retrieval with delays to give ComfyUI time to write the file
                List<byte[]> outputImages = new();
                int retryCount = 0;
                int maxRetries = 20; // Wait up to 100 seconds (20 retries × 5s)

                while (retryCount < maxRetries && !outputImages.Any())
                {
                    if (retryCount > 0)
                    {
                        AddLog($"Retry {retryCount}/{maxRetries} - waiting 5 seconds before checking again...");
                        await Task.Delay(5000, _cancellationTokenSource.Token);
                    }

                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    outputImages = await GetOutputImagesFromComfyUI(promptId);
                    retryCount++;
                }

                if (outputImages.Any())
                {
                    var outputImage = outputImages.First();
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "image-generator");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var outputPath = Path.Combine(outputDir, $"z-image_{timestamp}.png");

                    await File.WriteAllBytesAsync(outputPath, outputImage);
                    AddLog($"Output saved: {outputPath}");

                    ResultImagePath = outputPath;
                    LoadResultPreview(outputPath);
                    HasResultImage = true;

                    ProcessingProgress = 100;
                    ProcessingStatus = "Complete!";
                    StatusBarMessage = $"Image generation complete - {Path.GetFileName(outputPath)}";
                }
                else
                {
                    AddLog("WARNING: No output images received after all retries");
                    ProcessingStatus = "No output generated";
                    System.Windows.MessageBox.Show("No output images were generated. Please check the ComfyUI console for errors.", "Warning", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("Image generation cancelled by user");
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
                AddLog($"Stack Trace: {ex.StackTrace}");

                _logger.LogError($"Error generating image: {ex}");

                ProcessingStatus = "Error occurred";
                ProcessingProgress = 0;

                System.Windows.MessageBox.Show(
                    $"Error generating image:\n\n{ex.Message}\n\nCheck the log for more details.",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
                AddLog("=== Image generation ended ===");
            }
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            // Update prompt (node 45 - CLIPTextEncode)
            if (workflowDict.ContainsKey("45"))
            {
                var node45 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["45"].GetRawText());
                if (node45 != null && node45.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node45["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = ImagePrompt;
                        node45["inputs"] = inputs;
                        workflowDict["45"] = JsonSerializer.SerializeToElement(node45);
                    }
                }
            }

            // Update sampler settings (node 44 - KSampler)
            if (workflowDict.ContainsKey("44"))
            {
                var node44 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["44"].GetRawText());
                if (node44 != null && node44.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node44["inputs"]));
                    if (inputs != null)
                    {
                        // Generate random seed if seed is 0
                        var actualSeed = Seed == 0 ? new Random().NextInt64(0, 999999999999999) : Seed;

                        inputs["seed"] = actualSeed;
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        inputs["denoise"] = Denoise;
                        node44["inputs"] = inputs;
                        workflowDict["44"] = JsonSerializer.SerializeToElement(node44);
                    }
                }
            }

            // Update aspect ratio (node 57 - CR Aspect Ratio)
            if (workflowDict.ContainsKey("57"))
            {
                var aspectRatios = new[]
                {
                    "SDXL - 1:1 square 1024x1024",
                    "SDXL - 3:4 portrait 896x1152",
                    "SDXL - 9:16 portrait 768x1344",
                    "SDXL - 4:3 landscape 1152x896",
                    "SDXL - 16:9 landscape 1344x768"
                };

                var selectedRatio = aspectRatios[Math.Min(AspectRatioIndex, aspectRatios.Length - 1)];

                var node57 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["57"].GetRawText());
                if (node57 != null && node57.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node57["inputs"]));
                    if (inputs != null)
                    {
                        inputs["aspect_ratio"] = selectedRatio;
                        node57["inputs"] = inputs;
                        workflowDict["57"] = JsonSerializer.SerializeToElement(node57);
                    }
                }
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private async Task<List<byte[]>> GetOutputImagesFromComfyUI(string promptId)
        {
            var images = new List<byte[]>();

            try
            {
                // Get the actual ComfyUI server settings
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";

                // Parse the URL to get server and port
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;
                var actualPort = uri.Port.ToString();

                // Check if ComfyUI is running locally or remotely
                bool isRemoteComfyUI = IsComfyUIRemote(actualServer);

                AddLog($"ComfyUI server: {actualServer}:{actualPort}");
                AddLog($"Is remote ComfyUI: {isRemoteComfyUI}");

                if (isRemoteComfyUI)
                {
                    AddLog("Detected remote ComfyUI server, downloading generated image...");

                    // First try the history API approach
                    var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
                    AddLog($"Found {outputFiles.Count} potential output files");

                    // Look for z-image files in the output
                    var zImageFiles = outputFiles.Where(f => f.StartsWith("z-image_") && f.EndsWith(".png")).ToList();

                    if (zImageFiles.Any())
                    {
                        // Download the most recent z-image file
                        var filename = zImageFiles.Last(); // Get the last/most recent
                        AddLog($"Downloading generated image: {filename}");

                        var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filename);
                        if (imageData != null)
                        {
                            images.Add(imageData);
                            AddLog($"Successfully downloaded image ({imageData.Length} bytes)");
                        }
                        else
                        {
                            AddLog($"Failed to download image: {filename}");
                        }
                    }
                    else
                    {
                        AddLog("No z-image files found in history, trying alternative approach...");

                        // Try the fallback approach
                        var fallbackImage = await _comfyUIService.HttpClient.TryDownloadRecentOutputAsync(promptId);
                        if (fallbackImage != null)
                        {
                            images.Add(fallbackImage);
                            AddLog($"Successfully downloaded image via fallback method ({fallbackImage.Length} bytes)");
                        }
                        else
                        {
                            AddLog("Failed to download image using all available methods");
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
                }
                else
                {
                    // Local ComfyUI - check the output folder directly
                    var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                    if (string.IsNullOrEmpty(comfyUIOutputDir))
                    {
                        AddLog("ERROR: ComfyUI output folder not configured");
                        AddLog("Please restart the application and configure the ComfyUI folder path");
                        return images;
                    }

                    if (!Directory.Exists(comfyUIOutputDir))
                    {
                        AddLog($"ERROR: ComfyUI output folder not found: {comfyUIOutputDir}");
                        AddLog("Please check the ComfyUI folder configuration in settings");
                        return images;
                    }

                    // Look for z-image files
                    var imageFiles = Directory.GetFiles(comfyUIOutputDir, "z-image_*.png")
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .ToList();

                    if (imageFiles.Any())
                    {
                        var latestFile = imageFiles.First();
                        var fileAge = DateTime.Now - File.GetLastWriteTime(latestFile);

                        // Only use files created in the last 60 seconds
                        if (fileAge.TotalSeconds < 60)
                        {
                            AddLog($"Found output image: {Path.GetFileName(latestFile)}");
                            var imageData = await File.ReadAllBytesAsync(latestFile);
                            images.Add(imageData);
                        }
                        else
                        {
                            AddLog($"Latest file is too old ({fileAge.TotalSeconds:F0} seconds), waiting for new output...");
                        }
                    }
                    else
                    {
                        AddLog("No z-image output files found yet...");
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR retrieving output images: {ex.Message}");
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

                AddLog("Result image preview loaded");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading result preview: {ex.Message}");
            }
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
                AddLog($"ERROR opening result folder: {ex.Message}");
            }
        }

        private void SendToCameraEdit()
        {
            if (!HasResultImage || _serviceProvider == null) return;

            try
            {
                var cameraEditWindow = _serviceProvider.GetService(typeof(FlipPixWindow)) as FlipPixWindow;
                if (cameraEditWindow != null)
                {
                    cameraEditWindow.Show();

                    if (cameraEditWindow.DataContext is FlipPixViewModel viewModel)
                    {
                        viewModel.SetImagePath(ResultImagePath);
                    }

                    AddLog($"Sent image to Camera Edit: {Path.GetFileName(ResultImagePath)}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR sending to Camera Edit: {ex.Message}");
                System.Windows.MessageBox.Show($"Error opening Camera Edit window:\n\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void SendToVideoGenerator()
        {
            if (!HasResultImage || _serviceProvider == null) return;

            try
            {
                var videoWindow = _serviceProvider.GetService(typeof(VideoGeneratorWindow)) as VideoGeneratorWindow;
                if (videoWindow != null)
                {
                    videoWindow.Show();

                    if (videoWindow.DataContext is VideoGeneratorViewModel viewModel)
                    {
                        viewModel.SetImagePath(ResultImagePath);
                    }

                    AddLog($"Sent image to Video Generator: {Path.GetFileName(ResultImagePath)}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR sending to Video Generator: {ex.Message}");
                System.Windows.MessageBox.Show($"Error opening Video Generator window:\n\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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

        private void NavigateToVideoGenerator()
        {
            if (_serviceProvider == null) return;

            try
            {
                var videoWindow = _serviceProvider.GetService(typeof(VideoGeneratorWindow)) as VideoGeneratorWindow;
                videoWindow?.Show();
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Video Generator: {ex.Message}");
            }
        }

        private void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogOutput += $"[{timestamp}] {message}\n";
            _logger.LogInfo(message);
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
