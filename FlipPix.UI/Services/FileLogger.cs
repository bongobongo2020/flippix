using Microsoft.Extensions.Logging;
using System.IO;
using FlipPix.Core.Interfaces;

namespace FlipPix.UI.Services;

public class FileLogger : IAppLogger
{
    private readonly ILogger<FileLogger> _logger;
    private readonly string _logDirectory;
    private readonly string _logFilePath;

    public FileLogger(ILogger<FileLogger> logger)
    {
        _logger = logger;
        
        // Configure log directory
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _logDirectory = Path.Combine(appDataPath, "FlipPix", "Logs");
        
        // Ensure directory exists
        Directory.CreateDirectory(_logDirectory);
        
        // Create log file path with timestamp
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        _logFilePath = Path.Combine(_logDirectory, $"chunk_creator_{timestamp}.log");
    }

    private void WriteToFile(string level, string message)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logEntry = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";
            File.AppendAllText(_logFilePath, logEntry);
        }
        catch
        {
            // Ignore file write errors to prevent logging loops
        }
    }

    public void LogDebug(string message, params object[] args)
    {
        try
        {
            var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
            _logger.LogDebug(formattedMessage);
            WriteToFile("DEBUG", formattedMessage);
        }
        catch (FormatException)
        {
            _logger.LogDebug(message);
            WriteToFile("DEBUG", message);
        }
    }

    public void LogInfo(string message, params object[] args)
    {
        try
        {
            // Handle both structured logging {param} and string.Format {0} syntax
            var formattedMessage = args?.Length > 0 ? string.Format(message, args) : message;
            _logger.LogInformation(message, args ?? Array.Empty<object>());
            WriteToFile("INFO", formattedMessage);
        }
        catch (FormatException)
        {
            // Fallback for structured logging patterns
            _logger.LogInformation(message, args ?? Array.Empty<object>());
            WriteToFile("INFO", message);
        }
    }

    public void LogWarning(string message, params object[] args)
    {
        var formattedMessage = string.Format(message, args);
        _logger.LogWarning(formattedMessage);
        WriteToFile("WARNING", formattedMessage);
    }

    public void LogError(string message, params object[] args)
    {
        try
        {
            var formattedMessage = args?.Length > 0 ? string.Format(message, args) : message;
            _logger.LogError(message, args ?? Array.Empty<object>());
            WriteToFile("ERROR", formattedMessage);
        }
        catch (FormatException)
        {
            _logger.LogError(message, args ?? Array.Empty<object>());
            WriteToFile("ERROR", message);
        }
    }

    public void LogError(Exception exception, string message, params object[] args)
    {
        try
        {
            var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
            _logger.LogError(exception, formattedMessage);
            WriteToFile("ERROR", $"{formattedMessage} - Exception: {exception}");
        }
        catch (FormatException)
        {
            _logger.LogError(exception, message);
            WriteToFile("ERROR", $"{message} - Exception: {exception}");
        }
    }
}