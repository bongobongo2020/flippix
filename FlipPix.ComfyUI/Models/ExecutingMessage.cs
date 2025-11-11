using System.Text.Json.Serialization;

namespace FlipPix.ComfyUI.Models;

public class ExecutingMessage : WebSocketMessage
{
    [JsonPropertyName("data")]
    public new ExecutingData? Data { get; set; }
}

public class ExecutingData
{
    [JsonPropertyName("node")]
    public string? Node { get; set; }

    [JsonPropertyName("prompt_id")]
    public string? PromptId { get; set; }
}
