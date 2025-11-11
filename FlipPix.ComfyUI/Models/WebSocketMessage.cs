using System.Text.Json.Serialization;

namespace FlipPix.ComfyUI.Models;

public class WebSocketMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
    
    [JsonPropertyName("data")]
    public object? Data { get; set; }
    
    public string RawData { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class UnknownMessage : WebSocketMessage
{
    public UnknownMessage()
    {
        Type = "unknown";
    }
}