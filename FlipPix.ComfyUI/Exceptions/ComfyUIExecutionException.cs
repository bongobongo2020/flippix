namespace FlipPix.ComfyUI.Exceptions;

public class ComfyUIExecutionException : Exception
{
    public string? PromptId { get; }
    
    public ComfyUIExecutionException(string message, string? promptId = null) : base(message) 
    { 
        PromptId = promptId;
    }
    
    public ComfyUIExecutionException(string message, Exception innerException, string? promptId = null) : base(message, innerException) 
    { 
        PromptId = promptId;
    }
}