namespace FlipPix.ComfyUI.Exceptions;

public class ComfyUIConnectionException : Exception
{
    public ComfyUIConnectionException(string message) : base(message) { }
    
    public ComfyUIConnectionException(string message, Exception innerException) : base(message, innerException) { }
}