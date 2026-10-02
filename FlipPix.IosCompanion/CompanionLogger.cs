using FlipPix.Core.Interfaces;

namespace FlipPix.IosCompanion;

/// <summary>
/// Appends to <c>companion.log</c> under <see cref="CompanionConfig.LocalDir"/>
/// (<c>%LocalAppData%\FlipPix\companion\logs</c>; <c>~/.local/share/FlipPix/companion/logs</c> on Linux).
/// </summary>
internal sealed class CompanionLogger : IAppLogger
{
    private readonly object _lock = new();
    private readonly string _path;

    public CompanionLogger()
    {
        var dir = Path.Combine(CompanionConfig.LocalDir, "logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "companion.log");
        try { if (new FileInfo(_path).Length > 5_000_000) File.Delete(_path); } catch (IOException) { }
    }

    public string FolderPath => Path.GetDirectoryName(_path)!;

    public void LogDebug(string message, params object[] args) { }
    public void LogInfo(string message, params object[] args) => Write("INFO", message, args);
    public void LogWarning(string message, params object[] args) => Write("WARN", message, args);
    public void LogError(string message, params object[] args) => Write("ERROR", message, args);
    public void LogError(Exception exception, string message, params object[] args) =>
        Write("ERROR", message + " | " + exception, args);

    private void Write(string level, string message, object[] args)
    {
        string text;
        try { text = args.Length == 0 ? message : string.Format(Placeholders(message), args); }
        catch (FormatException) { text = message; }
        lock (_lock)
        {
            try { File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {text}{Environment.NewLine}"); }
            catch (IOException) { /* logging must never take the companion down */ }
        }
    }

    // The shared code logs with named placeholders ("{Path}"); string.Format wants positions.
    private static string Placeholders(string message)
    {
        var i = 0;
        return System.Text.RegularExpressions.Regex.Replace(message, @"\{(?!\d+\})[A-Za-z_][A-Za-z0-9_]*\}", _ => "{" + i++ + "}");
    }
}
