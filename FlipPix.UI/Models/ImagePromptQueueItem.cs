using System.Text.Json.Serialization;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// Model for image generator prompt queue items
    /// </summary>
    public class ImagePromptQueueItem : BaseQueueItem
    {
        public string Prompt { get; set; } = string.Empty;
        public int AspectRatioIndex { get; set; } = 0;
        public int Steps { get; set; } = 9;
        public double Cfg { get; set; } = 1.0;
        public long Seed { get; set; } = 0;
        public double Denoise { get; set; } = 1.0;
        public bool LoraEnabled { get; set; } = false;
        public string SelectedLora { get; set; } = string.Empty;
        public TextGeneratorWorkflow SelectedWorkflow { get; set; } = TextGeneratorWorkflow.Zimage;

        // Style tracking (for Zimage ZStyle workflows)
        public int SelectedStyleIndex { get; set; } = 0;
        public string StyleName { get; set; } = string.Empty;

        [JsonIgnore]
        public string DisplayPrompt => Prompt.Length > 50 ? Prompt.Substring(0, 47) + "..." : Prompt;

        // Workflow badge (Z/Q/K) — matches ImageAnalyzerQueueItem pattern
        [JsonIgnore]
        public string WorkflowBadge => SelectedWorkflow switch
        {
            TextGeneratorWorkflow.Zimage => "Z",
            TextGeneratorWorkflow.Qwen2512 => "Q",
            TextGeneratorWorkflow.Klien => "K",
            TextGeneratorWorkflow.Anima => "A",
            _ => "?"
        };

        [JsonIgnore]
        public string WorkflowBadgeColor => SelectedWorkflow switch
        {
            TextGeneratorWorkflow.Zimage => "#6366F1",    // Purple
            TextGeneratorWorkflow.Qwen2512 => "#10B981",  // Green
            TextGeneratorWorkflow.Klien => "#F59E0B",     // Orange
            TextGeneratorWorkflow.Anima => "#EC4899",     // Pink
            _ => "#6C757D"
        };

        // Aspect ratio display
        [JsonIgnore]
        public string AspectRatioDisplay => AspectRatioIndex switch
        {
            0 => "Landscape",
            1 => "Portrait",
            2 => "Square",
            _ => "?"
        };

        // Style name visibility (only show for Zimage)
        [JsonIgnore]
        public System.Windows.Visibility StyleNameVisibility =>
            SelectedWorkflow == TextGeneratorWorkflow.Zimage
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
    }
}
