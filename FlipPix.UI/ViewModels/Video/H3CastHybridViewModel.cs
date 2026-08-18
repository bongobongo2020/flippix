using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "H3 Cast Hybrid" tab — the 🪪👥 H3 Cast pipeline run on MiniMax H3's <b>hybrid</b> checkpoint, which
    /// completes supplied keyframes and generates from character references in the <i>same</i> pass.
    ///
    /// <para><b>What is different from H3 Cast.</b> Plain H3 Cast is reference-to-video: the cast's sheets go
    /// in, and everything that happens on screen is written. This tab adds a <b>keyframe timeline</b> — stills
    /// the video must land on exactly, each at a timestamp you set. At 0.00 seconds the frame <i>is</i> your
    /// picture; at 3.00 seconds it cuts to the next one, pose, wardrobe and background together. The cast
    /// sheets ride along as identity references that must never become frames. That combination is the
    /// documented exception in <c>prompts/h3-hybrid-prompting-guide.md</c>: alignment lives inside the prompt
    /// text rather than in a first/last-frame node, and the extra pictures exist only on the reference
    /// node.</para>
    ///
    /// <para><b>The prompt is half written in code.</b> Four of the six sections are pure bookkeeping — which
    /// picture is a frame lock at which timestamp, which is only a reference, and who is the same person —
    /// and a language model re-invents them every few clips. So
    /// <see cref="HybridCastPrompt.Assemble"/> writes <c>subject_definitions</c>,
    /// <c>retention_analysis</c>, the alignment paragraph and the global negatives from the reference list
    /// itself, and the llama-server is asked only for <c>summary</c>, the shots, the soundscape and the
    /// score. A wardrobe edit or a keyframe change therefore re-stamps a prompt already on screen without
    /// another round trip.</para>
    ///
    /// <para><b>The graph.</b> <c>h3-cast-hybrid.json</c>, adapted from <c>plagueh3.json</c>: the
    /// <c>minimax_h3_hybrid_fl2va_ref2va</c> int8 UNet with the 8-step fl2v turbo LoRA, chunked feed-forward,
    /// the int8 ConvRot video VAE and the w4a8 CLIP. The export shipped a fully wired
    /// <c>MiniMaxH3ReferenceToVideo</c> that nothing consumed — the sampler was conditioned on a
    /// first/last-frame node instead — so this tab drives the reference node, which is what hybrid mode
    /// actually means, and drops the image-to-video branch, the PlagueKind bundle and the reference resizers
    /// (references are sized by <c>ref_image_size: match</c>; a canvas-shaped reference is an invitation to
    /// render one). The tail — the face-refine pass, FILM ×2 interpolation and RTX ×2 — is wired per job and
    /// pruned by reachability when off.</para>
    ///
    /// <para><b>The face-refine pass</b> (nodes 100–111, the same chain the 🪪👥 H3 Cast tab runs) tracks and
    /// crops every face out of the rendered frames, re-generates the crops as img2img at
    /// <see cref="RefineDenoise"/> with stage one's own audio locked in, and stitches them back. Two things
    /// differ from plain H3 Cast, both because this tab's picture list is not all cast: the pass is
    /// conditioned on the <b>cast panels only</b> — a keyframe still is a frame lock, and a frame lock has no
    /// meaning on a 768px face crop — and it therefore gets its own copy of the prompt, assembled as pure
    /// reference generation with those panels numbered from one
    /// (<see cref="H3CastHybridQueueItem.RefinePrompt"/>). Its scheduler mirrors the base pass
    /// (<c>linear_quadratic</c>, 8 steps) rather than the 4-step <c>simple</c> one
    /// <c>h3facerefiner.json</c> uses, because this stack carries the 8-step turbo LoRA and the denoise
    /// dial only means what the sigma schedule says it means.</para>
    ///
    /// <para><b>Length.</b> H3 tops out at ~15 seconds, so a longer <see cref="StoryDurationSeconds"/> is
    /// delivered as a chain of clips written in one reply. <b>The keyframes belong to clip 1 only</b> — they
    /// are locked to timestamps inside a single pass — and clips 2…N are continuous takes driven by the cast
    /// references alone, joined into one file when the last lands.</para>
    /// </summary>
    public partial class H3CastHybridViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/h3-minimax/h3-cast-hybrid.json";
        private const string SheetWorkflowFileName = "workflow/image/qwen-edit/Qwen_Edit_2511_INT8_Convrot_WF.json";
        private const string OutputSubfolder = "h3_cast_hybrid";
        private const string SystemPromptFile = "h3-cast-hybrid.md";
        private const string StorySystemPromptFile = "h3-cast-hybrid_story.md";
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

        // ── Face-refine pass node ids (the 100-block of h3-cast-hybrid.json) ──────────────────
        private const string NodeRefinePrompt = "15";     // PrimitiveStringMultiline — the cast-only prompt
        private const string NodeFaceTrack = "100";       // H3FaceTrackCrop — tracks and crops every face
        private const string NodeRefineReference = "101"; // MiniMaxH3ReferenceToVideo (face-crop canvas)
        private const string NodeAudioLock = "103";       // MiniMaxH3NativeAudioLock — stage-1 audio
        private const string NodeRefineDenoise = "106";   // BasicScheduler of the refine pass (denoise)
        private const string NodeRefineSeed = "108";      // RandomNoise of the refine pass
        private const string NodeFaceStitch = "111";      // H3FaceStitch — refined crops back into the frames

        // ── Character 2's refine pass — the 100-block cloned into the 200s at submit time ──────
        /// <summary>What node 100–111 become in the clone. See <see cref="AddSecondRefinePass"/>.</summary>
        private const int RefinePass2IdOffset = 100;

        private const string NodeRefinePrompt2 = "215";    // PrimitiveStringMultiline — character 2's prompt
        private const string NodeFaceTrack2 = "200";       // H3FaceTrackCrop tracking character 2
        private const string NodeRefineReference2 = "201"; // MiniMaxH3ReferenceToVideo — their panels only
        private const string NodeRefineDenoise2 = "206";   // BasicScheduler of the second pass
        private const string NodeRefineSeed2 = "208";      // RandomNoise of the second pass
        private const string NodeFaceStitch2 = "211";      // H3FaceStitch — over character 1's stitched frames

        /// <summary>The autogrow input the reference node collects its images from.</summary>
        private const string RefImagePrefix = "ref_images.ref_image_";

        /// <summary>Ids for the injected <c>LoadImage</c> nodes, one per reference beyond the first. Well
        /// clear of every id the workflow uses (which top out at 111).</summary>
        private const int ReferenceNodeIdBase = 900;

        /// <summary><c>MiniMaxH3ReferenceToVideo</c>'s autogrow cap — nine <c>ref_image_N</c> slots, shared
        /// between the keyframes and the cast's panels.</summary>
        private const int MaxReferenceImages = 9;

        // ── Sheet node ids (locked from image/qwen-edit/Qwen_Edit_2511_INT8_Convrot_WF.json) ───
        private const string SheetLoadImage = "78";
        private const string SheetPositive = "115:111";
        private const string SheetSampler = "115:3";
        private const string SheetLatent = "115:112";
        private const string SheetSave = "60";

        private const int SheetWidth = 1536;
        private const int SheetHeight = 864;

        /// <summary>H3 renders at 24 fps and the duration maths is built on it.</summary>
        private const int OutputFrameRate = 24;

        /// <summary>FILM's multiplier — node 33's <c>multiplier</c> and node 36's expression, mirrored here so
        /// the tab can say what frame rate the file will carry before it renders.</summary>
        private const int InterpolationFactor = 2;

        /// <summary>RTX Video Super Resolution factor — node 34's scale.</summary>
        private const double RtxScale = 2.0;

        /// <summary>Where a whole-clip frame stack starts being the thing that fails, in gigabytes.</summary>
        private const double HeavyFrameStackGb = 8.0;

        // ── Character state ────────────────────────────────────────────────────
        private readonly CharacterSlot _character1;
        private readonly CharacterSlot _character2;

        // ── Keyframes ──────────────────────────────────────────────────────────
        private readonly ObservableCollection<KeyframeSlot> _keyframes = new();

        // ── Scene / prompt state ───────────────────────────────────────────────
        private string _sceneImagePath = string.Empty;
        private BitmapImage? _sceneImagePreview;
        private string _sceneImageInfo = string.Empty;
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
        private double _storyDurationSeconds = 8;
        private string _selectedAspectRatio = MiniMaxH3ViewModel.AutoAspect;
        private string _selectedMedium = "live-action and cinematic";
        private double _megapixels = 1.0;
        private double _lengthSeconds = 8;
        private long _seed = -1;
        private bool _isAnalyzing;
        private bool _faceRefine = true;
        private double _refineDenoise = 0.45;
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
        private readonly ObservableCollection<H3CastHybridQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;
        private bool _isProcessingQueue;
        private string _queueStatus = string.Empty;

        private static string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h3casthybrid_queue.json");

        public H3CastHybridViewModel(
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

            // The analysis clock. Nothing else drives AnalyzeBusyText between phases, and a chain can
            // sit inside a single llama-server turn for minutes.
            _analyzeClock.Tick += (_, _) => OnPropertyChanged(nameof(AnalyzeBusyText));

            _character1 = new CharacterSlot(1, LoadImagePreview, OnCharacterChanged);
            _character2 = new CharacterSlot(2, LoadImagePreview, OnCharacterChanged);

            SelectCharacter1Command = new RelayCommand(async () => await PickCharacterAsync(_character1));
            SelectCharacter2Command = new RelayCommand(async () => await PickCharacterAsync(_character2));
            ClearCharacter2Command = new RelayCommand(() => _character2.Clear());
            SelectSceneImageCommand = new RelayCommand(async () => await SelectSceneImageAsync());
            ClearSceneImageCommand = new RelayCommand(() => SceneImagePath = string.Empty);
            AddKeyframeCommand = new RelayCommand(async () => await AddKeyframesAsync(), () => CanAddKeyframe);
            RemoveKeyframeCommand = new RelayCommand<KeyframeSlot>(RemoveKeyframe);
            ClearKeyframesCommand = new RelayCommand(ClearKeyframes, () => HasKeyframes);
            SpreadKeyframesCommand = new RelayCommand(SpreadKeyframes, () => HasKeyframes);
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
            RemoveQueueItemCommand = new RelayCommand<H3CastHybridQueueItem>(RemoveQueueItem);
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

            AddLog("H3 Cast Hybrid initialized");
            ScheduleQueueLoad();
        }

        #region Commands

        public ICommand SelectCharacter1Command { get; }
        public ICommand SelectCharacter2Command { get; }
        public ICommand ClearCharacter2Command { get; }
        public ICommand SelectSceneImageCommand { get; }
        public RelayCommand ClearSceneImageCommand { get; }
        /// <summary>Adds one or more timeline stills, timestamped evenly across the clip.</summary>
        public RelayCommand AddKeyframeCommand { get; }
        public RelayCommand<KeyframeSlot> RemoveKeyframeCommand { get; }
        public RelayCommand ClearKeyframesCommand { get; }
        /// <summary>Re-spaces every keyframe evenly from 0 to just short of the clip length.</summary>
        public RelayCommand SpreadKeyframesCommand { get; }
        public RelayCommand LoadStoryCommand { get; }
        public RelayCommand ClearStoryCommand { get; }
        public RelayCommand DeriveWardrobeCommand { get; }
        public RelayCommand ClearWardrobeCommand { get; }
        public RelayCommand ToggleWardrobeLockCommand { get; }
        public RelayCommand BuildSheetsCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        /// <summary>Re-assembles the prompt in the box against the current keyframes, cast and wardrobe —
        /// without asking the llama-server again.</summary>
        public RelayCommand RestampCommand { get; }
        /// <summary>Named for the button it drives; it enqueues rather than running inline.</summary>
        public RelayCommand GenerateCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand<H3CastHybridQueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StartQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand RandomSeedCommand { get; }

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

        /// <summary>Room is capped by the reference node's nine slots, shared with the cast's panels.</summary>
        public bool CanAddKeyframe => OrderedKeyframes.Count + CastPanelCount < MaxReferenceImages;

        private async Task AddKeyframesAsync()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select a keyframe still",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: "h3casthybrid.keyframe");
            if (path == null) return;

            var slot = new KeyframeSlot(path, LoadImagePreview, OnKeyframesChanged);
            // Timed rather than re-spread: the first still is the opening frame, and every later one lands
            // halfway between the last lock and the end, so adding a picture never moves a timestamp the user
            // has already set. ⇔ Spread is there for when they do want them re-spaced.
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
            AddLog("Keyframes cleared — the clip becomes a plain reference-driven continuous take.");
        }

        /// <summary>
        /// Spreads the keyframes evenly from 0.00 to a little short of the clip length. The first one is
        /// always 0.00: a hybrid run whose opening frame is not locked has no reason to be hybrid, and a lock
        /// at the very end is explicitly not what this mode does — the clip runs on from the last keyframe.
        /// </summary>
        private void SpreadKeyframes()
        {
            var slots = _keyframes.ToList();
            if (slots.Count == 0) return;

            var len = ClampLength(LengthSeconds);
            // The last lock sits at ~⅔ of the way in when there are several, so there is room for the final
            // beat to play out rather than the clip ending on the cut.
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
                    return "No keyframe — the clip is a plain reference-driven continuous take, with no frame " +
                           "locked at either end. Add a still to pin the opening frame.";

                var times = string.Join(", ", keys.Select((k, i) => $"<Picture {i + 1}> @ {k.Seconds:0.00}s"));
                var opening = keys[0].Seconds <= 0.001
                    ? "The first is the exact opening frame. "
                    : "WARNING: nothing is locked at 0.00s — the opening frame is generated. Press ⇔ Spread. ";
                return $"{keys.Count} keyframe(s): {times}. {opening}" +
                       (keys.Count > 1
                           ? "Every later one is a hard cut that replaces pose, outfit and background together."
                           : "The clip runs on from it with no end-frame lock.");
            }
        }

        #endregion

        #region Characters

        public CharacterSlot Character1 => _character1;
        public CharacterSlot Character2 => _character2;

        public bool HasCharacter1 => _character1.HasSource;
        public bool HasCharacter2 => _character2.HasSource;

        private IEnumerable<CharacterSlot> LoadedCharacters =>
            new[] { _character1, _character2 }.Where(c => c.HasSource);

        public bool AllSheetsReady => HasCharacter1 && LoadedCharacters.All(c => c.HasSheet);

        private int Panels1 => HasCharacter1 ? ReferencePlanFor(_character1).Count : 0;
        private int Panels2 => HasCharacter2 ? ReferencePlanFor(_character2).Count : 0;
        private int CastPanelCount => HasCharacter1 ? Panels1 + Panels2 : 0;

        /// <summary>The cast as <see cref="HybridCastPrompt"/> wants it — index, noun, and what each of the
        /// pictures actually sent for them shows.</summary>
        private IReadOnlyList<HybridCastPrompt.CastMember> CastMembers =>
            LoadedCharacters.Select(c => new HybridCastPrompt.CastMember(
                c.Index, c.Noun, ReferencePlanFor(c).Views)).ToList();

        // ── Reference budget ───────────────────────────────────────────────────

        /// <summary>Every panel the sheet was cut into.</summary>
        public const int RefsAllPanels = 0;

        /// <summary>The face close-up and the front full body — the default.</summary>
        public const int RefsFrontAndFace = 1;

        /// <summary>The face close-up alone.</summary>
        public const int RefsFaceOnly = 2;

        private int _referenceBudget = RefsFrontAndFace;

        public IReadOnlyList<ReferenceBudgetOption> ReferenceBudgetOptions { get; } = new[]
        {
            new ReferenceBudgetOption(RefsFrontAndFace, "Front + face (2 per character)"),
            new ReferenceBudgetOption(RefsAllPanels, "Every panel (3 per character)"),
            new ReferenceBudgetOption(RefsFaceOnly, "Face close-up only (1 per character)"),
        };

        /// <summary>
        /// How many of each character's panels are handed to H3.
        ///
        /// <para>Defaults to <see cref="RefsFrontAndFace"/>. Every reference is encoded at the generation's
        /// pixel area and they share one nine-slot input, so a back view costs exactly as much as the face
        /// close-up while carrying almost none of the likeness — and with two characters, three panels each
        /// is six pictures of studio backdrop competing with the shot description for the same attention.</para>
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
            }
        }

        public string ReferenceBudgetSummary => !HasCharacter1
            ? string.Empty
            : $"→ H3 receives {CastPanelCount} cast reference(s): " +
              string.Join("; ", LoadedCharacters.Select(c =>
                  $"character {c.Index} as {string.Join(" + ", ReferencePlanFor(c).Views)}"));

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
            var views = HybridCastPrompt.DefaultViews(panels);
            if (panels != 3 || ReferenceBudget == RefsAllPanels)
                return new ReferencePlan(Enumerable.Range(0, panels).ToList(), views);

            var indices = ReferenceBudget == RefsFaceOnly ? new[] { 2 } : new[] { 0, 2 };
            return new ReferencePlan(indices, indices.Select(i => views[i]).ToList());
        }

        /// <summary>Which panels of a character are uploaded, and what each of them shows.</summary>
        private sealed record ReferencePlan(IReadOnlyList<int> Indices, IReadOnlyList<string> Views)
        {
            public int Count => Indices.Count;
        }

        /// <summary>The keyframes as <see cref="HybridCastPrompt"/> wants them.</summary>
        private IReadOnlyList<HybridCastPrompt.Keyframe> PromptKeyframes =>
            OrderedKeyframes.Select((k, i) => new HybridCastPrompt.Keyframe(k.Seconds, $"Keyframe {i + 1}"))
                            .ToList();

        /// <summary>
        /// The whole picture plan in one line — which numbers are frame locks and which are the cast — because
        /// getting that order wrong is the one mistake in this tab that renders a studio photograph as a shot.
        /// </summary>
        public string PicturePlanSummary
        {
            get
            {
                if (!HasCharacter1) return string.Empty;
                var keys = OrderedKeyframes.Count;
                var total = keys + CastPanelCount;
                var sb = new StringBuilder($"{total} reference image(s): ");
                sb.Append(keys == 0
                    ? "no keyframe locks; "
                    : $"<Picture 1>–<Picture {keys}> are the keyframe locks; ");
                sb.Append($"Character 1 is <Picture {keys + 1}>–<Picture {keys + Panels1}> " +
                          $"({string.Join(" + ", ReferencePlanFor(_character1).Views)})");
                if (Panels2 > 0)
                    sb.Append($", Character 2 is <Picture {keys + Panels1 + 1}>–<Picture {total}> " +
                              $"({string.Join(" + ", ReferencePlanFor(_character2).Views)})");
                sb.Append(". The cast pictures are identity references and are told, in the prompt, never to " +
                          "become frames.");
                if (total > MaxReferenceImages)
                    sb.Append($" ⚠ That is more than the {MaxReferenceImages} slots MiniMaxH3ReferenceToVideo " +
                              "has — drop a keyframe, or split the sheets into fewer panels.");
                return sb.ToString();
            }
        }

        public string CastSummary
        {
            get
            {
                if (!HasCharacter1) return "Load a photo of character 1 to start.";
                var missing = LoadedCharacters.Count(c => !c.HasSheet);
                var cast = HasCharacter2 ? "Two characters" : "One character";
                if (missing == 0)
                    return $"{cast} — sheets ready; H3 sees the sheets, not the photos.";

                var queued = IsProcessing || IsProcessingQueue
                    ? " A render is in flight, so the build waits for the GPU and starts when that job finishes."
                    : string.Empty;
                return $"{cast} — {missing} sheet(s) still to build. Build Sheets runs Qwen-Image-Edit-2511 " +
                       $"once per character.{queued}";
            }
        }

        private void OnCharacterChanged()
        {
            OnPropertyChanged(nameof(HasCharacter1));
            OnPropertyChanged(nameof(HasCharacter2));
            OnPropertyChanged(nameof(AllSheetsReady));
            OnPropertyChanged(nameof(CastSummary));
            OnPropertyChanged(nameof(PicturePlanSummary));
            OnPropertyChanged(nameof(ReferenceBudgetSummary));
            // Two characters means two refine passes, which the summary has to say out loud.
            OnPropertyChanged(nameof(RefineSummary));
            OnPropertyChanged(nameof(CanAddKeyframe));
            OnPropertyChanged(nameof(BuildSheetsButtonText));
            OnPropertyChanged(nameof(SheetsShowWardrobe));
            OnPropertyChanged(nameof(WardrobeSummary));
            OnCanExecuteChanged();
            ScheduleWardrobeDerive();
        }

        private async Task PickCharacterAsync(CharacterSlot slot)
        {
            var path = await PickImageAsync($"Select Character {slot.Index}", $"h3casthybrid.char{slot.Index}");
            if (path == null) return;
            slot.SourcePath = path;
            AddLog($"Character {slot.Index}: {Path.GetFileName(path)}");
        }

        private async Task SelectSceneImageAsync()
        {
            var path = await PickImageAsync("Select Scene Image", "h3casthybrid.scene");
            if (path == null) return;
            SceneImagePath = path;
            AddLog($"Scene image: {Path.GetFileName(path)}");
        }

        private async Task<string?> PickImageAsync(string title, string persistKey)
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

        #region Character sheets (Qwen-Image-Edit-2511 ConvRot)

        /// <summary>
        /// Deliberately <i>not</i> gated on a render being in flight: the workflow coordinator already
        /// serializes GPU access, so a build started mid-render simply waits for the lease. Gating it would
        /// make it impossible to prepare the next job while the current one runs, which is the point of the
        /// queue.
        /// </summary>
        public bool CanBuildSheets => HasCharacter1 && !IsBuildingSheets &&
                                      LoadedCharacters.Any(c => !c.UseSourceAsSheet);

        public bool IsBuildingSheets
        {
            get => _isBuildingSheets;
            private set
            {
                if (_isBuildingSheets == value) return;
                _isBuildingSheets = value;
                OnPropertyChanged();
                OnCanExecuteChanged();
            }
        }

        /// <summary>What the sheet builder is doing right now, shown beside its button — it cannot use the
        /// tab's status line, which belongs to whatever is rendering.</summary>
        public string SheetPhase
        {
            get => _sheetPhase;
            private set { if (_sheetPhase != value) { _sheetPhase = value; OnPropertyChanged(); } }
        }

        public string BuildSheetsButtonText
        {
            get
            {
                var n = LoadedCharacters.Count(c => !c.UseSourceAsSheet);
                return n > 1 ? $"🪪 Build {n} Character Sheets" : "🪪 Build Character Sheet";
            }
        }

        /// <summary>
        /// Runs Qwen-Image-Edit-2511 once per loaded character, turning each photo into the three-panel
        /// reference sheet H3 is handed — wearing the locked wardrobe rather than whatever the photo showed.
        /// </summary>
        private async Task BuildSheetsAsync()
        {
            if (!CanBuildSheets) return;

            IsBuildingSheets = true;
            SheetPhase = "Preparing…";

            _sheetCts?.Dispose();
            _sheetCts = new CancellationTokenSource();
            var token = _sheetCts.Token;

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                var todo = LoadedCharacters.Where(c => !c.UseSourceAsSheet).ToList();
                AddLog($"=== H3 Cast Hybrid: building {todo.Count} character sheet(s) with Qwen-Image-Edit-2511 ===");

                SheetPhase = "Deciding the wardrobe…";
                if (!await EnsureWardrobeAsync(token) && (HasStoryText || HasSceneImage))
                    AddLog("WARNING: no wardrobe could be derived, so the sheets keep the clothes in the source " +
                           "photos and each clip will describe an outfit of its own.");

                SheetPhase = IsProcessing || IsProcessingQueue
                    ? "Waiting for the current render to finish…"
                    : "Waiting for the GPU…";
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("H3CastHybrid", token);

                SheetPhase = "Checking ComfyUI…";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    SheetPhase = "Connecting to ComfyUI…";
                    await _comfyUIService.ConnectAsync();
                }

                var instruction = (await LoadFileAsync(Path.Combine("prompts", "prompt2json", SheetPromptFile), token)).Trim();

                for (var i = 0; i < todo.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var slot = todo[i];
                    var progress = todo.Count > 1 ? $" ({i + 1}/{todo.Count})" : string.Empty;
                    var outfit = CastPromptStamp.OutfitFor(CastWardrobe, slot.Index);

                    SheetPhase = $"Uploading character {slot.Index}…{progress}";
                    var uploaded = await EnsureUploadedAsync(slot.SourcePath);

                    var ts = DateTime.Now.ToString("yyyyMMddHHmmss");
                    var runToken = $"sheet_{slot.Index}_{ts}";

                    var json = await LoadFileAsync(SheetWorkflowFileName, token);
                    json = UseSheetCanvas(json);
                    SetInput(ref json, SheetLoadImage, "image", uploaded);
                    SetInput(ref json, SheetPositive, "prompt", BuildSheetInstruction(instruction, slot, outfit));
                    SetInput(ref json, SheetSampler, "seed", System.Random.Shared.NextInt64(0, 1_000_000_000_000_000L));
                    SetInput(ref json, SheetLatent, "width", SheetWidth);
                    SetInput(ref json, SheetLatent, "height", SheetHeight);
                    SetInput(ref json, SheetSave, "filename_prefix", $"{OutputSubfolder}/{runToken}");
                    // The sheet graph's SaveImage reads from an RTX upscale, and that node's widgets changed
                    // with the Nvidia pack — without this the sheet reaches the GPU and dies there.
                    json = RtxSuperResolutionCompat.Normalize(json, AddLog);

                    SheetPhase = $"Generating character {slot.Index}'s sheet…{progress}";
                    AddLog($"Character {slot.Index} ({slot.Noun}): generating a {SheetWidth}×{SheetHeight} sheet " +
                           $"from {Path.GetFileName(slot.SourcePath)}...");
                    if (outfit.Length > 0)
                        AddLog($"Character {slot.Index} is being dressed in the locked wardrobe: {outfit}");
                    var promptId = await SubmitSheetAsync(json, token);

                    string? local = null;
                    var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
                    if (byNode.TryGetValue(SheetSave, out var outs) && outs.Count > 0)
                        local = await ResolveImageToLocalAsync(outs[0]);
                    local ??= FindTokenImageOnDisk(runToken);
                    if (local == null || !File.Exists(local))
                        throw new Exception($"Character {slot.Index}'s sheet was not produced.");

                    await EnsureUploadedAsync(local);
                    var applied = local;
                    var wornInSheet = outfit;
                    Application.Current.Dispatcher.Invoke(() => slot.SetSheet(applied, wornInSheet));
                    AddLog($"Character {slot.Index}: sheet ready — {Path.GetFileName(local)}");
                }

                SheetPhase = AllSheetsReady ? "Sheets ready." : "Sheets built.";
            }
            catch (OperationCanceledException)
            {
                AddLog("Sheet building cancelled");
                SheetPhase = "Cancelled";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR (character sheets): {ex.Message}");
                SheetPhase = $"Error: {ex.Message}";
                MessageBox.Show($"Building the character sheets failed:\n{ex.Message}",
                    "H3 Cast Hybrid", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                lease?.Dispose();
                IsBuildingSheets = false;
                _sheetCts?.Dispose();
                _sheetCts = null;
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// The sheet instruction for one character: the shipped three-panel brief, plus who they are and —
        /// when a wardrobe is locked — the outfit the sheet must show them in. Photographing the cast in the
        /// locked outfit removes the picture-versus-prose disagreement rather than arbitrating it.
        /// </summary>
        private static string BuildSheetInstruction(string baseInstruction, CharacterSlot slot, string outfit)
        {
            var sb = new StringBuilder(baseInstruction);
            sb.Append($" The person is a {slot.Noun}.");
            if (outfit.Length == 0) return sb.ToString();

            sb.Append(" Dress them in exactly this outfit, replacing whatever clothing the input image shows: ")
              .Append(outfit.TrimEnd('.', ' '))
              .Append(". Every one of the three panels must show that same outfit, complete and clearly visible, " +
                      "with the same garments, colours and materials — the front view from the front, the back " +
                      "view from behind, and whatever of it reaches the shoulders in the close-up. Change only " +
                      "the clothing: the face, hair, skin tone, build and age stay exactly as they are in the " +
                      "input image.");
            return sb.ToString();
        }

        /// <summary>
        /// Points the sheet workflow's sampler at its empty latent instead of the VAE-encoded source photo,
        /// so the sheet is composed on a canvas of our choosing rather than inheriting the photo's framing.
        /// Idempotent.
        /// </summary>
        private static string UseSheetCanvas(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Sheet workflow JSON could not be parsed.");

            RequireClass(root, SheetSampler, "KSampler");
            RequireClass(root, SheetLatent, "EmptySD3LatentImage");
            RequireClass(root, SheetPositive, "TextEncodeQwenImageEditPlus");
            RequireClass(root, SheetLoadImage, "LoadImage");
            RequireClass(root, SheetSave, "SaveImage");

            json = root.ToJsonString();
            SetInput(ref json, SheetSampler, "latent_image", new JsonArray(SheetLatent, 0));
            return json;
        }

        private async Task<string?> ResolveImageToLocalAsync(string imageFile)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    var baseUrl = GetComfyUIBaseUrl();
                    var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                    var outputFolder = settings.ResolveOutputFolder(isRemote);
                    if (!string.IsNullOrEmpty(outputFolder))
                    {
                        var srcPath = Path.Combine(outputFolder, imageFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(srcPath))
                        {
                            await WaitForFileStableAsync(srcPath);
                            return srcPath;
                        }
                    }
                }

                var parts = imageFile.Split('/');
                var filename = parts.Last();
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : string.Empty;
                var bytes = await _comfyUIService.HttpClient.DownloadViewFileAsync(filename, subfolder, "output");
                if (bytes is { Length: > 0 })
                {
                    var tmp = Path.Combine(Path.GetTempPath(), $"h3casthybrid_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tmp, bytes);
                    return tmp;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Resolve sheet image failed: {ex.Message}");
            }
            return null;
        }

        private string? FindTokenImageOnDisk(string runToken)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;
                var baseUrl = GetComfyUIBaseUrl();
                var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                var outputFolder = settings.ResolveOutputFolder(isRemote);
                if (string.IsNullOrEmpty(outputFolder)) return null;

                var candidates = new List<string>();
                foreach (var folder in new[] { outputFolder, Path.Combine(outputFolder, OutputSubfolder) })
                {
                    if (Directory.Exists(folder))
                        candidates.AddRange(Directory.GetFiles(folder, "*.png")
                            .Where(f => Path.GetFileName(f).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                return candidates.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            }
            catch (Exception ex)
            {
                AddLog($"Disk scan failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Uploads a file to ComfyUI once, caching the returned input-folder name by path.</summary>
        private async Task<string> EnsureUploadedAsync(string path)
        {
            if (_uploadCache.TryGetValue(path, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;
            if (!File.Exists(path))
                throw new FileNotFoundException($"Image is gone: {path}");

            var name = await _comfyUIService.UploadImageAsync(path);
            if (string.IsNullOrEmpty(name)) throw new Exception($"Failed to upload {Path.GetFileName(path)}.");
            _uploadCache[path] = name;
            AddLog($"Uploaded: {name}");
            return name;
        }

        /// <summary>Reads a file shipped next to the exe (workflow JSON or prompt), relative to BaseDirectory.</summary>
        private static async Task<string> LoadFileAsync(string relativePath, CancellationToken token)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");
            return await File.ReadAllTextAsync(path, token);
        }

        #endregion

        #region Scene, prompt and settings

        /// <summary>
        /// Scene image — never uploaded to ComfyUI. It is the only image Analyze looks at, and it supplies
        /// setting, lighting, art style and the wardrobe. A keyframe still, by contrast, <i>is</i> uploaded
        /// and <i>is</i> a frame; the two are deliberately separate inputs.
        /// </summary>
        public string SceneImagePath
        {
            get => _sceneImagePath;
            set
            {
                if (_sceneImagePath == value) return;
                _sceneImagePath = value;
                _sceneImagePreview = LoadImagePreview(value, out _sceneImageInfo);
                OnPropertyChanged();
                OnPropertyChanged(nameof(SceneImagePreview));
                OnPropertyChanged(nameof(SceneImageInfo));
                OnPropertyChanged(nameof(HasSceneImage));
                OnPropertyChanged(nameof(ResolvedAspectRatio));
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
                OnPropertyChanged(nameof(StorySourceSummary));
                OnCanExecuteChanged();
                ScheduleWardrobeDerive();
            }
        }

        public BitmapImage? SceneImagePreview => _sceneImagePreview;
        public string SceneImageInfo => _sceneImageInfo;
        public bool HasSceneImage => !string.IsNullOrEmpty(SceneImagePath) && File.Exists(SceneImagePath);

        /// <summary>
        /// The assembled six-section hybrid prompt. Past one clip's worth of duration it holds the whole
        /// chain — one prompt per clip, separated by <c>=== CLIP n of N ===</c> headers — and stays editable,
        /// because it is what Add to Queue splits.
        /// </summary>
        public string Prompt
        {
            get => _prompt;
            set
            {
                if (_prompt == value) return;
                _prompt = value;
                _promptClipCount = SplitClips(_prompt).Count;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPrompt));
                OnPropertyChanged(nameof(PromptClipCount));
                OnPropertyChanged(nameof(HasPromptSequence));
                OnPropertyChanged(nameof(PromptClipSummary));
                OnPropertyChanged(nameof(PromptHealthSummary));
                RestampCommand.NotifyCanExecuteChanged();
                OnCanExecuteChanged();
            }
        }

        public bool HasPrompt => !string.IsNullOrWhiteSpace(_prompt);

        /// <summary>
        /// Re-assembles what is in the box against the keyframes, cast and wardrobe as they stand right now.
        /// The four model-written sections survive; the four code-written ones are rebuilt. It is what makes
        /// "add a keyframe" or "change the wardrobe" a free operation rather than another Analyze.
        /// </summary>
        private void Restamp()
        {
            if (!HasPrompt) return;
            var before = PromptClipCount;
            Prompt = AssembleChain(Prompt);
            AddLog(before > 1
                ? $"Re-stamped {before} clips against {OrderedKeyframes.Count} keyframe(s) and the current cast."
                : $"Re-stamped against {OrderedKeyframes.Count} keyframe(s) and the current cast.");
        }

        /// <summary>Reports a prompt whose picture numbers no longer match the keyframe list — the one way
        /// this tab can silently point a lock at a studio photograph.</summary>
        public string PromptHealthSummary
        {
            get
            {
                if (!HasPrompt) return string.Empty;
                var missing = HybridCastPrompt.MissingSections(SplitClips(Prompt).FirstOrDefault());
                if (missing.Count > 0)
                    return $"Missing section(s): {string.Join(", ", missing)}. Press ✎ Re-stamp, or Analyze again.";

                var keys = OrderedKeyframes.Count;
                var highest = SplitClips(Prompt).Select(HybridCastPrompt.HighestPictureReference).DefaultIfEmpty(0).Max();
                if (highest > keys)
                    return $"⚠ The prompt names <Picture {highest}> but only {keys} keyframe(s) are loaded — " +
                           "that number is a cast photograph, not a frame. Press ✎ Re-stamp after fixing the " +
                           "keyframe list, and re-check the shot list.";
                return string.Empty;
            }
        }

        /// <summary>
        /// The cast's outfits, decided once and stamped into every clip verbatim — see the H3 Cast tab for
        /// why this cannot be left to the model that writes the bodies.
        /// </summary>
        public string CastWardrobe
        {
            get => _castWardrobe;
            set
            {
                if (_castWardrobe == value) return;
                _castWardrobe = value;
                PushWardrobeToCast();
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCastWardrobe));
                OnPropertyChanged(nameof(WardrobeSummary));
                OnPropertyChanged(nameof(SheetsShowWardrobe));
                OnPropertyChanged(nameof(CastSummary));
                ClearWardrobeCommand.NotifyCanExecuteChanged();
            }
        }

        public bool HasCastWardrobe => !string.IsNullOrWhiteSpace(CastWardrobe);

        public bool IsWardrobeLocked
        {
            get => _isWardrobeLocked;
            set
            {
                if (_isWardrobeLocked == value) return;
                _isWardrobeLocked = value;
                _wardrobeIsManual = !value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WardrobeLockButtonText));
                OnPropertyChanged(nameof(WardrobeSummary));
            }
        }

        public string WardrobeLockButtonText => IsWardrobeLocked ? "🔒 Locked" : "🔓 Editing";

        /// <summary>True when every loaded character's sheet was generated wearing the wardrobe locked right
        /// now — what lets the prompt tell H3 to copy the clothing out of the references instead of
        /// disowning it.</summary>
        public bool SheetsShowWardrobe =>
            HasCastWardrobe && HasCharacter1 && LoadedCharacters.All(c => c.SheetMatchesWardrobe);

        private void PushWardrobeToCast()
        {
            _character1.ExpectedWardrobe = CastPromptStamp.OutfitFor(_castWardrobe, 1);
            _character2.ExpectedWardrobe = CastPromptStamp.OutfitFor(_castWardrobe, 2);
        }

        public string WardrobeSummary
        {
            get
            {
                if (!HasCastWardrobe)
                    return "Empty — written automatically from the story (or the scene image) a moment after " +
                           "you stop typing. Unlock to write your own.";

                var stale = LoadedCharacters.Where(c => !c.SheetMatchesWardrobe).ToList();
                var sheets = !HasCharacter1
                    ? " Load the cast below and build their sheets to have them photographed in it."
                    : stale.Count == 0
                        ? " The character sheets show this outfit, so the references and the prompt agree."
                        : $" Character {string.Join(" and ", stale.Select(c => c.Index))}'s sheet does not show " +
                          "this outfit yet — rebuild the sheets, or the references and the prompt will be " +
                          "dressing them differently.";

                var keys = HasKeyframes
                    ? " Where a keyframe still shows the cast, that still wins at its own timestamp."
                    : string.Empty;

                return (IsWardrobeLocked
                    ? "Locked. This exact text is written into every clip's prompt ahead of the sections, so " +
                      "the cast cannot change clothes between clips."
                    : "Unlocked — your edits stand and the story no longer rewrites it. Re-lock when you are done.")
                    + sheets + keys;
            }
        }

        /// <summary>
        /// Length of the <i>finished</i> video, 5–120 s in 5 s steps. Anything longer than
        /// <see cref="LengthSeconds"/> is written as a chain of <see cref="PlannedClipCount"/> clips.
        /// </summary>
        public double StoryDurationSeconds
        {
            get => _storyDurationSeconds;
            set
            {
                var snapped = Math.Clamp(Math.Round(value / 5.0) * 5.0, 5, 120);
                if (Math.Abs(_storyDurationSeconds - snapped) < 0.0001) return;
                _storyDurationSeconds = snapped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlannedClipCount));
                OnPropertyChanged(nameof(IsStorySequence));
                OnPropertyChanged(nameof(ClipPlanSummary));
            }
        }

        public int PlannedClipCount =>
            Math.Max(1, (int)Math.Ceiling(StoryDurationSeconds / ClampLength(LengthSeconds) - 0.0001));

        public bool IsStorySequence => PlannedClipCount > 1;

        public string ClipPlanSummary
        {
            get
            {
                var clip = ClampLength(LengthSeconds);
                var n = PlannedClipCount;
                if (n <= 1) return $"One clip of {clip:0.#}s — a single hybrid H3 pass.";
                return $"{n} clips × {clip:0.#}s → {n * clip:0.#}s of video. The keyframes are locked to " +
                       "timestamps inside one pass, so they belong to clip 1 only — clips 2–" + n +
                       " are continuous takes driven by the cast references, and all of them are joined into " +
                       "one file when the last lands.";
            }
        }

        public int PromptClipCount => _promptClipCount;
        public bool HasPromptSequence => PromptClipCount > 1;

        public string PromptClipSummary =>
            PromptClipCount > 1
                ? $"This prompt holds {PromptClipCount} clips — Add to Queue enqueues {PromptClipCount} jobs, in order."
                : string.Empty;

        public string StoryText
        {
            get => _storyText;
            set
            {
                if (_storyText == value) return;
                _storyText = value;
                if (!string.IsNullOrEmpty(_storyFileName)) _storyFileName = string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStoryText));
                OnPropertyChanged(nameof(StorySourceSummary));
                ClearStoryCommand.NotifyCanExecuteChanged();
                OnCanExecuteChanged();
                ScheduleWardrobeDerive();
            }
        }

        public bool HasStoryText => !string.IsNullOrWhiteSpace(StoryText);

        public string StorySourceSummary
        {
            get
            {
                var words = HasStoryText
                    ? StoryText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length
                    : 0;
                var loaded = string.IsNullOrEmpty(_storyFileName) ? string.Empty : $" from {_storyFileName}";

                if (HasSceneImage && HasStoryText)
                    return $"Analyze will use both: the scene image for the setting, lighting and wardrobe, " +
                           $"and these {words:N0} words{loaded} for what happens.";
                if (HasSceneImage)
                    return "Analyze will read the scene image alone and invent a story that suits it.";
                if (HasStoryText)
                    return $"No scene image — Analyze will work from these {words:N0} words{loaded} alone, " +
                           "writing the setting, lighting and wardrobe out of the story itself.";
                return "Load a scene image, write a story, or both — Analyze needs at least one of them.";
            }
        }

        private async Task LoadStoryFileAsync()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select a story (.txt)",
                "Text Files|*.txt;*.md;*.text|All Files|*.*",
                initialDir,
                persistKey: "h3casthybrid.story");
            if (path == null) return;

            try
            {
                var text = (await File.ReadAllTextAsync(path)).Trim();
                if (text.Length == 0)
                {
                    AddLog($"Story file is empty: {Path.GetFileName(path)}");
                    return;
                }

                StoryText = text;
                _storyFileName = Path.GetFileName(path);
                OnPropertyChanged(nameof(StorySourceSummary));
                AddLog($"Story loaded: {_storyFileName} ({text.Length:N0} chars)");
            }
            catch (Exception ex)
            {
                AddLog($"Could not read the story file: {ex.Message}");
                MessageBox.Show($"Could not read that file:\n{ex.Message}",
                    "H3 Cast Hybrid", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public IReadOnlyList<string> AspectRatioOptions { get; } =
            new[] { MiniMaxH3ViewModel.AutoAspect }
                .Concat(MiniMaxH3ViewModel.AspectRatios.Select(a => a.Option)).ToList();

        public string SelectedAspectRatio
        {
            get => _selectedAspectRatio;
            set
            {
                if (_selectedAspectRatio == value) return;
                _selectedAspectRatio = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResolvedAspectRatio));
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
            }
        }

        /// <summary>The aspect actually sent to ComfyUI — the picked one, the opening keyframe's, or the
        /// scene image's closest match. The keyframe wins because it is literally frame 0: rendering it at a
        /// different aspect is a crop of the picture the user asked to lock.</summary>
        public string ResolvedAspectRatio =>
            SelectedAspectRatio == MiniMaxH3ViewModel.AutoAspect
                ? ClosestAspectRatio(OrderedKeyframes.FirstOrDefault()?.Path ?? SceneImagePath)
                : SelectedAspectRatio;

        /// <summary>How the prompt's global rules open. It is stated once, in code, because a chain's clips
        /// are written independently and a style word that drifts is a style that changes mid-story.</summary>
        public IReadOnlyList<string> MediumOptions { get; } = new[]
        {
            "live-action and cinematic",
            "anime, cinematic, high-production",
            "3D CG, cinematic",
            "stop-motion, cinematic",
        };

        public string SelectedMedium
        {
            get => _selectedMedium;
            set
            {
                if (_selectedMedium == value) return;
                _selectedMedium = value;
                OnPropertyChanged();
            }
        }

        public IReadOnlyList<MegapixelOption> MegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.4, "0.4 MP — fast draft (≈864×480)"),
            new MegapixelOption(0.7, "0.7 MP — balanced (≈1120×640)"),
            new MegapixelOption(1.0, "1.0 MP — full quality (≈1344×768)"),
        };

        public double Megapixels
        {
            get => _megapixels;
            set
            {
                if (Math.Abs(_megapixels - value) <= 0.0001) return;
                _megapixels = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
            }
        }

        public double LengthSeconds
        {
            get => _lengthSeconds;
            set
            {
                if (Math.Abs(_lengthSeconds - value) <= 0.0001) return;
                _lengthSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LengthSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
                OnPropertyChanged(nameof(RefineSummary));
                OnPropertyChanged(nameof(PlannedClipCount));
                OnPropertyChanged(nameof(IsStorySequence));
                OnPropertyChanged(nameof(ClipPlanSummary));
                // Shrinking the clip must not leave a lock sitting past the end of it. Only the locks that
                // no longer fit are moved — the rest are the user's timing and stay put.
                ClampKeyframesToLength();
            }
        }

        /// <summary>Pulls any keyframe now past the end of the clip back inside it.</summary>
        private void ClampKeyframesToLength()
        {
            var last = ClampLength(LengthSeconds) * 2.0 / 3.0;
            foreach (var slot in _keyframes.Where(k => k.Seconds > last).ToList())
                slot.Seconds = Math.Round(last, 2);
        }

        public string LengthSummary
        {
            get
            {
                var len = ClampLength(LengthSeconds);
                var muxed = Interpolate ? OutputFrameRate * InterpolationFactor : OutputFrameRate;
                return $"{len:0.#}s → {FramesForSeconds(len)} frames rendered @ {OutputFrameRate} fps, " +
                       $"muxed at {muxed} fps";
            }
        }

        public long Seed
        {
            get => _seed;
            set { if (_seed != value) { _seed = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Whether the second H3 pass runs. On, every frame's faces are tracked, cropped, re-generated at
        /// <see cref="RefineDenoise"/> against the cast's own panels with stage one's audio locked in, and
        /// stitched back. Off, that whole branch is pruned and the base pass's frames go straight to the
        /// finishing passes.
        /// </summary>
        public bool FaceRefine
        {
            get => _faceRefine;
            set
            {
                if (_faceRefine == value) return;
                _faceRefine = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RefineSummary));
            }
        }

        /// <summary>
        /// Denoise of the refine pass — how far a cropped face is allowed to move away from what the base
        /// pass rendered. Low keeps the performance H3 rendered and only cleans it up; high re-draws the face
        /// from the sheet and starts fighting the lip-sync the audio lock is protecting.
        /// </summary>
        public double RefineDenoise
        {
            get => _refineDenoise;
            set
            {
                var snapped = Math.Clamp(Math.Round(value * 20) / 20.0, 0.15, 0.75);
                if (Math.Abs(_refineDenoise - snapped) <= 0.0001) return;
                _refineDenoise = snapped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RefineSummary));
            }
        }

        public string RefineSummary
        {
            get
            {
                if (!FaceRefine)
                    return "Off — the base H3 frames go straight to the finishing passes. Faces stay as H3 " +
                           "rendered them.";

                // The crops are their own full-length stack, held alongside the base frames for the stitch.
                var crops = FrameStackGb(FramesForSeconds(ClampLength(LengthSeconds)), 768, 768);
                var passes = HasCharacter2
                    ? "One H3 pass per character over their own tracked face crops"
                    : "A second H3 pass on the tracked face crops";
                return $"{passes} at denoise {RefineDenoise:0.00}, each conditioned on that character's own " +
                       "panels and tracked by their face close-up, stitched back with the stage-1 audio " +
                       "locked so lip-sync survives" +
                       (HasCharacter2 ? " (character 2 over character 1's stitched frames)" : string.Empty) +
                       $" — ≈{crops:0.#} GB of crops on top of the frames{(HasCharacter2 ? ", and roughly twice the refine time" : string.Empty)}. " +
                       "Needs the H3-FaceRefine + NativeAudioLock nodes.";
            }
        }

        /// <summary>
        /// FILM ×2 frame interpolation. On by default — the 8-step turbo stack renders 24 fps and the
        /// interpolation costs a fraction of what the diffusion did, so it is the one finishing pass that is
        /// nearly free. Off, the interpolation branch is pruned and the frames are muxed at 24 fps.
        /// </summary>
        public bool Interpolate
        {
            get => _interpolate;
            set
            {
                if (_interpolate == value) return;
                _interpolate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LengthSummary));
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
            }
        }

        /// <summary>
        /// The RTX Video Super Resolution ×2 finish. <b>Off by default</b>: it is the graph's single largest
        /// allocation, and with <see cref="Interpolate"/> on it runs over twice as many frames. For a long
        /// clip, render at the H3 canvas and upscale the finished file in ✨ Enhance Video.
        /// </summary>
        public bool RtxUpscale
        {
            get => _rtxUpscale;
            set
            {
                if (_rtxUpscale == value) return;
                _rtxUpscale = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
            }
        }

        public string UpscaleSummary
        {
            get
            {
                var (cw, ch) = CanvasSize(ResolvedAspectRatio, Megapixels);
                var fps = Interpolate ? OutputFrameRate * InterpolationFactor : OutputFrameRate;
                if (!RtxUpscale) return $"Output: ≈{cw}×{ch} @ {fps} fps. No upscale pass.";
                var (w, h) = UpscaleSize(ResolvedAspectRatio, Megapixels);
                return $"Output: RTX ×2 super-resolution → ≈{w}×{h} @ {fps} fps.";
            }
        }

        /// <summary>
        /// The frame stack the run will have to hold in one piece, and a warning when that is the size that
        /// kills ComfyUI mid-render. Every image node here works on the whole clip at once, interpolation
        /// doubles the frame count and RTX quadruples the pixel count — and both allocate at the very end of
        /// a run that has already spent its minutes.
        /// </summary>
        public string LoadSummary
        {
            get
            {
                var frames = FinishedFrameCount();
                var (cw, ch) = CanvasSize(ResolvedAspectRatio, Megapixels);
                var baseGb = FrameStackGb(frames, cw, ch);
                var interp = Interpolate ? $" (FILM ×{InterpolationFactor})" : string.Empty;

                if (!RtxUpscale)
                    return $"{frames} frames{interp} × {cw}×{ch} ≈ {baseGb:0.#} GB of frames held at once.";

                var (uw, uh) = UpscaleSize(ResolvedAspectRatio, Megapixels);
                var upGb = FrameStackGb(frames, uw, uh);
                var text = $"{frames} frames{interp}: ≈{baseGb:0.#} GB at the H3 canvas, ≈{upGb:0.#} GB after " +
                           "RTX ×2, both live at the same time during the upscale.";
                return upGb >= HeavyFrameStackGb
                    ? text + " ⚠ That is the size that takes ComfyUI down mid-render — shorten the clip, drop " +
                             "to 0.7 MP, turn interpolation off, or turn RTX off and upscale afterwards in " +
                             "✨ Enhance Video."
                    : text;
            }
        }

        public bool HasLoadWarning
        {
            get
            {
                if (!RtxUpscale) return false;
                var (uw, uh) = UpscaleSize(ResolvedAspectRatio, Megapixels);
                return FrameStackGb(FinishedFrameCount(), uw, uh) >= HeavyFrameStackGb;
            }
        }

        /// <summary>Frames that reach the file — the render's own count, doubled when FILM runs.</summary>
        private int FinishedFrameCount() =>
            FramesForSeconds(ClampLength(LengthSeconds)) * (Interpolate ? InterpolationFactor : 1);

        private static double FrameStackGb(int frames, int width, int height) =>
            (double)frames * width * height * 3 * 4 / (1024.0 * 1024.0 * 1024.0);

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (_isAnalyzing == value) return;
                _isAnalyzing = value;
                if (value)
                {
                    _analyzeStarted = DateTime.UtcNow;
                    AnalyzePhase = _isDerivingWardrobe ? "Writing the wardrobe…" : "Preparing…";
                    _analyzeClock.Start();
                }
                else
                {
                    _analyzeClock.Stop();
                    _analyzeStarted = default;
                    AnalyzePhase = string.Empty;
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(AnalyzeBusyText));
                OnPropertyChanged(nameof(AnalyzeButtonText));
                OnCanExecuteChanged();
            }
        }

        /// <summary>The button greys out while the model is working, and a greyed-out button reads as
        /// "nothing happened" — so it says what it is doing instead.</summary>
        public string AnalyzeButtonText => IsAnalyzing ? "⏳ Analyzing…" : "🔍 Analyze → H3 Prompt";

        /// <summary>What the analysis is doing right now. A chain is one llama-server turn that reports
        /// nothing at all until it lands — minutes of it, on a local model — so the stage has to be named
        /// here, or the button looks like it did nothing.</summary>
        public string AnalyzePhase
        {
            get => _analyzePhase;
            private set
            {
                if (_analyzePhase == value) return;
                _analyzePhase = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AnalyzeBusyText));
            }
        }

        /// <summary>The phase with a clock running behind it — what tells a stalled server apart from a
        /// slow one, which a phase on its own cannot.</summary>
        public string AnalyzeBusyText
        {
            get
            {
                var phase = string.IsNullOrEmpty(_analyzePhase) ? "Analyzing…" : _analyzePhase;
                if (_analyzeStarted == default) return phase;
                var elapsed = DateTime.UtcNow - _analyzeStarted;
                return $"{phase}  {elapsed.ToString(@"m\:ss")}";
            }
        }

        /// <summary>Analyze needs something to work from — a keyframe still, the scene image, or a story.
        /// Deliberately not gated on a render being in flight: it talks to the llama-server.</summary>
        public bool CanAnalyze => (HasSceneImage || HasStoryText || HasKeyframes) && !IsAnalyzing;

        public bool CanGenerate =>
            HasPrompt && AllSheetsReady && !IsAnalyzing &&
            OrderedKeyframes.Count + CastPanelCount <= MaxReferenceImages;

        private string ClosestAspectRatio(string path)
        {
            int w = 0, h = 0;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    w = frame.PixelWidth; h = frame.PixelHeight;
                }
                catch { /* fall through to the 16:9 default */ }
            }
            return MiniMaxH3ViewModel.ClosestAspectRatio(w, h);
        }

        /// <summary>Mirrors the ResolutionSelector's maths, for display only.</summary>
        private static (int Width, int Height) CanvasSize(string aspectOption, double megapixels)
        {
            var ratio = MiniMaxH3ViewModel.AspectRatios
                .FirstOrDefault(a => a.Option == aspectOption).Ratio;
            if (ratio <= 0) ratio = 16.0 / 9.0;

            var area = Math.Max(0.1, megapixels) * 1_000_000.0;
            var w = RoundTo32(Math.Sqrt(area * ratio));
            return (w, RoundTo32(w / ratio));

            static int RoundTo32(double v) => Math.Max(32, (int)Math.Round(v / 32.0) * 32);
        }

        private static (int Width, int Height) UpscaleSize(string aspectOption, double megapixels)
        {
            var (w, h) = CanvasSize(aspectOption, megapixels);
            return ((int)(w * RtxScale), (int)(h * RtxScale));
        }

        /// <summary>H3's supported clip length is 4–15 seconds at 24 fps.</summary>
        private static double ClampLength(double seconds) =>
            Math.Clamp(seconds <= 0 ? 8 : seconds, 4, 15);

        /// <summary>Mirrors node 13's expression: 24 fps snapped onto the model's 17k+5 frame grid.</summary>
        private static int FramesForSeconds(double seconds)
        {
            var frames = Math.Max(5, (int)Math.Round(seconds * 24));
            return frames + (5 - frames % 17 + 17) % 17;
        }

        #endregion

        #region Analysis (scene + story + keyframes → the four model sections)

        private async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;

            try
            {
                AnalyzePhase = "Finding the model…";
                var model = await ResolveLlmModelAsync(token);
                if (model == null) return;

                var len = ClampLength(LengthSeconds);
                var clipCount = PlannedClipCount;
                var fromImage = HasSceneImage;
                var keys = OrderedKeyframes;
                var source = fromImage
                    ? HasStoryText ? "the scene image + the story text" : "the scene image"
                    : "the story text";
                AddLog(clipCount > 1
                    ? $"Writing a {clipCount}-clip hybrid chain ({clipCount} × {len:0.#}s = {clipCount * len:0.#}s) " +
                      $"from {source} — sending to {_lmStudioService.DescribeTarget(model)}"
                    : $"Writing a {len:0.#}s hybrid H3 prompt from {source} " +
                      $"— sending to {_lmStudioService.DescribeTarget(model)}");

                if (HasStoryText && StoryText.Length > 20000)
                    AddLog($"WARNING: the story is {StoryText.Length:N0} characters — a local model will very " +
                           "likely truncate it. Cut it down to the beats you want on screen.");

                AnalyzePhase = "Dressing the cast…";
                if (!await EnsureWardrobeAsync(token, model))
                    AddLog("WARNING: the wardrobe could not be written — the clips will each describe the " +
                           "outfits themselves, which is where between-clip costume changes come from. " +
                           "Fill the wardrobe box in by hand, or press 🎽 Derive again.");

                AnalyzePhase = "Reading the system prompt…";
                var systemPrompt = await ReadSystemPromptAsync(SystemPromptFile, token);
                if (clipCount > 1)
                {
                    systemPrompt += "\n\n" + await ReadSystemPromptAsync(StorySystemPromptFile, token);
                    if (!fromImage)
                        systemPrompt += "\n\nNOTE FOR THIS RUN: there is no scene image. Wherever the rules " +
                                        "above say to read the setting or the wardrobe off the scene image, " +
                                        "read them off the STORY instead — decide each of them once, and " +
                                        "then repeat that wording verbatim in every clip.";
                }

                var draft = PromptClipCount > 1
                    ? "(the prompt box holds a previous sequence — ignore it and write a fresh one)"
                    : !HasPrompt
                        ? "(none — invent a sequence that suits the material above)"
                        : HybridCastPrompt.Strip(Prompt).Trim();

                var lengthBlock = clipCount > 1
                    ? $"Story sequence: write {clipCount} clips that together tell ONE continuous story " +
                      $"running about {clipCount * len:0.##} seconds in total. Each clip is {len:0.##} " +
                      "seconds long and is rendered separately, so each one must be a complete, " +
                      "self-contained set of the four sections. Separate them with a line spelled exactly " +
                      $"\"=== CLIP n of {clipCount} ===\", numbered 1 to {clipCount} in order. The same " +
                      "characters appear throughout — the same reference sheets are attached to every clip.\n"
                    : $"Target duration: {len:0.##} seconds.\n";

                var keyBlock = BuildKeyframeBrief(keys, len, clipCount);
                var castBlock = BuildCastBrief();

                string userMessage;
                if (fromImage)
                {
                    var story = HasStoryText
                        ? StoryText.Trim()
                        : "(none — invent a story that suits the scene and carry it from beginning to end)";

                    userMessage =
                        "Image role: REFERENCE ONLY — this image is the SCENE (setting, lighting, art style, " +
                        "mood and the wardrobe the cast wears). It is NOT one of the attached pictures and the " +
                        "generator will never see it, so describe the environment — and the clothing — " +
                        "explicitly.\n" +
                        keyBlock +
                        castBlock + "\n" +
                        lengthBlock +
                        $"Story the video must tell:\n{story}\n" +
                        $"Draft idea from the user:\n{draft}";
                }
                else
                {
                    var wholeStory = clipCount > 1
                        ? $"Together the {clipCount} clips must tell the whole story below, beginning to end — " +
                          "split it into that many beats before writing anything, one beat per clip.\n"
                        : $"The whole story has to be told inside {len:0.##} seconds, so pick the beats that " +
                          "carry it and compress the rest; do not stop halfway through.\n";

                    userMessage =
                        "There is NO reference image of the scene. The material below is the only source: read " +
                        "the setting, period, time of day, weather, lighting and mood out of it and write them " +
                        "into the prompt explicitly, keeping them consistent from the first shot to the last.\n" +
                        keyBlock +
                        castBlock + "\n" +
                        lengthBlock +
                        wholeStory +
                        (HasStoryText ? $"The story:\n{StoryText.Trim()}\n" : string.Empty) +
                        $"Draft idea from the user:\n{draft}";
                }

                var maxTokens = Math.Min(32000, 5000 + 2200 * (Math.Max(1, clipCount) - 1));

                // A chain is the one call in this app that asks a model for N structurally identical
                // blocks in a single turn, so it is the one call that needs repetition controls and a
                // scratchpad to plan the beats in — see LlmSampling for the failure they exist to stop.
                // A single clip keeps the request the tab has always sent.
                LlmSampling? sampling = clipCount > 1 ? LlmSampling.StoryChain : null;

                async Task<string> AskAsync(string message, LlmSampling? how) => fromImage
                    ? await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                        model, SceneImagePath, message, systemPrompt,
                        maxTokens: maxTokens, cancellationToken: token, sampling: how)
                    : await _lmStudioService.SendTextChatAsync(
                        model, systemPrompt, message, maxTokens: maxTokens,
                        cancellationToken: token, sampling: how);

                AnalyzePhase = clipCount > 1
                    ? $"Writing {clipCount} clips — {_lmStudioService.DescribeTarget(model)}…"
                    : $"Writing the prompt — {_lmStudioService.DescribeTarget(model)}…";
                var assembled = AssembleChain(CleanOutput(await AskAsync(userMessage, sampling)));

                if (clipCount > 1 && !string.IsNullOrWhiteSpace(assembled))
                    assembled = await BreakLoopAsync(assembled, userMessage, AskAsync, token);

                if (!string.IsNullOrWhiteSpace(assembled))
                {
                    Prompt = assembled;
                    var written = PromptClipCount;
                    AddLog(written > 1
                        ? $"Chain written ({written} clips, {assembled.Length} chars, {CountShots(assembled)} shots total)"
                        : $"Prompt written ({assembled.Length} chars, {CountShots(assembled)} shots)");

                    if (written > clipCount)
                        AddLog($"WARNING: asked for {clipCount} clip(s) but the model returned {written}. " +
                               "Add to Queue enqueues what is in the prompt box — re-run Analyze, or edit the " +
                               "headers by hand.");
                    else if (written < clipCount)
                        AddLog($"{written} of the {clipCount} clips asked for. Add to Queue enqueues what is " +
                               $"in the prompt box, so this is {written * ClampLength(LengthSeconds):0.#}s of " +
                               "video — re-run Analyze, or write the missing beats in by hand.");

                    ReportKeyframeCoverage(assembled, keys);

                    var drift = DescribeWardrobeDrift(SplitClips(assembled).Select(HybridCastPrompt.Strip).ToList());
                    if (drift != null)
                        AddLog(HasCastWardrobe
                            ? $"Note: the clip bodies describe the cast's appearance and {drift}. Every clip " +
                              "carries the same wardrobe block ahead of its sections and that block outranks " +
                              "them, so the outfits should still hold."
                            : $"WARNING: the clips describe the cast's appearance and {drift}, and there is no " +
                              "wardrobe locked to override them — they will change outfits between clips. Fill " +
                              "the wardrobe box in (🎽 Derive) and press ✎ Re-stamp.");
                }
                else
                {
                    AddLog("WARNING: Analysis returned empty result");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR during analysis: {ex.Message}");
                MessageBox.Show($"Analysis failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
            }
        }

        /// <summary>
        /// Tells the model exactly which picture numbers are frame locks and at what timestamps. It is the
        /// single most important paragraph in the request: everything else it writes is prose, and this is
        /// the part that has to line up with the wiring.
        /// </summary>
        private static string BuildKeyframeBrief(
            IReadOnlyList<KeyframeSlot> keys, double clipSeconds, int clipCount)
        {
            if (keys.Count == 0)
                return "KEYFRAMES: there are none. No picture is a frame — write one continuous take with no " +
                       "frame lock at 0.00 and no `<Picture n>` anywhere in your text.\n";

            var sb = new StringBuilder(
                $"KEYFRAMES: {keys.Count} still(s) are attached as frame locks and are numbered in timestamp " +
                "order. Each one must be a shot boundary in your shot list, opening with the lock sentence:\n");
            for (var i = 0; i < keys.Count; i++)
                sb.Append($"  <Picture {i + 1}> is locked at {keys[i].Seconds:0.00} seconds" +
                          (i == 0 && keys[i].Seconds <= 0.001 ? " — the exact opening frame.\n" : ".\n"));
            sb.Append($"After the last lock the clip continues to {clipSeconds:0.00} seconds with no " +
                      "end-frame lock. Never name a `<Picture n>` above " + keys.Count + ": those numbers are " +
                      "the cast's studio photographs, which are never frames.\n");
            if (clipCount > 1)
                sb.Append("These locks exist in CLIP 1 only. Clips 2 onwards must contain no `<Picture n>` at all.\n");
            return sb.ToString();
        }

        /// <summary>How the cast is described to the model: named as subjects, never as pictures, and with
        /// the wardrobe quoted as settled fact when there is one.</summary>
        private string BuildCastBrief()
        {
            var sb = new StringBuilder();
            sb.Append(HasCharacter2
                ? $"CAST: two character reference sheets are attached to the generator. Refer to the people as " +
                  $"<Subject 1> (a {_character1.Noun}) and <Subject 2> (a {_character2.Noun}) — never by a " +
                  "picture number and never by a name from the story; wherever the story names its people, " +
                  "cast <Subject 1> and <Subject 2> in those roles. "
                : $"CAST: one character reference sheet is attached to the generator. Refer to the person as " +
                  $"<Subject 1> (a {_character1.Noun}) — never by a picture number and never by a name from " +
                  "the story. ");
            sb.Append("You have NOT seen those sheets — the generator has. Use the stated sex for pronouns and " +
                      "write no word for their hair, face, skin, build or age. ");

            if (HasCastWardrobe)
            {
                sb.Append("CLOTHING IS ALREADY DECIDED AND IS NOT YOURS TO CHOOSE. The cast wears exactly this, " +
                          "in every shot of every clip:\n").Append(CastWardrobe.Trim()).Append('\n');
                sb.Append("Attach that outfit to the tag the first time each character appears in each clip — " +
                          "\"<Subject 1>, wearing …\" — copying the wording above rather than rephrasing it. ");
                sb.Append(SheetsShowWardrobe
                    ? "Their reference sheets were photographed in exactly these clothes, so the pictures and " +
                      "the words agree — do not contradict either. "
                    : "Their reference sheets are studio photographs and whatever those show them wearing is " +
                      "irrelevant. ");
                sb.Append("Never put them in anything else and never invent a costume change; the only clothing " +
                          "change allowed is one the user's story explicitly asks for.");
            }
            else if (HasSceneImage)
            {
                sb.Append("CLOTHING: dress the cast exactly as the people in the SCENE image are dressed, NOT as " +
                          "their reference sheets show them. Write the outfit out explicitly the first time each " +
                          "character appears — garments, colours, materials, footwear, headwear, worn " +
                          "accessories — attached to their tag, and restate it in the same words every later " +
                          "time. If the scene image shows no people, dress them in what the setting plainly " +
                          "calls for and keep that wording identical throughout.");
            }
            else
            {
                sb.Append("CLOTHING: take the wardrobe from the STORY where it describes it, and where it does " +
                          "not, dress the cast in what the period, place and situation plainly call for. Write " +
                          "it out explicitly the first time each character appears and restate it in exactly " +
                          "the same words everywhere else.");
            }

            if (HasKeyframes)
                sb.Append(" Where a keyframe still shows the cast, that still wins at its own timestamp — the " +
                          "wardrobe words describe what they wear between the locks.");

            // Repeated here as well as in the system prompt because it is the rule that decides whether the
            // cast survive the clip: a face a handful of pixels wide is a face H3 re-invents.
            sb.Append(" FRAMING IS A HARD CONSTRAINT: no shot may be wider than a full-body wide shot — no " +
                      "ultra-wide, no extreme long shot, no aerial — and every shot a character appears in " +
                      "must frame their face legibly. Do not combine a wide framing with a fast or large " +
                      "camera move. The cast may move as violently as the story needs; it is the camera's " +
                      "distance that is constrained.");
            return sb.ToString();
        }

        /// <summary>Says out loud whether the model actually put every lock in the shot list — a keyframe the
        /// shots never mention is a keyframe H3 has no reason to land on.</summary>
        private void ReportKeyframeCoverage(string chain, IReadOnlyList<KeyframeSlot> keys)
        {
            if (keys.Count == 0) return;

            var clip1 = SplitClips(chain).FirstOrDefault() ?? string.Empty;
            var shots = HybridCastPrompt.SplitSections(clip1)
                                        .TryGetValue(HybridCastPrompt.DetailedDescription, out var d)
                        ? d : string.Empty;

            var missing = Enumerable.Range(1, keys.Count)
                .Where(n => !Regex.IsMatch(shots, $@"<\s*Picture\s+{n}\s*>", RegexOptions.IgnoreCase))
                .ToList();

            AddLog(missing.Count == 0
                ? $"Keyframes: all {keys.Count} lock(s) appear in the shot list at their timestamps."
                : $"WARNING: the shot list never names <Picture {string.Join(">, <Picture ", missing)}> — " +
                  "those stills are still attached and still declared as locks in retention_analysis, but the " +
                  "shots do not cut to them. Edit the shot list, or re-run Analyze.");
        }

        private static async Task<string> ReadSystemPromptAsync(string fileName, CancellationToken token)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"System prompt not found: {path}");
            return await File.ReadAllTextAsync(path, token);
        }

        /// <summary>Strips the wrappers small models like to add without touching the section structure.</summary>
        private static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Replace("**", "").Trim();

            if (text.StartsWith("```"))
            {
                var firstBreak = text.IndexOf('\n');
                if (firstBreak > 0) text = text[(firstBreak + 1)..];
                var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0) text = text[..lastFence];
                text = text.Trim();
            }

            if (text.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase))
                text = text[7..].TrimStart();
            if (text.Length > 1 && text[0] == '"' && text[^1] == '"')
                text = text[1..^1].Trim();

            return text.Trim();
        }

        #endregion

        #region Wardrobe (decided once, stamped into every clip)

        private async Task<string?> ResolveLlmModelAsync(CancellationToken token, bool quiet = false)
        {
            var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
            await _lmStudioService.SetBaseUrlAsync(baseUrl);

            var models = await _lmStudioService.GetAvailableModelsAsync(token);
            var model = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
            if (string.IsNullOrEmpty(model) && models.Count > 0)
                model = models[0].Id ?? models[0].Name ?? string.Empty;
            if (!string.IsNullOrEmpty(model)) return model;

            if (quiet)
                AddLog("The wardrobe could not be written: no llama-server model is available. Start the " +
                       "server, then press 🎽 Derive.");
            else
                MessageBox.Show("No LM Studio / llama-server model available. Ensure the server is running and a model is loaded.",
                    "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        private async Task RederiveWardrobeAsync()
        {
            if (!CanAnalyze) return;

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;

            try
            {
                var model = await ResolveLlmModelAsync(token);
                if (model == null) return;

                var dress = WardrobeCast;
                AnalyzePhase = "Writing the wardrobe…";
                AddLog($"Writing outfits for {dress.Count} characters — sending to {_lmStudioService.DescribeTarget(model)}");
                var derived = await DeriveWardrobeAsync(model, dress, token);
                if (string.IsNullOrWhiteSpace(derived))
                {
                    AddLog("WARNING: the wardrobe came back empty — the box is unchanged.");
                    return;
                }

                SetDerivedWardrobe(derived, dress);
                if (HasPrompt)
                    AddLog("Press ✎ Re-stamp to write this wardrobe into the prompt already in the box — no " +
                           "need to re-run Analyze.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR writing the wardrobe: {ex.Message}");
                MessageBox.Show($"Writing the wardrobe failed:\n{ex.Message}",
                    "H3 Cast Hybrid", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
            }
        }

        private void ClearWardrobe()
        {
            CastWardrobe = string.Empty;
            _wardrobeStoryStamp = string.Empty;
            _wardrobeCastStamp = string.Empty;
            IsWardrobeLocked = true;
            _wardrobeIsManual = false;
            ScheduleWardrobeDerive();
        }

        private void SetDerivedWardrobe(string wardrobe, IReadOnlyList<CharacterSlot> dressed)
        {
            var covers = WardrobeCast.All(c => CastPromptStamp.OutfitFor(wardrobe, c.Index).Length > 0);
            var partial = HasCastWardrobe && !covers;
            CastWardrobe = partial ? CastPromptStamp.MergeWardrobe(CastWardrobe, wardrobe) : wardrobe;
            _wardrobeStoryStamp = StorySourceStamp();
            _wardrobeCastStamp = CastSexStamp();
            AddLog(partial
                ? $"Wardrobe: character {string.Join(" and ", dressed.Select(c => c.Index))} dressed; the rest "
                  + $"of the cast keeps what they had:\n{CastWardrobe.Trim()}"
                : $"Wardrobe locked:\n{CastWardrobe.Trim()}");

            var undressed = WardrobeCast
                .Where(c => CastPromptStamp.OutfitFor(CastWardrobe, c.Index).Length == 0).ToList();
            if (undressed.Count > 0)
                AddLog($"WARNING: character {string.Join(" and ", undressed.Select(c => c.Index))} came back " +
                       "with no outfit. The next Analyze or Build Sheets writes one; press 🎽 Derive to do it now.");

            var stale = LoadedCharacters.Where(c => c.HasSheet && !c.SheetMatchesWardrobe).ToList();
            if (stale.Count > 0)
                AddLog($"Character {string.Join(" and ", stale.Select(c => c.Index))}'s sheet was built in " +
                       "other clothes — press Build Character Sheet(s) again so the references H3 gets are " +
                       "wearing this wardrobe.");
        }

        /// <summary>Separates a stamp's fields — a control character, so no story text can forge one.</summary>
        private const char StampSeparator = (char)31;   // Unit Separator

        private string StorySourceStamp() =>
            StoryText.Trim() + StampSeparator + (HasSceneImage ? SceneImagePath : string.Empty);

        private string CastSexStamp() =>
            string.Join(StampSeparator, WardrobeCast.Select(c => c.Noun));

        /// <summary>
        /// The story is the source of the wardrobe, so a change to the story (or the scene image, or the cast)
        /// has to reach the outfits by itself. Debounced, because the story box is typed into.
        /// </summary>
        private void ScheduleWardrobeDerive()
        {
            if (_wardrobeIsManual) return;

            _wardrobeCts?.Cancel();
            _wardrobeCts = null;

            if (!HasStoryText && !HasSceneImage) return;

            var cts = new CancellationTokenSource();
            _wardrobeCts = cts;
            _ = AutoDeriveWardrobeAsync(cts);
        }

        private async Task AutoDeriveWardrobeAsync(CancellationTokenSource cts)
        {
            var token = cts.Token;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5), token);

                var dress = CharactersNeedingWardrobe();
                if (dress.Count == 0) return;

                if (IsAnalyzing || _isDerivingWardrobe)
                {
                    ScheduleWardrobeDerive();
                    return;
                }

                _isDerivingWardrobe = true;
                IsAnalyzing = true;
                try
                {
                    var model = await ResolveLlmModelAsync(token, quiet: true);
                    if (model == null) return;

                    AddLog(dress.Count < WardrobeCast.Count
                        ? $"Writing an outfit for character {string.Join(" and ", dress.Select(c => c.Index))}..."
                        : HasCastWardrobe
                            ? "Story changed — rewriting the cast's wardrobe from it..."
                            : "Deriving the cast's wardrobe from the story...");
                    var derived = await DeriveWardrobeAsync(model, dress, token);
                    token.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(derived))
                    {
                        AddLog("The wardrobe could not be written from this material — press 🎽 Derive to retry, " +
                               "or unlock the box and write it yourself.");
                        return;
                    }
                    SetDerivedWardrobe(derived, dress);
                }
                finally
                {
                    _isDerivingWardrobe = false;
                    IsAnalyzing = false;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"Automatic wardrobe pass failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_wardrobeCts, cts)) _wardrobeCts = null;
                cts.Dispose();
            }
        }

        /// <summary>
        /// Makes sure the wardrobe covers the whole cast before something that depends on it runs — the sheet
        /// builder, which photographs the cast in it, and Analyze, which quotes it.
        /// </summary>
        private async Task<bool> EnsureWardrobeAsync(CancellationToken token, string? llmModel = null)
        {
            var dress = CharactersNeedingWardrobe();
            if (dress.Count == 0)
            {
                if (HasCastWardrobe) AddLog("Wardrobe: using the outfits already in the wardrobe box.");
                return HasCastWardrobe;
            }
            if (!HasStoryText && !HasSceneImage) return HasCastWardrobe;

            _wardrobeCts?.Cancel();

            var model = llmModel ?? await ResolveLlmModelAsync(token);
            if (model == null) return HasCastWardrobe;

            AddLog(dress.Count < WardrobeCast.Count
                ? $"Wardrobe: character {string.Join(" and ", dress.Select(c => c.Index))} has no outfit yet — " +
                  "writing one, leaving the rest of the cast dressed as they are..."
                : "Wardrobe: deciding the cast's outfits once, so every clip — and every character sheet — " +
                  "can be dressed identically...");
            var derived = await DeriveWardrobeAsync(model, dress, token);
            if (string.IsNullOrWhiteSpace(derived)) return HasCastWardrobe;

            SetDerivedWardrobe(derived, dress);
            return true;
        }

        /// <summary>Both slots, always — the outfits are a costume decision about the story, not about which
        /// photo files have been browsed for yet.</summary>
        private IReadOnlyList<CharacterSlot> WardrobeCast => new[] { _character1, _character2 };

        private static IReadOnlyList<CastPromptStamp.CastRole> Roles(IEnumerable<CharacterSlot> cast) =>
            cast.Select(c => new CastPromptStamp.CastRole(c.Index, c.Noun)).ToList();

        private IReadOnlyList<CharacterSlot> CharactersNeedingWardrobe()
        {
            if (!HasCastWardrobe || StorySourceStamp() != _wardrobeStoryStamp) return WardrobeCast;

            var wroteFor = _wardrobeCastStamp.Split(StampSeparator);
            return WardrobeCast.Where(c =>
                CastPromptStamp.OutfitFor(CastWardrobe, c.Index).Length == 0 ||
                wroteFor.Length < c.Index ||
                !string.Equals(wroteFor[c.Index - 1], c.Noun, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private async Task<string> DeriveWardrobeAsync(
            string model, IReadOnlyList<CharacterSlot> dress, CancellationToken token)
        {
            if (dress.Count == 0) return string.Empty;

            const string systemPrompt =
                "You are a costume supervisor writing the wardrobe bible for a short film. You reply with " +
                "nothing but the wardrobe lines you were asked for — no preamble, no headings, no markdown, " +
                "no notes, no explanation.";

            var shape = string.Join("\n", dress.Select(c => $"CHARACTER {c.Index} ({c.Noun}): <outfit>"));
            var who = dress.Count == 2
                ? $"both characters — Character 1 is a {dress[0].Noun}, Character 2 is a {dress[1].Noun}"
                : $"Character {dress[0].Index}, who is a {dress[0].Noun}";

            var settled = WardrobeCast
                .Where(c => dress.All(d => d.Index != c.Index))
                .Select(c => (c, Outfit: CastPromptStamp.OutfitFor(CastWardrobe, c.Index)))
                .Where(x => x.Outfit.Length > 0)
                .Select(x => $"Character {x.c.Index} ({x.c.Noun}) is already dressed and must not be changed: {x.Outfit}")
                .ToList();
            var settledBlock = settled.Count == 0
                ? string.Empty
                : string.Join("\n", settled) + "\nDress the character(s) below to belong in the same production " +
                  "as that, without copying it and without writing a line for anyone already dressed.\n";

            var rules =
                settledBlock +
                $"Decide the outfit for {who} in this video. " +
                "Reply with exactly these lines and nothing else:\n" + shape + "\n\n" +
                "Each <outfit> is ONE sentence of at most 45 words naming every visible garment and worn item — " +
                "top, bottom or dress, outer layer, footwear, headwear, gloves, eyewear, jewellery, bag, belt — " +
                "each with its colour and its material. Write only clothing and worn accessories: no face, hair, " +
                "skin, build, age, name, pose, expression, background, weather or action. The outfit must be " +
                "practical for everything the character does in the story and must stay wearable from beginning " +
                "to end, because they will wear it in every shot of the finished video. " +
                "Write a line for every character listed above even if the story features fewer people than " +
                "that — an unused outfit costs nothing, whereas a missing one leaves that character undressed.";

            string userMessage;
            if (HasSceneImage)
            {
                var story = HasStoryText
                    ? $"The story they act out:\n{StoryText.Trim()}\n"
                    : string.Empty;
                userMessage =
                    "Image role: REFERENCE ONLY — this is the SCENE the video is set in, and it is where the " +
                    "wardrobe comes from. Read the clothing off the people in it. If it shows no people, dress " +
                    "the cast in what the setting, period and situation plainly call for.\n" +
                    story + rules;
            }
            else
            {
                userMessage =
                    "There is no reference image. The story below is the only source: take the wardrobe from it " +
                    "where it describes clothing, and where it does not, dress the cast in what the period, " +
                    "place and situation plainly call for.\n" +
                    $"The story:\n{StoryText.Trim()}\n" +
                    rules;
            }

            var result = HasSceneImage
                ? await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    model, SceneImagePath, userMessage, systemPrompt, maxTokens: 600, cancellationToken: token)
                : await _lmStudioService.SendTextChatAsync(
                    model, systemPrompt, userMessage, maxTokens: 600, cancellationToken: token);

            return CastPromptStamp.NormalizeWardrobe(CleanOutput(result), Roles(WardrobeCast), Roles(dress));
        }

        #endregion

        #region Clip chain (story mode)

        private const string ClipHeaderFormat = "=== CLIP {0} of {1} ===";

        private static readonly Regex ClipHeaderRegex = new(
            @"^[ \t]*[=#*\-–—\[]{0,6}[ \t]*CLIP[ \t]+(\d+)\b[^\r\n]{0,60}$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Splits a prompt chain into its individual clip prompts, headers removed. Text with no
        /// headers is one clip, so every caller can treat the single-clip case as a chain of length 1.</summary>
        private static List<string> SplitClips(string? text)
        {
            var t = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (t.Length == 0) return new List<string>();

            var headers = ClipHeaderRegex.Matches(t);
            if (headers.Count == 0) return new List<string> { t };

            var clips = new List<string>();
            var preamble = t[..headers[0].Index].Trim();

            for (var i = 0; i < headers.Count; i++)
            {
                var start = headers[i].Index + headers[i].Length;
                var end = i + 1 < headers.Count ? headers[i + 1].Index : t.Length;
                var body = t[start..end].Trim();

                if (i == 0 && preamble.Length > 0)
                    body = body.Length > 0 ? $"{preamble}\n\n{body}" : preamble;

                if (body.Length > 0) clips.Add(body);
            }

            return clips.Count > 0 ? clips : new List<string> { t };
        }

        private static string JoinClips(IReadOnlyList<string> clips)
        {
            if (clips.Count == 0) return string.Empty;
            if (clips.Count == 1) return clips[0].Trim();

            var sb = new StringBuilder();
            for (var i = 0; i < clips.Count; i++)
            {
                if (i > 0) sb.Append("\n\n");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    ClipHeaderFormat, i + 1, clips.Count);
                sb.Append("\n\n").Append(clips[i].Trim());
            }
            return sb.ToString();
        }

        /// <summary>
        /// Assembles every clip of a chain against the current keyframes, cast and wardrobe.
        ///
        /// <para><b>Only clip 1 gets the keyframes.</b> A lock is a timestamp inside one 15-second pass, and
        /// every clip restarts at zero — giving clip 5 the same locks would tell H3 to cut back to the
        /// opening frame five times over. The rest are continuous takes carried by the cast references, which
        /// is exactly what the story-mode system prompt asks the model to write.</para>
        /// </summary>
        private string AssembleChain(string chain)
        {
            var clips = SplitClips(chain);
            if (clips.Count == 0) return string.Empty;

            var cast = CastMembers;
            if (cast.Count == 0) return chain.Trim();

            var keys = PromptKeyframes;
            var len = ClampLength(LengthSeconds);
            var selective = clips.Count > 1;

            var assembled = clips.Select((clip, i) => HybridCastPrompt.Assemble(
                    clip,
                    i == 0 ? keys : Array.Empty<HybridCastPrompt.Keyframe>(),
                    cast, CastWardrobe, len, SelectedMedium, SheetsShowWardrobe, selective))
                .Where(c => c.Length > 0)
                .ToList();

            return JoinClips(assembled);
        }

        /// <summary>
        /// The same clip written for the face-refine pass: <b>no keyframes</b>, so the cast's panels are
        /// numbered from <c>&lt;Picture 1&gt;</c> and the prompt says in as many words that no attached
        /// picture aligns with a timestamp.
        ///
        /// <para>The shot list has to be rewritten as well as re-assembled: the model writes its own locks
        /// into it ("the frame is exactly &lt;Picture 2&gt; without reinterpretation"), and those numbers
        /// would land on the cast — see <see cref="HybridCastPrompt.DropPictureLocks"/>.</para>
        ///
        /// <para>Clips 2…N of a chain are already in this form — a keyframe lock lives in one pass, and the
        /// story system prompt bans <c>&lt;Picture n&gt;</c> from their bodies — so for them this returns the
        /// clip unchanged. Clip 1 is the one that needs the rewrite: its own prompt promises H3 a first
        /// frame, and the refine pass renders a face crop, which cannot be one.</para>
        ///
        /// <para><b>One prompt per character.</b> The tracker holds a single subject through a clip, so each
        /// character gets their own pass and their own prompt describing only their own photographs. It is
        /// also what keeps the selective cast honest: deciding it after
        /// <see cref="HybridCastPrompt.DropPictureLocks"/> once cost character 2 their whole pass when their
        /// only mention sat in the same sentence as a picture lock.</para>
        /// </summary>
        /// <param name="subject">1 or 2 — whose face this pass regenerates.</param>
        private string RefinePromptFor(string clip, int subject)
        {
            var cast = CastMembers;
            if (cast.All(c => c.Index != subject)) return string.Empty;

            return HybridCastPrompt.Assemble(
                HybridCastPrompt.DropPictureLocks(HybridCastPrompt.Strip(clip)),
                Array.Empty<HybridCastPrompt.Keyframe>(), cast, CastWardrobe,
                ClampLength(LengthSeconds), SelectedMedium, SheetsShowWardrobe, focusSubject: subject);
        }

        private static readonly string[] AppearanceTerms =
        {
            "dress", "gown", "skirt", "blouse", "shirt", "t-shirt", "tee", "jacket", "coat", "trenchcoat",
            "hoodie", "sweater", "cardigan", "jumper", "vest", "waistcoat", "blazer", "suit", "uniform",
            "robe", "cloak", "cape", "armor", "armour", "kimono", "leggings", "jeans", "trousers", "pants",
            "shorts", "boots", "heels", "sneakers", "shoes", "sandals", "gloves", "scarf", "hat", "cap",
            "helmet", "mask", "goggles", "glasses", "necklace", "earrings", "bracelet", "belt", "apron",
            "ponytail", "braid", "braids", "bun", "bangs", "fringe", "dreadlocks", "blonde", "brunette",
            "redhead", "bearded", "freckles",
        };

        /// <summary>Flags wardrobe drift across a chain: an appearance word that appears in some clips but
        /// not all. Returns null when there is nothing to report.</summary>
        private static string? DescribeWardrobeDrift(IReadOnlyList<string> clips)
        {
            if (clips.Count < 2) return null;

            var perClip = clips
                .Select(c => new HashSet<string>(
                    AppearanceTerms.Where(t => Regex.IsMatch(c, $@"\b{Regex.Escape(t)}\b", RegexOptions.IgnoreCase)),
                    StringComparer.OrdinalIgnoreCase))
                .ToList();

            var inconsistent = perClip
                .SelectMany(s => s)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t => perClip.Count(s => s.Contains(t)) != clips.Count)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (inconsistent.Count == 0) return null;

            var shown = string.Join(", ", inconsistent.Take(8));
            var more = inconsistent.Count > 8 ? $" (+{inconsistent.Count - 8} more)" : string.Empty;
            return $"the clips do not agree on: {shown}{more}";
        }

        /// <summary>
        /// Notes said once per Add to Queue, about the settings that decide whether a face survives the run.
        ///
        /// <para>Both of these are settings, not bugs, and neither is wrong for every clip — which is why
        /// they are advisories rather than changed defaults. They exist because the two ways a face comes
        /// back warped are invisible until 100 minutes of rendering have finished, and both are one
        /// checkbox away at this point in the workflow.</para>
        /// </summary>
        private IEnumerable<string> IdentityAdvisories()
        {
            // The tab's whole premise is a still the video has to land on. With none, the fl2va half of
            // the hybrid checkpoint is conditioned on nothing and this is plain reference-to-video with
            // extra machinery — including nothing anchoring composition, and so nothing anchoring a face.
            if (OrderedKeyframes.Count == 0)
                yield return "Note: no keyframe stills, so the hybrid checkpoint's first-frame half has " +
                             "nothing to lock onto and this runs as plain reference-to-video. Add a " +
                             "keyframe to anchor the opening, or use the 🪪👥 H3 Cast tab.";

            // FILM is flow-based. A spin kick, a snapped head or a fast whip pan is exactly the motion it
            // cannot track, and what it does instead is smear the face across the invented frame — read
            // afterwards as "H3 lost the likeness", which it did not.
            if (Interpolate)
                yield return $"Note: FILM ×{InterpolationFactor} is on. It invents every second frame from " +
                             "optical flow, so on fast action — spins, kicks, whip pans — it is the first " +
                             "thing to turn off if faces come back smeared. Interpolate afterwards in " +
                             "✨ Enhance Video instead.";

            // At high denoise the refine pass re-imagines each cropped face rather than cleaning it, and
            // with per-frame denoise now reaching mid-distance faces too, that reads as boiling.
            if (FaceRefine && RefineDenoise > 0.4)
                yield return $"Note: face refine is at {RefineDenoise:0.00}. Above ~0.40 the pass re-invents " +
                             "a cropped face per frame rather than cleaning it, which on fast motion looks " +
                             "like boiling; 0.30–0.35 holds the likeness better in action clips.";
        }

        #region Loop guard

        /// <summary>
        /// Which clips of a chain repeat an earlier one verbatim, as <c>{ 1-based duplicate → 1-based
        /// original }</c>.
        ///
        /// <para><b>The failure this catches.</b> A local model asked for 15 clips in one reply writes a
        /// few real beats and then alternates two of them to the end — the sampler falls into a cycle and
        /// nothing in the reply's shape says it has. Observed 2026-08-18 on a 35B-A3B Q4: three distinct
        /// beats, then B C B C B C to clip 15. It is invisible upstream (15 headers were emitted, every
        /// clip parses, the wardrobe check passes) and expensive downstream (12 renders of two files,
        /// ~85 minutes), so it has to be caught on the text.</para>
        /// </summary>
        private static Dictionary<int, int> FindRepeatedClips(IReadOnlyList<string> clips)
        {
            var firstSeen = new Dictionary<string, int>(StringComparer.Ordinal);
            var repeats = new Dictionary<int, int>();

            for (var i = 0; i < clips.Count; i++)
            {
                var key = HybridCastPrompt.Fingerprint(clips[i]);
                // Too little model-written text to call it a copy of anything — a stub clip, or a body
                // that is nothing but the code-written sections. Left alone rather than deleted.
                if (key.Length < 60) continue;

                if (firstSeen.TryGetValue(key, out var first)) repeats[i + 1] = first;
                else firstSeen[key] = i + 1;
            }

            return repeats;
        }

        private static string DescribeRepeats(IReadOnlyDictionary<int, int> repeats) =>
            string.Join(", ", repeats.OrderBy(r => r.Key).Select(r => $"clip {r.Key} = clip {r.Value}"));

        /// <summary>
        /// Given a chain that came back looping: ask once more with the used beats quoted back and the
        /// repetition penalty raised, keep whichever reply has more distinct clips, and drop whatever is
        /// still duplicated.
        ///
        /// <para>The retry is worth roughly two minutes of a language model against roughly seven minutes
        /// of GPU per duplicate clip. Dropping rather than keeping is the point: what is in the prompt box
        /// is what <c>AddToQueue</c> enqueues, so a shorter honest chain beats a full-length one that
        /// renders the same file six times.</para>
        /// </summary>
        private async Task<string> BreakLoopAsync(
            string assembled,
            string userMessage,
            Func<string, LlmSampling?, Task<string>> ask,
            CancellationToken token)
        {
            var clips = SplitClips(assembled);
            var repeats = FindRepeatedClips(clips);
            if (repeats.Count == 0) return assembled;

            var distinct = clips.Count - repeats.Count;
            AddLog($"WARNING: the model looped — {DescribeRepeats(repeats)}. Only {distinct} of " +
                   $"{clips.Count} clips are distinct. Asking again with the used beats quoted back and " +
                   "the repetition penalty raised.");

            var retryAssembled = string.Empty;
            AnalyzePhase = $"Re-writing {repeats.Count} repeated clip(s)…";
            try
            {
                retryAssembled = AssembleChain(CleanOutput(
                    await ask(userMessage + BuildLoopCorrection(clips, repeats), LlmSampling.StoryChainRetry)));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AddLog($"WARNING: the retry failed ({ex.Message}) — keeping the first reply.");
            }

            if (!string.IsNullOrWhiteSpace(retryAssembled))
            {
                var retryClips = SplitClips(retryAssembled);
                var retryRepeats = FindRepeatedClips(retryClips);
                var retryDistinct = retryClips.Count - retryRepeats.Count;

                if (retryDistinct > distinct)
                {
                    AddLog($"The retry is better — {retryDistinct} distinct clips against {distinct}.");
                    assembled = retryAssembled;
                    clips = retryClips;
                    repeats = retryRepeats;
                }
                else
                {
                    AddLog($"The retry was no better ({retryDistinct} distinct) — keeping the first reply.");
                }
            }

            if (repeats.Count == 0) return assembled;

            var kept = clips.Where((_, i) => !repeats.ContainsKey(i + 1)).ToList();
            var len = ClampLength(LengthSeconds);
            AddLog($"Dropped {repeats.Count} duplicate clip(s) — the chain is now {kept.Count} × " +
                   $"{len:0.#}s = {kept.Count * len:0.#}s. This model plans about 6–8 beats reliably in " +
                   "one reply; for a longer video, lower the story duration and run Analyze twice, or " +
                   "write the missing beats into the prompt box by hand.");

            return JoinClips(kept);
        }

        /// <summary>
        /// The corrective appended to the user message on the retry: the beats already spent, named as
        /// spent, plus an instruction to enumerate what is left before writing.
        ///
        /// <para>Quoting the used beats back is the part that matters. The model cannot see its own last
        /// reply, so "do not repeat yourself" is unenforceable — the same reason the wardrobe lock had to
        /// become code rather than prompt wording.</para>
        /// </summary>
        private static string BuildLoopCorrection(
            IReadOnlyList<string> clips, IReadOnlyDictionary<int, int> repeats)
        {
            var used = clips
                .Where((_, i) => !repeats.ContainsKey(i + 1))
                .Select(c => HybridCastPrompt.ActionSummary(c, 220))
                .Where(a => a.Length > 0)
                .ToList();

            var sb = new StringBuilder();
            sb.Append("\n\nYOUR PREVIOUS ATTEMPT LOOPED. You wrote ").Append(used.Count)
              .Append(" real beat(s) and then repeated them word for word to fill the clip count (")
              .Append(DescribeRepeats(repeats))
              .Append("). Every one of those repeats is a separate video render of a file identical to ")
              .Append("one already made, so it is worse than useless.\n\n")
              .Append("THESE BEATS ARE USED. Do not write any of them again, in any clip:\n");

            for (var i = 0; i < used.Count; i++)
                sb.Append("  ").Append(i + 1).Append(". ").Append(used[i]).Append('\n');

            sb.Append("\nBefore writing a single clip: list every remaining beat of the story in order and ")
              .Append("count them. If the story has fewer beats left than you have clips to fill, split a ")
              .Append("long beat into its separate physical actions — an approach, the exchange, the ")
              .Append("reversal and the aftermath are four beats, not one. Every clip must show an action ")
              .Append("that appears in no other clip. Never copy a block you have already written.");

            return sb.ToString();
        }

        #endregion

        /// <summary>Counts `[Shot n]` markers, purely for the log line.</summary>
        private static int CountShots(string prompt)
        {
            var count = 0;
            var idx = prompt.IndexOf("[Shot ", StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                count++;
                idx = prompt.IndexOf("[Shot ", idx + 6, StringComparison.OrdinalIgnoreCase);
            }
            return count;
        }

        #endregion

        #region Queue

        public ObservableCollection<H3CastHybridQueueItem> Queue => _queue;

        public bool HasQueueItems => _queue.Count > 0;
        public bool HasPendingItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Pending);
        public bool HasFailedItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            private set
            {
                if (_isProcessingQueue == value) return;
                _isProcessingQueue = value;
                OnPropertyChanged();
                OnCanExecuteChanged();
            }
        }

        public string QueueStatus
        {
            get => _queueStatus;
            private set { if (_queueStatus != value) { _queueStatus = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Freezes the form into queue items and starts the drain loop if it is not already running. The
        /// prompt box — not the duration slider — decides how many items are queued: it is split on its
        /// <c>=== CLIP n of N ===</c> headers and each clip becomes one job, so a hand-edited chain queues
        /// exactly what is on screen.
        /// </summary>
        private void AddToQueue()
        {
            if (!CanGenerate) return;

            // Re-assembled rather than trusted: editing the wardrobe box, adding a keyframe or switching a
            // character's sex and pressing Add to Queue is enough to bring the prompt back into line.
            var chain = AssembleChain(Prompt);
            if (chain != Prompt) Prompt = chain;


            var clips = SplitClips(chain);
            if (clips.Count == 0) return;

            // Last line of defence, and the cheapest one in the app. Analyze drops duplicates already,
            // but the prompt box is editable, a chain can be pasted in or restored from a saved queue,
            // and every clip of a story shares one seed — so an identical prompt is an identical file,
            // found out at the cost of a full render. 2026-08-18: 12 of 15 clips, ~85 minutes of GPU.
            var repeats = FindRepeatedClips(clips);
            if (repeats.Count > 0)
            {
                AddLog($"WARNING: {DescribeRepeats(repeats)} — the same prompt on the chain's shared seed " +
                       "renders the same file, so the duplicates are not queued. Re-run Analyze for more " +
                       "beats, or edit them by hand.");
                clips = clips.Where((_, i) => !repeats.ContainsKey(i + 1)).ToList();
                if (clips.Count == 0) return;
                chain = JoinClips(clips);
                Prompt = chain;
            }

            var storyId = clips.Count > 1
                ? $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..20]
                : string.Empty;

            // One seed for the whole chain: the prompts differ so the clips still differ, but the per-clip
            // re-roll of the noise is one more thing that made the cast look subtly re-cast between beats.
            var storySeed = Seed >= 0 || clips.Count == 1
                ? Seed
                : System.Random.Shared.NextInt64(0, long.MaxValue);

            var keys = OrderedKeyframes;

            for (var i = 0; i < clips.Count; i++)
            {
                var item = new H3CastHybridQueueItem
                {
                    // The locks live in one pass, so only clip 1 carries them — see AssembleChain.
                    KeyframePaths = i == 0 ? keys.Select(k => k.Path).ToList() : new List<string>(),
                    KeyframeSeconds = i == 0 ? keys.Select(k => k.Seconds).ToList() : new List<double>(),
                    Character1SheetPath = _character1.SheetPath,
                    Character2SheetPath = HasCharacter2 ? _character2.SheetPath : string.Empty,
                    Character1SourcePath = _character1.SourcePath,
                    Character2SourcePath = HasCharacter2 ? _character2.SourcePath : string.Empty,
                    Character1PanelPaths = _character1.PanelPaths.ToList(),
                    Character2PanelPaths = HasCharacter2 ? _character2.PanelPaths.ToList() : new List<string>(),
                    // Which of those panels this job actually sends, and what they show — frozen with the
                    // prompt, because the prompt's picture numbering was written from exactly this list.
                    Character1PanelIndices = ReferencePlanFor(_character1).Indices.ToList(),
                    Character1PanelViews = ReferencePlanFor(_character1).Views.ToList(),
                    Character2PanelIndices = HasCharacter2
                        ? ReferencePlanFor(_character2).Indices.ToList() : new List<int>(),
                    Character2PanelViews = HasCharacter2
                        ? ReferencePlanFor(_character2).Views.ToList() : new List<string>(),
                    SceneImagePath = HasSceneImage ? SceneImagePath : string.Empty,
                    Prompt = clips[i],
                    // Frozen here, not derived at submit time: it needs the keyframes, the cast and the
                    // wardrobe box, and by then the form may have moved on.
                    RefinePrompt = FaceRefine ? RefinePromptFor(clips[i], 1) : string.Empty,
                    RefinePrompt2 = FaceRefine && HasCharacter2 ? RefinePromptFor(clips[i], 2) : string.Empty,
                    AspectRatio = ResolvedAspectRatio,
                    Megapixels = Megapixels,
                    LengthSeconds = ClampLength(LengthSeconds),
                    Medium = SelectedMedium,
                    Seed = storySeed,
                    FaceRefine = FaceRefine,
                    RefineDenoise = RefineDenoise,
                    Interpolate = Interpolate,
                    RtxUpscale = RtxUpscale,
                    StoryId = storyId,
                    ClipIndex = i + 1,
                    ClipCount = clips.Count,
                    ItemStatus = QueueItemStatus.Pending,
                };

                _queue.Add(item);
                AddLog($"Queued: {item.DisplayText}");
            }

            AddLog(PicturePlanSummary);
            foreach (var note in IdentityAdvisories()) AddLog(note);

            if (clips.Count > 1)
            {
                if (keys.Count > 0)
                    AddLog($"The {keys.Count} keyframe lock(s) are attached to clip 1 only — clips 2–{clips.Count} " +
                           "are continuous takes carried by the cast references.");

                var soloed = _queue.Where(q => q.StoryId == storyId &&
                                               !HybridCastPrompt.IncludesCharacter2(q.Prompt, HasCharacter2))
                                   .Select(q => q.ClipIndex).ToList();
                if (Panels2 > 0 && soloed.Count > 0)
                    AddLog($"Character 2 is not in clip(s) {string.Join(", ", soloed)}, so their " +
                           $"{Panels2} reference(s) are left out of those prompts entirely.");

                AddLog($"Story queued: {clips.Count} clips × {ClampLength(LengthSeconds):0.#}s " +
                       $"→ {clips.Count * ClampLength(LengthSeconds):0.#}s of video, rendered one at a time " +
                       $"and joined when the last one lands. All {clips.Count} share seed {storySeed}.");
            }

            SaveQueueToFile();

            if (HasCastWardrobe)
            {
                var stale = LoadedCharacters.Where(c => !c.SheetMatchesWardrobe).ToList();
                AddLog(stale.Count == 0
                    ? "Wardrobe: the character sheets show the locked outfits, so the references and the prompts " +
                      "agree on the clothes."
                    : $"WARNING: character {string.Join(" and ", stale.Select(c => c.Index))}'s sheet does not " +
                      "show the locked wardrobe (it was built earlier, or loaded as-is). The prompt says one " +
                      "thing and the reference photograph shows another — rebuild the sheets and re-queue.");
            }
            else
            {
                AddLog("WARNING: no wardrobe is locked, so each clip dresses the cast from its own description " +
                       "— that is what makes them change clothes between clips. Press 🎽 Derive and re-queue.");
            }

            UpdateQueueStatus();

            if (!IsProcessingQueue) _ = ProcessQueueAsync();
        }

        private void RemoveQueueItem(H3CastHybridQueueItem? item)
        {
            if (item == null || item.ItemStatus == QueueItemStatus.Processing) return;
            _queue.Remove(item);
            SaveQueueToFile();
            UpdateQueueStatus();
        }

        private void ClearQueue()
        {
            _queueCts?.Cancel();
            _queue.Clear();
            SaveQueueToFile();
            UpdateQueueStatus();
            AddLog("Queue cleared");
        }

        private void StopQueue() => _queueCts?.Cancel();

        private void CancelEverything()
        {
            _sheetCts?.Cancel();
            _queueCts?.Cancel();
            _wardrobeCts?.Cancel();
        }

        private void ReprocessAllFailed()
        {
            var failed = _queue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (failed.Count == 0) return;
            foreach (var item in failed)
            {
                item.ItemStatus = QueueItemStatus.Pending;
                item.ErrorMessage = null;
            }
            UpdateQueueStatus();
            SaveQueueToFile();
            if (!IsProcessingQueue) _ = ProcessQueueAsync();
        }

        private void UpdateQueueStatus()
        {
            var pending = _queue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
            var running = _queue.Count(x => x.ItemStatus == QueueItemStatus.Processing);
            var done = _queue.Count(x => x.ItemStatus == QueueItemStatus.Completed);
            var failed = _queue.Count(x => x.ItemStatus == QueueItemStatus.Failed);
            QueueStatus = _queue.Count == 0
                ? string.Empty
                : $"{pending} pending • {running} running • {done} done • {failed} failed";

            OnPropertyChanged(nameof(HasPendingItems));
            OnPropertyChanged(nameof(HasFailedItems));
            OnCanExecuteChanged();
        }

        /// <summary>
        /// Drains pending items one at a time. The coordinator lease is taken <b>per item</b> rather than
        /// around the loop, so a long queue does not lock every other tab out of ComfyUI for its whole run.
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;

            IsProcessingQueue = true;
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;

            AddLog("Starting H3 Cast Hybrid queue...");
            try
            {
                H3CastHybridQueueItem? item;
                while (!token.IsCancellationRequested &&
                       (item = _queue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                {
                    item.ItemStatus = QueueItemStatus.Processing;
                    item.StartedAt = DateTime.Now;
                    UpdateQueueStatus();
                    SaveQueueToFile();

                    try
                    {
                        await GenerateItemAsync(item, token);
                        item.ItemStatus = QueueItemStatus.Completed;
                        item.CompletedAt = DateTime.Now;
                        AddLog($"Completed: {item.DisplayText}");
                        await CompleteStoryAsync(item, token);
                    }
                    catch (OperationCanceledException)
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
                        AddLog("Queue stopped — the current item is back to Pending.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (await TryHandleCrashAndRetryAsync(item, ex))
                        {
                            item.ItemStatus = QueueItemStatus.Pending;
                            AddLog("Item reset to Pending — will retry after ComfyUI restart");
                        }
                        else
                        {
                            item.ItemStatus = QueueItemStatus.Failed;
                            item.ErrorMessage = ex.Message;
                            AddLog($"FAILED: {ex.Message}");
                        }
                    }

                    UpdateQueueStatus();
                    SaveQueueToFile();
                }
            }
            finally
            {
                IsProcessingQueue = false;
                ProcessingStatus = token.IsCancellationRequested ? "Queue stopped" : "Queue finished";
                AddLog("Queue processing finished.");
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Runs once the <i>last</i> clip of a chain lands: announces the finished story, then FFmpeg-joins
        /// its clips into one continuous video. Deliberately exception-free — the drain loop's catch would
        /// otherwise read a join failure as a render failure.
        /// </summary>
        private async Task CompleteStoryAsync(H3CastHybridQueueItem finished, CancellationToken token)
        {
            try
            {
                if (!finished.IsStoryClip || string.IsNullOrEmpty(finished.StoryId)) return;

                var siblings = _queue.Where(x => x.StoryId == finished.StoryId)
                                     .OrderBy(x => x.ClipIndex)
                                     .ToList();

                if (siblings.Any(x => x.ItemStatus != QueueItemStatus.Completed))
                {
                    var stalled = siblings.Count(x => x.ItemStatus == QueueItemStatus.Failed);
                    if (stalled > 0 && !siblings.Any(x => x.ItemStatus is QueueItemStatus.Pending or QueueItemStatus.Processing))
                        AddLog($"Story not joined: {stalled} of {siblings.Count} clips failed. " +
                               "Retry them and the join runs when the last one lands.");
                    return;
                }

                var total = siblings.Sum(x => ClampLength(x.LengthSeconds));
                AddLog($"=== Story complete: {siblings.Count} clips, {total:0.#}s total ===");
                foreach (var clip in siblings)
                    AddLog($"  clip {clip.ClipIndex}/{clip.ClipCount}: {clip.OutputVideoPath}");

                await JoinStoryAsync(finished.StoryId, siblings, token);
            }
            catch (Exception ex)
            {
                AddLog($"Story join failed: {ex.Message}");
            }
        }

        private async Task JoinStoryAsync(string storyId, IReadOnlyList<H3CastHybridQueueItem> clips,
            CancellationToken token)
        {
            var paths = clips.Select(c => c.OutputVideoPath)
                             .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                             .Select(p => p!)
                             .ToList();

            if (paths.Count < clips.Count)
                AddLog($"Join: {clips.Count - paths.Count} clip file(s) are missing from disk and are left out.");
            if (paths.Count < 2)
            {
                AddLog("Join skipped: fewer than two clip files are available.");
                return;
            }

            var ffmpeg = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpeg))
            {
                AddLog("Join skipped: FFmpeg not found. The clips are separate files, in playback order.");
                return;
            }

            var outputDir = Path.GetDirectoryName(paths[0])
                            ?? Path.Combine(_settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "H3CastHybrid");
            Directory.CreateDirectory(outputDir);

            var joinedPath = Path.Combine(outputDir, $"H3CastHybrid_{storyId}_joined.mp4");
            var total = clips.Sum(c => ClampLength(c.LengthSeconds));

            ProcessingStatus = $"Joining {paths.Count} clips...";
            AddLog($"Joining {paths.Count} clips with FFmpeg → {Path.GetFileName(joinedPath)}");
            await ConcatClipsAsync(ffmpeg, paths, joinedPath, token);

            if (!File.Exists(joinedPath) || new FileInfo(joinedPath).Length == 0)
            {
                AddLog("Join produced no file — the individual clips are unaffected.");
                return;
            }

            await LocalCopyService.CopyVideoAsync(joinedPath);

            var fi = new FileInfo(joinedPath);
            Application.Current.Dispatcher.Invoke(() =>
            {
                ResultVideoPath = joinedPath;
                ResultVideoInfo = $"H3 Cast Hybrid • joined story • {paths.Count} clips • {total:0.#}s • " +
                                  $"{fi.Length / 1024 / 1024.0:F1}MB";
                HasResult = true;
                OnCanExecuteChanged();
            });
            ProcessingStatus = "Clips joined!";
            AddLog($"=== Joined video complete: {joinedPath} ===");
        }

        /// <summary>
        /// FFmpeg concat-demuxer join, re-encoded rather than stream-copied: H3 writes an audio track per
        /// clip, and a copy-mode concat of separately encoded H3 outputs is where the timestamp and
        /// codec-parameter edge cases live.
        /// </summary>
        private async Task ConcatClipsAsync(string ffmpeg, IReadOnlyList<string> clips, string outPath,
            CancellationToken token)
        {
            var listPath = Path.Combine(Path.GetTempPath(), $"h3casthybrid_concat_{Guid.NewGuid():N}.txt");
            var sb = new StringBuilder();
            foreach (var clip in clips)
                sb.AppendLine($"file '{clip.Replace("\\", "/").Replace("'", @"'\''")}'");
            await File.WriteAllTextAsync(listPath, sb.ToString(), token);

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in new[]
                {
                    "-y", "-f", "concat", "-safe", "0", "-i", listPath,
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-c:a", "aac", "-b:a", "192k", "-pix_fmt", "yuv420p", outPath
                }) psi.ArgumentList.Add(a);

                using var p = System.Diagnostics.Process.Start(psi)
                              ?? throw new Exception("Failed to start FFmpeg.");
                var stderr = await p.StandardError.ReadToEndAsync(token);
                await p.WaitForExitAsync(token);
                if (p.ExitCode != 0)
                {
                    var tail = stderr.Length <= 600 ? stderr : stderr[^600..];
                    throw new Exception($"FFmpeg exited {p.ExitCode}: {tail}");
                }
            }
            finally
            {
                try { File.Delete(listPath); } catch { /* temp file: best effort */ }
            }
        }

        #endregion

        #region Queue persistence

        private void SaveQueueToFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(QueueFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var pending = _queue.Where(q => q.ItemStatus != QueueItemStatus.Completed).ToList();
                File.WriteAllText(QueueFilePath,
                    JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AddLog($"Error saving queue: {ex.Message}"); }
        }

        /// <summary>Defers the persisted queue read off the constructor — this view model is built during app
        /// startup and must not do disk work there.</summary>
        private void ScheduleQueueLoad()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                _ = LoadQueueFromFileAsync();
                return;
            }

            dispatcher.InvokeAsync(async () => await LoadQueueFromFileAsync(),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private async Task LoadQueueFromFileAsync()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;

                var items = await Task.Run(() =>
                    JsonSerializer.Deserialize<List<H3CastHybridQueueItem>>(File.ReadAllText(QueueFilePath)));
                if (items == null || items.Count == 0) return;

                _queue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Completed) continue;
                    if (item.ItemStatus == QueueItemStatus.Processing) item.ItemStatus = QueueItemStatus.Pending;
                    _queue.Add(item);
                }

                UpdateQueueStatus();
                if (HasPendingItems)
                    AddLog($"Queue restored: {_queue.Count} items ({_queue.Count(x => x.ItemStatus == QueueItemStatus.Pending)} pending) — press ▶ Start to resume.");
                else if (_queue.Count > 0)
                    AddLog($"Queue restored: {_queue.Count} items");
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        #endregion

        #region Generation

        private async Task GenerateItemAsync(H3CastHybridQueueItem item, CancellationToken token)
        {
            IsProcessing = true;
            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = "Preparing H3 Cast Hybrid workflow...";

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                var clipLabel = item.IsStoryClip ? $", clip {item.ClipIndex}/{item.ClipCount}" : string.Empty;
                AddLog($"=== H3 Cast Hybrid ({item.KeyframeCount} keyframe(s), " +
                       $"{(item.HasCharacter2 ? "2 sheets" : "1 sheet")}{clipLabel}) ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("H3CastHybrid", token);

                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                var json = await LoadFileAsync(WorkflowFileName, token);
                json = EnsureInputPrimitives(json);

                ProcessingStatus = "Uploading keyframes and character references...";
                ProcessingProgress = 5;

                // Keyframes first, then the cast — this is the order <Picture 1>… was numbered in when the
                // prompt was assembled, and it is the only thing standing between a frame lock and a studio
                // photograph being rendered as the opening shot.
                var keyframes = item.KeyframePaths
                    .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                    .ToList();
                if (keyframes.Count != item.KeyframePaths.Count)
                    throw new FileNotFoundException(
                        $"{item.KeyframePaths.Count - keyframes.Count} keyframe still(s) are gone from disk. " +
                        "The prompt is numbered for all of them, so this item cannot be renumbered now — " +
                        "restore the files or re-queue the job.");

                var sheet1 = ResolvePanels(item.Character1PanelPaths, item.Character1SheetPath, 1);
                var cast1 = SelectPanels(sheet1, item.Character1PanelIndices, item.Character1PanelViews, 1);
                var panels1 = cast1.Paths;

                // A story clip's prompt casts the subjects it actually uses, so a character it never names is
                // not uploaded, not wired and not encoded.
                var includesCharacter2 = HybridCastPrompt.IncludesCharacter2(item.Prompt, item.HasCharacter2);
                var cast2 = includesCharacter2
                    ? SelectPanels(ResolvePanels(item.Character2PanelPaths, item.Character2SheetPath, 2),
                                   item.Character2PanelIndices, item.Character2PanelViews, 2)
                    : SelectedPanels.None;
                var panels2 = cast2.Paths;
                if (item.HasCharacter2 && !includesCharacter2)
                    AddLog("Character 2 is not named in this clip — their references are left out of it.");

                var uploadedRefs = new List<string>();
                foreach (var picture in keyframes.Concat(panels1).Concat(panels2))
                    uploadedRefs.Add(await EnsureUploadedAsync(picture));

                // The refine pass, and whether this item can actually have one. Every fallback below turns it
                // off rather than refining against the wrong prompt: a pass conditioned on keyframe numbering
                // it never receives redraws faces from whatever <Picture n> happens to resolve to.
                var faceRefine = item.FaceRefine;
                var refinePrompt = !string.IsNullOrWhiteSpace(item.RefinePrompt)
                    ? item.RefinePrompt
                    // With no keyframe the clip's own prompt already is the cast-only, cast-numbered one.
                    : item.KeyframeCount == 0 ? item.Prompt : string.Empty;

                // An item queued before the cast was described per view carries one refine prompt numbered
                // for the whole cast, so it keeps the old single-pass wiring — it would not survive being
                // shown character 1's panels alone.
                var perCharacterRefine = item.Character1PanelViews.Count > 0;
                var refinePrompt2 = perCharacterRefine && includesCharacter2 ? item.RefinePrompt2 : string.Empty;

                if (faceRefine && !HasRefineNodes(json))
                {
                    faceRefine = false;
                    AddLog("Face refine was requested but the workflow file no longer carries the refine " +
                           $"branch (nodes {NodeFaceTrack}–{NodeFaceStitch}) — rendering the base H3 frames " +
                           "as-is.");
                }
                if (faceRefine && refinePrompt.Length == 0)
                {
                    faceRefine = false;
                    AddLog("Face refine is off for this item: it was queued before the refine pass existed, " +
                           "so it carries no cast-only prompt and its own prompt is numbered for keyframes. " +
                           "Re-queue the job to refine it.");
                }

                // The refine prompt was numbered at queue time for the pictures its own pass receives; if the
                // two have drifted apart, a <Picture n> in it points at nothing that pass was sent.
                var castPanelCount = panels1.Count + panels2.Count;
                var pass1Pictures = perCharacterRefine ? panels1.Count : castPanelCount;
                if (faceRefine && HybridCastPrompt.HighestPictureReference(refinePrompt) > pass1Pictures)
                {
                    faceRefine = false;
                    AddLog($"Face refine is off for this item: its refine prompt numbers more pictures than " +
                           $"the {pass1Pictures} panel(s) that pass receives. Re-queue the job to renumber it.");
                }
                if (refinePrompt2.Length > 0 &&
                    HybridCastPrompt.HighestPictureReference(refinePrompt2) > panels2.Count)
                {
                    refinePrompt2 = string.Empty;
                    AddLog("Character 2's refine pass is off for this item: its prompt numbers more pictures " +
                           $"than the {panels2.Count} panel(s) it receives. Re-queue the job to renumber it.");
                }

                var refineCharacter2 = faceRefine && refinePrompt2.Length > 0 && panels2.Count > 0;
                if (faceRefine && includesCharacter2 && !refineCharacter2)
                    AddLog("Only character 1's face is refined in this clip: the tracker follows one subject " +
                           "per pass, and this item carries no second-character refine prompt. Re-queue the " +
                           "job to give character 2 their own pass.");

                json = WireReferenceImages(json, uploadedRefs, out var refLoaders);
                if (faceRefine)
                    json = WireRefinePasses(json, refLoaders, keyframes.Count, cast1, cast2,
                                            perCharacterRefine, refineCharacter2);

                var runSeed = item.Seed >= 0 ? item.Seed : System.Random.Shared.NextInt64(0, long.MaxValue);
                var len = ClampLength(item.LengthSeconds);
                var aspect = item.AspectRatio;
                var (canvasW, canvasH) = CanvasSize(aspect, item.Megapixels);
                var (upW, upH) = UpscaleSize(aspect, item.Megapixels);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var clipTag = item.IsStoryClip ? $"_c{item.ClipIndex:00}" : string.Empty;
                var runToken = $"h3casthybrid_{ts}{clipTag}";

                SetInput(ref json, NodePrompt, "value", item.Prompt);
                SetInput(ref json, NodeResolution, "aspect_ratio", aspect);
                SetInput(ref json, NodeResolution, "megapixels", item.Megapixels);
                SetInput(ref json, NodeDuration, "value", len);
                SetInput(ref json, NodeSeed, "noise_seed", runSeed);
                SetInput(ref json, NodeFps, "value", (double)OutputFrameRate);
                SetInput(ref json, NodeSaveVideo, "filename_prefix", $"{OutputSubfolder}/{runToken}");

                if (faceRefine)
                {
                    SetInput(ref json, NodeRefinePrompt, "value", refinePrompt);
                    SetInput(ref json, NodeRefineDenoise, "denoise", item.RefineDenoise);
                    // Its own noise, derived from the run's seed so the whole job still reproduces from one.
                    SetInput(ref json, NodeRefineSeed, "noise_seed", (runSeed % (long.MaxValue - 1)) + 1);
                    AddLog($"Face refine: character 1's crops re-generated at denoise {item.RefineDenoise:0.00} " +
                           $"against their own {(perCharacterRefine ? panels1.Count : castPanelCount)} " +
                           "panel(s), tracked by the face close-up, with the stage-1 audio locked, then " +
                           "stitched back into the frames.");

                    if (refineCharacter2)
                    {
                        SetInput(ref json, NodeRefinePrompt2, "value", refinePrompt2);
                        SetInput(ref json, NodeRefineDenoise2, "denoise", item.RefineDenoise);
                        // A different seed: the two passes redraw different faces and would otherwise share
                        // the same noise on crops of the same size.
                        SetInput(ref json, NodeRefineSeed2, "noise_seed", (runSeed % (long.MaxValue - 2)) + 2);
                        AddLog($"Face refine: character 2 gets a second pass over character 1's stitched " +
                               $"frames, tracked by their own face close-up against their {panels2.Count} " +
                               "panel(s).");
                    }
                }
                else
                {
                    AddLog("Face refine off: the base H3 frames go straight to the finishing passes.");
                }

                var rtxUpscale = item.RtxUpscale;
                json = WireOutputChain(json, faceRefine, refineCharacter2, item.Interpolate, ref rtxUpscale);
                if (item.RtxUpscale && !rtxUpscale)
                    AddLog($"RTX ×{RtxScale:0.#} was requested but the workflow file no longer has node " +
                           $"{NodeRtxUpscale} — rendering at the H3 canvas instead. Upscale the finished " +
                           "file in ✨ Enhance Video, or restore the node to the workflow.");

                // The Nvidia RTX pack changed this node's widgets; both sets are written so the graph runs
                // whichever version the server has. See RtxSuperResolutionCompat.
                json = RtxSuperResolutionCompat.Normalize(json, AddLog);

                var steps = ReadInt(json, NodeScheduler, "steps");
                json = PruneToOutputs(json, new[] { NodeSaveVideo }, out var prunedCount);
                if (prunedCount > 0)
                    AddLog($"Graph pruned to the video output: {prunedCount} disconnected node(s) removed.");

                var renderedFrames = FramesForSeconds(len);
                var finishedFrames = renderedFrames * (item.Interpolate ? InterpolationFactor : 1);
                var muxFps = item.Interpolate ? OutputFrameRate * InterpolationFactor : OutputFrameRate;
                var finish = (faceRefine ? $"face refine {item.RefineDenoise:0.00}, " : string.Empty) +
                             (item.Interpolate ? $"FILM ×{InterpolationFactor} → {muxFps}fps" : $"{muxFps}fps") +
                             (rtxUpscale ? $", RTX ×{RtxScale:0.#} → ≈{upW}×{upH}" : ", no upscale");

                AddLog(keyframes.Count == 0
                    ? $"References: {uploadedRefs.Count} cast picture(s), no keyframe lock — this clip is a " +
                      "continuous take."
                    : $"References: <Picture 1>–<Picture {keyframes.Count}> are the keyframe locks at " +
                      $"{string.Join(", ", item.KeyframeSeconds.Select(s => $"{s:0.00}s"))}; " +
                      $"<Picture {keyframes.Count + 1}>–<Picture {uploadedRefs.Count}> are the cast.");

                ProcessingProgress = 10;
                ProcessingStatus = "Generating video...";
                AddLog($"Generating (seed {runSeed}, {len:0.#}s / {renderedFrames} frames @ {OutputFrameRate}fps, " +
                       $"{aspect} ≈{canvasW}×{canvasH}, {item.Megapixels:0.0} MP, {steps} steps, {finish})...");

                var peakGb = rtxUpscale
                    ? FrameStackGb(finishedFrames, upW, upH)
                    : FrameStackGb(finishedFrames, canvasW, canvasH);
                AddLog($"Peak frame stack ≈{peakGb:0.#} GB ({finishedFrames} frames held at once)" +
                       (faceRefine
                           ? $", plus ≈{FrameStackGb(renderedFrames, 768, 768):0.#} GB of face crops during " +
                             "the refine pass."
                           : "."));
                if (peakGb >= HeavyFrameStackGb)
                    AddLog("WARNING: that is large enough to take ComfyUI down mid-render — if this job dies " +
                           "with the prompt \"neither queued nor in the run history\", shorten the clip, drop " +
                           "to 0.7 MP, turn interpolation off, or turn RTX off and upscale afterwards in " +
                           "✨ Enhance Video.");

                var local = await SubmitAndRetrieveAsync(json, runToken, NodeSaveVideo, 10, 95, token);
                if (local == null || !File.Exists(local))
                    throw new Exception("No output video was generated.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "H3CastHybrid");
                Directory.CreateDirectory(outputDir);
                var finalName = item.IsStoryClip
                    ? $"H3CastHybrid_{(string.IsNullOrEmpty(item.StoryId) ? ts : item.StoryId)}_clip{item.ClipIndex:00}.mp4"
                    : $"H3CastHybrid_{ts}.mp4";
                var finalPath = Path.Combine(outputDir, finalName);
                File.Copy(local, finalPath, true);
                await LocalCopyService.CopyVideoAsync(finalPath);

                var fi = new FileInfo(finalPath);
                var size = rtxUpscale ? $"RTX ×{RtxScale:0.#} ≈{upW}×{upH}" : $"≈{canvasW}×{canvasH}";
                item.OutputVideoPath = finalPath;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResultVideoPath = finalPath;
                    ResultVideoInfo = $"H3 Cast Hybrid • {(item.IsStoryClip ? $"clip {item.ClipIndex}/{item.ClipCount} • " : string.Empty)}" +
                                      $"{item.KeyframeCount} keyframe(s) • {(item.HasCharacter2 ? "2 sheets" : "1 sheet")} • " +
                                      $"{(faceRefine ? $"face refine {item.RefineDenoise:0.00} • " : string.Empty)}" +
                                      $"turbo {steps}-step • {size} • {muxFps}fps • {aspect} • " +
                                      $"{len:0.#}s • {fi.Length / 1024 / 1024.0:F1}MB";
                    HasResult = true;
                    OnCanExecuteChanged();
                });
                ProcessingProgress = 100;
                ProcessingStatus = "Complete!";
                AddLog($"=== Complete: {finalPath} ===");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddLog($"ERROR: {ex.Message}");
                ProcessingStatus = $"Error: {ex.Message}";
                throw;
            }
            finally
            {
                lease?.Dispose();
                IsProcessing = false;
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Wrapper around <see cref="WorkflowNodeUpdater.UpdateNodeInput"/> that fails loudly on a node id or
        /// input that is no longer in the graph. The updater silently no-ops instead, which on these
        /// workflows would mean shipping the baked-in demo prompt and reference image to the GPU.
        /// </summary>
        private static void SetInput(ref string json, string nodeId, string input, object value)
        {
            if (WorkflowNodeUpdater.GetNodeInput(json, nodeId, input) == null)
                throw new Exception($"Workflow node '{nodeId}' has no input '{input}' — the workflow file no longer matches this tab.");
            WorkflowNodeUpdater.UpdateNodeInput(ref json, nodeId, input, value);
        }

        /// <summary>
        /// Asserts the node classes the patches below assume, and makes sure the reference node reads its
        /// prompt, canvas and frame count from the input primitives rather than from widget values baked in
        /// by an export. Idempotent — the shipped file is already wired this way, and this is what keeps it
        /// that way after a re-export.
        ///
        /// <para>The refine pass's reference node keeps its own width and height: they come from the
        /// face-crop canvas, not from the video canvas. It reads its <i>own</i> prompt primitive rather than
        /// node 10's — see <see cref="H3CastHybridQueueItem.RefinePrompt"/>.</para>
        /// </summary>
        private static string EnsureInputPrimitives(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeReference, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodePrompt, "PrimitiveStringMultiline");
            RequireClass(root, NodeResolution, "ResolutionSelector");
            RequireClass(root, NodeFrames, "ComfyMathExpression");
            RequireClass(root, NodeDuration, "PrimitiveFloat");
            RequireClass(root, NodeSeed, "RandomNoise");
            RequireClass(root, NodeRefImage1, "LoadImage");
            RequireClass(root, NodeBaseFrames, "VAEDecode");
            RequireClass(root, NodeInterpolate, "FrameInterpolate");
            // The RTX upscale is optional in the file itself, not merely prunable: a workflow saved without
            // it is a perfectly good graph, and demanding it would fail every submit rather than the one
            // setting it affects. See HasRtxNode.
            if (root[NodeRtxUpscale] is JsonObject)
                RequireClass(root, NodeRtxUpscale, "RTXVideoSuperResolution");
            RequireClass(root, NodeFps, "PrimitiveFloat");
            RequireClass(root, NodeFpsDoubled, "ComfyMathExpression");
            RequireClass(root, NodeCreateVideo, "CreateVideo");
            RequireClass(root, NodeSaveVideo, "SaveVideo");

            json = root.ToJsonString();
            SetInput(ref json, NodeReference, "prompt", new JsonArray(NodePrompt, 0));
            SetInput(ref json, NodeReference, "width", new JsonArray(NodeResolution, 0));
            SetInput(ref json, NodeReference, "height", new JsonArray(NodeResolution, 1));
            SetInput(ref json, NodeReference, "length", new JsonArray(NodeFrames, 1));

            // The refine pass shares only the frame count; its canvas is the face crop and its prompt is
            // its own. Skipped rather than demanded when the file has no refine branch — see HasRefineNodes.
            if (JsonNode.Parse(json)?[NodeRefineReference] is JsonObject)
            {
                SetInput(ref json, NodeRefineReference, "prompt", new JsonArray(NodeRefinePrompt, 0));
                SetInput(ref json, NodeRefineReference, "length", new JsonArray(NodeFrames, 1));
            }
            return json;
        }

        /// <summary>
        /// Whether the workflow file still carries the face-refine branch. Like the RTX node it is optional
        /// in the file itself rather than merely prunable: a graph saved without it is a perfectly good
        /// base-pass graph, and demanding it would fail every submit rather than the one setting it affects.
        /// </summary>
        private static bool HasRefineNodes(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject();
            return root != null &&
                   root[NodeRefinePrompt] is JsonObject && root[NodeFaceTrack] is JsonObject &&
                   root[NodeRefineReference] is JsonObject && root[NodeAudioLock] is JsonObject &&
                   root[NodeRefineDenoise] is JsonObject && root[NodeRefineSeed] is JsonObject &&
                   root[NodeFaceStitch] is JsonObject;
        }

        /// <summary>
        /// Resolves the panel files a queued character actually renders from, splitting the sheet again when
        /// the frozen paths are gone. Whatever this returns has to have the <i>same number</i> of entries as
        /// the item's prompt was numbered for, so the panel count is forced, never re-detected.
        /// </summary>
        private IReadOnlyList<string> ResolvePanels(
            IReadOnlyList<string> frozen, string sheetPath, int character)
        {
            var kept = frozen.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).ToList();
            if (kept.Count > 0 && kept.Count == frozen.Count) return kept;

            var legacy = frozen.Count == 0;
            var requested = legacy ? CharacterSheetSplitter.WholeSheet : frozen.Count;
            var panels = CharacterSheetSplitter.Split(sheetPath, requested);
            if (panels.Count == 0)
                throw new FileNotFoundException($"Character {character}'s sheet is gone: {sheetPath}");

            AddLog($"Character {character}: cached panels missing, re-split ({panels.Note}).");
            if (!legacy && panels.Count != frozen.Count)
                AddLog($"WARNING: character {character} re-split into {panels.Count} panel(s) but the prompt " +
                       $"was numbered for {frozen.Count}. Re-queue this item to renumber it.");
            return panels.Paths;
        }

        /// <summary>
        /// Narrows a character's panels to the ones this job actually sends, and says what each one shows.
        ///
        /// <para>The selection is the reference budget frozen at queue time. An item queued before it existed
        /// has no indices and sends everything, described positionally — which is what its prompt says too.</para>
        /// </summary>
        private SelectedPanels SelectPanels(
            IReadOnlyList<string> panels, IReadOnlyList<int> indices, IReadOnlyList<string> views, int character)
        {
            if (indices.Count == 0)
                return SelectedPanels.Of(panels, HybridCastPrompt.DefaultViews(panels.Count));

            var usable = indices.Where(i => i >= 0 && i < panels.Count).ToList();
            if (usable.Count != indices.Count)
                AddLog($"WARNING: character {character} was queued sending panel(s) " +
                       $"{string.Join(", ", indices.Select(i => i + 1))} of a {panels.Count}-panel sheet, and " +
                       "that sheet no longer has them. The prompt is numbered for the full list — re-queue " +
                       "this item.");
            if (usable.Count == 0) usable = Enumerable.Range(0, panels.Count).ToList();

            var picked = usable.Select(i => panels[i]).ToList();
            var pickedViews = usable
                .Select((_, slot) => slot < views.Count ? views[slot] : $"view {slot + 1}")
                .ToList();
            return SelectedPanels.Of(picked, pickedViews);
        }

        /// <summary>The panels of one character that a job uploads, what they show, and which of them is the
        /// face close-up — the picture the refine pass tracks that character by.</summary>
        private sealed record SelectedPanels(
            IReadOnlyList<string> Paths, IReadOnlyList<string> Views, int FacePanel)
        {
            public static readonly SelectedPanels None =
                new(Array.Empty<string>(), Array.Empty<string>(), 0);

            public static SelectedPanels Of(IReadOnlyList<string> paths, IReadOnlyList<string> views)
            {
                var face = views.ToList().FindIndex(
                    v => string.Equals(v, HybridCastPrompt.ViewFace, StringComparison.OrdinalIgnoreCase));
                return new SelectedPanels(paths, views, face >= 0 ? face : Math.Max(0, paths.Count - 1));
            }
        }

        /// <summary>
        /// Wires the run's pictures into <c>ref_images.ref_image_0…N</c> in the order they were numbered:
        /// keyframe locks first, then the cast's panels.
        ///
        /// <para>They go in <b>unresized</b>. <c>MiniMaxH3ReferenceToVideo</c> sizes references itself
        /// (<c>ref_image_size: match</c> scales each one to the generation's pixel area keeping aspect), and
        /// pre-scaling every reference to the exact video canvas hands H3 a canvas-shaped, canvas-sized
        /// picture — the shape of an output frame, which is a strong invitation to render one. That is why
        /// the resize nodes the source export carried are not in this graph.</para>
        ///
        /// <para>What the face-refine passes are conditioned on is decided separately, out of the same
        /// loaders — see <see cref="WireRefinePasses"/>.</para>
        /// </summary>
        /// <param name="loaders">The injected <c>LoadImage</c> node ids, in picture order — what the refine
        /// passes pick their own conditioning out of.</param>
        private static string WireReferenceImages(
            string json, IReadOnlyList<string> uploadedNames, out IReadOnlyList<string> loaders)
        {
            if (uploadedNames.Count == 0)
                throw new Exception("No reference images to wire — the run has neither keyframes nor a cast.");
            if (uploadedNames.Count > MaxReferenceImages)
                throw new Exception($"{uploadedNames.Count} reference images, but MiniMaxH3ReferenceToVideo " +
                                    $"takes at most {MaxReferenceImages}. Drop a keyframe, or split the sheets " +
                                    "into fewer panels.");

            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeReference, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodeRefImage1, "LoadImage");

            // The workflow ships one LoadImage; the rest are injected beside it, ids well clear of the graph's.
            var ids = new List<string>();
            for (var i = 0; i < uploadedNames.Count; i++)
            {
                var id = i == 0
                    ? NodeRefImage1
                    : (ReferenceNodeIdBase + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                root[id] = new JsonObject
                {
                    ["inputs"] = new JsonObject { ["image"] = uploadedNames[i] },
                    ["class_type"] = "LoadImage",
                    ["_meta"] = new JsonObject { ["title"] = $"Ref Image {i + 1}" }
                };
                ids.Add(id);
            }

            Attach(root, NodeReference, ids);

            loaders = ids;
            return root.ToJsonString();

            static void Attach(JsonObject root, string nodeId, IReadOnlyList<string> loaders)
            {
                if (root[nodeId]?["inputs"] is not JsonObject inputs)
                    throw new Exception($"Workflow node '{nodeId}' has no inputs — the workflow file no longer matches this tab.");

                // Cleared rather than overwritten: a run with fewer pictures than the file was authored for
                // must not inherit a stale ref_image_N pointing at a node that is about to be pruned.
                foreach (var key in inputs.Select(kv => kv.Key)
                                          .Where(k => k.StartsWith(RefImagePrefix, StringComparison.Ordinal))
                                          .ToList())
                    inputs.Remove(key);

                for (var i = 0; i < loaders.Count; i++)
                    inputs[RefImagePrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                        new JsonArray(loaders[i], 0);
            }
        }

        /// <summary>
        /// Conditions the face-refine pass — or, for a two-hander, <b>the two of them</b> — on the cast's
        /// panels, and tells the tracker which face each pass is following.
        ///
        /// <para><b>One pass per character.</b> <c>H3FaceTrackCrop</c> holds a single subject through a clip:
        /// with no <c>identity_reference</c> it picks whoever is largest in the first frame and follows them,
        /// so in a two-character clip the other character's face was never refined at all — and the pass that
        /// did run was shown both cast members' photographs, which gave it nothing to say about which of the
        /// two faces it was looking at. Each character now gets their own pass: tracked by their own face
        /// close-up, conditioned on their own panels, prompted with their own copy of the clip
        /// (<see cref="H3CastHybridQueueItem.RefinePrompt2"/>). Character 2's pass runs over character 1's
        /// stitched frames, so the two edits compose rather than compete.</para>
        ///
        /// <para>The panels are wired onto the same <c>LoadImage</c> nodes as the base pass and renumbered
        /// from <c>ref_image_0</c> — the numbering those prompts were written for. The keyframe stills are
        /// left off deliberately: their whole job is to be a frame at a timestamp, and these passes have no
        /// timeline — they re-draw a 768px crop of a face the base pass already placed.</para>
        /// </summary>
        /// <param name="perCharacter">False for an item queued before the per-view cast existed: its one
        /// refine prompt is numbered for the whole cast, so that pass keeps every cast panel and no identity
        /// reference.</param>
        private static string WireRefinePasses(
            string json, IReadOnlyList<string> loaders, int castStart,
            SelectedPanels cast1, SelectedPanels cast2, bool perCharacter, bool refineCharacter2)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");
            if (root[NodeRefineReference] is not JsonObject) return json;

            var castLoaders = loaders.Skip(Math.Max(0, castStart)).ToList();
            if (castLoaders.Count == 0)
                throw new Exception("The face-refine pass has no cast panel to condition on — it would " +
                                    "redraw every face from the prompt text alone.");

            var loaders1 = castLoaders.Take(cast1.Paths.Count).ToList();
            var loaders2 = castLoaders.Skip(cast1.Paths.Count).Take(cast2.Paths.Count).ToList();

            AttachReferences(root, NodeRefineReference, perCharacter && loaders1.Count > 0 ? loaders1 : castLoaders);
            if (perCharacter && loaders1.Count > 0)
                SetIdentityReference(root, NodeFaceTrack, loaders1, cast1.FacePanel);

            json = root.ToJsonString();
            if (!refineCharacter2 || loaders2.Count == 0) return json;

            json = AddSecondRefinePass(json);
            root = JsonNode.Parse(json)!.AsObject();
            AttachReferences(root, NodeRefineReference2, loaders2);
            SetIdentityReference(root, NodeFaceTrack2, loaders2, cast2.FacePanel);
            return root.ToJsonString();

            static void AttachReferences(JsonObject root, string nodeId, IReadOnlyList<string> loaders)
            {
                if (root[nodeId]?["inputs"] is not JsonObject inputs)
                    throw new Exception($"Workflow node '{nodeId}' has no inputs — the workflow file no longer matches this tab.");

                foreach (var key in inputs.Select(kv => kv.Key)
                                          .Where(k => k.StartsWith(RefImagePrefix, StringComparison.Ordinal))
                                          .ToList())
                    inputs.Remove(key);

                for (var i = 0; i < loaders.Count; i++)
                    inputs[RefImagePrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                        new JsonArray(loaders[i], 0);
            }

            // The tracker's optional identity input: with it the subject is chosen by face identity rather
            // than by size, which is the only way two people in one frame can be told apart across the clip.
            static void SetIdentityReference(
                JsonObject root, string trackNode, IReadOnlyList<string> loaders, int facePanel)
            {
                if (root[trackNode]?["inputs"] is not JsonObject inputs) return;
                var index = Math.Clamp(facePanel, 0, loaders.Count - 1);
                inputs["identity_reference"] = new JsonArray(loaders[index], 0);
            }
        }

        /// <summary>
        /// Clones the refine chain (<c>100</c>–<c>111</c> plus its prompt primitive) into a second pass in
        /// the <c>200</c> block, reading the frames the first pass already stitched.
        ///
        /// <para>Injected here rather than shipped in the workflow file because it only exists for a clip
        /// that casts two characters — and because a hand-authored copy of eleven nodes is eleven more links
        /// to keep in step with the original every time the chain changes. Every link inside the clone is
        /// remapped to the clone; every link out of it is left alone, except the two that read the base
        /// render (<c>H3FaceTrackCrop.images</c> and <c>H3FaceStitch.base_images</c>), which are moved onto
        /// the first pass's output so the second edit lands on top of the first rather than discarding it.</para>
        /// </summary>
        private static string AddSecondRefinePass(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            var map = new Dictionary<string, string>(StringComparer.Ordinal) { [NodeRefinePrompt] = NodeRefinePrompt2 };
            foreach (var kv in root.ToList())
            {
                if (!int.TryParse(kv.Key, out var id) || id < 100 || id > 111) continue;
                map[kv.Key] = (id + RefinePass2IdOffset).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            foreach (var (source, clone) in map)
            {
                if (root[source] is not JsonObject node) continue;
                var copy = JsonNode.Parse(node.ToJsonString())!.AsObject();

                if (copy["inputs"] is JsonObject inputs)
                    foreach (var input in inputs.ToList())
                    {
                        if (input.Value is not JsonArray link || link.Count < 2) continue;
                        var from = link[0]?.GetValue<string>();
                        if (from == null) continue;
                        var slot = link[1]!.GetValue<int>();
                        var target = map.TryGetValue(from, out var mapped) ? mapped
                                   : from == NodeBaseFrames ? NodeFaceStitch
                                   : from;
                        inputs[input.Key] = new JsonArray(target, slot);
                    }

                var title = copy["_meta"]?["title"]?.GetValue<string>();
                copy["_meta"] = new JsonObject { ["title"] = $"{title ?? clone} (character 2)" };
                root[clone] = copy;
            }

            return root.ToJsonString();
        }

        /// <summary>
        /// Wires the tail of the graph — which frames reach the file, and at what rate — for the three
        /// optional passes.
        ///
        /// <para>The frames come from the face-stitch node when the refine pass runs and from the base decode
        /// when it does not; they then go through interpolation and the RTX upscale, or straight on when
        /// those are off. Whatever is left unreferenced becomes unreachable and
        /// <see cref="PruneToOutputs"/> deletes it, which is the only safe way to drop a branch: several of
        /// these nodes would otherwise still execute on their own.</para>
        /// </summary>
        private static string WireOutputChain(
            string json, bool faceRefine, bool refineCharacter2, bool interpolate, ref bool rtxUpscale)
        {
            // Character 2's pass stitches on top of character 1's, so the last stitch in the chain is the
            // one the file is made from.
            var rendered = faceRefine
                ? (refineCharacter2 ? NodeFaceStitch2 : NodeFaceStitch)
                : NodeBaseFrames;
            var frames = interpolate ? NodeInterpolate : rendered;
            SetInput(ref json, NodeInterpolate, "images", new JsonArray(rendered, 0));

            if (HasRtxNode(json))
                SetInput(ref json, NodeRtxUpscale, "images", new JsonArray(frames, 0));
            else
                // Reported by the caller, and turned off here so every downstream size and frame-stack
                // figure describes the file that is actually about to be written.
                rtxUpscale = false;

            SetInput(ref json, NodeCreateVideo, "images",
                new JsonArray(rtxUpscale ? NodeRtxUpscale : frames, 0));
            // The mux rate has to follow the frame count, or an interpolated clip plays at half speed.
            SetInput(ref json, NodeCreateVideo, "fps",
                new JsonArray(interpolate ? NodeFpsDoubled : NodeFps, 0));
            return json;
        }

        /// <summary>Whether the workflow file still carries the optional RTX upscale node.</summary>
        private static bool HasRtxNode(string json) =>
            JsonNode.Parse(json)?[NodeRtxUpscale] is JsonObject;

        /// <summary>Reads an integer widget out of the graph — used for the steps the workflow ships with,
        /// which the tab reports but never overrides.</summary>
        private static int ReadInt(string json, string nodeId, string input)
        {
            var node = JsonNode.Parse(json)?[nodeId]?["inputs"]?[input];
            return node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        }

        /// <summary>Fails loudly when a node the patches rewire is missing or is no longer the class they
        /// assume — both would otherwise produce a graph that only fails on the server, or worse, silently
        /// renders the wrong thing.</summary>
        private static void RequireClass(JsonObject root, string nodeId, string expected)
        {
            if (root[nodeId] is not JsonObject node)
                throw new Exception($"Workflow node '{nodeId}' is not in the graph — the workflow file no longer matches this tab.");
            var actual = node["class_type"]?.GetValue<string>();
            if (actual != expected)
                throw new Exception($"Workflow node '{nodeId}' is a {actual ?? "(none)"}, expected {expected} — the workflow file no longer matches this tab.");
        }

        /// <summary>
        /// Cuts the graph down to the output nodes we want plus everything they depend on, and deletes every
        /// other node outright. Pruning by reachability is the only reliable way to drop a branch: anything
        /// ending in an OUTPUT_NODE runs whether or not something downstream consumes it, so unhooking a sink
        /// is not enough on its own.
        /// </summary>
        private static string PruneToOutputs(string json, IEnumerable<string> keepOutputs, out int removed)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(keepOutputs);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!reachable.Add(id)) continue;
                if (root[id]?["inputs"] is not JsonObject inputs) continue;

                foreach (var input in inputs)
                {
                    if (input.Value is JsonArray link && link.Count == 2 && LinkSource(link[0]) is { } src)
                        stack.Push(src);
                }
            }

            removed = 0;
            foreach (var id in root.Select(kv => kv.Key).ToList())
            {
                if (reachable.Contains(id)) continue;
                root.Remove(id);
                removed++;
            }

            return root.ToJsonString();

            static string? LinkSource(JsonNode? node)
            {
                if (node is not JsonValue value) return null;
                if (value.TryGetValue<string>(out var s)) return s;
                if (value.TryGetValue<long>(out var i)) return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return null;
            }
        }

        /// <summary>Submits the workflow, waits for completion, and resolves the video sink's output to a
        /// local file — first via /history node outputs, then a disk scan for the run token.</summary>
        private async Task<string?> SubmitAndRetrieveAsync(
            string json, string runToken, string outputNode, double from, double to, CancellationToken token)
        {
            var existing = GetExistingVideoFiles("*.mp4", OutputSubfolder);
            var promptId = await SubmitAsync(json, from, to, token);

            ProcessingStatus = "Waiting for output...";
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
            if (byNode.TryGetValue(outputNode, out var outs) && outs.Count > 0)
            {
                var pick = outs.FirstOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? outs[0];
                var local = await ResolveOutputToLocalAsync(pick);
                if (local != null) return local;
            }

            var found = await WaitForNewVideoAsync(existing, "*.mp4",
                TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(4), OutputSubfolder);
            if (found != null && Path.GetFileName(found).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0)
                return found;
            return found ?? FindTokenVideoOnDisk(runToken);
        }

        /// <summary>Submits a sheet job. Unlike <see cref="SubmitAsync"/> it reports through
        /// <see cref="SheetPhase"/> and never touches the progress bar or the status line: a render may well
        /// be running underneath, and those belong to it.</summary>
        private async Task<string> SubmitSheetAsync(string json, CancellationToken token)
        {
            var workflow = JsonSerializer.Deserialize<JsonElement>(json);
            var phase = SheetPhase;
            var progress = new Progress<ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max > 0)
                    Application.Current.Dispatcher.Invoke(() =>
                        SheetPhase = $"{phase} {msg.Data.Value}/{msg.Data.Max}");
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token);
            AddLog($"Workflow submitted, ID: {promptId}");
            return promptId;
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
                    var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                    var outputFolder = settings.ResolveOutputFolder(isRemote);
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
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : string.Empty;
                var bytes = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, subfolder);
                if (bytes is { Length: > 0 })
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"h3casthybrid_{Guid.NewGuid():N}_{filename}");
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

        private string? FindTokenVideoOnDisk(string runToken)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;
                var baseUrl = GetComfyUIBaseUrl();
                var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                var outputFolder = settings.ResolveOutputFolder(isRemote);
                if (string.IsNullOrEmpty(outputFolder)) return null;

                var candidates = new List<string>();
                foreach (var folder in new[] { outputFolder, Path.Combine(outputFolder, OutputSubfolder) })
                {
                    if (Directory.Exists(folder))
                        candidates.AddRange(Directory.GetFiles(folder, "*.mp4", SearchOption.AllDirectories)
                            .Where(f => Path.GetFileName(f).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                return candidates.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
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
            OnPropertyChanged(nameof(CanBuildSheets));
            OnPropertyChanged(nameof(CanAddKeyframe));
            OnPropertyChanged(nameof(BuildSheetsButtonText));
            OnPropertyChanged(nameof(AllSheetsReady));
            OnPropertyChanged(nameof(CastSummary));
            OnPropertyChanged(nameof(PromptHealthSummary));
            BuildSheetsCommand.NotifyCanExecuteChanged();
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
        }
    }

    /// <summary>
    /// One timeline still: a picture the video must land on exactly, and the second it is locked at. Kept as
    /// its own observable so the timestamp box can be edited in place without rebuilding the list.
    /// </summary>
    public class KeyframeSlot : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly Action _onChanged;
        private double _seconds;

        public KeyframeSlot(string path, CharacterSlot.PreviewLoader loadPreview, Action onChanged)
        {
            _onChanged = onChanged;
            Path = path;
            Preview = loadPreview(path, out var info);
            Info = info;
        }

        public string Path { get; }
        public BitmapImage? Preview { get; }
        public string Info { get; }
        public bool Exists => !string.IsNullOrEmpty(Path) && File.Exists(Path);
        public string FileName => System.IO.Path.GetFileName(Path);

        /// <summary>
        /// Where in the clip this frame is locked. Clamped to H3's own range and rounded to hundredths,
        /// which is the precision the prompt states it in — a timestamp the prompt cannot express is a
        /// timestamp the model cannot honour.
        /// </summary>
        public double Seconds
        {
            get => _seconds;
            set
            {
                var snapped = Math.Round(Math.Clamp(value, 0, 15), 2);
                if (Math.Abs(_seconds - snapped) < 0.001) return;
                _seconds = snapped;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Seconds)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Label)));
                _onChanged();
            }
        }

        public string Label => $"{FileName} @ {Seconds:0.00}s";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>One entry of the tab's "how much of each sheet does H3 get" dropdown.</summary>
    public record ReferenceBudgetOption(int Value, string Label);
}
