using System.Collections.Generic;
using System.Text.Json.Serialization;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// Model for image analyzer queue items
    /// </summary>
    public class ImageAnalyzerQueueItem : BaseQueueItem
    {
        public string SourceImagePath { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public TextGeneratorWorkflow SelectedWorkflow { get; set; } = TextGeneratorWorkflow.Zimage;
        public int SelectedStyleIndex { get; set; } = 0;
        public string StyleName { get; set; } = string.Empty;
        public int AspectRatioIndex { get; set; } = 0; // Default to Portrait
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
        /// written back; the setter runs on deserialize and back-fills <see cref="SelectedKreaLoras"/>
        /// when an old item carries it and no list.
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

        public string NegativePrompt { get; set; } = string.Empty;
        public int Width { get; set; } = 944;
        public int Height { get; set; } = 1408;

        // Override StatusDisplay to include "Cancelled" status
        [JsonIgnore]
        public new string StatusDisplay => Status switch
        {
            "Pending" => "⏳ Pending",
            "Processing" => "⚙️ Processing",
            "Completed" => "✅ Completed",
            "Failed" => "❌ Failed",
            "Cancelled" => "⏹️ Cancelled",
            _ => Status
        };

        // Override StatusColor to include "Cancelled" color
        [JsonIgnore]
        public new string StatusColor => Status switch
        {
            "Pending" => "#6C757D",
            "Processing" => "#FFA500",
            "Completed" => "#28A745",
            "Failed" => "#DC3545",
            "Cancelled" => "#FFC107",
            _ => "#000000"
        };

        [JsonIgnore]
        public string DisplayPrompt => Prompt.Length > 50 ? Prompt.Substring(0, 47) + "..." : Prompt;

        // UI Helper Properties
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
            TextGeneratorWorkflow.Zimage => "#6366F1",
            TextGeneratorWorkflow.Qwen2512 => "#10B981",
            TextGeneratorWorkflow.Klien => "#F59E0B",
            TextGeneratorWorkflow.Anima => "#EC4899",
            _ => "#6C757D"
        };

        [JsonIgnore]
        public System.Windows.Visibility StyleNameVisibility =>
            SelectedWorkflow == TextGeneratorWorkflow.Zimage ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        [JsonIgnore]
        public string AspectRatioDisplay => AspectRatioIndex switch
        {
            0 => "Landscape",
            1 => "Portrait",
            2 => "Square",
            _ => "?"
        };
    }
}
