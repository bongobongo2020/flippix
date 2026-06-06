using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    public class IdeogramViewModel : INotifyPropertyChanged
    {
        private const string WorkflowFile = "workflow/image/Ideogram-4-NSFW.json";
        private const string PromptFile = "prompts/prompt2json/ideagram.md";
        private const string SavePrefix = "ideogram4";

        private readonly ComfyUIService _comfyUIService;
        private readonly SettingsService _settingsService;
        private readonly IFileDialogService _fileDialogService;
        private readonly IAppLogger _logger;
        private readonly LMStudioService _lmStudioService;
        private readonly WorkflowQueueCoordinator _workflowCoordinator;

        // Queue fields
        private ObservableCollection<IdeogramQueueItem> _queue = new();
        private bool _isProcessingQueue = false;
        private bool _isWaitingForLease = false;
        private readonly ManualResetEventSlim _pauseEvent = new(true);
        private CancellationTokenSource? _queueCts;

        // Input image
        private string _inputImagePath = string.Empty;
        private BitmapImage? _inputImageSource;
        private bool _hasInputImage;

        // LLM
        private ObservableCollection<string> _availableModels = new();
        private string _selectedLlmModel = string.Empty;
        private bool _isLoadingModels;

        // Prompt
        private string _ideogramPrompt = string.Empty;
        private string _detectedAspectRatio = "1:1";

        // Workflow state
        private bool _isAnalyzing;
        private bool _isGenerating;
        private double _progress;
        private string _statusMessage = "Upload an image to begin";
        private string _logOutput = string.Empty;
        private CancellationTokenSource? _cts;

        // Result
        private BitmapImage? _resultImageSource;
        private bool _hasResult;
        private string _resultImagePath = string.Empty;

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

            BrowseImageCommand = new RelayCommand(async () => await BrowseImageAsync(), () => true);
            LoadModelsCommand = new RelayCommand(async () => await LoadModelsAsync(), () => !IsBusy && !IsLoadingModels);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            GenerateCommand = new RelayCommand(async () => await GenerateAsync(), () => CanGenerate);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            OpenResultImageCommand = new RelayCommand(OpenResultImage, () => HasResult);

            // Queue commands
            AddToQueueCommand = new RelayCommand(AddToQueue, () => CanAddToQueue);
            RemoveFromQueueCommand = new RelayCommand<IdeogramQueueItem>(RemoveFromQueue);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => Queue.Any());
            ProcessQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => CanProcessQueue);
            CancelQueueCommand = new RelayCommand(CancelQueue, () => IsProcessingQueue);

            // Load available models on startup
            _ = LoadModelsAsync();

            LoadQueueFromFile();
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
                NotifyCommands();
            }
        }

        public bool NoInputImage => !_hasInputImage;

        // ── LLM Model ────────────────────────────────────────────────────
        public ObservableCollection<string> AvailableModels
        {
            get => _availableModels;
            set { _availableModels = value; OnPropertyChanged(); }
        }

        public string SelectedLlmModel
        {
            get => _selectedLlmModel;
            set
            {
                _selectedLlmModel = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanAnalyze));
            }
        }

        public bool IsLoadingModels
        {
            get => _isLoadingModels;
            set
            {
                _isLoadingModels = value;
                OnPropertyChanged();
            }
        }

        // ── Prompt ───────────────────────────────────────────────────────
        public string IdeogramPrompt
        {
            get => _ideogramPrompt;
            set
            {
                _ideogramPrompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
                OnPropertyChanged(nameof(CanAddToQueue));
                GenerateCommand.NotifyCanExecuteChanged();
                AddToQueueCommand.NotifyCanExecuteChanged();
            }
        }

        public string DetectedAspectRatio
        {
            get => _detectedAspectRatio;
            set { _detectedAspectRatio = value; OnPropertyChanged(); }
        }

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

        // ── CanExecute ────────────────────────────────────────────────────
        public bool CanAnalyze => HasInputImage && !string.IsNullOrWhiteSpace(SelectedLlmModel) && !IsBusy;
        public bool CanGenerate => HasInputImage && !string.IsNullOrWhiteSpace(IdeogramPrompt) && !IsBusy;

        // ── Commands ──────────────────────────────────────────────────────
        public RelayCommand BrowseImageCommand { get; }
        public RelayCommand LoadModelsCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand OpenResultImageCommand { get; }

        // Queue commands
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
            set
            {
                _isProcessingQueue = value;
                OnPropertyChanged();
                NotifyQueueCommands();
            }
        }

        public bool IsWaitingForLease
        {
            get => _isWaitingForLease;
            set
            {
                _isWaitingForLease = value;
                OnPropertyChanged();
            }
        }

        public bool CanAddToQueue => HasInputImage && !string.IsNullOrWhiteSpace(IdeogramPrompt);
        public bool CanProcessQueue => _queue.Any(q => q.Status == "Pending") && !IsProcessingQueue;

        private void NotifyCommands()
        {
            AnalyzeCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
            AddToQueueCommand.NotifyCanExecuteChanged();
        }

        private void NotifyQueueCommands()
        {
            AddToQueueCommand.NotifyCanExecuteChanged();
            ProcessQueueCommand.NotifyCanExecuteChanged();
            CancelQueueCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasQueueItems));
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(PendingQueueCount));
            OnPropertyChanged(nameof(CompletedQueueCount));
        }

        // ── Browse ────────────────────────────────────────────────────────
        private async Task BrowseImageAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Image",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp");
            if (!string.IsNullOrEmpty(path))
                SetInputImage(path);
        }

        public void SetInputImage(string path)
        {
            if (!File.Exists(path)) return;
            InputImagePath = path;
            try
            {
                var bmp = LoadBitmap(path);
                InputImageSource = bmp;
                HasInputImage = true;
                AddLog($"Image: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { AddLog($"ERROR loading image: {ex.Message}"); }
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
                    AvailableModels.Clear();
                    foreach (var m in models)
                        AvailableModels.Add(m.Id);
                    if (string.IsNullOrEmpty(SelectedLlmModel) && AvailableModels.Any())
                        SelectedLlmModel = AvailableModels[0];
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
        private async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                IsAnalyzing = true;
                Progress = 0;
                StatusMessage = "Loading system prompt...";
                AddLog("=== Analyze ===");

                // Load system prompt
                var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PromptFile);
                if (!File.Exists(promptPath))
                {
                    AddLog($"ERROR: Prompt file not found: {promptPath}");
                    StatusMessage = "Error: System prompt file not found";
                    return;
                }
                var systemPrompt = await File.ReadAllTextAsync(promptPath, _cts.Token);
                AddLog($"System prompt loaded ({systemPrompt.Length} chars)");

                Progress = 10;
                StatusMessage = "Sending image to LLM...";
                AddLog($"Using model: {SelectedLlmModel}");

                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    SelectedLlmModel,
                    InputImagePath,
                    "Analyze this image and generate an Ideogram v4 prompt.",
                    systemPrompt,
                    cancellationToken: _cts.Token);

                Progress = 60;
                StatusMessage = "Parsing LLM response...";
                AddLog($"LLM response ({result.Length} chars)");

                // Parse JSON response
                var parsed = ParseIdeogramResponse(result);
                if (parsed != null)
                {
                    WpfApp.Current?.Dispatcher.Invoke(() =>
                    {
                        IdeogramPrompt = parsed.Value.Prompt;
                        DetectedAspectRatio = parsed.Value.AspectRatio;
                    });
                    AddLog($"Prompt: {parsed.Value.Prompt.Substring(0, Math.Min(200, parsed.Value.Prompt.Length))}...");
                    AddLog($"Aspect ratio: {parsed.Value.AspectRatio}");
                    StatusMessage = "Prompt ready — edit if needed, then click Generate";
                }
                else
                {
                    // If JSON parsing fails, use raw response as prompt
                    WpfApp.Current?.Dispatcher.Invoke(() => IdeogramPrompt = result);
                    AddLog("WARNING: Could not parse JSON, using raw response as prompt");
                    StatusMessage = "Raw prompt loaded — edit if needed, then click Generate";
                }

                Progress = 100;
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
                _logger.LogError($"Ideogram analyze: {ex}");
            }
            finally
            {
                IsAnalyzing = false;
                AddLog("=== Analyze ended ===");
            }
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
                StatusMessage = "Connecting to ComfyUI...";
                AddLog("=== Generate ===");
                AddLog($"Prompt: {IdeogramPrompt}");

                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(_cts.Token);
                    AddLog("Connected");
                }

                Progress = 8;
                StatusMessage = "Building workflow...";

                var workflow = BuildWorkflow();

                var progressReporter = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        WpfApp.Current?.Dispatcher.Invoke(() =>
                        {
                            Progress = 18 + pct * 0.74;
                            StatusMessage = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                        });
                    }
                });

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
                _logger.LogError($"Ideogram generate: {ex}");
            }
            finally
            {
                IsGenerating = false;
                AddLog("=== Generate ended ===");
            }
        }

        // ── Workflow building ─────────────────────────────────────────────
        private JsonElement BuildWorkflow()
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse workflow JSON");

            var seed = new Random().NextInt64(0, 999_999_999_999_999L);

            // Node 197: Seed (rgthree) — set random seed
            UpdateNode(dict, "197", inputs => inputs["seed"] = seed);

            // Node 191: FluxResolutionNode — set aspect ratio via custom_ratio mode
            // Use custom_ratio so any aspect ratio from the LLM works without needing exact preset strings
            var ratio = SanitizeAspectRatio(DetectedAspectRatio);
            UpdateNode(dict, "191", inputs =>
            {
                inputs["custom_ratio"] = true;
                inputs["custom_aspect_ratio"] = ratio;
            });

            // Node 185: Ideogram4PromptBuilderKJ — set the enhanced prompt in both places:
            // 1. high_level_description (top input box)
            // 2. regions_json with a full-frame region containing the prompt as desc
            var escapedPrompt = IdeogramPrompt.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
            var regionJson = $"[{{\"x\":0.0,\"y\":0.0,\"w\":1.0,\"h\":1.0,\"type\":\"obj\",\"text\":\"\",\"desc\":\"{escapedPrompt}\",\"palette\":[]}}]";
            UpdateNode(dict, "185", inputs =>
            {
                inputs["high_level_description"] = IdeogramPrompt;
                inputs["regions_json"] = regionJson;
            });

            // Add a SaveImage node so the output is retrievable from history.
            // The workflow's PreviewImage (node 202) doesn't register in /history for file retrieval.
            // SaveImage takes VAEDecode output (node 162, output 0 = IMAGE).
            var saveNode = new Dictionary<string, object>
            {
                ["class_type"] = "SaveImage",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["images"] = new object[] { "162", 0 },
                    ["filename_prefix"] = SavePrefix
                },
                ["_meta"] = new Dictionary<string, object?> { ["title"] = "Save Ideogram Image" }
            };
            dict["210"] = JsonSerializer.SerializeToElement(saveNode);

            return JsonSerializer.SerializeToElement(dict);
        }

        private static string SanitizeAspectRatio(string aspectRatio)
        {
            // Ensure the ratio is in W:H format (e.g. "16:9", "1:1", "4:3")
            var r = (aspectRatio ?? "1:1").Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(r, @"^\d+:\d+$"))
                r = "1:1";
            return r;
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
                Prompt = IdeogramPrompt,
                AspectRatio = DetectedAspectRatio,
                LlmModel = SelectedLlmModel
            };
            _queue.Add(queueItem);
            NotifyQueueCommands();
            AddLog($"Added to queue: {queueItem.DisplayPrompt}");

            // Auto-start if not already running
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
            _cts?.Dispose();
            _cts = _queueCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken, _queueCts.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
            var token = _cts.Token;

            try
            {
                IsGenerating = true;

                // Apply queue item settings
                IdeogramPrompt = queueItem.Prompt;
                DetectedAspectRatio = queueItem.AspectRatio;

                // Load input image if different
                if (!string.IsNullOrEmpty(queueItem.InputImagePath) && File.Exists(queueItem.InputImagePath))
                    SetInputImage(queueItem.InputImagePath);

                Progress = 0;
                StatusMessage = "Connecting to ComfyUI...";

                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(token);
                    AddLog("Connected");
                }

                Progress = 8;
                StatusMessage = "Building workflow...";

                var workflow = BuildWorkflow();

                var progressReporter = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        WpfApp.Current?.Dispatcher.Invoke(() =>
                        {
                            Progress = 18 + pct * 0.74;
                            StatusMessage = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                            queueItem.Progress = Progress;
                        });
                    }
                });

                StatusMessage = "Running ComfyUI...";
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progressReporter, token);
                AddLog($"Done: {promptId}");

                Progress = 94;
                StatusMessage = "Retrieving image...";
                var bytes = await RetrieveOutputImageAsync(promptId, token);
                if (bytes != null)
                {
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "ideogram");
                    Directory.CreateDirectory(outputDir);
                    var path = Path.Combine(outputDir, $"ideogram4_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                    await File.WriteAllBytesAsync(path, bytes, token);
                    ResultImagePath = path;
                    WpfApp.Current?.Dispatcher.Invoke(() => LoadResultImage(path));
                    HasResult = true;
                    queueItem.OutputImagePath = path;
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

        // ── Parse LLM response ────────────────────────────────────────────
        private (string Prompt, string AspectRatio)? ParseIdeogramResponse(string response)
        {
            try
            {
                // Strip markdown code fences if present
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

                var prompt = root.TryGetProperty("ideogram_prompt", out var promptEl)
                    ? promptEl.GetString() ?? ""
                    : "";

                var aspectRatio = root.TryGetProperty("aspect_ratio", out var arEl)
                    ? arEl.GetString() ?? "1:1"
                    : "1:1";

                if (!string.IsNullOrWhiteSpace(prompt))
                    return (prompt, aspectRatio);
            }
            catch (Exception ex)
            {
                AddLog($"JSON parse error: {ex.Message}");
            }
            return null;
        }

        // ── Output image retrieval ────────────────────────────────────────
        private async Task<byte[]?> RetrieveOutputImageAsync(string promptId, CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
            Uri uri;
            try { uri = new Uri(baseUrl); } catch { uri = new Uri("http://127.0.0.1:8188"); }
            bool isRemote = !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

            const int maxRetries = 20;
            const int retryDelayMs = 5000;

            if (isRemote)
            {
                for (int i = 0; i < maxRetries; i++)
                {
                    if (i > 0) { AddLog($"Retry {i}/{maxRetries}..."); await Task.Delay(retryDelayMs, token); }
                    token.ThrowIfCancellationRequested();
                    var files = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                    AddLog($"History: {files.Count} file(s)");

                    // Log filenames on first attempt to help debug
                    if (i == 0)
                        foreach (var f in files)
                            AddLog($"  file: {f}");

                    // Try prefix match first, then fall back to any non-temp image
                    var imgFile = files.FirstOrDefault(f =>
                        Path.GetFileName(f).StartsWith(SavePrefix, StringComparison.OrdinalIgnoreCase) && IsImageExt(f));
                    imgFile ??= files.FirstOrDefault(f =>
                        IsImageExt(f) && !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase));
                    // Last resort: any image file at all
                    imgFile ??= files.FirstOrDefault(f => IsImageExt(f));

                    if (imgFile != null)
                    {
                        AddLog($"Downloading: {imgFile}");
                        var data = await _comfyUIService.HttpClient.DownloadOutputImageAsync(imgFile);
                        if (data != null) { AddLog($"Downloaded {data.Length} bytes"); return data; }
                        else { AddLog($"Download returned null for: {imgFile}"); }
                    }
                }
                return null;
            }
            else
            {
                var outputDir = _settingsService.Settings?.OutputFolderPath;
                if (string.IsNullOrEmpty(outputDir)) { AddLog("ERROR: Output folder not configured"); return null; }
                for (int i = 0; i < maxRetries; i++)
                {
                    if (i > 0) { AddLog($"Retry {i}/{maxRetries}..."); await Task.Delay(retryDelayMs, token); }
                    token.ThrowIfCancellationRequested();

                    // Search for any recently created image in the output folder
                    var files = Directory.GetFiles(outputDir, "*.png", SearchOption.AllDirectories)
                        .Where(f => !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(File.GetLastWriteTime).ToList();

                    if (files.Any())
                    {
                        var latest = files[0];
                        var age = DateTime.Now - File.GetLastWriteTime(latest);
                        AddLog($"Found: {Path.GetFileName(latest)} ({age.TotalSeconds:F0}s old)");
                        if (age.TotalSeconds < 120) return await File.ReadAllBytesAsync(latest, token);
                    }
                }
                return null;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────
        private async Task SaveAndDisplayResultAsync(byte[] bytes, CancellationToken token)
        {
            var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "ideogram");
            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, $"ideogram4_{DateTime.Now:yyyyMMdd_HHmmss}.png");
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
