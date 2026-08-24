using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;

namespace FlipPix.ComfyUI.Http;

public class ComfyUIHttpClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IAppLogger _logger;
    private readonly ComfyUISettings _settings;
    private bool _disposed = false;

    /// <summary>
    /// Optional handler that can install model files a workflow needs but ComfyUI is missing
    /// (download or copy from a user-selected folder). Set by the UI layer after DI is built.
    /// When null, missing models fail the submission with <see cref="MissingModelsException"/>.
    /// </summary>
    public IMissingModelResolver? MissingModelResolver { get; set; }

    /// <summary>
    /// Optional handler that can install custom-node packs a workflow needs but ComfyUI is missing
    /// (git clone into custom_nodes + restart). Set by the UI layer after DI is built. When null,
    /// missing nodes fail the submission with <see cref="MissingNodesException"/>.
    /// </summary>
    public IMissingNodeResolver? MissingNodeResolver { get; set; }

    // Cached path separator of the connected ComfyUI host ('/' on Linux/Mac, '\' on
    // Windows). Detected once per session from /system_stats and used to rewrite
    // model-file paths in submitted workflows so the same JSON runs on either host.
    private char? _hostPathSeparator;

    // Short-lived cache of the full /object_info response, used by pre-submit validation.
    // ComfyUI's object_info can be several MB; caching avoids refetching it for every submit
    // (e.g. batch/seed reruns). TTL is short so newly-added models are picked up quickly.
    private string? _objectInfoCacheJson;
    private DateTime _objectInfoCacheUtc;
    private static readonly TimeSpan ObjectInfoCacheTtl = TimeSpan.FromSeconds(60);

    // Model-weight file extensions whose subfolder separator must match the host OS.
    private static readonly string[] ModelFileExtensions =
        { ".safetensors", ".ckpt", ".pt", ".pth", ".bin", ".gguf", ".sft", ".onnx" };

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

            // Rewrite model-file path separators to match the ComfyUI host OS so a
            // workflow authored on Linux (forward slashes) also validates on a Windows
            // host (backslashes) and vice-versa — without hand-editing the JSON.
            var hostSep = await GetHostPathSeparatorAsync(cancellationToken);
            workflow = NormalizeModelPathSeparators(workflow, hostSep);

            // The Nvidia RTX pack renamed this node's widgets (scale/deblur -> resize_type). Write both
            // sets so a workflow exported against either version runs here; a re-export from an older
            // ComfyUI can otherwise silently break a tab. See RtxSuperResolutionCompat.
            workflow = RtxSuperResolutionCompat.Normalize(workflow, m => _logger.LogInfo(m));

            // Pre-submit validation (nodes first): catch custom-node types the workflow references
            // but ComfyUI doesn't have loaded, and offer to install them instead of failing with
            // ComfyUI's raw "missing_node_type" dump. Done before the model check because a missing
            // node isn't in /object_info, so its model inputs can't be validated until it's installed.
            var missingNodes = await FindMissingNodesAsync(workflow, cancellationToken);
            if (missingNodes.Count > 0)
            {
                if (MissingNodeResolver != null)
                {
                    _logger.LogInfo($"Pre-submit validation found {missingNodes.Count} missing node(s); invoking resolver.");
                    bool resolved;
                    try
                    {
                        resolved = await MissingNodeResolver.TryResolveAsync(missingNodes, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Missing-node resolver threw; surfacing the original error.");
                        resolved = false;
                    }

                    if (resolved)
                    {
                        // ComfyUI was (re)started with the new packs; its node/model lists changed.
                        InvalidateObjectInfoCache();
                        missingNodes = await FindMissingNodesAsync(workflow, cancellationToken);
                        if (missingNodes.Count == 0)
                            _logger.LogInfo("All missing nodes resolved; continuing submission.");
                    }
                }

                if (missingNodes.Count > 0)
                {
                    var bullets = string.Join("\n", missingNodes.Select(n =>
                        string.IsNullOrEmpty(n.PackName) ? $"  • {n.ClassType}" : $"  • {n.ClassType}  ({n.PackName})"));
                    var message =
                        "This workflow needs custom node(s) that aren't installed in the connected ComfyUI:\n\n" +
                        bullets +
                        "\n\nInstall the custom node pack(s) into ComfyUI (e.g. via ComfyUI-Manager's " +
                        "\"Install Missing Custom Nodes\"), restart ComfyUI, then try again.";
                    _logger.LogWarning($"Pre-submit validation blocked submission; {missingNodes.Count} missing node(s): {string.Join(", ", missingNodes.Select(n => n.ClassType))}");
                    throw new FlipPix.ComfyUI.MissingNodesException(message, missingNodes);
                }
            }

            // Pre-submit validation: catch model files the workflow references but ComfyUI doesn't
            // have, and fail with a clear message instead of ComfyUI's raw "value_not_in_list" dump.
            var missingModels = await FindMissingModelsAsync(workflow, cancellationToken);
            if (missingModels.Count > 0)
            {
                // Give the resolver (UI) a chance to download/copy the files in. If it succeeds,
                // re-validate against a fresh /object_info and proceed when nothing is missing.
                if (MissingModelResolver != null)
                {
                    _logger.LogInfo($"Pre-submit validation found {missingModels.Count} missing model(s); invoking resolver.");
                    bool resolved;
                    try
                    {
                        resolved = await MissingModelResolver.TryResolveAsync(missingModels, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Missing-model resolver threw; surfacing the original error.");
                        resolved = false;
                    }

                    if (resolved)
                    {
                        InvalidateObjectInfoCache();
                        missingModels = await FindMissingModelsAsync(workflow, cancellationToken);
                        if (missingModels.Count == 0)
                            _logger.LogInfo("All missing models resolved; continuing submission.");
                    }
                }

                if (missingModels.Count > 0)
                {
                    var bullets = string.Join("\n", missingModels.Select(m => $"  • {m.Name}"));
                    var message =
                        "This workflow needs model file(s) that aren't installed in the connected ComfyUI:\n\n" +
                        bullets +
                        "\n\nInstall the missing model(s) into ComfyUI's model folders (or choose an installed " +
                        "alternative in the workflow), then try again.";
                    _logger.LogWarning($"Pre-submit validation blocked submission; {missingModels.Count} missing model(s): {string.Join(", ", missingModels.Select(m => m.Name))}");
                    throw new FlipPix.ComfyUI.MissingModelsException(message, missingModels);
                }
            }

            // Self-healing pass: pull any numeric widget back inside the limits this ComfyUI
            // declares, so a node pack tightening its max (seeds, batch sizes, steps) doesn't
            // fail the run and force a manual edit.
            workflow = await ClampOutOfRangeInputsAsync(workflow, cancellationToken);

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

                // ComfyUI returns HTTP 200 with a prompt_id even when SOME output nodes
                // failed validation: those nodes (and their whole upstream chain) are
                // silently pruned from execution and reported in `node_errors`, while any
                // still-valid output runs. That means the prompt can "succeed" yet never
                // produce the image the user asked for (e.g. a required node input the
                // exported API JSON omits). Treat a populated node_errors as a hard failure
                // so the caller sees a clear message instead of a phantom "done" with no output.
                if (result != null && result.NodeErrors.Count > 0)
                {
                    var detail = FormatNodeErrors(responseContent);
                    _logger.LogError($"Prompt accepted but {result.NodeErrors.Count} node(s) failed validation and were dropped: {detail}");
                    throw new FlipPix.ComfyUI.Exceptions.ComfyUIExecutionException(
                        "ComfyUI rejected part of the workflow during validation, so it would not have produced an output:\n\n"
                        + detail
                        + "\n\nThis usually means a node input is missing or invalid (e.g. a custom node gained a required input the saved workflow doesn't set).",
                        result.PromptId);
                }

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

    /// <summary>
    /// Turns ComfyUI's /prompt `node_errors` map into a readable, per-node bullet list.
    /// Shape: { "&lt;nodeId&gt;": { "class_type": ..., "errors": [ { "message", "details" }, ... ] } }.
    /// </summary>
    private static string FormatNodeErrors(string responseContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            if (!doc.RootElement.TryGetProperty("node_errors", out var nodeErrors) ||
                nodeErrors.ValueKind != JsonValueKind.Object)
                return "(no error detail)";

            var lines = new List<string>();
            foreach (var node in nodeErrors.EnumerateObject())
            {
                var classType = node.Value.TryGetProperty("class_type", out var ct) ? ct.GetString() : null;
                var header = string.IsNullOrEmpty(classType)
                    ? $"  • node {node.Name}:"
                    : $"  • node {node.Name} ({classType}):";
                lines.Add(header);

                if (node.Value.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in errs.EnumerateArray())
                    {
                        var msg = e.TryGetProperty("message", out var m) ? m.GetString() : null;
                        var details = e.TryGetProperty("details", out var d) ? d.GetString() : null;
                        var text = string.IsNullOrEmpty(details) ? msg : $"{msg}: {details}";
                        if (!string.IsNullOrEmpty(text)) lines.Add($"      - {text}");
                    }
                }
            }
            return lines.Count > 0 ? string.Join("\n", lines) : "(no error detail)";
        }
        catch
        {
            return "(could not parse node_errors)";
        }
    }

    /// <summary>
    /// Asks ComfyUI to unload models and free VRAM/RAM via POST /free. Used when switching
    /// between heavy workflows so the next one starts with an empty card instead of inheriting
    /// the previous workflow's resident model (which forces lowvram weight-streaming and tanks
    /// throughput). Best-effort: logs and returns false on failure rather than throwing.
    /// </summary>
    public async Task<bool> FreeMemoryAsync(bool unloadModels = true, bool freeMemory = true, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new { unload_models = unloadModels, free_memory = freeMemory };

            using var freeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            freeCts.CancelAfter(_settings.ConnectionTimeout);
            var response = await _httpClient.PostAsJsonAsync("/free", payload, freeCts.Token);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInfo("Freed ComfyUI memory (unload_models={UnloadModels}, free_memory={FreeMemory})", unloadModels, freeMemory);
                return true;
            }

            _logger.LogError("POST /free failed with status: {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to free ComfyUI memory");
            return false;
        }
    }

    /// <summary>
    /// Detects the path separator used by the connected ComfyUI host by reading
    /// system.os from /system_stats ("nt" → Windows '\', otherwise POSIX '/').
    /// Cached for the session; defaults to '/' (the POSIX/Linux case) if detection fails.
    /// </summary>
    public async Task<char> GetHostPathSeparatorAsync(CancellationToken cancellationToken = default)
    {
        if (_hostPathSeparator.HasValue) return _hostPathSeparator.Value;

        var sep = '/';
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_settings.ConnectionTimeout);
            var response = await _httpClient.GetAsync("/system_stats", cts.Token);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("system", out var sys)
                    && sys.TryGetProperty("os", out var osEl)
                    && osEl.GetString() is { Length: > 0 } osName)
                {
                    bool isWindows = osName.Equals("nt", StringComparison.OrdinalIgnoreCase)
                                  || osName.StartsWith("win", StringComparison.OrdinalIgnoreCase);
                    sep = isWindows ? '\\' : '/';
                    _logger.LogInfo("ComfyUI host OS '{Os}' → model path separator '{Sep}'", osName, sep);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not detect ComfyUI host OS, defaulting to '/': {Message}", ex.Message);
        }

        _hostPathSeparator = sep;
        return sep;
    }

    /// <summary>
    /// ComfyUI builds its checkpoint/LoRA/VAE pick-lists with the host OS path
    /// separator (subfolder/file on Linux, subfolder\file on Windows) and validates
    /// submitted values against that list, so a workflow authored on one OS fails on
    /// the other. This rewrites any model-file input value (.safetensors, .gguf, …)
    /// to the host's separator. Non-model strings (prompts, plain filenames) are left
    /// untouched. On any error the original workflow is returned unchanged.
    /// </summary>
    internal static object NormalizeModelPathSeparators(object workflow, char hostSep)
    {
        try
        {
            var json = workflow is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(workflow);
            if (JsonNode.Parse(json) is not JsonObject nodes) return workflow;

            char otherSep = hostSep == '/' ? '\\' : '/';
            foreach (var node in nodes)
            {
                if (node.Value is not JsonObject obj || obj["inputs"] is not JsonObject inputs)
                    continue;

                // Collect first, then assign — mutating a JsonObject mid-enumeration throws.
                var changes = new List<KeyValuePair<string, string>>();
                foreach (var input in inputs)
                {
                    if (input.Value is JsonValue v && v.TryGetValue<string>(out var s)
                        && s.IndexOf(otherSep) >= 0
                        && ModelFileExtensions.Any(ext => s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    {
                        changes.Add(new(input.Key, s.Replace(otherSep, hostSep)));
                    }
                }
                foreach (var c in changes)
                    inputs[c.Key] = c.Value;
            }

            return nodes;
        }
        catch
        {
            return workflow;
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

                            // Check for gifs/audio/files. VHS_VideoCombine (and the LTX seed-hunter
                            // previews) emit their mp4 under "gifs"; without this, /history completion
                            // detection found the entry but collected zero files and spun until timeout.
                            foreach (var mediaKey in new[] { "gifs", "audio", "files" })
                            {
                                if (!output.Value.TryGetProperty(mediaKey, out var mediaProps) ||
                                    mediaProps.ValueKind != JsonValueKind.Array)
                                    continue;
                                foreach (var media in mediaProps.EnumerateArray())
                                {
                                    if (media.TryGetProperty("filename", out var filenameProp))
                                    {
                                        var filename = filenameProp.GetString();
                                        if (!string.IsNullOrEmpty(filename))
                                        {
                                            var subfolderStr = "";
                                            if (media.TryGetProperty("subfolder", out var subfolderProp))
                                            {
                                                subfolderStr = subfolderProp.GetString() ?? "";
                                            }
                                            var fullPath = string.IsNullOrEmpty(subfolderStr) ? filename : $"{subfolderStr}/{filename}";
                                            files.Add(fullPath);
                                            _logger.LogInfo($"Found output {mediaKey} for prompt: {fullPath}");
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
    /// True once ComfyUI has recorded this prompt in /history — which it only does after the prompt has
    /// fully finished, successfully or not.
    /// <para>This is the honest completion test. <see cref="GetOutputFilesForPromptAsync"/> is not: it
    /// returns the prompt's <i>media</i> outputs, and a workflow whose output nodes report only text
    /// (the MiniMaxH3Chain* nodes report their file paths as <c>text</c>) finishes with zero files. Using
    /// the file count as the completion signal makes such a run look like it never landed, and then like
    /// it vanished once it also left /queue.</para>
    /// </summary>
    public async Task<bool> HasHistoryEntryAsync(string promptId, CancellationToken cancellationToken = default)
    {
        try
        {
            // The per-prompt route avoids pulling (and parsing) the entire run history every 5 seconds.
            var response = await _httpClient.GetAsync($"/history/{Uri.EscapeDataString(promptId)}", cancellationToken);
            if (!response.IsSuccessStatusCode) return false;

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(promptId, out _);
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"HasHistoryEntryAsync failed for {promptId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns this prompt's <c>text</c> outputs grouped by the node that produced them.
    /// <para>Nodes that report a result rather than a file — ShowText, and the MiniMaxH3Chain* nodes,
    /// which report the absolute paths of the segments and the assembled video — put it under the
    /// <c>text</c> key, which none of the media readers above look at.</para>
    /// </summary>
    public async Task<Dictionary<string, List<string>>> GetTextOutputsByNodeAsync(string promptId, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, List<string>>();
        try
        {
            var response = await _httpClient.GetAsync($"/history/{Uri.EscapeDataString(promptId)}", cancellationToken);
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
                if (!node.Value.TryGetProperty("text", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    continue;

                var lines = new List<string>();
                foreach (var item in arr.EnumerateArray())
                {
                    var s = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                    if (!string.IsNullOrEmpty(s)) lines.Add(s);
                }
                if (lines.Count > 0) result[node.Name] = lines;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get text outputs for prompt: {PromptId}", promptId);
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

    /// <summary>
    /// Returns the full /object_info JSON, cached briefly (see <see cref="ObjectInfoCacheTtl"/>).
    /// Null on failure.
    /// </summary>
    private async Task<string?> GetObjectInfoJsonAsync(CancellationToken cancellationToken)
    {
        if (_objectInfoCacheJson != null && (DateTime.UtcNow - _objectInfoCacheUtc) < ObjectInfoCacheTtl)
            return _objectInfoCacheJson;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var response = await _httpClient.GetAsync("/object_info", cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning($"GetObjectInfoJsonAsync: /object_info returned {response.StatusCode}");
            return null;
        }
        var json = await response.Content.ReadAsStringAsync(cts.Token);
        _objectInfoCacheJson = json;
        _objectInfoCacheUtc = DateTime.UtcNow;
        return json;
    }

    /// <summary>
    /// True when the prompt is still running or waiting in ComfyUI's queue. Read from the raw
    /// /queue JSON rather than the typed model because ComfyUI serialises queue entries as
    /// heterogeneous arrays ([number, prompt_id, prompt, extra_data, outputs]), which the typed
    /// QueueItem cannot bind. Returns true on any error so a transient network blip is never
    /// mistaken for a lost prompt.
    /// </summary>
    public Task<bool> IsPromptQueuedAsync(string promptId, CancellationToken cancellationToken = default) =>
        IsQueueEntryContainingAsync(promptId, cancellationToken);

    /// <summary>
    /// True when any queued or running entry's JSON contains <paramref name="needle"/>.
    ///
    /// <para>A prompt id is the usual needle (see <see cref="IsPromptQueuedAsync"/>), but a VHS meta batch
    /// re-queues the same graph under <i>new</i> prompt ids until the input runs out, so a chain can only be
    /// followed by something that survives the requeue — a unique <c>filename_prefix</c>, say. Returns true
    /// on any error so a transient network blip never reads as "finished".</para>
    /// </summary>
    public async Task<bool> IsQueueEntryContainingAsync(string needle, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var response = await _httpClient.GetAsync("/queue", cts.Token);
            if (!response.IsSuccessStatusCode) return true;

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            foreach (var section in new[] { "queue_running", "queue_pending" })
            {
                if (!doc.RootElement.TryGetProperty(section, out var arr) || arr.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var entry in arr.EnumerateArray())
                    if (entry.GetRawText().Contains(needle, StringComparison.Ordinal))
                        return true;
            }
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug($"IsQueueEntryContainingAsync failed (assuming still queued): {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Newest output file in /history whose filename starts with <paramref name="filenamePrefix"/>, as
    /// "subfolder/filename" (or just "filename"), or null when the history holds none yet.
    ///
    /// <para>Written for the meta-batch case, where the file cannot be found by prompt id: every sub
    /// execution but the last reports <c>unfinished_batch</c> instead of a file, and the last one lands
    /// under a prompt id the app never submitted. A unique filename prefix is the only thread that runs
    /// through the whole chain — and a match here means the file is <i>finished</i>, since VHS only writes
    /// the history entry once the final batch has been muxed.</para>
    /// </summary>
    public async Task<string?> FindOutputFileFromHistoryAsync(
        string filenamePrefix, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await _httpClient.GetAsync("/history?max_items=64", cts.Token);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            string? match = null;   // history is oldest-first, so the last hit is the newest
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (!entry.Value.TryGetProperty("outputs", out var outputs) ||
                    outputs.ValueKind != JsonValueKind.Object) continue;

                foreach (var node in outputs.EnumerateObject())
                {
                    if (node.Value.ValueKind != JsonValueKind.Object) continue;

                    // VHS reports videos under "gifs"; other nodes use images/videos/files.
                    foreach (var key in new[] { "gifs", "videos", "images", "files" })
                    {
                        if (!node.Value.TryGetProperty(key, out var list) ||
                            list.ValueKind != JsonValueKind.Array) continue;

                        foreach (var file in list.EnumerateArray())
                        {
                            if (file.ValueKind != JsonValueKind.Object) continue;
                            var filename = file.TryGetProperty("filename", out var f) ? f.GetString() : null;
                            if (string.IsNullOrEmpty(filename) ||
                                !filename.StartsWith(filenamePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                            var subfolder = file.TryGetProperty("subfolder", out var sf) ? sf.GetString() : null;
                            match = string.IsNullOrEmpty(subfolder) ? filename : $"{subfolder}/{filename}";
                        }
                    }
                }
            }

            return match;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug($"FindOutputFileFromHistoryAsync failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Self-healing pre-submit pass: brings every numeric widget value inside the min/max the
    /// connected ComfyUI declares for it in /object_info. Node packs change their limits between
    /// versions (e.g. "easy globalSeed" caps seeds at 2^50 while other seed widgets accept 2^63),
    /// which otherwise drops the node during validation and fails the whole run with
    /// "Value bigger than max". Out-of-range seeds are wrapped (modulo) so they stay random;
    /// everything else is clamped to the nearest bound. Best-effort — on any error the workflow is
    /// returned untouched and the server decides.
    /// </summary>
    public async Task<object> ClampOutOfRangeInputsAsync(object workflow, CancellationToken cancellationToken)
    {
        try
        {
            var oiJson = await GetObjectInfoJsonAsync(cancellationToken);
            if (oiJson == null) return workflow;

            var json = workflow is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(workflow);
            if (JsonNode.Parse(json) is not JsonObject nodes) return workflow;

            using var oiDoc = JsonDocument.Parse(oiJson);
            var oiRoot = oiDoc.RootElement;

            var fixes = new List<string>();
            foreach (var node in nodes)
            {
                if (node.Value is not JsonObject obj) continue;
                var classType = (obj["class_type"] as JsonValue)?.GetValue<string>();
                if (string.IsNullOrEmpty(classType)) continue;
                if (obj["inputs"] is not JsonObject inputs) continue;
                if (!oiRoot.TryGetProperty(classType!, out var oiNode)) continue;
                if (!oiNode.TryGetProperty("input", out var oiInput)) continue;

                // Collect first, then assign — mutating a JsonObject mid-enumeration throws.
                var changes = new List<KeyValuePair<string, double>>();
                foreach (var input in inputs)
                {
                    if (input.Value is not JsonValue v || !v.TryGetValue<double>(out var current)) continue;
                    if (!TryGetNumericRange(oiInput, input.Key, out var min, out var max)) continue;
                    if (current >= min && current <= max) continue;

                    // Seed widgets are often named just "value" on dedicated seed nodes
                    // (e.g. "easy globalSeed"), so check the class type too — wrapping keeps
                    // the run random where clamping would pin every run to the same seed.
                    bool isSeed = input.Key.IndexOf("seed", StringComparison.OrdinalIgnoreCase) >= 0
                                  || classType!.IndexOf("seed", StringComparison.OrdinalIgnoreCase) >= 0;
                    double repaired = current > max
                        ? (isSeed && max > 0 ? Math.Floor(current % max) : max)
                        : min;

                    changes.Add(new(input.Key, repaired));
                    fixes.Add($"node {node.Key} ({classType}).{input.Key}: {current:F0} → {repaired:F0}");
                }

                foreach (var c in changes)
                    inputs[c.Key] = c.Value == Math.Floor(c.Value) && Math.Abs(c.Value) < long.MaxValue
                        ? JsonValue.Create((long)c.Value)
                        : JsonValue.Create(c.Value);
            }

            if (fixes.Count == 0) return workflow;

            _logger.LogWarning($"Auto-repaired {fixes.Count} out-of-range input(s) before submit: {string.Join("; ", fixes)}");
            return nodes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"ClampOutOfRangeInputsAsync failed (skipping): {ex.Message}");
            return workflow;
        }
    }

    /// <summary>
    /// Reads an INT/FLOAT input's declared min/max from an /object_info node "input" block.
    /// False for combo/string/link inputs or when no bounds are declared.
    /// </summary>
    private static bool TryGetNumericRange(JsonElement oiInput, string inputKey, out double min, out double max)
    {
        min = double.MinValue;
        max = double.MaxValue;
        foreach (var section in new[] { "required", "optional" })
        {
            if (!oiInput.TryGetProperty(section, out var sec) || sec.ValueKind != JsonValueKind.Object) continue;
            if (!sec.TryGetProperty(inputKey, out var spec)) continue;
            if (spec.ValueKind != JsonValueKind.Array || spec.GetArrayLength() < 2) return false;

            var type = spec[0].ValueKind == JsonValueKind.String ? spec[0].GetString() : null;
            if (type is not ("INT" or "FLOAT")) return false;
            if (spec[1].ValueKind != JsonValueKind.Object) return false;

            bool hasBound = false;
            if (spec[1].TryGetProperty("min", out var minEl) && minEl.ValueKind == JsonValueKind.Number)
            {
                min = minEl.GetDouble();
                hasBound = true;
            }
            if (spec[1].TryGetProperty("max", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number)
            {
                max = maxEl.GetDouble();
                hasBound = true;
            }
            return hasBound;
        }
        return false;
    }

    /// <summary>
    /// True when the connected ComfyUI exposes every one of <paramref name="classTypes"/>.
    ///
    /// <para>For workflows that have an optional better path on a newer custom-node pack: ask first,
    /// then emit the graph the server can actually run. This is deliberately <i>not</i> the missing-node
    /// resolver's job — that one fires after a submission has already been built around nodes the server
    /// does not have, and offers to install a whole pack. Here the pack is present and merely old, which
    /// the resolver cannot detect and an install would not fix.</para>
    ///
    /// <para>Returns false when /object_info cannot be read, so an unreachable or slow server degrades to
    /// the conservative branch rather than failing the submission.</para>
    /// </summary>
    public async Task<bool> HasNodeClassesAsync(
        IReadOnlyCollection<string> classTypes, CancellationToken cancellationToken = default)
    {
        if (classTypes.Count == 0) return true;
        try
        {
            var oiJson = await GetObjectInfoJsonAsync(cancellationToken);
            if (oiJson == null) return false;

            using var doc = JsonDocument.Parse(oiJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var classType in classTypes)
                if (!doc.RootElement.TryGetProperty(classType, out _)) return false;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning($"HasNodeClassesAsync: could not read /object_info ({ex.Message})");
            return false;
        }
    }

    /// <summary>
    /// Drops the cached /object_info so the next validation re-reads ComfyUI's model lists. Called
    /// after the resolver installs a model so a freshly-copied file is seen without waiting for TTL.
    /// </summary>
    public void InvalidateObjectInfoCache()
    {
        _objectInfoCacheJson = null;
    }

    private static bool HasModelExtension(string s)
    {
        foreach (var ext in ModelFileExtensions)
            if (s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Finds model files a workflow references that the connected ComfyUI does not expose, by
    /// replicating ComfyUI's own combo-input validation against /object_info. Returns the distinct
    /// missing filenames. Best-effort: returns empty (lets the server decide) if it can't validate,
    /// so a parsing hiccup never blocks a legitimate submission.
    /// </summary>
    public async Task<List<MissingModelInfo>> FindMissingModelsAsync(object workflow, CancellationToken cancellationToken = default)
    {
        var missing = new List<MissingModelInfo>();
        try
        {
            var promptJson = JsonSerializer.Serialize(workflow);
            using var promptDoc = JsonDocument.Parse(promptJson);
            if (promptDoc.RootElement.ValueKind != JsonValueKind.Object) return missing;

            var oiJson = await GetObjectInfoJsonAsync(cancellationToken);
            if (oiJson == null) return missing; // can't validate -> don't block
            using var oiDoc = JsonDocument.Parse(oiJson);
            var oiRoot = oiDoc.RootElement;

            // ComfyUI prunes nodes that don't feed an output node before it validates or
            // executes a prompt, so a workflow can carry leftover/orphan loader nodes (e.g. an
            // unused Wav2Vec/VAE loader) that reference models the server never loads. Mirror
            // that: only validate nodes reachable from an output node so those orphans don't
            // raise false "missing model" errors. Falls back to validating everything when no
            // output node can be identified.
            var reachable = GetReachableNodeIds(promptDoc.RootElement, oiRoot);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var nodeProp in promptDoc.RootElement.EnumerateObject())
            {
                if (reachable != null && !reachable.Contains(nodeProp.Name)) continue;
                var node = nodeProp.Value;
                if (node.ValueKind != JsonValueKind.Object) continue;
                if (!node.TryGetProperty("class_type", out var ctEl) || ctEl.ValueKind != JsonValueKind.String) continue;
                var classType = ctEl.GetString()!;
                if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) continue;

                // Unknown node type to this server -> let the server report it (custom-node issue,
                // not a missing model). Only validate inputs whose combo we can actually read.
                if (!oiRoot.TryGetProperty(classType, out var oiNode)) continue;
                if (!oiNode.TryGetProperty("input", out var oiInput)) continue;

                foreach (var inp in inputs.EnumerateObject())
                {
                    // Only string inputs can be model names; links are arrays, others are scalars.
                    if (inp.Value.ValueKind != JsonValueKind.String) continue;
                    var value = inp.Value.GetString();
                    if (string.IsNullOrEmpty(value)) continue;

                    if (!TryGetComboValues(oiInput, inp.Name, out var allowed)) continue;

                    // Decide if this combo is a model list (so we don't flag sampler/scheduler enums):
                    // either the submitted value looks like a model file, or the allowed entries do.
                    bool looksModel = HasModelExtension(value);
                    bool found = false;
                    foreach (var a in allowed)
                    {
                        if (a == value) { found = true; break; }
                        if (!looksModel && HasModelExtension(a)) looksModel = true;
                    }
                    if (found || !looksModel) continue;

                    if (seen.Add(value))
                        missing.Add(new MissingModelInfo
                        {
                            Name = value,
                            ClassType = classType,
                            Category = GetModelCategory(classType, inp.Name)
                        });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"FindMissingModelsAsync failed (skipping validation): {ex.Message}");
            return new List<MissingModelInfo>();
        }
        return missing;
    }

    /// <summary>
    /// Finds custom-node class types a workflow references that the connected ComfyUI hasn't loaded
    /// (i.e. absent from /object_info) — the "missing_node_type" case ComfyUI would otherwise reject
    /// the whole prompt for. Only validates nodes reachable from an output node (same pruning as the
    /// model check) so orphan/annotation nodes don't raise false positives. Each result is enriched
    /// with the providing pack from the offline <see cref="NodeCatalog"/> when known; the resolver
    /// fills in anything else from the running ComfyUI-Manager. Best-effort: returns empty (lets the
    /// server decide) if it can't validate, so a parsing hiccup never blocks a legitimate submission.
    /// </summary>
    public async Task<List<MissingNodeInfo>> FindMissingNodesAsync(object workflow, CancellationToken cancellationToken = default)
    {
        var missing = new List<MissingNodeInfo>();
        try
        {
            var promptJson = JsonSerializer.Serialize(workflow);
            using var promptDoc = JsonDocument.Parse(promptJson);
            if (promptDoc.RootElement.ValueKind != JsonValueKind.Object) return missing;

            var oiJson = await GetObjectInfoJsonAsync(cancellationToken);
            if (oiJson == null) return missing; // can't validate -> don't block
            using var oiDoc = JsonDocument.Parse(oiJson);
            var oiRoot = oiDoc.RootElement;

            var reachable = GetReachableNodeIds(promptDoc.RootElement, oiRoot);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var nodeProp in promptDoc.RootElement.EnumerateObject())
            {
                if (reachable != null && !reachable.Contains(nodeProp.Name)) continue;
                var node = nodeProp.Value;
                if (node.ValueKind != JsonValueKind.Object) continue;
                if (!node.TryGetProperty("class_type", out var ctEl) || ctEl.ValueKind != JsonValueKind.String) continue;
                var classType = ctEl.GetString();
                if (string.IsNullOrEmpty(classType)) continue;

                // Loaded on the server -> not missing.
                if (oiRoot.TryGetProperty(classType, out _)) continue;
                if (!seen.Add(classType)) continue;

                var title = "";
                if (node.TryGetProperty("_meta", out var meta)
                    && meta.TryGetProperty("title", out var titleEl)
                    && titleEl.ValueKind == JsonValueKind.String)
                    title = titleEl.GetString() ?? "";

                var repo = FlipPix.Core.Services.NodeCatalog.GetRepoUrl(classType);
                missing.Add(new MissingNodeInfo
                {
                    ClassType = classType,
                    Title = title,
                    RepoUrl = repo ?? "",
                    PackName = repo != null ? FlipPix.Core.Services.NodeCatalog.PackNameFromRepo(repo) : ""
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"FindMissingNodesAsync failed (skipping validation): {ex.Message}");
            return new List<MissingNodeInfo>();
        }
        return missing;
    }

    /// <summary>
    /// Computes the set of node ids ComfyUI would actually execute for this prompt: every output
    /// node (object_info "output_node": true) plus everything reachable from one by following input
    /// links (`["sourceId", slot]`). Returns null when no output node can be identified (e.g. all
    /// outputs are unknown custom nodes) so the caller validates every node rather than pruning blindly.
    /// </summary>
    private static HashSet<string>? GetReachableNodeIds(JsonElement prompt, JsonElement oiRoot)
    {
        var allIds = new HashSet<string>();
        var outputNodes = new List<string>();
        foreach (var nodeProp in prompt.EnumerateObject())
        {
            allIds.Add(nodeProp.Name);
            var node = nodeProp.Value;
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("class_type", out var ctEl) || ctEl.ValueKind != JsonValueKind.String) continue;
            if (oiRoot.TryGetProperty(ctEl.GetString()!, out var oiNode)
                && oiNode.TryGetProperty("output_node", out var on)
                && on.ValueKind == JsonValueKind.True)
            {
                outputNodes.Add(nodeProp.Name);
            }
        }

        // No identifiable output node -> don't prune (validate everything, as before).
        if (outputNodes.Count == 0) return null;

        var reachable = new HashSet<string>();
        var stack = new Stack<string>(outputNodes);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!reachable.Add(id)) continue;
            if (!prompt.TryGetProperty(id, out var node) || node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) continue;
            foreach (var inp in inputs.EnumerateObject())
            {
                // A link is [ "<nodeId>", <slot> ]; scalars/strings are literal inputs.
                if (inp.Value.ValueKind == JsonValueKind.Array
                    && inp.Value.GetArrayLength() >= 1
                    && inp.Value[0].ValueKind == JsonValueKind.String)
                {
                    var src = inp.Value[0].GetString();
                    if (!string.IsNullOrEmpty(src) && allIds.Contains(src))
                        stack.Push(src);
                }
            }
        }
        return reachable;
    }

    /// <summary>
    /// Maps a ComfyUI node class + combo input name to the models sub-folder the file belongs in
    /// (mirrors ComfyUI's folder_paths conventions). Returns "" when it can't be inferred, in which
    /// case the resolver falls back to mirroring the file's source layout.
    /// </summary>
    internal static string GetModelCategory(string classType, string inputName)
    {
        var ct = classType ?? "";
        var key = (inputName ?? "").ToLowerInvariant();

        // CLIP/text-encoder loaders and CLIP-vision loaders both use a "clip_name" input;
        // disambiguate by the node class first.
        if (ct.IndexOf("CLIPVision", StringComparison.OrdinalIgnoreCase) >= 0) return "clip_vision";
        if (ct.IndexOf("ControlNet", StringComparison.OrdinalIgnoreCase) >= 0) return "controlnet";
        if (ct.IndexOf("StyleModel", StringComparison.OrdinalIgnoreCase) >= 0) return "style_models";
        if (ct.IndexOf("Upscale", StringComparison.OrdinalIgnoreCase) >= 0
            && ct.IndexOf("Model", StringComparison.OrdinalIgnoreCase) >= 0) return "upscale_models";

        switch (key)
        {
            case "ckpt_name": return "checkpoints";
            case "vae_name": return "vae";
            case "lora_name": return "loras";
            case "control_net_name": return "controlnet";
            case "style_model_name": return "style_models";
            case "clip_name":
            case "clip_name1":
            case "clip_name2":
            case "clip_name3":
            case "clip_name4":
                return "text_encoders";
            case "unet_name":
            case "gguf_name":
            case "model_name" when ct.IndexOf("Unet", StringComparison.OrdinalIgnoreCase) >= 0
                               || ct.IndexOf("GGUF", StringComparison.OrdinalIgnoreCase) >= 0
                               || ct.IndexOf("Diffusion", StringComparison.OrdinalIgnoreCase) >= 0:
                return "diffusion_models";
            case "model_name" when ct.IndexOf("Upscale", StringComparison.OrdinalIgnoreCase) >= 0:
                return "upscale_models";
        }

        // Fall back on the value's extension / class hints.
        if (ct.IndexOf("Lora", StringComparison.OrdinalIgnoreCase) >= 0) return "loras";
        if (ct.IndexOf("VAE", StringComparison.OrdinalIgnoreCase) >= 0) return "vae";
        if (ct.IndexOf("Checkpoint", StringComparison.OrdinalIgnoreCase) >= 0) return "checkpoints";
        if (ct.IndexOf("Unet", StringComparison.OrdinalIgnoreCase) >= 0
            || ct.IndexOf("GGUF", StringComparison.OrdinalIgnoreCase) >= 0
            || ct.IndexOf("Diffusion", StringComparison.OrdinalIgnoreCase) >= 0) return "diffusion_models";
        if (ct.IndexOf("CLIP", StringComparison.OrdinalIgnoreCase) >= 0) return "text_encoders";

        return "";
    }

    /// <summary>
    /// If <paramref name="inputKey"/> is a combo input on this node (required or optional), returns
    /// its list of allowed string values. ComfyUI exposes two combo shapes:
    ///   legacy: [ [values...], {meta} ]
    ///   newer:  [ "COMBO", { "options": [values...], ... } ]   (e.g. GGUFLoaderKJ)
    /// Both are handled; missing the newer one silently skipped GGUF/COMBO model checks.
    /// </summary>
    private static bool TryGetComboValues(JsonElement oiInput, string inputKey, out List<string> values)
    {
        values = new List<string>();
        foreach (var section in new[] { "required", "optional" })
        {
            if (!oiInput.TryGetProperty(section, out var sec) || sec.ValueKind != JsonValueKind.Object) continue;
            if (!sec.TryGetProperty(inputKey, out var spec)) continue;
            if (spec.ValueKind != JsonValueKind.Array || spec.GetArrayLength() == 0) return false;

            var first = spec[0];

            // Legacy shape: the allowed values are the first array element.
            if (first.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in first.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String) values.Add(v.GetString()!);
                return true;
            }

            // Newer shape: ["COMBO", { "options": [...] }].
            if (first.ValueKind == JsonValueKind.String
                && string.Equals(first.GetString(), "COMBO", StringComparison.OrdinalIgnoreCase)
                && spec.GetArrayLength() > 1
                && spec[1].ValueKind == JsonValueKind.Object
                && spec[1].TryGetProperty("options", out var options)
                && options.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in options.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String) values.Add(v.GetString()!);
                return true;
            }

            return false; // e.g. ["STRING", {...}] / ["INT", {...}]
        }
        return false;
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