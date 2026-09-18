using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Linux.Models;
using FlipPix.UI.Linux.Services;

namespace FlipPix.UI.Linux.ViewModels.Video
{
    /// <summary>
    /// "Enhance HD" tab — re-render a finished MiniMax H3 clip at HD/2K.
    ///
    /// <para>This is not an upscaler. The clip is resized to a base canvas, encoded to an H3 latent,
    /// lifted by <c>MinimaxH3LatentUpscaler3D</c> to the detail megapixels, concatenated with its own
    /// audio latent, and re-sampled in one partial-denoise pass by the same REF2VA model that made it —
    /// conditioned by the same reference stills and prompt. So the model re-draws the clip at the larger
    /// size from what it already knows, rather than interpolating pixels. Denoise is the dial that
    /// decides how much of the original survives.</para>
    ///
    /// <para>Graph: <c>workflow/video/h3-minimax/h3-hd-detailer.json</c>, produced from the authored UI
    /// export by <c>tools/convert_h3_hd_detailer.py</c> — which is also where the reasons for the
    /// deviations from the export live.</para>
    /// </summary>
    public partial class VideoEnhanceViewModel
    {
        private const string H3HdWorkflow = "workflow/video/h3-minimax/h3-hd-detailer.json";
        private const string H3HdOutputSubfolder = "H3HDEnhance";

        /// <summary>Reference slots the tab offers. <c>MiniMaxH3ReferenceToVideo</c> accepts nine; six
        /// is already more identity than a single re-render pass can hold on to.</summary>
        public const int H3HdMaxReferences = 6;

        // ── the nodes the tab drives ──────────────────────────────────────────────────────────
        private const string HdVideoNode = "657";        // VHS_LoadVideo — the clip and the frame cap
        private const string HdRef2VideoNode = "609";    // MiniMaxH3ReferenceToVideo — conditioning
        private const string HdPromptNode = "641";       // PrimitiveStringMultiline
        private const string HdResolutionNode = "707";   // ResolutionSelector — replaced, see BuildH3HdWorkflowAsync
        private const string HdResizeNode = "659";       // ImageResizeKJv2 — source frames to the base canvas
        private const string HdDetailMpNode = "772";     // PrimitiveFloat — MinimaxH3LatentUpscaler3D target
        private const string HdUpscalerNode = "711";     // MinimaxH3LatentUpscaler3D
        private const string HdSamplerNode = "669";      // ClownsharKSampler_Beta — steps + denoise
        private const string HdSeedNode = "713";         // SeedGenerator
        private const string HdCombineNode = "389";      // VHS_VideoCombine — the only output

        // Nodes the tab adds. The authored graph sizes its canvas with a ResolutionSelector, whose
        // aspect is one of eight presets; a clip that is 576×320 (H3's own draft canvas) is not any of
        // them, and the resize ahead of the encode stretches rather than crops, so a mismatched preset
        // distorts the whole render. Two literals computed from the source replace it.
        private const string HdCanvasWidthNode = "h3hd_canvas_w";
        private const string HdCanvasHeightNode = "h3hd_canvas_h";

        /// <summary>Reference loaders replace the graph's authored pair, one per filled slot.</summary>
        private const string HdReferenceNodePrefix = "h3hd_ref_";

        /// <summary>The loaders the export ships with. They are removed and rebuilt per run, so the
        /// number of references is not stuck at the two the author happened to have wired.</summary>
        private static readonly string[] HdAuthoredReferenceNodes = { "683", "682" };

        /// <summary>Target megapixels for the latent upscale, per detail level.</summary>
        private static readonly Dictionary<H3HdDetail, double> H3HdMegapixels = new()
        {
            [H3HdDetail.Hd] = 1.5,
            [H3HdDetail.TwoK] = 2.1,
            [H3HdDetail.TwoKPlus] = 3.0,
        };

        public static IReadOnlyList<H3HdDetail> H3HdDetailLevels { get; } =
            new[] { H3HdDetail.Hd, H3HdDetail.TwoK, H3HdDetail.TwoKPlus };

        /// <summary>How references are encoded. "max" is the authored value and the one that holds a
        /// face; "match" is the fast one.</summary>
        public static IReadOnlyList<string> H3HdFidelityOptions { get; } = new[] { "max", "match" };

        private string H3HdQueueFilePath => UserPaths.Queue("video_enhance_h3hd_queue.json");

        private string _h3HdVideoPath = string.Empty;
        private string _h3HdVideoInfo = string.Empty;
        private (int Width, int Height) _h3HdSourceSize;
        private int _h3HdSourceFrames;
        private string _h3HdPrompt = string.Empty;
        private H3HdDetail _h3HdDetail = H3HdDetail.TwoK;
        private double _h3HdBaseMegapixels = 0.8;
        private double _h3HdDenoise = 0.45;
        private int _h3HdSteps = 4;
        private long _h3HdSeed;
        private int _h3HdMaxFrames;
        private string _h3HdFidelity = "max";
        private bool _isProcessingH3HdQueue;
        private string _h3HdQueueStatus = string.Empty;
        private readonly ObservableCollection<H3HdEnhanceQueueItem> _h3HdQueue = new();
        private CancellationTokenSource? _h3HdCts;

