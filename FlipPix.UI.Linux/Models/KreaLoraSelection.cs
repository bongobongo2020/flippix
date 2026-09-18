using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Linux.Models
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

        /// <summary>
        /// Word prepended to the positive prompt at submit time so the LoRA actually fires
        /// (e.g. "Famegrid, casual smartphone photo of a woman…"). Seeded from the file name, and
        /// editable — clear it to prepend nothing for this row. Picking a different LoRA rewrites it:
        /// the word belongs to the LoRA, not to the row, and
        /// <see cref="Services.KreaLoraTriggerTracker"/> is what decides it (a word the user saved for
        /// that LoRA, else the derived guess).
        /// </summary>
        [ObservableProperty] private string _triggerWord = string.Empty;

        public KreaLoraSelection() { }

        public KreaLoraSelection(string loraName, double strength = 1.0, string? triggerWord = null)
        {
            _loraName = loraName;
            _strength = strength;
            _triggerWord = triggerWord ?? DeriveTriggerWord(loraName);
        }

        // File-name noise that is never part of a trigger word.
        private static readonly string[] NoiseTokens =
            { "krea", "krea2", "comfy", "lora", "loras", "safetensors", "final", "epoch" };

        /// <summary>
        /// Best guess at a LoRA's trigger word from its file name, which is usually the name
        /// itself with the packaging stripped: "Famegrid-Natural-V1-Krea-2" ⇒ "Famegrid Natural",
        /// "nabila_krea2_000001250" ⇒ "Nabila", "bold-clay-toy-render-comfy" ⇒ "Bold clay toy render".
        /// A guess, not gospel — the row's trigger field is editable for the ones it gets wrong.
        /// </summary>
        public static string DeriveTriggerWord(string loraName)
        {
            if (string.IsNullOrWhiteSpace(loraName)) return string.Empty;

            // Drop a "(1)" duplicate-download suffix before splitting.
            var name = Regex.Replace(loraName.Trim(), @"\(\d+\)$", string.Empty);

            var tokens = name.Split(new[] { '-', '_', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries)
                             .Where(t => !IsNoiseToken(t))
                             .ToList();

            var word = tokens.Count > 0 ? string.Join(" ", tokens) : name.Trim();
            if (word.Length == 0) return string.Empty;

            return char.ToUpperInvariant(word[0]) + word.Substring(1);
        }

        private static bool IsNoiseToken(string token)
        {
            if (token.All(char.IsDigit)) return true;                                            // step counts: 000001250
            if (Regex.IsMatch(token, @"^v\d+$", RegexOptions.IgnoreCase)) return true;           // v1, V2
            if (Regex.IsMatch(token, @"^e\d+$", RegexOptions.IgnoreCase)) return true;           // e12 (epochs)
            return NoiseTokens.Contains(token, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Plain serializable snapshot stored on queue items.</summary>
        public KreaLoraDto ToDto() => new() { Name = LoraName, Strength = Strength, TriggerWord = TriggerWord };
    }

    /// <summary>
    /// Persisted form of a <see cref="KreaLoraSelection"/> stored on queue items.
    /// </summary>
    public sealed class KreaLoraDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

        [JsonPropertyName("strength")] public double Strength { get; set; } = 1.0;

        /// <summary>Null on queues saved before trigger words existed ⇒ re-derived from the name.</summary>
        [JsonPropertyName("triggerWord")] public string? TriggerWord { get; set; }

        public KreaLoraSelection ToSelection() => new(Name, Strength, TriggerWord);
    }
}
