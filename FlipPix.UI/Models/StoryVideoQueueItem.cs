namespace FlipPix.UI.Models
{
    /// <summary>
    /// Model for story video queue items
    /// </summary>
    public class StoryVideoQueueItem : BaseQueueItem
    {
        public int Index { get; set; }  // The prompt index (1-10)
        public string Prompt { get; set; } = string.Empty;
        public string InputImagePath { get; set; } = string.Empty;

        // OutputVideoPath maps to OutputImagePath in base class
        public string? OutputVideoPath
        {
            get => OutputImagePath;
            set => OutputImagePath = value;
        }
    }
}
