using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// LTX Director — drop images onto a horizontal timeline, give each its own prompt
    /// and duration, and generate a multi-shot video via the LTXDirector node (6822).
    /// </summary>
    public partial class LtxDirectorViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/ltx/LTX23_V3_LTXDirector_api.json";
        private const string OutputSubfolder = "ltx_director";

        // LTXDirector node + the satellite nodes we drive from the UI.
        private const string DirectorNode = "6822";
        private const string NegativeNode = "6746";
        private const string StepsNode = "6995";
        // The workflow's CustomCombo nodes (7032/7026/7035) can't resolve their index in
        // API mode (option lists are UI-only), so they always fall back to index 0. We bypass
        // them and drive the nodes they ultimately control. The OUTPUT resolution is governed by
        // the empty reference image's pixel size (7030, always landscape long×short; 7029 rotates
        // it for portrait), and orientation by which one the ImpactSwitch 7034 selects:
        //   7030.width/height — empty reference image size = output resolution
        //   7034.select       — landscape (2) vs portrait (1) reference canvas
        //   7021.value        — director custom long-side (kept consistent with 7030)
        private const string EmptyImageNode = "7030";
        private const string OrientationSwitchNode = "7034";
        private const string LongSideNode = "7021";
        private static readonly string[] SeedNodes = { "6825", "6832", "6848" };
        private const string GlobalSeedNode = "6884";

        public static readonly string[] ResolutionOptions = { "360p", "480p", "720p", "1080p", "1440p", "4k" };
        public static readonly string[] OrientationOptions = { "Landscape", "Portrait" };

        // easy globalSeed (node 6884) rejects values above 2^50. Cap all seeds to this.
        private const long MaxSeed = 1125899906842624L;

        private string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "ltx_director_queue.json");

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private readonly ObservableCollection<LtxDirectorTimelineItem> _timeline = new();
        private readonly ObservableCollection<LtxDirectorQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;
        private CancellationTokenSource? _analyzeCts;

        public LtxDirectorViewModel(
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

            AddImagesCommand = new RelayCommand(async () => await AddImagesAsync());
            RemoveTimelineItemCommand = new RelayCommand<LtxDirectorTimelineItem>(RemoveTimelineItem);
            MoveLeftCommand = new RelayCommand(MoveSelectedLeft, () => CanMoveLeft);
            MoveRightCommand = new RelayCommand(MoveSelectedRight, () => CanMoveRight);
            ClearTimelineCommand = new RelayCommand(ClearTimeline, () => HasTimeline);
            AnalyzeSelectedCommand = new RelayCommand(async () => await AnalyzeSelectedAsync(), () => CanAnalyze);
            AnalyzeAllCommand = new RelayCommand(async () => await AnalyzeAllAsync(), () => CanAnalyzeAll);
            GenerateVideoCommand = new RelayCommand(AddToQueue, () => CanAddToQueue);
            RemoveQueueItemCommand = new RelayCommand<LtxDirectorQueueItem>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => _queue.Any());
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            StartQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => HasQueueItems && !IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(async () => await ReprocessAllFailedAsync(), () => HasFailedItems);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);
            RandomSeedCommand = new RelayCommand(() => Seed = new Random().NextInt64(0, MaxSeed));

            _timeline.CollectionChanged += (s, e) =>
            {
                Reindex();
                OnPropertyChanged(nameof(HasTimeline));
                OnPropertyChanged(nameof(TimelineSummary));
                OnPropertyChanged(nameof(CanAddToQueue));
                OnPropertyChanged(nameof(CanAnalyzeAll));
                OnCanExecuteChanged();
            };
            _queue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
                OnCanExecuteChanged();
            };

            AddLog("LTX Director initialized");
            LoadQueueFromFile();
        }

        #region Commands
        public ICommand AddImagesCommand { get; }
        public RelayCommand<LtxDirectorTimelineItem> RemoveTimelineItemCommand { get; }
        public RelayCommand MoveLeftCommand { get; }
        public RelayCommand MoveRightCommand { get; }
        public RelayCommand ClearTimelineCommand { get; }
        public RelayCommand AnalyzeSelectedCommand { get; }
        public RelayCommand AnalyzeAllCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand<LtxDirectorQueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand StartQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }
        public RelayCommand RandomSeedCommand { get; }

        public bool HasFailedItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Failed);
        #endregion

        #region Timeline
        public ObservableCollection<LtxDirectorTimelineItem> Timeline => _timeline;
        public IReadOnlyList<string> ResolutionChoices => ResolutionOptions;
        public IReadOnlyList<string> OrientationChoices => OrientationOptions;

        private LtxDirectorTimelineItem? _selectedTimelineItem;
        public LtxDirectorTimelineItem? SelectedTimelineItem
        {
            get => _selectedTimelineItem;
            set
            {
                if (_selectedTimelineItem != value)
                {
                    if (_selectedTimelineItem != null) _selectedTimelineItem.IsSelected = false;
                    _selectedTimelineItem = value;
                    if (_selectedTimelineItem != null) _selectedTimelineItem.IsSelected = true;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(CanAnalyze));
                    OnCanExecuteChanged();
                }
            }
        }

        public bool HasTimeline => _timeline.Any();
        public bool HasSelection => _selectedTimelineItem != null;
        public int TotalFrames => _timeline.Sum(s => FramesFor(s.DurationSeconds));
        public double TotalSeconds => _timeline.Sum(s => s.DurationSeconds);

        public string TimelineSummary => _timeline.Count == 0
            ? "Drop images here to start your timeline"
            : $"{_timeline.Count} shot{(_timeline.Count == 1 ? "" : "s")} · {TotalSeconds:0.0}s · {TotalFrames} frames @ {FrameRate}fps";

        private int FramesFor(double seconds) => Math.Max(1, (int)Math.Round(seconds * FrameRate));

        private async Task AddImagesAsync()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var paths = await _fileDialogService.OpenFilesDialogAsync(
                "Add Timeline Images",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: "ltxdirector.images");

            if (paths != null && paths.Length > 0)
                AddImagesFromPaths(paths);
        }

        /// <summary>Adds image files to the end of the timeline (used by Browse and drag-drop).</summary>
        public void AddImagesFromPaths(IEnumerable<string> paths)
        {
            var imageExts = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
            int added = 0;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                if (!imageExts.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;

                var item = new LtxDirectorTimelineItem(path);
                item.PropertyChanged += OnTimelineItemChanged;
                _timeline.Add(item);
                added++;
            }
            if (added > 0)
            {
                AddLog($"Added {added} image{(added == 1 ? "" : "s")} to timeline");
                SelectedTimelineItem ??= _timeline.LastOrDefault();
            }
        }

        private void OnTimelineItemChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LtxDirectorTimelineItem.DurationSeconds))
            {
                OnPropertyChanged(nameof(TimelineSummary));
                OnPropertyChanged(nameof(TotalFrames));
                OnPropertyChanged(nameof(TotalSeconds));
            }
        }

        private void RemoveTimelineItem(LtxDirectorTimelineItem? item)
        {
            if (item == null) return;
            item.PropertyChanged -= OnTimelineItemChanged;
            var idx = _timeline.IndexOf(item);
            _timeline.Remove(item);
            if (SelectedTimelineItem == item)
                SelectedTimelineItem = _timeline.ElementAtOrDefault(Math.Min(idx, _timeline.Count - 1));
        }

        public bool CanMoveLeft => SelectedTimelineItem != null && _timeline.IndexOf(SelectedTimelineItem) > 0;
        public bool CanMoveRight => SelectedTimelineItem != null &&
                                    _timeline.IndexOf(SelectedTimelineItem) >= 0 &&
                                    _timeline.IndexOf(SelectedTimelineItem) < _timeline.Count - 1;

        private void MoveSelectedLeft()
        {
            if (!CanMoveLeft) return;
            var idx = _timeline.IndexOf(SelectedTimelineItem!);
            _timeline.Move(idx, idx - 1);
            OnCanExecuteChanged();
        }

        private void MoveSelectedRight()
        {
            if (!CanMoveRight) return;
            var idx = _timeline.IndexOf(SelectedTimelineItem!);
            _timeline.Move(idx, idx + 1);
            OnCanExecuteChanged();
        }

        private void ClearTimeline()
        {
            foreach (var item in _timeline) item.PropertyChanged -= OnTimelineItemChanged;
            _timeline.Clear();
            SelectedTimelineItem = null;
        }

        private void Reindex()
        {
            for (int i = 0; i < _timeline.Count; i++)
                _timeline[i].Index = i + 1;
            OnPropertyChanged(nameof(TotalFrames));
            OnPropertyChanged(nameof(TotalSeconds));
        }
        #endregion

        #region Global options
        private string _globalPrompt = string.Empty;
        public string GlobalPrompt { get => _globalPrompt; set => SetField(ref _globalPrompt, value); }

        private string _negativePrompt = "blurry, oversaturated, pixelated, low resolution, grainy, distorted, noise, static image, compression artifacts, jpeg artifacts, watermark, text, logo, signature, deformed anatomy, extra limbs, bad hands, mutated hands, bad proportions, missing limbs";
        public string NegativePrompt { get => _negativePrompt; set => SetField(ref _negativePrompt, value); }

        private int _frameRate = 24;
        public int FrameRate
        {
            get => _frameRate;
            set
            {
                if (SetField(ref _frameRate, value <= 0 ? 24 : value))
                {
                    OnPropertyChanged(nameof(TimelineSummary));
                    OnPropertyChanged(nameof(TotalFrames));
                }
            }
        }

        private double _guideStrength = 1.0;
        public double GuideStrength { get => _guideStrength; set => SetField(ref _guideStrength, Math.Clamp(value, 0, 1)); }

        private double _epsilon = 0.001;
        public double Epsilon { get => _epsilon; set => SetField(ref _epsilon, value); }

        private int _imgCompression = 18;
        public int ImgCompression { get => _imgCompression; set => SetField(ref _imgCompression, Math.Clamp(value, 0, 100)); }

        private int _steps = 8;
        public int Steps { get => _steps; set => SetField(ref _steps, Math.Clamp(value, 1, 50)); }

        private string _resolution = "1080p";
        public string Resolution { get => _resolution; set => SetField(ref _resolution, value); }

        private string _orientation = "Landscape";
        public string Orientation { get => _orientation; set => SetField(ref _orientation, value); }

        private bool _useCustomAudio;
        public bool UseCustomAudio { get => _useCustomAudio; set => SetField(ref _useCustomAudio, value); }

        private long _seed = -1;
        public long Seed { get => _seed; set => SetField(ref _seed, value); }

        private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
        #endregion

        #region Analyze
        private bool _isAnalyzing;
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
                    OnPropertyChanged(nameof(CanAnalyzeAll));
                    OnCanExecuteChanged();
                }
            }
        }

        public bool CanAnalyze => SelectedTimelineItem != null && !IsAnalyzing && !IsProcessing;
        public bool CanAnalyzeAll => HasTimeline && !IsAnalyzing && !IsProcessing;
        public bool CanAddToQueue => HasTimeline;

        private Task AnalyzeSelectedAsync()
        {
            var target = SelectedTimelineItem;
            return target == null
                ? Task.CompletedTask
                : RunAnalysisAsync(new[] { target });
        }

        /// <summary>Analyzes every shot in order (#1 → last), selecting each as it goes.</summary>
        private Task AnalyzeAllAsync() => RunAnalysisAsync(_timeline.ToList());

        private async Task RunAnalysisAsync(System.Collections.Generic.IReadOnlyList<LtxDirectorTimelineItem> targets)
        {
            var shots = targets.Where(t => File.Exists(t.ImagePath)).ToList();
            if (shots.Count == 0) return;

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
                    MessageBox.Show("No LM Studio model available. Ensure LM Studio is running and a model is loaded.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "ltx-director.md");
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"System prompt not found: {promptFilePath}");
                var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

                if (shots.Count > 1)
                    AddLog($"Analyzing {shots.Count} shots — sending images to {_lmStudioService.DescribeTarget(selectedModel)}");

                foreach (var target in shots)
                {
                    token.ThrowIfCancellationRequested();
                    SelectedTimelineItem = target; // visual progress: highlight the shot being analyzed
                    AddLog($"Analyzing shot #{target.Index}…");

                    var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                        selectedModel, target.ImagePath,
                        "Analyze this keyframe and write the motion + audio prompt for this shot.",
                        systemPrompt, maxTokens: 4000, cancellationToken: token);

                    var cleaned = CleanOutput(result);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        target.Prompt = cleaned;
                        AddLog($"Shot #{target.Index} prompt generated ({cleaned.Length} chars)");
                    }
                    else AddLog($"WARNING: Shot #{target.Index} analysis returned empty result");
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
            var lower = text.ToLowerInvariant();
            if (lower.StartsWith("prompt:") || lower.StartsWith("prompt :"))
                text = text.Substring(text.IndexOf(':') + 1).Trim();
            return text;
        }
        #endregion

        #region Queue
        public ObservableCollection<LtxDirectorQueueItem> Queue => _queue;

        private bool _isProcessingQueue;
        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            private set { if (_isProcessingQueue != value) { _isProcessingQueue = value; OnPropertyChanged(); OnCanExecuteChanged(); } }
        }

        private string _queueStatus = string.Empty;
        public string QueueStatus { get => _queueStatus; private set { if (_queueStatus != value) { _queueStatus = value; OnPropertyChanged(); } } }

        public bool HasQueueItems => _queue.Any();

        private void AddToQueue()
        {
            if (!CanAddToQueue) return;

            var item = new LtxDirectorQueueItem
            {
                Segments = _timeline.Select(t => new LtxDirectorSegment
                {
                    ImagePath = t.ImagePath,
                    Prompt = string.IsNullOrWhiteSpace(t.Prompt)
                        ? "Style: realistic - cinematic - smooth natural motion, photorealistic, high quality"
                        : t.Prompt,
                    DurationSeconds = t.DurationSeconds
                }).ToList(),
                GlobalPrompt = GlobalPrompt,
                NegativePrompt = NegativePrompt,
                FrameRate = FrameRate,
                GuideStrength = GuideStrength,
                Epsilon = Epsilon,
                ImgCompression = ImgCompression,
                Steps = Steps,
                Resolution = Resolution,
                Orientation = Orientation,
                UseCustomAudio = UseCustomAudio,
                Seed = Seed,
                ItemStatus = QueueItemStatus.Pending
            };

            _queue.Add(item);
            SaveQueueToFile();
            AddLog($"Added to queue: {item.DisplayText}");
            UpdateQueueStatus();

            if (!IsProcessingQueue) _ = ProcessQueueAsync();
        }

        private void RemoveQueueItem(LtxDirectorQueueItem? item)
        {
            if (item != null && item.ItemStatus != QueueItemStatus.Processing)
            {
                _queue.Remove(item);
                SaveQueueToFile();
                UpdateQueueStatus();
            }
        }

        private void UpdateQueueStatus()
        {
            var pending = _queue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
            var completed = _queue.Count(x => x.ItemStatus == QueueItemStatus.Completed);
            var failed = _queue.Count(x => x.ItemStatus == QueueItemStatus.Failed);
            QueueStatus = _queue.Count == 0 ? string.Empty : $"{pending} pending • {completed} done • {failed} failed";
            OnPropertyChanged(nameof(HasFailedItems));
            OnCanExecuteChanged();
        }

        private void ClearQueue()
        {
            _queueCts?.Cancel();
            _queue.Clear();
            SaveQueueToFile();
            UpdateQueueStatus();
            AddLog("Queue cleared");
        }

        private void StopQueue() => _queueCts?.Cancel();

        private async Task ReprocessAllFailedAsync()
        {
            var failed = _queue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (!failed.Any()) return;
            foreach (var item in failed) item.ItemStatus = QueueItemStatus.Pending;
            UpdateQueueStatus();
            SaveQueueToFile();
            if (!IsProcessingQueue) await ProcessQueueAsync();
        }

        private async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;
            IsProcessingQueue = true;
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;

            WorkflowQueueCoordinator.WorkflowLease lease;
            try
            {
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("LtxDirector", token);
            }
            catch (OperationCanceledException)
            {
                IsProcessingQueue = false;
                OnCanExecuteChanged();
                return;
            }

            AddLog("Starting LTX Director queue...");
            using (lease)
            try
            {
                LtxDirectorQueueItem? item;
                while (!token.IsCancellationRequested &&
                       (item = _queue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                {
                    item.ItemStatus = QueueItemStatus.Processing;
                    UpdateQueueStatus();
                    SaveQueueToFile();
                    try
                    {
                        await GenerateSingleVideoAsync(item, token);
                        item.ItemStatus = QueueItemStatus.Completed;
                        AddLog($"Completed: {item.DisplayText}");
                    }
                    catch (OperationCanceledException)
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
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
                            AddLog($"FAILED: {ex.Message}");
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
                OnCanExecuteChanged();
            }
        }
        #endregion

        #region Queue Persistence
        private void SaveQueueToFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(QueueFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
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
                var items = JsonSerializer.Deserialize<List<LtxDirectorQueueItem>>(File.ReadAllText(QueueFilePath));
                if (items?.Any() != true) return;
                _queue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Processing) item.ItemStatus = QueueItemStatus.Pending;
                    _queue.Add(item);
                }
                UpdateQueueStatus();
                AddLog($"Queue loaded: {_queue.Count} items");
                if (_queue.Any(x => x.ItemStatus == QueueItemStatus.Pending))
                    _ = ProcessQueueAsync();
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }
        #endregion

        #region Generation
        private async Task GenerateSingleVideoAsync(LtxDirectorQueueItem item, CancellationToken token)
        {
            try
            {
                IsProcessing = true;
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing LTX Director workflow...";

                AddLog($"=== LTX Director: {item.DisplayText} ===");

                ProcessingStatus = "Checking ComfyUI...";
                if (!await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}")))
                    throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFileName);
                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
                var workflowJson = await File.ReadAllTextAsync(workflowPath, token);

                ProcessingStatus = "Uploading timeline images...";
                ProcessingProgress = 5;

                // Upload each shot's image and build timeline segments.
                var segments = new List<Dictionary<string, object>>();
                var lengths = new List<int>();
                var prompts = new List<string>();
                int start = 0;
                for (int i = 0; i < item.Segments.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var seg = item.Segments[i];
                    AddLog($"Uploading shot #{i + 1}: {Path.GetFileName(seg.ImagePath)}");
                    var uploaded = await _comfyUIService.UploadImageAsync(seg.ImagePath);
                    if (string.IsNullOrEmpty(uploaded))
                        throw new Exception($"Failed to upload image for shot #{i + 1}.");

                    var length = FramesForFps(seg.DurationSeconds, item.FrameRate);
                    segments.Add(new Dictionary<string, object>
                    {
                        ["id"] = Guid.NewGuid().ToString("N").Substring(0, 13),
                        ["start"] = start,
                        ["length"] = length,
                        ["prompt"] = seg.Prompt,
                        ["type"] = "image",
                        ["imageFile"] = uploaded,
                        ["imageB64"] = $"/api/view?filename={Uri.EscapeDataString(uploaded)}&type=input&subfolder="
                    });
                    lengths.Add(length);
                    prompts.Add(seg.Prompt);
                    start += length;
                    ProcessingProgress = 5 + (i + 1) / (double)item.Segments.Count * 10;
                }

                int totalFrames = lengths.Sum();
                double totalSeconds = (double)totalFrames / item.FrameRate;

                var timelineData = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["segments"] = segments,
                    ["audioSegments"] = Array.Empty<object>()
                });

                var runSeed = item.Seed >= 0 ? Math.Min(item.Seed, MaxSeed) : new Random().NextInt64(0, MaxSeed);
                ProcessingStatus = "Building workflow...";
                ProcessingProgress = 18;

                var json = workflowJson;
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, DirectorNode, new Dictionary<string, object>
                {
                    ["timeline_data"] = timelineData,
                    ["global_prompt"] = item.GlobalPrompt ?? string.Empty,
                    ["duration_frames"] = totalFrames,
                    ["duration_seconds"] = Math.Round(totalSeconds, 3),
                    ["local_prompts"] = string.Join(" | ", prompts),
                    ["segment_lengths"] = string.Join(",", lengths),
                    ["guide_strength"] = item.GuideStrength.ToString("0.00", CultureInfo.InvariantCulture),
                    ["epsilon"] = item.Epsilon,
                    ["frame_rate"] = item.FrameRate,
                    ["use_custom_audio"] = item.UseCustomAudio,
                    ["display_mode"] = "seconds",
                    ["resize_method"] = "maintain aspect ratio",
                    ["divisible_by"] = 32,
                    ["img_compression"] = item.ImgCompression
                });

                WorkflowNodeUpdater.UpdateNodeInput(ref json, NegativeNode, "text", item.NegativePrompt ?? string.Empty);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, StepsNode, "steps", item.Steps);
                var isLandscape = string.Equals(item.Orientation, "Landscape", StringComparison.OrdinalIgnoreCase);
                var (resW, resH) = ResolutionDimsFor(item.Resolution); // landscape long×short
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, EmptyImageNode, new Dictionary<string, object>
                {
                    ["width"] = resW,
                    ["height"] = resH
                });
                WorkflowNodeUpdater.UpdateNodeInput(ref json, OrientationSwitchNode, "select", isLandscape ? 2 : 1);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, LongSideNode, "value", resW);
                AddLog($"✓ Resolution {item.Resolution} {(isLandscape ? $"{resW}x{resH}" : $"{resH}x{resW}")}");
                foreach (var seedNode in SeedNodes)
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, seedNode, "noise_seed", runSeed);
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, GlobalSeedNode, new Dictionary<string, object>
                {
                    ["value"] = runSeed,
                    ["last_seed"] = runSeed
                });

                AddLog($"✓ Timeline: {segments.Count} shots, {totalFrames} frames @ {item.FrameRate}fps, seed={runSeed}");
                var workflow = JsonSerializer.Deserialize<JsonElement>(json);

                ProcessingProgress = 22;
                ProcessingStatus = "Generating video...";
                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 22 + pct * 0.73;
                            ProcessingStatus = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                        });
                    }
                });

                var existingFiles = GetExistingVideoFiles("*.mp4", OutputSubfolder);
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress);
                AddLog($"Workflow submitted, ID: {promptId}");

                ProcessingProgress = 95;
                ProcessingStatus = "Waiting for output...";

                var outputVideo = await TryGetVideoFromHistoryAsync(promptId);
                if (outputVideo == null)
                {
                    AddLog("Falling back to filesystem polling...");
                    outputVideo = await WaitForNewVideoAsync(existingFiles, "*.mp4",
                        TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5), OutputSubfolder);
                }
                if (outputVideo == null || !File.Exists(outputVideo))
                    throw new Exception("No output video was generated.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "LtxDirector");
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"LtxDirector_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                File.Copy(outputVideo, finalPath, true);

                item.OutputVideoPath = finalPath;
                ResultVideoPath = finalPath;
                await LocalCopyService.CopyVideoAsync(finalPath);
                HasResult = true;
                var fi = new FileInfo(finalPath);
                ResultVideoInfo = $"LTX Director • {item.Segments.Count} shots • {fi.Length / 1024 / 1024.0:F1}MB";
                ProcessingProgress = 100;
                ProcessingStatus = "LTX Director Complete!";
                AddLog($"=== Complete: {finalPath} ===");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                ProcessingStatus = "Error";
                throw;
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private static int FramesForFps(double seconds, int fps) => Math.Max(1, (int)Math.Round(seconds * fps));

        /// <summary>Landscape (width, height) pixel dimensions for a resolution preset.</summary>
        private static (int Width, int Height) ResolutionDimsFor(string resolution) => resolution switch
        {
            "360p" => (640, 360),
            "480p" => (854, 480),
            "720p" => (1280, 720),
            "1080p" => (1920, 1080),
            "1440p" => (2560, 1440),
            "4k" => (3840, 2160),
            _ => (1920, 1080)
        };
        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(CanMoveLeft));
            OnPropertyChanged(nameof(CanMoveRight));
            OnPropertyChanged(nameof(CanAnalyze));
            OnPropertyChanged(nameof(CanAnalyzeAll));
            OnPropertyChanged(nameof(CanAddToQueue));
            MoveLeftCommand.NotifyCanExecuteChanged();
            MoveRightCommand.NotifyCanExecuteChanged();
            ClearTimelineCommand.NotifyCanExecuteChanged();
            AnalyzeSelectedCommand.NotifyCanExecuteChanged();
            AnalyzeAllCommand.NotifyCanExecuteChanged();
            GenerateVideoCommand.NotifyCanExecuteChanged();
            RemoveQueueItemCommand.NotifyCanExecuteChanged();
            ClearQueueCommand.NotifyCanExecuteChanged();
            StopQueueCommand.NotifyCanExecuteChanged();
            StartQueueCommand.NotifyCanExecuteChanged();
            ReprocessAllFailedCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            SendToEditCameraCommand.NotifyCanExecuteChanged();
        }
    }
}
