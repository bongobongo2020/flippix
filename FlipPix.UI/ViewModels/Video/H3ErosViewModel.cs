using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "H3 Eros" tab — the 🧪 H3 Experimental flow (story in, the beat sheet and the per-clip writer, the
    /// cast sheets, the wardrobe lock, the clip queue, the FFmpeg join) rendered through the author's
    /// <b>MiniMax SEEDHUNTER v122 EROS-Hybrid</b> graph, which turns every clip into a seed hunt.
    ///
    /// <para><b>What a clip costs, and why it is two submissions.</b> The graph builds one
    /// <c>MiniMaxH3ReferenceToVideo</c> conditioning and latent and then samples it <b>three times</b>,
    /// with three noise seeds, at a small draft canvas. That is the hunt: three cheap takes of the same
    /// prompt to choose a composition from. The picked take's latent — not a re-render of it — is then
    /// split, lifted to the finished megapixels by <c>MinimaxH3LatentUpscaler3D</c>, re-sampled by a
    /// short fixed-sigma pass, interpolated by RIFE and muxed. So:</para>
    /// <list type="number">
    /// <item><b>Hunt.</b> The graph pruned to its three preview sinks. Three clips come back and land in
    /// the sample strip.</item>
    /// <item><b>Pick.</b> The render stops here and waits — with the GPU lease <i>released</i>, so the
    /// rest of the app is not locked out while a human looks at three videos. Re-roll runs the hunt
    /// again on fresh seeds; Use this one moves on. <see cref="AutoPickSample"/> answers for you.</item>
    /// <item><b>Finish.</b> The same file, the same first-pass inputs — byte for byte, so ComfyUI's own
    /// cache hands back the exact latent the preview was decoded from rather than sampling it again —
    /// with the picked sampler wired into the upscale and the graph pruned to the final sink.</item>
    /// </list>
    ///
    /// <para><b>Two megapixel dials, not one.</b> <see cref="PreviewMegapixels"/> is what the three
    /// samples are drafted at (the composition, cheaply); <c>Megapixels</c> — the tab's usual quality
    /// dropdown — is what the picked one is finished at. They are independent here because the upscaler
    /// takes a target, not a factor, so the hunt can be as cheap as it likes without deciding the
    /// finished size.</para>
    ///
    /// <para>Everything before the render is inherited unchanged from
    /// <see cref="H3ExperimentalViewModel"/>: the story and scene inputs, the wardrobe derived once and
    /// locked, the two character cards and their panel-split sheets, the two-step writer that turns a
    /// story into a clip chain (one call divides it into one beat per clip, then one call writes each
    /// clip from its beat), the queue, and the FFmpeg join that runs when the last clip of a chain
    /// lands.</para>
    /// </summary>
    public partial class H3ErosViewModel : H3ExperimentalViewModel
    {
        /// <summary>How many samples one hunt produces. The graph has exactly three sampler branches.</summary>
        public const int SampleCount = 3;

        // ── Workflow node ids (locked to h3-minimax/h3-eros.json; see tools/convert_h3_eros.py) ──
        private const string NodePrompt = "22:11";        // PrimitiveStringMultiline
        private const string NodeSeconds = "22:23";       // PrimitiveFloat → the frame-count expression
        private const string NodeSteps = "22:8";          // INTConstant → BasicScheduler steps (first pass)
        private const string NodeResolution = "22:9";     // ResolutionSelector — the *draft* canvas
        private const string NodeRef2V = "5";             // MiniMaxH3ReferenceToVideo
        private const string NodeLatentSplit = "242";     // LTXVSeparateAVLatent — reads the picked latent
        private const string NodeUpscaler = "243";        // MinimaxH3LatentUpscaler3D — the finished canvas
        private const string NodeUpscaleSampler = "135:26";  // SamplerCustomAdvanced — the 2nd pass
        private const string NodeUpscaleNoise = "135:27"; // RandomNoise — the 2nd pass's own seed
        private const string NodeSinglePassVideo = "259"; // VAEDecode of the picked latent, upscale off
        private const string NodeSinglePassAudio = "258"; // VAEDecodeAudio of the same
        private const string NodeUpscaledVideo = "189";   // VAEDecode of the 2nd pass
        private const string NodeUpscaledAudio = "190";   // VAEDecodeAudio of the 2nd pass
        private const string NodeRife = "165";            // RIFEInterpolation — 24 → 48 fps
        private const string NodeFinalSave = "34";        // VHS_VideoCombine — the finished clip

        // Added to the graph, not present in the file: the two INT sources that stand in for
        // ResolutionSelector's outputs when the chosen aspect is one its combo does not accept.
        private const string NodeCanvasWidth = "eros_canvas_w";
        private const string NodeCanvasHeight = "eros_canvas_h";

        /// <summary>ManualSigmas schedules, by step count. The graph ships all three; the render links one.</summary>
        private static readonly Dictionary<int, string> SigmaSchedules = new()
        {
            [3] = "222",   // 0.9035, 0.6316, 0.3158, 0.0000
            [4] = "221",   // 0.9035, 0.8000, 0.6316, 0.3158, 0.0000
            [5] = "220",   // 0.9231, 0.8780, 0.8000, 0.6316, 0.3158, 0.0000
        };

        /// <summary>Preview slot (1-based) → the sampler that produced it and the sink that saved it.</summary>
        private static readonly (string Sampler, string Sink, string Noise)[] SampleBranches =
        {
            ("125:12", "18", "125:17"),
            ("133:129", "134", "133:128"),
            ("143:139", "144", "143:138"),
        };

        /// <summary>The sampler's <c>denoised_output</c>. Slot 0 is <c>output</c>, which is what the
        /// previews decode; the upscale reads the denoised one, exactly as the authored graph does.</summary>
        private const int DenoisedSlot = 1;

        /// <summary>Frames per second the preview sinks and the un-interpolated final are muxed at.</summary>
        private const int DraftFrameRate = 24;

        private readonly ObservableCollection<SeedHuntSample> _samples = new(
            Enumerable.Range(1, SampleCount).Select(i => new SeedHuntSample(i)));

        /// <summary>Completed by the sample-strip buttons; awaited by the render between the two phases.</summary>
        private TaskCompletionSource<int>? _pick;

        private double _previewMegapixels = 0.2;
        private int _upscaleSteps = 4;
        private bool _useRife = true;
        private bool _autoPickSample;
        private int _autoPickSlot = 1;
        private bool _isAwaitingPick;
        private string _huntStatus = string.Empty;
        private string? _activePreviewUri;
        private long _huntSeed = -1;
        private int _huntRound;

        public H3ErosViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, lmStudioService, logger, settingsService, serviceProvider,
                   workflowCoordinator, fileDialogService)
        {
            // The three previews are the whole point of this graph, and they are drafts — the finished
            // canvas is the upscaler's target, not a multiple of the draft. Nothing here doubles anything,
            // so the Duo tab's draft/finish switch has no meaning and stays off.
            UseLatentUpscale = false;
            RtxUpscale = false;

            UseSampleCommand = new RelayCommand<SeedHuntSample>(UseSample, s => IsAwaitingPick && s is { HasVideo: true });
            RerollSamplesCommand = new RelayCommand(() => ResolvePick(0), () => IsAwaitingPick);
            PreviewSampleCommand = new RelayCommand<SeedHuntSample>(PreviewSample);

            AddLog("H3 Eros initialized — every clip is a seed hunt: three drafts of the same prompt, " +
                   "you pick one, and only that one is upscaled and joined into the story");
        }

        // ── Identity ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The EROS-Hybrid seed-hunter graph, flattened to API format by
        /// <c>tools/convert_h3_eros.py</c>.</summary>
        protected override string WorkflowFileName => "workflow/video/h3-minimax/h3-eros.json";

        protected override string OutputSubfolder => "h3_eros";

        protected override string OutputFileStem => "H3Eros";

        protected override string OutputFolderName => "H3Eros";

        protected override string TabDisplayName => "H3 Eros";

        protected override string ChainLibraryFolder => "h3eros";

        protected override string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h3eros_queue.json");

        /// <summary>
        /// The dropdown names the <b>finished</b> canvas — the megapixel target handed to the 3D latent
        /// upscaler — not the draft the previews are sampled at. Sizes are what the upscaler's
        /// <c>keep_proportion</c> + 32px alignment really produces at 16:9.
        /// </summary>
        public override IReadOnlyList<MegapixelOption> MegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.5, "0.5 MP — fast finish (960×544)"),
            new MegapixelOption(0.8, "0.8 MP — balanced (1216×672)"),
            new MegapixelOption(1.0, "1.0 MP — full quality (1376×768)"),
            new MegapixelOption(1.5, "1.5 MP — high (1664×928)"),
            new MegapixelOption(2.0, "2.0 MP — 2K (1920×1088)"),
        };

        // ── The tab's own dials ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The megapixels the three seed previews are drafted at. Cheap on purpose — a hunt is three
        /// samples, so its cost is three times whatever this says, and its only job is to let you choose
        /// a composition. The finished clip is rendered at <c>Megapixels</c>.
        /// </summary>
        public double PreviewMegapixels
        {
            get => _previewMegapixels;
            set
            {
                var clamped = Math.Clamp(value, 0.1, 1.0);
                if (Math.Abs(_previewMegapixels - clamped) < 0.0001) return;
                _previewMegapixels = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HuntSummary));
            }
        }

        public IReadOnlyList<MegapixelOption> PreviewMegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.15, "0.15 MP — quickest (512×288)"),
            new MegapixelOption(0.2, "0.2 MP — default (608×352)"),
            new MegapixelOption(0.3, "0.3 MP — clearer (736×416)"),
            new MegapixelOption(0.4, "0.4 MP — closest to the finish (864×480)"),
        };

        /// <summary>Fixed sigmas on the upscale pass: 3, 4 or 5. Four is what the graph ships live.</summary>
        public int UpscaleSteps
        {
            get => _upscaleSteps;
            set
            {
                var clamped = SigmaSchedules.ContainsKey(value) ? value : 4;
                if (_upscaleSteps == clamped) return;
                _upscaleSteps = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HuntSummary));
            }
        }

        public IReadOnlyList<int> UpscaleStepOptions { get; } = new[] { 3, 4, 5 };

        /// <summary>RIFE on the finished frames, 24 → 48 fps. On is how the authored graph runs.</summary>
        public bool UseRife
        {
            get => _useRife;
            set { if (_useRife == value) return; _useRife = value; OnPropertyChanged(); OnPropertyChanged(nameof(HuntSummary)); }
        }

        /// <summary>
        /// Answers the pick for you, so a long chain renders unattended. Off by default — choosing is
        /// what the tab is for — but a twelve-clip story that has to be watched clip by clip is not a
        /// queue, and this is the switch that makes it one again.
        /// </summary>
        public bool AutoPickSample
        {
            get => _autoPickSample;
            set
            {
                if (_autoPickSample == value) return;
                _autoPickSample = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HuntSummary));
                // A render already parked on the gate is answered right away rather than at the next clip.
                if (value && IsAwaitingPick) ResolvePick(AutoPickSlot);
            }
        }

        /// <summary>Which sample <see cref="AutoPickSample"/> takes, 1-3.</summary>
        public int AutoPickSlot
        {
            get => _autoPickSlot;
            set
            {
                var clamped = Math.Clamp(value, 1, SampleCount);
                if (_autoPickSlot == clamped) return;
                _autoPickSlot = clamped;
                OnPropertyChanged();
            }
        }

        public IReadOnlyList<int> AutoPickSlotOptions { get; } = new[] { 1, 2, 3 };

        /// <summary>What the two phases will cost and produce, in one line under the controls.</summary>
        public string HuntSummary
        {
            get
            {
                var (dw, dh) = H3Canvas.Resolve(ResolvedAspectRatio, PreviewMegapixels, 32);
                var (fw, fh) = H3Canvas.Resolve(ResolvedAspectRatio, Megapixels, 32);
                var fps = UseRife ? $"RIFE {DraftFrameRate}→{DraftFrameRate * 2} fps" : $"{DraftFrameRate} fps";
                var pick = AutoPickSample
                    ? $"sample {AutoPickSlot} taken automatically"
                    : "you pick one";
                return $"Hunt: {SampleCount} drafts at ≈{dw}×{dh} ({PreviewMegapixels:0.##} MP), {pick}. " +
                       $"Finish: that latent upscaled to ≈{fw}×{fh} ({Megapixels:0.0} MP), " +
                       $"{UpscaleSteps} fixed sigmas, {fps}.";
            }
        }

        /// <summary>
        /// What the two phases produce, in the words of this graph rather than the Duo tab's. The base
        /// class describes a draft that is doubled by a fixed factor; here the finished canvas is the
        /// upscaler's own target and the draft is whatever the hunt was told to cost.
        /// </summary>
        public override string UpscaleSummary => HuntSummary;

        /// <summary>
        /// The peak frame stack, reported at the size the <i>finish</i> holds — the hunt's three drafts
        /// are sampled one after another and are a fraction of it, so the finished canvas is the number
        /// that decides whether the server survives the clip.
        /// </summary>
        public override string LoadSummary
        {
            get
            {
                var frames = FramesForSeconds(ClampLength(LengthSeconds));
                var (dw, dh) = H3Canvas.Resolve(ResolvedAspectRatio, PreviewMegapixels, 32);
                var (fw, fh) = H3Canvas.Resolve(ResolvedAspectRatio, Megapixels, 32);
                var draftGb = FrameStackGb(frames, dw, dh);
                var finishGb = FrameStackGb(frames, fw, fh);
                var rife = UseRife ? FrameStackGb(frames * 2, fw, fh) : finishGb;

                var text = $"{frames} frames: ≈{draftGb:0.#} GB per draft at {dw}×{dh}, " +
                           $"≈{finishGb:0.#} GB at the finished {fw}×{fh}" +
                           (UseRife ? $", ≈{rife:0.#} GB after RIFE doubles them." : ".");
                return rife >= HeavyFrameStackGb
                    ? text + " ⚠ That is the size that takes ComfyUI down mid-render — shorten the clip, " +
                             "drop the finished quality, or turn RIFE off."
                    : text;
            }
        }

        public override bool HasLoadWarning
        {
            get
            {
                var frames = FramesForSeconds(ClampLength(LengthSeconds));
                var (fw, fh) = H3Canvas.Resolve(ResolvedAspectRatio, Megapixels, 32);
                return FrameStackGb(UseRife ? frames * 2 : frames, fw, fh) >= HeavyFrameStackGb;
            }
        }

        // ── The sample strip ────────────────────────────────────────────────────────────────────────

        public ObservableCollection<SeedHuntSample> Samples => _samples;

        public RelayCommand<SeedHuntSample> UseSampleCommand { get; }
        public RelayCommand RerollSamplesCommand { get; }
        public RelayCommand<SeedHuntSample> PreviewSampleCommand { get; }

        /// <summary>True while a render is parked between the hunt and the finish, waiting to be told
        /// which sample to upscale. The strip's buttons are live only then.</summary>
        public bool IsAwaitingPick
        {
            get => _isAwaitingPick;
            private set
            {
                if (_isAwaitingPick == value) return;
                _isAwaitingPick = value;
                OnPropertyChanged();
                OnCanExecuteChanged();
            }
        }

        public bool HasSamples => _samples.Any(s => s.HasVideo);

        /// <summary>The one-line story of the hunt: which clip, which round, what to do next.</summary>
        public string HuntStatus
        {
            get => _huntStatus;
            private set { if (_huntStatus == value) return; _huntStatus = value; OnPropertyChanged(); }
        }

        /// <summary>The clip the shared player is showing — a sample while picking, the finished clip after.</summary>
        public string? ActivePreviewUri
        {
            get => _activePreviewUri;
            set
            {
                if (_activePreviewUri == value) return;
                _activePreviewUri = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasActivePreview));
            }
        }

        /// <summary>Whether the shared player has anything loaded. Separate from <c>HasResult</c>: a
        /// sample is playable long before this clip has a finished file.</summary>
        public bool HasActivePreview => !string.IsNullOrEmpty(ActivePreviewUri);

        private void PreviewSample(SeedHuntSample? sample)
        {
            if (sample?.VideoPath == null) return;
            ActivePreviewUri = sample.VideoPath;
        }

        private void UseSample(SeedHuntSample? sample)
        {
            if (sample == null) return;
            foreach (var s in _samples) s.IsSelected = s.Slot == sample.Slot;
            ResolvePick(sample.Slot);
        }

        /// <summary>Answers the gate. Slot 1-3 finishes that sample; 0 re-runs the hunt on fresh seeds.</summary>
        private void ResolvePick(int slot)
        {
            var pick = _pick;
            if (pick == null) return;
            pick.TrySetResult(slot);
        }

        // ── The render ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Renders one queued clip as a hunt, a pick and a finish. The queue loop awaits this, so the
        /// pause for the pick pauses the whole chain — which is the point: clip 2 is written to follow
        /// whichever take of clip 1 was chosen.
        /// </summary>
        protected override async Task GenerateItemAsync(H3CastQueueItem item, CancellationToken token)
        {
            IsProcessing = true;
            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = $"Preparing {TabDisplayName} workflow...";

            try
            {
                var clipLabel = item.IsStoryClip ? $", clip {item.ClipIndex}/{item.ClipCount}" : string.Empty;
                AddLog($"=== {TabDisplayName} · EROS-Hybrid seed hunt ({(item.HasCharacter2 ? "2 sheets" : "1 sheet")}{clipLabel}) ===");

                // Uploading the panels claims no GPU, so it happens before the lease rather than inside it.
                var uploaded = await UploadPanelsAsync(item);
                var prompt = CastPromptStamp.Detag(item.Prompt,
                    ResolvePanels(item.Character1PanelPaths, item.Character1SheetPath, 1).Count,
                    CastPromptStamp.IncludesCharacter2(item.Prompt, item.HasCharacter2)
                        ? ResolvePanels(item.Character2PanelPaths, item.Character2SheetPath, 2).Count
                        : 0);

                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var clipTag = item.IsStoryClip ? $"_c{item.ClipIndex:00}" : string.Empty;
                var runToken = $"h3eros_{ts}{clipTag}";
                var len = ClampLength(item.LengthSeconds);
                var (dw, dh) = H3Canvas.Resolve(item.AspectRatio, item.PreviewMegapixels, 32);
                var (fw, fh) = H3Canvas.Resolve(item.AspectRatio, item.Megapixels, 32);

                AddLog($"References wired: {uploaded.Count} panel image(s) as <Picture 1>–<Picture {uploaded.Count}>.");
                AddLog($"Hunt canvas ≈{dw}×{dh} ({item.PreviewMegapixels:0.##} MP), " +
                       $"finished canvas ≈{fw}×{fh} ({item.Megapixels:0.0} MP), " +
                       $"{len:0.#}s / {FramesForSeconds(len)} frames @ {DraftFrameRate}fps.");

                // ── Phase 1 + 2: hunt, then pick. Re-roll loops both. ────────────────────────────
                // A pick is only carried over from an earlier attempt when the seed it was made against
                // came with it: the three previews are that seed and its two successors, so finishing a
                // recorded slot on a freshly rolled seed would deliver a take nobody ever saw.
                var chosen = item.ChosenSampleSlot > 0 && item.HuntBaseSeed >= 0 ? item.ChosenSampleSlot : 0;
                _huntSeed = chosen > 0
                    ? item.HuntBaseSeed
                    : item.Seed >= 0 ? item.Seed : System.Random.Shared.NextInt64(0, long.MaxValue);
                _huntRound = 0;
                if (chosen > 0)
                    AddLog($"Sample {chosen} was already picked for this clip (hunt seed {_huntSeed}) — " +
                           "finishing it without hunting again.");

                while (chosen == 0)
                {
                    token.ThrowIfCancellationRequested();
                    item.HuntBaseSeed = _huntSeed;
                    await RunHuntAsync(item, uploaded, prompt, len, runToken, token);
                    chosen = await AwaitPickAsync(item, token);
                    if (chosen == 0)
                    {
                        _huntRound++;
                        _huntSeed = System.Random.Shared.NextInt64(0, long.MaxValue);
                        AddLog($"Re-rolling the hunt on a fresh base seed ({_huntSeed}).");
                    }
                }

                item.ChosenSampleSlot = chosen;
                item.HuntBaseSeed = _huntSeed;
                SaveQueueToFile();
                AddLog($"Sample {chosen} picked — finishing it at {fw}×{fh}.");

                // ── Phase 3: finish ─────────────────────────────────────────────────────────────
                await RunFinishAsync(item, uploaded, prompt, len, runToken, chosen, ts, fw, fh, token);
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
                IsAwaitingPick = false;
                _pick = null;
                IsProcessing = false;
                OnCanExecuteChanged();
            }
        }

        /// <summary>Uploads this clip's panels — the same policy as H3 Cast: one reference per view,
        /// never the assembled sheet, and character 2's only when the clip names them.</summary>
        private async Task<IReadOnlyList<string>> UploadPanelsAsync(H3CastQueueItem item)
        {
            ProcessingStatus = "Uploading character references...";
            ProcessingProgress = 3;

            var panels1 = ResolvePanels(item.Character1PanelPaths, item.Character1SheetPath, 1);
            var includesCharacter2 = CastPromptStamp.IncludesCharacter2(item.Prompt, item.HasCharacter2);
            var panels2 = includesCharacter2
                ? ResolvePanels(item.Character2PanelPaths, item.Character2SheetPath, 2)
                : (IReadOnlyList<string>)Array.Empty<string>();
            if (item.HasCharacter2 && !includesCharacter2)
                AddLog("Character 2 is not named in this clip — their references are left out of it.");

            var uploaded = new List<string>();
            foreach (var panel in panels1.Concat(panels2))
                uploaded.Add(await EnsureUploadedAsync(panel));
            if (uploaded.Count == 0)
                throw new Exception("No reference images to wire — the cast has no panels.");
            if (uploaded.Count > MaxReferenceImages)
                throw new Exception($"{uploaded.Count} reference images, but MiniMaxH3ReferenceToVideo " +
                                    $"takes at most {MaxReferenceImages}. Split the sheets into fewer panels.");
            return uploaded;
        }

        // ── Phase 1 — the hunt ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One submission, three sample clips. The graph is pruned to its three preview sinks, so nothing
        /// downstream of the picker — the upscaler, the 2nd pass, RIFE, the final mux — is even in the
        /// prompt, and the three samplers are the only thing the GPU spends time on.
        /// </summary>
        private async Task RunHuntAsync(
            H3CastQueueItem item, IReadOnlyList<string> uploaded, string prompt,
            double lengthSeconds, string runToken, CancellationToken token)
        {
            ResetSamples();
            var round = _huntRound;
            var huntToken = $"{runToken}_h{round:00}";

            var json = await LoadFileAsync(WorkflowFileName, token);
            var root = ParseGraph(json);
            ApplyCommonInputs(root, item, uploaded, prompt, lengthSeconds);

            for (var i = 0; i < SampleCount; i++)
            {
                var (_, sink, noise) = SampleBranches[i];
                SetInput(root, noise, "noise_seed", SeedForSlot(i + 1));
                SetInput(root, sink, "frame_rate", DraftFrameRate);
                SetInput(root, sink, "save_output", true);
                SetInput(root, sink, "filename_prefix", $"{OutputSubfolder}/{huntToken}_p{i + 1}");
            }

            json = PruneToOutputs(root.ToJsonString(), SampleBranches.Select(b => b.Sink), out var pruned);
            AddLog($"Hunt graph: pruned to the three preview sinks, {pruned} node(s) removed.");

            var lease = await AcquireLeaseAsync("H3Eros hunt", token);
            string promptId;
            try
            {
                ProcessingProgress = 5;
                ProcessingStatus = $"Hunting {SampleCount} seeds...";
                HuntStatus = item.IsStoryClip
                    ? $"Clip {item.ClipIndex}/{item.ClipCount} · hunting {SampleCount} seeds…"
                    : $"Hunting {SampleCount} seeds…";
                AddLog($"Hunt round {round + 1}: seeds {string.Join(", ", Enumerable.Range(1, SampleCount).Select(SeedForSlot))}.");

                promptId = await SubmitAsync(json, 5, 55, token);
            }
            finally
            {
                lease.Dispose();
            }

            ProcessingStatus = "Collecting the samples...";
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);

            var found = 0;
            for (var i = 0; i < SampleCount; i++)
            {
                token.ThrowIfCancellationRequested();
                var slot = i + 1;
                var (_, sink, _) = SampleBranches[i];

                string? local = null;
                if (byNode.TryGetValue(sink, out var outs) && outs.Count > 0)
                {
                    var pick = outs.FirstOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? outs[0];
                    local = await ResolveOutputToLocalAsync(pick);
                }
                local ??= FindTokenVideoOnDisk($"{huntToken}_p{slot}");

                if (local != null && File.Exists(local))
                {
                    SetSampleVideo(slot, local);
                    found++;
                    if (found == 1) ActivePreviewUri = local;
                }
                else
                {
                    SetSampleStatus(slot, "no output");
                    AddLog($"  Sample {slot}: no output produced.");
                }
            }

            if (found == 0)
                throw new Exception("The hunt produced no sample previews.");

            ProcessingProgress = 55;
            AddLog($"Hunt complete: {found}/{SampleCount} samples ready.");
        }

        /// <summary>The noise seed of one preview slot: the round's base seed, offset by the slot, exactly
        /// as the authored graph's <c>a</c>, <c>a+1</c>, <c>a+2</c> calculators did.</summary>
        private long SeedForSlot(int slot) => _huntSeed + (slot - 1);

        // ── Phase 2 — the pick ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Parks the render until the strip is answered. The GPU lease is <b>not</b> held here — it was
        /// released the moment the hunt's submission returned — so the rest of the app, and the other
        /// tabs, keep working while three videos are being watched.
        /// </summary>
        private async Task<int> AwaitPickAsync(H3CastQueueItem item, CancellationToken token)
        {
            if (AutoPickSample)
            {
                var slot = Math.Clamp(AutoPickSlot, 1, SampleCount);
                // Auto-pick must not choose a sample that never rendered.
                if (!_samples.First(s => s.Slot == slot).HasVideo)
                    slot = _samples.First(s => s.HasVideo).Slot;
                AddLog($"Auto-pick is on — taking sample {slot} without waiting.");
                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var s in _samples) s.IsSelected = s.Slot == slot;
                });
                return slot;
            }

            _pick = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            IsAwaitingPick = true;
            ProcessingStatus = "Waiting for you to pick a sample…";
            HuntStatus = item.IsStoryClip
                ? $"Clip {item.ClipIndex}/{item.ClipCount} · pick a sample to finish, or 🎲 re-roll"
                : "Pick a sample to finish, or 🎲 re-roll";
            AddLog("Waiting for a sample to be picked — ✓ Use this one finishes it, 🎲 Re-roll hunts again.");

            try
            {
                using (token.Register(() => _pick?.TrySetCanceled(token)))
                    return await _pick.Task;
            }
            finally
            {
                IsAwaitingPick = false;
                _pick = null;
            }
        }

        // ── Phase 3 — the finish ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The same file with the same first-pass inputs, so the picked sampler's latent comes straight
        /// out of ComfyUI's execution cache instead of being sampled again — which is what makes the
        /// finished clip the sample that was chosen rather than a new take of the same seed. Only the
        /// picked branch is left in the graph; the other two are pruned with everything else the final
        /// sink cannot reach.
        /// </summary>
        private async Task RunFinishAsync(
            H3CastQueueItem item, IReadOnlyList<string> uploaded, string prompt, double lengthSeconds,
            string runToken, int chosen, string ts, int canvasW, int canvasH, CancellationToken token)
        {
            var json = await LoadFileAsync(WorkflowFileName, token);
            var root = ParseGraph(json);
            ApplyCommonInputs(root, item, uploaded, prompt, lengthSeconds);

            // Byte-identical to the hunt: every one of the three seeds is written back, because a
            // different value on the *picked* branch is a cache miss and a different video.
            for (var i = 0; i < SampleCount; i++)
                SetInput(root, SampleBranches[i].Noise, "noise_seed", SeedForSlot(i + 1));

            var sampler = SampleBranches[chosen - 1].Sampler;
            Link(root, NodeLatentSplit, "av_latent", sampler, DenoisedSlot);
            // The graph's single-pass decode of the same latent — the author's "skip upscale" option.
            // Nothing consumes it here, so the prune below removes it; it is repointed anyway so the
            // graph never carries a link into a sampler that is about to be deleted.
            Link(root, NodeSinglePassVideo, "samples", sampler, DenoisedSlot);
            Link(root, NodeSinglePassAudio, "samples", sampler, DenoisedSlot);

            // The upscale pass. Its own noise seed is fresh every finish — re-rolling it is how the
            // authored graph offers variations of the same picked composition.
            SetInput(root, NodeUpscaler, "mode", "megapixels");
            SetInput(root, NodeUpscaler, "mode.megapixels", item.Megapixels);
            SetInput(root, NodeUpscaleNoise, "noise_seed", System.Random.Shared.NextInt64(0, long.MaxValue));
            Link(root, NodeUpscaleSampler, "sigmas",
                 SigmaSchedules.TryGetValue(item.UpscaleSteps, out var sigmas) ? sigmas : SigmaSchedules[4], 0);

            // Which decode feeds the mux, and whether RIFE stands between them.
            var video = NodeUpscaledVideo;
            var audio = NodeUpscaledAudio;
            if (item.UseRife)
            {
                SetInput(root, NodeRife, "source_fps", (double)DraftFrameRate);
                SetInput(root, NodeRife, "target_fps", (double)(DraftFrameRate * 2));
                Link(root, NodeRife, "images", video, 0);
                Link(root, NodeFinalSave, "images", NodeRife, 0);
                SetInput(root, NodeFinalSave, "frame_rate", DraftFrameRate * 2);
            }
            else
            {
                Link(root, NodeFinalSave, "images", video, 0);
                SetInput(root, NodeFinalSave, "frame_rate", DraftFrameRate);
            }
            Link(root, NodeFinalSave, "audio", audio, 0);
            SetInput(root, NodeFinalSave, "save_output", true);
            SetInput(root, NodeFinalSave, "filename_prefix", $"{OutputSubfolder}/{runToken}_final");

            json = PruneToOutputs(root.ToJsonString(), new[] { NodeFinalSave }, out var pruned);
            AddLog($"Finish graph: the picked branch kept, {pruned} node(s) removed — the other two " +
                   "samplers, their decodes and their sinks among them.");

            var lease = await AcquireLeaseAsync("H3Eros finish", token);
            try
            {
                ProcessingProgress = 60;
                ProcessingStatus = $"Upscaling sample {chosen} to {canvasW}×{canvasH}...";
                HuntStatus = item.IsStoryClip
                    ? $"Clip {item.ClipIndex}/{item.ClipCount} · finishing sample {chosen}…"
                    : $"Finishing sample {chosen}…";
                AddLog($"Finishing (sample {chosen}, {item.UpscaleSteps} fixed sigmas at {canvasW}×{canvasH}, " +
                       $"{(item.UseRife ? $"RIFE → {DraftFrameRate * 2}fps" : $"{DraftFrameRate}fps")}). " +
                       "The first pass comes out of ComfyUI's cache, so only the upscale is sampled.");

                var local = await SubmitAndRetrieveAsync(json, $"{runToken}_final", NodeFinalSave, 60, 95, token);
                if (local == null || !File.Exists(local))
                    throw new Exception("No output video was generated.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), OutputFolderName);
                Directory.CreateDirectory(outputDir);
                var finalName = item.IsStoryClip
                    ? $"{OutputFileStem}_{(string.IsNullOrEmpty(item.StoryId) ? ts : item.StoryId)}_clip{item.ClipIndex:00}.mp4"
                    : $"{OutputFileStem}_{ts}.mp4";
                var finalPath = Path.Combine(outputDir, finalName);
                File.Copy(local, finalPath, true);
                await LocalCopyService.CopyVideoAsync(finalPath);

                var fi = new FileInfo(finalPath);
                item.OutputVideoPath = finalPath;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResultVideoPath = finalPath;
                    ActivePreviewUri = finalPath;
                    ResultVideoInfo = $"{TabDisplayName} • " +
                                      $"{(item.IsStoryClip ? $"clip {item.ClipIndex}/{item.ClipCount} • " : string.Empty)}" +
                                      $"sample {chosen} • ≈{canvasW}×{canvasH} • {item.AspectRatio} • " +
                                      $"{lengthSeconds:0.#}s • {fi.Length / 1024 / 1024.0:F1}MB";
                    HasResult = true;
                    OnCanExecuteChanged();
                });
                ProcessingProgress = 100;
                ProcessingStatus = "Complete!";
                HuntStatus = string.Empty;
                AddLog($"=== Complete: {finalPath} ===");
            }
            finally
            {
                lease.Dispose();
            }
        }

        // ── Graph patching ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Everything both phases must agree on, written identically into both submissions. The prompt,
        /// the references, the draft canvas, the length and the first-pass step count all feed the three
        /// samplers, so a single difference between hunt and finish is a cache miss — and a finished clip
        /// that is not the sample that was picked.
        /// </summary>
        private void ApplyCommonInputs(
            JsonObject root, H3CastQueueItem item, IReadOnlyList<string> uploaded,
            string prompt, double lengthSeconds)
        {
            RequireClass(root, NodeRef2V, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodeFinalSave, "VHS_VideoCombine");

            // ── References ────────────────────────────────────────────────────
            // The graph ships none at all — the author's were LoadImageCrop nodes, a custom node this
            // server does not have — so every one is injected beside the reference node.
            var loaders = new List<string>();
            for (var i = 0; i < uploaded.Count; i++)
            {
                var id = $"eros_ref_{i}";
                root[id] = new JsonObject
                {
                    ["inputs"] = new JsonObject { ["image"] = uploaded[i] },
                    ["class_type"] = "LoadImage",
                    ["_meta"] = new JsonObject { ["title"] = $"Picture {i + 1}" }
                };
                loaders.Add(id);
            }
            AttachReferences(root, NodeRef2V, loaders);
            SetInput(root, NodeRef2V, "ref_image_size", item.MaxFidelityReferences ? "max" : "match");

            // ── Prompt, length, first-pass steps ──────────────────────────────
            SetInput(root, NodePrompt, "value", prompt);
            SetInput(root, NodeSeconds, "value", lengthSeconds);
            SetInput(root, NodeSteps, "value", FirstPassSteps);

            // ── The draft canvas ──────────────────────────────────────────────
            // ResolutionSelector sizes the previews only; the finished size is the upscaler's target and
            // is set in the finish phase. Aspects the node's combo does not accept are resolved here and
            // fed in as two PrimitiveInt nodes standing where its width and height outputs were.
            if (H3Canvas.RequiresLiteralCanvas(item.AspectRatio))
            {
                var (cw, ch) = H3Canvas.Resolve(item.AspectRatio, item.PreviewMegapixels, 32);
                root[NodeCanvasWidth] = IntNode(cw, "Draft width");
                root[NodeCanvasHeight] = IntNode(ch, "Draft height");
                Retarget(root, NodeResolution, 0, NodeCanvasWidth);
                Retarget(root, NodeResolution, 1, NodeCanvasHeight);
            }
            else
            {
                SetInput(root, NodeResolution, "aspect_ratio", item.AspectRatio);
                SetInput(root, NodeResolution, "megapixels", item.PreviewMegapixels);
                SetInput(root, NodeResolution, "multiple", 32);
            }
        }

        /// <summary>
        /// Steps on the first pass — the one that produces the three samples. The graph's own
        /// BasicScheduler count, kept as a constant rather than a dial: the hunt is a comparison between
        /// three seeds, and it is only honest if every sample is sampled the same way as every other
        /// hunt's.
        /// </summary>
        private const int FirstPassSteps = 12;

        private static JsonObject ParseGraph(string json) =>
            JsonNode.Parse(json)?.AsObject()
            ?? throw new Exception("Workflow JSON could not be parsed.");

        private async Task<WorkflowQueueCoordinator.WorkflowLease> AcquireLeaseAsync(
            string label, CancellationToken token)
        {
            ProcessingStatus = "Waiting for other workflows to finish...";
            AddLog($"{label}: waiting for other workflows to finish...");
            var lease = await _workflowCoordinator.AcquireAsync("H3Eros", token);
            try
            {
                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        private static void SetInput(JsonObject root, string nodeId, string input, object value)
        {
            if (root[nodeId]?["inputs"] is not JsonObject inputs)
                throw new Exception($"Workflow node '{nodeId}' is missing — the workflow file no longer matches this tab.");

            inputs[input] = value switch
            {
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(value.ToString())
            };
        }

        /// <summary>Points one node's input at another node's output.</summary>
        private static void Link(JsonObject root, string nodeId, string input, string sourceId, int slot)
        {
            if (root[nodeId]?["inputs"] is not JsonObject inputs)
                throw new Exception($"Workflow node '{nodeId}' is missing — the workflow file no longer matches this tab.");
            if (root[sourceId] == null)
                throw new Exception($"Workflow node '{sourceId}' is missing — the workflow file no longer matches this tab.");

            inputs[input] = new JsonArray(sourceId, slot);
        }

        private static JsonObject IntNode(int value, string title) => new()
        {
            ["inputs"] = new JsonObject { ["value"] = value },
            ["class_type"] = "PrimitiveInt",
            ["_meta"] = new JsonObject { ["title"] = title }
        };

        /// <summary>
        /// Repoints every link that reads <paramref name="slot"/> of <paramref name="sourceId"/> at slot 0
        /// of <paramref name="newId"/> instead.
        /// </summary>
        private static void Retarget(JsonObject root, string sourceId, int slot, string newId)
        {
            foreach (var node in root)
            {
                if (node.Value?["inputs"] is not JsonObject inputs) continue;

                foreach (var input in inputs.ToList())
                {
                    if (input.Value is not JsonArray link || link.Count != 2) continue;
                    if (link[0]?.ToString() != sourceId) continue;
                    if (link[1] is not JsonValue index || !index.TryGetValue<int>(out var i) || i != slot)
                        continue;

                    inputs[input.Key] = new JsonArray(newId, 0);
                }
            }
        }

        // ── Sample strip plumbing ───────────────────────────────────────────────────────────────────

        private void ResetSamples() => Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var s in _samples) s.Reset();
            ActivePreviewUri = null;
            OnPropertyChanged(nameof(HasSamples));
        });

        private void SetSampleVideo(int slot, string localPath)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var sample = _samples.First(s => s.Slot == slot);
                sample.VideoPath = localPath;
                sample.VideoFileUri = localPath;
                sample.Status = $"seed {SeedForSlot(slot)}";
                OnPropertyChanged(nameof(HasSamples));
                AddLog($"  Sample {slot} ready: {Path.GetFileName(localPath)}");
                OnCanExecuteChanged();
            });

            var thumb = ExtractFirstFrame(localPath);
            if (thumb != null)
                Application.Current.Dispatcher.Invoke(() =>
                    _samples.First(s => s.Slot == slot).ThumbnailImage = thumb);
        }

        private void SetSampleStatus(int slot, string status) =>
            Application.Current.Dispatcher.Invoke(() => _samples.First(s => s.Slot == slot).Status = status);

        /// <summary>
        /// The tile image. Three simultaneous WPF <c>MediaElement</c>s render as solid black — the reason
        /// every seed-hunt tab in this app shows still frames in the grid and plays the picked one in a
        /// single shared player.
        /// </summary>
        private BitmapImage? ExtractFirstFrame(string videoPath)
        {
            try
            {
                var ffmpeg = FindFFmpeg();
                if (ffmpeg == null) return null;
                var outPath = Path.Combine(Path.GetTempPath(), $"h3eros_thumb_{Guid.NewGuid():N}.png");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -i \"{videoPath}\" -frames:v 1 -q:v 3 \"{outPath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return null;
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                }
                if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0) return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(outPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                AddLog($"Thumbnail extract failed: {ex.Message}");
                return null;
            }
        }

        // ── Queue ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The stock queue-add, with this tab's own settings frozen onto every item and any previous
        /// pick cleared — a re-queued chain hunts again rather than silently finishing the samples of
        /// whatever run put those numbers there.
        /// </summary>
        protected override void AddToQueue()
        {
            var before = Queue.Count;
            base.AddToQueue();
            for (var i = before; i < Queue.Count; i++)
            {
                Queue[i].PreviewMegapixels = PreviewMegapixels;
                Queue[i].UpscaleSteps = UpscaleSteps;
                Queue[i].UseRife = UseRife;
                Queue[i].ChosenSampleSlot = 0;
                Queue[i].HuntBaseSeed = -1;
            }
            if (Queue.Count > before) SaveQueueToFile();
        }

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(HasSamples));
            OnPropertyChanged(nameof(HuntSummary));
            OnPropertyChanged(nameof(UpscaleSummary));
            OnPropertyChanged(nameof(LoadSummary));
            OnPropertyChanged(nameof(HasLoadWarning));
            UseSampleCommand.NotifyCanExecuteChanged();
            RerollSamplesCommand.NotifyCanExecuteChanged();
            PreviewSampleCommand.NotifyCanExecuteChanged();
        }
    }
}
