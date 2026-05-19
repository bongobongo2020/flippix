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
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    public class AmateurGeneratorViewModel : BasePromptViewModel, IDisposable
    {
        private bool _disposed = false;
        private readonly FlipPix.ComfyUI.Services.ComfyUIService _comfyUIService;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly ComfyUIImageRetriever _imageRetriever;
        private readonly WorkflowQueueCoordinator _workflowCoordinator;

        private string _additionalPrompt = string.Empty;
        private int _orientationIndex = 0; // 0 = Landscape, 1 = Portrait
        private int _styleIndex = 0;
        private int _steps = 9;
        private double _cfg = 1.0;
        private long _seed = 0;
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;
        private bool _hasResultImage = false;
        private string _resultImagePath = string.Empty;
        private BitmapImage? _resultImageSource;
        private string _imageInfo = string.Empty;
        private System.Threading.CancellationTokenSource? _cancellationTokenSource;

        // Image analysis fields
        private readonly LMStudioService? _lmStudioService;
        private readonly IFileDialogService? _fileDialogService;
        private string _sourceImagePath = string.Empty;
        private BitmapImage? _sourceImageSource;
        private bool _hasSourceImage = false;
        private bool _isAnalyzingImage = false;

        // Static random for seed generation (better than creating new Random() each time)
        private static readonly Random _random = new Random();

        // Amateur LoRA is always enabled
        private const string AmateurLoraName = "amateur_photography_zimage_v1.safetensors";
        private const double AmateurLoraStrength1 = 0.4; // Node 105
        private const double AmateurLoraStrength2 = 0.9; // Node 752

        // Orientation and Style options
        private ObservableCollection<string> _orientations = new(new[] { "Landscape", "Portrait" });
        private ObservableCollection<string> _styles = new(new[] { "Natural", "Cinematic", "Dramatic", "Vintage", "Modern" });

        // Queue fields
        private ObservableCollection<AmateurQueueItem> _queue = new();
        private bool _isProcessingQueue = false;
        private bool _isWaitingForLease = false;
        private readonly ManualResetEventSlim _pauseEvent = new(true);
        private CancellationTokenSource? _queueCancellationTokenSource;

        public AmateurGeneratorViewModel(
            FlipPix.ComfyUI.Services.ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IPromptService? promptService = null,
            LoraManager? loraManager = null,
            ComfyUIImageRetriever? imageRetriever = null,
            WorkflowQueueCoordinator? workflowCoordinator = null,
            LMStudioService? lmStudioService = null,
            IFileDialogService? fileDialogService = null)
            : base(promptService ?? new PromptService(logger), logger, "AmateurGenerator")
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _imageRetriever = imageRetriever ?? new ComfyUIImageRetriever();
            _workflowCoordinator = workflowCoordinator ?? new WorkflowQueueCoordinator();
            _lmStudioService = lmStudioService;
            _fileDialogService = fileDialogService;

            // Initialize commands
            AddToQueueCommand = new RelayCommand(AddToQueue, () => CanAddToQueue);
            RemoveFromQueueCommand = new RelayCommand<AmateurQueueItem>(RemoveFromQueue);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => _queue.Any());
            ProcessQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => CanProcessQueue);
            CancelQueueCommand = new RelayCommand(CancelQueue, () => IsProcessingQueue);
            CancelGenerationCommand = new RelayCommand(CancelGeneration, () => IsProcessing);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResultImage);
            OpenResultImageCommand = new RelayCommand(OpenResultImage, () => HasResultImage);
            PasteFromClipboardCommand = new RelayCommand(PasteFromClipboard);
            BrowseImageCommand = new RelayCommand(async () => await BrowseImageAsync());
            AnalyzeImageCommand = new RelayCommand(async () => await AnalyzeImageAndEnhancePromptAsync(), () => HasSourceImage && !IsAnalyzingImage);

            LoadQueueFromFile();
            AddLog("Amateur Generator initialized");
        }

        // Properties
        public string AdditionalPrompt
        {
            get => _additionalPrompt;
            set
            {
                if (_additionalPrompt != value)
                {
                    _additionalPrompt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanAddToQueue));
                    NotifyQueueCommands();
                }
            }
        }

        public int OrientationIndex
        {
            get => _orientationIndex;
            set
            {
                if (_orientationIndex != value)
                {
                    _orientationIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        public int StyleIndex
        {
            get => _styleIndex;
            set
            {
                if (_styleIndex != value)
                {
                    _styleIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<string> Orientations => _orientations;
        public ObservableCollection<string> Styles => _styles;

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (_isProcessing != value)
                {
                    _isProcessing = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanCancel));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool CanCancel => IsProcessing;

        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            set
            {
                if (_isProcessingQueue != value)
                {
                    _isProcessingQueue = value;
                    OnPropertyChanged();
                    NotifyQueueCommands();
                }
            }
        }

        public bool IsWaitingForLease
        {
            get => _isWaitingForLease;
            set
            {
                if (_isWaitingForLease != value)
                {
                    _isWaitingForLease = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ProcessingStatus
        {
            get => _processingStatus;
            set
            {
                if (_processingStatus != value)
                {
                    _processingStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public double ProcessingProgress
        {
            get => _processingProgress;
            set
            {
                if (_processingProgress != value)
                {
                    _processingProgress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ProgressPercentage));
                }
            }
        }

        public string ProgressPercentage => $"{ProcessingProgress:F0}%";

        public string LogOutput
        {
            get => _logOutput;
            set
            {
                if (_logOutput != value)
                {
                    _logOutput = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasResultImage
        {
            get => _hasResultImage;
            set
            {
                if (_hasResultImage != value)
                {
                    _hasResultImage = value;
                    OnPropertyChanged();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string ResultImagePath
        {
            get => _resultImagePath;
            set
            {
                if (_resultImagePath != value)
                {
                    _resultImagePath = value;
                    OnPropertyChanged();
                }
            }
        }

        public BitmapImage? ResultImageSource
        {
            get => _resultImageSource;
            set
            {
                if (_resultImageSource != value)
                {
                    _resultImageSource = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ImageInfo
        {
            get => _imageInfo;
            set
            {
                if (_imageInfo != value)
                {
                    _imageInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SourceImagePath
        {
            get => _sourceImagePath;
            set
            {
                if (_sourceImagePath != value)
                {
                    _sourceImagePath = value;
                    OnPropertyChanged();
                }
            }
        }

        public BitmapImage? SourceImageSource
        {
            get => _sourceImageSource;
            set
            {
                if (_sourceImageSource != value)
                {
                    _sourceImageSource = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasSourceImage
        {
            get => _hasSourceImage;
            set
            {
                if (_hasSourceImage != value)
                {
                    _hasSourceImage = value;
                    OnPropertyChanged();
                    AnalyzeImageCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool IsAnalyzingImage
        {
            get => _isAnalyzingImage;
            set
            {
                if (_isAnalyzingImage != value)
                {
                    _isAnalyzingImage = value;
                    OnPropertyChanged();
                    AnalyzeImageCommand.NotifyCanExecuteChanged();
                }
            }
        }

        // Queue properties
        public ObservableCollection<AmateurQueueItem> Queue => _queue;
        public bool HasQueueItems => _queue.Any();
        public int QueueCount => _queue.Count;
        public int PendingQueueCount => _queue.Count(q => q.Status == "Pending");
        public int CompletedQueueCount => _queue.Count(q => q.Status == "Completed");
        public bool CanAddToQueue => !string.IsNullOrWhiteSpace(AdditionalPrompt);
        public bool CanProcessQueue => _queue.Any(q => q.Status == "Pending") && !IsProcessingQueue;

        // Implementation of abstract BasePromptViewModel properties
        public override string CurrentPromptText => AdditionalPrompt;

        public override int AspectRatioIndex
        {
            get => OrientationIndex;
            set => OrientationIndex = value;
        }

        public override int Steps
        {
            get => _steps;
            set
            {
                if (_steps != value)
                {
                    _steps = value;
                    OnPropertyChanged();
                }
            }
        }

        public override double Cfg
        {
            get => _cfg;
            set
            {
                if (_cfg != value)
                {
                    _cfg = value;
                    OnPropertyChanged();
                }
            }
        }

        public override long Seed
        {
            get => _seed;
            set
            {
                if (_seed != value)
                {
                    _seed = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _denoise = 1.0;
        public override double Denoise
        {
            get => _denoise;
            set
            {
                if (_denoise != value)
                {
                    _denoise = value;
                    OnPropertyChanged();
                }
            }
        }

        // Override base class methods
        protected override void OnPromptSaved(string promptName)
        {
            AddLog($"Prompt saved: {promptName}");
        }

        protected override void OnPromptDeleted(string promptName)
        {
            AddLog($"Prompt deleted: {promptName}");
        }

        protected override void OnPromptLoaded(SavedPrompt savedPrompt)
        {
            AdditionalPrompt = savedPrompt.Prompt;
            OrientationIndex = savedPrompt.AspectRatioIndex;
            Steps = savedPrompt.Steps;
            Cfg = savedPrompt.Cfg;
            Seed = savedPrompt.Seed;
            Denoise = savedPrompt.Denoise;

            if (savedPrompt.AdditionalData != null && savedPrompt.AdditionalData is Dictionary<string, object> additionalData)
            {
                if (additionalData.TryGetValue("StyleIndex", out var styleIndexObj) && styleIndexObj is int styleIndex)
                {
                    StyleIndex = styleIndex;
                }
            }

            AddLog($"Prompt loaded: {savedPrompt.Name}");
        }

        protected override void OnPromptError(string error)
        {
            AddLog($"ERROR: {error}");
        }

        public override Dictionary<string, object> GetAdditionalPromptData()
        {
            return new Dictionary<string, object>
            {
                { "StyleIndex", StyleIndex }
            };
        }

        // Commands
        public RelayCommand AddToQueueCommand { get; }
        public ICommand RemoveFromQueueCommand { get; }
        public ICommand ClearQueueCommand { get; }
        public RelayCommand ProcessQueueCommand { get; }
        public ICommand CancelQueueCommand { get; }
        public ICommand CancelGenerationCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand OpenResultImageCommand { get; }
        public ICommand PasteFromClipboardCommand { get; }
        public ICommand BrowseImageCommand { get; }
        public RelayCommand AnalyzeImageCommand { get; }

        private void NotifyQueueCommands()
        {
            if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(NotifyQueueCommands);
                return;
            }
            AddToQueueCommand.NotifyCanExecuteChanged();
            ProcessQueueCommand.NotifyCanExecuteChanged();
            (CancelQueueCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }

        // Queue Management Methods

        private void AddToQueue()
        {
            if (!CanAddToQueue) return;

            var queueItem = new AmateurQueueItem
            {
                Prompt = AdditionalPrompt,
                OrientationIndex = OrientationIndex,
                StyleIndex = StyleIndex,
                Steps = Steps,
                Cfg = Cfg,
                Seed = Seed
            };

            _queue.Add(queueItem);
            SaveQueueToFile();
            AddLog($"Added to queue: {queueItem.DisplayPrompt}");

            OnPropertyChanged(nameof(HasQueueItems));
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(PendingQueueCount));
            NotifyQueueCommands();

            // Auto-start queue processing if not already running
            if (!IsProcessingQueue && _queue.Any(q => q.Status == "Pending"))
            {
                _ = ProcessQueueAsync();
            }
        }

        private void RemoveFromQueue(AmateurQueueItem? item)
        {
            if (item == null) return;

            _queue.Remove(item);
            SaveQueueToFile();
            AddLog($"Removed from queue: {item.DisplayPrompt}");

            OnPropertyChanged(nameof(HasQueueItems));
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(PendingQueueCount));
            NotifyQueueCommands();
        }

        private void ClearQueue()
        {
            if (!_queue.Any()) return;

            var count = _queue.Count;
            _queue.Clear();
            SaveQueueToFile();
            AddLog($"Cleared {count} items from queue");

            OnPropertyChanged(nameof(HasQueueItems));
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(PendingQueueCount));
            NotifyQueueCommands();
        }

        private void CancelQueue()
        {
            _queueCancellationTokenSource?.Cancel();
            foreach (var item in _queue.Where(q => q.Status == "Pending"))
                item.Status = "Cancelled";
            AddLog("Queue cancellation requested");
        }

        private async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;
            if (!_queue.Any(q => q.Status == "Pending")) return;

            IsProcessingQueue = true;

            try
            {
                _queueCancellationTokenSource?.Dispose();
                _queueCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
                IsWaitingForLease = true;
                AddLog("Starting queue processing...");
                AddLog("Waiting for other workflows to finish...");

                WorkflowQueueCoordinator.WorkflowLease lease;
                try
                {
                    lease = await _workflowCoordinator.AcquireAsync("AmateurGenerator", _queueCancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    AddLog("Queue processing cancelled while waiting for lease");
                    return;
                }
                catch (Exception ex)
                {
                    AddLog($"Error acquiring workflow lease: {ex.Message}");
                    return;
                }

                AddLog("=== Starting queue processing ===");
                IsWaitingForLease = false;

                using (lease)
                try
                {
                    AmateurQueueItem? queueItem;
                    while ((queueItem = _queue.FirstOrDefault(q => q.Status == "Pending")) != null)
                    {
                        if (_queueCancellationTokenSource?.Token.IsCancellationRequested == true)
                        {
                            AddLog("Queue processing cancelled");
                            break;
                        }

                        _pauseEvent.Wait(_queueCancellationTokenSource?.Token ?? CancellationToken.None);

                        try
                        {
                            queueItem.Status = "Processing";
                            queueItem.StartedAt = DateTime.Now;
                            queueItem.Progress = 0;
                            SaveQueueToFile();
                            OnPropertyChanged(nameof(PendingQueueCount));

                            AddLog($"Processing queue item: {queueItem.DisplayPrompt}");

                            await ProcessQueueItemAsync(queueItem);

                            queueItem.Status = "Completed";
                            queueItem.CompletedAt = DateTime.Now;
                            queueItem.Progress = 100;
                            SaveQueueToFile();
                            AddLog($"Completed queue item: {queueItem.DisplayPrompt}");
                        }
                        catch (OperationCanceledException)
                        {
                            queueItem.Status = "Cancelled";
                            queueItem.ErrorMessage = "Cancelled";
                            SaveQueueToFile();
                            AddLog($"Queue item cancelled: {queueItem.DisplayPrompt}");
                            break;
                        }
                        catch (Exception ex)
                        {
                            queueItem.Status = "Failed";
                            queueItem.ErrorMessage = ex.Message;
                            SaveQueueToFile();
                            AddLog($"ERROR processing queue item: {ex.Message}");
                        }
                        finally
                        {
                            OnPropertyChanged(nameof(CompletedQueueCount));
                        }
                    }

                    AddLog("=== Queue processing ended ===");
                }
                catch (Exception ex)
                {
                    AddLog($"Error in queue processing: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Unhandled error in queue processing: {ex.Message}");
                _logger.LogError($"Unhandled error in ProcessQueueAsync: {ex}");
            }
            finally
            {
                IsProcessingQueue = false;
                IsWaitingForLease = false;
                _pauseEvent.Set();
                _queueCancellationTokenSource?.Dispose();
                _queueCancellationTokenSource = null;
                NotifyQueueCommands();
            }
        }

        private async Task ProcessQueueItemAsync(AmateurQueueItem queueItem)
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = _queueCancellationTokenSource != null
                ? CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken, _queueCancellationTokenSource.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                IsProcessing = true;

                HasResultImage = false;
                ResultImageSource = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"Prompt: {queueItem.Prompt}");

                // Ensure ComfyUI is connected
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    AddLog("Connecting to ComfyUI WebSocket...");
                    await _comfyUIService.ConnectAsync(_cancellationTokenSource.Token);
                    AddLog("Connected to ComfyUI");
                }
                else
                {
                    AddLog("ComfyUI already connected");
                }

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Load workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "amateurZimageAPI.json");
                if (!File.Exists(workflowPath))
                {
                    throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
                }

                AddLog($"Loading workflow: {workflowPath}");
                var workflowJson = await File.ReadAllTextAsync(workflowPath, _cancellationTokenSource.Token);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 10;
                queueItem.Progress = 10;

                var updatedWorkflow = UpdateWorkflowParameters(workflow, queueItem);

                ProcessingStatus = "Generating image...";
                ProcessingProgress = 30;
                queueItem.Progress = 30;
                AddLog("Executing workflow in ComfyUI...");

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 30 + (percent * 0.6);
                            queueItem.Progress = ProcessingProgress;
                            ProcessingStatus = $"Generating: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, _cancellationTokenSource.Token);

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    queueItem.Progress = 90;
                    ProcessingStatus = "Workflow completed, retrieving output...";
                });

                AddLog($"Workflow execution completed with prompt ID: {promptId}");

                ProcessingStatus = "Retrieving output image...";
                ProcessingProgress = 95;
                AddLog("Looking for generated image...");

                var outputImages = await _imageRetriever.GetOutputImagesAsync(
                    _comfyUIService.HttpClient,
                    _settingsService,
                    _logger,
                    AddLog,
                    specificFolder: "ZImage",
                    expectedPattern: "AmateurImage",
                    promptId: promptId,
                    maxRetries: 20,
                    retryDelayMs: 5000,
                    ct: _cancellationTokenSource.Token);

                if (outputImages.Any())
                {
                    var outputImage = outputImages.First();
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "amateur-generator");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var outputPath = Path.Combine(outputDir, $"amateur_{timestamp}.png");

                    await File.WriteAllBytesAsync(outputPath, outputImage);
                    await LocalCopyService.CopyImageAsync(outputPath);
                    AddLog($"Output saved: {outputPath}");

                    queueItem.OutputImagePath = outputPath;
                    ResultImagePath = outputPath;
                    LoadResultPreview(outputPath);
                    HasResultImage = true;

                    ProcessingProgress = 100;
                    queueItem.Progress = 100;
                    ProcessingStatus = "Complete!";
                    AddLog("Image generation complete!");
                }
                else
                {
                    AddLog("WARNING: No output images received after all retries");
                    throw new Exception("No output images were generated. Please check the ComfyUI console for errors.");
                }
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, AmateurQueueItem queueItem)
        {
            var workflowJson = workflow.GetRawText();

            // Build the full prompt with photographer prefix (duplicated as requested)
            const string photographerPrefix = "A photo taken by the photographer Deedeemegadoodo, raw, unedited, ";
            string styleSuffix = GetStyleSuffix(queueItem.StyleIndex);
            string fullPrompt = photographerPrefix + photographerPrefix + queueItem.Prompt + styleSuffix;

            // 1. Update positive prompt (node 6)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "6", "text", fullPrompt);

            // 2. Update negative prompt (node 7)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "7", "text", "");

            // 3. Update seed (node 28) - max value is 2^50
            long maxSeed = 1125899906842624;
            var actualSeed = queueItem.Seed == 0 ? _random.NextInt64(0, maxSeed) : queueItem.Seed;
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "28", "seed", actualSeed);

            AddLog($"Using seed: {actualSeed}");

            // 4-7. Update KSampler settings
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "582", new Dictionary<string, object>
            {
                { "denoise", 0.5 },
                { "steps", queueItem.Steps },
                { "cfg", queueItem.Cfg }
            });

            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "620", new Dictionary<string, object>
            {
                { "denoise", 0.3 },
                { "steps", queueItem.Steps },
                { "cfg", queueItem.Cfg }
            });

            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "754", new Dictionary<string, object>
            {
                { "denoise", 0.9 },
                { "steps", queueItem.Steps },
                { "cfg", queueItem.Cfg }
            });

            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "768", new Dictionary<string, object>
            {
                { "denoise", 1.0 },
                { "steps", queueItem.Steps },
                { "cfg", queueItem.Cfg }
            });

            // 8. Update Amateur LoRA strengths (always applied)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "105", "strength_model", AmateurLoraStrength1);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "752", "strength_model", AmateurLoraStrength2);

            // 9. Set fallback LoRA for node 760 (prevents invalid LoRA errors)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "760", new Dictionary<string, object>
            {
                { "lora_name", $"zimage\\{AmateurLoraName}" },
                { "strength_model", 0.0 }
            });

            // 10. Remove metadata/watermark nodes
            AddLog("Removing metadata and watermark nodes");
            RemoveNodesFromWorkflow(ref workflowJson, new[] { "107", "109", "747", "748", "749", "751" });

            // 11. Update latent image dimensions based on orientation
            AddLog($"Setting orientation: {queueItem.OrientationIndex} (0=Land, 1=Port)");

            if (queueItem.OrientationIndex == 1)  // Portrait
            {
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "46", new Dictionary<string, object> { { "width", 416 }, { "height", 576 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "693", new Dictionary<string, object> { { "width", 208 }, { "height", 288 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "758", new Dictionary<string, object> { { "width", 416 }, { "height", 576 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "772", new Dictionary<string, object> { { "width", 1248 }, { "height", 1728 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "618", new Dictionary<string, object> { { "width", 1248 }, { "height", 1728 } });
                AddLog("Portrait dimensions: 416x576");
            }
            else  // Landscape (default)
            {
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "46", new Dictionary<string, object> { { "width", 576 }, { "height", 416 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "693", new Dictionary<string, object> { { "width", 288 }, { "height", 208 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "758", new Dictionary<string, object> { { "width", 288 }, { "height", 208 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "772", new Dictionary<string, object> { { "width", 1728 }, { "height", 1248 } });
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "618", new Dictionary<string, object> { { "width", 1728 }, { "height", 1248 } });
                AddLog("Landscape dimensions: 576x416");
            }

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        private string GetStyleSuffix(int styleIndex)
        {
            return styleIndex switch
            {
                0 => "", // Natural
                1 => ", cinematic lighting, dramatic shadows, professional photography",
                2 => ", dramatic lighting, high contrast, moody atmosphere",
                3 => ", vintage film look, grain, faded colors, nostalgic",
                4 => ", modern aesthetic, clean lines, vibrant colors",
                _ => ""
            };
        }

        private void RemoveNodesFromWorkflow(ref string workflowJson, string[] nodeIds)
        {
            try
            {
                var workflow = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson);
                if (workflow != null)
                {
                    foreach (var nodeId in nodeIds)
                    {
                        if (workflow.ContainsKey(nodeId))
                        {
                            workflow.Remove(nodeId);
                            AddLog($"Removed node {nodeId} from workflow");
                        }
                    }
                    workflowJson = JsonSerializer.Serialize(workflow, new JsonSerializerOptions { WriteIndented = false });
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR removing nodes from workflow: {ex.Message}");
            }
        }

        private async Task BrowseImageAsync()
        {
            try
            {
                if (_fileDialogService == null)
                {
                    AddLog("File dialog service not available");
                    return;
                }

                var path = await _fileDialogService.OpenFileDialogAsync(
                    "Select Image for Analysis",
                    "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|All Files|*.*");

                if (string.IsNullOrEmpty(path)) return;

                SourceImagePath = path;
                LoadSourceImagePreview(path);
                HasSourceImage = true;
                AddLog($"Image selected: {Path.GetFileName(path)}");

                if (_lmStudioService != null)
                {
                    await AnalyzeImageAndEnhancePromptAsync();
                }
                else
                {
                    AddLog("LM Studio not configured - image loaded but not analyzed");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR selecting image: {ex.Message}");
            }
        }

        private async Task AnalyzeImageAndEnhancePromptAsync()
        {
            if (!HasSourceImage || string.IsNullOrEmpty(SourceImagePath))
            {
                AddLog("No image selected for analysis");
                return;
            }

            if (_lmStudioService == null)
            {
                AddLog("LM Studio service not available");
                return;
            }

            if (IsAnalyzingImage) return;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
            try
            {
                IsAnalyzingImage = true;
                AddLog($"Analyzing image: {Path.GetFileName(SourceImagePath)}");
                AddLog("Sending to LM Studio for analysis and prompt enhancement...");

                const string systemPrompt =
                    "You are an expert AI image prompt engineer specializing in photorealistic photography. " +
                    "Analyze the provided image and generate a detailed prompt for creating a spectacular, highly realistic photograph based on its content.\n\n" +
                    "Include:\n" +
                    "- The main subject with precise visual detail (appearance, clothing, expression, pose, action)\n" +
                    "- Environment and background elements\n" +
                    "- Photographic quality descriptors (sharp focus, high detail, professional photography)\n" +
                    "- Lighting details (golden hour, soft diffused light, studio lighting, rim light)\n" +
                    "- Camera characteristics (85mm portrait lens, shallow depth of field, bokeh)\n\n" +
                    "CRITICAL: Return ONLY the prompt text as a single paragraph. No labels, no explanations, no preamble.";

                const string userPrompt = "Generate a spectacular realistic photo prompt from this image.";

                var modelName = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? "local-model";

                var enhancedPrompt = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    modelName,
                    SourceImagePath,
                    userPrompt,
                    systemPrompt,
                    maxTokens: 2000,
                    cts.Token);

                if (!string.IsNullOrWhiteSpace(enhancedPrompt))
                {
                    AdditionalPrompt = enhancedPrompt.Trim();
                    AddLog("Image analyzed — prompt enhanced and ready!");
                    var preview = enhancedPrompt.Length > 120 ? enhancedPrompt.Substring(0, 120) + "..." : enhancedPrompt;
                    AddLog($"Prompt: {preview}");
                }
                else
                {
                    AddLog("WARNING: Analysis returned empty result");
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("Image analysis cancelled");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR analyzing image: {ex.Message}");
            }
            finally
            {
                IsAnalyzingImage = false;
                cts.Dispose();
            }
        }

        private void LoadSourceImagePreview(string imagePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                SourceImageSource = bitmap;
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading image preview: {ex.Message}");
            }
        }

        private void PasteFromClipboard()
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    var clipboardText = System.Windows.Clipboard.GetText();
                    if (!string.IsNullOrEmpty(clipboardText))
                    {
                        AdditionalPrompt = clipboardText;
                        AddLog("Pasted content from clipboard");
                    }
                }
                else
                {
                    AddLog("Clipboard is empty or does not contain text");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR pasting from clipboard: {ex.Message}");
            }
        }

        private void CancelGeneration()
        {
            _cancellationTokenSource?.Cancel();
            AddLog("Cancellation requested by user");
            ProcessingStatus = "Cancelling...";
        }

        private void OpenResultFolder()
        {
            try
            {
                var folder = Path.GetDirectoryName(ResultImagePath);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = folder,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR opening result folder: {ex.Message}");
            }
        }

        private void OpenResultImage()
        {
            try
            {
                if (!string.IsNullOrEmpty(ResultImagePath) && File.Exists(ResultImagePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = ResultImagePath,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR opening result image: {ex.Message}");
            }
        }

        private void LoadResultPreview(string imagePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                ResultImageSource = bitmap;

                var fileInfo = new FileInfo(imagePath);
                ImageInfo = $"Size: {fileInfo.Length / 1024}KB | {bitmap.PixelWidth}x{bitmap.PixelHeight}";

                AddLog("Result image preview loaded");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading result preview: {ex.Message}");
            }
        }

        private string QueueFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix", "queue", "amateur_generator_queue.json");

        private void SaveQueueToFile()
        {
            try
            {
                var queueDir = Path.GetDirectoryName(QueueFilePath);
                if (!string.IsNullOrEmpty(queueDir) && !Directory.Exists(queueDir))
                {
                    Directory.CreateDirectory(queueDir);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_queue.ToList(), options);
                File.WriteAllText(QueueFilePath, json);
            }
            catch (Exception ex)
            {
                AddLog($"Error saving queue to file: {ex.Message}");
            }
        }

        private void LoadQueueFromFile()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;

                var json = File.ReadAllText(QueueFilePath);
                var savedItems = JsonSerializer.Deserialize<List<AmateurQueueItem>>(json);

                if (savedItems != null && savedItems.Any())
                {
                    _queue.Clear();
                    foreach (var item in savedItems)
                    {
                        if (item.Status == "Processing")
                        {
                            item.Status = "Failed";
                            item.ErrorMessage = "Interrupted by crash or app restart";
                        }
                        _queue.Add(item);
                    }
                    OnPropertyChanged(nameof(HasQueueItems));
                    OnPropertyChanged(nameof(QueueCount));
                    OnPropertyChanged(nameof(PendingQueueCount));
                    AddLog($"Queue loaded from file: {_queue.Count} items");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error loading queue from file: {ex.Message}");
            }
        }

        private void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogOutput += $"[{timestamp}] {message}\n";
            _logger.LogInfo(message);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _queueCancellationTokenSource?.Cancel();
                _queueCancellationTokenSource?.Dispose();
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
                _pauseEvent?.Dispose();

                _queue.Clear();
                _orientations.Clear();
                _styles.Clear();

                _additionalPrompt = string.Empty;
                _processingStatus = string.Empty;
                _logOutput = string.Empty;
                _resultImagePath = string.Empty;
                _imageInfo = string.Empty;

                _disposed = true;
            }
        }

        // RelayCommand class
        public class RelayCommand : ICommand
        {
            private readonly Action _execute;
            private readonly Func<bool>? _canExecute;

            public RelayCommand(Action execute, Func<bool>? canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public event EventHandler? CanExecuteChanged
            {
                add => CommandManager.RequerySuggested += value;
                remove => CommandManager.RequerySuggested -= value;
            }

            public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

            public void Execute(object? parameter) => _execute();

            public void NotifyCanExecuteChanged()
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public class RelayCommand<T> : ICommand
        {
            private readonly Action<T?> _execute;
            private readonly Func<T?, bool>? _canExecute;

            public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public event EventHandler? CanExecuteChanged
            {
                add => CommandManager.RequerySuggested += value;
                remove => CommandManager.RequerySuggested -= value;
            }

            public bool CanExecute(object? parameter) => _canExecute?.Invoke((T?)parameter) ?? true;

            public void Execute(object? parameter) => _execute((T?)parameter);
        }
    }
}
