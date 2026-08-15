using System;
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
        /// <summary>Character 1's sheet — uploaded as <c>ref_image_0</c> / <c>&lt;Picture 1&gt;</c>. Required.</summary>
        public string Character1SheetPath { get; set; } = string.Empty;

        /// <summary>Character 2's sheet — <c>ref_image_1</c> / <c>&lt;Picture 2&gt;</c>. Empty = single-reference run.</summary>
        public string Character2SheetPath { get; set; } = string.Empty;

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
