using System.Diagnostics;
using FlipPix.Core.Interfaces;

namespace FlipPix.Mobile.Services;

/// <summary>
/// The shared ComfyUI client logs with structured-style "{Name}" placeholders; on the phone those
/// go to the debug output (logcat on Android) and nowhere else.
/// </summary>
public sealed class MobileLogger : IAppLogger
{
    // Debug is dropped, and so is the per-message WebSocket trace: server monitors broadcast
    // several times a second and would bury everything else in logcat.
    public void LogDebug(string message, params object[] args) { }
    public void LogInfo(string message, params object[] args)
    {
        if (!message.StartsWith("WebSocket message received", StringComparison.Ordinal)) Write("I", message, args);
    }
    public void LogWarning(string message, params object[] args) => Write("W", message, args);
    public void LogError(string message, params object[] args) => Write("E", message, args);
    public void LogError(Exception exception, string message, params object[] args) =>
        Write("E", message + " :: " + exception.Message, args);

    private static void Write(string level, string message, object[] args)
    {
        if (args.Length > 0)
        {
            var i = 0;
            message = System.Text.RegularExpressions.Regex.Replace(message, @"\{[^{}]+\}",
                m => i < args.Length ? args[i++]?.ToString() ?? "" : m.Value);
        }
        Debug.WriteLine($"[FlipPix {level}] {message}");
    }
}
