using System.Collections.Generic;
using System.Linq;

namespace FlipPix.Core.Models;

public class ComfyUISettings
{
    public string BaseUrl { get; set; } = "http://localhost:8188";
    public int ConnectionTimeout { get; set; } = 10000; // 10 seconds
    public int UploadTimeoutMilliseconds { get; set; } = 600000; // 10 minutes for large file uploads
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 2000;
    public string ComfyUIFolderPath { get; set; } = string.Empty;
    public string OutputFolderPath { get; set; } = string.Empty;
    public string RemoteOutputFolderPath { get; set; } = string.Empty; // Network path to remote ComfyUI output folder
    public string RemoteLoraFolderPath { get; set; } = string.Empty; // Network path to remote ComfyUI LoRA folder
    public List<SavedCameraPrompt> SavedCameraPrompts { get; set; } = new();
    public LMStudioSettings LMStudioSettings { get; set; } = new LMStudioSettings();

    // ComfyUI crash detection and restart settings
    public bool AutoRestartComfyUI { get; set; } = true;
    public string ComfyUIRestartScriptPath { get; set; } = string.Empty;
    public int ComfyUIRestartDelaySeconds { get; set; } = 10; // Wait time before attempting restart
    public int ComfyUIStartupTimeoutSeconds { get; set; } = 300; // Max wait time for ComfyUI to start (5 minutes for large model loading)

    // Story Generator Q last used folder locations
    public string StoryGeneratorPromptJsonFolder { get; set; } = string.Empty;
    public string StoryGeneratorInputImageFolder { get; set; } = string.Empty;

    // Story Image Generator (Z) last used folder locations
    public string StoryImageGeneratorPromptJsonFolder { get; set; } = string.Empty;

    // Video Generator last used folder locations
    public string VideoGeneratorImageFolder { get; set; } = string.Empty;
    public string VideoGeneratorStoryPromptJsonFolder { get; set; } = string.Empty;
    public string VideoGeneratorStoryImagesFolder { get; set; } = string.Empty;

    // Story Video Generator last used folder locations
    public string StoryVideoPromptsFolder { get; set; } = string.Empty;

    // Video Generator workflow settings
    public string SelectedVideoWorkflow { get; set; } = "ltx2_i2v"; // Default to LTXV

    // Prompt2Json save directory for image analysis
    public string Prompt2JsonSaveDirectory { get; set; } = string.Empty;

    // Default prompts
    public string DefaultImagePrompt { get; set; } = string.Empty;
    public string DefaultVideoPrompt { get; set; } = string.Empty;
    public string DefaultNegativePrompt { get; set; } = string.Empty;

    // LM Studio helper properties for UI binding (parsed from LMStudioSettings.BaseUrl)
    public string LMStudioServer
    {
        get
        {
            try
            {
                var uri = new Uri(LMStudioSettings?.BaseUrl ?? "http://alien:8080");
                return uri.Host;
            }
            catch
            {
                return "localhost";
            }
        }
        set
        {
            try
            {
                if (LMStudioSettings == null) LMStudioSettings = new LMStudioSettings();
                var currentUri = new Uri(LMStudioSettings.BaseUrl);
                var newUri = new System.UriBuilder(currentUri) { Host = value }.Uri;
                LMStudioSettings.BaseUrl = newUri.ToString();
            }
            catch
            {
                // If parsing fails, construct a new URL
                LMStudioSettings.BaseUrl = $"http://{value}:8080";
            }
        }
    }

    public string LMStudioPort
    {
        get
        {
            try
            {
                var uri = new Uri(LMStudioSettings?.BaseUrl ?? "http://alien:8080");
                return uri.Port.ToString();
            }
            catch
            {
                return "1234";
            }
        }
        set
        {
            try
            {
                if (LMStudioSettings == null) LMStudioSettings = new LMStudioSettings();
                if (int.TryParse(value, out var port))
                {
                    var currentUri = new Uri(LMStudioSettings.BaseUrl);
                    var newUri = new System.UriBuilder(currentUri) { Port = port }.Uri;
                    LMStudioSettings.BaseUrl = newUri.ToString();
                }
            }
            catch
            {
                // If parsing fails, construct a new URL
                LMStudioSettings.BaseUrl = $"http://alien:8080";
            }
        }
    }

    // Selected model for binding to the ComboBox
    public string SelectedModel
    {
        get => LMStudioSettings?.SelectedModel ?? string.Empty;
        set
        {
            if (LMStudioSettings != null)
            {
                LMStudioSettings.SelectedModel = value;
            }
        }
    }
}

public class SavedCameraPrompt
{
    public string Name { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string Icon { get; set; } = "💾";
}