using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// FFLF-Dasiwa: an autoregressive I2V chain on the WAN 2.2 DaSiWa fast-fidelity FFLF workflow.
    /// The user uploads one image and analyzes it; the app then renders a chain of N short clips
    /// where each clip's automatically-extracted last frame becomes the next clip's first frame
    /// (re-analyzed by llama-server each step). All segments are kept and concatenated into one
    /// continuous video. Interactive runner (not queue-based); the tab binds DataContext directly.
    /// </summary>
    public partial class FflfDasiwaViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName =
            "workflow/video/wan/DasiwaSAFETENSOR-FFLFAPI.json";
        private const string OutputSubfolder = "fflf_dasiwa";
        private const string SystemPromptFile = "dasiwa-fflf.md";

        // ── Workflow node ids driven by this VM ────────────────────────────────
        private const string NodeFirstFrame = "23";        // LoadImage (first frame)
        private const string NodeLastFrame = "24";         // LoadImage (last frame placeholder, kept valid)
        private const string NodePositive = "2368";        // PrimitiveStringMultiline (positive)
        private const string NodeNegative = "2371";        // PrimitiveStringMultiline (negative)
        private const string NodeSeed = "1512:1670";       // PrimitiveInt (seed)
        private const string NodeSeconds = "1512:1668";    // PrimitiveInt (seconds)
        private const string NodeModeSwitch = "1512:2336"; // PrimitiveBoolean (true = I2V, false = FLF2V)
        private const string NodeVideoCombine = "28";      // VHS_VideoCombine (mp4 output)
        private const string NodeLastFrameSave = "2503";   // SaveImage (extracted last frame)
        private const string NodeGifCombine = "2502";      // VHS_VideoCombine (gif preview — disabled)

        // ── Input fields ───────────────────────────────────────────────────────
        private string _firstImagePath = string.Empty;
        private BitmapImage? _firstImagePreview;
        private string _firstImageInfo = string.Empty;

        private string _prompt = string.Empty;
        private string _negativePrompt =
            "censored, mosaic censoring, bar censor, pixelated, glowing, bloom, blurry, out of focus, low detail, " +
            "bad anatomy, ugly, overexposed, underexposed, distorted face, extra limbs, cartoonish, 3d render artifacts, " +
            "duplicate people, unnatural lighting, bad composition, missing shadows, low resolution, poorly textured, " +
            "glitch, noise, grain, static, motionless, still frame, stylized, artwork, painting, illustration, " +
            "many people in background, three legs, walking backward, deformed, disfigured, malformed limbs, extra fingers, fused fingers";

        private int _iterations = 3;
        private int _lengthSeconds = 5;
        private long _seed = -1;

        private bool _isAnalyzing;
        private bool _isRunning;
        private string _activePreviewUri = string.Empty;
        private FflfDasiwaSegment? _selectedSegment;

        // ── State ──────────────────────────────────────────────────────────────
        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private readonly ObservableCollection<FflfDasiwaSegment> _segments = new();
        private CancellationTokenSource? _runCts;
        private CancellationTokenSource? _analyzeCts;

        public FflfDasiwaViewModel(
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
            RunChainCommand = new RelayCommand(async () => await RunChainAsync(), () => CanRun);
            CancelCommand = new RelayCommand(Cancel, () => IsRunning);
            RandomSeedCommand = new RelayCommand(() => Seed = new Random().NextInt64(0, long.MaxValue));
            PlaySegmentCommand = new RelayCommand<FflfDasiwaSegment>(PlaySegment);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);

            AddLog("FFLF-Dasiwa initialized");
        }

        #region Commands

        public ICommand SelectImageCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand RunChainCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand RandomSeedCommand { get; }
        public RelayCommand<FflfDasiwaSegment> PlaySegmentCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }

        #endregion

        #region Input Properties

        public string FirstImagePath
        {
            get => _firstImagePath;
            set
            {
                if (_firstImagePath != value)
                {
                    _firstImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasFirstImage));
                    OnPropertyChanged(nameof(CanAnalyze));
                    OnPropertyChanged(nameof(CanRun));
                    LoadFirstImagePreview();
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? FirstImagePreview
        {
            get => _firstImagePreview;
            set { _firstImagePreview = value; OnPropertyChanged(); }
        }

        public string FirstImageInfo
        {
            get => _firstImageInfo;
            set { if (_firstImageInfo != value) { _firstImageInfo = value; OnPropertyChanged(); } }
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
                    OnPropertyChanged(nameof(CanRun));
                    OnCanExecuteChanged();
                }
            }
        }

        public string NegativePrompt
        {
            get => _negativePrompt;
            set { if (_negativePrompt != value) { _negativePrompt = value; OnPropertyChanged(); } }
        }

        /// <summary>Number of chained clips to render (1–5).</summary>
        public int Iterations
        {
            get => _iterations;
            set
            {
                var v = Math.Clamp(value, 1, 5);
                if (_iterations != v) { _iterations = v; OnPropertyChanged(); }
            }
        }

        /// <summary>Length of each clip in seconds (clamped 1–10).</summary>
        public int LengthSeconds
        {
            get => _lengthSeconds;
            set
            {
                var v = Math.Clamp(value, 1, 10);
                if (_lengthSeconds != v) { _lengthSeconds = v; OnPropertyChanged(); }
            }
        }

        public long Seed
        {
            get => _seed;
            set { if (_seed != value) { _seed = value; OnPropertyChanged(); } }
        }

        public bool HasFirstImage => !string.IsNullOrEmpty(FirstImagePath) && File.Exists(FirstImagePath);
        public bool CanAnalyze => HasFirstImage && !IsAnalyzing && !IsRunning;
        public bool CanRun => HasFirstImage && !IsRunning && !IsAnalyzing;

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
                    OnPropertyChanged(nameof(CanRun));
                    OnCanExecuteChanged();
                }
            }
        }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (_isRunning != value)
                {
                    _isRunning = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanAnalyze));
                    OnPropertyChanged(nameof(CanRun));
                    OnCanExecuteChanged();
                }
            }
        }

        public ObservableCollection<FflfDasiwaSegment> Segments => _segments;
        public bool HasSegments => _segments.Any();

        public string ActivePreviewUri
        {
            get => _activePreviewUri;
            set
            {
                if (_activePreviewUri != value)
                {
                    _activePreviewUri = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasActivePreview));
                }
            }
        }

        public bool HasActivePreview => !string.IsNullOrEmpty(ActivePreviewUri);

        public FflfDasiwaSegment? SelectedSegment
        {
            get => _selectedSegment;
            set
            {
                if (!ReferenceEquals(_selectedSegment, value))
                {
                    _selectedSegment = value;
                    OnPropertyChanged();
                    if (value != null && !string.IsNullOrEmpty(value.VideoPath))
                        ActivePreviewUri = value.VideoPath;
                }
            }
        }

        #endregion

        #region File Selection

        private async void SelectImage()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select First-Frame Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: "fflfdasiwa.image");

            if (path != null)
            {
                FirstImagePath = path;
                AddLog($"First frame: {Path.GetFileName(path)}");
            }
        }

        private void LoadFirstImagePreview()
        {
            if (!HasFirstImage)
            {
                FirstImagePreview = null;
                FirstImageInfo = string.Empty;
                return;
            }
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(FirstImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                FirstImagePreview = bitmap;
                var fi = new FileInfo(FirstImagePath);
                FirstImageInfo = $"{bitmap.PixelWidth}×{bitmap.PixelHeight} • {fi.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                FirstImageInfo = "Error loading image";
            }
        }

        #endregion

        #region Analysis

        private async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;
            try
            {
                var prompt = await AnalyzeImageForPromptAsync(FirstImagePath, token);
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    Prompt = prompt;
                    AddLog($"Prompt generated ({prompt.Length} chars)");
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
        /// Sends a single image to llama-server with the dasiwa-fflf system prompt and returns the
        /// cleaned WAN 2.2 motion prompt. Reused for the manual Analyze button and each chain step.
        /// </summary>
        private async Task<string> AnalyzeImageForPromptAsync(string imagePath, CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
            await _lmStudioService.SetBaseUrlAsync(baseUrl);

            var models = await _lmStudioService.GetAvailableModelsAsync(token);
            var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
            if (string.IsNullOrEmpty(selectedModel) && models.Count > 0)
                selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;

            if (string.IsNullOrEmpty(selectedModel))
            {
                MessageBox.Show("No LM Studio model available. Please ensure LM Studio is running and a model is loaded.",
                    "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                return string.Empty;
            }

            var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "prompts", "prompt2json", SystemPromptFile);
            if (!File.Exists(promptFilePath))
                throw new FileNotFoundException($"System prompt not found: {promptFilePath}");

            var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

            AddLog($"Analyzing {Path.GetFileName(imagePath)} with model: {selectedModel}");
            var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                selectedModel,
                imagePath,
                "Analyze this first-frame image and generate a WAN 2.2 forward-motion video prompt.",
                systemPrompt,
                maxTokens: 4000,
                cancellationToken: token);

            return CleanOutput(result);
        }

        private static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = text.Replace("**", "").Trim();
            // Strip <think>…</think> blocks some reasoning models emit before the answer.
            var thinkEnd = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (thinkEnd >= 0) text = text.Substring(thinkEnd + "</think>".Length).Trim();
            var lower = text.ToLowerInvariant();
            if (lower.StartsWith("prompt:") || lower.StartsWith("prompt :"))
                text = text.Substring(text.IndexOf(':') + 1).Trim();
            return text.Trim('"').Trim();
        }

        #endregion

        #region Chain runner

        private void Cancel()
        {
            _runCts?.Cancel();
            AddLog("Chain cancel requested");
        }

        private async Task RunChainAsync()
        {
            if (!CanRun) return;

            IsRunning = true;
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;

            WorkflowQueueCoordinator.WorkflowLease lease;
            try
            {
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("FflfDasiwa", token);
            }
            catch (OperationCanceledException)
            {
                IsRunning = false;
                OnCanExecuteChanged();
                return;
            }

            using (lease)
            try
            {
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                Application.Current.Dispatcher.Invoke(() => _segments.Clear());
                OnPropertyChanged(nameof(HasSegments));
                ProcessingProgress = 0;

                var total = Iterations;
                AddLog($"=== FFLF-Dasiwa chain: {total} iteration(s), {LengthSeconds}s each ===");

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
                var workflowJson = await File.ReadAllTextAsync(workflowPath, token);

                var runStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var chainSubfolder = $"{OutputSubfolder}/chain_{runStamp}";
                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(),
                    "FflfDasiwa", $"chain_{runStamp}");
                Directory.CreateDirectory(outputDir);

                var rng = new Random();
                var currentImagePath = FirstImagePath;
                var currentPrompt = Prompt;
                var segmentVideoPaths = new List<string>();

                for (int k = 1; k <= total; k++)
                {
                    token.ThrowIfCancellationRequested();

                    // Segment 1 reuses the manual Analyze result if present; everything else
                    // re-analyzes the freshly extracted first frame.
                    if (k > 1 || string.IsNullOrWhiteSpace(currentPrompt))
                    {
                        ProcessingStatus = $"Analyzing frame for segment {k}/{total}...";
                        AddLog($"--- Segment {k}: analyzing first frame ---");
                        currentPrompt = await AnalyzeImageForPromptAsync(currentImagePath, token);
                        if (string.IsNullOrWhiteSpace(currentPrompt))
                            currentPrompt = "cinematic motion, smooth camera move, photorealistic, high detail";
                    }

                    var segSeed = Seed >= 0 ? Seed : rng.NextInt64(0, long.MaxValue);
                    AddLog($"--- Segment {k}/{total} (seed={segSeed}) ---");
                    AddLog($"Prompt: {Truncate(currentPrompt, 160)}");

                    ProcessingStatus = $"Uploading frame for segment {k}/{total}...";
                    var uploadedName = await _comfyUIService.UploadImageAsync(currentImagePath);
                    if (string.IsNullOrEmpty(uploadedName))
                        throw new Exception($"Failed to upload first-frame image for segment {k}.");

                    var videoPrefix = $"{chainSubfolder}/seg{k}";
                    var lastFramePrefix = $"{chainSubfolder}/seg{k}_LASTFRAME";
                    var updatedWorkflow = BuildWorkflow(
                        workflowJson, uploadedName, currentPrompt, segSeed, videoPrefix, lastFramePrefix);

                    var seg = k; // capture
                    var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                    {
                        if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                        {
                            var pct = (double)msg.Data.Value / msg.Data.Max;
                            var overall = ((seg - 1) + pct) / total * 100.0;
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                ProcessingProgress = overall;
                                ProcessingStatus = $"Segment {seg}/{total}: {msg.Data.Value}/{msg.Data.Max}";
                            });
                        }
                    });

                    AddLog($"Submitting segment {k} workflow...");
                    // The DaSiWa graph runs an RTX upscaler + frame interpolation per clip, so give
                    // each segment generous headroom (real completion is signalled via websocket).
                    var promptId = await _comfyUIService.ExecuteWorkflowAsync(
                        updatedWorkflow, progress, token, TimeSpan.FromHours(1));
                    AddLog($"Segment {k} done, prompt ID: {promptId}");

                    ProcessingStatus = $"Retrieving segment {k}/{total} output...";
                    var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);

                    // Video (VHS_VideoCombine → reported under "gifs"/"videos")
                    var videoRef = PickFile(byNode, NodeVideoCombine, ".mp4", ".webm");
                    if (videoRef == null)
                        throw new Exception($"No output video found for segment {k}.");
                    var resolvedVideo = await ResolveNodeFileToLocalAsync(videoRef, token);
                    if (resolvedVideo == null || !File.Exists(resolvedVideo))
                        throw new Exception($"Could not resolve segment {k} video.");

                    var segVideoFinal = Path.Combine(outputDir, $"seg{k}.mp4");
                    File.Copy(resolvedVideo, segVideoFinal, true);
                    await LocalCopyService.CopyVideoAsync(segVideoFinal);
                    segmentVideoPaths.Add(segVideoFinal);

                    // Extracted last frame (SaveImage → "images"); feeds the next iteration.
                    string lastFrameLocal = string.Empty;
                    if (k < total)
                    {
                        var lastFrameRef = PickFile(byNode, NodeLastFrameSave, ".png", ".jpg", ".jpeg");
                        if (lastFrameRef == null)
                            throw new Exception($"No extracted last frame found for segment {k}.");
                        var resolvedFrame = await ResolveNodeFileToLocalAsync(lastFrameRef, token);
                        if (resolvedFrame == null || !File.Exists(resolvedFrame))
                            throw new Exception($"Could not resolve extracted last frame for segment {k}.");

                        lastFrameLocal = Path.Combine(outputDir, $"seg{k}_lastframe{Path.GetExtension(resolvedFrame)}");
                        File.Copy(resolvedFrame, lastFrameLocal, true);
                        AddLog($"Segment {k}: extracted last frame → next first frame");
                    }

                    var firstFrameForSeg = currentImagePath;
                    var promptForSeg = currentPrompt;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var item = new FflfDasiwaSegment
                        {
                            Index = seg,
                            VideoPath = segVideoFinal,
                            FirstFramePath = firstFrameForSeg,
                            LastFramePath = lastFrameLocal,
                            Prompt = promptForSeg,
                            Seed = segSeed,
                        };
                        _segments.Add(item);
                        SelectedSegment = item;
                        ActivePreviewUri = segVideoFinal;
                    });
                    OnPropertyChanged(nameof(HasSegments));

                    if (k < total)
                        currentImagePath = lastFrameLocal;
                }

                // Join all segments into one continuous video (also keep the individual clips).
                token.ThrowIfCancellationRequested();
                ProcessingStatus = "Joining segments...";
                var joinedPath = Path.Combine(outputDir, $"FflfDasiwa_{runStamp}_joined.mp4");
                var ffmpeg = FindFFmpeg();
                if (ffmpeg != null && segmentVideoPaths.Count > 0)
                {
                    await ConcatClipsAsync(ffmpeg, segmentVideoPaths, joinedPath, token);
                    if (File.Exists(joinedPath))
                    {
                        await LocalCopyService.CopyVideoAsync(joinedPath);
                        ResultVideoPath = joinedPath;
                        var fi = new FileInfo(joinedPath);
                        ResultVideoInfo = $"FFLF-Dasiwa chain • {total} segments • {fi.Length / 1024 / 1024.0:F1}MB";
                        HasResult = true;
                        Application.Current.Dispatcher.Invoke(() => ActivePreviewUri = joinedPath);
                        AddLog($"=== Joined video: {joinedPath} ===");
                    }
                }
                else
                {
                    // No ffmpeg — fall back to the last segment as the result.
                    ResultVideoPath = segmentVideoPaths.LastOrDefault() ?? string.Empty;
                    ResultVideoInfo = $"FFLF-Dasiwa chain • {total} segments (not joined — ffmpeg missing)";
                    HasResult = !string.IsNullOrEmpty(ResultVideoPath);
                    AddLog("FFmpeg not found — segments kept separately, no joined video.");
                }

                ProcessingProgress = 100;
                ProcessingStatus = "FFLF-Dasiwa chain complete!";
            }
            catch (OperationCanceledException)
            {
                ProcessingStatus = "Cancelled";
                AddLog("FFLF-Dasiwa chain cancelled");
            }
            catch (Exception ex)
            {
                ProcessingStatus = "Error";
                AddLog($"ERROR: {ex.Message}");
                MessageBox.Show($"FFLF-Dasiwa chain failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsRunning = false;
                _runCts?.Dispose();
                _runCts = null;
                OnCanExecuteChanged();
            }
        }

        private JsonElement BuildWorkflow(
            string originalJson,
            string imageName,
            string prompt,
            long seed,
            string videoPrefix,
            string lastFramePrefix)
        {
            var json = originalJson;

            // First frame is the only real input; the last-frame loader is kept pointing at the same
            // upload so it stays valid even though I2V mode discards that branch's output.
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeFirstFrame, "image", imageName);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeLastFrame, "image", imageName);

            // Force I2V (single start frame) rather than the workflow's native first+last mode.
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeModeSwitch, "value", true);

            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodePositive, "value", prompt);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeNegative, "value", NegativePrompt);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeSeed, "value", seed);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeSeconds, "value", LengthSeconds);

            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeVideoCombine, "filename_prefix", videoPrefix);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeLastFrameSave, "filename_prefix", lastFramePrefix);

            // Node 2502 is a gif VHS_VideoCombine whose `images` input is unconnected in the
            // exported API graph — it fails validation ("Required input is missing: images") and
            // ComfyUI ignores it anyway. Drop it entirely: we don't want the gif, and removing it
            // clears the recurring validation error from the log.
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
            dict.Remove(NodeGifCombine);
            return JsonSerializer.SerializeToElement(dict);
        }

        /// <summary>Picks the first node output file matching one of the given extensions.</summary>
        private static string? PickFile(Dictionary<string, List<string>> byNode, string nodeId, params string[] exts)
        {
            if (!byNode.TryGetValue(nodeId, out var files) || files.Count == 0) return null;
            return files.FirstOrDefault(f => exts.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                   ?? files[0];
        }

        /// <summary>Resolves a "subfolder/filename" node output to a local file (local folder first, else /view download).</summary>
        private async Task<string?> ResolveNodeFileToLocalAsync(string nodeFile, CancellationToken token)
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
                        var localPath = Path.Combine(outputFolder, nodeFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(localPath)) { await WaitForFileStableAsync(localPath); return localPath; }
                    }
                }

                var parts = nodeFile.Split('/');
                var filename = parts.Last();
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : "";
                var bytes = await _comfyUIService.HttpClient.DownloadViewFileAsync(filename, subfolder, "output", token);
                if (bytes is { Length: > 0 })
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"fflfdasiwa_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tempPath, bytes, token);
                    return tempPath;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Resolve output failed: {ex.Message}");
            }
            return null;
        }

        private async Task ConcatClipsAsync(string ffmpeg, IReadOnlyList<string> clips, string outPath, CancellationToken token)
        {
            if (clips.Count == 1)
            {
                File.Copy(clips[0], outPath, true);
                return;
            }

            var listPath = Path.Combine(Path.GetTempPath(), $"fflfdasiwa_concat_{Guid.NewGuid():N}.txt");
            var sb = new StringBuilder();
            foreach (var clip in clips)
                sb.AppendLine($"file '{clip.Replace("'", "'\\''")}'");
            await File.WriteAllTextAsync(listPath, sb.ToString(), token);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                // WAN 2.2 output is silent video; re-encode video only (segments share resolution/fps).
                foreach (var a in new[]
                {
                    "-y", "-f", "concat", "-safe", "0", "-i", listPath,
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-pix_fmt", "yuv420p", "-an", outPath
                }) psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p == null) throw new Exception("Failed to start FFmpeg.");
                var stderr = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(token);
                if (p.ExitCode != 0)
                    AddLog($"FFmpeg concat exited {p.ExitCode}: {Tail(stderr, 400)}");
            }
            finally
            {
                try { File.Delete(listPath); } catch { /* best effort */ }
            }
        }

        private static string Truncate(string s, int n) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(0, n) + "…");

        private static string Tail(string s, int n) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));

        #endregion

        #region Result Actions

        private void PlaySegment(FflfDasiwaSegment? segment)
        {
            if (segment == null || string.IsNullOrEmpty(segment.VideoPath)) return;
            SelectedSegment = segment;
            ActivePreviewUri = segment.VideoPath;
        }

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            AnalyzeCommand.NotifyCanExecuteChanged();
            RunChainCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
        }
    }
}
