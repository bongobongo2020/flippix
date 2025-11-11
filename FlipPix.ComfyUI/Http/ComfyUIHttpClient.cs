using System.Net.Http.Json;
using System.Text.Json;
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
        _httpClient.Timeout = TimeSpan.FromMilliseconds(_settings.ConnectionTimeout);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInfo("Testing connection to ComfyUI at {BaseUrl}", _settings.BaseUrl);
            
            var response = await _httpClient.GetAsync("/system_stats", cancellationToken);
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
            var response = await _httpClient.PostAsync("/upload/image", content, cancellationToken);
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
            content.Add(fileContent, "image", Path.GetFileName(filePath)); // ComfyUI uses "image" field for all uploads
            content.Add(new StringContent(type), "type");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            // Try video-specific upload endpoint first, fallback to image endpoint
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsync("/upload/video", content, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    // Fallback to image endpoint
                    response = await _httpClient.PostAsync("/upload/image", content, cancellationToken);
                }
            }
            catch
            {
                // Fallback to image endpoint if video endpoint doesn't exist
                response = await _httpClient.PostAsync("/upload/image", content, cancellationToken);
            }
            
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<UploadResponse>(responseContent);
                
                _logger.LogInfo("Video uploaded successfully in {ElapsedMs}ms: {FileName}",
                    stopwatch.ElapsedMilliseconds, result?.Name ?? "unknown");
                
                return result?.Name ?? throw new InvalidOperationException("Upload response missing filename");
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

    public async Task<string> SubmitPromptAsync(object workflow, string clientId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo("Submitting workflow for client: {ClientId}", clientId);

            var request = new PromptRequest
            {
                Prompt = workflow,
                ClientId = clientId
            };

            var response = await _httpClient.PostAsJsonAsync("/prompt", request, cancellationToken);

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

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient?.Dispose();
            _disposed = true;
        }
    }
}