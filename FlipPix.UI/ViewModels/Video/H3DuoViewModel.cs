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
    /// "H3 Duo" tab — the H3 Cast machinery on the <b>MiniMax I2V</b> render pipeline.
    ///
    /// <para>Everything the 🪪👥 H3 Cast tab does is inherited unchanged: the story/scene inputs, the
    /// wardrobe that is derived once and then locked, the two character cards (photo in — browsed or ✨
    /// generated with a picked LoRA — sheet out through Qwen-Image-Edit-2511, panels split before they are
    /// sent), the <c>texttovideoH3.md</c> Analyze that writes one prompt per clip of a chain, the queue
    /// that renders the clips in order and FFmpeg-joins them when the last one lands. See
    /// <see cref="H3CastViewModel"/> for all of it.</para>
    ///
    /// <para>The only thing this class replaces is <see cref="GenerateItemAsync"/> — the render itself.
    /// Where H3 Cast submits <c>h3facerefiner.json</c>, this tab submits <c>h3-duo.json</c>: the graph the
    /// 🌀 MiniMax I2V tab runs, which renders each clip as <b>four draft steps at a quarter of the canvas,
    /// a 2× pass through the MiniMax H3 3D latent upscaler, then three fixed-sigma steps at the finished
    /// size</b> — the lightx2v-turbo recipe that is both quicker and sharper than denoising the whole clip
    /// at full size. Each queued clip is one job against the base pass of that graph; the continuation loop
    /// the I2V tab uses for long takes is pruned out, because a story here is already a chain of separate
    /// jobs joined by FFmpeg.</para>
    ///
    /// <para>Two differences from the Cast graph are worth knowing before pressing Generate:</para>
    /// <list type="bullet">
    /// <item><b>There is no face-refine pass.</b> The I2V graph has no H3-FaceRefine branch, and what that
    /// pass bought is largely replaced here by keeping the references themselves at full fidelity — see
    /// <see cref="MaxFidelityReferences"/>, on by default on this tab.</item>
    /// <item><b>References are not held at the finished canvas by construction.</b> The Cast graph encodes
    /// its panels at the finished canvas regardless of the draft; the I2V graph's <c>ref_image_size:
    /// match</c> scales every reference to the <i>draft</i> canvas — a quarter of the chosen megapixels —
    /// which is not enough face for identity to survive. <see cref="MaxFidelityReferences"/> switches the
    /// node to <c>max</c> (a 2048px short edge) instead, at the cost of reference tokens riding through
    /// every sampling step.</item>
    /// </list>
    /// </summary>
    public class H3DuoViewModel : H3CastViewModel
    {
        // ── Workflow node ids (locked to h3-minimax/h3-duo.json — a copy of h3-minimax-i2v.json) ──
        private const string NodeReference0 = "10";        // LoadImage → ref_image_0 (the first panel)
        private const string NodeBaseRef2V = "4145:174";   // MiniMaxH3ReferenceToVideo (the clip's only pass)
        private const string NodeBasePrompt = "56";        // PrimitiveStringMultiline
        private const string NodeBaseSeconds = "4145:147"; // easy int → the frame-count expression
        private const string NodeBaseSeed = "4145:149";    // RandomNoise
        private const string NodeResolution = "60";        // ResolutionSelector — the *draft* canvas
        // Added to the graph, not present in the file: the two INT sources that stand in for
        // ResolutionSelector's outputs when the chosen aspect is one the node's combo does not accept.
        private const string NodeCanvasWidth = "duo_canvas_w";
        private const string NodeCanvasHeight = "duo_canvas_h";
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

        public H3DuoViewModel(
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
            // and this tab exists to hold faces, so full fidelity is the default here. It costs reference
            // tokens on every step; the checkbox turns it off for speed.
            MaxFidelityReferences = true;

            // The I2V graph has no face-refine branch; keeping the inherited flag false is what makes the
            // queue rows (and the queued items themselves) say so honestly.
            FaceRefine = false;

            AddLog("H3 Duo initialized — the H3 Cast flow on the MiniMax I2V turbo pipeline");
        }

        /// <summary>The MiniMax I2V turbo graph (a copy the I2V tab cannot break by evolving its own).</summary>
        protected override string WorkflowFileName => "workflow/video/h3-minimax/h3-duo.json";

        protected override string OutputSubfolder => "h3_duo";

        protected override string OutputFileStem => "H3Duo";

        protected override string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h3duo_queue.json");

        /// <summary>
        /// The quality dropdown names the <b>finished</b> canvas. Wider than H3 Cast's range because only
        /// three steps of this pipeline ever see the full canvas — the same reason the MiniMax I2V tab
        /// offers 1.5 MP. Sizes are what this graph really produces at 16:9.
        /// </summary>
        public override IReadOnlyList<MegapixelOption> MegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.4, "0.4 MP — fast draft (832×512)"),
            new MegapixelOption(0.7, "0.7 MP — balanced (1152×640)"),
            new MegapixelOption(1.0, "1.0 MP — full quality (1344×768)"),
            new MegapixelOption(1.5, "1.5 MP — high (1664×960)"),
        };

        /// <summary>What the saved file will be — the I2V canvas arithmetic, not the Cast tab's.</summary>
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

        // ── Canvas arithmetic — the MiniMax I2V tab's, reproduced here because this graph derives the
        //    finished canvas the same way (draft from ResolutionSelector, then ×2), where the Cast graph
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
        /// Renders one queued clip through the MiniMax I2V turbo graph. Same queue, same panels, same
        /// stamped prompt as H3 Cast — only the graph differs: this job runs the I2V base pass (draft → 2×
        /// latent upscale → finish) with the cast's panels as its reference pictures, and the continuation
        /// loop the I2V tab uses is pruned out entirely because a story here is a chain of jobs.
        /// </summary>
        protected override async Task GenerateItemAsync(H3CastQueueItem item, CancellationToken token)
        {
            IsProcessing = true;
            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = "Preparing H3 Duo workflow...";

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                var clipLabel = item.IsStoryClip ? $", clip {item.ClipIndex}/{item.ClipCount}" : string.Empty;
                AddLog($"=== H3 Duo · MiniMax I2V turbo pipeline ({(item.HasCharacter2 ? "2 sheets" : "1 sheet")}{clipLabel}) ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("H3Duo", token);

                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                var json = await LoadFileAsync(WorkflowFileName, token);

                ProcessingStatus = "Uploading character references...";
                ProcessingProgress = 5;

                // One reference per view, never the assembled sheet — H3 conditions on each ref_image as
                // a single subject. Same policy as H3 Cast, on the graph the I2V tab runs.
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

                var runSeed = item.Seed >= 0 ? item.Seed : System.Random.Shared.NextInt64(0, long.MaxValue);
                var len = ClampLength(item.LengthSeconds);
                var aspect = item.AspectRatio;
                var (canvasW, canvasH) = ResolveCanvas(aspect, item.Megapixels, item.UseLatentUpscale);
                var (draftW, draftH) = SampledCanvas(aspect, item.Megapixels, item.UseLatentUpscale);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var clipTag = item.IsStoryClip ? $"_c{item.ClipIndex:00}" : string.Empty;
                var runToken = $"h3duo_{ts}{clipTag}";

                // The prompt was stamped in @tags at queue time; the I2V graph runs plain
                // MiniMaxH3ReferenceToVideo, so the aliases are swapped back for the fixed picture
                // numbers the panels are wired in.
                var prompt = CastPromptStamp.Detag(item.Prompt, panels1.Count, panels2.Count);

                json = BuildWorkflow(json, item, uploaded, prompt, len, runSeed, runToken, out var pruned);
                AddLog($"References wired: {uploaded.Count} panel image(s) as <Picture 1>–" +
                       $"<Picture {uploaded.Count}> — character 1's first, character 2's after theirs.");
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
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "H3Duo");
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
                    ResultVideoInfo = $"H3 Duo • {(item.IsStoryClip ? $"clip {item.ClipIndex}/{item.ClipCount} • " : string.Empty)}" +
                                      $"{(item.HasCharacter2 ? "2 sheets" : "1 sheet")} • " +
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

        /// <summary>
        /// Writes one queued clip into the I2V graph and cuts it down to the base pass's sink. Mirrors the
        /// MiniMax I2V tab's patching for the no-continuation case: the cast's panels land in the
        /// reference node's autogrow slots, the draft/finish scheme and SLA are settled, and everything
        /// the sink cannot reach — the whole continuation loop included — is deleted rather than unhooked.
        /// </summary>
        private string BuildWorkflow(
            string json, H3CastQueueItem item, IReadOnlyList<string> uploaded, string prompt,
            double lengthSeconds, long runSeed, string runToken, out int pruned)
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
                var id = $"duo_ref_{i}";
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
            SetInput(root, NodeBasePrompt, "value", prompt);
            SetInput(root, NodeBaseSeconds, "value", (int)Math.Round(lengthSeconds));
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
            // 64 always: H3 packs audio at 80 rows per second, so a 128-row block forces 1.6s of audio
            // through one attention pattern and speech comes back robotic. Every clip this tab renders
            // has a soundtrack, so the wider block is never the right trade here.
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
            // split schedule (first four steps, stops at sigma 0.5 for the finish to pick up) or, with
            // the upscale off, the full 8-step shifted schedule.
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
