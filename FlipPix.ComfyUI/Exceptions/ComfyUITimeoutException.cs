namespace FlipPix.ComfyUI.Exceptions;

public class ComfyUITimeoutException : Exception
{
    public TimeSpan Timeout { get; }
    
    public ComfyUITimeoutException(string message, TimeSpan timeout) : base(message) 
    { 
        Timeout = timeout;
    }
    
    public ComfyUITimeoutException(string message, Exception innerException, TimeSpan timeout) : base(message, innerException) 
    { 
        Timeout = timeout;
    }
}