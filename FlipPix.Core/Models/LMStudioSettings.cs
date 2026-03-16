namespace FlipPix.Core.Models;

public class LMStudioSettings
{
    public string BaseUrl { get; set; } = "http://alien:8080";
    public string SelectedModel { get; set; } = string.Empty;
    public int ConnectionTimeout { get; set; } = 30000; // 30 seconds
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 2000;
    public int MaxImageSize { get; set; } = 256; // Maximum image dimension for token efficiency
    public bool AutoConnect { get; set; } = true;
}