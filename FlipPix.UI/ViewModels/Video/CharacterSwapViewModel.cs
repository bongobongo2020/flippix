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
    /// as a chain of overlapping clips through the Context Loop pack, which saves a checkpoint per clip. A swap
    /// cut short (Stop, or a ComfyUI restart) is continued from its last saved clip: by itself when the server
    /// comes back within a few minutes, otherwise with Resume, which finds the run on the server from the source
    /// video. Generate always starts a new swap.</para>
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
            foreach (var slot in Characters)
                slot.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(CharacterSwapSlot.Replaces)) ApplyReplaces(slot);
                    RefreshCommands();
                };
            FindPeopleCommand = new RelayCommand(() => _ = FindPeopleAsync(), () => !IsProcessing && HasVideo);

            BrowseVideoCommand = new RelayCommand(() => _ = BrowseVideoAsync(), () => !IsProcessing);
            GenerateCommand = new RelayCommand(() => _ = RunAsync(checkMask: false), CanGenerate);
            CheckMaskCommand = new RelayCommand(() => _ = RunAsync(checkMask: true), () => !IsProcessing && HasVideo && !IsAdvanced);
            StopCommand = new RelayCommand(Stop, () => IsProcessing);
            ResumeCommand = new RelayCommand(() => _ = RunAsync(checkMask: false, resume: _pending), () => !IsProcessing && _pending != null);
            CheckUnfinishedCommand = new RelayCommand(() => _ = CheckUnfinishedAsync(quiet: false), () => !IsProcessing && HasVideo);
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
            _keepVideoClothes = saved.Outfit == nameof(SwapOutfit.KeepVideo);
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

        /// <summary>Keep the video's clothes: only the head (face and hair) of the person is replaced, so the
        /// outfit is the source's own pixels and cannot change between clips. Off: the picture's outfit.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UsePictureClothes))]
        private bool _keepVideoClothes;

        public bool UsePictureClothes
        {
            get => !KeepVideoClothes;
            set => KeepVideoClothes = !value;
        }

        public SwapOutfit Outfit => KeepVideoClothes ? SwapOutfit.KeepVideo : SwapOutfit.FromPicture;
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

        partial void OnIsAdvancedChanged(bool value)
        {
            Save();
            RefreshCommands();
            // Simple and Advanced write a pick differently (role + SAM target vs. "role - look").
            foreach (var slot in Characters) ApplyReplaces(slot);
        }

        // ── Live clips ───────────────────────────────────────────────────────────────────────────

        /// <summary>One finished H3 clip of the running (or last) swap, previewed as it lands. A clip that was
        /// saved on the server by an earlier, interrupted run has no local file until it is first played.</summary>
        public sealed class LiveClip
        {
            public LiveClip(int number, string path, CharacterSwapGraph.SavedClip? saved = null)
            {
                Number = number;
                Path = path;
                Saved = saved;
            }

            public int Number { get; }
            public string Path { get; set; }
            public CharacterSwapGraph.SavedClip? Saved { get; }
            public string Label => Number.ToString();
        }

        /// <summary>Clips of the current or last swap, in order; click one to play it.</summary>
        public ObservableCollection<LiveClip> LiveClips { get; } = new();
        public bool HasLiveClips => LiveClips.Count > 0;

        public RelayCommand<LiveClip> PlayClipCommand => _playClipCommand ??= new RelayCommand<LiveClip>(clip =>
        {
            if (clip == null) return;
            // Picking an older clip stops the preview jumping to each new one; picking the newest resumes it.
            _followLatest = LiveClips.Count > 0 && clip == LiveClips[^1];
            if (File.Exists(clip.Path)) ActivePreview = clip.Path;
            else if (clip.Saved != null) _ = FetchSavedClipAsync(clip);
        });

        private async Task FetchSavedClipAsync(LiveClip clip)
        {
            if (clip.Saved is not { } saved) return;
            try
            {
                ProcessingStatus = $"Fetching clip {clip.Number} from the server…";
                var bytes = await _comfyUIService.HttpClient.DownloadViewFileAsync(saved.Filename, saved.Subfolder, "output");
                if (bytes is not { Length: > 0 })
                {
                    ProcessingStatus = $"The server no longer has clip {clip.Number}.";
                    return;
                }
                var folder = System.IO.Path.Combine(LiveClipRoot, "saved");
                Directory.CreateDirectory(folder);
                var path = System.IO.Path.Combine(folder, saved.Filename);
                await File.WriteAllBytesAsync(path, bytes);
                clip.Path = path;
                ActivePreview = path;
                ProcessingStatus = $"Clip {clip.Number} (saved on the server).";
            }
            catch (Exception ex)
            {
                ProcessingStatus = $"Could not fetch clip {clip.Number}: {ex.Message}";
            }
        }

        /// <summary>Lists a run's saved clips in the strip, so they can be watched before (or while) it resumes.</summary>
        private void ShowSavedClips(IReadOnlyList<CharacterSwapGraph.SavedClip> clips)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                LiveClips.Clear();
                foreach (var c in clips) LiveClips.Add(new LiveClip(c.Number, string.Empty, c));
                OnPropertyChanged(nameof(HasLiveClips));
            });
        }
        private RelayCommand<LiveClip>? _playClipCommand;
        private bool _followLatest = true;

        private static readonly string LiveClipRoot =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlipPix", "character_swap_clips");

        /// <summary>State of one run's live previews: which clip is rendering and when each one landed.</summary>
        private sealed class LiveRun
        {
            public int Index;            // clip being rendered now (1-based); 0 until the chain reports it
            public int Count;            // clips in the plan; 0 until known
            public int Done;
            public readonly List<DateTime> Landed = new();
            public string Folder = string.Empty;
            public string? RunName;      // the chain's folder under output/h3_chains, once the chain reports it
            public DateTime LastActivity = DateTime.Now;
            public EventHandler<FlipPix.ComfyUI.Models.NodeExecutedEventArgs> Handler = null!;
        }

        /// <param name="saved">Clips an earlier, interrupted run already saved: they stay in the strip.</param>
        private LiveRun StartLiveClips(CancellationToken ct, IReadOnlyList<CharacterSwapGraph.SavedClip>? saved = null)
        {
            var run = new LiveRun { Folder = System.IO.Path.Combine(LiveClipRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss")) };
            ClearOldLiveClips();
            if (saved is { Count: > 0 }) ShowSavedClips(saved);
            else
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    LiveClips.Clear();
                    OnPropertyChanged(nameof(HasLiveClips));
                });
            _followLatest = true;

            run.Handler = (_, e) =>
            {
                run.LastActivity = DateTime.Now;
                if (CharacterSwapGraph.ActiveClip(e.OutputJson) is { } scene)
                {
                    run.Index = scene.Index;
                    run.Count = scene.Count;
                    run.RunName ??= CharacterSwapGraph.ActiveRunName(e.OutputJson);
                    if (run.Done == 0)
                        ProcessingStatus = $"Rendering clip {scene.Index} of {scene.Count}… the first clip also loads the models.";
                    return;
                }
                var file = e.Files.FirstOrDefault(f => f.Filename.StartsWith(CharacterSwapGraph.ClipPreviewPrefix, StringComparison.Ordinal));
                if (file != null) _ = FetchLiveClipAsync(run, file, ct);
            };
            return run;
        }

        private async Task FetchLiveClipAsync(LiveRun run, FlipPix.ComfyUI.Models.OutputFileRef file, CancellationToken ct)
        {
            var number = run.Index > 0 ? run.Index : run.Done + 1;
            run.Done++;
            run.Landed.Add(DateTime.Now);
            try
            {
                var bytes = await _comfyUIService.HttpClient.DownloadViewFileAsync(file.Filename, file.Subfolder, file.Type, ct);
                if (bytes is not { Length: > 0 }) return;
                Directory.CreateDirectory(run.Folder);
                var path = System.IO.Path.Combine(run.Folder, $"clip_{number:000}.mp4");
                await File.WriteAllBytesAsync(path, bytes, ct);

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    var clip = new LiveClip(number, path);
                    var at = 0;
                    while (at < LiveClips.Count && LiveClips[at].Number < number) at++;
                    LiveClips.Insert(at, clip);
                    OnPropertyChanged(nameof(HasLiveClips));
                    if (_followLatest && at == LiveClips.Count - 1) ActivePreview = path;
                });

                var left = run.Count > 0 ? run.Count - number : 0;
                var eta = RemainingEstimate(run.Landed, left);
                ProcessingStatus = run.Count > 0
                    ? $"Clip {number} of {run.Count} done{(eta != null ? $" · about {eta} left" : "")}. The preview shows each clip as it lands."
                    : $"Clip {number} done. The preview shows each clip as it lands.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning($"Character Swap: could not fetch the preview of clip {number}: {ex.Message}");
            }
        }

        /// <summary>Time left from the gaps between finished clips. The first gap is skipped once there are
        /// others: it includes loading the models, which later clips do not repeat.</summary>
        private static string? RemainingEstimate(List<DateTime> landed, int clipsLeft)
        {
            if (clipsLeft <= 0 || landed.Count < 2) return null;
            var gaps = landed.Zip(landed.Skip(1), (a, b) => (b - a).TotalSeconds).ToList();
            if (gaps.Count > 1) gaps.RemoveAt(0);
            var left = TimeSpan.FromSeconds(gaps.Average() * clipsLeft);
            return left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes:00} min" : $"{Math.Max(1, (int)Math.Round(left.TotalMinutes))} min";
        }

        /// <summary>Previews are only for watching a run; earlier runs' folders go when a new run starts.
        /// A file the player still has open stays until next time.</summary>
        private static void ClearOldLiveClips()
        {
            try
            {
                if (!Directory.Exists(LiveClipRoot)) return;
                foreach (var dir in Directory.EnumerateDirectories(LiveClipRoot))
                    try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            catch (Exception) { }
        }

        // ── Resume ───────────────────────────────────────────────────────────────────────────────

        /// <summary>A swap that stopped part-way and can continue: the prompt it was queued with, and its
        /// run folder on the server when known (found from the source video otherwise).</summary>
        private sealed record PendingSwap(JsonObject Prompt, string? RunName, string VideoPath);

        private PendingSwap? _pending;

        /// <summary>A swap of this video stopped part-way and can continue from its last saved clip.</summary>
        [ObservableProperty] private bool _hasUnfinished;

        [ObservableProperty] private string _unfinishedStatus = string.Empty;

        /// <summary>Times a run is continued by itself after ComfyUI drops it, before it is left to the Resume button.</summary>
        private const int MaxAutoResumes = 3;
        private static readonly TimeSpan ServerReturnWait = TimeSpan.FromMinutes(15);
        /// <summary>No progress at all for this long during a clip, and the page says ComfyUI may be stuck.</summary>
        private static readonly TimeSpan StallWarning = TimeSpan.FromMinutes(20);

        private void SetPending(PendingSwap? pending, CharacterSwapGraph.ChainRun? run = null, bool fromServer = false)
        {
            _pending = pending;
            HasUnfinished = pending != null;
            UnfinishedStatus = pending == null ? string.Empty
                : run == null ? "This swap stopped part-way. Its finished clips are saved on the server; Resume continues from the last one."
                : $"{(fromServer ? $"An earlier swap of this video (last clip saved {run.ModifiedUtc.ToLocalTime():d MMM HH:mm})" : "This swap")} " +
                  $"stopped after {run.Done} of {run.Total} clips ({run.Percent}%). Resume continues at clip {run.Done + 1} " +
                  "with the settings it was started with.";
            RefreshCommands();
        }

        /// <summary>
        /// Looks on the server for a swap of <paramref name="localVideo"/> that stopped part-way: the newest
        /// unfinished swap run whose saved prompt loads this video.
        /// </summary>
        private async Task<(CharacterSwapGraph.ChainRun Run, JsonObject Prompt)?> FindUnfinishedAsync(string localVideo, CancellationToken ct)
        {
            var http = _comfyUIService.HttpClient;
            var runs = CharacterSwapGraph.UnfinishedRuns(
                await http.GetTextAsync($"{CharacterSwapGraph.ChainRoutes}/runs", TimeSpan.FromMinutes(1), ct));
            foreach (var run in runs.Where(r => r.ModifiedUtc > DateTime.UtcNow.AddDays(-30)).Take(8))
            {
                var bytes = await http.DownloadViewFileAsync("api_prompt.json", $"h3_chains/{run.RunName}/.h3", "output", ct);
                if (bytes == null) continue;
                var prompt = CharacterSwapGraph.ParseSavedPrompt(System.Text.Encoding.UTF8.GetString(bytes));
                if (prompt == null || CharacterSwapGraph.ChainVideo(prompt) is not { } video
                    || !CharacterSwapGraph.SameVideo(video, localVideo)) continue;
                // /runs counts every checkpoint; only the unbroken run from clip 1 can be continued.
                var (next, _) = await SavedClipsAsync(run.RunName, ct);
                return (run with { Done = next - 1 }, prompt);
            }
            return null;
        }

        private async Task<(int Next, List<CharacterSwapGraph.SavedClip> Clips)> SavedClipsAsync(string runName, CancellationToken ct) =>
            CharacterSwapGraph.SavedClips(await _comfyUIService.HttpClient.GetTextAsync(
                $"{CharacterSwapGraph.ChainRoutes}/checkpoints?run_name={Uri.EscapeDataString(runName)}", TimeSpan.FromMinutes(1), ct));

        /// <summary>Checks the server for an unfinished swap of the current video and offers Resume for it.</summary>
        private async Task CheckUnfinishedAsync(bool quiet)
        {
            if (!HasVideo || IsProcessing) return;
            var path = VideoPath;
            try
            {
                if (!quiet) UnfinishedStatus = "Looking for an unfinished swap of this video on the server…";
                var found = await FindUnfinishedAsync(path, CancellationToken.None);
                if (path != VideoPath || IsProcessing) return;
                if (found is { } f && f.Run.Done > 0)
                {
                    SetPending(new PendingSwap(f.Prompt, f.Run.RunName, path), f.Run, fromServer: true);
                    var (_, clips) = await SavedClipsAsync(f.Run.RunName, CancellationToken.None);
                    if (!IsProcessing) ShowSavedClips(clips);
                    AddLog($"Unfinished swap of {Path.GetFileName(path)} on the server: {f.Run.Done} of {f.Run.Total} clips saved " +
                           $"({f.Run.RunName}). Resume continues it; Generate starts a new one.");
                }
                else if (_pending?.VideoPath != path)
                {
                    SetPending(null);
                    if (!quiet) AddLog($"No unfinished swap of {Path.GetFileName(path)} on the server.");
                }
                else if (!quiet) UnfinishedStatus = "Could not find this swap's saved clips on the server yet. Is ComfyUI back up?";
            }
            catch (Exception ex)
            {
                if (!quiet) UnfinishedStatus = $"Could not ask the server about unfinished swaps: {ex.Message}";
                _logger.LogWarning($"Character Swap: unfinished-swap check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The prompt that continues <paramref name="pending"/>: its saved prompt pinned to its run folder and
        /// starting at the first clip the server has not saved. Throws when the run cannot be found.
        /// </summary>
        private async Task<(JsonObject Graph, string RunName, int Next, List<CharacterSwapGraph.SavedClip> Clips)> PrepareContinueAsync(
            PendingSwap pending, CancellationToken ct)
        {
            var runName = pending.RunName;
            if (runName == null)
            {
                var found = await FindUnfinishedAsync(pending.VideoPath, ct)
                            ?? throw new InvalidOperationException("No saved clips of this swap were found on the server, so it has to start over (Generate).");
                runName = found.Run.RunName;
            }
            var (next, clips) = await SavedClipsAsync(runName, ct);
            var graph = (JsonObject)pending.Prompt.DeepClone();
            if (next > 1) CharacterSwapGraph.PrepareResume(graph, runName, next);
            return (graph, runName, next, clips);
        }

        // ── People in the video ──────────────────────────────────────────────────────────────────

        /// <summary>Who the describer saw in the source, for the "Replaces" picker under each character.</summary>
        public ObservableCollection<SwapPerson> People { get; } = new();
        public bool HasPeople => People.Count > 0;

        [ObservableProperty]
        private string _peopleStatus = "Find people lists who is in the video, so you can pick who each character replaces.";

        /// <summary>
        /// A pick fills the fields the swap already reads. Advanced: the role field gets "role - look", the
        /// form Qwen matches tracks against. Simple: SAM replaces the biggest match of the target text, so
        /// the look becomes the target ("man in a blue shirt") and the role names them in the prompt.
        /// </summary>
        private void ApplyReplaces(CharacterSwapSlot slot)
        {
            var person = slot.Replaces;
            if (person == null) return;
            if (IsAdvanced)
            {
                slot.Role = person.Line;
            }
            else if (slot.Number == 1)
            {
                slot.Role = person.Role;
                // A short noun ("woman") tracks far more reliably than the whole description.
                if (person.Look.Length > 0) Target = CharacterSwapGraph.SamTarget(person.Look);
            }
        }

        private async Task FindPeopleAsync()
        {
            if (IsProcessing || !HasVideo) return;
            IsProcessing = true;
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;
            try
            {
                PeopleStatus = "Waiting for the ComfyUI queue…";
                using var lease = await _workflowCoordinator.AcquireAsync("character_swap", ct);

                PeopleStatus = "Uploading the source video…";
                ProcessingStatus = "Finding people in the video…";
                var video = await _comfyUIService.UploadVideoAsync(VideoPath, ct);
                var clips = await _comfyUIService.HttpClient.GetModelFilenamesAsync("CLIPLoader", "clip_name", ct);
                var describer = CharacterSwapGraph.ResolveFile(CharacterSwapGraph.DefaultDescriber, clips,
                    @"qwen3[-_]?vl[-_]?8b", @"qwen3[-_]?vl[-_]?4b");

                var graph = CharacterSwapGraph.BuildFindPeople(LoadGraph(CharacterSwapGraph.AdvancedFile, sourceHasAudio: true),
                    video, describer, SectionStart, SectionLength, VideoDuration);
                PeopleStatus = "Qwen3-VL is looking at the video…";
                AddLog($"Find people: {CharacterSwapGraph.PeopleFrames} frames across the {(SectionLength > 0 || SectionStart > 0 ? "chosen part" : "whole video")}, describer {describer}");

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(graph, null, ct, TimeSpan.FromMinutes(15));
                var texts = await _comfyUIService.HttpClient.GetTextOutputsByNodeAsync(promptId, ct);
                var text = texts.TryGetValue(CharacterSwapGraph.PeopleOutput, out var lines) ? string.Join("\n", lines) : null;
                var people = CharacterSwapGraph.ParsePeople(text);

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    foreach (var slot in Characters) slot.Replaces = null;
                    People.Clear();
                    foreach (var p in people) People.Add(p);
                    OnPropertyChanged(nameof(HasPeople));
                });

                if (people.Count == 0)
                {
                    PeopleStatus = "No people found. Try a part of the video where they are clearly visible.";
                    AddLog($"Find people: the describer listed nobody{(string.IsNullOrWhiteSpace(text) ? "" : $" (it said: {text.Trim()})")}");
                }
                else
                {
                    PeopleStatus = IsAdvanced
                        ? $"Found {people.Count}. Pick who each character replaces under its picture."
                        : $"Found {people.Count}. Pick who the character replaces under its picture (Simple mode tracks them by their look).";
                    foreach (var p in people) AddLog($"Find people: {p.Display}");
                }
                ProcessingStatus = string.Empty;
            }
            catch (OperationCanceledException)
            {
                PeopleStatus = "Stopped.";
                ProcessingStatus = string.Empty;
            }
            catch (Exception ex)
            {
                PeopleStatus = $"Could not list people: {ex.Message}";
                ProcessingStatus = string.Empty;
                AddLog($"Find people failed: {ex.Message}");
                _logger.LogError(ex, "Character Swap find people failed");
            }
            finally
            {
                IsProcessing = false;
                _runCts?.Dispose();
                _runCts = null;
                RefreshCommands();
            }
        }
        partial void OnResolutionChanged(string value) => Save();
        partial void OnClipSecondsChanged(double value) => Save();
        partial void OnMotionSizeChanged(int value) => Save();
        partial void OnMarginChanged(SwapMargin value) => Save();
        partial void OnHatFixChanged(bool value) => Save();
        partial void OnNoLipSyncChanged(bool value) => Save();
        partial void OnTargetChanged(string value) => Save();
        partial void OnKeepVideoClothesChanged(bool value) => Save();
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
            s.Outfit = Outfit.ToString();
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
        public RelayCommand FindPeopleCommand { get; }
        public RelayCommand StopCommand { get; }
        /// <summary>Continues a swap that stopped part-way from its last clip saved on the server.</summary>
        public RelayCommand ResumeCommand { get; }
        public RelayCommand CheckUnfinishedCommand { get; }
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
                FindPeopleCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
                ResumeCommand.NotifyCanExecuteChanged();
                CheckUnfinishedCommand.NotifyCanExecuteChanged();
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
            foreach (var slot in Characters) slot.Replaces = null;
            People.Clear();
            OnPropertyChanged(nameof(HasPeople));
            PeopleStatus = "Find people lists who is in this video, so you can pick who each character replaces.";
            VideoDuration = await Task.Run(() => GetVideoDuration(path));
            AddLog($"Source video: {Path.GetFileName(path)} ({VideoDuration:F1} s)");
            if (_pending?.VideoPath != path) SetPending(null);
            _ = CheckUnfinishedAsync(quiet: true);

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
            AddLog("Stopping… (ComfyUI is asked to interrupt; finished clips stay saved on the server, and Resume continues from them)");
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

        private static JsonObject LoadGraph(string file, bool sourceHasAudio)
        {
            var path = GraphPath(file);
            if (!File.Exists(path)) throw new FileNotFoundException($"Character Swap workflow not found: {path}");
            var graph = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                        ?? throw new InvalidDataException($"{file} is not an API-format workflow.");
            CharacterSwapGraph.SilenceWhenSoundless(graph);
            if (!sourceHasAudio) CharacterSwapGraph.UseSilenceForSoundless(graph);
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
            Outfit = Outfit,
            Target = Target,
            Seed = Seed,
            ClipSeconds = Math.Clamp(ClipSeconds, CharacterSwapGraph.MinClipSeconds, CharacterSwapGraph.MaxClipSeconds),
        };

        private async Task RunAsync(bool checkMask, PendingSwap? resume = null)
        {
            if (IsProcessing) return;
            if (resume == null && (!HasVideo || (!checkMask && !CanGenerate()))) return;

            IsProcessing = true;
            ProcessingProgress = 0;
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;
            var label = resume != null ? "Resume swap" : checkMask ? "Check mask" : IsAdvanced ? "Advanced swap" : "Simple swap";
            JsonObject? submitted = null;
            LiveRun? live = null;

            try
            {
                ProcessingStatus = "Waiting for the ComfyUI queue…";
                using var lease = await _workflowCoordinator.AcquireAsync("character_swap", ct);

                JsonObject graph;
                string finalNode;
                IReadOnlyList<CharacterSwapGraph.SavedClip>? saved = null;

                if (resume != null)
                {
                    ProcessingStatus = "Checking ComfyUI is up…";
                    if (!await _comfyUIService.WaitForServerReadyAsync(TimeSpan.FromSeconds(45), ct))
                        throw new InvalidOperationException("ComfyUI is not answering. Start it, then press Resume again.");
                    ProcessingStatus = "Reading the saved clips…";
                    var (resumed, runName, next, clips) = await PrepareContinueAsync(resume, ct);
                    graph = resumed;
                    saved = clips;
                    resume = resume with { RunName = runName };
                    // The run loads its source by its server name; put the file back if the server lost it.
                    if (File.Exists(resume.VideoPath) && CharacterSwapGraph.ChainVideo(graph) is { } serverVideo
                        && CharacterSwapGraph.SameVideo(serverVideo, resume.VideoPath))
                    {
                        var name = await _comfyUIService.UploadVideoAsync(resume.VideoPath, ct);
                        foreach (var (_, node) in graph)
                            if (node?["class_type"]?.GetValue<string>() == "VHS_LoadVideo" && node["inputs"]?["video"]?.GetValue<string>() == serverVideo)
                                node["inputs"]!["video"] = name;
                    }
                    finalNode = CharacterSwapGraph.NodeByTitle(graph, CharacterSwapGraph.FinalVideoTitle);
                    AddLog(next > 1
                        ? $"{label}: clips 1–{next - 1} are saved on the server ({runName}); continuing at clip {next} with the settings the swap was started with."
                        : $"{label}: no clip of {runName} was saved yet, so it starts from clip 1 with the settings it was started with.");
                }
                else
                {
                    ProcessingStatus = "Sending the source video to ComfyUI…";
                    var hasAudio = await Task.Run(() => HasAudioStream(VideoPath), ct);
                    if (!hasAudio) AddLog($"{label}: the source has no audio track; H3 gets silence instead.");
                    var sent = DateTime.Now;
                    var video = await _comfyUIService.UploadVideoAsync(VideoPath, ct);
                    var took = DateTime.Now - sent;
                    AddLog(took.TotalSeconds < 5
                        ? $"{label}: {Path.GetFileName(VideoPath)} is ready on the server"
                        : $"{label}: uploaded {Path.GetFileName(VideoPath)} in {took:m\\:ss}");

                    string videoNode;
                    var settings = CurrentSettings();

                    if (checkMask)
                    {
                        graph = CharacterSwapGraph.PruneMask(LoadGraph(CharacterSwapGraph.MaskFile, hasAudio), HatFix, Margin, Target, Outfit);
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
                            graph = CharacterSwapGraph.PruneAdvanced(LoadGraph(CharacterSwapGraph.AdvancedFile, hasAudio), uploaded.Count, settings);
                            videoNode = CharacterSwapGraph.NodeByTitle(graph, CharacterSwapGraph.AdvancedVideoTitle);
                            for (var i = 0; i < uploaded.Count; i++)
                                CharacterSwapGraph.Inputs(graph, CharacterSwapGraph.NodeByTitle(graph, $"Picture {i + 1}"))["image"] = uploaded[i];
                        }
                        else
                        {
                            graph = CharacterSwapGraph.PruneSimple(LoadGraph(CharacterSwapGraph.SimpleFile, hasAudio), settings);
                            videoNode = CharacterSwapGraph.SimpleNodes.Video;
                            CharacterSwapGraph.Inputs(graph, CharacterSwapGraph.SimpleNodes.Picture)["image"] = uploaded[0];
                        }

                        CharacterSwapGraph.ApplyModel(graph, await ResolveModelAsync(ct));
                        CharacterSwapGraph.AddClipPreview(graph);
                        finalNode = CharacterSwapGraph.NodeByTitle(graph, CharacterSwapGraph.FinalVideoTitle);
                        var assemble = CharacterSwapGraph.Inputs(graph, finalNode);
                        assemble["output_subfolder"] = "character_swap";
                        assemble["filename"] = $"swap_{Path.GetFileNameWithoutExtension(VideoPath)}_%date:yyyy-MM-dd%";
                    }

                    CharacterSwapGraph.Inputs(graph, videoNode)["video"] = video;
                    CharacterSwapGraph.SetSection(graph, videoNode, SectionStart, SectionLength);

                    AddLog(checkMask
                        ? $"{label}: tracking \"{Target}\" with SAM 3.1 (margin {Margin}, hat fix {(HatFix ? "on" : "off")})"
                        : $"{label}: {Resolution}, clips of {settings.ClipSeconds:0.#} s, motion reference {MotionSize} px, seed {Seed}, " +
                          $"hat fix {(HatFix ? "on" : "off")}, no lip-sync {(NoLipSync ? "on" : "off")}. {ClipEstimate}");
                    if (KeepVideoClothes)
                        AddLog($"{label}: keeping the video's clothes — only the head (face and hair) is replaced.");
                    if (!checkMask && IsAdvanced && ActiveCharacters.Count(c => c.HasPicture) == 1)
                        AddLog($"{label}: tip — with one character, Simple mode with \"What to replace\" set to e.g. \"woman\" tracks " +
                               "them by SAM alone. Advanced asks Qwen who each tracked person is, and parts it is unsure of stay unswapped, " +
                               "which shows as the original look coming back for a few frames.");
                }

                CharacterSwapGraph.AssertNoDanglingLinks(graph);
                ProcessingStatus = checkMask ? "Checking the mask…" : "Rendering — H3 sampling takes most of the time…";

                live = checkMask ? null : StartLiveClips(ct, saved);
                if (live != null && saved is { Count: > 0 }) live.Done = saved.Count;
                if (live != null && resume?.RunName != null) live.RunName = resume.RunName;
                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (live != null) live.LastActivity = DateTime.Now;
                    if (msg.Data == null || msg.Data.Max <= 0) return;
                    var step = (double)msg.Data.Value / msg.Data.Max;
                    // Over the whole run once the clip count is known, so the bar does not refill every clip.
                    ProcessingProgress = live is { Count: > 0 }
                        ? Math.Min(99, (live.Index - 1 + step) / live.Count * 100)
                        : step * 100;
                });

                var started = DateTime.Now;
                submitted = graph;
                var promptId = checkMask
                    ? await _comfyUIService.ExecuteWorkflowAsync(graph, progress, ct, TimeSpan.FromHours(1))
                    : await RenderChainAsync(graph, label, live!, progress, resume?.VideoPath ?? VideoPath, ct);
                AddLog($"{label}: finished in {(DateTime.Now - started):h\\:mm\\:ss}");
                if (!checkMask) SetPending(null);

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
                OfferResume(submitted, live, resume);
            }
            catch (Exception ex)
            {
                ProcessingStatus = $"Failed: {ex.Message}";
                AddLog($"{label} failed: {ex.Message}");
                var hint = resume != null ? null : CharacterSwapGraph.EmptyMaskHint(ex.Message, IsAdvanced && !checkMask,
                    ActiveCharacters.Where(c => c.HasPicture).Select(c => c.Role).ToList(), Target, SectionLength <= 0);
                if (hint != null)
                {
                    ProcessingStatus = "Failed: nobody to replace was found. See the log.";
                    AddLog($"{label}: {hint}");
                }
                else if (ex is FlipPix.ComfyUI.Exceptions.ComfyUIPromptLostException)
                {
                    OfferResume(submitted, live, resume);
                }
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

        /// <summary>After a stop or a lost server: what was saved, and the Resume button for the rest.</summary>
        private void OfferResume(JsonObject? submitted, LiveRun? live, PendingSwap? resume)
        {
            if (submitted == null || live == null) return;
            var video = resume?.VideoPath ?? VideoPath;
            var done = Math.Max(0, live.Index - 1);
            SetPending(new PendingSwap(submitted, live.RunName ?? resume?.RunName, video),
                live.Count > 0 ? new CharacterSwapGraph.ChainRun(live.RunName ?? string.Empty, DateTime.UtcNow, done, live.Count) : null);
            AddLog(live.Count > 0
                ? $"Clips 1–{done} of {live.Count} are saved on the server. Press Resume to continue from clip {done + 1} " +
                  "(if ComfyUI is down, start it first)."
                : "Finished clips are saved on the server. Press Resume to continue from the last one (if ComfyUI is down, start it first).");
        }

        /// <summary>
        /// Runs a chain and keeps it going through ComfyUI restarts. A restart loses the queued prompt, but
        /// every finished clip is saved with a checkpoint, so once the server is back the same prompt is
        /// queued again starting at the first clip that was not saved (not at clip 1, which is what queuing
        /// it unchanged would do). Also warns when nothing has happened for a long time.
        /// </summary>
        private async Task<string> RenderChainAsync(JsonObject graph, string label, LiveRun live,
            IProgress<FlipPix.ComfyUI.Models.ProgressMessage> progress, string videoPath, CancellationToken ct)
        {
            using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watch = WatchForStallAsync(live, label, watchCts.Token);
            try
            {
                for (var resumes = 0; ; resumes++)
                {
                    try
                    {
                        _comfyUIService.NodeExecuted += live.Handler;
                        live.LastActivity = DateTime.Now;
                        return await _comfyUIService.ExecuteWorkflowOnceAsync(graph, progress, ct, TimeSpan.FromHours(12));
                    }
                    catch (FlipPix.ComfyUI.Exceptions.ComfyUIPromptLostException) when (resumes < MaxAutoResumes)
                    {
                        var during = live.Index > 0 ? $" during clip {live.Index} of {live.Count}" : string.Empty;
                        AddLog($"{label}: ComfyUI went away{during}. Waiting up to {ServerReturnWait.TotalMinutes:0} min for it to come back, " +
                               "then continuing from the last saved clip.");
                        ProcessingStatus = $"ComfyUI went away{during} — waiting for it to come back to continue…";
                        if (!await _comfyUIService.WaitForServerReadyAsync(ServerReturnWait, ct))
                            throw new FlipPix.ComfyUI.Exceptions.ComfyUIPromptLostException(
                                $"ComfyUI went away{during} and did not come back within {ServerReturnWait.TotalMinutes:0} minutes.", string.Empty);

                        var runName = live.RunName;
                        if (runName == null && await FindUnfinishedAsync(videoPath, ct) is { } found) runName = found.Run.RunName;
                        var next = 1;
                        if (runName != null)
                        {
                            List<CharacterSwapGraph.SavedClip> clips;
                            (next, clips) = await SavedClipsAsync(runName, ct);
                            live.RunName = runName;
                            live.Done = clips.Count;
                        }
                        if (next > 1)
                        {
                            CharacterSwapGraph.PrepareResume(graph, runName!, next);
                            AddLog($"{label}: ComfyUI is back. Clips 1–{next - 1} are saved; continuing at clip {next}.");
                        }
                        else
                        {
                            AddLog($"{label}: ComfyUI is back. No clip had been saved yet, so the swap starts again from clip 1.");
                        }
                    }
                    finally
                    {
                        _comfyUIService.NodeExecuted -= live.Handler;
                    }
                }
            }
            finally
            {
                watchCts.Cancel();
                try { await watch; } catch (OperationCanceledException) { }
            }
        }

        /// <summary>Says so in the log when ComfyUI has reported nothing for a long time, once per quiet spell.</summary>
        private async Task WatchForStallAsync(LiveRun live, string label, CancellationToken ct)
        {
            var warnedAt = DateTime.MinValue;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                var quiet = DateTime.Now - live.LastActivity;
                if (quiet < StallWarning || warnedAt >= live.LastActivity) continue;
                warnedAt = DateTime.Now;
                var clip = live.Index > 0 ? $"clip {live.Index} of {live.Count}" : "the run";
                AddLog($"{label}: no progress from ComfyUI for {(int)quiet.TotalMinutes} min on {clip}. It may be stuck " +
                       "(out of VRAM and swapping, or a hung node) — check the ComfyUI console. If you restart ComfyUI, Resume continues " +
                       "from the last saved clip.");
                ProcessingStatus = $"No progress for {(int)quiet.TotalMinutes} min on {clip} — ComfyUI may be stuck.";
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
