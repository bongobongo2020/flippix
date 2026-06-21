using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    /// Video Sound — upload a clip, click Analyze to read its first frame into a
    /// [VISUAL]/[SPEECH]/[SOUNDS] directing prompt, then re-generate the clip with synchronized
    /// speech and sound effects through the LTX-2.3 audio-video workflow
    /// (workflow/video/ltx/VideoSound.json). An optional reference voice clip drives the
    /// LTXVReferenceAudio ID-LoRA so the spoken lines keep a chosen voice.
    /// </summary>
    public partial class VideoSoundViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/ltx/VideoSound.json";
        private const string OutputSubfolder = "video";
        private const string OutputPattern = "LTX_2.3_id_lora*.mp4";

        // Workflow nodes driven from the UI.
        private const string LoadVideoNode = "379";       // VHS_LoadVideo
        private const string PromptNode = "358:319";       // PrimitiveStringMultiline (prompt)
        private const string SeedNode = "358:286";         // RandomNoise (hi-res pass)
        private const string ReferenceAudioNode = "356";   // LoadAudio (reference voice)
        private const string WidthNode = "358:330";        // PrimitiveInt (width)
        private const string HeightNode = "358:324";       // PrimitiveInt (height)

        private string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "videosound_queue.json");

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private readonly ObservableCollection<VideoSoundQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;
        private CancellationTokenSource? _analyzeCts;

        public VideoSoundViewModel(
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

            SelectVideoCommand = new RelayCommand(SelectVideo);
            SelectReferenceAudioCommand = new RelayCommand(SelectReferenceAudio);
            ClearReferenceAudioCommand = new RelayCommand(() => ReferenceAudioPath = string.Empty);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            GenerateVideoCommand = new RelayCommand(AddToQueue, () => CanAddToQueue);
            RemoveQueueItemCommand = new RelayCommand<VideoSoundQueueItem>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => _queue.Any());
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            StartQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => HasQueueItems && !IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(async () => await ReprocessAllFailedAsync(), () => HasFailedItems);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);

            _queue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
                OnCanExecuteChanged();
            };

            AddLog("Video Sound initialized");
            LoadQueueFromFile();
        }

        #region Commands
        public ICommand SelectVideoCommand { get; }
        public ICommand SelectReferenceAudioCommand { get; }
        public ICommand ClearReferenceAudioCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand<VideoSoundQueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand StartQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }

        public bool HasFailedItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Failed);
        #endregion

        #region Input properties
        private string _videoPath = string.Empty;
        public string VideoPath
        {
            get => _videoPath;
            set
            {
                if (_videoPath != value)
                {
                    _videoPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasVideo));
                    OnPropertyChanged(nameof(CanAddToQueue));
                    OnPropertyChanged(nameof(CanAnalyze));
                    LoadVideoInfo();
                    OnCanExecuteChanged();
                }
            }
        }

        private string _videoInfo = string.Empty;
        public string VideoInfo
        {
            get => _videoInfo;
            set { if (_videoInfo != value) { _videoInfo = value; OnPropertyChanged(); } }
        }

        private string? _videoFileUri;
        public string? VideoFileUri
        {
            get => _videoFileUri;
            private set { if (_videoFileUri != value) { _videoFileUri = value; OnPropertyChanged(); } }
        }

        private string _referenceAudioPath = string.Empty;
        public string ReferenceAudioPath
        {
            get => _referenceAudioPath;
            set
            {
                if (_referenceAudioPath != value)
                {
                    _referenceAudioPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasReferenceAudio));
                    OnPropertyChanged(nameof(ReferenceAudioInfo));
                }
            }
        }

        public bool HasReferenceAudio => !string.IsNullOrEmpty(ReferenceAudioPath) && File.Exists(ReferenceAudioPath);
        public string ReferenceAudioInfo => HasReferenceAudio
            ? Path.GetFileName(ReferenceAudioPath)
            : "Using workflow's default voice reference";

        private string _prompt = string.Empty;
        public string Prompt
        {
            get => _prompt;
            set { if (_prompt != value) { _prompt = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanAddToQueue)); OnCanExecuteChanged(); } }
        }

        private int _width = 720;
        public int Width
        {
            get => _width;
            set { var v = value < 16 ? 16 : value; if (_width != v) { _width = v; OnPropertyChanged(); } }
        }

        private int _height = 1280;
        public int Height
        {
            get => _height;
            set { var v = value < 16 ? 16 : value; if (_height != v) { _height = v; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Sets the output Width/Height to match the uploaded clip's aspect ratio, keeping the
        /// longer edge at 1280 and snapping both dimensions to multiples of 32 (LTX latent grid).
        /// Called when the input video opens so the generated shot keeps the source proportions.
        /// </summary>
        public void SetOutputAspectFromVideo(int videoWidth, int videoHeight)
        {
            if (videoWidth <= 0 || videoHeight <= 0) return;

            const int longEdge = 1280;
            int w, h;
            if (videoWidth >= videoHeight)
            {
                w = longEdge;
                h = SnapTo32((int)Math.Round(longEdge * (double)videoHeight / videoWidth));
            }
            else
            {
                h = longEdge;
                w = SnapTo32((int)Math.Round(longEdge * (double)videoWidth / videoHeight));
            }

            if (Width == w && Height == h) return;
            Width = w;
            Height = h;
            AddLog($"Output aspect from video {videoWidth}×{videoHeight} → {w}×{h}");
        }

        private static int SnapTo32(int value)
        {
            var snapped = (int)(Math.Round(value / 32.0) * 32);
            return snapped < 256 ? 256 : snapped;
        }

        public bool HasVideo => !string.IsNullOrEmpty(VideoPath) && File.Exists(VideoPath);
        public bool CanAddToQueue => HasVideo && !string.IsNullOrWhiteSpace(Prompt);
        public bool CanAnalyze => HasVideo && !IsAnalyzing && !IsProcessing;

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
                    OnCanExecuteChanged();
                }
            }
        }
        #endregion

        #region Queue properties
        public ObservableCollection<VideoSoundQueueItem> Queue => _queue;

        private bool _isProcessingQueue;
        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            private set { if (_isProcessingQueue != value) { _isProcessingQueue = value; OnPropertyChanged(); OnCanExecuteChanged(); } }
        }

        private string _queueStatus = string.Empty;
        public string QueueStatus { get => _queueStatus; private set { if (_queueStatus != value) { _queueStatus = value; OnPropertyChanged(); } } }

        public bool HasQueueItems => _queue.Any();
        #endregion

        #region File selection
        private async void SelectVideo()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Video",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.webm|All Files|*.*",
                initialDir,
                persistKey: "videosound.video");

            if (path != null)
            {
                VideoPath = path;
                AddLog($"Input video: {Path.GetFileName(path)}");
            }
        }

        private async void SelectReferenceAudio()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Voice Audio (optional)",
                "Audio Files|*.mp3;*.wav;*.ogg;*.flac;*.m4a|All Files|*.*",
                initialDir,
                persistKey: "videosound.refaudio");

            if (path != null)
            {
                ReferenceAudioPath = path;
                AddLog($"Reference voice: {Path.GetFileName(path)}");
            }
        }

        public void SetVideoFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            VideoPath = path;
            AddLog($"Input video: {Path.GetFileName(path)}");
        }

        private void LoadVideoInfo()
        {
            if (!HasVideo)
            {
                VideoInfo = string.Empty;
                VideoFileUri = null;
                return;
            }
            var fi = new FileInfo(VideoPath);
            VideoInfo = $"{fi.Name} • {fi.Length / 1024 / 1024.0:F1}MB";
            VideoFileUri = VideoPath;
        }
        #endregion

        #region Analysis
        private async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;

            string? framePath = null;
            try
            {
                ProcessingStatus = "Extracting first frame…";
                framePath = ExtractFirstFrame(VideoPath);
                if (framePath == null || !File.Exists(framePath))
                    throw new Exception("Could not extract a frame from the video (is FFmpeg installed?).");

                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);

                var models = await _lmStudioService.GetAvailableModelsAsync(token);
                var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(selectedModel) && models.Count > 0)
                    selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;

                if (string.IsNullOrEmpty(selectedModel))
                {
                    MessageBox.Show("No LM Studio model available. Please ensure LM Studio is running and a model is loaded.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AddLog($"Analyzing first frame with model: {selectedModel}");

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "videosound-systemprompt.md");
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"System prompt not found: {promptFilePath}");
                var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel,
                    framePath,
                    "Describe the next few seconds of this clip as [VISUAL] / [SPEECH] / [SOUNDS].",
                    systemPrompt,
                    maxTokens: 2000,
                    cancellationToken: token);

                var cleaned = CleanOutput(result);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    Prompt = cleaned;
                    AddLog($"Prompt generated ({cleaned.Length} chars)");
                }
                else AddLog("WARNING: Analysis returned empty result");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR during analysis: {ex.Message}");
                MessageBox.Show($"Analysis failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (framePath != null) { try { File.Delete(framePath); } catch { } }
                ProcessingStatus = string.Empty;
                IsAnalyzing = false;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
            }
        }

        /// <summary>Extracts the first frame of a video to a temp PNG via FFmpeg. Returns null on failure.</summary>
        private string? ExtractFirstFrame(string videoPath)
        {
            try
            {
                var ffmpegPath = FindFFmpeg();
                if (ffmpegPath == null) return null;

                var tempPath = Path.Combine(Path.GetTempPath(), $"flippix_videosound_frame_{Guid.NewGuid():N}.png");
                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -i \"{videoPath}\" -frames:v 1 -q:v 2 \"{tempPath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var process = Process.Start(startInfo);
                if (process == null) return null;
                process.StandardError.ReadToEnd();
                process.WaitForExit(30000);

                return File.Exists(tempPath) && new FileInfo(tempPath).Length > 0 ? tempPath : null;
            }
            catch (Exception ex)
            {
                AddLog($"Frame extraction failed: {ex.Message}");
                return null;
            }
        }

        private static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = text.Replace("**", "").Trim();
            // Drop any code fences the model may wrap the blocks in.
            if (text.StartsWith("```"))
            {
                var firstNewline = text.IndexOf('\n');
                if (firstNewline >= 0) text = text.Substring(firstNewline + 1);
                if (text.EndsWith("```")) text = text.Substring(0, text.Length - 3);
                text = text.Trim();
            }
            return text;
        }
        #endregion

        #region Queue management
        private void AddToQueue()
        {
            if (!CanAddToQueue) return;

            var item = new VideoSoundQueueItem
            {
                VideoPath = VideoPath,
                ReferenceAudioPath = HasReferenceAudio ? ReferenceAudioPath : string.Empty,
                Prompt = Prompt ?? string.Empty,
                Width = Width,
                Height = Height,
                Seed = -1,
                ItemStatus = QueueItemStatus.Pending
            };

            _queue.Add(item);
            SaveQueueToFile();
            AddLog($"Added to queue: {item.DisplayText}");
            UpdateQueueStatus();

            if (!IsProcessingQueue) _ = ProcessQueueAsync();
        }

        private void RemoveQueueItem(VideoSoundQueueItem? item)
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
                lease = await _workflowCoordinator.AcquireAsync("VideoSound", token);
            }
            catch (OperationCanceledException)
            {
                IsProcessingQueue = false;
                OnCanExecuteChanged();
                return;
            }

            AddLog("Starting Video Sound queue...");
            using (lease)
            try
            {
                VideoSoundQueueItem? item;
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

        #region Queue persistence
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
                var items = JsonSerializer.Deserialize<List<VideoSoundQueueItem>>(File.ReadAllText(QueueFilePath));
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
        private async Task GenerateSingleVideoAsync(VideoSoundQueueItem item, CancellationToken token)
        {
            try
            {
                IsProcessing = true;
                HasResult = false;
                ResultVideoPath = string.Empty;
                ResultVideoInfo = string.Empty;
                ProcessingProgress = 0;
                ProcessingStatus = "Preparing Video Sound workflow...";

                AddLog($"=== Video Sound: {item.DisplayText} ===");

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

                ProcessingStatus = "Uploading video...";
                ProcessingProgress = 10;

                AddLog("Uploading input video...");
                var uploadedVideo = await _comfyUIService.UploadVideoAsync(item.VideoPath);
                if (string.IsNullOrEmpty(uploadedVideo))
                    throw new Exception("Failed to upload video.");
                AddLog($"Video uploaded: {uploadedVideo}");

                string? uploadedAudio = null;
                if (!string.IsNullOrEmpty(item.ReferenceAudioPath) && File.Exists(item.ReferenceAudioPath))
                {
                    ProcessingStatus = "Uploading reference voice...";
                    AddLog("Uploading reference voice audio...");
                    uploadedAudio = await _comfyUIService.UploadAudioAsync(item.ReferenceAudioPath);
                    if (string.IsNullOrEmpty(uploadedAudio))
                        AddLog("WARNING: Reference audio upload failed — using workflow default voice.");
                    else
                        AddLog($"Reference voice uploaded: {uploadedAudio}");
                }

                var runSeed = item.Seed >= 0 ? item.Seed : new Random().NextInt64(0, long.MaxValue);

                var json = workflowJson;
                WorkflowNodeUpdater.UpdateNodeInput(ref json, LoadVideoNode, "video", uploadedVideo);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, PromptNode, "value", item.Prompt ?? string.Empty);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, SeedNode, "noise_seed", runSeed);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, WidthNode, "value", item.Width);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, HeightNode, "value", item.Height);
                if (!string.IsNullOrEmpty(uploadedAudio))
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, ReferenceAudioNode, "audio", uploadedAudio);
                AddLog($"✓ {item.Width}×{item.Height} · seed={runSeed}" +
                       (uploadedAudio != null ? " · custom voice" : " · default voice"));

                // TEMP diagnostic: dump exactly what we send so it can be run manually in ComfyUI.
                // Restart ComfyUI, drag this file into the browser, and time it to compare.
                try
                {
                    var dumpPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        "flippix_videosound_submitted.json");
                    File.WriteAllText(dumpPath, json);
                    AddLog($"[diag] Submitted workflow dumped to {dumpPath}");
                }
                catch (Exception ex) { AddLog($"[diag] Failed to dump workflow: {ex.Message}"); }

                var workflow = JsonSerializer.Deserialize<JsonElement>(json);

                ProcessingProgress = 20;
                ProcessingStatus = "Generating video with sound...";
                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 20 + pct * 0.75;
                            ProcessingStatus = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                        });
                    }
                });

                var existing = GetExistingVideoFiles(OutputPattern, OutputSubfolder);
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress);
                AddLog($"Workflow submitted, ID: {promptId}");

                ProcessingProgress = 92;
                ProcessingStatus = "Retrieving video...";

                var outputVideo = await WaitForNewVideoAsync(
                    existing, OutputPattern,
                    TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5), OutputSubfolder);
                if (outputVideo == null || !File.Exists(outputVideo))
                    throw new Exception("No video output was generated.");
                AddLog($"Got output: {Path.GetFileName(outputVideo)}");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "VideoSound");
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"VideoSound_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                File.Copy(outputVideo, finalPath, true);

                item.OutputVideoPath = finalPath;
                ResultVideoPath = finalPath;
                await LocalCopyService.CopyVideoAsync(finalPath);
                HasResult = true;
                var fi = new FileInfo(finalPath);
                ResultVideoInfo = $"Video Sound • {fi.Length / 1024 / 1024.0:F1}MB";
                ProcessingProgress = 100;
                ProcessingStatus = "Video Sound Complete!";
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
        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            AnalyzeCommand.NotifyCanExecuteChanged();
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
