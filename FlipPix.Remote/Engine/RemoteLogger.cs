using System.Diagnostics;
using FlipPix.Core.Interfaces;

namespace FlipPix.Remote.Engine;

/// <summary>
/// Hands the shared ComfyUI client's log lines to the desktop's logger, minus the per-message
/// WebSocket trace: server monitors (crystools, dasiwa) broadcast several times a second and would
/// bury everything else. Without a desktop logger, lines go to the debug output.
/// </summary>
public sealed class RemoteLogger : IAppLogger
{
    private readonly IAppLogger? _inner;

    public RemoteLogger(IAppLogger? inner = null) => _inner = inner;

    public void LogDebug(string message, params object[] args) { }

    public void LogInfo(string message, params object[] args)
    {
        if (message.StartsWith("WebSocket message received", StringComparison.Ordinal)) return;
        if (_inner != null) _inner.LogInfo("[Remote] " + message, args);
        else Write("I", message, args);
    }

    public void LogWarning(string message, params object[] args)
    {
        if (_inner != null) _inner.LogWarning("[Remote] " + message, args);
        else Write("W", message, args);
    }

    public void LogError(string message, params object[] args)
    {
        if (_inner != null) _inner.LogError("[Remote] " + message, args);
        else Write("E", message, args);
    }

    public void LogError(Exception exception, string message, params object[] args)
    {
        if (_inner != null) _inner.LogError(exception, "[Remote] " + message, args);
        else Write("E", message + " :: " + exception.Message, args);
    }

    private static void Write(string level, string message, object[] args)
    {
        if (args.Length > 0)
        {
            var i = 0;
            message = System.Text.RegularExpressions.Regex.Replace(message, @"\{[^{}]+\}",
                m => i < args.Length ? args[i++]?.ToString() ?? "" : m.Value);
        }
        Debug.WriteLine($"[FlipPix Remote {level}] {message}");
    }
}
