using FlipPix.Core.Interfaces;

namespace FlipPix.IosCompanion;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One companion per PC: a second copy would fight the first for the phone's port.
        using var single = new Mutex(true, @"Local\FlipPix.IosCompanion", out var first);
        if (!first)
        {
            MessageBox.Show("FlipPix iOS Companion is already running. Look for its icon next to the clock.",
                "FlipPix iOS Companion", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // No visual styles, on purpose: the classic gray controls match the setup wizard.
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new CompanionForm());
    }
}

/// <summary>Appends to <c>%LocalAppData%\FlipPix\companion\logs\companion.log</c>.</summary>
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

/// <summary>Makes sure the remote can find ffmpeg (video thumbnails and the video content check).</summary>
internal static class Ffmpeg
{
    /// <summary>
    /// ffmpeg on PATH is used as is. Otherwise the copy imageio-ffmpeg installed for ComfyUI's
    /// VideoHelperSuite is copied to <c>ffmpeg.exe</c> beside the companion's data and put on PATH.
    /// Must run before anything asks the remote for ffmpeg: it looks once per run.
    /// </summary>
    public static void Ensure(string portableRoot, IAppLogger log)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(dir.Trim(), "ffmpeg.exe"))) return; }
            catch (Exception) { /* a malformed PATH entry */ }
        }

        var binaries = Path.Combine(portableRoot, "python_embeded", "Lib", "site-packages", "imageio_ffmpeg", "binaries");
        var source = Directory.Exists(binaries)
            ? Directory.EnumerateFiles(binaries, "ffmpeg*.exe").FirstOrDefault()
            : null;
        if (source == null)
        {
            log.LogWarning("ffmpeg not found; videos can't be checked, so they won't be shown");
            return;
        }
        var binDir = Path.Combine(CompanionConfig.LocalDir, "bin");
        Directory.CreateDirectory(binDir);
        var target = Path.Combine(binDir, "ffmpeg.exe");
        if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length)
            File.Copy(source, target, overwrite: true);
        Environment.SetEnvironmentVariable("PATH", binDir + Path.PathSeparator + path);
    }
}
