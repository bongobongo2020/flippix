namespace FlipPix.UI.Models
{
    /// <summary>A finished (Stage 2/3) video produced for one selected sample slot.</summary>
    public sealed class SeedHuntResult
    {
        public int Slot { get; init; }

        /// <summary>1-based batch pair this result came from, or 0 for the single-pair flow.</summary>
        public int PairIndex { get; init; }

        public string VideoPath { get; init; } = string.Empty;
        public string VideoFileUri { get; init; } = string.Empty;
        public string Info { get; init; } = string.Empty;

        /// <summary>Optional explicit label (e.g. the auto-joined video); falls back to slot/pair.</summary>
        public string? LabelOverride { get; init; }

        public string Label => LabelOverride
            ?? (PairIndex > 0 ? $"Pair {PairIndex} · Sample {Slot}" : $"Sample {Slot}");
    }
}
