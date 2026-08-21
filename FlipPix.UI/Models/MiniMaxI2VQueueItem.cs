using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// A serializable snapshot of one 🌀 MiniMax I2V job: the reference pictures, the Ref2VA prompt, any
    /// continuations, and every setting that shapes the render.
    ///
    /// <para>Everything is frozen at enqueue time, deliberately. The whole point of the queue is that the
    /// form stays live while jobs drain — so a queued item must not read the sliders again when it runs,
    /// or changing the length for the next job would silently rewrite the one already waiting.</para>
    ///
    /// <para>Reference <i>paths</i> are frozen rather than uploaded names: the upload happens when the item
    /// runs, so a queue restored from disk in a later session still uploads against whatever ComfyUI is
    /// live then.</para>
    /// </summary>
    public class MiniMaxI2VQueueItem : BaseQueueItem
    {
        /// <summary>Reference picture paths in slot order — this is the &lt;Picture N&gt; numbering the
        /// prompt was written against, so the order is part of the job, not a detail.</summary>
        public List<string> ReferencePaths { get; set; } = new();

        /// <summary>The base pass's Ref2VA prompt.</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>Continuation prompts, in order. Empty = a single-pass job.</summary>
        public List<string> ContinuationPrompts { get; set; } = new();

        /// <summary>Seconds for each continuation, index-matched to <see cref="ContinuationPrompts"/>.</summary>
        public List<int> ContinuationSeconds { get; set; } = new();

        public string AspectRatio { get; set; } = "16:9 (Widescreen)";
        public double Megapixels { get; set; } = 0.7;
        public int LengthSeconds { get; set; } = 10;

        /// <summary>-1 = pick a fresh random seed when the item runs.</summary>
        public long Seed { get; set; } = -1;

        /// <summary>Frames a continuation re-generates and blends over the pass before it.</summary>
        public int OverlapFrames { get; set; } = 22;

        public bool UseSla { get; set; } = true;
        public double SlaSparsity { get; set; } = 0.85;
        public bool UseSparseAttention { get; set; }
        public bool UseDetailPass { get; set; } = true;
        public bool UseRtxUpscale { get; set; }
        public bool UseAudioEnhancement { get; set; } = true;
        public bool MaxFidelityReferences { get; set; }

        [JsonIgnore]
        public int PassCount => ContinuationPrompts.Count + 1;

        [JsonIgnore]
        public int TotalSeconds => LengthSeconds + ContinuationSeconds.Sum();

        [JsonIgnore]
        public string DisplayText
        {
            get
            {
                var refs = ReferencePaths.Count == 0
                    ? "(no reference)"
                    : Path.GetFileNameWithoutExtension(ReferencePaths[0]) +
                      (ReferencePaths.Count > 1 ? $" +{ReferencePaths.Count - 1}" : string.Empty);
                var passes = PassCount == 1 ? string.Empty : $" · {PassCount} passes";
                var detail = UseDetailPass ? " · detail" : string.Empty;
                var rtx = UseRtxUpscale ? " · RTX ×2" : string.Empty;
                var sla = UseSla ? $" · SLA {SlaSparsity:0.00}" : string.Empty;
                return $"{refs} → {AspectRatio} · {Megapixels:0.0} MP · {TotalSeconds}s{passes}{detail}{rtx}{sla}";
            }
        }

        /// <summary>The prompt's opening line, for the queue row. The full text is on the row's tooltip —
        /// a six-field Ref2VA prompt has its own newlines, and TextTrimming would ellipsize every one of
        /// them separately rather than shortening the block.</summary>
        [JsonIgnore]
        public string PromptPreview
        {
            get
            {
                var body = Prompt.Replace("\r", " ").Replace("\n", " ").Trim();
                while (body.Contains("  ")) body = body.Replace("  ", " ");
                return body.Length <= 140 ? body : body[..140] + "…";
            }
        }

        private BitmapImage? _thumbnail;
        private bool _thumbnailTried;

        /// <summary>
        /// Small preview for the queue row — the first reference picture. Decoded on first bind rather
        /// than on deserialize, so a restored queue never pays for thumbnails nobody has looked at.
        /// </summary>
        [JsonIgnore]
        public BitmapImage? QueueThumbnail
        {
            get
            {
                if (_thumbnailTried) return _thumbnail;
                _thumbnailTried = true;

                var source = ReferencePaths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
                if (source == null) return null;

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
