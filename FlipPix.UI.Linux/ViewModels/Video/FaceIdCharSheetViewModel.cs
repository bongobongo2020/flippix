using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Linux.Models;
using FlipPix.UI.Linux.Services;
// MessageBox is fully qualified below: MsBox.Avalonia contributes a root
// namespace of the same name, so a using-alias would be a CS0576 conflict.
using Application = System.Windows.Application;

namespace FlipPix.UI.Linux.ViewModels.Video
{
    /// <summary>
    /// "FaceID Character Sheet" tab. A single-shot LTX 2.3 FaceID + Union-Control video:
    /// upload a character image (with an Analyze button that asks the llama-server for an LTX action
    /// prompt describing the image), an audio file (drives the generated speech/soundtrack) and a
    /// reference video (its pose/depth/edges are extracted and used as motion control) → produce one
    /// output video. Drives <c>faceid-charactersheet-unioncontrol-api.json</c> by editing node inputs.
    ///
    /// The shipped workflow copy is rewired so the audio conditioning reads the uploaded LoadAudio
    /// (node 42 → TrimAudioDuration 2182) instead of the reference video's own audio track.
    /// </summary>
    public partial class FaceIdCharSheetViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/ltx/faceid-charactersheet-unioncontrol-api.json";
        private const string OutputSubfolder = "faceid_charsheet";
        private const string SystemPromptFile = "faceid-charactersheet-action.md";

        // ── Workflow node ids (locked from faceid-charactersheet-unioncontrol-api.json) ─────────
        private const string NodeImage = "104";       // LoadImage "Face Reference"
        private const string NodeAudio = "42";        // LoadAudio "Load Custom Audio"
        private const string NodeAudioTrim = "2182";  // TrimAudioDuration (duration = clip length)
        private const string NodeVideo = "2243";      // VHS_LoadVideo "Reference Video To Extract Pose/Depth/Canny"
        private const string NodePrompt = "40";       // CLIPTextEncode "Automatic Prompt" (positive text)
        private const string NodeNegative = "35";     // CLIPTextEncode (negative text)
        private const string NodeSeed = "50";         // RandomNoise (pass 1)
        private const string NodeSeed2 = "131";       // RandomNoise (pass 2 / upscale)
        private const string NodeDuration = "31";     // PrimitiveFloat "Duration (Seconds)"
        private const string NodeOutputFinal = "167"; // SaveVideo (2-pass upscaled result)
        private const string NodeOutput1Pass = "101"; // SaveVideo (single-pass result)

        // ── Input state ────────────────────────────────────────────────────────
        private string _imagePath = string.Empty;
        private BitmapImage? _imagePreview;
        private string _imageInfo = string.Empty;

        private string _audioPath = string.Empty;

        private string _refVideoPath = string.Empty;
        private string _refVideoInfo = string.Empty;
        private string? _refVideoFileUri;

        private string _prompt = string.Empty;
        private string _negativePrompt =
            "pc game, console game, video game, cartoon, childish, ugly, artifacts, low resolution, blurry, jagged edges";
        private long _seed = -1;
        private double _lengthSeconds = 5;
        private bool _isAnalyzing;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _analyzeCts;
        private CancellationTokenSource? _runCts;

        public FaceIdCharSheetViewModel(
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

            SelectImageCommand = new RelayCommand(SelectImage);
            SelectAudioCommand = new RelayCommand(SelectAudio);
            SelectRefVideoCommand = new RelayCommand(SelectRefVideo);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            GenerateCommand = new RelayCommand(async () => await GenerateAsync(), () => CanGenerate);
            CancelCommand = new RelayCommand(() => _runCts?.Cancel(), () => IsProcessing);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            RandomSeedCommand = new RelayCommand(() => Seed = System.Random.Shared.NextInt64(0, long.MaxValue));

            AddLog("FaceID Character Sheet initialized");
        }

        #region Commands

        public ICommand SelectImageCommand { get; }
        public ICommand SelectAudioCommand { get; }
        public ICommand SelectRefVideoCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand RandomSeedCommand { get; }

        #endregion

