using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WpfApp = System.Windows.Application;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Services;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    public class InpaintEditorViewModel : INotifyPropertyChanged
    {
        private readonly ComfyUIService _comfyUIService;
        private readonly SettingsService _settingsService;
        private readonly IFileDialogService _fileDialogService;
        private readonly IAppLogger _logger;

        private string _sourceImagePath = string.Empty;
        private BitmapImage? _sourceImageSource;
        private bool _hasSourceImage;
        private string _prompt = string.Empty;
        private bool _isProcessing;
        private double _progress;
        private string _statusMessage = "Upload an image and paint the mask area to edit";
        private BitmapImage? _resultImageSource;
        private bool _hasResult;
        private string _resultImagePath = string.Empty;
        private int _brushSize = 40;
        private string _logOutput = string.Empty;
        private CancellationTokenSource? _cts;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public InpaintEditorViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            SettingsService settingsService,
            IFileDialogService fileDialogService)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            BrowseImageCommand = new RelayCommand(async () => await BrowseImageAsync());
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            OpenResultImageCommand = new RelayCommand(OpenResultImage, () => HasResult);
        }

        public string SourceImagePath
        {
            get => _sourceImagePath;
            set { _sourceImagePath = value; OnPropertyChanged(); }
        }

        public BitmapImage? SourceImageSource
        {
            get => _sourceImageSource;
            set { _sourceImageSource = value; OnPropertyChanged(); }
        }

        public bool HasSourceImage
        {
            get => _hasSourceImage;
            set
            {
                _hasSourceImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
                OnPropertyChanged(nameof(NoSourceImage));
            }
        }

        public bool NoSourceImage => !_hasSourceImage;

        public string Prompt
        {
            get => _prompt;
            set
            {
                _prompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
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
                BrowseImageCommand.NotifyCanExecuteChanged();
            }
        }

        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); }
        }

        public string ProgressText => $"{Progress:F0}%";

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public BitmapImage? ResultImageSource
        {
            get => _resultImageSource;
            set { _resultImageSource = value; OnPropertyChanged(); }
        }

        public bool HasResult
        {
            get => _hasResult;
            set
            {
                _hasResult = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoResult));
                OpenResultFolderCommand.NotifyCanExecuteChanged();
                OpenResultImageCommand.NotifyCanExecuteChanged();
            }
        }

        public bool NoResult => !_hasResult;

        public string ResultImagePath
        {
            get => _resultImagePath;
            set { _resultImagePath = value; OnPropertyChanged(); }
        }

        public int BrushSize
        {
            get => _brushSize;
            set { _brushSize = value; OnPropertyChanged(); }
        }

        public string LogOutput
        {
            get => _logOutput;
            set { _logOutput = value; OnPropertyChanged(); }
        }

        public bool CanGenerate => HasSourceImage && !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing;

        public RelayCommand BrowseImageCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand OpenResultImageCommand { get; }

        private async Task BrowseImageAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Source Image",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp");
            if (!string.IsNullOrEmpty(path))
                SetSourceImage(path);
        }

        public void SetSourceImage(string path)
        {
            if (!File.Exists(path)) return;
            SourceImagePath = path;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                SourceImageSource = bmp;
                HasSourceImage = true;
                AddLog($"Loaded image: {Path.GetFileName(path)} ({bmp.PixelWidth}x{bmp.PixelHeight})");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading image: {ex.Message}");
            }
        }

        public async Task RunInpaintAsync(string combinedMaskedImagePath)
        {
            if (!CanGenerate) return;

            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                IsProcessing = true;
                Progress = 0;
                StatusMessage = "Connecting to ComfyUI...";
                AddLog("=== Starting inpaint ===");
                AddLog($"Prompt: {Prompt}");

                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(_cts.Token);
                    AddLog("Connected to ComfyUI");
                }

                Progress = 10;
                StatusMessage = "Uploading masked image...";
                AddLog($"Uploading: {Path.GetFileName(combinedMaskedImagePath)}");

                var uploadedFilename = await _comfyUIService.UploadImageAsync(combinedMaskedImagePath, _cts.Token);
                AddLog($"Uploaded as: {uploadedFilename}");

                Progress = 25;
                StatusMessage = "Loading workflow...";

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "qwen", "Qwen_Inpaint_v4API.json");
                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow not found: {workflowPath}");

                var workflowJson = await File.ReadAllTextAsync(workflowPath, _cts.Token);
                var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson)
                    ?? throw new InvalidOperationException("Failed to parse workflow JSON");

                // Update positive prompt (node 6 - CLIPTextEncode)
                UpdateWorkflowNode(workflowDict, "6", inputs => inputs["text"] = Prompt);
                AddLog("Updated prompt node");

                // Update image source (node 435 - LoadAndResizeImage)
                UpdateWorkflowNode(workflowDict, "435", inputs => inputs["image"] = $"{uploadedFilename} [input]");
                AddLog($"Updated image node: {uploadedFilename} [input]");

                // Update SaveImage prefix so we can find the result
                UpdateWorkflowNode(workflowDict, "378", inputs => inputs["filename_prefix"] = "qwen-inpaint");

                // Randomize KSampler seed (node 500 is baked into the workflow JSON)
                var seed = new Random().NextInt64(0, 999_999_999_999_999L);
                UpdateWorkflowNode(workflowDict, "500", inputs => inputs["seed"] = seed);
                AddLog($"KSampler seed: {seed}");

                var updatedWorkflow = JsonSerializer.SerializeToElement(workflowDict);

                Progress = 35;
                StatusMessage = "Running inpaint...";
                AddLog("Executing workflow...");

                var progressReporter = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        WpfApp.Current?.Dispatcher.Invoke(() =>
                        {
                            Progress = 35 + pct * 0.55;
                            StatusMessage = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progressReporter, _cts.Token);
                AddLog($"Workflow submitted, prompt ID: {promptId}");

                Progress = 90;
                StatusMessage = "Retrieving result...";

                var outputBytes = await RetrieveOutputAsync(promptId);
                if (outputBytes != null)
                {
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "edited-images");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var outputPath = Path.Combine(outputDir, $"inpaint_{timestamp}.png");
                    await File.WriteAllBytesAsync(outputPath, outputBytes, _cts.Token);
                    AddLog($"Saved result: {outputPath}");

                    ResultImagePath = outputPath;
                    WpfApp.Current?.Dispatcher.Invoke(() => LoadResultImage(outputPath));
                    HasResult = true;
                    Progress = 100;
                    StatusMessage = $"Done! Saved to edited-images/{Path.GetFileName(outputPath)}";
                }
                else
                {
                    StatusMessage = "No result image found — check ComfyUI logs";
                    AddLog("WARNING: No output image retrieved after retries");
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Cancelled";
                AddLog("Generation cancelled");
                Progress = 0;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                AddLog($"ERROR: {ex.GetType().Name}: {ex.Message}");
                _logger.LogError($"Inpaint error: {ex}");
            }
            finally
            {
                IsProcessing = false;
                AddLog("=== Inpaint ended ===");
                TryDeleteTempFile(combinedMaskedImagePath);
            }
        }

        private static void UpdateWorkflowNode(
            Dictionary<string, JsonElement> dict,
            string nodeId,
            Action<Dictionary<string, object>> updater)
        {
            if (!dict.ContainsKey(nodeId)) return;

            var node = JsonSerializer.Deserialize<Dictionary<string, object>>(dict[nodeId].GetRawText());
            if (node == null || !node.ContainsKey("inputs")) return;

            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(node["inputs"]));
            if (inputs == null) return;

            updater(inputs);
            node["inputs"] = inputs;
            dict[nodeId] = JsonSerializer.SerializeToElement(node);
        }

        private async Task<byte[]?> RetrieveOutputAsync(string promptId)
        {
            var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
            Uri uri;
            try { uri = new Uri(baseUrl); }
            catch { uri = new Uri("http://127.0.0.1:8188"); }

            bool isRemote = !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

            const int maxRetries = 20;
            const int retryDelayMs = 5000;

            if (isRemote)
            {
                for (int i = 0; i < maxRetries; i++)
                {
                    if (i > 0)
                    {
                        AddLog($"Retry {i}/{maxRetries} — waiting...");
                        await Task.Delay(retryDelayMs, _cts!.Token);
                    }

                    _cts!.Token.ThrowIfCancellationRequested();
                    var files = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                    AddLog($"History returned {files.Count} file(s): {string.Join(", ", files)}");

                    // Prefer the SaveImage output (qwen-inpaint prefix) — skip PreviewImage temp files
                    var imgFile = files.FirstOrDefault(f =>
                        Path.GetFileName(f).StartsWith("qwen-inpaint", StringComparison.OrdinalIgnoreCase) &&
                        IsImageExtension(f));

                    // Fallback: any non-temp PNG from the history
                    if (imgFile == null)
                    {
                        imgFile = files.FirstOrDefault(f =>
                            IsImageExtension(f) &&
                            !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase));
                    }

                    if (imgFile != null)
                    {
                        AddLog($"Downloading: {imgFile}");
                        var data = await _comfyUIService.HttpClient.DownloadOutputImageAsync(imgFile);
                        if (data != null)
                        {
                            AddLog($"Downloaded {data.Length} bytes");
                            return data;
                        }
                        AddLog($"Download returned null for {imgFile}");
                    }
                }
                return null;
            }
            else
            {
                var outputDir = _settingsService.Settings?.OutputFolderPath;
                if (string.IsNullOrEmpty(outputDir))
                {
                    AddLog("ERROR: ComfyUI output folder not configured in settings");
                    return null;
                }

                for (int i = 0; i < maxRetries; i++)
                {
                    if (i > 0)
                    {
                        AddLog($"Retry {i}/{maxRetries} — waiting for output file...");
                        await Task.Delay(retryDelayMs, _cts!.Token);
                    }

                    _cts!.Token.ThrowIfCancellationRequested();

                    var files = Directory.GetFiles(outputDir, "qwen-inpaint_*.png", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTime)
                        .ToList();

                    if (files.Any())
                    {
                        var latest = files[0];
                        var age = DateTime.Now - File.GetLastWriteTime(latest);
                        AddLog($"Found: {Path.GetFileName(latest)} ({age.TotalSeconds:F0}s old)");
                        return await File.ReadAllBytesAsync(latest, _cts!.Token);
                    }
                }
                return null;
            }
        }

        private static bool IsImageExtension(string filename) =>
            filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            filename.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            filename.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

        private void LoadResultImage(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                ResultImageSource = bmp;
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading result preview: {ex.Message}");
            }
        }

        private void OpenResultFolder()
        {
            var folder = Path.GetDirectoryName(ResultImagePath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }

        private void OpenResultImage()
        {
            if (File.Exists(ResultImagePath))
                Process.Start(new ProcessStartInfo { FileName = ResultImagePath, UseShellExecute = true });
        }

        private static void TryDeleteTempFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private void AddLog(string msg)
        {
            WpfApp.Current?.Dispatcher.Invoke(() =>
            {
                var entry = $"{DateTime.Now:HH:mm:ss}  {msg}\n";
                LogOutput = entry + LogOutput;
                if (LogOutput.Length > 8000)
                    LogOutput = LogOutput[..8000];
            });
        }
    }
}
