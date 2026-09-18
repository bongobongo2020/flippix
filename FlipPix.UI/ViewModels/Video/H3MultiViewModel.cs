using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "H3 Multi" tab — the 🎬🎭 H3 Ensemble machinery on the <b>MiniMax I2V turbo</b> render pipeline.
    ///
    /// <para>Everything the Ensemble tab does is inherited unchanged: the five cast slots (photo browsed or
    /// ✨ generated with a picked LoRA), the wardrobe derived once and locked, the location photograph the
    /// language model reads <i>and</i> the generator is shown, the <c>h3-ensemble.md</c> Analyze that writes
    /// one prompt per clip naming only the characters actually in it, the storyboard pass that renders each
    /// clip's opening frame before the clips are committed, and the queue that renders the chain in order
    /// and FFmpeg-joins it when the last clip lands. See <see cref="H3EnsembleViewModel"/> for all of it.</para>
    ///
    /// <para>The only thing this class replaces is the render itself. Where the Ensemble tab submits
    /// <c>h3-cast-hybrid.json</c>, this tab submits <c>h3-multi.json</c>: the graph the 🪪🌀 H3 Duo tab runs,
    /// which renders each clip as <b>four draft steps at a quarter of the canvas, a 2× pass through the
    /// MiniMax H3 3D latent upscaler, then three fixed-sigma steps at the finished size</b> — the
    /// lightx2v-turbo recipe that is both quicker and sharper than denoising the whole clip at full size.
    /// It is the same relationship to the Ensemble tab that H3 Duo has to H3 Cast, widened from a two-hander
    /// to the full ensemble: keyframes, up to five characters' panels and the location ride in the reference
    /// node's nine slots in exactly the order the prompt numbered them.</para>
    ///
    /// <para>Three consequences of the graph swap are worth knowing before pressing Generate:</para>
    /// <list type="bullet">
    /// <item><b>There is no face-refine pass.</b> The I2V graph has no H3-FaceRefine branch, and what that
    /// pass bought is largely replaced by keeping the references themselves at full fidelity — see
    /// <see cref="MaxFidelityReferences"/>, on by default on this tab. On the Ensemble tab a five-hander
    /// meant five refine passes; here the likeness work is done once, in the encoding.</item>
    /// <item><b>References are not held at the finished canvas by construction.</b> The hybrid graph encodes
    /// its panels at the finished canvas regardless of the draft; the I2V graph's <c>ref_image_size:
    /// match</c> scales every reference to the <i>draft</i> canvas — a quarter of the chosen megapixels —
    /// which is not enough face for identity to survive. <see cref="MaxFidelityReferences"/> switches the
    /// node to <c>max</c> (a 2048px short edge) instead, at the cost of reference tokens riding through
    /// every sampling step.</item>
    /// <item><b>No FILM interpolation.</b> The I2V graph muxes at the render rate; a 48 fps file is a job
    /// for ✨ Enhance Video afterwards. The switch is forced off rather than merely hidden so a queue item
    /// restored from an Ensemble queue file cannot claim it.</item>
    /// </list>
    /// </summary>
    public class H3MultiViewModel : H3EnsembleViewModel
    {
        // ── Workflow node ids (locked to h3-minimax/h3-multi.json — a copy of h3-duo.json, itself a copy
        //    of h3-minimax-i2v.json; a copy the Duo tab cannot break by evolving its own) ───────────────
        private const string NodeReference0 = "10";        // LoadImage → ref_image_0 (the first picture)
        private const string NodeBaseRef2V = "4145:174";   // MiniMaxH3ReferenceToVideo (the clip's only pass)
        private const string NodeBasePrompt = "56";        // PrimitiveStringMultiline
        private const string NodeBaseSeconds = "4145:147"; // easy int → the frame-count expression
        private const string NodeBaseSeed = "4145:149";    // RandomNoise
        private const string NodeBaseFrames = "4145:138";  // VAEDecode — the finished frames, pre-RTX
        private const string NodeResolution = "60";        // ResolutionSelector — the *draft* canvas
        // Added to the graph, not present in the file: the two INT sources that stand in for
        // ResolutionSelector's outputs when the chosen aspect is one the node's combo does not accept.
        private const string NodeCanvasWidth = "multi_canvas_w";
        private const string NodeCanvasHeight = "multi_canvas_h";
        private const string NodeSlaBase = "sla_base";     // H3SLAAttention, last on the base MODEL wire
        private const string NodeSlaLoop = "sla_loop";     // the loop's copy — pruned with the loop itself
        private const string NodeSparseAttention = "55:3706"; // ComfySwitchNode — Sol-Attn on/off
        private const string NodeBaseDetail = "4145:4220"; // ComfySwitchNode — draft latent or finished
        private const string NodeBaseSampler = "4145:140"; // SamplerCustomAdvanced — the draft sampler
        private const string NodeDraftSigmas = "draft_split";  // SplitSigmas.high_sigmas, steps 1..4
        private const string NodeBaseFullSigmas = "4145:148"; // BasicScheduler, 8 steps, shifted
        private const string NodeBaseUpscaler = "4145:4318";  // MinimaxH3LatentUpscaler3D
        private const string NodeBaseRtx = "4145:139";     // ComfySwitchNode — RTX ×2 on the saved frames
        private const string NodeBaseAudio = "4145:143";   // ComfySwitchNode — audio enhancement
        private const string NodeSave = "49";              // VHS_VideoCombine — the base pass's sink
        // Ids for the storyboard pass's injected frame pickers and image sinks — string ids, well clear of
        // the graph's own (which look like "4145:174").
        private const string StillPickPrefix = "multi_still_pick_";
        private const string StillSavePrefix = "multi_still_save_";

        /// <summary>
        /// How much bigger the finished canvas is than the sampled draft, per side. Must stay an integer:
        /// the draft is the megapixel target divided by this squared, and the upscaler multiplies back by
        /// the same factor — only an integer keeps the two on the same number for every aspect ratio.
        /// </summary>
        private const double LatentUpscaleFactor = 2.0;

        /// <summary>The multiple ResolutionSelector rounds the <i>draft</i> canvas to. 32, not 64: the
        /// finish pass scales the draft by exactly <see cref="LatentUpscaleFactor"/>, so a 32-aligned draft
        /// doubles to a 64-aligned finish. Mirrors the MiniMax I2V tab's arithmetic exactly.</summary>
        private const int ResolutionMultiple = 32;

        /// <summary>RTX Video Super Resolution factor — mirrored so the tab can say what size the file
        /// will be before it renders.</summary>
        private const double RtxScale = 2.0;

        /// <summary>64 always: H3 packs audio at 80 rows per second, so a 128-row block forces 1.6s of
        /// audio through one attention pattern and speech comes back robotic. Every clip this tab renders
        /// has a soundtrack, so the wider block is never the right trade here.</summary>
        private const string SlaBlockSize = "64";

        public H3MultiViewModel(
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
            // The I2V graph scales references to the draft canvas under 'match' — a quarter of the area —
            // and this tab exists to hold faces without a refine pass, so full fidelity is the default.
            // It costs reference tokens on every step; the checkbox turns it off for speed.
            MaxFidelityReferences = true;

            // The I2V graph has neither branch; keeping the inherited flags false is what makes the queue
            // rows (and the queued items themselves) say so honestly.
            FaceRefine = false;
            Interpolate = false;

            AddLog("H3 Multi initialized — the H3 Ensemble flow on the MiniMax I2V turbo pipeline");
        }

        /// <summary>The MiniMax I2V turbo graph (a copy the Duo tab cannot break by evolving its own).</summary>
        protected override string WorkflowFileName => "workflow/video/h3-minimax/h3-multi.json";

        protected override string OutputSubfolder => "h3_multi";

        protected override string OutputFolderName => "H3Multi";

        /// <summary>Its own name in the logs and the message boxes — its runs read as its own, not as the
        /// Ensemble tab's.</summary>
        protected override string TabLogName => "H3 Multi";

        protected override string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h3multi_queue.json");

        /// <summary>
        /// The quality dropdown names the <b>finished</b> canvas. Wider than the Ensemble tab's range
        /// because only three steps of this pipeline ever see the full canvas — the same reason the MiniMax
        /// I2V tab offers 1.5 MP. Sizes are what this graph really produces at 16:9.
        /// </summary>
        public override IReadOnlyList<MegapixelOption> MegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.4, "0.4 MP — fast draft (832×512)"),
            new MegapixelOption(0.7, "0.7 MP — balanced (1152×640)"),
            new MegapixelOption(1.0, "1.0 MP — full quality (1344×768)"),
            new MegapixelOption(1.5, "1.5 MP — high (1664×960)"),
        };

        // ── The turbo pipeline's switches — this tab's replacements for the Ensemble tab's face-refine
        //    and interpolation settings, which the I2V graph does not have. ─────────────────────────────

        /// <summary>
        /// Whether the reference pictures are encoded at a 2048px short edge (<c>ref_image_size: max</c>)
        /// rather than scaled to the draft canvas (<c>match</c>). <b>On by default on this tab:</b> there
        /// is no face-refine pass here, so the likeness lives in the encoding or nowhere.
        /// </summary>
        public bool MaxFidelityReferences
        {
            get => _maxFidelityReferences;
            set
            {
                if (_maxFidelityReferences == value) return;
                _maxFidelityReferences = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
            }
        }
        private bool _maxFidelityReferences;

        /// <summary>
        /// The turbo sampling scheme: four draft steps at a quarter of the canvas, a 2× pass through the
        /// MiniMax H3 3D latent upscaler, then three fixed-sigma finish steps at the finished size. Off,
        /// one 8-step pass at the full canvas. On by default — this tab exists for this scheme.
        /// </summary>
        public bool UseLatentUpscale
        {
            get => _useLatentUpscale;
            set
            {
                if (_useLatentUpscale == value) return;
                _useLatentUpscale = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
            }
        }
        private bool _useLatentUpscale = true;

        /// <summary>SLA block-sparse attention on both samplers. On by default, at the lightx2v sparsity.</summary>
        public bool UseSla
        {
            get => _useSla;
            set
            {
                if (_useSla == value) return;
                _useSla = value;
                OnPropertyChanged();
            }
        }
        private bool _useSla = true;

        public IReadOnlyList<SlaSparsityOption> SlaSparsityOptions { get; } = new[]
        {
            new SlaSparsityOption(0.80, "0.80 - conservative"),
            new SlaSparsityOption(0.85, "0.85 - lightx2v default"),
            new SlaSparsityOption(0.90, "0.90 - validated, ~15% faster"),
            new SlaSparsityOption(0.95, "0.95 - maximum"),
        };

        public double SlaSparsity
        {
            get => _slaSparsity;
            set
            {
                if (Math.Abs(_slaSparsity - value) <= 0.0001) return;
                _slaSparsity = value;
                OnPropertyChanged();
            }
        }
        private double _slaSparsity = 0.85;

        /// <summary>Sol-Attn — the legacy sparse-attention switch. Off by default, as on the I2V tab.</summary>
        public bool UseSparseAttention
        {
            get => _useSparseAttention;
            set
            {
                if (_useSparseAttention == value) return;
                _useSparseAttention = value;
                OnPropertyChanged();
            }
        }
        private bool _useSparseAttention;

        /// <summary>The audio-enhancement pass over the saved clip — a switch the I2V graph carries and the
        /// hybrid graph does not. On by default.</summary>
        public bool UseAudioEnhancement
        {
            get => _useAudioEnhancement;
            set
            {
                if (_useAudioEnhancement == value) return;
                _useAudioEnhancement = value;
                OnPropertyChanged();
            }
        }
        private bool _useAudioEnhancement = true;

        /// <summary>What the saved file will be — the I2V canvas arithmetic, not the Ensemble tab's.</summary>
        public override string UpscaleSummary
        {
            get
            {
                var (cw, ch) = ResolveCanvas(ResolvedAspectRatio, Megapixels, UseLatentUpscale);
                var sampled = string.Empty;
                if (UseLatentUpscale)
                {
                    var (dw, dh) = SampledCanvas(ResolvedAspectRatio, Megapixels, UseLatentUpscale);
                    sampled = $" Sampled as a {dw}×{dh} draft, upscaled ×{LatentUpscaleFactor:0.#}.";
                }

                var refs = MaxFidelityReferences
                    ? " References at a 2048px short edge."
                    : UseLatentUpscale
                        ? $" References scaled to the draft canvas, {(cw / (int)LatentUpscaleFactor)}×{(ch / (int)LatentUpscaleFactor)} — a quarter of the area you picked; turn on max-fidelity references if faces are not holding."
                        : string.Empty;

                if (!RtxUpscale)
                    return $"Output: the H3 canvas as rendered, ≈{cw}×{ch}. No upscale pass.{sampled}{refs}";
                return $"Output: RTX ×{RtxScale:0.#} super-resolution → ≈{cw * RtxScale:0}×{ch * RtxScale:0}.{sampled}{refs}";
            }
        }

        public override string LoadSummary
        {
            get
            {
                var frames = FramesForSeconds(ClampLength(LengthSeconds));
                var (cw, ch) = ResolveCanvas(ResolvedAspectRatio, Megapixels, UseLatentUpscale);
                var baseGb = FrameStackGb(frames, cw, ch);

                if (!RtxUpscale)
                    return $"{frames} frames × {cw}×{ch} ≈ {baseGb:0.#} GB of frames held at once.";

                var upGb = FrameStackGb(frames, (int)(cw * RtxScale), (int)(ch * RtxScale));
                var text = $"{frames} frames: ≈{baseGb:0.#} GB at the H3 canvas, ≈{upGb:0.#} GB after RTX ×2, " +
                           "both live at the same time during the upscale.";
                return upGb >= HeavyFrameStackGb
                    ? text + " ⚠ That is the size that takes ComfyUI down mid-render — shorten the clip, " +
                             "drop the quality, or turn RTX off and upscale afterwards in ✨ Enhance Video."
                    : text;
            }
        }

        public override bool HasLoadWarning
        {
            get
            {
                if (!RtxUpscale) return false;
                var (cw, ch) = ResolveCanvas(ResolvedAspectRatio, Megapixels, UseLatentUpscale);
                return FrameStackGb(FramesForSeconds(ClampLength(LengthSeconds)),
                                    (int)(cw * RtxScale), (int)(ch * RtxScale)) >= HeavyFrameStackGb;
            }
        }

        /// <summary>Freezes this tab's pipeline switches onto the item — what <see cref="GenerateItemAsync"/>
        /// later reads back. The base tab stamps none; the hybrid graph honours none of these.</summary>
        protected override void ConfigureQueuedItem(H3EnsembleQueueItem item)
        {
            item.MaxFidelityReferences = MaxFidelityReferences;
            item.UseAudioEnhancement = UseAudioEnhancement;
            item.UseLatentUpscale = UseLatentUpscale;
            item.UseSla = UseSla;
            item.SlaSparsity = SlaSparsity;
            item.UseSparseAttention = UseSparseAttention;
        }

        // ── Canvas arithmetic — the MiniMax I2V tab's, reproduced here because this graph derives the
        //    finished canvas the same way (draft from ResolutionSelector, then ×2), where the hybrid graph
        //    names the finished canvas directly. ─────────────────────────────────────────────────────

        /// <summary>Width and height of the finished frames.</summary>
        private static (int Width, int Height) ResolveCanvas(
            string aspectRatio, double megapixels, bool latentUpscale)
        {
            var (w, h) = SampledCanvas(aspectRatio, megapixels, latentUpscale);
            if (!latentUpscale) return (w, h);
            return ((int)(w * LatentUpscaleFactor), (int)(h * LatentUpscaleFactor));
        }

        /// <summary>The canvas actually written into the graph — the draft when the latent upscale is on,
        /// which is also what <c>ref_image_size: match</c> scales the references to.</summary>
        private static (int Width, int Height) SampledCanvas(
            string aspectRatio, double megapixels, bool latentUpscale) =>
            H3Canvas.Resolve(aspectRatio, latentUpscale ? DraftMegapixels(megapixels) : megapixels,
                             ResolutionMultiple);

        /// <summary>The megapixel target the draft is sampled at in order to finish at
        /// <paramref name="megapixels"/>.</summary>
        private static double DraftMegapixels(double megapixels) =>
            megapixels / (LatentUpscaleFactor * LatentUpscaleFactor);

        // ── The render ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Renders one queued clip through the MiniMax I2V turbo graph. Same queue, same keyframe locks,
        /// same panels and location, same stamped prompt as the Ensemble tab — only the graph differs: this
        /// job runs the I2V base pass (draft → 2× latent upscale → finish) with the clip's pictures as its
        /// reference images, in the order the prompt numbered them. No face-refine passes are built — the
        /// I2V graph has no branch for them, and identity is held by the encoding instead.
        /// </summary>
        protected override async Task GenerateItemAsync(H3EnsembleQueueItem item, CancellationToken token)
        {
            IsProcessing = true;
            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = "Preparing H3 Multi workflow...";

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                var clipLabel = item.IsStoryClip ? $", clip {item.ClipIndex}/{item.ClipCount}" : string.Empty;
                AddLog($"=== H3 Multi · MiniMax I2V turbo pipeline ({item.KeyframeCount} keyframe(s), " +
                       $"{item.Cast.Count} sheet(s){(item.HasEnvironment ? " + location" : string.Empty)}{clipLabel}) ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("H3Multi", token);

                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                var json = await LoadFileAsync(WorkflowFileName, token);

                ProcessingStatus = "Uploading keyframes, cast and location...";
                ProcessingProgress = 5;

                // Keyframe stills must still be on disk — the prompt is numbered for all of them, so an
                // item whose locks have gone cannot be renumbered now. Same policy as the Ensemble tab.
                var keyframes = item.KeyframePaths
                    .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                    .ToList();
                if (keyframes.Count != item.KeyframePaths.Count)
                    throw new FileNotFoundException(
                        $"{item.KeyframePaths.Count - keyframes.Count} keyframe still(s) are gone from disk. " +
                        "The prompt is numbered for all of them, so this item cannot be renumbered now — " +
                        "restore the files or re-queue the job.");

                // Who this clip actually casts. The prompt was assembled with the selective cast already
                // applied, so a character it never names is not uploaded, not wired and not encoded — the
                // ensemble's whole economy, unchanged on this graph.
                var castInClip = item.Cast
                    .Where(m => HybridCastPrompt.IncludesSubject(item.Prompt, m.Index))
                    .ToList();
                if (castInClip.Count == 0 && item.Cast.Count > 0)
                {
                    castInClip = item.Cast.ToList();
                    AddLog("This prompt names no <Subject n> — sending the whole queued cast rather than none.");
                }
                var left = item.Cast.Where(m => castInClip.All(c => c.Index != m.Index)).Select(m => m.Index).ToList();
                if (left.Count > 0)
                    AddLog($"Character {string.Join(", ", left)} {(left.Count == 1 ? "is" : "are")} not named " +
                           "in this clip — their references are left out of it entirely.");

                // Panels, per character, in cast order.
                var selected = new List<(EnsembleCastMember Member, SelectedPanels Panels)>();
                foreach (var member in castInClip)
                {
                    var sheet = ResolvePanels(member.PanelPaths, member.SheetPath, member.Index);
                    selected.Add((member, SelectPanels(sheet, member.PanelIndices, member.PanelViews,
                                                      member.Index, member.IsPerson, member.IsGroup)));
                }

                // The picture order the prompt numbered: keyframe locks first, then the cast's panels, then
                // — last — the location. Getting this wrong renders a studio photograph as the opening shot.
                var pictures = new List<string>(keyframes);
                pictures.AddRange(selected.SelectMany(s => s.Panels.Paths));
                if (item.HasEnvironment) pictures.Add(item.EnvironmentPath);

                var uploaded = new List<string>();
                foreach (var picture in pictures) uploaded.Add(await EnsureUploadedAsync(picture));
                if (uploaded.Count == 0)
                    throw new Exception("No reference images to wire — the run has neither keyframes nor a cast.");
                if (uploaded.Count > MaxReferenceImages)
                    throw new Exception($"{uploaded.Count} reference images, but MiniMaxH3ReferenceToVideo " +
                                        $"takes at most {MaxReferenceImages}. Set References to Auto, drop a " +
                                        "keyframe, or write this beat around fewer characters.");

                var runSeed = item.Seed >= 0 ? item.Seed : System.Random.Shared.NextInt64(0, long.MaxValue);
                var len = ClampLength(item.LengthSeconds);
                var aspect = item.AspectRatio;
                var (canvasW, canvasH) = ResolveCanvas(aspect, item.Megapixels, item.UseLatentUpscale);
                var (draftW, draftH) = SampledCanvas(aspect, item.Megapixels, item.UseLatentUpscale);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var clipTag = item.IsStoryClip ? $"_c{item.ClipIndex:00}" : string.Empty;
                var runToken = $"h3multi_{ts}{clipTag}";

                // The prompt goes in as stamped — the hybrid prompt's <Picture n> numbers already match the
                // wiring order above, and the I2V graph runs plain MiniMaxH3ReferenceToVideo.
                json = BuildWorkflow(json, item, uploaded, runSeed, runToken, out var pruned);
                AddLog(keyframes.Count == 0
                    ? $"References wired: <Picture 1>–<Picture {uploaded.Count}> are the cast" +
                      (item.HasEnvironment ? " and the location" : string.Empty) +
                      " — a continuous take with no frame lock."
                    : $"References wired: <Picture 1>–<Picture {keyframes.Count}> are the keyframe locks at " +
                      $"{string.Join(", ", item.KeyframeSeconds.Select(s => $"{s:0.00}s"))}; " +
                      $"<Picture {keyframes.Count + 1}>–<Picture {uploaded.Count}> are the cast" +
                      (item.HasEnvironment ? " and the location" : string.Empty) + ".");
                if (pruned > 0)
                    AddLog($"Graph pruned to the video output: {pruned} node(s) removed — including the " +
                           "continuation loop the I2V tab uses, since each clip here is its own job.");

                AddLog(item.MaxFidelityReferences
                    ? $"References at a 2048px short edge (draft canvas {draftW}×{draftH}) — identity " +
                      "fidelity costs reference tokens on every step."
                    : $"References scaled to the sampled canvas, {draftW}×{draftH} — turn on max-fidelity " +
                      "references if identity or fine structure is not coming through.");

                var frameCount = FramesForSeconds(len);
                var steps = item.UseLatentUpscale ? $"4 draft steps at {draftW}×{draftH} → ×{LatentUpscaleFactor:0.#} → 3 finish steps" : "8 steps";
                var finish = item.RtxUpscale ? $"RTX ×{RtxScale:0.#} → ≈{canvasW * RtxScale:0}×{canvasH * RtxScale:0}" : "no upscale";

                ProcessingProgress = 10;
                ProcessingStatus = "Generating video...";
                AddLog($"Generating (seed {runSeed}, {len:0.#}s / {frameCount} frames @ {OutputFrameRate}fps, " +
                       $"{aspect} ≈{canvasW}×{canvasH}, {item.Megapixels:0.0} MP, {steps}" +
                       $"{(item.UseSla ? $", SLA {item.SlaSparsity:0.00}" : "")}, {finish})...");
                AddLog(item.UseLatentUpscale
                    ? $"Latent upscale on: the pass settles the composition at {draftW}×{draftH}, the " +
                      "MiniMax H3 3D upscaler doubles it, and three fixed-sigma steps finish at " +
                      $"{canvasW}×{canvasH}. Only those three ever see the full canvas — that is the " +
                      "MiniMax I2V recipe this tab runs."
                    : $"Latent upscale off: one 8-step pass at {canvasW}×{canvasH}.");

                // Said out loud before the wait rather than after the crash: every image node in this
                // graph holds the whole clip, so this is the number that decides whether the server survives.
                var peakGb = item.RtxUpscale
                    ? FrameStackGb(frameCount, (int)(canvasW * RtxScale), (int)(canvasH * RtxScale))
                    : FrameStackGb(frameCount, canvasW, canvasH);
                AddLog($"Peak frame stack ≈{peakGb:0.#} GB ({frameCount} frames held at once).");
                if (peakGb >= HeavyFrameStackGb)
                    AddLog("WARNING: that is large enough to take ComfyUI down mid-render — if this job dies " +
                           "with the prompt \"neither queued nor in the run history\", shorten the clip, drop " +
                           "to 0.7 MP, or turn RTX off here and upscale afterwards in ✨ Enhance Video.");

                var local = await SubmitAndRetrieveAsync(json, runToken, NodeSave, 10, 95, token);
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
                var size = item.RtxUpscale ? $"RTX ×{RtxScale:0.#} ≈{canvasW * RtxScale:0}×{canvasH * RtxScale:0}" : $"≈{canvasW}×{canvasH}";
                item.OutputVideoPath = finalPath;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResultVideoPath = finalPath;
                    ResultVideoInfo = $"H3 Multi • {(item.IsStoryClip ? $"clip {item.ClipIndex}/{item.ClipCount} • " : string.Empty)}" +
                                      $"{castInClip.Count} character(s) • {item.KeyframeCount} keyframe(s) • " +
                                      $"I2V turbo {(item.UseLatentUpscale ? "4+3" : "8")}-step • {size} • {aspect} • " +
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

        /// <summary>The stem this tab's finished files are named after — the Ensemble tab hard-codes its own
        /// into its render; the naming here stays in step with it.</summary>
        private const string OutputFileStem = "H3Multi";

        /// <summary>
        /// Writes one queued clip into the I2V graph and cuts it down to the base pass's sink. Mirrors the
        /// MiniMax I2V tab's patching for the no-continuation case: the clip's pictures (locks, panels,
        /// location) land in the reference node's autogrow slots, the draft/finish scheme and SLA are
        /// settled, and everything the sink cannot reach — the whole continuation loop included — is
        /// deleted rather than unhooked.
        /// </summary>
        private string BuildWorkflow(
            string json, H3EnsembleQueueItem item, IReadOnlyList<string> uploaded,
            long runSeed, string runToken, out int pruned)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeBaseRef2V, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodeSave, "VHS_VideoCombine");

            // ── References ────────────────────────────────────────────────────
            // The export ships one LoadImage; the rest are injected beside it with ids well clear of the
            // graph's own (which look like "4145:174").
            SetInput(root, NodeReference0, "image", uploaded[0]);
            var loaders = new List<string> { NodeReference0 };
            for (var i = 1; i < uploaded.Count; i++)
            {
                var id = $"multi_ref_{i}";
                root[id] = new JsonObject
                {
                    ["inputs"] = new JsonObject { ["image"] = uploaded[i] },
                    ["class_type"] = "LoadImage",
                    ["_meta"] = new JsonObject { ["title"] = $"Picture {i + 1}" }
                };
                loaders.Add(id);
            }
            AttachReferences(root, NodeBaseRef2V, loaders);
            SetInput(root, NodeBaseRef2V, "ref_image_size", item.MaxFidelityReferences ? "max" : "match");

            // ── Prompt, length, seed ──────────────────────────────────────────
            SetInput(root, NodeBasePrompt, "value", item.Prompt);
            SetInput(root, NodeBaseSeconds, "value", (int)Math.Round(ClampLength(item.LengthSeconds)));
            SetInput(root, NodeBaseSeed, "noise_seed", runSeed);

            // ── Canvas ────────────────────────────────────────────────────────
            // ResolutionSelector sizes the *sampled* canvas, which with the latent upscale on is the
            // draft — a quarter of the megapixel target, doubled afterwards. The dropdown names the
            // finished size, so the division happens here rather than in the user's head.
            // ResolutionSelector's aspect_ratio is a COMBO of eight fixed strings; for an aspect outside
            // that list the canvas is resolved here by the same arithmetic the node uses and fed in as two
            // PrimitiveInt nodes standing exactly where its width and height outputs were.
            if (H3Canvas.RequiresLiteralCanvas(item.AspectRatio))
            {
                var (cw, ch) = SampledCanvas(item.AspectRatio, item.Megapixels, item.UseLatentUpscale);
                root[NodeCanvasWidth] = IntNode(cw, "Canvas width");
                root[NodeCanvasHeight] = IntNode(ch, "Canvas height");
                Retarget(root, NodeResolution, 0, NodeCanvasWidth);
                Retarget(root, NodeResolution, 1, NodeCanvasHeight);
            }
            else
            {
                SetInput(root, NodeResolution, "aspect_ratio", item.AspectRatio);
                SetInput(root, NodeResolution, "megapixels",
                         item.UseLatentUpscale ? DraftMegapixels(item.Megapixels) : item.Megapixels);
                SetInput(root, NodeResolution, "multiple", ResolutionMultiple);
            }

            // ── Attention ─────────────────────────────────────────────────────
            foreach (var id in new[] { NodeSlaBase, NodeSlaLoop })
            {
                if (root[id] is not JsonObject) continue;
                SetInput(root, id, "enabled", item.UseSla);
                SetInput(root, id, "sparsity_ratio", item.SlaSparsity);
                SetInput(root, id, "block_size", SlaBlockSize);
            }
            SetInput(root, NodeSparseAttention, "switch", item.UseSparseAttention);

            // ── Sampling scheme ───────────────────────────────────────────────
            // The 2x has to be written into the upscaler, and the draft sampler pointed at either the
            // split schedule (first four steps, stops at sigma 0.5 for the finish to pick up) or, with the
            // upscale off, the full 8-step shifted schedule.
            SetInput(root, NodeBaseUpscaler, "mode.scale", LatentUpscaleFactor);
            SetInput(root, NodeBaseDetail, "switch", item.UseLatentUpscale);
            Link(root, NodeBaseSampler, "sigmas",
                 item.UseLatentUpscale ? NodeDraftSigmas : NodeBaseFullSigmas, 0);

            // ── The saved half's finishers, and the sink ──────────────────────
            SetInput(root, NodeBaseRtx, "switch", item.RtxUpscale);
            SetInput(root, NodeBaseAudio, "switch", item.UseAudioEnhancement);
            SetInput(root, NodeSave, "frame_rate", OutputFrameRate);
            SetInput(root, NodeSave, "filename_prefix", $"{OutputSubfolder}/{runToken}");
            SetInput(root, NodeSave, "save_output", true);

            return PruneToOutputs(root.ToJsonString(), new[] { NodeSave }, out pruned);
        }

        /// <summary>
        /// Renders one clip's storyboard stills on this tab's own graph — the turbo pipeline, so the frame
        /// that becomes the clip's opening lock is made by the same recipe the clip itself will run: draft
        /// → 2× latent upscale → finish, with the references at full fidelity exactly as the clip will
        /// encode them. The base implementation's wiring is written against the hybrid graph's node ids;
        /// everything around it (the pure reference-generation prompt, the cast selection, the candidate
        /// keeping) is inherited behaviour reproduced here against this graph's nodes.
        /// </summary>
        protected override async Task<IReadOnlyList<string>> RenderClipStillsAsync(
            int clipIndex, int clipCount, string clip, long seed, int frames, CancellationToken token)
        {
            var cast = CastMembers;
            if (cast.Count == 0) throw new Exception("No cast is loaded.");

            var stillPrompt = HybridCastPrompt.Assemble(
                HybridCastPrompt.DropPictureLocks(HybridCastPrompt.Strip(clip)),
                Array.Empty<HybridCastPrompt.Keyframe>(), cast, CastWardrobe,
                ClampLength(LengthSeconds), SelectedMedium, SheetsShowWardrobe,
                selectiveCast: clipCount > 1, environment: WiresEnvironment);
            if (stillPrompt.Length == 0)
                throw new Exception($"Clip {clipIndex} has no body to render a still from.");

            // Mirrors the submit path: a clip that never names a character is not shown their photographs,
            // and the prompt above was numbered for exactly that cast. The location is kept — this still
            // is about to be frame 0 of a render in that place.
            var panels = CurrentCastPanels(stillPrompt);
            if (panels.Count == 0)
                throw new Exception("The cast has no reference panels to render from — build the sheets first.");

            var pictures = new List<string>(panels);
            if (WiresEnvironment) pictures.Add(EnvironmentPath);
            if (pictures.Count > MaxReferenceImages)
                throw new Exception($"{pictures.Count} reference images for the still, but " +
                                    $"MiniMaxH3ReferenceToVideo takes at most {MaxReferenceImages}.");

            var uploaded = new List<string>();
            foreach (var picture in pictures) uploaded.Add(await EnsureUploadedAsync(picture));

            var json = await LoadFileAsync(WorkflowFileName, token);
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");
            RequireClass(root, NodeBaseRef2V, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodeBaseFrames, "VAEDecode");

            // References: the export ships one LoadImage; the rest are injected beside it. Max fidelity is
            // forced on for the still — it is a preview of what the clip will render, and the clip's own
            // default encodes its references at a 2048px short edge.
            SetInput(root, NodeReference0, "image", uploaded[0]);
            var loaders = new List<string> { NodeReference0 };
            for (var i = 1; i < uploaded.Count; i++)
            {
                var id = $"multi_still_ref_{i}";
                root[id] = new JsonObject
                {
                    ["inputs"] = new JsonObject { ["image"] = uploaded[i] },
                    ["class_type"] = "LoadImage",
                    ["_meta"] = new JsonObject { ["title"] = $"Still picture {i + 1}" }
                };
                loaders.Add(id);
            }
            AttachReferences(root, NodeBaseRef2V, loaders);
            SetInput(root, NodeBaseRef2V, "ref_image_size", "max");

            SetInput(root, NodeBasePrompt, "value", stillPrompt);
            SetInput(root, NodeBaseSeed, "noise_seed", seed);
            // The whole saving: the frame count. Written as a literal on the reference node — overwriting
            // its link to the seconds-derived expression — and as the seconds the rest of the graph would
            // derive its count from. The canvas is deliberately NOT reduced: this still is about to be
            // frame 0 of a render at exactly this size, and the draft→finish scheme runs so it previews
            // the finished canvas rather than the quarter-size draft.
            SetInput(root, NodeBaseRef2V, "length", frames);
            SetInput(root, NodeBaseSeconds, "value", Math.Max(1, (int)Math.Round(frames / (double)OutputFrameRate)));
            SetInput(root, NodeResolution, "aspect_ratio", ResolvedAspectRatio);
            SetInput(root, NodeResolution, "megapixels", DraftMegapixels(Megapixels));
            SetInput(root, NodeResolution, "multiple", ResolutionMultiple);
            SetInput(root, NodeBaseDetail, "switch", true);
            SetInput(root, NodeBaseUpscaler, "mode.scale", LatentUpscaleFactor);
            Link(root, NodeBaseSampler, "sigmas", NodeDraftSigmas, 0);

            // The still sinks: one ImageFromBatch + SaveImage pair per frame worth keeping, hung off the
            // decoded frames ahead of the RTX switch. Frame 0 becomes the lock; on a longer preview the
            // midpoint and the last frame are saved too, because by then the model has moved the camera.
            var runToken = $"storyboard_{DateTime.Now:yyyyMMdd_HHmmss}_c{clipIndex:00}";
            var indices = new List<int> { 0 };
            if (frames >= 22) { indices.Add(frames / 2); indices.Add(frames - 1); }

            var saves = new List<KeyValuePair<string, int>>();
            for (var i = 0; i < indices.Count; i++)
            {
                var pick = $"{StillPickPrefix}{i}";
                var save = $"{StillSavePrefix}{i}";
                root[pick] = new JsonObject
                {
                    ["inputs"] = new JsonObject
                    {
                        ["image"] = new JsonArray(NodeBaseFrames, 0),
                        ["batch_index"] = indices[i],
                        ["length"] = 1,
                    },
                    ["class_type"] = "ImageFromBatch",
                    ["_meta"] = new JsonObject { ["title"] = $"Storyboard frame {indices[i]}" }
                };
                root[save] = new JsonObject
                {
                    ["inputs"] = new JsonObject
                    {
                        ["images"] = new JsonArray(pick, 0),
                        ["filename_prefix"] = $"{OutputSubfolder}/{runToken}_f{indices[i]:000}",
                    },
                    ["class_type"] = "SaveImage",
                    ["_meta"] = new JsonObject { ["title"] = $"Storyboard save {indices[i]}" }
                };
                saves.Add(new KeyValuePair<string, int>(save, indices[i]));
            }

            json = PruneToOutputs(root.ToJsonString(), saves.Select(s => s.Key), out var pruned);
            if (pruned > 0)
                AddLog($"Storyboard graph pruned to the stills: {pruned} node(s) removed (the audio branch, " +
                       "RTX, the video mux and the continuation loop).");

            var promptId = await SubmitStoryboardAsync(json, token);
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);

            var stills = new List<string>();
            foreach (var save in saves)
            {
                if (!byNode.TryGetValue(save.Key, out var outs) || outs.Count == 0) continue;
                var local = await ResolveImageToLocalAsync(outs[0]);
                if (local == null || !File.Exists(local)) continue;
                stills.Add(KeepStill(local, clipIndex, save.Value, seed));
            }
            return stills;
        }

        // ── Graph helpers — the JsonObject-based set the MiniMax I2V tab uses, local to this tab so the
        //    two graphs can keep their own locked node ids. ─────────────────────────────────────────

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

        /// <summary>A literal INT the rest of the graph can link to.</summary>
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
    }
}
