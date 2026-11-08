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
    /// "🎞 Remake" tab — Viggle Remake (huggingface.co/Viggle/Viggle-Remake) on the MiniMax H3 install.
    /// A clip is re-rendered with its motion, camera, timing and soundtrack kept, while 1–4 painted frames decide
    /// who is in it (swap) or what the place looks like (edit).
    ///
    /// <para><b>The flow.</b> Pick a clip and the part to remake. Each painted frame is a moment of the clip: the
    /// server grabs the source frame there and Qwen Image 2.1 Edit paints it (the character photos into it, or the
    /// described change), or the user brings their own. Qwen3-VL then looks at the clip and the pictures, and the
    /// six-section prompt is assembled from what it said (<see cref="RemakeGraph.ComposePrompt"/>). Generate does
    /// every step that is still missing, so a clip, one photo and "the pizza maker" are enough to press it.</para>
    ///
    /// <para>Every finished take stays in the strip under the player with its seed, so takes can be compared.</para>
    /// </summary>
    public partial class RemakeViewModel : VideoProcessingBaseViewModel
    {
        private readonly IFileDialogService _fileDialogService;
        private CancellationTokenSource? _runCts;
        private CancellationTokenSource? _grabCts;
        private bool _loading = true;
        private string? _uploadedVideo, _uploadedVideoFor;
        private string _promptKey = string.Empty;
        private bool _settingPrompt;

        private static readonly string WorkRoot = Path.Combine(Path.GetTempPath(), "FlipPix", "remake");

        public RemakeViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _fileDialogService = fileDialogService;

            BrowseVideoCommand = new RelayCommand(() => _ = BrowseVideoAsync(), () => !IsProcessing);
            GenerateCommand = new RelayCommand(() => _ = RunAsync(RunKind.Generate), CanGenerate);
            PaintAllCommand = new RelayCommand(() => _ = RunAsync(RunKind.PaintAll), () => !IsProcessing && HasVideo && Keyframes.Count > 0);
            WritePromptCommand = new RelayCommand(() => _ = RunAsync(RunKind.WritePrompt), () => !IsProcessing && HasVideo && Keyframes.Count > 0);
            StopCommand = new RelayCommand(Stop, () => IsProcessing);
            AddCastCommand = new RelayCommand(AddCast, () => Cast.Count < RemakeGraph.MaxCast);
            AddKeyframeCommand = new RelayCommand(AddKeyframe, () => HasVideo && Keyframes.Count < RemakeGraph.MaxPictures);
            NewSeedCommand = new RelayCommand(() => Seed = NewSeed());
            RefreshModelsCommand = new RelayCommand(() => _ = LoadModelListsAsync());
            PlaySourceCommand = new RelayCommand(PlaySource, () => HasVideo);
            PlayResultCommand = new RelayCommand(() => Show(ResultVideoPath), () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            ResetPromptCommand = new RelayCommand(() => { PromptText = string.Empty; IsPromptAuto = true; _promptKey = string.Empty; });
            ClearLogCommand = new RelayCommand(() => LogOutput = string.Empty);
            SetModeCommand = new RelayCommand<string>(m => IsEdit = m == "Edit");

            var saved = settingsService.Settings?.Remake ?? new RemakeSettings();
            _isEdit = saved.Mode == nameof(RemakeMode.Edit);
            _shortEdge = RemakeGraph.ShortEdges.Contains(saved.ShortEdge) ? saved.ShortEdge : 480;
            _latentUpscale = saved.LatentUpscale && _shortEdge == RemakeGraph.UpscaleDraftEdge;
            _wantedFrames = RemakeGraph.IsValidFrames(saved.Frames) ? saved.Frames : RemakeGraph.DefaultFrames;
            _keyframeUntil = saved.KeyframeUntil is > 0 and <= 1 ? saved.KeyframeUntil : RemakeGraph.DefaultKeyframeUntil;
            _selectedModel = saved.Model;
            _seed = NewSeed();
            AddCast();
            _loading = false;

            Keyframes.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasKeyframes)); OnPropertyChanged(nameof(TimelineMarks)); RefreshCommands(); };
            Cast.CollectionChanged += (_, _) => RefreshCommands();
            Takes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTakes));
            RebuildLengths();

            AddLog("Remake ready — pick a clip, then add who it becomes (swap) or what changes (edit), and Generate. " +
                   "Viggle Remake keeps the clip's motion, camera, timing and sound.");
            _ = LoadModelListsAsync();
        }

        // ── Mode ─────────────────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSwap), nameof(Mode), nameof(ModeSummary), nameof(ChangeLabel), nameof(ChangeHint))]
        private bool _isEdit;

        public bool IsSwap
        {
            get => !IsEdit;
            set => IsEdit = !value;
        }

        public RemakeMode Mode => IsEdit ? RemakeMode.Edit : RemakeMode.Swap;

        public string ModeSummary => IsEdit
            ? "Change the world, keep the shot: swap, remove or add objects, or change the setting, weather or light."
            : "Recast the clip: each character takes the place of someone in it, with the same moves, timing and sound.";

        public string ChangeLabel => IsEdit ? "HOW THE RESULT LOOKS" : "ANYTHING ELSE? (optional)";

        public string ChangeHint => IsEdit
            ? "Describe the result, not the edit — e.g. \"just after a summer storm: dark clouds, a vivid rainbow over the hill, the grass glistening wet\""
            : "One sentence about the result, e.g. \"the dough is a glowing purple slime\". Leave it blank for a pure character swap.";

        partial void OnIsEditChanged(bool value)
        {
            Save();
            InvalidatePaint();
            RefreshCommands();
            _ = LoadModelListsAsync();
        }

        public RelayCommand<string> SetModeCommand { get; }

        // ── Source clip ──────────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasVideo), nameof(VideoFileName))]
        private string _videoPath = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VideoInfo))]
        private double _videoDuration;

        [ObservableProperty] private bool _videoHasAudio = true;

        public bool HasVideo => !string.IsNullOrEmpty(VideoPath) && File.Exists(VideoPath);
        public string VideoFileName => HasVideo ? Path.GetFileName(VideoPath) : "Drop in a clip to remake";
        public string VideoInfo => VideoDuration > 0 ? $"{VideoDuration:0.0} s{(VideoHasAudio ? "" : " · no sound")}" : string.Empty;

        /// <summary>Seconds into the source where the remade part starts.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(WindowLabel), nameof(TimelineMarks), nameof(WindowStartFraction), nameof(WindowWidthFraction),
                                  nameof(WindowBeforeStar), nameof(WindowStar), nameof(WindowAfterStar), nameof(WindowAfterFraction))]
        private double _startSeconds;

        public double MaxStartSeconds => Math.Max(0, VideoDuration - RemakeGraph.MinFrames / (double)RemakeGraph.Fps - 0.05);

        /// <summary>One length the model takes, for the picker.</summary>
        public sealed record LengthOption(int Frames, string Label)
        {
            public override string ToString() => Label;
        }

        public ObservableCollection<LengthOption> LengthOptions { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Frames), nameof(ClipSeconds), nameof(WindowLabel), nameof(TimelineMarks), nameof(WindowWidthFraction),
                                  nameof(WindowStar), nameof(WindowAfterStar), nameof(WindowAfterFraction))]
        private LengthOption? _selectedLength;

        private int _wantedFrames;

        public int Frames => SelectedLength?.Frames ?? 0;
        public double ClipSeconds => Frames / (double)RemakeGraph.Fps;

        public string WindowLabel => Frames <= 0
            ? (HasVideo ? "This clip is too short: Remake needs at least 3.75 s." : string.Empty)
            : $"{StartSeconds:0.00} s → {StartSeconds + ClipSeconds:0.00} s · {Frames} frames at 24 fps";

        /// <summary>Where the window sits on the source's bar (0–1).</summary>
        public double WindowStartFraction => VideoDuration > 0 ? Math.Clamp(StartSeconds / VideoDuration, 0, 1) : 0;
        public double WindowWidthFraction => VideoDuration > 0 ? Math.Clamp(ClipSeconds / VideoDuration, 0, 1) : 0;

        /// <summary>Each painted frame's position across the clip (0–1), for the dots on the timeline.</summary>
        public IReadOnlyList<TimelineMark> TimelineMarks =>
            ClipSeconds <= 0 ? Array.Empty<TimelineMark>()
            : Keyframes.Select(k => new TimelineMark(k.Number, Math.Clamp(k.Seconds / ClipSeconds, 0, 1))).ToList();

        public sealed record TimelineMark(int Number, double Fraction)
        {
            public string Label => Number.ToString();
            /// <summary>Star widths either side of the dot, so the bar lays out without a converter.</summary>
            public string BeforeStar => Star(Fraction);
            public string AfterStar => Star(1 - Fraction);
            public double AfterFraction => 1 - Fraction;
        }

        private static string Star(double weight) =>
            Math.Max(0.0001, weight).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "*";

        /// <summary>The remade part on the source's bar, as star widths: before, the part, after.</summary>
        public string WindowBeforeStar => Star(WindowStartFraction);
        public string WindowStar => Star(WindowWidthFraction);
        public string WindowAfterStar => Star(WindowAfterFraction);
        public double WindowAfterFraction => Math.Max(0, 1 - WindowStartFraction - WindowWidthFraction);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(QualityNote))]
        private int _shortEdge = 480;

        /// <summary>Render at 480, then lift the clip ×2 in latent space and refine it there — the I2V tabs'
        /// draft → ×2 → finish scheme. Only off a 480 draft (see <see cref="RemakeGraph.UpscaleDraftEdge"/>).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(QualityNote))]
        private bool _latentUpscale = true;

        /// <summary>The three quality pills: Fast (480), HD ×2 (480 → 960 in latent space), Sharp (736).</summary>
        public bool IsSharp
        {
            get => ShortEdge == 736;
            set { if (value) SetQuality(736, false); }
        }

        public bool IsFast
        {
            get => ShortEdge == 480 && !LatentUpscale;
            set { if (value) SetQuality(480, false); }
        }

        public bool IsHd
        {
            get => ShortEdge == 480 && LatentUpscale;
            set { if (value) SetQuality(480, true); }
        }

        private void SetQuality(int shortEdge, bool upscale)
        {
            LatentUpscale = upscale;
            ShortEdge = shortEdge;
            RaiseQuality();
        }

        private void RaiseQuality()
        {
            OnPropertyChanged(nameof(IsSharp));
            OnPropertyChanged(nameof(IsFast));
            OnPropertyChanged(nameof(IsHd));
            OnPropertyChanged(nameof(QualityNote));
        }

        public string QualityNote => ShortEdge == 736
            ? $"1280×736 sampled directly. On a 24 GB card the int8 model fits up to {RemakeGraph.MaxFramesAt736} frames ({RemakeGraph.MaxFramesAt736 / 24.0:0.0} s), and a render takes several times longer."
            : LatentUpscale
                ? "1664×960. Rendered at 480, then lifted ×2 in latent space and refined in 3 steps, like the I2V tabs — about 2½× the time of Fast, any length."
                : "832×480 — the size Remake was measured at, and the quickest to iterate with.";

        partial void OnShortEdgeChanged(int value)
        {
            RaiseQuality();
            Save();
            RebuildLengths();
        }

        partial void OnLatentUpscaleChanged(bool value)
        {
            RaiseQuality();
            Save();
        }

        partial void OnStartSecondsChanged(double value)
        {
            var clamped = Math.Clamp(value, 0, MaxStartSeconds);
            if (Math.Abs(clamped - value) > 1e-9) { StartSeconds = clamped; return; }
            RebuildLengths();
            foreach (var k in Keyframes) MarkMoved(k);
            ScheduleGrab();
        }

        partial void OnSelectedLengthChanged(LengthOption? value)
        {
            if (value != null && !_rebuildingLengths)
            {
                _wantedFrames = value.Frames;
                Save();
            }
            foreach (var k in Keyframes)
            {
                k.MaxSeconds = Math.Max(0.1, ClipSeconds - 1.0 / RemakeGraph.Fps);
                if (k.Seconds > k.MaxSeconds) k.Seconds = k.MaxSeconds;
            }
            OnPropertyChanged(nameof(TimelineMarks));
            RefreshCommands();
        }

        private bool _rebuildingLengths;

        /// <summary>The lengths that fit after the start (and at 736, on a 24 GB card), keeping the user's pick when it still fits.</summary>
        private void RebuildLengths()
        {
            _rebuildingLengths = true;
            try
            {
                var available = HasVideo ? VideoDuration - StartSeconds : RemakeGraph.MaxFrames / (double)RemakeGraph.Fps + 1;
                var fit = RemakeGraph.LongestFit(available);
                var cap = ShortEdge == 736 ? Math.Min(fit, RemakeGraph.MaxFramesAt736) : fit;
                var options = RemakeGraph.FrameOptions.Where(f => f <= cap).ToList();
                LengthOptions.Clear();
                foreach (var f in options) LengthOptions.Add(new LengthOption(f, $"{f / 24.0:0.0} s  ·  {f} frames"));
                SelectedLength = options.Count == 0 ? null
                    : LengthOptions.First(o => o.Frames == RemakeGraph.Snap(_wantedFrames, cap));
            }
            finally { _rebuildingLengths = false; }
            OnPropertyChanged(nameof(MaxStartSeconds));
            OnPropertyChanged(nameof(WindowLabel));
            OnPropertyChanged(nameof(Frames));
            OnPropertyChanged(nameof(ClipSeconds));
            OnPropertyChanged(nameof(WindowStartFraction));
            OnPropertyChanged(nameof(WindowWidthFraction));
            OnPropertyChanged(nameof(WindowBeforeStar));
            OnPropertyChanged(nameof(WindowStar));
            OnPropertyChanged(nameof(WindowAfterStar));
            OnPropertyChanged(nameof(WindowAfterFraction));
            OnPropertyChanged(nameof(TimelineMarks));
            RefreshCommands();
        }

        // ── Cast (swap) and change ───────────────────────────────────────────────────────────────

        public ObservableCollection<RemakeCastSlot> Cast { get; } = new();

        private void AddCast()
        {
            if (Cast.Count >= RemakeGraph.MaxCast) return;
            var slot = new RemakeCastSlot(Cast.Count + 1, s => _ = BrowsePhotoAsync(s), RemoveCast);
            slot.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(RemakeCastSlot.PhotoPath) or nameof(RemakeCastSlot.Replaces) or nameof(RemakeCastSlot.Look))
                    InvalidatePaint();
                RefreshCommands();
            };
            Cast.Add(slot);
        }

        private void RemoveCast(RemakeCastSlot slot)
        {
            if (Cast.Count <= 1) { slot.PhotoPath = string.Empty; slot.Replaces = slot.Name = slot.Look = string.Empty; return; }
            Cast.Remove(slot);
            for (var i = 0; i < Cast.Count; i++) Cast[i].Number = i + 1;
            InvalidatePaint();
        }

        /// <summary>Edit: what the result shows. Swap: an optional sentence about the result.</summary>
        [ObservableProperty] private string _change = string.Empty;

        partial void OnChangeChanged(string value) => InvalidatePaint();

        /// <summary>A painted frame that no longer matches the cast or the change is repainted by Generate.</summary>
        private void InvalidatePaint()
        {
            if (_loading) return;
            foreach (var k in Keyframes.Where(k => k.HasPainted && !k.IsOwnImage)) k.IsStale = true;
        }

        // ── Painted frames ───────────────────────────────────────────────────────────────────────

        public ObservableCollection<RemakeKeyframeSlot> Keyframes { get; } = new();
        public bool HasKeyframes => Keyframes.Count > 0;

        /// <summary>Pinned frames are keyframes the render passes through; off, they only set the look.</summary>
        [ObservableProperty] private bool _pinFrames = true;

        private void AddKeyframe()
        {
            if (Keyframes.Count >= RemakeGraph.MaxPictures) return;
            // First frame half a second in (upstream's examples pin at frame 12); later ones spread over the rest.
            var at = Keyframes.Count == 0 ? Math.Min(0.5, ClipSeconds / 2)
                : Math.Round(ClipSeconds * (Keyframes.Count + 0.5) / (Keyframes.Count + 1) * 4) / 4;
            AddKeyframeAt(at);
        }

        private RemakeKeyframeSlot AddKeyframeAt(double seconds)
        {
            var slot = new RemakeKeyframeSlot(Keyframes.Count + 1, seconds,
                s => _ = RunAsync(RunKind.PaintOne, s), s => _ = BrowsePaintedAsync(s), RemoveKeyframe,
                (_, path) => ShowImage(path))
            {
                MaxSeconds = Math.Max(0.1, ClipSeconds - 1.0 / RemakeGraph.Fps),
            };
            slot.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RemakeKeyframeSlot.Seconds))
                {
                    MarkMoved(slot);
                    OnPropertyChanged(nameof(TimelineMarks));
                    ScheduleGrab();
                }
            };
            Keyframes.Add(slot);
            ScheduleGrab();
            return slot;
        }

        private void RemoveKeyframe(RemakeKeyframeSlot slot)
        {
            Keyframes.Remove(slot);
            for (var i = 0; i < Keyframes.Count; i++) Keyframes[i].Number = i + 1;
            OnPropertyChanged(nameof(TimelineMarks));
        }

        private double SourceSecondsOf(RemakeKeyframeSlot k) => StartSeconds + k.Seconds;

        /// <summary>A frame whose moment moved needs a new source grab; its painting is stale unless the user brought it.</summary>
        private void MarkMoved(RemakeKeyframeSlot k)
        {
            if (!double.IsNaN(k.FrameAtSourceSeconds) && Math.Abs(k.FrameAtSourceSeconds - SourceSecondsOf(k)) > 0.5 / RemakeGraph.Fps)
            {
                if (k.HasPainted && !k.IsOwnImage) k.IsStale = true;
            }
        }

        private bool NeedsGrab(RemakeKeyframeSlot k) =>
            !k.HasFrame || double.IsNaN(k.FrameAtSourceSeconds) || Math.Abs(k.FrameAtSourceSeconds - SourceSecondsOf(k)) > 0.5 / RemakeGraph.Fps;

        private bool NeedsPaint(RemakeKeyframeSlot k) => !k.HasPainted || (k.IsStale && !k.IsOwnImage);

        /// <summary>Grabs the source frames shortly after the sliders stop moving, when the queue is free.</summary>
        private void ScheduleGrab()
        {
            if (_loading || !HasVideo) return;
            _grabCts?.Cancel();
            _grabCts = new CancellationTokenSource();
            _ = GrabSoonAsync(_grabCts.Token);
        }

        /// <summary>Runs on the UI thread's context (started from a property setter), so the slots' commands are
        /// refreshed where WPF and Avalonia allow it; only the waits leave the thread.</summary>
        private async Task GrabSoonAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(700, ct);
                foreach (var k in Keyframes.ToList().Where(NeedsGrab))
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsProcessing) return;
                    await GrabAsync(k, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AddLog($"Could not grab a frame: {ex.Message}"); }
        }

        // ── Prompt ───────────────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PromptProblems), nameof(HasPrompt))]
        private string _promptText = string.Empty;

        /// <summary>The prompt is the one this page wrote (and may rewrite); false once the user edits it.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PromptState))]
        private bool _isPromptAuto = true;

        public bool HasPrompt => !string.IsNullOrWhiteSpace(PromptText);

        public string PromptState => !HasPrompt ? "Written for you when you Generate (or press ✨ Write now)."
            : IsPromptAuto ? "Written from the clip and your frames — rewritten when they change. Edit it to take over."
            : "Your edits are kept. ↺ hands it back to auto.";

        public string PromptProblems
        {
            get
            {
                if (!HasPrompt) return string.Empty;
                var problems = RemakeGraph.CheckPrompt(PromptText, Keyframes.Count);
                return problems.Count == 0 ? string.Empty : "⚠ " + string.Join("; ", problems);
            }
        }

        partial void OnPromptTextChanged(string value)
        {
            if (!_settingPrompt && !_loading) IsPromptAuto = string.IsNullOrWhiteSpace(value);
            OnPropertyChanged(nameof(PromptState));
        }

        private void SetPrompt(string text, string key)
        {
            _settingPrompt = true;
            try
            {
                PromptText = text;
                IsPromptAuto = true;
                _promptKey = key;
            }
            finally { _settingPrompt = false; }
        }

        /// <summary>Everything the written prompt depends on; a change means an auto prompt is written again.</summary>
        private string PromptKey() => string.Join("|", new[]
        {
            Mode.ToString(), VideoPath, StartSeconds.ToString("0.000"), Frames.ToString(), Change,
        }.Concat(Cast.Select(c => $"{c.Replaces}/{c.Name}/{c.Look}/{c.PhotoPath}"))
         .Concat(Keyframes.Select(k => $"{k.Seconds:0.000}/{k.PaintedPath}")));

        // ── Settings ─────────────────────────────────────────────────────────────────────────────

        [ObservableProperty] private long _seed;
        [ObservableProperty] private double _keyframeUntil = RemakeGraph.DefaultKeyframeUntil;
        [ObservableProperty] private string _selectedModel = string.Empty;
        public ObservableCollection<string> ModelOptions { get; } = new();

        [ObservableProperty] private string _modelStatus = "Looking for the Viggle Remake model on the server…";

        partial void OnKeyframeUntilChanged(double value) => Save();
        partial void OnSelectedModelChanged(string value) => Save();

        private void Save()
        {
            if (_loading) return;
            var settings = _settingsService.Settings;
            if (settings == null) return;
            var s = settings.Remake ??= new RemakeSettings();
            s.Mode = Mode.ToString();
            s.ShortEdge = ShortEdge;
            s.LatentUpscale = LatentUpscale;
            s.Frames = _wantedFrames;
            s.KeyframeUntil = KeyframeUntil;
            s.Model = SelectedModel;
            try { _settingsService.SaveSettings(settings); }
            catch (Exception ex) { _logger.LogWarning($"Remake: could not save settings: {ex.Message}"); }
        }

        // ── Preview and takes ────────────────────────────────────────────────────────────────────

        /// <summary>The video the player shows.</summary>
        [ObservableProperty] private string _activePreview = string.Empty;

        /// <summary>A still shown over the player (a source or painted frame); empty to show the video.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPreviewImage), nameof(PreviewImage))]
        private string _previewImagePath = string.Empty;

        public bool HasPreviewImage => !string.IsNullOrEmpty(PreviewImagePath) && File.Exists(PreviewImagePath);
        public System.Windows.Media.Imaging.BitmapImage? PreviewImage => HasPreviewImage ? AsyncThumbnail.Decode(PreviewImagePath, 1280) : null;

        private void Show(string video)
        {
            PreviewImagePath = string.Empty;
            ActivePreview = video;
        }

        private void PlaySource() => Show(VideoPath);

        private void ShowImage(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) PreviewImagePath = path;
        }

        /// <summary>One finished render.</summary>
        public sealed record Take(int Number, string Path, long Seed, string Info)
        {
            public string Label => $"Take {Number}";
        }

        public ObservableCollection<Take> Takes { get; } = new();
        public bool HasTakes => Takes.Count > 0;

        public RelayCommand<Take> PlayTakeCommand => _playTakeCommand ??= new RelayCommand<Take>(t =>
        {
            if (t == null || !File.Exists(t.Path)) return;
            ResultVideoPath = t.Path;
            ResultVideoInfo = t.Info;
            Show(t.Path);
        });
        private RelayCommand<Take>? _playTakeCommand;

        // ── Commands ─────────────────────────────────────────────────────────────────────────────

        public RelayCommand BrowseVideoCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand PaintAllCommand { get; }
        public RelayCommand WritePromptCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand AddCastCommand { get; }
        public RelayCommand AddKeyframeCommand { get; }
        public RelayCommand NewSeedCommand { get; }
        public RelayCommand RefreshModelsCommand { get; }
        public RelayCommand PlaySourceCommand { get; }
        public RelayCommand PlayResultCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand ResetPromptCommand { get; }
        public RelayCommand ClearLogCommand { get; }
        /// <summary>Closes the still over the player and goes back to the video.</summary>
        public RelayCommand ClosePreviewImageCommand => _closePreviewImage ??= new RelayCommand(() => PreviewImagePath = string.Empty);
        private RelayCommand? _closePreviewImage;

        /// <summary>Why Generate is not ready yet, shown under the button; empty when it is.</summary>
        public string ReadyHint
        {
            get
            {
                if (!HasVideo) return "Pick a clip to start.";
                if (Frames <= 0) return "This clip is too short: Remake needs at least 3.75 s after the start.";
                if (Keyframes.Count == 0) return "Add at least one painted frame.";
                if (IsEdit && string.IsNullOrWhiteSpace(Change) && Keyframes.Any(k => !k.HasPainted))
                    return "Describe how the result looks (or bring your own painted frames).";
                if (IsSwap && Keyframes.Any(k => !k.HasPainted) && !Cast.Any(c => c.HasPhoto || !string.IsNullOrWhiteSpace(c.Look)))
                    return "Give the character a photo or a look in words (or bring your own painted frames).";
                var todo = new List<string>();
                var paint = Keyframes.Count(NeedsPaint);
                if (paint > 0) todo.Add($"paint {paint} frame{(paint == 1 ? "" : "s")}");
                if (!HasPrompt || (IsPromptAuto && _promptKey != PromptKey())) todo.Add("write the prompt");
                todo.Add("render");
                return "Generate will " + string.Join(", ", todo) + ".";
            }
        }

        private bool CanGenerate() =>
            !IsProcessing && HasVideo && Frames > 0 && Keyframes.Count > 0
            && (Keyframes.All(k => k.HasPainted)
                || (IsEdit ? !string.IsNullOrWhiteSpace(Change)
                           : Cast.Any(c => c.HasPhoto || !string.IsNullOrWhiteSpace(c.Look))));

        private void RefreshCommands()
        {
            void Notify()
            {
                BrowseVideoCommand.NotifyCanExecuteChanged();
                GenerateCommand.NotifyCanExecuteChanged();
                PaintAllCommand.NotifyCanExecuteChanged();
                WritePromptCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
                AddCastCommand.NotifyCanExecuteChanged();
                AddKeyframeCommand.NotifyCanExecuteChanged();
                PlaySourceCommand.NotifyCanExecuteChanged();
                PlayResultCommand.NotifyCanExecuteChanged();
                OpenResultFolderCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ReadyHint));
                OnPropertyChanged(nameof(PromptProblems));
            }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) Notify();
            else dispatcher.BeginInvoke(Notify);
        }

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            if (GenerateCommand != null) RefreshCommands();
        }

        private static long NewSeed() => Random.Shared.NextInt64(0, 0xffffffffL);

        private void Stop()
        {
            _runCts?.Cancel();
            AddLog("Stopping… (ComfyUI is asked to interrupt)");
            _ = _comfyUIService.HttpClient.InterruptAsync(CancellationToken.None);
        }

        // ── Pickers ──────────────────────────────────────────────────────────────────────────────

        private async Task BrowseVideoAsync()
        {
            var start = _settingsService.Settings?.Remake?.LastVideoFolder;
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
                start = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select the clip to remake",
                "Video Files|*.mp4;*.mov;*.mkv;*.webm;*.avi|All Files|*.*",
                start, persistKey: "remake.video");
            if (path != null) await SetVideoAsync(path);
        }

        /// <summary>Takes a clip dropped on the page or picked in the dialog.</summary>
        public async Task SetVideoAsync(string path)
        {
            if (IsProcessing || !File.Exists(path)) return;
            _grabCts?.Cancel();
            VideoPath = path;
            _uploadedVideo = null;
            VideoDuration = await Task.Run(() => GetVideoDuration(path));
            VideoHasAudio = await Task.Run(() => HasAudioStream(path));
            OnPropertyChanged(nameof(VideoInfo));
            Keyframes.Clear();
            StartSeconds = 0;
            RebuildLengths();
            if (Frames > 0) AddKeyframe();
            SetPrompt(string.Empty, string.Empty);
            Show(path);
            AddLog($"Clip: {Path.GetFileName(path)} ({VideoDuration:0.0} s{(VideoHasAudio ? "" : ", no sound")})");
            if (Frames <= 0) AddLog("This clip is shorter than 3.75 s, the shortest Remake renders.");

            var settings = _settingsService.Settings;
            if (settings != null)
            {
                (settings.Remake ??= new RemakeSettings()).LastVideoFolder = Path.GetDirectoryName(path) ?? string.Empty;
                Save();
            }
            RefreshCommands();
        }

        private async Task<string?> PickImageAsync(string title, string key)
        {
            var start = _settingsService.Settings?.Remake?.LastPhotoFolder;
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
                start = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var path = await _fileDialogService.OpenFileDialogAsync(title,
                "Image Files|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All Files|*.*", start, persistKey: key);
            if (path != null && _settingsService.Settings is { } settings)
            {
                (settings.Remake ??= new RemakeSettings()).LastPhotoFolder = Path.GetDirectoryName(path) ?? string.Empty;
                Save();
            }
            return path;
        }

        private async Task BrowsePhotoAsync(RemakeCastSlot slot)
        {
            var path = await PickImageAsync($"Photo of {slot.Label}", "remake.photo");
            if (path == null) return;
            slot.PhotoPath = path;
            AddLog($"{slot.Label}: {Path.GetFileName(path)}");
        }

        /// <summary>A painted frame made elsewhere (any image editor): used as is, never repainted.</summary>
        private async Task BrowsePaintedAsync(RemakeKeyframeSlot slot)
        {
            var path = await PickImageAsync($"Your painted version of {slot.Label}", "remake.painted");
            if (path == null) return;
            slot.IsOwnImage = true;
            slot.PaintedPath = path;
            slot.IsStale = false;
            slot.Status = string.Empty;
            ShowImage(path);
            AddLog($"{slot.Label}: using your painted frame {Path.GetFileName(path)}");
            RefreshCommands();
        }

        // ── Model lists ──────────────────────────────────────────────────────────────────────────

        private async Task LoadModelListsAsync()
        {
            try
            {
                var unets = await _comfyUIService.HttpClient.GetUnetFilenamesAsync();
                var mode = IsEdit ? "edit" : "swap";
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    var keep = SelectedModel;
                    ModelOptions.Clear();
                    foreach (var u in unets.Where(u => u.Contains("viggle_remake", StringComparison.OrdinalIgnoreCase)
                                                       || u.Contains("remake", StringComparison.OrdinalIgnoreCase)))
                        ModelOptions.Add(u);
                    var forMode = ModelOptions.Where(u => u.Contains($"remake_{mode}", StringComparison.OrdinalIgnoreCase)).ToList();
                    SelectedModel = !string.IsNullOrEmpty(keep) && forMode.Contains(keep) ? keep
                        : forMode.OrderBy(u => u.Contains("int8", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault() ?? string.Empty;
                    ModelStatus = SelectedModel.Length > 0
                        ? $"Viggle Remake {mode}: {Path.GetFileName(SelectedModel)}"
                        : $"The Viggle Remake {mode} model is not on the server yet. Put {(IsEdit ? RemakeGraph.MergedEditModel : RemakeGraph.MergedSwapModel)} in models/diffusion_models.";
                });
            }
            catch (Exception ex)
            {
                ModelStatus = $"Could not read the server's models: {ex.Message}";
            }
        }

        private sealed record Components(string Model, string? Lora, string TextEncoder, string VideoVae, string AudioVae);

        /// <summary>
        /// The Remake transformer for this mode (the merged int8/GGUF file, else the base fl2va model plus the
        /// mode's LoRA), and the H3 text encoder and VAEs this server has — the same weights in another quantisation
        /// stand in for upstream's bf16 files, and the log says which ran.
        /// </summary>
        private async Task<Components> ResolveComponentsAsync(CancellationToken ct)
        {
            var http = _comfyUIService.HttpClient;
            var unets = await http.GetUnetFilenamesAsync(ct);
            var clips = await http.GetModelFilenamesAsync("CLIPLoader", "clip_name", ct);
            var vaes = await http.GetModelFilenamesAsync("VAELoader", "vae_name", ct);
            var mode = IsEdit ? "edit" : "swap";

            string model;
            string? lora = null;
            var merged = !string.IsNullOrEmpty(SelectedModel) && unets.Contains(SelectedModel)
                         && SelectedModel.Contains($"remake_{mode}", StringComparison.OrdinalIgnoreCase)
                ? SelectedModel
                : unets.Where(u => u.Contains($"viggle_remake_{mode}", StringComparison.OrdinalIgnoreCase))
                       .OrderBy(u => u.Contains("int8", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
            if (merged != null) model = merged;
            else
            {
                var loras = await http.GetLoraFilenamesAsync(ct);
                lora = loras.FirstOrDefault(l => l.Contains($"viggle_remake_{mode}", StringComparison.OrdinalIgnoreCase));
                var fl2va = unets.FirstOrDefault(u => u.Contains("minimax_h3_fl2va", StringComparison.OrdinalIgnoreCase));
                if (lora == null || fl2va == null)
                    throw new InvalidOperationException(
                        $"The Viggle Remake {mode} model is not on the ComfyUI server. Download " +
                        $"int8/{(IsEdit ? RemakeGraph.MergedEditModel : RemakeGraph.MergedSwapModel)} from huggingface.co/Viggle/Viggle-Remake " +
                        "into models/diffusion_models (21 GB) and press Refresh.");
                model = fl2va;
            }

            var te = CharacterSwapGraph.ResolveFile(RemakeGraph.DefaultTextEncoder, clips,
                @"qwen3vl_32b_minimax_h3_bf16", @"qwen3vl_32b_minimax_h3.*int8", @"qwen3vl_32b_minimax_h3.*(int4|w4a8)", @"qwen3vl_32b_minimax_h3");
            var vv = CharacterSwapGraph.ResolveFile(RemakeGraph.DefaultVideoVae, vaes, @"minimax_h3_video_vae_fp16", @"minimax_h3_video_vae");
            var av = CharacterSwapGraph.ResolveFile(RemakeGraph.DefaultAudioVae, vaes, @"minimax_h3_audio_vae");
            AddLog($"Model: {model}{(lora != null ? $" + LoRA {lora} (bypass)" : "")} | text encoder: {te} | VAEs: {vv}, {av}");
            return new Components(model, lora, te, vv, av);
        }

        // ── Runs ─────────────────────────────────────────────────────────────────────────────────

        private enum RunKind { Generate, PaintAll, PaintOne, WritePrompt }

        private async Task RunAsync(RunKind kind, RemakeKeyframeSlot? one = null)
        {
            if (IsProcessing || !HasVideo) return;
            if (kind == RunKind.Generate && !CanGenerate()) return;
            _grabCts?.Cancel();

            IsProcessing = true;
            ProcessingProgress = 0;
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;
            var started = DateTime.Now;
            try
            {
                ProcessingStatus = "Waiting for the ComfyUI queue…";
                using var lease = await _workflowCoordinator.AcquireAsync("remake", ct);

                switch (kind)
                {
                    case RunKind.PaintOne:
                        await PaintAsync(one!, ct);
                        ProcessingStatus = $"{one!.Label} painted.";
                        break;
                    case RunKind.PaintAll:
                        await PaintMissingAsync(ct, all: true);
                        ProcessingStatus = "Frames painted.";
                        break;
                    case RunKind.WritePrompt:
                        await PaintMissingAsync(ct, all: false);
                        await WritePromptAsync(ct, 0, 100);
                        ProcessingStatus = "Prompt written — read it over, then Generate.";
                        break;
                    case RunKind.Generate:
                        await GenerateAsync(ct);
                        break;
                }
                ProcessingProgress = 100;
            }
            catch (OperationCanceledException)
            {
                ProcessingStatus = "Stopped.";
                AddLog("Stopped.");
            }
            catch (Exception ex)
            {
                ProcessingStatus = $"Failed: {ex.Message}";
                AddLog($"Failed: {ex.Message}");
                _logger.LogError(ex, "Remake run failed");
            }
            finally
            {
                foreach (var k in Keyframes) { k.IsBusy = false; if (k.Status.EndsWith("…")) k.Status = string.Empty; }
                IsProcessing = false;
                _runCts?.Dispose();
                _runCts = null;
                RefreshCommands();
                if (kind == RunKind.Generate) AddLog($"Took {(DateTime.Now - started):h\\:mm\\:ss}.");
            }
        }

        private async Task GenerateAsync(CancellationToken ct)
        {
            var paint = Keyframes.Count(NeedsPaint);
            await PaintMissingAsync(ct, all: false, from: 0, to: paint > 0 ? 15 : 0);

            var key = PromptKey();
            if (!HasPrompt || (IsPromptAuto && key != _promptKey))
                await WritePromptAsync(ct, 15, 25);
            else
                AddLog(IsPromptAuto ? "Prompt: up to date." : "Prompt: using your edited prompt.");

            var problems = RemakeGraph.CheckPrompt(PromptText, Keyframes.Count);
            if (problems.Count > 0) AddLog($"⚠ Prompt: {string.Join("; ", problems)}. Rendering anyway.");

            var parts = await ResolveComponentsAsync(ct);
            var video = await UploadVideoAsync(ct);
            var ordered = Keyframes.OrderBy(k => k.Seconds).ToList();
            var pictures = new List<string>();
            for (var i = 0; i < ordered.Count; i++)
            {
                ProcessingStatus = $"Uploading painted frame {i + 1}…";
                pictures.Add(await _comfyUIService.UploadImageAsync(ordered[i].PaintedPath, ct));
            }

            var template = LoadTemplate(IsEdit ? RemakeGraph.EditFile : RemakeGraph.SwapFile);
            var request = new RemakeRender
            {
                Mode = Mode,
                Video = video,
                HasAudio = VideoHasAudio,
                StartSeconds = StartSeconds,
                Frames = Frames,
                ShortEdge = ShortEdge,
                LatentUpscale = IsHd,
                Pictures = pictures,
                Pins = PinFrames ? ordered.Select(k => RemakeGraph.PinFor(k.Seconds, Frames)).ToList() : Array.Empty<int>(),
                Prompt = PromptText,
                Seed = Seed,
                KeyframeUntil = KeyframeUntil,
                Model = parts.Model,
                Lora = parts.Lora,
                TextEncoder = parts.TextEncoder,
                VideoVae = parts.VideoVae,
                AudioVae = parts.AudioVae,
                OutputName = $"{Mode.ToString().ToLowerInvariant()}_{Path.GetFileNameWithoutExtension(VideoPath)}",
            };
            var graph = RemakeGraph.BuildRender(template, request);
            AddLog($"Render: {Mode}, {Frames} frames ({ClipSeconds:0.0} s) from {StartSeconds:0.00} s at short edge {ShortEdge}{(IsHd ? " → latent ×2 (960) with a 3-step finish" : "")}, " +
                   $"{pictures.Count} painted frame{(pictures.Count == 1 ? "" : "s")}" +
                   $"{(PinFrames ? $" pinned at {string.Join(", ", request.Pins)}" : " (references only)")}, seed {Seed}.");

            ProcessingStatus = "Rendering — the first take also loads the models (a few minutes)…";
            var renderStart = DateTime.Now;
            var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
            {
                if (msg.Data == null || msg.Data.Max <= 0) return;
                // The sampler reports 6 steps (the ×2 finish another 3); anything else (encoders) moves the bar only a little.
                if (msg.Data.Max <= 12)
                {
                    var elapsed = DateTime.Now - renderStart;
                    var finishing = request.LatentUpscale && msg.Data.Max == 3;
                    ProcessingProgress = !request.LatentUpscale
                        ? 25 + 70.0 * msg.Data.Value / msg.Data.Max
                        : finishing ? 60 + 35.0 * msg.Data.Value / msg.Data.Max : 25 + 30.0 * msg.Data.Value / msg.Data.Max;
                    ProcessingStatus = finishing
                        ? $"Finishing at ×2 — step {msg.Data.Value} of {msg.Data.Max} · {elapsed:m\\:ss} so far"
                        : $"Rendering — step {msg.Data.Value} of {msg.Data.Max} · {elapsed:m\\:ss} so far";
                }
            });
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(graph, progress, ct, TimeSpan.FromHours(2));

            ProcessingStatus = "Fetching the result…";
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, ct);
            var file = byNode.TryGetValue(RemakeGraph.Nodes.Save, out var files) ? files.FirstOrDefault() : null;
            file ??= byNode.Values.SelectMany(f => f).LastOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            var output = file == null ? null : await FetchOutputFileAsync(file, ct);
            if (output == null) throw new InvalidOperationException("ComfyUI reported no video for this render. Check the ComfyUI console.");

            var info = $"{Path.GetFileName(output)} · seed {Seed} · {(DateTime.Now - renderStart):m\\:ss}";
            ResultVideoPath = output;
            ResultVideoInfo = info;
            HasResult = true;
            Application.Current?.Dispatcher.Invoke(() => Takes.Add(new Take(Takes.Count + 1, output, Seed, info)));
            Show(output);
            ProcessingStatus = $"Take {Takes.Count} done. 🎲 a new seed for another take, or tweak a frame and Generate again.";
            AddLog($"Output: {output}");
        }

        private static JsonObject LoadTemplate(string file)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "video", "viggle-remake", file);
            if (!File.Exists(path)) throw new FileNotFoundException($"Remake workflow not found: {path}");
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException($"{file} is not an API workflow.");
        }

        private static JsonObject LoadPaintTemplate()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "qwen-edit", RemakeGraph.PaintWorkflow);
            if (!File.Exists(path)) throw new FileNotFoundException($"Qwen Image 2.1 Edit workflow not found: {path}");
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("The paint workflow is not an API workflow.");
        }

        private async Task<string> UploadVideoAsync(CancellationToken ct)
        {
            if (_uploadedVideo != null && _uploadedVideoFor == VideoPath) return _uploadedVideo;
            ProcessingStatus = "Sending the clip to ComfyUI…";
            var path = VideoPath;
            var name = await _comfyUIService.UploadVideoAsync(path, ct);
            _uploadedVideo = name;
            _uploadedVideoFor = path;
            return name;
        }

        /// <summary>The first media file a finished prompt saved, as a local path.</summary>
        private async Task<string?> FirstOutputAsync(string promptId, string node, CancellationToken ct)
        {
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, ct);
            var file = byNode.TryGetValue(node, out var files) ? files.FirstOrDefault() : byNode.Values.SelectMany(f => f).FirstOrDefault();
            return file == null ? null : await FetchOutputFileAsync(file, ct);
        }

        /// <summary>Copies a server output into this page's work folder, so it outlives the download cache.</summary>
        private static string Keep(string path, string name)
        {
            Directory.CreateDirectory(WorkRoot);
            var target = Path.Combine(WorkRoot, $"{name}_{DateTime.Now:HHmmss_fff}{Path.GetExtension(path)}");
            File.Copy(path, target, overwrite: true);
            return target;
        }

        private async Task GrabAsync(RemakeKeyframeSlot k, CancellationToken ct)
        {
            var at = SourceSecondsOf(k);
            k.IsBusy = true;
            k.Status = "Grabbing the frame…";
            try
            {
                // Local ffmpeg when there is one: instant, and never queued behind a long render on the server.
                if (FindFFmpeg() is { } ffmpeg)
                {
                    Directory.CreateDirectory(WorkRoot);
                    var local = Path.Combine(WorkRoot, $"frame{k.Number}_{DateTime.Now:HHmmss_fff}.png");
                    var args = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "-hide_banner -loglevel error -ss {0:0.000} -i \"{1}\" -frames:v 1 -y \"{2}\"", at, VideoPath, local);
                    try { await Task.Run(() => RunFFmpeg(ffmpeg, args, 60000), ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
                    if (File.Exists(local))
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            k.FramePath = local;
                            k.FrameAtSourceSeconds = at;
                            if (!k.HasPainted) ShowImage(local);
                        });
                        return;
                    }
                }

                var video = await UploadVideoAsync(ct);
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(RemakeGraph.BuildGrabFrame(video, at), null, ct, TimeSpan.FromMinutes(5));
                var file = await FirstOutputAsync(promptId, RemakeGraph.GrabSave, ct)
                           ?? throw new InvalidOperationException("the server returned no frame");
                var kept = Keep(file, $"frame{k.Number}");
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    k.FramePath = kept;
                    k.FrameAtSourceSeconds = at;
                    if (!k.HasPainted) ShowImage(kept);
                });
            }
            finally
            {
                k.IsBusy = false;
                k.Status = string.Empty;
            }
        }

        private async Task PaintMissingAsync(CancellationToken ct, bool all, double from = 0, double to = 100)
        {
            var todo = Keyframes.Where(k => all ? !k.IsOwnImage : NeedsPaint(k)).ToList();
            for (var i = 0; i < todo.Count; i++)
            {
                ProcessingProgress = from + (to - from) * i / Math.Max(1, todo.Count);
                await PaintAsync(todo[i], ct);
            }
        }

        private async Task PaintAsync(RemakeKeyframeSlot k, CancellationToken ct)
        {
            if (IsEdit && string.IsNullOrWhiteSpace(Change))
                throw new InvalidOperationException("Describe how the result looks first, then paint.");
            var cast = Cast.Where(c => c.HasPhoto || !string.IsNullOrWhiteSpace(c.Look) || !string.IsNullOrWhiteSpace(c.Replaces)).ToList();
            if (IsSwap && !cast.Any(c => c.HasPhoto || !string.IsNullOrWhiteSpace(c.Look)))
                throw new InvalidOperationException("Give the character a photo or a look in words, then paint.");

            if (NeedsGrab(k))
            {
                ProcessingStatus = $"Grabbing {k.Label} from the clip…";
                await GrabAsync(k, ct);
            }
            k.IsBusy = true;
            k.Status = "Painting…";
            try
            {
                ProcessingStatus = $"Painting {k.Label} with Qwen Image 2.1 Edit…";
                var frame = await _comfyUIService.UploadImageAsync(k.FramePath, ct);
                var photos = new List<string>();
                var castInfo = new List<(RemakeCast, bool)>();
                if (IsSwap)
                    foreach (var c in cast)
                    {
                        if (c.HasPhoto) photos.Add(await _comfyUIService.UploadImageAsync(c.PhotoPath, ct));
                        castInfo.Add((ToCast(c), c.HasPhoto));
                    }
                var instruction = RemakeGraph.PaintInstruction(Mode, castInfo, Change);
                AddLog($"{k.Label} at {k.Seconds:0.00} s: painting — {instruction}");
                // About 1 MP: more than the 736 canvas needs, and far quicker than painting a 4K frame at its own size.
                var graph = RemakeGraph.BuildPaint(LoadPaintTemplate(), frame, photos, instruction,
                    Random.Shared.NextInt64(0, 999_999_999_999L), resolution: 1024);
                var started = DateTime.Now;
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(graph, null, ct, TimeSpan.FromMinutes(20));
                var file = await FirstOutputAsync(promptId, RemakeGraph.PaintNodes.Save, ct)
                           ?? throw new InvalidOperationException("Qwen Image Edit returned no image.");
                var kept = Keep(file, $"painted{k.Number}");
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    k.IsOwnImage = false;
                    k.PaintedPath = kept;
                    k.IsStale = false;
                    ShowImage(kept);
                });
                AddLog($"{k.Label}: painted in {(DateTime.Now - started).TotalSeconds:0} s.");
            }
            finally
            {
                k.IsBusy = false;
                k.Status = string.Empty;
                RefreshCommands();
            }
        }

        private static RemakeCast ToCast(RemakeCastSlot c) => new(c.Replaces.Trim(), c.Name.Trim(), c.Look.Trim());

        /// <summary>
        /// Qwen3-VL looks at the clip (and each character photo, or the first painted frame), then the prompt is
        /// assembled from its notes. Swap: a character with no photo is described from the first painted frame.
        /// </summary>
        private async Task WritePromptAsync(CancellationToken ct, double from, double to)
        {
            if (Keyframes.Any(k => !k.HasPainted))
                throw new InvalidOperationException("Paint every frame before writing the prompt.");
            ProcessingStatus = "Qwen3-VL is watching the clip to write the prompt…";
            ProcessingProgress = from;
            var video = await UploadVideoAsync(ct);
            var clips = await _comfyUIService.HttpClient.GetModelFilenamesAsync("CLIPLoader", "clip_name", ct);
            var describer = CharacterSwapGraph.ResolveFile(RemakeGraph.DefaultDescriber, clips, @"qwen3[-_]?vl[-_]?8b", @"qwen3[-_]?vl[-_]?4b");

            var ordered = Keyframes.OrderBy(k => k.Seconds).ToList();
            var cast = IsSwap ? Cast.Select(ToCast).ToList() : new List<RemakeCast>();
            var looks = new List<string?>();
            var firstPainted = await _comfyUIService.UploadImageAsync(ordered[0].PaintedPath, ct);
            if (IsSwap)
                foreach (var c in Cast)
                    looks.Add(!string.IsNullOrWhiteSpace(c.Look) ? null
                        : c.HasPhoto ? await _comfyUIService.UploadImageAsync(c.PhotoPath, ct) : firstPainted);
            else looks.Add(firstPainted);

            var graph = RemakeGraph.BuildNotes(video, StartSeconds, Frames, describer, Mode, cast, looks, IsEdit ? Change : string.Empty);
            AddLog($"Prompt: Qwen3-VL ({describer}) is describing the clip and {looks.Count(l => l != null)} picture(s)…");
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(graph, null, ct, TimeSpan.FromMinutes(15));
            var texts = await _comfyUIService.HttpClient.GetTextOutputsByNodeAsync(promptId, ct);
            string Text(string node) => texts.TryGetValue(node, out var lines) ? string.Join("\n", lines) : string.Empty;

            var (scene, style) = RemakeGraph.ParseScene(Text(RemakeGraph.SceneOutput));
            var timeline = Text(RemakeGraph.TimelineOutput);
            var lookTexts = looks.Select((_, i) => RemakeGraph.CleanSentence(Text(RemakeGraph.LookOutput(i)))).ToList();
            AddLog($"Scene: {scene}");
            AddLog($"Style: {style}");
            foreach (var line in timeline.Split('\n').Where(l => l.Trim().Length > 0)) AddLog($"  {line.Trim()}");
            for (var i = 0; i < lookTexts.Count; i++)
                if (lookTexts[i].Length > 0) AddLog(IsSwap ? $"Character {i + 1} looks like: {lookTexts[i]}" : $"Painted frame: {lookTexts[i]}");

            var notes = new RemakeNotes(scene, style, timeline, lookTexts);
            var frames = ordered.Select(k => new RemakeFrame(k.Seconds, PinFrames)).ToList();
            var prompt = RemakeGraph.ComposePrompt(Mode, Frames, frames, cast, Change, notes);
            Application.Current?.Dispatcher.Invoke(() =>
            {
                // Pictures go to the model in time order; number the cards the same way.
                var sorted = Keyframes.OrderBy(k => k.Seconds).ToList();
                for (var i = 0; i < sorted.Count; i++)
                {
                    sorted[i].Number = i + 1;
                    var at = Keyframes.IndexOf(sorted[i]);
                    if (at != i) Keyframes.Move(at, i);
                }
                SetPrompt(prompt, PromptKey());
            });
            ProcessingProgress = to;
            AddLog($"Prompt written ({prompt.Length} characters). It is in the Prompt box — edit it to take over.");
        }
    }
}
