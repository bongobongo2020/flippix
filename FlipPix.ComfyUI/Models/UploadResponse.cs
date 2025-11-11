using System.Text.Json.Serialization;

namespace FlipPix.ComfyUI.Models;

public class UploadResponse
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}