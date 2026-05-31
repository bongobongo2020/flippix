using System.IO;
using System.Text.Json.Serialization;

namespace FlipPix.UI.Models
{
    public class WanAnimateQueueItem : BaseQueueItem
    {
        public string CharacterImagePath { get; set; } = string.Empty;
        public string FaceImagePath { get; set; } = string.Empty;
        public string InputVideoPath { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string NegativePrompt { get; set; } = string.Empty;
        public int Fps { get; set; } = 16;
        public int Steps { get; set; } = 6;
        public long Seed { get; set; } = -1;
        public int? SingleChunkIndex { get; set; }
        public string PointsJson { get; set; } = "{\"positive\":[{\"x\":256,\"y\":256}],\"negative\":[{\"x\":0,\"y\":0}]}";
        public string OutputVideoPath { get; set; } = string.Empty;

        [JsonIgnore]
        public string DisplayText =>
            $"{Path.GetFileNameWithoutExtension(InputVideoPath)} → " +
            (SingleChunkIndex.HasValue ? $"chunk {SingleChunkIndex.Value + 1}" : "full video");
    }
}
