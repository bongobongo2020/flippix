using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.ComfyUI.Http;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Services;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Service for retrieving output images from ComfyUI
    /// </summary>
    public class ComfyUIImageRetriever
    {
        /// <summary>
        /// Get output images from ComfyUI after workflow execution
        /// </summary>
        /// <param name="comfyUIService">The ComfyUI service instance</param>
        /// <param name="settingsService">The settings service for configuration</param>
        /// <param name="logger">The logger for output</param>
        /// <param name="loggerAction">Optional action for logging output</param>
        /// <param name="specificFolder">Specific subfolder to search in (optional)</param>
        /// <param name="expectedPattern">Expected filename pattern to search for (optional)</param>
        /// <param name="promptId">The prompt ID from workflow execution (for fallback retrieval)</param>
        /// <param name="maxRetries">Maximum number of retry attempts</param>
        /// <param name="retryDelayMs">Delay between retries in milliseconds</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of output image byte arrays</returns>
        public async Task<List<byte[]>> GetOutputImagesAsync(
            ComfyUIHttpClient httpClient,
            SettingsService settingsService,
            IAppLogger logger,
            Action<string>? loggerAction = null,
            string? specificFolder = null,
            string? expectedPattern = null,
            string? promptId = null,
            int maxRetries = 20,
            int retryDelayMs = 5000,
            CancellationToken ct = default)
        {
            var images = new List<byte[]>();
            HashSet<string> filesBeforeGeneration = new HashSet<string>();

            void Log(string message)
            {
                logger.LogInfo(message);
                loggerAction?.Invoke(message);
            }

            try
            {
                var baseUrl = settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;

                bool isRemoteComfyUI = IsComfyUIRemote(settingsService);

                Log($"ComfyUI server: {actualServer}");
                Log($"Is remote ComfyUI: {isRemoteComfyUI}");

                // For local ComfyUI, capture existing files before generation
                var comfyUIOutputDir = settingsService.Settings?.OutputFolderPath;
                string subfolderPath = string.Empty;
                string[] searchDirs = Array.Empty<string>();

                if (!isRemoteComfyUI && !string.IsNullOrEmpty(comfyUIOutputDir) && Directory.Exists(comfyUIOutputDir))
                {
                    // Check both the subfolder and main output directory
                    var folderToCheck = !string.IsNullOrEmpty(specificFolder)
                        ? Path.Combine(comfyUIOutputDir, specificFolder)
                        : comfyUIOutputDir;

                    searchDirs = Directory.Exists(folderToCheck)
                        ? new[] { folderToCheck, comfyUIOutputDir }
                        : new[] { comfyUIOutputDir };

                    filesBeforeGeneration = new HashSet<string>(
                        searchDirs.SelectMany(dir => Directory.GetFiles(dir, "*.png"))
                        .Select(Path.GetFileName)
                        .Where(f => f != null)!,
                        StringComparer.OrdinalIgnoreCase
                    );
                    Log($"Tracking {filesBeforeGeneration.Count} existing files before generation");
                }

                // Retry image retrieval with delays to give ComfyUI time to write the file
                int retryCount = 0;

                while (retryCount < maxRetries && !images.Any() && !ct.IsCancellationRequested)
                {
                    if (retryCount > 0)
                    {
                        Log($"Retry {retryCount}/{maxRetries} - waiting {retryDelayMs / 1000} seconds before checking again...");
                        await Task.Delay(retryDelayMs, ct);
                    }

                    if (isRemoteComfyUI)
                    {
                        Log("Detected remote ComfyUI server, downloading generated image...");

                        var outputFiles = await httpClient.GetOutputFilesAsync();
                        Log($"Found {outputFiles.Count} potential output files");

                        var imageFiles = outputFiles.Where(f =>
                            f.EndsWith(".png") &&
                            (string.IsNullOrEmpty(expectedPattern) || f.Contains(expectedPattern)))
                            .ToList();

                        if (!string.IsNullOrEmpty(expectedPattern))
                        {
                            Log($"Looking for pattern: {expectedPattern}");
                        }

                        if (imageFiles.Any())
                        {
                            var filename = imageFiles.Last();
                            Log($"Downloading generated image: {filename}");

                            var imageData = await httpClient.DownloadOutputImageAsync(filename);
                            if (imageData != null)
                            {
                                images.Add(imageData);
                                Log($"Successfully downloaded image ({imageData.Length} bytes)");
                            }
                        }
                        else
                        {
                            Log($"No matching files found. Available files: {string.Join(", ", outputFiles.Take(5))}");
                            if (!string.IsNullOrEmpty(promptId))
                            {
                                var fallbackImage = await httpClient.TryDownloadRecentOutputAsync(promptId);
                                if (fallbackImage != null)
                                {
                                    images.Add(fallbackImage);
                                    Log($"Successfully downloaded image via fallback method ({fallbackImage.Length} bytes)");
                                }
                            }
                        }
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(comfyUIOutputDir))
                        {
                            Log("ERROR: ComfyUI output folder not configured");
                            return images;
                        }

                        if (!Directory.Exists(comfyUIOutputDir))
                        {
                            Log($"ERROR: ComfyUI output folder not found: {comfyUIOutputDir}");
                            return images;
                        }

                        // Determine search directory
                        var searchDir = !string.IsNullOrEmpty(specificFolder)
                            ? Path.Combine(comfyUIOutputDir, specificFolder)
                            : comfyUIOutputDir;

                        Log($"Searching for images in: {searchDir}");

                        if (!Directory.Exists(searchDir))
                        {
                            Log($"WARNING: Search directory not found: {searchDir}");
                            if (!string.IsNullOrEmpty(specificFolder))
                            {
                                Log("Falling back to main output directory...");
                                searchDir = comfyUIOutputDir;
                            }
                            else
                            {
                                return images;
                            }
                        }

                        // Search for files
                        IEnumerable<string> matchingFiles;
                        if (!string.IsNullOrEmpty(expectedPattern))
                        {
                            var pattern = $"{expectedPattern}*.png";
                            matchingFiles = Directory.GetFiles(searchDir, pattern);
                            Log($"Searching for pattern: {pattern}");
                        }
                        else
                        {
                            matchingFiles = Directory.GetFiles(searchDir, "*.png");
                            Log($"Searching for all .png files");
                        }

                        var fileInfos = matchingFiles
                            .Select(f => new FileInfo(f))
                            .OrderByDescending(f => f.LastWriteTime)
                            .ToList();

                        if (fileInfos.Any())
                        {
                            var latestFile = fileInfos.First();
                            Log($"Found matching file: {latestFile.Name} (modified: {latestFile.LastWriteTime})");
                            images.Add(await File.ReadAllBytesAsync(latestFile.FullName, ct));
                        }
                        else
                        {
                            // Find newly created files by comparing with files before generation
                            searchDirs = !string.IsNullOrEmpty(specificFolder) && Directory.Exists(Path.Combine(comfyUIOutputDir, specificFolder))
                                ? new[] { Path.Combine(comfyUIOutputDir, specificFolder), comfyUIOutputDir }
                                : new[] { comfyUIOutputDir };

                            var currentFiles = new HashSet<string>(
                                searchDirs.SelectMany(dir => Directory.GetFiles(dir, "*.png"))
                                .Select(Path.GetFileName)
                                .Where(f => f != null)!,
                                StringComparer.OrdinalIgnoreCase
                            );

                            var newFiles = currentFiles.Except(filesBeforeGeneration).ToList();
                            Log($"Found {newFiles.Count} new files since generation started");

                            if (newFiles.Any())
                            {
                                var newFileInfos = newFiles
                                    .SelectMany(f => searchDirs.Select(dir => Path.Combine(dir, f)))
                                    .Where(path => File.Exists(path))
                                    .Select(path => new FileInfo(path))
                                    .OrderByDescending(f => f.CreationTime > f.LastWriteTime ? f.CreationTime : f.LastWriteTime)
                                    .ToList();

                                var newestFile = newFileInfos.First();
                                var fileTime = newestFile.CreationTime > newestFile.LastWriteTime ? newestFile.CreationTime : newestFile.LastWriteTime;
                                Log($"Using newest created file: {newestFile.Name} (created/modified: {fileTime})");
                                images.Add(await File.ReadAllBytesAsync(newestFile.FullName, ct));
                            }
                        }

                        if (!images.Any())
                        {
                            Log($"No images found in retry {retryCount + 1}");
                        }
                    }

                    retryCount++;
                }

                if (!images.Any())
                {
                    Log("WARNING: No output images received after all retries");
                }
            }
            catch (OperationCanceledException)
            {
                Log("Image retrieval cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Log($"Error in GetOutputImagesAsync: {ex.Message}");
                throw;
            }

            return images;
        }

        /// <summary>
        /// Check if ComfyUI server is running remotely
        /// </summary>
        /// <param name="settingsService">Settings service containing ComfyUI URL</param>
        /// <returns>True if ComfyUI is remote, false if local</returns>
        public bool IsComfyUIRemote(SettingsService settingsService)
        {
            try
            {
                var baseUrl = settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                var uri = new Uri(baseUrl);
                var serverAddress = uri.Host;

                if (serverAddress.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    serverAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    serverAddress.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (System.Net.IPAddress.TryParse(serverAddress, out var ip))
                {
                    var bytes = ip.GetAddressBytes();
                    if (bytes.Length == 4)
                    {
                        if (bytes[0] == 192 && bytes[1] == 168) return true;
                        if (bytes[0] == 10) return true;
                        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                    }
                }

                return !string.IsNullOrEmpty(serverAddress) && serverAddress != ".";
            }
            catch
            {
                return true;
            }
        }
    }
}
