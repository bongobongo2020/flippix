using System.IO;
using System.Text.Json.Serialization;

namespace FlipPix.UI.Models
{
    public class WanCharReplaceQueueItem : BaseQueueItem
    {
        public string CharacterImagePath { get; set; } = string.Empty;
        public string InputVideoPath { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string NegativePrompt { get; set; } = string.Empty;
        public int Fps { get; set; } = 16;
        public int Steps { get; set; } = 4;
        public long Seed { get; set; } = -1;
        public int? SingleChunkIndex { get; set; }
        public string OutputVideoPath { get; set; } = string.Empty;

        [JsonIgnore]
        public string DisplayText =>
            $"{Path.GetFileNameWithoutExtension(InputVideoPath)} → " +
            (SingleChunkIndex.HasValue ? $"chunk {SingleChunkIndex.Value + 1}" : "full video");
    }
}
