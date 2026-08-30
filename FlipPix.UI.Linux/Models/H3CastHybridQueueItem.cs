using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using FlipPix.UI.Linux.Services;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// A serializable snapshot of one H3 Cast Hybrid job: the <b>keyframe stills</b> the video must land on
    /// at fixed timestamps, the 1–2 <b>character sheets</b> that carry the cast's identity, and the finished
    /// six-section hybrid prompt that ties them together — rendered through
    /// <c>workflow/video/h3-minimax/h3-cast-hybrid.json</c>.
    ///
    /// <para>The two picture kinds are kept in separate lists because they are wired in that order and the
    /// prompt is numbered from it: keyframes are <c>&lt;Picture 1&gt;</c>…<c>&lt;Picture K&gt;</c>, the cast
    /// panels follow. Swapping the two would not fail — it would silently render a studio photograph as the
    /// opening frame.</para>
    /// </summary>
    public class H3CastHybridQueueItem : BaseQueueItem
    {
        /// <summary>The timeline stills, in timestamp order. Empty = a pure reference-generation clip with
        /// no frame lock anywhere, which is what every clip of a chain after the first is.</summary>
        public List<string> KeyframePaths { get; set; } = new();

        /// <summary>Each keyframe's lock timestamp in seconds, index-aligned with
        /// <see cref="KeyframePaths"/>.</summary>
        public List<double> KeyframeSeconds { get; set; } = new();

        /// <summary>Character 1's sheet. Kept for the queue thumbnail and the row label; what is uploaded is
        /// <see cref="Character1PanelPaths"/>. Required.</summary>
        public string Character1SheetPath { get; set; } = string.Empty;

        /// <summary>Character 2's sheet. Empty = single-character run.</summary>
        public string Character2SheetPath { get; set; } = string.Empty;

        /// <summary>
        /// Character 1's sheet cut into single-view panels, left to right — the images actually uploaded and
        /// wired after the keyframes. Frozen at queue time rather than recomputed at submit time because the
        /// prompt's picture numbering was written from this count.
        /// </summary>
        public List<string> Character1PanelPaths { get; set; } = new();

        /// <summary>Character 2's panels, continuing the numbering after character 1's.</summary>
        public List<string> Character2PanelPaths { get; set; } = new();

        /// <summary>
        /// Which of character 1's panels are actually uploaded, as indices into
        /// <see cref="Character1PanelPaths"/> — the reference budget the tab was set to when this was queued
        /// (all three views, or front + face, or the face alone).
        ///
        /// <para>Stored as indices rather than as a second path list so the re-split fallback in
        /// <c>ResolvePanels</c> keeps working unchanged: the full sheet is resolved as it always was, and the
        /// selection is applied to it. Empty = every panel, which is what a legacy item means.</para>
        /// </summary>
        public List<int> Character1PanelIndices { get; set; } = new();

        /// <summary>Character 2's uploaded panels, as indices into <see cref="Character2PanelPaths"/>.</summary>
        public List<int> Character2PanelIndices { get; set; } = new();

        /// <summary>What each uploaded panel of character 1 shows ("full-body front", "face close-up", …),
        /// index-aligned with <see cref="Character1PanelIndices"/>. The prompt was written from these words,
        /// and the face one is the identity reference the refine pass tracks by.</summary>
        public List<string> Character1PanelViews { get; set; } = new();

        /// <summary>What each uploaded panel of character 2 shows.</summary>
        public List<string> Character2PanelViews { get; set; } = new();

        /// <summary>The photo character 1's sheet was built from. Never uploaded — kept for the thumbnail.</summary>
        public string Character1SourcePath { get; set; } = string.Empty;

        /// <summary>The photo character 2's sheet was built from. Never uploaded.</summary>
        public string Character2SourcePath { get; set; } = string.Empty;

        /// <summary>The scene image the prompt was analyzed from. Never uploaded.</summary>
        public string SceneImagePath { get; set; } = string.Empty;

        /// <summary>The assembled six-section hybrid prompt, wardrobe lock and all.</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>
        /// The same clip assembled as <b>pure reference generation</b> — no keyframe locks, no alignment
        /// paragraph, and the cast's panels numbered from <c>&lt;Picture 1&gt;</c> — which is what the
        /// face-refine pass conditions on.
        ///
        /// <para>It needs its own copy rather than sharing <see cref="Prompt"/> because the refine pass runs
        /// on a face crop, not on the video canvas: "at 0.00 seconds the frame is exactly
        /// &lt;Picture 1&gt;" describes a picture that pass never receives, and the cast panels it does
        /// receive are numbered from one. Written at queue time, where the keyframes, the cast and the
        /// wardrobe are all still in hand.</para>
        /// </summary>
        public string RefinePrompt { get; set; } = string.Empty;

        /// <summary>
        /// The refine prompt for <b>character 2's own pass</b>, written for their panels alone and numbered
        /// from <c>&lt;Picture 1&gt;</c>.
        ///
        /// <para>The face tracker follows one subject through a clip, so a two-hander needs two passes: one
        /// tracked on each character's face, each conditioned only on that character's photographs. A single
        /// pass shown both cast members' references had, by construction, nothing to say about which of the
        /// two faces it was looking at. Empty for a single-character clip.</para>
        /// </summary>
        public string RefinePrompt2 { get; set; } = string.Empty;

        public string AspectRatio { get; set; } = "16:9 (Widescreen)";
        public double Megapixels { get; set; } = 1.0;
        public double LengthSeconds { get; set; } = 8;

        /// <summary>"live-action and cinematic", "anime, cinematic", … — opens the prompt's global rules.</summary>
        public string Medium { get; set; } = "live-action and cinematic";

        /// <summary>-1 = pick a fresh random seed when the item runs.</summary>
        public long Seed { get; set; } = -1;

        /// <summary>
        /// The second H3 pass over the tracked face crops. <b>Default false, unlike the tab's own toggle</b>:
        /// an item queued before this pass existed deserializes without a <see cref="RefinePrompt"/>, and
        /// refining a keyframed clip against a prompt numbered for keyframes is worse than not refining it.
        /// Re-queue such an item to get the pass.
        /// </summary>
        public bool FaceRefine { get; set; }

        /// <summary>Denoise of the face-refine pass — node 106. How far a cropped face is allowed to move
        /// away from what the base pass rendered.</summary>
        public double RefineDenoise { get; set; } = 0.45;

        /// <summary>
        /// Opacity of the refined face at the stitch (<c>H3FaceStitch.blend</c>). 1.0 replaces the face
        /// outright with a VAE round-tripped copy; below that mixes the original pixels back, which is the
        /// only control that attenuates the round-trip loss the denoise cannot reach.
        /// </summary>
        public double RefineBlend { get; set; } = 1.0;

        /// <summary>
        /// Sizes the refine canvas from the largest crop rather than clamping it to 768
        /// (<c>H3FaceTrackCrop.canvas_mode</c>). Only bites on close-ups, where the capped mode would
        /// downscale the crop on the way in. Off by default: uncapped, and the cost is quadratic.
        /// </summary>
        public bool RefineNoDownscale { get; set; }

        /// <summary>
        /// Composite the refined face through a SAM mask (<c>H3FaceStitch.masks</c>) rather than the
        /// detected face box. On by default: the box is re-detected every frame, so pasting through it puts
        /// a moving rectangle over the face. Off, the mask nodes are pruned and the box comes back.
        /// </summary>
        public bool UseSamFaceMask { get; set; } = true;

        /// <summary>
        /// FILM ×2 frame interpolation, muxed at double the render rate. On by default: the 8-step turbo
        /// stack renders 24 fps and the interpolation is cheap next to the diffusion, so it is the one
        /// finishing pass that is nearly free.
        /// </summary>
        public bool Interpolate { get; set; } = true;

        /// <summary>
        /// The draft → 2× latent upscale → 3-step finish scheme on the base pass. On by default. The
        /// reference pictures are unaffected either way: they are encoded at the finished canvas.
        /// </summary>
        public bool UseLatentUpscale { get; set; } = true;

        /// <summary>
        /// Block-sparse attention on both H3 passes. On by default; when false the two H3SLAAttention nodes
        /// are unwired and pruned, so a server without the pack still renders the job.
        /// </summary>
        public bool UseSla { get; set; } = true;

        /// <summary>The fraction of key blocks SLA skips. Below ~0.60 the kernel is slower than dense.</summary>
        public double SlaSparsity { get; set; } = 0.85;

        /// <summary>
        /// When false — the default — the RTX ×2 super-resolution node is pruned. It is the single largest
        /// allocation in the graph, and with interpolation on it runs over twice as many frames.
        /// </summary>
        public bool RtxUpscale { get; set; }

        /// <summary>Groups the clips of one story so they render in order and can be joined when the last
        /// one lands. Empty for a standalone clip.</summary>
        public string StoryId { get; set; } = string.Empty;

        /// <summary>1-based position of this clip within its story. 1 for a standalone clip.</summary>
        public int ClipIndex { get; set; } = 1;

        /// <summary>How many clips the story was split into. 1 for a standalone clip.</summary>
        public int ClipCount { get; set; } = 1;

        [JsonIgnore]
        public bool IsStoryClip => ClipCount > 1;

        public string? OutputVideoPath { get; set; }

        [JsonIgnore]
        public bool HasCharacter2 => !string.IsNullOrEmpty(Character2SheetPath);

        [JsonIgnore]
        public int KeyframeCount => KeyframePaths.Count;

        [JsonIgnore]
        public string DisplayText
        {
            get
            {
                var cast = HasCharacter2
                    ? $"{Name(Character1SourcePath, Character1SheetPath)} + {Name(Character2SourcePath, Character2SheetPath)}"
                    : Name(Character1SourcePath, Character1SheetPath);
                var keys = KeyframeCount == 0
                    ? " · no keyframe"
                    : $" · {KeyframeCount} keyframe{(KeyframeCount == 1 ? "" : "s")} @ " +
                      string.Join("/", KeyframeSeconds.Select(s => $"{s:0.#}s"));
                var refine = FaceRefine
                    ? $" · face refine {RefineDenoise:0.00}{(RefinePrompt2.Length > 0 ? " ×2" : string.Empty)}"
                    : string.Empty;
                var finish = refine +
                             (Interpolate ? " · FILM ×2" : string.Empty) +
                             (RtxUpscale ? " · RTX ×2" : string.Empty);
                var clip = IsStoryClip ? $" · clip {ClipIndex}/{ClipCount}" : string.Empty;
                return $"{cast}{keys} → {AspectRatio} · {LengthSeconds:0.#}s{clip}{finish}";
            }
        }

        /// <summary>
        /// One line saying what this clip does — the beat, and nothing else.
        ///
        /// <para>The queue row used to bind straight to <see cref="Prompt"/>, which by the time it is
        /// queued is a full assembled prompt: a wardrobe lock, four subject definitions, four retention
        /// lines and an alignment paragraph ahead of the one sentence that differs — and on a chain, every
        /// word of that preamble is identical in every row. Thirty lines per item, the beat buried in the
        /// middle, and rows that looked alike whether or not they actually were.</para>
        /// </summary>
        [JsonIgnore]
        public string PromptPreview => HybridCastPrompt.ActionSummary(Prompt);

        private static string Name(string source, string fallback)
        {
            var path = string.IsNullOrEmpty(source) ? fallback : source;
            return string.IsNullOrEmpty(path) ? "(none)" : Path.GetFileNameWithoutExtension(path);
        }

        private BitmapImage? _thumbnail;
        private bool _thumbnailTried;

        /// <summary>
        /// Small preview for the queue row — the opening keyframe if there is one (it is literally the first
        /// frame of the result), otherwise the scene image, otherwise character 1's sheet. Decoded on first
        /// bind rather than on deserialize.
        /// </summary>
        [JsonIgnore]
        public BitmapImage? QueueThumbnail
        {
            get
            {
                if (_thumbnailTried) return _thumbnail;
                _thumbnailTried = true;

                var source = new[]
                    {
                        KeyframePaths.FirstOrDefault() ?? string.Empty,
                        SceneImagePath,
                        Character1SheetPath,
                    }
                    .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
                if (string.IsNullOrEmpty(source)) return null;

                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(source, UriKind.Absolute);
                    bitmap.DecodePixelHeight = 40;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _thumbnail = bitmap;
                }
                catch { _thumbnail = null; }

                return _thumbnail;
            }
        }
    }
}
