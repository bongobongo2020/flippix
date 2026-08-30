using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Linux.Services;
// MessageBox is fully qualified below: MsBox.Avalonia contributes a root
// namespace of the same name, so a using-alias would be a CS0576 conflict.
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Application = System.Windows.Application;

namespace FlipPix.UI.Linux.ViewModels.Video
{
    /// <summary>
    /// "MiniMax H3 T2V" tab. The long-form image-to-video variant: upload one image, press Analyze, and
    /// the llama-server turns it into a dense ~15-second multi-shot H3 prompt (9–14 timestamped shots,
    /// continuous motion, beat-locked cuts) rather than the single continuous beat
    /// <see cref="MiniMaxI2VViewModel"/> writes. Then one video with synchronized audio is generated.
    ///
    /// The image's role at generation time is the user's choice:
    /// <list type="bullet">
    /// <item><b>Use image as first frame</b> (default) — identical conditioning to the 🌀 tab: the image
    /// uploads and wires into <c>MiniMaxH3ImageToVideo.first_frame</c>, and the prompt keeps the
    /// <c>&lt;Picture 1&gt;</c> anchor line.</item>
    /// <item><b>Off</b> — true text-to-video. <see cref="PruneFirstFrame"/> strips the LoadImage node and the
    /// <c>first_frame</c> link before submit (the input is optional on the node), and the anchor line is
    /// removed from the prompt since there is no picture for it to refer to. The image is then only ever
    /// seen by the llama-server.</item>
    /// </list>
    /// </summary>
    public partial class MiniMaxH3TextToVideoViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/h3-minimax/video_minimax_h3_t2v.json";
        private const string OutputSubfolder = "minimax_h3_t2v";
        private const string SystemPromptFile = "texttovideoH3.md";

        // ── Workflow node ids (locked from video_minimax_h3_t2v.json) ──────────────────────────
        private const string NodeImage = "137";      // LoadImage — pruned when the first-frame toggle is off
        private const string NodeVideo = "136";      // MiniMaxH3ImageToVideo (first_frame is optional)
        private const string NodePrompt = "138";     // PrimitiveStringMultiline → prompt
        private const string NodeResolution = "115"; // ResolutionSelector (aspect_ratio, megapixels)
        private const string NodeSeed = "129";       // RandomNoise noise_seed
        private const string NodeDuration = "132";   // PrimitiveFloat (seconds → frames via node 131)
        private const string NodeOutput = "92";      // SaveVideo

        /// <summary>The fixed anchor line that pins the uploaded image to 0.00s. Only valid when the
        /// image is actually wired in as the first frame.</summary>
        private const string I2vaInstruction =
            "For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.";

        // ── Input state ────────────────────────────────────────────────────────
        private string _imagePath = string.Empty;
        private BitmapImage? _imagePreview;
        private string _imageInfo = string.Empty;
        private string _prompt = string.Empty;
        private H3VisualStyle _visualStyle = H3VisualStyles.Auto;
        private string _selectedAspectRatio = H3Canvas.AutoAspect;
        private double _megapixels = 1.0;
        private double _lengthSeconds = 15;
        private long _seed = -1;
        private bool _useImageAsFirstFrame = true;
        private bool _isAnalyzing;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _analyzeCts;
        private CancellationTokenSource? _runCts;

