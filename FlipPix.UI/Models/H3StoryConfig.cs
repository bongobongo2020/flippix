namespace FlipPix.UI.Models
{
    /// <summary>
    /// Configuration for Story to Video mode in the H3 Video Editor.
    /// Stores settings for story import, target duration, and cast generation.
    /// </summary>
    public class H3StoryConfig
    {
        /// <summary>Path to the imported story file.</summary>
        public string StoryFilePath { get; set; } = string.Empty;

        /// <summary>Target video duration in seconds (10-300).</summary>
        public double TargetDurationSeconds { get; set; } = 30.0;

        /// <summary>Default duration per clip in seconds.</summary>
        public double ClipDurationSeconds { get; set; } = 5.0;

        /// <summary>Engine for auto-generating cast photos: krea2spicy, krea2, qwen, ideogram, klein, zimage.</summary>
        public string CastPhotoEngine { get; set; } = "krea2spicy";

        /// <summary>Whether to auto-generate cast photos when slots are empty.</summary>
        public bool AutoGenerateCast { get; set; } = true;

        /// <summary>Last used story folder path.</summary>
        public string LastStoryFolder { get; set; } = string.Empty;
    }
}
