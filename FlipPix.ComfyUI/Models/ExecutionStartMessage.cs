using System.Text.Json.Serialization;

namespace FlipPix.ComfyUI.Models;

public class ExecutionStartMessage : WebSocketMessage
{
    [JsonPropertyName("data")]
    public new ExecutionStartData? Data { get; set; }
}

public class ExecutionStartData
{
    [JsonPropertyName("prompt_id")]
    public string PromptId { get; set; } = string.Empty;
}