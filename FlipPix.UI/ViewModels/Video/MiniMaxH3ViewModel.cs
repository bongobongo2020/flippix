using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "MiniMax H3" tab. A single-shot image-to-video runner: upload an image (it becomes the literal
    /// first frame at 0.00s), press Analyze to have the llama-server turn the image + a draft idea into
    /// a full H3 prompt, then generate one video with synchronized audio.
    ///
    /// Drives <c>video_minimax_h3_i2v.json</c> — the reference-to-video template rebuilt around
    /// <c>MiniMaxH3ImageToVideo</c> (first_frame instead of ref_images, fl2va checkpoint instead of
    /// ref2va) — by editing node inputs. No queue, like <see cref="FaceIdCharSheetViewModel"/>.
    /// </summary>
    public partial class MiniMaxH3ViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/h3-minimax/video_minimax_h3_i2v.json";
        private const string OutputSubfolder = "minimax_h3";
        private const string SystemPromptFile = "h3minimax.md";

        // ── Workflow node ids (locked from video_minimax_h3_i2v.json) ──────────────────────────
        private const string NodeImage = "137";      // LoadImage (first frame)
        private const string NodePrompt = "138";     // PrimitiveStringMultiline → MiniMaxH3ImageToVideo.prompt
        private const string NodeResolution = "115"; // ResolutionSelector (aspect_ratio, megapixels)
        private const string NodeSeed = "129";       // RandomNoise noise_seed
        private const string NodeDuration = "132";   // PrimitiveFloat (seconds → frames via node 131)
        private const string NodeOutput = "92";      // SaveVideo

        /// <summary>The fixed first line every I2VA prompt must carry (from the H3 prompt-writing guide).</summary>
        private const string I2vaInstruction =
            "For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.";

        /// <summary>Aspect ratios accepted by the workflow's ResolutionSelector node, widest to tallest.
        /// Shared with <see cref="MiniMaxFflfSeedHuntViewModel"/>, which drives the same node.</summary>
        internal const string AutoAspect = "Auto (match image)";
        internal static readonly (string Option, double Ratio)[] AspectRatios =
        {
            ("21:9 (Ultrawide)", 21.0 / 9.0),
            ("16:9 (Widescreen)", 16.0 / 9.0),
            ("3:2 (Photo)", 3.0 / 2.0),
            ("4:3 (Standard)", 4.0 / 3.0),
            ("1:1 (Square)", 1.0),
            ("3:4 (Portrait Standard)", 3.0 / 4.0),
            ("2:3 (Portrait Photo)", 2.0 / 3.0),
            ("9:16 (Portrait Widescreen)", 9.0 / 16.0),
        };

        // ── Input state ────────────────────────────────────────────────────────
        private string _imagePath = string.Empty;
        private BitmapImage? _imagePreview;
        private string _imageInfo = string.Empty;
        private string _prompt = string.Empty;
        private string _selectedAspectRatio = AutoAspect;
        private double _megapixels = 1.0;
        private double _lengthSeconds = 5;
        private long _seed = -1;
        private bool _isAnalyzing;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _analyzeCts;
        private CancellationTokenSource? _runCts;

        public MiniMaxH3ViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            SelectImageCommand = new RelayCommand(SelectImage);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            GenerateCommand = new RelayCommand(async () => await GenerateAsync(), () => CanGenerate);
            CancelCommand = new RelayCommand(() => _runCts?.Cancel(), () => IsProcessing);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            RandomSeedCommand = new RelayCommand(() => Seed = System.Random.Shared.NextInt64(0, long.MaxValue));

            AddLog("MiniMax H3 initialized");
        }

        #region Commands

        public ICommand SelectImageCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand RandomSeedCommand { get; }

        #endregion

        #region Input properties

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
                    _imagePreview = LoadImagePreview(value, out _imageInfo);
                    OnPropertyChanged(nameof(ImagePreview));
                    OnPropertyChanged(nameof(ImageInfo));
                    OnPropertyChanged(nameof(ResolvedAspectRatio));
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? ImagePreview => _imagePreview;
        public string ImageInfo => _imageInfo;

        /// <summary>The full H3 prompt: I2VA instruction line + the three core fields.</summary>
        public string Prompt
        {
            get => _prompt;
            set { if (_prompt != value) { _prompt = value; OnPropertyChanged(); OnCanExecuteChanged(); } }
        }

        public IReadOnlyList<string> AspectRatioOptions { get; } =
            new[] { AutoAspect }.Concat(AspectRatios.Select(a => a.Option)).ToList();

        public string SelectedAspectRatio
        {
            get => _selectedAspectRatio;
            set
            {
                if (_selectedAspectRatio != value)
                {
                    _selectedAspectRatio = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ResolvedAspectRatio));
                }
            }
        }

        /// <summary>The aspect actually sent to ComfyUI — the picked one, or the image's closest match.</summary>
        public string ResolvedAspectRatio =>
            SelectedAspectRatio == AutoAspect ? ClosestAspectRatio(ImagePath) : SelectedAspectRatio;

        /// <summary>
        /// Quality presets for the ResolutionSelector. H3's native canvas is a 768px short edge, so
        /// 1.0 MP (≈1344×768 at 16:9) is full quality; the lower steps trade detail for speed.
        /// </summary>
        public IReadOnlyList<MegapixelOption> MegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.4, "0.4 MP — fast draft (≈864×480)"),
            new MegapixelOption(0.7, "0.7 MP — balanced (≈1120×640)"),
            new MegapixelOption(1.0, "1.0 MP — full quality (≈1344×768)"),
        };

        public double Megapixels
        {
            get => _megapixels;
            set { if (Math.Abs(_megapixels - value) > 0.0001) { _megapixels = value; OnPropertyChanged(); } }
        }

        /// <summary>Video length in seconds (H3 supports 4–15; clamped when applied to the workflow).</summary>
        public double LengthSeconds
        {
            get => _lengthSeconds;
            set { if (Math.Abs(_lengthSeconds - value) > 0.0001) { _lengthSeconds = value; OnPropertyChanged(); } }
        }

        public long Seed
        {
            get => _seed;
            set { if (_seed != value) { _seed = value; OnPropertyChanged(); } }
        }

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (_isAnalyzing != value)
                {
                    _isAnalyzing = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanAnalyze));
                    OnCanExecuteChanged();
                }
            }
        }

        public bool HasImage => !string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath);

        public bool CanAnalyze => HasImage && !IsAnalyzing && !IsProcessing;
        public bool CanGenerate => HasImage && !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing && !IsAnalyzing;

        #endregion

        #region Image selection

        private async void SelectImage()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select First Frame Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: "minimaxh3.image");

            if (path != null)
            {
                ImagePath = path;
                AddLog($"First frame: {Path.GetFileName(path)}");
            }
        }

        private BitmapImage? LoadImagePreview(string path, out string info)
        {
            info = string.Empty;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                var fi = new FileInfo(path);
                info = $"{bitmap.PixelWidth}×{bitmap.PixelHeight} • {fi.Length / 1024}KB";
                return bitmap;
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                info = "Error loading image";
                return null;
            }
        }

        /// <summary>Maps the image's own aspect to the nearest ratio the ResolutionSelector offers.</summary>
        private string ClosestAspectRatio(string path)
        {
            int w = 0, h = 0;
            if (ImagePreview is { } preview && string.Equals(path, ImagePath, StringComparison.OrdinalIgnoreCase))
            {
                w = preview.PixelWidth; h = preview.PixelHeight;
            }
            if ((w <= 0 || h <= 0) && !string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    w = frame.PixelWidth; h = frame.PixelHeight;
                }
                catch { /* fall through to the 16:9 default */ }
            }
            return ClosestAspectRatio(w, h);
        }

        /// <summary>Nearest ResolutionSelector aspect option for a pixel size (16:9 if unknown).</summary>
        internal static string ClosestAspectRatio(int w, int h)
        {
            if (w <= 0 || h <= 0) return "16:9 (Widescreen)";

            var ratio = (double)w / h;
            return AspectRatios
                .OrderBy(a => Math.Abs(Math.Log(a.Ratio) - Math.Log(ratio)))
                .First().Option;
        }

        #endregion

        #region Analysis (image → H3 prompt)

        private async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;

            try
            {
                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);

                var models = await _lmStudioService.GetAvailableModelsAsync(token);
                var model = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(model) && models.Count > 0)
                    model = models[0].Id ?? models[0].Name ?? string.Empty;
                if (string.IsNullOrEmpty(model))
                {
                    MessageBox.Show("No LM Studio / llama-server model available. Ensure the server is running and a model is loaded.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AddLog($"Writing MiniMax H3 prompt from the image with model: {model}");

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", SystemPromptFile);
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"System prompt not found: {promptFilePath}");
                var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

                // The current prompt box doubles as the user's draft idea for the rewrite.
                var draft = string.IsNullOrWhiteSpace(Prompt) ? "(none — invent a natural continuation of the image)" : Prompt.Trim();
                var len = ClampLength(LengthSeconds);
                var userMessage =
                    $"This image is the first frame of the video at 0.00 seconds.\n" +
                    $"Target duration: {len:0.##} seconds.\n" +
                    $"Draft idea from the user:\n{draft}";

                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    model,
                    ImagePath,
                    userMessage,
                    systemPrompt,
                    maxTokens: 2000,
                    cancellationToken: token);

                var cleaned = EnsureI2vaInstruction(CleanOutput(result));
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    Prompt = cleaned;
                    AddLog($"Prompt written ({cleaned.Length} chars)");
                }
                else
                {
                    AddLog("WARNING: Analysis returned empty result");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR during analysis: {ex.Message}");
                MessageBox.Show($"Analysis failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
            }
        }

        /// <summary>
        /// Strips the wrappers small vision models like to add (code fences, bold markers, a leading
        /// "prompt:" label, surrounding quotes) without touching the H3 field structure.
        /// </summary>
        private static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Replace("**", "").Trim();

            // Unwrap a ```/```text fenced block.
            if (text.StartsWith("```"))
            {
                var firstBreak = text.IndexOf('\n');
                if (firstBreak > 0) text = text[(firstBreak + 1)..];
                var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0) text = text[..lastFence];
                text = text.Trim();
            }

            if (text.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase))
                text = text[7..].TrimStart();
            if (text.Length > 1 && text[0] == '"' && text[^1] == '"')
                text = text[1..^1].Trim();

            return text.Trim();
        }

        /// <summary>
        /// Guarantees the I2VA alignment sentence is the first line — the model treats it as the
        /// instruction that pins the uploaded image to 0.00s, and H3 expects it verbatim.
        /// </summary>
        private static string EnsureI2vaInstruction(string prompt)
        {
            var t = (prompt ?? string.Empty).Trim();
            if (t.Length == 0) return t;
            if (t.StartsWith("For the target video,", StringComparison.OrdinalIgnoreCase)) return t;
            return $"{I2vaInstruction}\n\n{t}";
        }

        /// <summary>H3's supported clip length is 4–15 seconds at 24 fps.</summary>
        private static double ClampLength(double seconds) =>
            Math.Clamp(seconds <= 0 ? 5 : seconds, 4, 15);

        #endregion

        #region Generation

        private async Task GenerateAsync()
        {
            if (!CanGenerate) return;

            IsProcessing = true;
            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = "Preparing MiniMax H3 workflow...";

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                AddLog("=== MiniMax H3 Image to Video ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("MiniMaxH3", token);

                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFileName);
                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
                var json = await File.ReadAllTextAsync(workflowPath, token);

                ProcessingStatus = "Uploading first frame...";
                ProcessingProgress = 5;
                AddLog("Uploading first frame...");
                var imageName = await _comfyUIService.UploadImageAsync(ImagePath);
                if (string.IsNullOrEmpty(imageName)) throw new Exception("Failed to upload the first-frame image.");
                AddLog($"Image uploaded: {imageName}");

                var runSeed = Seed >= 0 ? Seed : System.Random.Shared.NextInt64(0, long.MaxValue);
                var len = ClampLength(LengthSeconds);
                var aspect = ResolvedAspectRatio;
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var runToken = $"mmh3_{ts}";

                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeImage, "image", imageName);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodePrompt, "value", EnsureI2vaInstruction(Prompt.Trim()));
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeResolution, "aspect_ratio", aspect);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeResolution, "megapixels", Megapixels);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeDuration, "value", len);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeSeed, "noise_seed", runSeed);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeOutput, "filename_prefix", $"{OutputSubfolder}/{runToken}");

                ProcessingProgress = 10;
                ProcessingStatus = "Generating video...";
                AddLog($"Generating (seed {runSeed}, {len:0.#}s, {aspect}, {Megapixels:0.0} MP)...");

                var local = await SubmitAndRetrieveAsync(json, runToken, 10, 95, token);
                if (local == null || !File.Exists(local))
                    throw new Exception("No output video was generated.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "MiniMaxH3");
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"MiniMaxH3_{ts}.mp4");
                File.Copy(local, finalPath, true);
                await LocalCopyService.CopyVideoAsync(finalPath);

                var fi = new FileInfo(finalPath);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResultVideoPath = finalPath;
                    ResultVideoInfo = $"MiniMax H3 • {aspect} • {len:0.#}s • {fi.Length / 1024 / 1024.0:F1}MB";
                    HasResult = true;
                    OnCanExecuteChanged();
                });
                ProcessingProgress = 100;
                ProcessingStatus = "Complete!";
                AddLog($"=== Complete: {finalPath} ===");
            }
            catch (OperationCanceledException)
            {
                AddLog("Generation cancelled");
                ProcessingStatus = "Cancelled";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                ProcessingStatus = $"Error: {ex.Message}";
                MessageBox.Show($"Generation failed:\n{ex.Message}", "MiniMax H3 Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                lease?.Dispose();
                IsProcessing = false;
                _runCts?.Dispose();
                _runCts = null;
                OnCanExecuteChanged();
            }
        }

        /// <summary>Submits the workflow, waits for completion, and resolves the SaveVideo (node 92)
        /// output to a local file — first via /history node outputs, then a disk scan for the run token.</summary>
        private async Task<string?> SubmitAndRetrieveAsync(string json, string runToken, double from, double to, CancellationToken token)
        {
            var existing = GetExistingVideoFiles("*.mp4", OutputSubfolder);
            var promptId = await SubmitAsync(json, from, to, token);

            ProcessingStatus = "Waiting for output...";
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
            if (byNode.TryGetValue(NodeOutput, out var outs) && outs.Count > 0)
            {
                var pick = outs.FirstOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? outs[0];
                var local = await ResolveOutputToLocalAsync(pick);
                if (local != null) return local;
            }

            // Fallback: wait for a new mp4 carrying this run's token in the output subfolder.
            var found = await WaitForNewVideoAsync(existing, "*.mp4",
                TimeSpan.FromMinutes(45), TimeSpan.FromSeconds(4), OutputSubfolder);
            if (found != null && Path.GetFileName(found).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0)
                return found;
            return found ?? FindTokenFileOnDisk(runToken);
        }

        private async Task<string> SubmitAsync(string json, double progressFrom, double progressTo, CancellationToken token)
        {
            var workflow = JsonSerializer.Deserialize<JsonElement>(json);
            var span = progressTo - progressFrom;
            var progress = new Progress<ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                {
                    var pct = (double)msg.Data.Value / msg.Data.Max;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProcessingProgress = progressFrom + pct * span;
                        ProcessingStatus = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                    });
                }
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token);
            AddLog($"Workflow submitted, ID: {promptId}");
            return promptId;
        }

        private async Task<string?> ResolveOutputToLocalAsync(string videoFile)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    var baseUrl = GetComfyUIBaseUrl();
                    bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                    string outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;
                    if (!string.IsNullOrEmpty(outputFolder))
                    {
                        var localPath = Path.Combine(outputFolder, videoFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(localPath))
                        {
                            await WaitForFileStableAsync(localPath);
                            return localPath;
                        }
                    }
                }

                var parts = videoFile.Split('/');
                var filename = parts.Last();
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : "";
                var bytes = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, subfolder);
                if (bytes is { Length: > 0 })
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"mmh3_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tempPath, bytes);
                    return tempPath;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Resolve output failed: {ex.Message}");
            }
            return null;
        }

        private string? FindTokenFileOnDisk(string runToken)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;
                var baseUrl = GetComfyUIBaseUrl();
                bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                var outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;
                if (string.IsNullOrEmpty(outputFolder)) return null;

                var candidates = new List<string>();
                foreach (var folder in new[] { outputFolder, Path.Combine(outputFolder, OutputSubfolder) })
                {
                    if (Directory.Exists(folder))
                        candidates.AddRange(Directory.GetFiles(folder, "*.mp4", SearchOption.AllDirectories)
                            .Where(f => Path.GetFileName(f).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                return candidates.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            }
            catch (Exception ex)
            {
                AddLog($"Disk scan failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(CanAnalyze));
            OnPropertyChanged(nameof(CanGenerate));
            AnalyzeCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>A ResolutionSelector megapixel preset shown in the MiniMax H3 quality dropdown.</summary>
    public record MegapixelOption(double Value, string Label);
}
