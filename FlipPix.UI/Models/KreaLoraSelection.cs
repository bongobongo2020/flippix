using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// One Krea2 LoRA row in the multi-LoRA picker. Each row maps to a numbered slot
    /// (lora_1, lora_2, …) on the rgthree Power Lora Loader (node 17) of the Krea2 realism
    /// workflow. <see cref="LoraName"/> is the file name without extension as shown in the
    /// picker; the ViewModel turns it into the "&lt;subfolder&gt;/&lt;name&gt;.safetensors" path
    /// ComfyUI expects, applying <see cref="Strength"/> as both model/clip strength.
    /// </summary>
    public partial class KreaLoraSelection : ObservableObject
    {
        [ObservableProperty] private string _loraName = string.Empty;

        [ObservableProperty] private double _strength = 1.0;

        public KreaLoraSelection() { }

        public KreaLoraSelection(string loraName, double strength = 1.0)
        {
            _loraName = loraName;
            _strength = strength;
        }

        /// <summary>Plain serializable snapshot stored on queue items.</summary>
        public KreaLoraDto ToDto() => new() { Name = LoraName, Strength = Strength };
    }

    /// <summary>
    /// Persisted form of a <see cref="KreaLoraSelection"/> stored on queue items.
    /// </summary>
    public sealed class KreaLoraDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

        [JsonPropertyName("strength")] public double Strength { get; set; } = 1.0;

        public KreaLoraSelection ToSelection() => new(Name, Strength);
    }
}
