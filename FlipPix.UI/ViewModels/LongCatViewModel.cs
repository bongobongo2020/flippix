using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Services;
using System.Diagnostics;

namespace FlipPix.UI.ViewModels;

public partial class LongCatViewModel : ObservableObject, IDisposable
{
    private readonly ComfyUIService _comfyUIService;
    private readonly IAppLogger _logger;
    private readonly ImageAnalysisService _imageAnalysisService;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cancellationTokenSource;
    private double _originalAspectRatio = 1.0;
    private bool _isUpdatingDimensions = false;
    private string? _currentPromptId = null;

    [ObservableProperty]
    private string _imageFilePath = string.Empty;

    [ObservableProperty]
    private BitmapImage? _imagePreviewSource;

    [ObservableProperty]
    private string _imageInfo = "No image selected";

    [ObservableProperty]
    private int _videoLengthSeconds = 6;

    [ObservableProperty]
    private int _fPS = 16;

    [ObservableProperty]
    private int _width = 512;

    [ObservableProperty]
    private int _height = 512;

    [ObservableProperty]
    private bool _maintainAspectRatio = true;

    [ObservableProperty]
    private int _totalFrames = 96;

    [ObservableProperty]
    private string _comfyUIServer = "192.168.1.237";

    [ObservableProperty]
    private string _comfyUIPort = "8188";

    [ObservableProperty]
    private string _connectionStatus = "Not connected";

    [ObservableProperty]
    private bool _canGenerate = false;

    [ObservableProperty]
    private bool _isProcessing = false;

    [ObservableProperty]
    private string _processingStatus = "Ready";

    [ObservableProperty]
    private double _processingProgress = 0.0;

    [ObservableProperty]
    private string _progressPercentage = "0%";

    [ObservableProperty]
    private string _logOutput = string.Empty;

    [ObservableProperty]
    private string _statusBarMessage = "Ready to generate video";

    [ObservableProperty]
    private string _currentTime = DateTime.Now.ToString("HH:mm:ss");

    [ObservableProperty]
    private bool _hasOutputVideo = false;

    [ObservableProperty]
    private string _outputVideoPath = string.Empty;

