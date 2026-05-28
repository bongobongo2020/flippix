namespace FlipPix.UI.Linux.Models
{
    public class CameraQueueItem : BaseQueueItem
    {
        public string ImageFilePath { get; set; } = string.Empty;
        public string CameraControl { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string NegativePrompt { get; set; } = string.Empty;
        public int Steps { get; set; } = 8;
        public double Cfg { get; set; } = 1.5;
        public double Denoise { get; set; } = 1.0;
        public string SamplerName { get; set; } = "euler";
        public string Scheduler { get; set; } = "beta57";

        // ResultImagePath is unique to this model - maps to OutputImagePath in base
        public string? ResultImagePath
        {
            get => OutputImagePath;
            set => OutputImagePath = value;
        }
    }
}
