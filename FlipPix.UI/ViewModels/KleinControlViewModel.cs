using System;
using System.Collections.Generic;
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
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    public class KleinControlViewModel : INotifyPropertyChanged
    {
        private const string WorkflowFile = "workflow/image/klein/flux2_klein_control_netAPI.json";
        private const string SavePrefix = "flux2_klein";

        // The saved image is painted by the PiD 4K stage (nodes 203-212), not by Klein — the
        // Klein result only feeds it as guidance. PiD drifts off that guidance into a cool
        // colour cast once an axis runs past MaxPidAxis, so the canvas is clamped here.
        private const int MaxPidAxis = 4096;
        private const int PidScale = 4;                     // canvas stays exactly 4x the base
        private const int MaxBaseAxis = MaxPidAxis / PidScale;
        private const int BaseTargetPixels = 1024 * 1024;

        // Krea2 two-reference edit workflow (alternative pipeline)
        private const string Krea2WorkflowFile = "workflow/image/krea/krea2_edit_two_ref.json";
        private const string Krea2SavePrefix = "krea2_edit";
        private const string Krea2DefaultPrompt = "replace the woman in image a with the woman in image b.";

        private readonly ComfyUIService _comfyUIService;
        private readonly SettingsService _settingsService;
        private readonly IFileDialogService _fileDialogService;
        private readonly IAppLogger _logger;
        private readonly VideoAnalysisService _videoAnalysisService;

        // Reference image (subject)
        private string _refImagePath = string.Empty;
        private BitmapImage? _refImageSource;
        private bool _hasRefImage;

        // Pose image (from file)
        private string _poseImagePath = string.Empty;
        private BitmapImage? _poseImageSource;
        private bool _hasPoseImage;

        // Video mode
        private string _videoPath = string.Empty;
        private bool _hasVideo;
        private double _videoDurationSeconds = 100;
        private double _framePositionSeconds;
        private string _videoInfoText = string.Empty;
        private BitmapImage? _framePreviewSource;
        private bool _hasFramePreview;
        private bool _isExtractingFrame;
        private CancellationTokenSource? _previewCts;
        private int _previewGen = 0;

        // Prompt
        private string _prompt = string.Empty;

        // Workflow selection (0 = Klein Flux 2 control net, 1 = Krea2 two-reference edit)
        private int _selectedWorkflowIndex;

        // Workflow state
        private bool _isAnalyzing;
        private bool _isGenerating;
        private double _progress;
        private string _statusMessage = "Upload a reference image and a pose image to begin";
        private string _logOutput = string.Empty;
        private CancellationTokenSource? _cts;

        // Result
        private BitmapImage? _resultImageSource;
        private bool _hasResult;
        private string _resultImagePath = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public KleinControlViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            SettingsService settingsService,
            IFileDialogService fileDialogService,
            VideoAnalysisService videoAnalysisService)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            _videoAnalysisService = videoAnalysisService ?? throw new ArgumentNullException(nameof(videoAnalysisService));

            BrowseRefImageCommand = new RelayCommand(async () => await BrowseRefImageAsync(), () => !IsBusy);
            BrowsePoseImageCommand = new RelayCommand(async () => await BrowsePoseImageAsync(), () => !IsBusy);
            BrowseVideoCommand = new RelayCommand(async () => await BrowseVideoAsync(), () => !IsBusy);
            UseFrameCommand = new RelayCommand(async () => await UseFrameAsync(), () => HasVideo && !IsBusy && !IsExtractingFrame);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            GenerateCommand = new RelayCommand(async () => await GenerateAsync(), () => CanGenerate);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            OpenResultImageCommand = new RelayCommand(OpenResultImage, () => HasResult);
        }

        // ── Reference image ──────────────────────────────────────────────────
        public string RefImagePath
        {
            get => _refImagePath;
            set { _refImagePath = value; OnPropertyChanged(); }
        }

        public BitmapImage? RefImageSource
        {
            get => _refImageSource;
            set { _refImageSource = value; OnPropertyChanged(); }
        }

        public bool HasRefImage
        {
            get => _hasRefImage;
            set
            {
                _hasRefImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoRefImage));
                OnPropertyChanged(nameof(CanAnalyze));
                OnPropertyChanged(nameof(CanGenerate));
                NotifyCommands();
            }
        }

        public bool NoRefImage => !_hasRefImage;

        // ── Pose image ───────────────────────────────────────────────────────
        public string PoseImagePath
        {
            get => _poseImagePath;
            set { _poseImagePath = value; OnPropertyChanged(); }
        }

        public BitmapImage? PoseImageSource
        {
            get => _poseImageSource;
            set { _poseImageSource = value; OnPropertyChanged(); }
        }

        public bool HasPoseImage
        {
            get => _hasPoseImage;
            set
            {
                _hasPoseImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoPoseImage));
                OnPropertyChanged(nameof(CanAnalyze));
                OnPropertyChanged(nameof(CanGenerate));
                NotifyCommands();
            }
        }

        public bool NoPoseImage => !_hasPoseImage;

        // ── Video / frame picker ─────────────────────────────────────────────
        public string VideoPath
        {
            get => _videoPath;
            set { _videoPath = value; OnPropertyChanged(); }
        }

        public bool HasVideo
        {
            get => _hasVideo;
            set
            {
                _hasVideo = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoVideo));
                NotifyCommands();
            }
        }

        public bool NoVideo => !_hasVideo;

        public double VideoDurationSeconds
        {
            get => _videoDurationSeconds;
            set { _videoDurationSeconds = value; OnPropertyChanged(); }
        }

        public double FramePositionSeconds
        {
            get => _framePositionSeconds;
            set
            {
                _framePositionSeconds = Math.Max(0, Math.Min(value, VideoDurationSeconds));
                OnPropertyChanged();
                OnPropertyChanged(nameof(FramePositionText));
                _ = ExtractFramePreviewAsync();
            }
        }

        public string FramePositionText
        {
            get
            {
                var pos = TimeSpan.FromSeconds(_framePositionSeconds);
                var dur = TimeSpan.FromSeconds(_videoDurationSeconds);
                return $"{pos:mm\\:ss} / {dur:mm\\:ss}";
            }
        }

        public string VideoInfoText
        {
            get => _videoInfoText;
            set { _videoInfoText = value; OnPropertyChanged(); }
        }

        public BitmapImage? FramePreviewSource
        {
            get => _framePreviewSource;
            set { _framePreviewSource = value; OnPropertyChanged(); }
        }

        public bool HasFramePreview
        {
            get => _hasFramePreview;
            set { _hasFramePreview = value; OnPropertyChanged(); OnPropertyChanged(nameof(NoFramePreview)); }
        }

        public bool NoFramePreview => !_hasFramePreview;

        public bool IsExtractingFrame
        {
            get => _isExtractingFrame;
            set
            {
                _isExtractingFrame = value;
                OnPropertyChanged();
                UseFrameCommand.NotifyCanExecuteChanged();
            }
        }

        // ── Prompt ───────────────────────────────────────────────────────────
        public string Prompt
        {
            get => _prompt;
            set
            {
                _prompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
                GenerateCommand.NotifyCanExecuteChanged();
            }
        }

        // ── Workflow selection ────────────────────────────────────────────────
        public IReadOnlyList<string> WorkflowOptions { get; } = new[]
        {
            "Klein Flux 2 — Control Net",
            "Krea2 Edit — Two Reference",
        };

        public int SelectedWorkflowIndex
        {
            get => _selectedWorkflowIndex;
            set
            {
                if (_selectedWorkflowIndex == value) return;
                _selectedWorkflowIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsKleinMode));
                OnPropertyChanged(nameof(IsKrea2Mode));
                OnPropertyChanged(nameof(RefImageLabel));
                OnPropertyChanged(nameof(PoseImageLabel));
                OnPropertyChanged(nameof(WorkflowTitle));
                OnPropertyChanged(nameof(WorkflowDescription));
                OnPropertyChanged(nameof(ShowAnalyzeSection));
                OnPropertyChanged(nameof(CanAnalyze));
                OnPropertyChanged(nameof(CanGenerate));
                NotifyCommands();

                // Seed a sensible default prompt when switching into Krea2 mode
                if (IsKrea2Mode && string.IsNullOrWhiteSpace(Prompt))
                    Prompt = Krea2DefaultPrompt;
            }
        }

        public bool IsKleinMode => _selectedWorkflowIndex == 0;
        public bool IsKrea2Mode => _selectedWorkflowIndex == 1;

        public string RefImageLabel => IsKrea2Mode ? "Image A (scene to edit)" : "Reference Image (subject)";
        public string PoseImageLabel => IsKrea2Mode ? "Image B (new subject / reference)" : "Pose Image / Video (DWPose source + analysis)";
        public bool ShowAnalyzeSection => IsKleinMode;

        public string WorkflowTitle => IsKrea2Mode
            ? "🎛️ Krea2 Edit — Two Reference"
            : "🎛️ Klein Flux 2 — Control Net";

        public string WorkflowDescription => IsKrea2Mode
            ? "Upload image A (the scene) and image B (the new subject), then write a prompt describing the edit (e.g. \"replace the woman in image a with the woman in image b\") and click Generate."
            : "Upload a reference image (subject) and a pose image. Click Analyze to let QwenVL generate a prompt, then edit it and click Generate.";

        // ── Workflow state ────────────────────────────────────────────────────
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

        // ── Result ────────────────────────────────────────────────────────────
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

        public bool CanAnalyze => IsKleinMode && HasRefImage && HasPoseImage && !IsBusy;
        public bool CanGenerate => HasRefImage && HasPoseImage && !string.IsNullOrWhiteSpace(Prompt) && !IsBusy;

        // ── Commands ──────────────────────────────────────────────────────────
        public RelayCommand BrowseRefImageCommand { get; }
        public RelayCommand BrowsePoseImageCommand { get; }
        public RelayCommand BrowseVideoCommand { get; }
        public RelayCommand UseFrameCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand OpenResultImageCommand { get; }

        private void NotifyCommands()
        {
            BrowseRefImageCommand.NotifyCanExecuteChanged();
            BrowsePoseImageCommand.NotifyCanExecuteChanged();
            BrowseVideoCommand.NotifyCanExecuteChanged();
            UseFrameCommand.NotifyCanExecuteChanged();
            AnalyzeCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
        }

        // ── Browse handlers ───────────────────────────────────────────────────
        private async Task BrowseRefImageAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Image",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "kleincontrol.reference-image");
            if (!string.IsNullOrEmpty(path))
                SetRefImage(path);
        }

        private async Task BrowsePoseImageAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Pose Image",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "kleincontrol.pose-image");
            if (!string.IsNullOrEmpty(path))
            {
                ClearVideo();
                SetPoseImage(path);
            }
        }

        private async Task BrowseVideoAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Video",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.webm;*.wmv",
                persistKey: "kleincontrol.video");
            if (!string.IsNullOrEmpty(path))
                await LoadVideoAsync(path);
        }

        // ── Image / video loaders ─────────────────────────────────────────────
        public void SetRefImage(string path)
        {
            if (!File.Exists(path)) return;
            RefImagePath = path;
            try
            {
                var bmp = LoadBitmap(path);
                RefImageSource = bmp;
                HasRefImage = true;
                AddLog($"Reference: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { AddLog($"ERROR loading reference image: {ex.Message}"); }
        }

        public void SetPoseImage(string path)
        {
            if (!File.Exists(path)) return;
            PoseImagePath = path;
            try
            {
                var bmp = LoadBitmap(path);
                PoseImageSource = bmp;
                HasPoseImage = true;
                AddLog($"Pose: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { AddLog($"ERROR loading pose image: {ex.Message}"); }
        }

        private void ClearVideo()
        {
            HasVideo = false;
            VideoPath = string.Empty;
            VideoInfoText = string.Empty;
            FramePreviewSource = null;
            HasFramePreview = false;
            HasPoseImage = false;
            PoseImagePath = string.Empty;
            PoseImageSource = null;
        }

        private async Task LoadVideoAsync(string path)
        {
            if (!File.Exists(path)) return;

            // Clear previous image pose
            HasPoseImage = false;
            PoseImagePath = string.Empty;
            PoseImageSource = null;
            FramePreviewSource = null;
            HasFramePreview = false;

            try
            {
                AddLog($"Loading video: {Path.GetFileName(path)}");
                var info = await _videoAnalysisService.AnalyzeVideoAsync(path);
                VideoPath = path;
                VideoDurationSeconds = info.Duration.TotalSeconds;
                VideoInfoText = $"{info.Width}×{info.Height}  •  {info.FrameRate:F0} fps  •  {info.Duration:mm\\:ss}  •  {info.TotalFrames:N0} frames";
                HasVideo = true;
                // Start at frame 0
                _framePositionSeconds = 0;
                OnPropertyChanged(nameof(FramePositionSeconds));
                OnPropertyChanged(nameof(FramePositionText));
                AddLog(VideoInfoText);
                // Extract first frame preview
                await ExtractFramePreviewAsync();
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading video: {ex.Message}");
            }
        }

        // ── Frame extraction ──────────────────────────────────────────────────
        private async Task ExtractFramePreviewAsync()
        {
            if (!HasVideo || string.IsNullOrEmpty(VideoPath)) return;

            // Cancel previous pending extraction (debounce)
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = new CancellationTokenSource();
            var token = _previewCts.Token;

            try
            {
                // Debounce: wait 350 ms before firing FFmpeg
                await Task.Delay(350, token);

                IsExtractingFrame = true;
                var position = TimeSpan.FromSeconds(_framePositionSeconds);
                var tempDir = Path.Combine(Path.GetTempPath(), "flippix-frames");
                Directory.CreateDirectory(tempDir);
                // Unique filename per generation so WPF bitmap cache never hits a stale entry
                var gen = System.Threading.Interlocked.Increment(ref _previewGen);
                var tempPath = Path.Combine(tempDir, $"pose_preview_{gen}.png");

                await _videoAnalysisService.ExtractThumbnailAsync(VideoPath, tempPath, position);

                token.ThrowIfCancellationRequested();

                WpfApp.Current?.Dispatcher.Invoke(() =>
                {
                    LoadFramePreview(tempPath);
                    IsExtractingFrame = false;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"Frame preview error: {ex.Message}");
                IsExtractingFrame = false;
            }
        }

        // Extract the current frame and set it as the pose image
        private async Task UseFrameAsync()
        {
            if (!HasVideo || string.IsNullOrEmpty(VideoPath)) return;

            try
            {
                IsExtractingFrame = true;
                var position = TimeSpan.FromSeconds(_framePositionSeconds);
                var tempDir = Path.Combine(Path.GetTempPath(), "flippix-frames");
                Directory.CreateDirectory(tempDir);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var framePath = Path.Combine(tempDir, $"pose_frame_{ts}.png");

                await _videoAnalysisService.ExtractThumbnailAsync(VideoPath, framePath, position);
                SetPoseImage(framePath);
                AddLog($"Extracted frame at {position:mm\\:ss} → {Path.GetFileName(framePath)}");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR extracting frame: {ex.Message}");
            }
            finally
            {
                IsExtractingFrame = false;
            }
        }

        private void LoadFramePreview(string path)
        {
            try
            {
                // Use IgnoreImageCache so WPF doesn't serve a stale cached bitmap
                // when the same path was used before (shouldn't happen with gen-numbered
                // filenames, but belt-and-suspenders).
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                FramePreviewSource = bmp;
                HasFramePreview = true;
            }
            catch (Exception ex) { AddLog($"ERROR loading frame preview: {ex.Message}"); }
        }

        // ── Analyze ───────────────────────────────────────────────────────────
        private async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                IsAnalyzing = true;
                Progress = 0;
                StatusMessage = "Connecting to ComfyUI...";
                AddLog("=== Analyze ===");

                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(_cts.Token);
                    AddLog("Connected");
                }

                Progress = 8;
                StatusMessage = "Uploading images...";
                var uploadedRef = await _comfyUIService.UploadImageAsync(RefImagePath, _cts.Token);
                var uploadedPose = await _comfyUIService.UploadImageAsync(PoseImagePath, _cts.Token);
                AddLog($"ref={uploadedRef}  pose={uploadedPose}");

                Progress = 18;
                StatusMessage = "Building workflow...";
                var workflow = BuildWorkflow(uploadedRef, uploadedPose, customPrompt: null);

                var progressReporter = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        WpfApp.Current?.Dispatcher.Invoke(() =>
                        {
                            Progress = 18 + pct * 0.7;
                            StatusMessage = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                        });
                    }
                });

                StatusMessage = "Running ComfyUI (QwenVL + generation)...";
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progressReporter, _cts.Token);
                AddLog($"Done: {promptId}");

                Progress = 92;
                StatusMessage = "Reading generated prompt...";
                var promptText = await GetTextFromHistoryAsync(promptId, "59", _cts.Token);
                if (!string.IsNullOrWhiteSpace(promptText))
                {
                    var cleaned = StripThinkingTokens(promptText);
                    WpfApp.Current?.Dispatcher.Invoke(() => Prompt = cleaned);
                    AddLog($"Got prompt ({cleaned.Length} chars)");
                }
                else
                {
                    AddLog("WARNING: No text from ShowText node 59");
                }

                StatusMessage = "Retrieving image...";
                var bytes = await RetrieveOutputImageAsync(promptId, _cts.Token);
                if (bytes != null)
                    await SaveAndDisplayResultAsync(bytes, _cts.Token);
                else
                    AddLog("WARNING: No output image found");

                Progress = 100;
                StatusMessage = "Analysis complete — edit the prompt then click Generate";
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
                _logger.LogError($"Klein control analyze: {ex}");
            }
            finally
            {
                IsAnalyzing = false;
                AddLog("=== Analyze ended ===");
            }
        }

        // ── Generate ──────────────────────────────────────────────────────────
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
                AddLog($"Prompt: {Prompt}");

                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(_cts.Token);
                    AddLog("Connected");
                }

                Progress = 8;
                StatusMessage = "Uploading images...";
                var uploadedRef = await _comfyUIService.UploadImageAsync(RefImagePath, _cts.Token);
                var uploadedPose = await _comfyUIService.UploadImageAsync(PoseImagePath, _cts.Token);
                AddLog($"ref={uploadedRef}  pose={uploadedPose}");

                Progress = 18;
                var workflow = BuildWorkflow(uploadedRef, uploadedPose, customPrompt: Prompt);

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
                _logger.LogError($"Klein control generate: {ex}");
            }
            finally
            {
                IsGenerating = false;
                AddLog("=== Generate ended ===");
            }
        }

        // ── Workflow building ─────────────────────────────────────────────────
        private JsonElement BuildWorkflow(string uploadedRef, string uploadedPose, string? customPrompt)
        {
            if (IsKrea2Mode)
                return BuildKrea2Workflow(uploadedRef, uploadedPose, customPrompt);

            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse workflow JSON");

            UpdateNode(dict, "1", inputs => inputs["image"] = uploadedRef);
            UpdateNode(dict, "19", inputs => inputs["image"] = uploadedPose);
            UpdateNode(dict, "7", inputs => inputs["noise_seed"] = new Random().NextInt64(0, 999_999_999_999_999L));

            // Size both stages here rather than in-graph. The workflow used to take the base
            // from a 1 MP rescale of the pose (nodes 17/45/46) and the PiD canvas from an
            // independent 16 MP rescale (nodes 205/206), which put the long axis at ~5461 px
            // on a 9:16 source and tinted the bottom quarter. Driving 8:17/8:18 and 207 from
            // one base size keeps the canvas at exactly 4x, as the other PiD workflows do.
            var (baseWidth, baseHeight) = ComputeBaseSize(PoseImagePath);
            int pidWidth = baseWidth * PidScale;
            int pidHeight = baseHeight * PidScale;

            UpdateNode(dict, "8:17", inputs => { inputs["width"] = baseWidth; inputs["height"] = baseHeight; });
            UpdateNode(dict, "8:18", inputs => { inputs["width"] = baseWidth; inputs["height"] = baseHeight; });
            UpdateNode(dict, "207", inputs => { inputs["width"] = pidWidth; inputs["height"] = pidHeight; });
            AddLog($"Base {baseWidth}x{baseHeight} → PiD canvas {pidWidth}x{pidHeight}");

            if (customPrompt != null)
                UpdateNode(dict, "6", inputs => inputs["text"] = customPrompt);

            return JsonSerializer.SerializeToElement(dict);
        }

        // Krea2 two-reference edit: node 72 = image A (scene), node 86 = image B (new subject),
        // node 84 = edit prompt, node 53 = KSampler seed.
        private JsonElement BuildKrea2Workflow(string uploadedImageA, string uploadedImageB, string? customPrompt)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Krea2WorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse workflow JSON");

            UpdateNode(dict, "72", inputs => inputs["image"] = uploadedImageA);
            UpdateNode(dict, "86", inputs => inputs["image"] = uploadedImageB);
            UpdateNode(dict, "53", inputs => inputs["seed"] = new Random().NextInt64(0, 999_999_999_999_999L));

            if (!string.IsNullOrWhiteSpace(customPrompt))
                UpdateNode(dict, "84", inputs => inputs["prompt"] = customPrompt);

            return JsonSerializer.SerializeToElement(dict);
        }

        // Base render size at the pose image's aspect: ~1 MP, but never more than MaxBaseAxis
        // on the long edge so the 4x PiD canvas stays inside MaxPidAxis.
        private (int Width, int Height) ComputeBaseSize(string posePath)
        {
            int srcW = 1024, srcH = 1024;
            try
            {
                using var stream = File.OpenRead(posePath);
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (frame.PixelWidth > 0 && frame.PixelHeight > 0)
                {
                    srcW = frame.PixelWidth;
                    srcH = frame.PixelHeight;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Could not read pose dimensions ({ex.Message}) — using {srcW}x{srcH}");
            }

            var scale = Math.Sqrt(BaseTargetPixels / ((double)srcW * srcH));
            scale = Math.Min(scale, (double)MaxBaseAxis / Math.Max(srcW, srcH));

            return (SnapToBlock(srcW * scale), SnapToBlock(srcH * scale));
        }

        private static int SnapToBlock(double value)
            => Math.Clamp((int)Math.Round(value / 16.0) * 16, 256, MaxBaseAxis);

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

        // ── History polling ───────────────────────────────────────────────────
        private async Task<string?> GetTextFromHistoryAsync(string promptId, string nodeId, CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
            Uri uri;
            try { uri = new Uri(baseUrl); } catch { uri = new Uri("http://127.0.0.1:8188"); }

            for (int i = 0; i < 10; i++)
            {
                if (i > 0) await Task.Delay(2000, token);
                token.ThrowIfCancellationRequested();
                try
                {
                    using var http = new HttpClient { BaseAddress = uri };
                    var response = await http.GetAsync("/history", token);
                    if (!response.IsSuccessStatusCode) continue;
                    var history = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        await response.Content.ReadAsStringAsync(token));
                    if (history == null || !history.TryGetValue(promptId, out var entry)) continue;
                    JsonElement outputs = default;
                    if (!entry.TryGetProperty("outputs", out outputs) &&
                        !(entry.TryGetProperty("result", out var r) && r.TryGetProperty("outputs", out outputs)))
                        continue;
                    if (outputs.TryGetProperty(nodeId, out var nodeOut) &&
                        nodeOut.TryGetProperty("text", out var textArr) &&
                        textArr.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var item in textArr.EnumerateArray())
                        {
                            var s = item.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) sb.AppendLine(s);
                        }
                        var text = sb.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
                catch (Exception ex) { AddLog($"History poll: {ex.Message}"); }
            }
            return null;
        }

        // ── Output image retrieval ────────────────────────────────────────────
        private async Task<byte[]?> RetrieveOutputImageAsync(string promptId, CancellationToken token)
        {
            var savePrefix = IsKrea2Mode ? Krea2SavePrefix : SavePrefix;
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
                    var imgFile = files.FirstOrDefault(f =>
                        Path.GetFileName(f).StartsWith(savePrefix, StringComparison.OrdinalIgnoreCase) && IsImageExt(f));
                    imgFile ??= files.FirstOrDefault(f =>
                        IsImageExt(f) && !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase));
                    if (imgFile != null)
                    {
                        var data = await _comfyUIService.HttpClient.DownloadOutputImageAsync(imgFile);
                        if (data != null) { AddLog($"Downloaded {data.Length} bytes"); return data; }
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
                    var files = Directory.GetFiles(outputDir, $"{savePrefix}_*.png", SearchOption.AllDirectories)
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

        // ── Helpers ───────────────────────────────────────────────────────────
        private async Task SaveAndDisplayResultAsync(byte[] bytes, CancellationToken token)
        {
            var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "edited-images");
            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, $"klein-control_{DateTime.Now:yyyyMMdd_HHmmss}.png");
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

        private static string StripThinkingTokens(string text)
        {
            var opts = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            var result = System.Text.RegularExpressions.Regex.Replace(
                text, @"<think>[\s\S]*?</think>", string.Empty, opts).Trim();
            var lines = result.Split('\n');
            var kept = new System.Collections.Generic.List<string>();
            bool hasContent = false;
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (hasContent && string.IsNullOrWhiteSpace(t)) break;
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\(?(Note|Note:)\b", opts)) break;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\(\d+\)\s+The (input|output)\b", opts)) break;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^(Therefore|However|Additionally|The original|I'll ensure|Since we must|Corrected version)\b", opts)) break;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"(corrected version should read|per instructions|based on instruction)", opts)) break;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"\*\*(her|his|their)\s+\w+\s+is\s+now\b", opts)) break;
                if (hasContent && t.Length > 40 && kept.Any(k => k.Contains(t.Substring(0, Math.Min(40, t.Length))))) break;
                hasContent = true;
                kept.Add(System.Text.RegularExpressions.Regex.Replace(line, @"\*\*", string.Empty));
            }
            return string.Join("\n", kept).Trim();
        }

        private void AddLog(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            WpfApp.Current?.Dispatcher.Invoke(() => LogOutput = LogOutput + line + "\n");
            _logger.LogInfo(message);
        }
    }
}
