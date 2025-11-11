using FlipPix.ComfyUI.Services;
using FlipPix.ComfyUI.Models;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.Core.Services;
using System.Text.Json;
using System.IO;

namespace FlipPix.UI.Services;

public class ChunkCreatorService
{
    private readonly ComfyUIService _comfyUIService;
    private readonly IAppLogger _logger;
    private readonly VideoAnalysisService _videoAnalysisService;
    private readonly ImageAnalysisService _imageAnalysisService;
    private ComfyUIService? _activeComfyUIService;
    private CancellationTokenSource? _cancellationTokenSource;
    
    private const string WORKFLOW_PATH = "workflow/step1-chunkcreatorAPI.json";
    private const int FRAMES_PER_CHUNK = 49;

    public event EventHandler<int>? ProgressUpdated;
    public event EventHandler<string>? StatusUpdated;
    public event EventHandler<string>? ChunkCompleted;
    public event EventHandler<(int completed, int total)>? ChunkProgressUpdated;
    public event EventHandler<bool>? ProcessingCompleted;

    public ChunkCreatorService(ComfyUIService comfyUIService, IAppLogger logger, VideoAnalysisService videoAnalysisService, ImageAnalysisService imageAnalysisService)
    {
        _comfyUIService = comfyUIService;
        _logger = logger;
        _videoAnalysisService = videoAnalysisService;
        _imageAnalysisService = imageAnalysisService;
    }

