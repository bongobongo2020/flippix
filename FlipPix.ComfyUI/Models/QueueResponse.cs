using System.Text.Json.Serialization;

namespace FlipPix.ComfyUI.Models;

public class QueueResponse
{
    [JsonPropertyName("queue_running")]
    public List<QueueItem> QueueRunning { get; set; } = new();
    
    [JsonPropertyName("queue_pending")]
    public List<QueueItem> QueuePending { get; set; } = new();
}

public class QueueItem
{
    [JsonPropertyName("prompt_id")]
    public string PromptId { get; set; } = string.Empty;
    
    [JsonPropertyName("number")]
    public int Number { get; set; }
    
    [JsonPropertyName("prompt")]
    public object Prompt { get; set; } = new();
}