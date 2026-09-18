using System.Text.Json.Serialization;

namespace FlipPix.UI.Models
{
    public class IdeogramQueueItem : BaseQueueItem
    {
        /// <summary>Optional reference image used only for LLM analysis (the workflow is text→image).</summary>
        public string InputImagePath { get; set; } = string.Empty;

        /// <summary>High-level scene description (Ideogram4PromptBuilderKJ.high_level_description).</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>Normalized elements_data JSON for the composition regions (may be empty → full-frame).</summary>
        public string RegionsJson { get; set; } = string.Empty;

        /// <summary>
        /// Full scene document (high_level_description + style_description +
        /// compositional_deconstruction) fed to Ideogram4PromptBuilderKJ's import_json.
        /// Empty on items queued before the ideogram4 workflow switch — those fall back
        /// to the node's widget inputs.
        /// </summary>
        public string ImportJson { get; set; } = string.Empty;

        /// <summary>"Square" | "Widescreen" | "Portrait".</summary>
        public string AspectRatio { get; set; } = "Square";

        // ── Enriched style fields fed straight into Ideogram4PromptBuilderKJ (node 185) ──
        /// <summary>Setting / background description (node 185 "background").</summary>
        public string Background { get; set; } = string.Empty;

        /// <summary>Style bucket, normally "photo" (node 185 "style").</summary>
        public string Style { get; set; } = "photo";

        /// <summary>Photographic / lens detail (kept for the "photo" style bucket).</summary>
        public string StylePhoto { get; set; } = string.Empty;

        /// <summary>Art-style description (node 185 "style.art_style" — the bucket ideogram4-instant.json is wired to).</summary>
        public string ArtStyle { get; set; } = string.Empty;

        /// <summary>Mood / aesthetic keywords (node 185 "aesthetics").</summary>
        public string Aesthetics { get; set; } = string.Empty;

        /// <summary>Lighting description (node 185 "lighting").</summary>
        public string Lighting { get; set; } = string.Empty;

        /// <summary>Medium, e.g. "photograph" (node 185 "medium").</summary>
        public string Medium { get; set; } = string.Empty;

        /// <summary>Overall palette as a JSON array of hex strings (node 185 "style_palette_data").</summary>
        public string StylePaletteJson { get; set; } = string.Empty;

        /// <summary>When false the enriched style detail is dropped at build time (only the high-level prompt + regions drive the image).</summary>
        public bool UseEnrichedStyle { get; set; } = true;

        /// <summary>Output resolution budget in megapixels, e.g. "2.0". The Instant graph is single-pass, so this is the saved size.</summary>
        public string Megapixel { get; set; } = "2.0";

        public string LlmModel { get; set; } = string.Empty;

        [JsonIgnore]
        public string DisplayPrompt => Prompt.Length > 60 ? Prompt.Substring(0, 57) + "..." : Prompt;

        [JsonIgnore]
        public string RatioDisplay => $"{AspectRatio} · {Megapixel} MP";
    }
}
