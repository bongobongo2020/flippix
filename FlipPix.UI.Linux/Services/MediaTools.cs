using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FlipPix.UI.Linux.Services
{
    /// <summary>
    /// Locates the ffmpeg/ffprobe binaries on whichever platform the app is running on.
    /// On Arch (and Linux generally) these live on PATH as bare "ffmpeg"/"ffprobe" with no
    /// extension, so the Windows-only ".exe" probing the ViewModels used to do never matched.
    /// Results are cached because every video ViewModel resolves these on each operation.
    /// </summary>
    public static class MediaTools
    {
        private static readonly object _gate = new();
        private static bool _resolved;
        private static string? _ffmpeg;
        private static string? _ffprobe;

        /// <summary>Env var overrides, honoured first so packagers and users can pin a build.</summary>
        private const string FFmpegOverrideVar = "FLIPPIX_FFMPEG";
        private const string FFprobeOverrideVar = "FLIPPIX_FFPROBE";

        /// <summary>Absolute path to ffmpeg, or null when it is not installed.</summary>
        public static string? FFmpegPath { get { Resolve(); return _ffmpeg; } }

        /// <summary>Absolute path to ffprobe, or null when it is not installed.</summary>
        public static string? FFprobePath { get { Resolve(); return _ffprobe; } }

        /// <summary>Directory holding the binaries, for libraries such as FFMpegCore that want a folder.</summary>
        public static string? BinaryFolder
        {
            get
            {
                var exe = FFmpegPath;
                return exe is null ? null : Path.GetDirectoryName(exe);
            }
        }

        /// <summary>Platform-correct executable name: "ffmpeg" on Linux, "ffmpeg.exe" on Windows.</summary>
        public static string ExecutableName(string tool) =>
            OperatingSystem.IsWindows() ? tool + ".exe" : tool;

        /// <summary>Drops the cache so a mid-session install of ffmpeg is picked up.</summary>
        public static void Invalidate()
        {
            lock (_gate) { _resolved = false; _ffmpeg = null; _ffprobe = null; }
        }

        private static void Resolve()
        {
            lock (_gate)
            {
                if (_resolved) return;
                _resolved = true;

                _ffmpeg = FromOverride(FFmpegOverrideVar) ?? Find("ffmpeg");
                _ffprobe = FromOverride(FFprobeOverrideVar) ?? Sibling(_ffmpeg, "ffprobe") ?? Find("ffprobe");
            }
        }

        private static string? FromOverride(string variable)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            return !string.IsNullOrWhiteSpace(value) && File.Exists(value) ? value : null;
        }

        /// <summary>ffprobe almost always ships beside ffmpeg; check there before searching again.</summary>
        private static string? Sibling(string? ffmpegPath, string tool)
        {
            if (string.IsNullOrEmpty(ffmpegPath)) return null;
            var dir = Path.GetDirectoryName(ffmpegPath);
            if (string.IsNullOrEmpty(dir)) return null;
            var candidate = Path.Combine(dir, ExecutableName(tool));
            return File.Exists(candidate) ? candidate : null;
        }

        private static string? Find(string tool)
        {
            var name = ExecutableName(tool);

            // PATH first: on Arch this resolves /usr/bin/ffmpeg from the ffmpeg package.
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate;
                    try { candidate = Path.Combine(dir.Trim(), name); }
                    catch (ArgumentException) { continue; } // malformed PATH entry
                    if (File.Exists(candidate)) return candidate;
                }
            }

            foreach (var candidate in WellKnownLocations(name))
            {
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        private static IEnumerable<string> WellKnownLocations(string name)
        {
            var baseDir = AppContext.BaseDirectory;

            // Binaries shipped alongside the app win over system ones only if PATH missed.
            yield return Path.Combine(baseDir, name);
            yield return Path.Combine(baseDir, "ffmpeg", name);
            yield return Path.Combine(baseDir, "ffmpeg", "bin", name);

            if (OperatingSystem.IsWindows())
            {
                yield return Path.Combine(@"C:\ffmpeg\bin", name);
                yield return Path.Combine(@"C:\Program Files\ffmpeg\bin", name);
                yield return Path.Combine(@"C:\Program Files (x86)\ffmpeg\bin", name);
                yield break;
            }

            // Linux/BSD: pacman installs to /usr/bin; the rest cover manual and per-user installs.
            yield return Path.Combine("/usr/bin", name);
            yield return Path.Combine("/usr/local/bin", name);
            yield return Path.Combine("/bin", name);
            yield return Path.Combine("/opt/ffmpeg/bin", name);
            yield return Path.Combine("/var/lib/flatpak/exports/bin", name);
            yield return Path.Combine("/snap/bin", name);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, ".local", "bin", name);
                yield return Path.Combine(home, "bin", name);
            }
        }
    }
}
