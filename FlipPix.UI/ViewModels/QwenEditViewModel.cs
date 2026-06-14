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
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    /// <summary>
    /// Qwen Edit tab: character replacement in a scene. The user uploads three
    /// images — Character 1 (replaces the man), Character 2 (replaces the woman),
    /// and a Base scene that contains a man and a woman. "Analyze" sends all three
    /// to llama-server (system prompt prompts/prompt2json/qwen-edit.md) to produce a
    /// single Qwen-Image-Edit instruction that swaps the two people while keeping the
    /// scene unchanged. "Generate" uploads the three images and runs the
    /// Qwen-Image-Edit-2511 workflow.
    /// Workflow: workflow/image/qwen-edit/qwen-edit-charswapAPI.json.
    /// </summary>
    public class QwenEditViewModel : INotifyPropertyChanged
    {
        private const string WorkflowFile = "workflow/image/qwen-edit/qwen-edit-charswapAPI.json";
        private const string KleinCharSwapWorkflowFile = "workflow/image/qwen-edit/qwen-edit-klein-charswapAPI.json";
        private const string KleinWorkflowFile = "workflow/image/qwen-edit/qwen-edit-klein-enhanceAPI.json";
        private const string PromptFile = "prompts/prompt2json/qwen-edit.md";
        private const string SavePrefix = "qwenedit";

        // Generate workflow options shown in the Step 3 dropdown.
        public const string WorkflowQwenEdit = "Qwen Edit";
        public const string WorkflowKlein = "Klein (Flux.2)";

        private readonly ComfyUIService _comfyUIService;
        private readonly SettingsService _settingsService;
        private readonly IFileDialogService _fileDialogService;
        private readonly IAppLogger _logger;
        private readonly LMStudioService _lmStudioService;
        private readonly WorkflowQueueCoordinator _workflowCoordinator;

        // Character 1 (replaces the man)
        private string _char1ImagePath = string.Empty;
        private BitmapImage? _char1ImageSource;
        private bool _hasChar1Image;

        // Character 2 (replaces the woman)
        private string _char2ImagePath = string.Empty;
        private BitmapImage? _char2ImageSource;
        private bool _hasChar2Image;

        // Base scene (man + woman to be replaced)
        private string _baseImagePath = string.Empty;
        private BitmapImage? _baseImageSource;
        private bool _hasBaseImage;

        // Base scene video (scrub + snap a frame to use as the base image)
        private string _baseVideoPath = string.Empty;
        private string? _baseVideoFileUri;
        private bool _hasBaseVideo;

        // LLM
        private readonly ObservableCollection<string> _availableModels = new();
        private string _selectedLlmModel = string.Empty;
        private bool _isLoadingModels;

        // Prompt
        private string _prompt = string.Empty;

        // Generate workflow selection ("Qwen Edit" or "Klein (Flux.2)")
        private readonly ObservableCollection<string> _workflowOptions =
            new() { WorkflowQwenEdit, WorkflowKlein };
        private string _selectedWorkflow = WorkflowQwenEdit;

        // Workflow state
        private bool _isAnalyzing;
        private bool _isGenerating;
        private bool _isEnhancing;
        private double _progress;
        private string _statusMessage = "Upload Character 1, Character 2 and a Base scene to begin";
        private string _logOutput = string.Empty;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _analyzeCts;
        private DateTime _lastProgressLog = DateTime.MinValue;

        // Result
        private BitmapImage? _resultImageSource;
        private bool _hasResult;
        private string _resultImagePath = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public QwenEditViewModel(
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

            BrowseChar1Command = new RelayCommand(async () => await BrowseChar1Async(), () => !IsBusy);
            BrowseChar2Command = new RelayCommand(async () => await BrowseChar2Async(), () => !IsBusy);
            BrowseBaseCommand = new RelayCommand(async () => await BrowseBaseAsync(), () => !IsBusy);
            BrowseBaseVideoCommand = new RelayCommand(async () => await BrowseBaseVideoAsync(), () => !IsBusy);
            ClearBaseVideoCommand = new RelayCommand(ClearBaseVideo, () => HasBaseVideo);
            LoadModelsCommand = new RelayCommand(async () => await LoadModelsAsync(), () => !IsAnalyzing && !IsLoadingModels);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            GenerateCommand = new RelayCommand(async () => await GenerateAsync(), () => CanGenerate);
            EnhanceCommand = new RelayCommand(async () => await EnhanceAsync(), () => CanEnhance);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            OpenResultImageCommand = new RelayCommand(OpenResultImage, () => HasResult);

            _ = LoadModelsAsync();
        }

        // ── Character 1 ──────────────────────────────────────────────────────
        public string Char1ImagePath
        {
            get => _char1ImagePath;
            set { _char1ImagePath = value; OnPropertyChanged(); }
        }

        public BitmapImage? Char1ImageSource
        {
            get => _char1ImageSource;
            set { _char1ImageSource = value; OnPropertyChanged(); }
        }

        public bool HasChar1Image
        {
            get => _hasChar1Image;
            set
            {
                _hasChar1Image = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoChar1Image));
                OnPropertyChanged(nameof(CanAnalyze));
                OnPropertyChanged(nameof(CanGenerate));
                NotifyCommands();
            }
        }

        public bool NoChar1Image => !_hasChar1Image;

        // ── Character 2 ──────────────────────────────────────────────────────
        public string Char2ImagePath
        {
            get => _char2ImagePath;
            set { _char2ImagePath = value; OnPropertyChanged(); }
        }

        public BitmapImage? Char2ImageSource
        {
            get => _char2ImageSource;
            set { _char2ImageSource = value; OnPropertyChanged(); }
        }

        public bool HasChar2Image
        {
            get => _hasChar2Image;
            set
            {
                _hasChar2Image = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoChar2Image));
                OnPropertyChanged(nameof(CanAnalyze));
                OnPropertyChanged(nameof(CanGenerate));
                NotifyCommands();
            }
        }

        public bool NoChar2Image => !_hasChar2Image;

        // ── Base scene ───────────────────────────────────────────────────────
        public string BaseImagePath
        {
            get => _baseImagePath;
            set { _baseImagePath = value; OnPropertyChanged(); }
        }

        public BitmapImage? BaseImageSource
        {
            get => _baseImageSource;
            set { _baseImageSource = value; OnPropertyChanged(); }
        }

        public bool HasBaseImage
        {
            get => _hasBaseImage;
            set
            {
                _hasBaseImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoBaseImage));
                OnPropertyChanged(nameof(CanAnalyze));
                OnPropertyChanged(nameof(CanGenerate));
                NotifyCommands();
            }
        }

        public bool NoBaseImage => !_hasBaseImage;

        // ── Base scene video ─────────────────────────────────────────────────
        public string BaseVideoPath
        {
            get => _baseVideoPath;
            set { _baseVideoPath = value; OnPropertyChanged(); }
        }

        // Bound to the preview MediaElement's Source. Use an absolute file URI so
        // WPF loads it without treating the path as a relative/pack URI.
        public string? BaseVideoFileUri
        {
            get => _baseVideoFileUri;
            set { _baseVideoFileUri = value; OnPropertyChanged(); }
        }

        public bool HasBaseVideo
        {
            get => _hasBaseVideo;
            set
            {
                _hasBaseVideo = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoBaseVideo));
            }
        }

        public bool NoBaseVideo => !_hasBaseVideo;

        // ── LLM Model ────────────────────────────────────────────────────────
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

        // ── Prompt ───────────────────────────────────────────────────────────
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

        // ── Generate workflow selection ──────────────────────────────────────
        public ObservableCollection<string> WorkflowOptions => _workflowOptions;

        public string SelectedWorkflow
        {
            get => _selectedWorkflow;
            set
            {
                _selectedWorkflow = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GenerateDescription));
            }
        }

        private bool IsKleinSelected =>
            string.Equals(SelectedWorkflow, WorkflowKlein, StringComparison.Ordinal);

        // Short blurb under the Generate header, swapped to match the chosen workflow.
        public string GenerateDescription => IsKleinSelected
            ? "Uploads the three images as references and runs the Flux.2 Klein workflow to produce the scene with both people replaced."
            : "Uploads the three images and runs Qwen-Image-Edit-2511 to produce the base scene with both people replaced.";

        // ── Workflow state ───────────────────────────────────────────────────
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
                OnPropertyChanged(nameof(CanEnhance));
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
                OnPropertyChanged(nameof(CanEnhance));
                NotifyCommands();
            }
        }

        public bool IsEnhancing
        {
            get => _isEnhancing;
            set
            {
                _isEnhancing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanAnalyze));
                OnPropertyChanged(nameof(CanGenerate));
                OnPropertyChanged(nameof(CanEnhance));
                NotifyCommands();
            }
        }

        public bool IsBusy => _isAnalyzing || _isGenerating || _isEnhancing;

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

        // ── Result ───────────────────────────────────────────────────────────
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
                OnPropertyChanged(nameof(CanEnhance));
                OpenResultFolderCommand.NotifyCanExecuteChanged();
                OpenResultImageCommand.NotifyCanExecuteChanged();
                EnhanceCommand.NotifyCanExecuteChanged();
            }
        }

        public bool NoResult => !_hasResult;

        public string ResultImagePath
        {
            get => _resultImagePath;
            set { _resultImagePath = value; OnPropertyChanged(); }
        }

        // ── CanExecute ───────────────────────────────────────────────────────
        // Analyze hits llama-server (independent of ComfyUI) so it's gated only
        // against another analyze, letting it run while a generate is in flight.
        public bool CanAnalyze =>
            HasChar1Image && HasChar2Image && HasBaseImage &&
            !string.IsNullOrWhiteSpace(SelectedLlmModel) && !IsAnalyzing;

        public bool CanGenerate =>
            HasChar1Image && HasChar2Image && HasBaseImage &&
            !string.IsNullOrWhiteSpace(Prompt) && !IsBusy;

        // Enhance refines the existing Qwen result with a Flux.2 Klein img2img pass,
        // so it needs a result already on screen and nothing else running.
        public bool CanEnhance => HasResult && !IsBusy;

        // ── Commands ─────────────────────────────────────────────────────────
        public RelayCommand BrowseChar1Command { get; }
        public RelayCommand BrowseChar2Command { get; }
        public RelayCommand BrowseBaseCommand { get; }
        public RelayCommand BrowseBaseVideoCommand { get; }
        public RelayCommand ClearBaseVideoCommand { get; }
        public RelayCommand LoadModelsCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand EnhanceCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand OpenResultImageCommand { get; }

        private void NotifyCommands()
        {
            BrowseChar1Command.NotifyCanExecuteChanged();
            BrowseChar2Command.NotifyCanExecuteChanged();
            BrowseBaseCommand.NotifyCanExecuteChanged();
            BrowseBaseVideoCommand.NotifyCanExecuteChanged();
            LoadModelsCommand.NotifyCanExecuteChanged();
            AnalyzeCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
            EnhanceCommand.NotifyCanExecuteChanged();
        }

        // ── Browse handlers ──────────────────────────────────────────────────
        private async Task BrowseChar1Async()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Character 1 (replaces the man)",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "qwenedit.char1");
            if (!string.IsNullOrEmpty(path)) SetChar1Image(path);
        }

        private async Task BrowseChar2Async()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Character 2 (replaces the woman)",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "qwenedit.char2");
            if (!string.IsNullOrEmpty(path)) SetChar2Image(path);
        }

        private async Task BrowseBaseAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Base Scene (man + woman)",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "qwenedit.base");
            if (!string.IsNullOrEmpty(path)) SetBaseImage(path);
        }

        private async Task BrowseBaseVideoAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Base Scene Video (scrub and snap a frame)",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.webm|All Files|*.*",
                persistKey: "qwenedit.basevideo");
            if (!string.IsNullOrEmpty(path)) SetBaseVideo(path);
        }

        public void SetBaseVideo(string path)
        {
            if (!File.Exists(path)) return;
            BaseVideoPath = path;
            BaseVideoFileUri = new Uri(path, UriKind.Absolute).AbsoluteUri;
            HasBaseVideo = true;
            ClearBaseVideoCommand.NotifyCanExecuteChanged();
            AddLog($"Base scene video: {Path.GetFileName(path)} — scrub and Snap & Send a frame");
        }

        private void ClearBaseVideo()
        {
            BaseVideoPath = string.Empty;
            BaseVideoFileUri = null;
            HasBaseVideo = false;
            ClearBaseVideoCommand.NotifyCanExecuteChanged();
        }

        public void SetChar1Image(string path)
        {
            if (!File.Exists(path)) return;
            Char1ImagePath = path;
            try
            {
                Char1ImageSource = LoadBitmap(path);
                HasChar1Image = true;
                AddLog($"Character 1: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { AddLog($"ERROR loading Character 1: {ex.Message}"); }
        }

        public void SetChar2Image(string path)
        {
            if (!File.Exists(path)) return;
            Char2ImagePath = path;
            try
            {
                Char2ImageSource = LoadBitmap(path);
                HasChar2Image = true;
                AddLog($"Character 2: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { AddLog($"ERROR loading Character 2: {ex.Message}"); }
        }

        public void SetBaseImage(string path)
        {
            if (!File.Exists(path)) return;
            BaseImagePath = path;
            try
            {
                BaseImageSource = LoadBitmap(path);
                HasBaseImage = true;
                AddLog($"Base scene: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { AddLog($"ERROR loading Base scene: {ex.Message}"); }
        }

        // ── Load Models ──────────────────────────────────────────────────────
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

        // ── Snap & Send pipeline (analyze → generate, one click) ─────────────
        // Invoked by the base-scene "Snap & Send" button after the snapped frame is
        // set as the base image: send all three images for analysis, wait for the
        // edit prompt, then immediately run generation with it.
        public async Task AnalyzeAndGenerateAsync()
        {
            if (!CanAnalyze)
            {
                StatusMessage = "Need Character 1, Character 2, a base scene and an LLM model before Snap & Send";
                AddLog("Snap & Send: not ready (need 3 images + model)");
                return;
            }

            await AnalyzeAsync();

            // Only continue to generate if analyze actually produced a prompt
            // (it may have been cancelled or returned nothing).
            if (string.IsNullOrWhiteSpace(Prompt))
            {
                AddLog("Snap & Send: no edit prompt produced — skipping generate");
                return;
            }

            await GenerateAsync();
        }

        // ── Analyze (llama-server, 3 images → edit prompt) ───────────────────
        private async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;
            _analyzeCts?.Dispose();
            _analyzeCts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

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
                    return;
                }
                var systemPrompt = await File.ReadAllTextAsync(promptPath, _analyzeCts.Token);

                SetAnalyzeProgress(10);
                StatusMessage = "Sending images to LLM...";
                AddLog($"Using model: {SelectedLlmModel}");

                // Order matters and is documented in the system prompt:
                // image 1 = Character 1 (man), image 2 = Character 2 (woman), image 3 = base scene.
                var images = new List<string> { Char1ImagePath, Char2ImagePath, BaseImagePath };

                var result = await _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                    SelectedLlmModel,
                    images,
                    "Image 1 is Character 1 (replaces the man). Image 2 is Character 2 (replaces the woman). " +
                    "Image 3 is the base scene. Write the Qwen-Image-Edit instruction.",
                    systemPrompt,
                    cancellationToken: _analyzeCts.Token);

                SetAnalyzeProgress(80);
                StatusMessage = "Parsing response...";

                var cleaned = (result ?? string.Empty).Trim();
                WpfApp.Current?.Dispatcher.Invoke(() =>
                {
                    Prompt = cleaned;
                });

                SetAnalyzeProgress(100);
                StatusMessage = string.IsNullOrWhiteSpace(cleaned)
                    ? "LLM returned no text — write the edit prompt manually, then Generate"
                    : "Edit prompt ready — review/edit it, then Generate";
                AddLog($"Got edit prompt ({cleaned.Length} chars)");
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
                _logger.LogError($"Qwen edit analyze: {ex}");
            }
            finally
            {
                IsAnalyzing = false;
                AddLog("=== Analyze ended ===");
            }
        }

        // Analyze shares the Progress bar with generation. When a generate is
        // running, leave its progress untouched.
        private void SetAnalyzeProgress(double value)
        {
            if (!IsGenerating) Progress = value;
        }

        // ── Generate (upload 3 images + run Qwen-Image-Edit) ─────────────────
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

                // Serialize against other tabs/queues so we never double-submit to ComfyUI.
                StatusMessage = "Waiting for other workflows to finish...";
                using var lease = await _workflowCoordinator.AcquireAsync("QwenEdit", _cts.Token);

                StatusMessage = "Connecting to ComfyUI...";
                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(_cts.Token);
                    AddLog("Connected");
                }

                Progress = 8;
                StatusMessage = "Uploading images...";
                var uploadedChar1 = await _comfyUIService.UploadImageAsync(Char1ImagePath, _cts.Token);
                var uploadedChar2 = await _comfyUIService.UploadImageAsync(Char2ImagePath, _cts.Token);
                var uploadedBase = await _comfyUIService.UploadImageAsync(BaseImagePath, _cts.Token);
                AddLog($"char1={uploadedChar1}  char2={uploadedChar2}  base={uploadedBase}");

                Progress = 18;
                StatusMessage = $"Building workflow ({SelectedWorkflow})...";
                AddLog($"Workflow: {SelectedWorkflow}");
                var workflow = IsKleinSelected
                    ? BuildKleinCharSwapWorkflow(uploadedChar1, uploadedChar2, uploadedBase, Prompt)
                    : BuildWorkflow(uploadedChar1, uploadedChar2, uploadedBase, Prompt);

                var progressReporter = MakeProgressReporter();

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
                _logger.LogError($"Qwen edit generate: {ex}");
            }
            finally
            {
                IsGenerating = false;
                AddLog("=== Generate ended ===");
            }
        }

        // ── Enhance (Flux.2 Klein img2img refine of the current result) ──────
        private async Task EnhanceAsync()
        {
            if (!CanEnhance) return;
            if (string.IsNullOrEmpty(ResultImagePath) || !File.Exists(ResultImagePath))
            {
                StatusMessage = "No result image to enhance";
                return;
            }

            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
            var sourcePath = ResultImagePath;

            try
            {
                IsEnhancing = true;
                Progress = 0;
                AddLog("=== Enhance (Klein) ===");
                AddLog($"Source: {Path.GetFileName(sourcePath)}");

                // Serialize against other tabs/queues so we never double-submit to ComfyUI.
                StatusMessage = "Waiting for other workflows to finish...";
                using var lease = await _workflowCoordinator.AcquireAsync("QwenEditKlein", _cts.Token);

                StatusMessage = "Connecting to ComfyUI...";
                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(_cts.Token);
                    AddLog("Connected");
                }

                Progress = 8;
                StatusMessage = "Uploading image...";
                var uploaded = await _comfyUIService.UploadImageAsync(sourcePath, _cts.Token);
                AddLog($"Uploaded: {uploaded}");

                Progress = 18;
                StatusMessage = "Building Klein workflow...";
                var workflow = BuildKleinWorkflow(uploaded, Prompt);

                var progressReporter = MakeProgressReporter();

                StatusMessage = "Running Klein img2img...";
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progressReporter, _cts.Token);
                AddLog($"Done: {promptId}");

                Progress = 94;
                StatusMessage = "Retrieving enhanced image...";
                var bytes = await RetrieveOutputImageAsync(promptId, _cts.Token);
                if (bytes != null)
                {
                    await SaveAndDisplayResultAsync(bytes, _cts.Token, "klein");
                    Progress = 100;
                    StatusMessage = $"Enhanced! {Path.GetFileName(ResultImagePath)}";
                }
                else
                {
                    StatusMessage = "No result — check ComfyUI logs";
                    AddLog("WARNING: No enhanced image retrieved");
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
                _logger.LogError($"Qwen edit enhance: {ex}");
            }
            finally
            {
                IsEnhancing = false;
                AddLog("=== Enhance ended ===");
            }
        }

        private Progress<FlipPix.ComfyUI.Models.ProgressMessage> MakeProgressReporter()
            => new(msg =>
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

        // ── Workflow building ────────────────────────────────────────────────
        private JsonElement BuildWorkflow(string uploadedChar1, string uploadedChar2, string uploadedBase, string prompt)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse workflow JSON");

            // 301 = Character 1 (image1), 302 = Character 2 (image2), 213 = base scene (drives canvas + image3).
            UpdateNode(dict, "301", inputs => inputs["image"] = uploadedChar1);
            UpdateNode(dict, "302", inputs => inputs["image"] = uploadedChar2);
            UpdateNode(dict, "213", inputs => inputs["image"] = uploadedBase);

            // Positive prompt (node 153). Negative (154) stays empty / zeroed out.
            UpdateNode(dict, "153", inputs => inputs["prompt"] = prompt);

            // Fresh seed each run.
            UpdateNode(dict, "3", inputs => inputs["seed"] = new Random().NextInt64(0, 999_999_999_999_999L));

            return JsonSerializer.SerializeToElement(dict);
        }

        // Flux.2 Klein character-swap workflow (alternative to the Qwen-Image-Edit
        // workflow, selectable from the Step 3 dropdown). Three LoadImage nodes feed a
        // chain of ReferenceLatent nodes: 90 = Character 1, 91 = Character 2,
        // 94 = base scene. Node 6 holds the prompt, node 25 the noise seed.
        private JsonElement BuildKleinCharSwapWorkflow(string uploadedChar1, string uploadedChar2, string uploadedBase, string prompt)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, KleinCharSwapWorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Klein char-swap workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse Klein char-swap workflow JSON");

            // 90 = Character 1 (image1), 91 = Character 2 (image2), 94 = base scene (image3).
            UpdateNode(dict, "90", inputs => inputs["image"] = uploadedChar1);
            UpdateNode(dict, "91", inputs => inputs["image"] = uploadedChar2);
            UpdateNode(dict, "94", inputs => inputs["image"] = uploadedBase);

            // 6 = positive prompt (CLIPTextEncode).
            UpdateNode(dict, "6", inputs => inputs["text"] = prompt ?? string.Empty);

            // Fresh seed each run (RandomNoise node).
            UpdateNode(dict, "25", inputs => inputs["noise_seed"] = new Random().NextInt64(0, 999_999_999_999_999L));

            return JsonSerializer.SerializeToElement(dict);
        }

        // Flux.2 Klein img2img refine pass over an already-generated result.
        // Workflow: qwen-edit-klein-enhanceAPI.json (LoadImage 172, positive
        // CLIPTextEncode 38, KSampler 82). The Qwen edit prompt is reused as the
        // Klein prompt to reinforce identity/scene.
        private JsonElement BuildKleinWorkflow(string uploadedImage, string prompt)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, KleinWorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Klein workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse Klein workflow JSON");

            // 172 = source image to enhance.
            UpdateNode(dict, "172", inputs => inputs["image"] = uploadedImage);

            // 38 = positive prompt fed straight into CLIPTextEncode (the custom
            // Text Multiline / Concatenate nodes were removed — not installed on the server).
            UpdateNode(dict, "38", inputs => inputs["text"] = prompt ?? string.Empty);

            // Fresh seed each run.
            UpdateNode(dict, "82", inputs => inputs["seed"] = new Random().NextInt64(0, 999_999_999_999_999L));

            return JsonSerializer.SerializeToElement(dict);
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

        // ── Output image retrieval ───────────────────────────────────────────
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

        // ── Helpers ──────────────────────────────────────────────────────────
        private async Task SaveAndDisplayResultAsync(byte[] bytes, CancellationToken token, string? label = null)
        {
            var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "qwen-edit");
            Directory.CreateDirectory(outputDir);
            var tag = string.IsNullOrEmpty(label) ? string.Empty : $"_{label}";
            var path = Path.Combine(outputDir, $"qwen-edit{tag}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
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