        /// <summary>Wired from the constructor of the main partial.</summary>
        private void InitializeH3HdEnhance()
        {
            for (var slot = 1; slot <= H3HdMaxReferences; slot++)
            {
                var reference = new MiniMaxI2VReference(slot);
                reference.Changed += OnH3HdReferenceChanged;
                H3HdReferences.Add(reference);
            }

            SelectH3HdVideoCommand = new RelayCommand(SelectH3HdVideo);
            BrowseH3HdReferenceCommand = new RelayCommand<MiniMaxI2VReference>(
                async r => await BrowseH3HdReferenceAsync(r));
            ClearH3HdReferenceCommand = new RelayCommand<MiniMaxI2VReference>(r => r?.Clear());
            AddH3HdToQueueCommand = new RelayCommand(AddH3HdToQueue, () => CanAddH3Hd);
            RemoveH3HdQueueItemCommand = new RelayCommand<H3HdEnhanceQueueItem>(RemoveH3HdQueueItem);
            ClearH3HdQueueCommand = new RelayCommand(ClearH3HdQueue, () => _h3HdQueue.Any());
            StopH3HdQueueCommand = new RelayCommand(StopH3HdQueue, () => IsProcessingH3HdQueue);
            ReprocessH3HdFailedCommand = new RelayCommand(
                async () => await ReprocessH3HdFailedAsync(), () => HasH3HdFailedItems);

            _h3HdQueue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasH3HdQueueItems));
                UpdateH3HdQueueStatus();
                OnCanExecuteChanged();
            };

            LoadH3HdQueueFromFile();
        }

        #region Commands

        public RelayCommand SelectH3HdVideoCommand { get; private set; } = null!;
        public RelayCommand<MiniMaxI2VReference> BrowseH3HdReferenceCommand { get; private set; } = null!;
        public RelayCommand<MiniMaxI2VReference> ClearH3HdReferenceCommand { get; private set; } = null!;
        public RelayCommand AddH3HdToQueueCommand { get; private set; } = null!;
        public RelayCommand<H3HdEnhanceQueueItem> RemoveH3HdQueueItemCommand { get; private set; } = null!;
        public RelayCommand ClearH3HdQueueCommand { get; private set; } = null!;
        public RelayCommand StopH3HdQueueCommand { get; private set; } = null!;
        public RelayCommand ReprocessH3HdFailedCommand { get; private set; } = null!;

        public bool HasH3HdFailedItems => _h3HdQueue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        #endregion

        #region References

        /// <summary>The stills the clip was rendered from, in the order the prompt names them —
        /// "the woman from image 1" has to keep meaning the same picture on the re-render.</summary>
        public ObservableCollection<MiniMaxI2VReference> H3HdReferences { get; } = new();

        public IReadOnlyList<MiniMaxI2VReference> FilledH3HdReferences =>
            H3HdReferences.Where(r => r.HasImage).ToList();

        public string H3HdReferenceSummary
        {
            get
            {
                var filled = FilledH3HdReferences.Count;
                if (filled == 0)
                    return "No references — the pass will have only the prompt to hold identity together.";
                return $"{filled} reference{(filled == 1 ? string.Empty : "s")} carried into the re-render.";
            }
        }

        private void OnH3HdReferenceChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(FilledH3HdReferences));
            OnPropertyChanged(nameof(H3HdReferenceSummary));
        }

        private async Task BrowseH3HdReferenceAsync(MiniMaxI2VReference? reference)
        {
            if (reference == null) return;

            var path = await _fileDialogService.OpenFileDialogAsync(
                $"Select Reference Picture {reference.Slot}",
                "Image Files|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All Files|*.*");

            if (path == null) return;
            reference.Path = path;
            AddLog($"Enhance HD: reference {reference.Slot} = {Path.GetFileName(path)}");
        }

        #endregion

        #region Properties

        public string H3HdVideoPath
        {
            get => _h3HdVideoPath;
            set
            {
                if (_h3HdVideoPath == value) return;
                _h3HdVideoPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasH3HdVideo));
                OnPropertyChanged(nameof(CanAddH3Hd));
                LoadH3HdVideoInfo();
                OnCanExecuteChanged();
            }
        }

        public string H3HdVideoInfo
        {
            get => _h3HdVideoInfo;
            private set { if (_h3HdVideoInfo != value) { _h3HdVideoInfo = value; OnPropertyChanged(); } }
        }

        public bool HasH3HdVideo => !string.IsNullOrEmpty(H3HdVideoPath) && File.Exists(H3HdVideoPath);

        public bool CanAddH3Hd => HasH3HdVideo;

        /// <summary>The clip's original prompt, re-used as the conditioning for the re-render.</summary>
        public string H3HdPrompt
        {
            get => _h3HdPrompt;
            set { if (_h3HdPrompt != value) { _h3HdPrompt = value; OnPropertyChanged(); } }
        }

        public H3HdDetail H3HdDetailLevel
        {
            get => _h3HdDetail;
            set
            {
                if (_h3HdDetail == value) return;
                _h3HdDetail = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(H3HdDetailDescription));
                OnPropertyChanged(nameof(H3HdSegmentPlan));
            }
        }

        public IReadOnlyList<H3HdDetail> H3HdDetailOptions => H3HdDetailLevels;

        /// <summary>The finished size this detail level lands on for the clip that is loaded, so the
        /// number in the form means something before the render starts.</summary>
        public string H3HdDetailDescription
        {
            get
            {
                var mp = H3HdMegapixels[H3HdDetailLevel];
                var baseCanvas = PlanH3HdCanvas(_h3HdSourceSize, H3HdBaseMegapixels);
                var final = PlanH3HdCanvas(_h3HdSourceSize, mp);
                if (baseCanvas.Width == 0 || final.Width == 0)
                    return $"{mp:0.0} MP — load a clip to see the finished size.";
                // "about", and meant that way: the resize fits the frames inside the canvas and the
                // latent upscaler re-aligns to its own multiple of 32, so the rendered size lands
                // within a rounding step of this rather than on it.
                return $"{baseCanvas.Width}×{baseCanvas.Height} encoded → about {final.Width}×{final.Height} " +
                       $"rendered ({mp:0.0} MP).";
            }
        }

        /// <summary>
        /// How this clip will be cut up, shown in the form so a chunked job is never a surprise.
        /// H3 samples a clip as one sequence with no context window, so past a certain length the run
        /// is a hard OOM rather than a slow render — the tab splits instead, and rejoins afterwards.
        /// </summary>
        public string H3HdSegmentPlan
        {
            get
            {
                if (!HasH3HdVideo || _h3HdSourceFrames <= 0) return string.Empty;

                var frames = H3HdMaxFrames > 0
                    ? Math.Min(_h3HdSourceFrames, H3HdMaxFrames)
                    : _h3HdSourceFrames;
                var segments = PlanH3HdSegments(frames, H3HdChunkFrames(H3HdDetailLevel));

                if (segments.Count == 1)
                {
                    var dropped = frames - segments[0].Frames;
                    return $"One pass of {segments[0].Frames} frames" +
                           (dropped > 0
                               ? $" — H3 only accepts lengths of 5+17n, so the last {dropped} frame" +
                                 $"{(dropped == 1 ? " is" : "s are")} left off."
                               : ".");
                }

                return $"{frames} frames is more than one pass holds at this size — " +
                       $"{segments.Count} segments of {segments[0].Frames} frames, rendered in turn and " +
                       "rejoined. Each is sampled on its own, so a boundary can show a slight step in " +
                       "detail; a lower Detail level makes the segments longer and the steps fewer.";
            }
        }

        /// <summary>Megapixels the source is resized to before the encode. The latent upscaler works
        /// from this, so it is the floor on how much real detail the pass has to build on.</summary>
        public double H3HdBaseMegapixels
        {
            get => _h3HdBaseMegapixels;
            set
            {
                var clamped = Math.Clamp(Math.Round(value, 2), 0.2, 2.0);
                if (Math.Abs(_h3HdBaseMegapixels - clamped) < 0.001) return;
                _h3HdBaseMegapixels = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(H3HdDetailDescription));
            }
        }

        public double H3HdDenoise
        {
            get => _h3HdDenoise;
            set
            {
                var clamped = Math.Clamp(Math.Round(value, 2), 0.05, 1.0);
                if (Math.Abs(_h3HdDenoise - clamped) < 0.001) return;
                _h3HdDenoise = clamped;
                OnPropertyChanged();
            }
        }

        public int H3HdSteps
        {
            get => _h3HdSteps;
            set
            {
                var clamped = Math.Clamp(value, 1, 60);
                if (_h3HdSteps == clamped) return;
                _h3HdSteps = clamped;
                OnPropertyChanged();
            }
        }

        public long H3HdSeed
        {
            get => _h3HdSeed;
            set { if (_h3HdSeed != value) { _h3HdSeed = value; OnPropertyChanged(); } }
        }

        public int H3HdMaxFrames
        {
            get => _h3HdMaxFrames;
            set
            {
                var clamped = Math.Max(0, value);
                if (_h3HdMaxFrames == clamped) return;
                _h3HdMaxFrames = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(H3HdSegmentPlan));
            }
        }

        public string H3HdFidelity
        {
            get => _h3HdFidelity;
            set
            {
                if (_h3HdFidelity == value || string.IsNullOrEmpty(value)) return;
                _h3HdFidelity = value;
                OnPropertyChanged();
            }
        }

        public IReadOnlyList<string> H3HdFidelityChoices => H3HdFidelityOptions;

        public bool IsProcessingH3HdQueue
        {
            get => _isProcessingH3HdQueue;
            private set
            {
                if (_isProcessingH3HdQueue == value) return;
                _isProcessingH3HdQueue = value;
                OnPropertyChanged();
                OnCanExecuteChanged();
            }
        }

        public string H3HdQueueStatus
        {
            get => _h3HdQueueStatus;
            private set { if (_h3HdQueueStatus != value) { _h3HdQueueStatus = value; OnPropertyChanged(); } }
        }

        public ObservableCollection<H3HdEnhanceQueueItem> H3HdQueue => _h3HdQueue;

        public bool HasH3HdQueueItems => _h3HdQueue.Any();

        #endregion

        #region Selection

        private async void SelectH3HdVideo()
        {
            var initial = _settingsService.Settings?.EnhanceVideoFolder;
            if (string.IsNullOrEmpty(initial) || !Directory.Exists(initial))
                initial = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select the H3 Clip to Re-render",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv|All Files|*.*",
                initial);

            if (path == null) return;

            H3HdVideoPath = path;
            PersistBrowseFolder(Path.GetDirectoryName(path));
            AddLog($"Enhance HD: selected {Path.GetFileName(path)}");
        }

        /// <summary>
        /// Measures the picked clip. Probing shells out to ffprobe, so it runs off the setter's
        /// thread — see <see cref="VideoProcessingBaseViewModel.GetVideoDimensions"/>.
        /// </summary>
        private void LoadH3HdVideoInfo()
        {
            if (!HasH3HdVideo)
            {
                _h3HdSourceSize = default;
                _h3HdSourceFrames = 0;
                H3HdVideoInfo = string.Empty;
                OnPropertyChanged(nameof(H3HdDetailDescription));
                OnPropertyChanged(nameof(H3HdSegmentPlan));
                return;
            }

            var path = H3HdVideoPath;
            _ = Task.Run(() =>
            {
                string info;
                (int Width, int Height) size = default;
                var frames = 0;
                try
                {
                    var fi = new FileInfo(path);
                    size = GetVideoDimensions(path);
                    frames = GetVideoFrameCount(path);
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
                    if (!string.Equals(H3HdVideoPath, path, StringComparison.OrdinalIgnoreCase))
                        return;                       // the user moved on while ffprobe ran
                    _h3HdSourceSize = size;
                    _h3HdSourceFrames = frames;
                    H3HdVideoInfo = info;
                    OnPropertyChanged(nameof(H3HdDetailDescription));
                    OnPropertyChanged(nameof(H3HdSegmentPlan));
                });
            });
        }

        #endregion

        #region Queue

        private void AddH3HdToQueue()
        {
            if (!CanAddH3Hd) return;

            var item = new H3HdEnhanceQueueItem
            {
                InputVideoPath = H3HdVideoPath,
                ReferenceImagePaths = FilledH3HdReferences.Select(r => r.Path).ToList(),
                Prompt = H3HdPrompt,
                Detail = H3HdDetailLevel,
                BaseMegapixels = H3HdBaseMegapixels,
                Denoise = H3HdDenoise,
                Steps = H3HdSteps,
                Seed = H3HdSeed,
                MaxFrames = H3HdMaxFrames,
                ReferenceFidelity = H3HdFidelity,
                ItemStatus = QueueItemStatus.Pending
            };

            _h3HdQueue.Add(item);
            SaveH3HdQueueToFile();
            AddLog($"Added to Enhance HD queue: {item.DisplayText} — {item.SettingsDisplay}");
            UpdateH3HdQueueStatus();

            if (!IsProcessingH3HdQueue)
                _ = ProcessH3HdQueueAsync();
        }

        private void RemoveH3HdQueueItem(H3HdEnhanceQueueItem? item)
        {
            if (item != null && item.ItemStatus != QueueItemStatus.Processing)
                _h3HdQueue.Remove(item);
            UpdateH3HdQueueStatus();
        }

        private void UpdateH3HdQueueStatus()
        {
            if (_h3HdQueue.Count == 0) { H3HdQueueStatus = string.Empty; return; }
            var pending = _h3HdQueue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
            var done = _h3HdQueue.Count(x => x.ItemStatus == QueueItemStatus.Completed);
            var failed = _h3HdQueue.Count(x => x.ItemStatus == QueueItemStatus.Failed);
            H3HdQueueStatus = $"{pending} pending • {done} done • {failed} failed";
            OnPropertyChanged(nameof(HasH3HdFailedItems));
            OnCanExecuteChanged();
        }

        private void ClearH3HdQueue()
        {
            _h3HdCts?.Cancel();
            foreach (var item in _h3HdQueue.ToList())
                _h3HdQueue.Remove(item);
            SaveH3HdQueueToFile();
            UpdateH3HdQueueStatus();
            AddLog("Enhance HD queue cleared");
        }

        private void StopH3HdQueue()
        {
            _h3HdCts?.Cancel();
            AddLog("Enhance HD queue stop requested");
        }

        private async Task ReprocessH3HdFailedAsync()
        {
            var failed = _h3HdQueue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (!failed.Any()) return;
            foreach (var item in failed)
                item.ItemStatus = QueueItemStatus.Pending;
            UpdateH3HdQueueStatus();
            SaveH3HdQueueToFile();
            AddLog($"Reprocessing {failed.Count} failed Enhance HD item(s)...");
            if (!IsProcessingH3HdQueue)
                await ProcessH3HdQueueAsync();
        }

        private async Task ProcessH3HdQueueAsync()
        {
            if (IsProcessingH3HdQueue) return;
            IsProcessingH3HdQueue = true;
            _h3HdCts?.Dispose();
            _h3HdCts = new CancellationTokenSource();
            var token = _h3HdCts.Token;
            AddLog("Starting Enhance HD queue...");
            OnCanExecuteChanged();
            try
            {
                H3HdEnhanceQueueItem? item;
                while (!token.IsCancellationRequested &&
                       (item = _h3HdQueue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                {
                    item.ItemStatus = QueueItemStatus.Processing;
                    UpdateH3HdQueueStatus();
                    SaveH3HdQueueToFile();
                    try
                    {
                        await ProcessH3HdSingleAsync(item, token);
                        item.ItemStatus = QueueItemStatus.Completed;
                        AddLog($"Enhance HD complete: {item.DisplayText}");
                    }
                    catch (OperationCanceledException)
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
                        AddLog("Enhance HD item cancelled — reset to Pending");
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
                            AddLog($"Enhance HD FAILED: {ex.Message}");
                        }
                    }
                    UpdateH3HdQueueStatus();
                    SaveH3HdQueueToFile();
                }
            }
            finally
            {
                IsProcessingH3HdQueue = false;
                AddLog("Enhance HD queue finished.");
                OnCanExecuteChanged();
            }
        }

        private void SaveH3HdQueueToFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(H3HdQueueFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(H3HdQueueFilePath,
                    JsonSerializer.Serialize(_h3HdQueue.ToList(),
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AddLog($"Error saving Enhance HD queue: {ex.Message}"); }
        }

        private void LoadH3HdQueueFromFile()
        {
            try
            {
                if (!File.Exists(H3HdQueueFilePath)) return;
                var items = JsonSerializer.Deserialize<List<H3HdEnhanceQueueItem>>(
                    File.ReadAllText(H3HdQueueFilePath));
                if (items?.Any() != true) return;
                _h3HdQueue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Processing)
                        item.ItemStatus = QueueItemStatus.Pending;
                    _h3HdQueue.Add(item);
                }
                UpdateH3HdQueueStatus();
                AddLog($"Enhance HD queue loaded: {_h3HdQueue.Count} items");
            }
            catch (Exception ex) { AddLog($"Error loading Enhance HD queue: {ex.Message}"); }
        }

        #endregion

        #region Processing

        private async Task ProcessH3HdSingleAsync(H3HdEnhanceQueueItem item, CancellationToken token)
        {
            try
            {
                IsProcessing = true;
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing Enhance HD...";
                AddLog($"=== Enhance HD: {item.DisplayText} — {item.SettingsDisplay} ===");

                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    s => AddLog($"[ComfyUI] {s}"));
                if (!comfyUIOk) throw new Exception("ComfyUI is not running.");

                if (!_comfyUIService.IsConnected)
                    await _comfyUIService.ConnectAsync();

                ProcessingProgress = 2;
                ProcessingStatus = "Uploading clip...";
                if (!IsMp4Valid(item.InputVideoPath, out var validationError))
                    throw new Exception(
                        $"Input video is not a valid MP4: {validationError}\n\nPath: {item.InputVideoPath}");

                // H3 is an audio-video model: the graph encodes the clip's soundtrack into the same
                // latent it samples. A silent clip has no audio for VHS_LoadVideo to hand over, and the
                // run dies in the loader rather than anywhere that would explain itself.
                if (!HasAudioStream(item.InputVideoPath))
                    throw new Exception(
                        "This clip has no audio track. H3 samples video and audio as one latent, so the " +
                        "re-render needs the soundtrack the clip was generated with.");

                var uploadedName = await _comfyUIService.UploadVideoAsync(item.InputVideoPath);
                if (string.IsNullOrEmpty(uploadedName))
                    throw new Exception("Video upload failed.");
                AddLog($"Uploaded: {uploadedName}");

                var uploadedReferences = new List<string>();
                foreach (var referencePath in item.ReferenceImagePaths)
                {
                    if (!File.Exists(referencePath))
                    {
                        AddLog($"Reference missing, skipping: {referencePath}");
                        continue;
                    }
                    var name = await _comfyUIService.UploadImageAsync(referencePath);
                    if (string.IsNullOrEmpty(name))
                        throw new Exception($"Reference upload failed: {referencePath}");
                    uploadedReferences.Add(name!);
                }

                var totalFrames = GetVideoFrameCount(item.InputVideoPath);
                if (item.MaxFrames > 0) totalFrames = Math.Min(totalFrames, item.MaxFrames);
                var segments = PlanH3HdSegments(totalFrames, H3HdChunkFrames(item.Detail));
                var seed = item.Seed > 0 ? item.Seed : Random.Shared.NextInt64(1, int.MaxValue);

                if (segments.Count > 1)
                    AddLog($"{totalFrames} frames exceeds what one pass holds — rendering " +
                           $"{segments.Count} segments of {segments[0].Frames} frames and rejoining.");

                // One run token for the whole job so every segment's output is identifiable, and so a
                // half-finished job's files can be told apart from a previous one's.
                var runToken = $"h3hd_{DateTime.Now:yyyyMMdd_HHmmss}";
                var renderedSegments = new List<string>();

                for (var i = 0; i < segments.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var segment = segments[i];

                    // Each segment gets its own slice of the bar, so a long chunked job still moves.
                    var segmentStart = 5.0 + 85.0 * i / segments.Count;
                    var segmentEnd = 5.0 + 85.0 * (i + 1) / segments.Count;
                    var label = segments.Count > 1 ? $" (segment {i + 1}/{segments.Count})" : string.Empty;

                    ProcessingProgress = segmentStart;
                    ProcessingStatus = $"Re-rendering at HD{label}...";

                    var prefix = $"{H3HdOutputSubfolder}/{runToken}_s{i:00}";
                    var workflow = await BuildH3HdWorkflowAsync(
                        item, uploadedName!, uploadedReferences, prefix, segment, seed, i, segments.Count);

                    var existingFiles = GetExistingVideoFiles("*.mp4", H3HdOutputSubfolder);

                    var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(p =>
                    {
                        if (p.Data?.Value == null || p.Data?.Max == null || p.Data.Max <= 0) return;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = segmentStart +
                                (segmentEnd - segmentStart) * p.Data.Value / p.Data.Max;
                            ProcessingStatus = $"Sampling{label}: {p.Data.Value}/{p.Data.Max}";
                        });
                    });

                    var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress);
                    AddLog($"Segment {i + 1}/{segments.Count} submitted, ID: {promptId}");

                    var outputVideo = await TryGetVideoFromHistoryAsync(promptId);
                    if (outputVideo == null)
                    {
                        AddLog("Falling back to filesystem polling...");
                        outputVideo = await WaitForNewVideoAsync(existingFiles, "*.mp4",
                            TimeSpan.FromHours(3), TimeSpan.FromSeconds(10), H3HdOutputSubfolder);
                    }

                    token.ThrowIfCancellationRequested();

                    if (outputVideo == null || !File.Exists(outputVideo))
                        throw new Exception($"No output video found for segment {i + 1}.");

                    renderedSegments.Add(outputVideo);
                }

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), H3HdOutputSubfolder);
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"H3HD_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                ProcessingProgress = 92;
                if (renderedSegments.Count == 1)
                {
                    File.Copy(renderedSegments[0], finalPath, true);
                }
                else
                {
                    ProcessingStatus = "Rejoining segments...";
                    await JoinH3HdSegmentsAsync(renderedSegments, segments, finalPath, token);
                }

                item.OutputVideoPath = finalPath;
                ResultVideoPath = finalPath;
                await LocalCopyService.CopyVideoAsync(finalPath);
                HasResult = true;
                ResultVideoInfo = $"Enhance HD ({item.Detail}) • " +
                                  (segments.Count > 1 ? $"{segments.Count} segments • " : string.Empty) +
                                  $"{new FileInfo(finalPath).Length / 1024.0 / 1024.0:F1} MB";
                ProcessingProgress = 100;
                ProcessingStatus = "Enhance HD complete!";
                AddLog($"=== Done: {finalPath} ===");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        #endregion

        #region Segmenting

        /// <summary>One pass of the sampler: where it starts in the source, how many frames it takes,
        /// and how many of its rendered frames are a re-render of the previous segment's tail.</summary>
        private readonly record struct H3HdSegment(int Start, int Frames, int OverlapFrames);

        /// <summary>
        /// Frames a single pass may sample, for a detail level.
        ///
        /// <para>H3 has no context window here: the whole clip is one sequence, and the input
        /// projection allocates across every token of it at once, so the ceiling is a hard OOM rather
        /// than a slowdown. Measured on the 24 GB card this was built against: 124 frames at the 2.1 MP
        /// level (1952×1120 out) completes in 271 s; 872 frames at the same level dies allocating
        /// 5.62 GiB on top of 20.28 GiB already held. <see cref="H3HdFrameMegapixelBudget"/> is that
        /// measured point expressed as frames × megapixels, and the other levels are extrapolated from
        /// it — linearly in tokens, which is how the projection scales.</para>
        /// </summary>
        private static int H3HdChunkFrames(H3HdDetail detail)
        {
            var budget = H3HdFrameMegapixelBudget;

            // Scale off the card actually reported by /system_stats, keeping a fixed allowance for the
            // weights and the two VAEs, which do not grow with the clip. 0 means detection has not run,
            // so assume the card this was measured on.
            var vram = VramContext.DetectedVramGb > 0 ? VramContext.DetectedVramGb : 24.0;
            budget *= Math.Max(0.25, (vram - H3HdFixedVramGb) / (24.0 - H3HdFixedVramGb));

            return ValidH3Length((int)(budget / H3HdMegapixels[detail]));
        }

        /// <summary>124 frames × 2.1 MP, the one point that was actually measured to fit in 24 GB.</summary>
        private const double H3HdFrameMegapixelBudget = 124 * 2.1;

        /// <summary>VRAM the model, the LoRAs and the two VAEs hold regardless of clip length. Only what
        /// is left over scales with the sequence, so it is subtracted before scaling the budget.</summary>
        private const double H3HdFixedVramGb = 12.0;

        /// <summary>
        /// The largest frame count H3 accepts that is not longer than <paramref name="frames"/>.
        ///
        /// <para>H3 lengths are 5 + 17k. The graph derives its length from the loaded duration and
        /// rounds <em>up</em> to the next such number, so a segment whose frame count is not one of them
        /// builds conditioning for more frames than the encoder was given. Rounding down here is what
        /// keeps the two halves of the graph talking about the same clip.</para>
        /// </summary>
        private static int ValidH3Length(int frames) =>
            frames < 22 ? 5 : 5 + (frames - 5) / 17 * 17;

        /// <summary>
        /// Cuts a clip into passes of <paramref name="chunkFrames"/>.
        ///
        /// <para>The frame counts H3 accepts (5 + 17k) cannot tile an arbitrary length exactly — for a
        /// clip of 5 + 17m frames an exact tiling needs 1 or 18 or 35 segments and nothing in between —
        /// so segments have to overlap and the overlap is trimmed off each one's head at the join.
        /// Every segment is a full chunk and the starts are spread evenly across the clip, which shares
        /// the unavoidable overlap out between all of them rather than dumping it on the last: packing
        /// them tight instead leaves a remainder of a few frames that costs a whole extra pass to
        /// render. For 872 frames in chunks of 124 that is eight passes overlapping 17 frames each,
        /// against seven tight passes plus one that re-renders 120 frames to gain 4.</para>
        /// </summary>
        private static IReadOnlyList<H3HdSegment> PlanH3HdSegments(int totalFrames, int chunkFrames)
        {
            if (totalFrames <= chunkFrames)
                return new[] { new H3HdSegment(0, ValidH3Length(totalFrames), 0) };

            var count = (int)Math.Ceiling((double)totalFrames / chunkFrames);
            var stride = (double)(totalFrames - chunkFrames) / (count - 1);

            var segments = new List<H3HdSegment>(count);
            var previousStart = 0;
            for (var i = 0; i < count; i++)
            {
                var start = (int)Math.Round(i * stride);
                segments.Add(new H3HdSegment(start, chunkFrames,
                    i == 0 ? 0 : chunkFrames - (start - previousStart)));
                previousStart = start;
            }
            return segments;
        }

        /// <summary>
        /// Rejoins the rendered segments, trimming the head of any that overlaps its predecessor.
        ///
        /// <para>Re-encoded rather than stream-copied, for the reason the other H3 tabs give: H3 writes
        /// its own audio track per clip, and a copy-mode concat of separately encoded H3 outputs is
        /// where the timestamp and codec-parameter edge cases live. The trim is done as its own pass
        /// first, because the concat demuxer has no way to express "start this input late".</para>
        /// </summary>
        private async Task JoinH3HdSegmentsAsync(IReadOnlyList<string> rendered,
            IReadOnlyList<H3HdSegment> segments, string outPath, CancellationToken token)
        {
            var ffmpeg = FindFFmpeg() ?? throw new Exception(
                "FFmpeg was not found, so the rendered segments cannot be rejoined. " +
                $"They are in the ComfyUI output folder under {H3HdOutputSubfolder}.");

            var temporaries = new List<string>();
            try
            {
                var parts = new List<string>();
                for (var i = 0; i < rendered.Count; i++)
                {
                    if (segments[i].OverlapFrames <= 0) { parts.Add(rendered[i]); continue; }

                    var trimmed = Path.Combine(Path.GetTempPath(),
                        $"h3hd_trim_{Guid.NewGuid():N}.mp4");
                    temporaries.Add(trimmed);
                    // The sink always writes 24 fps, so the overlap converts straight to seconds.
                    var offset = (segments[i].OverlapFrames / 24.0).ToString("0.000",
                        System.Globalization.CultureInfo.InvariantCulture);
                    AddLog($"Segment {i + 1} overlaps the previous one by {segments[i].OverlapFrames} " +
                           $"frames; trimming {offset}s off its head.");
                    await RunFfmpegAsync(ffmpeg, new[]
                    {
                        "-y", "-ss", offset, "-i", rendered[i],
                        "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                        "-c:a", "aac", "-b:a", "192k", "-pix_fmt", "yuv420p", trimmed
                    }, token);
                    parts.Add(trimmed);
                }

                var listPath = Path.Combine(Path.GetTempPath(), $"h3hd_concat_{Guid.NewGuid():N}.txt");
                temporaries.Add(listPath);
                var sb = new System.Text.StringBuilder();
                foreach (var part in parts)
                {
                    // The concat demuxer reads a backslash as an escape and a single quote as the delimiter.
                    sb.AppendLine($"file '{part.Replace("\\", "/").Replace("'", @"'\''")}'");
                }
                await File.WriteAllTextAsync(listPath, sb.ToString(), token);

                await RunFfmpegAsync(ffmpeg, new[]
                {
                    "-y", "-f", "concat", "-safe", "0", "-i", listPath,
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-c:a", "aac", "-b:a", "192k", "-pix_fmt", "yuv420p", outPath
                }, token);
            }
            finally
            {
                foreach (var path in temporaries)
                {
                    try { File.Delete(path); } catch { /* temp file: best effort */ }
                }
            }
        }

        private static async Task RunFfmpegAsync(string ffmpeg, string[] args, CancellationToken token)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = System.Diagnostics.Process.Start(psi)
                          ?? throw new Exception("Failed to start FFmpeg.");
            // stderr is drained before the wait: FFmpeg logs everything there and blocks once the pipe
            // buffer fills, which would otherwise hang the join.
            var stderr = await p.StandardError.ReadToEndAsync(token);
            await p.WaitForExitAsync(token);
            if (p.ExitCode != 0)
            {
                var tail = stderr.Length <= 600 ? stderr : stderr[^600..];
                throw new Exception($"FFmpeg exited {p.ExitCode}: {tail}");
            }
        }

        #endregion

        #region Processing

        /// <summary>
        /// Patches the exported graph for one job.
        ///
        /// <para>Two structural edits. The <c>ResolutionSelector</c> that sizes the base canvas is
        /// replaced by two literals computed from the source clip, because its aspect is one of eight
        /// presets and the resize ahead of the encode stretches to whatever it is told — a clip whose
        /// shape is not on the list would be squeezed for the whole render. And the graph's two
        /// authored reference loaders are removed and rebuilt, one per filled slot, so the tab is not
        /// stuck at the two references the author happened to have wired.</para>
        ///
        /// <para>Every segment of a job is sampled with the <em>same</em> seed. At partial denoise the
        /// seed picks the noise field that is mixed into the source latent, so holding it fixed is what
        /// keeps two consecutive segments from being pushed in different directions across a cut.</para>
        /// </summary>
        private async Task<JsonElement> BuildH3HdWorkflowAsync(
            H3HdEnhanceQueueItem item, string uploadedVideo,
            IReadOnlyList<string> uploadedReferences, string filenamePrefix,
            H3HdSegment segment, long seed, int segmentIndex, int segmentCount)
        {
            var json = await ReadWorkflowAsync(H3HdWorkflow,
                HdVideoNode, HdRef2VideoNode, HdPromptNode, HdResolutionNode, HdResizeNode,
                HdDetailMpNode, HdUpscalerNode, HdSamplerNode, HdSeedNode, HdCombineNode);

            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new InvalidOperationException("Failed to parse the Enhance HD workflow.");

            var source = GetVideoDimensions(item.InputVideoPath);
            var baseCanvas = PlanH3HdCanvas(source, item.BaseMegapixels);
            var finalCanvas = PlanH3HdCanvas(source, H3HdMegapixels[item.Detail]);

            // ── Source clip ───────────────────────────────────────────────────
            // force_rate stays at the graph's 0: the sink encodes at a fixed 24fps and the frame count
            // the conditioning is built for comes off the loaded duration, so resampling here would
            // hand the sampler a different number of frames than the audio latent has.
            SetH3HdInput(root, HdVideoNode, "video", uploadedVideo);
            SetH3HdInput(root, HdVideoNode, "skip_first_frames", segment.Start);
            SetH3HdInput(root, HdVideoNode, "frame_load_cap", segment.Frames);

            // ── Canvas ────────────────────────────────────────────────────────
            root[HdCanvasWidthNode] = H3HdIntNode(baseCanvas.Width, "Enhance HD canvas width");
            root[HdCanvasHeightNode] = H3HdIntNode(baseCanvas.Height, "Enhance HD canvas height");
            RetargetH3Hd(root, HdResolutionNode, 0, HdCanvasWidthNode);
            RetargetH3Hd(root, HdResolutionNode, 1, HdCanvasHeightNode);
            root.Remove(HdResolutionNode);

            SetH3HdInput(root, HdDetailMpNode, "value", H3HdMegapixels[item.Detail]);

            // ── Conditioning ──────────────────────────────────────────────────
            SetH3HdInput(root, HdPromptNode, "value", item.Prompt ?? string.Empty);
            SetH3HdInput(root, HdRef2VideoNode, "ref_image_size", item.ReferenceFidelity);
            AttachH3HdReferences(root, uploadedReferences);

            // ── Sampler ───────────────────────────────────────────────────────
            SetH3HdInput(root, HdSamplerNode, "steps", item.Steps);
            SetH3HdInput(root, HdSamplerNode, "denoise", item.Denoise);
            SetH3HdInput(root, HdSeedNode, "seed", seed);

            // ── Sink ──────────────────────────────────────────────────────────
            SetH3HdInput(root, HdCombineNode, "filename_prefix", filenamePrefix);
            SetH3HdInput(root, HdCombineNode, "save_output", true);

            AddLog($"Enhance HD {baseCanvas.Width}×{baseCanvas.Height} → {finalCanvas.Width}×{finalCanvas.Height} " +
                   $"({H3HdMegapixels[item.Detail]:0.0} MP) • denoise {item.Denoise:0.##} • {item.Steps} steps • " +
                   $"{uploadedReferences.Count} ref ({item.ReferenceFidelity}) • seed {seed} • " +
                   $"segment {segmentIndex + 1}/{segmentCount}: frames {segment.Start}–" +
                   $"{segment.Start + segment.Frames - 1}");

            return JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
        }

        /// <summary>
        /// Base canvas for a clip: the source aspect at the requested megapixels, both sides rounded to
        /// a multiple of 32 — which is what the H3 VAE and the latent upscaler's alignment both want.
        ///
        /// <para>Holding the source aspect is what keeps the graph self-consistent. The resize ahead of
        /// the encode runs <c>keep_proportion: "resize"</c>, so it treats the canvas as a bounding box
        /// and fits the frames inside it — while <c>MiniMaxH3ReferenceToVideo</c> is told that canvas
        /// as a flat width and height. Feed a canvas of a different shape and the two disagree: the
        /// conditioning is built for one size and the latent arrives at another.</para>
        /// </summary>
        private static (int Width, int Height) PlanH3HdCanvas((int Width, int Height) source, double megapixels)
        {
            if (source.Width <= 0 || source.Height <= 0) return (0, 0);

            var aspect = (double)source.Width / source.Height;
            var height = Math.Sqrt(megapixels * 1_048_576 / aspect);
            var width = height * aspect;

            static int Align(double value) => Math.Max(32, (int)Math.Round(value / 32.0) * 32);
            return (Align(width), Align(height));
        }

        /// <summary>Rebuilds the reference loaders: one per uploaded picture, wired into the autogrow
        /// <c>ref_image_</c> slots in order, with the authored pair removed.</summary>
        private static void AttachH3HdReferences(JsonObject root, IReadOnlyList<string> uploadedReferences)
        {
            foreach (var nodeId in HdAuthoredReferenceNodes)
                root.Remove(nodeId);

            if (root[HdRef2VideoNode]?["inputs"] is not JsonObject inputs) return;

            foreach (var key in inputs.Select(kv => kv.Key)
                                      .Where(k => k.StartsWith("ref_images.ref_image_", StringComparison.Ordinal))
                                      .ToList())
                inputs.Remove(key);

            for (var i = 0; i < uploadedReferences.Count; i++)
            {
                var nodeId = HdReferenceNodePrefix + i;
                root[nodeId] = new JsonObject
                {
                    // The authored loader's settings: cap the long edge at 1344 keeping aspect, and land
                    // on a multiple of 32 so the reference encoder does not pad.
                    ["inputs"] = new JsonObject
                    {
                        ["image"] = uploadedReferences[i],
                        ["resize"] = true,
                        ["width"] = 1344,
                        ["height"] = 1344,
                        ["repeat"] = 1,
                        ["keep_proportion"] = true,
                        ["divisible_by"] = 32,
                        ["mask_channel"] = "alpha",
                        ["background_color"] = ""
                    },
                    ["class_type"] = "LoadAndResizeImage",
                    ["_meta"] = new JsonObject { ["title"] = $"H3 REF IMAGE {i + 1}" }
                };
                inputs[$"ref_images.ref_image_{i}"] = new JsonArray(nodeId, 0);
            }
        }

        /// <summary>A literal INT the rest of the graph can link to.</summary>
        private static JsonObject H3HdIntNode(int value, string title) => new()
        {
            ["inputs"] = new JsonObject { ["value"] = value },
            ["class_type"] = "PrimitiveInt",
            ["_meta"] = new JsonObject { ["title"] = title }
        };

        private static void SetH3HdInput(JsonObject root, string nodeId, string input, JsonNode? value)
        {
            if (root[nodeId]?["inputs"] is JsonObject inputs)
                inputs[input] = value;
        }

        /// <summary>
        /// Repoints every link that reads <paramref name="slot"/> of <paramref name="sourceId"/> at slot 0
        /// of <paramref name="newId"/> instead — how one node's output is substituted without having to
        /// know which nodes consume it.
        /// </summary>
        private static void RetargetH3Hd(JsonObject root, string sourceId, int slot, string newId)
        {
            foreach (var node in root)
            {
                if (node.Value?["inputs"] is not JsonObject inputs) continue;

                foreach (var input in inputs.ToList())
                {
                    if (input.Value is not JsonArray link || link.Count != 2) continue;
                    if (link[0]?.ToString() != sourceId) continue;
                    if (link[1] is not JsonValue index || !index.TryGetValue<int>(out var i) || i != slot)
                        continue;

                    inputs[input.Key] = new JsonArray(newId, 0);
                }
            }
        }

        #endregion

        private void OnH3HdCanExecuteChanged()
        {
            AddH3HdToQueueCommand?.NotifyCanExecuteChanged();
            ClearH3HdQueueCommand?.NotifyCanExecuteChanged();
            StopH3HdQueueCommand?.NotifyCanExecuteChanged();
            ReprocessH3HdFailedCommand?.NotifyCanExecuteChanged();
        }
    }
}
