using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// A serializable snapshot of one MiniMax Character job: 1–2 character reference images plus a
    /// finished H3 prompt, rendered through workflow/video/h3-minimax/ref2va-turbo-character.json — whose
    /// turbo model chain and RTX ×2 finish are fixed for every item and so are not part of this snapshot.
    ///
    /// <para>Everything the run needs is frozen here at the moment the item is queued, so the tab's
    /// inputs stay free — a new scene image can be loaded and analyzed while an earlier job is still
    /// on the GPU. <see cref="Prompt"/> already carries the reference line for <see cref="HasCharacter2"/>,
    /// because that line has to match the cast this item was queued with, not the one on screen now.</para>
    /// </summary>
    public class MiniMaxCharacterQueueItem : BaseQueueItem
    {
        /// <summary>Character 1 — uploaded as <c>ref_image_0</c> / <c>&lt;Picture 1&gt;</c>. Required.</summary>
        public string Character1Path { get; set; } = string.Empty;

        /// <summary>Character 2 — <c>ref_image_1</c> / <c>&lt;Picture 2&gt;</c>. Empty = single-reference run.</summary>
        public string Character2Path { get; set; } = string.Empty;

        /// <summary>The scene image this prompt was analyzed from. Never uploaded — kept for the
        /// queue thumbnail and so the scene library entry can be filed against it.</summary>
        public string SceneImagePath { get; set; } = string.Empty;

        /// <summary>The full H3 prompt, reference line included.</summary>
        public string Prompt { get; set; } = string.Empty;

        public string AspectRatio { get; set; } = "16:9";
        public double Megapixels { get; set; } = 1.0;
        public double LengthSeconds { get; set; } = 10;

        /// <summary>-1 = pick a fresh random seed when the item runs.</summary>
        public long Seed { get; set; } = -1;

        /// <summary>
        /// Groups the clips of one story so their output files sort together. Empty for a standalone clip.
        /// </summary>
        public string StoryId { get; set; } = string.Empty;

        /// <summary>1-based position of this clip within its story. 1 for a standalone clip.</summary>
        public int ClipIndex { get; set; } = 1;

        /// <summary>How many clips the story was split into. 1 for a standalone clip.</summary>
        public int ClipCount { get; set; } = 1;

        /// <summary>True when this item is one beat of a longer chain rather than a whole video.</summary>
        [JsonIgnore]
        public bool IsStoryClip => ClipCount > 1;

        public string? OutputVideoPath { get; set; }

        [JsonIgnore]
        public bool HasCharacter2 => !string.IsNullOrEmpty(Character2Path);

        [JsonIgnore]
        public string DisplayText
        {
            get
            {
                var cast = HasCharacter2
                    ? $"{Name(Character1Path)} + {Name(Character2Path)}"
                    : Name(Character1Path);
                var clip = IsStoryClip ? $" · clip {ClipIndex}/{ClipCount}" : string.Empty;
                return $"{cast} → {AspectRatio} · {LengthSeconds:0.#}s{clip}";
            }
        }

        private static string Name(string path) =>
            string.IsNullOrEmpty(path) ? "(none)" : Path.GetFileNameWithoutExtension(path);

        private BitmapImage? _thumbnail;
        private bool _thumbnailTried;

        /// <summary>
        /// Small preview for the queue row — the scene image if there is one, otherwise Character 1.
        /// Decoded on first bind rather than on deserialize, so a restored queue never pays for
        /// thumbnails the user has not looked at yet.
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
                    : Character1Path;
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
