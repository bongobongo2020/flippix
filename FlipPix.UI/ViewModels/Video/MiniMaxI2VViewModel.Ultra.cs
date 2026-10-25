using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// 🌀 MiniMax I2V's second workflow: <b>💎 All-In-One Ultra (Full-Res Tiled)</b>, JunkunStudio's graph,
    /// chosen from the dropdown at the top of the tab.
    ///
    /// <para><b>A workflow, not a stack.</b> Stacks are patches on h3-minimax-i2v.json. Ultra shares none of
    /// that graph's machinery — no draft canvas, no latent ×2 finish, no continuation loop — so it ships as
    /// its own file, <c>h3-minimax-ultra.json</c>, converted node-for-node from the authored graph by
    /// tools/convert_h3_ultra.py. Node ids are the authored ones.</para>
    ///
    /// <para><b>What it does.</b> One MiniMaxH3ReferenceToVideo at the <i>finished</i> canvas (2.0 MP 16:9 →
    /// 1920×1088 as authored), with up to nine pictures, three audio and three video references at
    /// <c>ref_image_size: max</c>, sampled by <c>SelfLiftH3Sampler</c>: euler over a 10-step beta schedule,
    /// the first six evaluations at 0.6× scale, then the LMS latent upscaler lifts the latent to full size
    /// and the rest run tiled there. The model wire is the fl2va/ref2va hybrid → comfy kitchen attention →
    /// sol-attn block-sparse attention → MysticXXX 0.50 → DMAD dare-ties 0.75 → (High motion fix: Combat,
    /// Motion Repair, Fight Flow, speed slider) → sigma shift 12/3.</para>
    ///
    /// <para><b>Every dial opens on the authored value</b> — that graph is what was tested for quality on
    /// the server — and ↺ puts them all back. See <see cref="MiniMaxI2VUltraOptions"/>.</para>
    /// </summary>
    public partial class MiniMaxI2VViewModel
    {
        internal const string UltraWorkflowFileName = "workflow/video/h3-minimax/h3-minimax-ultra.json";

        public const int MaxUltraPictures = 9;
        public const int MaxUltraAudios = 3;
        public const int MaxUltraVideos = 3;

        // ── Node ids (locked to h3-minimax-ultra.json — the authored graph's own) ────────────────────
        private const string UltraUnet = "100";             // UNETLoader
        private const string UltraClip = "33";              // CLIPLoader
        private const string UltraAttention = "120";        // ModelAttentionBackend
        private const string UltraSparse = "204";           // BlockSparseAttention (sol-attn)
        private const string UltraMystic = "186";           // LoraLoaderModelOnly, MysticXXX
        private const string UltraDmad = "242";             // LoraLoaderModelOnly, DMAD dare-ties
        private static readonly string[] UltraMotionLoras = { "215", "217", "216", "218" };  // authored order
        private const string UltraMotionSwitch = "222";     // ComfySwitchNode
        private const string UltraMotionFlag = "223";       // PrimitiveBoolean "Fighting High Motion Fix"
        private const string UltraShift = "219";            // MiniMaxH3SigmaShift
        private const string UltraPrompt = "190";           // PrimitiveStringMultiline
        internal const string UltraDuration = "111";         // PrimitiveFloat, seconds
        private const string UltraResolution = "112";       // ResolutionSelector
        internal const string UltraRef2V = "121";            // MiniMaxH3ReferenceToVideo
        private const string UltraScheduler = "165";        // BasicScheduler
        internal const string UltraSelfLift = "224";         // SelfLiftH3Sampler
        internal const string UltraSink = "163";             // VHS_VideoCombine
        private const string UltraBlockSwap = "138";        // UniBlockSwap — authored bypassed, grafted on demand
        internal const string UltraVideoVae = "4";          // VAELoader, video
        internal const string UltraAudioVae = "5";          // VAELoader, audio
        internal const string UltraNegative = "225";        // ConditioningZeroOut of the positive
        internal const string UltraDecode = "153";          // VAEDecode
        internal const string UltraDecodeAudio = "159";     // VAEDecodeAudio

        /// <summary>ResolutionSelector's multiple in the authored graph.</summary>
        internal const int UltraResolutionMultiple = 32;

        /// <summary>
        /// Seconds per Gpx of the finished canvas, learned separately from the Ref2VA graph's figure: SelfLift
        /// samples six of its ten steps at 0.36× the area, then tiles the rest, which is nothing like the
        /// draft/finish split that number was learned against. A guess until the first run replaces it.
        /// </summary>
        private static double _ultraSecondsPerGpx = 900;

        private I2VWorkflow _workflow = I2VWorkflow.Ref2VA;

        /// <summary>The Ultra card. The same object settings persist, so an edit is saved as it is made.</summary>
        public MiniMaxI2VUltraOptions Ultra { get; private set; } = new();

        /// <summary>Picture slots 5–9, shown only on Ultra. Slots 1–4 are <see cref="References"/>, shared with
        /// the Ref2VA workflow, so switching back and forth keeps the cast.</summary>
        public ObservableCollection<MiniMaxI2VReference> ExtraReferences { get; } = new();

        public ObservableCollection<MiniMaxI2VMediaReference> AudioReferences { get; } = new();

        public ObservableCollection<MiniMaxI2VMediaReference> VideoReferences { get; } = new();

        public RelayCommand<MiniMaxI2VMediaReference> BrowseMediaCommand { get; private set; } = new(_ => { });
        public RelayCommand<MiniMaxI2VMediaReference> ClearMediaCommand { get; private set; } = new(_ => { });
        public RelayCommand ResetUltraCommand { get; private set; } = new(() => { });

        private void InitUltra()
        {
            var settings = _settingsService.Settings;
            _workflow = Enum.TryParse<I2VWorkflow>(settings?.MiniMaxI2VWorkflow, ignoreCase: true, out var stored)
                ? stored
                : I2VWorkflow.Ref2VA;

            Ultra = settings?.MiniMaxI2VUltra ?? new MiniMaxI2VUltraOptions();
            if (settings != null) settings.MiniMaxI2VUltra = Ultra;
            Ultra.PropertyChanged += OnUltraChanged;

            for (var slot = MaxReferences + 1; slot <= MaxUltraPictures; slot++)
            {
                var reference = new MiniMaxI2VReference(slot);
                reference.Changed += (_, _) => OnUltraMediaChanged();
                ExtraReferences.Add(reference);
            }
            for (var slot = 1; slot <= MaxUltraAudios; slot++)
            {
                var audio = new MiniMaxI2VMediaReference(isVideo: false, slot);
                audio.Changed += (_, _) => OnUltraMediaChanged();
                AudioReferences.Add(audio);
            }
            for (var slot = 1; slot <= MaxUltraVideos; slot++)
            {
                var video = new MiniMaxI2VMediaReference(isVideo: true, slot);
                video.Changed += (_, _) => OnUltraMediaChanged();
                VideoReferences.Add(video);
            }

            BrowseMediaCommand = new RelayCommand<MiniMaxI2VMediaReference>(async m => await BrowseMediaAsync(m));
            ClearMediaCommand = new RelayCommand<MiniMaxI2VMediaReference>(m => m?.Clear());
            ResetUltraCommand = new RelayCommand(ResetUltra);

            OfferUltraModel();

            if (_workflow == I2VWorkflow.Ultra)
                AddLog($"Workflow: {WorkflowName(_workflow)}");
        }

        // ── The dropdown ────────────────────────────────────────────────────────────────────────────

        public IReadOnlyList<I2VWorkflowOption> WorkflowOptions { get; } = new[]
        {
            new I2VWorkflowOption(I2VWorkflow.Ref2VA, "🌀 Ref2VA + continuations (h3-minimax-i2v)"),
            new I2VWorkflowOption(I2VWorkflow.Ultra, "💎 All-In-One Ultra (Full-Res Tiled)"),
        };

        /// <summary>
        /// Which graph Add to Queue freezes onto the next item. Not locked while a queue drains: every item
        /// carries its own workflow and its own copy of the card, so the queue can mix the two.
        /// </summary>
        public I2VWorkflow Workflow
        {
            get => _workflow;
            set
            {
                if (_workflow == value) return;
                _workflow = value;
                OnPropertyChanged();
                RaiseWorkflowState();

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.MiniMaxI2VWorkflow = value.ToString();
                    _settingsService.SaveSettings(settings);
                }

                if (value == I2VWorkflow.Ultra) OfferUltraModel();
                AddLog($"Workflow: {WorkflowName(value)}");
                if (value == I2VWorkflow.Ultra && HasContinuations)
                    AddLog("  Note: Ultra renders one pass of up to 15s — the continuations below are kept for " +
                           "the Ref2VA workflow and ignored here.");
            }
        }

        public bool IsUltra => _workflow == I2VWorkflow.Ultra;

        public bool IsRef2VA => _workflow != I2VWorkflow.Ultra;

        private static string WorkflowName(I2VWorkflow workflow) => workflow == I2VWorkflow.Ultra
            ? "💎 All-In-One Ultra (Full-Res Tiled) — h3-minimax-ultra.json"
            : "🌀 Ref2VA + continuations — h3-minimax-i2v.json";

        public string WorkflowSummary => IsUltra
            ? "JunkunStudio's All-In-One Ultra, as tested: one pass at the finished canvas, sampled by SelfLift — " +
              "the first steps at 0.6× scale, then the LMS latent upscaler lifts it and the rest run tiled at full " +
              "resolution. Up to 9 pictures plus 3 audio and 3 video references. Every dial below opens on the " +
              "workflow's own value."
            : "References + optional continuations past 15s, sampled draft → latent ×2 → finish, with the " +
              "sampling stacks below.";

        /// <summary>The note on the REFERENCE PICTURES header.</summary>
        public string ReferenceLimitNote => IsUltra ? "  · up to 9 (Ultra)" : "  · up to 4";

        private void RaiseWorkflowState()
        {
            OnPropertyChanged(nameof(Workflow));
            OnPropertyChanged(nameof(IsUltra));
            OnPropertyChanged(nameof(IsRef2VA));
            OnPropertyChanged(nameof(WorkflowSummary));
            OnPropertyChanged(nameof(ReferenceLimitNote));
            OnPropertyChanged(nameof(MoreReferences));
            OnPropertyChanged(nameof(MoreReferencesNote));
            RaiseUltraState();
            OnCanExecuteChanged();
        }

        // ── Media ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>Every filled picture slot Ultra submits, 1–9 in slot order, gaps closed up.</summary>
        public IReadOnlyList<MiniMaxI2VReference> UltraPictures =>
            References.Concat(ExtraReferences).Where(r => r.HasImage).ToList();

        private IReadOnlyList<string> UltraAudioPaths =>
            AudioReferences.Where(a => a.HasFile).Select(a => a.Path).ToList();

        private IReadOnlyList<string> UltraVideoPaths =>
            VideoReferences.Where(v => v.HasFile).Select(v => v.Path).ToList();

        /// <summary>The graph's own rule: audio rides alongside pictures or video and is never the only input.</summary>
        private bool HasUltraVisualReference => UltraPictures.Count > 0 || UltraVideoPaths.Count > 0;

        public string UltraMediaSummary
        {
            get
            {
                var pictures = UltraPictures.Count;
                var audios = UltraAudioPaths.Count;
                var videos = UltraVideoPaths.Count;
                var parts = new List<string> { $"{pictures} picture{(pictures == 1 ? "" : "s")}" };
                if (audios > 0) parts.Add($"{audios} audio");
                if (videos > 0) parts.Add($"{videos} video");
                var line = string.Join(" · ", parts);

                if (pictures + audios + videos > 12)
                    return $"⚠ {line} — the graph takes at most 12 files across all inputs.";
                if (!HasUltraVisualReference && audios > 0)
                    return $"⚠ {line} — audio cannot be the only reference; add a picture or a video.";
                return line + " · audio and video clips should each run 2–15 s, ≤ 15 s in total.";
            }
        }

        private void OnUltraMediaChanged()
        {
            OnPropertyChanged(nameof(UltraPictures));
            OnPropertyChanged(nameof(UltraMediaSummary));
            OnPropertyChanged(nameof(UltraResolvedAspect));
            OnPropertyChanged(nameof(UltraCanvasSummary));
            OnPropertyChanged(nameof(MoreReferencesNote));
            OnCanExecuteChanged();
        }

        private async Task BrowseMediaAsync(MiniMaxI2VMediaReference? media)
        {
            if (media == null) return;

            var filter = media.IsVideo
                ? "Video Files|*.mp4;*.mov;*.webm;*.mkv;*.avi|All Files|*.*"
                : "Audio Files|*.wav;*.mp3;*.flac;*.ogg;*.m4a;*.aac|All Files|*.*";
            var initialDir = media.IsVideo
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

            var path = await _fileDialogService.OpenFileDialogAsync(
                $"Select reference {(media.IsVideo ? "video" : "audio")} {media.Slot}",
                filter,
                initialDir,
                persistKey: media.IsVideo ? "minimaxi2v.ultra.video" : "minimaxi2v.ultra.audio");

            if (path == null) return;
            media.Path = path;
            AddLog($"<{media.Label}>: {Path.GetFileName(path)}");
        }

        /// <summary>
        /// What Analyze tells the prompt writer about Ultra's non-picture references. The model only ever sees
        /// the pictures, so the clips are named here, in slot order, with the role the authored prompt gives
        /// them: an audio clip is a voice/sound reference whose words are never copied, and a video clip is a
        /// motion and appearance reference.
        /// </summary>
        private List<string> UltraMediaInstructions()
        {
            var lines = new List<string>();
            var audios = UltraAudioPaths;
            var videos = UltraVideoPaths;
            if (audios.Count == 0 && videos.Count == 0) return lines;

            lines.Add("This take also has reference clips the pictures above do not show. Bind them in "
                      + "subject_definitions and retention_analysis the same way as the pictures:");
            for (var i = 0; i < videos.Count; i++)
                lines.Add($"  <Video {i + 1}> — {Path.GetFileNameWithoutExtension(videos[i])} (reference video: "
                          + "appearance and motion reference)");
            for (var i = 0; i < audios.Count; i++)
                lines.Add($"  <Audio {i + 1}> — {Path.GetFileNameWithoutExtension(audios[i])} (reference audio: the "
                          + "voice timbre and accent of whichever subject the draft idea gives it; generate new "
                          + "words, never copy the recording)");
            lines.Add(string.Empty);
            return lines;
        }

        // ── The card ────────────────────────────────────────────────────────────────────────────────

        private void OnUltraChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Every edit also announces IsAuthored; one save per edit is enough.
            if (e.PropertyName == nameof(MiniMaxI2VUltraOptions.IsAuthored)) return;

            var settings = _settingsService.Settings;
            if (settings != null) _settingsService.SaveSettings(settings);
            RaiseUltraState();
        }

        private void RaiseUltraState()
        {
            OnPropertyChanged(nameof(UltraResolvedAspect));
            OnPropertyChanged(nameof(UltraCanvasSummary));
            OnPropertyChanged(nameof(UltraFrameSummary));
            OnPropertyChanged(nameof(UltraSamplingSummary));
            OnPropertyChanged(nameof(UltraLoraSummary));
            OnPropertyChanged(nameof(UltraAuthoredBadge));
        }

        private void ResetUltra()
        {
            Ultra.Reset();
            OfferUltraModel();
            AddLog("💎 Ultra: every dial back on the authored workflow's value.");
        }

        /// <summary>Puts the card's checkpoint in the shared model list, for when the server has not answered
        /// (or has never had that file).</summary>
        private void OfferUltraModel()
        {
            var name = NormalizeName(Ultra.DiffusionModel);
            if (name.Length == 0) return;
            if (DiffusionModelOptions.Any(o => string.Equals(o.Value, name, StringComparison.OrdinalIgnoreCase)))
                return;
            DiffusionModelOptions.Add(new DiffusionModelOption(name, LabelFor(name) + " (Ultra)"));
        }

        public string UltraAuthoredBadge => Ultra.IsAuthored
            ? "✓ Every dial is on the workflow's tested value."
            : "Changed from the workflow's tested values — ↺ puts them back.";

        public IReadOnlyList<DiffusionModelOption> UltraTextEncoderOptions => UltraTextEncoders;

        public IReadOnlyList<string> UltraAttentionOptions => UltraAttentions;

        public IReadOnlyList<string> UltraSchedulerOptions => UltraSchedulers;

        public IReadOnlyList<string> UltraUpscalerOptions => UltraUpscalers;

        public IReadOnlyList<MegapixelOption> UltraMegapixelOptions => UltraMegapixels;

        // The card's lists, shared with 🎬 H3 Video Editor's Ultra preset so both offer the same choices.

        internal static readonly IReadOnlyList<DiffusionModelOption> UltraTextEncoders = new[]
        {
            new DiffusionModelOption(MiniMaxI2VUltraOptions.AuthoredTextEncoder, "qwen3vl 32b nvfp4 awq (workflow)"),
            new DiffusionModelOption(MiniMaxI2VUltraOptions.AlternateTextEncoder,
                                     "qwen3vl 32b ultra uncensored heretic int8 (workflow alternative)"),
            new DiffusionModelOption("qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors", "qwen3vl 32b w4a8 convrot"),
        };

        internal static readonly IReadOnlyList<string> UltraAttentions =
            new[] { "comfy kitchen attention", "pytorch attention" };

        internal static readonly IReadOnlyList<string> UltraSchedulers =
            new[] { "beta", "simple", "normal", "sgm_uniform", "karras", "exponential", "linear_quadratic", "kl_optimal" };

        internal static readonly IReadOnlyList<string> UltraUpscalers = new[]
        {
            MiniMaxI2VUltraOptions.AuthoredUpscaler,
            "minimax_h3_latent_upscaler_3d_bf16.safetensors",
            "minimax_h3_latent_upscaler_3d_conv_v1_fp16.safetensors",
            "minimax_h3_latent_upscaler_3d_fp16.safetensors",
            "none",
        };

        /// <summary>The authored graph's own size table (16:9, multiple 32) — its MarkdownNote, row for row
        /// from 1.0 MP up.</summary>
        internal static readonly IReadOnlyList<MegapixelOption> UltraMegapixels = new[]
        {
            new MegapixelOption(0.7, "0.7 MP — 1152×640"),
            new MegapixelOption(0.9, "0.9 MP — 1280×736"),
            new MegapixelOption(1.0, "1.0 MP — 1376×768"),
            new MegapixelOption(1.2, "1.2 MP — 1504×832"),
            new MegapixelOption(1.5, "1.5 MP — 1664×928"),
            new MegapixelOption(1.8, "1.8 MP — 1824×1024"),
            new MegapixelOption(2.0, "2.0 MP — 1920×1088 (workflow)"),
        };

        /// <summary>The aspect Ultra renders at — the picked one, or Auto resolved off picture 1.</summary>
        public string UltraResolvedAspect =>
            Ultra.AspectRatio == H3Canvas.AutoAspect ? ResolvedAspectRatio : Ultra.AspectRatio;

        public string UltraCanvasSummary
        {
            get
            {
                var (w, h) = H3Canvas.Resolve(UltraResolvedAspect, Ultra.Megapixels, UltraResolutionMultiple);
                return $"→ {UltraResolvedAspect} · {w}×{h} finished, sampled at " +
                       $"{(int)(w * Ultra.LowresScale)}×{(int)(h * Ultra.LowresScale)} for the first " +
                       $"{UltraTransitionFor(Ultra)} step(s)";
            }
        }

        /// <summary>The authored frame expression: <c>max(5, round(s·24))</c> snapped up to H3's 17k+5 grid.</summary>
        internal static int UltraFrameCount(double seconds)
        {
            var f = Math.Max(5, (int)Math.Round(seconds * 24, MidpointRounding.ToEven));
            return f + (5 - (f % 17)) % 17;
        }

        public string UltraFrameSummary =>
            $"{UltraFrameCount(Ultra.DurationSeconds)} frames at 24 fps (≈ {UltraFrameCount(Ultra.DurationSeconds) / 24.0:0.0}s)";

        public string UltraSamplingSummary => UltraSamplingSummaryFor(Ultra);

        internal static string UltraSamplingSummaryFor(MiniMaxI2VUltraOptions o) =>
            $"euler · {o.Scheduler} · {o.Steps} steps · cfg {o.Cfg:0.0} · first {UltraTransitionFor(o)} at " +
            $"{o.LowresScale:0.00}× → {LabelFor(o.UpscalerModel)}" +
            $"{(o.HighresTiling ? " · tiled" : string.Empty)} · shift {o.ShiftVideo:0.##}/{o.ShiftAudio:0.##}";

        public string UltraLoraSummary => UltraLoraSummaryFor(Ultra);

        internal static string UltraLoraSummaryFor(MiniMaxI2VUltraOptions o)
        {
            var on = new List<string>();
            if (o.UseMystic && o.MysticStrength > 0) on.Add($"MysticXXX {o.MysticStrength:0.00}");
            if (o.UseDmad && o.DmadStrength > 0) on.Add($"DMAD {o.DmadStrength:0.00}");
            if (o.HighMotionFix)
                on.Add($"High motion fix (Combat {o.CombatStrength:0.00}, Motion Repair {o.MotionRepairStrength:0.00}, " +
                       $"Fight Flow {o.FightFlowStrength:0.00}, speed slider {o.SpeedSliderStrength:0.00})");
            return on.Count == 0 ? "No LoRA on the wire." : string.Join(" → ", on);
        }

        /// <summary>SelfLift's low-res evaluations, kept below the schedule so at least one step runs lifted.</summary>
        internal static int UltraTransitionFor(MiniMaxI2VUltraOptions o) =>
            Math.Clamp(o.TransitionStep, 1, Math.Max(1, o.Steps - 1));

        // ── Building the graph ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes one Ultra item into h3-minimax-ultra.json. Everything not set here stays exactly as the
        /// authored graph has it. 🎬 H3 Video Editor's Ultra preset builds its clips through this too.
        /// </summary>
        internal static string BuildUltraWorkflow(
            string json, MiniMaxI2VQueueItem item,
            IReadOnlyList<string> pictures, IReadOnlyList<string> audios, IReadOnlyList<string> videos,
            long runSeed, string runToken, out int pruned)
        {
            var o = item.Ultra ?? throw new Exception("This item has no Ultra settings.");
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, UltraUnet, "UNETLoader");
            RequireClass(root, UltraRef2V, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, UltraSelfLift, "SelfLiftH3Sampler");
            RequireClass(root, UltraSink, "VHS_VideoCombine");

            // ── Models ────────────────────────────────────────────────────────
            if (o.DiffusionModel.Length > 0) SetInput(root, UltraUnet, "unet_name", NormalizeName(o.DiffusionModel));
            if (o.TextEncoder.Length > 0) SetInput(root, UltraClip, "clip_name", o.TextEncoder);

            // ── The model wire ────────────────────────────────────────────────
            // Built from the loader outwards. A node the card switches off is stepped over — the one below is
            // pointed at the one above — and the prune takes it.
            var wire = UltraUnet;
            if (o.LowVramBlockSwap)
            {
                // Authored bypassed between the loader and the attention backend; put back where it stood.
                root[UltraBlockSwap] = Node("UniBlockSwap", "UniBlockSwap Low VRAM Node", new JsonObject
                {
                    ["model"] = new JsonArray(wire, 0),
                    ["num_blocks"] = o.BlockSwapBlocks,
                });
                wire = UltraBlockSwap;
            }

            Link(root, UltraAttention, "model", wire, 0);
            SetInput(root, UltraAttention, "attention", o.AttentionBackend);
            wire = UltraAttention;

            if (o.UseSparseAttention)
            {
                Link(root, UltraSparse, "model", wire, 0);
                SetInput(root, UltraSparse, "selection.tau", o.SparseTau);
                SetInput(root, UltraSparse, "start_percent", o.SparseStart);
                SetInput(root, UltraSparse, "end_percent", o.SparseEnd);
                wire = UltraSparse;
            }

            wire = ChainLora(root, UltraMystic, wire, o.UseMystic, o.MysticStrength);
            wire = ChainLora(root, UltraDmad, wire, o.UseDmad, o.DmadStrength);

            // The High motion fix group. As authored it hangs off a ComfySwitchNode reading one boolean, and
            // the lazy switch never loads the branch it is not on — so "off" is the switch's false input,
            // which is exactly this wire. Linking past the switch when off says the same thing without asking
            // the server to validate four LoRAs the take never loads.
            if (o.HighMotionFix)
            {
                var strengths = new[] { o.CombatStrength, o.MotionRepairStrength, o.FightFlowStrength, o.SpeedSliderStrength };
                var motion = wire;
                for (var i = 0; i < UltraMotionLoras.Length; i++)
                    motion = ChainLora(root, UltraMotionLoras[i], motion, true, strengths[i]);

                SetInput(root, UltraMotionFlag, "value", true);
                Link(root, UltraMotionSwitch, "on_false", wire, 0);
                Link(root, UltraMotionSwitch, "on_true", motion, 0);
                Link(root, UltraShift, "model", UltraMotionSwitch, 0);
            }
            else
            {
                Link(root, UltraShift, "model", wire, 0);
            }

            SetInput(root, UltraShift, "shift_video", o.ShiftVideo);
            SetInput(root, UltraShift, "shift_audio", o.ShiftAudio);

            // ── Prompt, length, canvas ────────────────────────────────────────
            SetInput(root, UltraPrompt, "value", item.Prompt);
            SetInput(root, UltraDuration, "value", o.DurationSeconds);

            // ResolutionSelector's aspect is a fixed COMBO; the stereo aspects are not on it, so those are
            // resolved here by the node's own arithmetic and fed in as literals — as on the Ref2VA graph.
            if (H3Canvas.RequiresLiteralCanvas(item.AspectRatio))
            {
                var (cw, ch) = H3Canvas.Resolve(item.AspectRatio, o.Megapixels, UltraResolutionMultiple);
                root[NodeCanvasWidth] = IntNode(cw, "Canvas width");
                root[NodeCanvasHeight] = IntNode(ch, "Canvas height");
                Retarget(root, UltraResolution, 0, NodeCanvasWidth);
                Retarget(root, UltraResolution, 1, NodeCanvasHeight);
            }
            else
            {
                SetInput(root, UltraResolution, "aspect_ratio", item.AspectRatio);
                SetInput(root, UltraResolution, "megapixels", o.Megapixels);
                SetInput(root, UltraResolution, "multiple", UltraResolutionMultiple);
            }

            // ── References ────────────────────────────────────────────────────
            SetInput(root, UltraRef2V, "ref_image_size", o.MaxFidelityReferences ? "max" : "match");
            if (root[UltraRef2V]?["inputs"] is JsonObject refInputs)
                foreach (var key in refInputs.Select(kv => kv.Key).Where(k => k.StartsWith("ref_", StringComparison.Ordinal) &&
                                                                         k.Contains('.')).ToList())
                    refInputs.Remove(key);

            for (var i = 0; i < pictures.Count; i++)
            {
                var id = $"ultra_img_{i}";
                root[id] = Node("LoadImage", $"Picture {i + 1}", new JsonObject { ["image"] = pictures[i] });
                Link(root, UltraRef2V, $"ref_images.ref_image_{i}", id, 0);
            }
            for (var i = 0; i < audios.Count; i++)
            {
                var id = $"ultra_aud_{i}";
                root[id] = Node("VHS_LoadAudioUpload", $"Audio {i + 1}", new JsonObject
                {
                    ["audio"] = audios[i],
                    ["start_time"] = 0,
                    ["duration"] = 0,
                });
                Link(root, UltraRef2V, $"ref_audios.ref_audio_{i}", id, 0);
            }
            for (var i = 0; i < videos.Count; i++)
            {
                // The authored loader's settings: native rate, native size, every frame.
                var id = $"ultra_vid_{i}";
                root[id] = Node("VHS_LoadVideo", $"Video {i + 1}", new JsonObject
                {
                    ["video"] = videos[i],
                    ["force_rate"] = 0,
                    ["custom_width"] = 0,
                    ["custom_height"] = 0,
                    ["frame_load_cap"] = 0,
                    ["skip_first_frames"] = 0,
                    ["select_every_nth"] = 1,
                    ["format"] = "AnimateDiff",
                });
                Link(root, UltraRef2V, $"ref_videos.ref_video_{i}", id, 0);
            }

            // ── Sampling ──────────────────────────────────────────────────────
            SetInput(root, UltraScheduler, "scheduler", o.Scheduler);
            SetInput(root, UltraScheduler, "steps", Math.Clamp(o.Steps, 2, 60));

            SetInput(root, UltraSelfLift, "seed", runSeed);
            SetInput(root, UltraSelfLift, "cfg", o.Cfg);
            SetInput(root, UltraSelfLift, "transition_step", UltraTransitionFor(o));
            SetInput(root, UltraSelfLift, "lowres_scale", o.LowresScale);
            SetInput(root, UltraSelfLift, "rho", o.Rho);
            SetInput(root, UltraSelfLift, "w_min", o.WMin);
            SetInput(root, UltraSelfLift, "w_max", o.WMax);
            SetInput(root, UltraSelfLift, "upscaler_model", o.UpscalerModel);
            SetInput(root, UltraSelfLift, "highres_tiling", o.HighresTiling);
            SetInput(root, UltraSelfLift, "upscaler_unload", o.UpscalerUnload);

            // ── Sink ──────────────────────────────────────────────────────────
            SetInput(root, UltraSink, "filename_prefix", $"{OutputSubfolder}/{runToken}");
            SetInput(root, UltraSink, "save_output", true);

            return PruneToOutputs(root.ToJsonString(), new[] { UltraSink }, out pruned);
        }

        /// <summary>Puts one authored LoRA node on the wire at the card's strength, or steps over it.</summary>
        private static string ChainLora(JsonObject root, string loraNode, string wire, bool on, double strength)
        {
            if (!on || strength <= 0) return wire;
            Link(root, loraNode, "model", wire, 0);
            SetInput(root, loraNode, "strength_model", strength);
            return loraNode;
        }

        /// <summary>The Ultra run, described to the progress tracker: one SelfLift sampler, then decode and the
        /// encode of the finished file.</summary>
        private static (List<ProgressStage> Stages, double LeadUnits, double EstimatedSeconds)
            BuildUltraProgressPlan(MiniMaxI2VQueueItem item)
        {
            var stages = new List<ProgressStage>
            {
                // Steps 0: adopt whatever SelfLift reports. Decoding the full-size AV latent follows it.
                new("Rendering (SelfLift)", 0, 1.0, 3.0),
                new("Writing the video", 0, 0.0054, 0.27),
            };
            return (stages, 1.5, 60 + _ultraSecondsPerGpx * UltraGpx(item));
        }

        /// <summary>Finished canvas × frames, in billions — what the Ultra run time is scaled by.</summary>
        private static double UltraGpx(MiniMaxI2VQueueItem item)
        {
            var o = item.Ultra!;
            var (w, h) = H3Canvas.Resolve(item.AspectRatio, o.Megapixels, UltraResolutionMultiple);
            return (double)w * h * UltraFrameCount(o.DurationSeconds) / 1e9;
        }

        private void RecalibrateUltraFromRun(MiniMaxI2VQueueItem item, TimeSpan elapsed)
        {
            var gpx = UltraGpx(item);
            if (gpx <= 0 || elapsed.TotalSeconds < 60) return;
            var measured = (elapsed.TotalSeconds - 60) / gpx;
            if (measured is < 100 or > 6000) return;
            _ultraSecondsPerGpx = 0.5 * _ultraSecondsPerGpx + 0.5 * measured;
            AddLog($"Took {elapsed:hh\\:mm\\:ss} for {gpx:0.00} Gpx — {measured:0} s/Gpx " +
                   $"(Ultra estimate for the next item: {_ultraSecondsPerGpx:0} s/Gpx)");
        }

        /// <summary>Frames the Ultra item decodes, in GB of float32 RGB host RAM — the one memory figure this
        /// graph shares with the Ref2VA one. VRAM is SelfLift's business: it tiles the full-size pass.</summary>
        private static double UltraFrameRamGb(MiniMaxI2VQueueItem item)
        {
            var o = item.Ultra!;
            var (w, h) = H3Canvas.Resolve(item.AspectRatio, o.Megapixels, UltraResolutionMultiple);
            return (double)w * h * UltraFrameCount(o.DurationSeconds) * 12 / 1e9;
        }

        /// <summary>Freezes the Ultra half of the form into a queue item.</summary>
        private MiniMaxI2VQueueItem BuildUltraQueueItem()
        {
            var options = Ultra.Clone();
            options.DiffusionModel = NormalizeName(options.DiffusionModel);
            return new MiniMaxI2VQueueItem
            {
                Workflow = I2VWorkflow.Ultra,
                Ultra = options,
                ReferencePaths = UltraPictures.Select(r => r.Path).ToList(),
                AudioPaths = UltraAudioPaths.ToList(),
                VideoPaths = UltraVideoPaths.ToList(),
                Prompt = Prompt.Trim(),
                AspectRatio = UltraResolvedAspect,
                Megapixels = options.Megapixels,
                LengthSeconds = (int)Math.Round(options.DurationSeconds),
                Seed = Seed,
                MaxFidelityReferences = options.MaxFidelityReferences,
                // The Ref2VA graph's finishing switches: none of them exist in this one.
                UseLatentUpscale = false,
                UseRtxUpscale = false,
                UseAudioEnhancement = false,
                UseSla = false,
                DiffusionModel = options.DiffusionModel,
                FirstPassSteps = options.Steps,
            };
        }
    }

    /// <summary>One entry in the tab's workflow dropdown.</summary>
    public record I2VWorkflowOption(I2VWorkflow Value, string Label);
}
