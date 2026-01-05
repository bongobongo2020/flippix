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
    public string RemoteOutputFolderPath { get; set; } = string.Empty; // Network path to remote ComfyUI output folder
    public List<SavedCameraPrompt> SavedCameraPrompts { get; set; } = new();
    public LMStudioSettings LMStudioSettings { get; set; } = new LMStudioSettings();

    // ComfyUI crash detection and restart settings
    public bool AutoRestartComfyUI { get; set; } = true;
    public string ComfyUIRestartScriptPath { get; set; } = string.Empty;
    public int ComfyUIRestartDelaySeconds { get; set; } = 10; // Wait time before attempting restart
    public int ComfyUIStartupTimeoutSeconds { get; set; } = 300; // Max wait time for ComfyUI to start (5 minutes for large model loading)
}

public class SavedCameraPrompt
{
    public string Name { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string Icon { get; set; } = "💾";
}