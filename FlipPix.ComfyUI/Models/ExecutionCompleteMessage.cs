using System.Text.Json.Serialization;
using System.Text.Json;

namespace FlipPix.ComfyUI.Models;

public class ExecutionCompleteMessage : WebSocketMessage
{
    [JsonPropertyName("data")]
    public new ExecutionCompleteData? Data { get; set; }
}

public class ExecutionCompleteData
{
    [JsonPropertyName("prompt_id")]
    public string PromptId { get; set; } = string.Empty;
    
    [JsonPropertyName("output")]
    public Dictionary<string, object> Output { get; set; } = new();
    
    /// <summary>
    /// Extracts video output information from the completion data
    /// </summary>
    public VideoOutputInfo? GetVideoOutput()
    {
        // Look for video outputs in different possible formats
        foreach (var nodeOutput in Output)
        {
            if (nodeOutput.Value is JsonElement element && element.ValueKind == JsonValueKind.Object)
            {
                // Check for "videos" property (VHS_VideoCombine format)
                if (element.TryGetProperty("videos", out var videos))
                {
                    return ParseVideoArray(nodeOutput.Key, videos);
                }
                
                // Check for "gifs" property (some nodes save as GIF)
                if (element.TryGetProperty("gifs", out var gifs))
                {
                    return ParseVideoArray(nodeOutput.Key, gifs);
                }
                
                // Check for "mp4" or other video format properties
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name.Contains("mp4") || prop.Name.Contains("video") || prop.Name.Contains("avi") || prop.Name.Contains("mov"))
                    {
                        return ParseVideoArray(nodeOutput.Key, prop.Value);
                    }
                }
            }
        }
        
        return null;
    }
    
    private VideoOutputInfo? ParseVideoArray(string nodeId, JsonElement videoArray)
    {
        if (videoArray.ValueKind == JsonValueKind.Array && videoArray.GetArrayLength() > 0)
        {
            var firstVideo = videoArray[0];
            if (firstVideo.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    var filename = firstVideo.TryGetProperty("filename", out var filenameElement) ? filenameElement.GetString() : null;
                    var subfolder = firstVideo.TryGetProperty("subfolder", out var subfolderElement) ? subfolderElement.GetString() : null;
                    var type = firstVideo.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                    
                    if (!string.IsNullOrEmpty(filename))
                    {
                        return new VideoOutputInfo
                        {
                            NodeId = nodeId,
                            Filename = filename,
                            Subfolder = subfolder ?? string.Empty,
                            Type = type ?? string.Empty,
                            FullPath = BuildFullPath(filename, subfolder)
                        };
                    }
                }
                catch (Exception)
                {
                    // Continue to next video if this one fails to parse
                }
            }
        }
        
        return null;
    }
    
    private string BuildFullPath(string? filename, string? subfolder)
    {
        if (string.IsNullOrEmpty(filename))
            return string.Empty;
            
        // Try network share first, then local paths
        var basePaths = new[]
        {
            @"\\proxmox-comfy\wan_vace",
            @"E:\comfy-kontext2\ComfyUI_windows_portable\ComfyUI\output"
        };
        
        foreach (var basePath in basePaths)
        {
            // Handle subfolder path (commonly "wan_vace" for this workflow)
            if (!string.IsNullOrEmpty(subfolder))
            {
                var pathWithSubfolder = Path.Combine(basePath, subfolder, filename);
                if (File.Exists(pathWithSubfolder))
                    return pathWithSubfolder;
            }
            
            // Try direct path
            var directPath = Path.Combine(basePath, filename);
            if (File.Exists(directPath))
                return directPath;
        }
        
        // Default to network share if file doesn't exist yet
        return Path.Combine(@"\\proxmox-comfy\wan_vace", filename);
    }
}

public class VideoOutputInfo
{
    public string NodeId { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public string Subfolder { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
}