namespace FlipPix.UI.Models
{
    /// <summary>
    /// A finished joined video — one combination of per-shot chosen seeds, concatenated in timeline
    /// order. Multiple selected seeds across shots produce one result per combination.
    /// </summary>
    public sealed class SeedDirectorResult
    {
        public string Label { get; init; } = string.Empty;
        public string VideoPath { get; init; } = string.Empty;
        public string VideoFileUri { get; init; } = string.Empty;
        public string Info { get; init; } = string.Empty;
    }
}
