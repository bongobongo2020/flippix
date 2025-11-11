using System.Text.Json.Serialization;

namespace FlipPix.ComfyUI.Models;

public class PromptResponse
{
    [JsonPropertyName("prompt_id")]
    public string PromptId { get; set; } = string.Empty;
    
    [JsonPropertyName("number")]
    public int Number { get; set; }
    
    [JsonPropertyName("node_errors")]
    public Dictionary<string, object> NodeErrors { get; set; } = new();
}