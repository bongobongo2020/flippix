using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// Base class for video processing ViewModels providing shared functionality
    /// for ComfyUI communication, progress tracking, and result handling.
    /// </summary>
    public abstract class VideoProcessingBaseViewModel : ObservableObject
    {
        protected readonly ComfyUIService _comfyUIService;
        protected readonly IAppLogger _logger;
        protected readonly FlipPix.Core.Services.SettingsService _settingsService;
        protected readonly IServiceProvider? _serviceProvider;
        protected readonly WorkflowQueueCoordinator _workflowCoordinator;

        // Processing state
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;

        // Result state
        private bool _hasResult = false;
        private string _resultVideoPath = string.Empty;
        private string _resultVideoInfo = string.Empty;

        public event EventHandler? PlayRequested;

        protected VideoProcessingBaseViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _serviceProvider = serviceProvider;
            _workflowCoordinator = workflowCoordinator ?? throw new ArgumentNullException(nameof(workflowCoordinator));
        }

        #region Properties

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (SetProperty(ref _isProcessing, value))
                {
                    OnCanExecuteChanged();
                }
            }
        }

        public string ProcessingStatus
        {
            get => _processingStatus;
            set => SetProperty(ref _processingStatus, value);
        }

        public double ProcessingProgress
        {
            get => _processingProgress;
            set
            {
                if (SetProperty(ref _processingProgress, value) && Math.Abs(_processingProgress - value) > 0.01)
                {
                    OnPropertyChanged(nameof(ProgressPercentage));
                }
            }
        }

        public string ProgressPercentage => $"{ProcessingProgress:F0}%";

        public string LogOutput
        {
            get => _logOutput;
            set => SetProperty(ref _logOutput, value);
        }

        public bool HasResult
        {
            get => _hasResult;
            set
            {
                if (SetProperty(ref _hasResult, value))
                {
                    OnCanExecuteChanged();
                }
            }
        }

        public string ResultVideoPath
        {
            get => _resultVideoPath;
            set => SetProperty(ref _resultVideoPath, value);
        }

        public string ResultVideoInfo
        {
            get => _resultVideoInfo;
            set => SetProperty(ref _resultVideoInfo, value);
        }

        #endregion

        #region Logging

        protected void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var logEntry = $"[{timestamp}] {message}\n";
            LogOutput += logEntry;
            _logger.LogInfo(message);
        }

        #endregion

        #region ComfyUI Helpers

        /// <summary>
        /// Checks if ComfyUI is running on a remote server.
        /// </summary>
        protected bool IsComfyUIRemote(string serverAddress)
        {
            if (string.IsNullOrEmpty(serverAddress))
                return false;

            // Local addresses
            if (serverAddress == "127.0.0.1" ||
                serverAddress == "localhost" ||
                serverAddress == "::1")
                return false;

            // Check if it's a local network IP
            if (serverAddress.StartsWith("192.168.") ||
                serverAddress.StartsWith("10.") ||
                serverAddress.StartsWith("172.16.") ||
                serverAddress.StartsWith("172.17.") ||
                serverAddress.StartsWith("172.18.") ||
                serverAddress.StartsWith("172.19.") ||
                serverAddress.StartsWith("172.2") ||
                serverAddress.StartsWith("172.30.") ||
                serverAddress.StartsWith("172.31."))
            {
                // Local network, but still remote from this machine's perspective
                return true;
            }

            return true;
        }

        /// <summary>
        /// Gets the base URL for ComfyUI from settings.
        /// </summary>
        protected string GetComfyUIBaseUrl()
        {
            var baseUrl = _settingsService.Settings?.BaseUrl;
            if (string.IsNullOrEmpty(baseUrl))
            {
                _logger.LogWarning("Settings BaseUrl is null or empty, reloading settings");
                baseUrl = _settingsService.LoadSettings().BaseUrl;
                if (string.IsNullOrEmpty(baseUrl))
                {
                    _logger.LogWarning("Failed to load BaseUrl from settings, using default");
                    baseUrl = "http://127.0.0.1:8188";
                }
            }
            return baseUrl;
        }

        /// <summary>
        /// Gets existing video files from output folders for tracking before workflow execution.
        /// </summary>
        protected HashSet<string> GetExistingVideoFiles(string filePattern = "*.mp4", params string[] additionalSubfolders)
        {
            var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var settings = _settingsService.Settings;
            if (settings == null) return existingFiles;

            var baseUrl = GetComfyUIBaseUrl();
            bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);

            string outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;
            if (string.IsNullOrEmpty(outputFolder) || !Directory.Exists(outputFolder))
                return existingFiles;

            // Check main folder
            foreach (var file in Directory.GetFiles(outputFolder, filePattern))
            {
                existingFiles.Add(file);
            }

            // Check subfolders
            foreach (var subfolder in additionalSubfolders)
            {
                var subfolderPath = Path.Combine(outputFolder, subfolder);
                if (Directory.Exists(subfolderPath))
                {
                    foreach (var file in Directory.GetFiles(subfolderPath, filePattern))
                    {
                        existingFiles.Add(file);
                    }
                }
            }

            return existingFiles;
        }

        /// <summary>
        /// Waits for a new video file to appear in the output folder.
        /// </summary>
        protected async Task<string?> WaitForNewVideoAsync(
            HashSet<string> existingFiles,
            string filePattern = "*.mp4",
            TimeSpan? maxWaitTime = null,
            TimeSpan? checkInterval = null,
            params string[] additionalSubfolders)
        {
            var settings = _settingsService.Settings;
            if (settings == null)
            {
                AddLog("ERROR: Settings not available");
                return null;
            }

            var baseUrl = GetComfyUIBaseUrl();
            bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);

            string outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;
            if (string.IsNullOrEmpty(outputFolder))
            {
                AddLog("ERROR: Output folder not configured");
                return null;
            }

            AddLog($"Monitoring output folder: {outputFolder} (subfolders: {string.Join(", ", additionalSubfolders.DefaultIfEmpty("none"))})");

            var actualMaxWait = maxWaitTime ?? TimeSpan.FromSeconds(60);
            var actualCheckInterval = checkInterval ?? TimeSpan.FromSeconds(2);
            var startTime = DateTime.Now;

            while (DateTime.Now - startTime < actualMaxWait)
            {
                await Task.Delay(actualCheckInterval);

                // Rebuild folder list each iteration so subfolders created during generation are picked up
                var foldersToCheck = new List<string> { outputFolder };
                foreach (var subfolder in additionalSubfolders)
                {
                    var subfolderPath = Path.Combine(outputFolder, subfolder);
                    if (Directory.Exists(subfolderPath))
                        foldersToCheck.Add(subfolderPath);
                }

                var currentFiles = new List<string>();
                foreach (var folder in foldersToCheck)
                {
                    if (Directory.Exists(folder))
                    {
                        currentFiles.AddRange(Directory.GetFiles(folder, filePattern));
                    }
                }

                var newFiles = currentFiles.Where(f => !existingFiles.Contains(f)).ToList();

                if (newFiles.Any())
                {
                    AddLog($"Found {newFiles.Count} new video file(s)");

                    var newestFile = newFiles
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .First();

                    // Wait until file size stops growing (moov atom written last in MP4)
                    await WaitForFileStableAsync(newestFile);

                    if (File.Exists(newestFile))
                    {
                        var fileInfo = new FileInfo(newestFile);
                        var sizeMB = fileInfo.Length / (1024.0 * 1024.0);
                        AddLog($"Video file ready: {Path.GetFileName(newestFile)} ({sizeMB:F2} MB)");
                        return newestFile;
                    }
                }
                else
                {
                    var elapsed = (int)(DateTime.Now - startTime).TotalSeconds;
                    var remaining = (int)(actualMaxWait - (DateTime.Now - startTime)).TotalSeconds;
                    AddLog($"Waiting for video... ({elapsed}s elapsed, {remaining}s remaining)");
                }
            }

            AddLog("ERROR: Timeout waiting for video file");
            return null;
        }

        /// <summary>
        /// Tries to resolve the output video path using ComfyUI's history API.
        /// Returns a local file path if found, or null if not available.
        /// </summary>
        protected async Task<string?> TryGetVideoFromHistoryAsync(string promptId)
        {
            try
            {
                var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                var videoFile = outputFiles.FirstOrDefault(f =>
                    f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".webm", StringComparison.OrdinalIgnoreCase));

                if (videoFile == null)
                {
                    AddLog($"History API: no video output found for prompt {promptId}");
                    return null;
                }

                AddLog($"History API: found video output: {videoFile}");

                // Try to resolve to a local path
                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    var baseUrl = GetComfyUIBaseUrl();
                    bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                    string outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;

                    if (!string.IsNullOrEmpty(outputFolder))
                    {
                        // videoFile is "subfolder/filename" or just "filename"
                        var localPath = Path.Combine(outputFolder, videoFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(localPath))
                        {
                            await WaitForFileStableAsync(localPath);
                            AddLog($"History API: resolved local path: {localPath}");
                            return localPath;
                        }
                        AddLog($"History API: local path not found at {localPath}, will download via /view");
                    }
                }

                // Download via /view endpoint and save to temp
                var parts = videoFile.Split('/');
                var filename = parts.Last();
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : "";

                var videoBytes = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, subfolder);
                if (videoBytes != null && videoBytes.Length > 0)
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"flippix_history_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tempPath, videoBytes);
                    AddLog($"History API: downloaded video to temp: {tempPath} ({videoBytes.Length / 1024.0 / 1024.0:F2} MB)");
                    return tempPath;
                }

                AddLog($"History API: download via /view failed for {videoFile}");
                return null;
            }
            catch (Exception ex)
            {
                AddLog($"History API lookup failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Waits until a file's size stops growing, indicating it has been fully written.
        /// Polls every second; declares stable after two consecutive equal-size reads.
        /// Times out after maxWait (default 60 s) to avoid hanging indefinitely.
        /// </summary>
        protected async Task WaitForFileStableAsync(string filePath, TimeSpan? maxWait = null)
        {
            var deadline = DateTime.Now + (maxWait ?? TimeSpan.FromSeconds(60));
            long prevSize = -1;
            int stableCount = 0;

            while (DateTime.Now < deadline)
            {
                await Task.Delay(1000);
                try
                {
                    long size = new FileInfo(filePath).Length;
                    if (size > 0 && size == prevSize)
                    {
                        stableCount++;
                        if (stableCount >= 2)
                        {
                            AddLog($"File stable at {size / 1024.0 / 1024.0:F2} MB: {Path.GetFileName(filePath)}");
                            return;
                        }
                    }
                    else
                    {
                        stableCount = 0;
                    }
                    prevSize = size;
                }
                catch
                {
                    // File may briefly be locked; keep waiting
                }
            }

            AddLog($"WaitForFileStable: timeout reached for {Path.GetFileName(filePath)}, proceeding anyway");
        }

        /// <summary>
        /// Copies a video from a remote folder to local storage.
        /// </summary>
        protected async Task<string?> CopyVideoFromRemoteFolder(string remoteOutputPath, string? filePattern = null)
        {
            try
            {
                AddLog($"Looking for video in remote folder: {remoteOutputPath}");

                await Task.Delay(2000);

                var pattern = filePattern ?? "*.mp4";
                var videoFiles = Directory.GetFiles(remoteOutputPath, pattern, SearchOption.AllDirectories)
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                    .ToList();

                // Filter for recent files
                var recentFiles = videoFiles.Where(f =>
                {
                    var fileInfo = new FileInfo(f);
                    var age = DateTime.Now - fileInfo.LastWriteTime;
                    return age.TotalMinutes <= 10;
                }).ToList();

                if (recentFiles.Any())
                {
                    var latestVideo = recentFiles.First();
                    var fileInfo = new FileInfo(latestVideo);
                    AddLog($"Found recent video: {fileInfo.Name}");

                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "video-generation");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var localFileName = $"video_{timestamp}.mp4";
                    var outputPath = Path.Combine(outputDir, localFileName);

                    File.Copy(latestVideo, outputPath, true);
                    await LocalCopyService.CopyVideoAsync(outputPath);
                    AddLog($"Video copied to: {outputPath}");

                    return outputPath;
                }

                AddLog("No recent video files found");
                return null;
            }
            catch (Exception ex)
            {
                AddLog($"ERROR copying video: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Scans an MP4/MOV file for the presence of a moov atom.
        /// Returns false (with a reason) if the file is missing the moov atom or is too small to be valid.
        /// </summary>
        protected bool IsMp4Valid(string filePath, out string reason)
        {
            reason = string.Empty;
            try
            {
                var fi = new FileInfo(filePath);
                if (fi.Length < 8) { reason = "file is too small to be a valid MP4"; return false; }

                using var stream = File.OpenRead(filePath);
                long pos = 0;
                bool foundMoov = false;

                while (pos < stream.Length - 8)
                {
                    stream.Seek(pos, SeekOrigin.Begin);
                    var header = new byte[8];
                    if (stream.Read(header, 0, 8) < 8) break;

                    uint boxSize = (uint)((header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3]);
                    string boxType = System.Text.Encoding.ASCII.GetString(header, 4, 4);

                    if (boxType == "moov") { foundMoov = true; break; }

                    // Extended size (boxSize == 1 means next 8 bytes are the real size)
                    if (boxSize == 1)
                    {
                        var extSize = new byte[8];
                        if (stream.Read(extSize, 0, 8) < 8) break;
                        ulong realSize = 0;
                        for (int i = 0; i < 8; i++) realSize = (realSize << 8) | extSize[i];
                        if (realSize == 0) break;
                        pos += (long)realSize;
                    }
                    else if (boxSize == 0) // box extends to EOF
                    {
                        break;
                    }
                    else
                    {
                        pos += boxSize;
                    }
                }

                if (!foundMoov)
                {
                    reason = "moov atom not found — the file is incomplete or corrupt";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = $"could not read file: {ex.Message}";
                return false;
            }
        }

        #endregion

        #region Result Actions

        protected void PlayVideo()
        {
            PlayRequested?.Invoke(this, EventArgs.Empty);
        }

        protected void OpenResultFolder()
        {
            if (!string.IsNullOrEmpty(ResultVideoPath) && File.Exists(ResultVideoPath))
            {
                var folderPath = Path.GetDirectoryName(ResultVideoPath);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    Process.Start("explorer.exe", folderPath);
                    AddLog($"Opened folder: {folderPath}");
                }
            }
        }

        protected void SendToEditCamera()
        {
            System.Windows.MessageBox.Show(
                "This feature will extract the first frame of the video and send it to the Edit Camera page.",
                "Info",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            AddLog("Send to Edit Camera requested");
        }

        #endregion

        #region FFmpeg Helpers

        /// <summary>
        /// Finds FFmpeg executable on the system.
        /// </summary>
        protected string? FindFFmpeg()
        {
            // Check common locations
            var possiblePaths = new[]
            {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"),
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    AddLog($"Found FFmpeg at: {path}");
                    return path;
                }
            }

            // Try PATH environment variable
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(';'))
                {
                    var ffmpegPath = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(ffmpegPath))
                    {
                        AddLog($"Found FFmpeg in PATH: {ffmpegPath}");
                        return ffmpegPath;
                    }
                }
            }

            AddLog("FFmpeg not found");
            return null;
        }

        /// <summary>
        /// Gets video duration using FFmpeg.
        /// </summary>
        protected double GetVideoDuration(string videoPath)
        {
            try
            {
                var ffmpegPath = FindFFmpeg();
                if (ffmpegPath == null) return 0;

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-i \"{videoPath}\" -hide_banner",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null) return 0;

                var output = process.StandardError.ReadToEnd();
                process.WaitForExit(10000);

                // Parse duration from FFmpeg output
                var durationMatch = System.Text.RegularExpressions.Regex.Match(
                    output, @"Duration: (\d+):(\d+):(\d+)\.(\d+)");

                if (durationMatch.Success)
                {
                    var hours = int.Parse(durationMatch.Groups[1].Value);
                    var minutes = int.Parse(durationMatch.Groups[2].Value);
                    var seconds = int.Parse(durationMatch.Groups[3].Value);
                    var centiseconds = int.Parse(durationMatch.Groups[4].Value);

                    return hours * 3600 + minutes * 60 + seconds + centiseconds / 100.0;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error getting video duration: {ex.Message}");
            }

            return 0;
        }

        /// <summary>
        /// Gets video frame count using FFmpeg.
        /// </summary>
        protected int GetVideoFrameCount(string videoPath)
        {
            try
            {
                var ffmpegPath = FindFFmpeg();
                if (ffmpegPath == null) return 0;

                // Use ffprobe if available
                var ffprobePath = ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");
                if (File.Exists(ffprobePath))
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = ffprobePath,
                        Arguments = $"-v error -select_streams v:0 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 \"{videoPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        var output = process.StandardOutput.ReadToEnd().Trim();
                        process.WaitForExit(30000);

                        if (int.TryParse(output, out int frameCount))
                        {
                            return frameCount;
                        }
                    }
                }

                // Fallback: estimate from duration and FPS
                var duration = GetVideoDuration(videoPath);
                return (int)(duration * 24); // Assume 24 FPS
            }
            catch (Exception ex)
            {
                AddLog($"Error getting frame count: {ex.Message}");
            }

            return 0;
        }

        #endregion

        #region Command Management

        protected virtual void OnCanExecuteChanged()
        {
            CommandManager.InvalidateRequerySuggested();
        }

        #endregion
    }
}
