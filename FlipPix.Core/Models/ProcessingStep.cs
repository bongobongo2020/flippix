namespace FlipPix.Core.Models;

public class ProcessingStep
{
    public StepType StepType { get; set; }
    public int SkipFrames { get; set; }
    public string OutputPrefix { get; set; } = string.Empty;
    public ProcessingStatus Status { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? ErrorMessage { get; set; }
}