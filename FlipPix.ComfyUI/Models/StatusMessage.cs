using System.Text.Json.Serialization;

namespace FlipPix.ComfyUI.Models;

public class StatusMessage : WebSocketMessage
{
    [JsonPropertyName("data")]
    public new StatusData? Data { get; set; }
}

public class StatusData
{
    [JsonPropertyName("status")]
    public StatusInfo Status { get; set; } = new();
}

public class StatusInfo
{
    [JsonPropertyName("exec_info")]
    public ExecutionInfo ExecInfo { get; set; } = new();
}

public class ExecutionInfo
{
    [JsonPropertyName("queue_remaining")]
    public int QueueRemaining { get; set; }
}