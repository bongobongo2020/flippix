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
    /// Ideogram v4 tab: a high-level scene prompt plus an optional set of
    /// bounding-box composition regions drawn on a canvas, rendered to a base
    /// image and optionally upscaled to a 4K image via the PiD path. Aspect
    /// ratio (square / widescreen / portrait) drives both the base and 4K sizes.
    /// Workflow: workflow/image/Ideogram4workflowAPI.json.
    /// </summary>
    public class IdeogramViewModel : INotifyPropertyChanged
    {
        private const string WorkflowFile = "workflow/image/Ideogram4workflowAPI.json";
        private const string PromptFile = "prompts/prompt2json/ideagram.md";
        private const string BasePrefix = "ideao";
        private const string Prefix4K = "ideao_4k";

        // PiD 4K-only nodes pruned when the user disables the 4K upscale.
        private static readonly string[] PiD4KNodeIds =
            { "74", "75", "76", "77", "78", "79", "81", "82", "84", "85", "86", "87", "88", "100" };

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
        private bool _generate4K = true;
        private readonly ObservableCollection<IdeogramRegion> _regions = new();

        // Enriched style fields produced by the autoprompter analysis and fed
        // straight into Ideogram4PromptBuilderKJ (node 105). They have no editor
        // UI yet; they are populated on Analyze and reused at generate time.
        private string _background = string.Empty;
        private string _style = "photo";
        private string _stylePhoto = string.Empty;
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
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
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
            LoadQueueFromFile();
            LoadGeneratedImagesFromFolder();
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

        // Base generation size (node 105) for the current aspect.
        private (int W, int H) BaseSize => _selectedAspectRatio switch
        {
            "Widescreen" => (1024, 576),
            "Portrait" => (576, 1024),
            _ => (1024, 1024),
        };

        // Final 4K canvas size (node 84) = 4× base for the current aspect.
        private (int W, int H) Size4K
        {
            get { var (w, h) = BaseSize; return (w * 4, h * 4); }
        }

        public string AspectSummary
        {
            get
            {
                var (bw, bh) = BaseSize;
                var (kw, kh) = Size4K;
                return Generate4K ? $"{bw}×{bh} → {kw}×{kh}" : $"{bw}×{bh}";
            }
        }

        public bool Generate4K
        {
            get => _generate4K;
            set { _generate4K = value; OnPropertyChanged(); OnPropertyChanged(nameof(AspectSummary)); }
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
            !string.IsNullOrWhiteSpace(_stylePhoto);

        /// <summary>Compact read-only summary of the enriched style for the UI.</summary>
        public string StyleSummary
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(_medium)) parts.Add(_medium);
                if (!string.IsNullOrWhiteSpace(_aesthetics)) parts.Add(_aesthetics);
                if (!string.IsNullOrWhiteSpace(_lighting)) parts.Add(_lighting);
                if (!string.IsNullOrWhiteSpace(_stylePhoto)) parts.Add(_stylePhoto);
                if (!string.IsNullOrWhiteSpace(_background)) parts.Add($"bg: {_background}");
                return string.Join("  •  ", parts);
            }
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

            double oldW = CanvasWidth, oldH = CanvasHeight;
            SelectedAspectRatio = aspect;
            double newW = CanvasWidth, newH = CanvasHeight;

            // Preserve each region's relative position/size across the resized canvas.
            if (oldW > 0 && oldH > 0)
            {
                double sx = newW / oldW, sy = newH / oldH;
                foreach (var r in _regions)
                {
                    r.X *= sx; r.Width *= sx;
                    r.Y *= sy; r.Height *= sy;
                }
            }
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

        /// <summary>
        /// Replaces the region set with one box per analyzed element. Each element's
        /// normalized rect (0..1) is scaled to the current editor canvas. Falls back
        /// to a single full-frame region carrying <paramref name="fallbackDesc"/>
        /// when no elements were parsed.
        /// </summary>
        private void PopulateRegionsFromElements(IReadOnlyList<ParsedElement> elements, string fallbackDesc)
        {
            _regions.Clear();

            if (elements == null || elements.Count == 0)
            {
                PopulateRegionOne(fallbackDesc);
                return;
            }

            double cw = CanvasWidth, ch = CanvasHeight;
            foreach (var el in elements)
            {
                double x = Clamp01(el.X) * cw;
                double y = Clamp01(el.Y) * ch;
                double w = Clamp01(el.W) * cw;
                double h = Clamp01(el.H) * ch;
                // Keep boxes inside the canvas and above the 24px drag minimum.
                if (x + w > cw) w = cw - x;
                if (y + h > ch) h = ch - y;
                w = Math.Max(24, w);
                h = Math.Max(24, h);

                _regions.Add(new IdeogramRegion
                {
                    X = x,
                    Y = y,
                    Width = w,
                    Height = h,
                    Description = string.IsNullOrWhiteSpace(el.Desc) ? fallbackDesc : el.Desc,
                    Palette = el.Palette ?? new List<string>(),
                });
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

        public void SetInputImage(string path)
        {
            if (!File.Exists(path)) return;
            InputImagePath = path;
            try
            {
                InputImageSource = LoadBitmap(path);
                HasInputImage = true;
                AddLog($"Reference image: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { AddLog($"ERROR loading image: {ex.Message}"); }
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
            SetInputImage(path);

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
            var analyzed = await AnalyzeAsync();
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

        // ── Analyze ───────────────────────────────────────────────────────
        private async Task<bool> AnalyzeAsync()
        {
            if (!CanAnalyze) return false;
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
                        ApplyAnalysis(parsed);
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
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progressReporter, _cts.Token);
                AddLog($"Done: {promptId}");

                Progress = 94;
                StatusMessage = "Retrieving image...";
                var retrieved = await RetrieveOutputImageAsync(promptId, Generate4K ? Prefix4K : BasePrefix, _cts.Token);
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

        // ── Workflow building ─────────────────────────────────────────────
        private JsonElement BuildWorkflow()
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse workflow JSON");

            var rng = new Random();
            var (baseW, baseH) = BaseSize;
            var elementsJson = BuildElementsJson();

            // Node 4 / 75 — fresh random seeds for the base and 4K samplers.
            UpdateNode(dict, "4", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L));
            UpdateNode(dict, "75", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L));

            // Node 105 — Ideogram4PromptBuilderKJ: prompt + enriched style + regions + base resolution.
            UpdateNode(dict, "105", inputs =>
            {
                inputs["high_level_description"] = HighLevelPrompt;
                inputs["width"] = baseW;
                inputs["height"] = baseH;
                inputs["elements_data"] = elementsJson;
                ApplyStyleInputs(inputs, UseEnrichedStyle,
                    Background, Style, StylePhoto, Aesthetics, Lighting, Medium, StylePaletteJson);
            });

            if (Generate4K)
            {
                // Node 84 — EmptyChromaRadianceLatentImage: final 4K canvas (= 4× base).
                var (w4k, h4k) = Size4K;
                UpdateNode(dict, "84", inputs =>
                {
                    inputs["width"] = w4k;
                    inputs["height"] = h4k;
                });
                AddLog($"Base {baseW}×{baseH} → 4K {w4k}×{h4k}");
            }
            else
            {
                // No upscale: drop the PiD path so ComfyUI only renders the base image.
                foreach (var id in PiD4KNodeIds)
                    dict.Remove(id);
                AddLog($"Base only: {baseW}×{baseH}");
            }

            return JsonSerializer.SerializeToElement(dict);
        }

        /// <summary>
        /// Normalized elements_data for node 105. Each region's pixel rect is divided
        /// by the editor canvas size. With no regions, a single full-frame region
        /// carries the high-level prompt (matching the workflow's default).
        /// </summary>
        private string BuildElementsJson()
        {
            var elements = new List<Dictionary<string, object>>();

            if (_regions.Any())
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
                    elements.Add(new Dictionary<string, object>
                    {
                        ["x"] = x,
                        ["y"] = y,
                        ["w"] = w,
                        ["h"] = h,
                        ["type"] = "obj",
                        ["text"] = "",
                        ["desc"] = string.IsNullOrWhiteSpace(r.Description) ? HighLevelPrompt : r.Description,
                        ["palette"] = (object?)r.Palette ?? Array.Empty<object>(),
                    });
                }
            }
            else
            {
                elements.Add(new Dictionary<string, object>
                {
                    ["x"] = 0.0,
                    ["y"] = 0.0,
                    ["w"] = 1.0,
                    ["h"] = 1.0,
                    ["type"] = "obj",
                    ["text"] = "",
                    ["desc"] = HighLevelPrompt,
                    ["palette"] = Array.Empty<object>(),
                });
            }

            return JsonSerializer.Serialize(elements);
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>
        /// Writes the enriched autoprompter fields onto an Ideogram4PromptBuilderKJ
        /// (node 105) inputs map. The node carries a dotted "style.photo" key. When
        /// <paramref name="useEnriched"/> is false the detected style detail is
        /// dropped (only the base "photo" style bucket is kept) so the image renders
        /// from the high-level prompt + regions alone.
        /// </summary>
        private static void ApplyStyleInputs(
            Dictionary<string, object> inputs,
            bool useEnriched,
            string background, string style, string stylePhoto,
            string aesthetics, string lighting, string medium, string stylePaletteJson)
        {
            inputs["style"] = string.IsNullOrWhiteSpace(style) ? "photo" : style;

            if (!useEnriched)
            {
                inputs["background"] = string.Empty;
                inputs["style.photo"] = string.Empty;
                inputs["aesthetics"] = string.Empty;
                inputs["lighting"] = string.Empty;
                inputs["medium"] = string.Empty;
                inputs["style_palette_data"] = string.Empty;
                return;
            }

            inputs["background"] = background ?? string.Empty;
            inputs["style.photo"] = stylePhoto ?? string.Empty;
            inputs["aesthetics"] = aesthetics ?? string.Empty;
            inputs["lighting"] = lighting ?? string.Empty;
            inputs["medium"] = medium ?? string.Empty;
            inputs["style_palette_data"] = stylePaletteJson ?? string.Empty;
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
                AspectRatio = SelectedAspectRatio,
                Generate4K = Generate4K,
                LlmModel = SelectedLlmModel,
                Background = Background,
                Style = Style,
                StylePhoto = StylePhoto,
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
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progressReporter, token);
                AddLog($"Done: {promptId}");

                Progress = 94;
                StatusMessage = "Retrieving image...";
                var retrieved = await RetrieveOutputImageAsync(promptId, queueItem.Generate4K ? Prefix4K : BasePrefix, token);
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
        /// Builds the workflow from a queued item's stored settings (regions are
        /// taken from the item's RegionsJson rather than the live canvas).
        /// </summary>
        private JsonElement BuildQueuedWorkflow(IdeogramQueueItem queueItem)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse workflow JSON");

            var rng = new Random();
            var (baseW, baseH) = AspectToBaseSize(queueItem.AspectRatio);
            var elementsJson = string.IsNullOrWhiteSpace(queueItem.RegionsJson)
                ? BuildElementsJson()
                : queueItem.RegionsJson;

            UpdateNode(dict, "4", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L));
            UpdateNode(dict, "75", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L));
            UpdateNode(dict, "105", inputs =>
            {
                inputs["high_level_description"] = queueItem.Prompt;
                inputs["width"] = baseW;
                inputs["height"] = baseH;
                inputs["elements_data"] = elementsJson;
                ApplyStyleInputs(inputs, queueItem.UseEnrichedStyle,
                    queueItem.Background, queueItem.Style, queueItem.StylePhoto,
                    queueItem.Aesthetics, queueItem.Lighting, queueItem.Medium, queueItem.StylePaletteJson);
            });

            if (queueItem.Generate4K)
            {
                UpdateNode(dict, "84", inputs =>
                {
                    inputs["width"] = baseW * 4;
                    inputs["height"] = baseH * 4;
                });
            }
            else
            {
                foreach (var id in PiD4KNodeIds)
                    dict.Remove(id);
            }

            return JsonSerializer.SerializeToElement(dict);
        }

        private static (int W, int H) AspectToBaseSize(string aspect) => aspect switch
        {
            "Widescreen" => (1024, 576),
            "Portrait" => (576, 1024),
            _ => (1024, 1024),
        };

        private void SaveQueueToFile()
        {
            try
            {
                var queueDir = Path.GetDirectoryName(QueueFilePath);
                if (!string.IsNullOrEmpty(queueDir) && !Directory.Exists(queueDir))
                    Directory.CreateDirectory(queueDir);

                var json = JsonSerializer.Serialize(_queue.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(QueueFilePath, json);
            }
            catch (Exception ex) { AddLog($"Error saving queue: {ex.Message}"); }
        }

        private void LoadQueueFromFile()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;
                var json = File.ReadAllText(QueueFilePath);
                var items = JsonSerializer.Deserialize<List<IdeogramQueueItem>>(json);
                if (items != null)
                {
                    _queue.Clear();
                    foreach (var item in items)
                    {
                        if (item.Status == "Processing")
                        {
                            item.Status = "Failed";
                            item.ErrorMessage = "Interrupted by restart";
                        }
                        _queue.Add(item);
                    }
                    NotifyQueueCommands();
                    AddLog($"Queue loaded: {_queue.Count} items");
                }
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        // ── Apply a parsed analysis to the live composition state ──────────
        private void ApplyAnalysis(IdeogramAnalysis a)
        {
            HighLevelPrompt = a.HighLevelDescription;
            SelectedAspectRatio = MapAspect(a.AspectRatio);
            Background = a.Background;
            Style = string.IsNullOrWhiteSpace(a.Style) ? "photo" : a.Style;
            StylePhoto = a.StylePhoto;
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
        /// {"ideogram_prompt", "aspect_ratio"} shape, and bbox values given either
        /// as 0..1 fractions or 0..1000 integers in [y_min, x_min, y_max, x_max].
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
                    Aesthetics = Str("aesthetics"),
                    Lighting = Str("lighting"),
                    Medium = Str("medium"),
                    StylePaletteJson = ReadPaletteJson(root, "color_palette"),
                };

                if (root.TryGetProperty("elements", out var elsEl) && elsEl.ValueKind == JsonValueKind.Array)
                {
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

                        // Documented order: [y_min, x_min, y_max, x_max].
                        double y1 = nums[0], x1 = nums[1], y2 = nums[2], x2 = nums[3];

                        // Normalize: values > 1 are on the 0..1000 grid.
                        double scale = (new[] { y1, x1, y2, x2 }.Max() > 1.0) ? 1000.0 : 1.0;
                        double nx1 = x1 / scale, ny1 = y1 / scale, nx2 = x2 / scale, ny2 = y2 / scale;

                        var el = new ParsedElement
                        {
                            X = Math.Min(nx1, nx2),
                            Y = Math.Min(ny1, ny2),
                            W = Math.Abs(nx2 - nx1),
                            H = Math.Abs(ny2 - ny1),
                            Desc = elEl.TryGetProperty("desc", out var dEl) ? dEl.GetString() ?? "" : "",
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
                    imgFile ??= files.FirstOrDefault(f =>
                        IsImageExt(f) && !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase));
                    imgFile ??= files.FirstOrDefault(IsImageExt);

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
        /// Rehydrates the gallery from previously saved images in output/ideogram so prior
        /// runs' images appear on startup. These carry no ComfyUI source path (unknown for
        /// historical files), so deleting them only removes the saved copy.
        /// </summary>
        private void LoadGeneratedImagesFromFolder()
        {
            try
            {
                var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "ideogram");
                if (!Directory.Exists(outputDir)) return;

                var files = Directory.GetFiles(outputDir, "*.png", SearchOption.TopDirectoryOnly)
                    .OrderBy(File.GetLastWriteTime)
                    .ToList();

                foreach (var file in files)
                {
                    try
                    {
                        _generatedImages.Add(new GeneratedImageItem
                        {
                            ImagePath = file,
                            Thumbnail = LoadThumbnail(file),
                        });
                    }
                    catch (Exception ex) { AddLog($"Skipped thumbnail {Path.GetFileName(file)}: {ex.Message}"); }
                }

                if (_generatedImages.Any())
                {
                    OnPropertyChanged(nameof(HasGeneratedImages));
                    ClearGeneratedImagesCommand.NotifyCanExecuteChanged();
                    AddLog($"Loaded {_generatedImages.Count} saved image(s) into gallery");
                }
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
        public List<string> Palette { get; set; } = new();
    }
}
