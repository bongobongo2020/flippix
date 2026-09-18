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
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Services;
using FlipPix.UI.Linux.Services;
using FlipPix.UI.Linux;

namespace FlipPix.UI.Linux.ViewModels
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

        // 57/62 caption the reference image, 63 joins them, 59 shows the result. Analyze runs
        // this chain to fill the prompt box; Generate has no use for it once the box is filled.
        private static readonly string[] QwenVlChainNodes = { "57", "62", "63", "59" };

        private readonly ComfyUIService _comfyUIService;
        private readonly SettingsService _settingsService;
        private readonly IFileDialogService _fileDialogService;
        private readonly IAppLogger _logger;
        private readonly VideoAnalysisService _videoAnalysisService;

        private string _refImagePath = string.Empty;
        private BitmapImage? _refImageSource;
        private bool _hasRefImage;

        private string _poseImagePath = string.Empty;
        private BitmapImage? _poseImageSource;
        private bool _hasPoseImage;

        private string _videoPath = string.Empty;
        private bool _hasVideo;
        private double _videoDurationSeconds = 100;
        private double _framePositionSeconds;
        private string _videoInfoText = string.Empty;
        private BitmapImage? _framePreviewSource;
        private string _framePreviewPath = string.Empty;
        private bool _hasFramePreview;
        private bool _isExtractingFrame;
        private CancellationTokenSource? _previewCts;
        private int _previewGen = 0;

        private string _prompt = string.Empty;

        private bool _isAnalyzing;
        private bool _isGenerating;
        private double _progress;
        private string _statusMessage = "Upload a reference image and a pose image to begin";
        private string _logOutput = string.Empty;
        private CancellationTokenSource? _cts;

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

        public string FramePreviewPath
        {
            get => _framePreviewPath;
            set { _framePreviewPath = value; OnPropertyChanged(); }
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

        public bool CanAnalyze => HasRefImage && HasPoseImage && !IsBusy;
        public bool CanGenerate => HasRefImage && HasPoseImage && !string.IsNullOrWhiteSpace(Prompt) && !IsBusy;

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

        private async Task BrowseRefImageAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync("Select Reference Image", "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp");
            if (!string.IsNullOrEmpty(path)) SetRefImage(path);
        }

        private async Task BrowsePoseImageAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync("Select Pose Image", "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp");
            if (!string.IsNullOrEmpty(path)) { ClearVideo(); SetPoseImage(path); }
        }

        private async Task BrowseVideoAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync("Select Video", "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.webm;*.wmv");
            if (!string.IsNullOrEmpty(path)) await LoadVideoAsync(path);
        }

        public void SetRefImage(string path)
        {
            if (!File.Exists(path)) return;
            RefImagePath = path;
            try { RefImageSource = LoadBitmap(path); HasRefImage = true; AddLog($"Reference: {Path.GetFileName(path)}"); }
            catch (Exception ex) { AddLog($"ERROR loading reference: {ex.Message}"); }
        }

        public void SetPoseImage(string path)
        {
            if (!File.Exists(path)) return;
            PoseImagePath = path;
            try { PoseImageSource = LoadBitmap(path); HasPoseImage = true; AddLog($"Pose: {Path.GetFileName(path)}"); }
            catch (Exception ex) { AddLog($"ERROR loading pose: {ex.Message}"); }
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
            HasPoseImage = false; PoseImagePath = string.Empty; PoseImageSource = null;
            FramePreviewSource = null; HasFramePreview = false;
            try
            {
                AddLog($"Loading video: {Path.GetFileName(path)}");
                var info = await _videoAnalysisService.AnalyzeVideoAsync(path);
                VideoPath = path;
                VideoDurationSeconds = info.Duration.TotalSeconds;
                VideoInfoText = $"{info.Width}×{info.Height}  •  {info.FrameRate:F0} fps  •  {info.Duration:mm\\:ss}  •  {info.TotalFrames:N0} frames";
                HasVideo = true;
                _framePositionSeconds = 0;
                OnPropertyChanged(nameof(FramePositionSeconds));
                OnPropertyChanged(nameof(FramePositionText));
                AddLog(VideoInfoText);
                await ExtractFramePreviewAsync();
            }
            catch (Exception ex) { AddLog($"ERROR loading video: {ex.Message}"); }
        }

        private async Task ExtractFramePreviewAsync()
        {
            if (!HasVideo || string.IsNullOrEmpty(VideoPath)) return;
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = new CancellationTokenSource();
            var token = _previewCts.Token;
            try
            {
                await Task.Delay(350, token);
                IsExtractingFrame = true;
                var position = TimeSpan.FromSeconds(_framePositionSeconds);
                var tempDir = Path.Combine(Path.GetTempPath(), "flippix-frames");
                Directory.CreateDirectory(tempDir);
                var gen = System.Threading.Interlocked.Increment(ref _previewGen);
                var tempPath = Path.Combine(tempDir, $"pose_preview_{gen}.png");
                await _videoAnalysisService.ExtractThumbnailAsync(VideoPath, tempPath, position);
                token.ThrowIfCancellationRequested();
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    LoadFramePreview(tempPath);
                    IsExtractingFrame = false;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AddLog($"Frame preview: {ex.Message}"); IsExtractingFrame = false; }
        }

        private async Task UseFrameAsync()
        {
            if (!HasVideo || string.IsNullOrEmpty(VideoPath)) return;
            try
            {
                IsExtractingFrame = true;
                var position = TimeSpan.FromSeconds(_framePositionSeconds);
                var tempDir = Path.Combine(Path.GetTempPath(), "flippix-frames");
                Directory.CreateDirectory(tempDir);
                var framePath = Path.Combine(tempDir, $"pose_frame_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                await _videoAnalysisService.ExtractThumbnailAsync(VideoPath, framePath, position);
                SetPoseImage(framePath);
                AddLog($"Extracted frame at {position:mm\\:ss} → {Path.GetFileName(framePath)}");
            }
            catch (Exception ex) { AddLog($"ERROR extracting frame: {ex.Message}"); }
            finally { IsExtractingFrame = false; }
        }

        private void LoadFramePreview(string path)
        {
            try
            {
                FramePreviewSource = LoadBitmap(path);
                FramePreviewPath = path;   // unique path → Avalonia PathToBitmapConverter always reloads
                HasFramePreview = true;
            }
            catch (Exception ex) { AddLog($"ERROR loading preview: {ex.Message}"); }
        }

        private async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
            try
            {
                IsAnalyzing = true; Progress = 0; StatusMessage = "Connecting..."; AddLog("=== Analyze ===");
                if (!_comfyUIService.IsConnected) { await _comfyUIService.ConnectAsync(_cts.Token); AddLog("Connected"); }
                Progress = 8; StatusMessage = "Uploading images...";
                var uploadedRef = await _comfyUIService.UploadImageAsync(RefImagePath, _cts.Token);
                var uploadedPose = await _comfyUIService.UploadImageAsync(PoseImagePath, _cts.Token);
                AddLog($"ref={uploadedRef}  pose={uploadedPose}");
                Progress = 18;
                var workflow = BuildWorkflow(uploadedRef, uploadedPose, null);
                var progressReporter = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        { Progress = 18 + pct * 0.7; StatusMessage = $"Generating: {msg.Data.Value}/{msg.Data.Max}"; });
                    }
                });
                StatusMessage = "Running ComfyUI...";
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progressReporter, _cts.Token);
                AddLog($"Done: {promptId}"); Progress = 92; StatusMessage = "Reading prompt...";
                var text = await GetTextFromHistoryAsync(promptId, "59", _cts.Token);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var cleaned = StripThinkingTokens(text);
                    System.Windows.Application.Current?.Dispatcher.Invoke(() => Prompt = cleaned);
                    AddLog($"Got prompt ({cleaned.Length} chars)");
                }
                StatusMessage = "Retrieving image...";
                var bytes = await RetrieveOutputImageAsync(promptId, _cts.Token);
                if (bytes != null) await SaveAndDisplayResultAsync(bytes, _cts.Token);
                Progress = 100; StatusMessage = "Analysis complete — edit the prompt then click Generate";
            }
            catch (OperationCanceledException) { StatusMessage = "Cancelled"; Progress = 0; }
            catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; AddLog($"ERROR: {ex.Message}"); _logger.LogError($"Klein control: {ex}"); }
            finally { IsAnalyzing = false; AddLog("=== Analyze ended ==="); }
        }

        private async Task GenerateAsync()
        {
            if (!CanGenerate) return;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
            try
            {
                IsGenerating = true; Progress = 0; StatusMessage = "Connecting..."; AddLog("=== Generate ==="); AddLog($"Prompt: {Prompt}");
                if (!_comfyUIService.IsConnected) { await _comfyUIService.ConnectAsync(_cts.Token); AddLog("Connected"); }
                Progress = 8; StatusMessage = "Uploading images...";
                var uploadedRef = await _comfyUIService.UploadImageAsync(RefImagePath, _cts.Token);
                var uploadedPose = await _comfyUIService.UploadImageAsync(PoseImagePath, _cts.Token);
                AddLog($"ref={uploadedRef}  pose={uploadedPose}");
                Progress = 18;
                var workflow = BuildWorkflow(uploadedRef, uploadedPose, Prompt);
                var progressReporter = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        { Progress = 18 + pct * 0.74; StatusMessage = $"Generating: {msg.Data.Value}/{msg.Data.Max}"; });
                    }
                });
                StatusMessage = "Running ComfyUI...";
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progressReporter, _cts.Token);
                AddLog($"Done: {promptId}"); Progress = 94; StatusMessage = "Retrieving image...";
                var bytes = await RetrieveOutputImageAsync(promptId, _cts.Token);
                if (bytes != null) { await SaveAndDisplayResultAsync(bytes, _cts.Token); Progress = 100; StatusMessage = $"Done! {Path.GetFileName(ResultImagePath)}"; }
                else { StatusMessage = "No result — check ComfyUI logs"; AddLog("WARNING: No output image"); }
            }
            catch (OperationCanceledException) { StatusMessage = "Cancelled"; Progress = 0; }
            catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; AddLog($"ERROR: {ex.Message}"); _logger.LogError($"Klein control: {ex}"); }
            finally { IsGenerating = false; AddLog("=== Generate ended ==="); }
        }

        private JsonElement BuildWorkflow(string uploadedRef, string uploadedPose, string? customPrompt)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFile);
            if (!File.Exists(workflowPath)) throw new FileNotFoundException($"Workflow not found: {workflowPath}");
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
            {
                // 59 (ShowText) feeds both the Klein encoder (6) and the PiD encoder (201);
                // a typed prompt has to replace it in both or PiD paints from the stale
                // caption node 6 no longer reads.
                UpdateNode(dict, "6", inputs => inputs["text"] = customPrompt);
                UpdateNode(dict, "201", inputs => inputs["text"] = customPrompt);

                // With both encoders holding literal text, the QwenVL chain is dead weight -
                // except 59 is an OUTPUT_NODE, so ComfyUI would still run 57/62 to reach it.
                // That matters beyond wasted time: a native crash inside the GGUF runtime
                // takes the whole ComfyUI process down (no node error, the port just dies),
                // which killed Generate as well as Analyze. Drop the branch.
                foreach (var deadNode in QwenVlChainNodes)
                    dict.Remove(deadNode);
            }

            return JsonSerializer.SerializeToElement(dict);
        }

        // Base render size at the pose image's aspect: ~1 MP, but never more than MaxBaseAxis
        // on the long edge so the 4x PiD canvas stays inside MaxPidAxis.
        private (int Width, int Height) ComputeBaseSize(string posePath)
        {
            int srcW = 1024, srcH = 1024;
            try
            {
                var frame = LoadBitmap(posePath);
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

        private static void UpdateNode(Dictionary<string, JsonElement> dict, string nodeId, Action<Dictionary<string, object>> updater)
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

        private async Task<string?> GetTextFromHistoryAsync(string promptId, string nodeId, CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
            Uri uri; try { uri = new Uri(baseUrl); } catch { uri = new Uri("http://127.0.0.1:8188"); }
            for (int i = 0; i < 10; i++)
            {
                if (i > 0) await Task.Delay(2000, token);
                token.ThrowIfCancellationRequested();
                try
                {
                    using var http = new HttpClient { BaseAddress = uri };
                    var resp = await http.GetAsync("/history", token);
                    if (!resp.IsSuccessStatusCode) continue;
                    var hist = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await resp.Content.ReadAsStringAsync(token));
                    if (hist == null || !hist.TryGetValue(promptId, out var entry)) continue;
                    JsonElement outputs = default;
                    if (!entry.TryGetProperty("outputs", out outputs) &&
                        !(entry.TryGetProperty("result", out var r) && r.TryGetProperty("outputs", out outputs))) continue;
                    if (outputs.TryGetProperty(nodeId, out var nodeOut) &&
                        nodeOut.TryGetProperty("text", out var textArr) && textArr.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var item in textArr.EnumerateArray()) { var s = item.GetString(); if (!string.IsNullOrWhiteSpace(s)) sb.AppendLine(s); }
                        var text = sb.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
                catch (Exception ex) { AddLog($"History poll: {ex.Message}"); }
            }
            return null;
        }

        private async Task<byte[]?> RetrieveOutputImageAsync(string promptId, CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
            Uri uri; try { uri = new Uri(baseUrl); } catch { uri = new Uri("http://127.0.0.1:8188"); }
            bool isRemote = !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
            const int maxRetries = 20; const int retryDelayMs = 5000;
            if (isRemote)
            {
                for (int i = 0; i < maxRetries; i++)
                {
                    if (i > 0) { AddLog($"Retry {i}/{maxRetries}..."); await Task.Delay(retryDelayMs, token); }
                    token.ThrowIfCancellationRequested();
                    var files = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                    var imgFile = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith(SavePrefix, StringComparison.OrdinalIgnoreCase) && IsImageExt(f));
                    imgFile ??= files.FirstOrDefault(f => IsImageExt(f) && !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase));
                    if (imgFile != null) { var data = await _comfyUIService.HttpClient.DownloadOutputImageAsync(imgFile); if (data != null) return data; }
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
                    var files = Directory.GetFiles(outputDir, $"{SavePrefix}_*.png", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTime).ToList();
                    if (files.Any())
                    {
                        var latest = files[0];
                        var age = DateTime.Now - File.GetLastWriteTime(latest);
                        if (age.TotalSeconds < 120) return await File.ReadAllBytesAsync(latest, token);
                    }
                }
                return null;
            }
        }

        private async Task SaveAndDisplayResultAsync(byte[] bytes, CancellationToken token)
        {
            var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "edited-images");
            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, $"klein-control_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            await File.WriteAllBytesAsync(path, bytes, token);
            ResultImagePath = path;
            System.Windows.Application.Current?.Dispatcher.Invoke(() => { try { ResultImageSource = LoadBitmap(path); } catch { } });
            HasResult = true;
            AddLog($"Saved: {path}");
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
                DesktopIntegration.RevealInFileManager(ResultImagePath);
        }

        private void OpenResultImage()
        {
            if (!string.IsNullOrEmpty(ResultImagePath) && File.Exists(ResultImagePath))
                DesktopIntegration.OpenFile(ResultImagePath);
        }

        private static string StripThinkingTokens(string text)
        {
            var opts = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            // Remove explicit <think>...</think> blocks
            var result = System.Text.RegularExpressions.Regex.Replace(
                text, @"<think>[\s\S]*?</think>", string.Empty, opts).Trim();
            var lines = result.Split('\n');
            var kept = new System.Collections.Generic.List<string>();
            bool hasContent = false;
            foreach (var line in lines)
            {
                var t = line.Trim();
                // First blank line after content = end of the clean paragraph
                if (hasContent && string.IsNullOrWhiteSpace(t)) break;
                if (string.IsNullOrWhiteSpace(t)) continue;
                // Known meta-commentary openers
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\(?(Note|Note:)\b", opts)) break;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\(\d+\)\s+The (input|output)\b", opts)) break;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^(Therefore|However|Additionally|The original|I'll ensure|Since we must|Corrected version)\b", opts)) break;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"(corrected version should read|per instructions|based on instruction)", opts)) break;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"\*\*(her|his|their)\s+\w+\s+is\s+now\b", opts)) break;
                // Duplicate paragraph guard
                if (hasContent && t.Length > 40 && kept.Any(k => k.Contains(t.Substring(0, Math.Min(40, t.Length))))) break;
                hasContent = true;
                kept.Add(System.Text.RegularExpressions.Regex.Replace(line, @"\*\*", string.Empty));
            }
            return string.Join("\n", kept).Trim();
        }

        private void AddLog(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            System.Windows.Application.Current?.Dispatcher.Invoke(() => LogOutput = LogOutput + line + "\n");
            _logger.LogInfo(message);
        }
    }
}
