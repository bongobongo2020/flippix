using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// A serializable snapshot of one H3 Cast job: the 1–2 <b>character sheets</b> that go to MiniMax H3 as
    /// reference frames, plus the finished H3 prompt, rendered through
    /// <c>workflow/video/h3-minimax/h3facerefiner.json</c>.
    ///
    /// <para>What is frozen here is the <i>sheet</i>, not the source photo: the Qwen-Image-Edit-2511 pass
    /// that builds a sheet runs before the job is queued, so a queued item never depends on the sheet builder
    /// running again — and the source paths are kept only so the queue row can show a face.</para>
    /// </summary>
    public class H3CastQueueItem : BaseQueueItem
    {
        /// <summary>Character 1's sheet. Kept for the queue thumbnail and the row label; what is uploaded is
        /// <see cref="Character1PanelPaths"/>. Required.</summary>
        public string Character1SheetPath { get; set; } = string.Empty;

        /// <summary>Character 2's sheet. Empty = single-character run.</summary>
        public string Character2SheetPath { get; set; } = string.Empty;

        /// <summary>
        /// Character 1's sheet cut into single-view panels, left to right — the images actually uploaded and
        /// wired to <c>ref_images.ref_image_0…</c>, so <c>&lt;Picture 1&gt;</c> onwards resolve to them.
        ///
        /// <para>Frozen at queue time rather than recomputed at submit time because the prompt's picture
        /// numbering was written from this count. A queue file written before panels existed deserializes to
        /// an empty list, which the tab reads as "split the sheet now" — the old items still run.</para>
        /// </summary>
        public List<string> Character1PanelPaths { get; set; } = new();

        /// <summary>Character 2's panels, continuing the numbering after character 1's.</summary>
        public List<string> Character2PanelPaths { get; set; } = new();

        /// <summary>The photo character 1's sheet was built from. Never uploaded — kept for the queue thumbnail.</summary>
        public string Character1SourcePath { get; set; } = string.Empty;

        /// <summary>The photo character 2's sheet was built from. Never uploaded.</summary>
        public string Character2SourcePath { get; set; } = string.Empty;

        /// <summary>The scene image the prompt was analyzed from. Never uploaded.</summary>
        public string SceneImagePath { get; set; } = string.Empty;

        /// <summary>The full H3 prompt, sheet reference line included.</summary>
        public string Prompt { get; set; } = string.Empty;

        public string AspectRatio { get; set; } = "16:9 (Widescreen)";
        public double Megapixels { get; set; } = 1.0;
        public double LengthSeconds { get; set; } = 10;

        /// <summary>-1 = pick a fresh random seed when the item runs.</summary>
        public long Seed { get; set; } = -1;

        /// <summary>When false the face-refine second pass is pruned out and the base H3 frames are saved as-is.</summary>
        public bool FaceRefine { get; set; } = true;

        /// <summary>Denoise of the face-refine pass — node 106. How far the cropped faces are allowed to move.</summary>
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
        /// One refine pass per character rather than one for the whole cast.
        ///
        /// <para><c>H3FaceTrackCrop</c> holds a single subject, so the old single pass refined whoever was
        /// largest and left the other character's face exactly as the base pass rendered it — while being
        /// shown both cast members' photographs, which gave it nothing to say about which face it had. Each
        /// character now gets a pass tracked by their own face close-up and conditioned on their own panels.</para>
        ///
        /// <para><b>Default false</b>: an item queued before this existed carries a prompt written for the
        /// whole cast, and running that prompt against one character's panels would number pictures the pass
        /// never receives. Re-queue such an item to get the second pass.</para>
        /// </summary>
        public bool PerCharacterRefine { get; set; }

        /// <summary>"man" / "woman" for character 1, frozen at queue time — the refine passes rebuild their
        /// own reference line and it names the cast the same way the clip's own prompt did.</summary>
        public string Sex1 { get; set; } = string.Empty;

        /// <summary>"man" / "woman" for character 2.</summary>
        public string Sex2 { get; set; } = string.Empty;

        /// <summary>Whether the sheets were built wearing the locked wardrobe — flips the reference line
        /// between disowning the references' clothing and pointing at it.</summary>
        public bool SheetsShowWardrobe { get; set; }

        /// <summary>
        /// The draft → 2× latent upscale → 3-step finish scheme on the base pass. On by default. The cast's
        /// panels are unaffected either way: they are encoded at the finished canvas.
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
        /// Sol-Attn (node 53), which this workflow shipped with always on. <b>Off by default</b>: SLA
        /// supersedes it for H3, and an item queued before this field existed deserializes to off, which is
        /// the right way round now that SLA carries the speedup.
        /// </summary>
        public bool UseSparseAttention { get; set; }

        /// <summary>
        /// When false — the default — the RTX ×2 super-resolution node is pruned and the frames are muxed at
        /// the H3 canvas. It is the single largest allocation in the graph (a whole ×2 frame stack, held at
        /// once, at the very end of a run), so it is opt-in rather than opt-out. The default also decides
        /// what a queue item written before this field existed deserializes to, which is the safe way round.
        /// </summary>
        public bool RtxUpscale { get; set; }

        /// <summary>
        /// Groups the clips of one story so they render in order, sort together on disk and can be joined
        /// when the last one lands. Empty for a standalone clip.
        /// </summary>
        public string StoryId { get; set; } = string.Empty;

        /// <summary>1-based position of this clip within its story. 1 for a standalone clip.</summary>
        public int ClipIndex { get; set; } = 1;

        /// <summary>How many clips the story was split into. 1 for a standalone clip.</summary>
        public int ClipCount { get; set; } = 1;

        /// <summary>True when this item is one beat of a longer chain rather than the whole video.</summary>
        [JsonIgnore]
        public bool IsStoryClip => ClipCount > 1;

        public string? OutputVideoPath { get; set; }

        [JsonIgnore]
        public bool HasCharacter2 => !string.IsNullOrEmpty(Character2SheetPath);

        [JsonIgnore]
        public string DisplayText
        {
            get
            {
                var cast = HasCharacter2
                    ? $"{Name(Character1SourcePath, Character1SheetPath)} + {Name(Character2SourcePath, Character2SheetPath)}"
                    : Name(Character1SourcePath, Character1SheetPath);
                var refine = FaceRefine ? $" · face refine {RefineDenoise:0.00}" : " · no refine";
                var rtx = RtxUpscale ? " · RTX ×2" : string.Empty;
                var clip = IsStoryClip ? $" · clip {ClipIndex}/{ClipCount}" : string.Empty;
                return $"{cast} → {AspectRatio} · {LengthSeconds:0.#}s{clip}{refine}{rtx}";
            }
        }

        private static string Name(string source, string fallback)
        {
            var path = string.IsNullOrEmpty(source) ? fallback : source;
            return string.IsNullOrEmpty(path) ? "(none)" : Path.GetFileNameWithoutExtension(path);
        }

        private BitmapImage? _thumbnail;
        private bool _thumbnailTried;

        /// <summary>
        /// Small preview for the queue row — the scene image if there is one, otherwise character 1's sheet.
        /// Decoded on first bind rather than on deserialize, so a restored queue never pays for thumbnails
        /// the user has not looked at yet.
        /// </summary>
        [JsonIgnore]
        public BitmapImage? QueueThumbnail
        {
            get
            {
                if (_thumbnailTried) return _thumbnail;
                _thumbnailTried = true;

                var source = !string.IsNullOrEmpty(SceneImagePath) && File.Exists(SceneImagePath)
                    ? SceneImagePath
                    : Character1SheetPath;
                if (string.IsNullOrEmpty(source) || !File.Exists(source)) return null;

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
