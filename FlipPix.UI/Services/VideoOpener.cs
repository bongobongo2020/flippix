using System;
using System.Diagnostics;

namespace FlipPix.UI.Services;

/// <summary>
/// Plays a finished video in the OS default player. Like <see cref="ImageOpener"/>, a platform
/// where that doesn't work sets <see cref="Handler"/>: on a Chromebook the Linux app plays it in
/// its own window.
/// </summary>
public static class VideoOpener
{
    /// <summary>Plays the video and returns true, or returns false to fall back to the OS default.</summary>
    public static Func<string, bool>? Handler { get; set; }

    /// <summary>Throws like Process.Start when the OS can't open the file.</summary>
    public static void Open(string videoPath)
    {
        if (Handler?.Invoke(videoPath) == true) return;
        Process.Start(new ProcessStartInfo(videoPath) { UseShellExecute = true });
    }
}
