using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "Scail 2" tab — a single, low-scroll page that chains two existing pipelines:
    ///
    ///   Stage A (Klein char-swap, from Qwen-Edit): a frame grabbed from the base scene video at the
    ///   current scrub position + Character 1 + Character 2 → LLM analysis → Flux.2 Klein char-swap
    ///   workflow → a composited still with both people replaced.
    ///
    ///   Stage B (WAN SCAIL II): that composited still becomes the character image and the same base
    ///   video becomes the driving video → LLM appearance/motion analysis over the marked range →
    ///   SCAIL2 workflow → final motion-transfer video.
    ///
    /// Subclasses <see cref="WanScailGgufViewModel"/> to inherit the entire SCAIL II back end
    /// (driving video, trim markers, motion analysis, queue, generation, result playback). It only
    /// adds the Klein char-swap front-end and the two auto-chain triggers. All numeric/advanced
    /// settings are hidden and defaulted (subject "person", animation mode, fps 24, maxEdge 1280,
    /// random seed, VRAM-optimize on — defaults come from the base view model).
    /// </summary>
    public class Scail2ViewModel : WanScailGgufViewModel
    {
        // Two-character path uses the dedicated Flux.2 Character Replacer (2 characters): LoadImage 40 =
        // Character 1 (left), 60 = Character 2 (right), 39 = pose source (base scene frame). Like the
        // single-character path the left/right prompt is baked into the workflow (node 41), so no separate
        // LLM analysis is needed.
        private const string KleinCharReplacer2CharWorkflowFile = "workflow/image/klein/Flux.2 Character Replacer - 2characters.json";
        private const string KleinCharReplacer2CharSavePrefix = "char_replaced_2c";

        // Single-character path uses the dedicated Flux.2 Character Replacer (v2.4): node 40 = subject
        // (Character 1 likeness), node 39 = pose source (base scene frame). Stage 47 builds a neutral
        // posed body from the pose image (cropped widescreen), stage 9 composites the subject onto it,
        // so the result keeps the base scene's pose + orientation with Character 1's likeness. The prompt
        // is fixed inside the workflow, so no separate LLM analysis is needed.
        private const string KleinCharReplacerWorkflowFile = "workflow/image/klein/Flux.2 Character Replacer v2.4.json";
        private const string KleinCharReplacerSavePrefix = "char_replaced";

        // Alternative single-character path: the "Klein Flux2 Control" workflow from the Image Generator
        // (Advanced ▸ Control). It self-analyses the subject appearance + pose via QwenVL and drives a
        // Flux.2 Klein reference-latent generation, which tends to hold the subject's likeness far more
        // accurately than the Character Replacer. node 1 = reference/subject, node 19 = pose (base frame),
        // node 7 = RandomNoise, saved by node 9 with the "flux2_klein" prefix.
        private const string KleinControlWorkflowFile = "workflow/image/klein/flux2_klein_control_netAPI.json";
        private const string KleinControlSavePrefix = "flux2_klein";

        // The image this workflow saves is painted by its PiD 4K stage (nodes 203-212), not by Klein —
        // the Klein result only feeds it as guidance. PiD drifts off that guidance into a cool colour
        // cast once an axis runs past MaxPidAxis, so the canvas is clamped. Mirrors KleinControlViewModel.
        private const int MaxPidAxis = 4096;
        private const int PidScale = 4;                     // canvas stays exactly 4x the base
        private const int MaxBaseAxis = MaxPidAxis / PidScale;
        private const int BaseTargetPixels = 1024 * 1024;

        // Third single-character path: the "Krea2 Edit (two ref)" workflow. Node 72 = image A (the base
        // scene frame containing the person to replace), node 86 = image B (Character 1, the replacement
        // likeness). The grounded-encode prompt on node 84 replaces the person in image A with the subject
        // in image B; node 53 is the KSampler (reseeded per run) and node 29 saves with the "krea2_edit"
        // prefix. Unlike the Klein paths it keeps the base scene composition while swapping the subject in.
        private const string Krea2EditWorkflowFile = "workflow/image/krea/krea2_edit_two_ref.json";
        private const string Krea2EditSavePrefix = "krea2_edit";

        // Own references — the base keeps these private, so Scail 2 stores its own copies from DI.
        private readonly IFileDialogService _fileDialogService;

        // ── Character 1 (replaces the LEFT person) ───────────────────────────
        private string _char1ImagePath = string.Empty;
        private BitmapImage? _char1ImageSource;
        private bool _hasChar1Image;

        // ── Character 2 (replaces the RIGHT person) ──────────────────────────
        private string _char2ImagePath = string.Empty;
        private BitmapImage? _char2ImageSource;
        private bool _hasChar2Image;

        // ── Klein char-swap result (also pushed into the inherited CharacterImagePath) ──
        private BitmapImage? _charSwapResultSource;
        private bool _hasCharSwapResult;
        private bool _isCharSwapping;
        private string _charSwapStatus = "Load a base video and characters, scrub to a frame, then press Swap";

        private CancellationTokenSource? _charSwapCts;

        // ── Pinned pose frame ────────────────────────────────────────────────
        // The Scail 2 equivalent of the Image Generator ▸ Advanced ▸ Control "📌 Use This Frame" button:
        // one frame is grabbed from the base video and held, so Analyze and every subsequent Generate run
        // against the *same* pose. Without it each pass re-grabbed the frame at whatever position the
        // playhead happened to be at, so a nudged scrubber silently changed the pose between the analyze
        // and the generate. Nothing pinned = fall back to grabbing at the current position (old behaviour).
        private string _poseFramePath = string.Empty;
        private BitmapImage? _poseFrameSource;
        private bool _hasPoseFrame;
        private double _poseFrameSeconds;
        private bool _isGrabbingPoseFrame;

        // 0–100 progress for the Klein Control analyze/generate passes (mirrors the Control tab's bar).
        private double _kleinProgress;

        public Scail2ViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, lmStudioService, logger, settingsService, serviceProvider, workflowCoordinator, fileDialogService)
        {
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            // SAM3 subject default for the SCAIL II stage (settings are hidden).
            Subject = "person";

            // Restore the persisted internal chunk size (VRAM tuning). Set the backing field directly so
            // this initial load doesn't trigger a redundant save.
            var savedBatch = _settingsService.Settings?.Scail2VideoBatchSize ?? 0;
            if (savedBatch >= 8) _videoBatchSize = savedBatch;

            // Restore the persisted output-resolution choice (backing field so no redundant save fires).
            // The two old defaults — "0x0" (the authored 640×960 portrait) and "960x544" — are migrated to
            // "auto". Neither was ever a deliberate aspect choice, and both forced a fixed canvas onto
            // whatever the source video was, which is what stretched the character image (see
            // ComputeOutputResolution). Explicitly picked presets are left alone. Nothing is saved here, so
            // the migration simply re-runs on the next launch.
            var savedRes = _settingsService.Settings?.Scail2Resolution?.Trim();
            if (!string.IsNullOrWhiteSpace(savedRes))
                _outputResolution = savedRes.Equals("0x0", StringComparison.OrdinalIgnoreCase)
                                 || savedRes.Equals("960x544", StringComparison.OrdinalIgnoreCase)
                    ? AutoResolutionToken
                    : savedRes;

            // Restore the persisted "keep original background" choice. ReplaceBackground is the inverse:
            // keep-original == replacement mode == !ReplaceBackground. Set the inherited property directly
            // (its own change notification fires, but nothing is persisted from that setter).
            ReplaceBackground = !(_settingsService.Settings?.Scail2KeepOriginalBackground ?? false);

            // Restore the persisted RIFE + RTX post-pass choice (backing field so no redundant save fires).
            _interpolateAndUpscale = _settingsService.Settings?.Scail2Interpolate ?? false;

            BrowseChar1Command = new RelayCommand(async () => await BrowseChar1Async(), () => !IsCharSwapping);
            BrowseChar2Command = new RelayCommand(async () => await BrowseChar2Async(), () => !IsCharSwapping);
            BrowseFinalImageCommand = new RelayCommand(async () => await BrowseFinalImageAsync(), () => !IsCharSwapping);
            SwapCharactersCommand = new RelayCommand(async () => await RunCharSwapStageAsync(), () => CanSwapCharacters);
            SwapChar1OnlyCommand = new RelayCommand(async () => await RunChar1OnlySwapAsync(), () => CanSwapChar1Only);
            AnalyzeKleinPromptCommand = new RelayCommand(async () => await RunKleinAnalyzeAsync(), () => CanAnalyzeKleinPrompt);
            GenerateKleinImageCommand = new RelayCommand(async () => await RunKleinGenerateAsync(), () => CanGenerateKleinImage);
            UsePoseFrameCommand = new RelayCommand(async () => await UsePoseFrameAsync(), () => CanUsePoseFrame);
            ClearPoseFrameCommand = new RelayCommand(ClearPoseFrame, () => HasPoseFrame && !IsCharSwapping);
            RemoveChar1Command = new RelayCommand(ClearChar1Image, () => HasChar1Image && !IsCharSwapping);
            RemoveChar2Command = new RelayCommand(ClearChar2Image, () => HasChar2Image && !IsCharSwapping);

            // Reset the char-swap stage whenever a different base video is loaded.
            PropertyChanged += OnSelfPropertyChanged;

            AddLog("Scail 2 initialized");
        }

        private void OnSelfPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(InputVideoPath))
            {
                // New base video → the previous char-swap composite no longer applies. The character
                // image (and its derived SCAIL II input) is cleared so a new clip can't be generated
                // against the old composite; the user must run the swap again for the new video.
                CharSwapResultSource = null;
                HasCharSwapResult = false;
                CharacterImagePath = string.Empty;
                // The pinned pose frame belonged to the old video, so it goes too.
                ClearPoseFrame();
                RefreshSwapReadiness();
            }
        }

        #region Character inputs

        public string Char1ImagePath
        {
            get => _char1ImagePath;
            private set { _char1ImagePath = value; OnPropertyChanged(); }
        }

        public BitmapImage? Char1ImageSource
        {
            get => _char1ImageSource;
            private set { _char1ImageSource = value; OnPropertyChanged(); }
        }

        public bool HasChar1Image
        {
            get => _hasChar1Image;
            private set
            {
                _hasChar1Image = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoChar1Image));
                RemoveChar1Command?.NotifyCanExecuteChanged();
                RefreshSwapReadiness();
            }
        }

        public bool NoChar1Image => !_hasChar1Image;

        public string Char2ImagePath
        {
            get => _char2ImagePath;
            private set { _char2ImagePath = value; OnPropertyChanged(); }
        }

        public BitmapImage? Char2ImageSource
        {
            get => _char2ImageSource;
            private set { _char2ImageSource = value; OnPropertyChanged(); }
        }

        public bool HasChar2Image
        {
            get => _hasChar2Image;
            private set { _hasChar2Image = value; OnPropertyChanged(); OnPropertyChanged(nameof(NoChar2Image)); RemoveChar2Command?.NotifyCanExecuteChanged(); RefreshSwapReadiness(); }
        }

        public bool NoChar2Image => !_hasChar2Image;

        #endregion

        #region Char-swap result / status

        public BitmapImage? CharSwapResultSource
        {
            get => _charSwapResultSource;
            private set { _charSwapResultSource = value; OnPropertyChanged(); }
        }

        public bool HasCharSwapResult
        {
            get => _hasCharSwapResult;
            private set { _hasCharSwapResult = value; OnPropertyChanged(); OnPropertyChanged(nameof(NoCharSwapResult)); }
        }

        public bool NoCharSwapResult => !_hasCharSwapResult;

        public bool IsCharSwapping
        {
            get => _isCharSwapping;
            private set
            {
                if (_isCharSwapping == value) return;
                _isCharSwapping = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSwapCharacters));
                OnPropertyChanged(nameof(CanSwapChar1Only));
                OnPropertyChanged(nameof(CanAnalyzeKleinPrompt));
                OnPropertyChanged(nameof(CanGenerateKleinImage));
                OnPropertyChanged(nameof(CanUsePoseFrame));
                BrowseChar1Command.NotifyCanExecuteChanged();
                BrowseChar2Command.NotifyCanExecuteChanged();
                BrowseFinalImageCommand.NotifyCanExecuteChanged();
                SwapCharactersCommand.NotifyCanExecuteChanged();
                SwapChar1OnlyCommand.NotifyCanExecuteChanged();
                AnalyzeKleinPromptCommand.NotifyCanExecuteChanged();
                GenerateKleinImageCommand.NotifyCanExecuteChanged();
                UsePoseFrameCommand.NotifyCanExecuteChanged();
                ClearPoseFrameCommand.NotifyCanExecuteChanged();
                RemoveChar1Command.NotifyCanExecuteChanged();
                RemoveChar2Command.NotifyCanExecuteChanged();
            }
        }

        public string CharSwapStatus
        {
            get => _charSwapStatus;
            private set { if (_charSwapStatus != value) { _charSwapStatus = value; OnPropertyChanged(); } }
        }

        // 0–100 progress of the running Klein Control pass, so the tab can show the same bar the
        // Image Generator ▸ Advanced ▸ Control tab does.
        public double KleinProgress
        {
            get => _kleinProgress;
            private set { if (Math.Abs(_kleinProgress - value) > 0.01) { _kleinProgress = value; OnPropertyChanged(); } }
        }

        #endregion

        #region Pinned pose frame

        /// <summary>Path of the frame pinned with “Use this frame”, or empty when nothing is pinned.</summary>
        public string PoseFramePath
        {
            get => _poseFramePath;
            private set { _poseFramePath = value; OnPropertyChanged(); }
        }

        public BitmapImage? PoseFrameSource
        {
            get => _poseFrameSource;
            private set { _poseFrameSource = value; OnPropertyChanged(); }
        }

        public bool HasPoseFrame
        {
            get => _hasPoseFrame;
            private set
            {
                _hasPoseFrame = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoPoseFrame));
                OnPropertyChanged(nameof(PoseFrameInfo));
                ClearPoseFrameCommand?.NotifyCanExecuteChanged();
            }
        }

        public bool NoPoseFrame => !_hasPoseFrame;

        public bool IsGrabbingPoseFrame
        {
            get => _isGrabbingPoseFrame;
            private set
            {
                _isGrabbingPoseFrame = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanUsePoseFrame));
                UsePoseFrameCommand?.NotifyCanExecuteChanged();
            }
        }

        /// <summary>Caption under the pinned-frame thumbnail: which timestamp the pose was taken from.</summary>
        public string PoseFrameInfo => _hasPoseFrame
            ? $"✓ Pose frame pinned at {TimeSpan.FromSeconds(_poseFrameSeconds):mm\\:ss\\.ff}"
            : "No pose frame pinned — the frame under the playhead is used";

        public bool CanUsePoseFrame => HasInputVideo && !IsCharSwapping && !IsGrabbingPoseFrame;

        /// <summary>
        /// Grabs the frame under the playhead and holds it as the pose source, exactly like the Control
        /// tab's “📌 Use This Frame”. Everything downstream (Analyze, Generate, both replacer paths)
        /// then works from this one still, so re-generating never silently changes the pose.
        /// </summary>
        private async Task UsePoseFrameAsync()
        {
            if (!CanUsePoseFrame) return;
            try
            {
                IsGrabbingPoseFrame = true;
                var seconds = PlaybackPositionSeconds;
                var grabbed = await ExtractFrameAtAsync(InputVideoPath, seconds, App.ShutdownToken);
                if (grabbed == null || !File.Exists(grabbed))
                    throw new Exception("Could not grab a frame from the base video (is ffmpeg installed?).");

                // Move it out of the volatile temp name into a stable per-pin file so the thumbnail keeps
                // working and the run-time cleanup never deletes a frame the user pinned.
                var dir = Path.Combine(Path.GetTempPath(), "flippix-frames");
                Directory.CreateDirectory(dir);
                var pinned = Path.Combine(dir, $"scail2_pose_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                File.Move(grabbed, pinned, overwrite: true);

                PoseFramePath = pinned;
                _poseFrameSeconds = seconds;
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    PoseFrameSource = LoadBitmap(pinned);
                    HasPoseFrame = true;
                });
                AddLog($"Scail 2: pose frame pinned at {seconds:F2}s → {Path.GetFileName(pinned)}");
                RefreshSwapReadiness();
            }
            catch (Exception ex)
            {
                AddLog($"ERROR pinning pose frame: {ex.Message}");
                CharSwapStatus = $"Could not pin the frame: {ex.Message}";
            }
            finally
            {
                IsGrabbingPoseFrame = false;
            }
        }

        public void ClearPoseFrame()
        {
            PoseFramePath = string.Empty;
            PoseFrameSource = null;
            HasPoseFrame = false;
            _poseFrameSeconds = 0;
        }

        /// <summary>
        /// Resolves the pose still for a run: the pinned frame when there is one (never deleted), else a
        /// throw-away grab at the current playhead. <c>isTemporary</c> tells the caller whether to delete it.
        /// </summary>
        private async Task<(string path, bool isTemporary)> ResolvePoseFrameAsync(CancellationToken token)
        {
            if (HasPoseFrame && File.Exists(PoseFramePath))
            {
                AddLog($"Pose frame: pinned still from {_poseFrameSeconds:F2}s ({Path.GetFileName(PoseFramePath)})");
                return (PoseFramePath, false);
            }

            var grabbed = await ExtractFrameAtAsync(InputVideoPath, PlaybackPositionSeconds, token);
            if (grabbed == null || !File.Exists(grabbed))
                throw new Exception("Could not grab a frame from the base video (is ffmpeg installed?).");
            AddLog($"Pose frame: grabbed at {PlaybackPositionSeconds:F2}s → {Path.GetFileName(grabbed)}");
            return (grabbed, true);
        }

        #endregion

        #region Replace method + SCAIL II settings

        // Which workflow the single-character "Replace this one only" button runs:
        //   0 = Klein Flux2 Control (default — more accurate likeness), 1 = Character Replacer (legacy),
        //   2 = Krea2 Edit (two ref — keeps the base scene composition while swapping the subject in).
        // The two-character "Replace Both" path always uses the 2-character replacer (Klein Control is
        // single-subject only), so this selector only affects the single-character swap.
        private int _charReplaceMethodIndex;
        public int CharReplaceMethodIndex
        {
            get => _charReplaceMethodIndex;
            set
            {
                if (_charReplaceMethodIndex != value)
                {
                    _charReplaceMethodIndex = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsKrea2EditSelected));
                    OnPropertyChanged(nameof(IsKleinControlSelected));
                    OnPropertyChanged(nameof(CanAnalyzeKleinPrompt));
                    OnPropertyChanged(nameof(CanGenerateKleinImage));
                    AnalyzeKleinPromptCommand?.NotifyCanExecuteChanged();
                    GenerateKleinImageCommand?.NotifyCanExecuteChanged();
                    // The status hint is method-specific (Klein has its own Analyze → edit → Generate flow).
                    RefreshSwapReadiness();
                }
            }
        }

        private bool UseKleinControl => CharReplaceMethodIndex == 0;
        private bool UseKrea2Edit => CharReplaceMethodIndex == 2;

        // Only the Krea2 Edit path exposes an editable instruction prompt (the Klein/Character Replacer
        // paths bake their prompt into the workflow), so the prompt field is shown only for that method.
        public bool IsKrea2EditSelected => CharReplaceMethodIndex == 2;

        // The Klein Flux2 Control path mirrors the Image Generator ▸ Advanced ▸ Control tab: an Analyze
        // pass runs the workflow's two QwenVL nodes (subject appearance + base-frame pose), shows the
        // combined prompt for editing, and the swap then generates from the edited text. So this method
        // gets its own prompt box + Analyze button.
        public bool IsKleinControlSelected => CharReplaceMethodIndex == 0;

        // Default instruction for the Krea2 Edit (two ref) grounded-encode node (84). The authored
        // workflow phrasing is gendered ("the woman … facing the man"); this neutral default works for
        // either sex. The user can edit it in the UI, and it is written into node 84 at run time.
        private const string DefaultKrea2EditPrompt =
            "replace the person in image a with the person in image b, keeping the original pose and scene.";
        private string _krea2EditPrompt = DefaultKrea2EditPrompt;
        public string Krea2EditPrompt
        {
            get => _krea2EditPrompt;
            set { if (_krea2EditPrompt != value) { _krea2EditPrompt = value; OnPropertyChanged(); } }
        }

        // Positive prompt for the Klein Flux2 Control path. Empty means "let the workflow write it" —
        // node 6 keeps its authored link to the combined QwenVL prompt (node 59), exactly as the swap
        // behaved before. Pressing Analyze fills this in with that generated text so it can be edited,
        // and any non-empty value is written into the text encoders in place of the link.
        private string _kleinControlPrompt = string.Empty;
        public string KleinControlPrompt
        {
            get => _kleinControlPrompt;
            set { if (_kleinControlPrompt != value) { _kleinControlPrompt = value; OnPropertyChanged(); } }
        }

        // Internal per-chunk window (frames) for the SCAIL II sampler = the SCAILAutoExtend
        // "chunk_length" / WanSCAILInfinity "window_length". The sampler loops over the clip in windows of
        // this many frames and only ever holds one window at a time, so this value is the main driver of
        // peak VRAM. Lowering it splits the clip into smaller chunks to avoid out-of-memory errors on long
        // videos, at the cost of more chunks and slightly more seams. The value is snapped to 4n+1 (the
        // WAN latent temporal stride) before it is written into the workflow, and the windows are still
        // blended back into one continuous output, so this stays user-invisible unless deliberately set.
        // The workflow's authored driving-video canvas (ImageResizeKJv2 node 199:89), reported in the log
        // when no resolution override is picked.
        private const int AuthoredCanvasWidth = 640;
        private const int AuthoredCanvasHeight = 960;

        // "Auto" resolution sentinel: instead of a fixed WxH, the generation canvas is derived from the
        // driving video's own aspect ratio at the same pixel budget the fixed 960×544 preset used, so a
        // portrait clip generates portrait and a landscape clip landscape. Every fixed preset forced its
        // own aspect onto the source, and node 199:500 resizes the character image onto that canvas — so a
        // 16:9 character image on a 640×960 canvas (or the reverse) came out visibly stretched.
        private const string AutoResolutionToken = "auto";
        private const int AutoPixelBudget = 960 * 544;   // ≈522k px — the measured VRAM sweet spot

        private int _videoBatchSize = 40;
        public int VideoBatchSize
        {
            get => _videoBatchSize;
            set
            {
                var v = Math.Max(8, value);
                if (_videoBatchSize == v) return;
                _videoBatchSize = v;
                OnPropertyChanged();
                // Persist the choice so it survives restarts.
                if (_settingsService.Settings != null)
                {
                    _settingsService.Settings.Scail2VideoBatchSize = v;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }
            }
        }

        // Output resolution for the final SCAIL II video, as "WxH" (e.g. "1280x720"). Defaults to "auto":
        // the driving video's own aspect ratio at ≈960×544 worth of pixels — the measured sweet spot that
        // survives long clips without OOM. Any concrete value instead forces the generation canvas to
        // exactly that size, which crops the driving video and the character image to that aspect;
        // "0x0" (or empty) keeps the workflow's authored 640×960 canvas. Pushing to 1280×720 is only safe
        // on short trims, as it OOMs the server on full-length clips. Persisted across restarts.
        private string _outputResolution = AutoResolutionToken;
        public string OutputResolution
        {
            get => _outputResolution;
            set
            {
                var v = string.IsNullOrWhiteSpace(value) ? "0x0" : value.Trim();
                if (_outputResolution == v) return;
                _outputResolution = v;
                OnPropertyChanged();
                if (_settingsService.Settings != null)
                {
                    _settingsService.Settings.Scail2Resolution = v;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }
            }
        }

        // User-facing "Keep original background" toggle for the Scail 2 tab. This is the inverse of the
        // inherited ReplaceBackground: when checked, SCAIL2 runs in replacement mode (node 39 = true),
        // compositing the driving video's real background every frame and regenerating only the swapped
        // character. That stops a static background (e.g. a waterfall) colour-drifting or softening across
        // the autoregressive chunks. Unchecked regenerates the whole frame (animation mode). Persisted.
        public bool KeepOriginalBackground
        {
            get => !ReplaceBackground;
            set
            {
                var replace = !value;
                if (ReplaceBackground == replace) return;
                ReplaceBackground = replace;
                OnPropertyChanged();
                if (_settingsService.Settings != null)
                {
                    _settingsService.Settings.Scail2KeepOriginalBackground = value;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }
            }
        }

        // Runs the workflow's post pass — RIFE 2× frame interpolation followed by the RTX Video Super
        // Resolution 2× upscale — instead of saving the raw sampler output. Doubles the frame rate and the
        // resolution, at the cost of a longer run and two extra custom-node dependencies (RIFE VFI and
        // nvidia-vfx), so it stays off by default. Whichever branch is off is stripped from the submitted
        // graph, leaving exactly one saved video for the history reader to find. Persisted.
        private bool _interpolateAndUpscale;
        public bool InterpolateAndUpscale
        {
            get => _interpolateAndUpscale;
            set
            {
                if (_interpolateAndUpscale == value) return;
                _interpolateAndUpscale = value;
                OnPropertyChanged();
                if (_settingsService.Settings != null)
                {
                    _settingsService.Settings.Scail2Interpolate = value;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }
            }
        }

        // Parses OutputResolution into (width, height). Returns (0, 0) for "auto", for the "keep authored
        // default" sentinel, and for any unparseable/non-positive value, so those cases fall through to
        // ComputeOutputResolution below.
        private (int width, int height) ParseOutputResolution()
        {
            var parts = (_outputResolution ?? string.Empty).Split('x', 'X');
            if (parts.Length == 2
                && int.TryParse(parts[0].Trim(), out var w)
                && int.TryParse(parts[1].Trim(), out var h)
                && w > 0 && h > 0)
                return (w, h);
            return (0, 0);
        }

        // True when the canvas follows the driving video's aspect ratio instead of a fixed preset.
        private bool IsAutoResolution =>
            AutoResolutionToken.Equals(_outputResolution, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The canvas the whole SCAIL II loop generates at. The base view model resolves this once per run
        /// and uses it for two things: it is written into node 199:89 (see UpdateWorkflowParameters), and
        /// the character image is centre-cropped to this aspect ratio before it is uploaded.
        ///
        /// That crop is the fix for stretched output. Node 199:500 resizes the character image onto this
        /// canvas, and it was authored with keep_proportion "stretch", so a character image that reached
        /// it at a different aspect ratio was squashed — and the whole video was generated from it.
        /// <see cref="WanScailGgufViewModel"/> returns (0, 0) here, which skipped the crop entirely, so a
        /// portrait character image on the landscape canvas (or the reverse) went in distorted. The node
        /// is now "crop"/centre as well, so a mismatch is cropped rather than distorted even if this
        /// crop is skipped (ffmpeg missing, or a user-supplied final image).
        ///
        /// "auto" (the default) sizes the canvas from the driving video itself, so the character image —
        /// which Stage A already renders at the pose frame's aspect — needs no crop at all.
        /// </summary>
        protected override (int Width, int Height) ComputeOutputResolution(int videoW, int videoH, int maxEdge)
        {
            var (w, h) = ParseOutputResolution();
            if (w > 0 && h > 0) return (w, h);                   // fixed preset

            if (IsAutoResolution && videoW > 0 && videoH > 0)
                return FitToPixelBudget(videoW, videoH, AutoPixelBudget);

            return (AuthoredCanvasWidth, AuthoredCanvasHeight);  // "0x0" = the authored 640×960 canvas
        }

        // Largest canvas at the source's aspect ratio that fits a pixel budget, both edges snapped to 16
        // (node 199:89 is divisible_by 16, and the WAN latent wants the same alignment).
        private static (int Width, int Height) FitToPixelBudget(int srcW, int srcH, int budgetPixels)
        {
            var scale = Math.Sqrt(budgetPixels / ((double)srcW * srcH));
            return (SnapToCanvasBlock(srcW * scale), SnapToCanvasBlock(srcH * scale));
        }

        private static int SnapToCanvasBlock(double value)
            => Math.Max(256, (int)Math.Round(value / 16.0) * 16);

        #endregion

        #region Commands

        public RelayCommand BrowseChar1Command { get; }
        public RelayCommand BrowseChar2Command { get; }
        public RelayCommand BrowseFinalImageCommand { get; }
        public RelayCommand SwapCharactersCommand { get; }
        public RelayCommand SwapChar1OnlyCommand { get; }
        public RelayCommand AnalyzeKleinPromptCommand { get; }
        public RelayCommand GenerateKleinImageCommand { get; }
        public RelayCommand UsePoseFrameCommand { get; }
        public RelayCommand ClearPoseFrameCommand { get; }
        public RelayCommand RemoveChar1Command { get; }
        public RelayCommand RemoveChar2Command { get; }

        // The two-character swap needs a base video + both characters.
        public bool CanSwapCharacters => HasInputVideo && HasChar1Image && HasChar2Image && !IsCharSwapping;

        // The single-character button only needs a base video + Character 1.
        public bool CanSwapChar1Only => HasInputVideo && HasChar1Image && !IsCharSwapping;

        // Analyze is Klein-Control-only and needs the same two inputs as the single-character swap
        // (Character 1 = the subject QwenVL describes, base frame = the pose QwenVL describes).
        public bool CanAnalyzeKleinPrompt => IsKleinControlSelected && HasInputVideo && HasChar1Image && !IsCharSwapping;

        // Step 3 of the Control-tab flow: generate (and re-generate) from the edited prompt. Same inputs
        // as Analyze — an empty prompt box just means "let QwenVL write it", as it always has.
        public bool CanGenerateKleinImage => CanAnalyzeKleinPrompt;

        private async Task BrowseChar1Async()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Character 1 (replaces the LEFT person)",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "scail2.char1");
            if (!string.IsNullOrEmpty(path)) SetChar1Image(path);
        }

        private async Task BrowseChar2Async()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Character 2 (replaces the RIGHT person)",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "scail2.char2");
            if (!string.IsNullOrEmpty(path)) SetChar2Image(path);
        }

        // Lets the user supply their own final image instead of (or in place of) a Klein char-swap
        // composite. The picked image becomes the SCAIL II character image directly.
        private async Task BrowseFinalImageAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select the final image (used as the character image)",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "scail2.final");
            if (!string.IsNullOrEmpty(path)) SetFinalImage(path);
        }

        public void SetFinalImage(string path)
        {
            if (!File.Exists(path)) return;
            try { CharSwapResultSource = LoadBitmap(path); }
            catch (Exception ex) { AddLog($"ERROR loading final image: {ex.Message}"); return; }
            HasCharSwapResult = true;
            // Drives the inherited preview + enables the SCAIL II stage.
            CharacterImagePath = path;
            AddLog($"Scail 2: final image = {Path.GetFileName(path)} (user-supplied)");
            RefreshSwapReadiness();
        }

        public void SetChar1Image(string path)
        {
            if (!File.Exists(path)) return;
            Char1ImagePath = path;
            try { Char1ImageSource = LoadBitmap(path); HasChar1Image = true; AddLog($"Scail 2: Character 1 = {Path.GetFileName(path)}"); }
            catch (Exception ex) { AddLog($"ERROR loading Character 1: {ex.Message}"); return; }
            // A changed character invalidates any existing composite; the user re-runs the swap.
            CharSwapResultSource = null;
            HasCharSwapResult = false;
            RefreshSwapReadiness();
        }

        public void SetChar2Image(string path)
        {
            if (!File.Exists(path)) return;
            Char2ImagePath = path;
            try { Char2ImageSource = LoadBitmap(path); HasChar2Image = true; AddLog($"Scail 2: Character 2 = {Path.GetFileName(path)}"); }
            catch (Exception ex) { AddLog($"ERROR loading Character 2: {ex.Message}"); return; }
            // A changed character invalidates any existing composite; the user re-runs the swap.
            CharSwapResultSource = null;
            HasCharSwapResult = false;
            RefreshSwapReadiness();
        }

        // Remove an uploaded character so it isn't sent into the swap (e.g. keep only Character 1).
        // Clears any existing composite so it rebuilds from the remaining inputs.
        public void ClearChar1Image()
        {
            if (IsCharSwapping) return;
            Char1ImagePath = string.Empty;
            Char1ImageSource = null;
            HasChar1Image = false;
            CharSwapResultSource = null;
            HasCharSwapResult = false;
            AddLog("Scail 2: Character 1 removed");
            RefreshSwapReadiness();
        }

        public void ClearChar2Image()
        {
            if (IsCharSwapping) return;
            Char2ImagePath = string.Empty;
            Char2ImageSource = null;
            HasChar2Image = false;
            CharSwapResultSource = null;
            HasCharSwapResult = false;
            AddLog("Scail 2: Character 2 removed");
            RefreshSwapReadiness();
        }

        #endregion

        #region Stage A — Klein char-swap

        /// <summary>
        /// Called from the view when the user positions the base video (pauses on a frame or moves a
        /// marker). Records that a frame was deliberately chosen so the Swap button knows a frame is
        /// ready. The swap itself never auto-runs — it waits for an explicit button press.
        /// </summary>
        public void NotifyScrubbed()
        {
            RefreshSwapReadiness();
        }

        // Updates the Swap buttons' enabled state and the status hint. The char-swap is explicit:
        // this never launches it — the user presses "Swap characters" / "Swap (Char 1)" when ready.
        private void RefreshSwapReadiness()
        {
            OnPropertyChanged(nameof(CanSwapCharacters));
            OnPropertyChanged(nameof(CanSwapChar1Only));
            OnPropertyChanged(nameof(CanAnalyzeKleinPrompt));
            OnPropertyChanged(nameof(CanGenerateKleinImage));
            OnPropertyChanged(nameof(CanUsePoseFrame));
            SwapCharactersCommand?.NotifyCanExecuteChanged();
            SwapChar1OnlyCommand?.NotifyCanExecuteChanged();
            AnalyzeKleinPromptCommand?.NotifyCanExecuteChanged();
            GenerateKleinImageCommand?.NotifyCanExecuteChanged();
            UsePoseFrameCommand?.NotifyCanExecuteChanged();
            ClearPoseFrameCommand?.NotifyCanExecuteChanged();

            if (IsCharSwapping) return;
            if (HasCharSwapResult)
            {
                CharSwapStatus = "Character image ready — set the In/Out markers to generate the video";
                return;
            }
            if (!HasInputVideo)
            {
                CharSwapStatus = "Load a base video, then add characters and scrub to a frame";
                return;
            }
            if (!HasChar1Image)
            {
                CharSwapStatus = "Add Character 1 (and optionally Character 2), then press Swap";
                return;
            }
            if (IsKleinControlSelected)
            {
                CharSwapStatus = HasPoseFrame
                    ? "Pose frame pinned — press Analyze, edit the prompt, then Generate image"
                    : "Scrub to a frame and press “📌 Use this frame”, then Analyze → edit prompt → Generate";
                return;
            }
            CharSwapStatus = HasChar2Image
                ? "Scrub to a frame showing both people, then press “Swap characters”"
                : "Scrub to a frame, then press “Swap (Char 1)” — or add Character 2 for a two-person swap";
        }

        public async Task RunCharSwapStageAsync()
        {
            if (IsCharSwapping) return;
            if (!HasInputVideo || !HasChar1Image || !HasChar2Image) return;

            _charSwapCts?.Dispose();
            _charSwapCts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
            var token = _charSwapCts.Token;
            string? baseStill = null;
            bool baseStillIsTemp = false;

            try
            {
                IsCharSwapping = true;
                HasCharSwapResult = false;
                AddLog("=== Scail 2: char-swap stage ===");

                // 1) Take the pinned pose frame, or grab the current scrub frame if nothing is pinned.
                CharSwapStatus = "Grabbing base frame…";
                (baseStill, baseStillIsTemp) = await ResolvePoseFrameAsync(token);

                // 2) Run the Flux.2 Character Replacer (2 characters) workflow. Character 1 → left
                //    (node 40), Character 2 → right (node 60), base frame = pose (node 39). The left/right
                //    mapping is baked into the workflow prompt, so no LLM analysis is needed.
                CharSwapStatus = "Running 2-character replacer (subjects + pose)…";
                using var lease = await _workflowCoordinator.AcquireAsync("Scail2CharSwap", token);

                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(token);
                    AddLog("Connected to ComfyUI");
                }

                // Character 1 = left (node 40), Character 2 = right (node 60), base frame = pose (node 39).
                var uploadedChar1 = await _comfyUIService.UploadImageAsync(Char1ImagePath, token);
                var uploadedChar2 = await _comfyUIService.UploadImageAsync(Char2ImagePath, token);
                var uploadedBase = await _comfyUIService.UploadImageAsync(baseStill, token);
                AddLog($"Uploaded char1(left)={uploadedChar1} char2(right)={uploadedChar2} pose(base)={uploadedBase}");

                var workflow = BuildCharReplacer2CharWorkflow(uploadedChar1, uploadedChar2, uploadedBase);
                await ExecuteKleinAndAdoptAsync(workflow, KleinCharReplacer2CharSavePrefix, token);

                CharSwapStatus = "Character image ready — set the In/Out markers to generate the video";
                AddLog("=== Char-swap complete ===");
            }
            catch (OperationCanceledException)
            {
                CharSwapStatus = "Char-swap cancelled";
                AddLog("Char-swap cancelled");
            }
            catch (Exception ex)
            {
                CharSwapStatus = $"Char-swap failed: {ex.Message}";
                AddLog($"ERROR (char-swap): {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (baseStillIsTemp && !string.IsNullOrEmpty(baseStill))
                    try { File.Delete(baseStill); } catch { }
                IsCharSwapping = false;
            }
        }

        // Single-character swap: drop Character 1 onto the current base frame's pose using the Flux.2
        // Character Replacer (v2.4) workflow. Character 1 is the subject (likeness), the base frame is
        // the pose source. The workflow rebuilds a neutral posed body from the base frame and composites
        // Character 1 onto it, holding the original pose + widescreen orientation with Character 1's
        // likeness. Like the full swap, the result becomes the SCAIL II character image.
        public async Task RunChar1OnlySwapAsync()
        {
            if (IsCharSwapping) return;
            if (!HasInputVideo || !HasChar1Image) return;

            // Klein Flux2 Control has its own Analyze → edit → Generate flow (the Control tab's steps 1–3),
            // so the button just runs step 3 with whatever is in the prompt box.
            if (UseKleinControl)
            {
                await RunKleinGenerateAsync();
                return;
            }

            _charSwapCts?.Dispose();
            _charSwapCts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
            var token = _charSwapCts.Token;
            string? baseStill = null;
            bool baseStillIsTemp = false;

            try
            {
                IsCharSwapping = true;
                HasCharSwapResult = false;
                bool useKrea2 = UseKrea2Edit;
                string methodName = useKrea2 ? "Krea2 Edit (two ref)" : "Character Replacer v2.4";
                AddLog($"=== Scail 2: single-character swap (Character 1, {methodName}) ===");

                CharSwapStatus = "Grabbing base frame…";
                (baseStill, baseStillIsTemp) = await ResolvePoseFrameAsync(token);

                CharSwapStatus = $"Running {methodName} (subject + pose)…";
                using var lease = await _workflowCoordinator.AcquireAsync("Scail2CharSwap", token);

                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(token);
                    AddLog("Connected to ComfyUI");
                }

                // Character 1 = subject/likeness; base frame = pose source.
                var uploadedSubject = await _comfyUIService.UploadImageAsync(Char1ImagePath, token);
                var uploadedPose = await _comfyUIService.UploadImageAsync(baseStill, token);
                AddLog($"Uploaded subject(char1)={uploadedSubject} pose(base)={uploadedPose}");

                if (useKrea2)
                {
                    // Match the output aspect ratio to the base scene frame (landscape stays landscape,
                    // portrait stays portrait) instead of the workflow's authored 1:1 default.
                    var (targetW, targetH) = ComputeKrea2TargetDimensions(baseStill);
                    var workflow = BuildKrea2EditWorkflow(uploadedSubject, uploadedPose, targetW, targetH);
                    await ExecuteKleinAndAdoptAsync(workflow, Krea2EditSavePrefix, token);
                }
                else
                {
                    var workflow = BuildCharReplacerWorkflow(uploadedSubject, uploadedPose);
                    await ExecuteKleinAndAdoptAsync(workflow, KleinCharReplacerSavePrefix, token);
                }

                CharSwapStatus = "Character image ready — set the In/Out markers to generate the video";
                AddLog("=== Single-character swap complete ===");
            }
            catch (OperationCanceledException)
            {
                CharSwapStatus = "Swap cancelled";
                AddLog("Single-character swap cancelled");
            }
            catch (Exception ex)
            {
                CharSwapStatus = $"Swap failed: {ex.Message}";
                AddLog($"ERROR (single swap): {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (baseStillIsTemp && !string.IsNullOrEmpty(baseStill))
                    try { File.Delete(baseStill); } catch { }
                IsCharSwapping = false;
            }
        }

        // Shared tail for both swap paths: run the built Klein workflow, retrieve the image, save it,
        // and adopt it as the SCAIL II character image. The save prefix differs per workflow
        // (2-character replacer vs. single Character Replacer), so it's passed in for the output-file match.
        private async Task ExecuteKleinAndAdoptAsync(JsonElement workflow, string savePrefix, CancellationToken token)
        {
            CharSwapStatus = "Compositing the character image…";
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, null, token);
            AddLog($"Klein submitted: {promptId}");

            var bytes = await RetrieveKleinImageAsync(promptId, savePrefix, token);
            if (bytes == null)
                throw new Exception("No image returned from the Klein workflow — check ComfyUI logs.");

            await SaveAndAdoptCharacterImageAsync(bytes, token);
        }

        // Writes a generated still to output/scail2 and adopts it as the SCAIL II character image.
        private async Task SaveAndAdoptCharacterImageAsync(byte[] bytes, CancellationToken token)
        {
            var outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "scail2");
            Directory.CreateDirectory(outDir);
            var resultPath = Path.Combine(outDir, $"charswap_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            await File.WriteAllBytesAsync(resultPath, bytes, token);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                CharSwapResultSource = LoadBitmap(resultPath);
                HasCharSwapResult = true;
                // Drives the inherited preview + enables the SCAIL II stage.
                CharacterImagePath = resultPath;
            });
            AddLog($"Saved character image: {Path.GetFileName(resultPath)}");
        }

        /// <summary>
        /// Step 1 of the Image Generator ▸ Advanced ▸ Control flow. Runs the Klein Control workflow with
        /// its authored wiring, so the two QwenVL nodes write the prompt themselves, and reads the combined
        /// text back out of ShowText node 59 into <see cref="KleinControlPrompt"/> for editing. The pass
        /// also produces a finished image, so that image is adopted as the character image.
        /// </summary>
        public Task RunKleinAnalyzeAsync() => RunKleinControlPassAsync(isAnalyze: true);

        /// <summary>
        /// Step 3 of the same flow: generate from the (edited) prompt. Re-runnable — press it again for
        /// another take when the first image isn't good enough; each run reseeds node 7, and the pinned
        /// pose frame keeps every take on the same pose. An empty prompt box falls back to letting QwenVL
        /// write the prompt, exactly as the swap behaved before.
        /// </summary>
        public Task RunKleinGenerateAsync() => RunKleinControlPassAsync(isAnalyze: false);

        // Shared Klein Control pass. Analyze runs the workflow with its authored QwenVL wiring and reads
        // the generated prompt back; Generate feeds the prompt box text into node 6 instead. Both adopt
        // the resulting image as the SCAIL II character image.
        private async Task RunKleinControlPassAsync(bool isAnalyze)
        {
            if (IsCharSwapping) return;
            if (!IsKleinControlSelected || !HasInputVideo || !HasChar1Image) return;

            _charSwapCts?.Dispose();
            _charSwapCts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
            var token = _charSwapCts.Token;
            string? baseStill = null;
            bool baseStillIsTemp = false;

            // Analyze always runs the QwenVL wiring; Generate uses the prompt box, and only falls back to
            // QwenVL when it is empty (in which case the generated text is worth reading back too).
            var typedPrompt = string.IsNullOrWhiteSpace(KleinControlPrompt) ? null : KleinControlPrompt;
            string? customPrompt = isAnalyze ? null : typedPrompt;
            bool readPromptBack = customPrompt == null;

            try
            {
                IsCharSwapping = true;
                if (!isAnalyze) HasCharSwapResult = false;
                KleinProgress = 0;
                AddLog(isAnalyze
                    ? "=== Scail 2: Klein Control analyze (QwenVL subject + pose) ==="
                    : "=== Scail 2: Klein Control generate ===");

                CharSwapStatus = "Grabbing base frame…";
                (baseStill, baseStillIsTemp) = await ResolvePoseFrameAsync(token);

                CharSwapStatus = isAnalyze ? "Analyzing subject + pose…" : "Running Klein Flux2 Control…";
                using var lease = await _workflowCoordinator.AcquireAsync(
                    isAnalyze ? "Scail2KleinAnalyze" : "Scail2KleinGenerate", token);

                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(token);
                    AddLog("Connected to ComfyUI");
                }

                KleinProgress = 10;
                var uploadedSubject = await _comfyUIService.UploadImageAsync(Char1ImagePath, token);
                var uploadedPose = await _comfyUIService.UploadImageAsync(baseStill, token);
                AddLog($"Uploaded subject(char1)={uploadedSubject} pose(base)={uploadedPose}");

                KleinProgress = 18;
                var workflow = BuildKleinControlWorkflow(uploadedSubject, uploadedPose, baseStill, customPrompt);

                // Same live sampler progress the Control tab shows.
                var reporter = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value == null || msg.Data?.Max == null || msg.Data.Max <= 0) return;
                    var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        KleinProgress = 18 + pct * 0.72;
                        CharSwapStatus = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                    });
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, reporter, token);
                AddLog($"Klein {(isAnalyze ? "analyze" : "generate")} submitted: {promptId}");

                if (readPromptBack)
                {
                    KleinProgress = 92;
                    CharSwapStatus = "Reading generated prompt…";
                    var text = await GetTextFromHistoryAsync(promptId, "59", token);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var cleaned = StripThinkingTokens(text);
                        Application.Current?.Dispatcher.Invoke(() => KleinControlPrompt = cleaned);
                        AddLog($"Generated prompt ({cleaned.Length} chars): {cleaned}");
                    }
                    else
                    {
                        AddLog("WARNING: no text returned from ShowText node 59");
                    }
                }

                // Every pass produces an image — adopt it so the run isn't wasted.
                CharSwapStatus = "Retrieving image…";
                var bytes = await RetrieveKleinImageAsync(promptId, KleinControlSavePrefix, token);
                if (bytes != null)
                    await SaveAndAdoptCharacterImageAsync(bytes, token);
                else
                    AddLog($"WARNING: {(isAnalyze ? "analyze" : "generate")} produced no image");

                KleinProgress = 100;
                CharSwapStatus = isAnalyze
                    ? (string.IsNullOrWhiteSpace(KleinControlPrompt)
                        ? "Analyze finished but no prompt came back — check ComfyUI logs"
                        : "Prompt ready — edit it, then press “Generate image” for another take")
                    : "Character image ready — press “Generate image” again for another take, or set the In/Out markers";
                AddLog($"=== Klein Control {(isAnalyze ? "analyze" : "generate")} complete ===");
            }
            catch (OperationCanceledException)
            {
                CharSwapStatus = isAnalyze ? "Analyze cancelled" : "Generate cancelled";
                KleinProgress = 0;
                AddLog($"Klein Control {(isAnalyze ? "analyze" : "generate")} cancelled");
            }
            catch (Exception ex)
            {
                CharSwapStatus = $"{(isAnalyze ? "Analyze" : "Generate")} failed: {ex.Message}";
                KleinProgress = 0;
                AddLog($"ERROR (Klein {(isAnalyze ? "analyze" : "generate")}): {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (baseStillIsTemp && !string.IsNullOrEmpty(baseStill))
                    try { File.Delete(baseStill); } catch { }
                IsCharSwapping = false;
            }
        }

        // Polls /history for a text output (ShowText|pysssss) on the given node. Mirrors the Image
        // Generator's Klein Control reader: the text can lag the execution-complete signal slightly, so
        // it retries for ~20 s before giving up.
        private async Task<string?> GetTextFromHistoryAsync(string promptId, string nodeId, CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
            Uri uri;
            try { uri = new Uri(baseUrl); } catch { uri = new Uri("http://127.0.0.1:8188"); }

            for (int i = 0; i < 10; i++)
            {
                if (i > 0) await Task.Delay(2000, token);
                token.ThrowIfCancellationRequested();
                try
                {
                    using var http = new System.Net.Http.HttpClient { BaseAddress = uri };
                    var response = await http.GetAsync("/history", token);
                    if (!response.IsSuccessStatusCode) continue;
                    var history = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        await response.Content.ReadAsStringAsync(token));
                    if (history == null || !history.TryGetValue(promptId, out var entry)) continue;
                    JsonElement outputs = default;
                    if (!entry.TryGetProperty("outputs", out outputs) &&
                        !(entry.TryGetProperty("result", out var r) && r.TryGetProperty("outputs", out outputs)))
                        continue;
                    if (outputs.TryGetProperty(nodeId, out var nodeOut) &&
                        nodeOut.TryGetProperty("text", out var textArr) &&
                        textArr.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var el in textArr.EnumerateArray())
                        {
                            var s = el.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) sb.AppendLine(s);
                        }
                        var text = sb.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { AddLog($"History poll: {ex.Message}"); }
            }
            return null;
        }

        // Drops <think>…</think> blocks and the commentary some QwenVL builds append after the prompt,
        // so only the usable prompt text reaches the editor. Same cleanup as the Image Generator's
        // Klein Control tab.
        private static string StripThinkingTokens(string text)
        {
            var opts = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            var result = System.Text.RegularExpressions.Regex.Replace(
                text, @"<think>[\s\S]*?</think>", string.Empty, opts).Trim();
            var kept = new List<string>();
            bool hasContent = false;
            foreach (var line in result.Split('\n'))
            {
                var t = line.Trim();
                if (hasContent && string.IsNullOrWhiteSpace(t)) break;
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\(?(Note|Note:)\b", opts)) break;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\(\d+\)\s+The (input|output)\b", opts)) break;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^(Therefore|However|Additionally|The original|I'll ensure|Since we must|Corrected version)\b", opts)) break;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"(corrected version should read|per instructions|based on instruction)", opts)) break;
                if (hasContent && t.Length > 40 && kept.Any(k => k.Contains(t.Substring(0, Math.Min(40, t.Length))))) break;
                hasContent = true;
                kept.Add(System.Text.RegularExpressions.Regex.Replace(line, @"\*\*", string.Empty));
            }
            return string.Join("\n", kept).Trim();
        }

        // Flux.2 Character Replacer (2 characters): LoadImage 40 = Character 1 (left), 60 = Character 2
        // (right), 39 = pose source (base scene frame). Stage 47 strips the two people in the pose frame,
        // stages 44/50 clean up each character reference, stage 9 composites both onto the scene with
        // three stacked reference latents (output node 9:65, saved by node 27 with the "char_replaced_2c"
        // prefix). The left/right mapping (image 2 = left, image 3 = right) is baked into the workflow
        // prompt (node 41). We reseed every noise source so each run varies.
        private JsonElement BuildCharReplacer2CharWorkflow(string uploadedChar1, string uploadedChar2, string uploadedPose)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, KleinCharReplacer2CharWorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Character Replacer (2 characters) workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse Character Replacer (2 characters) workflow JSON");

            var rng = new Random();

            UpdateNode(dict, "40", inputs => inputs["image"] = uploadedChar1); // Character 1 → left
            UpdateNode(dict, "60", inputs => inputs["image"] = uploadedChar2); // Character 2 → right
            UpdateNode(dict, "39", inputs => inputs["image"] = uploadedPose);  // pose = base scene frame

            // Reseed every noise source so successive runs differ.
            UpdateNode(dict, "42", inputs => inputs["value"] = rng.Next(1, 2_000_000_000));               // main combine seed (PrimitiveInt)
            UpdateNode(dict, "44:187", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L)); // Character 1 clean-up
            UpdateNode(dict, "50:187", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L)); // Character 2 clean-up
            UpdateNode(dict, "47:199", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L)); // posed-body strip

            return JsonSerializer.SerializeToElement(dict);
        }

        // Flux.2 Character Replacer (v2.4): LoadImage 40 = subject (likeness), LoadImage 39 = pose source.
        // Stage 47 rebuilds a neutral posed body from the pose image, stage 9 composites the subject onto
        // it (output node 9:65, saved by node 27 with the "char_replaced" prefix). We swap the two images
        // and reseed every noise source so each run varies. Everything else (prompts, sizing) is left as
        // authored — the graph already sizes the output to the pose image, keeping the widescreen aspect.
        private JsonElement BuildCharReplacerWorkflow(string uploadedSubject, string uploadedPose)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, KleinCharReplacerWorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Character Replacer workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse Character Replacer workflow JSON");

            var rng = new Random();

            UpdateNode(dict, "40", inputs => inputs["image"] = uploadedSubject); // subject = Character 1
            UpdateNode(dict, "39", inputs => inputs["image"] = uploadedPose);     // pose = base scene frame

            // Reseed every noise source so successive runs differ.
            UpdateNode(dict, "42", inputs => inputs["value"] = rng.Next(1, 2_000_000_000));        // main combine seed (PrimitiveInt)
            UpdateNode(dict, "44:187", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L)); // subject clean-up
            UpdateNode(dict, "47:199", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L)); // posed-body build
            UpdateNode(dict, "28:174", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L)); // face fix

            return JsonSerializer.SerializeToElement(dict);
        }

        // Klein Flux2 Control (flux2_klein_control_netAPI) — the same pipeline and models as the Image
        // Generator ▸ Advanced ▸ Control tab. LoadImage 1 = reference/subject (likeness), LoadImage 19 =
        // pose source (base scene frame), node 7 = RandomNoise. node 9 saves with the "flux2_klein" prefix
        // and the graph sizes the output to the pose image, keeping the base scene's aspect.
        //
        // customPrompt == null: leave the authored wiring, so QwenVL writes the prompt itself (node 57 =
        // subject appearance, node 62 = pose description, combined at 63 and shown by 59). That is the
        // Analyze pass, and it is also what Generate does when the prompt box is empty.
        //
        // customPrompt set: write the text into node 6 (the base Flux.2 positive encode) and nothing else.
        // This deliberately mirrors KleinControlViewModel.BuildWorkflow line for line — an earlier version
        // here also overrode node 201 (the PiD 4K encode) and pruned the QwenVL chain, which changed the
        // images relative to the Control tab. The graph is left exactly as the Control tab submits it.
        private JsonElement BuildKleinControlWorkflow(string uploadedSubject, string uploadedPose, string? posePath, string? customPrompt)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, KleinControlWorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Klein Control workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse Klein Control workflow JSON");

            UpdateNode(dict, "1", inputs => inputs["image"] = uploadedSubject); // reference = Character 1
            UpdateNode(dict, "19", inputs => inputs["image"] = uploadedPose);    // pose = base scene frame
            UpdateNode(dict, "7", inputs => inputs["noise_seed"] = new Random().NextInt64(0, 999_999_999_999_999L));

            // Size both stages from one base size instead of letting the graph derive them from two
            // independent rescales (1 MP via nodes 17/45/46, 16 MP via nodes 205/206). That mismatch
            // put the long axis at ~5461 px on a 9:16 frame and tinted the bottom quarter. Keeping the
            // canvas at exactly 4x the base is what the other PiD workflows in this repo do.
            var (baseWidth, baseHeight) = ComputeBaseSize(posePath);
            int pidWidth = baseWidth * PidScale;
            int pidHeight = baseHeight * PidScale;

            UpdateNode(dict, "8:17", inputs => { inputs["width"] = baseWidth; inputs["height"] = baseHeight; });
            UpdateNode(dict, "8:18", inputs => { inputs["width"] = baseWidth; inputs["height"] = baseHeight; });
            UpdateNode(dict, "207", inputs => { inputs["width"] = pidWidth; inputs["height"] = pidHeight; });
            AddLog($"Klein Control base {baseWidth}x{baseHeight} → PiD canvas {pidWidth}x{pidHeight}");

            if (customPrompt != null)
            {
                UpdateNode(dict, "6", inputs => inputs["text"] = customPrompt);
                AddLog($"Klein Control prompt (user): {customPrompt}");
            }
            else
            {
                AddLog("Klein Control prompt: auto (QwenVL appearance + pose)");
            }

            return JsonSerializer.SerializeToElement(dict);
        }

        // Base render size at the pose frame's aspect: ~1 MP, but never more than MaxBaseAxis on the
        // long edge so the 4x PiD canvas stays inside MaxPidAxis.
        private (int Width, int Height) ComputeBaseSize(string? posePath)
        {
            int srcW = 1024, srcH = 1024;
            try
            {
                using var stream = File.OpenRead(posePath!);
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (frame.PixelWidth > 0 && frame.PixelHeight > 0)
                {
                    srcW = frame.PixelWidth;
                    srcH = frame.PixelHeight;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Could not read pose frame dimensions ({ex.Message}) — using {srcW}x{srcH}");
            }

            var scale = Math.Sqrt(BaseTargetPixels / ((double)srcW * srcH));
            scale = Math.Min(scale, (double)MaxBaseAxis / Math.Max(srcW, srcH));

            return (SnapToBlock(srcW * scale), SnapToBlock(srcH * scale));
        }

        private static int SnapToBlock(double value)
            => Math.Clamp((int)Math.Round(value / 16.0) * 16, 256, MaxBaseAxis);

        // Krea2 Edit (two ref): LoadImage 72 = image A (base scene frame, the person to replace),
        // LoadImage 86 = image B (subject/likeness = Character 1). The grounded-encode prompt (node 84)
        // replaces the person in image A with the subject in image B; node 53 is the KSampler (reseeded)
        // and node 29 saves with the "krea2_edit" prefix. The graph sizes the output from the Resolution
        // Selector (node 83), and the prompt is authored inside the workflow, so we only wire the two
        // images and reseed the sampler.
        private JsonElement BuildKrea2EditWorkflow(string uploadedSubject, string uploadedPose, int targetWidth, int targetHeight)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Krea2EditWorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Krea2 Edit workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse Krea2 Edit workflow JSON");

            UpdateNode(dict, "72", inputs => inputs["image"] = uploadedPose);    // image A = base scene frame
            UpdateNode(dict, "86", inputs => inputs["image"] = uploadedSubject); // image B = Character 1

            // Node 84 = grounded-encode instruction. Use the user's edited prompt, falling back to the
            // authored default if it was cleared.
            var editPrompt = string.IsNullOrWhiteSpace(Krea2EditPrompt) ? DefaultKrea2EditPrompt : Krea2EditPrompt.Trim();
            UpdateNode(dict, "84", inputs => inputs["prompt"] = editPrompt);
            AddLog($"Krea2 Edit prompt: {editPrompt}");

            // Match the output to the base frame's aspect ratio instead of the authored 1:1. The
            // ResolutionSelector (node 83) feeds both the latent size (node 82) and the pose resize
            // (node 77); overriding those literal width/height detaches them from the square selector so
            // a landscape frame produces a landscape image (and portrait → portrait). targetWidth == 0
            // means we couldn't read the frame, so leave the authored square wiring in that case.
            if (targetWidth > 0 && targetHeight > 0)
            {
                UpdateNode(dict, "82", inputs => { inputs["width"] = targetWidth; inputs["height"] = targetHeight; });
                UpdateNode(dict, "77", inputs =>
                {
                    inputs["resize_type.width"] = targetWidth;
                    inputs["resize_type.height"] = targetHeight;
                });
                AddLog($"Krea2 Edit output size matched to base frame: {targetWidth}×{targetHeight}");
            }

            // Reseed the sampler so successive runs differ.
            UpdateNode(dict, "53", inputs => inputs["seed"] = new Random().NextInt64(0, 999_999_999_999_999L));

            return JsonSerializer.SerializeToElement(dict);
        }

        // Derives an output width/height that matches the base frame's aspect ratio, at ~1 megapixel
        // (matching the workflow's authored megapixels) and rounded to a multiple of 8 (Krea2/Flux latent
        // requirement). Returns (0, 0) if the frame dimensions can't be read, so the caller keeps the
        // workflow's authored square default.
        private (int width, int height) ComputeKrea2TargetDimensions(string? baseFramePath, double megapixels = 1.0)
        {
            try
            {
                if (string.IsNullOrEmpty(baseFramePath) || !File.Exists(baseFramePath)) return (0, 0);

                int srcW, srcH;
                using (var stream = File.OpenRead(baseFramePath))
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                    srcW = decoder.Frames[0].PixelWidth;
                    srcH = decoder.Frames[0].PixelHeight;
                }
                if (srcW <= 0 || srcH <= 0) return (0, 0);

                double aspect = srcW / (double)srcH;
                double area = megapixels * 1_000_000.0;
                double h = Math.Sqrt(area / aspect);
                double w = aspect * h;

                int W = Math.Max(8, (int)(Math.Round(w / 8.0) * 8));
                int H = Math.Max(8, (int)(Math.Round(h / 8.0) * 8));
                return (W, H);
            }
            catch (Exception ex)
            {
                AddLog($"Krea2 Edit: could not read base frame size ({ex.Message}); using authored square output");
                return (0, 0);
            }
        }

        private static void UpdateNode(
            Dictionary<string, JsonElement> dict,
            string nodeId,
            Action<Dictionary<string, object>> updater)
        {
            if (!dict.ContainsKey(nodeId)) return;
            var node = JsonSerializer.Deserialize<Dictionary<string, object>>(dict[nodeId].GetRawText());
            if (node == null || !node.ContainsKey("inputs")) return;
            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
            if (inputs == null) return;
            updater(inputs);
            node["inputs"] = inputs;
            dict[nodeId] = JsonSerializer.SerializeToElement(node);
        }

        private async Task<byte[]?> RetrieveKleinImageAsync(string promptId, string savePrefix, CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
            Uri uri;
            try { uri = new Uri(baseUrl); } catch { uri = new Uri("http://127.0.0.1:8188"); }
            bool isRemote = !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

            const int maxRetries = 30;
            const int retryDelayMs = 5000;

            if (isRemote)
            {
                for (int i = 0; i < maxRetries; i++)
                {
                    if (i > 0) await Task.Delay(retryDelayMs, token);
                    token.ThrowIfCancellationRequested();
                    var files = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                    var imgFile = files.FirstOrDefault(f =>
                        Path.GetFileName(f).StartsWith(savePrefix, StringComparison.OrdinalIgnoreCase) && IsImageExt(f));
                    imgFile ??= files.FirstOrDefault(f =>
                        IsImageExt(f) && !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase));
                    if (imgFile != null)
                    {
                        var data = await _comfyUIService.HttpClient.DownloadOutputImageAsync(imgFile);
                        if (data != null) return data;
                    }
                }
                return null;
            }

            var outputDir = _settingsService.Settings?.OutputFolderPath;
            if (string.IsNullOrEmpty(outputDir)) { AddLog("ERROR: Output folder not configured"); return null; }
            for (int i = 0; i < maxRetries; i++)
            {
                if (i > 0) await Task.Delay(retryDelayMs, token);
                token.ThrowIfCancellationRequested();
                var files = Directory.GetFiles(outputDir, $"{savePrefix}*.png", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime).ToList();
                if (files.Any())
                {
                    var latest = files[0];
                    var age = DateTime.Now - File.GetLastWriteTime(latest);
                    if (age.TotalSeconds < 180) return await File.ReadAllBytesAsync(latest, token);
                }
            }
            return null;
        }

        // Single-frame grab at an arbitrary timestamp (full resolution, PNG).
        private async Task<string?> ExtractFrameAtAsync(string videoPath, double timeSeconds, CancellationToken token)
        {
            var ffmpegPath = FindFFmpeg();
            if (ffmpegPath == null) return null;

            var tempFile = Path.Combine(Path.GetTempPath(), $"scail2_base_{Guid.NewGuid():N}.png");
            var ts = Math.Max(0, timeSeconds);

            await Task.Run(() =>
            {
                var si = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-loglevel quiet -nostats -ss {ts:F3} -i \"{videoPath}\" -frames:v 1 -q:v 2 \"{tempFile}\" -y",
                    UseShellExecute = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(si);
                if (proc != null && !proc.WaitForExit(30000))
                    try { proc.Kill(); } catch { }
            }, token);

            return File.Exists(tempFile) ? tempFile : null;
        }

        #endregion

        #region Stage B — SCAIL II motion transfer

        /// <summary>
        /// Triggered by the explicit "Generate video" button once the In/Out range is set: runs the
        /// inherited appearance/motion analysis (over the trimmed range) and enqueues the SCAIL II job,
        /// which auto-processes. No-op until the char-swap character image exists.
        /// </summary>
        public async Task OnTrimFinalizedAsync()
        {
            if (IsCharSwapping || IsProcessingQueue) return;
            if (!HasCharacterImage || !HasInputVideo) return;

            try
            {
                CharSwapStatus = "Analyzing motion and generating the video…";
                AddLog("=== Scail 2: motion-transfer stage ===");
                await AnalyzeImageAsync();
                AddAllChunksToQueue(); // enqueues all chunks → ProcessQueueAsync runs automatically
            }
            catch (Exception ex)
            {
                CharSwapStatus = $"Generate failed: {ex.Message}";
                AddLog($"ERROR (motion-transfer): {ex.Message}");
            }
        }

        #endregion

        #region Stage B — workflow (SCAIL-2 segmentation control)

        // Scail 2 uses the "Wan SCAIL-2 segmentation control" workflow instead of the simple single-node
        // one inherited from WanScailGgufViewModel. Its sampler (SCAILAutoExtend, node 199:180) walks the
        // whole clip autoregressively in overlapping windows, so the C# side still runs one whole-video
        // execution (FramesPerChunk == int.MaxValue, inherited). The node layout is completely different
        // from both the simple workflow and the previous hi-res-fix one, so the whole
        // UpdateWorkflowParameters mapping is overridden below.
        protected override string WorkflowFileName =>
            Path.Combine("video", "wan", "Wan SCAIL-2 segmentation control.json");

        protected override JsonElement UpdateWorkflowParameters(
            JsonElement workflow,
            string characterImageName,
            string videoName,
            int startFrame,
            int framesInChunk,
            string prompt,
            string negativePrompt,
            int fps,
            int maxEdge,
            long seed,
            int outputWidth = 0,
            int outputHeight = 0,
            WanScailQueueItem? item = null)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText())
                ?? throw new InvalidOperationException("Failed to parse SCAIL2 segmentation-control workflow JSON");

            // TryGetVideoFromHistoryAsync takes the FIRST mp4 the prompt produced and does not filter out
            // save_output=false temp previews, so every video sink except the final combine has to go:
            //   88      "Segmented Input Video"  — the coloured SAM mask rendered over the driving clip
            //   199:609 "Preview Video"          — the same mask stack, inside the SCAIL subgraph
            //   199:617 "Preview  Image"         — the per-frame mask preview
            //   92      "Segmented Input Image"  — a PreviewImage (no mp4, but pure preview work)
            // All four are terminal (nothing reads their output), so removing them only skips rendering.
            foreach (var previewNode in new[] { "88", "92", "199:609", "199:617" })
                dict.Remove(previewNode);

            // Audio: node 414 picks between an uploaded mp3 (node 204) and the driving video's own audio
            // (node 207 output 2), and the authored graph selects the video. Node 204 still names a
            // hard-coded "ltx_flow.mp3" that will not exist on the server, and ComfyUI validates inputs it
            // never executes, so the whole prompt would be rejected. Point both switch branches at the
            // video's audio and drop the upload node — matching the previous workflow, which also fed the
            // final combine from the driving video's audio track.
            var videoAudio = new object[] { "207", 2 };
            UpdateNode(dict, "414", inputs => { inputs["input1"] = videoAudio; inputs["input2"] = videoAudio; });
            dict.Remove("204");

            // This prompt always ends at the raw sampler frames, saved by node 425. The authored graph
            // continues into RIFE (599) → RTX upscale (196) → node 8, but running that tail here means the
            // only save node in the graph sits behind it: the RTX upscale materialises every frame at 2×
            // in float32 and reliably exhausts host RAM on a long clip, and when it dies it takes the
            // sampler's frames — which only ever existed as tensors — with it. An hour of correct sampling
            // is not worth risking on a post-process, so the tail moved to its own prompt over the saved
            // mp4 (see RunPostProcessAsync). InterpolateAndUpscale now selects whether that second prompt
            // runs, not what this one contains.
            //
            // Both branches of 624 have to point at the raw frames (602) before 599 goes, otherwise the
            // unselected input1 is a link to a node that no longer exists and ComfyUI rejects the whole
            // prompt at validation — it checks every declared input, including ones a lazy switch will
            // never pull on. Node 425 is re-pointed at node 624 so the authored VRAM-cleanup chain
            // (436 → 430 cleanGpuUsed → 520 clearCacheAll → 602) still runs on the raw path.
            const string finalCombineNode = "425";
            var rawFrames = new object[] { "602", 0 };
            UpdateNode(dict, "624", inputs =>
            {
                inputs["select"] = 2;
                inputs["input1"] = rawFrames;
                inputs["input2"] = rawFrames;
            });
            UpdateNode(dict, "425", inputs => inputs["images"] = new object[] { "624", 0 });
            foreach (var n in new[] { "8", "196", "599" })
                dict.Remove(n);

            var workflowJson = JsonSerializer.Serialize(dict);

            var subject = string.IsNullOrWhiteSpace(item?.Subject) ? "person" : item!.Subject.Trim();

            // Node 199:502 "replacement_mode" is the inverse of ReplaceBackground: replacement mode keeps
            // the original background (character only), while leaving it off regenerates the whole frame
            // (character + background).
            bool replaceBackground = item?.ReplaceBackground ?? true;
            bool replacementMode = !replaceBackground;

            // This workflow trims in FRAMES on the loader itself (node 207 skip_first_frames /
            // frame_load_cap, both applied after force_rate), so the trim markers map straight across —
            // no seconds conversion like the previous graph needed. cap 0 = load the whole clip.
            int skipFrames = Math.Max(0, item?.TrimSkipFrames ?? 0);
            int capFrames = Math.Max(0, item?.TrimFrameCap ?? 0);

            // Sampler window, snapped to the 4n+1 the WAN latent temporal stride wants (24→25, 40→41,
            // 60→61, 80→81 — 81 being the workflow's authored value).
            int windowFrames = Math.Max(5, (int)Math.Round(VideoBatchSize / 4.0) * 4 + 1);

            // Generation canvas. The base view model already resolved this through
            // ComputeOutputResolution (and centre-cropped the character image to it before upload), so use
            // what it passed down; fall back to the raw setting only if it could not read the driving
            // video's dimensions.
            var (outResW, outResH) = (outputWidth, outputHeight);
            if (outResW <= 0 || outResH <= 0)
                (outResW, outResH) = ParseOutputResolution();

            AddLog($"Updating SCAIL2 segmentation-control workflow: whole video, fps={fps}, " +
                   $"subject=\"{subject}\", replacementMode={replacementMode}, skip={skipFrames} frames, " +
                   $"cap={(capFrames > 0 ? capFrames + " frames" : "all")}, " +
                   $"post={(InterpolateAndUpscale ? "RIFE 2× + RTX upscale 2× (second prompt)" : "none")}");

            // Node 208: main character / reference image (LoadImage)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "208", "image", characterImageName);

            // Node 207: reference (driving) video, resampled to the target fps and trimmed to the markers.
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "207", new Dictionary<string, object>
            {
                { "video", videoName },
                { "force_rate", fps },
                { "skip_first_frames", skipFrames },
                { "frame_load_cap", capFrames }
            });

            // Node 605: positive prompt, node 199:4: negative prompt (both CLIPTextEncode).
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "605", "text", prompt);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "199:4", "text", negativePrompt);

            // SAM3 grounding text for the identity tracker (node 199:492): 199:497 segments the subject in
            // the reference image, 199:485 segments it in the driving video. Both get the same subject so
            // the tracker links the same person across the two.
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "199:497", "text", subject);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "199:485", "text", subject);

            // Node 199:502: replacement (keep background) vs. animation (regenerate everything).
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "199:502", "value", replacementMode);

            // Seed: node 199:534 is the single INTConstant both samplers read.
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "199:534", "value", seed);

            // Sampler window. Node 199:596 selects which sampler runs (1 = SCAILAutoExtend 199:180,
            // 2 = WanSCAILInfinity 199:528); both are written so the setting holds either way.
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "199:180", "chunk_length", windowFrames);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "199:528", "window_length", windowFrames);
            AddLog($"SCAIL2 sampler window: {windowFrames} frames (from chunk size {VideoBatchSize})");

            // Final combine — match the frame rate and pin into the wan_scail subfolder so the
            // filesystem-polling fallback (OutputSubfolder = "wan_scail") can find it.
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, finalCombineNode, new Dictionary<string, object>
            {
                { "frame_rate", fps },
                { "filename_prefix", "wan_scail/SCAIL2_seg" },
                { "save_output", true }
            });

            // Output resolution: node 199:89 (ImageResizeKJv2) crops/resizes the driving video, and its
            // width/height outputs drive both samplers and the reference-image resize (199:500) — so it is
            // the single knob that sets the resolution the whole SCAIL II loop generates at. "0x0" leaves
            // the authored default.
            if (outResW > 0 && outResH > 0)
            {
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "199:89", new Dictionary<string, object>
                {
                    { "width", outResW },
                    { "height", outResH }
                });
                AddLog($"SCAIL2 output resolution: {outResW}×{outResH} " +
                       $"({(IsAutoResolution ? "auto — matches the driving video's aspect" : "fixed preset")}, " +
                       $"Resize Image Video node 199:89)");
            }
            else
            {
                AddLog($"SCAIL2 output resolution: authored default " +
                       $"({AuthoredCanvasWidth}×{AuthoredCanvasHeight} portrait, node 199:89)");
            }

            AddLog("✓ SCAIL2 segmentation-control workflow nodes updated");
            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        #endregion

        #region Stage B — post pass (RIFE + RTX upscale as a second prompt)

        // Raw frames per post segment. The RTX upscale holds its entire output batch in host RAM at
        // float32, and RIFE has already doubled the frame count by then, so one segment costs roughly
        //     2·N · (2·W)·(2·H) · 3 channels · 4 bytes
        // ≈ 18 GB at N = 300 on the authored 640×960 canvas. The whole 1141-frame clip in one pass would
        // have wanted ~67 GB, which is what used to take the run down.
        private const int PostSegmentFrames = 300;

        // Node id for the ImageFromBatch that drops each segment's duplicated leading frame. Any id not
        // already in the authored graph works; 700 is clear of both the top-level and 199:* ranges.
        private const string PostTrimNode = "700";

        /// <summary>
        /// Second ComfyUI prompt: RIFE 2× interpolation and the RTX 2× upscale, run over the raw mp4 the
        /// sampler already saved rather than as a tail on the sampler's own graph.
        /// </summary>
        /// <remarks>
        /// Three things this buys over the authored single-graph layout:
        /// the sampler's output is on disk before any of this is attempted, so a failure here costs only
        /// the post; the clip is fed through in <see cref="PostSegmentFrames"/>-frame segments, so peak
        /// host RAM is bounded by segment size instead of clip length; and the pass is re-runnable on its
        /// own against the saved raw video.
        ///
        /// Segments overlap by one raw frame. RIFE turns n frames into 2n−1 — it emits intermediates
        /// *between* frames — so butt-joined segments would lose the intermediate spanning every cut. The
        /// overlap hands the next segment the previous segment's last frame as its first, and node 700
        /// drops the duplicate from the output batch, leaving the frame sequence continuous across joins.
        ///
        /// Audio is deliberately not rendered per segment: VHS_LoadVideo's audio output is not reliably
        /// sliced to the loaded frame range, and a full-length track on every segment would survive the
        /// concat as garbage. The segments are rendered mute and the raw video's audio is muxed back over
        /// the joined result, which lines up exactly — 2× the frame rate over 2× the frames is the same
        /// wall-clock duration.
        /// </remarks>
        protected override async Task<string?> RunPostProcessAsync(
            string rawVideoPath, WanScailQueueItem item, CancellationToken cancellationToken)
        {
            if (!InterpolateAndUpscale) return null;

            var segmentFiles = new List<string>();
            try
            {
                AddLog("=== SCAIL2 post pass: RIFE 2× + RTX upscale 2× (second prompt) ===");
                ProcessingStatus = "Post: interpolating and upscaling…";

                var totalRawFrames = GetVideoFrameCount(rawVideoPath);
                if (totalRawFrames <= 0)
                {
                    AddLog("Post pass skipped: could not read the raw video's frame count. Keeping the raw video.");
                    return null;
                }

                var (rawW, rawH) = GetVideoDimensions(rawVideoPath);
                var segmentCount = (int)Math.Ceiling((double)totalRawFrames / PostSegmentFrames);
                AddLog($"Post: {totalRawFrames} frames at {rawW}×{rawH} → " +
                       $"{segmentCount} segment(s) of ≤{PostSegmentFrames} raw frames");

                if (!_comfyUIService.IsConnected)
                    await _comfyUIService.ConnectAsync();

                AddLog("Post: uploading the raw video…");
                var uploadedRaw = await _comfyUIService.UploadVideoAsync(rawVideoPath);
                if (string.IsNullOrEmpty(uploadedRaw))
                {
                    AddLog("Post pass skipped: failed to upload the raw video. Keeping the raw video.");
                    return null;
                }
                AddLog($"Post: raw video uploaded as {uploadedRaw}");

                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", WorkflowFileName);
                var baseWorkflowJson = await File.ReadAllTextAsync(workflowPath, cancellationToken);

                for (int seg = 0; seg < segmentCount; seg++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Segment s covers raw frames [start, start + cap - 1]; every segment after the first
                    // starts one frame early so it shares a frame with its predecessor (see remarks).
                    var start = seg == 0 ? 0 : seg * PostSegmentFrames - 1;
                    var cap = Math.Min(totalRawFrames - start, PostSegmentFrames + (seg == 0 ? 0 : 1));
                    if (cap <= 0) break;

                    AddLog($"Post: segment {seg + 1}/{segmentCount} — raw frames {start}–{start + cap - 1}");
                    ProcessingStatus = $"Post: segment {seg + 1}/{segmentCount}";
                    ProcessingProgress = 85.0 + 14.0 * seg / segmentCount;

                    var segmentWorkflow = BuildPostWorkflow(
                        baseWorkflowJson, uploadedRaw, start, cap, dropLeadingFrame: seg > 0, fps: item.Fps);

                    var existingFiles = GetExistingVideoFiles("*.mp4", OutputSubfolder);
                    var promptId = await _comfyUIService.ExecuteWorkflowAsync(
                        segmentWorkflow, null, cancellationToken, executionTimeout: ExecutionTimeout);
                    AddLog($"Post: segment {seg + 1} submitted, prompt ID: {promptId}");

                    var outputVideo = await TryGetVideoFromHistoryAsync(promptId)
                                      ?? await WaitForNewVideoAsync(
                                          existingFiles, "*.mp4", ExecutionTimeout,
                                          TimeSpan.FromSeconds(5), OutputSubfolder);

                    if (outputVideo == null || !File.Exists(outputVideo))
                    {
                        AddLog($"Post: segment {seg + 1} produced no output — abandoning the post pass, " +
                               "keeping the raw video.");
                        return null;
                    }

                    var segmentFile = Path.Combine(
                        Path.GetTempPath(), $"scail2_post_{seg:D3}_{Path.GetFileName(outputVideo)}");
                    File.Copy(outputVideo, segmentFile, true);
                    segmentFiles.Add(segmentFile);
                    AddLog($"Post: segment {seg + 1}/{segmentCount} complete");
                }

                if (segmentFiles.Count == 0)
                {
                    AddLog("Post pass produced no segments. Keeping the raw video.");
                    return null;
                }

                // Join the mute segments, then mux the raw video's audio back over the result.
                var mutePath = Path.Combine(
                    Path.GetTempPath(), $"scail2_post_joined_{Guid.NewGuid():N}.mp4");
                if (segmentFiles.Count == 1)
                    File.Copy(segmentFiles[0], mutePath, true);
                else
                    MergeVideoChunks(segmentFiles, mutePath, "scail2post");

                var finalPath = Path.Combine(
                    Path.GetDirectoryName(rawVideoPath)!,
                    Path.GetFileNameWithoutExtension(rawVideoPath) + "_hires.mp4");

                if (!MuxAudioFrom(rawVideoPath, mutePath, finalPath))
                {
                    // No audio track, or ffmpeg refused the mux — the picture is still the point.
                    File.Copy(mutePath, finalPath, true);
                    AddLog("Post: no audio muxed; keeping the interpolated video as-is.");
                }
                try { File.Delete(mutePath); } catch { /* temp file: best effort */ }

                var info = new FileInfo(finalPath);
                AddLog($"=== SCAIL2 post pass complete: {Path.GetFileName(finalPath)} " +
                       $"({info.Length / 1024 / 1024:F1}MB, {item.Fps * 2}fps, {rawW * 2}×{rawH * 2}) ===");
                return finalPath;
            }
            catch (OperationCanceledException)
            {
                AddLog("Post pass cancelled — keeping the raw video.");
                return null;
            }
            catch (Exception ex)
            {
                // Never let the post pass throw: the sampler's video is already the run's result.
                AddLog($"Post pass failed ({ex.Message}) — keeping the raw video.");
                return null;
            }
            finally
            {
                foreach (var f in segmentFiles)
                    try { File.Delete(f); } catch { /* temp file: best effort */ }
            }
        }

        /// <summary>
        /// Prunes the SCAIL-2 graph down to its post-processing tail and points it at an uploaded video:
        /// 207 (loader) → 599 (RIFE) → 700 (drop the shared frame) → 196 (RTX upscale) → 8 (combine).
        /// Everything else — the samplers, the SAM3 tracker, the loaders, the raw sink — is dropped.
        /// </summary>
        private JsonElement BuildPostWorkflow(
            string baseWorkflowJson, string uploadedVideo, int startFrame, int frameCap, bool dropLeadingFrame, int fps)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(baseWorkflowJson)
                ?? throw new InvalidOperationException("Failed to parse SCAIL2 workflow JSON for the post pass");

            foreach (var id in dict.Keys.ToList())
                if (id is not ("207" or "599" or "196" or "8"))
                    dict.Remove(id);

            // 207: the saved raw video. force_rate 0 and format "None" keep it exactly as written —
            // it is already at the target rate and canvas, and any resample here would fight the sampler.
            UpdateNode(dict, "207", inputs =>
            {
                inputs["video"] = uploadedVideo;
                inputs["force_rate"] = 0;
                inputs["custom_width"] = 0;
                inputs["custom_height"] = 0;
                inputs["select_every_nth"] = 1;
                inputs["skip_first_frames"] = startFrame;
                inputs["frame_load_cap"] = frameCap;
                inputs["format"] = "None";
            });

            // 599: RIFE reads the loader directly — node 602 and the VRAM-cleanup chain feeding it
            // belong to the sampler graph and are gone.
            UpdateNode(dict, "599", inputs => inputs["frames"] = new object[] { "207", 0 });

            // 700: drop the leading frame this segment shares with the previous one. length is the node's
            // 4096 ceiling, which comfortably exceeds a segment's 2·N−1 output frames and means "the rest".
            dict[PostTrimNode] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["inputs"] = new Dictionary<string, object>
                {
                    ["image"] = new object[] { "599", 0 },
                    ["batch_index"] = dropLeadingFrame ? 1 : 0,
                    ["length"] = 4096
                },
                ["class_type"] = "ImageFromBatch",
                ["_meta"] = new Dictionary<string, object> { ["title"] = "Drop shared segment frame" }
            });

            UpdateNode(dict, "196", inputs => inputs["images"] = new object[] { PostTrimNode, 0 });

            // 8: mute (audio is muxed back after the join) and pinned into wan_scail so the
            // filesystem-polling fallback can find it. RIFE doubled the frames, so double the rate.
            UpdateNode(dict, "8", inputs =>
            {
                inputs["images"] = new object[] { "196", 0 };
                inputs["frame_rate"] = fps * 2;
                inputs["filename_prefix"] = "wan_scail/SCAIL2_post";
                inputs["save_output"] = true;
                inputs.Remove("audio");
            });

            return JsonSerializer.SerializeToElement(dict);
        }

        /// <summary>
        /// Copies the audio track of <paramref name="audioSource"/> over <paramref name="videoSource"/>.
        /// Returns false when there is no audio to copy or ffmpeg is unavailable.
        /// </summary>
        private bool MuxAudioFrom(string audioSource, string videoSource, string outputPath)
        {
            var ffmpeg = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpeg)) return false;

            try
            {
                RunFFmpeg(ffmpeg,
                    $"-y -i \"{videoSource}\" -i \"{audioSource}\" " +
                    "-map 0:v:0 -map 1:a:0? -c:v copy -c:a aac -shortest " +
                    $"\"{outputPath}\"");
                return File.Exists(outputPath);
            }
            catch (Exception ex)
            {
                AddLog($"Post: audio mux failed ({ex.Message}).");
                return false;
            }
        }

        #endregion

        #region Helpers

        private static BitmapImage LoadBitmap(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private static bool IsImageExt(string f) =>
            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

        #endregion
    }
}
