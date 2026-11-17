using System;
using System.Collections.Generic;
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

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>One sampler / first-pass scheduler pairing, with the step count it is meant for.</summary>
    public sealed record ActionSampling(string Key, string Label, string Sampler, string Scheduler, int Steps);

    /// <summary>
    /// "🥋 Action" tab — an image and a story in, one coherent action scene out.
    ///
    /// <para><b>What it is.</b> The ⛓ FFLF tab (<see cref="H3FflfViewModel"/>) with the "MiniMax H3 Action
    /// Director" workflow on top of it. FFLF brings the chain: beats rendered one after another through the
    /// ErosMax 2-Pass graph, each starting on the last frame of the one before, the opening face restored on
    /// every handoff, the takes joined, the model and LoRA pickers, the story library. This tab adds:</para>
    ///
    /// <list type="bullet">
    /// <item><b>The nine combat presets</b> (<see cref="H3CombatPreset"/>) — A0 basic strikes … A8 motion
    /// repair. A preset is a mix of five LoRAs (Combat, Weapon, GunFu, Motion Repair, Fight Flow), stacked
    /// after the tab's own LoRA; the refine pass runs on the workflow's weaker second-pass mix
    /// (<see cref="FflfGraph.AddRefineLoraMix"/>). The five strengths can be set by hand instead.</item>
    /// <item><b>The fight director</b> (<see cref="ActionDirector"/>) — the opening image and a pasted story go
    /// to the writer model, which returns the setting and the beats as timecoded exchanges, each beat ending
    /// in a pose the next one can start on. They land in the same boxes a hand-written story would.</item>
    /// <item><b>The cast</b> — optional photos of the two fighters, sent with every beat as extra references:
    /// the workflow's reference mode, inside the chain.</item>
    /// <item><b>Sampling</b> — the Turbo pairing FFLF uses, or the workflow's own res_multistep · simple at 20
    /// steps for a non-turbo base model.</item>
    /// </list>
    /// </summary>
    public partial class H3ActionViewModel : H3FflfViewModel
    {
        public static FflfTabProfile ActionProfile { get; } =
            new("Action", "action", "action-stories", "AccentRoseColor", s => s.H3Action ??= new H3ActionSettings());

        public static IReadOnlyList<ActionSampling> Samplings { get; } = new[]
        {
            new ActionSampling("turbo", "Turbo — er_sde · sgm_uniform · 8 steps", string.Empty, string.Empty, 8),
            new ActionSampling("authored", "As authored — res_multistep · simple · 20 steps (base model)", "res_multistep", "simple", 20),
        };

        private const string RoleA = "Fighter A";
        private const string RoleB = "Fighter B";

        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _directCts;
        private List<(string Role, string Uploaded)> _uploadedCast = new();
        private bool _applyingMix;

        public H3ActionViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService,
            LMStudioService lmStudioService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator, fileDialogService, ActionProfile)
        {
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));

            DirectCommand = new RelayCommand(() => _ = DirectAsync(), () => !IsProcessing && !IsDirecting && (HasInitialImage || !string.IsNullOrWhiteSpace(StoryIdea)));
            CancelDirectCommand = new RelayCommand(() => _directCts?.Cancel(), () => IsDirecting);
            BrowseFighterACommand = new RelayCommand(() => _ = BrowseCastAsync(0), () => !IsProcessing);
            BrowseFighterBCommand = new RelayCommand(() => _ = BrowseCastAsync(1), () => !IsProcessing);
            ClearFighterACommand = new RelayCommand(() => FighterAPath = string.Empty, () => !IsProcessing);
            ClearFighterBCommand = new RelayCommand(() => FighterBPath = string.Empty, () => !IsProcessing);
            ResetMixCommand = new RelayCommand(() => { ManualMix = false; SoftenRefine = true; ApplyPresetMix(); });

            LoadThumbnail(0);
            LoadThumbnail(1);
            RefreshCommands();
        }

        protected override string ReadyMessage =>
            "Action ready — drop the opening image, paste the story and press 🎬 Direct (or write the beats yourself), pick a combat preset, and Generate. " +
            "Each beat starts on the last frame of the one before and the takes are joined into one scene.";

        // ── Commands ─────────────────────────────────────────────────────────────────────────────

        public RelayCommand DirectCommand { get; } = null!;
        public RelayCommand CancelDirectCommand { get; } = null!;
        public RelayCommand BrowseFighterACommand { get; } = null!;
        public RelayCommand BrowseFighterBCommand { get; } = null!;
        public RelayCommand ClearFighterACommand { get; } = null!;
        public RelayCommand ClearFighterBCommand { get; } = null!;
        public RelayCommand ResetMixCommand { get; } = null!;

        protected override void RefreshCommands()
        {
            base.RefreshCommands();
            OnUi(() =>
            {
                // Null until this class's constructor has run: the base constructor refreshes too.
                DirectCommand?.NotifyCanExecuteChanged();
                CancelDirectCommand?.NotifyCanExecuteChanged();
                BrowseFighterACommand?.NotifyCanExecuteChanged();
                BrowseFighterBCommand?.NotifyCanExecuteChanged();
                ClearFighterACommand?.NotifyCanExecuteChanged();
                ClearFighterBCommand?.NotifyCanExecuteChanged();
            });
        }

        // ── Combat preset and LoRA mix ───────────────────────────────────────────────────────────

        public IReadOnlyList<H3CombatPreset> Presets => H3CombatPreset.All;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PresetSummary))]
        private H3CombatPreset _selectedPreset = H3CombatPreset.ByNumber(H3CombatPreset.DefaultNumber);

        public string PresetSummary => SelectedPreset?.Summary ?? string.Empty;

        /// <summary>Off: the preset sets the five strengths. On: the sliders do.</summary>
        [ObservableProperty] private bool _manualMix;

        [ObservableProperty] private double _combatStrength = 0.65;
        [ObservableProperty] private double _weaponStrength;
        [ObservableProperty] private double _gunFuStrength;
        [ObservableProperty] private double _motionStrength;
        [ObservableProperty] private double _fightFlowStrength = 0.65;

        /// <summary>Run the refine pass on the workflow's weaker second-pass mix.</summary>
        [ObservableProperty] private bool _softenRefine = true;

        public H3CombatMix FirstPassMix => new(CombatStrength, WeaponStrength, GunFuStrength, MotionStrength, FightFlowStrength);

        /// <summary>What the refine pass runs on: the preset's own second-pass row, or — for a hand-set mix —
        /// the workflow's scale-and-cap of it.</summary>
        public H3CombatMix RefineMix =>
            !SoftenRefine ? FirstPassMix
            : ManualMix || SelectedPreset == null ? H3CombatPreset.RefineFor(FirstPassMix)
            : SelectedPreset.Refine;

        public string MixSummary =>
            PreviewOnly
                ? $"First pass: {FirstPassMix.Describe()}"
                : $"First pass: {FirstPassMix.Describe()}\nRefine: {RefineMix.Describe()}";

        partial void OnSelectedPresetChanged(H3CombatPreset value)
        {
            if (value == null) return;
            if (!ManualMix) ApplyPresetMix();
            MixChanged();
        }

        partial void OnManualMixChanged(bool value)
        {
            if (!value) ApplyPresetMix();
            MixChanged();
        }

        partial void OnCombatStrengthChanged(double value) => MixChanged();
        partial void OnWeaponStrengthChanged(double value) => MixChanged();
        partial void OnGunFuStrengthChanged(double value) => MixChanged();
        partial void OnMotionStrengthChanged(double value) => MixChanged();
        partial void OnFightFlowStrengthChanged(double value) => MixChanged();
        partial void OnSoftenRefineChanged(bool value) => MixChanged();

        private void ApplyPresetMix()
        {
            if (SelectedPreset == null) return;
            _applyingMix = true;
            try
            {
                var mix = SelectedPreset.FirstPass;
                CombatStrength = mix.Combat;
                WeaponStrength = mix.Weapon;
                GunFuStrength = mix.GunFu;
                MotionStrength = mix.Motion;
                FightFlowStrength = mix.FightFlow;
            }
            finally { _applyingMix = false; }
            MixChanged();
        }

        private void MixChanged()
        {
            if (_applyingMix) return;
            OnPropertyChanged(nameof(MixSummary));
            OnPropertyChanged(nameof(MissingLoraNote));
        }

        /// <summary>The server's name for one of the five LoRAs: under loras/H3 when it is there, otherwise
        /// wherever a file of that name is. Null when the server has none. Before the lists are read the H3
        /// folder is assumed, as the other H3 tabs do.</summary>
        private string? ResolveLora(H3CombatLoraFile lora)
        {
            if (ServerLoras.Count == 0) return $"H3/{lora.FileName}";
            return ServerLoras.FirstOrDefault(n => n.Equals($"H3/{lora.FileName}", StringComparison.OrdinalIgnoreCase))
                   ?? ServerLoras.FirstOrDefault(n => Path.GetFileName(n).Equals(lora.FileName, StringComparison.OrdinalIgnoreCase));
        }

        private List<MiniMaxI2VLora> Resolve(H3CombatMix mix) =>
            H3CombatPreset.Loras
                .Where(l => mix[l.Lora] > 0)
                .Select(l => (File: ResolveLora(l), Strength: mix[l.Lora]))
                .Where(l => l.File != null)
                .Select(l => new MiniMaxI2VLora { Name = l.File!, Strength = l.Strength })
                .ToList();

        private List<H3CombatLoraFile> MissingLoras() =>
            H3CombatPreset.Loras.Where(l => FirstPassMix[l.Lora] > 0 && ResolveLora(l) == null).ToList();

        /// <summary>Names the LoRAs of the mix the server does not have, and where they come from.</summary>
        public string MissingLoraNote
        {
            get
            {
                var missing = MissingLoras();
                return missing.Count == 0
                    ? string.Empty
                    : "⚠ Not on the server — put in loras/H3, then ⟳ Server lists:\n" + string.Join("\n", missing.Select(l => $"{l.FileName}\n{l.Url}"));
            }
        }

        public bool HasMissingLoras => MissingLoraNote.Length > 0;

        protected override void OnModelListsLoaded()
        {
            OnPropertyChanged(nameof(MissingLoraNote));
            OnPropertyChanged(nameof(HasMissingLoras));
            foreach (var l in MissingLoras())
                AddLog($"⚠ {l.FileName} is not on the server ({l.Label}: {l.Purpose}) — get it from {l.Url}");
        }

        // ── Sampling ─────────────────────────────────────────────────────────────────────────────

        public IReadOnlyList<ActionSampling> SamplingOptions => Samplings;

        [ObservableProperty] private ActionSampling _selectedSampling = Samplings[0];

        partial void OnSelectedSamplingChanged(ActionSampling value)
        {
            if (value == null || IsLoading) return;
            // The pairing and its step count go together: 8 turbo steps on res_multistep, or 20 on a turbo
            // model, are both the wrong picture.
            FirstPassSteps = value.Steps;
            AddLog(value.Sampler.Length == 0
                ? "Sampling: Turbo — er_sde · sgm_uniform, 8 first-pass steps. For the turbo models (Eros Max, anything with a 4/8-step LoRA)."
                : "Sampling: as authored — res_multistep · simple, 20 first-pass steps. For a non-turbo base model (minimax_h3_fl2va…); several times slower.");
        }

        // ── Cast ─────────────────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasFighterA), nameof(CastSummary))]
        private string _fighterAPath = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasFighterB), nameof(CastSummary))]
        private string _fighterBPath = string.Empty;

        public bool HasFighterA => FighterAPath.Length > 0 && File.Exists(FighterAPath);
        public bool HasFighterB => FighterBPath.Length > 0 && File.Exists(FighterBPath);

        private System.Windows.Media.Imaging.BitmapImage? _fighterAThumbnail, _fighterBThumbnail;
        public System.Windows.Media.Imaging.BitmapImage? FighterAThumbnail => _fighterAThumbnail;
        public System.Windows.Media.Imaging.BitmapImage? FighterBThumbnail => _fighterBThumbnail;

        public string CastSummary
        {
            get
            {
                var n = (HasFighterA ? 1 : 0) + (HasFighterB ? 1 : 0);
                return n == 0
                    ? "Optional. Without photos the fighters are taken from the opening image."
                    : $"{n} photo{(n == 1 ? "" : "s")} sent with every beat as extra references, after the start frame and the opening image.";
            }
        }

        partial void OnFighterAPathChanged(string value) => LoadThumbnail(0);
        partial void OnFighterBPathChanged(string value) => LoadThumbnail(1);

        private void LoadThumbnail(int slot)
        {
            var path = slot == 0 ? FighterAPath : FighterBPath;
            if (slot == 0) _fighterAThumbnail = null; else _fighterBThumbnail = null;
            OnPropertyChanged(slot == 0 ? nameof(FighterAThumbnail) : nameof(FighterBThumbnail));
            AsyncThumbnail.Load(path, 240, 0, bitmap =>
            {
                if ((slot == 0 ? FighterAPath : FighterBPath) != path) return;
                if (slot == 0) _fighterAThumbnail = bitmap; else _fighterBThumbnail = bitmap;
                OnPropertyChanged(slot == 0 ? nameof(FighterAThumbnail) : nameof(FighterBThumbnail));
            });
        }

        private async Task BrowseCastAsync(int slot)
        {
            var start = _settingsService.Settings is { } all ? Profile.Settings(all).LastPhotoFolder : null;
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
                start = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var path = await _fileDialogService.OpenFileDialogAsync($"A photo of {(slot == 0 ? RoleA : RoleB)}",
                "Image Files|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All Files|*.*", start, persistKey: "action.cast");
            if (path != null) SetCastImage(slot, path);
        }

        /// <summary>Lets a dropped file be a fighter's photo.</summary>
        public void SetCastImage(int slot, string path)
        {
            if (IsProcessing || !File.Exists(path)) return;
            if (slot == 0) FighterAPath = path; else FighterBPath = path;
            AddLog($"{(slot == 0 ? RoleA : RoleB)}: {Path.GetFileName(path)}");
        }

        private List<(string Role, string Path)> Cast()
        {
            var cast = new List<(string, string)>();
            if (HasFighterA) cast.Add((RoleA, FighterAPath));
            if (HasFighterB) cast.Add((RoleB, FighterBPath));
            return cast;
        }

        // ── The director ─────────────────────────────────────────────────────────────────────────

        /// <summary>The story, pasted or typed: what the director turns into beats.</summary>
        [ObservableProperty] private string _storyIdea = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DirectPlan))]
        private int _beatCount = 4;

        public string DirectPlan => $"{BeatCount} beat{(BeatCount == 1 ? "" : "s")} × {BeatSeconds:0.#} s = {BeatCount * BeatSeconds:0.#} s";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DirectLabel))]
        private bool _isDirecting;

        public string DirectLabel => IsDirecting ? "Directing…" : HasBeats ? "🎬 Direct again" : "🎬 Direct the scene";

        [ObservableProperty] private string _directStatus = string.Empty;

        partial void OnStoryIdeaChanged(string value) => DirectCommand?.NotifyCanExecuteChanged();
        partial void OnIsDirectingChanged(bool value) => RefreshCommands();

        private async Task<string> ResolveWriterModelAsync(CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://localhost:8080";
            await _lmStudioService.SetBaseUrlAsync(baseUrl);
            var models = await _lmStudioService.GetAvailableModelsAsync(token);
            var model = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
            if (string.IsNullOrEmpty(model) && models.Count > 0)
                model = models[0].Id ?? models[0].Name ?? string.Empty;
            if (string.IsNullOrEmpty(model))
                throw new InvalidOperationException("no LM Studio / llama-server model is available. Make sure the server is running and a model is loaded.");
            return model;
        }

        /// <summary>
        /// The opening image, the cast photos and the story go to the writer; the setting and the beats it
        /// returns replace the ones on the page. Beats already rendered from different text render again, as
        /// with any edit of the beat box.
        /// </summary>
        private async Task DirectAsync()
        {
            if (IsDirecting || IsProcessing) return;
            IsDirecting = true;
            _directCts = new CancellationTokenSource();
            var token = _directCts.Token;
            var started = DateTime.Now;
            try
            {
                DirectStatus = "Finding the writer model…";
                var model = await ResolveWriterModelAsync(token);

                var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", ActionDirector.SystemPromptFile);
                if (!File.Exists(promptPath)) throw new FileNotFoundException($"System prompt not found: {promptPath}");
                var system = await File.ReadAllTextAsync(promptPath, token);

                var cast = Cast();
                var pictures = new List<string>();
                if (HasInitialImage) pictures.Add(InitialImagePath);
                pictures.AddRange(cast.Select(c => c.Path));
                var preset = SelectedPreset;
                var beats = Math.Clamp(BeatCount, ActionDirector.MinBeats, ActionDirector.MaxBeats);
                var request = ActionDirector.BuildRequest(preset, beats, BeatSeconds, StoryIdea, HasInitialImage, cast.Select(c => c.Role).ToList());

                AddLog($"🎬 Directing {beats} beat(s) of {BeatSeconds:0.#} s in style {preset.Label} — {pictures.Count} picture(s) → " +
                       (pictures.Count > 0 ? _lmStudioService.DescribeTarget(model) : _lmStudioService.DescribeTextTarget(model)));
                DirectStatus = $"The writer is choreographing {beats} beat(s)…";

                // Off the UI thread: the service resizes and encodes the pictures before it sends anything.
                var maxTokens = 1500 + beats * 700;
                var reply = pictures.Count > 0
                    ? await Task.Run(() => _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                        model, pictures, request, system, maxTokens: maxTokens, cancellationToken: token), token)
                    : await _lmStudioService.SendTextChatAsync(model, system, request, maxTokens, token);

                var script = ActionDirector.ParseScript(CleanLLMOutput(reply));
                if (script.Beats.Count == 0) throw new InvalidOperationException("the writer returned no beats.");

                OnUi(() =>
                {
                    if (script.Setting.Length > 0) StyleText = script.Setting;
                    BeatsText = ActionDirector.ToBeatsText(script.Beats);
                });
                var note = script.Beats.Count == beats ? string.Empty : $" (asked for {beats})";
                DirectStatus = $"{script.Beats.Count} beat(s) written{note} — read them through, then Generate.";
                AddLog($"🎬 {script.Beats.Count} beat(s) written{note} in {(DateTime.Now - started):m\\:ss}" +
                       (script.Setting.Length > 0 ? ", with the setting." : " — no setting came back; the one on the page is kept."));
            }
            catch (OperationCanceledException)
            {
                DirectStatus = "Cancelled.";
            }
            catch (Exception ex)
            {
                DirectStatus = $"Failed: {ex.Message}";
                AddLog($"🎬 Directing failed: {ex.Message}");
                _logger.LogError(ex, "Action director failed");
            }
            finally
            {
                _directCts?.Dispose();
                _directCts = null;
                IsDirecting = false;
            }
        }

        // ── The render ───────────────────────────────────────────────────────────────────────────

        protected override async Task PrepareRunAsync(CancellationToken ct)
        {
            var uploaded = new List<(string, string)>();
            foreach (var (role, path) in Cast())
            {
                ProcessingStatus = $"Sending {role}'s photo to ComfyUI…";
                var name = await _comfyUIService.UploadImageAsync(path, ct);
                if (string.IsNullOrEmpty(name)) throw new InvalidOperationException($"{role}'s photo could not be uploaded.");
                uploaded.Add((role, name));
            }
            _uploadedCast = uploaded;

            var missing = MissingLoras();
            if (missing.Count > 0)
                AddLog($"⚠ Rendering without {string.Join(", ", missing.Select(l => l.FileName))} — not on the server. The rest of the mix is applied.");
            AddLog($"Preset {SelectedPreset.Label}{(ManualMix ? " (mix set by hand)" : string.Empty)} — {MixSummary.Replace("\n", " · ")}" +
                   (uploaded.Count > 0 ? $" · cast: {string.Join(", ", uploaded.Select(u => u.Item1))}" : string.Empty) + ".");
        }

        protected override string ComposeBeatPrompt(FflfBeat beat, int index, int total, bool hasIdentityImage) =>
            ActionDirector.ComposeBeatPrompt(beat.Text, StyleText, SelectedPreset, FirstPassMix, BeatSeconds,
                hasIdentityImage, isFirstBeat: index == 0, isLastBeat: index == total - 1,
                _uploadedCast.Select(c => c.Role).ToList(), MiniMaxI2VViewModel.MaxTwoPassPictures);

        protected override FflfBeatRender CustomizeRender(FflfBeatRender request, int index) => request with
        {
            ExtraLoras = Resolve(FirstPassMix),
            RefineLoras = SoftenRefine && !PreviewOnly ? Resolve(RefineMix) : null,
            CastImages = _uploadedCast.Select(c => c.Uploaded).ToList(),
            Sampler = SelectedSampling.Sampler,
            FirstPassScheduler = SelectedSampling.Scheduler,
        };

        // ── Settings and stories ─────────────────────────────────────────────────────────────────

        private static readonly HashSet<string> OwnPersisted = new()
        {
            nameof(SelectedPreset), nameof(ManualMix), nameof(CombatStrength), nameof(WeaponStrength), nameof(GunFuStrength),
            nameof(MotionStrength), nameof(FightFlowStrength), nameof(SoftenRefine), nameof(FighterAPath), nameof(FighterBPath),
            nameof(StoryIdea), nameof(BeatCount), nameof(SelectedSampling),
        };

        protected override bool IsPersistedProperty(string name) =>
            base.IsPersistedProperty(name) || (OwnPersisted.Contains(name) && !_applyingMix);

        protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            switch (e.PropertyName)
            {
                case nameof(BeatSeconds): OnPropertyChanged(nameof(DirectPlan)); break;
                case nameof(PreviewOnly): OnPropertyChanged(nameof(MixSummary)); break;
                case nameof(HasBeats): OnPropertyChanged(nameof(DirectLabel)); break;
                case nameof(MissingLoraNote): OnPropertyChanged(nameof(HasMissingLoras)); break;
            }
        }

        private void ReadMix(int preset, bool manual, double combat, double weapon, double gunFu, double motion, double fightFlow,
                             bool soften, string? fighterA, string? fighterB, string? idea, int beats, string? sampling)
        {
            SelectedPreset = H3CombatPreset.ByNumber(preset);
            ManualMix = manual;
            if (manual)
            {
                CombatStrength = Math.Clamp(combat, 0, 2);
                WeaponStrength = Math.Clamp(weapon, 0, 2);
                GunFuStrength = Math.Clamp(gunFu, 0, 2);
                MotionStrength = Math.Clamp(motion, 0, 2);
                FightFlowStrength = Math.Clamp(fightFlow, 0, 2);
            }
            else ApplyPresetMix();
            SoftenRefine = soften;
            FighterAPath = File.Exists(fighterA) ? fighterA! : string.Empty;
            FighterBPath = File.Exists(fighterB) ? fighterB! : string.Empty;
            StoryIdea = idea ?? string.Empty;
            BeatCount = Math.Clamp(beats, ActionDirector.MinBeats, ActionDirector.MaxBeats);
            SelectedSampling = Samplings.FirstOrDefault(o => o.Key == sampling) ?? Samplings[0];
        }

        protected override void LoadTabSettings(H3FflfSettings settings)
        {
            if (settings is not H3ActionSettings s) return;
            ReadMix(s.Preset, s.ManualMix, s.CombatStrength, s.WeaponStrength, s.GunFuStrength, s.MotionStrength, s.FightFlowStrength,
                    s.SoftenRefine, s.FighterAPath, s.FighterBPath, s.StoryIdea, s.BeatCount, s.Sampling);
        }

        protected override void SaveTabSettings(H3FflfSettings settings)
        {
            if (settings is not H3ActionSettings s) return;
            s.Preset = SelectedPreset?.Number ?? H3CombatPreset.DefaultNumber;
            s.ManualMix = ManualMix;
            s.CombatStrength = CombatStrength;
            s.WeaponStrength = WeaponStrength;
            s.GunFuStrength = GunFuStrength;
            s.MotionStrength = MotionStrength;
            s.FightFlowStrength = FightFlowStrength;
            s.SoftenRefine = SoftenRefine;
            s.FighterAPath = FighterAPath;
            s.FighterBPath = FighterBPath;
            s.StoryIdea = StoryIdea;
            s.BeatCount = BeatCount;
            s.Sampling = SelectedSampling?.Key ?? Samplings[0].Key;
        }

        protected override void SnapshotTabStory(FflfStory story) => story.Action = new FflfActionStory
        {
            Preset = SelectedPreset?.Number ?? H3CombatPreset.DefaultNumber,
            ManualMix = ManualMix,
            CombatStrength = CombatStrength,
            WeaponStrength = WeaponStrength,
            GunFuStrength = GunFuStrength,
            MotionStrength = MotionStrength,
            FightFlowStrength = FightFlowStrength,
            SoftenRefine = SoftenRefine,
            FighterAPath = FighterAPath,
            FighterBPath = FighterBPath,
            StoryIdea = StoryIdea,
            BeatCount = BeatCount,
            Sampling = SelectedSampling?.Key ?? Samplings[0].Key,
        };

        protected override void ApplyTabStory(FflfStory story)
        {
            if (story.Action is not { } a) return;
            ReadMix(a.Preset, a.ManualMix, a.CombatStrength, a.WeaponStrength, a.GunFuStrength, a.MotionStrength, a.FightFlowStrength,
                    a.SoftenRefine, a.FighterAPath, a.FighterBPath, a.StoryIdea, a.BeatCount, a.Sampling);
            _uploadedCast = new List<(string, string)>();
        }

        /// <summary>＋ New keeps the preset and the mix — they are how the user likes fights rendered — and
        /// clears what belongs to the story: its idea and its cast.</summary>
        protected override void ClearTabStory()
        {
            StoryIdea = string.Empty;
            FighterAPath = string.Empty;
            FighterBPath = string.Empty;
            DirectStatus = string.Empty;
            _uploadedCast = new List<(string, string)>();
        }
    }
}