    public LongCatViewModel(ComfyUIService comfyUIService, IAppLogger logger, ImageAnalysisService imageAnalysisService)
    {
        _comfyUIService = comfyUIService;
        _logger = logger;
        _imageAnalysisService = imageAnalysisService;

        // Initialize timer for current time
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (s, e) => CurrentTime = DateTime.Now.ToString("HH:mm:ss");
        _timer.Start();

        // Subscribe to property changes
        PropertyChanged += OnPropertyChanged;

        // Subscribe to ComfyUI events
        _comfyUIService.ProgressUpdated += OnProgressUpdated;
        _comfyUIService.ExecutionCompleted += OnExecutionCompleted;

        UpdateCanGenerate();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ImageFilePath):
                UpdateCanGenerate();
                break;
            case nameof(VideoLengthSeconds):
            case nameof(FPS):
                UpdateTotalFrames();
                break;
            case nameof(Width):
                if (MaintainAspectRatio && !_isUpdatingDimensions)
                {
                    _isUpdatingDimensions = true;
                    Height = (int)Math.Round(Width / _originalAspectRatio / 16) * 16;
                    _isUpdatingDimensions = false;
                }
                break;
            case nameof(Height):
                if (MaintainAspectRatio && !_isUpdatingDimensions)
                {
                    _isUpdatingDimensions = true;
                    Width = (int)Math.Round(Height * _originalAspectRatio / 16) * 16;
                    _isUpdatingDimensions = false;
                }
                break;
            case nameof(ProcessingProgress):
                ProgressPercentage = $"{ProcessingProgress:F1}%";
                break;
        }
    }

    private void UpdateTotalFrames()
    {
        TotalFrames = VideoLengthSeconds * FPS;
    }

    private void UpdateCanGenerate()
    {
        CanGenerate = !IsProcessing && !string.IsNullOrEmpty(ImageFilePath) && File.Exists(ImageFilePath);
    }

    [RelayCommand]
    private async Task SelectImageAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Image",
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All Files|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            ImageFilePath = dialog.FileName;
            await LoadImagePreviewAsync(dialog.FileName);
            await UpdateImageInfoAsync(dialog.FileName);
        }
    }

    private async Task LoadImagePreviewAsync(string imagePath)
    {
        try
        {
            await Task.Run(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 400; // Limit preview size
                bitmap.EndInit();
                bitmap.Freeze(); // Make it cross-thread accessible

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ImagePreviewSource = bitmap;
                });
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load image preview");
            AddLog($"Error loading image preview: {ex.Message}");
        }
    }

    private async Task UpdateImageInfoAsync(string imagePath)
    {
        try
        {
            var imageInfo = await Task.Run(() => _imageAnalysisService.AnalyzeImage(imagePath));

            var fileInfo = new System.IO.FileInfo(imagePath);
            var fileSize = fileInfo.Length;
            var format = Path.GetExtension(imagePath).TrimStart('.').ToUpper();

            ImageInfo = $"Resolution: {imageInfo.Width}×{imageInfo.Height} | " +
                       $"Format: {format} | " +
                       $"Size: {FormatFileSize(fileSize)}";

            // Store original aspect ratio
            _originalAspectRatio = (double)imageInfo.Width / imageInfo.Height;

            // Update dimensions to match image aspect ratio
            _isUpdatingDimensions = true;

            // Calculate dimensions that are divisible by 16 and maintain aspect ratio
            if (imageInfo.Width >= imageInfo.Height)
            {
                Width = 512;
                Height = (int)Math.Round(512 / _originalAspectRatio / 16) * 16;
            }
            else
            {
                Height = 512;
                Width = (int)Math.Round(512 * _originalAspectRatio / 16) * 16;
            }

            _isUpdatingDimensions = false;

            AddLog($"Image loaded: {Path.GetFileName(imagePath)} ({imageInfo.Width}×{imageInfo.Height})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze image");
            AddLog($"Error analyzing image: {ex.Message}");
        }
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    [RelayCommand]
    private async Task GenerateVideoAsync()
    {
        try
        {
            IsProcessing = true;
            UpdateCanGenerate();
            HasOutputVideo = false;
            OutputVideoPath = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = "Initializing...";

            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            AddLog("Starting video generation...");
            AddLog($"Parameters: {Width}×{Height}, {VideoLengthSeconds}s @ {FPS}fps = {TotalFrames} frames");

            // Calculate number of chunks
            int chunksNeeded = (int)Math.Ceiling(TotalFrames / 81.0);
            AddLog($"Using chunked generation: {chunksNeeded} chunks × 81 frames to prevent OOM errors");

            // Connect to ComfyUI
            ProcessingStatus = "Connecting to ComfyUI...";
            AddLog($"Connecting to ComfyUI at {ComfyUIServer}:{ComfyUIPort}");

            if (!_comfyUIService.IsConnected)
            {
                await _comfyUIService.ConnectAsync(cancellationToken);
            }

            ConnectionStatus = "Connected ✓";
            AddLog("Connected to ComfyUI successfully");

            // Upload image
            ProcessingStatus = "Uploading image...";
            AddLog($"Uploading image: {Path.GetFileName(ImageFilePath)}");

            var uploadedImagePath = await _comfyUIService.UploadImageAsync(ImageFilePath, cancellationToken);
            AddLog($"Image uploaded: {uploadedImagePath}");

            // Load and modify workflow
            ProcessingStatus = "Preparing chunked workflow...";
            AddLog("Loading LongCat chunked workflow...");

            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "LongCatAPI_Chunked.json");
            if (!File.Exists(workflowPath))
            {
                throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
            }

            var workflowJson = await File.ReadAllTextAsync(workflowPath, cancellationToken);
            var workflow = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowJson);

            if (workflow == null)
            {
                throw new InvalidOperationException("Failed to parse workflow JSON");
            }

            // Modify workflow parameters
            ModifyWorkflowParameters(workflow, uploadedImagePath);
            AddLog("Workflow configured with custom parameters");

            // Submit workflow
            ProcessingStatus = "Generating video...";
            AddLog("Submitting workflow to ComfyUI...");

            _currentPromptId = await _comfyUIService.QueuePromptAsync(workflow, cancellationToken);
            AddLog($"Workflow submitted with prompt ID: {_currentPromptId}");

            StatusBarMessage = "Video generation in progress...";

            // Start a fallback timer in case ExecutionCompleted event doesn't fire
            StartCompletionFallbackTimer();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate video");
            AddLog($"ERROR: {ex.Message}");
            ProcessingStatus = "Error occurred";
            IsProcessing = false;
            UpdateCanGenerate();
        }
    }

    private void ModifyWorkflowParameters(Dictionary<string, object> workflow, string uploadedImagePath)
    {
        try
        {
            var workflowElement = JsonSerializer.SerializeToElement(workflow);
            var nodes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowElement.GetRawText());

            if (nodes == null) return;

            // Update node 12 (LoadImage) with uploaded image
            if (nodes.ContainsKey("12"))
            {
                var node12 = JsonSerializer.Deserialize<Dictionary<string, object>>(nodes["12"].GetRawText());
                if (node12 != null && node12.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.SerializeToElement(node12["inputs"]).GetRawText());

                    if (inputs != null)
                    {
                        inputs["image"] = Path.GetFileName(uploadedImagePath);
                        node12["inputs"] = inputs;
                        nodes["12"] = JsonSerializer.SerializeToElement(node12);
                    }
                }
            }

            // Update node 100 (Video Length in Seconds)
            if (nodes.ContainsKey("100"))
            {
                var node100 = JsonSerializer.Deserialize<Dictionary<string, object>>(nodes["100"].GetRawText());
                if (node100 != null && node100.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.SerializeToElement(node100["inputs"]).GetRawText());

                    if (inputs != null)
                    {
                        inputs["value"] = VideoLengthSeconds;
                        node100["inputs"] = inputs;
                        nodes["100"] = JsonSerializer.SerializeToElement(node100);
                    }
                }
            }

            // Update node 101 (FPS)
            if (nodes.ContainsKey("101"))
            {
                var node101 = JsonSerializer.Deserialize<Dictionary<string, object>>(nodes["101"].GetRawText());
                if (node101 != null && node101.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.SerializeToElement(node101["inputs"]).GetRawText());

                    if (inputs != null)
                    {
                        inputs["value"] = FPS;
                        node101["inputs"] = inputs;
                        nodes["101"] = JsonSerializer.SerializeToElement(node101);
                    }
                }
            }

            // Update node 103 (Width)
            if (nodes.ContainsKey("103"))
            {
                var node103 = JsonSerializer.Deserialize<Dictionary<string, object>>(nodes["103"].GetRawText());
                if (node103 != null && node103.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.SerializeToElement(node103["inputs"]).GetRawText());

                    if (inputs != null)
                    {
                        inputs["value"] = Width;
                        node103["inputs"] = inputs;
                        nodes["103"] = JsonSerializer.SerializeToElement(node103);
                    }
                }
            }

            // Update node 104 (Height)
            if (nodes.ContainsKey("104"))
            {
                var node104 = JsonSerializer.Deserialize<Dictionary<string, object>>(nodes["104"].GetRawText());
                if (node104 != null && node104.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.SerializeToElement(node104["inputs"]).GetRawText());

                    if (inputs != null)
                    {
                        inputs["value"] = Height;
                        node104["inputs"] = inputs;
                        nodes["104"] = JsonSerializer.SerializeToElement(node104);
                    }
                }
            }

            // Convert back to Dictionary<string, object>
            workflow.Clear();
            foreach (var kvp in nodes)
            {
                workflow[kvp.Key] = JsonSerializer.Deserialize<object>(kvp.Value.GetRawText())!;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to modify workflow parameters");
            throw;
        }
    }

    private void OnProgressUpdated(object? sender, ComfyUI.Models.ProgressMessage e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (e.Data != null)
            {
                ProcessingProgress = (e.Data.Value / (double)e.Data.Max) * 100;
                ProcessingStatus = $"Processing: {e.Data.Value}/{e.Data.Max}";
            }
        });
    }

    private void OnExecutionCompleted(object? sender, ComfyUI.Models.ExecutionCompleteMessage e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            AddLog($"Execution completed event received. Prompt ID: {e.Data?.PromptId}, Current: {_currentPromptId}");

            // Only process if this matches our current prompt (or if we're not tracking)
            if (_currentPromptId == null || e.Data?.PromptId == _currentPromptId)
            {
                CompleteGeneration();
            }
        });
    }

    private void CompleteGeneration()
    {
        ProcessingProgress = 100;
        ProcessingStatus = "Video generated successfully!";
        IsProcessing = false;
        _currentPromptId = null;

        // Explicitly update the CanGenerate property
        UpdateCanGenerate();

        // Try to find the output video path
        FindOutputVideo();

        AddLog("Video generation completed successfully!");
        StatusBarMessage = "Video generation completed ✓";
        AddLog($"Button state: CanGenerate={CanGenerate}, IsProcessing={IsProcessing}");
    }

    private void FindOutputVideo()
    {
        try
        {
            // Look for the most recent video in ComfyUI output folder
            var outputFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "comfy-kontext2", "ComfyUI_windows_portable", "ComfyUI", "output");

            if (Directory.Exists(outputFolder))
            {
                var videoFiles = Directory.GetFiles(outputFolder, "LongCat_Video*.mp4", SearchOption.AllDirectories)
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();

                if (videoFiles.Any())
                {
                    OutputVideoPath = videoFiles.First();
                    HasOutputVideo = true;
                    AddLog($"Output video: {Path.GetFileName(OutputVideoPath)}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find output video");
        }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        try
        {
            if (!string.IsNullOrEmpty(OutputVideoPath) && File.Exists(OutputVideoPath))
            {
                Process.Start("explorer.exe", $"/select,\"{OutputVideoPath}\"");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open output folder");
            AddLog($"Error opening output folder: {ex.Message}");
        }
    }

    private void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogOutput += $"[{timestamp}] {message}\n";
    }

    private void StartCompletionFallbackTimer()
    {
        // Create a fallback timer that will reset state after 5 minutes if no completion event is received
        var fallbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };

        fallbackTimer.Tick += (s, e) =>
        {
            fallbackTimer.Stop();
            if (IsProcessing)
            {
                AddLog("Warning: Completion event not received after 5 minutes. Resetting state...");
                CompleteGeneration();
            }
        };

        fallbackTimer.Start();
    }

    public async void SetInitialImage(string imagePath)
    {
        if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
        {
            ImageFilePath = imagePath;
            await LoadImagePreviewAsync(imagePath);
            await UpdateImageInfoAsync(imagePath);
            AddLog($"Initial image loaded: {Path.GetFileName(imagePath)}");
        }
    }

    public void Dispose()
    {
        _timer?.Stop();
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
    }
}
