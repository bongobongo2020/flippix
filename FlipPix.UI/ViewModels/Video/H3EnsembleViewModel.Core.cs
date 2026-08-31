using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "H3 Ensemble" tab — the 🪪👥⚡ H3 Cast Hybrid pipeline widened from a two-hander to an
    /// <b>ensemble</b>: up to five characters and one photograph of the location they are in.
    ///
    /// <para><b>What is different.</b> H3 Cast Hybrid has two named character slots and a scene image the
    /// language model reads but the generator never sees. This tab has five interchangeable cast slots — fill
    /// any subset — and the sixth picture is the <b>set</b>: it is both what Analyze reads the setting off
    /// <i>and</i> a reference wired into the generator, so the place a five-hander walks around in is a place
    /// H3 has actually been shown rather than one described to it in words.</para>
    ///
    /// <para><b>The nine slots are the whole design constraint.</b>
    /// <c>MiniMaxH3ReferenceToVideo</c> takes nine reference images, and every one of them is encoded at the
    /// generation's full pixel area. Five characters at three panels each is fifteen — so the reference
    /// budget is no longer a preference but arithmetic, and <see cref="ReferenceBudget"/> defaults to
    /// <see cref="RefsAuto"/>, which divides what is left after the location and the opening keyframe between
    /// however many characters are loaded. At five characters that is one picture each, and the picture it
    /// keeps is the face close-up: it is the only panel that carries a likeness at the distances this tab
    /// renders at.</para>
    ///
    /// <para><b>Not every clip casts everybody</b>, and that is what makes an ensemble affordable. The
    /// language model writes each beat naming only the subjects actually in it; a character a clip never
    /// names has their whole wardrobe line, their subject definition and their photographs dropped from that
    /// clip — see <see cref="HybridCastPrompt"/>'s selective cast. A five-character story therefore renders
    /// as a chain of two- and three-handers, each with room in the slot budget for real likeness.</para>
    ///
    /// <para><b>Everything else is the Hybrid tab's.</b> The same six-section prompt with four sections
    /// written in code, the same wardrobe lock stamped identically into every clip, the same Qwen-Image-Edit
    /// character sheets cut into panels, the same storyboard pass that has H3 render each clip's opening
    /// frame before the clips are committed, the same loop guard on a chain that repeats itself, and the same
    /// <c>h3-cast-hybrid.json</c> graph — with the face-refine chain now cloned once per character in the
    /// clip rather than once for a second character.</para>
    ///
    /// <para>This file holds the tab's inputs: the cast, the location, the wardrobe and the settings.
    /// Analysis, the storyboard, the queue and the submit-time graph patches are in
    /// <c>H3EnsembleViewModel.Render.cs</c>.</para>
    /// </summary>
    public partial class H3EnsembleViewModel : VideoProcessingBaseViewModel
    {
        // Workflow/output names are virtual so the 🪪🎬 H3 Multi tab — this same machinery on the
        // MiniMax I2V turbo graph, see H3MultiViewModel — can substitute its own while inheriting everything else.
        /// <summary>The hybrid graph this tab renders through.</summary>
        protected virtual string WorkflowFileName => "workflow/video/h3-minimax/h3-cast-hybrid.json";
        private const string SheetWorkflowFileName = "workflow/image/qwen-edit/Qwen_Edit_2511_INT8_Convrot_WF.json";
        /// <summary>The ComfyUI output subfolder this tab's renders are written under.</summary>
        protected virtual string OutputSubfolder => "h3_ensemble";
        /// <summary>The folder under the output root this tab's finished files are copied to.</summary>
        protected virtual string OutputFolderName => "H3Ensemble";
        private const string SystemPromptFile = "h3-ensemble.md";
        private const string StorySystemPromptFile = "h3-ensemble_story.md";
        private const string SheetPromptFile = "h3-charsheet-2511.md";

        // ── Video node ids (locked from h3-minimax/h3-cast-hybrid.json) ────────────────────────
        private const string NodePrompt = "10";        // PrimitiveStringMultiline → the reference node
        private const string NodeResolution = "11";    // ResolutionSelector → canvas
        private const string NodeDuration = "12";      // PrimitiveFloat seconds → node 13
        private const string NodeFrames = "13";        // ComfyMathExpression seconds → frames (output slot 1)
        private const string NodeSeed = "14";          // RandomNoise noise_seed
        private const string NodeReference = "20";     // MiniMaxH3ReferenceToVideo
        private const string NodeScheduler = "22";     // BasicScheduler (steps — read, never written)
        private const string NodeBaseFrames = "30";    // VAEDecode — the render's own frames
        private const string NodeInterpolate = "33";   // FrameInterpolate (FILM ×2)
        private const string NodeRtxUpscale = "34";    // RTXVideoSuperResolution
        private const string NodeFps = "35";           // PrimitiveFloat — the render frame rate
        private const string NodeFpsDoubled = "36";    // ComfyMathExpression — fps × 2, for the interpolated mux
        private const string NodeCreateVideo = "37";   // CreateVideo — frames + audio → a video
        private const string NodeSaveVideo = "38";     // SaveVideo — the graph's only output
        private const string NodeRefImage1 = "40";     // LoadImage → ref_image_0

        // ── Face-refine pass node ids — the 100-block, cloned once per character ───────────────
        private const string NodeRefinePrompt = "15";     // PrimitiveStringMultiline — the cast-only prompt
        private const string NodeFaceTrack = "100";       // H3FaceTrackCrop — tracks and crops one face
        private const string NodeRefineReference = "101"; // MiniMaxH3ReferenceToVideo (face-crop canvas)
        private const string NodeAudioLock = "103";       // MiniMaxH3NativeAudioLock — stage-1 audio
        private const string NodeRefineDenoise = "106";   // BasicScheduler of the refine pass (denoise)
        private const string NodeRefineSeed = "108";      // RandomNoise of the refine pass
        private const string NodeFaceStitch = "111";      // H3FaceStitch — refined crops back into the frames

        /// <summary>Node ids of the refine block, in the order they are cloned. Pass <c>k</c> ≥ 2 lives in
        /// the <c>100·k</c> block: 200–211, 300–311, … See <c>AddRefinePass</c>.</summary>
        private const int RefineBlockFirst = 100;
        private const int RefineBlockLast = 111;

        /// <summary>The autogrow input the reference node collects its images from.</summary>
        private const string RefImagePrefix = "ref_images.ref_image_";

        /// <summary>Ids for the injected <c>LoadImage</c> nodes, one per reference beyond the first. Well
        /// clear of every id the workflow uses, including the cloned refine blocks (which top out at 515).</summary>
        private const int ReferenceNodeIdBase = 900;

        /// <summary>Ids for the storyboard pass's frame pickers and image sinks.</summary>
        private const int StillPickIdBase = 800;
        private const int StillSaveIdBase = 810;

        /// <summary><c>MiniMaxH3ReferenceToVideo</c>'s autogrow cap — nine <c>ref_image_N</c> slots, shared
        /// between the keyframes, every cast panel and the location. This number is why this tab has a
        /// reference budget rather than a preference.</summary>
        protected const int MaxReferenceImages = 9;

        /// <summary>How many character slots the tab offers. Five plus the location is six pictures before a
        /// single panel is doubled up, which already spends two thirds of the nine slots.</summary>
        public const int MaxCharacters = 5;

        // ── Sheet node ids (locked from image/qwen-edit/Qwen_Edit_2511_INT8_Convrot_WF.json) ───
        private const string SheetLoadImage = "78";
        private const string SheetPositive = "115:111";
        private const string SheetSampler = "115:3";
        private const string SheetLatent = "115:112";
        private const string SheetSave = "60";

        private const int SheetWidth = 1536;
        private const int SheetHeight = 864;

        /// <summary>H3 renders at 24 fps and the duration maths is built on it.</summary>
        protected const int OutputFrameRate = 24;

        /// <summary>FILM's multiplier — node 33's <c>multiplier</c> and node 36's expression.</summary>
        private const int InterpolationFactor = 2;

        /// <summary>RTX Video Super Resolution factor — node 34's scale.</summary>
        private const double RtxScale = 2.0;

        /// <summary>Where a whole-clip frame stack starts being the thing that fails, in gigabytes.</summary>
        protected const double HeavyFrameStackGb = 8.0;

        // ── Cast ───────────────────────────────────────────────────────────────
        private readonly ObservableCollection<CharacterSlot> _cast = new();

        // ── Keyframes ──────────────────────────────────────────────────────────
        private readonly ObservableCollection<KeyframeSlot> _keyframes = new();

        // ── Storyboard: the keyframes H3 renders for itself ────────────────────
        private readonly ObservableCollection<StoryboardShot> _storyboard = new();
        private bool _isStoryboarding;
        private string _storyboardPhase = string.Empty;
        private int _storyboardFrames = 39;
        private CancellationTokenSource? _storyboardCts;

        // ── Location / prompt state ────────────────────────────────────────────
        private string _environmentPath = string.Empty;
        private BitmapImage? _environmentPreview;
        private string _environmentInfo = string.Empty;
        private bool _wireEnvironment = true;
        private string _prompt = string.Empty;
        private int _promptClipCount;
        private string _castWardrobe = string.Empty;
        private bool _isWardrobeLocked = true;
        private bool _wardrobeIsManual;
        private string _wardrobeStoryStamp = string.Empty;
        private string _wardrobeCastStamp = string.Empty;
        private CancellationTokenSource? _wardrobeCts;
        private bool _isDerivingWardrobe;
        private string _storyText = string.Empty;
        private string _storyFileName = string.Empty;
        private double _storyDurationSeconds = 24;
        private H3VisualStyle _visualStyle = H3VisualStyles.Auto;
        private string _selectedAspectRatio = H3Canvas.AutoAspect;
        private string _selectedMedium = "live-action and cinematic";
        private double _megapixels = 1.0;
        private double _lengthSeconds = 8;
        private long _seed = -1;
        private bool _isAnalyzing;
        private bool _faceRefine = true;
        private double _refineDenoise = 0.35;
        private bool _interpolate = true;
        private bool _rtxUpscale;
        private bool _isBuildingSheets;
        private string _sheetPhase = string.Empty;
        private string _analyzePhase = string.Empty;
        private DateTime _analyzeStarted;
        private readonly DispatcherTimer _analyzeClock = new() { Interval = TimeSpan.FromSeconds(1) };

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _analyzeCts;
        private CancellationTokenSource? _sheetCts;

        /// <summary>path → ComfyUI input-folder filename; each file is uploaded once per session.</summary>
        private readonly Dictionary<string, string> _uploadCache = new(StringComparer.OrdinalIgnoreCase);

        // ── Queue ──────────────────────────────────────────────────────────────
        private readonly ObservableCollection<H3EnsembleQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;
        private bool _isProcessingQueue;
        private string _queueStatus = string.Empty;

        /// <summary>Where this tab's queue is persisted. Per-tab so a Multi queue never loads as an
        /// Ensemble one — the items serialize identically but render on different graphs.</summary>
        protected virtual string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h3ensemble_queue.json");

        /// <summary>This tab's name in logs, message-box captions and the queue's lease — what the H3 Multi
        /// tab overrides along with its graph so its runs read as its own.</summary>
        protected virtual string TabLogName => "H3 Ensemble";

        public H3EnsembleViewModel(
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

            _analyzeClock.Tick += (_, _) => OnPropertyChanged(nameof(AnalyzeBusyText));

            for (var i = 1; i <= MaxCharacters; i++)
                _cast.Add(new CharacterSlot(i, LoadImagePreview, OnCharacterChanged));

            SelectCharacterCommand = new RelayCommand<CharacterSlot>(async slot => await PickCharacterAsync(slot));
            ClearCharacterCommand = new RelayCommand<CharacterSlot>(ClearCharacter);
            GenerateCastZimageLoraCommand = new RelayCommand<CastPhotoWorkflows.CastLora>(
                async lora => await GenerateCastPhotoAsync(CastPhotoMenuSlot, "zimage", lora),
                _ => CanGenerateCastPhoto(CastPhotoMenuSlot));
            GenerateCastFamegridLoraCommand = new RelayCommand<CastPhotoWorkflows.CastLora>(
                async lora => await GenerateCastPhotoAsync(CastPhotoMenuSlot, "famegrid", lora),
                _ => CanGenerateCastPhoto(CastPhotoMenuSlot));
            GenerateCastKrea2LoraCommand = new RelayCommand<CastPhotoWorkflows.CastLora>(
                async lora => await GenerateCastPhotoAsync(CastPhotoMenuSlot, "krea2", lora),
                _ => CanGenerateCastPhoto(CastPhotoMenuSlot));
            GenerateCastQwenCommand = new RelayCommand<CharacterSlot>(
                async slot => await GenerateCastPhotoAsync(slot, "qwen"), slot => CanGenerateCastPhoto(slot));
            GenerateCastKrea2SpicyCommand = new RelayCommand<CharacterSlot>(
                async slot => await GenerateCastPhotoAsync(slot, "krea2spicy"), slot => CanGenerateCastPhoto(slot));
            SelectEnvironmentCommand = new RelayCommand(async () => await SelectEnvironmentAsync());
            ClearEnvironmentCommand = new RelayCommand(() => EnvironmentPath = string.Empty, () => HasEnvironment);
            AddKeyframeCommand = new RelayCommand(async () => await AddKeyframesAsync(), () => CanAddKeyframe);
            RemoveKeyframeCommand = new RelayCommand<KeyframeSlot>(RemoveKeyframe);
            ClearKeyframesCommand = new RelayCommand(ClearKeyframes, () => HasKeyframes);
            SpreadKeyframesCommand = new RelayCommand(SpreadKeyframes, () => HasKeyframes);
            PreviewKeyframesCommand = new RelayCommand(async () => await BuildStoryboardAsync(), () => CanPreviewKeyframes);
            RerollShotCommand = new RelayCommand<StoryboardShot>(async shot => await RerollShotAsync(shot));
            NextCandidateCommand = new RelayCommand<StoryboardShot>(shot => shot?.Next());
            PrevCandidateCommand = new RelayCommand<StoryboardShot>(shot => shot?.Previous());
            ClearStoryboardCommand = new RelayCommand(ClearStoryboard, () => HasStoryboard);
            LoadStoryCommand = new RelayCommand(async () => await LoadStoryFileAsync());
            ClearStoryCommand = new RelayCommand(() => StoryText = string.Empty, () => HasStoryText);
            DeriveWardrobeCommand = new RelayCommand(async () => await RederiveWardrobeAsync(), () => CanAnalyze);
            ClearWardrobeCommand = new RelayCommand(ClearWardrobe, () => HasCastWardrobe);
            ToggleWardrobeLockCommand = new RelayCommand(() => IsWardrobeLocked = !IsWardrobeLocked);
            BuildSheetsCommand = new RelayCommand(async () => await BuildSheetsAsync(), () => CanBuildSheets);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            RestampCommand = new RelayCommand(Restamp, () => HasPrompt);
            GenerateCommand = new RelayCommand(AddToQueue, () => CanGenerate);
            CancelCommand = new RelayCommand(CancelEverything, () => IsProcessingQueue || IsProcessing || IsBuildingSheets);
            RemoveQueueItemCommand = new RelayCommand<H3EnsembleQueueItem>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => HasQueueItems);
            StartQueueCommand = new RelayCommand(() => _ = ProcessQueueAsync(), () => HasPendingItems && !IsProcessingQueue);
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(ReprocessAllFailed, () => HasFailedItems);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            RandomSeedCommand = new RelayCommand(() => Seed = System.Random.Shared.NextInt64(0, long.MaxValue));

            _queue.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
            };
            _keyframes.CollectionChanged += (_, _) => OnKeyframesChanged();
            _storyboard.CollectionChanged += (_, _) => OnStoryboardChanged();

            AddLog("H3 Ensemble initialized");
            ScheduleQueueLoad();
        }

        #region Commands

        /// <summary>Browses for one cast slot's photo. Takes the slot as its parameter because the five cards
        /// are an <c>ItemsControl</c>, not five hand-written panels.</summary>
        public RelayCommand<CharacterSlot> SelectCharacterCommand { get; }
        public RelayCommand<CharacterSlot> ClearCharacterCommand { get; }
        /// <summary>Renders this card's character photo with the Image Generator's Z-Image base
        /// workflow and the LoRA picked in the ✨ menu (or the workflow's own when "as authored" is
        /// picked). Runs for the card whose menu was last opened — see <see cref="CastPhotoMenuSlot"/>.</summary>
        public RelayCommand<CastPhotoWorkflows.CastLora> GenerateCastZimageLoraCommand { get; }
        public RelayCommand<CastPhotoWorkflows.CastLora> GenerateCastFamegridLoraCommand { get; }
        public RelayCommand<CastPhotoWorkflows.CastLora> GenerateCastKrea2LoraCommand { get; }
        public RelayCommand<CharacterSlot> GenerateCastQwenCommand { get; }
        /// <summary>The ✨ menu's Krea2-Spicy entry — the famegrid spicy selfie workflow, its
        /// LoRAs baked in, no picking. Takes the card directly, like Qwen.</summary>
        public RelayCommand<CharacterSlot> GenerateCastKrea2SpicyCommand { get; }
        public RelayCommand SelectEnvironmentCommand { get; }
        public RelayCommand ClearEnvironmentCommand { get; }
        public RelayCommand AddKeyframeCommand { get; }
        public RelayCommand<KeyframeSlot> RemoveKeyframeCommand { get; }
        public RelayCommand ClearKeyframesCommand { get; }
        public RelayCommand SpreadKeyframesCommand { get; }
        /// <summary>Renders one H3 still per clip in the prompt box — the opening frame of each, to look at
        /// before any of them is committed to a full render.</summary>
        public RelayCommand PreviewKeyframesCommand { get; }
        public RelayCommand<StoryboardShot> RerollShotCommand { get; }
        public RelayCommand<StoryboardShot> NextCandidateCommand { get; }
        public RelayCommand<StoryboardShot> PrevCandidateCommand { get; }
        public RelayCommand ClearStoryboardCommand { get; }
        public RelayCommand LoadStoryCommand { get; }
        public RelayCommand ClearStoryCommand { get; }
        public RelayCommand DeriveWardrobeCommand { get; }
        public RelayCommand ClearWardrobeCommand { get; }
        public RelayCommand ToggleWardrobeLockCommand { get; }
        public RelayCommand BuildSheetsCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        /// <summary>Re-assembles the prompt in the box against the current keyframes, cast, location and
        /// wardrobe — without asking the llama-server again.</summary>
        public RelayCommand RestampCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand<H3EnsembleQueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StartQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand RandomSeedCommand { get; }

        #endregion

        #region The cast

        /// <summary>All five slots, always — the empty ones are cards waiting to be filled, and an ensemble
        /// tab that hides them is one where adding a fourth character is a hunt for a button.</summary>
        public ObservableCollection<CharacterSlot> Cast => _cast;

        /// <summary>The slots with a photo in them, in slot order. Slot order <b>is</b> wiring order, and
        /// therefore picture order — so the cards read top to bottom the way the prompt numbers them.</summary>
        private IReadOnlyList<CharacterSlot> LoadedCharacters =>
            _cast.Where(c => c.HasSource).ToList();

        public int LoadedCharacterCount => LoadedCharacters.Count;
        public bool HasAnyCharacter => LoadedCharacterCount > 0;

        public bool AllSheetsReady => HasAnyCharacter && LoadedCharacters.All(c => c.HasSheet);

        /// <summary>How many reference slots the whole cast occupies at the current budget.</summary>
        private int CastPanelCount => LoadedCharacters.Sum(c => ReferencePlanFor(c).Count);

        /// <summary>The cast as <see cref="HybridCastPrompt"/> wants it — subject index, the word for them,
        /// what each of the pictures actually sent for them shows, and whether they are a person at all.</summary>
        protected IReadOnlyList<HybridCastPrompt.CastMember> CastMembers =>
            LoadedCharacters.Select(c => new HybridCastPrompt.CastMember(
                c.Index, c.Noun, ReferencePlanFor(c).Views, c.IsPerson, c.Descriptor, c.IsGroup)).ToList();

        /// <summary>The loaded characters who are not people — a cloud, a mountain, a herd. They change what
        /// the sheet builder is asked for, what the costume supervisor is asked for, and whether a
        /// face-refine pass runs at all.</summary>
        private IReadOnlyList<CharacterSlot> NonPersonCast =>
            LoadedCharacters.Where(c => !c.IsPerson).ToList();

        /// <summary>The loaded characters a face-refine pass can actually track.</summary>
        private IReadOnlyList<CharacterSlot> FaceCast =>
            LoadedCharacters.Where(c => c.HasFace).ToList();

        /// <summary>
        /// Every slot the user has said something about — a photo, a part, or a non-person kind. Wider than
        /// <see cref="LoadedCharacters"/> on purpose: the wardrobe is derived from the story long before
        /// anybody browses for pictures, which is what lets the character sheets be built already dressed.
        /// </summary>
        private IReadOnlyList<CharacterSlot> CastToDress => _cast.Where(c => c.IsCast).ToList();

        /// <summary>The characters that have been described but have no photo yet, and so cannot reach the
        /// video at all until one is loaded and a sheet is built.</summary>
        private IReadOnlyList<CharacterSlot> CastWithoutPhotos =>
            _cast.Where(c => c.IsCast && !c.HasSource).ToList();

        private void ClearCharacter(CharacterSlot? slot)
        {
            if (slot == null) return;
            slot.Clear();
            AddLog($"Character {slot.Index} cleared.");
        }

        private async System.Threading.Tasks.Task PickCharacterAsync(CharacterSlot? slot)
        {
            if (slot == null) return;
            var path = await PickImageAsync($"Select Character {slot.Index}", $"h3ensemble.char{slot.Index}");
            if (path == null) return;
            slot.SourcePath = path;
            AddLog($"Character {slot.Index}: {Path.GetFileName(path)}");
        }

        private void OnCharacterChanged()
        {
            OnPropertyChanged(nameof(LoadedCharacterCount));
            OnPropertyChanged(nameof(HasAnyCharacter));
            OnPropertyChanged(nameof(AllSheetsReady));
            OnPropertyChanged(nameof(CastSummary));
            OnPropertyChanged(nameof(PicturePlanSummary));
            OnPropertyChanged(nameof(ReferenceBudgetSummary));
            OnPropertyChanged(nameof(RefineSummary));
            OnPropertyChanged(nameof(CanAddKeyframe));
            OnPropertyChanged(nameof(BuildSheetsButtonText));
            OnPropertyChanged(nameof(SheetsShowWardrobe));
            OnPropertyChanged(nameof(WardrobeSummary));
            OnCanExecuteChanged();
            ScheduleWardrobeDerive();
        }

        public string CastSummary
        {
            get
            {
                var loaded = LoadedCharacters;
                if (loaded.Count == 0)
                    return $"No cast yet. Load a photo into any of the {MaxCharacters} slots — they are " +
                           "interchangeable, and the ones you leave empty cost nothing.";

                var missing = loaded.Count(c => !c.HasSheet);
                var who = $"{loaded.Count} character{(loaded.Count == 1 ? "" : "s")} " +
                          $"(<Subject {string.Join(">, <Subject ", loaded.Select(c => c.Index))}>)";
                // The Part field is optional for a person and load-bearing for anything else: nothing in the
                // app knows a slot is a cloud rather than a mountain unless it says so.
                var unnamed = NonPersonCast.Where(c => !c.HasRole).Select(c => c.Index).ToList();
                var partWarning = unnamed.Count == 0
                    ? string.Empty
                    : $" ⚠ Character {string.Join(", ", unnamed)} {(unnamed.Count == 1 ? "is" : "are")} not a " +
                      "person and no Part is filled in, so nothing can say what they are — write it in " +
                      "(\"Nimbus, a fluffy little cloud\") before building sheets.";
                if (missing == 0)
                    return $"{who} — sheets ready; H3 sees the sheets, not the photos.{partWarning}";

                var queued = IsProcessing || IsProcessingQueue
                    ? " A render is in flight, so the build waits for the GPU and starts when that job finishes."
                    : string.Empty;
                return $"{who} — {missing} sheet(s) still to build. Build Sheets runs Qwen-Image-Edit-2511 " +
                       $"once per character.{queued}";
            }
        }

        #endregion

        #region Reference budget — nine slots, divided

        /// <summary>Divide what is left after the location and the opening keyframe between the cast.</summary>
        public const int RefsAuto = -1;

        /// <summary>Every panel the sheet was cut into.</summary>
        public const int RefsAllPanels = 0;

        /// <summary>The face close-up and the front full body.</summary>
        public const int RefsFrontAndFace = 1;

        /// <summary>The face close-up alone.</summary>
        public const int RefsFaceOnly = 2;

        private int _referenceBudget = RefsAuto;

        public IReadOnlyList<ReferenceBudgetOption> ReferenceBudgetOptions { get; } = new[]
        {
            new ReferenceBudgetOption(RefsAuto, "Auto — fit the cast into the free slots"),
            new ReferenceBudgetOption(RefsFaceOnly, "Closest view only (1 per character)"),
            new ReferenceBudgetOption(RefsFrontAndFace, "Front + closest view (2 per character)"),
            new ReferenceBudgetOption(RefsAllPanels, "Every panel (3 per character)"),
        };

        /// <summary>
        /// How many of each character's panels are handed to H3.
        ///
        /// <para>Defaults to <see cref="RefsAuto"/>, which is the only setting that scales: nine slots, minus
        /// one for the location and one for the clip's opening keyframe, divided by however many characters
        /// are loaded. Two characters get three panels each; five get one — and the one they get is the face
        /// close-up, because a full-body back view costs exactly as much encoding as the face while carrying
        /// almost none of the likeness.</para>
        /// </summary>
        public int ReferenceBudget
        {
            get => _referenceBudget;
            set
            {
                if (_referenceBudget == value) return;
                _referenceBudget = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PicturePlanSummary));
                OnPropertyChanged(nameof(ReferenceBudgetSummary));
                OnPropertyChanged(nameof(CanAddKeyframe));
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Panels per character under <see cref="RefsAuto"/>: the free slots divided by the cast, floored at
        /// one and capped at the three a sheet has.
        ///
        /// <para>One slot is reserved for the location when one is wired, and the clip-1 keyframe count is
        /// held back as well — clip 1 is the worst case, since a hand-placed timeline can only live there.
        /// Neither of those depends on the budget, so there is no circularity here.</para>
        /// </summary>
        private int AutoPanelsPerCharacter
        {
            get
            {
                var cast = LoadedCharacters.Count;
                if (cast == 0) return 2;
                var free = MaxReferenceImages - (WiresEnvironment ? 1 : 0) - KeyframesForClip(1).Count;
                return Math.Clamp(free / cast, 1, 3);
            }
        }

        public string ReferenceBudgetSummary
        {
            get
            {
                var loaded = LoadedCharacters;
                if (loaded.Count == 0) return string.Empty;
                var auto = ReferenceBudget == RefsAuto
                    ? $"Auto → {AutoPanelsPerCharacter} panel(s) each. "
                    : string.Empty;
                return $"{auto}H3 receives {CastPanelCount} cast reference(s): " +
                       string.Join("; ", loaded.Select(c =>
                           $"<Subject {c.Index}> as {string.Join(" + ", ReferencePlanFor(c).Views)}"));
            }
        }

        /// <summary>
        /// Which of a character's panels are sent, and what each one shows.
        ///
        /// <para>Only a canonical three-panel sheet (front, back, face — what Build Sheets produces) can be
        /// trimmed: with any other split there is no way to know which piece holds the face, so everything
        /// goes and the views are described positionally.</para>
        /// </summary>
        private ReferencePlan ReferencePlanFor(CharacterSlot slot)
        {
            var panels = Math.Max(1, slot.PanelCount);
            // Per character, not per tab: a cast can hold two people and a cloud, and the cloud's third
            // panel is a detail shot rather than a face.
            var views = HybridCastPrompt.DefaultViews(panels, slot.IsPerson, slot.IsGroup);

            var budget = ReferenceBudget == RefsAuto
                ? AutoPanelsPerCharacter switch
                {
                    1 => RefsFaceOnly,
                    2 => RefsFrontAndFace,
                    _ => RefsAllPanels,
                }
                : ReferenceBudget;

            if (panels != 3 || budget == RefsAllPanels)
                return new ReferencePlan(Enumerable.Range(0, panels).ToList(), views);

            var indices = budget == RefsFaceOnly ? new[] { 2 } : new[] { 0, 2 };
            return new ReferencePlan(indices, indices.Select(i => views[i]).ToList());
        }

        /// <summary>Which panels of a character are uploaded, and what each of them shows.</summary>
        private sealed record ReferencePlan(IReadOnlyList<int> Indices, IReadOnlyList<string> Views)
        {
            public int Count => Indices.Count;
        }

        /// <summary>
        /// The whole picture plan in one line — which numbers are frame locks, which are the cast and which
        /// is the set. Getting that order wrong is the one mistake on this tab that renders a studio
        /// photograph as a shot.
        /// </summary>
        public string PicturePlanSummary
        {
            get
            {
                var loaded = LoadedCharacters;
                if (loaded.Count == 0) return string.Empty;

                // Clip 1's plan: the hand-placed timeline lives there, and a storyboard still — when one is
                // ticked — sits ahead of it as that clip's opening lock.
                var keys = KeyframesForClip(1).Count;
                var total = keys + CastPanelCount + (WiresEnvironment ? 1 : 0);

                var sb = new StringBuilder($"{total} reference image(s): ");
                sb.Append(keys == 0
                    ? "no keyframe locks; "
                    : $"<Picture 1>–<Picture {keys}> are the keyframe locks; ");

                var n = keys;
                foreach (var slot in loaded)
                {
                    var count = ReferencePlanFor(slot).Count;
                    sb.Append($"<Subject {slot.Index}> is ")
                      .Append(count == 1
                          ? $"<Picture {n + 1}>"
                          : $"<Picture {n + 1}>–<Picture {n + count}>")
                      .Append($" ({string.Join(" + ", ReferencePlanFor(slot).Views)}); ");
                    n += count;
                }

                if (WiresEnvironment) sb.Append($"<Picture {total}> is the location; ");
                sb.Append("the cast and the location are references and are told, in the prompt, never to " +
                          "become frames.");

                if (total > MaxReferenceImages)
                    sb.Append($" ⚠ That is more than the {MaxReferenceImages} slots " +
                              "MiniMaxH3ReferenceToVideo has — switch References to Auto, drop a keyframe, or " +
                              "take a character out.");
                return sb.ToString();
            }
        }

        #endregion

        #region The location

        /// <summary>
        /// The photograph of the set. Unlike the 🪪👥⚡ H3 Cast Hybrid tab's scene image, this one has
        /// <b>two</b> jobs: the llama-server reads the setting, lighting, art style and wardrobe off it, and
        /// — when <see cref="WireEnvironment"/> is on — it is also uploaded and wired as the last reference
        /// picture, so H3 is shown the place rather than only told about it.
        ///
        /// <para>It is a reference, never a frame. The code-written sections say so in three places, and say
        /// as well that anybody visible in it is scenery: a location photograph with people in it is
        /// otherwise an invitation to cast them.</para>
        /// </summary>
        public string EnvironmentPath
        {
            get => _environmentPath;
            set
            {
                if (_environmentPath == value) return;
                _environmentPath = value;
                _environmentPreview = LoadImagePreview(value, out _environmentInfo);
                OnPropertyChanged();
                OnPropertyChanged(nameof(EnvironmentPreview));
                OnPropertyChanged(nameof(EnvironmentInfo));
                OnPropertyChanged(nameof(HasEnvironment));
                OnPropertyChanged(nameof(WiresEnvironment));
                OnPropertyChanged(nameof(EnvironmentSummary));
                OnPropertyChanged(nameof(ResolvedAspectRatio));
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
                OnPropertyChanged(nameof(StorySourceSummary));
                OnPropertyChanged(nameof(PicturePlanSummary));
                OnPropertyChanged(nameof(ReferenceBudgetSummary));
                ClearEnvironmentCommand.NotifyCanExecuteChanged();
                OnCanExecuteChanged();
                ScheduleWardrobeDerive();
            }
        }

        public BitmapImage? EnvironmentPreview => _environmentPreview;
        public string EnvironmentInfo => _environmentInfo;
        public bool HasEnvironment => !string.IsNullOrEmpty(EnvironmentPath) && File.Exists(EnvironmentPath);

        /// <summary>
        /// Whether the location photograph is also handed to the generator. Off, it stays what the Hybrid
        /// tab's scene image is — read by the language model, never uploaded — which buys back a reference
        /// slot for a fifth character.
        /// </summary>
        public bool WireEnvironment
        {
            get => _wireEnvironment;
            set
            {
                if (_wireEnvironment == value) return;
                _wireEnvironment = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WiresEnvironment));
                OnPropertyChanged(nameof(EnvironmentSummary));
                OnPropertyChanged(nameof(PicturePlanSummary));
                OnPropertyChanged(nameof(ReferenceBudgetSummary));
                OnPropertyChanged(nameof(CanAddKeyframe));
                OnCanExecuteChanged();
            }
        }

        /// <summary>There is a location photograph <i>and</i> it is being sent to the generator — the
        /// condition every picture-numbering decision on this tab actually turns on.</summary>
        public bool WiresEnvironment => HasEnvironment && WireEnvironment;

        public string EnvironmentSummary
        {
            get
            {
                if (!HasEnvironment)
                    return "Optional. With no location picture the setting comes from the story's words " +
                           "alone, restated in every clip — which holds, but holds less well across a long " +
                           "chain than a photograph does.";
                return WireEnvironment
                    ? "Analyze reads the setting, lighting and art style off it, AND it is wired to the " +
                      "generator as the last reference picture — costing one of the nine slots. Nobody in it " +
                      "is cast: the prompt says so."
                    : "Read by the language model only, never uploaded — the Hybrid tab's behaviour. It frees " +
                      "a reference slot for another character's panel.";
            }
        }

        private async System.Threading.Tasks.Task SelectEnvironmentAsync()
        {
            var path = await PickImageAsync("Select the location", "h3ensemble.environment");
            if (path == null) return;
            EnvironmentPath = path;
            AddLog($"Location: {Path.GetFileName(path)}");
        }

        #endregion

        #region Keyframes

        /// <summary>
        /// The timeline stills, in the order they were added. Each is a frame the video must land on exactly
        /// at its own timestamp — a hard cut that replaces pose, wardrobe and background together.
        /// </summary>
        public ObservableCollection<KeyframeSlot> Keyframes => _keyframes;

        public bool HasKeyframes => _keyframes.Count > 0;

        /// <summary>The keyframes actually on disk, in timestamp order — the wiring order, and therefore the
        /// order <c>&lt;Picture 1&gt;</c>… is numbered in.</summary>
        private IReadOnlyList<KeyframeSlot> OrderedKeyframes =>
            _keyframes.Where(k => k.Exists).OrderBy(k => k.Seconds).ToList();

        /// <summary>Room is capped by the reference node's nine slots, shared with the cast and the set.</summary>
        public bool CanAddKeyframe =>
            OrderedKeyframes.Count + CastPanelCount + (WiresEnvironment ? 1 : 0) < MaxReferenceImages;

        private async System.Threading.Tasks.Task AddKeyframesAsync()
        {
            var path = await PickImageAsync("Select a keyframe still", "h3ensemble.keyframe");
            if (path == null) return;

            var slot = new KeyframeSlot(path, LoadImagePreview, OnKeyframesChanged);
            // Timed rather than re-spread: the first still is the opening frame, and every later one lands
            // halfway between the last lock and the end, so adding a picture never moves a timestamp that
            // has already been set. ⇔ Spread is there for when they do want them re-spaced.
            var last = _keyframes.Count == 0 ? -1 : _keyframes.Max(k => k.Seconds);
            slot.Seconds = last < 0 ? 0 : Math.Round((last + ClampLength(LengthSeconds)) / 2.0, 2);
            _keyframes.Add(slot);
            AddLog($"Keyframe {_keyframes.Count}: {Path.GetFileName(path)} @ {slot.Seconds:0.00}s");
        }

        private void RemoveKeyframe(KeyframeSlot? slot)
        {
            if (slot == null) return;
            _keyframes.Remove(slot);
        }

        private void ClearKeyframes()
        {
            _keyframes.Clear();
            AddLog("Keyframes cleared — clip 1 becomes a plain reference-driven continuous take.");
        }

        /// <summary>
        /// Spreads the keyframes evenly from 0.00 to a little short of the clip length. The first one is
        /// always 0.00: a hybrid run whose opening frame is not locked has no reason to be hybrid, and a lock
        /// at the very end is explicitly not what this mode does.
        /// </summary>
        private void SpreadKeyframes()
        {
            var slots = _keyframes.ToList();
            if (slots.Count == 0) return;

            var len = ClampLength(LengthSeconds);
            var span = slots.Count == 1 ? 0 : len * 2.0 / 3.0;
            for (var i = 0; i < slots.Count; i++)
                slots[i].Seconds = slots.Count == 1 ? 0 : Math.Round(span * i / (slots.Count - 1), 2);

            OnKeyframesChanged();
        }

        private void OnKeyframesChanged()
        {
            OnPropertyChanged(nameof(HasKeyframes));
            OnPropertyChanged(nameof(CanAddKeyframe));
            OnPropertyChanged(nameof(KeyframeSummary));
            OnPropertyChanged(nameof(PicturePlanSummary));
            OnPropertyChanged(nameof(ReferenceBudgetSummary));
            ClearKeyframesCommand.NotifyCanExecuteChanged();
            SpreadKeyframesCommand.NotifyCanExecuteChanged();
            AddKeyframeCommand.NotifyCanExecuteChanged();
            OnCanExecuteChanged();
        }

        public string KeyframeSummary
        {
            get
            {
                var keys = OrderedKeyframes;
                if (keys.Count == 0)
                    return "No hand-placed keyframe. Either add a still here, or press 🎬 Preview Keyframes " +
                           "below and let H3 render one opening frame per clip.";

                var times = string.Join(", ", keys.Select((k, i) => $"<Picture {i + 1}> @ {k.Seconds:0.00}s"));
                var opening = keys[0].Seconds <= 0.001
                    ? "The first is the exact opening frame. "
                    : "WARNING: nothing is locked at 0.00s — the opening frame is generated. Press ⇔ Spread. ";
                return $"{keys.Count} keyframe(s), clip 1 only: {times}. {opening}" +
                       (keys.Count > 1
                           ? "Every later one is a hard cut that replaces pose, outfit and background together."
                           : "The clip runs on from it with no end-frame lock.");
            }
        }

        /// <summary>
        /// The frame locks one clip of the chain carries: the storyboard still H3 rendered for it, at 0.00s,
        /// plus — for clip 1, the only clip a hand-placed timeline can belong to — whatever is in the keyframe
        /// list. A manual still already at 0.00 wins.
        /// </summary>
        private IReadOnlyList<(string Path, double Seconds)> KeyframesForClip(int clipIndex)
        {
            var keys = clipIndex == 1
                ? OrderedKeyframes.Select(k => (k.Path, k.Seconds)).ToList()
                : new List<(string Path, double Seconds)>();

            if (!UsedStoryboard.TryGetValue(clipIndex, out var shot)) return keys;
            if (keys.Any(k => k.Seconds <= 0.001)) return keys;

            keys.Insert(0, (shot.Path, 0.0));
            return keys;
        }

        /// <summary>The same list as <see cref="KeyframesForClip"/>, as <see cref="HybridCastPrompt"/> wants
        /// it.</summary>
        private IReadOnlyList<HybridCastPrompt.Keyframe> PromptKeyframesForClip(int clipIndex) =>
            KeyframesForClip(clipIndex)
                .Select((k, i) => new HybridCastPrompt.Keyframe(k.Seconds, $"Keyframe {i + 1}"))
                .ToList();

        #endregion

        #region Image helpers

        private async System.Threading.Tasks.Task<string?> PickImageAsync(string title, string persistKey)
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            return await _fileDialogService.OpenFileDialogAsync(
                title,
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: persistKey);
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

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(CanAnalyze));
            OnPropertyChanged(nameof(CanGenerate));
            OnPropertyChanged(nameof(CanBuildSheets));
            OnPropertyChanged(nameof(CanPreviewKeyframes));
            OnPropertyChanged(nameof(PreviewKeyframesButtonText));
            OnPropertyChanged(nameof(StoryboardSummary));
            OnPropertyChanged(nameof(CanAddKeyframe));
            OnPropertyChanged(nameof(BuildSheetsButtonText));
            OnPropertyChanged(nameof(AllSheetsReady));
            OnPropertyChanged(nameof(CastSummary));
            OnPropertyChanged(nameof(PromptHealthSummary));
            BuildSheetsCommand.NotifyCanExecuteChanged();
            PreviewKeyframesCommand.NotifyCanExecuteChanged();
            ClearStoryboardCommand.NotifyCanExecuteChanged();
            AnalyzeCommand.NotifyCanExecuteChanged();
            RestampCommand.NotifyCanExecuteChanged();
            DeriveWardrobeCommand.NotifyCanExecuteChanged();
            AddKeyframeCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            RemoveQueueItemCommand.NotifyCanExecuteChanged();
            ClearQueueCommand.NotifyCanExecuteChanged();
            StartQueueCommand.NotifyCanExecuteChanged();
            StopQueueCommand.NotifyCanExecuteChanged();
            ReprocessAllFailedCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            GenerateCastZimageLoraCommand.NotifyCanExecuteChanged();
            GenerateCastFamegridLoraCommand.NotifyCanExecuteChanged();
            GenerateCastKrea2LoraCommand.NotifyCanExecuteChanged();
            GenerateCastQwenCommand.NotifyCanExecuteChanged();
            GenerateCastKrea2SpicyCommand.NotifyCanExecuteChanged();
        }
    }
}
