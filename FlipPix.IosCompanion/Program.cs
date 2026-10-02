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
