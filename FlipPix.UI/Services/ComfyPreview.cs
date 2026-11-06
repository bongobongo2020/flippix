using System;
using System.IO;

namespace FlipPix.UI.Services;

/// <summary>
/// The quick look FlipPix shows of a result on a remote ComfyUI (over Tailscale, say) while the
/// full PNG follows in the background: ComfyUI's /view re-encodes the image to this format first,
/// typically well under a tenth of the PNG's size.
/// </summary>
public static class ComfyPreview
{
    /// <summary>The /view <c>preview</c> value. WebP is smallest, but WPF can only decode it when
    /// Windows has the WebP codec, so Windows gets JPEG.</summary>
    public static string Format => OperatingSystem.IsWindows() ? "jpeg;85" : "webp;85";

    public static string Extension => Format.StartsWith("webp", StringComparison.Ordinal) ? ".webp" : ".jpg";

    /// <summary>A temp path for the preview of <paramref name="outputPath"/>.</summary>
    public static string TempPathFor(string outputPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), "flippix-previews");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(outputPath) + "_" +
                                 Guid.NewGuid().ToString("N")[..8] + Extension);
    }

    /// <summary>Splits ComfyUI's "subfolder/filename" into its parts.</summary>
    public static (string Subfolder, string FileName) Split(string remoteFile)
    {
        var slash = remoteFile.LastIndexOf('/');
        return slash < 0 ? ("", remoteFile) : (remoteFile[..slash], remoteFile[(slash + 1)..]);
    }
}
