using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ViewModel for the Long Video tab.
    /// Uploads a video, extracts its last frame via FFmpeg, analyzes it with llamaserver,
    /// generates a new video via the selected workflow (Wan 2.2 Remix or LTX 2.3), then repeats.
    /// Each iteration feeds the previous output as the new input.
    /// </summary>
    public partial class LongVideoViewModel : VideoProcessingBaseViewModel
    {
        public enum LongVideoWorkflow { Wan, LTX23 }
        public enum WanAnalysisPrompt { Single, LongFight }

        private string _videoPath = string.Empty;
        private string _videoInfo = string.Empty;
        private int _maxIterations = 3;
        private int _currentIteration = 0;
        private bool _isRunning = false;
        private string _currentFramePath = string.Empty;
        private BitmapImage? _currentFramePreview;
        private string _currentAnalysis = string.Empty;
        private LongVideoWorkflow _selectedWorkflow = LongVideoWorkflow.Wan;
        private WanAnalysisPrompt _wanAnalysisPrompt = WanAnalysisPrompt.LongFight;

        private readonly LMStudioService _lmStudioService;
        private readonly IFileDialogService _fileDialogService;
        private readonly ObservableCollection<LongVideoIterationItem> _iterations = new();
        private CancellationTokenSource? _cts;
        private readonly ConcurrentQueue<LongVideoJob> _jobQueue = new();

        public LongVideoViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            LMStudioService lmStudioService,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            SelectVideoCommand = new RelayCommand(SelectVideo);
            StartCommand = new RelayCommand(async () => await StartLoopAsync(), () => CanStart);
            StopCommand = new RelayCommand(Stop, () => IsRunning);
            PlayResultCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            PlayIterationVideoCommand = new RelayCommand<LongVideoIterationItem>(PlayIterationVideo);
            OpenIterationFolderCommand = new RelayCommand<LongVideoIterationItem>(OpenIterationFolder);
            ToggleWorkflowCommand = new RelayCommand(ToggleWorkflow);
            SelectWanSinglePromptCommand = new RelayCommand(() => SetWanPrompt(WanAnalysisPrompt.Single));
            SelectWanFightPromptCommand = new RelayCommand(() => SetWanPrompt(WanAnalysisPrompt.LongFight));

            AddLog("Long Video Generator initialized");
        }

        #region Commands

        public ICommand SelectVideoCommand { get; }
        public RelayCommand StartCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand PlayResultCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand<LongVideoIterationItem> PlayIterationVideoCommand { get; }
        public RelayCommand<LongVideoIterationItem> OpenIterationFolderCommand { get; }
        public RelayCommand ToggleWorkflowCommand { get; }
        public RelayCommand SelectWanSinglePromptCommand { get; }
        public RelayCommand SelectWanFightPromptCommand { get; }

        #endregion

        #region Workflow Selection

        public LongVideoWorkflow SelectedWorkflow
        {
            get => _selectedWorkflow;
            set
            {
                if (_selectedWorkflow != value)
                {
                    _selectedWorkflow = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(UseWanWorkflow));
                    OnPropertyChanged(nameof(UseLTX23Workflow));
                    AddLog($"Long Video workflow: {(value == LongVideoWorkflow.LTX23 ? "LTX 2.3" : "Wan 2.2 Remix")}");
                }
            }
        }

        public bool UseWanWorkflow => _selectedWorkflow == LongVideoWorkflow.Wan;
        public bool UseLTX23Workflow => _selectedWorkflow == LongVideoWorkflow.LTX23;

        private void ToggleWorkflow()
        {
            SelectedWorkflow = _selectedWorkflow == LongVideoWorkflow.Wan
                ? LongVideoWorkflow.LTX23
                : LongVideoWorkflow.Wan;
        }

        public WanAnalysisPrompt SelectedWanPrompt
        {
            get => _wanAnalysisPrompt;
            private set
            {
                if (_wanAnalysisPrompt != value)
                {
                    _wanAnalysisPrompt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(UseWanSinglePrompt));
                    OnPropertyChanged(nameof(UseWanFightPrompt));
                }
            }
        }

        public bool UseWanSinglePrompt => _wanAnalysisPrompt == WanAnalysisPrompt.Single;
        public bool UseWanFightPrompt => _wanAnalysisPrompt == WanAnalysisPrompt.LongFight;

        private void SetWanPrompt(WanAnalysisPrompt prompt)
        {
            SelectedWanPrompt = prompt;
            var name = prompt == WanAnalysisPrompt.Single ? "wan-system-single" : "wan-long-fight";
            AddLog($"Wan analysis prompt: {name}.md");
        }

        #endregion

        #region Properties

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
                    OnPropertyChanged(nameof(CanStart));
                    LoadVideoInfo();
                    OnCanExecuteChanged();
                }
            }
        }

        public string VideoInfo
        {
            get => _videoInfo;
            private set { if (_videoInfo != value) { _videoInfo = value; OnPropertyChanged(); } }
        }

        public bool HasVideo => !string.IsNullOrEmpty(VideoPath) && File.Exists(VideoPath);

        public int MaxIterations
        {
            get => _maxIterations;
            set
            {
                var clamped = Math.Clamp(value, 1, 10);
                if (_maxIterations != clamped)
                {
                    _maxIterations = clamped;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanStart));
                    OnCanExecuteChanged();
                }
            }
        }

        public int CurrentIteration
        {
            get => _currentIteration;
            private set { if (_currentIteration != value) { _currentIteration = value; OnPropertyChanged(); } }
        }

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (_isRunning != value)
                {
                    _isRunning = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanStart));
                    OnPropertyChanged(nameof(StartButtonContent));
                    OnCanExecuteChanged();
                }
            }
        }

        public bool CanStart => HasVideo;

        public int QueuedJobCount => _jobQueue.Count;
        public bool HasQueuedJobs => !_jobQueue.IsEmpty;
        public string StartButtonContent => IsRunning ? "➕ Add to Queue" : "▶ Start Long Video Loop";

        public BitmapImage? CurrentFramePreview
        {
            get => _currentFramePreview;
            private set { _currentFramePreview = value; OnPropertyChanged(); }
        }

        public string CurrentAnalysis
        {
            get => _currentAnalysis;
            private set { if (_currentAnalysis != value) { _currentAnalysis = value; OnPropertyChanged(); } }
        }

        public ObservableCollection<LongVideoIterationItem> Iterations => _iterations;
        public bool HasIterations => _iterations.Any();

        #endregion

        #region File Selection

        private async void SelectVideo()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Input Video",
                "Video Files|*.mp4;*.mov;*.avi;*.mkv;*.webm|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                VideoPath = filePath;
                AddLog($"Selected video: {Path.GetFileName(VideoPath)}");
            }
        }

        private void LoadVideoInfo()
        {
            if (!HasVideo) { VideoInfo = string.Empty; return; }
            try
            {
                var fi = new FileInfo(VideoPath);
                VideoInfo = $"{fi.Name}  •  {fi.Length / 1024.0 / 1024.0:F1} MB";
            }
            catch { VideoInfo = Path.GetFileName(VideoPath); }
        }

        private void LoadFramePreview(string framePath)
        {
            if (string.IsNullOrEmpty(framePath) || !File.Exists(framePath))
            { CurrentFramePreview = null; return; }
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(framePath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                CurrentFramePreview = bmp;
            }
            catch { CurrentFramePreview = null; }
        }

        #endregion

        #region Main Loop

        private async Task StartLoopAsync()
        {
            if (!CanStart) return;

            var job = new LongVideoJob
            {
                InputVideoPath = VideoPath,
                MaxIterations = MaxIterations,
                Workflow = SelectedWorkflow
            };

            if (IsRunning)
            {
                _jobQueue.Enqueue(job);
                OnPropertyChanged(nameof(QueuedJobCount));
                OnPropertyChanged(nameof(HasQueuedJobs));
                AddLog($"Job queued ({_jobQueue.Count} pending): {Path.GetFileName(job.InputVideoPath)}, {job.MaxIterations} iteration(s)");
                return;
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            IsRunning = true;

            try
            {
                await ProcessJobAsync(job, _cts.Token);

                while (!_cts.Token.IsCancellationRequested && _jobQueue.TryDequeue(out var nextJob))
                {
                    OnPropertyChanged(nameof(QueuedJobCount));
                    OnPropertyChanged(nameof(HasQueuedJobs));
                    AddLog($"\n=== Starting queued job: {Path.GetFileName(nextJob.InputVideoPath)}, {nextJob.MaxIterations} iteration(s) ===");
                    await ProcessJobAsync(nextJob, _cts.Token);
                }
            }
            finally
            {
                while (_jobQueue.TryDequeue(out _)) { }
                OnPropertyChanged(nameof(QueuedJobCount));
                OnPropertyChanged(nameof(HasQueuedJobs));
                IsRunning = false;
                IsProcessing = false;
                OnCanExecuteChanged();
            }
        }

        private async Task ProcessJobAsync(LongVideoJob job, CancellationToken token)
        {
            // Apply this job's snapshot settings
            _maxIterations = job.MaxIterations;
            OnPropertyChanged(nameof(MaxIterations));
            if (_selectedWorkflow != job.Workflow)
            {
                _selectedWorkflow = job.Workflow;
                OnPropertyChanged(nameof(UseWanWorkflow));
                OnPropertyChanged(nameof(UseLTX23Workflow));
            }

            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            CurrentIteration = 0;
            CurrentAnalysis = string.Empty;
            CurrentFramePreview = null;

            _iterations.Clear();
            OnPropertyChanged(nameof(HasIterations));

            for (int i = 1; i <= job.MaxIterations; i++)
                _iterations.Add(new LongVideoIterationItem { Number = i });
            OnPropertyChanged(nameof(HasIterations));

            var currentVideoPath = job.InputVideoPath;

            AddLog($"=== Starting Long Video loop: {job.MaxIterations} iteration(s) ===");
            AddLog($"Input: {Path.GetFileName(currentVideoPath)}");

            for (int i = 0; i < job.MaxIterations; i++)
            {
                if (token.IsCancellationRequested) break;

                CurrentIteration = i + 1;
                var item = _iterations[i];
                item.InputVideoPath = currentVideoPath;
                item.ItemStatus = QueueItemStatus.Processing;

                AddLog($"\n--- Iteration {CurrentIteration}/{job.MaxIterations} ---");
                AddLog($"Input: {Path.GetFileName(currentVideoPath)}");

                try
                {
                    // Step 1: Extract last frame
                    ProcessingStatus = $"[{CurrentIteration}/{job.MaxIterations}] Extracting last frame...";
                    ProcessingProgress = (double)i / job.MaxIterations * 100 + 5;

                    var framePath = await ExtractLastFrameAsync(currentVideoPath, CurrentIteration, token);
                    item.LastFramePath = framePath;
                    System.Windows.Application.Current.Dispatcher.Invoke(
                        () => LoadFramePreview(framePath));
                    AddLog($"Last frame extracted: {Path.GetFileName(framePath)}");

                    // Step 2: Analyze with llamaserver
                    ProcessingStatus = $"[{CurrentIteration}/{job.MaxIterations}] Analyzing frame...";
                    ProcessingProgress = (double)i / job.MaxIterations * 100 + 15;

                    var analysis = await AnalyzeFrameAsync(framePath, token);
                    item.AnalysisPrompt = analysis;
                    CurrentAnalysis = analysis;
                    AddLog($"Analysis complete ({analysis.Length} chars)");

                    // Step 3: Generate video
                    ProcessingStatus = $"[{CurrentIteration}/{job.MaxIterations}] Generating video...";
                    var outputVideo = await GenerateVideoFromFrameAsync(
                        framePath, analysis, CurrentIteration, token);

                    item.OutputVideoPath = outputVideo;
                    item.ItemStatus = QueueItemStatus.Completed;
                    AddLog($"Video ready: {Path.GetFileName(outputVideo)}");

                    currentVideoPath = outputVideo;
                    ResultVideoPath = outputVideo;
                    HasResult = true;

                    ProcessingProgress = (double)(i + 1) / job.MaxIterations * 100;
                }
                catch (OperationCanceledException)
                {
                    item.ItemStatus = QueueItemStatus.Pending;
                    AddLog($"Iteration {CurrentIteration} cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    item.ItemStatus = QueueItemStatus.Failed;
                    item.ErrorMessage = ex.Message;
                    AddLog($"ERROR in iteration {CurrentIteration}: {ex.Message}");
                    System.Windows.MessageBox.Show(
                        $"Iteration {CurrentIteration} failed:\n{ex.Message}",
                        "Long Video Error",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                    break;
                }
            }

            var completedVideos = _iterations
                .Where(x => x.ItemStatus == QueueItemStatus.Completed && x.HasOutput)
                .Select(x => x.OutputVideoPath)
                .ToList();

            if (completedVideos.Count > 1)
            {
                ProcessingStatus = "Joining chunks into final video...";
                AddLog($"\n--- Joining {completedVideos.Count} chunk(s) ---");

                var joinedPath = await JoinVideosAsync(completedVideos);
                if (joinedPath != null)
                {
                    ResultVideoPath = joinedPath;
                    HasResult = true;
                    var fi = new FileInfo(joinedPath);
                    ResultVideoInfo = $"Long Video • {completedVideos.Count} chunks joined • {fi.Length / 1024.0 / 1024.0:F1} MB";
                    AddLog($"Joined video saved: {Path.GetFileName(joinedPath)}");
                }
            }

            if (HasResult)
            {
                ProcessingStatus = token.IsCancellationRequested
                    ? $"Stopped — {completedVideos.Count} chunk(s) joined."
                    : $"Complete! {completedVideos.Count} chunk(s) joined.";
                ProcessingProgress = 100;
                AddLog("=== Long Video loop complete ===");
            }
            else if (token.IsCancellationRequested)
            {
                ProcessingStatus = "Stopped";
                AddLog("Loop stopped by user");
            }
        }

        private void Stop()
        {
            while (_jobQueue.TryDequeue(out _)) { }
            OnPropertyChanged(nameof(QueuedJobCount));
            OnPropertyChanged(nameof(HasQueuedJobs));
            _cts?.Cancel();
            AddLog("Stop requested — clearing queue and finishing current step...");
        }

        #endregion

        private sealed class LongVideoJob
        {
            public string InputVideoPath { get; init; } = string.Empty;
            public int MaxIterations { get; init; }
            public LongVideoWorkflow Workflow { get; init; }
        }

        #region FFmpeg — Join Chunks

        private async Task<string?> JoinVideosAsync(List<string> videoPaths)
        {
            var ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                AddLog("WARNING: FFmpeg not found — skipping join step");
                return null;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "flippix_longvideo");
            Directory.CreateDirectory(tempDir);

            // Write concat list file
            var listPath = Path.Combine(tempDir, $"concat_{Guid.NewGuid():N}.txt");
            var lines = videoPaths.Select(p => $"file '{p.Replace("'", "'\\''")}'");
            await File.WriteAllLinesAsync(listPath, lines);

            var outputDir = Path.Combine(
                _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(),
                "LongVideo");
            Directory.CreateDirectory(outputDir);

            var outputPath = Path.Combine(outputDir,
                $"LongVid_joined_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

            var exitCode = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -f concat -safe 0 -i \"{listPath}\" -c copy \"{outputPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(120_000);
                return proc?.ExitCode ?? -1;
            });

            File.Delete(listPath);

            if (exitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                AddLog($"WARNING: FFmpeg join failed (exit code {exitCode}) — keeping last iteration video");
                return null;
            }

            await LocalCopyService.CopyVideoAsync(outputPath);
            return outputPath;
        }

        #endregion

        #region FFmpeg — Extract Last Frame

        private async Task<string> ExtractLastFrameAsync(
            string videoPath, int iteration, CancellationToken ct)
        {
            var ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
                throw new Exception(
                    "FFmpeg not found. Place ffmpeg.exe in C:\\ffmpeg\\bin\\ or add it to PATH.");

            var tempDir = Path.Combine(Path.GetTempPath(), "flippix_longvideo");
            Directory.CreateDirectory(tempDir);
            var framePath = Path.Combine(tempDir, $"lastframe_iter{iteration}_{Guid.NewGuid():N}.jpg");

            // Primary strategy: seek from end of file
            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -sseof -0.5 -i \"{videoPath}\" -vframes 1 -q:v 2 \"{framePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(30_000);
            }, ct);

            // Fallback: get duration explicitly and seek forward
            if (!File.Exists(framePath) || new FileInfo(framePath).Length == 0)
            {
                AddLog("Primary frame extraction failed — trying duration-based fallback...");
                var duration = GetVideoDuration(videoPath);
                if (duration <= 0) duration = 5.0;
                var seekTo = Math.Max(0, duration - 0.5);

                await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = $"-y -ss {seekTo:F3} -i \"{videoPath}\" -vframes 1 -q:v 2 \"{framePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(30_000);
                }, ct);
            }

            if (!File.Exists(framePath) || new FileInfo(framePath).Length == 0)
                throw new Exception(
                    $"Could not extract last frame from: {Path.GetFileName(videoPath)}");

            return framePath;
        }

        #endregion

        #region Analysis

        private async Task<string> AnalyzeFrameAsync(string framePath, CancellationToken ct)
        {
            var models = await _lmStudioService.GetAvailableModelsAsync();
            var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel
                                ?? string.Empty;
            if (string.IsNullOrEmpty(selectedModel))
            {
                if (models.Count > 0)
                    selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;
                else
                    throw new Exception("No models available in llamaserver. Please load a vision model.");
            }

            AddLog($"Using model: {selectedModel}");

            if (_selectedWorkflow == LongVideoWorkflow.LTX23)
            {
                // Step 1: image analysis
                var analysisPromptPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "image-analysis-prompt.md");
                if (!File.Exists(analysisPromptPath))
                    throw new FileNotFoundException($"Prompt file not found: {analysisPromptPath}");

                var analysisSystemPrompt = await File.ReadAllTextAsync(analysisPromptPath, ct);
                AddLog("Step 1/2: Analyzing frame with image-analysis-prompt...");
                var analysisResult = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel,
                    framePath,
                    "Analyze this image.",
                    analysisSystemPrompt,
                    cancellationToken: ct);
                AddLog($"Image analysis done ({analysisResult.Length} chars)");

                // Step 2: enhance to LTX video prompt
                var enhancePromptPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", "ltx-audio-video.md");
                if (!File.Exists(enhancePromptPath))
                    throw new FileNotFoundException($"Prompt file not found: {enhancePromptPath}");

                var enhanceSystemPrompt = await File.ReadAllTextAsync(enhancePromptPath, ct);
                AddLog("Step 2/2: Generating LTX video prompt...");
                var enhanced = await _lmStudioService.SendTextChatAsync(
                    selectedModel, enhanceSystemPrompt, analysisResult, maxTokens: 2000);
                AddLog($"LTX prompt ready ({enhanced.Length} chars)");
                return enhanced;
            }
            else
            {
                // Wan: single-step image analysis using the selected prompt file
                var promptFileName = _wanAnalysisPrompt == WanAnalysisPrompt.Single
                    ? "wan-system-single.md"
                    : "wan-long-fight.md";
                var promptFilePath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", promptFileName);
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"Prompt file not found: {promptFilePath}");

                AddLog($"Using prompt: {promptFileName}");
                var systemPrompt = await File.ReadAllTextAsync(promptFilePath, ct);
                return await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    selectedModel,
                    framePath,
                    "Analyze this image and generate a cinematic video prompt.",
                    systemPrompt,
                    cancellationToken: ct);
            }
        }

        #endregion

        #region ComfyUI Video Generation

        private static (int width, int height) ComputeWanDimensions(int srcWidth, int srcHeight)
        {
            if (srcWidth <= 0 || srcHeight <= 0) return (1280, 720);
            const int targetArea = 1280 * 720;
            double ratio = (double)srcWidth / srcHeight;
            int w = (int)Math.Round(Math.Sqrt(targetArea * ratio) / 16) * 16;
            int h = (int)Math.Round(Math.Sqrt(targetArea / ratio) / 16) * 16;
            w = Math.Clamp(w, 256, 1536);
            h = Math.Clamp(h, 256, 1536);
            return (w, h);
        }

        private async Task<string> GenerateVideoFromFrameAsync(
            string framePath, string prompt, int iteration, CancellationToken ct)
        {
            IsProcessing = true;
            ProcessingProgress = 20;

            var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                status => AddLog($"[Auto-Restart] {status}"));
            if (!comfyUIOk)
                throw new Exception("ComfyUI is not running. Please start it manually.");

            if (!_comfyUIService.IsConnected)
            {
                await _comfyUIService.ConnectAsync();
                AddLog("Connected to ComfyUI");
            }

            ProcessingStatus = $"[{iteration}/{MaxIterations}] Uploading frame to ComfyUI...";
            var uploadedImageName = await _comfyUIService.UploadImageAsync(framePath);
            if (string.IsNullOrEmpty(uploadedImageName))
                throw new Exception("Frame upload to ComfyUI failed.");
            AddLog($"Frame uploaded: {uploadedImageName}");

            string finalPath;
            if (_selectedWorkflow == LongVideoWorkflow.LTX23)
                finalPath = await GenerateLTX23VideoAsync(framePath, uploadedImageName, prompt, iteration, ct);
            else
                finalPath = await GenerateWanVideoAsync(framePath, uploadedImageName, prompt, iteration, ct);

            await LocalCopyService.CopyVideoAsync(finalPath);
            IsProcessing = false;
            return finalPath;
        }

        private async Task<string> GenerateWanVideoAsync(
            string framePath, string uploadedImageName, string prompt, int iteration, CancellationToken ct)
        {
            var workflowPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "workflow", "Wan2_2_RemixAPI.json");
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow not found: {workflowPath}");

            var workflowJson = await File.ReadAllTextAsync(workflowPath, ct);
            var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

            // Read frame dimensions so video matches the source aspect ratio
            int frameWidth = 0, frameHeight = 0;
            try
            {
                using var stream = new FileStream(framePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                    System.Windows.Media.Imaging.BitmapCacheOption.None);
                var bmpFrame = decoder.Frames[0];
                frameWidth = bmpFrame.PixelWidth;
                frameHeight = bmpFrame.PixelHeight;
            }
            catch (Exception ex) { AddLog($"WARNING: Could not read frame size ({ex.Message}) — using default 1280x720"); }

            var (vidW, vidH) = ComputeWanDimensions(frameWidth, frameHeight);
            AddLog($"Video dimensions: {vidW}x{vidH} (frame: {frameWidth}x{frameHeight})");

            var rawJson = workflow.GetRawText();
            var seed = (long)new Random().Next(1, int.MaxValue);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "258", "image", uploadedImageName);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "6", "text", prompt);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "57", "noise_seed", seed);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "58", "noise_seed", seed);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "304", "filename_prefix",
                $"{DateTime.Now:yyyyMMdd_HHmmss}_LongVid_i{iteration}");
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "131", "value", vidW);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "130", "value", vidH);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "445", "width", vidW);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "445", "height", vidH);

            return await ExecuteWorkflowAndSaveAsync(rawJson, iteration, "LongVid_Wan", ct);
        }

        private async Task<string> GenerateLTX23VideoAsync(
            string framePath, string uploadedImageName, string prompt, int iteration, CancellationToken ct)
        {
            var workflowPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "workflow", "LTX2.3-I2VAPI.json");
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow not found: {workflowPath}");

            var workflowJson = await File.ReadAllTextAsync(workflowPath, ct);
            var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

            // Detect portrait vs landscape for LTX 2.3 constrained dimensions
            int itemWidth = 320, itemHeight = 224;
            try
            {
                using var stream = new FileStream(framePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                    System.Windows.Media.Imaging.BitmapCacheOption.None);
                var bmpFrame = decoder.Frames[0];
                bool isPortrait = bmpFrame.PixelHeight > bmpFrame.PixelWidth;
                if (isPortrait) { itemWidth = 224; itemHeight = 320; }
                AddLog($"Frame: {bmpFrame.PixelWidth}x{bmpFrame.PixelHeight} ({(isPortrait ? "portrait" : "landscape")}) → LTX dims: {itemWidth}x{itemHeight}");
            }
            catch (Exception ex) { AddLog($"WARNING: Could not read frame size ({ex.Message}) — using 320x224"); }

            var rawJson = workflow.GetRawText();
            var seed = (long)new Random().Next(1, int.MaxValue);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "5016:2004", "image", uploadedImageName);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "5026:5018", "text", prompt);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "5002:4832", "noise_seed", seed);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "5001:4967", "noise_seed", seed);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "5012:5009", "noise_seed", seed);
            WorkflowNodeUpdater.UpdateNodeInput(ref rawJson, "4852", "filename_prefix",
                $"{DateTime.Now:yyyyMMdd_HHmmss}_LongVid_LTX_i{iteration}");
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref rawJson, "5013:3059",
                new Dictionary<string, object> { { "width", itemWidth }, { "height", itemHeight } });

            return await ExecuteWorkflowAndSaveAsync(rawJson, iteration, "LongVid_LTX23", ct);
        }

        private async Task<string> ExecuteWorkflowAndSaveAsync(
            string rawJson, int iteration, string filePrefix, CancellationToken ct)
        {
            var updatedWorkflow = JsonSerializer.Deserialize<JsonElement>(rawJson);
            var generationStart = DateTime.Now.AddSeconds(-2);

            var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max != null)
                {
                    var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProcessingProgress = 30 + pct * 0.60;
                        ProcessingStatus =
                            $"[{iteration}/{MaxIterations}] Generating: {msg.Data.Value}/{msg.Data.Max}";
                    });
                }
            });

            ProcessingStatus = $"[{iteration}/{MaxIterations}] Executing workflow...";
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
            AddLog($"Workflow submitted (prompt ID: {promptId})");

            ProcessingProgress = 92;
            ProcessingStatus = $"[{iteration}/{MaxIterations}] Waiting for output video...";

            var outputVideo = await WaitForVideoByTimestampAsync(
                generationStart, TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(5));

            if (outputVideo == null || !File.Exists(outputVideo))
                throw new Exception("No output video was produced within the timeout.");

            var outputDir = Path.Combine(
                _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(),
                "LongVideo");
            Directory.CreateDirectory(outputDir);

            var finalPath = Path.Combine(outputDir,
                $"{filePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}_iter{iteration}.mp4");
            File.Copy(outputVideo, finalPath, true);
            AddLog($"Saved: {finalPath}");
            return finalPath;
        }

        private async Task<string?> WaitForVideoByTimestampAsync(
            DateTime after, TimeSpan maxWait, TimeSpan checkInterval)
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

            // Only monitor the direct output folder, not subdirectories, to avoid
            // picking up previously-copied files from the LongVideo subfolder.
            AddLog($"Monitoring: {outputFolder}  (files newer than {after:HH:mm:ss})");

            string? trackedCandidate = null;
            long lastSize = -1;

            var deadline = DateTime.Now + maxWait;
            while (DateTime.Now < deadline)
            {
                await Task.Delay(checkInterval);

                if (!Directory.Exists(outputFolder)) continue;

                // Search top-level only — the LongVideo subfolder is inside outputFolder
                // and searching AllDirectories would pick up our own copied files.
                var candidate = Directory
                    .GetFiles(outputFolder, "*.mp4", SearchOption.TopDirectoryOnly)
                    .Where(f => File.GetLastWriteTime(f) > after)
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .FirstOrDefault();

                if (candidate != null)
                {
                    long currentSize = new FileInfo(candidate).Length;

                    // Reset tracking when a different (newer) file appears
                    if (candidate != trackedCandidate)
                    {
                        trackedCandidate = candidate;
                        lastSize = -1;
                    }

                    if (currentSize == 0 || currentSize != lastSize)
                    {
                        // File is still being written by ComfyUI — keep waiting
                        AddLog($"Video found but still writing: {Path.GetFileName(candidate)} ({currentSize / 1024.0 / 1024.0:F2} MB)");
                        lastSize = currentSize;
                        continue;
                    }

                    // Size is non-zero and stable — file is fully written
                    var fi = new FileInfo(candidate);
                    AddLog($"Found: {fi.Name} ({fi.Length / 1024.0 / 1024.0:F2} MB)");
                    return candidate;
                }

                trackedCandidate = null;
                lastSize = -1;
                var remaining = (int)(deadline - DateTime.Now).TotalSeconds;
                AddLog($"Waiting for video... ({remaining}s remaining)");
            }

            AddLog("ERROR: Timeout waiting for output video");
            return null;
        }

        #endregion

        #region Iteration Result Actions

        private void PlayIterationVideo(LongVideoIterationItem? item)
        {
            if (item?.HasOutput == true)
            {
                ResultVideoPath = item.OutputVideoPath;
                PlayVideo();
            }
        }

        private void OpenIterationFolder(LongVideoIterationItem? item)
        {
            if (item?.HasOutput == true)
            {
                var folder = Path.GetDirectoryName(item.OutputVideoPath);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    Process.Start("explorer.exe", folder);
            }
        }

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            PlayResultCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            ToggleWorkflowCommand.NotifyCanExecuteChanged();
        }
    }
}
