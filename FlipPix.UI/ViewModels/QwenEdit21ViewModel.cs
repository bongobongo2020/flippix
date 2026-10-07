using System;
using System.Collections.ObjectModel;
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
    /// Qwen Edit 2.1 tab: simple image editing with Qwen Image 2.1 model.
    /// Upload one source image, provide a prompt describing the desired edit,
    /// and generate a new image. Uses workflow:
    /// workflow/image/qwen-edit/image_qwen_image_2_1_image_edit-changephoto.json
    /// </summary>
    public class QwenEdit21ViewModel : INotifyPropertyChanged
    {
        private const string WorkflowFile = "workflow/image/qwen-edit/image_qwen_image_2_1_image_edit-changephoto.json";
        private const string SavePrefix = "Qwen_image_2.1";

        private readonly ComfyUIService _comfyUIService;
        private readonly SettingsService _settingsService;
        private readonly IFileDialogService _fileDialogService;
        private readonly IAppLogger _logger;
        private readonly WorkflowQueueCoordinator _workflowCoordinator;

        // Source image
        private string _sourceImagePath = string.Empty;
        private BitmapImage? _sourceImageSource;
        private bool _hasSourceImage;

        // Prompt settings
        private string _prompt = string.Empty;
        private string _negativePrompt = "(worst quality, low quality:1.4), (low resolution, blurry, blur:1.2), jpeg artifacts, compression artifacts, poorly drawn, bad anatomy, wrong anatomy, deformed, mutated, disfigured, ugly, disgusting, text, watermark, signature, logo, username, artist name, (cartoon, anime, illustration, painting, drawing, sketch:1.3), 3d render, cgi, digital art, unrealistic, artificial, oversaturated, smooth skin, plastic";

        // Aspect ratio options
        private readonly ObservableCollection<string> _aspectRatios = new()
        {
            "1:1 (Square)",
            "4:3 (Standard)",
            "3:4 (Portrait Standard)",
            "16:9 (Widescreen)",
            "9:16 (Portrait Widescreen)",
            "3:2 (Photo)",
            "2:3 (Portrait Photo)",
            "21:9 (Cinematic)"
        };
        private string _selectedAspectRatio = "9:16 (Portrait Widescreen)";

        // Generation settings
        private int _steps = 25;
        private double _cfg = 3.5;

        // Workflow state
        private bool _isGenerating;
        private double _progress;
        private string _statusMessage = "Upload an image to begin";
        private string _logOutput = string.Empty;
        private CancellationTokenSource? _cts;
        private DateTime _lastProgressLog = DateTime.MinValue;

        // Result
        private BitmapImage? _resultImageSource;
        private bool _hasResult;
        private string _resultImagePath = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public QwenEdit21ViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            SettingsService settingsService,
            IFileDialogService fileDialogService,
            WorkflowQueueCoordinator workflowCoordinator)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            _workflowCoordinator = workflowCoordinator ?? throw new ArgumentNullException(nameof(workflowCoordinator));

            BrowseSourceCommand = new RelayCommand(async () => await BrowseSourceAsync(), () => !IsGenerating);
            GenerateCommand = new RelayCommand(async () => await GenerateAsync(), () => CanGenerate);
            CancelCommand = new RelayCommand(Cancel, () => IsGenerating);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            OpenResultImageCommand = new RelayCommand(OpenResultImage, () => HasResult);
        }

        // ── Source Image ────────────────────────────────────────────────────────
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

        // ── Prompt ──────────────────────────────────────────────────────────────
        public string Prompt
        {
            get => _prompt;
            set
            {
                _prompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
                GenerateCommand.NotifyCanExecuteChanged();
            }
        }

        public string NegativePrompt
        {
            get => _negativePrompt;
            set { _negativePrompt = value; OnPropertyChanged(); }
        }

        // ── Aspect Ratio ────────────────────────────────────────────────────────
        public ObservableCollection<string> AspectRatios => _aspectRatios;

        public string SelectedAspectRatio
        {
            get => _selectedAspectRatio;
            set { _selectedAspectRatio = value; OnPropertyChanged(); }
        }

        // ── Generation Settings ─────────────────────────────────────────────────
        public int Steps
        {
            get => _steps;
            set { _steps = Math.Clamp(value, 1, 100); OnPropertyChanged(); }
        }

        public double Cfg
        {
            get => _cfg;
            set { _cfg = Math.Clamp(value, 1.0, 20.0); OnPropertyChanged(); }
        }

        // ── Workflow state ──────────────────────────────────────────────────────
        public bool IsGenerating
        {
            get => _isGenerating;
            set
            {
                _isGenerating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
                NotifyCommands();
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

        public string LogOutput
        {
            get => _logOutput;
            set { _logOutput = value; OnPropertyChanged(); }
        }

        // ── Result ──────────────────────────────────────────────────────────────
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

        // ── CanExecute ──────────────────────────────────────────────────────────
        public bool CanGenerate =>
            HasSourceImage && !string.IsNullOrWhiteSpace(Prompt) && !IsGenerating;

        // ── Commands ────────────────────────────────────────────────────────────
        public RelayCommand BrowseSourceCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand OpenResultImageCommand { get; }

        private void NotifyCommands()
        {
            BrowseSourceCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }

        // ── Browse handler ──────────────────────────────────────────────────────
        private async Task BrowseSourceAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Source Image",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "qwenedit21.source");
            if (!string.IsNullOrEmpty(path)) SetSourceImage(path);
        }

        public void SetSourceImage(string path)
        {
            if (!File.Exists(path)) return;
            SourceImagePath = path;
            try
            {
                SourceImageSource = LoadBitmap(path);
                HasSourceImage = true;
                AddLog($"Source image: {Path.GetFileName(path)}");
                StatusMessage = "Image loaded — enter a prompt and click Generate";
            }
            catch (Exception ex) { AddLog($"ERROR loading source image: {ex.Message}"); }
        }

        // ── Cancel ──────────────────────────────────────────────────────────────
        private void Cancel()
        {
            _cts?.Cancel();
            StatusMessage = "Cancelling...";
        }

        // ── Generate ────────────────────────────────────────────────────────────
        private async Task GenerateAsync()
        {
            if (!CanGenerate) return;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                IsGenerating = true;
                Progress = 0;
                AddLog("=== Generate ===");
                AddLog($"Prompt: {Prompt}");
                AddLog($"Aspect Ratio: {SelectedAspectRatio}");

                // Serialize against other tabs/queues so we never double-submit to ComfyUI.
                StatusMessage = "Waiting for other workflows to finish...";
                using var lease = await _workflowCoordinator.AcquireAsync("QwenEdit21", _cts.Token);

                StatusMessage = "Connecting to ComfyUI...";
                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(_cts.Token);
                    AddLog("Connected");
                }

                Progress = 8;
                StatusMessage = "Uploading image...";
                var uploadedImage = await _comfyUIService.UploadImageAsync(SourceImagePath, _cts.Token);
                AddLog($"Uploaded: {uploadedImage}");

                Progress = 18;
                StatusMessage = "Building workflow...";
                var workflow = BuildWorkflow(uploadedImage);

                var progressReporter = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        WpfApp.Current?.Dispatcher.Invoke(() =>
                        {
                            Progress = 18 + pct * 0.74;
                            StatusMessage = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                        });

                        if ((DateTime.Now - _lastProgressLog).TotalSeconds >= 15)
                        {
                            _lastProgressLog = DateTime.Now;
                            AddLog($"Generating: {msg.Data.Value}/{msg.Data.Max} ({pct:F0}%)");
                        }
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
                _logger.LogError($"Qwen Edit 2.1 generate: {ex}");
            }
            finally
            {
                IsGenerating = false;
                AddLog("=== Generate ended ===");
            }
        }

        // ── Workflow building ───────────────────────────────────────────────────
        private JsonElement BuildWorkflow(string uploadedImage)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse workflow JSON");

            // Node 470 = LoadImage (source image)
            UpdateNode(dict, "470", inputs => inputs["image"] = uploadedImage);

            // Node 13 = ResolutionSelector (aspect ratio)
            UpdateNode(dict, "13", inputs => inputs["aspect_ratio"] = SelectedAspectRatio);

            // Node 459:474 = TextEncodeQwenImage21 (prompt + negative prompt)
            UpdateNode(dict, "459:474", inputs =>
            {
                inputs["prompt"] = Prompt;
                inputs["negative_prompt"] = NegativePrompt;
            });

            // Node 459:458 = KSampler (steps, cfg, seed)
            UpdateNode(dict, "459:458", inputs =>
            {
                inputs["steps"] = Steps;
                inputs["cfg"] = Cfg;
                inputs["seed"] = new Random().NextInt64(0, 999_999_999_999_999L);
            });

            return JsonSerializer.SerializeToElement(dict);
        }

        private static void UpdateNode(
            System.Collections.Generic.Dictionary<string, JsonElement> dict,
            string nodeId,
            Action<System.Collections.Generic.Dictionary<string, object>> updater)
        {
            if (!dict.ContainsKey(nodeId)) return;
            var node = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(dict[nodeId].GetRawText());
            if (node == null || !node.ContainsKey("inputs")) return;
            var inputs = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
            if (inputs == null) return;
            updater(inputs);
            node["inputs"] = inputs;
            dict[nodeId] = JsonSerializer.SerializeToElement(node);
        }

        // ── Output image retrieval ──────────────────────────────────────────────
        private async Task<byte[]?> RetrieveOutputImageAsync(string promptId, CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
            Uri uri;
            try { uri = new Uri(baseUrl); } catch { uri = new Uri("http://127.0.0.1:8188"); }
            bool isRemote = !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

            const int maxRetries = 30;
            const int retryDelayMs = 5000;

            if (isRemote)
            {
                for (int i = 0; i < maxRetries; i++)
                {
                    if (i > 0) { AddLog($"Retry {i}/{maxRetries}..."); await Task.Delay(retryDelayMs, token); }
                    token.ThrowIfCancellationRequested();
                    var files = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                    var imgFile = files.FirstOrDefault(f =>
                        Path.GetFileName(f).StartsWith(SavePrefix, StringComparison.OrdinalIgnoreCase) && IsImageExt(f));
                    imgFile ??= files.FirstOrDefault(f =>
                        IsImageExt(f) && !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase));
                    if (imgFile != null)
                    {
                        AddLog($"Downloading: {imgFile}");
                        var data = await _comfyUIService.HttpClient.DownloadOutputImageAsync(imgFile);
                        if (data != null) { AddLog($"Downloaded {data.Length} bytes"); return data; }
                    }
                }
                return null;
            }

            var outputDir = _settingsService.Settings?.OutputFolderPath;
            if (string.IsNullOrEmpty(outputDir)) { AddLog("ERROR: Output folder not configured"); return null; }
            for (int i = 0; i < maxRetries; i++)
            {
                if (i > 0) { AddLog($"Retry {i}/{maxRetries}..."); await Task.Delay(retryDelayMs, token); }
                token.ThrowIfCancellationRequested();
                var files = Directory.GetFiles(outputDir, $"{SavePrefix}*.png", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime).ToList();
                if (files.Any())
                {
                    var latest = files[0];
                    var age = DateTime.Now - File.GetLastWriteTime(latest);
                    AddLog($"Found: {Path.GetFileName(latest)} ({age.TotalSeconds:F0}s old)");
                    if (age.TotalSeconds < 180) return await File.ReadAllBytesAsync(latest, token);
                }
            }
            return null;
        }

        // ── Helpers ─────────────────────────────────────────────────────────────
        private async Task SaveAndDisplayResultAsync(byte[] bytes, CancellationToken token)
        {
            var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "qwen-edit-21");
            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, $"qwen-edit-21_{DateTime.Now:yyyyMMdd_HHmmss}.png");
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
