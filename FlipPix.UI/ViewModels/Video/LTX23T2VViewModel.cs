using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ViewModel for LTX 2.3 text-to-video generation.
    /// Supports a pure text prompt workflow with configurable duration/resolution and a simple queue.
    /// </summary>
    public partial class LTX23T2VViewModel : VideoProcessingBaseViewModel
    {
        private string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "ltx23t2v_queue.json");

        private string _prompt = string.Empty;
        private int _length = 10;
        private int _width = 1920;
        private int _height = 1080;
        private long _seed = -1;
        private bool _isProcessingQueue = false;
        private string _queueStatus = string.Empty;
        private readonly ObservableCollection<QueueItem> _queue = new();
        private static readonly Random _rng = new();
        private CancellationTokenSource? _queueCts;

        public LTX23T2VViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            GenerateVideoCommand = new RelayCommand(AddToQueueAndProcess, () => CanAddToQueue);
            RemoveQueueItemCommand = new RelayCommand<QueueItem>(RemoveQueueItem);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);

            _queue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
                OnCanExecuteChanged();
            };

            AddLog("LTX 2.3 Text-to-Video initialized");
            LoadQueueFromFile();
        }

        #region Commands

        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand<QueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }

        #endregion

        #region Input Properties

        public string Prompt
        {
            get => _prompt;
            set
            {
                if (_prompt != value)
                {
                    _prompt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanAddToQueue));
                    OnCanExecuteChanged();
                }
            }
        }

        public int Length
        {
            get => _length;
            set { if (_length != value) { _length = value; OnPropertyChanged(); } }
        }

        public int Width
        {
            get => _width;
            set { if (_width != value) { _width = value; OnPropertyChanged(); } }
        }

        public int Height
        {
            get => _height;
            set { if (_height != value) { _height = value; OnPropertyChanged(); } }
        }

        public long Seed
        {
            get => _seed;
            set { if (_seed != value) { _seed = value; OnPropertyChanged(); } }
        }

        public bool CanAddToQueue => !string.IsNullOrWhiteSpace(Prompt);

        #endregion

        #region Queue Properties

        public ObservableCollection<QueueItem> Queue => _queue;

        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            private set
            {
                if (_isProcessingQueue != value)
                {
                    _isProcessingQueue = value;
                    OnPropertyChanged();
                    OnCanExecuteChanged();
                }
            }
        }

        public string QueueStatus
        {
            get => _queueStatus;
            private set { if (_queueStatus != value) { _queueStatus = value; OnPropertyChanged(); } }
        }

        public bool HasQueueItems => _queue.Any();

        #endregion

        #region Queue Management

        private void AddToQueueAndProcess()
        {
            if (!CanAddToQueue) return;

            var effectiveSeed = Seed >= 0 ? Seed : (long)(_rng.NextDouble() * long.MaxValue);

            var item = new QueueItem
            {
                Prompt = Prompt,
                Seed = effectiveSeed,
                ItemStatus = QueueItemStatus.Pending
            };

            _queue.Add(item);
            SaveQueueToFile();
            AddLog($"Added to queue: \"{Prompt.Substring(0, Math.Min(60, Prompt.Length))}\"");
            UpdateQueueStatus();

            if (!IsProcessingQueue)
                _ = ProcessQueueAsync();
        }

        private void RemoveQueueItem(QueueItem? item)
        {
            if (item != null && item.ItemStatus != QueueItemStatus.Processing)
            {
                _queue.Remove(item);
                UpdateQueueStatus();
            }
        }

        private void UpdateQueueStatus()
        {
            var pending = _queue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
            var completed = _queue.Count(x => x.ItemStatus == QueueItemStatus.Completed);
            var failed = _queue.Count(x => x.ItemStatus == QueueItemStatus.Failed);
            var total = _queue.Count;

            QueueStatus = total == 0
                ? string.Empty
                : $"{pending} pending • {completed} done • {failed} failed";
        }

        private async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;

            IsProcessingQueue = true;
            _queueCts?.Dispose();
            _queueCts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
            var token = _queueCts.Token;
            AddLog("Waiting for other workflows to finish...");

            WorkflowQueueCoordinator.WorkflowLease lease;
            try
            {
                lease = await _workflowCoordinator.AcquireAsync("LTX23T2V", token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue processing cancelled while waiting");
                IsProcessingQueue = false;
                return;
            }

            AddLog("Starting queue processing...");
            using (lease)
            try
            {
                QueueItem? item;
                while (!token.IsCancellationRequested &&
                       (item = _queue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                {
                    item.ItemStatus = QueueItemStatus.Processing;
                    UpdateQueueStatus();
                    SaveQueueToFile();
                    try
                    {
                        await GenerateSingleVideoAsync(item);
                        item.ItemStatus = QueueItemStatus.Completed;
                        AddLog("Queue item completed.");
                    }
                    catch (OperationCanceledException)
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
                        AddLog("Queue item cancelled — reset to Pending");
                        break;
                    }
                    catch (Exception ex)
                    {
                        var shouldRetry = await TryHandleCrashAndRetryAsync(item, ex);
                        if (shouldRetry)
                        {
                            item.ItemStatus = QueueItemStatus.Pending;
                            AddLog("Item reset to Pending — will retry after ComfyUI restart");
                        }
                        else
                        {
                            item.ItemStatus = QueueItemStatus.Failed;
                            item.ErrorMessage = ex.Message;
                            AddLog($"Queue item FAILED: {ex.Message}");
                        }
                    }
                    UpdateQueueStatus();
                    SaveQueueToFile();
                }
            }
            finally
            {
                IsProcessingQueue = false;
                AddLog("Queue processing finished.");
            }
        }

        #endregion

        #region Queue Persistence

        private void SaveQueueToFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(QueueFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(QueueFilePath,
                    JsonSerializer.Serialize(_queue.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AddLog($"Error saving queue: {ex.Message}"); }
        }

        private void LoadQueueFromFile()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;
                var items = JsonSerializer.Deserialize<List<QueueItem>>(File.ReadAllText(QueueFilePath));
                if (items?.Any() != true) return;
                _queue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Processing)
                        item.ItemStatus = QueueItemStatus.Pending;
                    _queue.Add(item);
                }
                UpdateQueueStatus();
                AddLog($"Queue loaded: {_queue.Count} items");
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        #endregion

        #region Video Generation

        private async Task GenerateSingleVideoAsync(QueueItem item)
        {
            try
            {
                var preview = item.Prompt.Length > 80 ? item.Prompt.Substring(0, 80) + "…" : item.Prompt;
                AddLog($"=== Generating LTX 2.3 T2V: \"{preview}\" ===");
                IsProcessing = true;
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";

                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddLog($"[Auto-Restart] {status}"));

                if (!comfyUIOk)
                    throw new Exception("ComfyUI is not running. Please start it manually.");

                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                    AddLog("Connected to ComfyUI");
                }

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "workflow", "LTX-2.3T2VGGUFAPI.json");

                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow not found: {workflowPath}");

                AddLog("Loading workflow...");
                var rawJson = await File.ReadAllTextAsync(workflowPath);

                // Patch workflow nodes
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "121", "text", item.Prompt);
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "291", "value", Length);
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "292", "value", Width);
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "293", "value", Height);
                WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "115", "noise_seed", item.Seed);

                var updatedWorkflow = JsonSerializer.Deserialize<JsonElement>(rawJson);

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 10 + pct * 0.85;
                            ProcessingStatus = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                        });
                    }
                });

                ProcessingStatus = "Executing workflow...";
                ProcessingProgress = 10;

                var generationStart = DateTime.Now.AddSeconds(-2);
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                AddLog($"Workflow submitted (prompt ID: {promptId})");

                ProcessingProgress = 20;
                ProcessingStatus = "Waiting for output video...";

                var outputVideo = await WaitForVideoWithCrashRecoveryAsync(
                    generationStart, "LTX-2*.mp4",
                    TimeSpan.FromMinutes(25), TimeSpan.FromSeconds(5),
                    updatedWorkflow, progress);

                if (outputVideo == null || !File.Exists(outputVideo))
                    throw new Exception("No output video was produced within the timeout.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "LTX23T2V");
                Directory.CreateDirectory(outputDir);

                var outName = $"T2V_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
                var finalPath = Path.Combine(outputDir, outName);
                File.Copy(outputVideo, finalPath, true);
                AddLog($"Video saved: {finalPath}");

                ResultVideoPath = finalPath;
                await LocalCopyService.CopyVideoAsync(finalPath);
                HasResult = true;
                item.OutputImagePath = finalPath;

                var fileInfo = new FileInfo(finalPath);
                ResultVideoInfo = $"LTX 2.3 T2V • {Length}s • {Width}×{Height} • {fileInfo.Length / 1024.0 / 1024.0:F1} MB";

                ProcessingProgress = 100;
                ProcessingStatus = "Complete!";
                AddLog("=== T2V generation complete ===");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        /// <summary>
        /// Waits for an output video, periodically checking ComfyUI health.
        /// On crash detection: restarts ComfyUI, reconnects, re-submits the workflow, and continues waiting.
        /// </summary>
        private async Task<string?> WaitForVideoWithCrashRecoveryAsync(
            DateTime after, string filePattern,
            TimeSpan maxWait, TimeSpan checkInterval,
            JsonElement workflow, IProgress<FlipPix.ComfyUI.Models.ProgressMessage> progress)
        {
            var settings = _settingsService.Settings;
            if (settings == null) { AddLog("ERROR: Settings not available"); return null; }

            var baseUrl = GetComfyUIBaseUrl();
            var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
            var outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;

            if (string.IsNullOrEmpty(outputFolder))
            {
                AddLog("ERROR: Output folder not configured in settings");
                return null;
            }

            AddLog($"Monitoring: {outputFolder}  (files newer than {after:HH:mm:ss})");

            var deadline = DateTime.Now + maxWait;
            int iteration = 0;
            bool comfyWasDown = false;
            // Health check every ~30s (6 iterations × 5s interval)
            const int healthCheckEvery = 6;

            while (DateTime.Now < deadline)
            {
                await Task.Delay(checkInterval);
                iteration++;

                // Periodic health check
                if (iteration % healthCheckEvery == 0)
                {
                    bool restartOccurred = false;
                    var isUp = await _comfyUIService.DetectAndRestartIfCrashedAsync(status =>
                    {
                        AddLog($"[Health] {status}");
                        // DetectAndRestart can complete a full crash+restart in one call,
                        // returning true immediately. Detect this via the status string.
                        if (status.IndexOf("restarted", StringComparison.OrdinalIgnoreCase) >= 0)
                            restartOccurred = true;
                    });

                    if (!isUp)
                    {
                        // Restart failed or ComfyUI is still coming up — keep looping
                        comfyWasDown = true;
                        ProcessingStatus = "ComfyUI down — waiting for restart...";
                        AddLog("ComfyUI is not reachable. Waiting for it to recover...");
                        continue;
                    }

                    bool needsResubmit = comfyWasDown || restartOccurred;
                    comfyWasDown = false;

                    if (needsResubmit)
                    {
                        AddLog("ComfyUI recovered. Reconnecting and re-submitting...");
                        ProcessingStatus = "ComfyUI recovered — reconnecting...";

                        if (!_comfyUIService.IsConnected)
                            await _comfyUIService.ConnectAsync();

                        // Reset file timestamp and deadline for the fresh job
                        after = DateTime.Now.AddSeconds(-2);
                        deadline = DateTime.Now + maxWait;

                        AddLog("Re-submitting workflow after crash recovery...");
                        ProcessingStatus = "Re-submitting workflow...";
                        ProcessingProgress = 10;

                        var newId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress);
                        AddLog($"Workflow re-submitted (prompt ID: {newId})");

                        ProcessingStatus = "Waiting for output video...";
                        ProcessingProgress = 20;
                        iteration = 0;
                        continue;
                    }
                }

                if (!Directory.Exists(outputFolder)) continue;

                var candidate = Directory.GetFiles(outputFolder, filePattern)
                    .Where(f => File.GetLastWriteTime(f) > after)
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .FirstOrDefault();

                if (candidate != null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    var fi = new FileInfo(candidate);
                    AddLog($"Found: {fi.Name} ({fi.Length / 1024.0 / 1024.0:F2} MB)");
                    return candidate;
                }

                // Log progress every ~60s to avoid log spam
                if (iteration % 12 == 0)
                {
                    var remaining = (int)(deadline - DateTime.Now).TotalSeconds;
                    AddLog($"Still waiting... ({remaining / 60}m {remaining % 60}s remaining)");
                }
            }

            AddLog("ERROR: Timeout waiting for output video");
            return null;
        }

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            GenerateVideoCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
        }
    }
}
