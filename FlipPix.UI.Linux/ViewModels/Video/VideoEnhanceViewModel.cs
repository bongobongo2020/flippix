using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Linux.Models;
using FlipPix.UI.Linux.Services;

namespace FlipPix.UI.Linux.ViewModels.Video
{
    public partial class VideoEnhanceViewModel : VideoProcessingBaseViewModel
    {
        private const string InterpolateOutputSubfolder = "AnimateDiff";
        private const string UpscaleOutputSubfolder = "upscale";

        // ── RTX graph (workflow/upscale nvidaAPI.json) ────────────────────────────────────────
        // VHS_LoadVideo → RTXVideoSuperResolution → VHS_VideoCombine, with a VHS meta batch so a long
        // clip is upscaled 29 frames at a time instead of holding every frame of the 2–4× result in RAM.
        // The meta batch is why the tab cannot just wait on its own prompt — see WaitForMetaBatchOutputAsync.
        private const string RtxUpscaleWorkflow = "workflow/upscale nvidaAPI.json";
        private const string RtxUpscaleVideoNode = "2";      // VHS_LoadVideo
        private const string RtxUpscaleNode = "1";           // RTXVideoSuperResolution — the scale factor
        private const string RtxCombineNode = "3";           // VHS_VideoCombine — fps + audio come off node 2

        /// <summary>The multiplier the graph ships with, and what the tab offers first.</summary>
        private const double DefaultRtxScale = 2.0;

        /// <summary>Selectable RTX multipliers. The node itself accepts 1.0–4.0.</summary>
        public static IReadOnlyList<double> RtxScales { get; } = new[] { 1.5, 2.0, 3.0, 4.0 };

        private string InterpolateQueueFilePath => UserPaths.Queue("video_enhance_interpolate_queue.json");

        private string UpscaleQueueFilePath => UserPaths.Queue("video_enhance_upscale_queue.json");

        // Interpolate state
        private string _interpolateVideoPath = string.Empty;
        private string _interpolateVideoInfo = string.Empty;
        private bool _isProcessingInterpolateQueue = false;
        private string _interpolateQueueStatus = string.Empty;
        private readonly ObservableCollection<VideoEnhanceQueueItem> _interpolateQueue = new();
        private CancellationTokenSource? _interpolateCts;

        // Upscale state
        private string _upscaleVideoPath = string.Empty;
        private string _upscaleVideoInfo = string.Empty;
        private double _rtxScale = DefaultRtxScale;
        private bool _isProcessingUpscaleQueue = false;
        private string _upscaleQueueStatus = string.Empty;
        private readonly ObservableCollection<VideoEnhanceQueueItem> _upscaleQueue = new();
        private CancellationTokenSource? _upscaleCts;

        private readonly IFileDialogService _fileDialogService;

        public VideoEnhanceViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            SelectInterpolateVideoCommand = new RelayCommand(SelectInterpolateVideo);
            AddInterpolateToQueueCommand = new RelayCommand(AddInterpolateToQueue, () => CanAddInterpolate);
            RemoveInterpolateQueueItemCommand = new RelayCommand<VideoEnhanceQueueItem>(RemoveInterpolateQueueItem);
            ClearInterpolateQueueCommand = new RelayCommand(ClearInterpolateQueue, () => _interpolateQueue.Any());
            StopInterpolateQueueCommand = new RelayCommand(StopInterpolateQueue, () => IsProcessingInterpolateQueue);
            ReprocessInterpolateFailedCommand = new RelayCommand(async () => await ReprocessInterpolateFailedAsync(), () => HasInterpolateFailedItems);

            SelectUpscaleVideoCommand = new RelayCommand(SelectUpscaleVideo);
            AddUpscaleToQueueCommand = new RelayCommand(AddUpscaleToQueue, () => CanAddUpscale);
            RemoveUpscaleQueueItemCommand = new RelayCommand<VideoEnhanceQueueItem>(RemoveUpscaleQueueItem);
            ClearUpscaleQueueCommand = new RelayCommand(ClearUpscaleQueue, () => _upscaleQueue.Any());
            StopUpscaleQueueCommand = new RelayCommand(StopUpscaleQueue, () => IsProcessingUpscaleQueue);
            ReprocessUpscaleFailedCommand = new RelayCommand(async () => await ReprocessUpscaleFailedAsync(), () => HasUpscaleFailedItems);

            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);