    public async Task<List<string>> ProcessVideoInChunksAsync(
        string videoPath, 
        string referenceImage1Path, 
        string referenceImage2Path, 
        string prompt,
        int totalFrames, 
        ComfyUISettings? settings = null)
    {
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;
        var generatedChunks = new List<string>();

        try
        {
            // Create or reuse service with provided settings
            _activeComfyUIService = settings != null ? CreateComfyUIService(settings) : _comfyUIService;

            // Test initial connection
            if (!_activeComfyUIService.IsConnected)
            {
                _logger.LogInfo("Establishing initial ComfyUI connection...");
                await _activeComfyUIService.ConnectAsync(cancellationToken);
            }

            // Calculate number of chunks
            int numChunks = (int)Math.Ceiling((double)totalFrames / FRAMES_PER_CHUNK);
            _logger.LogInfo($"Processing {totalFrames} frames in {numChunks} chunks of {FRAMES_PER_CHUNK} frames each");
            StatusUpdated?.Invoke(this, $"Processing {totalFrames} frames in {numChunks} chunks...");

            // Process each chunk
            for (int chunkIndex = 0; chunkIndex < numChunks; chunkIndex++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInfo("Chunk processing cancelled");
                    break;
                }

                int startFrame = chunkIndex * FRAMES_PER_CHUNK;
                int endFrame = Math.Min(startFrame + FRAMES_PER_CHUNK - 1, totalFrames - 1);
                int framesToProcess = endFrame - startFrame + 1;

                _logger.LogInfo($"=== STARTING CHUNK {chunkIndex + 1}/{numChunks}: frames {startFrame}-{endFrame} ===");
                StatusUpdated?.Invoke(this, $"Processing chunk {chunkIndex + 1}/{numChunks}...");

                // Add delay between chunks
                if (chunkIndex > 0)
                {
                    _logger.LogInfo($"Waiting 5 seconds before starting chunk {chunkIndex + 1}...");
                    await Task.Delay(5000, cancellationToken);
                    _logger.LogInfo($"Delay completed, starting chunk {chunkIndex + 1} processing");
                }

                try
                {
                    // Verify connection before processing each chunk
                    if (!_activeComfyUIService.IsConnected)
                    {
                        _logger.LogWarning($"ComfyUI connection lost before chunk {chunkIndex + 1}, reconnecting...");
                        await _activeComfyUIService.ConnectAsync(cancellationToken);
                        _logger.LogInfo($"ComfyUI reconnected for chunk {chunkIndex + 1}");
                    }

                    // Process chunk
                    _logger.LogInfo($"Calling ProcessSingleChunkAsync for chunk {chunkIndex + 1}...");
                    var chunkPath = await ProcessSingleChunkAsync(
                        videoPath, referenceImage1Path, referenceImage2Path, prompt,
                        startFrame, framesToProcess, chunkIndex, cancellationToken);

                    _logger.LogInfo($"ProcessSingleChunkAsync returned for chunk {chunkIndex + 1}: {chunkPath}");

                    if (!string.IsNullOrEmpty(chunkPath))
                    {
                        generatedChunks.Add(chunkPath);
                        ChunkCompleted?.Invoke(this, chunkPath);
                        ChunkProgressUpdated?.Invoke(this, (chunkIndex + 1, numChunks));
                        _logger.LogInfo($"✓ Successfully completed chunk {chunkIndex + 1}/{numChunks}");
                    }
                    else
                    {
                        _logger.LogError($"✗ Chunk {chunkIndex + 1} failed to generate output - continuing to next chunk");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"✗ Exception processing chunk {chunkIndex + 1}: {ex.Message}");
                    _logger.LogError($"Stack trace: {ex.StackTrace}");
                    // Continue to next chunk even if one fails
                }

                _logger.LogInfo($"=== FINISHED CHUNK {chunkIndex + 1}/{numChunks} ===");
            }

            ProcessingCompleted?.Invoke(this, generatedChunks.Count > 0);
            return generatedChunks;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Video chunk processing failed: {ex}");
            StatusUpdated?.Invoke(this, $"Processing failed: {ex.Message}");
            ProcessingCompleted?.Invoke(this, false);
            return generatedChunks;
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private async Task<string> ProcessSingleChunkAsync(
        string videoPath,
        string refImage1Path,
        string refImage2Path,
        string prompt,
        int skipFrames,
        int frameCount,
        int chunkIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInfo($"=== Starting chunk {chunkIndex + 1} processing ===");
            _logger.LogInfo($"Video: {videoPath}");
            _logger.LogInfo($"Ref1: {refImage1Path}");
            _logger.LogInfo($"Ref2: {refImage2Path}");
            _logger.LogInfo($"Skip frames: {skipFrames}, Frame count: {frameCount}");
            _logger.LogInfo($"Prompt: {prompt}");

            // Ensure connection
            if (!_activeComfyUIService!.IsConnected)
            {
                _logger.LogInfo("ComfyUI not connected, attempting to connect...");
                await _activeComfyUIService.ConnectAsync(cancellationToken);
                _logger.LogInfo($"ComfyUI connection status: {_activeComfyUIService.IsConnected}");
            }

            // Upload files
            _logger.LogInfo("Starting file upload...");
            var uploadedFiles = await _activeComfyUIService.UploadAndPrepareFilesAsync(
                videoPath, refImage1Path, refImage2Path, cancellationToken);
            
            _logger.LogInfo("Files uploaded successfully:");
            foreach (var file in uploadedFiles)
            {
                _logger.LogInfo($"  {file.Key}: {file.Value}");
            }

            // Analyze reference image for resolution
            _logger.LogInfo("Analyzing reference image for resolution configuration...");
            var imageInfo = _imageAnalysisService.AnalyzeImage(refImage1Path);
            var (targetWidth, targetHeight) = GetTargetResolution(imageInfo);
            var orientation = imageInfo.Width > imageInfo.Height ? "Landscape" : "Portrait";
            _logger.LogInfo($"Reference image analysis: {imageInfo.Width}x{imageInfo.Height} ({orientation}) -> Target: {targetWidth}x{targetHeight}");

            // Load and configure workflow
            _logger.LogInfo("Loading and configuring workflow...");
            var workflowJson = await LoadWorkflowAsync();
            var configuredWorkflow = ConfigureWorkflow(
                workflowJson, uploadedFiles, 
                prompt, skipFrames, frameCount, chunkIndex, targetWidth, targetHeight);

            _logger.LogInfo("Workflow configured successfully");

            // Execute workflow
            _logger.LogInfo($"Sending workflow to ComfyUI for execution...");
            var progress = new Progress<ProgressMessage>(msg =>
            {
                _logger.LogInfo($"Progress: {msg.Data?.Node} - {msg.Data?.Value}/{msg.Data?.Max}");
                if (msg.Data?.Max > 0)
                {
                    var nodeProgress = (double)msg.Data.Value / msg.Data.Max;
                    var overallProgress = (int)(nodeProgress * 100);
                    ProgressUpdated?.Invoke(this, overallProgress);
                }
            });

            try
            {
                _logger.LogInfo("Starting workflow execution...");

                // Add timeout for workflow execution
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var executionResult = await _activeComfyUIService.ExecuteWorkflowAsync(configuredWorkflow, progress, combinedCts.Token);
                _logger.LogInfo($"Workflow execution completed with result: {executionResult}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Workflow execution cancelled by user");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Workflow execution failed: {ex.Message}");
                // Continue anyway - the video might still be generated
            }

            // Wait for video generation
            _logger.LogInfo("Waiting for video file generation...");
            await Task.Delay(5000, cancellationToken); // Increased delay
            var outputVideoPath = TryFindRecentVideoFile($"chunk{chunkIndex + 1:D2}");

            if (!string.IsNullOrEmpty(outputVideoPath))
            {
                _logger.LogInfo($"Chunk {chunkIndex + 1} completed successfully: {outputVideoPath}");
            }
            else
            {
                _logger.LogError($"Chunk {chunkIndex + 1} completed but no output video found");
            }

            return outputVideoPath ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Chunk {chunkIndex + 1} processing failed: {ex}");
            _logger.LogError($"Exception details: {ex.StackTrace}");
            return string.Empty;
        }
    }

