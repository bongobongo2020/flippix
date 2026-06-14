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

        /// <summary>"Square" | "Widescreen" | "Portrait".</summary>
        public string AspectRatio { get; set; } = "Square";

        // ── Enriched style fields fed straight into Ideogram4PromptBuilderKJ (node 105) ──
        /// <summary>Setting / background description (node 105 "background").</summary>
        public string Background { get; set; } = string.Empty;

        /// <summary>Style bucket, normally "photo" (node 105 "style").</summary>
        public string Style { get; set; } = "photo";

        /// <summary>Photographic / lens detail (node 105 "style.photo").</summary>
        public string StylePhoto { get; set; } = string.Empty;

        /// <summary>Mood / aesthetic keywords (node 105 "aesthetics").</summary>
        public string Aesthetics { get; set; } = string.Empty;

        /// <summary>Lighting description (node 105 "lighting").</summary>
        public string Lighting { get; set; } = string.Empty;

        /// <summary>Medium, e.g. "photograph" (node 105 "medium").</summary>
        public string Medium { get; set; } = string.Empty;

        /// <summary>Overall palette as a JSON array of hex strings (node 105 "style_palette_data").</summary>
        public string StylePaletteJson { get; set; } = string.Empty;

        /// <summary>When false the enriched style detail is dropped at build time (only the high-level prompt + regions drive the image).</summary>
        public bool UseEnrichedStyle { get; set; } = true;

        /// <summary>When true the PiD 4K upscale path runs and the 4K output is retrieved.</summary>
        public bool Generate4K { get; set; } = true;

        public string LlmModel { get; set; } = string.Empty;

        [JsonIgnore]
        public string DisplayPrompt => Prompt.Length > 60 ? Prompt.Substring(0, 57) + "..." : Prompt;

        [JsonIgnore]
        public string RatioDisplay => Generate4K ? $"{AspectRatio} · 4K" : AspectRatio;
    }
}