        #region Input properties

        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (_imagePath != value)
                {
                    _imagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasImage));
                    _imagePreview = LoadImagePreview(value, out _imageInfo);
                    OnPropertyChanged(nameof(ImagePreview));
                    OnPropertyChanged(nameof(ImageInfo));
                    OnCanExecuteChanged();
                }
            }
        }

        public BitmapImage? ImagePreview => _imagePreview;
        public string ImageInfo => _imageInfo;

        public string AudioPath
        {
            get => _audioPath;
            set
            {
                if (_audioPath != value)
                {
                    _audioPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasAudio));
                    OnPropertyChanged(nameof(AudioInfo));
                    OnCanExecuteChanged();
                }
            }
        }

        public bool HasAudio => !string.IsNullOrEmpty(AudioPath) && File.Exists(AudioPath);
        public string AudioInfo => HasAudio ? Path.GetFileName(AudioPath) : string.Empty;

        public string RefVideoPath
        {
            get => _refVideoPath;
            set
            {
                if (_refVideoPath != value)
                {
                    _refVideoPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasRefVideo));
                    LoadRefVideoInfo();
                    OnCanExecuteChanged();
                }
            }
        }

        public string RefVideoInfo
        {
            get => _refVideoInfo;
            private set { if (_refVideoInfo != value) { _refVideoInfo = value; OnPropertyChanged(); } }
        }

        public string? RefVideoFileUri
        {
            get => _refVideoFileUri;
            private set { if (_refVideoFileUri != value) { _refVideoFileUri = value; OnPropertyChanged(); } }
        }

        public string Prompt
        {
            get => _prompt;
            set { if (_prompt != value) { _prompt = value; OnPropertyChanged(); OnCanExecuteChanged(); } }
        }

        public string NegativePrompt
        {
            get => _negativePrompt;
            set { if (_negativePrompt != value) { _negativePrompt = value; OnPropertyChanged(); } }
        }

        public long Seed
        {
            get => _seed;
            set { if (_seed != value) { _seed = value; OnPropertyChanged(); } }
        }

        /// <summary>Video length in seconds (clamped 1–30 when applied to the workflow).</summary>
        public double LengthSeconds
        {
            get => _lengthSeconds;
            set { if (Math.Abs(_lengthSeconds - value) > 0.0001) { _lengthSeconds = value; OnPropertyChanged(); } }
        }

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

        public bool HasImage => !string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath);
        public bool HasRefVideo => !string.IsNullOrEmpty(RefVideoPath) && File.Exists(RefVideoPath);

        public bool CanAnalyze => HasRefVideo && !IsAnalyzing && !IsProcessing;
        public bool CanGenerate => HasImage && HasAudio && HasRefVideo && !IsProcessing && !IsAnalyzing;

        #endregion

        #region File selection

        private async void SelectImage()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Character Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: "faceidcharsheet.image");

            if (path != null)
            {
                ImagePath = path;
                AddLog($"Character image: {Path.GetFileName(path)}");
            }
        }

        private async void SelectAudio()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Audio File",
                "Audio Files|*.mp3;*.wav;*.ogg;*.flac;*.m4a|All Files|*.*",
                initialDir,
                persistKey: "faceidcharsheet.audio");

            if (path != null)
            {
                AudioPath = path;
                AddLog($"Audio: {Path.GetFileName(path)}");
            }
        }

        private async void SelectRefVideo()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Reference Video",
                "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.webm|All Files|*.*",
                initialDir,
                persistKey: "faceidcharsheet.video");

            if (path != null)
            {
                RefVideoPath = path;
                AddLog($"Reference video: {Path.GetFileName(path)}");
            }
        }

        private BitmapImage? LoadImagePreview(string path, out string info)
        {
            info = string.Empty;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                var fi = new FileInfo(path);
                info = $"{bitmap.PixelWidth}×{bitmap.PixelHeight} • {fi.Length / 1024}KB";
                return bitmap;
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                info = "Error loading image";
                return null;
            }
        }

        private void LoadRefVideoInfo()
        {
            if (!HasRefVideo)
            {
                RefVideoInfo = string.Empty;
                RefVideoFileUri = null;
                return;
            }
            var fi = new FileInfo(RefVideoPath);
            RefVideoInfo = $"{fi.Name} • {fi.Length / 1024 / 1024.0:F1}MB";
            RefVideoFileUri = RefVideoPath;
        }

        #endregion

        #region Analysis (image → LTX action prompt)

        private async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;

            try
            {
                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);

                var models = await _lmStudioService.GetAvailableModelsAsync(token);
                var model = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(model) && models.Count > 0)
                    model = models[0].Id ?? models[0].Name ?? string.Empty;
                if (string.IsNullOrEmpty(model))
                {
                    System.Windows.MessageBox.Show("No LM Studio / llama-server model available. Ensure the server is running and a model is loaded.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AddLog("Extracting a frame from the reference video...");
                var framePath = ExtractAnalysisFrame(RefVideoPath);
                if (framePath == null)
                    throw new Exception("Could not extract a frame from the reference video (is FFmpeg installed?).");

                try
                {
                    AddLog($"Sending reference-video frame to {_lmStudioService.DescribeTarget(model)}");

                    var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                        "prompts", "prompt2json", SystemPromptFile);
                    if (!File.Exists(promptFilePath))
                        throw new FileNotFoundException($"System prompt not found: {promptFilePath}");
                    var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

                    var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                        model,
                        framePath,
                        "This is a frame from a reference video. Write one LTX video action prompt describing the person and the action shown.",
                        systemPrompt,
                        maxTokens: 2000,
                        cancellationToken: token);

                    var cleaned = CleanOutput(result);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        Prompt = cleaned;
                        AddLog($"Prompt generated ({cleaned.Length} chars)");
                    }
                    else
                    {
                        AddLog("WARNING: Analysis returned empty result");
                    }
                }
                finally
                {
                    try { File.Delete(framePath); } catch { /* best effort */ }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR during analysis: {ex.Message}");
                System.Windows.MessageBox.Show($"Analysis failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            // Keep a `ref_t2v:` prefix (the LTX FaceID model expects it); only strip a stray "prompt:".
            if (lower.StartsWith("prompt:") || lower.StartsWith("prompt :"))
                text = text.Substring(text.IndexOf(':') + 1).Trim();
            if (text.Length > 1 && text[0] == '"' && text[^1] == '"')
                text = text[1..^1].Trim();
            return text;
        }

        private static string EnsureRefPrefix(string prompt)
        {
            var t = (prompt ?? string.Empty).TrimStart();
            return t.StartsWith("ref_t2v:", StringComparison.OrdinalIgnoreCase) ? t : "ref_t2v: " + t;
        }

        #endregion

        #region Generation

        private async Task GenerateAsync()
        {
            if (!CanGenerate) return;

            IsProcessing = true;
            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = "Preparing FaceID Character Sheet workflow...";

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                AddLog("=== FaceID Character Sheet ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("FaceIdCharSheet", token);

                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFileName);
                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
                var json = await File.ReadAllTextAsync(workflowPath, token);

                ProcessingStatus = "Uploading character image...";
                ProcessingProgress = 5;
                AddLog("Uploading character image...");
                var imageName = await _comfyUIService.UploadImageAsync(ImagePath);
                if (string.IsNullOrEmpty(imageName)) throw new Exception("Failed to upload character image.");
                AddLog($"Image uploaded: {imageName}");

                ProcessingStatus = "Preparing audio...";
                ProcessingProgress = 8;
                // ComfyUI's LoadAudio decodes with PyAV, which chokes on some MP3/M4A headers and
                // exotic codecs ("Invalid data found ... avcodec_send_packet()"). Normalizing to a clean
                // PCM WAV first sidesteps those decode failures. Falls back to the original if ffmpeg is
                // unavailable or the transcode produces nothing.
                var (audioForUpload, audioIsTemp) = PrepareAudioForUpload(AudioPath);
                AddLog("Uploading audio...");
                var audioName = await _comfyUIService.UploadAudioAsync(audioForUpload);
                if (audioIsTemp) { try { File.Delete(audioForUpload); } catch { /* best effort */ } }
                if (string.IsNullOrEmpty(audioName)) throw new Exception("Failed to upload audio.");
                // Audio upload (unlike video) doesn't self-verify — confirm the bytes actually persisted
                // so a phantom 2xx doesn't surface later as a cryptic LoadAudio decode error.
                await _comfyUIService.HttpClient.VerifyInputFileExistsAsync(audioName, "", token);
                AddLog($"Audio uploaded: {audioName}");

                ProcessingStatus = "Uploading reference video...";
                ProcessingProgress = 12;
                AddLog("Uploading reference video...");
                var videoName = await _comfyUIService.UploadVideoAsync(RefVideoPath);
                if (string.IsNullOrEmpty(videoName)) throw new Exception("Failed to upload reference video.");
                AddLog($"Video uploaded: {videoName}");

                var runSeed = Seed >= 0 ? Seed : System.Random.Shared.NextInt64(0, long.MaxValue);
                var len = Math.Clamp(LengthSeconds <= 0 ? 5 : LengthSeconds, 1, 30);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var runToken = $"fics_{ts}";

                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeImage, "image", imageName);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeAudio, "audio", audioName);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeVideo, "video", videoName);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeAudioTrim, "duration", (int)Math.Round(len));
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeDuration, "value", len);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeSeed, "noise_seed", runSeed);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeSeed2, "noise_seed", runSeed);
                if (!string.IsNullOrWhiteSpace(NegativePrompt))
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeNegative, "text", NegativePrompt.Trim());
                // Only override the positive prompt when the user supplied/analyzed one — otherwise leave
                // node 40 wired to the workflow's in-graph auto-prompt (TextGenerate from the image).
                if (!string.IsNullOrWhiteSpace(Prompt))
                    WorkflowNodeUpdater.UpdateNodeInput(ref json, NodePrompt, "text", EnsureRefPrefix(Prompt));
                // Save both passes under a per-run token so we can locate the output reliably.
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeOutputFinal, "filename_prefix", $"{OutputSubfolder}/{runToken}_final");
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeOutput1Pass, "filename_prefix", $"{OutputSubfolder}/{runToken}_1pass");

                ProcessingProgress = 15;
                ProcessingStatus = "Generating video...";
                AddLog($"Generating (seed {runSeed}, {len:0.#}s)...");

                var local = await SubmitAndRetrieveAsync(json, runToken, 15, 95, token);
                if (local == null || !File.Exists(local))
                    throw new Exception("No output video was generated.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "FaceIdCharSheet");
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"FaceIdCharSheet_{ts}.mp4");
                File.Copy(local, finalPath, true);
                await LocalCopyService.CopyVideoAsync(finalPath);

                var fi = new FileInfo(finalPath);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResultVideoPath = finalPath;
                    ResultVideoInfo = $"FaceID Character Sheet • {fi.Length / 1024 / 1024.0:F1}MB";
                    HasResult = true;
                    OnCanExecuteChanged();
                });
                ProcessingProgress = 100;
                ProcessingStatus = "Complete!";
                AddLog($"=== Complete: {finalPath} ===");
            }
            catch (OperationCanceledException)
            {
                AddLog("Generation cancelled");
                ProcessingStatus = "Cancelled";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                ProcessingStatus = $"Error: {ex.Message}";
                System.Windows.MessageBox.Show($"Generation failed:\n{ex.Message}", "FaceID Character Sheet Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                lease?.Dispose();
                IsProcessing = false;
                _runCts?.Dispose();
                _runCts = null;
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Submits the workflow, waits for completion, and resolves the final SaveVideo output to a local
        /// file — preferring the 2-pass result (node 167), then the 1-pass (node 101), then a disk scan.
        /// </summary>
        private async Task<string?> SubmitAndRetrieveAsync(string json, string runToken, double from, double to, CancellationToken token)
        {
            var existing = GetExistingVideoFiles("*.mp4", OutputSubfolder);
            var promptId = await SubmitAsync(json, from, to, token);

            ProcessingStatus = "Waiting for output...";
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
            foreach (var node in new[] { NodeOutputFinal, NodeOutput1Pass })
            {
                if (byNode.TryGetValue(node, out var outs) && outs.Count > 0)
                {
                    var pick = outs.FirstOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? outs[0];
                    var local = await ResolveOutputToLocalAsync(pick);
                    if (local != null) return local;
                }
            }

            // Fallback: wait for a new mp4 carrying this run's token in the output subfolder.
            var found = await WaitForNewVideoAsync(existing, "*.mp4",
                TimeSpan.FromMinutes(45), TimeSpan.FromSeconds(4), OutputSubfolder);
            if (found != null && Path.GetFileName(found).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0)
                return found;
            return found ?? FindTokenFileOnDisk(runToken);
        }

        /// <summary>
        /// Extracts a single representative frame (the video's midpoint) from the reference video to a temp
        /// PNG for the llama-server to analyze into an action prompt. Returns null if FFmpeg is unavailable
        /// or extraction fails. Caller is responsible for deleting the returned file.
        /// </summary>
        private string? ExtractAnalysisFrame(string videoPath)
        {
            try
            {
                var ffmpeg = FindFFmpeg();
                if (ffmpeg == null) { AddLog("FFmpeg not found — cannot extract a video frame."); return null; }

                var dur = GetVideoDuration(videoPath);
                var at = dur > 1 ? dur / 2.0 : 0; // the midpoint frame is more representative of the action
                var outPath = Path.Combine(Path.GetTempPath(), $"fics_frame_{Guid.NewGuid():N}.png");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in new[]
                {
                    "-y", "-ss", at.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "-i", videoPath, "-frames:v", "1", "-q:v", "3", outPath
                }) psi.ArgumentList.Add(a);

                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return null;
                p.StandardError.ReadToEnd();
                p.WaitForExit(30000);

                if (File.Exists(outPath) && new FileInfo(outPath).Length > 0) return outPath;
                return null;
            }
            catch (Exception ex)
            {
                AddLog($"Frame extract failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Transcodes the selected audio to a clean 44.1 kHz stereo 16-bit PCM WAV in temp so ComfyUI's
        /// PyAV-based LoadAudio can always decode it. Returns (path, isTempFile); on any failure (ffmpeg
        /// missing, transcode error) it returns the original path so upload can still proceed.
        /// </summary>
        private (string path, bool isTemp) PrepareAudioForUpload(string audioPath)
        {
            try
            {
                var ffmpeg = FindFFmpeg();
                if (ffmpeg == null)
                {
                    AddLog("FFmpeg not found — uploading original audio as-is.");
                    return (audioPath, false);
                }

                var outPath = Path.Combine(Path.GetTempPath(), $"fics_audio_{Guid.NewGuid():N}.wav");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in new[] { "-y", "-i", audioPath, "-vn", "-ac", "2", "-ar", "44100", "-c:a", "pcm_s16le", outPath })
                    psi.ArgumentList.Add(a);

                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return (audioPath, false);
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(60000);

                if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                {
                    AddLog($"Audio normalized to WAV ({new FileInfo(outPath).Length / 1024}KB)");
                    return (outPath, true);
                }

                var tail = string.IsNullOrEmpty(err) ? "" : err.Substring(Math.Max(0, err.Length - 200));
                AddLog($"Audio transcode produced no file; uploading original. {tail}");
                return (audioPath, false);
            }
            catch (Exception ex)
            {
                AddLog($"Audio transcode failed: {ex.Message}; uploading original.");
                return (audioPath, false);
            }
        }

        private async Task<string> SubmitAsync(string json, double progressFrom, double progressTo, CancellationToken token)
        {
            var workflow = JsonSerializer.Deserialize<JsonElement>(json);
            var span = progressTo - progressFrom;
            var progress = new Progress<ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                {
                    var pct = (double)msg.Data.Value / msg.Data.Max;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProcessingProgress = progressFrom + pct * span;
                        ProcessingStatus = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                    });
                }
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token);
            AddLog($"Workflow submitted, ID: {promptId}");
            return promptId;
        }

        private async Task<string?> ResolveOutputToLocalAsync(string videoFile)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    var baseUrl = GetComfyUIBaseUrl();
                    bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                    string outputFolder = settings.ResolveOutputFolder(isRemote);
                    if (!string.IsNullOrEmpty(outputFolder))
                    {
                        var localPath = Path.Combine(outputFolder, videoFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(localPath))
                        {
                            await WaitForFileStableAsync(localPath);
                            return localPath;
                        }
                    }
                }

                var parts = videoFile.Split('/');
                var filename = parts.Last();
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : "";
                var bytes = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, subfolder);
                if (bytes is { Length: > 0 })
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"fics_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tempPath, bytes);
                    return tempPath;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Resolve output failed: {ex.Message}");
            }
            return null;
        }

        private string? FindTokenFileOnDisk(string runToken)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;
                var baseUrl = GetComfyUIBaseUrl();
                bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                var outputFolder = settings.ResolveOutputFolder(isRemote);
                if (string.IsNullOrEmpty(outputFolder)) return null;

                var candidates = new List<string>();
                foreach (var folder in new[] { outputFolder, Path.Combine(outputFolder, OutputSubfolder) })
                {
                    if (Directory.Exists(folder))
                        candidates.AddRange(Directory.GetFiles(folder, "*.mp4", SearchOption.AllDirectories)
                            .Where(f => Path.GetFileName(f).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                // Prefer the 2-pass "final" file over the 1-pass one.
                return candidates
                    .OrderByDescending(f => Path.GetFileName(f).IndexOf("_final", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ThenByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                AddLog($"Disk scan failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(CanAnalyze));
            OnPropertyChanged(nameof(CanGenerate));
            AnalyzeCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
        }
    }
}
