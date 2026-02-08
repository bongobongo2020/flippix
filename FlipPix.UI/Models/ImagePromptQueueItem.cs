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

        // HasOutputImage is unique to this model
        [JsonIgnore]
        public bool HasOutputImage => !string.IsNullOrEmpty(OutputImagePath);

        [JsonIgnore]
        public string DisplayPrompt => Prompt.Length > 50 ? Prompt.Substring(0, 47) + "..." : Prompt;
    }
}
