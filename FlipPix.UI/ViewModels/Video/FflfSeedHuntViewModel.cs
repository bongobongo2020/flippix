using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    /// "FFLF Seed Hunter" page. Upload a FIRST and LAST frame → Analyze (both images → llama-server
    /// with <c>fflf-seedhunter.md</c> → a prompt describing the action that bridges them) → generate
    /// 3 fast low-res samples (reroll for fresh seeds) → pick one → Stage 2 + Stage 3 upscale for the
    /// final high-res video. Drives <c>ltx23fflf-seedhunter-api.json</c> by editing/pruning nodes,
    /// the same node-pruning two-phase strategy as <see cref="SeedHuntViewModel"/>.
    /// </summary>
    public partial class FflfSeedHuntViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/ltx/ltx23fflf-seedhunter-api.json";
        private const string OutputSubfolder = "fflf_seedhunt";
        private const string SystemPromptFile = "fflf-seedhunter.md";

        // ── Workflow node ids (locked from the generated ltx23fflf-seedhunter-api.json) ──────────
        private const string NodeImageFirst = "5052";   // LoadImage "Input Img"
        private const string NodeImageLast = "5075";     // LoadImage "End Image"
        private const string NodePrompt = "5109:5108";   // positive CLIPTextEncode (Prompt subgraph)
        private const string NodeTargetWidth = "5013:5167";   // PrimitiveInt "Width"  (Settings subgraph)
        private const string NodeTargetHeight = "5013:5168";  // PrimitiveInt "Height" (Settings subgraph)
        private const string NodeLength = "5110";        // mxSlider "Length (seconds)"
        private const string NodeInputRefStrength = "5151";   // mxSlider INPUT reference strength
        private const string NodeEndRefStrength = "5152";     // mxSlider END reference strength
        private const string NodeBatchSeed = "5038";     // easy seed "Start from Scratch" (drives all 3 samples)
        private const string NodeStage2Seed = "5040";    // easy seed "Start Finish Mode / Reroll 2nd Stage"
        private const string NodeSelect = "5174";        // mxSlider "Which Gen To Proceed with?" (UI-only under raw API)
        private const string NodeSelectSwitch = "5173";  // ImpactSwitch (API-incompatible; pruned on Finish)
        private const string NodeSepAfterSwitch = "5177"; // LTXVSeparateAVLatent fed by the switch
        private const string NodeFinalOutput = "5033";   // final VHS_VideoCombine "Final Video"

        // slot → SamplerCustomAdvanced av-latent output the ImpactSwitch would have selected.
        private static readonly Dictionary<int, string> SamplerOutputBySlot = new()
        {
            { 1, "5002:4829" },
            { 2, "5190:5185" },
            { 3, "5206:5201" },
        };

        // slot (1-based, == select value) → preview VHS_VideoCombine node id
        private static readonly Dictionary<int, string> PreviewNodeBySlot = new()
        {
            { 1, "5062" }, // sample 1 (seed = base)
            { 2, "5186" }, // sample 2 (seed = base+1)
            { 3, "5202" }, // sample 3 (seed = base+2)
        };

        private static readonly Dictionary<string, int> SlotByPreviewNode =
            PreviewNodeBySlot.ToDictionary(kv => kv.Value, kv => kv.Key);

        // ── Input state ────────────────────────────────────────────────────────
        private string _firstImagePath = string.Empty;
        private string _lastImagePath = string.Empty;
        private BitmapImage? _firstImagePreview;
        private BitmapImage? _lastImagePreview;
        private string _firstImageInfo = string.Empty;
        private string _lastImageInfo = string.Empty;
        private string _prompt = string.Empty;
        private double _lengthSeconds = 5;
        private double _inputRefStrength = 0.75;
        private double _endRefStrength = 0.75;
        private long _baseSeed = -1;
        private bool _isAnalyzing;
        private string _currentPhase = string.Empty;
        private string? _uploadedFirstName;
        private string? _uploadedLastName;
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
        // batch pair samples we've subscribed to (so we can detach on reload/clear).
        private readonly List<SeedHuntSample> _batchSampleSubs = new();

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _analyzeCts;
        private CancellationTokenSource? _runCts;

        public FflfSeedHuntViewModel(
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

            // Per-sample checkbox selection drives Finish enablement.
            foreach (var s in _samples)
                s.PropertyChanged += OnSampleSelectionChanged;

            AddLog("FFLF Seed Hunter initialized");
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
                    _uploadedFirstName = null;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasFirstImage));
                    _firstImagePreview = LoadPreview(value, out _firstImageInfo);
                    OnPropertyChanged(nameof(FirstImagePreview));
                    OnPropertyChanged(nameof(FirstImageInfo));
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
                    _uploadedLastName = null;
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

        public string Prompt
        {
            get => _prompt;
            set { if (_prompt != value) { _prompt = value; OnPropertyChanged(); OnCanExecuteChanged(); } }
        }

        public long BaseSeed
        {
            get => _baseSeed;
            set { if (_baseSeed != value) { _baseSeed = value; OnPropertyChanged(); } }
        }

        /// <summary>Video length in seconds (clamped to 1–60 when applied to the workflow).</summary>
        public double LengthSeconds
        {
            get => _lengthSeconds;
            set { if (Math.Abs(_lengthSeconds - value) > 0.0001) { _lengthSeconds = value; OnPropertyChanged(); } }
        }

        /// <summary>First-frame reference strength (best 0.6–0.9), clamped 0–1 when applied.</summary>
        public double InputRefStrength
        {
            get => _inputRefStrength;
            set { if (Math.Abs(_inputRefStrength - value) > 0.0001) { _inputRefStrength = value; OnPropertyChanged(); } }
        }

        /// <summary>Last-frame reference strength (best 0.6–0.9), clamped 0–1 when applied.</summary>
        public double EndRefStrength
        {
            get => _endRefStrength;
            set { if (Math.Abs(_endRefStrength - value) > 0.0001) { _endRefStrength = value; OnPropertyChanged(); } }
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

        /// <summary>Single shared player source — the selected sample, or the final video once finished.</summary>
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
        /// previews (each pair keeps its own tick state, so switching pairs preserves selection).
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
                    // Auto-load this pair's first ready preview into the shared player.
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

        // ListBox selection (which sample tile is currently being previewed). Setting it loads the
        // sample into the shared player — same mechanism the FflfDasiwa segment list uses.
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
        public bool CanHunt => !IsBatchMode && HasFirstImage && HasLastImage && !string.IsNullOrWhiteSpace(Prompt)
                               && !IsProcessing && !IsAnalyzing;
        public bool CanRunBatch => IsBatchMode && _pairs.Count > 0 && !IsProcessing && !IsAnalyzing;
        public bool CanRerollPair => IsBatchMode && SelectedPair != null
                                     && !string.IsNullOrWhiteSpace(SelectedPair.Prompt)
                                     && !IsProcessing && !IsAnalyzing;
        public bool CanFinish => !IsProcessing && !IsAnalyzing && HasSelection;

        public string HuntButtonText => HasSamples ? "🎲 Reroll — new 3 seeds" : "🎯 Generate 3 Samples";
        /// <summary>Single-mode reroll button is shown only once there are samples to replace.</summary>
        public bool ShowSingleReroll => IsSingleMode && HasSamples;
        public string FinishButtonText => SelectedCount > 1
            ? $"✅ Finish {SelectedCount} Selected → Final Videos"
            : "✅ Finish Selected → Final Video";

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
                persistKey: isFirst ? "fflfseedhunt.first" : "fflfseedhunt.last");

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
                var selectedModel = await ResolveLlmModelAsync(token);
                var cleaned = await AnalyzePairAsync(selectedModel, FirstImagePath, LastImagePath, token);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    Prompt = cleaned;
                    AddLog($"Prompt generated ({cleaned.Length} chars)");
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
        /// Sends a first/last frame pair to the LLM (<c>fflf-seedhunter.md</c> system prompt) and
        /// returns the cleaned transition prompt. Shared by the manual Analyze button and the batch runner.
        /// </summary>
        private async Task<string> AnalyzePairAsync(string model, string firstPath, string lastPath, CancellationToken token)
        {
            AddLog($"Analyzing first + last frame with model: {model}");

            var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "prompts", "prompt2json", SystemPromptFile);
            if (!File.Exists(promptFilePath))
                throw new FileNotFoundException($"System prompt not found: {promptFilePath}");

            var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

            var result = await _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                model,
                new[] { firstPath, lastPath },
                "Image 1 is the FIRST frame. Image 2 is the LAST frame. Write the FFLF video prompt that bridges them.",
                systemPrompt,
                maxTokens: 4000,
                cancellationToken: token);

            return CleanOutput(result);
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

        private static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = text.Replace("**", "").Trim();
            var lower = text.ToLowerInvariant();
            if (lower.StartsWith("prompt:") || lower.StartsWith("prompt :"))
                text = text.Substring(text.IndexOf(':') + 1).Trim();
            // Strip a single pair of wrapping quotes if the model added them.
            if (text.Length > 1 && text[0] == '"' && text[^1] == '"')
                text = text[1..^1].Trim();
            return text;
        }

        #endregion

        #region Selection

        private void PreviewSample(SeedHuntSample? sample)
        {
            if (sample == null || !sample.HasVideo) return;
            // Drive the tile highlight (SelectedSampleForPreview) and the shared player. The setter
            // loads the player on first click; set ActivePreviewUri explicitly too so re-clicking the
            // same (already-selected) tile still (re)loads it.
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

            // Reroll always gets a fresh seed so ComfyUI re-samples instead of returning the cached
            // batch. First-time gen honors a user-pinned seed.
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

                var (firstName, lastName) = await EnsureImagesUploadedAsync();
                reportPhase("Generating 3 samples — previews appear as each finishes...");
                var found = await HuntCoreAsync(token, firstName, lastName, Prompt, FirstImagePath,
                    batchSeed, batchId, _samples, 0, 95, reportPhase);
                if (found == 0)
                    throw new Exception("No sample previews were produced.");
                ProcessingStatus = $"{found}/3 samples ready — pick one, then Finish";
            });
        }

        /// <summary>
        /// Runs one Stage-1 batch (3 fast previews) for a first/last pair and fills <paramref name="samples"/>.
        /// Used by both the single-pair hunt and the folder batch. Returns the number of previews produced.
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

            var (tw, th) = ComputeTargetResolution(firstImagePath);
            AddLog($"Output: {tw}×{th} ({(tw == th ? "square" : tw > th ? "widescreen" : "portrait")}), " +
                   $"{Math.Clamp(LengthSeconds <= 0 ? 5 : LengthSeconds, 1, 60):0.#}s, " +
                   $"ref in/out {Math.Clamp(InputRefStrength, 0, 1):0.##}/{Math.Clamp(EndRefStrength, 0, 1):0.##}");

            var json = await LoadWorkflowJsonAsync(token);
            ApplyCommonInputs(ref json, firstName, lastName, prompt, firstImagePath);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeBatchSeed, "seed", batchSeed);

            // Make the 3 previews retrievable: save to output/fflf_seedhunt with per-slot prefixes.
            foreach (var (slot, nodeId) in PreviewNodeBySlot)
            {
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, nodeId, new Dictionary<string, object>
                {
                    { "save_output", true },
                    { "filename_prefix", $"{OutputSubfolder}/fsh{batchId}_p{slot}" },
                });
            }

            // Prune the final output so Stage 2/3 don't run (nothing downstream of the samplers
            // is left with an output consumer) → only the 3 fast samples render.
            json = RemoveNodes(json, NodeFinalOutput);

            var filled = new HashSet<int>();
            var downloads = new List<Task>();
            void OnNode(object? s, NodeExecutedEventArgs e) => HandleHuntNode(e, samples, filled, downloads, token);
            _comfyUIService.NodeExecuted += OnNode;
            try
            {
                var promptId = await SubmitAsync(json, progressFrom, progressTo, token);

                Task[] pending;
                lock (downloads) pending = downloads.ToArray();
                try { await Task.WhenAll(pending); } catch { /* per-task errors handled inside */ }
                await FillMissingSamplesAsync(promptId, samples, filled, batchId, token);
            }
            finally
            {
                _comfyUIService.NodeExecuted -= OnNode;
            }

            var found = samples.Count(x => x.HasVideo);
            OnPropertyChanged(nameof(HasSamples));
            return found;
        }

        private void HandleHuntNode(NodeExecutedEventArgs e, ObservableCollection<SeedHuntSample> samples,
            HashSet<int> filled, List<Task> downloads, CancellationToken token)
        {
            if (!SlotByPreviewNode.TryGetValue(e.NodeId, out var slot)) return;
            var file = e.Files.FirstOrDefault(f => f.Filename.IndexOf("-audio", StringComparison.OrdinalIgnoreCase) >= 0)
                       ?? e.Files.FirstOrDefault();
            if (file == null) return;

            lock (filled) { if (!filled.Add(slot)) return; }

            var task = Task.Run(async () =>
            {
                try
                {
                    var local = await DownloadRefToTempAsync(file, token);
                    if (local != null) SetSampleVideo(samples, slot, local);
                    else { lock (filled) { filled.Remove(slot); } }
                }
                catch { lock (filled) { filled.Remove(slot); } }
            }, token);
            lock (downloads) downloads.Add(task);
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

        private BitmapImage? ExtractFirstFrame(string videoPath)
        {
            try
            {
                var ffmpeg = FindFFmpeg();
                if (ffmpeg == null) return null;
                var outPath = Path.Combine(Path.GetTempPath(), $"fflfsh_thumb_{Guid.NewGuid():N}.png");
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

        private async Task FillMissingSamplesAsync(string promptId, ObservableCollection<SeedHuntSample> samples,
            HashSet<int> filled, string batchId, CancellationToken token)
        {
            List<KeyValuePair<int, string>> missing;
            lock (filled) missing = PreviewNodeBySlot.Where(kv => !filled.Contains(kv.Key)).ToList();
            if (missing.Count == 0) return;

            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
            AddLog($"Backfill: history reported {byNode.Count} output node(s)");
            foreach (var (slot, nodeId) in missing)
            {
                string? local = null;
                if (byNode.TryGetValue(nodeId, out var outs) && outs.Count > 0)
                {
                    var pick = outs.FirstOrDefault(f => f.IndexOf("-audio", StringComparison.OrdinalIgnoreCase) >= 0)
                               ?? outs[0];
                    local = await ResolveOutputToLocalAsync(pick);
                }
                local ??= FindSlotFileOnDisk(batchId, slot);

                if (local != null)
                {
                    lock (filled) filled.Add(slot);
                    SetSampleVideo(samples, slot, local);
                }
                else
                {
                    SetSampleStatus(samples, slot, "no output");
                    AddLog($"  Sample {slot}: no output found (node {nodeId})");
                }
            }
        }

        private string? FindSlotFileOnDisk(string batchId, int slot)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;
                var baseUrl = GetComfyUIBaseUrl();
                bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                var outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;
                if (string.IsNullOrEmpty(outputFolder) || !Directory.Exists(outputFolder)) return null;

                var token = $"fsh{batchId}_p{slot}";
                var candidates = new List<string>();
                foreach (var folder in new[] { outputFolder, Path.Combine(outputFolder, OutputSubfolder) })
                {
                    if (Directory.Exists(folder))
                        candidates.AddRange(Directory.GetFiles(folder, "*.mp4")
                            .Where(f => Path.GetFileName(f).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                if (candidates.Count == 0) return null;
                return candidates
                    .OrderByDescending(f => f.IndexOf("-audio", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ThenByDescending(File.GetLastWriteTime)
                    .First();
            }
            catch (Exception ex)
            {
                AddLog($"Disk scan failed: {ex.Message}");
                return null;
            }
        }

        private void SetSampleStatus(ObservableCollection<SeedHuntSample> samples, int slot, string status) =>
            Application.Current.Dispatcher.Invoke(() => samples.First(s => s.Slot == slot).Status = status);

        private async Task<string?> DownloadRefToTempAsync(OutputFileRef r, CancellationToken token)
        {
            var settings = _settingsService.Settings;
            if (settings != null)
            {
                var baseUrl = GetComfyUIBaseUrl();
                bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                string outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;
                if (!string.IsNullOrEmpty(outputFolder) && r.Type == "output")
                {
                    var localPath = Path.Combine(outputFolder, r.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(localPath)) { await WaitForFileStableAsync(localPath); return localPath; }
                }
            }

            var bytes = await _comfyUIService.HttpClient.DownloadViewFileAsync(r.Filename, r.Subfolder, r.Type, token);
            if (bytes is { Length: > 0 })
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"fflfsh_{Guid.NewGuid():N}_{r.Filename}");
                await File.WriteAllBytesAsync(tempPath, bytes, token);
                return tempPath;
            }
            return null;
        }

        #endregion

        #region Stage 2/3 — Finish

        /// <summary>One queued seed preview to upscale at Finish (covers both single and batch flows).</summary>
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
                int done = 0;
                var finishedPaths = new List<string>(); // completed videos, in work order — joined at the end
                foreach (var item in work)
                {
                    token.ThrowIfCancellationRequested();
                    var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var label = item.PairIndex > 0 ? $"Pair {item.PairIndex} · Sample {item.Slot}" : $"Sample {item.Slot}";
                    reportPhase($"Finishing {label} ({done + 1}/{work.Count}) — Stage 2 → Stage 3...");

                    var firstName = await EnsureUploadedAsync(item.FirstPath);
                    var lastName = await EnsureUploadedAsync(item.LastPath);

                    var json = await LoadWorkflowJsonAsync(token);
                    // Identical Stage-1 inputs → ComfyUI reuses this sample's cached latent.
                    ApplyCommonInputs(ref json, firstName, lastName, item.Prompt, item.FirstPath);
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeBatchSeed, "seed", item.BatchSeed);

                    // Wire the chosen sampler's av-latent straight into the downstream Separate node.
                    // The ImpactSwitch (5173) + selector mxSlider (5174) are UI-only nodes that throw
                    // KeyError('inputs') under raw /prompt submission, so we drop them.
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeSepAfterSwitch, "av_latent",
                        new object[] { SamplerOutputBySlot[item.Slot], 0 });

                    // Fresh Stage 2/3 seed each finish.
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeStage2Seed, "seed", NewSeed());

                    var prefix = item.PairIndex > 0
                        ? $"{OutputSubfolder}/final_p{item.PairIndex}_s{item.Slot}_{ts}"
                        : $"{OutputSubfolder}/final_s{item.Slot}_{ts}";
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeFinalOutput, "filename_prefix", prefix);
                    json = RemoveNodes(json, PreviewNodeBySlot.Values
                        .Append(NodeSelect).Append(NodeSelectSwitch).ToArray());

                    var from = done * 100.0 / work.Count;
                    var to = (done + 1) * 100.0 / work.Count;
                    var existing = GetExistingVideoFiles("*.mp4", OutputSubfolder);
                    var promptId = await SubmitAsync(json, from, to, token);

                    AddLog($"Retrieving final video for {label}...");
                    string? outputVideo = null;
                    var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
                    if (byNode.TryGetValue(NodeFinalOutput, out var outs) && outs.Count > 0)
                    {
                        var pick = outs.FirstOrDefault(f => f.IndexOf("-audio", StringComparison.OrdinalIgnoreCase) >= 0)
                                   ?? outs[0];
                        outputVideo = await ResolveOutputToLocalAsync(pick);
                    }
                    outputVideo ??= await WaitForNewVideoAsync(
                        existing, "*.mp4", TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5), OutputSubfolder);

                    if (outputVideo == null || !File.Exists(outputVideo))
                    {
                        AddLog($"{label}: no final video produced — skipping");
                        continue;
                    }

                    var outputDir = Path.Combine(
                        _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "FflfSeedHunt");
                    Directory.CreateDirectory(outputDir);
                    var nameStem = item.PairIndex > 0
                        ? $"FflfSeedHunt_p{item.PairIndex}_s{item.Slot}_{ts}"
                        : $"FflfSeedHunt_s{item.Slot}_{ts}";
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
                        Info = $"{label} • {fi.Length / 1024 / 1024.0:F1}MB"
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
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "FflfSeedHunt");
                Directory.CreateDirectory(outputDir);
                var joinedPath = Path.Combine(outputDir,
                    $"FflfSeedHunt_joined_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

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

        /// <summary>Concatenates clips (all share the workflow's output resolution/fps) into one MP4 via
        /// FFmpeg's concat demuxer with a re-encode (robust to copy/codec edge-cases; keeps audio).</summary>
        private async Task ConcatClipsAsync(string ffmpeg, IReadOnlyList<string> clips, string outPath, CancellationToken token)
        {
            if (clips.Count == 1)
            {
                File.Copy(clips[0], outPath, true);
                return;
            }

            var listPath = Path.Combine(Path.GetTempPath(), $"fflfsh_concat_{Guid.NewGuid():N}.txt");
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
        /// Pick a folder of images, order them by creation time, and build overlapping
        /// first→last pairs (image i → image i+1). Enters batch mode.
        /// </summary>
        private async Task SelectFolderAsync()
        {
            if (IsProcessing || IsAnalyzing) return;

            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var folder = await _fileDialogService.OpenFolderDialogAsync(
                "Select a folder of images (ordered by creation time → overlapping FFLF pairs)",
                initialDir, showNewFolderButton: false, persistKey: "fflfseedhunt.folder");
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            LoadFolder(folder);
        }

        /// <summary>
        /// Loads a folder of images into batch mode without showing a dialog. Images are ordered by
        /// creation time (then filename) and chained into overlapping FFLF pairs. Used by the folder
        /// picker and by the "open in FFLF Seed Hunter" handoff from Story Image Q's keyframes.
        /// Must be called on the UI thread.
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
        /// Walks every pair sequentially: analyze first+last → write the transition prompt → generate
        /// 3 seed previews. A failed pair is logged and skipped; the batch continues.
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
                        var prompt = await AnalyzePairAsync(model, pair.FirstImagePath, pair.LastImagePath, token);
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

                        SetPairStatus(pair, found > 0 ? $"ready {found}/3" : "no output");
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
        /// Rerolls just the currently <see cref="SelectedPair"/> with a fresh random seed → 3 new
        /// seed previews, reusing the pair's existing transition prompt (no re-analyze). For when the
        /// user doesn't like a pair's samples but the prompt is fine. Leaves every other pair untouched.
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

                reportPhase($"Pair {pair.Index}: rerolling 3 new seeds — previews appear as each finishes...");
                var found = await HuntCoreAsync(token, firstName, lastName, pair.Prompt, pair.FirstImagePath,
                    seed, batchId, pair.Samples, 0, 95,
                    status => reportPhase($"Pair {pair.Index}: {status}"));

                SetPairStatus(pair, found > 0 ? $"ready {found}/3" : "no output");
                if (found == 0)
                    throw new Exception("No sample previews were produced.");

                Application.Current.Dispatcher.Invoke(() =>
                    ActivePreviewUri = pair.Samples.FirstOrDefault(s => s.HasVideo)?.VideoFileUri);
                ProcessingStatus = $"Pair {pair.Index}: {found}/3 fresh samples ready — pick one, then Finish";
            });
        }

        private void SetPairStatus(FflfPair pair, string status) =>
            Application.Current.Dispatcher.Invoke(() => pair.Status = status);

        /// <summary>
        /// Moves a pair one slot up (<paramref name="delta"/> = -1) or down (+1) in the batch order.
        /// Pairs are renumbered so labels follow the new order, and Finish concatenates the final
        /// clips in this same order — so reordering pairs reorders the joined output video.
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

        /// <summary>Re-assigns each pair's 1-based <see cref="FflfPair.Index"/> to its current position.</summary>
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
                AddLog($"=== FFLF Seed Hunter {phase} ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("FflfSeedHunt", token);

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
                MessageBox.Show($"{phase} failed:\n{ex.Message}", "FFLF Seed Hunter Error",
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

        private async Task<(string first, string last)> EnsureImagesUploadedAsync()
        {
            _uploadedFirstName ??= await EnsureUploadedAsync(FirstImagePath);
            _uploadedLastName ??= await EnsureUploadedAsync(LastImagePath);
            return (_uploadedFirstName!, _uploadedLastName!);
        }

        /// <summary>
        /// Uploads an image to ComfyUI once, caching the returned filename by path so overlapping
        /// batch pairs (which share a frame) don't re-upload the same file.
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

        private void ApplyCommonInputs(ref string json, string firstName, string lastName,
            string prompt, string firstImagePath)
        {
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeImageFirst, "image", firstName);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeImageLast, "image", lastName);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodePrompt, "text", prompt);

            var (tw, th) = ComputeTargetResolution(firstImagePath);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeTargetWidth, "value", tw);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeTargetHeight, "value", th);

            // Reference strengths — mxSliders run in float mode (isfloatX=1) so the Xf value is used.
            var inStr = Math.Clamp(InputRefStrength, 0, 1);
            var endStr = Math.Clamp(EndRefStrength, 0, 1);
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, NodeInputRefStrength, new Dictionary<string, object>
            {
                { "Xf", inStr }, { "isfloatX", 1 },
            });
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, NodeEndRefStrength, new Dictionary<string, object>
            {
                { "Xf", endStr }, { "isfloatX", 1 },
            });

            // Video length (seconds) → drives the Stage-1 latent frame count.
            var len = Math.Clamp(LengthSeconds <= 0 ? 5 : LengthSeconds, 1, 60);
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, NodeLength, new Dictionary<string, object>
            {
                { "Xi", len }, { "Xf", len },
            });
        }

        /// <summary>
        /// Picks the output resolution from the FIRST frame's aspect ratio:
        /// 1024×1024 (square), 1920×1024 (widescreen) or 1024×1920 (portrait).
        /// </summary>
        private (int width, int height) ComputeTargetResolution(string firstImagePath)
        {
            int iw = 0, ih = 0;
            // Reuse the already-decoded preview when it matches the single-pair image (avoids re-read).
            if (!IsBatchMode && string.Equals(firstImagePath, FirstImagePath, StringComparison.OrdinalIgnoreCase)
                && FirstImagePreview is { } preview)
            {
                iw = preview.PixelWidth; ih = preview.PixelHeight;
            }
            if ((iw <= 0 || ih <= 0) && File.Exists(firstImagePath))
            {
                try
                {
                    using var fs = File.OpenRead(firstImagePath);
                    var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    iw = frame.PixelWidth; ih = frame.PixelHeight;
                }
                catch { /* fall through to square default */ }
            }
            if (iw <= 0 || ih <= 0) return (1024, 1024);
            if (iw > ih * 1.1) return (1920, 1024); // widescreen
            if (ih > iw * 1.1) return (1024, 1920); // portrait
            return (1024, 1024);                     // square
        }

        private async Task<string> SubmitAsync(string json, double progressFrom, double progressTo, CancellationToken token)
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

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token);
            AddLog($"Workflow submitted, ID: {promptId}");
            return promptId;
        }

        private static string RemoveNodes(string json, params string[] ids)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (dict == null) return json;
            foreach (var id in ids) dict.Remove(id);
            return JsonSerializer.Serialize(dict);
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
                    var tempPath = Path.Combine(Path.GetTempPath(), $"fflfsh_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tempPath, bytes);
                    return tempPath;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Resolve preview failed: {ex.Message}");
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
            OnPropertyChanged(nameof(HuntButtonText));
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
