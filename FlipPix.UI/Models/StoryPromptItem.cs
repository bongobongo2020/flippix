using System.Text.Json.Serialization;

namespace FlipPix.UI.Models
{
    public class StoryPromptItem : BaseQueueItem
    {
        public int Index { get; set; }  // The prompt index
        public string Prompt { get; set; } = string.Empty;
        public string InputImagePath { get; set; } = string.Empty;

        // Settings snapshot (captured when prompt is added to queue)
        public string StyleName { get; set; } = string.Empty;
        public string StyleWorkflowFile { get; set; } = string.Empty;
        public bool LoraEnabled { get; set; }
        public string SelectedLora { get; set; } = string.Empty;
        public double LoraStrengthModel { get; set; } = 1.0;
        public double LoraStrengthClip { get; set; } = 1.0;
        public string SelectedStyle { get; set; } = "Phone Photo";
        public bool SpicyContentEnabled { get; set; }
        public string NegativePrompt { get; set; } = string.Empty;
        public string SelectedOrientation { get; set; } = "Portrait (944x1408)";
    }
}
