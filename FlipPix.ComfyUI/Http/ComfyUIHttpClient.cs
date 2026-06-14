using System.Net.Http.Json;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.ComfyUI.Models;

namespace FlipPix.ComfyUI.Http;

public class ComfyUIHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IAppLogger _logger;
    private readonly ComfyUISettings _settings;
    private bool _disposed = false;

    public ComfyUIHttpClient(HttpClient httpClient, IAppLogger logger, ComfyUISettings settings)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settings = settings;

        _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
        // Use infinite timeout globally; upload methods apply per-request timeouts via CancellationToken.
        // Connection-check methods (TestConnectionAsync, IsComfyUIReadyAsync) apply their own short timeouts.
        _httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInfo("Testing connection to ComfyUI at {BaseUrl}", _settings.BaseUrl);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_settings.ConnectionTimeout);
            var response = await _httpClient.GetAsync("/system_stats", cts.Token);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInfo("Connection successful in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
                return true;
            }
            else
            {
                _logger.LogError("Connection failed with status: {StatusCode}", response.StatusCode);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection test failed");
            return false;
        }
    }

    /// <summary>
    /// Tests if ComfyUI is fully ready to process workflows by checking if object_info is available
    /// This is a better readiness check than just HTTP connectivity
    /// </summary>
    public async Task<bool> IsComfyUIReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Checking if ComfyUI is fully ready...");

            // The /object_info endpoint requires all nodes to be loaded
            // This ensures ComfyUI is not just HTTP-responsive, but actually ready to process workflows
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_settings.ConnectionTimeout);
            var response = await _httpClient.GetAsync("/object_info", cts.Token);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("ComfyUI is ready (object_info accessible)");
                return true;
            }
            else
            {
                _logger.LogDebug("ComfyUI not ready yet (HTTP {StatusCode})", response.StatusCode);
                return false;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Expected during startup - ComfyUI not ready yet
            _logger.LogDebug("ComfyUI not ready yet: {Message}", ex.Message);
            return false;
        }
    }

    public async Task<string> UploadImageAsync(string filePath, string type = "input", CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {filePath}");
            }

            var fileInfo = new FileInfo(filePath);
            _logger.LogInfo("Uploading image: {FilePath} ({FileSize} bytes)", filePath, fileInfo.Length);

            using var content = new MultipartFormDataContent();
            using var fileStream = File.OpenRead(filePath);
            using var fileContent = new StreamContent(fileStream);
            
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            content.Add(fileContent, "image", Path.GetFileName(filePath));
            content.Add(new StringContent(type), "type");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            uploadCts.CancelAfter(_settings.UploadTimeoutMilliseconds);
            var response = await _httpClient.PostAsync("/upload/image", content, uploadCts.Token);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<UploadResponse>(responseContent);
                
                _logger.LogInfo("Image uploaded successfully in {ElapsedMs}ms: {FileName}",
                    stopwatch.ElapsedMilliseconds, result?.Name ?? "unknown");
                
                return result?.Name ?? throw new InvalidOperationException("Upload response missing filename");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"Upload failed with status {response.StatusCode}: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload image: {FilePath}", filePath);
            throw;
        }
    }

    public async Task<string> UploadVideoAsync(string filePath, string type = "input", CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {filePath}");
            }

            var fileInfo = new FileInfo(filePath);
            _logger.LogInfo("Uploading video: {FilePath} ({FileSize} bytes)", filePath, fileInfo.Length);

            using var content = new MultipartFormDataContent();
            using var fileStream = File.OpenRead(filePath);
            using var fileContent = new StreamContent(fileStream);
            
            // Set appropriate content type for video files
            var extension = Path.GetExtension(filePath).ToLower();
            var contentType = extension switch
            {
                ".mp4" => "video/mp4",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                ".webm" => "video/webm",
                _ => "video/mp4" // Default fallback
            };
            
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            // ComfyUI uses the /upload/image endpoint with the "image" field for ALL file types
            // (image, video, audio). There is no /upload/video endpoint in stock ComfyUI; posting
            // to it can be silently answered by a proxy/custom node with a 2xx that never persists
            // the file, leaving the workflow to fail later with "could not be loaded with cv."
            content.Add(fileContent, "image", Path.GetFileName(filePath));
            content.Add(new StringContent(type), "type");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            uploadCts.CancelAfter(_settings.UploadTimeoutMilliseconds);
            var response = await _httpClient.PostAsync("/upload/image", content, uploadCts.Token);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<UploadResponse>(responseContent);

                var uploadedName = result?.Name
                    ?? throw new InvalidOperationException("Upload response missing filename");

                _logger.LogInfo("Video uploaded in {ElapsedMs}ms: {FileName} (subfolder='{Subfolder}'), verifying on server...",
                    stopwatch.ElapsedMilliseconds, uploadedName, result!.Subfolder);

                // Verify the file actually landed in ComfyUI's input folder. A 2xx upload response
                // is not proof the bytes were persisted, so confirm via /view before the caller
                // queues a workflow that references this name.
                await VerifyInputFileExistsAsync(uploadedName, result.Subfolder, cancellationToken);

                _logger.LogInfo("Video upload verified on server: {FileName}", uploadedName);
                return uploadedName;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"Video upload failed with status {response.StatusCode}: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload video: {FilePath}", filePath);
            throw;
        }
    }

    /// <summary>
    /// Confirms an uploaded file is actually retrievable from ComfyUI's input folder via /view.
    /// A successful /upload response is not proof the bytes were persisted (a proxy or custom node
    /// may answer 2xx without writing the file), so this guards against queueing a workflow that
    /// references a name the server can't load. Throws if the file is missing or empty.
    /// </summary>
    public async Task VerifyInputFileExistsAsync(string filename, string? subfolder, CancellationToken cancellationToken = default)
    {
        var url = $"/view?filename={Uri.EscapeDataString(filename)}" +
                  $"&subfolder={Uri.EscapeDataString(subfolder ?? "")}" +
                  "&type=input";

        try
        {
            // ResponseHeadersRead so we don't download the whole video just to confirm it exists.
            using var response = await _httpClient.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Uploaded file '{filename}' is not present on the ComfyUI server " +
                    $"(/view?...&type=input returned {(int)response.StatusCode} {response.StatusCode}). " +
                    "The upload reported success but the file was not persisted to the input folder.");
            }

            if (response.Content.Headers.ContentLength is 0)
            {
                throw new InvalidOperationException(
                    $"Uploaded file '{filename}' exists on the ComfyUI server but is empty (0 bytes).");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to verify uploaded file '{filename}' on the ComfyUI server.", ex);
        }
    }

    public async Task<string> UploadAudioAsync(string filePath, string type = "input", CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File not found: {filePath}");
            }

            var fileInfo = new FileInfo(filePath);
            _logger.LogInfo("Uploading audio: {FilePath} ({FileSize} bytes)", filePath, fileInfo.Length);

            using var content = new MultipartFormDataContent();
            using var fileStream = File.OpenRead(filePath);
            using var fileContent = new StreamContent(fileStream);

            // Set appropriate content type for audio files
            var extension = Path.GetExtension(filePath).ToLower();
            var contentType = extension switch
            {
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".ogg" => "audio/ogg",
                ".flac" => "audio/flac",
                ".m4a" => "audio/mp4",
                ".aac" => "audio/aac",
                _ => "audio/mpeg" // Default fallback
            };

            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

            // ComfyUI uses /upload/image for all file types (image, video, audio)
            // The 'image' field name is used for all uploads in ComfyUI
            content.Add(fileContent, "image", Path.GetFileName(filePath));
            content.Add(new StringContent(type), "type");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            uploadCts.CancelAfter(_settings.UploadTimeoutMilliseconds);
            var response = await _httpClient.PostAsync("/upload/image", content, uploadCts.Token);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<UploadResponse>(responseContent);

                _logger.LogInfo("Audio uploaded successfully in {ElapsedMs}ms: {FileName}",
                    stopwatch.ElapsedMilliseconds, result?.Name ?? "unknown");

                return result?.Name ?? throw new InvalidOperationException("Upload response missing filename");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"Audio upload failed with status {response.StatusCode}: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload audio: {FilePath}", filePath);
            throw;
        }
    }

    public async Task<string> SubmitPromptAsync(object workflow, string clientId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo("Submitting workflow for client: {ClientId}", clientId);

            var request = new PromptRequest
            {
                Prompt = workflow,
                ClientId = clientId,
                ExtraData = new ExtraData
                {
                    ExtraPnginfo = new Dictionary<string, object>
                    {
                        ["workflow"] = BuildUiWorkflow(workflow)
                    }
                }
            };

            // Log the request JSON for debugging
            var requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = false });
            _logger.LogInfo("Sending prompt request: {RequestJson}", requestJson.Substring(0, Math.Min(500, requestJson.Length)));

            using var promptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            promptCts.CancelAfter(_settings.ConnectionTimeout);
            var response = await _httpClient.PostAsJsonAsync("/prompt", request, promptCts.Token);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<PromptResponse>(responseContent);
                
                _logger.LogInfo("Workflow submitted successfully: {PromptId}", result?.PromptId ?? "unknown");
                
                return result?.PromptId ?? throw new InvalidOperationException("Prompt response missing ID");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"Prompt submission failed with status {response.StatusCode}: {errorContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit workflow");
            throw;
        }
    }

    // ShowText|pysssss requires extra_pnginfo.workflow to have a "nodes" array (UI format).
    // The API format is a flat dict keyed by node ID, so we convert it here.
    private static object BuildUiWorkflow(object workflow)
    {
        try
        {
            var json = workflow is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(workflow);
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (dict == null) return workflow;

            var nodes = dict.Select(kv =>
            {
                var node = new Dictionary<string, object> { ["id"] = kv.Key };
                if (kv.Value.TryGetProperty("class_type", out var ct))
                    node["type"] = ct.GetString() ?? string.Empty;
                if (kv.Value.TryGetProperty("_meta", out var meta) &&
                    meta.TryGetProperty("title", out var title))
                    node["title"] = title.GetString() ?? string.Empty;
                // UI-format nodes always carry inputs/outputs slot arrays. Some nodes
                // (e.g. Impact-Pack "Switch (Any)") iterate node['inputs'] on the
                // extra_pnginfo workflow, so the key must exist to avoid KeyError.
                node["inputs"] = new List<object>();
                node["outputs"] = new List<object>();
                return (object)node;
            }).ToList();

            return new Dictionary<string, object> { ["nodes"] = nodes, ["links"] = new List<object>() };
        }
        catch
        {
            return workflow;
        }
    }

    public async Task<QueueResponse> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/queue", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<QueueResponse>(responseContent);

                return result ?? new QueueResponse();
            }
            else
            {
                throw new HttpRequestException($"Failed to get queue with status {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queue information");
            throw;
        }
    }

    public async Task<byte[]?> DownloadOutputImageAsync(string filename, string subfolder = "", CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo($"Downloading output image: {filename}");

            // If filename contains a path separator and no explicit subfolder was provided,
            // split it into subfolder + filename for ComfyUI's /view endpoint
            if (string.IsNullOrEmpty(subfolder) && filename.Contains('/'))
            {
                var lastSlash = filename.LastIndexOf('/');
                subfolder = filename.Substring(0, lastSlash);
                filename = filename.Substring(lastSlash + 1);
                _logger.LogInfo($"Split path into subfolder='{subfolder}', filename='{filename}'");
            }

            // Build the URL with query parameters
            var url = $"/view?filename={Uri.EscapeDataString(filename)}";
            if (!string.IsNullOrEmpty(subfolder))
            {
                url += $"&subfolder={Uri.EscapeDataString(subfolder)}";
            }

            // Log the full URL for debugging
            var baseUrl = _httpClient.BaseAddress?.ToString()?.TrimEnd('/') ?? "";
            var fullUrl = $"{baseUrl}{url}";
            _logger.LogInfo($"Image download URL: {fullUrl}");

            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var imageData = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                _logger.LogInfo($"Successfully downloaded image: {filename} ({imageData.Length} bytes)");
                return imageData;
            }
            else
            {
                _logger.LogError($"Failed to download image {filename} with status: {response.StatusCode} from URL: {fullUrl}");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to download output image: {filename}");
            return null;
        }
    }

    public async Task<byte[]?> DownloadOutputVideoAsync(string filename, string subfolder = "", CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo("Downloading output video: {Filename}", filename);

            // Try different URL patterns that ComfyUI might use for videos
            var urlPatterns = new List<string>
            {
                $"/view?filename={Uri.EscapeDataString(filename)}", // Standard pattern
                $"/view/{Uri.EscapeDataString(filename)}", // Direct path pattern
                $"/api/view?filename={Uri.EscapeDataString(filename)}", // API prefix pattern
                // Try with content type parameter for videos
                $"/view?filename={Uri.EscapeDataString(filename)}&type=video",
                // Try with format parameter
                $"/view?filename={Uri.EscapeDataString(filename)}&format=mp4",
                // Try without any encoding (just in case)
                $"/view?filename={filename}",
            };

            // If subfolder is provided, try those patterns too
            if (!string.IsNullOrEmpty(subfolder))
            {
                urlPatterns.AddRange(new[]
                {
                    $"/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}",
                    $"/view/{Uri.EscapeDataString(subfolder)}/{Uri.EscapeDataString(filename)}",
                    $"/api/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}",
                    $"/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type=video",
                    $"/view?filename={filename}&subfolder={subfolder}",
                });
            }

            // Also try to find the file by checking if the extension affects the URL
            if (filename.EndsWith(".mp4"))
            {
                urlPatterns.AddRange(new[]
                {
                    $"/view?filename={filename.Replace(".mp4", "")}&format=mp4",
                    $"/view?filename={filename.Replace(".mp4", ".webm")}", // Try webm extension
                    $"/view?filename={filename.Replace(".mp4", ".avi")}", // Try avi extension
                });
            }

            foreach (var url in urlPatterns)
            {
                // Fix double slash issue in URL construction
                var baseUrl = _httpClient.BaseAddress?.ToString()?.TrimEnd('/') ?? "";
                var fullUrl = $"{baseUrl}{url}";
                _logger.LogInfo($"Trying download URL: {fullUrl}");

                // Create a new request with necessary headers for ComfyUI
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Accept", "video/*, */*");

                try
                {
                    var response = await _httpClient.SendAsync(request, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var videoData = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                        _logger.LogInfo($"Successfully downloaded video: {filename} ({videoData.Length} bytes) from URL: {url}");
                        return videoData;
                    }
                    else
                    {
                        _logger.LogWarning($"Failed to download video {filename} with status: {response.StatusCode} from URL: {fullUrl}");

                        // Log more details for debugging
                        var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (!string.IsNullOrEmpty(errorContent) && errorContent.Length < 200)
                        {
                            _logger.LogWarning($"Error response content: {errorContent}");
                        }

                        // Continue to next URL pattern
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Exception trying to download from {url}: {ex.Message}");
                    // Continue to next URL pattern
                }
            }

            _logger.LogError($"Failed to download video {filename} using all URL patterns");

            // Try to get more info about what's happening
            await TestVideoEndpointAsync();

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download output video: {Filename}", filename);
            return null;
        }
    }

    public async Task<List<string>> GetOutputFilesAsync(string subfolder = "", string fileFilter = "", CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo("Getting output files list from ComfyUI");

            // First try to get history
            var url = "/history";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogInfo("History response received, parsing for outputs...");

                // Parse the history response to find recent outputs
                var history = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseContent);
                var files = new List<string>();

                if (history != null && history.Count > 0)
                {
                    // Get all prompt entries, sorted by key (assuming it's timestamp-based)
                    var sortedEntries = history.OrderByDescending(kvp => kvp.Key);

                    foreach (var entry in sortedEntries.Take(5)) // Check last 5 entries
                    {
                        var historyEntry = entry.Value;

                        // Try different structures that ComfyUI might return
                        JsonElement outputs = default;

                        // Check for outputs in different locations
                        if (historyEntry.TryGetProperty("outputs", out outputs))
                        {
                            _logger.LogInfo("Found outputs in main outputs property");
                        }
                        else if (historyEntry.TryGetProperty("result", out var result) &&
                                result.TryGetProperty("outputs", out outputs))
                        {
                            _logger.LogInfo("Found outputs in result.outputs property");
                        }

                        if (!outputs.Equals(default(JsonElement)))
                        {
                            foreach (var output in outputs.EnumerateObject())
                            {
                                // Check for images (for backward compatibility)
                                if (output.Value.TryGetProperty("images", out var images))
                                {
                                    foreach (var image in images.EnumerateArray())
                                    {
                                        if (image.TryGetProperty("filename", out var filenameProp))
                                        {
                                            var filename = filenameProp.GetString();
                                            if (!string.IsNullOrEmpty(filename))
                                            {
                                                // Check if there's a subfolder and include it in the path
                                                var subfolderStr = "";
                                                if (image.TryGetProperty("subfolder", out var subfolderProp))
                                                {
                                                    subfolderStr = subfolderProp.GetString() ?? "";
                                                }
                                                var fullPath = string.IsNullOrEmpty(subfolderStr) ? filename : $"{subfolderStr}/{filename}";
                                                files.Add(fullPath);
                                                _logger.LogInfo($"Found output image: {fullPath}");
                                            }
                                        }
                                    }
                                }

                                // Check for videos (new logic)
                                if (output.Value.TryGetProperty("videos", out var videos))
                                {
                                    foreach (var video in videos.EnumerateArray())
                                    {
                                        if (video.TryGetProperty("filename", out var filenameProp))
                                        {
                                            var filename = filenameProp.GetString();
                                            if (!string.IsNullOrEmpty(filename))
                                            {
                                                // Check if there's a subfolder and include it in the path
                                                var subfolderStr = "";
                                                if (video.TryGetProperty("subfolder", out var subfolderProp))
                                                {
                                                    subfolderStr = subfolderProp.GetString() ?? "";
                                                }
                                                var fullPath = string.IsNullOrEmpty(subfolderStr) ? filename : $"{subfolderStr}/{filename}";
                                                files.Add(fullPath);
                                                _logger.LogInfo($"Found output video: {fullPath}");
                                            }
                                        }
                                    }
                                }

                                // Check for files (generic case - some workflows might use this)
                                if (output.Value.TryGetProperty("files", out var fileProps))
                                {
                                    foreach (var file in fileProps.EnumerateArray())
                                    {
                                        if (file.TryGetProperty("filename", out var filenameProp))
                                        {
                                            var filename = filenameProp.GetString();
                                            if (!string.IsNullOrEmpty(filename))
                                            {
                                                // Check if there's a subfolder and include it in the path
                                                var subfolderStr = "";
                                                if (file.TryGetProperty("subfolder", out var subfolderProp))
                                                {
                                                    subfolderStr = subfolderProp.GetString() ?? "";
                                                }
                                                var fullPath = string.IsNullOrEmpty(subfolderStr) ? filename : $"{subfolderStr}/{filename}";
                                                files.Add(fullPath);
                                                _logger.LogInfo($"Found output file: {fullPath}");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // If no files found via history, try the /view endpoint list approach
                if (!files.Any())
                {
                    _logger.LogWarning("No files found in history, trying alternative approach...");

                    // Try to find files by checking common patterns in the output
                    // This is a fallback approach
                    var commonPatterns = new[] { "z-image_", "output_", "ComfyUI_" };
                    foreach (var pattern in commonPatterns)
                    {
                        // Since we can't list directories via HTTP, we'll try to guess the filename
                        // based on the prompt ID if we have one
                        if (history != null && history.Count > 0)
                        {
                            var lastPromptId = history.Keys.LastOrDefault();
                            if (!string.IsNullOrEmpty(lastPromptId))
                            {
                                var guessFilename = $"{pattern}{lastPromptId.Substring(0, 8)}.png";
                                files.Add(guessFilename);
                                _logger.LogInfo($"Trying guessed filename: {guessFilename}");
                            }
                        }
                    }
                }

                return files.Distinct().ToList();
            }
            else
            {
                _logger.LogError("Failed to get output files with status: {StatusCode}", response.StatusCode);
                return new List<string>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get output files list");
            return new List<string>();
        }
    }

    /// <summary>
    /// Gets output files for a specific prompt ID from the history endpoint
    /// </summary>
    public async Task<List<string>> GetOutputFilesForPromptAsync(string promptId, CancellationToken cancellationToken = default)
    {
        var files = new List<string>();
        try
        {
            _logger.LogInfo($"Getting output files for prompt: {promptId}");

            var url = "/history";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var history = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseContent);

                if (history != null && history.TryGetValue(promptId, out var historyEntry))
                {
                    _logger.LogInfo($"Found history entry for prompt: {promptId}");

                    // Try different structures that ComfyUI might return
                    JsonElement outputs = default;

                    if (historyEntry.TryGetProperty("outputs", out outputs))
                    {
                        _logger.LogInfo("Found outputs in main outputs property");
                    }
                    else if (historyEntry.TryGetProperty("result", out var result) &&
                            result.TryGetProperty("outputs", out outputs))
                    {
                        _logger.LogInfo("Found outputs in result.outputs property");
                    }

                    if (!outputs.Equals(default(JsonElement)))
                    {
                        foreach (var output in outputs.EnumerateObject())
                        {
                            // Check for images
                            if (output.Value.TryGetProperty("images", out var images))
                            {
                                foreach (var image in images.EnumerateArray())
                                {
                                    if (image.TryGetProperty("filename", out var filenameProp))
                                    {
                                        var filename = filenameProp.GetString();
                                        if (!string.IsNullOrEmpty(filename))
                                        {
                                            var subfolderStr = "";
                                            if (image.TryGetProperty("subfolder", out var subfolderProp))
                                            {
                                                subfolderStr = subfolderProp.GetString() ?? "";
                                            }
                                            var fullPath = string.IsNullOrEmpty(subfolderStr) ? filename : $"{subfolderStr}/{filename}";
                                            files.Add(fullPath);
                                            _logger.LogInfo($"Found output image for prompt: {fullPath}");
                                        }
                                    }
                                }
                            }

                            // Check for videos (VHS_VideoCombine outputs here)
                            if (output.Value.TryGetProperty("videos", out var videoProps))
                            {
                                foreach (var video in videoProps.EnumerateArray())
                                {
                                    if (video.TryGetProperty("filename", out var filenameProp))
                                    {
                                        var filename = filenameProp.GetString();
                                        if (!string.IsNullOrEmpty(filename))
                                        {
                                            var subfolderStr = "";
                                            if (video.TryGetProperty("subfolder", out var subfolderProp))
                                            {
                                                subfolderStr = subfolderProp.GetString() ?? "";
                                            }
                                            var fullPath = string.IsNullOrEmpty(subfolderStr) ? filename : $"{subfolderStr}/{filename}";
                                            files.Add(fullPath);
                                            _logger.LogInfo($"Found output video for prompt: {fullPath}");
                                        }
                                    }
                                }
                            }

                            // Check for files (generic case)
                            if (output.Value.TryGetProperty("files", out var fileProps))
                            {
                                foreach (var file in fileProps.EnumerateArray())
                                {
                                    if (file.TryGetProperty("filename", out var filenameProp))
                                    {
                                        var filename = filenameProp.GetString();
                                        if (!string.IsNullOrEmpty(filename))
                                        {
                                            var subfolderStr = "";
                                            if (file.TryGetProperty("subfolder", out var subfolderProp))
                                            {
                                                subfolderStr = subfolderProp.GetString() ?? "";
                                            }
                                            var fullPath = string.IsNullOrEmpty(subfolderStr) ? filename : $"{subfolderStr}/{filename}";
                                            files.Add(fullPath);
                                            _logger.LogInfo($"Found output file for prompt: {fullPath}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    _logger.LogWarning($"No history entry found for prompt: {promptId}");
                }
            }
            else
            {
                _logger.LogError("Failed to get history with status: {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get output files for prompt: {PromptId}", promptId);
        }

        return files;
    }

    /// <summary>
    /// Returns this prompt's output media grouped by the node that produced them.
    /// Each value is a list of "subfolder/filename" strings. Reads images, videos, gifs
    /// (VHS_VideoCombine reports mp4/webm under "gifs") and files.
    /// </summary>
    public async Task<Dictionary<string, List<string>>> GetOutputsByNodeAsync(string promptId, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, List<string>>();
        try
        {
            var response = await _httpClient.GetAsync("/history", cancellationToken);
            if (!response.IsSuccessStatusCode) return result;

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var history = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(content);
            if (history == null || !history.TryGetValue(promptId, out var entry)) return result;

            JsonElement outputs;
            if (!entry.TryGetProperty("outputs", out outputs) &&
                !(entry.TryGetProperty("result", out var r) && r.TryGetProperty("outputs", out outputs)))
                return result;

            foreach (var node in outputs.EnumerateObject())
            {
                var files = new List<string>();
                foreach (var key in new[] { "images", "videos", "gifs", "files" })
                {
                    if (!node.Value.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (!item.TryGetProperty("filename", out var fnProp)) continue;
                        var filename = fnProp.GetString();
                        if (string.IsNullOrEmpty(filename)) continue;
                        var subfolder = item.TryGetProperty("subfolder", out var sfProp) ? sfProp.GetString() ?? "" : "";
                        files.Add(string.IsNullOrEmpty(subfolder) ? filename : $"{subfolder}/{filename}");
                    }
                }
                if (files.Count > 0) result[node.Name] = files;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get node outputs for prompt: {PromptId}", promptId);
        }
        return result;
    }

    /// <summary>
    /// Downloads a file from ComfyUI's /view endpoint using an explicit type ("output"/"temp"/"input").
    /// Falls back to the multi-pattern <see cref="DownloadOutputVideoAsync"/> if the direct request fails.
    /// </summary>
    public async Task<byte[]?> DownloadViewFileAsync(string filename, string subfolder, string type, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"/view?filename={Uri.EscapeDataString(filename)}" +
                      $"&subfolder={Uri.EscapeDataString(subfolder ?? "")}" +
                      $"&type={Uri.EscapeDataString(string.IsNullOrEmpty(type) ? "output" : type)}";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length > 0)
                {
                    _logger.LogInfo($"Downloaded {filename} ({bytes.Length} bytes, type={type})");
                    return bytes;
                }
            }
            else
            {
                _logger.LogWarning($"/view {filename} (type={type}) returned {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"DownloadViewFileAsync failed for {filename}: {ex.Message}");
        }
        return await DownloadOutputVideoAsync(filename, subfolder ?? "", cancellationToken);
    }

    public async Task<byte[]?> TryDownloadRecentOutputAsync(string promptId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo($"Attempting to download recent output for prompt: {promptId}");

            // Try common filename patterns that ComfyUI generates
            var patterns = new[]
            {
                // Try various ComfyUI naming patterns
                "z-image_00000_.png",  // Common pattern with counter
                "z-image_00001_.png",
                "z-image_00002_.png",
                "z-image.png",         // Simple version
                "z-image_0.png",       // With single digit
                "output.png",          // Generic output
                $"{promptId}.png",     // Using prompt ID
                $"ComfyUI_00001_.png", // Alternative naming
                $"z-image_{DateTime.Now:yyyyMMdd_HHmmss}.png", // Timestamp pattern
                $"z-image_{promptId.Substring(0, Math.Min(8, promptId.Length))}.png"
            };

            foreach (var pattern in patterns)
            {
                _logger.LogInfo($"Trying to download: {pattern}");
                var imageData = await DownloadOutputImageAsync(pattern, "", cancellationToken);
                if (imageData != null)
                {
                    _logger.LogInfo($"Successfully downloaded image: {pattern}");
                    return imageData;
                }
            }

            // If all patterns fail, try to get the actual filename from the workflow execution
            // by checking the /history endpoint again but with more detailed logging
            var historyUrl = "/history";
            var response = await _httpClient.GetAsync(historyUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var historyContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogInfo($"Full history response: {historyContent}");

                // Look for any mention of files in the response
                if (historyContent.Contains("\"filename\""))
                {
                    _logger.LogInfo("Found filename references in history response");
                    // Parse and extract actual filenames
                    var history = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(historyContent);
                    if (history != null && history.TryGetValue(promptId, out var promptHistory))
                    {
                        _logger.LogInfo($"Found history for our prompt ID: {promptId}");
                        // Extract actual filenames from this specific prompt
                    }
                }
            }

            // As a last resort, try to access ComfyUI's output with current timestamp pattern
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var timestampPattern = $"z-image_{timestamp}.png";
            _logger.LogInfo($"Last resort attempt: trying timestamp pattern {timestampPattern}");

            var timestampImage = await DownloadOutputImageAsync(timestampPattern, "", cancellationToken);
            if (timestampImage != null)
            {
                return timestampImage;
            }

            // Also try without the z-image prefix if the workflow saves with different naming
            var simplePattern = $"{timestamp}.png";
            _logger.LogInfo($"Last resort attempt: trying simple pattern {simplePattern}");

            var simpleImage = await DownloadOutputImageAsync(simplePattern, "", cancellationToken);
            if (simpleImage != null)
            {
                return simpleImage;
            }

            _logger.LogWarning("Could not find any downloadable output images");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download recent output");
            return null;
        }
    }

    public async Task<bool> TestVideoEndpointAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo("Testing ComfyUI video/file access endpoints...");

            // Test basic server connectivity
            var rootResponse = await _httpClient.GetAsync("/", cancellationToken);
            _logger.LogInfo($"Root endpoint status: {rootResponse.StatusCode}");

            // Try common ComfyUI endpoints
            var endpointsToTest = new[]
            {
                "/view",
                "/view/",
                "/api/view",
                "/system_stats",
                "/history",
                "/queue",
                "/prompt",
                "/object_info",
                "/output",
                "/output/",
                "/files",
                "/files/",
                "/static",
                "/static/",
                "/serve",
                "/serve/",
                "/download",
                "/download/"
            };

            foreach (var endpoint in endpointsToTest)
            {
                try
                {
                    var response = await _httpClient.GetAsync(endpoint, cancellationToken);
                    _logger.LogInfo($"Endpoint {endpoint}: {response.StatusCode}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to test endpoint {endpoint}: {ex.Message}");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test video endpoints");
            return false;
        }
    }

    /// <summary>
    /// Returns every LoRA filename ComfyUI exposes (from /object_info/LoraLoader's lora_name enum).
    /// These are paths relative to the loras root, exactly as the server resolves them (so they work
    /// even when the loras live on a remote/mounted drive the client can't see on disk).
    /// </summary>
    public async Task<List<string>> GetLoraFilenamesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<string>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var response = await _httpClient.GetAsync("/object_info/LoraLoader", cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInfo($"GetLoraFilenamesAsync: /object_info/LoraLoader returned {response.StatusCode}");
                return result;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);

            // Shape: { "LoraLoader": { "input": { "required": { "lora_name": [ [names...], {..} ] } } } }
            if (doc.RootElement.TryGetProperty("LoraLoader", out var node) &&
                node.TryGetProperty("input", out var input) &&
                input.TryGetProperty("required", out var required) &&
                required.TryGetProperty("lora_name", out var loraName) &&
                loraName.ValueKind == JsonValueKind.Array && loraName.GetArrayLength() > 0)
            {
                var names = loraName[0];
                if (names.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in names.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            result.Add(item.GetString()!);
                }
            }
            _logger.LogInfo($"GetLoraFilenamesAsync: {result.Count} LoRAs reported by ComfyUI");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"GetLoraFilenamesAsync failed: {ex.Message}");
        }
        return result;
    }

    public async Task<byte[]?> TryDownloadRecentVideoAsync(string promptId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo($"Attempting to download recent video for prompt: {promptId}");

            // Try common video filename patterns that ComfyUI generates
            var patterns = new[]
            {
                // Try various ComfyUI video naming patterns
                "video_00000_.mp4",    // Common pattern with counter
                "video_00001_.mp4",
                "video_00002_.mp4",
                "video.mp4",           // Simple version
                "video_0.mp4",         // With single digit
                "output.mp4",          // Generic output
                $"{promptId}.mp4",     // Using prompt ID
                "ComfyUI_00001_.mp4", // Alternative naming
                "ComfyUI_00002_.mp4",
                "ComfyUI_00003_.mp4",
                "ComfyUI_00004_.mp4",
                "ComfyUI_00005_.mp4",
                "ComfyUI_00006_.mp4",
                "ComfyUI_00007_.mp4",
                "ComfyUI_00008_.mp4",
                "ComfyUI_00009_.mp4",
                "ComfyUI_00010_.mp4",
                "ComfyUI_00011_.mp4",
                "ComfyUI_00012_.mp4",
                "ComfyUI_00013_.mp4",
                "ComfyUI_00014_.mp4",
                "ComfyUI_00015_.mp4",
                $"video_{DateTime.Now:yyyyMMdd_HHmmss}.mp4", // Timestamp pattern
                $"video_{promptId.Substring(0, Math.Min(8, promptId.Length))}.mp4",
                "WanVideo_00000_.mp4", // Common for Wan2 video model
                "WanVideo_00001_.mp4"
            };

            foreach (var pattern in patterns)
            {
                _logger.LogInfo($"Trying to download video: {pattern}");
                var videoData = await DownloadOutputVideoAsync(pattern, "", cancellationToken);
                if (videoData != null)
                {
                    _logger.LogInfo($"Successfully downloaded video: {pattern}");
                    return videoData;
                }
            }

            // As a last resort, try to access ComfyUI's output with current timestamp pattern
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var timestampPattern = $"video_{timestamp}.mp4";
            _logger.LogInfo($"Last resort attempt: trying timestamp pattern {timestampPattern}");

            var timestampVideo = await DownloadOutputVideoAsync(timestampPattern, "", cancellationToken);
            if (timestampVideo != null)
            {
                return timestampVideo;
            }

            // Also try without the video prefix if the workflow saves with different naming
            var simplePattern = $"{timestamp}.mp4";
            _logger.LogInfo($"Last resort attempt: trying simple pattern {simplePattern}");

            var simpleVideo = await DownloadOutputVideoAsync(simplePattern, "", cancellationToken);
            if (simpleVideo != null)
            {
                return simpleVideo;
            }

            _logger.LogWarning("Could not find any downloadable output videos");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download recent video");
            return null;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient?.Dispose();
            _disposed = true;
        }
    }
}