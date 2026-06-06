using System.Text.Json.Serialization;

namespace FlipPix.UI.Models
{
    public class IdeogramQueueItem : BaseQueueItem
    {
        public string InputImagePath { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string AspectRatio { get; set; } = "1:1";
        public string LlmModel { get; set; } = string.Empty;

        [JsonIgnore]
        public string DisplayPrompt => Prompt.Length > 60 ? Prompt.Substring(0, 57) + "..." : Prompt;

        [JsonIgnore]
        public string RatioDisplay => AspectRatio;
    }
}