            _interpolateQueue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasInterpolateQueueItems));
                UpdateInterpolateQueueStatus();
                OnCanExecuteChanged();
            };
            _upscaleQueue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasUpscaleQueueItems));
                UpdateUpscaleQueueStatus();
                OnCanExecuteChanged();
            };

            AddLog("Video Enhance initialized");
            LoadInterpolateQueueFromFile();
            LoadUpscaleQueueFromFile();
        }

        #region Commands

        public RelayCommand SelectInterpolateVideoCommand { get; }
        public RelayCommand AddInterpolateToQueueCommand { get; }
        public RelayCommand<VideoEnhanceQueueItem> RemoveInterpolateQueueItemCommand { get; }
        public RelayCommand ClearInterpolateQueueCommand { get; }
        public RelayCommand StopInterpolateQueueCommand { get; }
        public RelayCommand ReprocessInterpolateFailedCommand { get; }

        public RelayCommand SelectUpscaleVideoCommand { get; }
        public RelayCommand AddUpscaleToQueueCommand { get; }
        public RelayCommand<VideoEnhanceQueueItem> RemoveUpscaleQueueItemCommand { get; }
        public RelayCommand ClearUpscaleQueueCommand { get; }
        public RelayCommand StopUpscaleQueueCommand { get; }
        public RelayCommand ReprocessUpscaleFailedCommand { get; }

        public bool HasInterpolateFailedItems => _interpolateQueue.Any(x => x.ItemStatus == QueueItemStatus.Failed);
        public bool HasUpscaleFailedItems => _upscaleQueue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }

        #endregion

        #region Interpolate Properties

        public string InterpolateVideoPath
        {
            get => _interpolateVideoPath;
            set
            {
                if (_interpolateVideoPath != value)
                {
                    _interpolateVideoPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasInterpolateVideo));
                    OnPropertyChanged(nameof(CanAddInterpolate));
                    LoadInterpolateVideoInfo();
                    OnCanExecuteChanged();
                }
            }
        }

        public string InterpolateVideoInfo
        {
            get => _interpolateVideoInfo;
            set { if (_interpolateVideoInfo != value) { _interpolateVideoInfo = value; OnPropertyChanged(); } }
        }

        public bool HasInterpolateVideo => !string.IsNullOrEmpty(InterpolateVideoPath) && File.Exists(InterpolateVideoPath);
        public bool CanAddInterpolate => HasInterpolateVideo;

        public ObservableCollection<VideoEnhanceQueueItem> InterpolateQueue => _interpolateQueue;
        public bool HasInterpolateQueueItems => _interpolateQueue.Any();

        public bool IsProcessingInterpolateQueue
        {
            get => _isProcessingInterpolateQueue;
            private set
            {
                if (_isProcessingInterpolateQueue != value)
                {
                    _isProcessingInterpolateQueue = value;
                    OnPropertyChanged();
                    OnCanExecuteChanged();
                }
            }
        }

        public string InterpolateQueueStatus
        {
            get => _interpolateQueueStatus;
            private set { if (_interpolateQueueStatus != value) { _interpolateQueueStatus = value; OnPropertyChanged(); } }
        }

        #endregion

        #region Upscale Properties

        public string UpscaleVideoPath
        {
            get => _upscaleVideoPath;
            set
            {
                if (_upscaleVideoPath != value)
                {
                    _upscaleVideoPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasUpscaleVideo));
                    OnPropertyChanged(nameof(CanAddUpscale));
                    LoadUpscaleVideoInfo();
                    OnCanExecuteChanged();
                }
            }
        }

        public string UpscaleVideoInfo
        {
            get => _upscaleVideoInfo;
            set { if (_upscaleVideoInfo != value) { _upscaleVideoInfo = value; OnPropertyChanged(); } }
        }

        public bool HasUpscaleVideo => !string.IsNullOrEmpty(UpscaleVideoPath) && File.Exists(UpscaleVideoPath);
        public bool CanAddUpscale => HasUpscaleVideo;

        /// <summary>Upscale factor handed to the RTX node. Snapshotted onto the queue item, so changing it
        /// afterwards does not re-target jobs already waiting.</summary>
        public double RtxScale
        {
            get => _rtxScale;
            set
            {
                if (Math.Abs(_rtxScale - value) < 0.001) return;
                _rtxScale = value <= 0 ? DefaultRtxScale : value;
                OnPropertyChanged();
            }
        }

        public IReadOnlyList<double> RtxScaleOptions => RtxScales;

        public ObservableCollection<VideoEnhanceQueueItem> UpscaleQueue => _upscaleQueue;
        public bool HasUpscaleQueueItems => _upscaleQueue.Any();

        public bool IsProcessingUpscaleQueue
        {
            get => _isProcessingUpscaleQueue;
            private set
            {
                if (_isProcessingUpscaleQueue != value)
                {
                    _isProcessingUpscaleQueue = value;
                    OnPropertyChanged();
                    OnCanExecuteChanged();
                }
            }
        }

        public string UpscaleQueueStatus
        {
            get => _upscaleQueueStatus;
            private set { if (_upscaleQueueStatus != value) { _upscaleQueueStatus = value; OnPropertyChanged(); } }
        }

        #endregion

        #region File Selection

        private async void SelectInterpolateVideo()
        {
            var initial = _settingsService.Settings?.EnhanceVideoFolder;
            if (string.IsNullOrEmpty(initial) || !Directory.Exists(initial))
                initial = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Video to Interpolate",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv|All Files|*.*",
                initial);

            if (path != null)
            {
                InterpolateVideoPath = path;
                PersistBrowseFolder(Path.GetDirectoryName(path));
                AddLog($"Interpolate: selected {Path.GetFileName(path)}");
            }
        }

        private async void SelectUpscaleVideo()
        {
            var initial = _settingsService.Settings?.EnhanceVideoFolder;
            if (string.IsNullOrEmpty(initial) || !Directory.Exists(initial))
                initial = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Video to Upscale",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv|All Files|*.*",
                initial);

            if (path != null)
            {
                UpscaleVideoPath = path;
                PersistBrowseFolder(Path.GetDirectoryName(path));
                AddLog($"Upscale: selected {Path.GetFileName(path)}");
            }
        }

        private void PersistBrowseFolder(string? folder)
        {
            if (string.IsNullOrEmpty(folder) || _settingsService.Settings == null) return;
            _settingsService.Settings.EnhanceVideoFolder = folder;
            _settingsService.SaveSettings(_settingsService.Settings);
        }

        private void LoadInterpolateVideoInfo()
        {
            if (!HasInterpolateVideo) { InterpolateVideoInfo = string.Empty; return; }
            try
            {
                var fi = new FileInfo(InterpolateVideoPath);
                InterpolateVideoInfo = $"{fi.Name} • {fi.Length / 1024.0 / 1024.0:F1} MB";
            }
            catch { InterpolateVideoInfo = string.Empty; }
        }

        private void LoadUpscaleVideoInfo()
        {
            if (!HasUpscaleVideo) { UpscaleVideoInfo = string.Empty; return; }
            try
            {
                var fi = new FileInfo(UpscaleVideoPath);
                UpscaleVideoInfo = $"{fi.Name} • {fi.Length / 1024.0 / 1024.0:F1} MB";
            }
            catch { UpscaleVideoInfo = string.Empty; }
        }

        #endregion

        #region Interpolate Queue

        private void AddInterpolateToQueue()
        {
            if (!CanAddInterpolate) return;

            var item = new VideoEnhanceQueueItem
            {
                InputVideoPath = InterpolateVideoPath,
                Mode = VideoEnhanceMode.Interpolate,
                ItemStatus = QueueItemStatus.Pending
            };

            _interpolateQueue.Add(item);
            SaveInterpolateQueueToFile();
            AddLog($"Added to interpolate queue: {item.DisplayText}");
            UpdateInterpolateQueueStatus();

            if (!IsProcessingInterpolateQueue)
                _ = ProcessInterpolateQueueAsync();
        }

        private void RemoveInterpolateQueueItem(VideoEnhanceQueueItem? item)
        {
            if (item != null && item.ItemStatus != QueueItemStatus.Processing)
                _interpolateQueue.Remove(item);
            UpdateInterpolateQueueStatus();
        }

        private void UpdateInterpolateQueueStatus()
        {
            var total = _interpolateQueue.Count;
            if (total == 0) { InterpolateQueueStatus = string.Empty; return; }
            var pending = _interpolateQueue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
            var done = _interpolateQueue.Count(x => x.ItemStatus == QueueItemStatus.Completed);
            var failed = _interpolateQueue.Count(x => x.ItemStatus == QueueItemStatus.Failed);
            InterpolateQueueStatus = $"{pending} pending • {done} done • {failed} failed";
            OnPropertyChanged(nameof(HasInterpolateFailedItems));
            OnCanExecuteChanged();
        }

        private void ClearInterpolateQueue()
        {
            _interpolateCts?.Cancel();
            foreach (var item in _interpolateQueue.ToList())
                _interpolateQueue.Remove(item);
            SaveInterpolateQueueToFile();
            UpdateInterpolateQueueStatus();
            AddLog("Interpolate queue cleared");
        }

        private void StopInterpolateQueue()
        {
            _interpolateCts?.Cancel();
            AddLog("Interpolate queue stop requested");
        }

        private async Task ReprocessInterpolateFailedAsync()
        {
            var failed = _interpolateQueue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (!failed.Any()) return;
            foreach (var item in failed)
                item.ItemStatus = QueueItemStatus.Pending;
            UpdateInterpolateQueueStatus();
            SaveInterpolateQueueToFile();
            AddLog($"Reprocessing {failed.Count} failed interpolate item(s)...");
            if (!IsProcessingInterpolateQueue)
                await ProcessInterpolateQueueAsync();
        }

        private async Task ProcessInterpolateQueueAsync()
        {
            if (IsProcessingInterpolateQueue) return;
            IsProcessingInterpolateQueue = true;
            _interpolateCts?.Dispose();
            _interpolateCts = new CancellationTokenSource();
            var token = _interpolateCts.Token;
            AddLog("Starting interpolate queue...");
            OnCanExecuteChanged();
            try
            {
                VideoEnhanceQueueItem? item;
                while (!token.IsCancellationRequested &&
                       (item = _interpolateQueue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                {
                    item.ItemStatus = QueueItemStatus.Processing;
                    UpdateInterpolateQueueStatus();
                    SaveInterpolateQueueToFile();
                    try
                    {
                        await ProcessInterpolateSingleAsync(item);
                        item.ItemStatus = QueueItemStatus.Completed;
                        AddLog($"Interpolate complete: {item.DisplayText}");
                    }
                    catch (OperationCanceledException)
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
                        AddLog("Interpolate queue item cancelled — reset to Pending");
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
                            AddLog($"Interpolate FAILED: {ex.Message}");
                        }
                    }
                    UpdateInterpolateQueueStatus();
                    SaveInterpolateQueueToFile();
                }
            }
            finally
            {
                IsProcessingInterpolateQueue = false;
                AddLog("Interpolate queue finished.");
                OnCanExecuteChanged();
            }
        }

        #endregion

        #region Upscale Queue

        private void AddUpscaleToQueue()
        {
            if (!CanAddUpscale) return;

            var item = new VideoEnhanceQueueItem
            {
                InputVideoPath = UpscaleVideoPath,
                Mode = VideoEnhanceMode.Upscale,
                UpscaleScale = RtxScale,
                ItemStatus = QueueItemStatus.Pending
            };

            _upscaleQueue.Add(item);
            SaveUpscaleQueueToFile();
            AddLog($"Added to upscale queue: {item.DisplayText}");
            UpdateUpscaleQueueStatus();

            if (!IsProcessingUpscaleQueue)
                _ = ProcessUpscaleQueueAsync();
        }

        private void RemoveUpscaleQueueItem(VideoEnhanceQueueItem? item)
        {
            if (item != null && item.ItemStatus != QueueItemStatus.Processing)
                _upscaleQueue.Remove(item);
            UpdateUpscaleQueueStatus();
        }

        private void UpdateUpscaleQueueStatus()
        {
            var total = _upscaleQueue.Count;
            if (total == 0) { UpscaleQueueStatus = string.Empty; return; }
            var pending = _upscaleQueue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
            var done = _upscaleQueue.Count(x => x.ItemStatus == QueueItemStatus.Completed);
            var failed = _upscaleQueue.Count(x => x.ItemStatus == QueueItemStatus.Failed);
            UpscaleQueueStatus = $"{pending} pending • {done} done • {failed} failed";
            OnPropertyChanged(nameof(HasUpscaleFailedItems));
            OnCanExecuteChanged();
        }

        private void ClearUpscaleQueue()
        {
            _upscaleCts?.Cancel();
            foreach (var item in _upscaleQueue.ToList())
                _upscaleQueue.Remove(item);
            SaveUpscaleQueueToFile();
            UpdateUpscaleQueueStatus();
            AddLog("Upscale queue cleared");
        }

        private void StopUpscaleQueue()
        {
            _upscaleCts?.Cancel();
            AddLog("Upscale queue stop requested");
        }

        private async Task ReprocessUpscaleFailedAsync()
        {
            var failed = _upscaleQueue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (!failed.Any()) return;
            foreach (var item in failed)
                item.ItemStatus = QueueItemStatus.Pending;
            UpdateUpscaleQueueStatus();
            SaveUpscaleQueueToFile();
            AddLog($"Reprocessing {failed.Count} failed upscale item(s)...");
            if (!IsProcessingUpscaleQueue)
                await ProcessUpscaleQueueAsync();
        }

        private async Task ProcessUpscaleQueueAsync()
        {
            if (IsProcessingUpscaleQueue) return;
            IsProcessingUpscaleQueue = true;
            _upscaleCts?.Dispose();
            _upscaleCts = new CancellationTokenSource();
            var token = _upscaleCts.Token;
            AddLog("Starting upscale queue...");
            OnCanExecuteChanged();
            try
            {
                VideoEnhanceQueueItem? item;
                while (!token.IsCancellationRequested &&
                       (item = _upscaleQueue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                {
                    item.ItemStatus = QueueItemStatus.Processing;
                    UpdateUpscaleQueueStatus();
                    SaveUpscaleQueueToFile();
                    try
                    {
                        await ProcessUpscaleSingleAsync(item);
                        item.ItemStatus = QueueItemStatus.Completed;
                        AddLog($"Upscale complete: {item.DisplayText}");
                    }
                    catch (OperationCanceledException)
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
                        AddLog("Upscale queue item cancelled — reset to Pending");
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
                            AddLog($"Upscale FAILED: {ex.Message}");
                        }
                    }
                    UpdateUpscaleQueueStatus();
                    SaveUpscaleQueueToFile();
                }
            }
            finally
            {
                IsProcessingUpscaleQueue = false;
                AddLog("Upscale queue finished.");
                OnCanExecuteChanged();
            }
        }

        #endregion

        #region Queue Persistence

        private void SaveInterpolateQueueToFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(InterpolateQueueFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(InterpolateQueueFilePath,
                    JsonSerializer.Serialize(_interpolateQueue.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AddLog($"Error saving interpolate queue: {ex.Message}"); }
        }

        private void LoadInterpolateQueueFromFile()
        {
            try
            {
                if (!File.Exists(InterpolateQueueFilePath)) return;
                var items = JsonSerializer.Deserialize<List<VideoEnhanceQueueItem>>(File.ReadAllText(InterpolateQueueFilePath));
                if (items?.Any() != true) return;
                _interpolateQueue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Processing)
                        item.ItemStatus = QueueItemStatus.Pending;
                    _interpolateQueue.Add(item);
                }
                UpdateInterpolateQueueStatus();
                AddLog($"Interpolate queue loaded: {_interpolateQueue.Count} items");
            }
            catch (Exception ex) { AddLog($"Error loading interpolate queue: {ex.Message}"); }
        }

        private void SaveUpscaleQueueToFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(UpscaleQueueFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(UpscaleQueueFilePath,
                    JsonSerializer.Serialize(_upscaleQueue.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AddLog($"Error saving upscale queue: {ex.Message}"); }
        }

        private void LoadUpscaleQueueFromFile()
        {
            try
            {
                if (!File.Exists(UpscaleQueueFilePath)) return;
                var items = JsonSerializer.Deserialize<List<VideoEnhanceQueueItem>>(File.ReadAllText(UpscaleQueueFilePath));
                if (items?.Any() != true) return;
                _upscaleQueue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Processing)
                        item.ItemStatus = QueueItemStatus.Pending;
                    _upscaleQueue.Add(item);
                }
                UpdateUpscaleQueueStatus();
                AddLog($"Upscale queue loaded: {_upscaleQueue.Count} items");
            }
            catch (Exception ex) { AddLog($"Error loading upscale queue: {ex.Message}"); }
        }

        #endregion

        #region Processing

        private async Task ProcessInterpolateSingleAsync(VideoEnhanceQueueItem item)
        {
            try
            {
                IsProcessing = true;
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing interpolation workflow...";
                AddLog($"=== Interpolating: {item.DisplayText} ===");

                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    s => AddLog($"[ComfyUI] {s}"));
                if (!comfyUIOk) throw new Exception("ComfyUI is not running.");

                if (!_comfyUIService.IsConnected)
                    await _comfyUIService.ConnectAsync();

                ProcessingProgress = 10;
                ProcessingStatus = "Uploading video...";
                if (!IsMp4Valid(item.InputVideoPath, out var interpolateValidationError))
                    throw new Exception($"Input video is not a valid MP4: {interpolateValidationError}\n\nPath: {item.InputVideoPath}\n\nRe-generate the source video and try again.");
                var uploadedName = await _comfyUIService.UploadVideoAsync(item.InputVideoPath);
                if (string.IsNullOrEmpty(uploadedName))
                    throw new Exception("Video upload failed.");
                AddLog($"Uploaded: {uploadedName}");

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "GIMM interpolationAPI.json");
                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow not found: {workflowPath}");

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "4", "video", uploadedName);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                ProcessingProgress = 20;
                ProcessingStatus = "Executing interpolation...";
                var existingFiles = GetExistingVideoFiles("*.mp4", InterpolateOutputSubfolder);

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(p =>
                {
                    if (p.Data?.Value != null && p.Data?.Max != null && p.Data.Max > 0)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 20 + (double)p.Data.Value / p.Data.Max * 70;
                            ProcessingStatus = $"Interpolating: {p.Data.Value}/{p.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress);
                AddLog($"Workflow submitted, ID: {promptId}");

                ProcessingProgress = 90;
                ProcessingStatus = "Waiting for output...";

                var outputVideo = await TryGetVideoFromHistoryAsync(promptId);
                if (outputVideo == null)
                {
                    AddLog("Falling back to filesystem polling...");
                    outputVideo = await WaitForNewVideoAsync(existingFiles, "*.mp4",
                        TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5), InterpolateOutputSubfolder);
                }

                if (outputVideo == null || !File.Exists(outputVideo))
                    throw new Exception("No output video found.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "Interpolate");
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"Interpolate_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                File.Copy(outputVideo, finalPath, true);

                item.OutputVideoPath = finalPath;
                ResultVideoPath = finalPath;
                await LocalCopyService.CopyVideoAsync(finalPath);
                HasResult = true;
                ResultVideoInfo = $"Interpolated • {new FileInfo(finalPath).Length / 1024.0 / 1024.0:F1} MB";
                ProcessingProgress = 100;
                ProcessingStatus = "Interpolation complete!";
                AddLog($"=== Done: {finalPath} ===");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private async Task ProcessUpscaleSingleAsync(VideoEnhanceQueueItem item)
        {
            try
            {
                IsProcessing = true;
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing upscale workflow...";
                AddLog($"=== Upscaling: {item.DisplayText} ===");

                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    s => AddLog($"[ComfyUI] {s}"));
                if (!comfyUIOk) throw new Exception("ComfyUI is not running.");

                if (!_comfyUIService.IsConnected)
                    await _comfyUIService.ConnectAsync();

                ProcessingProgress = 10;
                ProcessingStatus = "Uploading video...";
                if (!IsMp4Valid(item.InputVideoPath, out var upscaleValidationError))
                    throw new Exception($"Input video is not a valid MP4: {upscaleValidationError}\n\nPath: {item.InputVideoPath}\n\nRe-generate the source video and try again.");
                var uploadedName = await _comfyUIService.UploadVideoAsync(item.InputVideoPath);
                if (string.IsNullOrEmpty(uploadedName))
                    throw new Exception("Video upload failed.");
                AddLog($"Uploaded: {uploadedName}");

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    RtxUpscaleWorkflow.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow not found: {workflowPath}");

                // Unique per job: it names the output file, and it is also the only thing that survives a
                // meta-batch requeue, so it is what the wait below follows. See WaitForMetaBatchOutputAsync.
                var rtxPrefix = $"RTX_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..30];

                // The node's own range; outside it the run fails validation.
                var scale = Math.Clamp(item.UpscaleScale <= 0 ? DefaultRtxScale : item.UpscaleScale, 1.0, 4.0);

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, RtxUpscaleVideoNode, "video", uploadedName);
                // Both widget shapes the Nvidia pack has shipped; the server reads the one it declares.
                WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, RtxUpscaleNode, "resize_type.scale", scale);
                WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, RtxUpscaleNode, "scale", scale);
                WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, RtxCombineNode, "filename_prefix",
                    $"{UpscaleOutputSubfolder}/{rtxPrefix}");
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);
                AddLog($"RTX VSR: ×{scale:0.##}, output prefix {rtxPrefix}");

                ProcessingProgress = 20;
                ProcessingStatus = "Executing upscale...";
                var existingFiles = GetExistingVideoFiles("*.mp4", UpscaleOutputSubfolder);

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(p =>
                {
                    if (p.Data?.Value != null && p.Data?.Max != null && p.Data.Max > 0)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 20 + (double)p.Data.Value / p.Data.Max * 70;
                            ProcessingStatus = $"Upscaling: {p.Data.Value}/{p.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress);
                AddLog($"Workflow submitted, ID: {promptId}");

                ProcessingProgress = 90;
                ProcessingStatus = "Waiting for output...";

                // The graph runs under a meta batch, so the prompt above is only the first batch of the clip
                // and its history entry holds no file at all. Follow the requeue chain to the finished mp4.
                var outputVideo = await WaitForMetaBatchOutputAsync(rtxPrefix,
                    _upscaleCts?.Token ?? CancellationToken.None);

                outputVideo ??= await TryGetVideoFromHistoryAsync(promptId);
                if (outputVideo == null)
                {
                    AddLog("Falling back to filesystem polling...");
                    outputVideo = await WaitForNewVideoAsync(existingFiles, "*.mp4",
                        TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5), UpscaleOutputSubfolder);
                }

                if (outputVideo == null || !File.Exists(outputVideo))
                    throw new Exception("No output video found.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "Upscale");
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"Upscale_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                File.Copy(outputVideo, finalPath, true);

                item.OutputVideoPath = finalPath;
                ResultVideoPath = finalPath;
                await LocalCopyService.CopyVideoAsync(finalPath);
                HasResult = true;
                ResultVideoInfo = $"Upscaled • {new FileInfo(finalPath).Length / 1024.0 / 1024.0:F1} MB";
                ProcessingProgress = 100;
                ProcessingStatus = "Upscale complete!";
                AddLog($"=== Done: {finalPath} ===");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        /// <summary>How long to keep following a meta-batch chain before giving up on it.</summary>
        private static readonly TimeSpan MetaBatchTimeout = TimeSpan.FromHours(2);

        /// <summary>
        /// Waits for the finished file of a run that upscales in meta batches, and returns a local path to
        /// it (or null if it never appeared).
        ///
        /// <para><b>Why this exists.</b> A VHS meta batch does not process the clip in one execution: it
        /// runs <c>frames_per_batch</c> frames, then <i>requeues the whole prompt under a new id</i> and
        /// repeats until the loader runs dry. So the prompt this tab submitted covers only the first 29
        /// frames; its history entry reports <c>unfinished_batch</c> and no file at all, and the finished
        /// mp4 lands under a prompt id the app never submitted and cannot ask about.</para>
        ///
        /// <para>Waiting on the submitted id therefore returned nothing, the code fell through to
        /// filesystem polling, and that picked up the output file <i>while ffmpeg was still appending to
        /// it</i> — the file only gets its moov atom when the last batch closes it. That is the truncated,
        /// unplayable, far-too-small "upscale" this replaces.</para>
        ///
        /// <para>The chain is followed by filename prefix instead, which is the one thing that survives a
        /// requeue: a history entry naming the prefix means VHS closed (and audio-muxed) the file, so it is
        /// safe to copy. If the chain leaves the queue without ever producing one, the run died — return
        /// null and let the caller's fallbacks report it.</para>
        /// </summary>
        private async Task<string?> WaitForMetaBatchOutputAsync(string filenamePrefix, CancellationToken token)
        {
            var http = _comfyUIService.HttpClient;
            var deadline = DateTime.UtcNow + MetaBatchTimeout;
            var lastLog = DateTime.MinValue;
            var queueClearPolls = 0;

            AddLog($"Meta batch: following the requeue chain for {filenamePrefix}...");

            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();

                var historyFile = await http.FindOutputFileFromHistoryAsync(filenamePrefix, token);
                if (historyFile != null)
                {
                    AddLog($"Meta batch finished: {historyFile}");
                    return await ResolveOutputFileAsync(historyFile);
                }

                // Still queued or running (our own chain, or someone else's job ahead of it).
                if (await http.IsQueueEntryContainingAsync(filenamePrefix, token))
                {
                    queueClearPolls = 0;
                }
                else if (++queueClearPolls >= 4)
                {
                    // Four quiet polls with nothing in history: the chain is gone without an output. The
                    // gap between one batch ending and the requeue landing is milliseconds, so this is not it.
                    AddLog("Meta batch: the chain left the queue without producing a file.");
                    return null;
                }

                if (DateTime.UtcNow - lastLog >= TimeSpan.FromSeconds(30))
                {
                    lastLog = DateTime.UtcNow;
                    var elapsed = (int)(MetaBatchTimeout - (deadline - DateTime.UtcNow)).TotalSeconds;
                    AddLog($"Meta batch: still upscaling ({elapsed}s elapsed)...");
                }

                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }

            AddLog("ERROR: timed out following the meta-batch chain.");
            return null;
        }

        /// <summary>
        /// Turns a "subfolder/filename" from ComfyUI's history into a local path — the shared output folder
        /// when it is reachable, otherwise a temp copy pulled over /view.
        /// </summary>
        private async Task<string?> ResolveOutputFileAsync(string relativeFile)
        {
            var settings = _settingsService.Settings;
            if (settings != null)
            {
                var isRemote = IsComfyUIRemote(new Uri(GetComfyUIBaseUrl()).Host);
                var outputFolder = settings.ResolveOutputFolder(isRemote);
                if (!string.IsNullOrEmpty(outputFolder))
                {
                    var localPath = Path.Combine(outputFolder, relativeFile.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(localPath)) return localPath;
                    AddLog($"Output not visible at {localPath}; downloading over /view instead.");
                }
            }

            var parts = relativeFile.Split('/');
            var filename = parts.Last();
            var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : string.Empty;

            var bytes = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, subfolder);
            if (bytes == null || bytes.Length == 0)
            {
                AddLog($"Download failed for {relativeFile}");
                return null;
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"flippix_upscale_{Guid.NewGuid():N}_{filename}");
            await File.WriteAllBytesAsync(tempPath, bytes);
            AddLog($"Downloaded {filename} ({bytes.Length / 1024.0 / 1024.0:F1} MB)");
            return tempPath;
        }

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            AddInterpolateToQueueCommand.NotifyCanExecuteChanged();
            ClearInterpolateQueueCommand.NotifyCanExecuteChanged();
            StopInterpolateQueueCommand.NotifyCanExecuteChanged();
            ReprocessInterpolateFailedCommand.NotifyCanExecuteChanged();
            AddUpscaleToQueueCommand.NotifyCanExecuteChanged();
            ClearUpscaleQueueCommand.NotifyCanExecuteChanged();
            StopUpscaleQueueCommand.NotifyCanExecuteChanged();
            ReprocessUpscaleFailedCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
        }
    }
}
