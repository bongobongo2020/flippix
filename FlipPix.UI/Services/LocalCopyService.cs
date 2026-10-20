using System;
using System.IO;
using System.Threading.Tasks;

namespace FlipPix.UI.Services;

/// <summary>
/// Copies generated images and videos to well-known local user folders.
/// </summary>
public static class LocalCopyService
{
#if FLIPPIX_LINUX
    // On ChromeOS, a "flippix" folder in My files (or My files itself) that is shared with Linux
    // appears under /mnt/chromeos/MyFiles, or /mnt/shared/MyFiles on some ChromeOS versions.
    // Saving there puts results straight into the Files app, instead of in the Linux files folder,
    // which ChromeOS browses over a slow SFTP bridge. Resolved on every copy so sharing the folder
    // takes effect without restarting FlipPix.
    private static readonly string[] ChromeOsMyFilesRoots = { "/mnt/chromeos/MyFiles", "/mnt/shared/MyFiles" };

    private static string? ChromeOsDestination =>
        Array.Find(ChromeOsMyFilesRoots, Directory.Exists) is { } root ? Path.Combine(root, "flippix") : null;

    // Otherwise ~/flippix and ~/flippix-vids. On Linux GetFolderPath(MyPictures/MyVideos) returns ""
    // when ~/Pictures or ~/Videos does not exist (a fresh ChromeOS container), which made these
    // copies land relative to the working directory, i.e. inside the install folder a reinstall wipes.
    public static string ImageDestination => ChromeOsDestination ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "flippix");

    public static string VideoDestination => ChromeOsDestination ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "flippix-vids");
#else
    public static readonly string ImageDestination = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "flippix-images");

    public static readonly string VideoDestination = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "flippix-vids");
#endif

    /// <summary>
    /// Copies a generated image file to the local pictures folder.
    /// </summary>
    public static async Task CopyImageAsync(string sourcePath)
    {
        try
        {
            Directory.CreateDirectory(ImageDestination);
            var destPath = Path.Combine(ImageDestination, Path.GetFileName(sourcePath));
            destPath = GetUniqueFilePath(destPath);
            await Task.Run(() => File.Copy(sourcePath, destPath, true));
        }
        catch
        {
            // Silently fail — copying to local is a convenience, not critical
        }
    }

    /// <summary>
    /// Writes image bytes to the local pictures folder.
    /// </summary>
    public static async Task CopyImageAsync(byte[] imageData, string fileName)
    {
        try
        {
            Directory.CreateDirectory(ImageDestination);
            var destPath = Path.Combine(ImageDestination, fileName);
            destPath = GetUniqueFilePath(destPath);
            await File.WriteAllBytesAsync(destPath, imageData);
        }
        catch
        {
            // Silently fail
        }
    }

    /// <summary>
    /// Copies a generated video file to the local videos folder.
    /// </summary>
    /// <param name="fileName">Optional destination file name; defaults to the source file's name.</param>
    public static async Task CopyVideoAsync(string sourcePath, string? fileName = null)
    {
        try
        {
            Directory.CreateDirectory(VideoDestination);
            var destPath = Path.Combine(VideoDestination, fileName ?? Path.GetFileName(sourcePath));
            destPath = GetUniqueFilePath(destPath);
            await Task.Run(() => File.Copy(sourcePath, destPath, true));
        }
        catch
        {
            // Silently fail
        }
    }

    /// <summary>
    /// If a file already exists at the path, appends a numeric suffix to avoid overwriting.
    /// </summary>
    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int counter = 1;

        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name}_{counter}{ext}");
            counter++;
        } while (File.Exists(candidate));

        return candidate;
    }
}
