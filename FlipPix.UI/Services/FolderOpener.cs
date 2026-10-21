using System;
using System.Diagnostics;

namespace FlipPix.UI.Services;

/// <summary>
/// Opens a folder, or shows a file in its folder. By default that is the OS file manager
/// (explorer.exe on Windows). Like <see cref="ImageOpener"/>, a platform where that doesn't work
/// sets <see cref="Handler"/>: on a Chromebook the Linux app shows its own folder browser.
/// </summary>
public static class FolderOpener
{
    /// <summary>Called with (folder, file to select or null). Returns true when it handled the
    /// request, false to fall back to the OS default.</summary>
    public static Func<string, string?, bool>? Handler { get; set; }

    /// <summary>Throws like Process.Start when the OS can't open the folder.</summary>
    public static void Open(string folder)
    {
        if (Handler?.Invoke(folder, null) == true) return;
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    /// <summary>Opens the folder holding <paramref name="filePath"/> with that file selected.</summary>
    public static void Reveal(string filePath)
    {
        var folder = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(folder) && Handler?.Invoke(folder, filePath) == true) return;
        Process.Start("explorer.exe", $"/select,\"{filePath}\"");
    }
}
