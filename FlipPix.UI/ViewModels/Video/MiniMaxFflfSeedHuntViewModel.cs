using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "MiniMax FFLF" page — the first/last-frame seed-hunter flow driven by the MiniMax H3
    /// first/last-frame workflow.
    ///
    /// Upload a first and last frame (or a whole folder, chained into overlapping pairs) → Analyze
    /// (both frames → llama-server with <c>h3minimax-fflf.md</c> → a full FL2VA H3 prompt) → generate 3
    /// cheap seed previews per pair (low megapixels, few steps) → tick the ones worth keeping → Finish
    /// re-renders each at full resolution and the full step count, then joins them into one video.
    ///
    /// H3 renders one video per submission (no in-graph sample batch), so a hunt is 3 sequential
    /// submissions with seeds base, base+1, base+2 — the same loop the FaceID engine uses in the LTX
    /// seed hunter. Preview and final differ only in <c>megapixels</c>, <c>steps</c> and the RTX pass;
    /// the prompt, duration, aspect and seed are identical, so a final closely tracks the preview it
    /// came from.
    ///
    /// <para>The tab runs <c>fl2va-turbo-fflf.json</c> — the turbo stack (4-step EMA LoRA + Sage
    /// attention + a ×2 RTX super-resolution pass) as authored. That export is a plain text-to-video
    /// graph: its <c>MiniMaxH3ImageToVideo</c> carries the prompt as a literal widget and has no frame
    /// inputs at all, so submission injects the two <c>LoadImage</c> nodes and links them to
    /// <c>first_frame</c>/<c>last_frame</c> (see <see cref="AttachFrameLoaders"/>). Doing it in code
    /// rather than in the file means a re-export from ComfyUI keeps working.</para>
    /// </summary>
    public partial class MiniMaxFflfSeedHuntViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/h3-minimax/fl2va-turbo-fflf.json";
        private const string OutputSubfolder = "minimax_fflf";
        private const string SystemPromptFile = "h3minimax-fflf.md";
        private const int SampleSlots = 3;

        // ── Workflow node ids (locked from fl2va-turbo-fflf.json) ──────────────────────────────
        private const string NodeImageToVideo = "131"; // MiniMaxH3ImageToVideo (prompt widget + frames)
        private const string NodeResolution = "115";   // ResolutionSelector (aspect_ratio, megapixels)
        private const string NodeSteps = "124";        // BasicScheduler steps
        private const string NodeSeed = "143";         // Seed (rgthree) → RandomNoise 129
        private const string NodeDuration = "133";     // PrimitiveFloat seconds (node 132 → frames)
        private const string NodeVaeDecode = "122";    // VAEDecode — the un-upscaled frames
        private const string NodeRtx = "142";          // RTXVideoSuperResolution (×2, finals only)
        private const string NodeCreateVideo = "130";  // CreateVideo (images source is switched)
        private const string NodeOutput = "92";        // SaveVideo

        // Injected at submission — the export has no frame loaders of its own. Picked well clear of the
        // graph's own ids; AttachFrameLoaders still refuses to overwrite anything that isn't a LoadImage.
        private const string NodeFirstImage = "300";   // LoadImage → MiniMaxH3ImageToVideo.first_frame
        private const string NodeLastImage = "301";    // LoadImage → MiniMaxH3ImageToVideo.last_frame

        /// <summary>Matches the "N.NN-second mark" timestamps inside the FL2VA alignment line.</summary>
        private static readonly Regex SecondMarkRegex =
            new(@"\d+(?:\.\d+)?-second mark", RegexOptions.Compiled);

        // Client-side ceilings on a single ComfyUI run. ComfyUIService defaults to 30 minutes, which a
        // full-resolution 20-step H3 render at 15s blows straight through — the client then aborts a job
        // that is still happily running on the server. Real completion is detected within ~5s via the
        // /history poll and a server that died mid-run is caught by the lost-prompt check, so a generous
        // ceiling costs nothing. Same magnitude as WanScail's 3-hour ExecutionTimeout.
        private static readonly TimeSpan PreviewRunTimeout = TimeSpan.FromHours(1);
        private static readonly TimeSpan FinishRunTimeout = TimeSpan.FromHours(4);

        // ── Input state ────────────────────────────────────────────────────────
        private string _firstImagePath = string.Empty;
        private string _lastImagePath = string.Empty;
        private BitmapImage? _firstImagePreview;
        private BitmapImage? _lastImagePreview;
        private string _firstImageInfo = string.Empty;
        private string _lastImageInfo = string.Empty;
        private string _prompt = string.Empty;
        private string _selectedAspectRatio = MiniMaxH3ViewModel.AutoAspect;
        private double _previewMegapixels = 0.3;
        private int _previewSteps = 4;
        private double _finalMegapixels = 1.0;
        private int _finalSteps = 8;
        private double _lengthSeconds = 5;
        private long _baseSeed = -1;
        private bool _isAnalyzing;
        private string _currentPhase = string.Empty;
        private string? _activePreviewUri;
        private long _currentBatchSeed = -1; // seed that produced the on-screen samples

        private readonly ObservableCollection<SeedHuntSample> _samples = new()
        {
            new SeedHuntSample(1), new SeedHuntSample(2), new SeedHuntSample(3),
        };
        private static readonly ObservableCollection<SeedHuntSample> _emptySamples = new();
        private readonly ObservableCollection<SeedHuntResult> _results = new();

        // ── Folder-batch state ─────────────────────────────────────────────────
        private readonly ObservableCollection<FflfPair> _pairs = new();
        private FflfPair? _selectedPair;
        private bool _isBatchMode;
        private string _batchInfo = string.Empty;
        // path → ComfyUI uploaded filename (overlapping pairs share frames; upload once).
        private readonly Dictionary<string, string> _uploadCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<SeedHuntSample> _batchSampleSubs = new();

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _analyzeCts;
        private CancellationTokenSource? _runCts;

        public MiniMaxFflfSeedHuntViewModel(
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

            SelectFirstImageCommand = new RelayCommand(() => SelectImage(isFirst: true));
            SelectLastImageCommand = new RelayCommand(() => SelectImage(isFirst: false));
            SelectFolderCommand = new RelayCommand(async () => await SelectFolderAsync());
            RunBatchCommand = new RelayCommand(async () => await RunBatchAsync(), () => CanRunBatch);
            RerollPairCommand = new RelayCommand(async () => await RerollPairAsync(), () => CanRerollPair);
            MovePairUpCommand = new RelayCommand<FflfPair>(p => MovePair(p, -1), _ => !IsProcessing && !IsAnalyzing);
            MovePairDownCommand = new RelayCommand<FflfPair>(p => MovePair(p, +1), _ => !IsProcessing && !IsAnalyzing);
            ClearFolderCommand = new RelayCommand(ClearFolder, () => IsBatchMode && !IsProcessing && !IsAnalyzing);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            HuntCommand = new RelayCommand(async () => await RunHuntAsync(), () => CanHunt);
            PreviewSampleCommand = new RelayCommand<SeedHuntSample>(PreviewSample);
            FinishCommand = new RelayCommand(async () => await RunFinishAsync(), () => CanFinish);
            PlayResultCommand = new RelayCommand<SeedHuntResult>(PlayResult);
            CancelCommand = new RelayCommand(() => _runCts?.Cancel(), () => IsProcessing);
            RandomSeedCommand = new RelayCommand(() => BaseSeed = NewSeed());
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);

            foreach (var s in _samples)
                s.PropertyChanged += OnSampleSelectionChanged;

            AddLog("MiniMax FFLF Seed Hunter initialized");
        }

        #region Commands

        public ICommand SelectFirstImageCommand { get; }
        public ICommand SelectLastImageCommand { get; }
        public ICommand SelectFolderCommand { get; }
        public RelayCommand RunBatchCommand { get; }
        public RelayCommand RerollPairCommand { get; }
        public RelayCommand<FflfPair> MovePairUpCommand { get; }
        public RelayCommand<FflfPair> MovePairDownCommand { get; }
        public RelayCommand ClearFolderCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand HuntCommand { get; }
        public RelayCommand<SeedHuntSample> PreviewSampleCommand { get; }
        public RelayCommand FinishCommand { get; }
        public RelayCommand<SeedHuntResult> PlayResultCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand RandomSeedCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }

        #endregion

        #region Input properties

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
                    _firstImagePreview = LoadPreview(value, out _firstImageInfo);
                    OnPropertyChanged(nameof(FirstImagePreview));
                    OnPropertyChanged(nameof(FirstImageInfo));
                    OnPropertyChanged(nameof(ResolvedAspectRatio));
                    OnCanExecuteChanged();
                }
            }
        }

        public string LastImagePath
        {
            get => _lastImagePath;
            set
            {
                if (_lastImagePath != value)
                {
                    _lastImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasLastImage));
                    _lastImagePreview = LoadPreview(value, out _lastImageInfo);
                    OnPropertyChanged(nameof(LastImagePreview));
                    OnPropertyChanged(nameof(LastImageInfo));
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? FirstImagePreview => _firstImagePreview;
        public BitmapImage? LastImagePreview => _lastImagePreview;
        public string FirstImageInfo => _firstImageInfo;
        public string LastImageInfo => _lastImageInfo;

        /// <summary>The full FL2VA H3 prompt: alignment line + the three core fields.</summary>
        public string Prompt
        {
            get => _prompt;
            set { if (_prompt != value) { _prompt = value; OnPropertyChanged(); OnCanExecuteChanged(); } }
        }

        public IReadOnlyList<string> AspectRatioOptions { get; } =
            new[] { MiniMaxH3ViewModel.AutoAspect }
                .Concat(MiniMaxH3ViewModel.AspectRatios.Select(a => a.Option)).ToList();

        public string SelectedAspectRatio
        {
            get => _selectedAspectRatio;
            set
            {
                if (_selectedAspectRatio != value && value != null)
                {
                    _selectedAspectRatio = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ResolvedAspectRatio));
                }
            }
        }

        /// <summary>The aspect shown in the UI — the picked one, or the single-mode first frame's match.</summary>
        public string ResolvedAspectRatio => ResolveAspect(FirstImagePath);

        /// <summary>Cheap canvas sizes for the seed previews. H3's native short edge is 768px, so these
        /// are all well below native — fast to sample, enough to judge composition and motion.</summary>
        public IReadOnlyList<MegapixelOption> PreviewMegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.2, "0.2 MP — fastest (≈608×352)"),
            new MegapixelOption(0.3, "0.3 MP — default (≈736×416)"),
            new MegapixelOption(0.4, "0.4 MP — sharper (≈864×480)"),
        };

        /// <summary>Canvas sizes for the finished videos. 1.0 MP ≈ 1344×768 is H3's native canvas; the
        /// workflow's RTX pass then doubles it, so the file on disk is twice these numbers.</summary>
        public IReadOnlyList<MegapixelOption> FinalMegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.7, "0.7 MP — balanced (≈1120×640 → ×2 = 2240×1280)"),
            new MegapixelOption(1.0, "1.0 MP — native (≈1344×768 → ×2 = 2688×1536)"),
            new MegapixelOption(1.3, "1.3 MP — oversized (≈1536×864 → ×2 = 3072×1728)"),
        };

        public double PreviewMegapixels
        {
            get => _previewMegapixels;
            set { if (Math.Abs(_previewMegapixels - value) > 0.0001) { _previewMegapixels = value; OnPropertyChanged(); } }
        }

        public double FinalMegapixels
        {
            get => _finalMegapixels;
            set { if (Math.Abs(_finalMegapixels - value) > 0.0001) { _finalMegapixels = value; OnPropertyChanged(); } }
        }

        /// <summary>Sampling steps for the seed previews (clamped 1–100 when applied).</summary>
        public int PreviewSteps
        {
            get => _previewSteps;
            set { if (_previewSteps != value) { _previewSteps = value; OnPropertyChanged(); } }
        }

        /// <summary>Sampling steps for the finished videos (clamped 1–100 when applied).</summary>
        public int FinalSteps
        {
            get => _finalSteps;
            set { if (_finalSteps != value) { _finalSteps = value; OnPropertyChanged(); } }
        }

        /// <summary>Clip length in seconds (H3 supports 4–15; clamped when applied to the workflow).</summary>
        public double LengthSeconds
        {
            get => _lengthSeconds;
            set { if (Math.Abs(_lengthSeconds - value) > 0.0001) { _lengthSeconds = value; OnPropertyChanged(); } }
        }

        public long BaseSeed
        {
            get => _baseSeed;
            set { if (_baseSeed != value) { _baseSeed = value; OnPropertyChanged(); } }
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

        public string CurrentPhase
        {
            get => _currentPhase;
            private set { if (_currentPhase != value) { _currentPhase = value; OnPropertyChanged(); } }
        }

        /// <summary>Single shared player source — the selected sample, or a finished video.</summary>
        public string? ActivePreviewUri
        {
            get => _activePreviewUri;
            private set
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

        /// <summary>
        /// The three sample tiles bound by the view. In single-pair mode these are the VM's own
        /// <see cref="_samples"/>; in batch mode they mirror the currently <see cref="SelectedPair"/>'s
        /// previews, so each pair keeps its own tick state.
        /// </summary>
        public ObservableCollection<SeedHuntSample> Samples =>
            IsBatchMode ? (SelectedPair?.Samples ?? _emptySamples) : _samples;

        public ObservableCollection<SeedHuntResult> Results => _results;

        // ── Folder-batch ───────────────────────────────────────────────────────
        public ObservableCollection<FflfPair> Pairs => _pairs;

        public bool IsBatchMode
        {
            get => _isBatchMode;
            private set
            {
                if (_isBatchMode != value)
                {
                    _isBatchMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsSingleMode));
                    OnPropertyChanged(nameof(Samples));
                    OnCanExecuteChanged();
                }
            }
        }

        public bool IsSingleMode => !IsBatchMode;

        public string BatchInfo
        {
            get => _batchInfo;
            private set { if (_batchInfo != value) { _batchInfo = value; OnPropertyChanged(); } }
        }

        public FflfPair? SelectedPair
        {
            get => _selectedPair;
            set
            {
                if (_selectedPair != value)
                {
                    _selectedPair = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Samples));
                    SelectedSampleForPreview = null;
                    var firstReady = value?.Samples.FirstOrDefault(s => s.HasVideo);
                    ActivePreviewUri = firstReady?.VideoFileUri;
                    OnPropertyChanged(nameof(HasSamples));
                    OnPropertyChanged(nameof(CanRerollPair));
                    OnCanExecuteChanged();
                }
            }
        }

        /// <summary>All samples across the batch (or the single-pair samples) — drives Finish selection.</summary>
        private IEnumerable<SeedHuntSample> AllSamples =>
            IsBatchMode ? _pairs.SelectMany(p => p.Samples) : _samples;

        private SeedHuntSample? _selectedSampleForPreview;
        public SeedHuntSample? SelectedSampleForPreview
        {
            get => _selectedSampleForPreview;
            set
            {
                if (_selectedSampleForPreview != value)
                {
                    _selectedSampleForPreview = value;
                    OnPropertyChanged();
                    if (value != null && value.HasVideo)
                    {
                        ActivePreviewUri = value.VideoFileUri;
                        AddLog($"Preview Sample {value.Slot}: {Path.GetFileName(value.VideoFileUri ?? "")}");
                    }
                }
            }
        }

        public bool HasFirstImage => !string.IsNullOrEmpty(FirstImagePath) && File.Exists(FirstImagePath);
        public bool HasLastImage => !string.IsNullOrEmpty(LastImagePath) && File.Exists(LastImagePath);
        public bool HasSamples => IsBatchMode
            ? (SelectedPair?.Samples.Any(s => s.HasVideo) ?? false)
            : _samples.Any(s => s.HasVideo);

        public IEnumerable<SeedHuntSample> SelectedSamples =>
            _samples.Where(s => s.IsSelected && s.HasVideo).OrderBy(s => s.Slot);
        public int SelectedCount => AllSamples.Count(s => s.IsSelected && s.HasVideo);
        public bool HasSelection => AllSamples.Any(s => s.IsSelected && s.HasVideo);

        public bool CanAnalyze => !IsBatchMode && HasFirstImage && HasLastImage && !IsAnalyzing && !IsProcessing;
        public bool CanHunt => !IsBatchMode && HasFirstImage && HasLastImage
                               && !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing && !IsAnalyzing;
        public bool CanRunBatch => IsBatchMode && _pairs.Count > 0 && !IsProcessing && !IsAnalyzing;
        public bool CanRerollPair => IsBatchMode && SelectedPair != null
                                     && !string.IsNullOrWhiteSpace(SelectedPair.Prompt)
                                     && !IsProcessing && !IsAnalyzing;
        public bool CanFinish => !IsProcessing && !IsAnalyzing && HasSelection;

        /// <summary>Single-mode reroll button is shown only once there are samples to replace.</summary>
        public bool ShowSingleReroll => IsSingleMode && HasSamples;
        public string FinishButtonText => SelectedCount > 1
            ? $"✅ Finish {SelectedCount} Selected → {FinalSteps}-step Full Res"
            : $"✅ Finish Selected → {FinalSteps}-step Full Res";

        #endregion

        #region Image selection

        private async void SelectImage(bool isFirst)
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                isFirst ? "Select First Frame" : "Select Last Frame",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: isFirst ? "minimaxfflf.first" : "minimaxfflf.last");

            if (path != null)
            {
                if (isFirst) FirstImagePath = path; else LastImagePath = path;
                AddLog($"{(isFirst ? "First" : "Last")} frame: {Path.GetFileName(path)}");
            }
        }

        private BitmapImage? LoadPreview(string path, out string info)
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

        /// <summary>
        /// The ResolutionSelector aspect for a pair: the user's pick, or (on Auto) the nearest option
        /// to the FIRST frame's own aspect ratio.
        /// </summary>
        private string ResolveAspect(string firstImagePath)
        {
            if (SelectedAspectRatio != MiniMaxH3ViewModel.AutoAspect) return SelectedAspectRatio;

            int w = 0, h = 0;
            if (!IsBatchMode && FirstImagePreview is { } preview
                && string.Equals(firstImagePath, FirstImagePath, StringComparison.OrdinalIgnoreCase))
            {
                w = preview.PixelWidth; h = preview.PixelHeight;
            }
            if ((w <= 0 || h <= 0) && !string.IsNullOrEmpty(firstImagePath) && File.Exists(firstImagePath))
            {
                try
                {
                    using var fs = File.OpenRead(firstImagePath);
                    var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    w = frame.PixelWidth; h = frame.PixelHeight;
                }
                catch { /* fall through to the 16:9 default */ }
            }
            return MiniMaxH3ViewModel.ClosestAspectRatio(w, h);
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
                var model = await ResolveLlmModelAsync(token);
                var cleaned = await AnalyzePairAsync(model, FirstImagePath, LastImagePath, Prompt, token);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    Prompt = cleaned;
                    AddLog($"H3 prompt written ({cleaned.Length} chars)");
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
        /// Sends a first/last frame pair to the LLM (<c>h3minimax-fflf.md</c> system prompt) and returns
        /// the cleaned FL2VA prompt, alignment line guaranteed. Shared by Analyze and the batch runner.
        /// </summary>
        private async Task<string> AnalyzePairAsync(string model, string firstPath, string lastPath,
            string draft, CancellationToken token)
        {
            AddLog($"Writing the MiniMax H3 FL2VA prompt — sending both frames to {_lmStudioService.DescribeTarget(model)}");

            var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "prompts", "prompt2json", SystemPromptFile);
            if (!File.Exists(promptFilePath))
                throw new FileNotFoundException($"System prompt not found: {promptFilePath}");

            var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);
            var len = ClampLength(LengthSeconds);
            var idea = string.IsNullOrWhiteSpace(draft)
                ? "(none — invent the most natural transition between the two frames)"
                : draft.Trim();

            var userMessage =
                $"Image 1 is Picture 1, the FIRST frame at 0.00 seconds.\n" +
                $"Image 2 is Picture 2, the LAST frame at {len.ToString("0.00", CultureInfo.InvariantCulture)} seconds.\n" +
                $"Target duration: {len.ToString("0.00", CultureInfo.InvariantCulture)} seconds.\n" +
                $"Draft idea from the user:\n{idea}";

            var result = await _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                model,
                new[] { firstPath, lastPath },
                userMessage,
                systemPrompt,
                maxTokens: 4000,
                cancellationToken: token);

            return EnsureFl2vaInstruction(CleanOutput(result), len);
        }

        /// <summary>Resolves the LLM model id once (shared by Analyze and batch). Throws if none.</summary>
        private async Task<string> ResolveLlmModelAsync(CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
            await _lmStudioService.SetBaseUrlAsync(baseUrl);

            var models = await _lmStudioService.GetAvailableModelsAsync(token);
            var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
            if (string.IsNullOrEmpty(selectedModel) && models.Count > 0)
                selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;
            if (string.IsNullOrEmpty(selectedModel))
                throw new Exception("No LM Studio / llama-server model available. Ensure the server is running and a model is loaded.");
            return selectedModel;
        }

        /// <summary>
        /// Strips the wrappers small vision models like to add (code fences, bold markers, a leading
        /// "prompt:" label, surrounding quotes) without touching the H3 field structure.
        /// </summary>
        private static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Replace("**", "").Trim();

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

        /// <summary>The fixed FL2VA alignment sentence, carrying the clip's real duration.</summary>
        private static string Fl2vaInstruction(double seconds) =>
            "How the reference pictures align with the target video — Picture 1 (from Shot 1) aligns with " +
            "the 0.00-second mark of the target video; Picture 2 (from Shot 1) aligns with the " +
            seconds.ToString("0.00", CultureInfo.InvariantCulture) + "-second mark of the target video.";

        /// <summary>
        /// Guarantees the FL2VA alignment sentence is the first line and that its last-frame timestamp
        /// matches the duration actually being submitted — H3 reads that line as the instruction pinning
        /// Picture 1 to 0.00s and Picture 2 to the end. An existing line is edited in place (only its
        /// final timestamp), so a multi-shot prompt keeps whichever shot the model assigned Picture 2 to.
        /// </summary>
        private static string EnsureFl2vaInstruction(string prompt, double seconds)
        {
            var t = (prompt ?? string.Empty).Trim();
            if (t.Length == 0) return t;
            if (!t.StartsWith("How the reference pictures align", StringComparison.OrdinalIgnoreCase))
                return $"{Fl2vaInstruction(seconds)}\n\n{t}";

            var brk = t.IndexOf('\n');
            var line = brk > 0 ? t[..brk] : t;
            var rest = brk > 0 ? t[(brk + 1)..] : string.Empty;

            var marks = SecondMarkRegex.Matches(line);
            if (marks.Count >= 2)
            {
                var last = marks[^1];
                line = line[..last.Index]
                       + seconds.ToString("0.00", CultureInfo.InvariantCulture) + "-second mark"
                       + line[(last.Index + last.Length)..];
            }
            return rest.Length == 0 ? line : $"{line}\n{rest}";
        }

        /// <summary>H3's supported clip length is 4–15 seconds at 24 fps.</summary>
        private static double ClampLength(double seconds) =>
            Math.Clamp(seconds <= 0 ? 5 : seconds, 4, 15);

        #endregion

        #region Selection

        private void PreviewSample(SeedHuntSample? sample)
        {
            if (sample == null || !sample.HasVideo) return;
            SelectedSampleForPreview = sample;
            ActivePreviewUri = sample.VideoFileUri;
        }

        private void PlayResult(SeedHuntResult? result)
        {
            if (result != null) ActivePreviewUri = result.VideoFileUri;
        }

        /// <summary>Called by the view when the shared MediaElement fails to open the preview.</summary>
        public void ReportPreviewFailed(string message) =>
            AddLog($"Preview playback failed: {message} (uri: {ActivePreviewUri})");

        /// <summary>Called by the view when the shared MediaElement successfully opens a preview.</summary>
        public void ReportPreviewOpened(string uri) =>
            AddLog($"Preview opened: {uri}");

        #endregion

        #region Stage 1 — Hunt / Reroll

        private async Task RunHuntAsync()
        {
            if (!CanHunt) return;

            // Reroll always gets a fresh seed; a first-time run honors a user-pinned seed.
            if (HasSamples || BaseSeed < 0) BaseSeed = NewSeed();
            var batchSeed = BaseSeed;
            _currentBatchSeed = batchSeed;
            var batchId = DateTime.Now.ToString("yyyyMMddHHmmss");

            await RunWorkflowAsync("Hunt", async (token, reportPhase) =>
            {
                SelectedSampleForPreview = null;
                _results.Clear();
                ActivePreviewUri = null;
                HasResult = false;

                var firstName = await EnsureUploadedAsync(FirstImagePath);
                var lastName = await EnsureUploadedAsync(LastImagePath);

                var found = await HuntCoreAsync(token, firstName, lastName, Prompt, FirstImagePath,
                    batchSeed, batchId, _samples, 0, 100, reportPhase);
                if (found == 0)
                    throw new Exception("No sample previews were produced.");
                ProcessingStatus = $"{found}/{SampleSlots} samples ready — pick one, then Finish";
            });
        }

        /// <summary>
        /// Runs one hunt batch for a first/last pair: <see cref="SampleSlots"/> sequential submissions at
        /// preview quality with seeds base, base+1, base+2, filling <paramref name="samples"/>. Used by
        /// the single-pair hunt, the folder batch and the per-pair reroll. Returns previews produced.
        /// </summary>
        private async Task<int> HuntCoreAsync(CancellationToken token, string firstName, string lastName,
            string prompt, string firstImagePath, long batchSeed, string batchId,
            ObservableCollection<SeedHuntSample> samples, double progressFrom, double progressTo,
            Action<string> reportPhase)
        {
            Application.Current.Dispatcher.Invoke(() => { foreach (var s in samples) s.Reset(); });
            OnPropertyChanged(nameof(HasSamples));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedCount));

            var aspect = ResolveAspect(firstImagePath);
            var len = ClampLength(LengthSeconds);
            var steps = ClampSteps(PreviewSteps);
            var mp = ClampMegapixels(PreviewMegapixels);
            AddLog($"Previews (turbo, no RTX pass): {aspect}, {mp:0.0} MP, {steps} steps, {len:0.#}s — seeds {batchSeed}..{batchSeed + SampleSlots - 1}");

            var span = progressTo - progressFrom;
            int found = 0;
            for (int slot = 1; slot <= samples.Count; slot++)
            {
                token.ThrowIfCancellationRequested();
                var seed = batchSeed + (slot - 1);
                reportPhase($"Sample {slot}/{samples.Count} (seed {seed})...");
                SetSampleStatus(samples, slot, "generating");

                var json = await LoadWorkflowJsonAsync(token);
                ApplyCommonInputs(ref json, firstName, lastName, prompt, aspect, mp, steps, len,
                    upscale: false);
                SetInput(ref json, NodeSeed, "seed", seed);
                var runToken = $"mmf{batchId}_p{slot}";
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeOutput, "filename_prefix",
                    $"{OutputSubfolder}/{runToken}");

                var from = progressFrom + (slot - 1) * span / samples.Count;
                var to = progressFrom + slot * span / samples.Count;
                var local = await SubmitAndRetrieveAsync(json, runToken, from, to, PreviewRunTimeout, token);
                if (local != null)
                {
                    SetSampleVideo(samples, slot, local);
                    found++;
                }
                else
                {
                    SetSampleStatus(samples, slot, "no output");
                    AddLog($"  Sample {slot}: no output produced");
                }
            }

            OnPropertyChanged(nameof(HasSamples));
            return found;
        }

        /// <summary>
        /// Writes every per-run input into the workflow. Preview and finish differ only in
        /// <paramref name="megapixels"/>, <paramref name="steps"/> and <paramref name="upscale"/>; the
        /// seed and output prefix are set by the caller.
        /// </summary>
        private void ApplyCommonInputs(ref string json, string firstName, string lastName, string prompt,
            string aspect, double megapixels, int steps, double lengthSeconds, bool upscale)
        {
            json = AttachFrameLoaders(json, firstName, lastName, upscale);

            SetInput(ref json, NodeImageToVideo, "prompt",
                EnsureFl2vaInstruction(prompt.Trim(), lengthSeconds));
            SetInput(ref json, NodeResolution, "aspect_ratio", aspect);
            SetInput(ref json, NodeResolution, "megapixels", megapixels);
            SetInput(ref json, NodeSteps, "steps", steps);
            SetInput(ref json, NodeDuration, "value", lengthSeconds);
        }

        /// <summary>
        /// Turns the text-to-video export into a first/last-frame graph: injects the two
        /// <c>LoadImage</c> nodes the file has none of and links them into
        /// <c>MiniMaxH3ImageToVideo</c>'s <c>first_frame</c>/<c>last_frame</c>.
        ///
        /// <para>It also decides where <c>CreateVideo</c> takes its frames from. Finals go through the
        /// workflow's ×2 <c>RTXVideoSuperResolution</c> pass as authored; seed previews are wired
        /// straight off the <c>VAEDecode</c>, since paying for a super-resolution pass on a 0.3 MP
        /// throwaway is the one thing a seed hunt must not do. The RTX node is simply left unreferenced
        /// then, so ComfyUI never executes it.</para>
        /// </summary>
        private static string AttachFrameLoaders(string json, string firstName, string lastName, bool upscale)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeImageToVideo, "MiniMaxH3ImageToVideo");
            RequireClass(root, NodeResolution, "ResolutionSelector");
            RequireClass(root, NodeSteps, "BasicScheduler");
            RequireClass(root, NodeSeed, "Seed (rgthree)");
            RequireClass(root, NodeDuration, "PrimitiveFloat");
            RequireClass(root, NodeVaeDecode, "VAEDecode");
            RequireClass(root, NodeRtx, "RTXVideoSuperResolution");
            RequireClass(root, NodeCreateVideo, "CreateVideo");
            RequireClass(root, NodeOutput, "SaveVideo");

            foreach (var (id, uploadedName, title) in new[]
                     {
                         (NodeFirstImage, firstName, "First Frame"),
                         (NodeLastImage, lastName, "Last Frame"),
                     })
            {
                // A re-export that happened to reuse these ids would otherwise be silently overwritten.
                if (root[id] is JsonObject taken && taken["class_type"]?.GetValue<string>() != "LoadImage")
                    throw new Exception(
                        $"Workflow node '{id}' is already a {taken["class_type"]?.GetValue<string>() ?? "(none)"} — " +
                        "the frame loaders this tab injects need that id free.");

                root[id] = new JsonObject
                {
                    ["inputs"] = new JsonObject { ["image"] = uploadedName },
                    ["class_type"] = "LoadImage",
                    ["_meta"] = new JsonObject { ["title"] = title },
                };
            }

            if (root[NodeImageToVideo]?["inputs"] is not JsonObject i2vInputs)
                throw new Exception($"Workflow node '{NodeImageToVideo}' has no inputs — the workflow file no longer matches this tab.");
            i2vInputs["first_frame"] = new JsonArray(NodeFirstImage, 0);
            i2vInputs["last_frame"] = new JsonArray(NodeLastImage, 0);

            if (root[NodeCreateVideo]?["inputs"] is not JsonObject videoInputs)
                throw new Exception($"Workflow node '{NodeCreateVideo}' has no inputs — the workflow file no longer matches this tab.");
            videoInputs["images"] = upscale ? new JsonArray(NodeRtx, 0) : new JsonArray(NodeVaeDecode, 0);

            return root.ToJsonString();
        }

        /// <summary>Fails loudly when a node the patches rewire is missing or is no longer the class they
        /// assume — both would otherwise produce a graph that only fails on the server, or worse,
        /// silently renders the workflow's baked-in demo prompt.</summary>
        private static void RequireClass(JsonObject root, string nodeId, string expected)
        {
            if (root[nodeId] is not JsonObject node)
                throw new Exception($"Workflow node '{nodeId}' is not in the graph — the workflow file no longer matches this tab.");
            var actual = node["class_type"]?.GetValue<string>();
            if (actual != expected)
                throw new Exception($"Workflow node '{nodeId}' is a {actual ?? "(none)"}, expected {expected} — the workflow file no longer matches this tab.");
        }

        /// <summary>Wrapper around <see cref="WorkflowNodeUpdater.UpdateNodeInput"/> that fails loudly on
        /// a node id that is no longer in the graph — the updater silently no-ops instead.</summary>
        private static void SetInput(ref string json, string nodeId, string input, object value)
        {
            if (WorkflowNodeUpdater.GetNodeInput(json, nodeId, input) == null)
                throw new Exception($"Workflow node '{nodeId}' has no input '{input}' — the workflow file no longer matches this tab.");
            WorkflowNodeUpdater.UpdateNodeInput(ref json, nodeId, input, value);
        }

        /// <summary>The turbo stack is a 4-step LoRA; the workflow ships 8 steps for a final.</summary>
        private static int ClampSteps(int steps) => Math.Clamp(steps <= 0 ? 4 : steps, 1, 100);

        /// <summary>ResolutionSelector accepts 0.1–16.0 MP; anything above ~2 MP is far off H3's canvas.</summary>
        private static double ClampMegapixels(double mp) => Math.Clamp(mp <= 0 ? 0.3 : mp, 0.1, 4.0);

        /// <summary>Submits one H3 render, waits, and resolves the SaveVideo (node 92) output to a local
        /// file — via /history node outputs first, then a disk scan for this run's token. The disk-scan
        /// window matches <paramref name="runTimeout"/> so it can't give up before the render could.</summary>
        private async Task<string?> SubmitAndRetrieveAsync(string json, string runToken,
            double from, double to, TimeSpan runTimeout, CancellationToken token)
        {
            var existing = GetExistingVideoFiles("*.mp4", OutputSubfolder);
            var promptId = await SubmitAsync(json, from, to, token, runTimeout);

            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
            if (byNode.TryGetValue(NodeOutput, out var outs) && outs.Count > 0)
            {
                var pick = outs.FirstOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? outs[0];
                var local = await ResolveOutputToLocalAsync(pick);
                if (local != null) return local;
            }

            var found = await WaitForNewVideoAsync(existing, "*.mp4",
                runTimeout, TimeSpan.FromSeconds(4), OutputSubfolder);
            if (found != null && Path.GetFileName(found).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0)
                return found;
            return found ?? FindTokenFileOnDisk(runToken);
        }

        /// <summary>Disk fallback: newest mp4 in the output (sub)folder whose name carries the run token.</summary>
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
                        candidates.AddRange(Directory.GetFiles(folder, "*.mp4")
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

        private void SetSampleVideo(ObservableCollection<SeedHuntSample> samples, int slot, string localPath)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var sample = samples.First(s => s.Slot == slot);
                sample.VideoPath = localPath;
                sample.VideoFileUri = localPath;
                sample.Status = "ready";
                OnPropertyChanged(nameof(HasSamples));
                AddLog($"  Sample {slot} ready: {Path.GetFileName(localPath)}");
                OnCanExecuteChanged();
            });

            var thumb = ExtractFirstFrame(localPath);
            if (thumb != null)
                Application.Current.Dispatcher.Invoke(() =>
                    samples.First(s => s.Slot == slot).ThumbnailImage = thumb);
        }

        private void SetSampleStatus(ObservableCollection<SeedHuntSample> samples, int slot, string status) =>
            Application.Current.Dispatcher.Invoke(() => samples.First(s => s.Slot == slot).Status = status);

        private BitmapImage? ExtractFirstFrame(string videoPath)
        {
            try
            {
                var ffmpeg = FindFFmpeg();
                if (ffmpeg == null) return null;
                var outPath = Path.Combine(Path.GetTempPath(), $"mmfflf_thumb_{Guid.NewGuid():N}.png");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -i \"{videoPath}\" -frames:v 1 -q:v 3 \"{outPath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return null;
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                }
                if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0) return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(outPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                AddLog($"Thumbnail extract failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Finish — full resolution, full steps

        /// <summary>One queued seed preview to re-render at full quality (covers single and batch flows).</summary>
        private sealed record FinishItem(string FirstPath, string LastPath, string Prompt,
            long BatchSeed, int Slot, int PairIndex);

        private List<FinishItem> BuildFinishWorklist()
        {
            if (IsBatchMode)
            {
                return _pairs
                    .SelectMany(p => p.Samples
                        .Where(s => s.IsSelected && s.HasVideo)
                        .OrderBy(s => s.Slot)
                        .Select(s => new FinishItem(p.FirstImagePath, p.LastImagePath, p.Prompt,
                            p.BatchSeed, s.Slot, p.Index)))
                    .ToList();
            }
            var batchSeed = _currentBatchSeed >= 0 ? _currentBatchSeed : BaseSeed;
            return SelectedSamples
                .Select(s => new FinishItem(FirstImagePath, LastImagePath, Prompt, batchSeed, s.Slot, 0))
                .ToList();
        }

        private async Task RunFinishAsync()
        {
            var work = BuildFinishWorklist();
            if (work.Count == 0) return;

            await RunWorkflowAsync("Finish", async (token, reportPhase) =>
            {
                var steps = ClampSteps(FinalSteps);
                var mp = ClampMegapixels(FinalMegapixels);
                var len = ClampLength(LengthSeconds);

                int done = 0;
                var finishedPaths = new List<string>(); // completed videos, in work order — joined at the end
                foreach (var item in work)
                {
                    token.ThrowIfCancellationRequested();
                    var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var label = item.PairIndex > 0 ? $"Pair {item.PairIndex} · Sample {item.Slot}" : $"Sample {item.Slot}";
                    reportPhase($"Finishing {label} ({done + 1}/{work.Count}) — {mp:0.0} MP, {steps} steps, RTX ×2...");

                    var firstName = await EnsureUploadedAsync(item.FirstPath);
                    var lastName = await EnsureUploadedAsync(item.LastPath);
                    var aspect = ResolveAspect(item.FirstPath);

                    // Same prompt, duration, aspect and seed as the chosen preview — only the canvas
                    // size, the step count and the RTX pass go up.
                    var json = await LoadWorkflowJsonAsync(token);
                    ApplyCommonInputs(ref json, firstName, lastName, item.Prompt, aspect, mp, steps, len,
                        upscale: true);
                    SetInput(ref json, NodeSeed, "seed", item.BatchSeed + (item.Slot - 1));

                    var runToken = item.PairIndex > 0
                        ? $"final_p{item.PairIndex}_s{item.Slot}_{ts}"
                        : $"final_s{item.Slot}_{ts}";
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeOutput, "filename_prefix",
                        $"{OutputSubfolder}/{runToken}");

                    var from = done * 100.0 / work.Count;
                    var to = (done + 1) * 100.0 / work.Count;
                    string? outputVideo;
                    try
                    {
                        outputVideo = await SubmitAndRetrieveAsync(json, runToken, from, to, FinishRunTimeout, token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // One bad render shouldn't discard the rest of the queue — log it and keep going.
                        AddLog($"{label} failed: {ex.Message}");
                        continue;
                    }

                    if (outputVideo == null || !File.Exists(outputVideo))
                    {
                        AddLog($"{label}: no final video produced — skipping");
                        continue;
                    }

                    var outputDir = Path.Combine(
                        _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "MiniMaxFflf");
                    Directory.CreateDirectory(outputDir);
                    var nameStem = item.PairIndex > 0
                        ? $"MiniMaxFflf_p{item.PairIndex}_s{item.Slot}_{ts}"
                        : $"MiniMaxFflf_s{item.Slot}_{ts}";
                    var finalPath = Path.Combine(outputDir, $"{nameStem}.mp4");
                    File.Copy(outputVideo, finalPath, true);
                    await LocalCopyService.CopyVideoAsync(finalPath);

                    var fi = new FileInfo(finalPath);
                    var result = new SeedHuntResult
                    {
                        Slot = item.Slot,
                        PairIndex = item.PairIndex,
                        VideoPath = finalPath,
                        VideoFileUri = finalPath,
                        Info = $"{label} • {mp:0.0} MP • {fi.Length / 1024 / 1024.0:F1}MB"
                    };
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _results.Add(result);
                        ResultVideoPath = finalPath;
                        ResultVideoInfo = result.Info;
                        ActivePreviewUri = finalPath;
                        HasResult = true;
                        OnCanExecuteChanged();
                    });
                    AddLog($"=== {label} complete: {finalPath} ===");
                    finishedPaths.Add(finalPath);
                    done++;
                }

                // Auto-join every finished clip (in selection / pair order) into one continuous video.
                if (finishedPaths.Count > 1)
                {
                    reportPhase($"Joining {finishedPaths.Count} videos into one...");
                    await JoinFinishedVideosAsync(finishedPaths, token);
                }

                ProcessingStatus = done == work.Count
                    ? $"Finished {done} video(s)!"
                    : $"Finished {done}/{work.Count} video(s)";
            });
        }

        /// <summary>
        /// FFmpeg-concatenates all finished videos (in selection / pair order) into one continuous MP4,
        /// adds it as a result, and loads it in the shared player. Best-effort: a concat failure leaves
        /// the individual finished videos intact.
        /// </summary>
        private async Task JoinFinishedVideosAsync(IReadOnlyList<string> clips, CancellationToken token)
        {
            try
            {
                var ffmpeg = FindFFmpeg();
                if (ffmpeg == null)
                {
                    AddLog("Join skipped: FFmpeg not found.");
                    return;
                }

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "MiniMaxFflf");
                Directory.CreateDirectory(outputDir);
                var joinedPath = Path.Combine(outputDir,
                    $"MiniMaxFflf_joined_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                await ConcatClipsAsync(ffmpeg, clips, joinedPath, token);
                if (!File.Exists(joinedPath) || new FileInfo(joinedPath).Length == 0)
                {
                    AddLog("Join produced no file.");
                    return;
                }

                await LocalCopyService.CopyVideoAsync(joinedPath);
                var fi = new FileInfo(joinedPath);
                var result = new SeedHuntResult
                {
                    VideoPath = joinedPath,
                    VideoFileUri = joinedPath,
                    LabelOverride = $"🎬 Joined ({clips.Count})",
                    Info = $"Joined {clips.Count} clips • {fi.Length / 1024 / 1024.0:F1}MB"
                };
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _results.Add(result);
                    ResultVideoPath = joinedPath;
                    ResultVideoInfo = result.Info;
                    ActivePreviewUri = joinedPath;
                    HasResult = true;
                    OnCanExecuteChanged();
                });
                AddLog($"=== Joined video complete: {joinedPath} ===");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AddLog($"Join failed: {ex.Message}");
            }
        }

        /// <summary>Concatenates clips (all share the run's resolution/fps) into one MP4 via FFmpeg's
        /// concat demuxer with a re-encode (robust to codec edge-cases; keeps H3's generated audio).</summary>
        private async Task ConcatClipsAsync(string ffmpeg, IReadOnlyList<string> clips, string outPath, CancellationToken token)
        {
            if (clips.Count == 1)
            {
                File.Copy(clips[0], outPath, true);
                return;
            }

            var listPath = Path.Combine(Path.GetTempPath(), $"mmfflf_concat_{Guid.NewGuid():N}.txt");
            var sb = new System.Text.StringBuilder();
            foreach (var clip in clips)
                sb.AppendLine($"file '{clip.Replace("'", "'\\''")}'");
            await File.WriteAllTextAsync(listPath, sb.ToString(), token);

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in new[]
                {
                    "-y", "-f", "concat", "-safe", "0", "-i", listPath,
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-c:a", "aac", "-b:a", "192k", "-pix_fmt", "yuv420p", outPath
                }) psi.ArgumentList.Add(a);

                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) throw new Exception("Failed to start FFmpeg.");
                var stderr = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(token);
                if (p.ExitCode != 0)
                {
                    var tail = stderr.Length <= 400 ? stderr : stderr.Substring(stderr.Length - 400);
                    AddLog($"FFmpeg concat exited {p.ExitCode}: {tail}");
                }
            }
            finally
            {
                try { File.Delete(listPath); } catch { /* best effort */ }
            }
        }

        private void OnSampleSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SeedHuntSample.IsSelected)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(FinishButtonText));
            OnCanExecuteChanged();
        }

        #endregion

        #region Folder batch

        /// <summary>
        /// Pick a folder of images, order them by creation time, and build overlapping first→last
        /// pairs (image i → image i+1). Enters batch mode.
        /// </summary>
        private async Task SelectFolderAsync()
        {
            if (IsProcessing || IsAnalyzing) return;

            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var folder = await _fileDialogService.OpenFolderDialogAsync(
                "Select a folder of images (ordered by creation time → overlapping FFLF pairs)",
                initialDir, showNewFolderButton: false, persistKey: "minimaxfflf.folder");
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            LoadFolder(folder);
        }

        /// <summary>
        /// Loads a folder of images into batch mode. Images are ordered by creation time (then filename)
        /// and chained into overlapping FFLF pairs. Must be called on the UI thread.
        /// </summary>
        public void LoadFolder(string folder)
        {
            if (IsProcessing || IsAnalyzing) return;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
            var images = Directory.EnumerateFiles(folder)
                .Where(f => exts.Contains(Path.GetExtension(f)))
                .OrderBy(ImageOrderKey)
                .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (images.Count < 2)
            {
                MessageBox.Show($"Found {images.Count} image(s) in:\n{folder}\n\nNeed at least 2 to form a FFLF pair.",
                    "Not enough images", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResetBatchCollections();

            for (int i = 0; i < images.Count - 1; i++)
            {
                var pair = new FflfPair(i + 1, images[i], images[i + 1])
                {
                    FirstThumb = LoadThumb(images[i]),
                    LastThumb = LoadThumb(images[i + 1]),
                };
                foreach (var s in pair.Samples)
                {
                    s.PropertyChanged += OnSampleSelectionChanged;
                    _batchSampleSubs.Add(s);
                }
                _pairs.Add(pair);
            }

            BatchInfo = $"{images.Count} images → {_pairs.Count} pairs";
            IsBatchMode = true;
            _results.Clear();
            ActivePreviewUri = null;
            HasResult = false;
            SelectedPair = _pairs.FirstOrDefault();
            AddLog($"Folder loaded: {folder} — {images.Count} images → {_pairs.Count} overlapping pairs");
        }

        /// <summary>Creation time (the user's ordering), falling back to last-write if unavailable.</summary>
        private static DateTime ImageOrderKey(string path)
        {
            try
            {
                var created = File.GetCreationTime(path);
                var written = File.GetLastWriteTime(path);
                // A copied/moved file can carry a creation time newer than its real write time; take the earlier.
                return created <= written ? created : written;
            }
            catch { return DateTime.MaxValue; }
        }

        private static BitmapImage? LoadThumb(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 140;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private void ResetBatchCollections()
        {
            foreach (var s in _batchSampleSubs) s.PropertyChanged -= OnSampleSelectionChanged;
            _batchSampleSubs.Clear();
            _selectedPair = null;
            OnPropertyChanged(nameof(SelectedPair));
            _pairs.Clear();
            _uploadCache.Clear();
        }

        private void ClearFolder()
        {
            if (IsProcessing || IsAnalyzing) return;
            ResetBatchCollections();
            IsBatchMode = false;
            BatchInfo = string.Empty;
            ActivePreviewUri = null;
            OnPropertyChanged(nameof(Samples));
            OnPropertyChanged(nameof(HasSamples));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedCount));
            AddLog("Exited batch mode");
        }

        /// <summary>
        /// Walks every pair sequentially: analyze first+last into an FL2VA prompt → generate 3 seed
        /// previews. A failed pair is logged and skipped; the batch continues.
        /// </summary>
        private async Task RunBatchAsync()
        {
            if (!CanRunBatch) return;

            await RunWorkflowAsync("Batch", async (token, reportPhase) =>
            {
                _results.Clear();
                HasResult = false;
                ActivePreviewUri = null;

                var model = await ResolveLlmModelAsync(token);
                int total = _pairs.Count, idx = 0, ok = 0;

                foreach (var pair in _pairs)
                {
                    token.ThrowIfCancellationRequested();
                    idx++;
                    var span = 100.0 / total;
                    var from = (idx - 1) * span;
                    try
                    {
                        SetPairStatus(pair, "analyzing");
                        reportPhase($"Pair {idx}/{total}: analyzing {Path.GetFileName(pair.FirstImagePath)} → {Path.GetFileName(pair.LastImagePath)}...");
                        var prompt = await AnalyzePairAsync(model, pair.FirstImagePath, pair.LastImagePath,
                            pair.Prompt, token);
                        if (string.IsNullOrWhiteSpace(prompt))
                            throw new Exception("LLM returned an empty prompt");
                        Application.Current.Dispatcher.Invoke(() => pair.Prompt = prompt);

                        var firstName = await EnsureUploadedAsync(pair.FirstImagePath);
                        var lastName = await EnsureUploadedAsync(pair.LastImagePath);

                        var seed = NewSeed();
                        Application.Current.Dispatcher.Invoke(() => pair.BatchSeed = seed);
                        SetPairStatus(pair, "generating");
                        var batchId = DateTime.Now.ToString("yyyyMMddHHmmss") + $"_{idx}";

                        var found = await HuntCoreAsync(token, firstName, lastName, prompt, pair.FirstImagePath,
                            seed, batchId, pair.Samples, from, from + span,
                            status => reportPhase($"Pair {idx}/{total}: {status}"));

                        SetPairStatus(pair, found > 0 ? $"ready {found}/{SampleSlots}" : "no output");
                        if (found > 0) ok++;

                        // Surface the first finished pair in the player as soon as it's ready.
                        if (SelectedPair == pair) Application.Current.Dispatcher.Invoke(() =>
                            ActivePreviewUri ??= pair.Samples.FirstOrDefault(s => s.HasVideo)?.VideoFileUri);
                    }
                    catch (OperationCanceledException) { SetPairStatus(pair, "cancelled"); throw; }
                    catch (Exception ex)
                    {
                        AddLog($"Pair {idx} failed: {ex.Message}");
                        SetPairStatus(pair, "failed");
                    }
                }

                OnPropertyChanged(nameof(HasSamples));
                ProcessingStatus = $"Batch done — {ok}/{total} pairs produced previews. Pick seeds, then Finish.";
            });
        }

        /// <summary>
        /// Rerolls just the currently <see cref="SelectedPair"/> with a fresh seed → 3 new previews,
        /// reusing the pair's existing prompt (no re-analyze). Leaves every other pair untouched.
        /// </summary>
        private async Task RerollPairAsync()
        {
            var pair = SelectedPair;
            if (pair == null || !CanRerollPair) return;

            await RunWorkflowAsync("Reroll", async (token, reportPhase) =>
            {
                if (string.IsNullOrWhiteSpace(pair.Prompt))
                    throw new Exception("This pair has no prompt yet. Run Batch first to analyze it.");

                _results.Clear();
                HasResult = false;
                ActivePreviewUri = null;
                SelectedSampleForPreview = null;

                var firstName = await EnsureUploadedAsync(pair.FirstImagePath);
                var lastName = await EnsureUploadedAsync(pair.LastImagePath);

                var seed = NewSeed();
                Application.Current.Dispatcher.Invoke(() => pair.BatchSeed = seed);
                SetPairStatus(pair, "rerolling");
                var batchId = DateTime.Now.ToString("yyyyMMddHHmmss") + $"_p{pair.Index}r";

                reportPhase($"Pair {pair.Index}: rerolling {SampleSlots} new seeds...");
                var found = await HuntCoreAsync(token, firstName, lastName, pair.Prompt, pair.FirstImagePath,
                    seed, batchId, pair.Samples, 0, 100,
                    status => reportPhase($"Pair {pair.Index}: {status}"));

                SetPairStatus(pair, found > 0 ? $"ready {found}/{SampleSlots}" : "no output");
                if (found == 0)
                    throw new Exception("No sample previews were produced.");

                Application.Current.Dispatcher.Invoke(() =>
                    ActivePreviewUri = pair.Samples.FirstOrDefault(s => s.HasVideo)?.VideoFileUri);
                ProcessingStatus = $"Pair {pair.Index}: {found}/{SampleSlots} fresh samples ready — pick one, then Finish";
            });
        }

        private void SetPairStatus(FflfPair pair, string status) =>
            Application.Current.Dispatcher.Invoke(() => pair.Status = status);

        /// <summary>
        /// Moves a pair one slot up (<paramref name="delta"/> = -1) or down (+1) in the batch order.
        /// Pairs are renumbered so labels follow the new order, and Finish concatenates the final clips
        /// in this same order — so reordering pairs reorders the joined output video.
        /// </summary>
        private void MovePair(FflfPair? pair, int delta)
        {
            if (pair == null || IsProcessing || IsAnalyzing) return;
            int i = _pairs.IndexOf(pair);
            if (i < 0) return;
            int j = i + delta;
            if (j < 0 || j >= _pairs.Count) return;

            _pairs.Move(i, j);
            RenumberPairs();
            SelectedPair = pair; // keep the moved pair selected/highlighted
            AddLog($"Moved pair to position {j + 1} of {_pairs.Count}");
        }

        private void RenumberPairs()
        {
            for (int k = 0; k < _pairs.Count; k++)
                _pairs[k].Index = k + 1;
        }

        #endregion

        #region Shared workflow runner

        private async Task RunWorkflowAsync(string phase, Func<CancellationToken, Action<string>, Task> body)
        {
            IsProcessing = true;
            CurrentPhase = phase;
            ProcessingProgress = 0;
            ProcessingStatus = $"Preparing {phase}...";

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                AddLog($"=== MiniMax FFLF {phase} ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("MiniMaxFflf", token);

                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                await body(token, status => Application.Current.Dispatcher.Invoke(() => ProcessingStatus = status));

                ProcessingProgress = 100;
            }
            catch (OperationCanceledException)
            {
                AddLog($"{phase} cancelled");
                ProcessingStatus = "Cancelled";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR ({phase}): {ex.Message}");
                ProcessingStatus = $"Error: {ex.Message}";
                MessageBox.Show($"{phase} failed:\n{ex.Message}", "MiniMax FFLF Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                lease?.Dispose();
                IsProcessing = false;
                CurrentPhase = string.Empty;
                _runCts?.Dispose();
                _runCts = null;
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Uploads an image to ComfyUI once, caching the returned filename by path so overlapping batch
        /// pairs (which share a frame) don't re-upload the same file.
        /// </summary>
        private async Task<string> EnsureUploadedAsync(string path)
        {
            if (_uploadCache.TryGetValue(path, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;
            AddLog($"Uploading {Path.GetFileName(path)}...");
            var name = await _comfyUIService.UploadImageAsync(path);
            if (string.IsNullOrEmpty(name)) throw new Exception($"Failed to upload {Path.GetFileName(path)}.");
            _uploadCache[path] = name;
            AddLog($"Uploaded: {name}");
            return name;
        }

        private static async Task<string> LoadWorkflowJsonAsync(CancellationToken token)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Workflow file not found: {path}");
            return await File.ReadAllTextAsync(path, token);
        }

        private async Task<string> SubmitAsync(string json, double progressFrom, double progressTo,
            CancellationToken token, TimeSpan? executionTimeout = null)
        {
            var workflow = JsonSerializer.Deserialize<JsonElement>(json);
            var span = progressTo - progressFrom;
            var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                {
                    var pct = (double)msg.Data.Value / msg.Data.Max;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProcessingProgress = progressFrom + pct * span;
                        ProcessingStatus = $"{CurrentPhase}: {msg.Data.Value}/{msg.Data.Max}";
                    });
                }
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token, executionTimeout);
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
                    var tempPath = Path.Combine(Path.GetTempPath(), $"mmfflf_{Guid.NewGuid():N}_{filename}");
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

        private static long NewSeed() => System.Random.Shared.NextInt64(0, 1_000_000_000_000_000L);

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(CanAnalyze));
            OnPropertyChanged(nameof(CanHunt));
            OnPropertyChanged(nameof(CanRunBatch));
            OnPropertyChanged(nameof(CanRerollPair));
            OnPropertyChanged(nameof(CanFinish));
            OnPropertyChanged(nameof(ShowSingleReroll));
            OnPropertyChanged(nameof(FinishButtonText));
            AnalyzeCommand.NotifyCanExecuteChanged();
            HuntCommand.NotifyCanExecuteChanged();
            RunBatchCommand.NotifyCanExecuteChanged();
            RerollPairCommand.NotifyCanExecuteChanged();
            MovePairUpCommand.NotifyCanExecuteChanged();
            MovePairDownCommand.NotifyCanExecuteChanged();
            ClearFolderCommand.NotifyCanExecuteChanged();
            FinishCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
        }
    }
}
