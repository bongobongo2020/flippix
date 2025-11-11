using FlipPix.Core.Models;
using FlipPix.Core.Interfaces;
using System.Drawing;
using System.Runtime.Versioning;

namespace FlipPix.Core.Services;

public class ImageAnalysisService
{
    private readonly IAppLogger _logger;

    public ImageAnalysisService(IAppLogger logger)
    {
        _logger = logger;
    }

    [SupportedOSPlatform("windows6.1")]
    public ImageInfo AnalyzeImage(string imagePath)
    {
        try
        {
            _logger.LogInfo($"Analyzing image: {imagePath}");

            using var image = Image.FromFile(imagePath);
            var imageInfo = new ImageInfo
            {
                Width = image.Width,
                Height = image.Height,
                FilePath = imagePath
            };

            _logger.LogInfo($"Image dimensions: {imageInfo.Width}x{imageInfo.Height}");
            return imageInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to analyze image: {imagePath}");
            throw;
        }
    }

    public (int width, int height) GetTargetResolutionForWanVACE(ImageInfo imageInfo)
    {
        // FlipPix works best with resolutions that are multiples of 8
        // Use conservative resolutions to prevent OOM errors
        // Priority is given to memory efficiency over maximum quality

        var maxWidth = 832;  // Conservative maximum width for memory stability
        var maxHeight = 832;  // Conservative maximum height for memory stability

        // Calculate scaling factor to fit within limits
        var scaleX = (double)maxWidth / imageInfo.Width;
        var scaleY = (double)maxHeight / imageInfo.Height;
        var scale = Math.Min(scaleX, scaleY);

        // Calculate target dimensions
        var targetWidth = (int)(imageInfo.Width * scale);
        var targetHeight = (int)(imageInfo.Height * scale);

        // Round down to nearest multiple of 8 (FlipPix requirement)
        targetWidth = (targetWidth / 8) * 8;
        targetHeight = (targetHeight / 8) * 8;

        // Ensure minimum dimensions for stability
        targetWidth = Math.Max(targetWidth, 256);
        targetHeight = Math.Max(targetHeight, 256);

        // Additional memory optimization: cap at common stable resolutions
        var stableResolutions = new[]
        {
            (256, 256), (320, 320), (384, 384), (448, 448), (512, 512),
            (576, 576), (640, 640), (704, 704), (768, 768), (832, 832)
        };

        // Find the largest stable resolution that fits within our calculated dimensions
        var bestResolution = stableResolutions
            .Where(r => r.Item1 <= targetWidth && r.Item2 <= targetHeight)
            .OrderByDescending(r => r.Item1 * r.Item2)
            .FirstOrDefault();

        if (bestResolution != default)
        {
            targetWidth = bestResolution.Item1;
            targetHeight = bestResolution.Item2;
        }

        _logger.LogInfo($"Target resolution for FlipPix: {targetWidth}x{targetHeight} (from original {imageInfo.Width}x{imageInfo.Height})");
        _logger.LogInfo($"Using conservative resolution to prevent OOM errors");

        return (targetWidth, targetHeight);
    }
}