using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// Model for story prompt JSON files
    /// </summary>
    public class StoryPromptData
    {
        [JsonPropertyName("CustomPrompt")]
        public string CustomPrompt { get; set; } = string.Empty;

        [JsonPropertyName("Prompts")]
        public List<string> Prompts { get; set; } = new();

        [JsonPropertyName("SavedAt")]
        public DateTime SavedAt { get; set; }

        [JsonPropertyName("Version")]
        public string? Version { get; set; }
    }
}
