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
using WpfApp = System.Windows.Application;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Services;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    /// <summary>
    /// Restore tab: a single-image Flux.2 Klein restoration pass. The source image is
    /// upscaled to a target megapixel budget, re-rendered with a guidance prompt, then
    /// realigned (Pixel Drift Fix) and blended back over the original to keep the
    /// restoration faithful. Workflow: workflow/image/klein/klein_restorationAPI.json.
    /// </summary>
    public class RestoreViewModel : INotifyPropertyChanged
    {
        private const string WorkflowFile = "workflow/image/klein/klein_restorationAPI.json";
        private const string SavePrefix = "klein_restore";
        private const string DefaultPrompt = "enhance image\nadd soft back light";

        // Workflow node ids in klein_restorationAPI.json.
        private const string LoadImageNode = "76";   // LoadImage (source)
        private const string PromptNode = "107";     // CLIPTextEncode (guidance prompt)
        private const string SeedNode = "104";       // RandomNoise (noise_seed)
        private const string ScaleNode = "109";      // ImageScaleToTotalPixels (megapixels)
        private const string BlendNode = "164";      // ImageBlend (blend_factor)

        private readonly ComfyUIService _comfyUIService;
        private readonly SettingsService _settingsService;
        private readonly IFileDialogService _fileDialogService;
        private readonly IAppLogger _logger;

        // Source image
        private string _sourceImagePath = string.Empty;
        private BitmapImage? _sourceImageSource;
        private bool _hasSourceImage;

        // Settings
        private string _prompt = DefaultPrompt;
        private double _blendFactor = 0.25;
        private double _megapixels = 2;

        // Workflow state
        private bool _isGenerating;
        private double _progress;
        private string _statusMessage = "Upload an image to restore";
        private string _logOutput = string.Empty;
        private CancellationTokenSource? _cts;

        // Result
        private BitmapImage? _resultImageSource;
        private bool _hasResult;
        private string _resultImagePath = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public RestoreViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            SettingsService settingsService,
            IFileDialogService fileDialogService)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            BrowseSourceImageCommand = new RelayCommand(async () => await BrowseSourceImageAsync(), () => !IsBusy);
            GenerateCommand = new RelayCommand(async () => await GenerateAsync(), () => CanGenerate);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            OpenResultImageCommand = new RelayCommand(OpenResultImage, () => HasResult);
        }

        // ── Source image ─────────────────────────────────────────────────────
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
                OnPropertyChanged(nameof(NoSourceImage));
                OnPropertyChanged(nameof(CanGenerate));
                NotifyCommands();
            }
        }

        public bool NoSourceImage => !_hasSourceImage;

        // ── Settings ─────────────────────────────────────────────────────────
        public string Prompt
        {
            get => _prompt;
            set { _prompt = value; OnPropertyChanged(); }
        }

        /// <summary>How much of the original image is blended back over the restored
        /// result (0 = fully restored, 1 = original). Maps to ImageBlend.blend_factor.</summary>
        public double BlendFactor
        {
            get => _blendFactor;
            set { _blendFactor = Math.Max(0, Math.Min(1, value)); OnPropertyChanged(); OnPropertyChanged(nameof(BlendFactorText)); }
        }

        public string BlendFactorText => $"{_blendFactor:0.00}";

        /// <summary>Target resolution the source is rescaled to before restoration.</summary>
        public double Megapixels
        {
            get => _megapixels;
            set { _megapixels = Math.Max(0.25, Math.Min(8, value)); OnPropertyChanged(); OnPropertyChanged(nameof(MegapixelsText)); }
        }

        public string MegapixelsText => $"{_megapixels:0.0} MP";

        // ── Workflow state ───────────────────────────────────────────────────
        public bool IsGenerating
        {
            get => _isGenerating;
            set
            {
                _isGenerating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanGenerate));
                NotifyCommands();
            }
        }

        public bool IsBusy => _isGenerating;

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

        public string LogOutput
        {
            get => _logOutput;
            set { _logOutput = value; OnPropertyChanged(); }
        }

        // ── Result ───────────────────────────────────────────────────────────
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

        public bool CanGenerate => HasSourceImage && !IsBusy;

        // ── Commands ─────────────────────────────────────────────────────────
        public RelayCommand BrowseSourceImageCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand OpenResultImageCommand { get; }

        private void NotifyCommands()
        {
            BrowseSourceImageCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
        }

        // ── Browse / load ────────────────────────────────────────────────────
        private async Task BrowseSourceImageAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Image to Restore",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "restore.source-image");
            if (!string.IsNullOrEmpty(path))
                SetSourceImage(path);
        }

        public void SetSourceImage(string path)
        {
            if (!File.Exists(path)) return;
            SourceImagePath = path;
            try
            {
                SourceImageSource = LoadBitmap(path);
                HasSourceImage = true;
                AddLog($"Source: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { AddLog($"ERROR loading image: {ex.Message}"); }
        }

        // ── Generate ─────────────────────────────────────────────────────────
        private async Task GenerateAsync()
        {
            if (!CanGenerate) return;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                IsGenerating = true;
                Progress = 0;
                StatusMessage = "Connecting to ComfyUI...";
                AddLog("=== Restore ===");

                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(_cts.Token);
                    AddLog("Connected");
                }

                Progress = 8;
                StatusMessage = "Uploading image...";
                var uploaded = await _comfyUIService.UploadImageAsync(SourceImagePath, _cts.Token);
                AddLog($"image={uploaded}");

                Progress = 18;
                StatusMessage = "Building workflow...";
                var workflow = BuildWorkflow(uploaded);

                var progressReporter = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        WpfApp.Current?.Dispatcher.Invoke(() =>
                        {
                            Progress = 18 + pct * 0.74;
                            StatusMessage = $"Restoring: {msg.Data.Value}/{msg.Data.Max}";
                        });
                    }
                });

                StatusMessage = "Running ComfyUI...";
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progressReporter, _cts.Token);
                AddLog($"Done: {promptId}");

                Progress = 94;
                StatusMessage = "Retrieving image...";
                var bytes = await RetrieveOutputImageAsync(promptId, _cts.Token);
                if (bytes != null)
                {
                    await SaveAndDisplayResultAsync(bytes, _cts.Token);
                    Progress = 100;
                    StatusMessage = $"Done! {Path.GetFileName(ResultImagePath)}";
                }
                else
                {
                    StatusMessage = "No result — check ComfyUI logs";
                    AddLog("WARNING: No output image retrieved");
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Cancelled";
                AddLog("Cancelled");
                Progress = 0;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                AddLog($"ERROR: {ex.GetType().Name}: {ex.Message}");
                _logger.LogError($"Restore generate: {ex}");
            }
            finally
            {
                IsGenerating = false;
                AddLog("=== Restore ended ===");
            }
        }

        // ── Workflow building ────────────────────────────────────────────────
        private JsonElement BuildWorkflow(string uploadedImage)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse workflow JSON");

            UpdateNode(dict, LoadImageNode, inputs => inputs["image"] = uploadedImage);
            UpdateNode(dict, SeedNode, inputs => inputs["noise_seed"] = new Random().NextInt64(0, 999_999_999_999_999L));
            UpdateNode(dict, ScaleNode, inputs => inputs["megapixels"] = Megapixels);
            UpdateNode(dict, BlendNode, inputs => inputs["blend_factor"] = BlendFactor);

            var promptText = string.IsNullOrWhiteSpace(Prompt) ? DefaultPrompt : Prompt;
            UpdateNode(dict, PromptNode, inputs => inputs["text"] = promptText);

            return JsonSerializer.SerializeToElement(dict);
        }

        private static void UpdateNode(
            Dictionary<string, JsonElement> dict,
            string nodeId,
            Action<Dictionary<string, object>> updater)
        {
            if (!dict.ContainsKey(nodeId)) return;
            var node = JsonSerializer.Deserialize<Dictionary<string, object>>(dict[nodeId].GetRawText());
            if (node == null || !node.ContainsKey("inputs")) return;
            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
            if (inputs == null) return;
            updater(inputs);
            node["inputs"] = inputs;
            dict[nodeId] = JsonSerializer.SerializeToElement(node);
        }

        // ── Output image retrieval ───────────────────────────────────────────
        private async Task<byte[]?> RetrieveOutputImageAsync(string promptId, CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
            Uri uri;
            try { uri = new Uri(baseUrl); } catch { uri = new Uri("http://127.0.0.1:8188"); }
            bool isRemote = !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

            const int maxRetries = 20;
            const int retryDelayMs = 5000;

            if (isRemote)
            {
                for (int i = 0; i < maxRetries; i++)
                {
                    if (i > 0) { AddLog($"Retry {i}/{maxRetries}..."); await Task.Delay(retryDelayMs, token); }
                    token.ThrowIfCancellationRequested();
                    var files = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                    AddLog($"History: {files.Count} file(s)");
                    var imgFile = files.FirstOrDefault(f =>
                        Path.GetFileName(f).StartsWith(SavePrefix, StringComparison.OrdinalIgnoreCase) && IsImageExt(f));
                    imgFile ??= files.FirstOrDefault(f =>
                        IsImageExt(f) && !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase));
                    if (imgFile != null)
                    {
                        var data = await _comfyUIService.HttpClient.DownloadOutputImageAsync(imgFile);
                        if (data != null) { AddLog($"Downloaded {data.Length} bytes"); return data; }
                    }
                }
                return null;
            }
            else
            {
                var outputDir = _settingsService.Settings?.OutputFolderPath;
                if (string.IsNullOrEmpty(outputDir)) { AddLog("ERROR: Output folder not configured"); return null; }
                for (int i = 0; i < maxRetries; i++)
                {
                    if (i > 0) { AddLog($"Retry {i}/{maxRetries}..."); await Task.Delay(retryDelayMs, token); }
                    token.ThrowIfCancellationRequested();
                    var files = Directory.GetFiles(outputDir, $"{SavePrefix}_*.png", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTime).ToList();
                    if (files.Any())
                    {
                        var latest = files[0];
                        var age = DateTime.Now - File.GetLastWriteTime(latest);
                        AddLog($"Found: {Path.GetFileName(latest)} ({age.TotalSeconds:F0}s old)");
                        if (age.TotalSeconds < 120) return await File.ReadAllBytesAsync(latest, token);
                    }
                }
                return null;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private async Task SaveAndDisplayResultAsync(byte[] bytes, CancellationToken token)
        {
            var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "edited-images");
            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, $"klein-restore_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            await File.WriteAllBytesAsync(path, bytes, token);
            ResultImagePath = path;
            WpfApp.Current?.Dispatcher.Invoke(() => LoadResultImage(path));
            HasResult = true;
            AddLog($"Saved: {path}");
        }

        private void LoadResultImage(string path)
        {
            try { ResultImageSource = LoadBitmap(path); }
            catch (Exception ex) { AddLog($"ERROR loading result: {ex.Message}"); }
        }

        private static BitmapImage LoadBitmap(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private static bool IsImageExt(string f) =>
            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

        private void OpenResultFolder()
        {
            if (!string.IsNullOrEmpty(ResultImagePath) && File.Exists(ResultImagePath))
                Process.Start("explorer.exe", $"/select,\"{ResultImagePath}\"");
        }

        private void OpenResultImage()
        {
            if (!string.IsNullOrEmpty(ResultImagePath) && File.Exists(ResultImagePath))
                Process.Start(new ProcessStartInfo(ResultImagePath) { UseShellExecute = true });
        }

        private void AddLog(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            WpfApp.Current?.Dispatcher.Invoke(() => LogOutput = LogOutput + line + "\n");
            _logger.LogInfo(message);
        }
    }
}
