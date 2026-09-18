using System;
using System.Diagnostics;
using System.IO;

namespace FlipPix.UI.Linux.Services
{
    /// <summary>
    /// Desktop-shell actions that have no portable .NET equivalent. On Linux "reveal this file
    /// in the file manager" is a org.freedesktop.FileManager1 D-Bus call rather than a shell verb,
    /// so it needs an explicit implementation instead of Windows' explorer.exe /select.
    /// </summary>
    public static class DesktopIntegration
    {
        /// <summary>Opens a directory in the user's file manager.</summary>
        public static void OpenFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;
            Launch(folderPath);
        }

        /// <summary>Opens a file with the user's default application for its type.</summary>
        public static void OpenFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
            Launch(filePath);
        }

        /// <summary>
        /// Shows a file selected inside its containing folder, falling back to just opening
        /// the folder when the desktop offers no way to preselect an item.
        /// </summary>
        public static void RevealInFileManager(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

            if (OperatingSystem.IsWindows())
            {
                TryStart("explorer.exe", $"/select,\"{filePath}\"");
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                TryStart("open", $"-R \"{filePath}\"");
                return;
            }

            // Freedesktop: Nautilus, Dolphin, Nemo and Thunar all implement FileManager1.
            var uri = new Uri(filePath).AbsoluteUri;
            var revealed = TryStart("dbus-send",
                "--session --print-reply --dest=org.freedesktop.FileManager1 " +
                "--type=method_call /org/freedesktop/FileManager1 " +
                $"org.freedesktop.FileManager1.ShowItems array:string:\"{uri}\" string:\"\"",
                waitForExit: true);

            if (!revealed)
            {
                var folder = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(folder)) Launch(folder);
            }
        }

        /// <summary>Hands a path to the desktop's opener (xdg-open on Linux).</summary>
        private static void Launch(string target)
        {
            if (OperatingSystem.IsWindows())
            {
                TryStart(new ProcessStartInfo(target) { UseShellExecute = true });
                return;
            }

            var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            if (!TryStart(opener, $"\"{target}\""))
            {
                // Some minimal installs have no xdg-utils; UseShellExecute is the last resort.
                TryStart(new ProcessStartInfo(target) { UseShellExecute = true });
            }
        }

        private static bool TryStart(string fileName, string arguments, bool waitForExit = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                using var process = Process.Start(psi);
                if (process is null) return false;
                if (!waitForExit) return true;
                return process.WaitForExit(3000) && process.ExitCode == 0;
            }
            catch (Exception)
            {
                return false; // binary missing or not executable
            }
        }

        private static bool TryStart(ProcessStartInfo psi)
        {
            try
            {
                using var process = Process.Start(psi);
                return process is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
