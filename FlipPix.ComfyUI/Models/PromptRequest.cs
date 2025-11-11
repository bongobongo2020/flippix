using System.Text.Json.Serialization;

namespace FlipPix.ComfyUI.Models;

public class PromptRequest
{
    [JsonPropertyName("prompt")]
    public object Prompt { get; set; } = new();
    
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;
}