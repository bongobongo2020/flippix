using System;
using System.Collections.Generic;
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
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    /// <summary>
    /// Ideogram v4 tab: an uploaded reference image is analyzed by the LLM into a
    /// high-level scene prompt, an enriched style block and a set of bounding-box
    /// composition segments, which are then rendered to a single image.
    /// Workflow: workflow/image/ideogram4.json (dual-model guider, first pass at the
    /// chosen base resolution then a 2x latent upscale + refine second pass).
    ///
    /// The whole scene is handed to Ideogram4PromptBuilderKJ (node 4864) through its
    /// `import_json` input — node 4868 carries the full
    /// {high_level_description, style_description, compositional_deconstruction}
    /// document, which with import_mode "always" is the authoritative description.
    /// The node's own widgets are written in sync as a fallback.
    /// </summary>
    public class IdeogramViewModel : INotifyPropertyChanged
    {
        private const string WorkflowFile = "workflow/image/ideogram4.json";
        private const string PromptFile = "prompts/prompt2json/ideagram.md";
        private const string SavePrefix = "gram";

        // Node ids in ideogram4.json.
        private const string PromptBuilderNode = "4864";  // Ideogram4PromptBuilderKJ
        private const string ImportJsonNode = "4868";     // PrimitiveStringMultiline → 4864.import_json
        private const string LatentNode = "4839";         // EmptyFlux2LatentImage
        private const string BaseNoiseNode = "4801";      // RandomNoise (first pass)
        private const string SaveImageNode = "4830";      // SaveImage
        // KSamplerAdvanced nodes of the second (upscale/refine) pass.
        private static readonly string[] RefinePassNodes = { "4821", "4912" };

        // The workflow's second pass upscales the latent by this factor (node 4939),
        // so the saved image is this much larger than the base resolution below.
        private const double SecondPassScale = 2.0;

        // Selectable BASE resolutions in megapixels. The final image is
        // SecondPassScale larger on each edge, so 1.0 MP here ≈ 4 MP saved.
        private static readonly string[] MegapixelOptions = { "0.5", "0.75", "1.0", "1.25", "1.5" };

        private readonly ComfyUIService _comfyUIService;
        private readonly SettingsService _settingsService;
        private readonly IFileDialogService _fileDialogService;
        private readonly IAppLogger _logger;
        private readonly LMStudioService _lmStudioService;
        private readonly WorkflowQueueCoordinator _workflowCoordinator;

        // Queue fields
        private readonly ObservableCollection<IdeogramQueueItem> _queue = new();
        private bool _isProcessingQueue;
        private bool _isWaitingForLease;
        private readonly ManualResetEventSlim _pauseEvent = new(true);
        private CancellationTokenSource? _queueCts;

        // Input image (for optional LLM analysis only)
        private string _inputImagePath = string.Empty;
        private BitmapImage? _inputImageSource;
        private bool _hasInputImage;

        // LLM
        private readonly ObservableCollection<string> _availableModels = new();
        private string _selectedLlmModel = string.Empty;
        private bool _isLoadingModels;

        // Composition
        private string _highLevelPrompt = string.Empty;
        private string _selectedAspectRatio = "Square";
        private string _megapixel = "1.0";
        // On by default: the analyzed segments are what let Ideogram place each
        // subject deliberately instead of re-interpreting one flat sentence.
        private bool _useRegions = true;
        private readonly ObservableCollection<IdeogramRegion> _regions = new();

        // Enriched style fields produced by the autoprompter analysis and fed
        // straight into Ideogram4PromptBuilderKJ (node 105). They have no editor
        // UI yet; they are populated on Analyze and reused at generate time.
        private string _background = string.Empty;
        private string _style = "photo";
        private string _stylePhoto = string.Empty;
        private string _artStyle = string.Empty;
        private string _aesthetics = string.Empty;
        private string _lighting = string.Empty;
        private string _medium = string.Empty;
        private string _stylePaletteJson = string.Empty;
        private bool _useEnrichedStyle = true;

        // Workflow state
        private bool _isAnalyzing;
        private bool _isGenerating;
        private double _progress;
        private string _statusMessage = "Describe a scene, optionally add regions, then Generate";
        private string _logOutput = string.Empty;
        private CancellationTokenSource? _cts;
        // Separate from _cts so analyzing a new image never tears down the
        // cancellation source a concurrent manual Generate is still using.
        private CancellationTokenSource? _analyzeCts;
        // Tracks the in-flight AnalyzeAsync so a rapid re-trigger (e.g. fast
        // orientation changes) can cancel and await it before starting a fresh run.
        private Task? _analyzeTask;
        private DateTime _lastProgressLog = DateTime.MinValue;

        // Result
        private BitmapImage? _resultImageSource;
        private bool _hasResult;
        private string _resultImagePath = string.Empty;

        // Generated-image gallery (thumbnails of every produced image)
        private readonly ObservableCollection<GeneratedImageItem> _generatedImages = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public IdeogramViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            SettingsService settingsService,
            IFileDialogService fileDialogService,
            LMStudioService lmStudioService,
            WorkflowQueueCoordinator workflowCoordinator)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _workflowCoordinator = workflowCoordinator ?? throw new ArgumentNullException(nameof(workflowCoordinator));

            BrowseImageCommand = new RelayCommand(async () => await BrowseImageAsync());
            // Analysis hits the LLM (LM Studio), independent of ComfyUI generation,
            // so it is gated only against another analyze — not against a running
            // generate/queue. This lets the user prep & queue more images while a
            // batch is processing.
            BrowseAndAnalyzeCommand = new RelayCommand(async () => await BrowseAndAnalyzeAsync(), () => !IsAnalyzing);
            LoadModelsCommand = new RelayCommand(async () => await LoadModelsAsync(), () => !IsAnalyzing && !IsLoadingModels);
            AnalyzeCommand = new RelayCommand(async () => await RunAnalyzeAsync(), () => CanAnalyze);
            CancelAnalyzeCommand = new RelayCommand(CancelAnalyze, () => IsAnalyzing);
            GenerateCommand = new RelayCommand(async () => await GenerateAsync(), () => CanGenerate);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            OpenResultImageCommand = new RelayCommand(OpenResultImage, () => HasResult);

            OpenGeneratedImageCommand = new RelayCommand<GeneratedImageItem>(OpenGeneratedImage);
            DeleteGeneratedImageCommand = new RelayCommand<GeneratedImageItem>(DeleteGeneratedImage);
            ClearGeneratedImagesCommand = new RelayCommand(ClearGeneratedImages, () => _generatedImages.Any());

            SetAspectCommand = new RelayCommand<string>(SetAspect);
            AddRegionCommand = new RelayCommand(AddRegion);
            RemoveRegionCommand = new RelayCommand<IdeogramRegion>(RemoveRegion);
            ClearRegionsCommand = new RelayCommand(ClearRegions, () => _regions.Any());
            SelectRegionCommand = new RelayCommand<IdeogramRegion>(SelectRegion);

            AddToQueueCommand = new RelayCommand(AddToQueue, () => CanAddToQueue);
            RemoveFromQueueCommand = new RelayCommand<IdeogramQueueItem>(RemoveFromQueue);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => _queue.Any());
            ProcessQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => CanProcessQueue);
            CancelQueueCommand = new RelayCommand(CancelQueue, () => IsProcessingQueue);

            _ = LoadModelsAsync();
            ScheduleQueueLoad();
            ScheduleGalleryLoad();
        }

        // ── Input image ──────────────────────────────────────────────────
        public string InputImagePath
        {
            get => _inputImagePath;
            set { _inputImagePath = value; OnPropertyChanged(); }
        }

        public BitmapImage? InputImageSource
        {
            get => _inputImageSource;
            set { _inputImageSource = value; OnPropertyChanged(); }
        }

        public bool HasInputImage
        {
            get => _hasInputImage;
            set
            {
                _hasInputImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoInputImage));
                OnPropertyChanged(nameof(CanAnalyze));
                AnalyzeCommand.NotifyCanExecuteChanged();
            }
        }

        public bool NoInputImage => !_hasInputImage;

        // ── LLM Model ────────────────────────────────────────────────────
        public ObservableCollection<string> AvailableModels => _availableModels;

        public string SelectedLlmModel
        {
            get => _selectedLlmModel;
            set
            {
                _selectedLlmModel = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanAnalyze));
                AnalyzeCommand.NotifyCanExecuteChanged();
            }
        }

        public bool IsLoadingModels
        {
            get => _isLoadingModels;
            set { _isLoadingModels = value; OnPropertyChanged(); }
        }

        // ── Composition: prompt ──────────────────────────────────────────
        public string HighLevelPrompt
        {
            get => _highLevelPrompt;
            set
            {
                _highLevelPrompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
                OnPropertyChanged(nameof(CanAddToQueue));
                GenerateCommand.NotifyCanExecuteChanged();
                AddToQueueCommand.NotifyCanExecuteChanged();
            }
        }

        // ── Composition: aspect ratio ────────────────────────────────────
        public string SelectedAspectRatio
        {
            get => _selectedAspectRatio;
            private set
            {
                _selectedAspectRatio = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSquare));
                OnPropertyChanged(nameof(IsWidescreen));
                OnPropertyChanged(nameof(IsPortrait));
                OnPropertyChanged(nameof(CanvasWidth));
                OnPropertyChanged(nameof(CanvasHeight));
                OnPropertyChanged(nameof(AspectSummary));
            }
        }

        public bool IsSquare => _selectedAspectRatio == "Square";
        public bool IsWidescreen => _selectedAspectRatio == "Widescreen";
        public bool IsPortrait => _selectedAspectRatio == "Portrait";

        // Editor canvas display size (px) for the current aspect.
        public double CanvasWidth => _selectedAspectRatio switch
        {
            "Widescreen" => 344,
            "Portrait" => 194,
            _ => 320,
        };

        public double CanvasHeight => _selectedAspectRatio switch
        {
            "Widescreen" => 194,
            "Portrait" => 344,
            _ => 320,
        };

        // W:H ratio for the current aspect, used to compute the output resolution.
        private string AspectRatioString => AspectToRatioString(_selectedAspectRatio);

        private static string AspectToRatioString(string aspect) => aspect switch
        {
            "Widescreen" => "16:9",
            "Portrait" => "9:16",
            _ => "1:1",
        };

        public ObservableCollection<string> MegapixelChoices { get; } = new(MegapixelOptions);

        /// <summary>Base (first pass) resolution budget in megapixels; the saved image is <see cref="SecondPassScale"/>× larger per edge.</summary>
        public string Megapixel
        {
            get => _megapixel;
            set
            {
                _megapixel = string.IsNullOrWhiteSpace(value) ? "1.0" : value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AspectSummary));
            }
        }

        public string AspectSummary
        {
            get
            {
                var (w, h) = ApproxResolution(_selectedAspectRatio, _megapixel);
                var (fw, fh) = FinalResolution(w, h);
                return $"≈ {w}×{h} → {fw}×{fh}";
            }
        }

        /// <summary>Saved-image size after the workflow's 2× latent upscale second pass.</summary>
        private static (int W, int H) FinalResolution(int baseW, int baseH)
            => ((int)(baseW * SecondPassScale), (int)(baseH * SecondPassScale));

        // Base latent size: the megapixel budget at the chosen ratio, rounded to /16.
        private static (int W, int H) ApproxResolution(string aspect, string megapixel)
        {
            var ratioStr = AspectToRatioString(aspect);
            var parts = ratioStr.Split(':');
            double rw = double.TryParse(parts[0], out var a) ? a : 1;
            double rh = parts.Length > 1 && double.TryParse(parts[1], out var b) ? b : 1;
            if (rh == 0) rh = 1;
            double ratio = rw / rh;
            double mp = double.TryParse(megapixel, out var m) ? m : 1.0;
            // Clamp to the offered range: this is the BASE size and the workflow's
            // second pass squares it up (2× per edge = 4× the pixels), so a legacy
            // queue item saved at "2.5" would otherwise ask for a 10 MP render.
            mp = Math.Clamp(mp, 0.25, 1.5);
            double total = mp * 1_000_000;
            int w = (int)(Math.Round(Math.Sqrt(total * ratio) / 16) * 16);
            int h = (int)(Math.Round(Math.Sqrt(total / ratio) / 16) * 16);
            return (Math.Max(16, w), Math.Max(16, h));
        }

        // ── Enriched style (from the autoprompter analysis) ───────────────
        public string Background
        {
            get => _background;
            set { _background = value; OnPropertyChanged(); OnPropertyChanged(nameof(StyleSummary)); OnPropertyChanged(nameof(HasStyleDetails)); }
        }

        public string Style
        {
            get => _style;
            set { _style = value; OnPropertyChanged(); }
        }

        public string StylePhoto
        {
            get => _stylePhoto;
            set { _stylePhoto = value; OnPropertyChanged(); OnPropertyChanged(nameof(StyleSummary)); OnPropertyChanged(nameof(HasStyleDetails)); }
        }

        /// <summary>
        /// Free-text art-style description. This workflow's prompt builder is wired to
        /// the "art_style" bucket (node 4864 style / style.art_style), which accepts a
        /// photographic description just as well as an illustrative one.
        /// </summary>
        public string ArtStyle
        {
            get => _artStyle;
            set { _artStyle = value; OnPropertyChanged(); OnPropertyChanged(nameof(StyleSummary)); OnPropertyChanged(nameof(HasStyleDetails)); }
        }

        public string Aesthetics
        {
            get => _aesthetics;
            set { _aesthetics = value; OnPropertyChanged(); OnPropertyChanged(nameof(StyleSummary)); OnPropertyChanged(nameof(HasStyleDetails)); }
        }

        public string Lighting
        {
            get => _lighting;
            set { _lighting = value; OnPropertyChanged(); OnPropertyChanged(nameof(StyleSummary)); OnPropertyChanged(nameof(HasStyleDetails)); }
        }

        public string Medium
        {
            get => _medium;
            set { _medium = value; OnPropertyChanged(); OnPropertyChanged(nameof(StyleSummary)); OnPropertyChanged(nameof(HasStyleDetails)); }
        }

        public string StylePaletteJson
        {
            get => _stylePaletteJson;
            set { _stylePaletteJson = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// When true, the enriched style detail detected by the autoprompter
        /// (background, camera/lens, aesthetics, lighting, medium, palette) is fed
        /// into Ideogram4PromptBuilderKJ. When false, only the high-level prompt and
        /// region boxes drive the generation.
        /// </summary>
        public bool UseEnrichedStyle
        {
            get => _useEnrichedStyle;
            set { _useEnrichedStyle = value; OnPropertyChanged(); }
        }

        public bool HasStyleDetails =>
            !string.IsNullOrWhiteSpace(_background) ||
            !string.IsNullOrWhiteSpace(_aesthetics) ||
            !string.IsNullOrWhiteSpace(_lighting) ||
            !string.IsNullOrWhiteSpace(_medium) ||
            !string.IsNullOrWhiteSpace(_artStyle) ||
            !string.IsNullOrWhiteSpace(_stylePhoto);

        /// <summary>Compact read-only summary of the enriched style for the UI.</summary>
        public string StyleSummary
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(_medium)) parts.Add(_medium);
                if (!string.IsNullOrWhiteSpace(_artStyle)) parts.Add(_artStyle);
                if (!string.IsNullOrWhiteSpace(_aesthetics)) parts.Add(_aesthetics);
                if (!string.IsNullOrWhiteSpace(_lighting)) parts.Add(_lighting);
                if (!string.IsNullOrWhiteSpace(_stylePhoto)) parts.Add(_stylePhoto);
                if (!string.IsNullOrWhiteSpace(_background)) parts.Add($"bg: {_background}");
                return string.Join("  •  ", parts);
            }
        }

        /// <summary>
        /// When true (default) the analyzed composition segments are sent as discrete
        /// elements with bounding boxes, which is what lets Ideogram 4 place each
        /// subject where the reference image had it. When false a single full-frame
        /// element carrying the high-level prompt is sent instead.
        /// </summary>
        public bool UseRegions
        {
            get => _useRegions;
            set { _useRegions = value; OnPropertyChanged(); }
        }

        public ObservableCollection<IdeogramRegion> Regions => _regions;
        public bool HasRegions => _regions.Any();

        // ── Workflow state ────────────────────────────────────────────────
        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                _isAnalyzing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanAnalyze));
                OnPropertyChanged(nameof(CanGenerate));
                NotifyCommands();
            }
        }

        public bool IsGenerating
        {
            get => _isGenerating;
            set
            {
                _isGenerating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanAnalyze));
                OnPropertyChanged(nameof(CanGenerate));
                NotifyCommands();
            }
        }

        public bool IsBusy => _isAnalyzing || _isGenerating;

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

        // ── Result ────────────────────────────────────────────────────────
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

        // ── Generated-image gallery ───────────────────────────────────────
        public ObservableCollection<GeneratedImageItem> GeneratedImages => _generatedImages;
        public bool HasGeneratedImages => _generatedImages.Any();

        // ── CanExecute ────────────────────────────────────────────────────
        // Only block on a concurrent analyze; a running generate/queue must not
        // disable analysis (different backend, no shared live state — the queue
        // builds from each item's stored values).
        public bool CanAnalyze => HasInputImage && !string.IsNullOrWhiteSpace(SelectedLlmModel) && !IsAnalyzing;
        public bool CanGenerate => !string.IsNullOrWhiteSpace(HighLevelPrompt) && !IsBusy;
        public bool CanAddToQueue => !string.IsNullOrWhiteSpace(HighLevelPrompt);
        public bool CanProcessQueue => _queue.Any(q => q.Status == "Pending") && !IsProcessingQueue;

        // ── Commands ──────────────────────────────────────────────────────
        public RelayCommand BrowseImageCommand { get; }
        public RelayCommand BrowseAndAnalyzeCommand { get; }
        public RelayCommand LoadModelsCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand CancelAnalyzeCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand OpenResultImageCommand { get; }
        public RelayCommand<GeneratedImageItem> OpenGeneratedImageCommand { get; }
        public RelayCommand<GeneratedImageItem> DeleteGeneratedImageCommand { get; }
        public RelayCommand ClearGeneratedImagesCommand { get; }

        public RelayCommand<string> SetAspectCommand { get; }
        public RelayCommand AddRegionCommand { get; }
        public RelayCommand<IdeogramRegion> RemoveRegionCommand { get; }
        public RelayCommand ClearRegionsCommand { get; }
        public RelayCommand<IdeogramRegion> SelectRegionCommand { get; }

        public RelayCommand AddToQueueCommand { get; }
        public RelayCommand<IdeogramQueueItem> RemoveFromQueueCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand ProcessQueueCommand { get; }
        public RelayCommand CancelQueueCommand { get; }

        // ── Queue properties ──────────────────────────────────────────────
        public ObservableCollection<IdeogramQueueItem> Queue => _queue;
        public bool HasQueueItems => _queue.Any();
        public int QueueCount => _queue.Count;
        public int PendingQueueCount => _queue.Count(q => q.Status == "Pending");
        public int CompletedQueueCount => _queue.Count(q => q.Status == "Completed");

        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            set { _isProcessingQueue = value; OnPropertyChanged(); NotifyQueueCommands(); }
        }

        public bool IsWaitingForLease
        {
            get => _isWaitingForLease;
            set { _isWaitingForLease = value; OnPropertyChanged(); }
        }

        private void NotifyCommands()
        {
            AnalyzeCommand.NotifyCanExecuteChanged();
            CancelAnalyzeCommand.NotifyCanExecuteChanged();
            BrowseAndAnalyzeCommand.NotifyCanExecuteChanged();
            LoadModelsCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
            AddToQueueCommand.NotifyCanExecuteChanged();
        }

        private void NotifyQueueCommands()
        {
            AddToQueueCommand.NotifyCanExecuteChanged();
            ProcessQueueCommand.NotifyCanExecuteChanged();
            CancelQueueCommand.NotifyCanExecuteChanged();
            ClearQueueCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasQueueItems));
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(PendingQueueCount));
            OnPropertyChanged(nameof(CompletedQueueCount));
        }

        // ── Aspect ratio ──────────────────────────────────────────────────
        private void SetAspect(string? aspect)
        {
            if (string.IsNullOrEmpty(aspect) || aspect == _selectedAspectRatio) return;

            SelectedAspectRatio = aspect;

            // The existing region boxes were laid out for the previous orientation.
            // Rescaling them to the new canvas distorts each box (a square becomes a
            // thin rectangle), so the composition ends up looking messed up. Instead,
            // drop the stale boxes and — when an input image is loaded — regenerate
            // fresh regions tailored to the new orientation via the analyzer. The
            // re-analyze preserves the orientation the user just picked.
            ClearRegions();

            if (HasInputImage && !string.IsNullOrWhiteSpace(SelectedLlmModel))
            {
                AddLog($"Orientation → {aspect}: regenerating regions...");
                _ = RestartAnalyzeAsync(preserveAspect: true);
            }
        }

        /// <summary>
        /// Cancels any in-flight analyze and waits for it to unwind, then starts a
        /// fresh analyze. Used when the user changes orientation while a previous
        /// region-regeneration is still running, so the stale run can't land its
        /// boxes after the new one.
        /// </summary>
        private async Task RestartAnalyzeAsync(bool preserveAspect)
        {
            try { _analyzeCts?.Cancel(); } catch (ObjectDisposedException) { }

            var prev = _analyzeTask;
            if (prev != null)
            {
                try { await prev; } catch { /* cancellation / prior failure already handled in AnalyzeAsync */ }
            }

            await RunAnalyzeAsync(preserveAspect);
        }

        /// <summary>
        /// Starts an analyze run and records it as the in-flight task so a restart
        /// (orientation change) can await it. All analyze entry points go through here.
        /// </summary>
        private Task<bool> RunAnalyzeAsync(bool preserveAspect = false)
        {
            var task = AnalyzeAsync(preserveAspect);
            _analyzeTask = task;
            return task;
        }

        // ── Regions ───────────────────────────────────────────────────────
        private void AddRegion()
        {
            double w = CanvasWidth * 0.4, h = CanvasHeight * 0.4;
            var region = new IdeogramRegion
            {
                Width = w,
                Height = h,
                X = (CanvasWidth - w) / 2,
                Y = (CanvasHeight - h) / 2,
            };
            _regions.Add(region);
            ReindexRegions();
            SelectRegion(region);
            ClearRegionsCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasRegions));
        }

        private void RemoveRegion(IdeogramRegion? region)
        {
            if (region == null) return;
            _regions.Remove(region);
            ReindexRegions();
            ClearRegionsCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasRegions));
        }

        private void ClearRegions()
        {
            _regions.Clear();
            ClearRegionsCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasRegions));
        }

        private void SelectRegion(IdeogramRegion? region)
        {
            foreach (var r in _regions)
                r.IsSelected = ReferenceEquals(r, region);
        }

        /// <summary>
        /// Sets region 1's description from an analyzed prompt, creating a
        /// full-frame region 1 if none exist yet.
        /// </summary>
        private void PopulateRegionOne(string desc)
        {
            if (_regions.Count == 0)
            {
                var region = new IdeogramRegion
                {
                    X = 0,
                    Y = 0,
                    Width = CanvasWidth,
                    Height = CanvasHeight,
                    Description = desc,
                };
                _regions.Add(region);
                ReindexRegions();
                SelectRegion(region);
                ClearRegionsCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(HasRegions));
            }
            else
            {
                _regions[0].Description = desc;
            }
        }

        // Subject box edges within this fraction of a canvas edge get snapped out
        // to the edge ("expand toward edges"), so subjects reach the frame instead
        // of leaving a thin margin.
        private const double EdgeSnap = 0.10;

        // An element covering at least this fraction of the frame in BOTH axes is
        // treated as the background and dropped from the element list — the setting
        // is carried by Ideogram4PromptBuilderKJ's dedicated `background` input, and a
        // full-frame element box overlapping every subject makes the model split or
        // garble the scene (Ideogram expects tight, mostly non-overlapping elements).
        private const double FullFrameThreshold = 0.92;

        /// <summary>
        /// Maps the analyzed subject elements onto the editor canvas as composition
        /// regions. Each element becomes a tightly-bounded region (scaled to the canvas
        /// and snapped out to any nearby edge). The scene/background is NOT added as a
        /// region — it flows through the prompt builder's separate `background` input,
        /// matching Ideogram 4's compositional_deconstruction schema (background string
        /// + discrete element bboxes). Any near-full-frame element returned by the LLM
        /// is dropped for the same reason. When the analysis yields no discrete subject,
        /// a single full-frame region carrying the high-level prompt is used as a
        /// fallback so region mode still has content.
        /// </summary>
        private void PopulateRegionsFromElements(IReadOnlyList<ParsedElement> elements, string fallbackDesc)
        {
            _regions.Clear();

            double cw = CanvasWidth, ch = CanvasHeight;

            if (elements != null)
            {
                foreach (var el in elements)
                {
                    double x1 = Clamp01(el.X);
                    double y1 = Clamp01(el.Y);
                    double x2 = Clamp01(el.X + el.W);
                    double y2 = Clamp01(el.Y + el.H);

                    // Drop a background-sized element: it overlaps every subject and is
                    // already represented by the builder's `background` input.
                    if ((x2 - x1) >= FullFrameThreshold && (y2 - y1) >= FullFrameThreshold)
                        continue;

                    // Expand toward edges.
                    if (x1 <= EdgeSnap) x1 = 0;
                    if (y1 <= EdgeSnap) y1 = 0;
                    if (x2 >= 1 - EdgeSnap) x2 = 1;
                    if (y2 >= 1 - EdgeSnap) y2 = 1;

                    double x = x1 * cw;
                    double y = y1 * ch;
                    double w = Math.Max(24, (x2 - x1) * cw);
                    double h = Math.Max(24, (y2 - y1) * ch);
                    if (x + w > cw) w = cw - x;
                    if (y + h > ch) h = ch - y;

                    _regions.Add(new IdeogramRegion
                    {
                        X = x,
                        Y = y,
                        Width = w,
                        Height = h,
                        Description = string.IsNullOrWhiteSpace(el.Desc) ? fallbackDesc : el.Desc,
                        Type = string.IsNullOrWhiteSpace(el.Type) ? "obj" : el.Type,
                        Text = el.Text ?? string.Empty,
                        Palette = el.Palette ?? new List<string>(),
                    });
                }
            }

            // Fallback: no discrete subjects → one full-frame region with the global
            // prompt (equivalent to the non-region single-element path, no overlap).
            if (_regions.Count == 0)
            {
                var desc = !string.IsNullOrWhiteSpace(fallbackDesc) ? fallbackDesc : HighLevelPrompt;
                _regions.Add(new IdeogramRegion { X = 0, Y = 0, Width = cw, Height = ch, Description = desc });
            }

            ReindexRegions();
            if (_regions.Count > 0) SelectRegion(_regions[0]);
            ClearRegionsCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasRegions));
        }

        private void ReindexRegions()
        {
            for (int i = 0; i < _regions.Count; i++)
                _regions[i].Index = i + 1;
        }

        // ── Browse ────────────────────────────────────────────────────────
        private async Task BrowseImageAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Image",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "ideogram.image");
            if (!string.IsNullOrEmpty(path))
                SetInputImage(path);
        }

        /// <summary>
        /// Loads a reference image and — unless the caller drives the analysis itself
        /// (<paramref name="autoAnalyze"/> false) — immediately sends it to the LLM so
        /// the enhanced prompt, style block and composition segments are populated
        /// without a second click. Analysis is fire-and-forget; the UI shows its
        /// progress via IsAnalyzing/StatusMessage.
        /// </summary>
        public void SetInputImage(string path, bool autoAnalyze = true)
        {
            if (!File.Exists(path)) return;
            InputImagePath = path;
            try
            {
                InputImageSource = LoadBitmap(path);
                HasInputImage = true;
                AddLog($"Reference image: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { AddLog($"ERROR loading image: {ex.Message}"); return; }

            if (!autoAnalyze) return;

            if (string.IsNullOrWhiteSpace(SelectedLlmModel))
            {
                StatusMessage = "Pick an LLM model, then click Analyze";
                AddLog("No LLM model selected — image loaded but not analyzed");
                return;
            }

            AddLog("Auto-analyzing uploaded image...");
            _ = RestartAnalyzeAsync(preserveAspect: false);
        }

        /// <summary>
        /// Browse for a reference image and immediately analyze it so the
        /// high-level prompt (and region 1) are populated in one step.
        /// </summary>
        private async Task BrowseAndAnalyzeAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Image",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "ideogram.image");
            if (string.IsNullOrEmpty(path)) return;
            // This path awaits the analysis itself (so it can queue the result), so
            // suppress SetInputImage's own auto-analyze instead of running two.
            SetInputImage(path, autoAnalyze: false);

            if (string.IsNullOrWhiteSpace(SelectedLlmModel))
            {
                StatusMessage = "Pick an LLM model, then click Analyze";
                AddLog("No LLM model selected — image loaded but not analyzed");
                return;
            }

            // Analyze, then auto-queue the result so a freshly browsed image is
            // generated without a second manual click. AddToQueue feeds the live
            // processing loop (or starts it), so this works whether or not a batch
            // is already running. The analyzed values are read synchronously right
            // after AnalyzeAsync returns (same UI-thread continuation, no await in
            // between), so a concurrent queue item can't clobber them mid-capture.
            var analyzed = await RunAnalyzeAsync();
            if (analyzed && !string.IsNullOrWhiteSpace(HighLevelPrompt))
                AddToQueue();
            else if (!analyzed)
                AddLog("Analysis did not complete — image not queued");
        }

        // ── Load Models ───────────────────────────────────────────────────
        private async Task LoadModelsAsync()
        {
            try
            {
                IsLoadingModels = true;
                var models = await _lmStudioService.GetAvailableModelsAsync();
                WpfApp.Current?.Dispatcher.Invoke(() =>
                {
                    _availableModels.Clear();
                    foreach (var m in models)
                        _availableModels.Add(m.Id);
                    if (string.IsNullOrEmpty(SelectedLlmModel) && _availableModels.Any())
                        SelectedLlmModel = _availableModels[0];
                });
                AddLog($"Loaded {models.Count} LLM models");
            }
            catch (Exception ex)
            {
                AddLog($"Could not load models: {ex.Message}");
            }
            finally
            {
                IsLoadingModels = false;
            }
        }

        /// <summary>
        /// Cancels an in-flight image→LLM analyze. The LLM call is non-streaming and
        /// can hang for minutes ("Sending image to LLM..."), so this gives the user a
        /// way out without waiting on the 15-minute HTTP timeout.
        /// </summary>
        private void CancelAnalyze()
        {
            try { _analyzeCts?.Cancel(); } catch (ObjectDisposedException) { }
            StatusMessage = "Cancelling analyze...";
            AddLog("Analyze cancellation requested");
        }

        // ── Analyze ───────────────────────────────────────────────────────
        private async Task<bool> AnalyzeAsync(bool preserveAspect = false)
        {
            if (!CanAnalyze) return false;
            try { _analyzeCts?.Cancel(); } catch (ObjectDisposedException) { }
            _analyzeCts?.Dispose();
            _analyzeCts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
            bool succeeded = false;

            try
            {
                IsAnalyzing = true;
                SetAnalyzeProgress(0);
                StatusMessage = "Loading system prompt...";
                AddLog("=== Analyze ===");

                var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PromptFile);
                if (!File.Exists(promptPath))
                {
                    AddLog($"ERROR: Prompt file not found: {promptPath}");
                    StatusMessage = "Error: System prompt file not found";
                    return false;
                }
                var systemPrompt = await File.ReadAllTextAsync(promptPath, _analyzeCts.Token);

                SetAnalyzeProgress(10);
                StatusMessage = "Sending image to LLM...";
                AddLog($"Using model: {SelectedLlmModel}");

                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    SelectedLlmModel,
                    InputImagePath,
                    "Analyze this image and generate an Ideogram v4 prompt.",
                    systemPrompt,
                    cancellationToken: _analyzeCts.Token);

                SetAnalyzeProgress(60);
                StatusMessage = "Parsing LLM response...";

                var parsed = ParseIdeogramAnalysis(result);
                WpfApp.Current?.Dispatcher.Invoke(() =>
                {
                    if (parsed != null)
                    {
                        ApplyAnalysis(parsed, applyAspect: !preserveAspect);
                        AddLog($"Aspect ratio: {parsed.AspectRatio} → {SelectedAspectRatio}");
                        AddLog($"Parsed {parsed.Elements.Count} element region(s)");
                        StatusMessage = parsed.Elements.Count > 0
                            ? $"Prompt + {parsed.Elements.Count} regions ready — adjust if needed, then Generate"
                            : "Prompt ready — adjust regions if needed, then Generate";
                    }
                    else
                    {
                        HighLevelPrompt = result;
                        PopulateRegionOne(result);
                        AddLog("WARNING: Could not parse JSON, using raw response as prompt");
                        StatusMessage = "Raw prompt loaded — edit if needed, then Generate";
                    }
                });

                SetAnalyzeProgress(100);
                succeeded = true;
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Cancelled";
                AddLog("Cancelled");
                SetAnalyzeProgress(0);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                AddLog($"ERROR: {ex.GetType().Name}: {ex.Message}");
                _logger.LogError($"Ideogram analyze: {ex}");
            }
            finally
            {
                IsAnalyzing = false;
                AddLog("=== Analyze ended ===");
            }

            return succeeded;
        }

        // Analyze shares the Progress bar with generation. When a generate/queue
        // is running, leave its progress untouched so analyzing a new image
        // doesn't reset or flicker the generation progress.
        private void SetAnalyzeProgress(double value)
        {
            if (!IsGenerating) Progress = value;
        }

        // ── Generate ──────────────────────────────────────────────────────
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

                // Serialize against the queue (and other tabs) so a manual generate
                // never double-submits to ComfyUI alongside a running queue item.
                StatusMessage = "Waiting for other workflows to finish...";
                using var lease = await _workflowCoordinator.AcquireAsync("Ideogram", _cts.Token);

                StatusMessage = "Connecting to ComfyUI...";
                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(_cts.Token);
                    AddLog("Connected");
                }

                Progress = 8;
                StatusMessage = "Building workflow...";
                var workflow = BuildWorkflow();

                var progressReporter = MakeProgressReporter(null);

                StatusMessage = "Running ComfyUI...";
                string promptId;
                using (StartWaitHeartbeat(null))
                    promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progressReporter, _cts.Token);
                AddLog($"Done: {promptId}");

                Progress = 94;
                StatusMessage = "Retrieving image...";
                var retrieved = await RetrieveOutputImageAsync(promptId, SavePrefix, _cts.Token);
                if (retrieved != null)
                {
                    await SaveAndDisplayResultAsync(retrieved, _cts.Token);
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
                _logger.LogError($"Ideogram generate: {ex}");
            }
            finally
            {
                IsGenerating = false;
                AddLog("=== Generate ended ===");
            }
        }

        private Progress<FlipPix.ComfyUI.Models.ProgressMessage> MakeProgressReporter(IdeogramQueueItem? queueItem)
            => new(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                {
                    // Real per-step progress arrived over the websocket — stop the elapsed-time
                    // heartbeat from also driving the bar so they don't fight.
                    _realProgressSeen = true;
                    var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                    WpfApp.Current?.Dispatcher.Invoke(() =>
                    {
                        Progress = 18 + pct * 0.74;
                        StatusMessage = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                        if (queueItem != null) queueItem.Progress = Progress;
                    });

                    // Heartbeat to the log so a slow remote generation is visibly alive
                    // (otherwise the log is silent for minutes and looks frozen).
                    if ((DateTime.Now - _lastProgressLog).TotalSeconds >= 15)
                    {
                        _lastProgressLog = DateTime.Now;
                        AddLog($"Generating: {msg.Data.Value}/{msg.Data.Max} ({pct:F0}%)");
                    }
                }
            });

        // Set true once a real websocket progress message is seen for the current run.
        // Many remote ComfyUI servers never push step-progress (this app then relies on the
        // /history completion poll), leaving the UI on a static status for the whole ~30s+
        // generation — which reads as "stuck". The heartbeat below fills that silence.
        private volatile bool _realProgressSeen;

        /// <summary>
        /// Drives an elapsed-time status + a gentle progress crawl while a generation is in
        /// flight, so the tab visibly stays alive even when the server sends no step-progress
        /// over the websocket. As soon as a real progress message arrives (<see cref="_realProgressSeen"/>)
        /// the crawl backs off and lets the accurate reporter own the bar. Dispose to stop.
        /// </summary>
        private IDisposable StartWaitHeartbeat(IdeogramQueueItem? queueItem)
        {
            _realProgressSeen = false;
            var start = DateTime.Now;
            // ~2%/s reaches the 90% ceiling around 37s, matching a typical Ideogram gen; it
            // holds at 90% until real completion bumps it to 94/100, so it never claims "done".
            var timer = new System.Threading.Timer(_ =>
            {
                if (_realProgressSeen) return;
                var elapsed = (DateTime.Now - start).TotalSeconds;
                WpfApp.Current?.Dispatcher.Invoke(() =>
                {
                    if (_realProgressSeen || !IsGenerating) return;
                    Progress = Math.Min(90, 15 + elapsed * 2.0);
                    StatusMessage = $"Generating on ComfyUI… {elapsed:F0}s";
                    if (queueItem != null) queueItem.Progress = Progress;
                });
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            return timer;
        }

        // ── Workflow building ─────────────────────────────────────────────
        /// <summary>
        /// Everything one generation needs, snapshotted so a queued item renders from
        /// its own values while the live editor moves on to the next image.
        /// </summary>
        private sealed class IdeogramComposition
        {
            public string Aspect { get; init; } = "Square";
            public string Megapixel { get; init; } = "1.0";
            public string HighLevelPrompt { get; init; } = string.Empty;
            /// <summary>Elements array for node 4864's `elements_data` widget.</summary>
            public string ElementsJson { get; init; } = "[]";
            /// <summary>Full scene document for node 4868 → node 4864's `import_json`.</summary>
            public string ImportJson { get; init; } = string.Empty;
            public bool UseEnrichedStyle { get; init; } = true;
            public string Background { get; init; } = string.Empty;
            public string StylePhoto { get; init; } = string.Empty;
            public string ArtStyle { get; init; } = string.Empty;
            public string Aesthetics { get; init; } = string.Empty;
            public string Lighting { get; init; } = string.Empty;
            public string Medium { get; init; } = string.Empty;
            public string StylePaletteJson { get; init; } = string.Empty;
        }

        private JsonElement BuildWorkflow()
        {
            var composition = new IdeogramComposition
            {
                Aspect = _selectedAspectRatio,
                Megapixel = _megapixel,
                HighLevelPrompt = HighLevelPrompt,
                ElementsJson = BuildElementsJson(),
                ImportJson = BuildImportJson(),
                UseEnrichedStyle = UseEnrichedStyle,
                Background = Background,
                StylePhoto = StylePhoto,
                ArtStyle = ArtStyle,
                Aesthetics = Aesthetics,
                Lighting = Lighting,
                Medium = Medium,
                StylePaletteJson = StylePaletteJson,
            };

            var dict = LoadWorkflowDict();
            ApplyToWorkflow(dict, composition);

            var (w, h) = ApproxResolution(_selectedAspectRatio, _megapixel);
            var (fw, fh) = FinalResolution(w, h);
            AddLog($"Resolution {w}×{h} → {fw}×{fh} after the 2× second pass ({AspectRatioString})");
            AddLog($"Segments: {(UseRegions ? _regions.Count : 1)}");

            return JsonSerializer.SerializeToElement(dict);
        }

        private static Dictionary<string, JsonElement> LoadWorkflowDict()
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow not found: {workflowPath}");

            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse workflow JSON");
        }

        /// <summary>
        /// Applies one composition to a parsed ideogram4 graph: fresh seeds on both
        /// passes (4801 / 4821 / 4912), base latent size (4839), the save prefix the
        /// retrieval loop looks for (4830), the full scene document (4868) and the
        /// prompt builder's own widgets (4864).
        /// </summary>
        private static void ApplyToWorkflow(Dictionary<string, JsonElement> dict, IdeogramComposition c)
        {
            var rng = new Random();
            var (w, h) = ApproxResolution(c.Aspect, c.Megapixel);

            UpdateNode(dict, BaseNoiseNode, inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L));
            foreach (var nodeId in RefinePassNodes)
                UpdateNode(dict, nodeId, inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L));

            UpdateNode(dict, LatentNode, inputs =>
            {
                inputs["width"] = w;
                inputs["height"] = h;
            });

            // RetrieveOutputImageAsync matches saved files by this prefix.
            UpdateNode(dict, SaveImageNode, inputs => inputs["filename_prefix"] = SavePrefix);

            // Node 4868 (PrimitiveStringMultiline) feeds node 4864's `import_json`.
            // With import_mode "always" this document, not the widgets, describes the
            // scene — it is the same shape the workflow ships with by hand.
            UpdateNode(dict, ImportJsonNode, inputs => inputs["value"] = c.ImportJson);

            UpdateNode(dict, PromptBuilderNode, inputs =>
            {
                inputs["high_level_description"] = c.HighLevelPrompt;
                inputs["width"] = w;
                inputs["height"] = h;
                inputs["elements_data"] = c.ElementsJson;
                // Import only when we actually produced a document, otherwise fall back
                // to the widgets rather than re-importing whatever was there before.
                inputs["import_mode"] = string.IsNullOrWhiteSpace(c.ImportJson) ? "when empty" : "always";
                // The bbox convention BuildElements emits: [y_min, x_min, y_max, x_max]
                // on a 0..1000 grid. These two inputs are what make the node read it
                // that way, so they are pinned here rather than trusted from the file.
                inputs["coord_mode"] = "normalized";
                inputs["bbox_order"] = "yx";
                ApplyStyleInputs(inputs, c);
            });
        }

        /// <summary>0..1000 normalized, y-first bbox for one canvas-space region.</summary>
        private static int[] BboxYx(double x, double y, double w, double h)
        {
            int Norm(double v) => (int)Math.Round(Clamp01(v) * 1000);
            return new[] { Norm(y), Norm(x), Norm(y + h), Norm(x + w) };
        }

        /// <summary>
        /// The composition segments as Ideogram element objects. With UseRegions on
        /// these are the analyzed/drawn boxes; with it off, a single full-frame element
        /// carrying the high-level prompt.
        /// </summary>
        private List<Dictionary<string, object>> BuildElements()
        {
            var elements = new List<Dictionary<string, object>>();

            if (UseRegions && _regions.Any())
            {
                double cw = CanvasWidth, ch = CanvasHeight;
                foreach (var r in _regions)
                {
                    double x = Clamp01(r.X / cw);
                    double y = Clamp01(r.Y / ch);
                    double w = Clamp01(r.Width / cw);
                    double h = Clamp01(r.Height / ch);
                    if (x + w > 1) w = 1 - x;
                    if (y + h > 1) h = 1 - y;

                    var element = new Dictionary<string, object>
                    {
                        ["type"] = string.IsNullOrWhiteSpace(r.Type) ? "obj" : r.Type,
                        ["bbox"] = BboxYx(x, y, w, h),
                        ["desc"] = string.IsNullOrWhiteSpace(r.Description) ? HighLevelPrompt : r.Description,
                        ["color_palette"] = (object?)r.Palette ?? new List<string>(),
                    };
                    // `text` is only meaningful for typography elements; sending an empty
                    // one on every object invites the model to render stray lettering.
                    if (!string.IsNullOrWhiteSpace(r.Text))
                        element["text"] = r.Text;
                    elements.Add(element);
                }
            }
            else
            {
                elements.Add(new Dictionary<string, object>
                {
                    ["type"] = "obj",
                    ["bbox"] = BboxYx(0, 0, 1, 1),
                    ["desc"] = HighLevelPrompt,
                    ["color_palette"] = new List<string>(),
                });
            }

            return elements;
        }

        private string BuildElementsJson() => Serialize(BuildElements());

        /// <summary>
        /// The full scene document fed to node 4864's `import_json` — the same
        /// {high_level_description, style_description, compositional_deconstruction}
        /// shape the workflow ships with, so the app-driven run is byte-for-byte the
        /// kind of input the graph was authored around.
        /// </summary>
        private string BuildImportJson()
        {
            var doc = new Dictionary<string, object>
            {
                ["high_level_description"] = HighLevelPrompt ?? string.Empty,
                ["style_description"] = new Dictionary<string, object>
                {
                    ["aesthetics"] = UseEnrichedStyle ? Aesthetics ?? string.Empty : string.Empty,
                    ["lighting"] = UseEnrichedStyle ? Lighting ?? string.Empty : string.Empty,
                    ["medium"] = UseEnrichedStyle ? Medium ?? string.Empty : string.Empty,
                    ["art_style"] = UseEnrichedStyle ? EffectiveArtStyle() : string.Empty,
                    ["color_palette"] = UseEnrichedStyle ? ParsePalette(StylePaletteJson) : new List<string>(),
                },
                ["compositional_deconstruction"] = new Dictionary<string, object>
                {
                    ["background"] = UseEnrichedStyle ? Background ?? string.Empty : string.Empty,
                    ["elements"] = BuildElements(),
                },
            };

            return Serialize(doc);
        }

        /// <summary>
        /// Art-style text for the builder's "art_style" bucket. Falls back to the
        /// photographic/lens detail and then the medium so the bucket is never empty
        /// when the analysis produced any style at all.
        /// </summary>
        private string EffectiveArtStyle()
        {
            if (!string.IsNullOrWhiteSpace(ArtStyle)) return ArtStyle;
            if (!string.IsNullOrWhiteSpace(StylePhoto)) return StylePhoto;
            return Medium ?? string.Empty;
        }

        private static List<string> ParsePalette(string paletteJson)
        {
            if (string.IsNullOrWhiteSpace(paletteJson)) return new List<string>();
            try { return JsonSerializer.Deserialize<List<string>>(paletteJson) ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        // Relaxed escaping keeps the document readable in the ComfyUI node (and leaves
        // accented characters and quotes in prompts intact rather than \uXXXX soup).
        private static readonly JsonSerializerOptions JsonOut = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOut);

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>
        /// Writes the enriched autoprompter fields onto the Ideogram4PromptBuilderKJ
        /// (node 4864) inputs map. The node exposes one dotted sub-field per style
        /// bucket; this workflow is wired to "art_style", so that is the bucket kept in
        /// sync (an out-of-list `style` value is silently dropped by ComfyUI's input
        /// gather and then fails the node with "missing required argument: 'style'").
        /// When <paramref name="c"/> has UseEnrichedStyle off, the detected detail is
        /// cleared and only the high-level prompt + segments drive the image.
        /// </summary>
        private static void ApplyStyleInputs(Dictionary<string, object> inputs, IdeogramComposition c)
        {
            inputs["style"] = "art_style";

            if (!c.UseEnrichedStyle)
            {
                inputs["background"] = string.Empty;
                inputs["style.art_style"] = string.Empty;
                inputs["aesthetics"] = string.Empty;
                inputs["lighting"] = string.Empty;
                inputs["medium"] = string.Empty;
                inputs["style_palette_data"] = string.Empty;
                return;
            }

            var artStyle = !string.IsNullOrWhiteSpace(c.ArtStyle) ? c.ArtStyle
                         : !string.IsNullOrWhiteSpace(c.StylePhoto) ? c.StylePhoto
                         : c.Medium ?? string.Empty;

            inputs["background"] = c.Background ?? string.Empty;
            inputs["style.art_style"] = artStyle;
            inputs["aesthetics"] = c.Aesthetics ?? string.Empty;
            inputs["lighting"] = c.Lighting ?? string.Empty;
            inputs["medium"] = c.Medium ?? string.Empty;
            inputs["style_palette_data"] = c.StylePaletteJson ?? string.Empty;
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

        // ── Aspect helpers ────────────────────────────────────────────────
        private static string MapAspect(string aspectRatio)
        {
            var r = (aspectRatio ?? "1:1").Trim();
            var m = System.Text.RegularExpressions.Regex.Match(r, @"^(\d+)\s*:\s*(\d+)$");
            if (!m.Success) return "Square";
            if (!double.TryParse(m.Groups[1].Value, out var w) || !double.TryParse(m.Groups[2].Value, out var h) || h == 0)
                return "Square";
            var ratio = w / h;
            if (ratio > 1.15) return "Widescreen";
            if (ratio < 0.87) return "Portrait";
            return "Square";
        }

        // ── Queue Management ─────────────────────────────────────────────
        private string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "ideogram_queue.json");

        private void AddToQueue()
        {
            if (!CanAddToQueue) return;

            var queueItem = new IdeogramQueueItem
            {
                InputImagePath = InputImagePath,
                Prompt = HighLevelPrompt,
                RegionsJson = BuildElementsJson(),
                ImportJson = BuildImportJson(),
                AspectRatio = SelectedAspectRatio,
                Megapixel = Megapixel,
                LlmModel = SelectedLlmModel,
                Background = Background,
                Style = Style,
                StylePhoto = StylePhoto,
                ArtStyle = ArtStyle,
                Aesthetics = Aesthetics,
                Lighting = Lighting,
                Medium = Medium,
                StylePaletteJson = StylePaletteJson,
                UseEnrichedStyle = UseEnrichedStyle
            };
            _queue.Add(queueItem);
            NotifyQueueCommands();
            AddLog($"Added to queue: {queueItem.DisplayPrompt}");

            if (!IsProcessingQueue && _queue.Any(q => q.Status == "Pending"))
                _ = ProcessQueueAsync();
        }

        private void RemoveFromQueue(IdeogramQueueItem? item)
        {
            if (item == null) return;
            _queue.Remove(item);
            NotifyQueueCommands();
        }

        private void ClearQueue()
        {
            _queue.Clear();
            NotifyQueueCommands();
        }

        private void CancelQueue()
        {
            _queueCts?.Cancel();
            _cts?.Cancel();
            _pauseEvent.Set();
            AddLog("Queue cancellation requested");
        }

        private async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;
            if (!_queue.Any(q => q.Status == "Pending")) return;

            IsProcessingQueue = true;

            try
            {
                _queueCts?.Dispose();
                _queueCts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
                IsWaitingForLease = true;
                AddLog("Starting queue processing...");
                AddLog("Waiting for other workflows to finish...");

                WorkflowQueueCoordinator.WorkflowLease lease;
                try
                {
                    lease = await _workflowCoordinator.AcquireAsync("Ideogram", _queueCts.Token);
                }
                catch (OperationCanceledException)
                {
                    AddLog("Queue cancelled while waiting for lease");
                    return;
                }
                catch (Exception ex)
                {
                    AddLog($"Error acquiring lease: {ex.Message}");
                    return;
                }

                AddLog("=== Ideogram queue started ===");
                IsWaitingForLease = false;

                using (lease)
                try
                {
                    IdeogramQueueItem? queueItem;
                    while ((queueItem = _queue.FirstOrDefault(q => q.Status == "Pending")) != null)
                    {
                        if (_queueCts?.Token.IsCancellationRequested == true)
                        {
                            AddLog("Queue cancelled");
                            break;
                        }

                        _pauseEvent.Wait(_queueCts?.Token ?? CancellationToken.None);

                        try
                        {
                            queueItem.Status = "Processing";
                            queueItem.StartedAt = DateTime.Now;
                            queueItem.Progress = 0;
                            SaveQueueToFile();
                            OnPropertyChanged(nameof(PendingQueueCount));

                            AddLog($"Processing: {queueItem.DisplayPrompt}");
                            await ProcessQueueItemAsync(queueItem);

                            queueItem.Status = "Completed";
                            queueItem.CompletedAt = DateTime.Now;
                            queueItem.Progress = 100;
                            SaveQueueToFile();
                            AddLog($"Completed: {queueItem.DisplayPrompt}");
                        }
                        catch (OperationCanceledException)
                        {
                            queueItem.Status = "Failed";
                            queueItem.ErrorMessage = "Cancelled";
                            SaveQueueToFile();
                            AddLog($"Cancelled: {queueItem.DisplayPrompt}");
                            break;
                        }
                        catch (Exception ex)
                        {
                            queueItem.Status = "Failed";
                            queueItem.ErrorMessage = ex.Message;
                            SaveQueueToFile();
                            AddLog($"ERROR: {ex.Message}");
                        }

                        OnPropertyChanged(nameof(PendingQueueCount));
                        OnPropertyChanged(nameof(CompletedQueueCount));
                    }
                }
                finally
                {
                    IsProcessingQueue = false;
                    IsWaitingForLease = false;
                    NotifyQueueCommands();
                }
            }
            catch (Exception ex)
            {
                IsProcessingQueue = false;
                IsWaitingForLease = false;
                AddLog($"Queue error: {ex.Message}");
                NotifyQueueCommands();
            }

            AddLog("=== Ideogram queue ended ===");
        }

        private async Task ProcessQueueItemAsync(IdeogramQueueItem queueItem)
        {
            // Scope the token to this item so we never dispose a CancellationTokenSource
            // that a concurrent manual Generate/Analyze still owns via _cts. Cancellation
            // still flows in through _queueCts (CancelQueue cancels it).
            using var itemCts = _queueCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken, _queueCts.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
            var token = itemCts.Token;

            try
            {
                IsGenerating = true;

                // Deliberately DO NOT push the queue item's settings into the live
                // composition state. The editor (prompt / regions / aspect / style)
                // must stay pinned to the most recently uploaded+analyzed image so
                // the user can keep pressing "Add to Queue" to enqueue more variations
                // of THAT image while older items are still generating. The workflow
                // for this item is built entirely from its own snapshot via
                // BuildQueuedWorkflow(queueItem), so nothing here needs live state.

                Progress = 0;
                StatusMessage = "Connecting to ComfyUI...";

                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(token);
                    AddLog("Connected");
                }

                Progress = 8;
                StatusMessage = "Building workflow...";
                var workflow = BuildQueuedWorkflow(queueItem);

                var progressReporter = MakeProgressReporter(queueItem);

                StatusMessage = "Running ComfyUI...";
                string promptId;
                using (StartWaitHeartbeat(queueItem))
                    promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progressReporter, token);
                AddLog($"Done: {promptId}");

                Progress = 94;
                StatusMessage = "Retrieving image...";
                var retrieved = await RetrieveOutputImageAsync(promptId, SavePrefix, token);
                if (retrieved != null)
                {
                    var path = await WriteOutputAsync(retrieved.Bytes, token);
                    ResultImagePath = path;
                    WpfApp.Current?.Dispatcher.Invoke(() => LoadResultImage(path));
                    HasResult = true;
                    queueItem.OutputImagePath = path;
                    AddGeneratedImage(path, retrieved.ComfyUISourcePath);
                    AddLog($"Saved: {path}");
                    Progress = 100;
                    StatusMessage = $"Done! {Path.GetFileName(path)}";
                }
                else
                {
                    StatusMessage = "No result — check ComfyUI logs";
                    AddLog("WARNING: No output image retrieved");
                }
            }
            finally
            {
                IsGenerating = false;
            }
        }

        /// <summary>
        /// Builds the workflow from a queued item's stored snapshot (segments come from
        /// the item's own JSON rather than the live canvas, which by now may belong to a
        /// different image).
        /// </summary>
        private JsonElement BuildQueuedWorkflow(IdeogramQueueItem queueItem)
        {
            var dict = LoadWorkflowDict();

            ApplyToWorkflow(dict, new IdeogramComposition
            {
                Aspect = queueItem.AspectRatio,
                Megapixel = queueItem.Megapixel,
                HighLevelPrompt = queueItem.Prompt,
                ElementsJson = string.IsNullOrWhiteSpace(queueItem.RegionsJson)
                    ? BuildElementsJson()
                    : queueItem.RegionsJson,
                // Items queued before this workflow switch carry no import document;
                // they fall back to the widget path (import_mode "when empty").
                ImportJson = queueItem.ImportJson,
                UseEnrichedStyle = queueItem.UseEnrichedStyle,
                Background = queueItem.Background,
                StylePhoto = queueItem.StylePhoto,
                ArtStyle = queueItem.ArtStyle,
                Aesthetics = queueItem.Aesthetics,
                Lighting = queueItem.Lighting,
                Medium = queueItem.Medium,
                StylePaletteJson = queueItem.StylePaletteJson,
            });

            return JsonSerializer.SerializeToElement(dict);
        }

        private void SaveQueueToFile()
        {
            try
            {
                var queueDir = Path.GetDirectoryName(QueueFilePath);
                if (!string.IsNullOrEmpty(queueDir) && !Directory.Exists(queueDir))
                    Directory.CreateDirectory(queueDir);

                // Don't persist completed items — they're session history, not pending work.
                // Keeps the queue file small so it never bloats or slows startup.
                var json = JsonSerializer.Serialize(_queue.Where(q => q.Status != "Completed").ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(QueueFilePath, json);
            }
            catch (Exception ex) { AddLog($"Error saving queue: {ex.Message}"); }
        }

        /// <summary>
        /// Queues the persisted queue load at Background dispatcher priority so a large saved queue
        /// never blocks app startup; the file read + deserialize run off the UI thread.
        /// </summary>
        private void ScheduleQueueLoad()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                _ = LoadQueueFromFileAsync();
                return;
            }

            dispatcher.InvokeAsync(
                async () => await LoadQueueFromFileAsync(),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private async System.Threading.Tasks.Task LoadQueueFromFileAsync()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;
                var items = await System.Threading.Tasks.Task.Run(() =>
                {
                    var json = File.ReadAllText(QueueFilePath);
                    return JsonSerializer.Deserialize<List<IdeogramQueueItem>>(json);
                });
                if (items != null)
                {
                    _queue.Clear();
                    bool prunedCompleted = false;
                    foreach (var item in items)
                    {
                        // Drop completed items so finished history never accumulates in the queue.
                        if (item.Status == "Completed") { prunedCompleted = true; continue; }
                        if (item.Status == "Processing")
                        {
                            item.Status = "Failed";
                            item.ErrorMessage = "Interrupted by restart";
                        }
                        _queue.Add(item);
                    }
                    NotifyQueueCommands();
                    AddLog($"Queue loaded: {_queue.Count} items");
                    // Rewrite the (now smaller) file once so previously bloated queues shrink immediately.
                    if (prunedCompleted) SaveQueueToFile();
                }
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        // ── Apply a parsed analysis to the live composition state ──────────
        private void ApplyAnalysis(IdeogramAnalysis a, bool applyAspect = true)
        {
            HighLevelPrompt = a.HighLevelDescription;
            // When re-analyzing after a manual orientation change, keep the aspect
            // the user just selected instead of overriding it with the LLM's guess.
            if (applyAspect)
                SelectedAspectRatio = MapAspect(a.AspectRatio);
            Background = a.Background;
            Style = string.IsNullOrWhiteSpace(a.Style) ? "photo" : a.Style;
            StylePhoto = a.StylePhoto;
            ArtStyle = a.ArtStyle;
            Aesthetics = a.Aesthetics;
            Lighting = a.Lighting;
            Medium = a.Medium;
            StylePaletteJson = a.StylePaletteJson;
            PopulateRegionsFromElements(a.Elements, a.HighLevelDescription);
        }

        // ── Parse LLM response ────────────────────────────────────────────
        /// <summary>
        /// Parses the autoprompter JSON (high-level description + style fields +
        /// elements with bounding boxes). Tolerates Markdown fences, the legacy
        /// {"ideogram_prompt", "aspect_ratio"} shape, and bbox values given as 0..1
        /// fractions, 0..1000 normalized, or absolute pixels.
        ///
        /// Order is <c>[x_min, y_min, x_max, y_max]</c> — the native convention of the
        /// configured vision model (Qwen2.5-VL), which emits boxes in that order in
        /// absolute pixels of the image it was sent regardless of the prompt's stated
        /// convention. Reading them y-first and assuming a 0..1000 grid (the old code)
        /// transposed every box and divided pixel values by 1000, collapsing the whole
        /// composition into the top-left corner — the cause of the garbled layouts.
        /// </summary>
        private IdeogramAnalysis? ParseIdeogramAnalysis(string response)
        {
            try
            {
                var json = response.Trim();
                if (json.StartsWith("```"))
                {
                    var firstNewline = json.IndexOf('\n');
                    if (firstNewline >= 0)
                        json = json.Substring(firstNewline + 1);
                    if (json.EndsWith("```"))
                        json = json.Substring(0, json.Length - 3);
                    json = json.Trim();
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string Str(string name) =>
                    root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                        ? el.GetString() ?? "" : "";

                // High-level description: new key first, then legacy "ideogram_prompt".
                var prompt = Str("high_level_description");
                if (string.IsNullOrWhiteSpace(prompt)) prompt = Str("ideogram_prompt");
                if (string.IsNullOrWhiteSpace(prompt)) return null;

                var result = new IdeogramAnalysis
                {
                    HighLevelDescription = prompt,
                    AspectRatio = string.IsNullOrWhiteSpace(Str("aspect_ratio")) ? "1:1" : Str("aspect_ratio"),
                    Background = Str("background"),
                    Style = Str("style"),
                    // accept both "style_photo" and a literal "style.photo" key
                    StylePhoto = !string.IsNullOrWhiteSpace(Str("style_photo")) ? Str("style_photo") : Str("style.photo"),
                    ArtStyle = !string.IsNullOrWhiteSpace(Str("art_style")) ? Str("art_style") : Str("style.art_style"),
                    Aesthetics = Str("aesthetics"),
                    Lighting = Str("lighting"),
                    Medium = Str("medium"),
                    StylePaletteJson = ReadPaletteJson(root, "color_palette"),
                };

                if (root.TryGetProperty("elements", out var elsEl) && elsEl.ValueKind == JsonValueKind.Array)
                {
                    // Dimensions of the image actually sent to the VLM (512px longest edge,
                    // aspect preserved). Qwen2.5-VL returns boxes in pixels of this image.
                    var (rw, rh) = VisionResizedDims(InputImagePath);

                    foreach (var elEl in elsEl.EnumerateArray())
                    {
                        if (elEl.ValueKind != JsonValueKind.Object) continue;
                        if (!elEl.TryGetProperty("bbox", out var bboxEl) || bboxEl.ValueKind != JsonValueKind.Array)
                            continue;

                        var nums = bboxEl.EnumerateArray()
                            .Where(n => n.ValueKind == JsonValueKind.Number)
                            .Select(n => n.GetDouble())
                            .ToArray();
                        if (nums.Length < 4) continue;

                        // Qwen2.5-VL native order: [x_min, y_min, x_max, y_max].
                        double x1 = nums[0], y1 = nums[1], x2 = nums[2], y2 = nums[3];

                        // Detect the coordinate scale from the magnitude of the values:
                        //  • <= 1.0           → already 0..1 fractions
                        //  • <= resized dims  → absolute pixels (Qwen's native output)
                        //  • otherwise        → 0..1000 normalized grid
                        double maxv = new[] { x1, y1, x2, y2 }.Max();
                        double sx, sy;
                        if (maxv <= 1.0)            { sx = 1.0;  sy = 1.0;  }
                        else if (maxv <= Math.Max(rw, rh) * 1.10) { sx = rw; sy = rh; }
                        else                        { sx = 1000.0; sy = 1000.0; }

                        double nx1 = Clamp01(x1 / sx), ny1 = Clamp01(y1 / sy);
                        double nx2 = Clamp01(x2 / sx), ny2 = Clamp01(y2 / sy);

                        var type = elEl.TryGetProperty("type", out var tEl) ? tEl.GetString() ?? "obj" : "obj";
                        var text = elEl.TryGetProperty("text", out var txEl) ? txEl.GetString() ?? "" : "";

                        var el = new ParsedElement
                        {
                            X = Math.Min(nx1, nx2),
                            Y = Math.Min(ny1, ny2),
                            W = Math.Abs(nx2 - nx1),
                            H = Math.Abs(ny2 - ny1),
                            Desc = elEl.TryGetProperty("desc", out var dEl) ? dEl.GetString() ?? "" : "",
                            // A "text" element with no literal string is just an object.
                            Type = string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
                                   && !string.IsNullOrWhiteSpace(text) ? "text" : "obj",
                            Text = text,
                            Palette = ReadPaletteList(elEl, "color_palette"),
                        };
                        if (el.W <= 0 || el.H <= 0) continue;
                        result.Elements.Add(el);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                AddLog($"JSON parse error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Dimensions of the downscaled image the VLM actually receives. Mirrors
        /// LMStudioService.ResizeImageForVision: longest edge capped at 512px, aspect
        /// preserved. Used to normalize Qwen2.5-VL's pixel-space bounding boxes.
        /// </summary>
        private static (double W, double H) VisionResizedDims(string imagePath)
        {
            const double max = 512.0;
            try
            {
                if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return (max, max);
                using var img = System.Drawing.Image.FromFile(imagePath);
                double ow = img.Width, oh = img.Height;
                if (ow <= 0 || oh <= 0) return (max, max);
                return ow > oh
                    ? (max, Math.Max(1.0, max * oh / ow))
                    : (Math.Max(1.0, max * ow / oh), max);
            }
            catch { return (max, max); }
        }

        private static List<string> ReadPaletteList(JsonElement parent, string name)
        {
            var list = new List<string>();
            if (parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in el.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
                        list.Add(c.GetString()!);
            }
            return list;
        }

        private static string ReadPaletteJson(JsonElement parent, string name)
        {
            var list = ReadPaletteList(parent, name);
            return list.Count > 0 ? JsonSerializer.Serialize(list) : string.Empty;
        }

        // ── Output image retrieval ────────────────────────────────────────
        /// <summary>Retrieved image bytes plus the original ComfyUI file path when it lives on the local filesystem.</summary>
        private sealed record RetrievedImage(byte[] Bytes, string? ComfyUISourcePath);

        private async Task<RetrievedImage?> RetrieveOutputImageAsync(string promptId, string savePrefix, CancellationToken token)
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
                    if (i == 0)
                        foreach (var f in files)
                            AddLog($"  file: {f}");

                    var imgFile = files.FirstOrDefault(f =>
                        Path.GetFileName(f).StartsWith(savePrefix, StringComparison.OrdinalIgnoreCase) && IsImageExt(f));
                    // Only fall back to non-prefixed images that aren't ComfyUI temp/preview
                    // outputs (e.g. SigmasPreview_temp_*.png). Those aren't downloadable as
                    // type=output, so picking one sends the retry loop spinning to no purpose.
                    imgFile ??= files.FirstOrDefault(f =>
                        IsImageExt(f) && !IsTempPreview(f));

                    if (imgFile != null)
                    {
                        AddLog($"Downloading: {imgFile}");
                        var data = await _comfyUIService.HttpClient.DownloadOutputImageAsync(imgFile);
                        // Remote ComfyUI: no local filesystem path, so the source can't be deleted from disk.
                        if (data != null) { AddLog($"Downloaded {data.Length} bytes"); return new RetrievedImage(data, null); }
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

                var files = Directory.GetFiles(outputDir, $"{savePrefix}*.png", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime).ToList();
                if (!files.Any())
                    files = Directory.GetFiles(outputDir, "*.png", SearchOption.AllDirectories)
                        .Where(f => !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(File.GetLastWriteTime).ToList();

                if (files.Any())
                {
                    var latest = files[0];
                    var age = DateTime.Now - File.GetLastWriteTime(latest);
                    AddLog($"Found: {Path.GetFileName(latest)} ({age.TotalSeconds:F0}s old)");
                    if (age.TotalSeconds < 180)
                        return new RetrievedImage(await File.ReadAllBytesAsync(latest, token), latest);
                }
            }
            return null;
        }

        // ── Helpers ───────────────────────────────────────────────────────
        private async Task SaveAndDisplayResultAsync(RetrievedImage retrieved, CancellationToken token)
        {
            var path = await WriteOutputAsync(retrieved.Bytes, token);
            ResultImagePath = path;
            WpfApp.Current?.Dispatcher.Invoke(() => LoadResultImage(path));
            HasResult = true;
            AddGeneratedImage(path, retrieved.ComfyUISourcePath);
            AddLog($"Saved: {path}");
        }

        private async Task<string> WriteOutputAsync(byte[] bytes, CancellationToken token)
        {
            var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "ideogram");
            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, $"ideogram4_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            await File.WriteAllBytesAsync(path, bytes, token);
            return path;
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

        /// <summary>Loads an image decoded down to gallery-thumbnail size to keep memory low.</summary>
        private static BitmapImage LoadThumbnail(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 192;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private static bool IsImageExt(string f) =>
            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

        // ComfyUI temp/preview outputs (e.g. ComfyUI_temp_*, SigmasPreview_temp_*) are
        // written to the temp folder and aren't retrievable via /view?type=output, so
        // they must never be treated as the final result image.
        private static bool IsTempPreview(string f)
        {
            var name = Path.GetFileName(f);
            return name.StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase)
                || name.Contains("_temp_", StringComparison.OrdinalIgnoreCase);
        }

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

        // ── Generated-image gallery ───────────────────────────────────────
        /// <summary>
        /// Loads a freshly saved image as a thumbnail and appends it to the gallery (UI thread).
        /// <paramref name="comfyUISourcePath"/> is the original local ComfyUI file (deleted along
        /// with the saved copy by the gallery's delete), or null when it isn't on the local disk.
        /// </summary>
        private void AddGeneratedImage(string path, string? comfyUISourcePath = null)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            WpfApp.Current?.Dispatcher.Invoke(() =>
            {
                try
                {
                    _generatedImages.Add(new GeneratedImageItem
                    {
                        ImagePath = path,
                        ComfyUISourcePath = comfyUISourcePath,
                        Thumbnail = LoadThumbnail(path),
                    });
                    OnPropertyChanged(nameof(HasGeneratedImages));
                    ClearGeneratedImagesCommand.NotifyCanExecuteChanged();
                }
                catch (Exception ex) { AddLog($"ERROR adding thumbnail: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Queues the gallery rehydration at Background dispatcher priority so decoding a large
        /// history of saved thumbnails never blocks app startup; the scan + thumbnail decode run
        /// off the UI thread and the bound collection is filled once the window has painted.
        /// </summary>
        private void ScheduleGalleryLoad()
        {
            var dispatcher = WpfApp.Current?.Dispatcher;
            if (dispatcher == null)
            {
                _ = LoadGeneratedImagesFromFolderAsync();
                return;
            }

            dispatcher.InvokeAsync(
                async () => await LoadGeneratedImagesFromFolderAsync(),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Rehydrates the gallery from previously saved images in output/ideogram so prior
        /// runs' images appear on startup. These carry no ComfyUI source path (unknown for
        /// historical files), so deleting them only removes the saved copy. The directory scan
        /// and (frozen) thumbnail decode happen on a background thread; only the collection
        /// mutation is marshalled back to the UI thread.
        /// </summary>
        private async System.Threading.Tasks.Task LoadGeneratedImagesFromFolderAsync()
        {
            try
            {
                var items = await System.Threading.Tasks.Task.Run(() =>
                {
                    var result = new List<GeneratedImageItem>();
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "ideogram");
                    if (!Directory.Exists(outputDir)) return result;

                    var files = Directory.GetFiles(outputDir, "*.png", SearchOption.TopDirectoryOnly)
                        .OrderBy(File.GetLastWriteTime)
                        .ToList();

                    foreach (var file in files)
                    {
                        try
                        {
                            result.Add(new GeneratedImageItem
                            {
                                ImagePath = file,
                                Thumbnail = LoadThumbnail(file), // frozen → safe to build off the UI thread
                            });
                        }
                        catch (Exception ex) { AddLog($"Skipped thumbnail {Path.GetFileName(file)}: {ex.Message}"); }
                    }
                    return result;
                });

                if (items.Count == 0) return;

                foreach (var item in items)
                    _generatedImages.Add(item);

                OnPropertyChanged(nameof(HasGeneratedImages));
                ClearGeneratedImagesCommand.NotifyCanExecuteChanged();
                AddLog($"Loaded {_generatedImages.Count} saved image(s) into gallery");
            }
            catch (Exception ex) { AddLog($"Error loading gallery: {ex.Message}"); }
        }

        /// <summary>Opens a gallery image with the registered Windows image viewer.</summary>
        private void OpenGeneratedImage(GeneratedImageItem? item)
        {
            if (item == null || string.IsNullOrEmpty(item.ImagePath)) return;
            if (File.Exists(item.ImagePath))
                Process.Start(new ProcessStartInfo(item.ImagePath) { UseShellExecute = true });
            else
            {
                AddLog($"Image no longer on disk: {item.FileName}");
                _generatedImages.Remove(item);
                OnPropertyChanged(nameof(HasGeneratedImages));
                ClearGeneratedImagesCommand.NotifyCanExecuteChanged();
            }
        }

        /// <summary>
        /// Deletes the saved image file (and the original ComfyUI-side file when it's on the
        /// local disk) and drops the thumbnail from the gallery.
        /// </summary>
        private void DeleteGeneratedImage(GeneratedImageItem? item)
        {
            if (item == null) return;
            TryDeleteFile(item.ImagePath, item.FileName);

            // Also remove the original from the ComfyUI output folder when we have its path
            // (local generations only) and it isn't the very same file we already deleted.
            if (!string.IsNullOrEmpty(item.ComfyUISourcePath) &&
                !string.Equals(item.ComfyUISourcePath, item.ImagePath, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(item.ComfyUISourcePath, Path.GetFileName(item.ComfyUISourcePath));
            }

            _generatedImages.Remove(item);
            OnPropertyChanged(nameof(HasGeneratedImages));
            ClearGeneratedImagesCommand.NotifyCanExecuteChanged();
        }

        private void TryDeleteFile(string path, string label)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                    AddLog($"Deleted: {label}");
                }
            }
            catch (Exception ex) { AddLog($"ERROR deleting {label}: {ex.Message}"); }
        }

        /// <summary>Removes all thumbnails from view without deleting any files.</summary>
        private void ClearGeneratedImages()
        {
            _generatedImages.Clear();
            OnPropertyChanged(nameof(HasGeneratedImages));
            ClearGeneratedImagesCommand.NotifyCanExecuteChanged();
        }

        private void AddLog(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            WpfApp.Current?.Dispatcher.Invoke(() => LogOutput = LogOutput + line + "\n");
            _logger.LogInfo(message);
        }
    }

    /// <summary>Result of parsing the autoprompter JSON returned by the llama-server.</summary>
    internal sealed class IdeogramAnalysis
    {
        public string HighLevelDescription { get; set; } = string.Empty;
        public string AspectRatio { get; set; } = "1:1";
        public string Background { get; set; } = string.Empty;
        public string Style { get; set; } = "photo";
        public string StylePhoto { get; set; } = string.Empty;
        /// <summary>Free-text art style fed to the builder's "art_style" bucket.</summary>
        public string ArtStyle { get; set; } = string.Empty;
        public string Aesthetics { get; set; } = string.Empty;
        public string Lighting { get; set; } = string.Empty;
        public string Medium { get; set; } = string.Empty;
        /// <summary>Overall palette serialized as a JSON array of hex strings (or empty).</summary>
        public string StylePaletteJson { get; set; } = string.Empty;
        public List<ParsedElement> Elements { get; } = new();
    }

    /// <summary>One analyzed element with a normalized (0..1) rect, description and palette.</summary>
    internal sealed class ParsedElement
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public string Desc { get; set; } = string.Empty;
        /// <summary>"obj" (subject) or "text" (rendered typography).</summary>
        public string Type { get; set; } = "obj";
        /// <summary>Literal string to render, for "text" elements.</summary>
        public string Text { get; set; } = string.Empty;
        public List<string> Palette { get; set; } = new();
    }
}
