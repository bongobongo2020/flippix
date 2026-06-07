namespace FlipPix.UI.Models
{
    /// <summary>A finished (Stage 2/3) video produced for one selected sample slot.</summary>
    public sealed class SeedHuntResult
    {
        public int Slot { get; init; }
        public string VideoPath { get; init; } = string.Empty;
        public string VideoFileUri { get; init; } = string.Empty;
        public string Info { get; init; } = string.Empty;
        public string Label => $"Sample {Slot}";
    }
}
