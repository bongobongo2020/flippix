using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// A serializable snapshot of one H3 Chain job: 1–2 reference images, a soundtrack, and the whole
    /// ordered list of segment prompts, rendered through
    /// <c>workflow/video/h3-minimax/h3-chain-ref2va.json</c>.
    ///
    /// <para>Unlike the other MiniMax queues, one item is <b>the entire video</b>, not one clip of it.
    /// The chain nodes loop inside ComfyUI — a single submission renders every segment, carries the tail
    /// of each into the head of the next, and muxes the finished take against the soundtrack — so the
    /// segment list travels with the item rather than being split into one job per segment.</para>
    ///
    /// <para><see cref="RunName"/> is the checkpoint identity ComfyUI files this chain's segments under.
    /// It is frozen at queue time and never regenerated, which is what makes a stopped or crashed run
    /// resumable: re-running an item with the same run name and the same
    /// <see cref="GenerationFingerprint"/> picks the accepted segments back up instead of re-rendering
    /// them.</para>
    /// </summary>
    public class H3ChainQueueItem : BaseQueueItem
    {
        /// <summary>Reference 1 — <c>ref_image_0</c> / <c>&lt;Picture 1&gt;</c>. Facial identity. Required.</summary>
        public string Reference1Path { get; set; } = string.Empty;

        /// <summary>Reference 2 — <c>ref_image_1</c> / <c>&lt;Picture 2&gt;</c>. Body, wardrobe, proportions.
        /// Empty means the chain runs with a single reference frame.</summary>
        public string Reference2Path { get; set; } = string.Empty;

        /// <summary>The soundtrack the whole chain is locked to — sliced per segment by the chain nodes
        /// and muxed back over the assembled take. Required.</summary>
        public string AudioPath { get; set; } = string.Empty;

        /// <summary>One finished H3 plan per segment, in playback order.</summary>
        public List<string> SegmentPrompts { get; set; } = new();

        /// <summary>Per-segment length in seconds. The chain node rounds each one up to H3's 17k+5
        /// frame grid at 24 fps.</summary>
        public double SegmentSeconds { get; set; } = 15;

        public int Width { get; set; } = 960;
        public int Height { get; set; } = 544;

        /// <summary>Sampler steps per segment. The workflow ships the 4-step turbo LoRA, so this is
        /// small by design.</summary>
        public int Steps { get; set; } = 5;

        /// <summary>Previous-segment frames carried into each continuation, then trimmed back off.
        /// One of the values <c>MiniMaxH3ChainPlan</c> accepts: 1, 5, 22 or 39.</summary>
        public int ContextLength { get; set; } = 22;

        /// <summary>-1 = derive a fresh random base seed when the item runs.</summary>
        public long BaseSeed { get; set; } = -1;

        /// <summary>Checkpoint folder name for this chain, under ComfyUI's output. Stable across
        /// retries of the same item, which is what allows a partly-rendered chain to resume.</summary>
        public string RunName { get; set; } = string.Empty;

        /// <summary>Guards the checkpoint: the chain refuses to resume segments rendered under a
        /// different model/reference/canvas configuration.</summary>
        public string GenerationFingerprint { get; set; } = string.Empty;

        /// <summary>Segment to start (or resume) at. 1 for a fresh run; raised by
        /// <c>Resume</c> so an interrupted chain picks up where its checkpoint left off.</summary>
        public int StartSegment { get; set; } = 1;

        /// <summary>True when the soundtrack was looped/trimmed to cover the requested running time
        /// rather than used as-is. Recorded so the log can say what the audio actually was.</summary>
        public bool AudioLooped { get; set; }

        /// <summary>
        /// The highest segment the running chain was seen to <b>start</b>, read off the executing node id
        /// and persisted with the item. It is what lets Resume work when the ComfyUI host's output folder
        /// is not visible from this machine, so the segment files cannot be counted directly.
        /// </summary>
        public int LastReportedSegment { get; set; }

        public string? OutputVideoPath { get; set; }

        [JsonIgnore]
        public bool HasReference2 => !string.IsNullOrEmpty(Reference2Path);

        [JsonIgnore]
        public int SegmentCount => SegmentPrompts?.Count ?? 0;

        /// <summary>Nominal running time — segment count × segment length. The rendered file is a little
        /// longer, because each segment is rounded up onto H3's frame grid.</summary>
        [JsonIgnore]
        public double TotalSeconds => SegmentCount * SegmentSeconds;

        [JsonIgnore]
        public string DisplayText
        {
            get
            {
                var cast = HasReference2
                    ? $"{Name(Reference1Path)} + {Name(Reference2Path)}"
                    : Name(Reference1Path);
                var resume = StartSegment > 1 ? $" · resuming at {StartSegment}" : string.Empty;
                return $"{cast} ♪ {Name(AudioPath)} → {SegmentCount} × {SegmentSeconds:0.#}s " +
                       $"= {TotalSeconds:0.#}s · {Width}×{Height}{resume}";
            }
        }

        /// <summary>First line of segment 1's plan — enough to tell two queued chains apart at a glance.</summary>
        [JsonIgnore]
        public string PromptPreview
        {
            get
            {
                if (SegmentPrompts == null || SegmentPrompts.Count == 0) return string.Empty;
                var first = SegmentPrompts[0] ?? string.Empty;
                var summary = first.IndexOf("summary:", StringComparison.OrdinalIgnoreCase);
                if (summary >= 0) first = first[(summary + "summary:".Length)..];
                return first.Replace('\r', ' ').Replace('\n', ' ').Trim();
            }
        }

        private static string Name(string path) =>
            string.IsNullOrEmpty(path) ? "(none)" : Path.GetFileNameWithoutExtension(path);

        private BitmapImage? _thumbnail;
        private bool _thumbnailTried;

        /// <summary>
        /// Small preview for the queue row. Decoded on first bind rather than on deserialize, so a
        /// restored queue never pays for thumbnails the user has not looked at yet.
        /// </summary>
        [JsonIgnore]
        public BitmapImage? QueueThumbnail
        {
            get
            {
                if (_thumbnailTried) return _thumbnail;
                _thumbnailTried = true;

                if (string.IsNullOrEmpty(Reference1Path) || !File.Exists(Reference1Path)) return null;

                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(Reference1Path, UriKind.Absolute);
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
