using System;
using System.Diagnostics;

namespace FlipPix.UI.Services;

/// <summary>
/// Opens a finished image for full-size viewing. By default that is the OS's default image
/// app. A platform where that doesn't work sets <see cref="Handler"/>: on a Chromebook, Linux
/// apps can't launch the Gallery and the shell hands the file to Chrome, which can't read the
/// Linux container's files, so the Linux app shows its own viewer instead.
/// </summary>
public static class ImageOpener
{
    /// <summary>Shows the image and returns true, or returns false to fall back to the OS default.</summary>
    public static Func<string, bool>? Handler { get; set; }

    /// <summary>Throws like Process.Start when the OS can't open the file.</summary>
    public static void Open(string imagePath)
    {
        if (Handler?.Invoke(imagePath) == true) return;
        Process.Start(new ProcessStartInfo(imagePath) { UseShellExecute = true });
    }
}
