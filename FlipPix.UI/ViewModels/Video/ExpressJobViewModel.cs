using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>What a stack is authored to load and sample at — what the composer moves to when the stack
    /// radio is changed. Supplied by ⚡ H3 Express, which knows the per-checkpoint step counts.</summary>
    public sealed record ExpressStackDefaults(string Model, int Steps, int MinSteps);

    /// <summary>
    /// The ➕ <b>New job</b> sheet on ⚡ H3 Express: a whole job — its folder of stories, its cast, its
    /// stack, its canvas and its prompt build — composed <i>beside</i> the run that is already going, and
    /// queued behind it.
    ///
    /// <para><b>Why this exists rather than a second set of controls on the rail.</b> The rail <i>is</i> the
    /// running job: the stack, the step count and the LoRA are read live as each clip's graph is built, so
    /// everything on it is frozen while anything renders. A job you can set up mid-run therefore has to be
    /// edited somewhere the render is not reading — here — and handed over as a snapshot
    /// (<see cref="ExpressJob"/>) that is written onto the rail only when its turn comes.</para>
    ///
    /// <para><b>It opens on a copy.</b> Cancel leaves the queue exactly as it was, and re-opening a queued
    /// job to change its mind is the same flow as making one.</para>
    /// </summary>
    public partial class ExpressJobViewModel : ObservableObject
    {
        private readonly IFileDialogService _files;
        private readonly Func<bool, bool, ExpressStackDefaults> _stackDefaults;
        private readonly Action<string> _log;
        private readonly string _pictureFolder;

        public ExpressJobViewModel(
            ExpressJob job,
            bool isNew,
            IReadOnlyList<DiffusionModelOption> models,
            IReadOnlyList<DiffusionModelOption> loras,
            IReadOnlyList<string> aspectRatios,
            IReadOnlyList<MegapixelOption> megapixels,
            IReadOnlyList<MegapixelOption> previewMegapixels,
            IReadOnlyList<int> upscaleSteps,
            IReadOnlyList<DiffusionModelOption> castPhotoEngines,
            IReadOnlyList<H3VisualStyle> visualStyles,
            double maxStoryDurationSeconds,
            Func<bool, bool, ExpressStackDefaults> stackDefaults,
            IFileDialogService files,
            Action<string> log,
            string pictureFolder)
        {
            Job = job;
            IsNew = isNew;
            DiffusionModelOptions = models;
            LoraOptions = loras;
            AspectRatioOptions = aspectRatios;
            MegapixelOptions = megapixels;
            PreviewMegapixelOptions = previewMegapixels;
            UpscaleStepOptions = upscaleSteps;
            CastPhotoEngineOptions = castPhotoEngines;
            VisualStyleOptions = visualStyles;
            MaxStoryDurationSeconds = maxStoryDurationSeconds;
            _stackDefaults = stackDefaults;
            _files = files;
            _log = log;
            _pictureFolder = pictureFolder;

            // The two cast cards are the tab's own ExpressCastMember, so the photo tile, the preview load and
            // the sex dropdown behave here exactly as they do on the rail — including the rule that a new
            // photo drops an outfit read from the old one.
            Cast1 = Member(1, job.Cast1Photo, job.Cast1Sex, job.Cast1Outfit, job.Cast1OutfitSource);
            Cast2 = Member(2, job.Cast2Photo, job.Cast2Sex, job.Cast2Outfit, job.Cast2OutfitSource);

            PickFolderCommand = new RelayCommand(async () => await PickFolderAsync());
            RescanCommand = new RelayCommand(() => Rescan(report: true), () => Job.Folder.Length > 0);
            RemoveStoryCommand = new RelayCommand<BatchStory>(s => { if (s != null) Job.Stories.Remove(s); });
            ClearStoriesCommand = new RelayCommand(() => Job.Stories.Clear(), () => Job.Stories.Count > 0);

            Job.Stories.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(StoriesSummary));
                OnPropertyChanged(nameof(CanQueue));
                RaiseFooter();
                ClearStoriesCommand.NotifyCanExecuteChanged();
            };
            Job.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ExpressJob.Folder))
                    RescanCommand.NotifyCanExecuteChanged();
            };
        }

        /// <summary>The working copy every control on the sheet writes into.</summary>
        public ExpressJob Job { get; }

        /// <summary>True for ➕ New job, false when an already-queued job was reopened.</summary>
        public bool IsNew { get; }

        public string WindowTitle => IsNew ? "Queue a new job — H3 Express" : "Edit queued job — H3 Express";

        public string HeadingText => IsNew ? "➕ New job" : "✎ Queued job";

        public string ConfirmText => IsNew ? "➕ Add to queue" : "💾 Save changes";

        /// <summary>Set by the window when ➕/💾 is pressed, read by the caller.</summary>
        public bool Confirmed { get; private set; }

        public void Confirm()
        {
            // The cards are the source of truth for the cast while the sheet is open.
            Job.Cast1Photo = Cast1.PhotoPath;
            Job.Cast1Sex = Cast1.Sex;
            Job.Cast1Outfit = Cast1.Outfit;
            Job.Cast1OutfitSource = Cast1.OutfitSource;
            Job.Cast2Photo = Cast2.PhotoPath;
            Job.Cast2Sex = Cast2.Sex;
            Job.Cast2Outfit = Cast2.Outfit;
            Job.Cast2OutfitSource = Cast2.OutfitSource;
            Confirmed = true;
        }

        // ── The lists the dropdowns are filled from, borrowed from the tab ──────────────────────────

        public IReadOnlyList<DiffusionModelOption> DiffusionModelOptions { get; }
        public IReadOnlyList<DiffusionModelOption> LoraOptions { get; }
        public IReadOnlyList<string> AspectRatioOptions { get; }
        public IReadOnlyList<MegapixelOption> MegapixelOptions { get; }
        public IReadOnlyList<MegapixelOption> PreviewMegapixelOptions { get; }
        public IReadOnlyList<int> UpscaleStepOptions { get; }
        public IReadOnlyList<DiffusionModelOption> CastPhotoEngineOptions { get; }
        public IReadOnlyList<H3VisualStyle> VisualStyleOptions { get; }
        public double MaxStoryDurationSeconds { get; }

        // ── The stories ─────────────────────────────────────────────────────────────────────────────

        public RelayCommand PickFolderCommand { get; }
        public RelayCommand RescanCommand { get; }
        public RelayCommand<BatchStory> RemoveStoryCommand { get; }
        public RelayCommand ClearStoriesCommand { get; }

        public ObservableCollection<BatchStory> Stories => Job.Stories;

        public string Folder
        {
            get => Job.Folder;
            set { Job.Folder = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(FolderText)); }
        }

        public string FolderText => Job.Folder.Length > 0 ? Job.Folder : "No folder chosen yet.";

        public string StoriesSummary => Stories.Count == 0
            ? "Nothing to render yet — choose a folder of story .txt files."
            : $"{Stories.Count} stor{(Stories.Count == 1 ? "y" : "ies")} in this job. Each one becomes its own film, " +
              "with this job's cast and these settings.";

        /// <summary>A job with no stories has nothing to do, so ➕ stays off until there is one.</summary>
        public bool CanQueue => Stories.Count > 0;

        /// <summary>
        /// The line along the bottom of the sheet: the whole job in one sentence, so what is about to be
        /// queued can be read without scrolling back up through both columns. Live — it follows the
        /// controls rather than the copy that is written on ➕.
        /// </summary>
        public string JobFooterLine
        {
            get
            {
                var cast = new[] { Cast1, Cast2 }.Count(m => m.PhotoPath.Length > 0);
                var who = cast == 0 ? "cast from each story"
                    : $"{cast} of your own, {(CastOwnClothes ? "their own clothes" : "the story's wardrobe")}";
                var stack = Job.UseTaoMate ? "TaoMate"
                    : Job.UseSingularity ? (SingularityErSde ? "Singularity er_sde" : "Singularity")
                    : "H3 Eros";
                return $"{Stories.Count} stor{(Stories.Count == 1 ? "y" : "ies")} · {stack} at {Steps} steps · " +
                       $"{Megapixels:0.##} MP · {StoryDurationSeconds:0}s films of {ClipLengthSeconds:0}s clips · {who}" +
                       (HasLora ? $" · LoRA at {LoraStrength:0.00}" : string.Empty);
            }
        }

        /// <summary>Every control that changes what the footer says goes through here.</summary>
        private void RaiseFooter() => OnPropertyChanged(nameof(JobFooterLine));

        private async Task PickFolderAsync()
        {
            var start = Directory.Exists(Job.Folder) ? Job.Folder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var folder = await _files.OpenFolderDialogAsync("Select a folder of story .txt files", start);
            if (string.IsNullOrWhiteSpace(folder)) return;
            Folder = folder;
            Rescan(report: true);
        }

        /// <summary>
        /// Reads the folder into the job's own list. Files already on it are kept as they are — re-reading
        /// after dropping two more in does not disturb the rest — and a story is never listed twice.
        /// </summary>
        private void Rescan(bool report)
        {
            var found = H3BatchViewModel.ScanStoryFiles(Job.Folder);
            var known = new HashSet<string>(Stories.Select(s => s.FilePath), StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var path in found)
            {
                if (!known.Add(path)) continue;
                Stories.Add(new BatchStory(path));
                added++;
            }

            if (!report) return;
            if (found.Count == 0) _log($"➕ New job: no .txt, .md or .text files in {Job.Folder}.");
            else _log($"➕ New job: {added} story file(s) added — {Stories.Count} in the job.");
        }

        // ── The cast ────────────────────────────────────────────────────────────────────────────────

        public ExpressCastMember Cast1 { get; }
        public ExpressCastMember Cast2 { get; }

        private ExpressCastMember Member(int index, string photo, string sex, string outfit, string source)
        {
            var m = new ExpressCastMember(index)
            {
                Sex = string.Equals(sex, ExpressCastMember.Female, StringComparison.OrdinalIgnoreCase)
                    ? ExpressCastMember.Female
                    : ExpressCastMember.Male,
                Outfit = outfit ?? string.Empty,
                OutfitSource = source ?? string.Empty,
            };
            m.PhotoPath = photo ?? string.Empty;
            m.BrowseCommand = new RelayCommand(async () => await BrowseAsync(m));
            m.ClearCommand = new RelayCommand(() => m.PhotoPath = string.Empty, () => m.PhotoPath.Length > 0);
            // The vision read is the tab's, and it needs the LLM and the run's cancellation. On this sheet the
            // outfit box is typed into instead; it is read for real when the job starts, exactly as an
            // unread outfit on the rail is.
            m.ReadOutfitCommand = new RelayCommand(() => { }, () => false);
            m.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(ExpressCastMember.HasPhoto):
                    case nameof(ExpressCastMember.PhotoPath):
                        m.ClearCommand.NotifyCanExecuteChanged();
                        OnPropertyChanged(nameof(CastSummary));
                RaiseFooter();
                        OnPropertyChanged(nameof(HasAnyCast));
                        break;
                    case nameof(ExpressCastMember.Sex):
                        OnPropertyChanged(nameof(CastSummary));
                RaiseFooter();
                        break;
                    case nameof(ExpressCastMember.Outfit):
                        // Typed by hand counts as this photo's outfit, as it does on the rail.
                        if (m.Outfit.Trim().Length > 0 && m.OutfitSource != m.PhotoPath)
                            m.OutfitSource = m.PhotoPath;
                        break;
                }
            };
            return m;
        }

        private async Task BrowseAsync(ExpressCastMember m)
        {
            var start = Directory.Exists(_pictureFolder)
                ? _pictureFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var path = await _files.OpenFileDialogAsync(
                $"Select a photo for character {m.Index}",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                start,
                persistKey: $"h3express.cast{m.Index}");
            if (path == null) return;
            m.PhotoPath = path;
        }

        public bool HasAnyCast => Cast1.PhotoPath.Length > 0 || Cast2.PhotoPath.Length > 0;

        public bool CastOwnClothes
        {
            get => Job.CastOwnClothes;
            set
            {
                if (Job.CastOwnClothes == value) return;
                Job.CastOwnClothes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CastStoryWardrobe));
                OnPropertyChanged(nameof(CastSummary));
                RaiseFooter();
            }
        }

        public bool CastStoryWardrobe
        {
            get => !Job.CastOwnClothes;
            set { if (value) CastOwnClothes = false; }
        }

        public string CastPhotoEngine
        {
            get => Job.CastPhotoEngine;
            set { if (value == null) return; Job.CastPhotoEngine = value; OnPropertyChanged(); }
        }

        public string CastSummary
        {
            get
            {
                var have = new[] { Cast1, Cast2 }.Where(m => m.PhotoPath.Length > 0).ToList();
                if (have.Count == 0)
                    return "Empty: every story in this job casts itself, and its characters are photographed " +
                           "from the story.";
                var who = have.Count == 2
                    ? $"Your {have[0].Noun} and {have[1].Noun} play characters 1 and 2"
                    : $"Your {have[0].Noun} plays character {have[0].Index}";
                return CastOwnClothes
                    ? $"{who} in every story of this job, in their own clothes. Leave an outfit box empty and it " +
                      "is read off the photo when the job starts."
                    : $"{who} in every story of this job, dressed in each story's own wardrobe.";
            }
        }

        // ── The stack ───────────────────────────────────────────────────────────────────────────────

        public bool StackIsEros
        {
            get => !Job.UseTaoMate && !Job.UseSingularity;
            set { if (value) SelectStack(taoMate: false, singularity: false); }
        }

        public bool StackIsSingularity
        {
            get => !Job.UseTaoMate && Job.UseSingularity;
            set { if (value) SelectStack(taoMate: false, singularity: true); }
        }

        public bool StackIsTaoMate
        {
            get => Job.UseTaoMate;
            set { if (value) SelectStack(taoMate: true, singularity: false); }
        }

        private void SelectStack(bool taoMate, bool singularity)
        {
            if (taoMate == Job.UseTaoMate && singularity == Job.UseSingularity) return;
            Job.UseTaoMate = taoMate;
            Job.UseSingularity = singularity;

            // The checkpoint is the stack, as it is on the rail — and the step count comes back with it,
            // this checkpoint's own if one has been saved for it.
            var d = _stackDefaults(taoMate, singularity);
            Job.DiffusionModel = d.Model;
            Job.Steps = Math.Clamp(d.Steps, d.MinSteps, MaxSteps);

            OnPropertyChanged(nameof(StackIsEros));
            OnPropertyChanged(nameof(StackIsSingularity));
            OnPropertyChanged(nameof(StackIsTaoMate));
            OnPropertyChanged(nameof(SelectedDiffusionModel));
            OnPropertyChanged(nameof(Steps));
            OnPropertyChanged(nameof(MinSteps));
            OnPropertyChanged(nameof(UsesDraftCanvas));
            OnPropertyChanged(nameof(StackSummary));
                RaiseFooter();
        }

        public const int MaxSteps = 30;

        public bool SingularityErSde
        {
            get => Job.SingularityErSde;
            set { Job.SingularityErSde = value; OnPropertyChanged(); OnPropertyChanged(nameof(StackSummary));
                RaiseFooter(); }
        }

        public string SelectedDiffusionModel
        {
            get => Job.DiffusionModel;
            set { if (value == null) return; Job.DiffusionModel = value; OnPropertyChanged(); }
        }

        public string SelectedLora
        {
            get => Job.Lora;
            set
            {
                Job.Lora = (value ?? string.Empty).Trim().Replace('\\', '/');
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLora));
                RaiseFooter();
            }
        }

        public bool HasLora => Job.Lora.Length > 0;

        public double LoraStrength
        {
            get => Job.LoraStrength;
            set { Job.LoraStrength = Math.Clamp(Math.Round(value, 2), 0, 2); OnPropertyChanged(); }
        }

        public int Steps
        {
            get => Job.Steps;
            set
            {
                var v = Math.Clamp(value, MinSteps, MaxSteps);
                if (Job.Steps == v) return;
                Job.Steps = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StackSummary));
                RaiseFooter();
            }
        }

        /// <summary>Seven on 🍥 TaoMate, which spends six of the schedule before the LoRA leg starts.</summary>
        public int MinSteps => _stackDefaults(Job.UseTaoMate, Job.UseSingularity).MinSteps;

        public bool UsesDraftCanvas => !Job.UseTaoMate;

        public string StackSummary => Job.UseTaoMate
            ? $"🍥 TaoMate — a {Steps}-step schedule split across two samplers, the last leg on the TaoMate " +
              "3-step LoRA, then an RTX ×2 frame upscale. No draft canvas: the Quality below is what the " +
              "model paints, and the file lands at twice it in each direction."
            : Job.UseSingularity
                ? $"✴️ Singularity — the Singularity ref2va checkpoint with the author's patches, " +
                  $"{(SingularityErSde ? "er_sde/beta" : "euler/simple")} at {Steps} steps, composed at the " +
                  "draft canvas and lifted to the Quality one."
                : $"🌹 H3 Eros — the 10Eros hybrid checkpoint, er_sde/beta at {Steps} steps, composed at the " +
                  "draft canvas and lifted to the Quality one.";

        // ── The canvas and the lengths ──────────────────────────────────────────────────────────────

        public string SelectedAspectRatio
        {
            get => Job.AspectRatio;
            set { if (value == null) return; Job.AspectRatio = value; OnPropertyChanged(); }
        }

        public double Megapixels
        {
            get => Job.Megapixels;
            set { Job.Megapixels = value; OnPropertyChanged(); RaiseFooter(); }
        }

        public double PreviewMegapixels
        {
            get => Job.PreviewMegapixels;
            set { Job.PreviewMegapixels = value; OnPropertyChanged(); }
        }

        public int UpscaleSteps
        {
            get => Job.UpscaleSteps;
            set { Job.UpscaleSteps = value; OnPropertyChanged(); }
        }

        public bool UseRife
        {
            get => Job.UseRife;
            set { Job.UseRife = value; OnPropertyChanged(); }
        }

        public double StoryDurationSeconds
        {
            get => Job.StoryDurationSeconds;
            set
            {
                var snapped = Math.Clamp(Math.Round(value / 5.0) * 5.0, 5, MaxStoryDurationSeconds);
                if (Math.Abs(Job.StoryDurationSeconds - snapped) < 0.001) return;
                Job.StoryDurationSeconds = snapped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ClipPlanSummary));
                RaiseFooter();
            }
        }

        public double ClipLengthSeconds
        {
            get => Job.ClipLengthSeconds;
            set
            {
                var snapped = Math.Clamp(Math.Round(value), 4, 15);
                if (Math.Abs(Job.ClipLengthSeconds - snapped) < 0.001) return;
                Job.ClipLengthSeconds = snapped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ClipPlanSummary));
                RaiseFooter();
            }
        }

        public string ClipPlanSummary
        {
            get
            {
                var clips = Math.Max(1, (int)Math.Ceiling(StoryDurationSeconds / Math.Max(4, ClipLengthSeconds) - 0.0001));
                return $"{clips} clip{(clips == 1 ? string.Empty : "s")} of ≈{ClipLengthSeconds:0}s per story, joined " +
                       $"into one film of about {StoryDurationSeconds:0}s.";
            }
        }

        // ── How the clips are written ───────────────────────────────────────────────────────────────

        public bool ResearchPrompts
        {
            get => Job.ResearchPrompts;
            set { Job.ResearchPrompts = value; OnPropertyChanged(); }
        }

        public bool SpecPrompts
        {
            get => Job.SpecPrompts;
            set { Job.SpecPrompts = value; OnPropertyChanged(); }
        }

        public bool ReuseSavedPrompts
        {
            get => Job.ReuseSavedPrompts;
            set { Job.ReuseSavedPrompts = value; OnPropertyChanged(); }
        }

        public H3VisualStyle? VisualStyle
        {
            get => VisualStyleOptions.FirstOrDefault(s => s.Name == Job.VisualStyle) ?? VisualStyleOptions.FirstOrDefault();
            set { if (value == null) return; Job.VisualStyle = value.Name; OnPropertyChanged(); }
        }
    }
}
