using System.Collections.Generic;
using System.Linq;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// A serializable snapshot of an LTX Director timeline + global settings, queued for generation.
    /// </summary>
    public class LtxDirectorQueueItem : BaseQueueItem
    {
        public List<LtxDirectorSegment> Segments { get; set; } = new();

        // ── Global LTXDirector settings ─────────────────────────────────────
        public string GlobalPrompt { get; set; } = string.Empty;
        public string NegativePrompt { get; set; } = string.Empty;
        public int FrameRate { get; set; } = 24;
        public double GuideStrength { get; set; } = 1.0;
        public double Epsilon { get; set; } = 0.001;
        public int ImgCompression { get; set; } = 18;
        public int Steps { get; set; } = 8;
        public string Resolution { get; set; } = "1080p";
        public string Orientation { get; set; } = "Landscape";
        public bool UseCustomAudio { get; set; } = false;
        public long Seed { get; set; } = -1;

        public string? OutputVideoPath { get; set; }

        public int TotalFrames => Segments.Sum(s => (int)System.Math.Round(s.DurationSeconds * FrameRate));
        public double TotalSeconds => Segments.Sum(s => s.DurationSeconds);

        public string DisplayText =>
            Segments.Count == 0
                ? "(empty timeline)"
                : $"{Segments.Count} shot{(Segments.Count == 1 ? "" : "s")} · {TotalSeconds:0.0}s";

        /// <summary>First shot's prompt, for the queue row subtitle.</summary>
        public string Prompt => Segments.FirstOrDefault()?.Prompt ?? string.Empty;
    }

    public class LtxDirectorSegment
    {
        public string ImagePath { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public double DurationSeconds { get; set; } = 3.0;
    }
}
