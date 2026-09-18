namespace FlipPix.ComfyUI.Exceptions;

/// <summary>
/// Thrown when a submitted prompt disappears from the connected ComfyUI: it is neither queued,
/// running, nor recorded in /history. ComfyUI keeps its queue and history in memory only, so this
/// is the signature of the server process having restarted or been killed mid-run (commonly a VRAM
/// OOM). Without this detection the client waits out its whole execution timeout for a job that no
/// longer exists.
/// </summary>
public class ComfyUIPromptLostException : Exception
{
    public string PromptId { get; }

    public ComfyUIPromptLostException(string message, string promptId) : base(message)
    {
        PromptId = promptId;
    }
}
