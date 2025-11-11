namespace FlipPix.Core.Models;

public class ProcessingSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public VideoInfo? VideoInfo { get; set; }
    public List<ProcessingStep> Steps { get; set; } = new();
    public string StyleImagePath { get; set; } = string.Empty;
    public string FaceImagePath { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
}