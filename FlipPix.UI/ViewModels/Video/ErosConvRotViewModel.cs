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
    /// "10Eros ConvRot" seed-hunter tab. Upload a single face-reference image + type an action prompt →
    /// (optionally) Analyze to merge the character's appearance into the prompt → generate 4 fast low-res
    /// seed previews (reroll for fresh seeds) → pick one or more → Finish re-renders the chosen seed(s) at
    /// full resolution (the "upscale"). Drives <c>10eros-convrot-api.json</c> (LTX 2.3 FaceID character
    /// sheet, 10Eros DMD convrot model) by editing node inputs — no queue, a stateful interactive runner
    /// like <see cref="SeedHuntViewModel"/>.
    ///
    /// Unlike the LTX seed-hunter (one graph with N samplers), this workflow has a single sampler, so
    /// each preview is a separate submission with a stepped seed. "Upscale" = the same workflow re-run
    /// for the chosen seed with the in-graph half-resolution switch turned off (full res).
    /// </summary>
    public partial class ErosConvRotViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/ltx/10eros-convrot-api.json";
        private const string OutputSubfolder = "eros_convrot";
        private const string SystemPromptFile = "eros-convrot-faceid.md";
        private const int SampleCount = 4;

        // ── Workflow node ids (locked from 10eros-convrot-api.json) ─────────────────────────────
        private const string NodeFaceImage = "104";     // LoadImage "Face Reference"
        private const string NodePrompt = "40";          // CLIPTextEncode "Automatic Prompt" (literal text)
        private const string NodeSeed = "50";            // RandomNoise noise_seed
        private const string NodeWidth = "33";           // PrimitiveInt "Width"
        private const string NodeHeight = "173";         // PrimitiveInt "Height"
        private const string NodeWidthSwitch = "2237";   // ComfySwitchNode (true = half-res preview)
        private const string NodeHeightSwitch = "2239";  // ComfySwitchNode (true = half-res preview)
        private const string NodeDuration = "31";        // PrimitiveFloat "Duration (Seconds)"
        private const string NodeOutput = "101";         // SaveVideo "Result"
        private const string NodeRefResize = "118";      // ImageResizeKJv2 (character-sheet reference resize)

        // ── Krea 2 character-sheet preprocessing workflow ───────────────────────────────────────
        // Repurposes the two-reference edit workflow: the single source photo feeds BOTH image slots,
        // driven by the character-sheet instruction to emit a 4-panel sheet at 1536×1024 (the resolution
        // the LTX-Best-Face-ID character-sheet checkpoint expects).
        private const string KreaWorkflowFileName = "workflow/image/krea/krea2_edit_two_ref.json";
        private const string SheetPromptFile = "character-sheet-reference.md";
        private const int SheetWidth = 1536;
        private const int SheetHeight = 1024;
        private const string KreaImageA = "72";   // LoadImage (source / image a)
        private const string KreaImageB = "86";   // LoadImage (identity ref / image b)
        private const string KreaPrompt = "84";   // Krea2EditGroundedEncode (positive)
        private const string KreaSeed = "53";     // KSampler
        private const string KreaLatent = "82";   // EmptySD3LatentImage
        private const string KreaResize = "77";   // ResizeImageMaskNode
        private const string KreaSave = "29";     // SaveImage

        // ── Input state ─────────────────────────────────────────────────────────
        private string _imagePath = string.Empty;
        private BitmapImage? _imagePreview;
        private string _imageInfo = string.Empty;
        private string _prompt = string.Empty;
        private double _lengthSeconds = 5;
        private long _baseSeed = -1;
        private bool _isAnalyzing;
        private string _currentPhase = string.Empty;
        private string? _activePreviewUri;
        private long _currentBatchSeed = -1; // base seed that produced the on-screen samples

        // ── Character-sheet preprocessing state ─────────────────────────────────
        private string _sheetPath = string.Empty;
        private BitmapImage? _sheetPreview;
        private bool _useSourceAsSheet;
        // path → ComfyUI uploaded (input-folder) filename; upload each file once.
        private readonly Dictionary<string, string> _uploadCache = new(StringComparer.OrdinalIgnoreCase);

        private readonly ObservableCollection<SeedHuntSample> _samples = new(
            Enumerable.Range(1, SampleCount).Select(i => new SeedHuntSample(i)));
        private readonly ObservableCollection<SeedHuntResult> _results = new();
        private SeedHuntSample? _selectedSampleForPreview;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _analyzeCts;
        private CancellationTokenSource? _runCts;

        public ErosConvRotViewModel(
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
            BuildSheetCommand = new RelayCommand(async () => await BuildCharacterSheetAsync(), () => CanBuildSheet);
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

            AddLog("10Eros ConvRot initialized");
        }

        #region Commands

        public ICommand SelectImageCommand { get; }
        public RelayCommand BuildSheetCommand { get; }
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
                    _imagePreview = LoadPreview(value, out _imageInfo);
                    OnPropertyChanged(nameof(ImagePreview));
                    OnPropertyChanged(nameof(ImageInfo));
                    // A new source photo invalidates any previously built sheet.
                    SheetPath = string.Empty;
                    _sheetPreview = null;
                    OnPropertyChanged(nameof(SheetPreview));
                    OnPropertyChanged(nameof(HasReference));
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? ImagePreview => _imagePreview;
        public string ImageInfo => _imageInfo;

        /// <summary>Local path of the generated (or user-supplied) 4-panel character sheet fed to the video.</summary>
        public string SheetPath
        {
            get => _sheetPath;
            private set
            {
                if (_sheetPath != value)
                {
                    _sheetPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSheet));
                    OnPropertyChanged(nameof(HasReference));
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? SheetPreview => _sheetPreview;

        /// <summary>When true, the uploaded image IS already a character sheet — skip the Krea 2 step.</summary>
        public bool UseSourceAsSheet
        {
            get => _useSourceAsSheet;
            set
            {
                if (_useSourceAsSheet != value)
                {
                    _useSourceAsSheet = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasReference));
                    OnPropertyChanged(nameof(CanBuildSheet));
                    OnCanExecuteChanged();
                }
            }
        }

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

        /// <summary>Video length in seconds (clamped 1–60 when applied to the workflow).</summary>
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

        /// <summary>The single fixed workflow name shown in the tab's selector.</summary>
        public IReadOnlyList<string> WorkflowOptions { get; } = new[] { "10eros-convrot" };
        public string SelectedWorkflow { get; set; } = "10eros-convrot";

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

        public ObservableCollection<SeedHuntSample> Samples => _samples;
        public ObservableCollection<SeedHuntResult> Results => _results;

        // ListBox selection (which tile is being previewed). Setting it loads the shared player.
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

        public bool HasImage => !string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath);
        public bool HasSheet => !string.IsNullOrEmpty(SheetPath) && File.Exists(SheetPath);

        /// <summary>The image actually fed to the video's FaceID node: the built sheet, or the source
        /// photo directly when the user marked it as already-a-sheet.</summary>
        public string ReferencePath => UseSourceAsSheet ? ImagePath : SheetPath;
        public bool HasReference => !string.IsNullOrEmpty(ReferencePath) && File.Exists(ReferencePath);

        public bool HasSamples => _samples.Any(s => s.HasVideo);
        public IEnumerable<SeedHuntSample> SelectedSamples =>
            _samples.Where(s => s.IsSelected && s.HasVideo).OrderBy(s => s.Slot);
        public int SelectedCount => _samples.Count(s => s.IsSelected && s.HasVideo);
        public bool HasSelection => _samples.Any(s => s.IsSelected && s.HasVideo);

        public bool CanAnalyze => HasImage && !string.IsNullOrWhiteSpace(Prompt) && !IsAnalyzing && !IsProcessing;
        public bool CanBuildSheet => HasImage && !UseSourceAsSheet && !IsProcessing && !IsAnalyzing;
        public bool CanHunt => HasReference && !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing && !IsAnalyzing;
        public bool CanFinish => !IsProcessing && !IsAnalyzing && HasSelection;

        public string HuntButtonText => HasSamples ? $"🎲 Reroll — new {SampleCount} seeds" : $"🎯 Generate {SampleCount} Samples";
        public bool ShowReroll => HasSamples;
        public string FinishButtonText => SelectedCount > 1
            ? $"✅ Upscale {SelectedCount} Selected → Final Videos"
            : "✅ Upscale Selected → Final Video";

        #endregion

        #region Image selection

        private async void SelectImage()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Source Photo",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: "erosconvrot.face");

            if (path != null)
            {
                ImagePath = path;
                AddLog($"Source photo: {Path.GetFileName(path)}");
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

        #region Analysis (optional prompt enhancement)

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
                AddLog($"Merging character appearance into prompt — sending to {_lmStudioService.DescribeTarget(model)}");

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", SystemPromptFile);
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"System prompt not found: {promptFilePath}");
                var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

                var result = await _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                    model,
                    new[] { ImagePath },
                    $"Reference image is the character. Draft caption to enhance:\n{Prompt}",
                    systemPrompt,
                    maxTokens: 2000,
                    cancellationToken: token);

                var cleaned = CleanOutput(result);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    Prompt = cleaned;
                    AddLog($"Prompt enhanced ({cleaned.Length} chars)");
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
            // Keep a `ref_t2v:` prefix (the LTX FaceID model expects it); only strip a stray "prompt:".
            if (lower.StartsWith("prompt:") || lower.StartsWith("prompt :"))
                text = text.Substring(text.IndexOf(':') + 1).Trim();
            if (text.Length > 1 && text[0] == '"' && text[^1] == '"')
                text = text[1..^1].Trim();
            return text;
        }

        #endregion

        #region Character sheet (Krea 2 preprocessing)

        /// <summary>
        /// Runs the Krea 2 edit workflow to turn the single source photo into a 4-panel character
        /// reference sheet (close-up face + full-body front/side/back at 1536×1024) — the reference the
        /// LTX-Best-Face-ID character-sheet checkpoint is trained on. The source photo feeds both image
        /// slots; the character-sheet instruction drives the layout. The sheet is downloaded, uploaded
        /// back to ComfyUI as an input, and used as the FaceID reference for the video.
        /// </summary>
        private async Task BuildCharacterSheetAsync()
        {
            if (!CanBuildSheet) return;

            await RunWorkflowAsync("Character Sheet", async (token, reportPhase) =>
            {
                reportPhase("Uploading source photo...");
                var srcName = await EnsureUploadedAsync(ImagePath);
                var sheetPrompt = (await LoadFileAsync(Path.Combine("prompts", "prompt2json", SheetPromptFile), token)).Trim();
                var ts = DateTime.Now.ToString("yyyyMMddHHmmss");

                var json = await LoadFileAsync(KreaWorkflowFileName, token);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, KreaImageA, "image", srcName);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, KreaImageB, "image", srcName);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, KreaPrompt, "prompt", sheetPrompt);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, KreaSeed, "seed", NewSeed());
                // Force the 4-panel sheet resolution, bypassing the workflow's ResolutionSelector.
                WorkflowNodeUpdater.UpdateNodeInput(ref json, KreaLatent, "width", SheetWidth);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, KreaLatent, "height", SheetHeight);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, KreaResize, "resize_type.width", SheetWidth);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, KreaResize, "resize_type.height", SheetHeight);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, KreaSave, "filename_prefix", $"{OutputSubfolder}/sheet_{ts}");

                reportPhase("Generating character sheet with Krea 2...");
                var promptId = await SubmitAsync(json, 0, 95, token);

                string? local = null;
                var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
                if (byNode.TryGetValue(KreaSave, out var outs) && outs.Count > 0)
                    local = await ResolveImageToLocalAsync(outs[0]);
                local ??= FindTokenImageOnDisk($"sheet_{ts}");
                if (local == null || !File.Exists(local))
                    throw new Exception("Character sheet was not produced.");

                // Upload the sheet so the video workflow's LoadImage (node 104) can read it as an input.
                await EnsureUploadedAsync(local);
                SetSheet(local);
                ProcessingStatus = "Character sheet ready — write an action prompt, then Generate Samples.";
                AddLog($"Character sheet ready: {Path.GetFileName(local)}");
            });
        }

        private void SetSheet(string localPath)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _sheetPreview = LoadPreview(localPath, out _);
                SheetPath = localPath;
                OnPropertyChanged(nameof(SheetPreview));
                OnCanExecuteChanged();
            });
        }

        private async Task<string?> ResolveImageToLocalAsync(string imageFile)
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
                        var srcPath = Path.Combine(outputFolder, imageFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(srcPath))
                        {
                            await WaitForFileStableAsync(srcPath);
                            var tmpLocal = Path.Combine(Path.GetTempPath(), $"ecr_sheet_{Guid.NewGuid():N}.png");
                            File.Copy(srcPath, tmpLocal, true);
                            return tmpLocal;
                        }
                    }
                }

                var parts = imageFile.Split('/');
                var filename = parts.Last();
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : "";
                var bytes = await _comfyUIService.HttpClient.DownloadViewFileAsync(filename, subfolder, "output");
                if (bytes is { Length: > 0 })
                {
                    var tmp = Path.Combine(Path.GetTempPath(), $"ecr_sheet_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tmp, bytes);
                    return tmp;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Resolve sheet image failed: {ex.Message}");
            }
            return null;
        }

        private string? FindTokenImageOnDisk(string token_)
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
                        candidates.AddRange(Directory.GetFiles(folder, "*.png")
                            .Where(f => Path.GetFileName(f).IndexOf(token_, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                var newest = candidates.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                if (newest == null) return null;
                var tmp = Path.Combine(Path.GetTempPath(), $"ecr_sheet_{Guid.NewGuid():N}.png");
                File.Copy(newest, tmp, true);
                return tmp;
            }
            catch (Exception ex)
            {
                AddLog($"Sheet disk scan failed: {ex.Message}");
                return null;
            }
        }

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

        public void ReportPreviewFailed(string message) =>
            AddLog($"Preview playback failed: {message} (uri: {ActivePreviewUri})");

        public void ReportPreviewOpened(string uri) =>
            AddLog($"Preview opened: {uri}");

        private void OnSampleSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(SeedHuntSample.IsSelected)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(FinishButtonText));
            OnCanExecuteChanged();
        }

        #endregion

        #region Stage 1 — Hunt / Reroll (4 low-res seed previews)

        private async Task RunHuntAsync()
        {
            if (!CanHunt) return;

            // Reroll always gets a fresh seed so ComfyUI re-samples; first gen honors a pinned seed.
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
                Application.Current.Dispatcher.Invoke(() => { foreach (var s in _samples) s.Reset(); });
                OnPropertyChanged(nameof(HasSamples));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedCount));

                var faceName = await EnsureUploadedAsync(ReferencePath);
                var (tw, th) = ComputeTargetResolution(ImagePath);
                AddLog($"Full-res target {tw}×{th} ({(tw == th ? "square" : tw > th ? "widescreen" : "portrait")}), " +
                       $"{Math.Clamp(LengthSeconds <= 0 ? 5 : LengthSeconds, 1, 60):0.#}s — previews render at half res.");

                int found = 0;
                for (int slot = 1; slot <= SampleCount; slot++)
                {
                    token.ThrowIfCancellationRequested();
                    var seed = batchSeed + (slot - 1);
                    reportPhase($"Generating sample {slot}/{SampleCount} (seed {seed})...");
                    SetSampleStatus(slot, "generating");

                    var json = await LoadWorkflowJsonAsync(token);
                    ApplyCommonInputs(ref json, faceName, Prompt, tw, th, halfRes: true);
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeSeed, "noise_seed", seed);
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeOutput, "filename_prefix",
                        $"{OutputSubfolder}/ecr{batchId}_p{slot}");

                    var from = (slot - 1) * 95.0 / SampleCount;
                    var to = slot * 95.0 / SampleCount;
                    var local = await SubmitAndRetrieveAsync(json, $"ecr{batchId}_p{slot}", from, to, token);
                    if (local != null)
                    {
                        SetSampleVideo(slot, local);
                        found++;
                        if (found == 1) ActivePreviewUri ??= local;
                    }
                    else
                    {
                        SetSampleStatus(slot, "no output");
                        AddLog($"  Sample {slot}: no output produced");
                    }
                }

                if (found == 0) throw new Exception("No sample previews were produced.");
                ProcessingStatus = $"{found}/{SampleCount} samples ready — pick one, then Upscale";
            });
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

            var thumb = ExtractFirstFrame(localPath);
            if (thumb != null)
                Application.Current.Dispatcher.Invoke(() =>
                    _samples.First(s => s.Slot == slot).ThumbnailImage = thumb);
        }

        private void SetSampleStatus(int slot, string status) =>
            Application.Current.Dispatcher.Invoke(() => _samples.First(s => s.Slot == slot).Status = status);

        #endregion

        #region Stage 2 — Finish (re-render chosen seed at full resolution)

        private async Task RunFinishAsync()
        {
            var selected = SelectedSamples.ToList();
            if (selected.Count == 0) return;
            var batchSeed = _currentBatchSeed >= 0 ? _currentBatchSeed : BaseSeed;

            await RunWorkflowAsync("Upscale", async (token, reportPhase) =>
            {
                var faceName = await EnsureUploadedAsync(ReferencePath);
                var (tw, th) = ComputeTargetResolution(ImagePath);
                int done = 0;
                var finishedPaths = new List<string>();

                foreach (var sample in selected)
                {
                    token.ThrowIfCancellationRequested();
                    var slot = sample.Slot;
                    var seed = batchSeed + (slot - 1);
                    var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    reportPhase($"Upscaling Sample {slot} ({done + 1}/{selected.Count}) → {tw}×{th}...");

                    var json = await LoadWorkflowJsonAsync(token);
                    // Same seed + inputs as the preview, but full resolution (half-res switch off).
                    ApplyCommonInputs(ref json, faceName, Prompt, tw, th, halfRes: false);
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeSeed, "noise_seed", seed);
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeOutput, "filename_prefix",
                        $"{OutputSubfolder}/final_s{slot}_{ts}");

                    var from = done * 100.0 / selected.Count;
                    var to = (done + 1) * 100.0 / selected.Count;
                    var local = await SubmitAndRetrieveAsync(json, $"final_s{slot}_{ts}", from, to, token);
                    if (local == null || !File.Exists(local))
                    {
                        AddLog($"Sample {slot}: no final video produced — skipping");
                        done++;
                        continue;
                    }

                    var outputDir = Path.Combine(
                        _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "ErosConvRot");
                    Directory.CreateDirectory(outputDir);
                    var finalPath = Path.Combine(outputDir, $"ErosConvRot_s{slot}_{ts}.mp4");
                    File.Copy(local, finalPath, true);
                    await LocalCopyService.CopyVideoAsync(finalPath);

                    var fi = new FileInfo(finalPath);
                    var result = new SeedHuntResult
                    {
                        Slot = slot,
                        VideoPath = finalPath,
                        VideoFileUri = finalPath,
                        Info = $"Sample {slot} • {tw}×{th} • {fi.Length / 1024 / 1024.0:F1}MB"
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
                    AddLog($"=== Sample {slot} upscaled: {finalPath} ===");
                    finishedPaths.Add(finalPath);
                    done++;
                }

                if (finishedPaths.Count > 1)
                {
                    reportPhase($"Joining {finishedPaths.Count} videos into one...");
                    await JoinFinishedVideosAsync(finishedPaths, token);
                }

                ProcessingStatus = done == selected.Count
                    ? $"Upscaled {done} video(s)!"
                    : $"Upscaled {finishedPaths.Count}/{selected.Count} video(s)";
            });
        }

        /// <summary>
        /// FFmpeg-concatenates all finished videos (in slot order) into one continuous MP4, adds it as a
        /// result, and loads it in the shared player. Best-effort: a failure leaves the singles intact.
        /// </summary>
        private async Task JoinFinishedVideosAsync(IReadOnlyList<string> clips, CancellationToken token)
        {
            try
            {
                var ffmpeg = FindFFmpeg();
                if (ffmpeg == null) { AddLog("Join skipped: FFmpeg not found."); return; }

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "ErosConvRot");
                Directory.CreateDirectory(outputDir);
                var joinedPath = Path.Combine(outputDir, $"ErosConvRot_joined_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                var listPath = Path.Combine(Path.GetTempPath(), $"ecr_concat_{Guid.NewGuid():N}.txt");
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
                        "-c:a", "aac", "-b:a", "192k", "-pix_fmt", "yuv420p", joinedPath
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
                finally { try { File.Delete(listPath); } catch { /* best effort */ } }

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
            catch (Exception ex) { AddLog($"Join failed: {ex.Message}"); }
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
                AddLog($"=== 10Eros ConvRot {phase} ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("ErosConvRot", token);

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
                MessageBox.Show($"{phase} failed:\n{ex.Message}", "10Eros ConvRot Error",
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
        /// Sets the inputs common to hunt and finish: face image, prompt, resolution, duration and the
        /// half-res preview switch. Seed and output prefix are set by the caller (they differ per slot).
        /// </summary>
        private void ApplyCommonInputs(ref string json, string faceName, string prompt, int width, int height, bool halfRes)
        {
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeFaceImage, "image", faceName);
            // The LTX FaceID model expects a `ref_t2v:` prompt prefix.
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodePrompt, "text", EnsureRefPrefix(prompt));
            // Resize the reference to the 4-panel character-sheet resolution the checkpoint expects.
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeRefResize, "width", SheetWidth);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeRefResize, "height", SheetHeight);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeWidth, "value", width);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeHeight, "value", height);
            // Half-res preview during hunt; full res at finish. The in-graph switches halve W/H when true.
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeWidthSwitch, "switch", halfRes);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeHeightSwitch, "switch", halfRes);

            var len = Math.Clamp(LengthSeconds <= 0 ? 5 : LengthSeconds, 1, 60);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeDuration, "value", len);
        }

        /// <summary>
        /// Picks the full-resolution target from the reference image aspect:
        /// 1024×1024 (square), 1920×1024 (widescreen) or 1024×1920 (portrait).
        /// </summary>
        private (int width, int height) ComputeTargetResolution(string imagePath)
        {
            int iw = 0, ih = 0;
            if (string.Equals(imagePath, ImagePath, StringComparison.OrdinalIgnoreCase) && ImagePreview is { } preview)
            {
                iw = preview.PixelWidth; ih = preview.PixelHeight;
            }
            if ((iw <= 0 || ih <= 0) && File.Exists(imagePath))
            {
                try
                {
                    using var fs = File.OpenRead(imagePath);
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

        /// <summary>Submits a workflow, waits for completion, and resolves the SaveVideo (node 101) output
        /// to a local file — first via /history node outputs, then a disk scan for the per-run token.</summary>
        private async Task<string?> SubmitAndRetrieveAsync(string json, string token_, double from, double to, CancellationToken token)
        {
            var existing = GetExistingVideoFiles("*.mp4", OutputSubfolder);
            var promptId = await SubmitAsync(json, from, to, token);

            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
            if (byNode.TryGetValue(NodeOutput, out var outs) && outs.Count > 0)
            {
                var pick = outs.FirstOrDefault(f => f.IndexOf("-audio", StringComparison.OrdinalIgnoreCase) >= 0) ?? outs[0];
                var local = await ResolveOutputToLocalAsync(pick);
                if (local != null) return local;
            }

            // Fallback: wait for a new mp4 carrying this run's token in the output subfolder.
            var found = await WaitForNewVideoAsync(existing, "*.mp4",
                TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(3), OutputSubfolder);
            if (found != null && Path.GetFileName(found).IndexOf(token_, StringComparison.OrdinalIgnoreCase) >= 0)
                return found;
            return found ?? FindTokenFileOnDisk(token_);
        }

        private string? FindTokenFileOnDisk(string token_)
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
                            .Where(f => Path.GetFileName(f).IndexOf(token_, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                return candidates
                    .OrderByDescending(f => f.IndexOf("-audio", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ThenByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                AddLog($"Disk scan failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Uploads a file to ComfyUI once, caching the returned input-folder name by path.</summary>
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

        private static Task<string> LoadWorkflowJsonAsync(CancellationToken token) =>
            LoadFileAsync(WorkflowFileName, token);

        /// <summary>Reads a file shipped next to the exe (workflow JSON or prompt), relative to BaseDirectory.</summary>
        private static async Task<string> LoadFileAsync(string relativePath, CancellationToken token)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");
            return await File.ReadAllTextAsync(path, token);
        }

        private static string EnsureRefPrefix(string prompt)
        {
            var t = (prompt ?? string.Empty).TrimStart();
            return t.StartsWith("ref_t2v:", StringComparison.OrdinalIgnoreCase) ? t : "ref_t2v: " + t;
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
                        ProcessingStatus = $"{CurrentPhase}: {msg.Data.Value}/{msg.Data.Max}";
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
                    var tempPath = Path.Combine(Path.GetTempPath(), $"ecr_{Guid.NewGuid():N}_{filename}");
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

        private BitmapImage? ExtractFirstFrame(string videoPath)
        {
            try
            {
                var ffmpeg = FindFFmpeg();
                if (ffmpeg == null) return null;
                var outPath = Path.Combine(Path.GetTempPath(), $"ecr_thumb_{Guid.NewGuid():N}.png");
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

        private static long NewSeed() => System.Random.Shared.NextInt64(0, 1_000_000_000_000_000L);

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(CanAnalyze));
            OnPropertyChanged(nameof(CanBuildSheet));
            OnPropertyChanged(nameof(CanHunt));
            OnPropertyChanged(nameof(CanFinish));
            OnPropertyChanged(nameof(HasReference));
            OnPropertyChanged(nameof(HuntButtonText));
            OnPropertyChanged(nameof(ShowReroll));
            OnPropertyChanged(nameof(FinishButtonText));
            AnalyzeCommand.NotifyCanExecuteChanged();
            BuildSheetCommand.NotifyCanExecuteChanged();
            HuntCommand.NotifyCanExecuteChanged();
            FinishCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
        }
    }
}
