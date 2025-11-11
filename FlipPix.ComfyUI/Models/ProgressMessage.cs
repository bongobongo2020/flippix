using System.Text.Json.Serialization;

namespace FlipPix.ComfyUI.Models;

public class ProgressMessage : WebSocketMessage
{
    [JsonPropertyName("data")]
    public new ProgressData? Data { get; set; }
}

public class ProgressData
{
    [JsonPropertyName("value")]
    public int Value { get; set; }
    
    [JsonPropertyName("max")]
    public int Max { get; set; }
    
    [JsonPropertyName("prompt_id")]
    public string PromptId { get; set; } = string.Empty;
    
    [JsonPropertyName("node")]
    public string Node { get; set; } = string.Empty;
}