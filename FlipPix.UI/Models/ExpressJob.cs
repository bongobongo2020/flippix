using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Models
{
    /// <summary>Where one ⚡ H3 Express job is in the queue. Ordered as the card reads it, top to bottom.</summary>
    public enum ExpressJobState
    {
        Queued,
        Running,
        Done,
        Stopped
    }

    /// <summary>
    /// One <b>job</b> on ⚡ H3 Express: a folder of stories, the cast that plays them, and every dial the
    /// render reads — taken down as one frozen set so a second folder can be queued behind the first
    /// <i>with settings of its own</i> while the first is still rendering.
    ///
    /// <para><b>Why a snapshot and not a second view model.</b> The tab renders from its own live
    /// properties — the stack, the step count and the LoRA are read as each clip's graph is built, which is
    /// why the rail is frozen mid-run. A queued job therefore cannot be "the rail, later": it has to be a
    /// copy of every value, held apart from the running job, and written onto the rail at the moment its
    /// turn comes (<c>H3ExpressViewModel.ApplyJob</c>). Nothing here is read by the render path directly —
    /// by the time a clip is submitted these values <i>are</i> the rail.</para>
    ///
    /// <para><b>The stories are the same rows the list shows.</b> A job carries live <see cref="BatchStory"/>
    /// objects rather than paths, so its rows keep their state — waiting, processing, done, the film they
    /// produced — whether they are on screen or waiting their turn in the queue.</para>
    /// </summary>
    public partial class ExpressJob : ObservableObject
    {
        public ExpressJob()
        {
            Stories.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(StoryCount));
                OnPropertyChanged(nameof(StoriesLine));
                OnPropertyChanged(nameof(HasStories));
            };
        }

        /// <summary>Stable for the life of the job, so the list can find a row after a reorder.</summary>
        public string Id { get; } = Guid.NewGuid().ToString("N");

        // ── What it renders ─────────────────────────────────────────────────────────────────────────

        /// <summary>The folder its stories were scanned from. Empty for a job built only of saved stories.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Title))]
        [NotifyPropertyChangedFor(nameof(FolderLine))]
        private string _folder = string.Empty;

        /// <summary>The rows this job renders — the very objects the STORIES list shows while it is the
        /// current job.</summary>
        public ObservableCollection<BatchStory> Stories { get; } = new();

        public int StoryCount => Stories.Count;

        public bool HasStories => Stories.Count > 0;

        /// <summary>The job's name on the card: the folder's own leaf, or the first story's title.</summary>
        public string Title
        {
            get
            {
                var leaf = LeafOf(Folder);
                if (leaf.Length > 0) return leaf;
                return Stories.Count > 0 ? Stories[0].Title : "Saved stories";
            }
        }

        public string FolderLine => Folder.Length > 0 ? Folder : "saved stories — no folder";

        public string StoriesLine
        {
            get
            {
                if (Stories.Count == 0) return "no stories";
                var done = Stories.Count(s => s.IsDone);
                var failed = Stories.Count(s => s.IsFailed);
                var text = $"{Stories.Count} stor{(Stories.Count == 1 ? "y" : "ies")}";
                if (done > 0) text += $" · {done} done";
                if (failed > 0) text += $" · {failed} failed";
                return text;
            }
        }

        // ── The cast ────────────────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CastCount))]
        [NotifyPropertyChangedFor(nameof(CastLine))]
        [NotifyPropertyChangedFor(nameof(Tooltip))]
        private string _cast1Photo = string.Empty;
        [ObservableProperty] private string _cast1Sex = ExpressCastMember.Male;
        [ObservableProperty] private string _cast1Outfit = string.Empty;
        [ObservableProperty] private string _cast1OutfitSource = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CastCount))]
        [NotifyPropertyChangedFor(nameof(CastLine))]
        [NotifyPropertyChangedFor(nameof(Tooltip))]
        private string _cast2Photo = string.Empty;
        [ObservableProperty] private string _cast2Sex = ExpressCastMember.Female;
        [ObservableProperty] private string _cast2Outfit = string.Empty;
        [ObservableProperty] private string _cast2OutfitSource = string.Empty;

        /// <summary>On: the cast wear what their photos show. Off: each story's own saved wardrobe.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CastLine))]
        [NotifyPropertyChangedFor(nameof(Tooltip))]
        private bool _castOwnClothes;

        /// <summary>Which text-to-image graph photographs a character this job has no photo for.</summary>
        [ObservableProperty] private string _castPhotoEngine = "krea2spicy";

        public int CastCount =>
            (Cast1Photo.Length > 0 ? 1 : 0) + (Cast2Photo.Length > 0 ? 1 : 0);

        public string CastLine =>
            CastCount == 0
                ? "cast from each story"
                : $"{CastCount} of your own · {(CastOwnClothes ? "their own clothes" : "the story's wardrobe")}";

        // ── The stack ───────────────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StackLabel))]
        [NotifyPropertyChangedFor(nameof(RenderLine))]
        private bool _useTaoMate;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StackLabel))]
        [NotifyPropertyChangedFor(nameof(RenderLine))]
        private bool _useSingularity = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StackLabel))]
        [NotifyPropertyChangedFor(nameof(RenderLine))]
        private bool _singularityErSde;

        [ObservableProperty] private string _diffusionModel = string.Empty;

        /// <summary>The first pass's step count, as the slider reads it for this job's checkpoint.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RenderLine))]
        private int _steps = 10;

        [ObservableProperty] private string _lora = string.Empty;
        [ObservableProperty] private double _loraStrength = 1.0;

        public string StackLabel =>
            UseTaoMate ? "🍥 TaoMate"
            : UseSingularity ? (SingularityErSde ? "✴️ Singularity · er_sde" : "✴️ Singularity")
            : "🌹 H3 Eros";

        // ── Chained clips ───────────────────────────────────────────────────────────

        /// <summary>Whether this job's stories are rendered as one continuous shot — each clip after the
        /// first continuing from the tail of the one before it (H3 Motion Context). Kept per job, because the
        /// render reads it live as each clip's graph is built and it is therefore frozen on the rail while
        /// anything is rendering.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RenderLine))]
        [NotifyPropertyChangedFor(nameof(ChainLine))]
        [NotifyPropertyChangedFor(nameof(Tooltip))]
        private bool _chainClips = true;

        /// <summary>Also pin this job's finish (upscale) pass to the previous clip's finished tail.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ChainLine))]
        [NotifyPropertyChangedFor(nameof(Tooltip))]
        private bool _chainPinFinish = true;

        public string ChainLine =>
            !ChainClips
                ? "clips rendered on their own"
                : ChainPinFinish
                    ? "🔗 chained · upscale pinned too"
                    : "🔗 chained · draft pass only";

        // ── The canvas ──────────────────────────────────────────────────────────────────────────────

        /// <summary>The label, as the dropdown spells it. Only a placeholder: every job in the app is built
        /// by copying the page, which carries the real one.</summary>
        [ObservableProperty] private string _aspectRatio = "Auto (match image)";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RenderLine))]
        private double _megapixels = 1.0;

        [ObservableProperty] private double _previewMegapixels = 0.15;
        [ObservableProperty] private int _upscaleSteps = 4;
        [ObservableProperty] private bool _useRife = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LengthLine))]
        private double _storyDurationSeconds = 30;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LengthLine))]
        private double _clipLengthSeconds = 10;

        // ── How the clips are written ───────────────────────────────────────────────────────────────

        [ObservableProperty] private bool _researchPrompts = true;
        [ObservableProperty] private bool _specPrompts;
        [ObservableProperty] private bool _reuseSavedPrompts = true;
        [ObservableProperty] private string _visualStyle = string.Empty;

        // ── Where it is ─────────────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsQueued))]
        [NotifyPropertyChangedFor(nameof(IsRunning))]
        [NotifyPropertyChangedFor(nameof(IsFinished))]
        [NotifyPropertyChangedFor(nameof(StateText))]
        private ExpressJobState _state = ExpressJobState.Queued;

        /// <summary>1-based, set by the queue so the card can number the jobs as they will run.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Ordinal))]
        private int _position = 1;

        public bool IsQueued => State == ExpressJobState.Queued;
        public bool IsRunning => State == ExpressJobState.Running;
        public bool IsFinished => State is ExpressJobState.Done or ExpressJobState.Stopped;

        public string StateText => State switch
        {
            ExpressJobState.Running => "now rendering",
            ExpressJobState.Done => "finished",
            ExpressJobState.Stopped => "stopped",
            _ => "queued"
        };

        /// <summary>The circled number on the row — ①②③… up to ⑳, then a plain one.</summary>
        public string Ordinal =>
            Position is >= 1 and <= 20 ? ((char)('①' + Position - 1)).ToString() : Position.ToString();

        // ── The two lines under the name ────────────────────────────────────────────────────────────

        public string RenderLine =>
            $"{StackLabel} · {Steps} steps · {Megapixels:0.##} MP" +
            (ChainClips ? " · 🔗 chained" : string.Empty);

        public string LengthLine =>
            $"{StoryDurationSeconds:0}s films · {ClipLengthSeconds:0}s clips";

        /// <summary>Everything the job changes, in one line, for the row's tooltip.</summary>
        public string Tooltip =>
            $"{FolderLine}\n{StoriesLine}\n\nCast: {CastLine}\nStack: {StackLabel} at {Steps} steps\n" +
            $"Canvas: {Megapixels:0.##} MP, {AspectRatio}\n{LengthLine}\nClips: {ChainLine}" +
            (Lora.Length > 0 ? $"\nLoRA: {LabelOf(Lora)} at {LoraStrength:0.00}" : string.Empty);

        // ── The faces on the row ────────────────────────────────────────────────────────────────────

        [ObservableProperty] private BitmapImage? _cast1Preview;
        [ObservableProperty] private BitmapImage? _cast2Preview;

        /// <summary>
        /// Decodes the two cast thumbnails the row shows. Off the UI thread on purpose — a job may be built
        /// from photos on a mapped drive, and this is called while another job is rendering.
        /// </summary>
        public async Task LoadPreviewsAsync()
        {
            var one = Cast1Photo;
            var two = Cast2Photo;
            var (a, b) = await Task.Run(() => (Thumb(one), Thumb(two)));
            Cast1Preview = a;
            Cast2Preview = b;
        }

        private static BitmapImage? Thumb(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (!File.Exists(path)) return null;
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.DecodePixelWidth = 96;
                b.UriSource = new Uri(path, UriKind.Absolute);
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch
            {
                return null;
            }
        }

        // ── Copying ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Everything but the stories and the state — what "start the next job from this one's settings"
        /// means. The composer opens on a copy so a cancelled edit leaves the queued job alone.
        /// </summary>
        public ExpressJob CloneSettings()
        {
            var c = new ExpressJob
            {
                Folder = Folder,
                Cast1Photo = Cast1Photo,
                Cast1Sex = Cast1Sex,
                Cast1Outfit = Cast1Outfit,
                Cast1OutfitSource = Cast1OutfitSource,
                Cast2Photo = Cast2Photo,
                Cast2Sex = Cast2Sex,
                Cast2Outfit = Cast2Outfit,
                Cast2OutfitSource = Cast2OutfitSource,
                CastOwnClothes = CastOwnClothes,
                CastPhotoEngine = CastPhotoEngine,
                UseTaoMate = UseTaoMate,
                UseSingularity = UseSingularity,
                SingularityErSde = SingularityErSde,
                ChainClips = ChainClips,
                ChainPinFinish = ChainPinFinish,
                DiffusionModel = DiffusionModel,
                Steps = Steps,
                Lora = Lora,
                LoraStrength = LoraStrength,
                AspectRatio = AspectRatio,
                Megapixels = Megapixels,
                PreviewMegapixels = PreviewMegapixels,
                UpscaleSteps = UpscaleSteps,
                UseRife = UseRife,
                StoryDurationSeconds = StoryDurationSeconds,
                ClipLengthSeconds = ClipLengthSeconds,
                ResearchPrompts = ResearchPrompts,
                SpecPrompts = SpecPrompts,
                ReuseSavedPrompts = ReuseSavedPrompts,
                VisualStyle = VisualStyle,
            };
            return c;
        }

        /// <summary>A full copy, stories included — the rows themselves, so a re-queued job shares them.</summary>
        public ExpressJob Clone()
        {
            var c = CloneSettings();
            foreach (var s in Stories) c.Stories.Add(s);
            return c;
        }

        /// <summary>Writes another job's settings over this one's, leaving the stories and state alone —
        /// what the composer's OK does to the job it was opened on.</summary>
        public void TakeSettingsFrom(ExpressJob other)
        {
            Folder = other.Folder;
            Cast1Photo = other.Cast1Photo;
            Cast1Sex = other.Cast1Sex;
            Cast1Outfit = other.Cast1Outfit;
            Cast1OutfitSource = other.Cast1OutfitSource;
            Cast2Photo = other.Cast2Photo;
            Cast2Sex = other.Cast2Sex;
            Cast2Outfit = other.Cast2Outfit;
            Cast2OutfitSource = other.Cast2OutfitSource;
            CastOwnClothes = other.CastOwnClothes;
            CastPhotoEngine = other.CastPhotoEngine;
            UseTaoMate = other.UseTaoMate;
            UseSingularity = other.UseSingularity;
            SingularityErSde = other.SingularityErSde;
            ChainClips = other.ChainClips;
            ChainPinFinish = other.ChainPinFinish;
            DiffusionModel = other.DiffusionModel;
            Steps = other.Steps;
            Lora = other.Lora;
            LoraStrength = other.LoraStrength;
            AspectRatio = other.AspectRatio;
            Megapixels = other.Megapixels;
            PreviewMegapixels = other.PreviewMegapixels;
            UpscaleSteps = other.UpscaleSteps;
            UseRife = other.UseRife;
            StoryDurationSeconds = other.StoryDurationSeconds;
            ClipLengthSeconds = other.ClipLengthSeconds;
            ResearchPrompts = other.ResearchPrompts;
            SpecPrompts = other.SpecPrompts;
            ReuseSavedPrompts = other.ReuseSavedPrompts;
            VisualStyle = other.VisualStyle;
        }

        /// <summary>Puts every story back to waiting — what "run this job again" means.</summary>
        public void ResetStories()
        {
            foreach (var s in Stories) s.Reset();
            OnPropertyChanged(nameof(StoriesLine));
        }

        /// <summary>Tells the row to re-read what its stories say, after a run has moved them on.</summary>
        public void RefreshStoriesLine() => OnPropertyChanged(nameof(StoriesLine));

        private static string LeafOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try { return new DirectoryInfo(path.TrimEnd('\\', '/')).Name; }
            catch { return string.Empty; }
        }

        private static string LabelOf(string name)
        {
            var file = (name ?? string.Empty).Replace('\\', '/');
            var slash = file.LastIndexOf('/');
            if (slash >= 0) file = file[(slash + 1)..];
            var dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }
    }
}