    private Dictionary<string, object> ConfigureWorkflow(
        string workflowJson,
        Dictionary<string, string> uploadedFiles,
        string prompt,
        int skipFrames,
        int frameCount,
        int chunkIndex,
        int targetWidth,
        int targetHeight)
    {
        _logger.LogInfo("=== Configuring Workflow ===");
        _logger.LogInfo($"Skip frames: {skipFrames}, Frame count: {frameCount}");
        _logger.LogInfo($"Prompt: {prompt}");

        var workflow = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowJson)
            ?? throw new InvalidOperationException("Failed to parse workflow JSON");

        int nodesConfigured = 0;
        foreach (var kvp in workflow)
        {
            if (kvp.Value is JsonElement element && element.ValueKind == JsonValueKind.Object)
            {
                var nodeDict = JsonSerializer.Deserialize<Dictionary<string, object>>(element.GetRawText());
                if (nodeDict != null && nodeDict.TryGetValue("inputs", out var inputs))
                {
                    var inputsDict = inputs is JsonElement inputsElement
                        ? JsonSerializer.Deserialize<Dictionary<string, object>>(inputsElement.GetRawText()) ?? new Dictionary<string, object>()
                        : new Dictionary<string, object>();

                    // Configure based on node ID
                    switch (kvp.Key)
                    {
                        case "14": // VHS_LoadVideoPath
                            if (uploadedFiles.TryGetValue("video", out var videoFile))
                            {
                                // VHS_LoadVideoPath needs the full path with quotes as per the original workflow
                                var videoPath = $"\"E:\\comfy-kontext2\\ComfyUI_windows_portable\\ComfyUI\\input\\{videoFile}\"";
                                inputsDict["video"] = videoPath;
                                inputsDict["frame_load_cap"] = frameCount;
                                inputsDict["skip_first_frames"] = skipFrames;
                                _logger.LogInfo($"Node 14 (VHS_LoadVideoPath): video={videoPath}, frames={frameCount}, skip={skipFrames}");
                                nodesConfigured++;
                            }
                            break;

                        case "24": // First reference image
                            if (uploadedFiles.TryGetValue("style", out var styleFile))
                            {
                                inputsDict["image"] = styleFile;
                                _logger.LogInfo($"Node 24 (LoadImage): image={styleFile}");
                                nodesConfigured++;
                            }
                            break;

                        case "25": // Second reference image
                            if (uploadedFiles.TryGetValue("face", out var faceFile))
                            {
                                inputsDict["image"] = faceFile;
                                _logger.LogInfo($"Node 25 (LoadImage): image={faceFile}");
                                nodesConfigured++;
                            }
                            break;

                        case "26": // Prompt
                            inputsDict["string"] = prompt;
                            _logger.LogInfo($"Node 26 (StringConstantMultiline): prompt='{prompt}'");
                            nodesConfigured++;
                            break;

                        case "28": // WanVideoBlockSwap - memory optimization
                            inputsDict["blocks_to_swap"] = 25;  // Aggressive block swapping
                            inputsDict["vace_blocks_to_swap"] = 12;  // Aggressive VACE block swapping
                            inputsDict["offload_img_emb"] = true;
                            inputsDict["offload_txt_emb"] = true;
                            inputsDict["use_non_blocking"] = true;
                            _logger.LogInfo($"Node 28 (WanVideoBlockSwap): Applied memory optimization settings");
                            nodesConfigured++;
                            break;

                        case "38": // WanVideoVACEEncode - primary resolution
                            inputsDict["width"] = targetWidth;
                            inputsDict["height"] = targetHeight;
                            inputsDict["num_frames"] = frameCount;
                            inputsDict["tiled_vae"] = true;  // Enable tiled VAE for memory efficiency
                            _logger.LogInfo($"Node 38 (WanVideoVACEEncode): width={targetWidth}, height={targetHeight}, num_frames={frameCount}, tiled_vae=true");
                            nodesConfigured++;
                            break;

                        case "48": // WanVideoVACEEncode - secondary resolution
                            inputsDict["width"] = targetWidth;
                            inputsDict["height"] = targetHeight;
                            inputsDict["num_frames"] = frameCount;
                            inputsDict["tiled_vae"] = true;  // Enable tiled VAE for memory efficiency
                            _logger.LogInfo($"Node 48 (WanVideoVACEEncode): width={targetWidth}, height={targetHeight}, num_frames={frameCount}, tiled_vae=true");
                            nodesConfigured++;
                            break;

                        case "73": // WanVideoDecode - VAE tiling for memory efficiency
                            inputsDict["enable_vae_tiling"] = true;
                            inputsDict["tile_x"] = 256;  // Smaller tiles for memory efficiency
                            inputsDict["tile_y"] = 256;  // Smaller tiles for memory efficiency
                            inputsDict["tile_stride_x"] = 128;
                            inputsDict["tile_stride_y"] = 128;
                            _logger.LogInfo($"Node 73 (WanVideoDecode): Enabled VAE tiling with smaller tiles for memory efficiency");
                            nodesConfigured++;
                            break;

                        case "81": // Video output
                            if (uploadedFiles.TryGetValue("video", out var outputVideoFile))
                            {
                                var videoName = Path.GetFileNameWithoutExtension(outputVideoFile);
                                var outputPrefix = $"wan_vace/{videoName}_chunk{chunkIndex + 1:D2}";
                                inputsDict["filename_prefix"] = outputPrefix;
                                _logger.LogInfo($"Node 81 (VHS_VideoCombine): filename_prefix={outputPrefix}");
                                nodesConfigured++;
                            }
                            break;
                    }

                    nodeDict["inputs"] = inputsDict;
                    workflow[kvp.Key] = nodeDict;
                }
            }
        }

