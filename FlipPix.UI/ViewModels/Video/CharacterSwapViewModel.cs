using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "🔁 Character Swap" tab — Openjutsu's one-click MiniMax H3 character swap (github.com/D3voz/openjutsu)
    /// inside FlipPix. Drop a video and a character photo, press Generate, and the main person in the video
    /// is replaced by the character; the camera, cuts, motion and soundtrack stay.
    ///
    /// <para><b>How it works.</b> Each clip, SAM 3.1 tracks everyone at 1008 px, the person who fills the most
    /// of the frame (Simple) or every person Qwen3-VL matches to a role (Advanced) is masked with some
    /// headroom, and H3 regenerates only that area with the photo as reference. The motion H3 follows is the
    /// source clip with that person turned into a grey mannequin of their exact silhouette. Long videos run
    /// as a chain of overlapping clips through the Context Loop pack, which saves a checkpoint per clip — so
    /// running the same inputs again resumes from the last finished clip.</para>
    ///
    /// <para>The graphs are upstream's API exports, unchanged; <see cref="CharacterSwapGraph"/> prunes and
    /// fills them per request exactly as Openjutsu's server does. Upstream's model filenames are resolved
    /// against what this ComfyUI actually has (a <c>h3-minimax/</c> subfolder, a different quantisation of
    /// the same text encoder), so a server laid out the FlipPix way runs it without renaming files.</para>
    /// </summary>
    public partial class CharacterSwapViewModel : VideoProcessingBaseViewModel
    {
        private readonly IFileDialogService _fileDialogService;
        private CancellationTokenSource? _runCts;
        private bool _loading = true;

        public CharacterSwapViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _fileDialogService = fileDialogService;

            for (var i = 1; i <= CharacterSwapGraph.MaxCharacters; i++)
                Characters.Add(new CharacterSwapSlot(i, slot => _ = BrowsePictureAsync(slot)));
            foreach (var slot in Characters) slot.PropertyChanged += (_, _) => RefreshCommands();

            BrowseVideoCommand = new RelayCommand(() => _ = BrowseVideoAsync(), () => !IsProcessing);
            GenerateCommand = new RelayCommand(() => _ = RunAsync(checkMask: false), CanGenerate);
            CheckMaskCommand = new RelayCommand(() => _ = RunAsync(checkMask: true), () => !IsProcessing && HasVideo && !IsAdvanced);
            StopCommand = new RelayCommand(Stop, () => IsProcessing);
            NewSeedCommand = new RelayCommand(() => Seed = NewSeed());
            RefreshModelsCommand = new RelayCommand(() => _ = LoadModelListsAsync());
            PlayResultCommand = new RelayCommand(() => ActivePreview = ResultVideoPath, () => HasResult);
            PlaySourceCommand = new RelayCommand(() => ActivePreview = VideoPath, () => HasVideo);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            ClearLogCommand = new RelayCommand(() => LogOutput = string.Empty);

            var saved = settingsService.Settings?.CharacterSwap ?? new CharacterSwapSettings();
            _isAdvanced = saved.Advanced;
            _resolution = CharacterSwapGraph.Resolutions.Contains(saved.Resolution) ? saved.Resolution : "736p";
            _clipSeconds = Math.Clamp(saved.ClipSeconds, CharacterSwapGraph.MinClipSeconds, CharacterSwapGraph.MaxClipSeconds);
            _motionSize = CharacterSwapGraph.MotionSizes.Contains(saved.MotionSize) ? saved.MotionSize : 512;
            _margin = Enum.TryParse<SwapMargin>(saved.Margin, out var m) ? m : SwapMargin.Normal;
            _hatFix = saved.HatFix;
            _noLipSync = saved.NoLipSync;
            _target = string.IsNullOrWhiteSpace(saved.Target) ? CharacterSwapGraph.DefaultTarget : saved.Target;
            _steps = Math.Clamp(saved.Steps, 1, 100);
            _selectedModel = saved.Model;
            _selectedLora = saved.Lora;
            _loraStrength = saved.LoraStrength;
            _seed = NewSeed();
            _loading = false;

            AddLog("Character Swap ready — pick a source video and a character photo, then Generate. " +
                   "Check mask previews what H3 will regenerate in about a minute per 3 s of video.");
            _ = LoadModelListsAsync();
        }

        // ── Options shown on the page ────────────────────────────────────────────────────────────

        public ObservableCollection<CharacterSwapSlot> Characters { get; } = new();
        public IReadOnlyList<string> ResolutionOptions => CharacterSwapGraph.Resolutions;
        public IReadOnlyList<int> MotionSizeOptions => CharacterSwapGraph.MotionSizes;
        public IReadOnlyList<SwapMargin> MarginOptions { get; } = Enum.GetValues<SwapMargin>();
        public ObservableCollection<string> ModelOptions { get; } = new();
        public ObservableCollection<string> LoraOptions { get; } = new();

        /// <summary>The characters this mode uses: slot 1 in Simple, all four in Advanced.</summary>
        public IEnumerable<CharacterSwapSlot> ActiveCharacters => IsAdvanced ? Characters : Characters.Take(1);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ActiveCharacters), nameof(IsSimple), nameof(ModeSummary))]
        private bool _isAdvanced;

        public bool IsSimple
        {
            get => !IsAdvanced;
            set => IsAdvanced = !value;
        }

        public string ModeSummary => IsAdvanced
            ? "Advanced: 1–4 characters, each with an optional role. Qwen3-VL matches every tracked person to a role per shot; people without a character stay untouched."
            : "Simple: one character replaces the main person — the one who fills the most of the frame. Anyone else stays as they are.";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasVideo), nameof(VideoFileName), nameof(ClipEstimate))]
        private string _videoPath = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ClipEstimate), nameof(VideoInfo))]
        private double _videoDuration;

        public bool HasVideo => !string.IsNullOrEmpty(VideoPath) && File.Exists(VideoPath);
        public string VideoFileName => HasVideo ? Path.GetFileName(VideoPath) : "No video selected";
        public string VideoInfo => VideoDuration > 0 ? $"{VideoDuration:F1} s" : string.Empty;

        /// <summary>Part to use: seconds into the source, and how long (0 = to the end). The sound is cut to the same part.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ClipEstimate))]
        private double _sectionStart;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ClipEstimate))]
        private double _sectionLength;

        [ObservableProperty] private string _resolution = "736p";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ClipEstimate))]
        private double _clipSeconds = CharacterSwapGraph.DefaultClipSeconds;

        [ObservableProperty] private int _motionSize = 512;
        [ObservableProperty] private SwapMargin _margin = SwapMargin.Normal;
        [ObservableProperty] private bool _hatFix;
        [ObservableProperty] private bool _noLipSync;
        [ObservableProperty] private string _target = CharacterSwapGraph.DefaultTarget;
        [ObservableProperty] private long _seed;
        [ObservableProperty] private int _steps = CharacterSwapGraph.DefaultSteps;
        [ObservableProperty] private string _selectedModel = string.Empty;
        [ObservableProperty] private string _selectedLora = string.Empty;
        [ObservableProperty] private double _loraStrength = 1.0;
        [ObservableProperty] private string _activePreview = string.Empty;
        [ObservableProperty] private string _maskPreviewPath = string.Empty;

        public string ClipEstimate
        {
            get
            {
                if (VideoDuration <= 0) return string.Empty;
                var used = Math.Max(0, VideoDuration - SectionStart);
                if (SectionLength > 0) used = Math.Min(used, SectionLength);
                var clips = CharacterSwapGraph.ClipCount(used, ClipSeconds);
                return clips <= 0
                    ? "That part is too short to render."
                    : $"{used:F1} s → about {clips} H3 clip{(clips == 1 ? "" : "s")} (hard cuts can add one)";
            }
        }

        partial void OnIsAdvancedChanged(bool value) { Save(); RefreshCommands(); }
        partial void OnResolutionChanged(string value) => Save();
        partial void OnClipSecondsChanged(double value) => Save();
        partial void OnMotionSizeChanged(int value) => Save();
        partial void OnMarginChanged(SwapMargin value) => Save();
        partial void OnHatFixChanged(bool value) => Save();
        partial void OnNoLipSyncChanged(bool value) => Save();
        partial void OnTargetChanged(string value) => Save();
        partial void OnStepsChanged(int value) => Save();
        partial void OnSelectedModelChanged(string value) => Save();
        partial void OnSelectedLoraChanged(string value) => Save();
        partial void OnLoraStrengthChanged(double value) => Save();
        partial void OnVideoPathChanged(string value) => RefreshCommands();

        private void Save()
        {
            if (_loading) return;
            var settings = _settingsService.Settings;
            if (settings == null) return;
            var s = settings.CharacterSwap ??= new CharacterSwapSettings();
            s.Advanced = IsAdvanced;
            s.Resolution = Resolution;
            s.ClipSeconds = ClipSeconds;
            s.MotionSize = MotionSize;
            s.Margin = Margin.ToString();
            s.HatFix = HatFix;
            s.NoLipSync = NoLipSync;
            s.Target = Target;
            s.Steps = Steps;
            s.Model = SelectedModel;
            s.Lora = SelectedLora;
            s.LoraStrength = LoraStrength;
            try { _settingsService.SaveSettings(settings); }
            catch (Exception ex) { _logger.LogWarning($"Character Swap: could not save settings: {ex.Message}"); }
        }

        // ── Commands ─────────────────────────────────────────────────────────────────────────────

        public RelayCommand BrowseVideoCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand CheckMaskCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand NewSeedCommand { get; }
        public RelayCommand RefreshModelsCommand { get; }
        public RelayCommand PlayResultCommand { get; }
        public RelayCommand PlaySourceCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand ClearLogCommand { get; }

        private bool CanGenerate() =>
            !IsProcessing && HasVideo && ActiveCharacters.Any(c => c.HasPicture)
            && (IsAdvanced || Characters[0].HasPicture);

        private void RefreshCommands()
        {
            void Notify()
            {
                BrowseVideoCommand.NotifyCanExecuteChanged();
                GenerateCommand.NotifyCanExecuteChanged();
                CheckMaskCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
                PlayResultCommand.NotifyCanExecuteChanged();
                PlaySourceCommand.NotifyCanExecuteChanged();
                OpenResultFolderCommand.NotifyCanExecuteChanged();
            }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) Notify();
            else dispatcher.BeginInvoke(Notify);
        }

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            // The base fires this from IsProcessing / HasResult setters, which run before the
            // generated commands exist during construction.
            if (GenerateCommand != null) RefreshCommands();
        }

        private static long NewSeed() => Random.Shared.NextInt64(0, 0xffffffffL);

        private async Task BrowseVideoAsync()
        {
            var start = _settingsService.Settings?.CharacterSwap?.LastVideoFolder;
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
                start = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select the source video (the person in it gets replaced)",
                "Video Files|*.mp4;*.mov;*.mkv;*.webm;*.avi|All Files|*.*",
                start, persistKey: "characterswap.video");
            if (path == null) return;

            VideoPath = path;
            SectionStart = 0;
            SectionLength = 0;
            ActivePreview = path;
            MaskPreviewPath = string.Empty;
            VideoDuration = await Task.Run(() => GetVideoDuration(path));
            AddLog($"Source video: {Path.GetFileName(path)} ({VideoDuration:F1} s)");

            var settings = _settingsService.Settings;
            if (settings != null)
            {
                (settings.CharacterSwap ??= new CharacterSwapSettings()).LastVideoFolder = Path.GetDirectoryName(path) ?? string.Empty;
                Save();
            }
        }

        private async Task BrowsePictureAsync(CharacterSwapSlot slot)
        {
            var start = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
                start = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var path = await _fileDialogService.OpenFileDialogAsync(
                $"Select the photo for {slot.Label}",
                "Image Files|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All Files|*.*",
                start, persistKey: "characterswap.picture");
            if (path == null) return;
            slot.PicturePath = path;
            AddLog($"{slot.Label}: {Path.GetFileName(path)}");
        }

        private void Stop()
        {
            _runCts?.Cancel();
            AddLog("Stopping… (ComfyUI is asked to interrupt; finished clips stay saved, so Generate resumes from them)");
            _ = _comfyUIService.HttpClient.InterruptAsync(CancellationToken.None);
        }

        // ── Model lists ──────────────────────────────────────────────────────────────────────────

        private async Task LoadModelListsAsync()
        {
            try
            {
                var unets = await _comfyUIService.HttpClient.GetUnetFilenamesAsync();
                var loras = await _comfyUIService.HttpClient.GetLoraFilenamesAsync();
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    var keepModel = SelectedModel;
                    var keepLora = SelectedLora;
                    Fill(ModelOptions, unets.Where(IsH3File));
                    Fill(LoraOptions, new[] { NoLora }.Concat(loras.Where(IsH3File)));
                    SelectedModel = !string.IsNullOrEmpty(keepModel) && ModelOptions.Contains(keepModel)
                        ? keepModel : CharacterSwapGraph.ResolveFile(CharacterSwapGraph.DefaultModel, ModelOptions.ToList(), "ref2va.*pruned", "ref2va");
                    SelectedLora = !string.IsNullOrEmpty(keepLora) && LoraOptions.Contains(keepLora)
                        ? keepLora : CharacterSwapGraph.ResolveFile(CharacterSwapGraph.DefaultLora, LoraOptions.ToList(), "dmad.*4step", "dmad");
                });
                AddLog($"Models: {ModelOptions.Count} H3 diffusion model(s), {LoraOptions.Count - 1} H3 LoRA(s) on the server.");
            }
            catch (Exception ex)
            {
                AddLog($"Could not read the server's model lists: {ex.Message}");
            }

            static bool IsH3File(string f) => f.Contains("h3", StringComparison.OrdinalIgnoreCase)
                                             || f.Contains("minimax", StringComparison.OrdinalIgnoreCase);
            static void Fill(ObservableCollection<string> target, IEnumerable<string> items)
            {
                target.Clear();
                foreach (var i in items) target.Add(i);
            }
        }

        public const string NoLora = "(none — use 20+ steps)";

        /// <summary>
        /// Upstream's component filenames resolved against this server. A different quantisation of the
        /// same model stands in when the exact file is absent (the 32B H3 text encoder ships as int4, w4a8
        /// and nvfp4; the 8B describer as int8 and fp8), and the log says which file each run used.
        /// </summary>
        private async Task<SwapModel> ResolveModelAsync(CancellationToken ct)
        {
            var http = _comfyUIService.HttpClient;
            var clips = await http.GetModelFilenamesAsync("CLIPLoader", "clip_name", ct);
            var vaes = await http.GetModelFilenamesAsync("VAELoader", "vae_name", ct);
            var ckpts = await http.GetModelFilenamesAsync("CheckpointLoaderSimple", "ckpt_name", ct);

            var lora = SelectedLora == NoLora || string.IsNullOrEmpty(SelectedLora) ? null : SelectedLora;
            var model = new SwapModel
            {
                Model = string.IsNullOrEmpty(SelectedModel) ? CharacterSwapGraph.DefaultModel : SelectedModel,
                Loras = lora == null ? new() : new() { new SwapLora(lora, LoraStrength) },
                Steps = Steps,
                Describer = CharacterSwapGraph.ResolveFile(CharacterSwapGraph.DefaultDescriber, clips,
                    @"qwen3[-_]?vl[-_]?8b", @"qwen3[-_]?vl[-_]?4b"),
                TextEncoder = CharacterSwapGraph.ResolveFile(CharacterSwapGraph.DefaultTextEncoder, clips,
                    @"qwen3vl_32b_minimax_h3.*(int4|w4a8)", @"qwen3vl_32b_minimax_h3"),
                VideoVae = CharacterSwapGraph.ResolveFile(CharacterSwapGraph.DefaultVideoVae, vaes, @"minimax_h3_video_vae"),
                AudioVae = CharacterSwapGraph.ResolveFile(CharacterSwapGraph.DefaultAudioVae, vaes, @"minimax_h3_audio_vae"),
                Sam = CharacterSwapGraph.ResolveFile("sam3.1_multiplex_fp16.safetensors", ckpts, @"sam3\.1", @"sam3"),
            };
            AddLog($"Model: {model.Model} | LoRA: {lora ?? "none"} | {model.Steps} steps");
            AddLog($"Text encoder: {model.TextEncoder} | describer: {model.Describer} | SAM: {model.Sam}");
            if (lora == null && Steps < 20) AddLog("⚠ No speed LoRA: H3 needs 20 or more steps without one.");
            return model;
        }

        // ── Run ──────────────────────────────────────────────────────────────────────────────────

        private static string GraphPath(string file) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "video", "h3-minimax", "character-swap", file);

        private static JsonObject LoadGraph(string file)
        {
            var path = GraphPath(file);
            if (!File.Exists(path)) throw new FileNotFoundException($"Character Swap workflow not found: {path}");
            var graph = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                        ?? throw new InvalidDataException($"{file} is not an API-format workflow.");
            CharacterSwapGraph.SilenceWhenSoundless(graph);
            return graph;
        }

        private SwapSettings CurrentSettings() => new()
        {
            Roles = ActiveCharacters.Where(c => c.HasPicture).Select(c => c.Role).ToList(),
            Resolution = Resolution,
            MotionSize = MotionSize,
            HatFix = HatFix,
            NoLipSync = NoLipSync,
            Margin = Margin,
            Target = Target,
            Seed = Seed,
            ClipSeconds = Math.Clamp(ClipSeconds, CharacterSwapGraph.MinClipSeconds, CharacterSwapGraph.MaxClipSeconds),
        };

        private async Task RunAsync(bool checkMask)
        {
            if (IsProcessing || !HasVideo) return;
            if (!checkMask && !CanGenerate()) return;

            IsProcessing = true;
            ProcessingProgress = 0;
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;
            var label = checkMask ? "Check mask" : IsAdvanced ? "Advanced swap" : "Simple swap";

            try
            {
                ProcessingStatus = "Waiting for the ComfyUI queue…";
                using var lease = await _workflowCoordinator.AcquireAsync("character_swap", ct);

                ProcessingStatus = "Uploading the source video…";
                AddLog($"{label}: uploading {Path.GetFileName(VideoPath)}");
                var video = await _comfyUIService.UploadVideoAsync(VideoPath, ct);

                JsonObject graph;
                string videoNode, finalNode;
                var settings = CurrentSettings();

                if (checkMask)
                {
                    graph = CharacterSwapGraph.PruneMask(LoadGraph(CharacterSwapGraph.MaskFile), HatFix, Margin, Target);
                    videoNode = CharacterSwapGraph.MaskNodes.Video;
                    finalNode = CharacterSwapGraph.MaskNodes.Preview;
                    var ckpts = await _comfyUIService.HttpClient.GetModelFilenamesAsync("CheckpointLoaderSimple", "ckpt_name", ct);
                    CharacterSwapGraph.Inputs(graph, "2")["ckpt_name"] =
                        CharacterSwapGraph.ResolveFile("sam3.1_multiplex_fp16.safetensors", ckpts, @"sam3\.1", @"sam3");
                }
                else
                {
                    var pictures = ActiveCharacters.Where(c => c.HasPicture).ToList();
                    var uploaded = new List<string>();
                    foreach (var p in pictures)
                    {
                        ProcessingStatus = $"Uploading {p.Label}…";
                        uploaded.Add(await _comfyUIService.UploadImageAsync(p.PicturePath, ct));
                    }

                    if (IsAdvanced)
                    {
                        graph = CharacterSwapGraph.PruneAdvanced(LoadGraph(CharacterSwapGraph.AdvancedFile), uploaded.Count, settings);
                        videoNode = CharacterSwapGraph.NodeByTitle(graph, CharacterSwapGraph.AdvancedVideoTitle);
                        for (var i = 0; i < uploaded.Count; i++)
                            CharacterSwapGraph.Inputs(graph, CharacterSwapGraph.NodeByTitle(graph, $"Picture {i + 1}"))["image"] = uploaded[i];
                    }
                    else
                    {
                        graph = CharacterSwapGraph.PruneSimple(LoadGraph(CharacterSwapGraph.SimpleFile), settings);
                        videoNode = CharacterSwapGraph.SimpleNodes.Video;
                        CharacterSwapGraph.Inputs(graph, CharacterSwapGraph.SimpleNodes.Picture)["image"] = uploaded[0];
                    }

                    CharacterSwapGraph.ApplyModel(graph, await ResolveModelAsync(ct));
                    finalNode = CharacterSwapGraph.NodeByTitle(graph, CharacterSwapGraph.FinalVideoTitle);
                    var assemble = CharacterSwapGraph.Inputs(graph, finalNode);
                    assemble["output_subfolder"] = "character_swap";
                    assemble["filename"] = $"swap_{Path.GetFileNameWithoutExtension(VideoPath)}_%date:yyyy-MM-dd%";
                }

                CharacterSwapGraph.Inputs(graph, videoNode)["video"] = video;
                CharacterSwapGraph.SetSection(graph, videoNode, SectionStart, SectionLength);
                CharacterSwapGraph.AssertNoDanglingLinks(graph);

                AddLog(checkMask
                    ? $"{label}: tracking \"{Target}\" with SAM 3.1 (margin {Margin}, hat fix {(HatFix ? "on" : "off")})"
                    : $"{label}: {Resolution}, clips of {settings.ClipSeconds:0.#} s, motion reference {MotionSize} px, seed {Seed}, " +
                      $"hat fix {(HatFix ? "on" : "off")}, no lip-sync {(NoLipSync ? "on" : "off")}. {ClipEstimate}");
                ProcessingStatus = checkMask ? "Checking the mask…" : "Rendering — H3 sampling takes most of the time…";

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data != null && msg.Data.Max > 0)
                        ProcessingProgress = (double)msg.Data.Value / msg.Data.Max * 100;
                });

                var started = DateTime.Now;
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(graph, progress, ct, TimeSpan.FromHours(8));
                AddLog($"{label}: finished in {(DateTime.Now - started):h\\:mm\\:ss}");

                var output = await ResolveOutputAsync(promptId, finalNode, ct);
                if (output == null)
                {
                    ProcessingStatus = "Finished, but no video was reported — see the log.";
                    AddLog("ComfyUI reported no video for this run. Check the ComfyUI console.");
                    return;
                }

                if (checkMask)
                {
                    MaskPreviewPath = output;
                    ActivePreview = output;
                    ProcessingStatus = "Mask ready: cyan is regenerated, orange is the tracked person; right is the mannequin H3 follows.";
                }
                else
                {
                    ResultVideoPath = output;
                    HasResult = true;
                    ResultVideoInfo = Path.GetFileName(output);
                    ActivePreview = output;
                    ProcessingStatus = "Done.";
                }
                ProcessingProgress = 100;
                AddLog($"Output: {output}");
            }
            catch (OperationCanceledException)
            {
                ProcessingStatus = "Stopped.";
                AddLog($"{label} stopped.");
            }
            catch (Exception ex)
            {
                ProcessingStatus = $"Failed: {ex.Message}";
                AddLog($"{label} failed: {ex.Message}");
                _logger.LogError(ex, "Character Swap run failed");
            }
            finally
            {
                IsProcessing = false;
                _runCts?.Dispose();
                _runCts = null;
                RefreshCommands();
            }
        }

        private async Task<string?> ResolveOutputAsync(string promptId, string finalNode, CancellationToken ct)
        {
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, ct);
            var file = byNode.TryGetValue(finalNode, out var files) ? files.FirstOrDefault(IsVideo) : null;
            file ??= byNode.Values.SelectMany(f => f).LastOrDefault(IsVideo);
            if (file == null) return null;
            return await FetchOutputFileAsync(file, ct);

            static bool IsVideo(string f) => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                                             || f.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
        }
    }
}
