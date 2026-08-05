using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// images</b> and keeps those characters' faces, hair and costumes consistent through a whole
    /// multi-shot sequence.
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
    /// which then becomes the scene the referenced characters act out.</item>
    /// </list>
    ///
    /// The workflow also carries two optional refinement chains after the H3 pass — Wan 2.2 5B and an LTX
    /// 2.3 latent ×2 spatial upscale — selected by <see cref="SelectedUpscale"/>. The LTX chain is broken as
    /// exported and is patched at submit time by <see cref="RepairLtxBranch"/>. The H3 render is always
    /// saved, and <see cref="PruneToOutputs"/> then deletes every node the kept SaveVideo nodes do not
    /// depend on, which is what actually keeps the unused models out of VRAM.
    /// </summary>
    public partial class MiniMaxCharacterViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/H3_Ref2Video_UpscaleLTX_Wan.json";
        private const string OutputSubfolder = "minimax_character";
        private const string SystemPromptFile = "texttovideoH3.md";

        // ── Workflow node ids (locked from H3_Ref2Video_UpscaleLTX_Wan.json) ───────────────────
        private const string NodeCharacter1 = "137";   // LoadImage → ref_image_0
        private const string NodeCharacter2 = "139";   // LoadImage → ref_image_1 (pruned when unused)
        private const string NodeReference = "136";    // MiniMaxH3ReferenceToVideo
        private const string NodePrompt = "595:523";   // PrimitiveStringMultiline (node 138 reads from it)
        private const string NodeResolution = "595:380"; // ResolutionSelector (aspect_ratio, megapixels)
        private const string NodeDuration = "595:568"; // PrimitiveFloat seconds (nodes 132 + 608:569 read it)
        private const string NodeSeed = "129";         // RandomNoise noise_seed (H3 pass)

        // Wan 2.2 upscale branch
        private const string NodeWanSave = "611";      // SaveVideo
        private const string NodeWanResize = "610:550"; // ImageResizeKJv2 (upscale target size)
        private const string NodeWanSampler = "610:538"; // KSampler seed
        private const string NodeWanDecode = "610:540";  // VAEDecode → tiled, see SwitchToTiledDecode

        // LTX 2.3 refinement branch. As exported it is unrunnable — see RepairLtxBranch, which patches it
        // at submit time rather than requiring the workflow file to be re-authored.
        private const string NodeLtxSave = "609";            // SaveVideo
        private const string NodeLtxWidth = "608:517";       // PrimitiveInt (halved into the pre-upsample resize)
        private const string NodeLtxHeight = "608:520";
        private const string NodeLtxSeed = "608:411";        // RandomNoise noise_seed
        private const string NodeLtxCheckpoint = "608:423";  // CheckpointLoaderSimple — MODEL only, its VAE is None
        private const string NodeLtxVae = "608:930";         // VAELoader injected by RepairLtxBranch (video)
        private const string NodeLtxAudioVaeLoader = "608:931"; // VAELoader injected by RepairLtxBranch (audio)
        private const string NodeLtxTextEncoder = "608:435"; // LTXAVTextEncoderLoader
        private const string NodeLtxAudioVae = "608:445";    // LTXVAudioVAELoader — replaced, see RepairLtxBranch
        private const string NodeLtxAudioFrames = "608:565"; // ComfyMathExpression → LTXVEmptyLatentAudio.frames_number
        private const string NodeLtxDecode = "608:576";      // VAEDecode → tiled, see SwitchToTiledDecode

        /// <summary>VAE output index on CheckpointLoaderSimple (MODEL, CLIP, VAE).</summary>
        private const int CheckpointVaeSlot = 2;

        // The LTX 2.3 assets every working LTX workflow in this repo uses. The H3 export has LTX-2.0 ones.
        // Both VAEs live in the vae/ folder and load through a plain VAELoader.
        private const string LtxVideoVae = "ltx-2-3-22b-VAE.safetensors";
        private const string LtxAudioVae = "ltx-2-3-22b-audio_vae.safetensors";
        private const string LtxTextProjection = "ltx-2.3_text_projection_bf16.safetensors";

        /// <summary>Node 131's frame-count formula: 24 fps snapped onto H3's 17k+5 grid. The LTX branch has
        /// to build its empty audio latent on the same grid, because the video latent it gets concatenated
        /// with is an encode of H3's actual output frames.</summary>
        private const string H3FrameGridExpression =
            "max(5, round(a * 24)) + (5 - (max(5, round(a * 24)) % 17)) % 17";

        // Base H3 output
        private const string NodeH3Save = "612";       // SaveVideo

        /// <summary>Reference line pinning both characters. Mirrors the phrasing the H3 reference workflow
        /// ships with ("use &lt;Picture n&gt; as reference frames").</summary>
        private const string RefInstructionTwo =
            "For the target video, use <Picture 1> and <Picture 2> as reference frames — Character 1 is <Picture 1> and Character 2 is <Picture 2>; keep each one's face, hair and clothing exactly as shown.";

        private const string RefInstructionOne =
            "For the target video, use <Picture 1> as a reference frame — Character 1 is <Picture 1>; keep their face, hair and clothing exactly as shown.";

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
        private string _selectedAspectRatio = MiniMaxH3ViewModel.AutoAspect;
        private double _megapixels = 1.0;
        private double _lengthSeconds = 10;
        private long _seed = -1;
        private UpscaleOption _selectedUpscale;
        private bool _isAnalyzing;

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private CancellationTokenSource? _analyzeCts;
        private CancellationTokenSource? _runCts;

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
            _selectedUpscale = UpscaleOptions[0];
            _sceneLibrary = new ScenePromptLibrary(AddLog);

            SelectCharacter1Command = new RelayCommand(async () => await SelectCharacter1Async());
            SelectCharacter2Command = new RelayCommand(async () => await SelectCharacter2Async());
            ClearCharacter2Command = new RelayCommand(() => Character2Path = string.Empty);
            SelectSceneImageCommand = new RelayCommand(async () => await SelectSceneImageAsync());
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            GenerateCommand = new RelayCommand(async () => await GenerateAsync(), () => CanGenerate);
            CancelCommand = new RelayCommand(() => _runCts?.Cancel(), () => IsProcessing);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            RandomSeedCommand = new RelayCommand(() => Seed = System.Random.Shared.NextInt64(0, long.MaxValue));
            OpenSceneLibraryCommand = new RelayCommand(async () => await OpenSceneLibraryAsync());
            SaveSceneCommand = new RelayCommand(async () => await SaveCurrentSceneAsync(manual: true),
                () => !string.IsNullOrWhiteSpace(Prompt));

            AddLog("MiniMax Character initialized");

            // Reading the index is cheap (thumbnails are separate files), but it is still disk I/O on a
            // startup-path view model — keep it off the constructor's thread. See the tab's startup notes.
            _ = PrimeSceneLibraryAsync();
        }

        #region Commands

        public ICommand SelectCharacter1Command { get; }
        public ICommand SelectCharacter2Command { get; }
        public ICommand ClearCharacter2Command { get; }
        public ICommand SelectSceneImageCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand RandomSeedCommand { get; }
        public RelayCommand OpenSceneLibraryCommand { get; }
        public RelayCommand SaveSceneCommand { get; }

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

        /// <summary>The full H3 prompt: reference line + the three core fields.</summary>
        public string Prompt
        {
            get => _prompt;
            set { if (_prompt != value) { _prompt = value; OnPropertyChanged(); OnCanExecuteChanged(); } }
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
            }
        }

        /// <summary>The aspect actually sent to ComfyUI — the picked one, or the scene image's closest match.</summary>
        public string ResolvedAspectRatio =>
            SelectedAspectRatio == MiniMaxH3ViewModel.AutoAspect
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
            set { if (Math.Abs(_megapixels - value) > 0.0001) { _megapixels = value; OnPropertyChanged(); } }
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
        /// Which post-H3 refinement pass to run. The H3 render is always saved on its own, so "None" still
        /// produces a video — the other two just add a second, larger file.
        /// </summary>
        public IReadOnlyList<UpscaleOption> UpscaleOptions { get; } = new[]
        {
            new UpscaleOption("none", "None — H3 render only (fastest)"),
            new UpscaleOption("wan", "Wan 2.2 5B — light 4-step refine at ~2 MP"),
            new UpscaleOption("ltx", "LTX 2.3 — latent ×2 spatial upscale at ~2 MP"),
        };

        private static string UpscaleName(string key) => key switch
        {
            "wan" => "Wan 2.2",
            "ltx" => "LTX 2.3",
            _ => "none"
        };

        public UpscaleOption SelectedUpscale
        {
            get => _selectedUpscale;
            set
            {
                if (_selectedUpscale == value || value == null) return;
                _selectedUpscale = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpscaleSummary));
            }
        }

        public string UpscaleSummary
        {
            get
            {
                if (SelectedUpscale.Key == "none")
                    return "Output: the H3 render at the canvas size above. Nothing else is loaded.";
                var (w, h) = UpscaleSize(ResolvedAspectRatio);
                return $"Output: the H3 render plus a {UpscaleName(SelectedUpscale.Key)} pass at {w}×{h} — a second model into VRAM.";
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

        /// <summary>Analyze needs the scene image — it is the only thing the LLM is shown.</summary>
        public bool CanAnalyze => HasSceneImage && !IsAnalyzing && !IsProcessing;

        /// <summary>Generation needs a prompt and at least the first character reference.</summary>
        public bool CanGenerate => !string.IsNullOrWhiteSpace(Prompt) && HasCharacter1 && !IsProcessing && !IsAnalyzing;

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
            return MiniMaxH3ViewModel.ClosestAspectRatio(w, h);
        }

        /// <summary>
        /// The upscale branches ship hard-coded at 1920×1080. Recompute that target for whatever aspect the
        /// H3 pass actually rendered, holding the same ~2.07 MP area so a portrait or square run is not
        /// stretched back into widescreen. Both branches halve the size internally, so keep it on an 8-grid.
        /// </summary>
        private static (int Width, int Height) UpscaleSize(string aspectOption)
        {
            var ratio = MiniMaxH3ViewModel.AspectRatios
                .FirstOrDefault(a => a.Option == aspectOption).Ratio;
            if (ratio <= 0) ratio = 16.0 / 9.0;

            const double area = 1920.0 * 1080.0;
            var h = Math.Sqrt(area / ratio);
            var w = ratio * h;
            return (RoundTo8(w), RoundTo8(h));

            static int RoundTo8(double v) => Math.Max(8, (int)Math.Round(v / 8.0) * 8);
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

                AddLog($"Writing a {ClampLength(LengthSeconds):0.#}s multi-shot H3 prompt — sending to {_lmStudioService.DescribeTarget(model)}");

                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", SystemPromptFile);
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"System prompt not found: {promptFilePath}");
                var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

                // The current prompt box doubles as the user's draft idea for the rewrite.
                var draft = string.IsNullOrWhiteSpace(Prompt)
                    ? "(none — invent a dynamic sequence that suits the scene)"
                    : StripReferenceLine(Prompt).Trim();
                var len = ClampLength(LengthSeconds);

                // The scene image is REFERENCE ONLY (it is never uploaded), but the *characters* are real
                // reference frames the generator will see, so they are named rather than described.
                var cast = HasCharacter2
                    ? "Two character reference images will additionally be given to the video model and are addressed as <Picture 1> (Character 1) and <Picture 2> (Character 2). Write both of them into the action and refer to them by those tags instead of describing their faces, hair or clothing."
                    : "One character reference image will additionally be given to the video model and is addressed as <Picture 1> (Character 1). Write them into the action and refer to them by that tag instead of describing their face, hair or clothing.";

                var userMessage =
                    "Image role: REFERENCE ONLY — this image is the SCENE (setting, lighting, art style, mood). " +
                    "The video does not start on it and the generator will never see it, so describe the environment explicitly.\n" +
                    $"{cast}\n" +
                    $"Target duration: {len:0.##} seconds.\n" +
                    $"Draft idea from the user:\n{draft}";

                var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    model,
                    SceneImagePath,
                    userMessage,
                    systemPrompt,
                    maxTokens: 6000,
                    cancellationToken: token);

                var cleaned = ApplyReferenceLine(CleanOutput(result));
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    Prompt = cleaned;
                    AddLog($"Prompt written ({cleaned.Length} chars, {CountShots(cleaned)} shots)");
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
        /// Rewrites the reference line so it always matches the number of character images actually wired
        /// in — a prompt that mentions &lt;Picture 2&gt; with no second reference has nothing to resolve.
        /// </summary>
        private string ApplyReferenceLine(string prompt)
        {
            var body = StripReferenceLine(prompt);
            if (body.Length == 0) return string.Empty;
            return $"{(HasCharacter2 ? RefInstructionTwo : RefInstructionOne)}\n\n{body}";
        }

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
        private async Task SaveCurrentSceneAsync(bool manual)
        {
            var body = StripReferenceLine(Prompt).Trim();
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
                var draft = new ScenePrompt
                {
                    Name = ScenePromptLibrary.SuggestName(SceneImagePath, body, scenes),
                    Prompt = body,
                    SceneImagePath = HasSceneImage ? SceneImagePath : string.Empty,
                    AspectRatio = ResolvedAspectRatio,
                    LengthSeconds = ClampLength(LengthSeconds),
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

                Prompt = ApplyReferenceLine(picked.Prompt);

                if (picked.LengthSeconds > 0)
                    LengthSeconds = ClampLength(picked.LengthSeconds);

                if (!string.IsNullOrEmpty(picked.AspectRatio) && AspectRatioOptions.Contains(picked.AspectRatio))
                    SelectedAspectRatio = picked.AspectRatio;

                AddLog($"Loaded \"{picked.Name}\" from the scene library " +
                       $"({picked.Shots} shots, {ClampLength(picked.LengthSeconds):0.#}s, {ResolvedAspectRatio}) — " +
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

        #region Generation

        private async Task GenerateAsync()
        {
            if (!CanGenerate) return;

            IsProcessing = true;
            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = "Preparing MiniMax Character workflow...";

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                AddLog($"=== MiniMax Character ({(HasCharacter2 ? "2 references" : "1 reference")}) ===");
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
                var char1Name = await _comfyUIService.UploadImageAsync(Character1Path);
                if (string.IsNullOrEmpty(char1Name)) throw new Exception("Failed to upload the Character 1 image.");
                AddLog($"Character 1 uploaded: {char1Name}");
                SetInput(ref json, NodeCharacter1, "image", char1Name);

                if (HasCharacter2)
                {
                    var char2Name = await _comfyUIService.UploadImageAsync(Character2Path);
                    if (string.IsNullOrEmpty(char2Name)) throw new Exception("Failed to upload the Character 2 image.");
                    AddLog($"Character 2 uploaded: {char2Name}");
                    SetInput(ref json, NodeCharacter2, "image", char2Name);
                }
                else
                {
                    json = PruneSecondCharacter(json);
                    AddLog("Single-character run: ref_image_1 pruned.");
                }

                var runSeed = Seed >= 0 ? Seed : System.Random.Shared.NextInt64(0, long.MaxValue);
                var len = ClampLength(LengthSeconds);
                var aspect = ResolvedAspectRatio;
                var (upW, upH) = UpscaleSize(aspect);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var runToken = $"mmchar_{ts}";

                SetInput(ref json, NodePrompt, "value", ApplyReferenceLine(Prompt.Trim()));
                SetInput(ref json, NodeResolution, "aspect_ratio", aspect);
                SetInput(ref json, NodeResolution, "megapixels", Megapixels);
                SetInput(ref json, NodeDuration, "value", len);
                SetInput(ref json, NodeSeed, "noise_seed", runSeed);

                // Keep the H3 render plus at most one refinement branch, then cut everything the kept
                // SaveVideo nodes do not depend on. This has to be a full reachability prune — see
                // PruneToOutputs for why deleting the unwanted SaveVideo on its own leaves the branch live.
                var upscale = SelectedUpscale.Key;
                SetInput(ref json, NodeH3Save, "filename_prefix", $"{OutputSubfolder}/{runToken}_h3");

                var keepOutputs = new List<string> { NodeH3Save };
                var outputNode = NodeH3Save;

                if (upscale == "wan")
                {
                    SetInput(ref json, NodeWanResize, "width", upW);
                    SetInput(ref json, NodeWanResize, "height", upH);
                    SetInput(ref json, NodeWanSampler, "seed", runSeed);
                    SetInput(ref json, NodeWanSave, "filename_prefix", $"{OutputSubfolder}/{runToken}_wan");
                    json = SwitchToTiledDecode(json, NodeWanDecode);
                    AddLog("[Wan] final VAE decode switched to tiled.");
                    keepOutputs.Add(NodeWanSave);
                    outputNode = NodeWanSave;
                }
                else if (upscale == "ltx")
                {
                    json = RepairLtxBranch(json, len, s => AddLog($"[LTX fix] {s}"));
                    json = SwitchToTiledDecode(json, NodeLtxDecode);
                    SetInput(ref json, NodeLtxWidth, "value", upW);
                    SetInput(ref json, NodeLtxHeight, "value", upH);
                    SetInput(ref json, NodeLtxSeed, "noise_seed", runSeed);
                    SetInput(ref json, NodeLtxSave, "filename_prefix", $"{OutputSubfolder}/{runToken}_ltx");
                    keepOutputs.Add(NodeLtxSave);
                    outputNode = NodeLtxSave;
                }

                json = PruneToOutputs(json, keepOutputs, out var prunedCount);
                AddLog($"Graph pruned to {(upscale == "none" ? "H3 only" : $"H3 + {UpscaleName(upscale)}")}: {prunedCount} unused nodes removed.");

                ProcessingProgress = 10;
                ProcessingStatus = "Generating video...";
                AddLog($"Generating (seed {runSeed}, {len:0.#}s / {FramesForSeconds(len)} frames, {aspect}, {Megapixels:0.0} MP" +
                       (upscale == "none" ? ")..." : $", {UpscaleName(upscale)} → {upW}×{upH})..."));

                var local = await SubmitAndRetrieveAsync(json, runToken, outputNode, 10, 95, token);
                if (local == null || !File.Exists(local))
                    throw new Exception("No output video was generated.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "MiniMaxCharacter");
                Directory.CreateDirectory(outputDir);
                var finalPath = Path.Combine(outputDir, $"MiniMaxCharacter_{ts}.mp4");
                File.Copy(local, finalPath, true);
                await LocalCopyService.CopyVideoAsync(finalPath);

                var fi = new FileInfo(finalPath);
                var pass = upscale == "none" ? "H3 render" : $"{UpscaleName(upscale)} {upW}×{upH}";
                var cast = HasCharacter2 ? "2 refs" : "1 ref";
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResultVideoPath = finalPath;
                    ResultVideoInfo = $"MiniMax Character • {cast} • {pass} • {aspect} • {len:0.#}s • {fi.Length / 1024 / 1024.0:F1}MB";
                    HasResult = true;
                    OnCanExecuteChanged();
                });
                ProcessingProgress = 100;
                ProcessingStatus = "Complete!";
                AddLog($"=== Complete: {finalPath} ===");

                // A prompt that produced a video is worth keeping even if it was typed rather than analyzed.
                await SaveCurrentSceneAsync(manual: false);
            }
            catch (OperationCanceledException)
            {
                AddLog("Generation cancelled");
                ProcessingStatus = "Cancelled";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                ProcessingStatus = $"Error: {ex.Message}";
                MessageBox.Show($"Generation failed:\n{ex.Message}", "MiniMax Character Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                lease?.Dispose();
                IsProcessing = false;
                _runCts?.Dispose();
                _runCts = null;
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
        /// Drops the second reference slot from MiniMaxH3ReferenceToVideo and the LoadImage feeding it, so a
        /// single-character run does not fail validation on the node's placeholder filename.
        /// </summary>
        private static string PruneSecondCharacter(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            if (root[NodeReference]?["inputs"] is JsonObject refInputs)
                refInputs.Remove("ref_images.ref_image_1");
            root.Remove(NodeCharacter2);

            return root.ToJsonString();
        }

        /// <summary>
        /// Converts a refinement branch's final <c>VAEDecode</c> to <c>VAEDecodeTiled</c> (a drop-in: same
        /// samples/vae inputs, same IMAGE output).
        /// <para>
        /// Both branches decode the full clip at ~2 MP in one call — 362 frames at 1920×1080 — which
        /// reliably blows up: "Ran out of memory when regular VAE decoding, retrying with tiled VAE
        /// decoding". ComfyUI does recover on its own, but only after hitting the wall and fragmenting
        /// VRAM, so tile up front instead. <c>LTX-22-B.json</c> decodes the same way.
        /// </para>
        /// </summary>
        private static string SwitchToTiledDecode(string json, string nodeId)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            if (root[nodeId] is not JsonObject node || node["inputs"] is not JsonObject inputs)
                throw new Exception($"Tiled decode: node '{nodeId}' is not in the workflow.");
            if (node["class_type"]?.GetValue<string>() is not "VAEDecode")
                throw new Exception($"Tiled decode: node '{nodeId}' is not a VAEDecode — the workflow no longer matches.");

            node["class_type"] = "VAEDecodeTiled";
            inputs["tile_size"] = 512;      // node defaults; overlap keeps the tile seams out of the frame
            inputs["overlap"] = 64;
            inputs["temporal_size"] = 64;
            inputs["temporal_overlap"] = 8;

            return root.ToJsonString();
        }

        /// <summary>
        /// Makes the LTX 2.3 refinement branch runnable. As exported it has LTX-2.0 assets wired into an
        /// LTX-2.3 chain and takes its video VAE from a checkpoint that does not contain one:
        /// <list type="number">
        /// <item><b>Video VAE.</b> <c>608:427/428/429/576</c> all read <c>vae</c> from
        /// <c>608:423 CheckpointLoaderSimple</c> output 2, but that checkpoint is
        /// <c>ltx-2.3-22b-dev_transformer_only…</c> — "No VAE weights detected", so the slot is None and the
        /// branch dies with <c>node 608:429: ERROR: VAE is invalid: None</c>. A real
        /// <c>VAELoader</c> is injected and every consumer of that slot is repointed at it. Only the MODEL
        /// output (index 0) legitimately comes from the checkpoint.</item>
        /// <item><b>Text projection.</b> <c>608:435</c> was pointed at the LTX-2.0 audio VAE; all six
        /// working LTX 2.3 workflows in this repo use <c>ltx-2.3_text_projection_bf16</c>.</item>
        /// <item><b>Audio VAE.</b> <c>608:445 LTXVAudioVAELoader</c> reads from <c>checkpoints/</c>, where the
        /// only real audio VAE is the LTX-2.0 one — and the 2.3 transformer checkpoint has no audio VAE in
        /// it either, so that node can only ever yield None or 2.0 weights. Both working references
        /// (VideoSound, Vantage-Sulphur-2) instead feed <c>audio_vae</c> from a plain <c>VAELoader</c> on
        /// <c>ltx-2-3-22b-audio_vae</c> in <c>vae/</c>, which is what gets injected here.</item>
        /// </list>
        /// It also puts the empty audio latent on H3's frame grid — the video latent it is concatenated
        /// with is an encode of H3's real output frames (362 for 15 s), while LTX's native
        /// <c>seconds × fps + 1</c> would have asked for 361.
        /// </summary>
        private static string RepairLtxBranch(string json, double lengthSeconds, Action<string> log)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            AddVaeLoader(root, NodeLtxVae, LtxVideoVae, "Load VAE (LTX 2.3 video)");
            AddVaeLoader(root, NodeLtxAudioVaeLoader, LtxAudioVae, "Load VAE (LTX 2.3 audio)");

            // The video VAE only ever came from the checkpoint's VAE slot; the audio VAE came from a whole
            // node, so that one is matched on the source id alone. Both leave their old producer orphaned,
            // and PruneToOutputs drops it.
            var video = Rewire(root, NodeLtxCheckpoint, CheckpointVaeSlot, NodeLtxVae);
            var audio = Rewire(root, NodeLtxAudioVae, null, NodeLtxAudioVaeLoader);

            if (video.Count == 0 || audio.Count == 0)
                throw new Exception(
                    $"LTX branch: expected consumers of {NodeLtxCheckpoint}:{CheckpointVaeSlot} (found {video.Count}) " +
                    $"and of {NodeLtxAudioVae} (found {audio.Count}) — the workflow no longer matches this repair.");

            json = root.ToJsonString();
            SetInput(ref json, NodeLtxTextEncoder, "ckpt_name", LtxTextProjection);
            SetInput(ref json, NodeLtxAudioFrames, "expression", H3FrameGridExpression);

            log($"video VAE → {LtxVideoVae} ({string.Join(", ", video)})");
            log($"audio VAE → {LtxAudioVae} ({string.Join(", ", audio)})");
            log($"text projection → {LtxTextProjection}");
            log($"audio latent on H3's frame grid → {FramesForSeconds(lengthSeconds)} frames");
            return json;

            static void AddVaeLoader(JsonObject root, string id, string vaeName, string title) =>
                root[id] = new JsonObject
                {
                    ["inputs"] = new JsonObject { ["vae_name"] = vaeName },
                    ["class_type"] = "VAELoader",
                    ["_meta"] = new JsonObject { ["title"] = title }
                };

            // Repoints every input fed by sourceId (optionally only from sourceSlot) at targetId output 0.
            static List<string> Rewire(JsonObject root, string sourceId, int? sourceSlot, string targetId)
            {
                var hits = new List<string>();
                foreach (var node in root)
                {
                    if (node.Value?["inputs"] is not JsonObject inputs) continue;
                    foreach (var input in inputs.ToList())
                    {
                        if (input.Value is not JsonArray link || link.Count != 2) continue;
                        if (link[0] is not JsonValue srcValue || !srcValue.TryGetValue<string>(out var src)) continue;
                        if (src != sourceId) continue;
                        if (sourceSlot is { } want)
                        {
                            if (link[1] is not JsonValue slotValue || !slotValue.TryGetValue<int>(out var slot)) continue;
                            if (slot != want) continue;
                        }

                        inputs[input.Key] = new JsonArray(targetId, 0);
                        hits.Add($"{node.Key}.{input.Key}");
                    }
                }
                return hits;
            }
        }

        /// <summary>
        /// Cuts the graph down to exactly the SaveVideo nodes we want plus everything they depend on, and
        /// deletes every other node outright.
        /// <para>
        /// Deleting just the unwanted SaveVideo is <b>not</b> enough on this workflow. Both refinement
        /// branches end in a <c>LayerUtility: PurgeVRAM V2</c> node (<c>610:562</c>, <c>608:563</c>), and
        /// that node is an OUTPUT_NODE — ComfyUI runs output nodes whether or not anything downstream
        /// consumes them. Leaving them in the prompt kept both chains alive, which loaded WAN22 and the LTX
        /// checkpoint into VRAM after the H3 pass had already finished.
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
                    string outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;
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
                var outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;
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
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>One entry in the MiniMax Character tab's post-H3 refinement dropdown.</summary>
    public record UpscaleOption(string Key, string Label);
}
