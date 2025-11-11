using FlipPix.ComfyUI.Services;
using FlipPix.ComfyUI.Models;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using System.Text.Json;
using System.IO;

namespace FlipPix.UI.Services;

public class WorkflowExecutionService
{
    private readonly ComfyUIService _comfyUIService;
    private readonly IAppLogger _logger;
    private readonly string _workflowPath;
    private readonly VideoJoiningService _videoJoiningService;
    private readonly VideoAnalysisService _videoAnalysisService;
    private CancellationTokenSource? _cancellationTokenSource;
    private ComfyUIService? _activeComfyUIService; // Keep track of the active service for reuse

    public event EventHandler<int>? ProgressUpdated;
    public event EventHandler<string>? StatusUpdated;
    public event EventHandler<bool>? ExecutionCompleted;
    public event EventHandler<string>? VideoOutputPathUpdated;
    public event EventHandler<List<string>>? AllVideosCompleted;
    public event EventHandler<string>? FinalVideoCompleted;

    public WorkflowExecutionService(ComfyUIService comfyUIService, IAppLogger logger)
    {
        _comfyUIService = comfyUIService;
        _logger = logger;
        _workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "workflow", "FlipPix_V2V_Master_API.json");
        _videoJoiningService = new VideoJoiningService();
        _videoAnalysisService = new VideoAnalysisService(logger);
        
        // Subscribe to ComfyUI service events for progress monitoring
        _comfyUIService.ProgressUpdated += OnComfyUIProgress;
        _comfyUIService.ExecutionCompleted += OnComfyUIExecutionCompleted;
        
