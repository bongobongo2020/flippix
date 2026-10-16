using System;
using System.IO;
using FlipPix.Core.Interfaces;

namespace FlipPix.UI.Services;

/// <summary>
/// The shared view models were written for Windows: they look for ffmpeg.exe / ffprobe.exe beside
/// the app and reveal files with explorer.exe /select. Rather than fork that code, the Linux app
/// puts same-named stand-ins in its own folder — symlinks to the system ffmpeg/ffprobe and a tiny
/// explorer.exe script that hands the path to the desktop's opener. .NET resolves a bare
/// "explorer.exe" from the executable's folder first, so every call site works unchanged.
/// The installer creates these too; this re-creates them if the folder was copied without them.
/// </summary>
public static class WindowsToolShims
{
    private const string ExplorerScript = """
        #!/bin/sh
        # explorer.exe stand-in for FlipPix on Linux: "explorer.exe /select,<file>" or "explorer.exe <folder>".
        arg="$*"
        case "$arg" in
          /select,*) target="${arg#/select,}"; target="${target#\"}"; target="${target%\"}"; target="$(dirname "$target")" ;;
          *) target="${arg#\"}"; target="${target%\"}" ;;
        esac
        exec xdg-open "$target" >/dev/null 2>&1
        """;

    public static void Install(IAppLogger logger)
    {
        if (!OperatingSystem.IsLinux()) return;
        // The app's own folder. Run through its native launcher (FlipPix.UI.Linux), that is also
        // where Environment.ProcessPath points, which is where the shared code looks.
        var dir = AppContext.BaseDirectory;
        try
        {
            Link(Path.Combine(dir, "ffmpeg.exe"), MediaTools.FFmpegPath);
            Link(Path.Combine(dir, "ffprobe.exe"), MediaTools.FFprobePath);

            var explorer = Path.Combine(dir, "explorer.exe");
            if (!File.Exists(explorer))
            {
                File.WriteAllText(explorer, ExplorerScript.Replace("\r\n", "\n") + "\n");
                File.SetUnixFileMode(explorer,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex)
        {
            // Read-only install (e.g. system package): the package provides them instead.
            logger.LogWarning($"Could not create Windows tool shims in {dir}: {ex.Message}");
        }
    }

    private static void Link(string path, string? target)
    {
        if (target == null) return;
        var existing = new FileInfo(path);
        if (existing.Exists || existing.LinkTarget != null)
        {
            if (existing.LinkTarget == target) return;
            existing.Delete();
        }
        File.CreateSymbolicLink(path, target);
    }
}
