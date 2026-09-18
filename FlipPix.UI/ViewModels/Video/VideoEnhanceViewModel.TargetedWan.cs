using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "Targeted Wan Enhance" tab — rebuild only the things you name inside a clip.
    ///
    /// <para>SAM3 tracks the named targets across every frame and hands back a per-frame mask.
    /// Three WanVideo 2.2 T2V low-noise passes then sample through that mask at falling denoise and
    /// rising resolution, each one composited back into the untouched source through a feathered
    /// edge, so the background is bit-for-bit the original and only the target is rebuilt. The
    /// source audio is remuxed onto the result.</para>
    ///
    /// <para>Graph: <c>workflow/video/wan/targeted-wan-enhance.json</c>, produced from the authored
    /// UI export by <c>tools/convert_targeted_wan_enhance.py</c> — which is also where the reasons
    /// for the deviations from the export live.</para>
    /// </summary>
    public partial class VideoEnhanceViewModel
    {
        private const string TargetedEnhanceWorkflow = "workflow/video/wan/targeted-wan-enhance.json";
        private const string TargetedEnhanceOutputSubfolder = "TargetedEnhance";

        // ── the nodes the tab drives ──────────────────────────────────────────────────────────
        private const string TeVideoNode = "168";        // VHS_LoadVideo — the clip, frame cap, fps
        private const string TeSam3ModelNode = "216";    // LoadSAM3Model
        private const string TeSam3SegmentNode = "217";  // SAM3VideoSegmentation — targets + threshold
        private const string TeMaskGrowNode = "184";     // GrowMaskWithBlur on the SAM3 mask — fill_holes
        private const string TeFillHolesNode = "436";    // PrimitiveBoolean feeding node 184
        private const string TeFeatherNode = "327";      // PrimitiveInt — composite feather, in pixels
        private const string TePhase1WidthNode = "174";  // INTConstant — phase-one canvas
        private const string TePhase1HeightNode = "175";
        private const string TePhase2ResizeNode = "239"; // ImageResizeKJv2 — phase-two canvas
        private const string TePhase3ResizeNode = "243"; // ImageResizeKJv2 — phase-three canvas
        private const string TeDenoise1Node = "487";     // easy float ×3 — per-phase denoise
        private const string TeDenoise2Node = "496";
        private const string TeDenoise3Node = "497";
        private const string TeMaskPhase2Node = "516";   // PrimitiveBoolean — mask phases two/three
        private const string TeMaskPhase3Node = "517";
        private const string TePromptNode = "182";       // String Literal — the positive prompt
        private const string TeSampler1Node = "151";     // WanVideoSampler ×3 — steps + seed
        private const string TeSampler2Node = "234";
        private const string TeSampler3Node = "241";
        private const string TeCombineNode = "276";      // VHS_VideoCombine — the only output

        /// <summary>Long edges for the three passes, per detail level. The last one is the finished
        /// resolution; the source aspect fills in the other side.</summary>
        private static readonly Dictionary<TargetedEnhanceDetail, int[]> DetailLadders = new()
        {
            [TargetedEnhanceDetail.Draft] = new[] { 512, 768, 1024 },
            [TargetedEnhanceDetail.Standard] = new[] { 768, 1024, 1280 },
            [TargetedEnhanceDetail.High] = new[] { 1024, 1280, 1536 },
        };

        public static IReadOnlyList<TargetedEnhanceDetail> TargetedDetailLevels { get; } =
            new[] { TargetedEnhanceDetail.Draft, TargetedEnhanceDetail.Standard, TargetedEnhanceDetail.High };

        private string TargetedQueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "video_enhance_targeted_queue.json");

        private string _targetedVideoPath = string.Empty;
        private string _targetedVideoInfo = string.Empty;
        private (int Width, int Height) _targetedSourceSize;
        private string _targetedTargets = "woman";
        private string _targetedPrompt =
            "Ultra high detail video, 8K, UHD, ultra realistic. Natural skin texture, sharp eyes, " +
            "crisp hands. Depth of field, background in lens blur.";
        private double _targetedDetectionThreshold = 0.3;
        private int _targetedMaskFeather = 3;
        private bool _targetedFillHoles;
        private TargetedEnhanceDetail _targetedDetail = TargetedEnhanceDetail.Standard;
        private double _targetedDenoise1 = 0.4;
        private double _targetedDenoise2 = 0.2;
        private double _targetedDenoise3 = 0.1;
        private int _targetedSteps = 6;
        private long _targetedSeed;
        private int _targetedMaxFrames;
        private bool _targetedMaskPhase2 = true;
        private bool _targetedMaskPhase3 = true;
        private bool _isProcessingTargetedQueue;
        private string _targetedQueueStatus = string.Empty;
        private readonly ObservableCollection<TargetedWanEnhanceQueueItem> _targetedQueue = new();
        private CancellationTokenSource? _targetedCts;

        /// <summary>Wired from the constructor of the main partial.</summary>
        private void InitializeTargetedEnhance()
        {
            SelectTargetedVideoCommand = new RelayCommand(SelectTargetedVideo);
            AddTargetedToQueueCommand = new RelayCommand(AddTargetedToQueue, () => CanAddTargeted);
            RemoveTargetedQueueItemCommand = new RelayCommand<TargetedWanEnhanceQueueItem>(RemoveTargetedQueueItem);
            ClearTargetedQueueCommand = new RelayCommand(ClearTargetedQueue, () => _targetedQueue.Any());
            StopTargetedQueueCommand = new RelayCommand(StopTargetedQueue, () => IsProcessingTargetedQueue);
            ReprocessTargetedFailedCommand = new RelayCommand(
                async () => await ReprocessTargetedFailedAsync(), () => HasTargetedFailedItems);

            _targetedQueue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasTargetedQueueItems));
                UpdateTargetedQueueStatus();
                OnCanExecuteChanged();
            };

            LoadTargetedQueueFromFile();
        }

        #region Commands

        public RelayCommand SelectTargetedVideoCommand { get; private set; } = null!;
        public RelayCommand AddTargetedToQueueCommand { get; private set; } = null!;
        public RelayCommand<TargetedWanEnhanceQueueItem> RemoveTargetedQueueItemCommand { get; private set; } = null!;
        public RelayCommand ClearTargetedQueueCommand { get; private set; } = null!;
        public RelayCommand StopTargetedQueueCommand { get; private set; } = null!;
        public RelayCommand ReprocessTargetedFailedCommand { get; private set; } = null!;

        public bool HasTargetedFailedItems => _targetedQueue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        #endregion

        #region Properties

        public string TargetedVideoPath
        {
            get => _targetedVideoPath;
            set
            {
                if (_targetedVideoPath == value) return;
                _targetedVideoPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasTargetedVideo));
                OnPropertyChanged(nameof(CanAddTargeted));
                LoadTargetedVideoInfo();
                OnCanExecuteChanged();
            }
        }

        public string TargetedVideoInfo
        {
            get => _targetedVideoInfo;
            private set { if (_targetedVideoInfo != value) { _targetedVideoInfo = value; OnPropertyChanged(); } }
        }

        public bool HasTargetedVideo => !string.IsNullOrEmpty(TargetedVideoPath) && File.Exists(TargetedVideoPath);

        public bool CanAddTargeted => HasTargetedVideo && !string.IsNullOrWhiteSpace(TargetedTargets);

        /// <summary>What SAM3 looks for. Comma-separated for several things at once.</summary>
        public string TargetedTargets
        {
            get => _targetedTargets;
            set
            {
                if (_targetedTargets == value) return;
                _targetedTargets = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanAddTargeted));
                OnCanExecuteChanged();
            }
        }

        public string TargetedPrompt
        {
            get => _targetedPrompt;
            set { if (_targetedPrompt != value) { _targetedPrompt = value; OnPropertyChanged(); } }
        }

        public double TargetedDetectionThreshold
        {
            get => _targetedDetectionThreshold;
            set
            {
                var clamped = Math.Clamp(value, 0.05, 0.95);
                if (Math.Abs(_targetedDetectionThreshold - clamped) < 0.001) return;
                _targetedDetectionThreshold = clamped;
                OnPropertyChanged();
            }
        }

        public int TargetedMaskFeather
        {
            get => _targetedMaskFeather;
            set
            {
                var clamped = Math.Clamp(value, 0, 64);
                if (_targetedMaskFeather == clamped) return;
                _targetedMaskFeather = clamped;
                OnPropertyChanged();
            }
        }

        public bool TargetedFillHoles
        {
            get => _targetedFillHoles;
            set { if (_targetedFillHoles != value) { _targetedFillHoles = value; OnPropertyChanged(); } }
        }

        public TargetedEnhanceDetail TargetedDetail
        {
            get => _targetedDetail;
            set
            {
                if (_targetedDetail == value) return;
                _targetedDetail = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TargetedDetailDescription));
            }
        }

        public IReadOnlyList<TargetedEnhanceDetail> TargetedDetailOptions => TargetedDetailLevels;

        /// <summary>The three canvases this job will actually sample at, so the ladder is not a
        /// mystery until the log scrolls past.</summary>
        public string TargetedDetailDescription
        {
            get
            {
                var ladder = PlanTargetedCanvases(_targetedDetail, _targetedSourceSize);
                var steps = string.Join("  →  ", ladder.Select(c => $"{c.Width}×{c.Height}"));
                return _targetedSourceSize.Width > 0
                    ? $"{steps}   (source {_targetedSourceSize.Width}×{_targetedSourceSize.Height})"
                    : steps;
            }
        }

        public double TargetedDenoise1
        {
            get => _targetedDenoise1;
            set { SetTargetedDenoise(ref _targetedDenoise1, value); }
        }

        public double TargetedDenoise2
        {
            get => _targetedDenoise2;
            set { SetTargetedDenoise(ref _targetedDenoise2, value); }
        }

        public double TargetedDenoise3
        {
            get => _targetedDenoise3;
            set { SetTargetedDenoise(ref _targetedDenoise3, value); }
        }

        private void SetTargetedDenoise(ref double field, double value,
            [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(field - clamped) < 0.001) return;
            field = clamped;
            OnPropertyChanged(name);
        }

        public int TargetedSteps
        {
            get => _targetedSteps;
            set
            {
                var clamped = Math.Clamp(value, 2, 30);
                if (_targetedSteps == clamped) return;
                _targetedSteps = clamped;
                OnPropertyChanged();
            }
        }

        /// <summary>0 = a fresh seed per job.</summary>
        public long TargetedSeed
        {
            get => _targetedSeed;
            set { if (_targetedSeed != value) { _targetedSeed = Math.Max(0, value); OnPropertyChanged(); } }
        }

        /// <summary>0 = the whole clip.</summary>
        public int TargetedMaxFrames
        {
            get => _targetedMaxFrames;
            set { if (_targetedMaxFrames != value) { _targetedMaxFrames = Math.Max(0, value); OnPropertyChanged(); } }
        }

        public bool TargetedMaskPhase2
        {
            get => _targetedMaskPhase2;
            set { if (_targetedMaskPhase2 != value) { _targetedMaskPhase2 = value; OnPropertyChanged(); } }
        }

        public bool TargetedMaskPhase3
        {
            get => _targetedMaskPhase3;
            set { if (_targetedMaskPhase3 != value) { _targetedMaskPhase3 = value; OnPropertyChanged(); } }
        }

        public ObservableCollection<TargetedWanEnhanceQueueItem> TargetedQueue => _targetedQueue;
        public bool HasTargetedQueueItems => _targetedQueue.Any();

        public bool IsProcessingTargetedQueue
        {
            get => _isProcessingTargetedQueue;
            private set
            {
                if (_isProcessingTargetedQueue == value) return;
                _isProcessingTargetedQueue = value;
                OnPropertyChanged();
                OnCanExecuteChanged();
            }
        }

        public string TargetedQueueStatus
        {
            get => _targetedQueueStatus;
            private set { if (_targetedQueueStatus != value) { _targetedQueueStatus = value; OnPropertyChanged(); } }
        }

        #endregion

        #region Canvas planning

        /// <summary>
        /// Turns a detail level plus the source dimensions into the three canvases the passes run at.
        ///
        /// <para>The graph resizes with keep_proportion "stretch", so it renders whatever shape it is
        /// handed — feed the authored portrait ladder a landscape clip and it squashes it. Each rung
        /// is therefore fitted to the source aspect (long edge from the ladder, short edge derived)
        /// and rounded to a multiple of 16, which is what the resize nodes and the WAN latent
        /// grid both want. With no source measured yet, fall back to 9:16 so the label shows
        /// something sane before a file is picked.</para>
        /// </summary>
        private static (int Width, int Height)[] PlanTargetedCanvases(
            TargetedEnhanceDetail detail, (int Width, int Height) source)
        {
            var ladder = DetailLadders[detail];
            var w = source.Width > 0 ? source.Width : 720;
            var h = source.Height > 0 ? source.Height : 1280;
            var landscape = w >= h;
            var aspect = landscape ? (double)h / w : (double)w / h;

            var planned = new (int Width, int Height)[ladder.Length];
            for (int i = 0; i < ladder.Length; i++)
            {
                var longEdge = Round16(ladder[i]);
                var shortEdge = Round16((int)Math.Round(ladder[i] * aspect));
                planned[i] = landscape ? (longEdge, shortEdge) : (shortEdge, longEdge);
            }
            return planned;
        }

        private static int Round16(int value) => Math.Max(16, (int)Math.Round(value / 16.0) * 16);

        #endregion

        #region Selection

        private async void SelectTargetedVideo()
        {
            var initial = _settingsService.Settings?.EnhanceVideoFolder;
            if (string.IsNullOrEmpty(initial) || !Directory.Exists(initial))
                initial = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Video to Enhance",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv|All Files|*.*",
                initial,
                persistKey: "videoenhance.targeted-video");

            if (path == null) return;

            TargetedVideoPath = path;
            PersistBrowseFolder(Path.GetDirectoryName(path));
            AddLog($"Targeted enhance: selected {Path.GetFileName(path)}");
        }

        /// <summary>
        /// Measures the picked clip. Probing shells out to ffprobe, so it runs off the setter's
        /// thread — see <see cref="VideoProcessingBaseViewModel.GetVideoDimensions"/>.
        /// </summary>
        private void LoadTargetedVideoInfo()
        {
            if (!HasTargetedVideo)
            {
                _targetedSourceSize = default;
                TargetedVideoInfo = string.Empty;
                OnPropertyChanged(nameof(TargetedDetailDescription));
                return;
            }

            var path = TargetedVideoPath;
            _ = Task.Run(() =>
            {
                string info;
                (int Width, int Height) size = default;
                try
                {
                    var fi = new FileInfo(path);
                    size = GetVideoDimensions(path);
                    var frames = GetVideoFrameCount(path);
                    var duration = GetVideoDuration(path);
                    info = $"{fi.Name} • {size.Width}×{size.Height} • {frames} frames • " +
                           $"{duration:F1}s • {fi.Length / 1024.0 / 1024.0:F1} MB";
                }
                catch (Exception ex)
                {
                    info = $"{Path.GetFileName(path)} (could not probe: {ex.Message})";
                }

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (!string.Equals(TargetedVideoPath, path, StringComparison.OrdinalIgnoreCase))
                        return;                       // the user moved on while ffprobe ran
                    _targetedSourceSize = size;
                    TargetedVideoInfo = info;
                    OnPropertyChanged(nameof(TargetedDetailDescription));
                });
            });
        }

        #endregion

        #region Queue

        private void AddTargetedToQueue()
        {
            if (!CanAddTargeted) return;

            var item = new TargetedWanEnhanceQueueItem
            {
                InputVideoPath = TargetedVideoPath,
                Targets = TargetedTargets.Trim(),
                DetectionThreshold = TargetedDetectionThreshold,
                MaskFeather = TargetedMaskFeather,
                FillHoles = TargetedFillHoles,
                Prompt = TargetedPrompt,
                Detail = TargetedDetail,
                DenoisePhase1 = TargetedDenoise1,
                DenoisePhase2 = TargetedDenoise2,
                DenoisePhase3 = TargetedDenoise3,
                Steps = TargetedSteps,
                Seed = TargetedSeed,
                MaxFrames = TargetedMaxFrames,
                MaskPhase2 = TargetedMaskPhase2,
                MaskPhase3 = TargetedMaskPhase3,
                ItemStatus = QueueItemStatus.Pending
            };

            _targetedQueue.Add(item);
            SaveTargetedQueueToFile();
            AddLog($"Added to targeted enhance queue: {item.DisplayText} — {item.TargetsDisplay}");
            UpdateTargetedQueueStatus();

            if (!IsProcessingTargetedQueue)
                _ = ProcessTargetedQueueAsync();
        }

        private void RemoveTargetedQueueItem(TargetedWanEnhanceQueueItem? item)
        {
            if (item != null && item.ItemStatus != QueueItemStatus.Processing)
                _targetedQueue.Remove(item);
            UpdateTargetedQueueStatus();
        }

        private void UpdateTargetedQueueStatus()
        {
            if (_targetedQueue.Count == 0) { TargetedQueueStatus = string.Empty; return; }
            var pending = _targetedQueue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
            var done = _targetedQueue.Count(x => x.ItemStatus == QueueItemStatus.Completed);
            var failed = _targetedQueue.Count(x => x.ItemStatus == QueueItemStatus.Failed);
            TargetedQueueStatus = $"{pending} pending • {done} done • {failed} failed";
            OnPropertyChanged(nameof(HasTargetedFailedItems));
            OnCanExecuteChanged();
        }

        private void ClearTargetedQueue()
        {
            _targetedCts?.Cancel();
            foreach (var item in _targetedQueue.ToList())
                _targetedQueue.Remove(item);
            SaveTargetedQueueToFile();
            UpdateTargetedQueueStatus();
            AddLog("Targeted enhance queue cleared");
        }

        private void StopTargetedQueue()
        {
            _targetedCts?.Cancel();
            AddLog("Targeted enhance queue stop requested");
        }

        private async Task ReprocessTargetedFailedAsync()
        {
            var failed = _targetedQueue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (!failed.Any()) return;
            foreach (var item in failed)
                item.ItemStatus = QueueItemStatus.Pending;
            UpdateTargetedQueueStatus();
            SaveTargetedQueueToFile();
            AddLog($"Reprocessing {failed.Count} failed targeted enhance item(s)...");
            if (!IsProcessingTargetedQueue)
                await ProcessTargetedQueueAsync();
        }

        private async Task ProcessTargetedQueueAsync()
        {
            if (IsProcessingTargetedQueue) return;
            IsProcessingTargetedQueue = true;
            _targetedCts?.Dispose();
            _targetedCts = new CancellationTokenSource();
            var token = _targetedCts.Token;
            AddLog("Starting targeted enhance queue...");
            OnCanExecuteChanged();
            try
            {
                TargetedWanEnhanceQueueItem? item;
                while (!token.IsCancellationRequested &&
                       (item = _targetedQueue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                {
                    item.ItemStatus = QueueItemStatus.Processing;
                    UpdateTargetedQueueStatus();
                    SaveTargetedQueueToFile();
                    try
                    {
                        await ProcessTargetedSingleAsync(item, token);
                        item.ItemStatus = QueueItemStatus.Completed;
                        AddLog($"Targeted enhance complete: {item.DisplayText}");
                    }
                    catch (OperationCanceledException)
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
                        AddLog("Targeted enhance item cancelled — reset to Pending");
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
                            AddLog($"Targeted enhance FAILED: {ex.Message}");
                        }
                    }
                    UpdateTargetedQueueStatus();
                    SaveTargetedQueueToFile();
                }
            }
            finally
            {
                IsProcessingTargetedQueue = false;
                AddLog("Targeted enhance queue finished.");
                OnCanExecuteChanged();
            }
        }

        private void SaveTargetedQueueToFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(TargetedQueueFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(TargetedQueueFilePath,
                    JsonSerializer.Serialize(_targetedQueue.ToList(),
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AddLog($"Error saving targeted enhance queue: {ex.Message}"); }
        }

        private void LoadTargetedQueueFromFile()
        {
            try
            {
                if (!File.Exists(TargetedQueueFilePath)) return;
                var items = JsonSerializer.Deserialize<List<TargetedWanEnhanceQueueItem>>(
                    File.ReadAllText(TargetedQueueFilePath));
                if (items?.Any() != true) return;
                _targetedQueue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Processing)
                        item.ItemStatus = QueueItemStatus.Pending;
                    _targetedQueue.Add(item);
                }
                UpdateTargetedQueueStatus();
                AddLog($"Targeted enhance queue loaded: {_targetedQueue.Count} items");
            }
            catch (Exception ex) { AddLog($"Error loading targeted enhance queue: {ex.Message}"); }
        }

        #endregion

        #region Processing

        private async Task ProcessTargetedSingleAsync(TargetedWanEnhanceQueueItem item, CancellationToken token)
        {
            try
            {
                IsProcessing = true;
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing targeted enhance...";
                AddLog($"=== Targeted enhance: {item.DisplayText} — {item.TargetsDisplay} ===");

                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    s => AddLog($"[ComfyUI] {s}"));
                if (!comfyUIOk) throw new Exception("ComfyUI is not running.");

                if (!_comfyUIService.IsConnected)
                    await _comfyUIService.ConnectAsync();

                ProcessingProgress = 5;
                ProcessingStatus = "Uploading video...";
                if (!IsMp4Valid(item.InputVideoPath, out var validationError))
                    throw new Exception(
                        $"Input video is not a valid MP4: {validationError}\n\nPath: {item.InputVideoPath}");

                var uploadedName = await _comfyUIService.UploadVideoAsync(item.InputVideoPath);
                if (string.IsNullOrEmpty(uploadedName))
                    throw new Exception("Video upload failed.");
                AddLog($"Uploaded: {uploadedName}");

                // Unique per job so the output can be told apart from anything else in the folder.
                var prefix = $"{TargetedEnhanceOutputSubfolder}/te_{DateTime.Now:yyyyMMdd_HHmmss}";

                var workflow = await BuildTargetedWorkflowAsync(item, uploadedName, prefix);

                ProcessingProgress = 10;
                ProcessingStatus = "Tracking targets and enhancing...";
                var existingFiles = GetExistingVideoFiles("*.mp4", TargetedEnhanceOutputSubfolder);

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(p =>
                {
                    if (p.Data?.Value == null || p.Data?.Max == null || p.Data.Max <= 0) return;
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProcessingProgress = 10 + (double)p.Data.Value / p.Data.Max * 80;
                        ProcessingStatus = $"Enhancing: {p.Data.Value}/{p.Data.Max}";
                    });
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress);
                AddLog($"Workflow submitted, ID: {promptId}");

                ProcessingProgress = 92;
                ProcessingStatus = "Waiting for output...";

                var outputVideo = await TryGetVideoFromHistoryAsync(promptId);
                if (outputVideo == null)
                {
                    AddLog("Falling back to filesystem polling...");
                    outputVideo = await WaitForNewVideoAsync(existingFiles, "*.mp4",
                        TimeSpan.FromHours(3), TimeSpan.FromSeconds(10), TargetedEnhanceOutputSubfolder);
                }

                token.ThrowIfCancellationRequested();

                if (outputVideo == null || !File.Exists(outputVideo))
                    throw new Exception("No output video found.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "TargetedEnhance");
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"Targeted_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                File.Copy(outputVideo, finalPath, true);

                item.OutputVideoPath = finalPath;
                ResultVideoPath = finalPath;
                await LocalCopyService.CopyVideoAsync(finalPath);
                HasResult = true;
                ResultVideoInfo = $"Targeted enhance ({item.Targets}) • " +
                                  $"{new FileInfo(finalPath).Length / 1024.0 / 1024.0:F1} MB";
                ProcessingProgress = 100;
                ProcessingStatus = "Targeted enhance complete!";
                AddLog($"=== Done: {finalPath} ===");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        /// <summary>
        /// Patches the exported graph for one job.
        ///
        /// <para>The one structural edit is the audio link: <c>VHS_LoadVideo</c> raises rather than
        /// returning silence when a clip has no audio stream, and only raises once something
        /// consumes that output — so for a silent source the <c>audio</c> input has to come off
        /// <c>VHS_VideoCombine</c> entirely, or the run dies in the loader before a single frame is
        /// sampled.</para>
        /// </summary>
        private async Task<JsonElement> BuildTargetedWorkflowAsync(
            TargetedWanEnhanceQueueItem item, string uploadedName, string filenamePrefix)
        {
            var json = await ReadWorkflowAsync(TargetedEnhanceWorkflow,
                TeVideoNode, TeSam3SegmentNode, TeSam3ModelNode, TePhase1WidthNode, TePhase1HeightNode,
                TePhase2ResizeNode, TePhase3ResizeNode, TeDenoise1Node, TeDenoise2Node, TeDenoise3Node,
                TePromptNode, TeSampler1Node, TeSampler2Node, TeSampler3Node, TeCombineNode);

            var canvases = PlanTargetedCanvases(item.Detail, GetVideoDimensions(item.InputVideoPath));
            var seed = item.Seed > 0 ? item.Seed : Random.Shared.NextInt64(1, int.MaxValue);

            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, TeVideoNode, new Dictionary<string, object>
            {
                ["video"] = uploadedName,
                ["frame_load_cap"] = item.MaxFrames,
                // 0 keeps the source frame rate; the authored 24 resampled every clip to 24fps and
                // silently drifted the remuxed audio on anything shot at another rate.
                ["force_rate"] = 0,
            });

            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, TeSam3SegmentNode, new Dictionary<string, object>
            {
                ["text_prompt"] = item.Targets,
                ["score_threshold"] = item.DetectionThreshold,
            });

            WorkflowNodeUpdater.UpdateNodeInput(ref json, TeFillHolesNode, "value", item.FillHoles);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, TeFeatherNode, "value", item.MaskFeather);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, TeMaskPhase2Node, "value", item.MaskPhase2);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, TeMaskPhase3Node, "value", item.MaskPhase3);

            WorkflowNodeUpdater.UpdateNodeInput(ref json, TePhase1WidthNode, "value", canvases[0].Width);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, TePhase1HeightNode, "value", canvases[0].Height);
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, TePhase2ResizeNode,
                new Dictionary<string, object> { ["width"] = canvases[1].Width, ["height"] = canvases[1].Height });
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, TePhase3ResizeNode,
                new Dictionary<string, object> { ["width"] = canvases[2].Width, ["height"] = canvases[2].Height });

            WorkflowNodeUpdater.UpdateNodeInput(ref json, TeDenoise1Node, "value", item.DenoisePhase1);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, TeDenoise2Node, "value", item.DenoisePhase2);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, TeDenoise3Node, "value", item.DenoisePhase3);

            WorkflowNodeUpdater.UpdateNodeInput(ref json, TePromptNode, "string", item.Prompt ?? string.Empty);

            // Each phase gets its own seed derived from the job's, so re-running with the same seed
            // reproduces the whole ladder rather than only the first pass.
            var phaseSeeds = new[] { seed, seed + 1, seed + 2 };
            var samplers = new[] { TeSampler1Node, TeSampler2Node, TeSampler3Node };
            for (int i = 0; i < samplers.Length; i++)
            {
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, samplers[i],
                    new Dictionary<string, object> { ["seed"] = phaseSeeds[i], ["steps"] = item.Steps });
            }

            WorkflowNodeUpdater.UpdateNodeInput(ref json, TeCombineNode, "filename_prefix", filenamePrefix);

            var nodes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                        ?? throw new InvalidOperationException("Failed to parse the targeted enhance workflow.");

            if (!HasAudioStream(item.InputVideoPath))
            {
                nodes[TeCombineNode] = RemoveInput(nodes[TeCombineNode], "audio");
                AddLog("Source has no audio track — rendering the enhance silent.");
            }

            AddLog($"Targets '{item.Targets}' @ {item.DetectionThreshold:0.##} • feather {item.MaskFeather}px • " +
                   $"{string.Join(" → ", canvases.Select(c => $"{c.Width}×{c.Height}"))} • " +
                   $"denoise {item.DenoisePhase1:0.##}/{item.DenoisePhase2:0.##}/{item.DenoisePhase3:0.##} • " +
                   $"{item.Steps} steps • seed {seed}" +
                   (item.MaxFrames > 0 ? $" • first {item.MaxFrames} frames" : string.Empty));

            return JsonSerializer.SerializeToElement(nodes);
        }

        /// <summary>Returns the node with one input dropped — API-format nodes carry their links in
        /// <c>inputs</c>, so removing the key is how a connection is severed.</summary>
        private static JsonElement RemoveInput(JsonElement node, string inputName)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(node.GetRawText())
                       ?? new Dictionary<string, JsonElement>();
            if (dict.TryGetValue("inputs", out var inputsElement))
            {
                var inputs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                                 inputsElement.GetRawText()) ?? new Dictionary<string, JsonElement>();
                inputs.Remove(inputName);
                dict["inputs"] = JsonSerializer.SerializeToElement(inputs);
            }
            return JsonSerializer.SerializeToElement(dict);
        }

        /// <summary>True when the file carries at least one audio stream. ffprobe missing or the probe
        /// failing answers "yes", which keeps the graph as authored — the loader's own error is then
        /// the thing that reports the real problem.</summary>
        private bool HasAudioStream(string videoPath)
        {
            try
            {
                var ffmpegPath = FindFFmpeg();
                if (ffmpegPath == null) return true;
                var ffprobePath = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");
                if (!File.Exists(ffprobePath)) return true;

                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -select_streams a -show_entries stream=index -of csv=p=0 \"{videoPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });
                if (process == null) return true;
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(30000);
                return !string.IsNullOrWhiteSpace(output);
            }
            catch (Exception ex)
            {
                AddLog($"Could not probe audio stream ({ex.Message}); assuming the clip has one.");
                return true;
            }
        }

        #endregion

        private void OnTargetedCanExecuteChanged()
        {
            AddTargetedToQueueCommand?.NotifyCanExecuteChanged();
            ClearTargetedQueueCommand?.NotifyCanExecuteChanged();
            StopTargetedQueueCommand?.NotifyCanExecuteChanged();
            ReprocessTargetedFailedCommand?.NotifyCanExecuteChanged();
        }
    }
}
