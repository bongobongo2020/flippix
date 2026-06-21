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

    /// <summary>
    /// Last-used browse folder per dialog context, keyed by an arbitrary caller key
    /// (e.g. "wanscail.image", "wanscail.video"). Lets each browse button reopen where it
    /// was last used, persisted across app restarts. The reserved key "__global__" holds the
    /// most-recent folder across all callers and is used as a fallback for keyless dialogs.
    /// </summary>
    public Dictionary<string, string> LastBrowseFolders { get; set; } = new();

    // ComfyUI crash detection and restart settings
    public bool AutoRestartComfyUI { get; set; } = true;
    public string ComfyUIRestartScriptPath { get; set; } = string.Empty;
    public int ComfyUIRestartDelaySeconds { get; set; } = 10; // Wait time before attempting restart
    public int ComfyUIStartupTimeoutSeconds { get; set; } = 300; // Max wait time for ComfyUI to start (5 minutes for large model loading)

    // Story Generator Q last used folder locations
    public string StoryGeneratorPromptJsonFolder { get; set; } = string.Empty;
    public string StoryGeneratorInputImageFolder { get; set; } = string.Empty;

    // Story Generator Q — Z mode LoRA settings (persisted across sessions)
    public bool StoryImageQZLoraEnabled { get; set; } = false;
    public string StoryImageQZSelectedLora { get; set; } = string.Empty;
    public double StoryImageQZLoraStrengthModel { get; set; } = 1.0;

    // Story Image Generator (Z) last used folder locations
    public string StoryImageGeneratorPromptJsonFolder { get; set; } = string.Empty;

    // Enhance Video last used folder location
    public string EnhanceVideoFolder { get; set; } = string.Empty;

    // Video Generator last used folder locations
    public string VideoGeneratorImageFolder { get; set; } = string.Empty;
    public string VideoGeneratorStoryPromptJsonFolder { get; set; } = string.Empty;
    public string VideoGeneratorStoryImagesFolder { get; set; } = string.Empty;

    // Story Video Generator last used folder locations
    public string StoryVideoPromptsFolder { get; set; } = string.Empty;

    // Video Generator workflow settings
    public string SelectedVideoWorkflow { get; set; } = "ltx2_i2v"; // Default to LTXV
    public int Ltx23FrameCount { get; set; } = 240;

    // Painter (WAN 2.2 LightX2V) workflow model names — adjust to match your ComfyUI server
    public string PainterHighNoiseModel { get; set; } = @"wan\wan2.2_i2v_high_noise_14B_Q8_0.gguf";
    public string PainterLowNoiseModel { get; set; } = @"wan\wan2.2_i2v_low_noise_14B_Q8_0.gguf";

    // Prompt2Json save directory for image analysis
    public string Prompt2JsonSaveDirectory { get; set; } = string.Empty;

    // Default prompts
    public string DefaultImagePrompt { get; set; } = string.Empty;
    public string DefaultVideoPrompt { get; set; } = string.Empty;
    public string DefaultNegativePrompt { get; set; } = string.Empty;

    // LM Studio helper properties for UI binding (parsed from LMStudioSettings.BaseUrl)
    public string LMStudioServer
    {
        get => LMStudioSettings.ParseBaseUrl(LMStudioSettings?.BaseUrl).Host;
        set
        {
            LMStudioSettings ??= new LMStudioSettings();
            var (_, port) = LMStudioSettings.ParseBaseUrl(LMStudioSettings.BaseUrl);
            LMStudioSettings.BaseUrl = LMStudioSettings.BuildBaseUrl(value, port);
        }
    }

    public string LMStudioPort
    {
        get => LMStudioSettings.ParseBaseUrl(LMStudioSettings?.BaseUrl).Port;
        set
        {
            LMStudioSettings ??= new LMStudioSettings();
            var (host, _) = LMStudioSettings.ParseBaseUrl(LMStudioSettings.BaseUrl);
            LMStudioSettings.BaseUrl = LMStudioSettings.BuildBaseUrl(host, value);
        }
    }

    /// <summary>Saved LM Studio server URLs (most-recent first), surfaced for UI binding.</summary>
    public List<string> LMStudioServerHistory => LMStudioSettings?.ServerHistory ?? new List<string>();

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