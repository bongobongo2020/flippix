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
    /// "Seedhunt" page. Upload an image → analyze into a combat prompt → generate a batch of
    /// 4 fast low-res samples (reroll for fresh batches) → select one → run Stage 2/3 for the
    /// final high-res video. Drives <c>seed-hunter-api.json</c> by editing/pruning nodes:
    /// Stage-1 hunt prunes the final output (5033); Finish prunes the 4 preview outputs and keeps 5033,
    /// reusing the selected sample's cached Stage-1 latent (same image/prompt/base-seed).
    /// </summary>
    public partial class SeedHuntViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/ltx/seed-hunter-api.json";
        private const string OutputSubfolder = "seedhunt";

        // ── Workflow node ids (from seed-hunter-api.json) ──────────────────────
        private const string NodeImage = "5052";          // LoadImage
        private const string NodeStage1Loras = "5078";     // Power Lora Loader (rgthree) "1st Stage LoRAs"
        private const string CharacterLoraSubfolder = "LTX-23/characters"; // under the loras root
        private const string NodePrompt = "5026:5018";     // positive CLIPTextEncode
        private const string NodeTargetWidth = "5013:5215";   // PrimitiveInt "Target Width"
        private const string NodeTargetHeight = "5013:5216";  // PrimitiveInt "Target Height"
        private const string NodeLength = "5074";             // mxSlider "Length (seconds)"
        private const string NodeBatchSeed = "5038";       // Stage-1 batch seed ("Start a new Batch of 4")
        private const string NodeStage2Seed = "5039";      // Stage-2 reroll seed
        private const string NodeStage3Seed = "5040";      // Stage-3 reroll seed
        private const string NodeSelect = "5144";          // mxSlider "Which Gen To Proceed with?"
        private const string NodeSelectSwitch = "5152";    // ImpactSwitch (UI-only; broken under raw API)
        private const string NodeSepAfterSwitch = "5140";  // LTXVSeparateAVLatent fed by the switch
        private const string NodeFinalOutput = "5033";     // final VHS_VideoCombine (save_output)
        private const string NodeStage2Preview = "5034";   // "2nd Sampler Preview" — depends on Stage 2

        // slot → the SamplerCustomAdvanced output that ImpactSwitch would have selected.
        // Lets Finish wire the chosen latent directly and drop the API-incompatible switch.
        private static readonly Dictionary<int, string> SamplerOutputBySlot = new()
        {
            { 1, "5087:4829" },
            { 2, "5134:5172" },
            { 3, "5108:5167" },
            { 4, "5100:5162" },
        };

        // slot (1-based, == select value) → preview VHS_VideoCombine node id
        private static readonly Dictionary<int, string> PreviewNodeBySlot = new()
        {
            { 1, "5086" }, // sampler 5087 (seed a)
            { 2, "5062" }, // sampler 5134 (seed a+2)
            { 3, "5109" }, // sampler 5108 (seed a+3)
            { 4, "5101" }, // sampler 5100 (seed a+4)
        };

        // preview node id → slot (inverse of PreviewNodeBySlot), for live "executed" events
        private static readonly Dictionary<string, int> SlotByPreviewNode =
            PreviewNodeBySlot.ToDictionary(kv => kv.Value, kv => kv.Key);

        // ── Input state ────────────────────────────────────────────────────────
        private string _imagePath = string.Empty;
        private BitmapImage? _imagePreview;
        private string _imageInfo = string.Empty;
        private string _prompt = string.Empty;
        private double _lengthSeconds = 5;
        private long _baseSeed = -1;
        private bool _isAnalyzing;
        private string _currentPhase = string.Empty;
        private string? _uploadedImageName;
        private string? _resultVideoFileUri;
        private string? _activePreviewUri;
        private long _currentBatchSeed = -1; // seed that produced the on-screen samples

        private readonly ObservableCollection<SeedHuntSample> _samples = new()
        {
            new SeedHuntSample(1), new SeedHuntSample(2),
            new SeedHuntSample(3), new SeedHuntSample(4),
        };

        private readonly ObservableCollection<SeedHuntResult> _results = new();

        // ── Prompt template selection (drives which system prompt is sent for analysis) ──
        private readonly ObservableCollection<SeedHuntPromptTemplate> _promptTemplates = new()
        {
            new SeedHuntPromptTemplate("⚔️ Fight (combat duel)", "ltx-seedhunt-fight.md",
                "Analyze this image and generate an LTX combat action video prompt."),
            new SeedHuntPromptTemplate("⚔️ Fight — extended", "ltx-seedhunt-fight-extended.md",
                "Analyze this image and generate an LTX combat action video prompt."),
            new SeedHuntPromptTemplate("🎬 Cinematic (LTXV2)", "ltxv2_system_prompt_addition.md",
                "Analyze this image and generate a detailed cinematic LTXV2 video prompt guided by the elements in the image."),
        };
        private SeedHuntPromptTemplate _selectedPromptTemplate;

        // ── Optional character LoRA (scanned from loras/LTX-23/characters) ──
        private readonly ObservableCollection<SeedHuntCharacterLora> _characterLoras = new() { SeedHuntCharacterLora.None };
        private SeedHuntCharacterLora _selectedCharacterLora = SeedHuntCharacterLora.None;
        private double _characterLoraStrength = 1.0;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _analyzeCts;
        private CancellationTokenSource? _runCts;

        public SeedHuntViewModel(
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
            _selectedPromptTemplate = _promptTemplates[0];

            SelectImageCommand = new RelayCommand(SelectImage);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            HuntCommand = new RelayCommand(async () => await RunHuntAsync(), () => CanHunt);
            PreviewSampleCommand = new RelayCommand<SeedHuntSample>(PreviewSample);
            FinishCommand = new RelayCommand(async () => await RunFinishAsync(), () => CanFinish);
            PlayResultCommand = new RelayCommand<SeedHuntResult>(PlayResult);
            StopCommand = new RelayCommand(() => _runCts?.Cancel(), () => IsProcessing);
            RandomSeedCommand = new RelayCommand(() => BaseSeed = NewSeed());
            ToggleMuteCommand = new RelayCommand(() => IsPreviewMuted = !IsPreviewMuted);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            RefreshLorasCommand = new RelayCommand(async () => await RefreshCharacterLorasAsync());

            _ = RefreshCharacterLorasAsync();

            // Checkbox selection lives on the sample; mirror its changes into Finish enablement.
            foreach (var s in _samples)
                s.PropertyChanged += (sender, e) =>
                {
                    if (e.PropertyName == nameof(SeedHuntSample.IsSelected))
                    {
                        OnPropertyChanged(nameof(HasSelection));
                        OnPropertyChanged(nameof(SelectedCount));
                        OnPropertyChanged(nameof(FinishButtonText));
                        OnCanExecuteChanged();
                    }
                };

            AddLog("Seedhunt initialized");
        }

        #region Commands

        public ICommand SelectImageCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand HuntCommand { get; }
        public RelayCommand<SeedHuntSample> PreviewSampleCommand { get; }
        public RelayCommand FinishCommand { get; }
        public RelayCommand<SeedHuntResult> PlayResultCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand RandomSeedCommand { get; }
        public RelayCommand ToggleMuteCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand RefreshLorasCommand { get; }

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
                    _uploadedImageName = null; // new image must be re-uploaded
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasImage));
                    LoadImagePreview();
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? ImagePreview
        {
            get => _imagePreview;
            set { _imagePreview = value; OnPropertyChanged(); }
        }

        public string ImageInfo
        {
            get => _imageInfo;
            set { if (_imageInfo != value) { _imageInfo = value; OnPropertyChanged(); } }
        }

        /// <summary>The system-prompt templates available for image analysis.</summary>
        public ObservableCollection<SeedHuntPromptTemplate> PromptTemplates => _promptTemplates;

        /// <summary>The template whose markdown is sent to llama-server as the analysis system prompt.</summary>
        public SeedHuntPromptTemplate SelectedPromptTemplate
        {
            get => _selectedPromptTemplate;
            set
            {
                if (value != null && _selectedPromptTemplate != value)
                {
                    _selectedPromptTemplate = value;
                    OnPropertyChanged();
                    AddLog($"Analysis template: {value.DisplayName}");
                }
            }
        }

        /// <summary>Discovered character LoRAs (always includes "(none)" first; optional by default).</summary>
        public ObservableCollection<SeedHuntCharacterLora> CharacterLoras => _characterLoras;

        /// <summary>The selected character LoRA, or <see cref="SeedHuntCharacterLora.None"/> for none.</summary>
        public SeedHuntCharacterLora SelectedCharacterLora
        {
            get => _selectedCharacterLora;
            set
            {
                if (value != null && _selectedCharacterLora != value)
                {
                    _selectedCharacterLora = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasCharacterLora));
                    if (value.RelativePath != null)
                        AddLog($"Character LoRA: {value.DisplayName} @ {CharacterLoraStrength:0.##}");
                }
            }
        }

        public bool HasCharacterLora => _selectedCharacterLora?.RelativePath != null;

        /// <summary>Strength applied to the selected character LoRA (clamped 0–3 when applied).</summary>
        public double CharacterLoraStrength
        {
            get => _characterLoraStrength;
            set { if (Math.Abs(_characterLoraStrength - value) > 0.0001) { _characterLoraStrength = value; OnPropertyChanged(); } }
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
                    OnCanExecuteChanged();
                }
            }
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

        public string? ResultVideoFileUri
        {
            get => _resultVideoFileUri;
            private set { if (_resultVideoFileUri != value) { _resultVideoFileUri = value; OnPropertyChanged(); } }
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

        private bool _isPreviewMuted;
        public bool IsPreviewMuted
        {
            get => _isPreviewMuted;
            set
            {
                if (_isPreviewMuted != value)
                {
                    _isPreviewMuted = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(MuteIcon));
                }
            }
        }

        public string MuteIcon => IsPreviewMuted ? "🔇 Muted" : "🔊 Audio";

        public ObservableCollection<SeedHuntSample> Samples => _samples;
        public ObservableCollection<SeedHuntResult> Results => _results;

        public bool HasImage => !string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath);
        public bool HasSamples => _samples.Any(s => s.HasVideo);

        /// <summary>Checked samples (multi-select) that have a video, ordered by slot.</summary>
        public IEnumerable<SeedHuntSample> SelectedSamples =>
            _samples.Where(s => s.IsSelected && s.HasVideo).OrderBy(s => s.Slot);
        public int SelectedCount => SelectedSamples.Count();
        public bool HasSelection => SelectedSamples.Any();

        public bool CanAnalyze => HasImage && !IsAnalyzing && !IsProcessing;
        public bool CanHunt => HasImage && !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing && !IsAnalyzing;
        public bool CanFinish => !IsProcessing && !IsAnalyzing && HasSelection;

        public string HuntButtonText => HasSamples ? "🎲 Reroll — new batch of 4" : "🎯 Generate 4 Samples";
        public string FinishButtonText => SelectedCount > 1
            ? $"✅ Finish {SelectedCount} Selected → Final Videos"
            : "✅ Finish Selected → Final Video";

        #endregion

        #region Image selection

        private async void SelectImage()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: "seedhunt.image");

            if (path != null)
            {
                ImagePath = path;
                AddLog($"Image: {Path.GetFileName(path)}");
            }
        }

        private void LoadImagePreview()
        {
            if (!HasImage)
            {
                ImagePreview = null;
                ImageInfo = string.Empty;
                return;
            }
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(ImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                ImagePreview = bitmap;
                var fi = new FileInfo(ImagePath);
                ImageInfo = $"{bitmap.PixelWidth}×{bitmap.PixelHeight} • {fi.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                ImageInfo = "Error loading image";
            }
        }

        #endregion

        #region Character LoRA discovery

        /// <summary>
        /// Repopulates the character-LoRA picker. Primary source is ComfyUI itself
        /// (/object_info → every LoRA the server sees, including remote/mounted drives the client
        /// can't reach on disk); falls back to a local filesystem scan if the server is unreachable.
        /// Keeps the current selection if it still exists; otherwise resets to "(none)".
        /// </summary>
        private async Task RefreshCharacterLorasAsync()
        {
            var previous = _selectedCharacterLora?.RelativePath;
            _characterLoras.Clear();
            _characterLoras.Add(SeedHuntCharacterLora.None);

            try
            {
                // 1) Ask ComfyUI for its LoRA list and keep the ones under LTX-23/characters.
                var prefix = CharacterLoraSubfolder + "/"; // "LTX-23/characters/"
                var reported = await _comfyUIService.HttpClient.GetLoraFilenamesAsync();
                var matches = reported
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n.Replace('\\', '/'))
                    .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (matches.Count > 0)
                {
                    foreach (var path in matches)
                        _characterLoras.Add(new SeedHuntCharacterLora(
                            Path.GetFileNameWithoutExtension(path), path));
                    AddLog($"Character LoRAs from ComfyUI: {matches.Count}");
                }
                else
                {
                    // 2) Fallback: scan a local loras folder if one is reachable on disk.
                    var (root, dir) = ResolveCharacterLoraDir();
                    if (dir != null && root != null)
                    {
                        foreach (var file in Directory.GetFiles(dir, "*.safetensors", SearchOption.AllDirectories)
                                     .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                        {
                            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                            _characterLoras.Add(new SeedHuntCharacterLora(Path.GetFileNameWithoutExtension(file), rel));
                        }
                        AddLog($"Character LoRAs from disk ({dir}): {_characterLoras.Count - 1}");
                    }
                    else
                    {
                        AddLog("No character LoRAs found (ComfyUI reported none under LTX-23/characters, and no local folder matched).");
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"Character LoRA scan failed: {ex.Message}");
            }

            SelectedCharacterLora = _characterLoras.FirstOrDefault(l => l.RelativePath == previous)
                                    ?? SeedHuntCharacterLora.None;
        }

        /// <summary>
        /// Resolves the loras root + its LTX-23/characters subfolder. ComfyUIFolderPath is often
        /// unset, so we try several candidate roots (explicit lora folder, LoraManager, and roots
        /// derived from the output folders) and return the first whose characters subfolder exists.
        /// Returns (root, charactersDir) so scanned files can be made relative to the loras root.
        /// </summary>
        private (string? root, string? dir) ResolveCharacterLoraDir()
        {
            var settings = _settingsService.Settings;
            var sub = CharacterLoraSubfolder.Replace('/', Path.DirectorySeparatorChar);
            var loraManager = _serviceProvider?.GetService(typeof(LoraManager)) as LoraManager;

            string? DerivedModelsLoras(string? outputFolder)
            {
                if (string.IsNullOrEmpty(outputFolder)) return null;
                var parent = Path.GetDirectoryName(outputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "models", "loras");
            }

            var candidates = new[]
            {
                settings?.RemoteLoraFolderPath,
                loraManager?.ResolveLoraPath(),
                DerivedModelsLoras(settings?.RemoteOutputFolderPath),
                DerivedModelsLoras(settings?.OutputFolderPath),
            };

            foreach (var root in candidates)
            {
                if (string.IsNullOrEmpty(root)) continue;
                var dir = Path.Combine(root, sub);
                AddLog($"LoRA root candidate: '{root}' → characters exists={Directory.Exists(dir)}");
                if (Directory.Exists(dir)) return (root, dir);
            }
            return (null, null);
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
                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);

                var models = await _lmStudioService.GetAvailableModelsAsync(token);
                var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(selectedModel) && models.Count > 0)
                    selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;

                if (string.IsNullOrEmpty(selectedModel))
                {
                    MessageBox.Show("No LM Studio / llama-server model available. Ensure the server is running and a model is loaded.",
                        "LLM Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var template = SelectedPromptTemplate;
                AddLog($"Analyzing image with model: {selectedModel} • template: {template.DisplayName}");

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", template.FileName);
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"System prompt not found: {promptFilePath}");

                var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel,
                    ImagePath,
                    template.UserInstruction,
                    systemPrompt,
                    maxTokens: 4000,
                    cancellationToken: token);

                var cleaned = CleanOutput(result);
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

        private static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = text.Replace("**", "").Trim();
            // Strip any leading "prompt:" label the model may add.
            var lower = text.ToLowerInvariant();
            if (lower.StartsWith("prompt:") || lower.StartsWith("prompt :"))
                text = text.Substring(text.IndexOf(':') + 1).Trim();
            return text;
        }

        #endregion

        #region Selection

        /// <summary>Clicking a sample previews it in the shared player (selection is via its checkbox).</summary>
        private void PreviewSample(SeedHuntSample? sample)
        {
            if (sample == null || !sample.HasVideo) return;
            ActivePreviewUri = sample.VideoFileUri;
        }

        private void PlayResult(SeedHuntResult? result)
        {
            if (result != null) ActivePreviewUri = result.VideoFileUri;
        }

        #endregion

        #region Stage 1 — Hunt / Reroll

        private async Task RunHuntAsync()
        {
            if (!CanHunt) return;

            // Reroll (samples already on screen) always gets a fresh seed so ComfyUI re-samples
            // instead of returning the cached batch. First-time gen honors a user-pinned seed.
            if (HasSamples || BaseSeed < 0) BaseSeed = NewSeed();
            var batchSeed = BaseSeed;
            _currentBatchSeed = batchSeed; // Finish reuses exactly this for the cache hit
            var batchId = DateTime.Now.ToString("yyyyMMddHHmmss");

            await RunWorkflowAsync("Hunt", async (token, reportPhase) =>
            {
                foreach (var s in _samples) s.Reset();
                _results.Clear();
                ActivePreviewUri = null;
                HasResult = false;
                OnPropertyChanged(nameof(HasSamples));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedCount));

                var imageName = await EnsureImageUploadedAsync();
                var (tw, th) = ComputeTargetResolution();
                AddLog($"Output: {tw}×{th} ({(tw == th ? "square" : tw > th ? "widescreen" : "portrait")}), {Math.Clamp(LengthSeconds <= 0 ? 5 : LengthSeconds, 1, 60):0.#}s");

                var json = await LoadWorkflowJsonAsync(token);
                ApplyCommonInputs(ref json, imageName);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeBatchSeed, "seed", batchSeed);

                // Make the 4 previews retrievable: save to output/seedhunt with per-slot prefixes.
                foreach (var (slot, nodeId) in PreviewNodeBySlot)
                {
                    WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, nodeId, new Dictionary<string, object>
                    {
                        { "save_output", true },
                        { "filename_prefix", $"{OutputSubfolder}/sh{batchId}_p{slot}" },
                    });
                }

                // Prune every output that depends on Stage 2/3 so only the 4 fast samples run.
                json = RemoveNodes(json, NodeFinalOutput, NodeStage2Preview);

                // Subscribe to per-node "executed" events so each preview shows the moment it finishes.
                var filled = new HashSet<int>();
                var downloads = new List<Task>();
                void OnNode(object? s, NodeExecutedEventArgs e) => HandleHuntNode(e, filled, downloads, token);
                _comfyUIService.NodeExecuted += OnNode;
                try
                {
                    reportPhase("Generating 4 samples — previews appear as each finishes...");
                    var promptId = await SubmitAsync(json, 0, 95, token);

                    // Wait for any live preview downloads still in flight, then backfill the rest.
                    Task[] pending;
                    lock (downloads) pending = downloads.ToArray();
                    try { await Task.WhenAll(pending); } catch { /* per-task errors handled inside */ }
                    await FillMissingSamplesAsync(promptId, filled, batchId, token);
                }
                finally
                {
                    _comfyUIService.NodeExecuted -= OnNode;
                }

                var found = _samples.Count(x => x.HasVideo);
                OnPropertyChanged(nameof(HasSamples));
                if (found == 0)
                    throw new Exception("No sample previews were produced.");
                ProcessingStatus = $"{found}/4 samples ready — pick one, then Finish";
            });
        }

        /// <summary>Live handler: when a preview node finishes, download + show that sample immediately.</summary>
        private void HandleHuntNode(NodeExecutedEventArgs e, HashSet<int> filled, List<Task> downloads, CancellationToken token)
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
                    if (local != null) SetSampleVideo(slot, local);
                    else { lock (filled) { filled.Remove(slot); } }
                }
                catch { lock (filled) { filled.Remove(slot); } }
            }, token);
            lock (downloads) downloads.Add(task);
        }

        private void SetSampleVideo(int slot, string localPath)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var sample = _samples.First(s => s.Slot == slot);
                sample.VideoPath = localPath;
                sample.VideoFileUri = localPath;
                sample.Status = "ready";
                OnPropertyChanged(nameof(HasSamples));
                AddLog($"  Sample {slot} ready: {Path.GetFileName(localPath)}");
                OnCanExecuteChanged();
            });

            // Grid shows a still thumbnail (4 MediaElements can't render simultaneously in WPF).
            var thumb = ExtractFirstFrame(localPath);
            if (thumb != null)
                Application.Current.Dispatcher.Invoke(() =>
                    _samples.First(s => s.Slot == slot).ThumbnailImage = thumb);
        }

        /// <summary>Extracts the first frame of a video to a BitmapImage via ffmpeg (best-effort).</summary>
        private BitmapImage? ExtractFirstFrame(string videoPath)
        {
            try
            {
                var ffmpeg = FindFFmpeg();
                if (ffmpeg == null) return null;
                var outPath = Path.Combine(Path.GetTempPath(), $"seedhunt_thumb_{Guid.NewGuid():N}.png");
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

        /// <summary>
        /// After execution, fills any slots the live events missed. Tries /history node outputs
        /// first, then a filesystem scan of the output folder (the path other tabs rely on).
        /// </summary>
        private async Task FillMissingSamplesAsync(string promptId, HashSet<int> filled, string batchId, CancellationToken token)
        {
            List<KeyValuePair<int, string>> missing;
            lock (filled) missing = PreviewNodeBySlot.Where(kv => !filled.Contains(kv.Key)).ToList();
            if (missing.Count == 0) return;

            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
            AddLog($"Backfill: history reported {byNode.Count} output node(s)");
            foreach (var (slot, nodeId) in missing)
            {
                string? local = null;

                // 1) /history → /view download
                if (byNode.TryGetValue(nodeId, out var outs) && outs.Count > 0)
                {
                    var pick = outs.FirstOrDefault(f => f.IndexOf("-audio", StringComparison.OrdinalIgnoreCase) >= 0)
                               ?? outs[0];
                    local = await ResolveOutputToLocalAsync(pick);
                }

                // 2) Filesystem scan of the output folder for this slot's prefixed file
                local ??= FindSlotFileOnDisk(batchId, slot);

                if (local != null)
                {
                    lock (filled) filled.Add(slot);
                    SetSampleVideo(slot, local);
                }
                else
                {
                    SetSampleStatus(slot, "no output");
                    AddLog($"  Sample {slot}: no output found (node {nodeId})");
                }
            }
        }

        /// <summary>Scans the configured output folder (and /seedhunt subfolder) for this slot's file.</summary>
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

                var token = $"sh{batchId}_p{slot}";
                var candidates = new List<string>();
                foreach (var folder in new[] { outputFolder, Path.Combine(outputFolder, OutputSubfolder) })
                {
                    if (Directory.Exists(folder))
                        candidates.AddRange(Directory.GetFiles(folder, "*.mp4")
                            .Where(f => Path.GetFileName(f).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                if (candidates.Count == 0) return null;
                // Prefer the audio-muxed variant, then newest.
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

        private void SetSampleStatus(int slot, string status) =>
            Application.Current.Dispatcher.Invoke(() => _samples.First(s => s.Slot == slot).Status = status);

        private async Task<string?> DownloadRefToTempAsync(OutputFileRef r, CancellationToken token)
        {
            // Prefer a directly-readable local file (mounted output folder), else download via /view.
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
                var tempPath = Path.Combine(Path.GetTempPath(), $"seedhunt_{Guid.NewGuid():N}_{r.Filename}");
                await File.WriteAllBytesAsync(tempPath, bytes, token);
                return tempPath;
            }
            return null;
        }

        #endregion

        #region Stage 2/3 — Finish

        private async Task RunFinishAsync()
        {
            var slots = SelectedSamples.Select(s => s.Slot).ToList();
            if (slots.Count == 0) return;

            await RunWorkflowAsync("Finish", async (token, reportPhase) =>
            {
                var imageName = await EnsureImageUploadedAsync();
                var batchSeed = _currentBatchSeed >= 0 ? _currentBatchSeed : BaseSeed;
                int done = 0;

                foreach (var slot in slots)
                {
                    token.ThrowIfCancellationRequested();
                    var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    reportPhase($"Finishing Sample {slot} ({done + 1}/{slots.Count}) — Stage 2 → Stage 3...");

                    var json = await LoadWorkflowJsonAsync(token);
                    // Identical Stage-1 inputs → ComfyUI reuses this sample's cached latent.
                    ApplyCommonInputs(ref json, imageName);
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeBatchSeed, "seed", batchSeed);

                    // Wire the chosen sampler's latent directly into the downstream node. The
                    // ImpactSwitch (5152) + mxSlider (5144) are UI-only nodes that throw
                    // KeyError('inputs') under raw /prompt submission, so we drop them.
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeSepAfterSwitch, "av_latent",
                        new object[] { SamplerOutputBySlot[slot], 0 });

                    // Fresh Stage 2/3 seeds each finish.
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeStage2Seed, "seed", NewSeed());
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeStage3Seed, "seed", NewSeed());

                    // Name the final output and prune the preview outputs + the dropped switch nodes.
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeFinalOutput, "filename_prefix", $"{OutputSubfolder}/final_s{slot}_{ts}");
                    json = RemoveNodes(json, PreviewNodeBySlot.Values
                        .Append(NodeStage2Preview).Append(NodeSelect).Append(NodeSelectSwitch).ToArray());

                    var from = done * 100.0 / slots.Count;
                    var to = (done + 1) * 100.0 / slots.Count;
                    var existing = GetExistingVideoFiles("*.mp4", OutputSubfolder);
                    var promptId = await SubmitAsync(json, from, to, token);

                    AddLog($"Retrieving final video for Sample {slot}...");
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
                        AddLog($"Sample {slot}: no final video produced — skipping");
                        continue;
                    }

                    var outputDir = Path.Combine(
                        _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "SeedHunt");
                    Directory.CreateDirectory(outputDir);
                    var finalPath = Path.Combine(outputDir, $"SeedHunt_s{slot}_{ts}.mp4");
                    File.Copy(outputVideo, finalPath, true);
                    await LocalCopyService.CopyVideoAsync(finalPath);

                    var fi = new FileInfo(finalPath);
                    var result = new SeedHuntResult
                    {
                        Slot = slot,
                        VideoPath = finalPath,
                        VideoFileUri = finalPath,
                        Info = $"Sample {slot} • {fi.Length / 1024 / 1024.0:F1}MB"
                    };
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _results.Add(result);
                        ResultVideoPath = finalPath;
                        ResultVideoInfo = result.Info;
                        ResultVideoFileUri = finalPath;
                        ActivePreviewUri = finalPath; // shared player shows the newest final
                        HasResult = true;
                        OnCanExecuteChanged();
                    });
                    AddLog($"=== Sample {slot} complete: {finalPath} ===");
                    done++;
                }

                ProcessingStatus = done == slots.Count
                    ? $"Finished {done} sample(s)!"
                    : $"Finished {done}/{slots.Count} sample(s)";
            });
        }

        #endregion

        #region Shared workflow runner

        /// <summary>
        /// Acquires the workflow lease, ensures ComfyUI is up, and runs <paramref name="body"/>,
        /// handling progress/cancellation/error reporting uniformly for both phases.
        /// </summary>
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
                AddLog($"=== Seedhunt {phase} ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync($"SeedHunt-{phase}", token);

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
                MessageBox.Show($"{phase} failed:\n{ex.Message}", "Seedhunt Error",
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

        private async Task<string> EnsureImageUploadedAsync()
        {
            if (!string.IsNullOrEmpty(_uploadedImageName)) return _uploadedImageName;
            AddLog("Uploading image...");
            var uploaded = await _comfyUIService.UploadImageAsync(ImagePath);
            if (string.IsNullOrEmpty(uploaded))
                throw new Exception("Failed to upload image.");
            _uploadedImageName = uploaded;
            AddLog($"Image uploaded: {uploaded}");
            return uploaded;
        }

        private static async Task<string> LoadWorkflowJsonAsync(CancellationToken token)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Workflow file not found: {path}");
            return await File.ReadAllTextAsync(path, token);
        }

        private void ApplyCommonInputs(ref string json, string imageName)
        {
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeImage, "image", imageName);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodePrompt, "text", Prompt);

            // Optional character LoRA → add a slot to the Stage-1 Power Lora Loader (node 5078).
            // No selection leaves the workflow's built-in LoRAs untouched.
            var charLora = SelectedCharacterLora;
            if (charLora?.RelativePath is { Length: > 0 } loraPath)
            {
                var strength = Math.Clamp(CharacterLoraStrength, 0, 3);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeStage1Loras, "lora_2",
                    new { on = true, lora = loraPath, strength });
                AddLog($"Applied character LoRA: {loraPath} @ {strength:0.##}");
            }

            // Match output orientation to the source image: square / widescreen / portrait.
            var (tw, th) = ComputeTargetResolution();
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeTargetWidth, "value", tw);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeTargetHeight, "value", th);

            // Video length (seconds) → drives the Stage-1 latent frame count.
            var len = Math.Clamp(LengthSeconds <= 0 ? 5 : LengthSeconds, 1, 60);
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, NodeLength, new Dictionary<string, object>
            {
                { "Xi", len },
                { "Xf", len },
            });
        }

        /// <summary>
        /// Picks the output resolution from the source image's aspect ratio:
        /// 1080×1080 (square), 1920×1080 (widescreen) or 1080×1920 (portrait).
        /// </summary>
        private (int width, int height) ComputeTargetResolution()
        {
            int iw = 0, ih = 0;
            var preview = ImagePreview;
            if (preview != null) { iw = preview.PixelWidth; ih = preview.PixelHeight; }
            if ((iw <= 0 || ih <= 0) && File.Exists(ImagePath))
            {
                try
                {
                    using var fs = File.OpenRead(ImagePath);
                    var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    iw = frame.PixelWidth; ih = frame.PixelHeight;
                }
                catch { /* fall through to square default */ }
            }
            if (iw <= 0 || ih <= 0) return (1080, 1080);
            if (iw > ih * 1.1) return (1920, 1080); // widescreen
            if (ih > iw * 1.1) return (1080, 1920); // portrait
            return (1080, 1080);                     // square
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

        /// <summary>Removes nodes from an API-format workflow so they are not executed.</summary>
        private static string RemoveNodes(string json, params string[] ids)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (dict == null) return json;
            foreach (var id in ids) dict.Remove(id);
            return JsonSerializer.Serialize(dict);
        }

        /// <summary>Resolves a ComfyUI "subfolder/filename" output to a local file (local path or /view download).</summary>
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
                    var tempPath = Path.Combine(Path.GetTempPath(), $"seedhunt_{Guid.NewGuid():N}_{filename}");
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
            OnPropertyChanged(nameof(CanFinish));
            OnPropertyChanged(nameof(HuntButtonText));
            OnPropertyChanged(nameof(FinishButtonText));
            AnalyzeCommand.NotifyCanExecuteChanged();
            HuntCommand.NotifyCanExecuteChanged();
            FinishCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
        }
    }
}
