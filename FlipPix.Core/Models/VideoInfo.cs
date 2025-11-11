namespace FlipPix.Core.Models;

public class VideoInfo
{
    public int TotalFrames { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string Codec { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
}

public class ImageInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string FilePath { get; set; } = string.Empty;
}