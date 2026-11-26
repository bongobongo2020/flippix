using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
    /// "🥷 Martial" tab — the MiniMax H3 martial-arts workflow (02_MiniMax_H3martial, see <see cref="MartialGraph"/>)
    /// inside the ⛓ FFLF chain.
    ///
    /// <para><b>From FFLF</b> (<see cref="H3FflfViewModel"/>): the opening image, the beats and the setting, each
    /// beat starting on the last frame of the one before (face restored on the handoff), the takes joined, the
    /// story library — and 📥 Import, which brings a story over from the ⛓ FFLF or 🥋 Action library.</para>
    ///
    /// <para><b>From the workflow</b>: the wushu LoRA stack (turbo 8-step, spatial physics, wushu_action, camera
    /// motion — any rows, any strengths, on either pass), the diffusion model and an optional second one for the
    /// refine, the text encoder, up to seven reference pictures with what each one shows, keyframes pinned at a
    /// second of a beat (MiniMaxH3AddGuide), the trigger words that lead every prompt, and the two-stage refine
    /// through the 3D latent upscaler.</para>
    /// </summary>
    public partial class H3MartialViewModel : H3FflfViewModel
    {
        /// <summary>This tab brings its own cast pictures.</summary>
        protected override bool UsesCharacterSheets => false;

        public static FflfTabProfile MartialProfile { get; } =
            new("Martial", "martial", "martial-stories", "AccentAmberColor", s => s.H3Martial ??= new H3MartialSettings());

        /// <summary>Start frame and character reference take two of the nine picture slots.</summary>
        public const int MaxReferences = MartialGraph.MaxPictures - 2;
        public const int MaxGuides = 3;

        public static IReadOnlyList<MegapixelOption> MartialFirstPassSizes { get; } = new[]
        {
            new MegapixelOption(0.3, "0.3 MP — 736×416"),
            new MegapixelOption(0.4, "0.4 MP — 864×480"),
            new MegapixelOption(0.5, "0.5 MP — 960×544"),
            new MegapixelOption(0.6, "0.6 MP — 1056×608"),
            new MegapixelOption(0.7, "0.7 MP — 1152×640"),
            new MegapixelOption(0.8, "0.8 MP — 1216×672 (workflow)"),
            new MegapixelOption(0.98, "0.98 MP — 1344×768"),
        };

        public static IReadOnlyList<MegapixelOption> MartialFinalSizes { get; } = new[]
        {
            new MegapixelOption(1.0, "1.0 MP — 1376×768"),
            new MegapixelOption(1.2, "1.2 MP — 1504×832 (workflow)"),
            new MegapixelOption(1.5, "1.5 MP — 1664×928"),
            new MegapixelOption(1.8, "1.8 MP — 1824×1024"),
            new MegapixelOption(2.0, "2.0 MP — 1920×1088"),
        };

        public static IReadOnlyList<MegapixelOption> AttentionOptionsList { get; } = new[]
        {
            new MegapixelOption(0, MartialGraph.DefaultAttention),
            new MegapixelOption(1, MartialGraph.SageAttention),
            new MegapixelOption(2, "pytorch attention"),
            new MegapixelOption(3, MartialGraph.NoAttentionPatch),
        };

        private readonly Dictionary<string, string> _uploaded = new(StringComparer.OrdinalIgnoreCase);
        private bool _restoring;

        public H3MartialViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator, fileDialogService, MartialProfile)
        {
            AddLoraCommand = new RelayCommand(() => AddLora(string.Empty, 0.5), () => !IsProcessing);
            RemoveLoraCommand = new RelayCommand<MartialLoraRow>(r => { if (r != null) Loras.Remove(r); }, _ => !IsProcessing);
            AuthoredStackCommand = new RelayCommand(UseAuthoredStack, () => !IsProcessing);
            AddReferenceCommand = new RelayCommand(() => _ = BrowseReferenceAsync(), () => !IsProcessing && References.Count < MaxReferences);
            RemoveReferenceCommand = new RelayCommand<MartialReference>(r => { if (r != null) References.Remove(r); }, _ => !IsProcessing);
            AddGuideCommand = new RelayCommand(() => _ = BrowseGuideAsync(), () => !IsProcessing && Guides.Count < MaxGuides);
            RemoveGuideCommand = new RelayCommand<MartialGuide>(g => { if (g != null) Guides.Remove(g); }, _ => !IsProcessing);
            ImportFflfCommand = new RelayCommand(() => _ = ImportStoryAsync(FflfTabProfile.Fflf), () => !IsProcessing);
            ImportActionCommand = new RelayCommand(() => _ = ImportStoryAsync(H3ActionViewModel.ActionProfile), () => !IsProcessing);

            Loras.CollectionChanged += Items_CollectionChanged;
            References.CollectionChanged += Items_CollectionChanged;
            Guides.CollectionChanged += Items_CollectionChanged;
            foreach (var r in Loras) r.PropertyChanged += Item_PropertyChanged;
            foreach (var r in References) r.PropertyChanged += Item_PropertyChanged;
            foreach (var g in Guides) g.PropertyChanged += Item_PropertyChanged;
            UpdateGuideHints();
            RefreshCommands();
        }

        protected override string ReadyMessage =>
            "Martial ready — pick the opening image, write the fight beat by beat (or 📥 import an FFLF story), add reference pictures " +
            "and keyframes if you have them, and Generate. Each beat renders through the MiniMax H3 martial workflow and starts on the last frame of the one before.";

        // ── Commands ─────────────────────────────────────────────────────────────────────────────

        public RelayCommand AddLoraCommand { get; } = null!;
        public RelayCommand<MartialLoraRow> RemoveLoraCommand { get; } = null!;
        public RelayCommand AuthoredStackCommand { get; } = null!;
        public RelayCommand AddReferenceCommand { get; } = null!;
        public RelayCommand<MartialReference> RemoveReferenceCommand { get; } = null!;
        public RelayCommand AddGuideCommand { get; } = null!;
        public RelayCommand<MartialGuide> RemoveGuideCommand { get; } = null!;
        public RelayCommand ImportFflfCommand { get; } = null!;
        public RelayCommand ImportActionCommand { get; } = null!;

        protected override void RefreshCommands()
        {
            base.RefreshCommands();
            OnUi(() =>
            {
                // Null until this class's constructor has run: the base constructor refreshes too.
                AddLoraCommand?.NotifyCanExecuteChanged();
                RemoveLoraCommand?.NotifyCanExecuteChanged();
                AuthoredStackCommand?.NotifyCanExecuteChanged();
                AddReferenceCommand?.NotifyCanExecuteChanged();
                RemoveReferenceCommand?.NotifyCanExecuteChanged();
                AddGuideCommand?.NotifyCanExecuteChanged();
                RemoveGuideCommand?.NotifyCanExecuteChanged();
                ImportFflfCommand?.NotifyCanExecuteChanged();
                ImportActionCommand?.NotifyCanExecuteChanged();
            });
        }

        // ── Canvas ───────────────────────────────────────────────────────────────────────────────

        public override IReadOnlyList<MegapixelOption> FirstPassSizes => MartialFirstPassSizes;
        public override IReadOnlyList<MegapixelOption> FinalSizes => MartialFinalSizes;

        public int FramesPerBeat => MartialGraph.FrameCount(BeatSeconds);

        public override string CanvasSummary
        {
            get
            {
                var aspect = ResolvedAspectRatio;
                var (w1, h1) = H3Canvas.Resolve(aspect, FirstPassMegapixels, MartialGraph.ResolutionMultiple);
                var frames = $"{FramesPerBeat} frames @ 24 fps";
                if (PreviewOnly) return $"{aspect} · {frames} · draft at {w1}×{h1} (pass 1 only)";
                var (w2, h2) = H3Canvas.Resolve(aspect, FinalMegapixels, MartialGraph.ResolutionMultiple);
                var rtx = RtxUpscale ? $" → RTX ×{RtxScale:0.#}" : string.Empty;
                return $"{aspect} · {frames} · {w1}×{h1} pass 1 → refined at ≈{w2}×{h2}{rtx}";
            }
        }

        // ── Model, text encoder, trigger words, refine ───────────────────────────────────────────

        public ObservableCollection<string> RefineModelOptions { get; } = new() { string.Empty };

        /// <summary>Empty: the refine runs on the first-pass model.</summary>
        [ObservableProperty] private string _refineModel = string.Empty;
        [ObservableProperty] private string _triggers = H3MartialSettings.DefaultTriggers;
        [ObservableProperty] private MegapixelOption _selectedAttention = AttentionOptionsList[0];
        public IReadOnlyList<MegapixelOption> AttentionOptions => AttentionOptionsList;

        [ObservableProperty] private double _refineStrength = 0.6;
        [ObservableProperty] private int _refineSteps = 10;
        [ObservableProperty] private bool _rtxUpscale;
        [ObservableProperty] private double _rtxScale = 2.0;

        public string RefineSummary
        {
            get
            {
                var (high, low) = MartialGraph.RefineSigmas(RefineStrength, RefineSteps);
                return $"{high.Length - 1} steps from σ {high[0]:0.00} at pass-1 size, 3D upscale, {low.Length - 1} steps from σ {low[0]:0.00} to finish.";
            }
        }

        partial void OnRtxUpscaleChanged(bool value) => OnPropertyChanged(nameof(CanvasSummary));
        partial void OnRtxScaleChanged(double value) => OnPropertyChanged(nameof(CanvasSummary));
        partial void OnRefineStrengthChanged(double value) => OnPropertyChanged(nameof(RefineSummary));
        partial void OnRefineStepsChanged(int value) => OnPropertyChanged(nameof(RefineSummary));

        protected override void ResetModel()
        {
            DiffusionModel = H3MartialSettings.DefaultMartialModel;
            RefineModel = string.Empty;
            TextEncoder = H3MartialSettings.DefaultTextEncoder;
            FirstPassSteps = 8;
            RefineStrength = 0.6;
            RefineSteps = 10;
            SelectedAttention = AttentionOptionsList[0];
            Triggers = H3MartialSettings.DefaultTriggers;
            UseAuthoredStack();
        }

        protected override string DescribeModel()
        {
            var on = Loras.Where(l => l.On && l.Name.Length > 0).ToList();
            var refine = RefineModel.Trim().Length > 0 ? $", refine on {Path.GetFileName(RefineModel)}" : string.Empty;
            return $"Model {Path.GetFileName(DiffusionModel)}{refine}, text encoder {Path.GetFileName(TextEncoder)}, " +
                   (on.Count == 0 ? "no LoRAs" : "LoRAs " + string.Join(" + ", on.Select(l => $"{Path.GetFileNameWithoutExtension(l.Name)} @ {l.Strength:0.##}")));
        }

        // ── LoRA stack ───────────────────────────────────────────────────────────────────────────

        public ObservableCollection<MartialLoraRow> Loras { get; } = new();

        public string StackSummary
        {
            get
            {
                var on = Loras.Count(l => l.On && l.Name.Length > 0);
                var missing = Loras.Count(l => l.On && l.IsMissing);
                return $"{on} of {Loras.Count} LoRA(s) on" + (missing > 0 ? $" · ⚠ {missing} not on the server" : string.Empty);
            }
        }

        private void AddLora(string name, double strength, bool on = true, bool onRefine = true)
        {
            var row = new MartialLoraRow { Name = name, Strength = strength, On = on, OnRefine = onRefine };
            row.MissingNote = MissingNoteFor(row.Name);
            Loras.Add(row);
        }

        private void UseAuthoredStack()
        {
            _restoring = true;
            try
            {
                Loras.Clear();
                foreach (var (name, strength) in MartialCatalog.AuthoredStack) AddLora(ResolveLora(name) ?? name, strength);
            }
            finally { _restoring = false; }
            MartialChanged();
        }

        /// <summary>The server's name for a LoRA: as given when it has it, otherwise wherever a file of that name
        /// is. Null when the server's list is read and the file is not on it.</summary>
        private string? ResolveLora(string name)
        {
            var norm = name.Trim().Replace('\\', '/');
            if (ServerLoras.Count == 0) return norm;
            return ServerLoras.FirstOrDefault(n => n.Equals(norm, StringComparison.OrdinalIgnoreCase))
                   ?? ServerLoras.FirstOrDefault(n => Path.GetFileName(n).Equals(Path.GetFileName(norm), StringComparison.OrdinalIgnoreCase));
        }

        private string MissingNoteFor(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || ServerLoras.Count == 0 || ResolveLora(name) != null) return string.Empty;
            var known = MartialCatalog.Find(name);
            return known == null
                ? "⚠ Not on the server."
                : $"⚠ Not on the server — put it in {known.Folder}, then ⟳ Server lists:\n{known.Url}";
        }

        public ObservableCollection<string> MartialLoraOptions { get; } = new() { string.Empty };

        protected override void OnModelListsLoaded()
        {
            foreach (var n in ServerLoras.Where(n => n.StartsWith("H3/", StringComparison.OrdinalIgnoreCase) || n.Contains("wushu", StringComparison.OrdinalIgnoreCase))
                                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                if (!MartialLoraOptions.Contains(n)) MartialLoraOptions.Add(n);
            foreach (var m in ModelOptions)
                if (!RefineModelOptions.Contains(m)) RefineModelOptions.Add(m);

            foreach (var row in Loras)
            {
                // A row saved under one folder that the server keeps in another is re-pointed.
                if (ResolveLora(row.Name) is { } found && !found.Equals(row.Name, StringComparison.Ordinal)) row.Name = found;
                row.MissingNote = MissingNoteFor(row.Name);
                if (row.On && row.IsMissing) AddLog($"⚠ LoRA {Path.GetFileName(row.Name)} is not on the server" +
                                                    (MartialCatalog.Find(row.Name) is { } k ? $" — get it from {k.Url}" : "."));
            }
            OnPropertyChanged(nameof(StackSummary));
        }

        // ── References ───────────────────────────────────────────────────────────────────────────

        public ObservableCollection<MartialReference> References { get; } = new();

        public string ReferenceSummary =>
            References.Count == 0
                ? "Optional. Character sheets, outfits, places, creatures, weapons — each with a line saying what it is."
                : $"{References.Count} of {MaxReferences} · sent with every beat as <Picture {FirstReferenceNumber(true)}>… " +
                  "(after the start frame and, from beat 2, the opening image).";

        private static int FirstReferenceNumber(bool withIdentity) => withIdentity ? 3 : 2;

        private async Task BrowseReferenceAsync()
        {
            var path = await PickImageAsync("A reference picture", "martial.reference");
            if (path != null) AddReference(path);
        }

        /// <summary>Lets a dropped file be a reference.</summary>
        public void AddReference(string path)
        {
            if (IsProcessing || !File.Exists(path) || References.Count >= MaxReferences) return;
            References.Add(new MartialReference(path, string.Empty));
            AddLog($"Reference: {Path.GetFileName(path)} — say what it shows in the line under it.");
        }

        // ── Keyframes ────────────────────────────────────────────────────────────────────────────

        public ObservableCollection<MartialGuide> Guides { get; } = new();

        public string GuideSummary =>
            Guides.Count == 0
                ? "Optional. A picture the beat must pass through at a given second (MiniMaxH3AddGuide) — up to three, as authored."
                : $"{Guides.Count} of {MaxGuides} keyframe(s). Each is scaled to the canvas and pinned in its beat; " +
                  "describe the matching moment in the beat at that time.";

        private async Task BrowseGuideAsync()
        {
            var path = await PickImageAsync("A keyframe", "martial.keyframe");
            if (path != null) AddGuide(path);
        }

        /// <summary>Lets a dropped file be a keyframe: in the first beat, after the last keyframe there.</summary>
        public void AddGuide(string path)
        {
            if (IsProcessing || !File.Exists(path) || Guides.Count >= MaxGuides) return;
            var beat = 1;
            var at = Guides.Where(g => g.Beat == beat).Select(g => g.Seconds).DefaultIfEmpty(0).Max();
            var seconds = Math.Min(Math.Max(BeatSeconds - 0.5, 0.5), at + Math.Max(1, Math.Round(BeatSeconds / 4, 1)));
            Guides.Add(new MartialGuide(path, beat, seconds));
            AddLog($"Keyframe: {Path.GetFileName(path)} in beat {beat} at {seconds:0.#} s.");
        }

        private void UpdateGuideHints()
        {
            var frames = FramesPerBeat;
            foreach (var g in Guides)
            {
                var beatOk = g.Beat >= 1 && (Beats.Count == 0 || g.Beat <= Beats.Count);
                g.FrameHint = beatOk
                    ? $"frame {MartialGraph.KeyframeIndex(g.Seconds, frames)} of {frames}"
                    : $"⚠ there is no beat {g.Beat}";
            }
        }

        private async Task<string?> PickImageAsync(string title, string key)
        {
            var start = _settingsService.Settings is { } all ? Profile.Settings(all).LastPhotoFolder : null;
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
                start = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            return await _fileDialogService.OpenFileDialogAsync(title,
                "Image Files|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All Files|*.*", start, persistKey: key);
        }

        // ── Change tracking ──────────────────────────────────────────────────────────────────────

        private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (INotifyPropertyChanged i in e.OldItems) i.PropertyChanged -= Item_PropertyChanged;
            if (e.NewItems != null) foreach (INotifyPropertyChanged i in e.NewItems) i.PropertyChanged += Item_PropertyChanged;
            if (ReferenceEquals(sender, References)) _uploaded.Clear();
            MartialChanged();
            RefreshCommands();
        }

        private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MartialLoraRow.MissingNote) or nameof(MartialLoraRow.IsMissing) or nameof(MartialLoraRow.Purpose)
                or nameof(MartialGuide.FrameHint) or nameof(MartialGuide.Thumbnail) or nameof(MartialGuide.FileName)) return;
            if (sender is MartialLoraRow row && e.PropertyName == nameof(MartialLoraRow.Name)) row.MissingNote = MissingNoteFor(row.Name);
            MartialChanged();
        }

        /// <summary>The stack, references or keyframes changed: summaries refresh, settings and the story save.</summary>
        private void MartialChanged()
        {
            if (_restoring) return;
            UpdateGuideHints();
            OnPropertyChanged(nameof(ReferenceSummary));
            OnPropertyChanged(nameof(GuideSummary));
            OnPropertyChanged(nameof(StackSummary));   // persisted: saves settings and the story
        }

        // ── 📥 Import from the FFLF / Action story libraries ─────────────────────────────────────

        /// <summary>Opens another tab's story library and brings the picked story's opening image, beats, setting,
        /// beat length and aspect onto this page as a new story. Its takes stay behind — they were rendered by
        /// another graph — and the other library is only read.</summary>
        private async Task ImportStoryAsync(FflfTabProfile from)
        {
            if (IsProcessing) return;
            try
            {
                var library = new ScenePromptLibrary(AddLog, ScenePromptLibrary.FolderFor(from.LibraryKey));
                var stories = (await library.LoadAsync()).Where(s => s.Fflf != null).ToList();
                if (stories.Count == 0) { AddLog($"📥 The {from.Name} library has no stories yet."); return; }

                var window = new ScenePromptLibraryWindow(library, stories, new ScenePromptLibraryChrome
                {
                    WindowTitle = $"Import a story from {from.Name}",
                    Heading = $"{from.Name} stories",
                    Subtitle = $"Pick a story from the {from.Name} tab to render through the martial workflow. Its opening image, beats, "
                             + "setting and beat length come over as a new Martial story; its takes stay with it.",
                    PromptNote = "Read-only here — edit the beats on the Martial page after importing.",
                    PromptEditable = false,
                    EmptyText = $"No stories in the {from.Name} library.",
                    NoMatchText = "No story matches that search.",
                    UseButtonText = "📥 Import This Story",
                    LibraryNoun = "stories",
                    AccentColorKey = MartialProfile.AccentColorKey,
                });
                window.Owner = Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive);
                if (window.Owner == null) window.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

                if (window.ShowDialog() != true || window.SelectedScene?.Fflf is not { } s) return;
                NewStoryCommand.Execute(null);
                InitialImagePath = File.Exists(s.InitialImagePath) ? s.InitialImagePath : string.Empty;
                StyleText = FflfGraph.ComposeLook(s.StyleText, s.SettingText, s.WardrobeText);
                BeatSeconds = Math.Clamp(s.BeatSeconds, 1, 20);
                if (AspectRatioOptions.Contains(s.AspectRatio)) SelectedAspectRatio = s.AspectRatio;
                BeatsText = s.BeatsText ?? string.Empty;
                AddLog($"📥 Imported \"{window.SelectedScene.Name}\" from {from.Name} — {Beats.Count} beat(s)" +
                       (HasInitialImage ? "." : "; its opening image is gone, pick one."));
                if (Triggers.Trim().Length > 0 && !(BeatsText + StyleText).Contains("wushu", StringComparison.OrdinalIgnoreCase))
                    AddLog($"The trigger words \"{Triggers.Trim()}\" are put in front of every beat.");
            }
            catch (Exception ex)
            {
                AddLog($"📥 Import failed: {ex.Message}");
                _logger.LogError(ex, "Martial import failed");
            }
        }

        // ── The render ───────────────────────────────────────────────────────────────────────────

        protected override string? BeatWorkflowFile => null;
        protected override string VideoOutputNode => MartialGraph.VideoSaveNode;
        protected override string LastFrameOutputNode => MartialGraph.LastFrameSaveNode;

        private async Task<string> UploadAsync(string path, string what, CancellationToken ct)
        {
            if (_uploaded.TryGetValue(path, out var name)) return name;
            if (!File.Exists(path)) throw new FileNotFoundException($"{what} is missing: {path}");
            ProcessingStatus = $"Sending {what} to ComfyUI…";
            name = await _comfyUIService.UploadImageAsync(path, ct);
            if (string.IsNullOrEmpty(name)) throw new InvalidOperationException($"{what} could not be uploaded.");
            _uploaded[path] = name;
            return name;
        }

        protected override async Task PrepareRunAsync(CancellationToken ct)
        {
            for (var i = 0; i < References.Count; i++)
                await UploadAsync(References[i].Path, $"reference {i + 1} ({References[i].FileName})", ct);
            foreach (var g in Guides)
                await UploadAsync(g.Path, $"the keyframe {g.FileName}", ct);

            var missing = Loras.Where(l => l.On && l.IsMissing).Select(l => Path.GetFileName(l.Name)).ToList();
            if (missing.Count > 0)
                AddLog($"⚠ {string.Join(", ", missing)} not on the server — ComfyUI will refuse the graph. Untick the row(s) or download them.");
            var unlabeled = References.Count(r => string.IsNullOrWhiteSpace(r.Label));
            if (unlabeled > 0) AddLog($"Note: {unlabeled} reference(s) have no description — the prompt only says they are references.");
            var stray = Guides.Where(g => g.Beat < 1 || g.Beat > Beats.Count).ToList();
            if (stray.Count > 0) AddLog($"⚠ {stray.Count} keyframe(s) point at a beat that does not exist and are skipped.");
            AddLog($"Martial: {Loras.Count(l => l.On)} LoRA(s), {References.Count} reference(s), {Guides.Count} keyframe(s), " +
                   $"attention {SelectedAttention.Label}, {(PreviewOnly ? "draft (pass 1 only)" : RefineSummary)}");
        }

        /// <summary>What each picture is, in the order they are sent.</summary>
        private List<string> PictureRoles(int index, bool hasIdentityImage)
        {
            var roles = new List<string>
            {
                index == 0
                    ? "is the first frame of this shot: the video starts exactly on it and the action moves on from there."
                    : "is the first frame of this shot — the last moment of the shot before: the video starts exactly on it and the action carries straight on.",
            };
            if (hasIdentityImage) roles.Add("shows how the characters look; keep their faces, hair, build and clothing identical to it throughout.");
            foreach (var r in References.Take(MartialGraph.MaxPictures - roles.Count))
            {
                var label = r.Label.Trim().TrimEnd('.');
                roles.Add(label.Length == 0
                    ? "is a reference for the look of the scene; keep what it shows consistent."
                    : $"is {label}; keep it exactly as shown throughout the video.");
            }
            return roles;
        }

        protected override string ComposeBeatPrompt(FflfBeat beat, int index, int total, bool hasIdentityImage) =>
            MartialGraph.ComposePrompt(Triggers, beat.Text, ComposedStyle, PictureRoles(index, hasIdentityImage));

        protected override JsonObject BuildBeatGraph(string template, FflfBeatRender request, int index, out int pruned)
        {
            pruned = 0;
            var pictures = new List<string> { request.StartImage };
            if (!string.IsNullOrEmpty(request.IdentityImage)) pictures.Add(request.IdentityImage!);
            foreach (var r in References)
                if (pictures.Count < MartialGraph.MaxPictures && _uploaded.TryGetValue(r.Path, out var name)) pictures.Add(name);

            var frames = MartialGraph.FrameCount(request.Seconds);
            var keyframes = Guides
                .Where(g => g.Beat == index + 1 && _uploaded.ContainsKey(g.Path))
                .Select(g => new MartialKeyframe(_uploaded[g.Path], MartialGraph.KeyframeIndex(g.Seconds, frames)))
                .ToList();
            foreach (var k in keyframes) AddLog($"Beat {index + 1}: keyframe pinned at frame {k.Frame}.");

            var on = Loras.Where(l => l.On && l.Name.Trim().Length > 0 && l.Strength != 0).ToList();
            var render = new MartialBeatRender
            {
                Pictures = pictures,
                FirstFrame = request.LockFirstFrame ? request.StartImage : null,
                Keyframes = keyframes,
                Prompt = request.Prompt,
                Seconds = request.Seconds,
                Seed = request.Seed,
                AspectRatio = request.AspectRatio,
                FirstPassMegapixels = request.FirstPassMegapixels,
                FinalMegapixels = request.FinalMegapixels,
                Steps = request.FirstPassSteps,
                Draft = request.PreviewOnly,
                DiffusionModel = request.DiffusionModel,
                RefineModel = RefineModel,
                TextEncoder = TextEncoder,
                Loras = on.Select(l => new MartialLora(l.Name, l.Strength)).ToList(),
                RefineLoras = on.Where(l => l.OnRefine).Select(l => new MartialLora(l.Name, l.Strength)).ToList(),
                Attention = SelectedAttention?.Label ?? MartialGraph.DefaultAttention,
                MaxFidelityReferences = request.MaxFidelityReferences,
                RefineStrength = RefineStrength,
                RefineSteps = RefineSteps,
                RtxUpscale = RtxUpscale,
                RtxScale = RtxScale,
                OutputPrefix = request.OutputPrefix,
            };
            if (!render.MaxFidelityReferences && MartialGraph.FullSizeReferences(render, pictures.Count))
                AddLog($"Beat {index + 1}: {pictures.Count} pictures with the refine — references sent at full size (ref_image_size: max); " +
                       "scaled to the pass-1 canvas they break the upscaled finish.");
            return MartialGraph.Build(render);
        }

        /// <summary>The face refine runs on the refine pass's model and its LoRAs (the ones ticked for the
        /// refine), with the page's text encoder.</summary>
        protected override FaceRefineModels FaceRefineModelsFor(string template) => new()
        {
            DiffusionModel = string.IsNullOrWhiteSpace(RefineModel) ? DiffusionModel : RefineModel,
            TextEncoder = TextEncoder,
            VideoVae = MartialGraph.VideoVae,
            AudioVae = MartialGraph.AudioVae,
            Loras = Loras.Where(l => l.On && l.OnRefine && l.Name.Trim().Length > 0 && l.Strength != 0)
                         .Select(l => new FaceRefineLora(l.Name, l.Strength)).ToList(),
        };

        // ── Settings and stories ─────────────────────────────────────────────────────────────────

        private static readonly HashSet<string> OwnPersisted = new()
        {
            nameof(RefineModel), nameof(TextEncoder), nameof(Triggers), nameof(SelectedAttention), nameof(RefineStrength),
            nameof(RefineSteps), nameof(RtxUpscale), nameof(RtxScale), nameof(StackSummary),
        };

        protected override bool IsPersistedProperty(string name) =>
            base.IsPersistedProperty(name) || (OwnPersisted.Contains(name) && !_restoring);

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            switch (e.PropertyName)
            {
                case nameof(BeatSeconds):
                    OnPropertyChanged(nameof(FramesPerBeat));
                    OnPropertyChanged(nameof(CanvasSummary));
                    UpdateGuideHints();
                    break;
                case nameof(HasBeats):
                case nameof(BeatSummary):
                    UpdateGuideHints();
                    break;
            }
        }

        private void Read(string? refineModel, string? textEncoder, IEnumerable<H3MartialLoraRow>? loras, IEnumerable<H3MartialReference>? references,
                          IEnumerable<H3MartialGuide>? guides, string? triggers, string? attention, double refineStrength, int refineSteps,
                          bool rtx, double rtxScale)
        {
            _restoring = true;
            try
            {
                RefineModel = refineModel ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(RefineModel) && !RefineModelOptions.Contains(RefineModel)) RefineModelOptions.Add(RefineModel);
                TextEncoder = string.IsNullOrWhiteSpace(textEncoder) ? H3MartialSettings.DefaultTextEncoder : textEncoder!;
                OfferTextEncoder(TextEncoder);
                Triggers = triggers ?? H3MartialSettings.DefaultTriggers;
                SelectedAttention = AttentionOptionsList.FirstOrDefault(o => o.Label == attention) ?? AttentionOptionsList[0];
                RefineStrength = Math.Clamp(refineStrength <= 0 ? 0.6 : refineStrength, 0.1, 1.0);
                RefineSteps = Math.Clamp(refineSteps <= 0 ? 10 : refineSteps, 2, 40);
                RtxUpscale = rtx;
                RtxScale = Math.Clamp(rtxScale <= 0 ? 2.0 : rtxScale, 1.0, 4.0);

                Loras.Clear();
                if (loras == null)
                    foreach (var (name, strength) in MartialCatalog.AuthoredStack) AddLora(name, strength);
                else
                    foreach (var l in loras) AddLora(l.Name ?? string.Empty, Math.Clamp(l.Strength, -2, 2), l.On, l.OnRefine);
                foreach (var l in Loras) if (!string.IsNullOrWhiteSpace(l.Name) && !MartialLoraOptions.Contains(l.Name)) MartialLoraOptions.Add(l.Name);

                References.Clear();
                foreach (var r in (references ?? Enumerable.Empty<H3MartialReference>()).Where(r => File.Exists(r.Path)).Take(MaxReferences))
                    References.Add(new MartialReference(r.Path, r.Label ?? string.Empty));
                Guides.Clear();
                foreach (var g in (guides ?? Enumerable.Empty<H3MartialGuide>()).Where(g => File.Exists(g.Path)).Take(MaxGuides))
                    Guides.Add(new MartialGuide(g.Path, Math.Max(1, g.Beat), Math.Max(0, g.Seconds)));
                _uploaded.Clear();
            }
            finally { _restoring = false; }
            UpdateGuideHints();
            OnPropertyChanged(nameof(ReferenceSummary));
            OnPropertyChanged(nameof(GuideSummary));
            OnPropertyChanged(nameof(RefineSummary));
        }

        private List<H3MartialLoraRow> LoraRows() =>
            Loras.Select(l => new H3MartialLoraRow { On = l.On, Name = l.Name, Strength = l.Strength, OnRefine = l.OnRefine }).ToList();
        private List<H3MartialReference> ReferenceRows() =>
            References.Select(r => new H3MartialReference { Path = r.Path, Label = r.Label }).ToList();
        private List<H3MartialGuide> GuideRows() =>
            Guides.Select(g => new H3MartialGuide { Path = g.Path, Beat = g.Beat, Seconds = g.Seconds }).ToList();

        protected override void LoadTabSettings(H3FflfSettings settings)
        {
            if (settings is not H3MartialSettings s) return;
            Read(s.RefineModel, s.TextEncoder, s.Loras, s.References, s.Guides, s.Triggers, s.Attention, s.RefineStrength, s.RefineSteps, s.RtxUpscale, s.RtxScale);
        }

        protected override void SaveTabSettings(H3FflfSettings settings)
        {
            if (settings is not H3MartialSettings s) return;
            s.RefineModel = RefineModel;
            s.TextEncoder = TextEncoder;
            s.Loras = LoraRows();
            s.References = ReferenceRows();
            s.Guides = GuideRows();
            s.Triggers = Triggers;
            s.Attention = SelectedAttention?.Label ?? MartialGraph.DefaultAttention;
            s.RefineStrength = RefineStrength;
            s.RefineSteps = RefineSteps;
            s.RtxUpscale = RtxUpscale;
            s.RtxScale = RtxScale;
        }

        protected override void SnapshotTabStory(FflfStory story) => story.Martial = new FflfMartialStory
        {
            RefineModel = RefineModel,
            TextEncoder = TextEncoder,
            Loras = LoraRows(),
            References = ReferenceRows(),
            Guides = GuideRows(),
            Triggers = Triggers,
            Attention = SelectedAttention?.Label ?? MartialGraph.DefaultAttention,
            RefineStrength = RefineStrength,
            RefineSteps = RefineSteps,
            RtxUpscale = RtxUpscale,
            RtxScale = RtxScale,
        };

        protected override void ApplyTabStory(FflfStory story)
        {
            if (story.Martial is not { } m) return;
            Read(m.RefineModel, m.TextEncoder, m.Loras, m.References, m.Guides, m.Triggers, m.Attention, m.RefineStrength, m.RefineSteps, m.RtxUpscale, m.RtxScale);
        }

        /// <summary>📒 A recipe keeps the refine model, the LoRA stack and the refine dials — not the story's
        /// references or keyframes.</summary>
        protected override string SnapshotTabRecipe() => System.Text.Json.JsonSerializer.Serialize(new FflfMartialStory
        {
            RefineModel = RefineModel,
            TextEncoder = TextEncoder,
            Loras = LoraRows(),
            Triggers = Triggers,
            Attention = SelectedAttention?.Label ?? MartialGraph.DefaultAttention,
            RefineStrength = RefineStrength,
            RefineSteps = RefineSteps,
            RtxUpscale = RtxUpscale,
            RtxScale = RtxScale,
        });

        protected override void ApplyTabRecipe(string json)
        {
            if (System.Text.Json.JsonSerializer.Deserialize<FflfMartialStory>(json) is not { } m) return;
            Read(m.RefineModel, m.TextEncoder, m.Loras, ReferenceRows(), GuideRows(), m.Triggers, m.Attention, m.RefineStrength, m.RefineSteps, m.RtxUpscale, m.RtxScale);
            MartialChanged();
        }

        /// <summary>＋ New keeps the model and the LoRA stack — how the user likes fights rendered — and clears
        /// what belongs to the story: its references and keyframes.</summary>
        protected override void ClearTabStory()
        {
            References.Clear();
            Guides.Clear();
            _uploaded.Clear();
        }
    }
}
