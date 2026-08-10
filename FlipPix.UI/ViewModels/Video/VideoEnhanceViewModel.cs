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
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    public partial class VideoEnhanceViewModel : VideoProcessingBaseViewModel
    {
        private const string InterpolateOutputSubfolder = "AnimateDiff";
        private const string UpscaleOutputSubfolder = "upscale";

        private const string RtxUpscaleWorkflow = "workflow/upscale nvidaAPI.json";
        private const string RtxUpscaleVideoNode = "2";      // VHS_LoadVideo

        // ── SeedVR2 graph (workflow/video/utility_seedvr2_7b_int8_upscale_video.json) ──────────
        // A 1-step diffusion restore: the clip is pre-resized by a multiplier, chunked on the temporal
        // axis, denoised by seedvr2_7b_int8_convrot, then merged back. Slower than RTX VSR and it loads a
        // 7B model, but it reconstructs detail rather than interpolating it.
        private const string SeedVr2UpscaleWorkflow = "workflow/video/utility_seedvr2_7b_int8_upscale_video.json";
        private const string SeedVr2VideoNode = "125";       // VHS_LoadVideoFFmpeg — also feeds fps + audio through
        private const string SeedVr2ResizeNode = "111";      // ResizeImageMaskNode — the actual upscale factor
        private const string SeedVr2ResizeMultiplierInput = "resize_type.multiplier";
        private const string SeedVr2SamplerNode = "109";     // KSampler — seed only; steps stay at the graph's 1
        private const string SeedVr2CombineNode = "124";     // VHS_VideoCombine — the graph's only real output
        private const string SeedVr2PlaySoundNode = "127";   // PlaySound|pysssss — stripped, see BuildSeedVr2Workflow

        /// <summary>The multiplier the exported graph ships with, and what the tab offers first.</summary>
        private const double DefaultSeedVr2Scale = 1.5;

        /// <summary>Selectable pre-resize multipliers. SeedVR2 restores what the resize invents, so this is
        /// the knob that decides the output resolution.</summary>
        public static IReadOnlyList<double> SeedVr2Scales { get; } = new[] { 1.25, 1.5, 2.0, 3.0 };

        private string InterpolateQueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "video_enhance_interpolate_queue.json");

        private string UpscaleQueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "video_enhance_upscale_queue.json");

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
        private VideoUpscaleEngine _upscaleEngine = VideoUpscaleEngine.Rtx;
        private double _seedVr2Scale = DefaultSeedVr2Scale;
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

        /// <summary>Which graph "Add to Queue" will snapshot onto the next upscale item.</summary>
        public VideoUpscaleEngine UpscaleEngine
        {
            get => _upscaleEngine;
            set
            {
                if (_upscaleEngine == value) return;
                _upscaleEngine = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsRtxSelected));
                OnPropertyChanged(nameof(IsSeedVr2Selected));
                OnPropertyChanged(nameof(UpscaleEngineDescription));
            }
        }

        /// <summary>Bound by the radio buttons — <see cref="UpscaleEngine"/> is not directly two-way bindable
        /// from a RadioButton without a converter, and the tab only has two engines.</summary>
        public bool IsRtxSelected
        {
            get => _upscaleEngine == VideoUpscaleEngine.Rtx;
            set { if (value) UpscaleEngine = VideoUpscaleEngine.Rtx; OnPropertyChanged(); }
        }

        public bool IsSeedVr2Selected
        {
            get => _upscaleEngine == VideoUpscaleEngine.SeedVr2;
            set { if (value) UpscaleEngine = VideoUpscaleEngine.SeedVr2; OnPropertyChanged(); }
        }

        public double SeedVr2Scale
        {
            get => _seedVr2Scale;
            set
            {
                if (Math.Abs(_seedVr2Scale - value) < 0.001) return;
                _seedVr2Scale = value <= 0 ? DefaultSeedVr2Scale : value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpscaleEngineDescription));
            }
        }

        public IReadOnlyList<double> SeedVr2ScaleOptions => SeedVr2Scales;

        public string UpscaleEngineDescription => _upscaleEngine == VideoUpscaleEngine.SeedVr2
            ? $"SeedVR2 7B INT8 — diffusion restore at {_seedVr2Scale:0.##}×. Reconstructs detail; loads a 7B model and is much slower than RTX."
            : "RTX Video Super Resolution — fast hardware upscale, no model load.";

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
                initial,
                persistKey: "videoenhance.interpolate-video");

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
                initial,
                persistKey: "videoenhance.upscale-video");

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
                UpscaleEngine = UpscaleEngine,
                UpscaleScale = SeedVr2Scale,
                ItemStatus = QueueItemStatus.Pending
            };

            _upscaleQueue.Add(item);
            SaveUpscaleQueueToFile();
            AddLog($"Added to upscale queue: {item.DisplayText} ({item.EngineDisplay})");
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
                AddLog($"=== Upscaling: {item.DisplayText} ({item.EngineDisplay}) ===");

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

                var workflow = item.UpscaleEngine == VideoUpscaleEngine.SeedVr2
                    ? await BuildSeedVr2WorkflowAsync(uploadedName, item.UpscaleScale)
                    : await BuildRtxWorkflowAsync(uploadedName);

                ProcessingProgress = 20;
                ProcessingStatus = item.UpscaleEngine == VideoUpscaleEngine.SeedVr2
                    ? "Executing SeedVR2 upscale..."
                    : "Executing upscale...";
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

                var outputVideo = await TryGetVideoFromHistoryAsync(promptId);
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
                var namePrefix = item.UpscaleEngine == VideoUpscaleEngine.SeedVr2 ? "SeedVR2" : "Upscale";
                var finalPath = Path.Combine(outputDir, $"{namePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                File.Copy(outputVideo, finalPath, true);

                item.OutputVideoPath = finalPath;
                ResultVideoPath = finalPath;
                await LocalCopyService.CopyVideoAsync(finalPath);
                HasResult = true;
                ResultVideoInfo = $"Upscaled ({item.EngineDisplay}) • {new FileInfo(finalPath).Length / 1024.0 / 1024.0:F1} MB";
                ProcessingProgress = 100;
                ProcessingStatus = "Upscale complete!";
                AddLog($"=== Done: {finalPath} ===");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        /// <summary>RTX Video Super Resolution — the tab's original upscale path, unchanged.</summary>
        private async Task<JsonElement> BuildRtxWorkflowAsync(string uploadedName)
        {
            var workflowJson = await ReadWorkflowAsync(RtxUpscaleWorkflow, RtxUpscaleVideoNode);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, RtxUpscaleVideoNode, "video", uploadedName);
            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        /// <summary>
        /// SeedVR2 7B INT8. The export is already API-shaped, so only three things are patched: the uploaded
        /// clip, the pre-resize multiplier that decides the output resolution, and a fresh sampler seed.
        /// Frame rate and audio are carried through by the graph itself (node 126 reads them off the loaded
        /// video), so nothing needs to be told the source fps.
        /// <para>The exported graph also carries a <c>PlaySound|pysssss</c> node, which is an OUTPUT node: it
        /// contributes nothing the app reads, beeps on the ComfyUI host once per job, and fails validation
        /// outright on an install without pysssss. It is dropped here rather than in the file, so the file
        /// stays a faithful export.</para>
        /// </summary>
        private async Task<JsonElement> BuildSeedVr2WorkflowAsync(string uploadedName, double scale)
        {
            var workflowJson = await ReadWorkflowAsync(
                SeedVr2UpscaleWorkflow,
                SeedVr2VideoNode, SeedVr2ResizeNode, SeedVr2SamplerNode, SeedVr2CombineNode);

            if (scale <= 0) scale = DefaultSeedVr2Scale;

            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, SeedVr2VideoNode, "video", uploadedName);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, SeedVr2ResizeNode, SeedVr2ResizeMultiplierInput, scale);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, SeedVr2SamplerNode, "seed",
                Random.Shared.NextInt64(0, long.MaxValue));
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, SeedVr2CombineNode, "filename_prefix",
                UpscaleOutputSubfolder);

            var nodes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson)
                        ?? throw new InvalidOperationException("Failed to parse the SeedVR2 workflow.");
            if (nodes.Remove(SeedVr2PlaySoundNode))
                AddLog("SeedVR2: dropped the PlaySound notification node");

            AddLog($"SeedVR2: pre-resize ×{scale:0.##}, 1-step restore");
            return JsonSerializer.SerializeToElement(nodes);
        }

        /// <summary>Loads a workflow from the build output and fails loudly if it is not the graph the node
        /// ids above were written against — patching a renumbered export otherwise silently no-ops and the
        /// job renders the wrong clip at the wrong size.</summary>
        private static async Task<string> ReadWorkflowAsync(string relativePath, params string[] requiredNodeIds)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                throw new FileNotFoundException($"Workflow not found: {path}");

            var json = await File.ReadAllTextAsync(path);
            var nodes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                        ?? throw new InvalidOperationException($"Failed to parse workflow: {path}");

            var missing = requiredNodeIds.Where(id => !nodes.ContainsKey(id)).ToList();
            if (missing.Any())
                throw new InvalidOperationException(
                    $"Workflow {Path.GetFileName(path)} is missing expected node(s) {string.Join(", ", missing)} — " +
                    "it was re-exported with different node ids and the app can no longer patch it.");

            return json;
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
