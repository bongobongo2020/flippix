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

        /// <summary>When true the PiD 4K upscale path runs and the 4K output is retrieved.</summary>
        public bool Generate4K { get; set; } = true;

        public string LlmModel { get; set; } = string.Empty;

        [JsonIgnore]
        public string DisplayPrompt => Prompt.Length > 60 ? Prompt.Substring(0, 57) + "..." : Prompt;

        [JsonIgnore]
        public string RatioDisplay => Generate4K ? $"{AspectRatio} · 4K" : AspectRatio;
    }
}
