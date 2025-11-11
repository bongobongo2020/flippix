using FFMpegCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FlipPix.UI.Services
{
    public class VideoJoiningService
    {
        public event EventHandler<string>? ProgressUpdated;
        public event EventHandler<string>? StatusUpdated;

        public async Task<string> JoinVideosAsync(List<string> videoPaths, string outputPath, CancellationToken cancellationToken = default)
        {
            if (videoPaths == null || !videoPaths.Any())
                throw new ArgumentException("Video paths cannot be null or empty", nameof(videoPaths));

            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentException("Output path cannot be null or empty", nameof(outputPath));

            StatusUpdated?.Invoke(this, "Preparing video joining...");

            // Validate all input videos exist
            var missingFiles = videoPaths.Where(path => !File.Exists(path)).ToList();
            if (missingFiles.Any())
            {
                throw new FileNotFoundException($"Missing video files: {string.Join(", ", missingFiles)}");
            }

            // Create output directory if it doesn't exist
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            StatusUpdated?.Invoke(this, $"Joining {videoPaths.Count} video chunks...");

            try
            {
                // Create a temporary text file listing all videos for FFmpeg concat
                var tempListFile = Path.GetTempFileName();
                var listContent = string.Join(Environment.NewLine, 
                    videoPaths.Select(path => $"file '{path.Replace("\\", "/")}'"));
                
                await File.WriteAllTextAsync(tempListFile, listContent, cancellationToken);

                // Use FFmpeg concat demuxer for seamless joining
                StatusUpdated?.Invoke(this, "Executing video concatenation...");
                ProgressUpdated?.Invoke(this, "50%");
                
                // Use Process.Start to run ffmpeg directly
                var ffmpegArgs = $"-f concat -safe 0 -i \"{tempListFile}\" -c copy \"{outputPath}\" -y";
                StatusUpdated?.Invoke(this, $"FFmpeg command: ffmpeg {ffmpegArgs}");
                
                var processStartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = ffmpegArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(processStartInfo);
                if (process != null)
                {
                    // Set a reasonable timeout for video joining (10 minutes)
                    var timeout = TimeSpan.FromMinutes(10);
                    
                    // Use Task.WhenAny to implement timeout with progress updates
                    var processTask = process.WaitForExitAsync(cancellationToken);
                    var timeoutTask = Task.Delay(timeout, cancellationToken);
                    
                    // Create a progress monitoring task
                    var progressTask = MonitorProgressAsync(cancellationToken);
                    
                    var completedTask = await Task.WhenAny(processTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        StatusUpdated?.Invoke(this, "Video joining timed out, terminating process...");
                        process.Kill();
                        throw new TimeoutException($"Video joining process timed out after {timeout.TotalMinutes} minutes");
                    }
                    
                    // Process completed, check exit code
                    if (process.ExitCode != 0)
                    {
                        var error = await process.StandardError.ReadToEndAsync();
                        throw new InvalidOperationException($"FFmpeg failed with exit code {process.ExitCode}: {error}");
                    }
                }

                // Clean up temporary file
                if (File.Exists(tempListFile))
                {
                    File.Delete(tempListFile);
                }

                StatusUpdated?.Invoke(this, "Video joining completed successfully!");
                ProgressUpdated?.Invoke(this, "100%");

                return outputPath;
            }
            catch (Exception ex)
            {
                StatusUpdated?.Invoke(this, $"Video joining failed: {ex.Message}");
                throw;
            }
        }

        public async Task<string> JoinVideosWithReencodingAsync(List<string> videoPaths, string outputPath, CancellationToken cancellationToken = default)
        {
            if (videoPaths == null || !videoPaths.Any())
                throw new ArgumentException("Video paths cannot be null or empty", nameof(videoPaths));

            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentException("Output path cannot be null or empty", nameof(outputPath));

            StatusUpdated?.Invoke(this, "Preparing video joining with re-encoding...");

            var missingFiles = videoPaths.Where(path => !File.Exists(path)).ToList();
            if (missingFiles.Any())
            {
                throw new FileNotFoundException($"Missing video files: {string.Join(", ", missingFiles)}");
            }

            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            StatusUpdated?.Invoke(this, $"Joining {videoPaths.Count} video chunks with re-encoding...");

            try
            {
                var tempListFile = Path.GetTempFileName();
                var listContent = string.Join(Environment.NewLine, 
                    videoPaths.Select(path => $"file '{path.Replace("\\", "/")}'"));
                
                await File.WriteAllTextAsync(tempListFile, listContent, cancellationToken);

                // Use FFmpeg concat with re-encoding for better compatibility
                StatusUpdated?.Invoke(this, "Executing video concatenation with re-encoding...");
                
                // Use Process.Start to run ffmpeg directly
                var processStartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-f concat -safe 0 -i \"{tempListFile}\" -c:v libx264 -c:a aac -crf 23 \"{outputPath}\" -y",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(processStartInfo);
                if (process != null)
                {
                    // Set a reasonable timeout for video joining with re-encoding (15 minutes)
                    var timeout = TimeSpan.FromMinutes(15);
                    
                    // Use Task.WhenAny to implement timeout
                    var processTask = process.WaitForExitAsync(cancellationToken);
                    var timeoutTask = Task.Delay(timeout, cancellationToken);
                    
                    var completedTask = await Task.WhenAny(processTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        StatusUpdated?.Invoke(this, "Video joining timed out, terminating process...");
                        process.Kill();
                        throw new TimeoutException($"Video joining process timed out after {timeout.TotalMinutes} minutes");
                    }
                    
                    // Process completed, check exit code
                    if (process.ExitCode != 0)
                    {
                        var error = await process.StandardError.ReadToEndAsync();
                        throw new InvalidOperationException($"FFmpeg failed with exit code {process.ExitCode}: {error}");
                    }
                }

                if (File.Exists(tempListFile))
                {
                    File.Delete(tempListFile);
                }

                StatusUpdated?.Invoke(this, "Video joining with re-encoding completed successfully!");
                ProgressUpdated?.Invoke(this, "100%");

                return outputPath;
            }
            catch (Exception ex)
            {
                StatusUpdated?.Invoke(this, $"Video joining failed: {ex.Message}");
                throw;
            }
        }

        private async Task MonitorProgressAsync(CancellationToken cancellationToken)
        {
            var interval = TimeSpan.FromSeconds(30);
            var progressValues = new[] { "60%", "70%", "80%", "90%" };
            var index = 0;
            
            while (!cancellationToken.IsCancellationRequested && index < progressValues.Length)
            {
                await Task.Delay(interval, cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                {
                    ProgressUpdated?.Invoke(this, progressValues[index]);
                    StatusUpdated?.Invoke(this, $"Video joining in progress... {progressValues[index]}");
                    index++;
                }
            }
        }
    }
}