using FFMpegCore;
using FFMpegCore.Enums;
using FlipPix.Core.Models;
using FlipPix.Core.Interfaces;
using System.IO;

namespace FlipPix.UI.Services;

public class VideoAnalysisService
{
    private readonly IAppLogger _logger;

    public VideoAnalysisService(IAppLogger logger)
    {
        _logger = logger;
    }

    public async Task<VideoInfo> AnalyzeVideoAsync(string filePath)
    {
        try
        {
            _logger.LogInfo($"Starting video analysis for: {filePath}");

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Video file not found: {filePath}");
            }

            var fileInfo = new FileInfo(filePath);
            
            // Analyze video using FFMpeg
            var mediaInfo = await FFProbe.AnalyseAsync(filePath);
            
            var videoStream = mediaInfo.VideoStreams.FirstOrDefault();
            if (videoStream == null)
            {
                throw new InvalidOperationException("No video stream found in the file");
            }

            var videoInfo = new VideoInfo
            {
                FilePath = filePath,
                FileSizeBytes = fileInfo.Length,
                Duration = mediaInfo.Duration,
                Width = videoStream.Width,
                Height = videoStream.Height,
                FrameRate = videoStream.FrameRate,
                TotalFrames = (int)(mediaInfo.Duration.TotalSeconds * videoStream.FrameRate),
                Codec = videoStream.CodecName ?? "Unknown"
            };

            _logger.LogInfo($"Video analysis completed: {videoInfo.Width}x{videoInfo.Height}, {videoInfo.Duration:mm\\:ss}, {videoInfo.FrameRate:F2}fps");
            
            return videoInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Video analysis failed: {ex.Message}");
            throw;
        }
    }

    public async Task<string> ExtractThumbnailAsync(string videoPath, string outputPath, TimeSpan? timePosition = null)
    {
        try
        {
            var position = timePosition ?? TimeSpan.FromSeconds(1);
            
            _logger.LogInfo($"Extracting thumbnail from {videoPath} at {position:mm\\:ss}");

            await FFMpeg.SnapshotAsync(videoPath, outputPath, null, position);
            
            _logger.LogInfo($"Thumbnail extracted to: {outputPath}");
            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Thumbnail extraction failed: {ex.Message}");
            throw;
        }
    }

    public async Task<bool> IsValidVideoFileAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return false;

            var mediaInfo = await FFProbe.AnalyseAsync(filePath);
            return mediaInfo.VideoStreams.Any();
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GetVideoInfoStringAsync(string filePath)
    {
        try
        {
            var videoInfo = await AnalyzeVideoAsync(filePath);
            
            return $"Resolution: {videoInfo.Width}x{videoInfo.Height} | " +
                   $"Duration: {videoInfo.Duration:mm\\:ss} | " +
                   $"Frame Rate: {videoInfo.FrameRate:F2} fps | " +
                   $"Codec: {videoInfo.Codec} | " +
                   $"Total Frames: {videoInfo.TotalFrames:N0} | " +
                   $"File Size: {videoInfo.FileSizeBytes / (1024.0 * 1024.0):F1} MB";
        }
        catch (Exception ex)
        {
            return $"Analysis failed: {ex.Message}";
        }
    }
}