        _logger.LogInfo($"Workflow configuration complete. Configured {nodesConfigured} nodes.");
        return workflow;
    }

    private (int width, int height) GetTargetResolution(ImageInfo imageInfo)
    {
        // Use the ImageAnalysisService to get FlipPix compatible resolution
        return _imageAnalysisService.GetTargetResolutionForWanVACE(imageInfo);
    }

    private async Task<string> LoadWorkflowAsync()
    {
        var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", WORKFLOW_PATH);
        if (!File.Exists(fullPath))
        {
            // Try alternative paths
            var alternativePaths = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), WORKFLOW_PATH),
                WORKFLOW_PATH
            };

            foreach (var path in alternativePaths)
            {
                if (File.Exists(path))
                {
                    fullPath = path;
                    break;
                }
            }
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Workflow file not found: {WORKFLOW_PATH}");
        }

        _logger.LogInfo($"Loading workflow from: {fullPath}");
        return await File.ReadAllTextAsync(fullPath);
    }

    private string? TryFindRecentVideoFile(string chunkIdentifier)
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
                    var recentVideo = Directory.GetFiles(outputPath, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(file => videoExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                        .Where(file => file.Contains(chunkIdentifier, StringComparison.OrdinalIgnoreCase))
                        .Select(file => new FileInfo(file))
                        .Where(fileInfo => fileInfo.CreationTime > DateTime.Now.AddMinutes(-10))
                        .OrderByDescending(fileInfo => fileInfo.CreationTime)
                        .FirstOrDefault();

                    if (recentVideo != null)
                    {
                        _logger.LogInfo($"Found chunk video: {recentVideo.FullName}");
                        return recentVideo.FullName;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error finding chunk video: {ex.Message}");
        }

        return null;
    }

    private ComfyUIService CreateComfyUIService(ComfyUISettings settings)
    {
        _logger.LogInfo($"Creating ComfyUI service with settings: {settings.BaseUrl}");
        
        // Increase timeout for connection and workflow execution
        settings.ConnectionTimeout = 30000; // 30 seconds for connection
        
        var httpClient = new System.Net.Http.HttpClient();
        // Don't set BaseAddress and Timeout here - let ComfyUIHttpClient handle it

        var comfyUIHttpClient = new FlipPix.ComfyUI.Http.ComfyUIHttpClient(httpClient, _logger, settings);
        var webSocketClient = new FlipPix.ComfyUI.WebSocket.ComfyUIWebSocketClient(_logger, settings.BaseUrl);
        return new ComfyUIService(comfyUIHttpClient, webSocketClient, _logger, settings);
    }

    public void StopProcessing()
    {
        _logger.LogInfo("Stopping chunk processing");
        StatusUpdated?.Invoke(this, "Stopping processing...");
        _cancellationTokenSource?.Cancel();
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Dispose();
    }
}