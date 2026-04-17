using System.Text.Json.Serialization;

namespace FlipPix.UI.Models
{
    public class AmateurQueueItem : BaseQueueItem
    {
        public string Prompt { get; set; } = string.Empty;
        public int OrientationIndex { get; set; } = 0;
        public int StyleIndex { get; set; } = 0;
        public int Steps { get; set; } = 9;
        public double Cfg { get; set; } = 1.0;
        public long Seed { get; set; } = 0;

        [JsonIgnore]
        public string DisplayPrompt => Prompt.Length > 50 ? Prompt.Substring(0, 47) + "..." : Prompt;

        [JsonIgnore]
        public string OrientationDisplay => OrientationIndex switch
        {
            0 => "Landscape",
            1 => "Portrait",
            _ => "?"
        };

        [JsonIgnore]
        public string StyleDisplay => StyleIndex switch
        {
            0 => "Natural",
            1 => "Cinematic",
            2 => "Dramatic",
            3 => "Vintage",
            4 => "Modern",
            _ => "?"
        };
    }
}
