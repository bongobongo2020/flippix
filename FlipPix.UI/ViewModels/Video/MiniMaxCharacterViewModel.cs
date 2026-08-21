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
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "MiniMax Character" tab. Reference-to-video counterpart to <see cref="MiniMaxH3TextToVideoViewModel"/>:
    /// instead of conditioning on a first frame, MiniMax H3 is handed one or two <b>character reference
    /// images</b> and keeps those characters' faces, hair and build consistent through a whole
    /// multi-shot sequence. Their <i>clothing</i> deliberately does not come from those images — see the
    /// scene image below.
    ///
    /// Three images are uploaded, and they do not all reach ComfyUI:
    /// <list type="bullet">
    /// <item><b>Character 1</b> → <c>MiniMaxH3ReferenceToVideo.ref_images.ref_image_0</c>, addressed in the
    /// prompt as <c>&lt;Picture 1&gt;</c>.</item>
    /// <item><b>Character 2</b> (optional) → <c>ref_image_1</c> / <c>&lt;Picture 2&gt;</c>. When it is left
    /// empty <see cref="PruneSecondCharacter"/> strips the slot and the graph runs as a single-character
    /// reference.</item>
    /// <item><b>Scene image</b> — never uploaded. It is only ever seen by the llama-server: Analyze turns it
    /// into the same dense ~15-second multi-shot H3 prompt the 🌀📝 tab writes (<c>texttovideoH3.md</c>),
    /// which then becomes the scene the referenced characters act out. It is also the <b>only</b> source of
    /// the cast's wardrobe: Analyze reads the outfits off this image and writes them into the prompt, and the
    /// reference line tells H3 to take clothing from that text rather than from the character frames.</item>
    /// </list>
    ///
    /// <para><b>Turbo, as shipped.</b> The graph is <c>ref2va-turbo-character.json</c>: the ref2va UNet with
    /// the lightx2v 4-step turbo LoRA, Sol-Attn + Sage patches, an H3 sigma shift, a
    /// <c>MiniMaxH3TurboSampler</c>, and an RTX Video Super Resolution ×2 finish feeding a single
    /// VHS_VideoCombine. Nothing about the model chain is patched at submit time any more — the tab only
    /// fills in the prompt, the reference images, the canvas, the duration and the seed.</para>
    ///
    /// The export leaves its input primitives (prompt, duration, resolution) disconnected from
    /// <c>MiniMaxH3ReferenceToVideo</c> with their values baked into the node itself, so those values are
    /// written straight onto the node and <see cref="PruneToOutputs"/> drops the orphans.
    ///
    /// <para><b>Queued, not blocking.</b> "Add to Queue" snapshots the whole form into a
    /// <see cref="MiniMaxCharacterQueueItem"/> and the queue drains in the background, so while one job is
    /// on the GPU the tab is still free to load a new scene image, Analyze it, and stack the next job.
    /// Only the ComfyUI submission is serialized (one item at a time, via the workflow coordinator);
    /// analysis talks to the llama-server and is deliberately never gated on it.</para>
    ///
    /// <para><b>Story mode.</b> H3 tops out at ~15 seconds, so <see cref="StoryDurationSeconds"/> (5–120 s)
    /// is delivered as a <i>chain</i> of clips: Analyze asks the LLM for
    /// <see cref="PlannedClipCount"/> complete H3 prompts in one reply, separated by
    /// <c>=== CLIP n of N ===</c> headers, each one a beat of the same story. The prompt box holds the whole
    /// chain and stays editable; "Add to Queue" splits it on those headers and enqueues one job per clip,
    /// which the existing drain loop then renders one at a time. See <see cref="SplitClips"/>.
    /// Once every clip of a chain has completed, <see cref="CompleteStoryAsync"/> FFmpeg-concatenates them
    /// in clip order into one continuous video, which becomes the tab's result.</para>
    /// </summary>
    public partial class MiniMaxCharacterViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/h3-minimax/ref2va-turbo-character.json";
        private const string OutputSubfolder = "minimax_character";
        private const string SystemPromptFile = "texttovideoH3.md";

        /// <summary>Appended to <see cref="SystemPromptFile"/> when more than one clip is being written, so
        /// the H3 format itself stays defined in exactly one place.</summary>
        private const string StorySystemPromptFile = "texttovideoH3_story.md";

        // ── Workflow node ids (locked from h3-minimax/ref2va-turbo-character.json) ────────────
        private const string NodeCharacter1 = "44";    // LoadImage → resize 13 → ref_image_0
        private const string NodeChar1Resize = "13";   // ImageResizeKJv2 — the template AddSecondCharacter clones
        private const string NodeReference = "23";     // MiniMaxH3ReferenceToVideo
        private const string NodePrompt = "48";        // PrimitiveStringMultiline → reference.prompt
        private const string NodeResolution = "45";    // ResolutionSelector → reference canvas + resize 13
        private const string NodeFrames = "5";         // ComfyMathExpression seconds → frames (output slot 1)
        private const string NodeDuration = "56";      // PrimitiveFloat seconds → node 5
        private const string NodeSeed = "46";          // RandomNoise noise_seed
        private const string NodeScheduler = "57";     // BasicScheduler (steps — read, never written)
        private const string NodeRtxUpscale = "64";    // RTXVideoSuperResolution (scale)
        private const string NodeVideoCombine = "65";  // VHS_VideoCombine — the graph's only output

        // Ids for the second reference chain, injected only when Character 2 is loaded. Outside every id
        // the export uses.
        private const string NodeCharacter2 = "910";
        private const string NodeChar2Resize = "911";

        /// <summary>
        /// H3 renders at 24 fps and the duration maths below is built on that. The workflow file agrees, but
        /// it is still written on every submit: an export at any other rate (the file arrived at 60, which
        /// would have played a 5-second clip in two) would otherwise desync every duration the tab reports
        /// and every FFmpeg join it does.
        /// </summary>
        private const int OutputFrameRate = 24;

        /// <summary>RTX Video Super Resolution factor — node 64's <c>scale</c>, mirrored here so the tab can
        /// tell the user what size the file will be before it renders.</summary>
        private const double RtxScale = 2.0;

        /// <summary>
        /// Reference line pinning both characters. Mirrors the phrasing the H3 reference workflow ships with
        /// ("use &lt;Picture n&gt; as reference frames").
        /// <para>This line is written by code, identically, into <b>every</b> clip of a chain, so the identity
        /// pin cannot drift between clips. It pins <i>identity</i> only — face, hair, build. The clip bodies
        /// are still forbidden from describing those (see <c>texttovideoH3_story.md</c>): the LLM never sees
        /// the character images, so anything it writes about them is invented afresh per clip, and H3 follows
        /// that text over the reference frame.</para>
        /// <para><b>Clothing is deliberately excluded from the pin.</b> The wardrobe is supposed to come from
        /// the <i>scene</i> image — the one image the LLM does see — so the prompt body dresses the cast and
        /// this line has to say so explicitly, otherwise it fights the description and H3 falls back to the
        /// outfit in the character reference frames.</para>
        /// </summary>
        private const string RefInstructionTwo =
            "For the target video, use <Picture 1> and <Picture 2> as reference frames — Character 1 is <Picture 1> and Character 2 is <Picture 2>; keep each one's face, hair and build exactly as shown in their reference frame, identical and unchanged from the first frame to the last. Their clothing is NOT taken from the reference frames: dress each of them strictly in the outfit described below and keep that outfit unchanged throughout.";

        private const string RefInstructionOne =
            "For the target video, use <Picture 1> as a reference frame — Character 1 is <Picture 1>; keep their face, hair and build exactly as shown in the reference frame, identical and unchanged from the first frame to the last. Their clothing is NOT taken from the reference frame: dress them strictly in the outfit described below and keep that outfit unchanged throughout.";

        /// <summary>Every anchor/reference line the tab writes starts with this, so an existing one can be
        /// found and rewritten when the character count changes.</summary>
        private const string RefLinePrefix = "For the target video,";

        // ── Input state ────────────────────────────────────────────────────────
        private string _character1Path = string.Empty;
        private BitmapImage? _character1Preview;
        private string _character1Info = string.Empty;

        private string _character2Path = string.Empty;
        private BitmapImage? _character2Preview;
        private string _character2Info = string.Empty;

        private string _sceneImagePath = string.Empty;
        private BitmapImage? _sceneImagePreview;
        private string _sceneImageInfo = string.Empty;

        private string _prompt = string.Empty;
        private int _promptClipCount;
        private string _storyGuidance = string.Empty;
        private double _storyDurationSeconds = 10;
        private string _selectedAspectRatio = H3Canvas.AutoAspect;
        private double _megapixels = 1.0;
        private double _lengthSeconds = 10;
        private long _seed = -1;
        private bool _isAnalyzing;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _analyzeCts;

        // ── Queue ──────────────────────────────────────────────────────────────
        private readonly ObservableCollection<MiniMaxCharacterQueueItem> _queue = new();
        private CancellationTokenSource? _queueCts;
        private bool _isProcessingQueue;
        private string _queueStatus = string.Empty;

        private static string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "minimaxcharacter_queue.json");

        // ── Scene library ──────────────────────────────────────────────────────
        private readonly ScenePromptLibrary _sceneLibrary;
        /// <summary>Serializes every read-modify-write of <see cref="_scenes"/>, which is mutated from the
        /// UI thread but written (and thumbnailed) on background threads.</summary>
        private readonly SemaphoreSlim _sceneLock = new(1, 1);
        private List<ScenePrompt>? _scenes;
        private int _savedSceneCount;

        public MiniMaxCharacterViewModel(
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
            _sceneLibrary = new ScenePromptLibrary(AddLog);

            SelectCharacter1Command = new RelayCommand(async () => await SelectCharacter1Async());
            SelectCharacter2Command = new RelayCommand(async () => await SelectCharacter2Async());
            ClearCharacter2Command = new RelayCommand(() => Character2Path = string.Empty);
            SelectSceneImageCommand = new RelayCommand(async () => await SelectSceneImageAsync());
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            GenerateCommand = new RelayCommand(AddToQueue, () => CanGenerate);
            CancelCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            RemoveQueueItemCommand = new RelayCommand<MiniMaxCharacterQueueItem>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => HasQueueItems);
            StartQueueCommand = new RelayCommand(() => _ = ProcessQueueAsync(), () => HasPendingItems && !IsProcessingQueue);
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(ReprocessAllFailed, () => HasFailedItems);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            RandomSeedCommand = new RelayCommand(() => Seed = System.Random.Shared.NextInt64(0, long.MaxValue));
            OpenSceneLibraryCommand = new RelayCommand(async () => await OpenSceneLibraryAsync());
            JoinClipsCommand = new RelayCommand(async () => await JoinClipsManuallyAsync(), () => !IsJoining);
            SaveSceneCommand = new RelayCommand(async () => await SaveCurrentSceneAsync(manual: true),
                () => !string.IsNullOrWhiteSpace(Prompt));

            _queue.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
            };

            AddLog("MiniMax Character initialized");

            // Reading the index is cheap (thumbnails are separate files), but it is still disk I/O on a
            // startup-path view model — keep it off the constructor's thread. See the tab's startup notes.
            _ = PrimeSceneLibraryAsync();
            ScheduleQueueLoad();
        }

        #region Commands

        public ICommand SelectCharacter1Command { get; }
        public ICommand SelectCharacter2Command { get; }
        public ICommand ClearCharacter2Command { get; }
        public ICommand SelectSceneImageCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        /// <summary>Named for the button it has always driven; it now enqueues instead of running inline.</summary>
        public RelayCommand GenerateCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand<MiniMaxCharacterQueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StartQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand RandomSeedCommand { get; }
        public RelayCommand OpenSceneLibraryCommand { get; }
        public RelayCommand SaveSceneCommand { get; }
        /// <summary>Joins clips already on disk — see <see cref="JoinClipsManuallyAsync"/>.</summary>
        public RelayCommand JoinClipsCommand { get; }

        #endregion

        #region Image inputs

        /// <summary>Character 1 — uploaded to ComfyUI as <c>ref_image_0</c> / <c>&lt;Picture 1&gt;</c>.</summary>
        public string Character1Path
        {
            get => _character1Path;
            set
            {
                if (_character1Path == value) return;
                _character1Path = value;
                _character1Preview = LoadImagePreview(value, out _character1Info);
                OnPropertyChanged();
                OnPropertyChanged(nameof(Character1Preview));
                OnPropertyChanged(nameof(Character1Info));
                OnPropertyChanged(nameof(HasCharacter1));
                OnCanExecuteChanged();
            }
        }

        public BitmapImage? Character1Preview => _character1Preview;
        public string Character1Info => _character1Info;
        public bool HasCharacter1 => !string.IsNullOrEmpty(Character1Path) && File.Exists(Character1Path);

        /// <summary>Character 2 — optional. Empty means the graph runs with a single reference image.</summary>
        public string Character2Path
        {
            get => _character2Path;
            set
            {
                if (_character2Path == value) return;
                _character2Path = value;
                _character2Preview = LoadImagePreview(value, out _character2Info);
                OnPropertyChanged();
                OnPropertyChanged(nameof(Character2Preview));
                OnPropertyChanged(nameof(Character2Info));
                OnPropertyChanged(nameof(HasCharacter2));
                OnPropertyChanged(nameof(CharacterSummary));
                OnCanExecuteChanged();
            }
        }

        public BitmapImage? Character2Preview => _character2Preview;
        public string Character2Info => _character2Info;
        public bool HasCharacter2 => !string.IsNullOrEmpty(Character2Path) && File.Exists(Character2Path);

        public string CharacterSummary => HasCharacter2
            ? "Two references — <Picture 1> and <Picture 2> both stay on model."
            : "One reference — add a second image for a two-character scene.";

        /// <summary>
        /// Scene image — never uploaded to ComfyUI. It is the only thing Analyze looks at, and the prompt
        /// it produces is what the referenced characters end up acting out.
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
                OnCanExecuteChanged();
            }
        }

        public BitmapImage? SceneImagePreview => _sceneImagePreview;
        public string SceneImageInfo => _sceneImageInfo;
        public bool HasSceneImage => !string.IsNullOrEmpty(SceneImagePath) && File.Exists(SceneImagePath);

        private Task SelectCharacter1Async() => PickImageAsync("Select Character 1", "minimaxchar.char1",
            path => { Character1Path = path; AddLog($"Character 1: {Path.GetFileName(path)}"); });

        private Task SelectCharacter2Async() => PickImageAsync("Select Character 2", "minimaxchar.char2",
            path => { Character2Path = path; AddLog($"Character 2: {Path.GetFileName(path)}"); });

        private Task SelectSceneImageAsync() => PickImageAsync("Select Scene Image", "minimaxchar.scene",
            path => { SceneImagePath = path; AddLog($"Scene image: {Path.GetFileName(path)}"); });

        private async Task PickImageAsync(string title, string persistKey, Action<string> apply)
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                title,
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: persistKey);

            if (path != null) apply(path);
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

        #region Settings

        /// <summary>
        /// The full H3 prompt: reference line + the three core fields. In story mode it holds the whole
        /// chain — one such prompt per clip, separated by <c>=== CLIP n of N ===</c> headers — and is what
        /// "Add to Queue" splits, so hand-edits to the chain are honoured.
        /// </summary>
        public string Prompt
        {
            get => _prompt;
            set
            {
                if (_prompt == value) return;
                _prompt = value;
                // Cached: the prompt box updates on every keystroke and a chain is tens of kilobytes.
                _promptClipCount = SplitClips(_prompt).Count;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PromptClipCount));
                OnPropertyChanged(nameof(HasPromptSequence));
                OnPropertyChanged(nameof(PromptClipSummary));
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Free-text story the whole video should follow. Optional: left empty, Analyze invents a story that
        /// suits the scene image. Filled, it is the authority the LLM writes every clip against.
        /// </summary>
        public string StoryGuidance
        {
            get => _storyGuidance;
            set { if (_storyGuidance != value) { _storyGuidance = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Target length of the <i>finished</i> video, 5–120 s in 5 s steps. H3 renders at most ~15 s per
        /// pass, so anything longer than <see cref="LengthSeconds"/> is split into
        /// <see cref="PlannedClipCount"/> clips — see the type remarks.
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
                OnPropertyChanged(nameof(StorySummary));
            }
        }

        /// <summary>How many clips Analyze will be asked for: the target duration divided by the per-clip
        /// length, rounded up. 1 means the tab behaves exactly as it did before story mode existed.</summary>
        public int PlannedClipCount =>
            Math.Max(1, (int)Math.Ceiling(StoryDurationSeconds / ClampLength(LengthSeconds) - 0.0001));

        public bool IsStorySequence => PlannedClipCount > 1;

        public string StorySummary
        {
            get
            {
                var clip = ClampLength(LengthSeconds);
                var n = PlannedClipCount;
                if (n <= 1)
                    return $"One clip of {clip:0.#}s — a single H3 pass.";
                return $"{n} clips × {clip:0.#}s → {n * clip:0.#}s of video, rendered one at a time " +
                       "and joined into a single file when the last one lands.";
            }
        }

        /// <summary>How many clips the prompt box <i>actually</i> holds — what Generate will queue.</summary>
        public int PromptClipCount => _promptClipCount;

        public bool HasPromptSequence => PromptClipCount > 1;

        public string PromptClipSummary =>
            PromptClipCount > 1
                ? $"This prompt holds {PromptClipCount} clips — Generate queues {PromptClipCount} jobs, in order."
                : string.Empty;

        public IReadOnlyList<string> AspectRatioOptions { get; } =
            new[] { H3Canvas.AutoAspect }
                .Concat(H3Canvas.AspectRatios.Select(a => a.Option)).ToList();

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
            }
        }

        /// <summary>The aspect actually sent to ComfyUI — the picked one, or the scene image's closest match.</summary>
        public string ResolvedAspectRatio =>
            SelectedAspectRatio == H3Canvas.AutoAspect
                ? ClosestAspectRatio(SceneImagePath)
                : SelectedAspectRatio;

        /// <summary>Same ResolutionSelector presets as the other H3 tabs — H3's native canvas is a 768px short edge.</summary>
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
            }
        }

        public double LengthSeconds
        {
            get => _lengthSeconds;
            set
            {
                if (Math.Abs(_lengthSeconds - value) > 0.0001)
                {
                    _lengthSeconds = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LengthSummary));
                    // The clip length is the divisor behind the story split, so the plan moves with it.
                    OnPropertyChanged(nameof(PlannedClipCount));
                    OnPropertyChanged(nameof(IsStorySequence));
                    OnPropertyChanged(nameof(StorySummary));
                }
            }
        }

        /// <summary>Shows the frame count the workflow's math node will actually snap the duration to.</summary>
        public string LengthSummary
        {
            get
            {
                var len = ClampLength(LengthSeconds);
                return $"{len:0.#}s → {FramesForSeconds(len)} frames @ 24 fps";
            }
        }

        public long Seed
        {
            get => _seed;
            set { if (_seed != value) { _seed = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// What the saved file will be: the H3 canvas run through the fixed RTX ×2 pass. No second
        /// diffusion model is involved, so this costs a few seconds rather than a second render.
        /// </summary>
        public string UpscaleSummary
        {
            get
            {
                var (w, h) = UpscaleSize(ResolvedAspectRatio, Megapixels);
                return $"Output: RTX ×2 super-resolution → ≈{w}×{h}. No second model is loaded.";
            }
        }

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (_isAnalyzing == value) return;
                _isAnalyzing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanAnalyze));
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Analyze needs the scene image — it is the only thing the LLM is shown. Deliberately <i>not</i>
        /// gated on <see cref="VideoProcessingBaseViewModel.IsProcessing"/>: analysis runs against the
        /// llama-server, so the next scene can be written while the queue is busy on the GPU.
        /// </summary>
        public bool CanAnalyze => HasSceneImage && !IsAnalyzing;

        /// <summary>
        /// Queueing needs a prompt and at least the first character reference. A render in flight does not
        /// block it — that is the whole point of the queue. An in-flight <i>analysis</i> does, because it is
        /// about to overwrite the prompt box that would be snapshotted.
        /// </summary>
        public bool CanGenerate => !string.IsNullOrWhiteSpace(Prompt) && HasCharacter1 && !IsAnalyzing;

        /// <summary>Maps the scene image's own aspect to the nearest ratio the ResolutionSelector offers.</summary>
        private string ClosestAspectRatio(string path)
        {
            int w = 0, h = 0;
            if (SceneImagePreview is { } preview && string.Equals(path, SceneImagePath, StringComparison.OrdinalIgnoreCase))
            {
                w = preview.PixelWidth; h = preview.PixelHeight;
            }
            if ((w <= 0 || h <= 0) && !string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    w = frame.PixelWidth; h = frame.PixelHeight;
                }
                catch { /* fall through to the 16:9 default */ }
            }
            return H3Canvas.ClosestAspectRatio(w, h);
        }

        /// <summary>
        /// The canvas node 45 will pick, for display only — the graph itself takes it straight from
        /// ResolutionSelector. Mirrors that node's maths: the aspect's area at this megapixel count, the
        /// width snapped to a multiple of 32, and the height then derived from the <i>snapped</i> width
        /// (which is what makes 16:9 at 1.0 MP come out 1344×768 rather than 1344×736).
        /// </summary>
        private static (int Width, int Height) CanvasSize(string aspectOption, double megapixels)
        {
            var ratio = H3Canvas.AspectRatios
                .FirstOrDefault(a => a.Option == aspectOption).Ratio;
            if (ratio <= 0) ratio = 16.0 / 9.0;

            var area = Math.Max(0.1, megapixels) * 1_000_000.0;
            var w = RoundTo32(Math.Sqrt(area * ratio));
            return (w, RoundTo32(w / ratio));

            static int RoundTo32(double v) => Math.Max(32, (int)Math.Round(v / 32.0) * 32);
        }

        /// <summary>The size that reaches the file: <see cref="CanvasSize"/> through the graph's RTX ×2 pass.</summary>
        private static (int Width, int Height) UpscaleSize(string aspectOption, double megapixels)
        {
            var (w, h) = CanvasSize(aspectOption, megapixels);
            return ((int)(w * RtxScale), (int)(h * RtxScale));
        }

        /// <summary>H3's supported clip length is 4–15 seconds at 24 fps.</summary>
        private static double ClampLength(double seconds) =>
            Math.Clamp(seconds <= 0 ? 10 : seconds, 4, 15);

        /// <summary>Mirrors node 131's expression: 24 fps snapped up onto the model's 17k+5 frame grid.</summary>
        private static int FramesForSeconds(double seconds)
        {
            var frames = Math.Max(5, (int)Math.Round(seconds * 24));
            return frames + (5 - frames % 17 + 17) % 17;
        }

        #endregion

        #region Analysis (scene image → multi-shot H3 prompt)

        private async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;

            try
            {
                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);

                var models = await _lmStudioService.GetAvailableModelsAsync(token);
                var model = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(model) && models.Count > 0)
                    model = models[0].Id ?? models[0].Name ?? string.Empty;
                if (string.IsNullOrEmpty(model))
                {
                    MessageBox.Show("No LM Studio / llama-server model available. Ensure the server is running and a model is loaded.",
                        "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var len = ClampLength(LengthSeconds);
                var clipCount = PlannedClipCount;

                AddLog(clipCount > 1
                    ? $"Writing a {clipCount}-clip story chain ({clipCount} × {len:0.#}s = {clipCount * len:0.#}s) — sending to {_lmStudioService.DescribeTarget(model)}"
                    : $"Writing a {len:0.#}s multi-shot H3 prompt — sending to {_lmStudioService.DescribeTarget(model)}");

                var systemPrompt = await ReadSystemPromptAsync(SystemPromptFile, token);
                if (clipCount > 1)
                    systemPrompt += "\n\n" + await ReadSystemPromptAsync(StorySystemPromptFile, token);

                // The current prompt box doubles as the user's draft idea for the rewrite — unless it
                // already holds a chain, which is far too long to feed back in as a "draft".
                var draft = PromptClipCount > 1
                    ? "(the prompt box holds a previous sequence — ignore it and write a fresh one)"
                    : string.IsNullOrWhiteSpace(Prompt)
                        ? "(none — invent a dynamic sequence that suits the scene)"
                        : StripReferenceLine(Prompt).Trim();

                // The scene image is REFERENCE ONLY (it is never uploaded), but the *characters* are real
                // reference frames the generator will see, so their identity is named rather than described.
                // Wardrobe is the deliberate exception: it comes from the scene image, which is the only
                // image the LLM can actually see — so it must be written out, and written out identically
                // everywhere, or across a chain it is re-invented per clip and the cast changes outfits
                // mid-story.
                const string wardrobeRule =
                    "CLOTHING IS THE ONE EXCEPTION, and it is a hard requirement: the cast must be dressed exactly as the people in the SCENE image are dressed, NOT as their own reference frames show them. Read the wardrobe off the scene image and write it out explicitly the first time each character appears in a clip — garments, colours, materials, footwear, headwear and worn accessories — attached to their tag, e.g. \"<Picture 1>, wearing a <full outfit description from the scene image>,\". Then restate that same outfit in exactly the same words every later time it is mentioned, in every shot and in every clip. If the scene image shows no people, dress them in what the setting plainly calls for and keep that wording identical throughout.";

                var cast = HasCharacter2
                    ? "Two character reference images will additionally be given to the video model and are addressed as <Picture 1> (Character 1) and <Picture 2> (Character 2). You are NOT shown those images — the video model is. Write both characters into the action and refer to them ONLY by those tags. Do not write any word for their hair, face, skin, build or age; the tag already carries all of it, and anything you invent overrides the real reference frame. " + wardrobeRule
                    : "One character reference image will additionally be given to the video model and is addressed as <Picture 1> (Character 1). You are NOT shown that image — the video model is. Write them into the action and refer to them ONLY by that tag. Do not write any word for their hair, face, skin, build or age; the tag already carries all of it, and anything you invent overrides the real reference frame. " + wardrobeRule;

                var story = string.IsNullOrWhiteSpace(StoryGuidance)
                    ? "(none — invent a story that suits the scene and carry it from beginning to end)"
                    : StoryGuidance.Trim();

                var lengthBlock = clipCount > 1
                    ? $"Story sequence: write {clipCount} clips that together tell ONE continuous story " +
                      $"running about {clipCount * len:0.##} seconds in total. Each clip is " +
                      $"{len:0.##} seconds long and is rendered separately, so each one must be a complete, " +
                      $"self-contained H3 prompt. Separate them with a line spelled exactly " +
                      $"\"=== CLIP n of {clipCount} ===\", numbered 1 to {clipCount} in order.\n"
                    : $"Target duration: {len:0.##} seconds.\n";

                var userMessage =
                    "Image role: REFERENCE ONLY — this image is the SCENE (setting, lighting, art style, mood " +
                    "and the wardrobe the cast wears). The video does not start on it and the generator will " +
                    "never see it, so describe the environment — and the clothing — explicitly.\n" +
                    $"{cast}\n" +
                    lengthBlock +
                    $"Story the video must tell:\n{story}\n" +
                    $"Draft idea from the user:\n{draft}";

                // A chain needs headroom for N prompts, not one. One H3 prompt runs ~700 tokens, so the
                // single-clip 6000 is already generous — each extra clip only needs a fraction of that,
                // and the total is capped so the request cannot exceed a modest local context window.
                var maxTokens = Math.Min(32000, 6000 + 2500 * (Math.Max(1, clipCount) - 1));

                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    model,
                    SceneImagePath,
                    userMessage,
                    systemPrompt,
                    maxTokens: maxTokens,
                    cancellationToken: token);

                var cleaned = ApplyReferenceLineToChain(CleanOutput(result), HasCharacter2);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    Prompt = cleaned;
                    var written = PromptClipCount;
                    AddLog(written > 1
                        ? $"Chain written ({written} clips, {cleaned.Length} chars, {CountShots(cleaned)} shots total)"
                        : $"Prompt written ({cleaned.Length} chars, {CountShots(cleaned)} shots)");

                    if (written != clipCount)
                        AddLog($"WARNING: asked for {clipCount} clip(s) but the model returned {written}. " +
                               "Generate will queue what is in the prompt box — re-run Analyze or edit the headers by hand.");

                    // Checked on the bodies alone: the reference line is code-written and identical in every
                    // clip, so including it would only ever mask a real disagreement.
                    var drift = DescribeWardrobeDrift(SplitClips(cleaned).Select(StripReferenceLine).ToList());
                    if (drift != null)
                        AddLog($"WARNING: the clips describe the characters' appearance and {drift}. " +
                               "The wardrobe is supposed to be read off the scene image and worded identically " +
                               "in every clip, so the cast may change outfits between clips — re-run Analyze, " +
                               "or harmonise those words in the prompt box before generating.");

                    await SaveCurrentSceneAsync(manual: false);
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

        private static async Task<string> ReadSystemPromptAsync(string fileName, CancellationToken token)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"System prompt not found: {path}");
            return await File.ReadAllTextAsync(path, token);
        }

        /// <summary>
        /// Strips the wrappers small vision models like to add (code fences, bold markers, a leading
        /// "prompt:" label, surrounding quotes) without touching the H3 field structure.
        /// </summary>
        private static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Replace("**", "").Trim();

            // Unwrap a ```/```text fenced block.
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

        /// <summary>Removes a leading reference/anchor line so it can be rewritten for the current cast.</summary>
        private static string StripReferenceLine(string prompt)
        {
            var t = (prompt ?? string.Empty).Trim();
            if (!t.StartsWith(RefLinePrefix, StringComparison.OrdinalIgnoreCase)) return t;

            var idx = t.IndexOf("integrated_multimodal_description:", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return t[idx..].Trim();

            var nl = t.IndexOf('\n');
            return nl > 0 ? t[(nl + 1)..].Trim() : string.Empty;
        }

        /// <summary>
        /// Rewrites one clip's reference line so it always matches the number of character images actually
        /// wired in — a prompt that mentions &lt;Picture 2&gt; with no second reference has nothing to
        /// resolve. The cast is passed in rather than read from the tab, because a queued item's line has
        /// to describe the cast it was queued with, not the pair sitting in the tab now.
        /// </summary>
        private static string ApplyReferenceLine(string prompt, bool twoCharacters)
        {
            var body = StripReferenceLine(prompt);
            if (body.Length == 0) return string.Empty;
            return $"{(twoCharacters ? RefInstructionTwo : RefInstructionOne)}\n\n{body}";
        }

        #endregion

        #region Clip chain (story mode)

        /// <summary>The header written between clips. The LLM is told to emit exactly this shape.</summary>
        private const string ClipHeaderFormat = "=== CLIP {0} of {1} ===";

        /// <summary>
        /// Matches a clip header on a line of its own. Deliberately loose about the decoration around it
        /// (<c>===</c>, <c>##</c>, <c>[CLIP 3]</c>, <c>Clip 3:</c> — small models produce all of them) but
        /// capped at 60 characters so a line of prompt body that happens to start with the word can never
        /// be mistaken for a header.
        /// </summary>
        private static readonly Regex ClipHeaderRegex = new(
            @"^[ \t]*[=#*\-–—\[]{0,6}[ \t]*CLIP[ \t]+(\d+)\b[^\r\n]{0,60}$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Splits a prompt chain into its individual clip prompts, headers removed. Text with no headers is
        /// one clip, so every caller can treat the single-clip case as a chain of length 1. Empty text
        /// yields an empty list.
        /// </summary>
        private static List<string> SplitClips(string? text)
        {
            // Normalized first: `$` in a .NET multiline match sits *before* the \n, so a CRLF header line
            // would never match — and the prompt box hands back CRLF the moment it is edited by hand.
            var t = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (t.Length == 0) return new List<string>();

            var headers = ClipHeaderRegex.Matches(t);
            if (headers.Count == 0) return new List<string> { t };

            var clips = new List<string>();
            // Anything ahead of the first header is a preamble the model added; it belongs to clip 1.
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

        /// <summary>Reassembles clip prompts into one chain. A single clip is returned bare — headers only
        /// appear once there is actually a sequence.</summary>
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

        /// <summary>Chain-aware <see cref="ApplyReferenceLine(string, bool)"/> — every clip needs its own
        /// reference line, because every clip is submitted to H3 as a separate prompt.</summary>
        private static string ApplyReferenceLineToChain(string prompt, bool twoCharacters)
        {
            var clips = SplitClips(prompt);
            if (clips.Count == 0) return string.Empty;
            return JoinClips(clips.Select(c => ApplyReferenceLine(c, twoCharacters))
                                  .Where(c => c.Length > 0).ToList());
        }

        /// <summary>
        /// Wearable and hairstyle words. Present only so <see cref="DescribeWardrobeDrift"/> can spot a chain
        /// whose clips dress the characters differently — deliberately nouns, not colours, because lighting
        /// colours legitimately change from beat to beat.
        /// </summary>
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

        /// <summary>
        /// Flags wardrobe drift across a chain: an appearance word that appears in some clips but not all.
        /// Returns null when there is nothing to report.
        ///
        /// <para>The LLM is told to dress the cast from the <i>scene</i> image and to restate that wardrobe in
        /// the same words in every clip (identity — face, hair, build — it must not describe at all, since it
        /// has never seen the character images). When it drifts it does so <i>per clip</i>, and the result is
        /// a character who changes outfit mid-story. That only becomes visible after every clip has rendered,
        /// which is minutes of GPU time per clip, so it is worth catching the moment the prompt is
        /// written.</para>
        ///
        /// <para>Only <b>inconsistent</b> terms are reported, which is what keeps the check quiet: a coat
        /// rack that is genuinely part of the scene gets restated in every clip and never trips it.</para>
        /// </summary>
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

        /// <summary>Chain-aware <see cref="StripReferenceLine"/>, used when filing a chain in the library.</summary>
        private static string StripReferenceLineFromChain(string prompt)
        {
            var clips = SplitClips(prompt);
            if (clips.Count == 0) return string.Empty;
            return JoinClips(clips.Select(StripReferenceLine).Where(c => c.Length > 0).ToList());
        }

        #endregion

        #region Analysis helpers

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

        #region Scene library

        /// <summary>
        /// How many scenes are saved. Drives the button caption so the library advertises itself without
        /// needing a panel of its own.
        /// </summary>
        public int SavedSceneCount
        {
            get => _savedSceneCount;
            private set
            {
                if (_savedSceneCount == value) return;
                _savedSceneCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SceneLibraryLabel));
            }
        }

        public string SceneLibraryLabel =>
            SavedSceneCount > 0 ? $"📚 Scene Library ({SavedSceneCount})" : "📚 Scene Library";

        /// <summary>Reads the index in the background so the button can show a count from the first paint.</summary>
        private async Task PrimeSceneLibraryAsync()
        {
            try
            {
                await EnsureScenesLoadedAsync();
            }
            catch (Exception ex)
            {
                AddLog($"Scene library unavailable: {ex.Message}");
            }
        }

        private async Task EnsureScenesLoadedAsync()
        {
            if (_scenes != null) return;
            await _sceneLock.WaitAsync();
            try
            {
                if (_scenes != null) return;
                _scenes = await _sceneLibrary.LoadAsync();
                SavedSceneCount = _scenes.Count;
            }
            finally
            {
                _sceneLock.Release();
            }
        }

        /// <summary>
        /// Files the current prompt in the library. Called automatically after every Analyze and every
        /// successful Generate, so the library fills up on its own; <paramref name="manual"/> is the
        /// explicit Save button, which is the only caller that reports back when nothing was new.
        ///
        /// <para>What gets stored is the prompt <b>body</b> — the reference line is stripped, because it is
        /// rewritten from the character images loaded at the time the scene is recalled.</para>
        /// </summary>
        private Task SaveCurrentSceneAsync(bool manual) =>
            SaveSceneAsync(Prompt, SceneImagePath, ClampLength(LengthSeconds), ResolvedAspectRatio,
                StoryDurationSeconds, manual);

        /// <summary>
        /// Files an explicit prompt/scene pair. The queue calls this with the finished item's own snapshot,
        /// because by the time a job completes the tab may already be showing the next scene.
        /// <para>A whole clip chain is filed as one entry, headers and all, so recalling it restores the
        /// entire story rather than a single beat of it.</para>
        /// </summary>
        private async Task SaveSceneAsync(string prompt, string sceneImagePath, double lengthSeconds,
            string aspectRatio, double storyDurationSeconds, bool manual)
        {
            var body = StripReferenceLineFromChain(prompt).Trim();
            if (body.Length == 0)
            {
                if (manual) AddLog("Nothing to save — the prompt box is empty.");
                return;
            }

            var held = false;
            try
            {
                await EnsureScenesLoadedAsync();
                await _sceneLock.WaitAsync();
                held = true;

                var scenes = _scenes!;
                var hasScene = !string.IsNullOrEmpty(sceneImagePath) && File.Exists(sceneImagePath);
                var draft = new ScenePrompt
                {
                    Name = ScenePromptLibrary.SuggestName(sceneImagePath, body, scenes),
                    Prompt = body,
                    SceneImagePath = hasScene ? sceneImagePath : string.Empty,
                    AspectRatio = aspectRatio,
                    LengthSeconds = ClampLength(lengthSeconds),
                    StoryDurationSeconds = storyDurationSeconds,
                    Shots = CountShots(body),
                };

                // Thumbnail encoding runs inside AddOrRefresh — keep the whole thing off the UI thread.
                var (entry, isNew) = await Task.Run(() => _sceneLibrary.AddOrRefresh(scenes, draft));
                await _sceneLibrary.SaveAsync(scenes);
                SavedSceneCount = scenes.Count;

                AddLog(isNew
                    ? $"Saved to the scene library as \"{entry.Name}\" ({SavedSceneCount} scenes)."
                    : $"Already in the scene library as \"{entry.Name}\" — timestamp refreshed.");
            }
            catch (Exception ex)
            {
                // Never let a library problem fail the Analyze or Generate that triggered it.
                AddLog($"Could not save to the scene library: {ex.Message}");
            }
            finally
            {
                if (held) _sceneLock.Release();
            }
        }

        /// <summary>
        /// Opens the picker and, on a pick, drops the saved body into the prompt box with a reference line
        /// written for the characters that are loaded <i>now</i>. The duration and aspect the prompt was
        /// authored against come back with it — a 14-shot prompt recalled at 4 seconds would be truncated.
        /// </summary>
        private async Task OpenSceneLibraryAsync()
        {
            try
            {
                await EnsureScenesLoadedAsync();

                var window = new ScenePromptLibraryWindow(_sceneLibrary, _scenes!);
                window.Owner = Application.Current?.Windows
                    .OfType<System.Windows.Window>()
                    .FirstOrDefault(w => w.IsActive);
                // CenterOwner with no owner lands the window in the top-left corner.
                if (window.Owner == null)
                    window.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

                var picked = window.ShowDialog() == true ? window.SelectedScene : null;
                SavedSceneCount = _scenes!.Count;
                if (picked == null) return;

                Prompt = ApplyReferenceLineToChain(picked.Prompt, HasCharacter2);

                if (picked.LengthSeconds > 0)
                    LengthSeconds = ClampLength(picked.LengthSeconds);

                // Older entries predate story mode and have no total; fall back to the clip length so the
                // planner agrees with the single clip that was actually saved.
                StoryDurationSeconds = picked.StoryDurationSeconds > 0
                    ? picked.StoryDurationSeconds
                    : ClampLength(picked.LengthSeconds);

                if (!string.IsNullOrEmpty(picked.AspectRatio) && AspectRatioOptions.Contains(picked.AspectRatio))
                    SelectedAspectRatio = picked.AspectRatio;

                var clips = PromptClipCount;
                AddLog($"Loaded \"{picked.Name}\" from the scene library " +
                       $"({(clips > 1 ? $"{clips} clips, " : string.Empty)}{picked.Shots} shots, " +
                       $"{ClampLength(picked.LengthSeconds):0.#}s per clip, {ResolvedAspectRatio}) — " +
                       $"reference line rewritten for {(HasCharacter2 ? "2 characters" : "1 character")}.");
            }
            catch (Exception ex)
            {
                AddLog($"Scene library failed to open: {ex.Message}");
                MessageBox.Show($"Could not open the scene library:\n{ex.Message}",
                    "Scene Library", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Queue

        public ObservableCollection<MiniMaxCharacterQueueItem> Queue => _queue;

        public bool HasQueueItems => _queue.Count > 0;
        public bool HasPendingItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Pending);
        public bool HasFailedItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        /// <summary>True while the drain loop is alive — one ComfyUI submission at a time.</summary>
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
        /// Freezes the entire form into queue items and starts the drain loop if it is not already
        /// running. Nothing here waits on the GPU, so the tab stays usable the moment the button is hit.
        ///
        /// <para>The prompt box — not the duration slider — decides how many items are queued: it is split
        /// on its <c>=== CLIP n of N ===</c> headers and each clip becomes one job, so a chain the user has
        /// hand-edited (a clip deleted, a header added) queues exactly what is on screen. The clips are
        /// added in order and the drain loop always takes the first Pending item, so they render in story
        /// order.</para>
        /// </summary>
        private void AddToQueue()
        {
            if (!CanGenerate) return;

            var clips = SplitClips(Prompt);
            if (clips.Count == 0) return;

            // Shared by every clip of one story: groups the output files and labels the queue rows.
            var storyId = clips.Count > 1
                ? $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..20]
                : string.Empty;

            for (var i = 0; i < clips.Count; i++)
            {
                var item = new MiniMaxCharacterQueueItem
                {
                    Character1Path = Character1Path,
                    Character2Path = HasCharacter2 ? Character2Path : string.Empty,
                    SceneImagePath = HasSceneImage ? SceneImagePath : string.Empty,
                    // The reference line is baked in now: it has to name this item's cast, not whichever
                    // characters happen to be loaded when the item eventually runs.
                    Prompt = ApplyReferenceLine(clips[i], HasCharacter2),
                    AspectRatio = ResolvedAspectRatio,
                    Megapixels = Megapixels,
                    LengthSeconds = ClampLength(LengthSeconds),
                    Seed = Seed,
                    StoryId = storyId,
                    ClipIndex = i + 1,
                    ClipCount = clips.Count,
                    ItemStatus = QueueItemStatus.Pending,
                };

                _queue.Add(item);
                AddLog($"Queued: {item.DisplayText}");
            }

            SaveQueueToFile();
            if (clips.Count > 1)
            {
                AddLog($"Story queued: {clips.Count} clips × {ClampLength(LengthSeconds):0.#}s " +
                       $"→ {clips.Count * ClampLength(LengthSeconds):0.#}s of video, rendering one at a time.");
                // Filed once, as a whole chain — the per-item save on completion would otherwise drop one
                // library entry per clip.
                _ = SaveCurrentSceneAsync(manual: false);
            }
            UpdateQueueStatus();

            if (!IsProcessingQueue) _ = ProcessQueueAsync();
        }

        private void RemoveQueueItem(MiniMaxCharacterQueueItem? item)
        {
            // A Processing item is mid-submission; removing it would orphan the run, not stop it.
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
        /// Drains pending items one at a time. The workflow-coordinator lease is taken <b>per item</b>
        /// rather than around the loop, so a long character queue does not lock every other tab out of
        /// ComfyUI for its whole run — and so items added mid-drain are picked up on the next pass.
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;

            IsProcessingQueue = true;
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;

            AddLog("Starting MiniMax Character queue...");
            try
            {
                MiniMaxCharacterQueueItem? item;
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
                        // Never throws — a join problem must not push a rendered clip back to Pending.
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
        /// its clips, in <see cref="MiniMaxCharacterQueueItem.ClipIndex"/> order, into one continuous video.
        ///
        /// <para>Deliberately exception-free. It is called from the drain loop straight after an item has
        /// been marked Completed, and that loop's catch would otherwise read a join failure as a render
        /// failure and push an already-rendered clip back to Pending.</para>
        /// </summary>
        private async Task CompleteStoryAsync(MiniMaxCharacterQueueItem finished, CancellationToken token)
        {
            try
            {
                if (!finished.IsStoryClip || string.IsNullOrEmpty(finished.StoryId)) return;

                var siblings = _queue.Where(x => x.StoryId == finished.StoryId)
                                     .OrderBy(x => x.ClipIndex)
                                     .ToList();

                if (siblings.Any(x => x.ItemStatus != QueueItemStatus.Completed))
                {
                    // Nothing left running means the chain is stuck on a failure rather than still
                    // rendering — say so, otherwise the missing joined file looks like a silent bug.
                    var stalled = siblings.Count(x => x.ItemStatus == QueueItemStatus.Failed);
                    if (stalled > 0 && !siblings.Any(x => x.ItemStatus is QueueItemStatus.Pending or QueueItemStatus.Processing))
                        AddLog($"Story not joined: {stalled} of {siblings.Count} clips failed. " +
                               "Reprocess them and the join runs when the last one lands.");
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
                // Includes cancellation: the clips themselves are already on disk either way.
                AddLog($"Story join skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Concatenates a finished chain's clips into one MP4 next to them, and makes it the tab's current
        /// result so ▶ Play opens the whole story rather than its last beat. Best-effort — the individual
        /// clips are untouched and remain usable if the join cannot run.
        /// </summary>
        private async Task JoinStoryAsync(string storyId, IReadOnlyList<MiniMaxCharacterQueueItem> clips,
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

            // Alongside the clips, which already share this stem — the joined file sorts with them.
            var outputDir = Path.GetDirectoryName(paths[0])
                            ?? Path.Combine(_settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(),
                                            "MiniMaxCharacter");
            Directory.CreateDirectory(outputDir);

            // Re-running a chain (after reprocessing a failed clip) re-joins the same story from the same
            // clips, so overwriting this file is a refresh, not a loss.
            var joinedPath = Path.Combine(outputDir, $"MiniMaxCharacter_{storyId}_joined.mp4");
            var total = clips.Sum(c => ClampLength(c.LengthSeconds));

            await RunJoinAsync(paths, joinedPath, $"joined story • {paths.Count} clips • {total:0.#}s", token);
        }

        /// <summary>
        /// Shared tail of the automatic and manual joins: runs FFmpeg, checks it actually produced a file,
        /// copies it out and makes it the tab's current result. Returns the joined file, or null when the
        /// join could not run — in which case the source clips are untouched and still usable.
        /// </summary>
        private async Task<string?> RunJoinAsync(IReadOnlyList<string> paths, string joinedPath,
            string summary, CancellationToken token)
        {
            var ffmpeg = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpeg))
            {
                AddLog("Join skipped: FFmpeg not found. The clips are separate files, in playback order.");
                return null;
            }

            ProcessingStatus = $"Joining {paths.Count} clips...";
            AddLog($"Joining {paths.Count} clips with FFmpeg → {Path.GetFileName(joinedPath)}");

            await ConcatClipsAsync(ffmpeg, paths, joinedPath, token);
            if (!File.Exists(joinedPath) || new FileInfo(joinedPath).Length == 0)
            {
                AddLog("Join produced no file — the individual clips are unaffected.");
                return null;
            }

            await LocalCopyService.CopyVideoAsync(joinedPath);

            var fi = new FileInfo(joinedPath);
            Application.Current.Dispatcher.Invoke(() =>
            {
                ResultVideoPath = joinedPath;
                ResultVideoInfo = $"MiniMax Character • {summary} • {fi.Length / 1024 / 1024.0:F1}MB";
                HasResult = true;
                OnCanExecuteChanged();
            });
            ProcessingStatus = "Clips joined!";
            AddLog($"=== Joined video complete: {joinedPath} ===");
            return joinedPath;
        }

        /// <summary>
        /// FFmpeg concat-demuxer join. Every clip of a chain comes out of the same graph at the same
        /// resolution and frame rate, but it is re-encoded rather than stream-copied for the same reason the
        /// 🌀🎯 tab does it: H3 writes an audio track per clip, and a copy-mode concat of separately encoded
        /// H3 outputs is where the timestamp and codec-parameter edge cases live. veryfast/CRF 18 is
        /// visually lossless and costs seconds on clips this short.
        /// </summary>
        private async Task ConcatClipsAsync(string ffmpeg, IReadOnlyList<string> clips, string outPath,
            CancellationToken token)
        {
            var listPath = Path.Combine(Path.GetTempPath(), $"mmchar_concat_{Guid.NewGuid():N}.txt");
            var sb = new StringBuilder();
            foreach (var clip in clips)
            {
                // The concat demuxer reads a backslash as an escape and a single quote as the delimiter.
                sb.AppendLine($"file '{clip.Replace("\\", "/").Replace("'", @"'\''")}'");
            }
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
                // stderr is drained before the wait: FFmpeg logs everything there and blocks once the
                // pipe buffer fills, which would otherwise hang the join.
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

        #region Manual join

        /// <summary>
        /// Matches the per-clip file names <see cref="GenerateItemAsync"/> writes —
        /// <c>MiniMaxCharacter_{StoryId}_clipNN.mp4</c> — so any one of them identifies its whole story.
        /// </summary>
        private static readonly Regex ClipFileRegex = new(
            @"^(?<stem>.+)_clip(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private bool _isJoining;

        /// <summary>True while a manual join is running — the automatic one reports through the queue.</summary>
        public bool IsJoining
        {
            get => _isJoining;
            private set
            {
                if (_isJoining == value) return;
                _isJoining = value;
                OnPropertyChanged();
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Joins clips that are already on disk, for chains the queue can no longer join by itself: a story
        /// rendered in an earlier session is gone from the in-memory queue (completed items are pruned from
        /// the persisted file), and a chain whose clips were rendered piecemeal never had a single moment
        /// where its last sibling landed.
        ///
        /// <para>Pick <b>one</b> clip of a story and its siblings are collected automatically, in clip
        /// order. Pick <b>several</b> files and exactly those are joined, in file-name order — which is what
        /// makes the button work for hand-assembled sequences too, including clips from other tabs.</para>
        /// </summary>
        private async Task JoinClipsManuallyAsync()
        {
            if (IsJoining) return;

            var outputDir = Path.Combine(
                _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "MiniMaxCharacter");
            var initialDir = Directory.Exists(outputDir)
                ? outputDir
                : _settingsService.Settings?.OutputFolderPath;

            var picked = await _fileDialogService.OpenFilesDialogAsync(
                "Select clips to join — one clip of a story picks up the rest",
                "Video Files|*.mp4;*.mov;*.mkv|All Files|*.*",
                initialDir,
                persistKey: "minimaxchar.join");

            if (picked == null || picked.Length == 0) return;

            IsJoining = true;
            try
            {
                var clips = picked.Where(File.Exists).ToList();
                var storyStem = string.Empty;

                if (clips.Count == 1)
                {
                    // One pick is a shorthand for "this story", not a one-file join.
                    var siblings = ExpandStoryClips(clips[0], out storyStem);
                    if (siblings == null || siblings.Count < 2)
                    {
                        AddLog($"Nothing to join: {Path.GetFileName(clips[0])} has no sibling clips " +
                               "next to it. Select the clips you want joined, or pick one clip of a story.");
                        return;
                    }
                    clips = siblings;
                    AddLog($"Found {clips.Count} clips of \"{storyStem}\".");
                }
                else
                {
                    // The dialog hands files back in an order the user cannot control, so sort them the way
                    // the clip suffix reads. Logged below, because the order is the whole result.
                    clips.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b),
                        StringComparison.OrdinalIgnoreCase));
                    storyStem = CommonClipStem(clips);
                }

                AddLog($"=== Manual join: {clips.Count} clips, in this order ===");
                foreach (var clip in clips) AddLog($"  {Path.GetFileName(clip)}");

                var dir = Path.GetDirectoryName(clips[0]) ?? outputDir;
                Directory.CreateDirectory(dir);
                var baseName = string.IsNullOrEmpty(storyStem)
                    ? $"MiniMaxCharacter_joined_{DateTime.Now:yyyyMMdd_HHmmss}"
                    : $"{storyStem}_joined";
                // Never overwritten: an existing join may well be of a different selection of these clips.
                var joinedPath = UniquePath(Path.Combine(dir, $"{baseName}.mp4"));

                await RunJoinAsync(clips, joinedPath, $"joined • {clips.Count} clips", CancellationToken.None);
            }
            catch (Exception ex)
            {
                AddLog($"Join failed: {ex.Message}");
                MessageBox.Show($"Could not join the clips:\n{ex.Message}",
                    "Join Clips", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsJoining = false;
            }
        }

        /// <summary>
        /// Given one clip of a story, returns every clip beside it that shares the story stem, ordered by
        /// clip number. Null when the file is not a story clip at all.
        /// </summary>
        private static List<string>? ExpandStoryClips(string clipPath, out string stem)
        {
            stem = string.Empty;
            var dir = Path.GetDirectoryName(clipPath);
            if (string.IsNullOrEmpty(dir)) return null;

            var match = ClipFileRegex.Match(Path.GetFileNameWithoutExtension(clipPath));
            if (!match.Success) return null;
            // Held in a local as well: an `out` parameter cannot be captured by the query below.
            var storyStem = match.Groups["stem"].Value;
            stem = storyStem;

            var extension = Path.GetExtension(clipPath);
            // Ordered by the parsed number rather than by name: a chain long enough to reach clip 100 would
            // otherwise sort clip10 between clip1 and clip2.
            return Directory.EnumerateFiles(dir, $"{storyStem}_clip*{extension}")
                .Select(p => (Path: p, Match: ClipFileRegex.Match(Path.GetFileNameWithoutExtension(p))))
                .Where(x => x.Match.Success &&
                            string.Equals(x.Match.Groups["stem"].Value, storyStem, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => int.Parse(x.Match.Groups["n"].Value))
                .Select(x => x.Path)
                .ToList();
        }

        /// <summary>The story stem shared by every selected file, or empty when they are not one story's
        /// clips — which is how a hand-picked mixture gets a timestamped name instead of a story's.</summary>
        private static string CommonClipStem(IReadOnlyList<string> clips)
        {
            var stems = clips
                .Select(p => ClipFileRegex.Match(Path.GetFileNameWithoutExtension(p)))
                .Select(m => m.Success ? m.Groups["stem"].Value : string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return stems.Count == 1 && stems[0].Length > 0 ? stems[0] : string.Empty;
        }

        /// <summary>Appends <c>_2</c>, <c>_3</c>… until the name is free, so a join never overwrites one.</summary>
        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;

            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            for (var i = 2; i < 1000; i++)
            {
                var candidate = Path.Combine(dir, $"{name}_{i}{extension}");
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{extension}");
        }

        #endregion

        #region Queue persistence

        private void SaveQueueToFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(QueueFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // Completed items are session history, not pending work — keeping them out stops the
                // queue file (and therefore startup) from growing without bound.
                var pending = _queue.Where(q => q.ItemStatus != QueueItemStatus.Completed).ToList();
                File.WriteAllText(QueueFilePath,
                    JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AddLog($"Error saving queue: {ex.Message}"); }
        }

        /// <summary>
        /// Defers the persisted queue read to Background dispatcher priority, with the file I/O itself on
        /// a worker thread — this view model is built during app startup and must not do disk work in its
        /// constructor. See the tab's startup notes.
        /// </summary>
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
                    JsonSerializer.Deserialize<List<MiniMaxCharacterQueueItem>>(File.ReadAllText(QueueFilePath)));
                if (items == null || items.Count == 0) return;

                _queue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Completed) continue;
                    // Anything left mid-flight by a crash or a close is unfinished work, not a running job.
                    if (item.ItemStatus == QueueItemStatus.Processing) item.ItemStatus = QueueItemStatus.Pending;
                    _queue.Add(item);
                }

                UpdateQueueStatus();
                // Deliberately not auto-started: a leftover queue should not seize the GPU the moment the
                // app opens. The ▶ Start button picks it back up.
                if (HasPendingItems)
                    AddLog($"Queue restored: {_queue.Count} items ({_queue.Count(x => x.ItemStatus == QueueItemStatus.Pending)} pending) — press ▶ Start to resume.");
                else if (_queue.Count > 0)
                    AddLog($"Queue restored: {_queue.Count} items");
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        #endregion

        #region Generation

        private async Task GenerateItemAsync(MiniMaxCharacterQueueItem item, CancellationToken token)
        {
            IsProcessing = true;
            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = "Preparing MiniMax Character workflow...";

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                var clipLabel = item.IsStoryClip ? $", clip {item.ClipIndex}/{item.ClipCount}" : string.Empty;
                AddLog($"=== MiniMax Character ({(item.HasCharacter2 ? "2 references" : "1 reference")}{clipLabel}) ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("MiniMaxCharacter", token);

                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFileName);
                if (!File.Exists(workflowPath))
                    throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
                var json = await File.ReadAllTextAsync(workflowPath, token);

                ProcessingStatus = "Uploading character references...";
                ProcessingProgress = 5;
                if (!File.Exists(item.Character1Path))
                    throw new FileNotFoundException($"Character 1 image is gone: {item.Character1Path}");
                var char1Name = await _comfyUIService.UploadImageAsync(item.Character1Path);
                if (string.IsNullOrEmpty(char1Name)) throw new Exception("Failed to upload the Character 1 image.");
                AddLog($"Character 1 uploaded: {char1Name}");
                SetInput(ref json, NodeCharacter1, "image", char1Name);

                if (item.HasCharacter2)
                {
                    if (!File.Exists(item.Character2Path))
                        throw new FileNotFoundException($"Character 2 image is gone: {item.Character2Path}");
                    var char2Name = await _comfyUIService.UploadImageAsync(item.Character2Path);
                    if (string.IsNullOrEmpty(char2Name)) throw new Exception("Failed to upload the Character 2 image.");
                    AddLog($"Character 2 uploaded: {char2Name}");
                    json = AddSecondCharacter(json, char2Name);
                }
                else
                {
                    AddLog("Single-character run: the graph ships with ref_image_0 only.");
                }

                var runSeed = item.Seed >= 0 ? item.Seed : System.Random.Shared.NextInt64(0, long.MaxValue);
                var len = ClampLength(item.LengthSeconds);
                var aspect = item.AspectRatio;
                var (canvasW, canvasH) = CanvasSize(aspect, item.Megapixels);
                var (upW, upH) = UpscaleSize(aspect, item.Megapixels);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                // Story clips carry their index in the run token so a chain's outputs sort in story order
                // and the disk-scan fallback can still tell two clips apart.
                var clipTag = item.IsStoryClip ? $"_c{item.ClipIndex:00}" : string.Empty;
                var runToken = $"mmchar_{ts}{clipTag}";

                // The export dropped the links from the input primitives into the conditioning node and left
                // their last values baked in as widgets, so they are reconnected before anything is written:
                // ResolutionSelector then stays the single source of the canvas, which is what node 13 also
                // resizes the reference images to.
                json = ReconnectInputPrimitives(json);

                SetInput(ref json, NodePrompt, "value", item.Prompt);
                SetInput(ref json, NodeResolution, "aspect_ratio", aspect);
                SetInput(ref json, NodeResolution, "megapixels", item.Megapixels);
                SetInput(ref json, NodeDuration, "value", len);
                SetInput(ref json, NodeSeed, "noise_seed", runSeed);
                SetInput(ref json, NodeVideoCombine, "frame_rate", OutputFrameRate);
                SetInput(ref json, NodeVideoCombine, "filename_prefix", $"{OutputSubfolder}/{runToken}");

                var steps = ReadInt(json, NodeScheduler, "steps");
                json = PruneToOutputs(json, new[] { NodeVideoCombine }, out var prunedCount);
                if (prunedCount > 0)
                    AddLog($"Graph pruned to the video output: {prunedCount} disconnected node(s) removed.");

                var outputNode = NodeVideoCombine;

                ProcessingProgress = 10;
                ProcessingStatus = "Generating video...";
                AddLog($"Generating{(item.IsStoryClip ? $" clip {item.ClipIndex}/{item.ClipCount}" : string.Empty)} " +
                       $"(seed {runSeed}, {len:0.#}s / {FramesForSeconds(len)} frames @ {OutputFrameRate}fps, " +
                       $"{aspect} ≈{canvasW}×{canvasH}, {item.Megapixels:0.0} MP, " +
                       $"{steps} steps, RTX ×{RtxScale:0.#} → ≈{upW}×{upH})...");

                var local = await SubmitAndRetrieveAsync(json, runToken, outputNode, 10, 95, token);
                if (local == null || !File.Exists(local))
                    throw new Exception("No output video was generated.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "MiniMaxCharacter");
                Directory.CreateDirectory(outputDir);
                // One story's clips share a stem and differ only by index, so the finished chain sits
                // together in the folder in playback order.
                var finalName = item.IsStoryClip
                    ? $"MiniMaxCharacter_{(string.IsNullOrEmpty(item.StoryId) ? ts : item.StoryId)}_clip{item.ClipIndex:00}.mp4"
                    : $"MiniMaxCharacter_{ts}.mp4";
                var finalPath = Path.Combine(outputDir, finalName);
                File.Copy(local, finalPath, true);
                await LocalCopyService.CopyVideoAsync(finalPath);

                var fi = new FileInfo(finalPath);
                var pass = $"turbo {steps}-step • RTX ×{RtxScale:0.#} ≈{upW}×{upH}";
                var cast = item.HasCharacter2 ? "2 refs" : "1 ref";
                var clipInfo = item.IsStoryClip ? $"clip {item.ClipIndex}/{item.ClipCount} • " : string.Empty;
                item.OutputVideoPath = finalPath;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResultVideoPath = finalPath;
                    ResultVideoInfo = $"MiniMax Character • {clipInfo}{cast} • {pass} • {aspect} • {len:0.#}s • {fi.Length / 1024 / 1024.0:F1}MB";
                    HasResult = true;
                    OnCanExecuteChanged();
                });
                ProcessingProgress = 100;
                ProcessingStatus = "Complete!";
                AddLog($"=== Complete: {finalPath} ===");

                // A prompt that produced a video is worth keeping even if it was typed rather than
                // analyzed. Filed from the item's own snapshot — the prompt box has very likely moved on
                // to the next scene while this one was rendering. Story clips are skipped: the chain was
                // filed whole when it was queued, and filing each beat separately would bury the library.
                if (!item.IsStoryClip)
                    await SaveSceneAsync(item.Prompt, item.SceneImagePath, len, aspect, len, manual: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The queue loop decides whether this is a retry or a failure; it just needs the reason.
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
        /// Wrapper around <see cref="WorkflowNodeUpdater.UpdateNodeInput"/> that fails loudly on a node id
        /// that is no longer in the graph. The updater silently no-ops instead, which on this workflow
        /// would mean shipping the baked-in demo prompt and reference images to the GPU.
        /// </summary>
        private static void SetInput(ref string json, string nodeId, string input, object value)
        {
            if (WorkflowNodeUpdater.GetNodeInput(json, nodeId, input) == null)
                throw new Exception($"Workflow node '{nodeId}' has no input '{input}' — the workflow file no longer matches this tab.");
            WorkflowNodeUpdater.UpdateNodeInput(ref json, nodeId, input, value);
        }

        /// <summary>
        /// Reconnects <c>MiniMaxH3ReferenceToVideo</c> to the three input primitives the export disconnected.
        /// The graph as exported carries the last run's prompt, canvas and frame count as literal widget
        /// values on node 23, leaving the prompt string, the ResolutionSelector and the duration float
        /// feeding nothing — so setting those primitives would silently change nothing at all.
        ///
        /// <para>The links are restored rather than the widgets overwritten so that the canvas keeps coming
        /// out of ResolutionSelector, which is also what node 13 resizes the reference images to: computing
        /// it here instead would risk sizing the latent and the references differently.</para>
        ///
        /// <para><c>length</c> takes ComfyMathExpression's <b>second</b> output — the INT — matching how the
        /// original ref2video export wired the same pair of nodes.</para>
        /// </summary>
        private static string ReconnectInputPrimitives(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeReference, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodePrompt, "PrimitiveStringMultiline");
            RequireClass(root, NodeResolution, "ResolutionSelector");
            RequireClass(root, NodeFrames, "ComfyMathExpression");
            RequireClass(root, NodeDuration, "PrimitiveFloat");

            json = root.ToJsonString();
            SetInput(ref json, NodeReference, "prompt", new JsonArray(NodePrompt, 0));
            SetInput(ref json, NodeReference, "width", new JsonArray(NodeResolution, 0));
            SetInput(ref json, NodeReference, "height", new JsonArray(NodeResolution, 1));
            SetInput(ref json, NodeReference, "length", new JsonArray(NodeFrames, 1));
            return json;
        }

        /// <summary>Reads an integer widget out of the graph — used for the steps the workflow ships with,
        /// which the tab reports but never overrides.</summary>
        private static int ReadInt(string json, string nodeId, string input)
        {
            var node = JsonNode.Parse(json)?[nodeId]?["inputs"]?[input];
            return node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        }

        /// <summary>
        /// Adds the second character's reference chain. The workflow is authored for a single reference, so
        /// rather than shipping a disabled branch that would fail validation on a placeholder filename, the
        /// LoadImage → ImageResizeKJv2 pair is cloned from the first character's own resize node and wired
        /// into <c>ref_images.ref_image_1</c>. Cloning means the second reference is sized and cropped by
        /// exactly the settings the workflow chose for the first.
        /// </summary>
        private static string AddSecondCharacter(string json, string uploadedName)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeReference, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodeCharacter1, "LoadImage");
            RequireClass(root, NodeChar1Resize, "ImageResizeKJv2");

            root[NodeCharacter2] = new JsonObject
            {
                ["inputs"] = new JsonObject { ["image"] = uploadedName },
                ["class_type"] = "LoadImage",
                ["_meta"] = new JsonObject { ["title"] = "Ref Image 2" }
            };

            var resize = root[NodeChar1Resize]!.DeepClone().AsObject();
            resize["inputs"]!["image"] = new JsonArray(NodeCharacter2, 0);
            resize["_meta"] = new JsonObject { ["title"] = "Resize Image v2 (Ref 2)" };
            root[NodeChar2Resize] = resize;

            if (root[NodeReference]?["inputs"] is not JsonObject refInputs)
                throw new Exception($"Workflow node '{NodeReference}' has no inputs — the workflow file no longer matches this tab.");
            refInputs["ref_images.ref_image_1"] = new JsonArray(NodeChar2Resize, 0);

            return root.ToJsonString();
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
        /// other node outright.
        /// <para>
        /// On this workflow that is only the export's disconnected input primitives — the prompt string, the
        /// duration float and its frame-count expression, which ComfyUI would ignore anyway. It is kept
        /// because pruning by reachability is the only reliable way to drop a branch: anything ending in an
        /// OUTPUT_NODE runs whether or not something downstream consumes it, so deleting a sink is not
        /// enough on its own.
        /// </para>
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
                    // A link is ["<source node id>", <output index>]; widget values are anything else.
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

            // Node ids are strings here (this graph was exported with subgraphs flattened, so they look
            // like "610:550"), but plain integer ids show up in other exports of the same nodes.
            static string? LinkSource(JsonNode? node)
            {
                if (node is not JsonValue value) return null;
                if (value.TryGetValue<string>(out var s)) return s;
                if (value.TryGetValue<long>(out var i)) return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return null;
            }
        }

        /// <summary>Submits the workflow, waits for completion, and resolves the chosen SaveVideo node's
        /// output to a local file — first via /history node outputs, then a disk scan for the run token.</summary>
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

            // Fallback: wait for a new mp4 carrying this run's token in the output subfolder.
            var found = await WaitForNewVideoAsync(existing, "*.mp4",
                TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(4), OutputSubfolder);
            if (found != null && Path.GetFileName(found).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0)
                return found;
            return found ?? FindTokenFileOnDisk(runToken);
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
                    bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                    string outputFolder = settings.ResolveOutputFolder(isRemote);
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
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : "";
                var bytes = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, subfolder);
                if (bytes is { Length: > 0 })
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"mmchar_{Guid.NewGuid():N}_{filename}");
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

        private string? FindTokenFileOnDisk(string runToken)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;
                var baseUrl = GetComfyUIBaseUrl();
                bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
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
            AnalyzeCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
            SaveSceneCommand.NotifyCanExecuteChanged();
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
}
