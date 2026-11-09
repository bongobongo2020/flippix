using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
    /// "⛓ FFLF" tab — a story told beat by beat, first frame to last frame. Paste the story beats and pick the
    /// image the story opens on; each beat is rendered through the ErosMax 2-Pass graph (h3-erosmax-2pass.json),
    /// and the last frame of every take becomes the first frame of the next beat, so the beats play on as one shot.
    ///
    /// <para><b>Per beat.</b> <c>&lt;Picture 1&gt;</c> is the frame the beat starts on — anchored at frame 0 of
    /// both passes (MiniMaxH3AddGuide) as well as sent as a reference — and, from beat 2 on, the initial image is
    /// <c>&lt;Picture 2&gt;</c> so the characters keep the look they started with. The model and LoRA default to
    /// 10Eros Max Singularity V2 PRISM2 PikaFlex with the DMAD 4-step LoRA at 0.25.</para>
    ///
    /// <para><b>Stop and carry on.</b> A finished beat stays finished: Generate continues from the first beat
    /// without a take. ↻ on a beat renders it again with a new seed, and every beat after it again from there,
    /// because they started on its last frame. Editing a beat's text does the same to it and the beats after it.
    /// When the last beat lands the takes are joined, the repeated join frame dropped.</para>
    /// </summary>
    public partial class H3FflfViewModel : VideoProcessingBaseViewModel
    {
        private readonly IFileDialogService _fileDialogService;
        private readonly LivePreviewFeed _livePreview = new("live_preview_fflf");
        private CancellationTokenSource? _runCts;
        private bool _loading = true;
        private string _runFolder = string.Empty;
        private string? _uploadedInitial, _uploadedInitialFor;
        private string _liveBeatLabel = string.Empty;
        private volatile bool _holdingLease;

        /// <summary>Where each take's last frame is kept — beside the story library, so a story reopened after a
        /// restart (or a temp clean-up) can still carry on from it.</summary>
        private static readonly string WorkRoot = Path.Combine(ScenePromptLibrary.FolderFor("fflf-stories"), "frames");

        internal const string WorkflowFileName = MiniMaxI2VViewModel.TwoPassWorkflowFileName;

        public H3FflfViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _fileDialogService = fileDialogService;
            _storyLibrary = new ScenePromptLibrary(AddLog, ScenePromptLibrary.FolderFor("fflf-stories"));
            _storiesLoad = _storyLibrary.LoadAsync();

            BrowseImageCommand = new RelayCommand(() => _ = BrowseImageAsync(), () => !IsProcessing);
            ClearImageCommand = new RelayCommand(() => InitialImagePath = string.Empty, () => !IsProcessing && HasInitialImage);
            GenerateCommand = new RelayCommand(() => _ = RunAsync(FirstUnfinished()), CanGenerate);
            StopCommand = new RelayCommand(Stop, () => IsProcessing);
            RedoBeatCommand = new RelayCommand<FflfBeat>(b => { if (b != null) _ = RedoAsync(b); }, b => b != null && !IsProcessing && CanStartAt(Beats.IndexOf(b)));
            PlayBeatCommand = new RelayCommand<FflfBeat>(b => { if (b?.HasVideo == true) Show(b.VideoPath); });
            ShowFrameCommand = new RelayCommand<FflfBeat>(b => { if (b?.HasLastFrame == true) PreviewImagePath = b.LastFramePath; });
            ClosePreviewImageCommand = new RelayCommand(() => PreviewImagePath = string.Empty);
            PlayJoinedCommand = new RelayCommand(() => Show(JoinedVideoPath), () => HasJoined);
            JoinCommand = new RelayCommand(() => _ = JoinNowAsync(), () => !IsProcessing && Beats.Count(b => b.HasVideo) >= 2);
            StartOverCommand = new RelayCommand(StartOver, () => !IsProcessing && Beats.Any(b => b.State != FflfBeatState.Pending));
            OpenFolderCommand = new RelayCommand(OpenFolder, () => Beats.Any(b => b.HasVideo) || HasJoined);
            RefreshModelsCommand = new RelayCommand(() => _ = LoadModelListsAsync());
            NewSeedCommand = new RelayCommand(() => BaseSeed = NewSeed());
            ClearLogCommand = new RelayCommand(() => LogOutput = string.Empty);
            ResetModelCommand = new RelayCommand(() =>
            {
                DiffusionModel = H3FflfSettings.DefaultModel;
                Lora = H3FflfSettings.DefaultLora;
                LoraStrength = 0.25;
            });

            var s = settingsService.Settings?.H3Fflf ?? new H3FflfSettings();
            _initialImagePath = File.Exists(s.InitialImagePath) ? s.InitialImagePath : string.Empty;
            _beatsText = s.BeatsText ?? string.Empty;
            _styleText = s.StyleText ?? string.Empty;
            _beatSeconds = Math.Clamp(s.BeatSeconds, 1, 20);
            _selectedAspectRatio = AspectRatioOptions.Contains(s.AspectRatio) ? s.AspectRatio : H3Canvas.AutoAspect;
            _firstPassMegapixels = FirstPassSizes.Any(o => o.Value == s.FirstPassMegapixels) ? s.FirstPassMegapixels : 0.3;
            _finalMegapixels = FinalSizes.Any(o => o.Value == s.FinalMegapixels) ? s.FinalMegapixels : 1.0;
            _previewOnly = s.PreviewOnly;
            _diffusionModel = string.IsNullOrWhiteSpace(s.DiffusionModel) ? H3FflfSettings.DefaultModel : s.DiffusionModel;
            _lora = s.Lora ?? string.Empty;
            _loraStrength = Math.Clamp(s.LoraStrength, 0, 2);
            _firstPassSteps = Math.Clamp(s.FirstPassSteps, 1, 60);
            _lockFirstFrame = s.LockFirstFrame;
            _keepInitialReference = s.KeepInitialReference;
            _joinWhenDone = s.JoinWhenDone;
            _baseSeed = NewSeed();
            OfferModel(_diffusionModel);
            OfferLora(_lora);
            OnInitialImagePathChanged(_initialImagePath);
            _loading = false;

            Beats.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasBeats)); OnPropertyChanged(nameof(BeatSummary)); RefreshCommands(); };
            ReparseBeats();

            _comfyUIService.LivePreviewReceived += OnLivePreviewReceived;
            InitStories();

            AddLog("FFLF ready — pick the opening image, paste the story beats (one per paragraph or numbered line), and Generate. " +
                   "Each beat starts on the last frame of the one before.");
            _ = LoadModelListsAsync();
        }

        // ── Commands ─────────────────────────────────────────────────────────────────────────────

        public RelayCommand BrowseImageCommand { get; }
        public RelayCommand ClearImageCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand<FflfBeat> RedoBeatCommand { get; }
        public RelayCommand<FflfBeat> PlayBeatCommand { get; }
        public RelayCommand<FflfBeat> ShowFrameCommand { get; }
        public RelayCommand ClosePreviewImageCommand { get; }
        public RelayCommand PlayJoinedCommand { get; }
        public RelayCommand JoinCommand { get; }
        public RelayCommand StartOverCommand { get; }
        public RelayCommand OpenFolderCommand { get; }
        public RelayCommand RefreshModelsCommand { get; }
        public RelayCommand NewSeedCommand { get; }
        public RelayCommand ClearLogCommand { get; }
        public RelayCommand ResetModelCommand { get; }

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            RefreshCommands();
        }

        private void RefreshCommands() => OnUi(() =>
        {
            BrowseImageCommand?.NotifyCanExecuteChanged();
            ClearImageCommand?.NotifyCanExecuteChanged();
            GenerateCommand?.NotifyCanExecuteChanged();
            StopCommand?.NotifyCanExecuteChanged();
            RedoBeatCommand?.NotifyCanExecuteChanged();
            PlayJoinedCommand?.NotifyCanExecuteChanged();
            JoinCommand?.NotifyCanExecuteChanged();
            StartOverCommand?.NotifyCanExecuteChanged();
            OpenFolderCommand?.NotifyCanExecuteChanged();
            OpenStoriesCommand?.NotifyCanExecuteChanged();
            NewStoryCommand?.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ReadyHint));
            OnPropertyChanged(nameof(GenerateLabel));
        });

        // ── Opening image ────────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasInitialImage), nameof(InitialImageName), nameof(CanvasSummary))]
        private string _initialImagePath = string.Empty;

        public bool HasInitialImage => !string.IsNullOrEmpty(InitialImagePath) && File.Exists(InitialImagePath);
        public string InitialImageName => HasInitialImage ? Path.GetFileName(InitialImagePath) : "Click to pick the image the story opens on";

        private System.Windows.Media.Imaging.BitmapImage? _initialThumbnail;
        public System.Windows.Media.Imaging.BitmapImage? InitialThumbnail => _initialThumbnail;

        partial void OnInitialImagePathChanged(string value)
        {
            _initialThumbnail = null;
            OnPropertyChanged(nameof(InitialThumbnail));
            AsyncThumbnail.Load(value, 480, 0, bitmap =>
            {
                if (InitialImagePath != value) return;
                _initialThumbnail = bitmap;
                OnPropertyChanged(nameof(InitialThumbnail));
            });
            if (_loading) return;
            // Every beat descends from the opening frame.
            if (Beats.Any(b => b.State != FflfBeatState.Pending))
            {
                ResetFrom(0);
                AddLog("New opening image — every beat will render again from it.");
            }
            RefreshCommands();
        }

        private async Task BrowseImageAsync()
        {
            var start = _settingsService.Settings?.H3Fflf?.LastPhotoFolder;
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
                start = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var path = await _fileDialogService.OpenFileDialogAsync("The image the story opens on",
                "Image Files|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All Files|*.*", start, persistKey: "fflf.initial");
            if (path == null) return;
            if (_settingsService.Settings is { } settings)
                (settings.H3Fflf ??= new H3FflfSettings()).LastPhotoFolder = Path.GetDirectoryName(path) ?? string.Empty;
            InitialImagePath = path;
            AddLog($"Opening image: {Path.GetFileName(path)}");
        }

        /// <summary>Lets a dropped file be the opening image.</summary>
        public void SetInitialImage(string path)
        {
            if (IsProcessing || !File.Exists(path)) return;
            InitialImagePath = path;
            AddLog($"Opening image: {Path.GetFileName(path)}");
        }

        // ── Story beats ──────────────────────────────────────────────────────────────────────────

        [ObservableProperty] private string _beatsText = string.Empty;

        /// <summary>Written in front of every beat: style, place, who is who. Optional.</summary>
        [ObservableProperty] private string _styleText = string.Empty;

        public ObservableCollection<FflfBeat> Beats { get; } = new();
        public bool HasBeats => Beats.Count > 0;

        public string BeatSummary
        {
            get
            {
                if (Beats.Count == 0) return "No beats yet — paste them above.";
                var done = Beats.Count(b => b.IsDone);
                var total = Beats.Count * BeatSeconds;
                return $"{Beats.Count} beat{(Beats.Count == 1 ? "" : "s")} · ≈{total:0.#} s in all · {done} rendered";
            }
        }

        partial void OnBeatsTextChanged(string value)
        {
            if (_loading) return;
            // The running story keeps the beats it started with; the edit is read when it stops.
            if (IsProcessing) { _reparsePending = true; return; }
            ReparseBeats();
        }

        private bool _reparsePending;

        /// <summary>Reads the beats from the text box. Beats whose text is unchanged keep their takes — up to the
        /// first beat that changed: everything from there on started on a frame that is no longer right.</summary>
        private void ReparseBeats()
        {
            var parsed = FflfGraph.ParseBeats(BeatsText);
            var keep = 0;
            while (keep < parsed.Count && keep < Beats.Count && Beats[keep].Text == parsed[keep].Text) keep++;

            if (keep < Beats.Count && Beats.Skip(keep).Any(b => b.State == FflfBeatState.Done) && !IsProcessing)
                AddLog($"Beat {keep + 1} changed — it and every beat after it will render again.");

            OnUi(() =>
            {
                for (var i = 0; i < keep; i++) Beats[i].Title = parsed[i].Title;
                while (Beats.Count > keep) Beats.RemoveAt(Beats.Count - 1);
                for (var i = keep; i < parsed.Count; i++)
                    Beats.Add(new FflfBeat(i + 1, parsed[i].Title, parsed[i].Text));
            });
            if (keep < parsed.Count || keep < Beats.Count) JoinedVideoPath = string.Empty;
            OnPropertyChanged(nameof(BeatSummary));
        }

        private void ResetFrom(int index)
        {
            for (var i = Math.Max(0, index); i < Beats.Count; i++)
            {
                Beats[i].Reset();
                Beats[i].Seed = 0;
            }
            JoinedVideoPath = string.Empty;
            OnPropertyChanged(nameof(BeatSummary));
            RefreshCommands();
            ScheduleStorySave();
        }

        private void StartOver()
        {
            ResetFrom(0);
            _runFolder = string.Empty;
            BaseSeed = NewSeed();
            AddLog("Start over — every beat will render again.");
        }

        // ── Render settings ──────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BeatSummary))]
        private double _beatSeconds = 5;

        public IReadOnlyList<string> AspectRatioOptions { get; } =
            new[] { H3Canvas.AutoAspect }.Concat(H3Canvas.AspectRatios.Select(a => a.Option)).ToList();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanvasSummary))]
        private string _selectedAspectRatio = H3Canvas.AutoAspect;

        public IReadOnlyList<MegapixelOption> FirstPassSizes => MiniMaxI2VViewModel.TwoPassFirstPassSizes;
        public IReadOnlyList<MegapixelOption> FinalSizes => MiniMaxI2VViewModel.TwoPassFinalSizes;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanvasSummary))]
        private double _firstPassMegapixels = 0.3;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanvasSummary))]
        private double _finalMegapixels = 1.0;

        /// <summary>Stop after the first pass: a quick draft of the whole story.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanvasSummary))]
        private bool _previewOnly;

        [ObservableProperty] private int _firstPassSteps = 8;

        [ObservableProperty] private bool _lockFirstFrame = true;
        [ObservableProperty] private bool _keepInitialReference = true;
        [ObservableProperty] private bool _joinWhenDone = true;

        [ObservableProperty] private long _baseSeed;

        /// <summary>The aspect sent to ComfyUI: the picked one, or the opening image's closest match.</summary>
        public string ResolvedAspectRatio
        {
            get
            {
                if (SelectedAspectRatio != H3Canvas.AutoAspect) return SelectedAspectRatio;
                if (!HasInitialImage) return "16:9 (Widescreen)";
                var (w, h) = BitmapLoader.TryReadSize(InitialImagePath);
                return H3Canvas.ClosestAspectRatio(w, h);
            }
        }

        public string CanvasSummary
        {
            get
            {
                var aspect = ResolvedAspectRatio;
                var (w1, h1) = H3Canvas.Resolve(aspect, FirstPassMegapixels, MiniMaxI2VViewModel.TwoPassResolutionMultiple);
                if (PreviewOnly) return $"{aspect} · draft at {w1}×{h1} (first pass only, no RTX)";
                var (w2, h2) = H3Canvas.Resolve(aspect, FinalMegapixels, MiniMaxI2VViewModel.TwoPassResolutionMultiple);
                return $"{aspect} · {w1}×{h1} first pass → refined at ≈{w2}×{h2} → RTX ×1.2";
            }
        }

        // ── Model and LoRA ───────────────────────────────────────────────────────────────────────

        public ObservableCollection<string> ModelOptions { get; } = new();
        public ObservableCollection<string> LoraOptions { get; } = new() { string.Empty };

        [ObservableProperty] private string _diffusionModel = H3FflfSettings.DefaultModel;
        [ObservableProperty] private string _lora = H3FflfSettings.DefaultLora;
        [ObservableProperty] private double _loraStrength = 0.25;

        private void OfferModel(string name)
        {
            if (!string.IsNullOrWhiteSpace(name) && !ModelOptions.Contains(name)) ModelOptions.Add(name);
        }

        private void OfferLora(string name)
        {
            if (!string.IsNullOrWhiteSpace(name) && !LoraOptions.Contains(name)) LoraOptions.Add(name);
        }

        /// <summary>The server's h3-minimax checkpoints and loras/H3, for the two dropdowns.</summary>
        private async Task LoadModelListsAsync()
        {
            try
            {
                var http = _comfyUIService.HttpClient;
                var models = (await http.GetUnetFilenamesAsync())
                    .Select(n => n.Replace('\\', '/'))
                    .Where(n => n.StartsWith("h3-minimax/", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var loras = (await http.GetLoraFilenamesAsync())
                    .Select(n => n.Replace('\\', '/'))
                    .Where(n => n.StartsWith("H3/", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                OnUi(() =>
                {
                    var model = DiffusionModel;
                    var lora = Lora;
                    foreach (var n in models) OfferModel(n);
                    foreach (var n in loras) OfferLora(n);
                    DiffusionModel = model;
                    Lora = lora;
                });
                if (models.Count > 0 && !models.Contains(DiffusionModel.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase))
                    AddLog($"⚠ {Path.GetFileName(DiffusionModel)} is not in the server's diffusion_models/h3-minimax.");
                if (loras.Count > 0 && Lora.Length > 0 && !loras.Contains(Lora.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase))
                    AddLog($"⚠ {Path.GetFileName(Lora)} is not in the server's loras/H3.");
            }
            catch (Exception ex)
            {
                AddLog($"Model lists could not be read ({ex.Message}).");
            }
        }

        // ── Settings ─────────────────────────────────────────────────────────────────────────────

        private static readonly HashSet<string> Persisted = new()
        {
            nameof(InitialImagePath), nameof(BeatsText), nameof(StyleText), nameof(BeatSeconds), nameof(SelectedAspectRatio),
            nameof(FirstPassMegapixels), nameof(FinalMegapixels), nameof(PreviewOnly), nameof(DiffusionModel), nameof(Lora),
            nameof(LoraStrength), nameof(FirstPassSteps), nameof(LockFirstFrame), nameof(KeepInitialReference), nameof(JoinWhenDone),
        };

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (_loading || e.PropertyName == null) return;
            if (Persisted.Contains(e.PropertyName)) { Save(); ScheduleStorySave(); }
            else if (e.PropertyName == nameof(BaseSeed)) ScheduleStorySave();
        }

        private void Save()
        {
            var settings = _settingsService.Settings;
            if (settings == null) return;
            var s = settings.H3Fflf ??= new H3FflfSettings();
            s.InitialImagePath = InitialImagePath;
            s.BeatsText = BeatsText;
            s.StyleText = StyleText;
            s.BeatSeconds = BeatSeconds;
            s.AspectRatio = SelectedAspectRatio;
            s.FirstPassMegapixels = FirstPassMegapixels;
            s.FinalMegapixels = FinalMegapixels;
            s.PreviewOnly = PreviewOnly;
            s.DiffusionModel = DiffusionModel;
            s.Lora = Lora;
            s.LoraStrength = LoraStrength;
            s.FirstPassSteps = FirstPassSteps;
            s.LockFirstFrame = LockFirstFrame;
            s.KeepInitialReference = KeepInitialReference;
            s.JoinWhenDone = JoinWhenDone;
            try { _settingsService.SaveSettings(settings); }
            catch (Exception ex) { _logger.LogWarning($"FFLF: could not save settings: {ex.Message}"); }
        }

        // ── Player ───────────────────────────────────────────────────────────────────────────────

        /// <summary>The video the player shows.</summary>
        [ObservableProperty] private string _activePreview = string.Empty;

        /// <summary>A still shown over the player (a beat's last frame); empty to show the video.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPreviewImage), nameof(PreviewImage))]
        private string _previewImagePath = string.Empty;

        public bool HasPreviewImage => !string.IsNullOrEmpty(PreviewImagePath) && File.Exists(PreviewImagePath);
        public System.Windows.Media.Imaging.BitmapImage? PreviewImage => HasPreviewImage ? AsyncThumbnail.Decode(PreviewImagePath, 1280) : null;

        private void Show(string video)
        {
            if (string.IsNullOrEmpty(video)) return;
            PreviewImagePath = string.Empty;
            ActivePreview = video;
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasJoined))]
        private string _joinedVideoPath = string.Empty;

        public bool HasJoined => !string.IsNullOrEmpty(JoinedVideoPath) && File.Exists(JoinedVideoPath);

        partial void OnJoinedVideoPathChanged(string value) => RefreshCommands();

        private void OpenFolder()
        {
            var path = HasJoined ? JoinedVideoPath : Beats.LastOrDefault(b => b.HasVideo)?.VideoPath;
            var folder = path == null ? null : Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) FolderOpener.Open(folder);
        }

        // ── Live sampler preview (the graph's ModelPreviewOverrideKJ) ───────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLivePreview))]
        private string _livePreviewPath = string.Empty;

        public bool HasLivePreview => !string.IsNullOrEmpty(LivePreviewPath);

        [ObservableProperty] private string _livePreviewCaption = string.Empty;

        private void SetLiveBeat(string? label) => OnUi(() =>
        {
            _livePreview.Reset();
            _liveBeatLabel = label ?? string.Empty;
            LivePreviewPath = string.Empty;
            LivePreviewCaption = label == null ? string.Empty : $"{label} · encoding the prompt…";
        });

        private void OnLivePreviewReceived(object? sender, string raw)
        {
            if (!IsProcessing || _liveBeatLabel.Length == 0) return;
            if (_livePreview.Write(raw) is not { } frame) return;
            var pass = frame.Pass > 1 ? " · refining" : string.Empty;
            var caption = frame.Step == 0
                ? $"{_liveBeatLabel}{pass} · starting noise"
                : $"{_liveBeatLabel}{pass} · step {frame.Step}/{frame.Total}";
            OnUi(() =>
            {
                if (_liveBeatLabel.Length == 0) return;
                LivePreviewPath = frame.Path;
                LivePreviewCaption = caption;
            });
        }

        // ── Running ──────────────────────────────────────────────────────────────────────────────

        public string GenerateLabel
        {
            get
            {
                var next = FirstUnfinished();
                if (Beats.Count == 0 || next < 0) return "▶ Generate";
                return next == 0 ? $"▶ Generate {Beats.Count} beat{(Beats.Count == 1 ? "" : "s")}" : $"▶ Continue from beat {next + 1}";
            }
        }

        public string ReadyHint
        {
            get
            {
                if (IsProcessing) return ProcessingStatus;
                if (!HasInitialImage) return "Pick the image the story opens on.";
                if (Beats.Count == 0) return "Paste the story beats.";
                if (FirstUnfinished() < 0) return HasJoined ? "Every beat is rendered and joined." : "Every beat is rendered.";
                return PreviewOnly ? "Draft mode: first pass only — quick, to check the action." : "Each beat: first pass, refine, RTX, encode.";
            }
        }

        private int FirstUnfinished()
        {
            for (var i = 0; i < Beats.Count; i++)
                if (Beats[i].State != FflfBeatState.Done || !Beats[i].HasVideo) return i;
            return -1;
        }

        /// <summary>A beat can render once the one before it has a last frame (or it is the first).</summary>
        private bool CanStartAt(int index) =>
            index == 0 ? HasInitialImage : index > 0 && index < Beats.Count && Beats[index - 1].IsDone && Beats[index - 1].HasLastFrame;

        private bool CanGenerate() => !IsProcessing && HasInitialImage && Beats.Count > 0 && FirstUnfinished() is var i && i >= 0 && CanStartAt(i);

        private void Stop()
        {
            _runCts?.Cancel();
            // Only this tab's own render is interrupted — while it still waits for the queue, another tab's job runs.
            if (!_holdingLease) return;
            AddLog("Stopping — interrupting the current beat…");
            _ = _comfyUIService.HttpClient.InterruptAsync();
        }

        private async Task RedoAsync(FflfBeat beat)
        {
            var index = Beats.IndexOf(beat);
            if (index < 0 || !CanStartAt(index)) return;
            ResetFrom(index);
            beat.Seed = NewSeed();
            AddLog($"↻ {beat.Label}: a new take (seed {beat.Seed}), and every beat after it again.");
            await RunAsync(index);
        }

        private async Task RunAsync(int start)
        {
            if (IsProcessing || start < 0 || start >= Beats.Count || !CanStartAt(start)) return;

            IsProcessing = true;
            ProcessingProgress = 0;
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;
            var started = DateTime.Now;
            var beats = Beats.ToList();
            try
            {
                ProcessingStatus = "Waiting for the ComfyUI queue…";
                RefreshCommands();
                using var lease = await _workflowCoordinator.AcquireAsync("h3_fflf", ct);
                _holdingLease = true;

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFileName);
                if (!File.Exists(workflowPath)) throw new FileNotFoundException($"Workflow file not found: {WorkflowFileName}");
                var template = await File.ReadAllTextAsync(workflowPath, ct);

                if (start == 0 || _runFolder.Length == 0) _runFolder = $"fflf_{DateTime.Now:yyyyMMdd_HHmmss}";
                var initial = await UploadInitialAsync(ct);
                var aspect = ResolvedAspectRatio;
                AddLog($"Story: beats {start + 1}–{beats.Count} of {beats.Count}, {BeatSeconds:0.#} s each, {CanvasSummary}. " +
                       $"Model {Path.GetFileName(DiffusionModel)}" +
                       (Lora.Length > 0 && LoraStrength > 0 ? $" + {Path.GetFileName(Lora)} @ {LoraStrength:0.##}" : string.Empty) + ".");

                for (var i = start; i < beats.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var beat = beats[i];
                    var startImage = i == 0 ? initial : await LastFrameOfAsync(beats[i - 1], ct);
                    await RenderBeatAsync(template, beat, i, beats.Count, start, startImage, i > 0 && KeepInitialReference ? initial : null, aspect, ct);
                }

                ProcessingProgress = 100;
                if (JoinWhenDone && beats.Count >= 2 && beats.All(b => b.HasVideo))
                    await JoinAsync(ct);
                ProcessingStatus = HasJoined ? "Done — every beat rendered and joined." : "Done — every beat rendered.";
            }
            catch (OperationCanceledException)
            {
                ProcessingStatus = "Stopped.";
                AddLog("Stopped. Generate carries on from the first beat without a take.");
            }
            catch (Exception ex)
            {
                ProcessingStatus = $"Failed: {ex.Message}";
                AddLog($"Failed: {ex.Message}");
                _logger.LogError(ex, "FFLF run failed");
            }
            finally
            {
                foreach (var b in beats.Where(b => b.State == FflfBeatState.Rendering))
                {
                    b.State = FflfBeatState.Pending;
                    b.Status = string.Empty;
                }
                _holdingLease = false;
                SetLiveBeat(null);
                IsProcessing = false;
                _runCts?.Dispose();
                _runCts = null;
                if (_reparsePending) { _reparsePending = false; ReparseBeats(); }
                ScheduleStorySave();
                OnPropertyChanged(nameof(BeatSummary));
                RefreshCommands();
                AddLog($"Took {(DateTime.Now - started):h\\:mm\\:ss}.");
            }
        }

        private async Task<string> UploadInitialAsync(CancellationToken ct)
        {
            if (!HasInitialImage) throw new InvalidOperationException("Pick the image the story opens on.");
            if (_uploadedInitial != null && _uploadedInitialFor == InitialImagePath) return _uploadedInitial;
            ProcessingStatus = "Sending the opening image to ComfyUI…";
            var name = await _comfyUIService.UploadImageAsync(InitialImagePath, ct);
            if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("The opening image could not be uploaded.");
            _uploadedInitial = name;
            _uploadedInitialFor = InitialImagePath;
            return name;
        }

        /// <summary>The previous beat's last frame on the server — uploaded again if this session has not sent it.</summary>
        private async Task<string> LastFrameOfAsync(FflfBeat previous, CancellationToken ct)
        {
            if (previous.UploadedLastFrame.Length > 0) return previous.UploadedLastFrame;
            if (!previous.HasLastFrame) throw new InvalidOperationException($"{previous.Label} has no last frame to start the next beat on.");
            previous.UploadedLastFrame = await _comfyUIService.UploadImageAsync(previous.LastFramePath, ct);
            return previous.UploadedLastFrame;
        }

        private async Task RenderBeatAsync(string template, FflfBeat beat, int index, int total, int start,
                                           string startImage, string? identityImage, string aspect, CancellationToken ct)
        {
            if (beat.Seed <= 0) beat.Seed = index == 0 ? BaseSeed : NewSeed();
            var label = $"Beat {index + 1}/{total}";
            beat.State = FflfBeatState.Rendering;
            beat.Status = "rendering…";
            ProcessingStatus = $"{label}: {Shorten(beat.Text, 70)}";
            OnPropertyChanged(nameof(ReadyHint));

            var prompt = FflfGraph.ComposePrompt(beat.Text, StyleText, identityImage != null, index == 0);
            var request = new FflfBeatRender
            {
                StartImage = startImage,
                IdentityImage = identityImage,
                Prompt = prompt,
                Seconds = BeatSeconds,
                Seed = beat.Seed,
                AspectRatio = aspect,
                FirstPassMegapixels = FirstPassMegapixels,
                FinalMegapixels = FinalMegapixels,
                FirstPassSteps = FirstPassSteps,
                PreviewOnly = PreviewOnly,
                DiffusionModel = DiffusionModel,
                Lora = Lora,
                LoraStrength = LoraStrength,
                LockFirstFrame = LockFirstFrame,
                OutputPrefix = $"video/fflf/{_runFolder}/beat_{index + 1:D2}{(PreviewOnly ? "_draft" : string.Empty)}",
            };
            var graph = FflfGraph.Build(template, request, out var pruned);
            AddLog($"{label} — seed {beat.Seed}, starts on {(index == 0 ? "the opening image" : $"beat {index}'s last frame")}" +
                   $"{(LockFirstFrame ? " (anchored at frame 0)" : " (reference only)")}, {pruned} node(s) pruned.");

            var share = 1.0 / Math.Max(1, total - start);
            var done = index - start;
            var renderStart = DateTime.Now;
            var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
            {
                if (msg.Data == null || msg.Data.Max <= 0 || msg.Data.Max > 60) return;
                // Pass 1 runs the first-pass steps; the refine runs the second-pass schedule.
                var secondPass = !PreviewOnly && msg.Data.Max != FirstPassSteps;
                var frac = (double)msg.Data.Value / msg.Data.Max;
                var within = PreviewOnly ? frac : secondPass ? 0.6 + 0.35 * frac : 0.6 * frac;
                ProcessingProgress = (done + within) * share * 100;
                ProcessingStatus = $"{label}{(secondPass ? " · refining" : string.Empty)} — step {msg.Data.Value}/{msg.Data.Max} · {(DateTime.Now - renderStart):m\\:ss}";
                OnPropertyChanged(nameof(ReadyHint));
            });

            SetLiveBeat(label);
            string promptId;
            try
            {
                promptId = await _comfyUIService.ExecuteWorkflowAsync(graph, progress, ct, TimeSpan.FromHours(2));
            }
            catch
            {
                beat.State = FflfBeatState.Failed;
                beat.Status = "failed";
                throw;
            }
            finally { SetLiveBeat(null); }
            if (string.IsNullOrEmpty(promptId)) throw new InvalidOperationException("ComfyUI returned no prompt id — the graph may have errors.");

            ProcessingStatus = $"{label}: fetching the take…";
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, ct);
            var videoFile = byNode.TryGetValue(FflfGraph.VideoSaveNode, out var vids)
                ? vids.FirstOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? vids.FirstOrDefault()
                : byNode.Values.SelectMany(f => f).LastOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            var frameFile = byNode.TryGetValue(FflfGraph.LastFrameSaveNode, out var frames) ? frames.FirstOrDefault() : null;

            var video = videoFile == null ? null : await FetchOutputFileAsync(videoFile, ct);
            if (video == null) { beat.State = FflfBeatState.Failed; throw new InvalidOperationException($"{label}: ComfyUI reported no video. Check the ComfyUI console."); }
            var frame = frameFile == null ? null : await FetchOutputFileAsync(frameFile, ct);
            if (frame == null) { beat.State = FflfBeatState.Failed; throw new InvalidOperationException($"{label}: the last frame was not saved, so the next beat has nothing to start on."); }

            beat.VideoPath = video;
            beat.LastFramePath = Keep(frame, $"{_runFolder}_beat{index + 1:D2}_last");
            beat.UploadedLastFrame = string.Empty;   // uploaded when the next beat needs it
            beat.RenderedText = beat.Text;
            beat.State = FflfBeatState.Done;
            beat.Status = $"seed {beat.Seed} · {(DateTime.Now - renderStart):m\\:ss}";
            ResultVideoPath = video;
            ResultVideoInfo = $"{beat.Label} · {Path.GetFileName(video)}";
            HasResult = true;
            Show(video);
            OnPropertyChanged(nameof(BeatSummary));
            ScheduleStorySave();
            AddLog($"{label} done in {(DateTime.Now - renderStart):m\\:ss}: {video}");
        }

        // ── Join ─────────────────────────────────────────────────────────────────────────────────

        private async Task JoinNowAsync()
        {
            if (IsProcessing) return;
            IsProcessing = true;
            try { await JoinAsync(CancellationToken.None); }
            catch (Exception ex) { AddLog($"Join failed: {ex.Message}"); ProcessingStatus = "Join failed — see the log."; }
            finally { IsProcessing = false; RefreshCommands(); }
        }

        /// <summary>Joins every rendered beat, in order, up to the first gap.</summary>
        private async Task JoinAsync(CancellationToken ct)
        {
            var clips = Beats.TakeWhile(b => b.HasVideo).Select(b => b.VideoPath).ToList();
            if (clips.Count < 2) { AddLog("Join: at least two rendered beats in a row are needed."); return; }
            var ffmpeg = FindFFmpeg() ?? throw new InvalidOperationException("FFmpeg not found — install it to join the beats.");

            ProcessingStatus = $"Joining {clips.Count} beats…";
            var folder = Path.GetDirectoryName(clips[0]) ?? WorkRoot;
            var output = Path.Combine(folder, $"{(_runFolder.Length > 0 ? _runFolder : "fflf")}_joined_{DateTime.Now:HHmmss}.mp4");
            var withAudio = clips.All(HasAudioStream);
            var args = FflfGraph.JoinArguments(clips, output, dropFirstFrame: LockFirstFrame, withAudio);
            await Task.Run(() => RunFFmpeg(ffmpeg, args), ct);
            if (!File.Exists(output)) throw new InvalidOperationException("FFmpeg did not write the joined video.");

            JoinedVideoPath = output;
            ResultVideoPath = output;
            ResultVideoInfo = $"Joined · {clips.Count} beats · {Path.GetFileName(output)}";
            HasResult = true;
            Show(output);
            ScheduleStorySave();
            try { await LocalCopyService.CopyVideoAsync(output); } catch { /* the joined file is on the share either way */ }
            AddLog($"Joined {clips.Count} beats{(LockFirstFrame ? " (repeated join frames dropped)" : string.Empty)}: {output}");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────

        private static long NewSeed() => Random.Shared.NextInt64(1, 999_999_999_999_999L);

        private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

        /// <summary>Copies a server output into this tab's work folder, so it outlives the download cache.</summary>
        private static string Keep(string path, string name)
        {
            Directory.CreateDirectory(WorkRoot);
            var target = Path.Combine(WorkRoot, $"{name}{Path.GetExtension(path)}");
            File.Copy(path, target, overwrite: true);
            return target;
        }

        private static void OnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }
    }
}