        // Subscribe to video joining service events
        _videoJoiningService.ProgressUpdated += OnVideoJoiningProgress;
        _videoJoiningService.StatusUpdated += OnVideoJoiningStatus;
    }

    public async Task<bool> ExecuteWorkflowIncrementalAsync(string videoPath, string stylePath, string facePath, int totalFrames, ComfyUISettings? settings = null)
    {
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;
        var allGeneratedVideos = new List<string>();
        const int FRAMES_PER_CHUNK = 81;

        try
        {
            // Create or reuse service with provided settings
            if (settings != null)
            {
                _logger.LogInfo($"Creating ComfyUI service for workflow with settings: {settings.BaseUrl}");
                var httpClient = new System.Net.Http.HttpClient();
                httpClient.BaseAddress = new Uri(settings.BaseUrl);
                httpClient.Timeout = TimeSpan.FromHours(2);
                
                var comfyUIHttpClient = new FlipPix.ComfyUI.Http.ComfyUIHttpClient(httpClient, _logger, settings);
                var webSocketClient = new FlipPix.ComfyUI.WebSocket.ComfyUIWebSocketClient(_logger, settings.BaseUrl);
                _activeComfyUIService = new ComfyUIService(comfyUIHttpClient, webSocketClient, _logger, settings);
            }
            else
            {
                _activeComfyUIService = _comfyUIService;
            }
            
            // Analyze video to determine orientation
            _logger.LogInfo($"Analyzing video for orientation detection: {videoPath}");
            var videoInfo = await _videoAnalysisService.AnalyzeVideoAsync(videoPath);
            _logger.LogInfo($"Video dimensions: {videoInfo.Width}x{videoInfo.Height}, Orientation: {(videoInfo.Width > videoInfo.Height ? "Landscape" : "Portrait")}");
            
            _logger.LogInfo($"Starting incremental workflow execution for {totalFrames} total frames");
            StatusUpdated?.Invoke(this, $"Processing video with {totalFrames} frames in {Math.Ceiling((double)totalFrames / FRAMES_PER_CHUNK)} chunks...");
            
            // Calculate number of chunks needed
            int numChunks = (int)Math.Ceiling((double)totalFrames / FRAMES_PER_CHUNK);
            
            for (int chunkIndex = 0; chunkIndex < numChunks; chunkIndex++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInfo("Incremental processing cancelled");
                    break;
                }

                int startFrame = chunkIndex * FRAMES_PER_CHUNK;
                int endFrame = Math.Min(startFrame + FRAMES_PER_CHUNK - 1, totalFrames - 1);
                int framesToProcess = endFrame - startFrame + 1;
                
                _logger.LogInfo($"Processing chunk {chunkIndex + 1}/{numChunks}: frames {startFrame}-{endFrame} ({framesToProcess} frames)");
                StatusUpdated?.Invoke(this, $"Processing chunk {chunkIndex + 1}/{numChunks}: frames {startFrame}-{endFrame}");
                
                // Calculate overall progress based on chunks
                int baseProgress = (chunkIndex * 100) / numChunks;
                
                // Add delay between chunks to ensure server is ready
                if (chunkIndex > 0)
                {
                    _logger.LogInfo($"Waiting 5 seconds before starting chunk {chunkIndex + 1}...");
                    StatusUpdated?.Invoke(this, $"Preparing for chunk {chunkIndex + 1}...");
                    await Task.Delay(5000, cancellationToken);
                }
                
                // Execute workflow for this chunk using the active service
                var chunkVideoPath = await ExecuteWorkflowChunkAsync(
                    videoPath, stylePath, facePath, 
                    startFrame, framesToProcess, 
                    _activeComfyUIService!, cancellationToken,
                    baseProgress, 100 / numChunks, chunkIndex, videoInfo);
                
                if (!string.IsNullOrEmpty(chunkVideoPath))
                {
                    allGeneratedVideos.Add(chunkVideoPath);
                    _logger.LogInfo($"Chunk {chunkIndex + 1} completed successfully: {chunkVideoPath}");
                    StatusUpdated?.Invoke(this, $"Chunk {chunkIndex + 1} completed: {Path.GetFileName(chunkVideoPath)}");
                }
                else
                {
                    _logger.LogError($"Chunk {chunkIndex + 1} failed to generate video");
                    StatusUpdated?.Invoke(this, $"Chunk {chunkIndex + 1} failed!");
                    
                    // Continue processing other chunks even if one fails
                    continue;
                }
                
                // Log progress
                int completedChunks = chunkIndex + 1;
                _logger.LogInfo($"Progress: {completedChunks}/{numChunks} chunks completed ({allGeneratedVideos.Count} videos generated)");
            }
            
            // Notify completion with all generated videos
            if (allGeneratedVideos.Count > 0)
            {
                _logger.LogInfo($"Incremental processing completed. Generated {allGeneratedVideos.Count} videos");
                StatusUpdated?.Invoke(this, $"Completed! Generated {allGeneratedVideos.Count} video chunks");
                AllVideosCompleted?.Invoke(this, allGeneratedVideos);
                
                // Step 2: Join all videos seamlessly
                _logger.LogInfo("Step 2: Starting video joining...");
                StatusUpdated?.Invoke(this, "Step 2: Joining video chunks...");
                var finalVideoPath = await JoinVideoChunksAsync(allGeneratedVideos, videoPath, cancellationToken);
                
                // Process complete after joining
                if (!string.IsNullOrEmpty(finalVideoPath))
                {
                    _logger.LogInfo($"Processing completed successfully! Final video: {finalVideoPath}");
                    StatusUpdated?.Invoke(this, $"Processing complete! Final video: {Path.GetFileName(finalVideoPath)}");
                }
                else
                {
                    _logger.LogError("Final video path is empty - video joining failed");
                    StatusUpdated?.Invoke(this, "Error: Final video not created");
                }
                
                ExecutionCompleted?.Invoke(this, true);
                return true;
            }
            else
            {
                _logger.LogError("No videos were generated during incremental processing");
                StatusUpdated?.Invoke(this, "Failed to generate any video chunks");
                ExecutionCompleted?.Invoke(this, false);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Incremental workflow execution failed: {ex}");
            StatusUpdated?.Invoke(this, $"Execution failed: {ex.Message}");
            ExecutionCompleted?.Invoke(this, false);
            return false;
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private async Task<string> ExecuteWorkflowChunkAsync(
        string videoPath, string stylePath, string facePath,
        int startFrame, int frameCount,
        ComfyUIService comfyUIService, CancellationToken cancellationToken,
        int baseProgress, int progressRange, int chunkIndex, VideoInfo? videoInfo = null)
    {
        try
        {
            _logger.LogInfo($"Starting chunk execution: frames {startFrame}-{startFrame + frameCount - 1}");
            
            // Use the provided ComfyUI service
            ComfyUIService serviceToUse = comfyUIService;

            // Subscribe to events with progress offset
            EventHandler<ProgressMessage> progressHandler = (sender, msg) =>
            {
                if (msg.Data?.Max > 0)
                {
                    var nodeProgress = (double)msg.Data.Value / msg.Data.Max;
                    var chunkProgress = baseProgress + (int)(nodeProgress * progressRange * 0.9);
                    ProgressUpdated?.Invoke(this, Math.Min(baseProgress + progressRange - 1, chunkProgress));
                }
            };
            
            serviceToUse.ProgressUpdated += progressHandler;

            try
            {
                // Ensure connection before proceeding
                if (!serviceToUse.IsConnected)
                {
                    _logger.LogInfo("Connecting to ComfyUI server for chunk...");
                    await serviceToUse.ConnectAsync(cancellationToken);
                }
                
                // Upload files (skip if using same service and files already uploaded)
                _logger.LogInfo("Uploading files for chunk...");
                var uploadedFiles = await serviceToUse.UploadAndPrepareFilesAsync(videoPath, stylePath, facePath, cancellationToken);
                
                // Load and configure workflow with frame range
                _logger.LogInfo($"Configuring workflow for frames {startFrame}-{startFrame + frameCount - 1}");
                string workflowJson = await LoadWorkflowAsync();
                var configuredWorkflow = await ConfigureWorkflowWithFrameRangeAsync(
                    workflowJson, uploadedFiles, startFrame, frameCount, chunkIndex, videoInfo);
                
                // Execute workflow
                _logger.LogInfo("Starting workflow execution on ComfyUI server...");
                var progress = new Progress<ProgressMessage>(msg => progressHandler(this, msg));
                
                // Record baseline video count
                var baselineVideoCount = GetVideoCount();
                _logger.LogInfo($"Baseline video count before chunk: {baselineVideoCount}");
                
                // Start workflow execution
                var workflowTask = serviceToUse.ExecuteWorkflowAsync(configuredWorkflow, progress, cancellationToken);
                
                // Start video monitoring
                var videoCheckTask = MonitorForNewVideos(baselineVideoCount, cancellationToken);
                
                // Wait for either completion
                _logger.LogInfo("Waiting for workflow completion or video detection...");
                var completedTask = await Task.WhenAny(workflowTask, videoCheckTask);
                
                string? detectedVideoPath = null;
                
                if (completedTask == videoCheckTask)
                {
                    detectedVideoPath = await videoCheckTask;
                    _logger.LogInfo($"Video detected via monitoring: {detectedVideoPath}");
                    
                    // Wait a bit for workflow to complete
                    try
                    {
                        await workflowTask.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
                        _logger.LogInfo("Workflow task completed after video detection");
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning("Workflow didn't complete within timeout, but video was detected");
                    }
                }
                else
                {
                    _logger.LogInfo("Workflow task completed first, checking for video...");
                    var workflowResult = await workflowTask;
                    await Task.Delay(3000, cancellationToken);
                    detectedVideoPath = TryFindRecentVideoFile();
                    _logger.LogInfo($"Video detection after workflow completion: {detectedVideoPath ?? "None"}");
                }
                
                ProgressUpdated?.Invoke(this, baseProgress + progressRange);
                
                if (!string.IsNullOrEmpty(detectedVideoPath))
                {
                    _logger.LogInfo($"Chunk completed successfully with video: {detectedVideoPath}");
                }
                else
                {
                    _logger.LogWarning("Chunk completed but no video was detected");
                }
                
                return detectedVideoPath ?? string.Empty;
            }
            finally
            {
                serviceToUse.ProgressUpdated -= progressHandler;
                _logger.LogInfo("Cleaned up chunk execution event handlers");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Workflow chunk execution failed for frames {startFrame}-{startFrame + frameCount - 1}: {ex}");
            _logger.LogError($"Exception details: {ex.StackTrace}");
            return string.Empty;
        }
    }

    public async Task<bool> ExecuteWorkflowAsync(string videoPath, string stylePath, string facePath, ComfyUISettings? settings = null)
    {
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        try
        {
            _logger.LogInfo("Starting workflow execution");
            StatusUpdated?.Invoke(this, "Initializing workflow...");
            ProgressUpdated?.Invoke(this, 0);

            // Update ComfyUI service settings if provided
            if (settings != null)
            {
                _logger.LogInfo($"Using ComfyUI settings: {settings.BaseUrl}");
                StatusUpdated?.Invoke(this, $"Connecting to ComfyUI at {settings.BaseUrl}...");
                
                // Create a new ComfyUI service with updated settings for this execution
                var httpClient = new System.Net.Http.HttpClient();
                httpClient.BaseAddress = new Uri(settings.BaseUrl);
                httpClient.Timeout = TimeSpan.FromHours(2); // Set 2 hour timeout for long-running workflows
                
                var comfyUIHttpClient = new FlipPix.ComfyUI.Http.ComfyUIHttpClient(httpClient, _logger, settings);
                var webSocketClient = new FlipPix.ComfyUI.WebSocket.ComfyUIWebSocketClient(_logger, settings.BaseUrl);
                var tempComfyUIService = new ComfyUIService(comfyUIHttpClient, webSocketClient, _logger, settings);
                
                // Use the temporary service for this execution
                return await ExecuteWithService(tempComfyUIService, videoPath, stylePath, facePath, cancellationToken);
            }

            // Connect to ComfyUI if not already connected (using default service)
            if (!_comfyUIService.IsConnected)
            {
                StatusUpdated?.Invoke(this, "Connecting to ComfyUI...");
                await _comfyUIService.ConnectAsync(cancellationToken);
                ProgressUpdated?.Invoke(this, 10);
            }

            // Analyze video first
            var videoInfo = await _videoAnalysisService.AnalyzeVideoAsync(videoPath);
            return await ExecuteWithService(_comfyUIService, videoPath, stylePath, facePath, cancellationToken, videoInfo);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInfo("Workflow execution was cancelled");
            StatusUpdated?.Invoke(this, "Execution cancelled");
            ExecutionCompleted?.Invoke(this, false);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Workflow execution failed: {ex}");
            StatusUpdated?.Invoke(this, $"Execution failed: {ex.Message}");
            ExecutionCompleted?.Invoke(this, false);
            return false;
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private async Task<bool> ExecuteWithService(ComfyUIService service, string videoPath, string stylePath, string facePath, CancellationToken cancellationToken, VideoInfo? videoInfo = null)
    {
        // Subscribe to the service's events for progress reporting
        service.ProgressUpdated += OnComfyUIProgress;
        service.ExecutionCompleted += OnComfyUIExecutionCompleted;
        
        try
        {
            // Step 1: Upload files
            StatusUpdated?.Invoke(this, "Uploading files...");
            _logger.LogInfo($"Uploading files - Video: {videoPath}, Style: {stylePath}, Face: {facePath}");
            
            var uploadedFiles = await service.UploadAndPrepareFilesAsync(videoPath, stylePath, facePath, cancellationToken);
            ProgressUpdated?.Invoke(this, 30);

            // Step 2: Load and configure workflow
            StatusUpdated?.Invoke(this, "Loading workflow...");
            string workflowJson;
            Dictionary<string, object> configuredWorkflow;
            
            try
            {
                workflowJson = await LoadWorkflowAsync();
                configuredWorkflow = await ConfigureWorkflowWithUploadedFilesAsync(workflowJson, uploadedFiles, videoInfo);
                ProgressUpdated?.Invoke(this, 50);
                StatusUpdated?.Invoke(this, "Workflow loaded and configured successfully");
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogError($"Workflow file not found: {ex.Message}");
                StatusUpdated?.Invoke(this, $"Error: {ex.Message}");
                throw;
            }

            // Step 3: Execute workflow
            StatusUpdated?.Invoke(this, "Executing workflow...");
            _logger.LogInfo("Starting workflow execution");
            
            // Create progress reporter
            var progress = new Progress<ProgressMessage>(progressMsg =>
            {
                _logger.LogInfo($"Workflow progress: {progressMsg.Data?.Node} - {progressMsg.Data?.Value}/{progressMsg.Data?.Max}");
                
                // Convert ComfyUI progress to percentage (50-95% range)
                if (progressMsg.Data?.Max > 0)
                {
                    var nodeProgress = (double)progressMsg.Data.Value / progressMsg.Data.Max;
                    var overallProgress = 50 + (int)(nodeProgress * 45);
                    ProgressUpdated?.Invoke(this, Math.Min(95, overallProgress));
                }
            });

            // Start the workflow execution
            var workflowTask = service.ExecuteWorkflowAsync(configuredWorkflow, progress, cancellationToken);
            
            // Record baseline video count before execution
            var baselineVideoCount = GetVideoCount();
            _logger.LogInfo($"Baseline video count: {baselineVideoCount}");
            
            // Start periodic video checking
            var videoCheckTask = MonitorForNewVideos(baselineVideoCount, cancellationToken);
            
            // Wait for either workflow completion OR video detection
            var completedTask = await Task.WhenAny(workflowTask, videoCheckTask);
            
            string? detectedVideoPath = null;
            
            if (completedTask == videoCheckTask)
            {
                // Video was detected before workflow completion
                detectedVideoPath = await videoCheckTask;
                _logger.LogInfo($"Video detected via monitoring: {detectedVideoPath}");
                
                // Continue waiting for workflow to complete, but with shorter timeout
                try
                {
                    await workflowTask.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Workflow didn't complete within 2 minutes after video detection, proceeding anyway");
                }
            }
            else
            {
                // Workflow completed first, check for video
                var workflowResult = await workflowTask;
                _logger.LogInfo($"Workflow execution completed. Result: {workflowResult}");
                
                // Wait a bit for file system to finalize the video
                await Task.Delay(3000, cancellationToken);
                
                detectedVideoPath = TryFindRecentVideoFile();
            }
            
            // Update UI with video detection results
            if (!string.IsNullOrEmpty(detectedVideoPath))
            {
                _logger.LogInfo($"Final video output: {detectedVideoPath}");
                VideoOutputPathUpdated?.Invoke(this, detectedVideoPath);
                StatusUpdated?.Invoke(this, $"Video saved to: {detectedVideoPath}");
            }
            else
            {
                _logger.LogWarning("No video output detected despite workflow completion");
                StatusUpdated?.Invoke(this, "Workflow completed but no video output found");
            }
            
            ProgressUpdated?.Invoke(this, 100);
            StatusUpdated?.Invoke(this, "Workflow completed successfully!");
            ExecutionCompleted?.Invoke(this, true);
            return true;
        }
        finally
        {
            // Unsubscribe from events to prevent memory leaks
            service.ProgressUpdated -= OnComfyUIProgress;
            service.ExecutionCompleted -= OnComfyUIExecutionCompleted;
        }
    }

    private async Task<string> LoadWorkflowAsync()
    {
        // Try multiple possible workflow locations
        var possiblePaths = new[]
        {
            _workflowPath,
            Path.Combine(Directory.GetCurrentDirectory(), "workflow", "FlipPix_V2V_Master_API.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "workflow", "FlipPix_V2V_Master_API.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "workflow", "FlipPix_V2V_Master_API.json"),
            "workflow/FlipPix_V2V_Master_API.json",
            "../workflow/FlipPix_V2V_Master_API.json"
        };

        foreach (var path in possiblePaths)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    _logger.LogInfo($"Found workflow at: {fullPath}");
                    return await File.ReadAllTextAsync(fullPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error checking path {path}: {ex.Message}");
            }
        }

        throw new FileNotFoundException($"Workflow file not found. Searched paths: {string.Join(", ", possiblePaths)}");
    }

    private Task<Dictionary<string, object>> ConfigureWorkflowWithUploadedFilesAsync(
        string workflowJson, 
        Dictionary<string, string> uploadedFiles,
        VideoInfo? videoInfo = null)
    {
        _logger.LogInfo("Configuring workflow with uploaded files");

        var workflow = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowJson) 
            ?? throw new InvalidOperationException("Failed to parse workflow JSON");

        // Configure workflow nodes with uploaded file references
        foreach (var kvp in workflow)
        {
            if (kvp.Value is JsonElement element && element.ValueKind == JsonValueKind.Object)
            {
                var nodeDict = JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText());
                if (nodeDict != null && nodeDict.TryGetValue("class_type", out var classType))
                {
                    ConfigureNodeWithUploadedFiles(nodeDict, classType.ToString(), uploadedFiles);
                    // Configure resolution based on video orientation
                    ConfigureResolutionNodes(nodeDict, kvp.Key, videoInfo);
                    workflow[kvp.Key] = nodeDict;
                }
            }
        }

        _logger.LogInfo("Workflow configuration completed");
        return Task.FromResult(workflow);
    }

    private Task<Dictionary<string, object>> ConfigureWorkflowWithFrameRangeAsync(
        string workflowJson, 
        Dictionary<string, string> uploadedFiles,
        int startFrame,
        int frameCount,
        int? chunkIndex = null,
        VideoInfo? videoInfo = null)
    {
        _logger.LogInfo($"Configuring workflow with frame range: start={startFrame}, count={frameCount}, chunk={chunkIndex}");

        var workflow = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowJson) 
            ?? throw new InvalidOperationException("Failed to parse workflow JSON");

        // Configure workflow nodes with uploaded file references and frame range
        foreach (var kvp in workflow)
        {
            if (kvp.Value is JsonElement element && element.ValueKind == JsonValueKind.Object)
            {
                var nodeDict = JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText());
                if (nodeDict != null)
                {
                    if (nodeDict.TryGetValue("class_type", out var classType))
                    {
                        ConfigureNodeWithUploadedFiles(nodeDict, classType.ToString(), uploadedFiles, chunkIndex);
                        // Configure resolution based on video orientation
                        ConfigureResolutionNodes(nodeDict, kvp.Key, videoInfo);
                    }
                    
                    // Configure frame range parameters
                    // Node 149 controls skip_first_frames
                    if (kvp.Key == "149" && nodeDict.TryGetValue("inputs", out var inputs149))
                    {
                        var inputsDict = inputs149 is JsonElement inputsElement 
                            ? JsonSerializer.Deserialize<Dictionary<string, object>>(inputsElement.GetRawText()) ?? new Dictionary<string, object>()
                            : new Dictionary<string, object>();
                        
                        inputsDict["value"] = startFrame;
                        nodeDict["inputs"] = inputsDict;
                        _logger.LogInfo($"Set skip_first_frames (node 149) to {startFrame}");
                    }
                    
                    // Node 19 controls frame_load_cap (should remain at 81 or frameCount)
                    if (kvp.Key == "19" && nodeDict.TryGetValue("inputs", out var inputs19))
                    {
                        var inputsDict = inputs19 is JsonElement inputsElement 
                            ? JsonSerializer.Deserialize<Dictionary<string, object>>(inputsElement.GetRawText()) ?? new Dictionary<string, object>()
                            : new Dictionary<string, object>();
                        
                        inputsDict["value"] = frameCount;
                        nodeDict["inputs"] = inputsDict;
                        _logger.LogInfo($"Set frame_load_cap (node 19) to {frameCount}");
                    }
                    
                    workflow[kvp.Key] = nodeDict;
                }
            }
        }

        _logger.LogInfo("Workflow configuration with frame range completed");
        return Task.FromResult(workflow);
    }

    private void ConfigureNodeWithUploadedFiles(
        Dictionary<string, object> nodeDict, 
        string? classType, 
        Dictionary<string, string> uploadedFiles,
        int? chunkIndex = null)
    {
        if (string.IsNullOrEmpty(classType) || !nodeDict.TryGetValue("inputs", out var inputs)) 
            return;

        var inputsDict = inputs is JsonElement inputsElement 
            ? JsonSerializer.Deserialize<Dictionary<string, object>>(inputsElement.GetRawText()) ?? new Dictionary<string, object>()
            : new Dictionary<string, object>();

        switch (classType)
        {
            case "VHS_LoadVideoPath":
                // VHS nodes need absolute paths
                if (uploadedFiles.TryGetValue("video", out var videoFile))
                {
                    var videoFileName = Path.GetFileName(videoFile);
                    inputsDict["video"] = $"E:\\comfy-kontext2\\ComfyUI_windows_portable\\ComfyUI\\input\\{videoFileName}";
                }
                break;

            case "LoadImage":
                // LoadImage nodes for style and face images - use filename only
                // This is a simplified approach; in production, you'd have better node identification
                if (uploadedFiles.TryGetValue("style", out var styleFile))
                {
                    inputsDict["image"] = styleFile;
                }
                else if (uploadedFiles.TryGetValue("face", out var faceFile))
                {
                    inputsDict["image"] = faceFile;
                }
                break;

            case "StringConstant":
                // Configure filename prefix for output videos
                if (inputsDict.TryGetValue("string", out var stringValue))
                {
                    var currentString = stringValue?.ToString() ?? "";
                    // Only modify StringConstant nodes that contain "wan_vace" in their string value
                    if (currentString.Contains("wan_vace") && uploadedFiles.TryGetValue("video", out var videoFileName))
                    {
                        var videoFileNameWithoutExt = Path.GetFileNameWithoutExtension(videoFileName);
                        // Clean the filename to remove any special characters that might cause issues
                        var cleanFileName = System.Text.RegularExpressions.Regex.Replace(videoFileNameWithoutExt, @"[^\w\-_]", "_");
                        
                        // For single video processing, use clean format without chunk numbers
                        // For incremental processing, only add chunk numbers when explicitly requested
                        string newPrefix;
                        if (chunkIndex.HasValue && chunkIndex.Value >= 0)
                        {
                            // Only add chunk number for incremental processing
                            newPrefix = $"wan_vace/wan_vace_{cleanFileName}_chunk{chunkIndex.Value + 1:D2}";
                        }
                        else
                        {
                            // Clean single video format
                            newPrefix = $"wan_vace/wan_vace_{cleanFileName}";
                        }
                        
                        inputsDict["string"] = newPrefix;
                        _logger.LogInfo($"Updated StringConstant filename prefix from '{currentString}' to '{newPrefix}' (chunkIndex: {chunkIndex})");
                    }
                }
                break;

            case var ct when ct?.Contains("Video") == true || ct?.Contains("WanVideo") == true:
                // Other video nodes use filename only
                if (uploadedFiles.TryGetValue("video", out var vidFile))
                {
                    if (inputsDict.ContainsKey("video"))
                        inputsDict["video"] = vidFile;
                    if (inputsDict.ContainsKey("video_file"))
                        inputsDict["video_file"] = vidFile;
                }
                break;
        }

        nodeDict["inputs"] = inputsDict;
    }
    
    private void ConfigureResolutionNodes(Dictionary<string, object> nodeDict, string nodeId, VideoInfo? videoInfo)
    {
        if (videoInfo == null || !nodeDict.TryGetValue("class_type", out var classType))
            return;
            
        var classTypeStr = classType.ToString();
        
        // Configure width and height nodes based on video orientation
        if (classTypeStr == "INTConstant" && nodeDict.TryGetValue("inputs", out var inputs))
        {
            var inputsDict = inputs is JsonElement inputsElement 
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(inputsElement.GetRawText()) ?? new Dictionary<string, object>()
                : inputs as Dictionary<string, object> ?? new Dictionary<string, object>();
                
            bool isLandscape = videoInfo.Width > videoInfo.Height;
            
            // Node 20 is height, Node 21 is width (based on the workflow analysis)
            if (nodeId == "20") // Height node
            {
                inputsDict["value"] = isLandscape ? 576 : 1024;
                nodeDict["inputs"] = inputsDict;
                _logger.LogInfo($"Set height (node 20) to {inputsDict["value"]} for {(isLandscape ? "landscape" : "portrait")} video");
            }
            else if (nodeId == "21") // Width node  
            {
                inputsDict["value"] = isLandscape ? 1024 : 576;
                nodeDict["inputs"] = inputsDict;
                _logger.LogInfo($"Set width (node 21) to {inputsDict["value"]} for {(isLandscape ? "landscape" : "portrait")} video");
            }
        }
    }

    private void OnComfyUIProgress(object? sender, ProgressMessage progressMsg)
    {
        // Additional progress handling if needed
        StatusUpdated?.Invoke(this, $"Processing: {progressMsg.Data?.Node ?? "Unknown"}");
    }

    private void OnComfyUIExecutionCompleted(object? sender, ExecutionCompleteMessage completeMsg)
    {
        // Extract video output information
        _logger.LogInfo("ComfyUI execution completed, checking for video output...");
        
        // Debug: Log the complete message structure
        try
        {
            var jsonOutput = System.Text.Json.JsonSerializer.Serialize(completeMsg.Data?.Output, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            _logger.LogInfo($"DEBUG: Complete execution output data: {jsonOutput}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to serialize output for debugging: {ex.Message}");
        }
        
        var videoOutput = completeMsg.Data?.GetVideoOutput();
        if (videoOutput != null)
        {
            _logger.LogInfo($"Video generated successfully: {videoOutput.Filename}");
            _logger.LogInfo($"Video saved to: {videoOutput.FullPath}");
            
            // Notify listeners about the video output path
            VideoOutputPathUpdated?.Invoke(this, videoOutput.FullPath);
            StatusUpdated?.Invoke(this, $"Video saved to: {videoOutput.FullPath}");
        }
        else
        {
            _logger.LogWarning("Workflow completed but no video output was found in the response");
            var outputKeys = completeMsg.Data?.Output?.Keys?.ToArray() ?? Array.Empty<string>();
            _logger.LogInfo($"DEBUG: Output keys found: {string.Join(", ", outputKeys)}");
            
            // Fallback: Try to find the most recent video file in the output directory
            var fallbackPath = TryFindRecentVideoFile();
            if (!string.IsNullOrEmpty(fallbackPath))
            {
                _logger.LogInfo($"Found recent video file as fallback: {fallbackPath}");
                VideoOutputPathUpdated?.Invoke(this, fallbackPath);
                StatusUpdated?.Invoke(this, $"Video likely saved to: {fallbackPath}");
            }
            else
            {
                StatusUpdated?.Invoke(this, "Workflow completed but no video output was detected");
            }
        }
    }

    private string TryFindRecentVideoFile()
    {
        try
        {
            var outputPaths = new[]
            {
                @"\\proxmox-comfy\wan_vace",
                @"E:\comfy-kontext2\ComfyUI_windows_portable\ComfyUI\output\wan_vace",
                @"E:\comfy-kontext2\ComfyUI_windows_portable\ComfyUI\output"
            };

            var videoExtensions = new[] { ".mp4", ".avi", ".mov", ".mkv", ".webm" };
            
            foreach (var outputPath in outputPaths)
            {
                if (Directory.Exists(outputPath))
                {
                    _logger.LogInfo($"Checking for recent videos in: {outputPath}");
                    
                    var videoFiles = Directory.GetFiles(outputPath, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(file => videoExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                        .Select(file => new FileInfo(file))
                        .Where(fileInfo => fileInfo.CreationTime > DateTime.Now.AddMinutes(-10)) // Files created in last 10 minutes
                        .OrderByDescending(fileInfo => fileInfo.CreationTime)
                        .FirstOrDefault();
                    
                    if (videoFiles != null)
                    {
                        _logger.LogInfo($"Found recent video: {videoFiles.FullName} (created: {videoFiles.CreationTime})");
                        return videoFiles.FullName;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error searching for recent video files: {ex.Message}");
        }
        
        return string.Empty;
    }

    private int GetVideoCount()
    {
        try
        {
            var outputPath = @"\\proxmox-comfy\wan_vace";
            if (Directory.Exists(outputPath))
            {
                var videoExtensions = new[] { ".mp4", ".avi", ".mov", ".mkv", ".webm" };
                return Directory.GetFiles(outputPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Count(file => videoExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error counting videos: {ex.Message}");
        }
        return 0;
    }

    private async Task<string> MonitorForNewVideos(int baselineCount, CancellationToken cancellationToken)
    {
        var checkInterval = TimeSpan.FromSeconds(10); // Check every 10 seconds
        var maxWaitTime = TimeSpan.FromMinutes(15); // Maximum wait time
        var startTime = DateTime.Now;
        
        _logger.LogInfo($"Starting video monitoring. Baseline count: {baselineCount}");
        
        while (DateTime.Now - startTime < maxWaitTime && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(checkInterval, cancellationToken);
            
            var currentCount = GetVideoCount();
            _logger.LogInfo($"Video monitoring check: {currentCount} videos (was {baselineCount})");
            
            if (currentCount > baselineCount)
            {
                // New video detected, find the most recent one
                var recentVideo = TryFindRecentVideoFile();
                if (!string.IsNullOrEmpty(recentVideo))
                {
                    _logger.LogInfo($"New video detected: {recentVideo}");
                    StatusUpdated?.Invoke(this, "Video generation detected!");
                    ProgressUpdated?.Invoke(this, 95);
                    return recentVideo;
                }
            }
            
            // Update progress during monitoring (60-90% range)
            var elapsed = DateTime.Now - startTime;
            var progressPercent = 60 + (int)((elapsed.TotalMinutes / maxWaitTime.TotalMinutes) * 30);
            ProgressUpdated?.Invoke(this, Math.Min(90, progressPercent));
        }
        
        _logger.LogWarning($"Video monitoring timeout after {maxWaitTime.TotalMinutes} minutes");
        return string.Empty;
    }

    public async Task<string> JoinVideoChunksAsync(List<string> videoPaths, string originalVideoPath, CancellationToken cancellationToken = default)
    {
        if (videoPaths == null || !videoPaths.Any())
        {
            _logger.LogWarning("No video paths provided for joining");
            return string.Empty;
        }

        if (videoPaths.Count == 1)
        {
            _logger.LogInfo("Only one video chunk, no joining needed");
            var singleVideo = videoPaths[0];
            FinalVideoCompleted?.Invoke(this, singleVideo);
            return singleVideo;
        }

        try
        {
            _logger.LogInfo($"Starting step 2: Joining {videoPaths.Count} video chunks seamlessly");
            StatusUpdated?.Invoke(this, $"Step 2: Joining {videoPaths.Count} video chunks...");

            // Sort video paths to ensure correct order (assuming they have chunk numbers in filename)
            var sortedVideos = videoPaths.OrderBy(path => path).ToList();
            _logger.LogInfo($"Sorted video paths: {string.Join(", ", sortedVideos.Select(Path.GetFileName))}");
            
            // Generate output filename based on original video
            var originalFileName = Path.GetFileNameWithoutExtension(originalVideoPath);
            var outputDirectory = Path.GetDirectoryName(sortedVideos[0]) ?? @"\\proxmox-comfy\wan_vace";
            var outputFileName = $"wan_vace_{originalFileName}_final.mp4";
            var outputPath = Path.Combine(outputDirectory, outputFileName);

            _logger.LogInfo($"Joining videos to: {outputPath}");

            // Join videos using VideoJoiningService
            _logger.LogInfo("Calling VideoJoiningService.JoinVideosAsync...");
            var finalVideoPath = await _videoJoiningService.JoinVideosAsync(sortedVideos, outputPath, cancellationToken);
            _logger.LogInfo($"VideoJoiningService completed, returned path: {finalVideoPath}");

            if (!string.IsNullOrEmpty(finalVideoPath) && File.Exists(finalVideoPath))
            {
                _logger.LogInfo($"Video joining completed successfully: {finalVideoPath}");
                StatusUpdated?.Invoke(this, $"Final video created: {Path.GetFileName(finalVideoPath)}");
                FinalVideoCompleted?.Invoke(this, finalVideoPath);
                return finalVideoPath;
            }
            else
            {
                _logger.LogError("Video joining failed - output file not found");
                StatusUpdated?.Invoke(this, "Video joining failed");
                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error joining video chunks: {ex.Message}");
            StatusUpdated?.Invoke(this, $"Video joining failed: {ex.Message}");
            return string.Empty;
        }
    }

    private void OnVideoJoiningProgress(object? sender, string progress)
    {
        _logger.LogInfo($"Video joining progress: {progress}");
        StatusUpdated?.Invoke(this, $"Joining videos: {progress}");
    }

    private void OnVideoJoiningStatus(object? sender, string status)
    {
        _logger.LogInfo($"Video joining status: {status}");
        StatusUpdated?.Invoke(this, status);
    }


    public void StopExecution()
    {
        _logger.LogInfo("Stopping workflow execution");
        StatusUpdated?.Invoke(this, "Stopping execution...");
        _cancellationTokenSource?.Cancel();
    }

    public void Dispose()
    {
        _comfyUIService.ProgressUpdated -= OnComfyUIProgress;
        _comfyUIService.ExecutionCompleted -= OnComfyUIExecutionCompleted;
        _videoJoiningService.ProgressUpdated -= OnVideoJoiningProgress;
        _videoJoiningService.StatusUpdated -= OnVideoJoiningStatus;
        _cancellationTokenSource?.Dispose();
    }
}