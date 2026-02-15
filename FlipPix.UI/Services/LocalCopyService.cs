using System;
using System.IO;
using System.Threading.Tasks;

namespace FlipPix.UI.Services;

/// <summary>
/// Copies generated images and videos to well-known local user folders.
/// </summary>
public static class LocalCopyService
{
    private static readonly string ImageDestination = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "flippix-images");

    private static readonly string VideoDestination = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "flippix-vids");

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
    public static async Task CopyVideoAsync(string sourcePath)
    {
        try
        {
            Directory.CreateDirectory(VideoDestination);
            var destPath = Path.Combine(VideoDestination, Path.GetFileName(sourcePath));
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