        public MiniMaxH3TextToVideoViewModel(
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

            AddLog("MiniMax H3 T2V initialized");
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

        /// <summary>The full H3 prompt: optional anchor line + the three core fields.</summary>
        public string Prompt
        {
            get => _prompt;
            set { if (_prompt != value) { _prompt = value; OnPropertyChanged(); OnCanExecuteChanged(); } }
        }

        /// <summary>
        /// When true the image is uploaded and conditioned as frame 0. When false the graph runs as pure
        /// text-to-video and the image only ever reaches the llama-server.
        /// </summary>
        public bool UseImageAsFirstFrame
        {
            get => _useImageAsFirstFrame;
            set
            {
                if (_useImageAsFirstFrame != value)
                {
                    _useImageAsFirstFrame = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ImageRoleDescription));
                    OnCanExecuteChanged(); // an image stops being required once it is no longer conditioned
                }
            }
        }

        public string ImageRoleDescription => UseImageAsFirstFrame
            ? "First frame at 0.00s — the video starts on this exact image."
            : "Reference only — the model never sees it; the prompt carries the whole look.";

        /// <summary>
        /// The medium the prompt writer must work in. Left on Auto the writer picks — which is what every
        /// H3 tab did before this existed, and it kept picking the same high-production gacha anime whatever
        /// the story was, because that was the first example the system prompt showed it.
        /// </summary>
        public IReadOnlyList<H3VisualStyle> VisualStyleOptions { get; } = H3VisualStyles.All;

        public H3VisualStyle VisualStyle
        {
            get => _visualStyle;
            set
            {
                if (value == null || ReferenceEquals(_visualStyle, value)) return;
                _visualStyle = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VisualStyleSummary));
            }
        }

        /// <summary>The line the clips will actually open with, so the choice is visible before Analyze runs.</summary>
        public string VisualStyleSummary => VisualStyle.IsAuto
            ? "The writer picks the medium off the image it is shown."
            : "[Shot 1] opens: " + VisualStyle.Clause;

        public IReadOnlyList<string> AspectRatioOptions { get; } =
            new[] { H3Canvas.AutoAspect }
                .Concat(H3Canvas.AspectRatios.Select(a => a.Option)).ToList();

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
            SelectedAspectRatio == H3Canvas.AutoAspect
                ? ClosestAspectRatio(ImagePath)
                : SelectedAspectRatio;

        /// <summary>Same ResolutionSelector presets as the 🌀 tab — H3's native canvas is a 768px short edge.</summary>
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

        /// <summary>
        /// Video length in seconds. Defaults to the full 15 s this tab is built around (362 frames at
        /// 24 fps, the top of H3's trained range); drop it for cheap test runs before a full render.
        /// </summary>
        public double LengthSeconds
        {
            get => _lengthSeconds;
            set
            {
                if (Math.Abs(_lengthSeconds - value) > 0.0001)
                {
                    _lengthSeconds = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LengthSummary));
                }
            }
        }

        /// <summary>Shows the frame count the workflow's math node will actually snap the duration to.</summary>
        public string LengthSummary
        {
            get
            {
                var len = ClampLength(LengthSeconds);
                return $"{len:0.#}s → {FramesForSeconds(len)} frames @ 24 fps";
            }
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

        /// <summary>Analyze always needs the image — it is the only thing the LLM has to work from.</summary>
        public bool CanAnalyze => HasImage && !IsAnalyzing && !IsProcessing;

        /// <summary>
        /// Generation needs a prompt, and an image only when it is being conditioned as the first frame —
        /// with the toggle off the prompt alone is enough.
        /// </summary>
        public bool CanGenerate => !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing && !IsAnalyzing
                                   && (!UseImageAsFirstFrame || HasImage);

        #endregion

        #region Image selection

        private async void SelectImage()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Source Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: "minimaxh3t2v.image");

            if (path != null)
            {
                ImagePath = path;
                AddLog($"Source image: {Path.GetFileName(path)}");
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
            return H3Canvas.ClosestAspectRatio(w, h);
        }

        #endregion

        #region Analysis (image → 15-second multi-shot H3 prompt)

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
                    System.Windows.MessageBox.Show("No LM Studio / llama-server model available. Ensure the server is running and a model is loaded.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AddLog($"Writing a {ClampLength(LengthSeconds):0.#}s multi-shot H3 prompt — sending to {_lmStudioService.DescribeTarget(model)}");

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", SystemPromptFile);
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"System prompt not found: {promptFilePath}");
                var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

                // The current prompt box doubles as the user's draft idea for the rewrite.
                var draft = string.IsNullOrWhiteSpace(Prompt)
                    ? "(none — invent a dynamic sequence that suits the image)"
                    : Prompt.Trim();
                var len = ClampLength(LengthSeconds);
                var role = UseImageAsFirstFrame
                    ? "Image role: FIRST FRAME — this image is literally frame 0 of the video at 0.00 seconds."
                    : "Image role: REFERENCE ONLY — the video does not start on this image and the generator will never see it, so describe everything explicitly.";
                var userMessage =
                    $"{role}\n" +
                    $"Target duration: {len:0.##} seconds.\n" +
                    H3VisualStyles.Rule(VisualStyle) +
                    $"Draft idea from the user:\n{draft}";

                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    model,
                    ImagePath,
                    userMessage,
                    systemPrompt,
                    maxTokens: 6000,
                    cancellationToken: token);

                var cleaned = ApplyAnchorLine(CleanOutput(result));
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    Prompt = cleaned;
                    AddLog($"Prompt written ({cleaned.Length} chars, {CountShots(cleaned)} shots)");
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
                System.Windows.MessageBox.Show($"Analysis failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
        /// Makes the anchor line match the image's actual role: present and verbatim when the image is
        /// conditioned as frame 0, removed when it is not (there is no &lt;Picture 1&gt; for H3 to resolve).
        /// </summary>
        private string ApplyAnchorLine(string prompt)
        {
            var t = (prompt ?? string.Empty).Trim();
            if (t.Length == 0) return t;

            var hasAnchor = t.StartsWith("For the target video,", StringComparison.OrdinalIgnoreCase);

            if (UseImageAsFirstFrame)
                return hasAnchor ? t : $"{I2vaInstruction}\n\n{t}";

            if (!hasAnchor) return t;
            var idx = t.IndexOf("integrated_multimodal_description:", StringComparison.OrdinalIgnoreCase);
            return idx > 0 ? t[idx..].Trim() : t;
        }

        /// <summary>Counts `[Shot n]` markers, purely for the log line.</summary>
        private static int CountShots(string prompt)
        {
            var count = 0;
            var idx = prompt.IndexOf("[Shot ", StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                count++;
                idx = prompt.IndexOf("[Shot ", idx + 6, StringComparison.OrdinalIgnoreCase);
            }
            return count;
        }

        /// <summary>H3's supported clip length is 4–15 seconds at 24 fps.</summary>
        private static double ClampLength(double seconds) =>
            Math.Clamp(seconds <= 0 ? 15 : seconds, 4, 15);

        /// <summary>Mirrors node 131's expression: 24 fps snapped up onto the model's 17k+5 frame grid.</summary>
        private static int FramesForSeconds(double seconds)
        {
            var frames = Math.Max(5, (int)Math.Round(seconds * 24));
            return frames + (5 - frames % 17 + 17) % 17;
        }

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
                AddLog($"=== MiniMax H3 {(UseImageAsFirstFrame ? "Image" : "Text")} to Video ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("MiniMaxH3T2V", token);

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

                if (UseImageAsFirstFrame)
                {
                    ProcessingStatus = "Uploading first frame...";
                    ProcessingProgress = 5;
                    AddLog("Uploading first frame...");
                    var imageName = await _comfyUIService.UploadImageAsync(ImagePath);
                    if (string.IsNullOrEmpty(imageName)) throw new Exception("Failed to upload the first-frame image.");
                    AddLog($"Image uploaded: {imageName}");
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeImage, "image", imageName);
                }
                else
                {
                    json = PruneFirstFrame(json);
                    AddLog("Text-to-video: first frame pruned, the prompt alone drives the shot.");
                }

                var runSeed = Seed >= 0 ? Seed : System.Random.Shared.NextInt64(0, long.MaxValue);
                var len = ClampLength(LengthSeconds);
                var aspect = ResolvedAspectRatio;
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var runToken = $"mmh3t2v_{ts}";

                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodePrompt, "value", ApplyAnchorLine(Prompt.Trim()));
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeResolution, "aspect_ratio", aspect);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeResolution, "megapixels", Megapixels);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeDuration, "value", len);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeSeed, "noise_seed", runSeed);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeOutput, "filename_prefix", $"{OutputSubfolder}/{runToken}");

                ProcessingProgress = 10;
                ProcessingStatus = "Generating video...";
                AddLog($"Generating (seed {runSeed}, {len:0.#}s / {FramesForSeconds(len)} frames, {aspect}, {Megapixels:0.0} MP)...");

                var local = await SubmitAndRetrieveAsync(json, runToken, 10, 95, token);
                if (local == null || !File.Exists(local))
                    throw new Exception("No output video was generated.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "MiniMaxH3T2V");
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"MiniMaxH3T2V_{ts}.mp4");
                File.Copy(local, finalPath, true);
                await LocalCopyService.CopyVideoAsync(finalPath);

                var fi = new FileInfo(finalPath);
                var mode = UseImageAsFirstFrame ? "first frame" : "text only";
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResultVideoPath = finalPath;
                    ResultVideoInfo = $"MiniMax H3 • {mode} • {aspect} • {len:0.#}s • {fi.Length / 1024 / 1024.0:F1}MB";
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
                System.Windows.MessageBox.Show($"Generation failed:\n{ex.Message}", "MiniMax H3 T2V Error",
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

        /// <summary>
        /// Turns the graph into a pure text-to-video one: drops the <c>first_frame</c> link from
        /// MiniMaxH3ImageToVideo (the input is optional, so the node falls back to an empty latent) and
        /// removes the now-orphaned LoadImage node, which would otherwise fail validation on its
        /// placeholder filename.
        /// </summary>
        private static string PruneFirstFrame(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            if (root[NodeVideo]?["inputs"] is JsonObject videoInputs)
                videoInputs.Remove("first_frame");
            root.Remove(NodeImage);

            return root.ToJsonString();
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
                    string outputFolder = settings.ResolveOutputFolder(isRemote);
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
                    var tempPath = Path.Combine(Path.GetTempPath(), $"mmh3t2v_{Guid.NewGuid():N}_{filename}");
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
                var outputFolder = settings.ResolveOutputFolder(isRemote);
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
}
