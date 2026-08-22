using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// A serializable snapshot of one 🌀🎯 MiniMax FFLF job: the keyframe chain, the FL2VA prompt for
    /// each clip between two of those keyframes, and every setting that shapes the render.
    ///
    /// <para>Everything is frozen at enqueue time, deliberately. The whole point of the queue is that the
    /// form stays live while jobs drain — so a queued item must not read the sliders again when it runs,
    /// or changing the length for the next job would silently rewrite the one already waiting.</para>
    ///
    /// <para>Keyframe <i>paths</i> are frozen rather than uploaded names: the upload happens when the item
    /// runs, so a queue restored from disk in a later session still uploads against whatever ComfyUI is
    /// live then.</para>
    /// </summary>
    public class MiniMaxFflfQueueItem : BaseQueueItem
    {
        /// <summary>The picture at 0.00s.</summary>
        public string OpeningFramePath { get; set; } = string.Empty;

        /// <summary>The still each clip has to arrive at, in clip order. One entry per clip.</summary>
        public List<string> EndFramePaths { get; set; } = new();

        /// <summary>The FL2VA prompt for each clip, index-matched to <see cref="EndFramePaths"/>.</summary>
        public List<string> Prompts { get; set; } = new();

        /// <summary>Seconds for each clip, index-matched to <see cref="EndFramePaths"/>.</summary>
        public List<int> Seconds { get; set; } = new();

        public string AspectRatio { get; set; } = "16:9 (Widescreen)";
        public double Megapixels { get; set; } = 0.7;

        /// <summary>-1 = pick a fresh random seed when the item runs.</summary>
        public long Seed { get; set; } = -1;

        /// <summary>Frames a continuing clip re-generates and blends over the clip before it.</summary>
        public int OverlapFrames { get; set; } = 22;

        /// <summary>
        /// Identifies the folder run this take belongs to, or null for a one-off chain. Every take queued
        /// off one loaded folder shares the id, which is what lets the drain loop FFmpeg-join them into a
        /// single video once the last of them lands. See
        /// <c>MiniMaxFflfViewModel.CompleteFolderRunAsync</c>.
        /// </summary>
        public string? FolderRunId { get; set; }

        /// <summary>The folder the run's keyframes came from, for naming the joined file.</summary>
        public string FolderName { get; set; } = string.Empty;

        /// <summary>This take's position in the folder run, 1-based. The join order.</summary>
        public int TakeNumber { get; set; }

        /// <summary>Takes the folder was worth when this one was queued. Reporting only — it says how
        /// much of the folder a run covers when queueing stopped short of the end.</summary>
        public int TakeCount { get; set; }

        public bool UseSparseAttention { get; set; }
        public bool UseDetailPass { get; set; } = true;
        public bool UseRtxUpscale { get; set; }
        public bool UseAudioEnhancement { get; set; } = true;

        /// <summary>Clips in the chain — the base pass plus one loop iteration each after it.</summary>
        [JsonIgnore]
        public int PassCount => Math.Max(1, Prompts.Count);

        /// <summary>Continuing clips, i.e. loop iterations.</summary>
        [JsonIgnore]
        public int ExtensionCount => Math.Max(0, PassCount - 1);

        [JsonIgnore]
        public int TotalSeconds => Seconds.Sum();

        [JsonIgnore]
        public string DisplayText
        {
            get
            {
                var opening = string.IsNullOrEmpty(OpeningFramePath)
                    ? "(no opening frame)"
                    : Path.GetFileNameWithoutExtension(OpeningFramePath);
                var chain = $"{EndFramePaths.Count + 1} keyframes";
                var passes = PassCount == 1 ? string.Empty : $" · {PassCount} clips";
                var detail = UseDetailPass ? " · detail" : string.Empty;
                var rtx = UseRtxUpscale ? " · RTX ×2" : string.Empty;
                // Which take of the folder run this is — the queue rows of one run otherwise differ only
                // by their opening frame's filename.
                var take = TakeNumber > 0 ? $"take {TakeNumber}"
                                            + (TakeCount > 0 ? $"/{TakeCount}" : string.Empty) + " · "
                                          : string.Empty;
                return $"{take}{opening} → {chain} · {AspectRatio} · {Megapixels:0.0} MP · {TotalSeconds}s{passes}{detail}{rtx}";
            }
        }

        /// <summary>The first clip's prompt, collapsed to one line for the queue row. The full text is on
        /// the row's tooltip — an FL2VA prompt carries its own newlines, and TextTrimming would ellipsize
        /// every one of them separately rather than shortening the block.</summary>
        [JsonIgnore]
        public string PromptPreview
        {
            get
            {
                var body = (Prompts.FirstOrDefault() ?? string.Empty)
                    .Replace("\r", " ").Replace("\n", " ").Trim();
                while (body.Contains("  ")) body = body.Replace("  ", " ");
                return body.Length <= 140 ? body : body[..140] + "…";
            }
        }

        /// <summary>The whole chain's prompts, for the queue row's tooltip.</summary>
        [JsonIgnore]
        public string Prompt => string.Join("\n\n────────\n\n",
            Prompts.Select((p, i) => $"CLIP {i + 1} · {Seconds.ElementAtOrDefault(i)}s\n{p}"));

        private BitmapImage? _thumbnail;
        private bool _thumbnailTried;

        /// <summary>
        /// Small preview for the queue row — the opening frame. Decoded on first bind rather than on
        /// deserialize, so a restored queue never pays for thumbnails nobody has looked at.
        /// </summary>
        [JsonIgnore]
        public BitmapImage? QueueThumbnail
        {
            get
            {
                if (_thumbnailTried) return _thumbnail;
                _thumbnailTried = true;

                var source = new[] { OpeningFramePath }.Concat(EndFramePaths)
                    .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
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
