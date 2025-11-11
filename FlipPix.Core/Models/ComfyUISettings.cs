using System.Collections.Generic;

namespace FlipPix.Core.Models;

public class ComfyUISettings
{
    public string BaseUrl { get; set; } = "http://localhost:8188";
    public int ConnectionTimeout { get; set; } = 10000; // 10 seconds
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 2000;
    public string ComfyUIFolderPath { get; set; } = string.Empty;
    public string OutputFolderPath { get; set; } = string.Empty;
    public List<SavedCameraPrompt> SavedCameraPrompts { get; set; } = new();
}

public class SavedCameraPrompt
{
    public string Name { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string Icon { get; set; } = "💾";
}