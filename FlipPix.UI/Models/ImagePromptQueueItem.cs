using System.Collections.Generic;
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

        /// <summary>Krea2 LoRA stack (name + strength), applied in order to Power Lora Loader slots.</summary>
        public List<KreaLoraDto> SelectedKreaLoras { get; set; } = new();

        /// <summary>
        /// Legacy single-LoRA field from older saved queues. The getter returns null so it is never
        /// written back (kept out of new files); the setter runs on deserialize and back-fills
        /// <see cref="SelectedKreaLoras"/> when an old item carries it and no list.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SelectedKreaLora
        {
            get => null;
            set
            {
                if (!string.IsNullOrEmpty(value) && SelectedKreaLoras.Count == 0)
                    SelectedKreaLoras.Add(new KreaLoraDto { Name = value, Strength = 1.0 });
            }
        }

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
            TextGeneratorWorkflow.ZimageBase => "ZB",
            _ => "?"
        };

        [JsonIgnore]
        public string WorkflowBadgeColor => SelectedWorkflow switch
        {
            TextGeneratorWorkflow.Zimage => "#6366F1",    // Purple
            TextGeneratorWorkflow.Qwen2512 => "#10B981",  // Green
            TextGeneratorWorkflow.Klien => "#F59E0B",     // Orange
            TextGeneratorWorkflow.Anima => "#EC4899",     // Pink
            TextGeneratorWorkflow.ZimageBase => "#8B5CF6",  // Violet
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
