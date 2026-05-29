using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Linux.Models;

namespace FlipPix.UI.Linux.Services
{
    public class OllamaService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly IAppLogger _logger;
        private const string DefaultBaseUrl = "http://localhost:11434";
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed = false;

        public OllamaService(HttpClient httpClient, IAppLogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _httpClient.BaseAddress = new Uri(DefaultBaseUrl);
            _httpClient.Timeout = TimeSpan.FromMinutes(5); // 5 minute timeout
            _semaphore = new SemaphoreSlim(1, 1); // Limit concurrent requests
        }

        public async Task<List<OllamaModel>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                CheckMemoryUsage();

                using var response = await _httpClient.GetAsync("/api/tags", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<OllamaModelsResponse>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }, cancellationToken);

                var models = result?.Models ?? new List<OllamaModel>();
                _logger.LogInfo($"Found {models.Count} models");

                // Log model names separately to avoid large string concatenations
                if (models.Count > 0)
                {
                    _logger.LogInfo($"Available models: {string.Join(", ", models.Select(m => m.Name))}");
                }

                return models;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Model fetching was cancelled");
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"Failed to connect to Ollama: {ex.Message}");
                throw new Exception("Unable to connect to Ollama. Please ensure Ollama is running on localhost:11434", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching Ollama models: {ex.Message}");
                throw;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<bool> IsOllamaRunningAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.GetAsync("/api/tags", cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> GenerateEnhancedPromptAsync(string modelName, string userPrompt, string enhancementType, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                CheckMemoryUsage();

                var systemPrompt = enhancementType switch
                {
                    "video" => "You are an expert at creating detailed 5-second video prompts. Enhance the user's image prompt to include motion, camera movement, and timing details suitable for a 5-second video generation. Keep the enhancement concise but descriptive.",
                    "monologue" => "You are an expert scriptwriter. Based on the user's image prompt, create a compelling monologue script that matches the visual content. The script should be suitable for a short voice-over and match the mood of the image.",
                    _ => "You are an expert at enhancing image prompts. Make the user's prompt more detailed and descriptive for better AI image generation results."
                };

                // Create the request in the correct Ollama API format
                var requestBody = new OllamaGenerateRequest
                {
                    Model = modelName,
                    System = systemPrompt,
                    Prompt = userPrompt,
                    Stream = false,
                    Options = new OllamaOptions
                    {
                        Temperature = 0.7,
                        TopP = 0.9,
                        MaxTokens = 500  // Limit response size
                    }
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                _logger.LogInfo($"Sending enhancement request to Ollama for model: {modelName}");

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync("/api/generate", content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError($"Ollama API returned {response.StatusCode}: {errorContent}");
                    throw new Exception($"Ollama API error: {response.StatusCode} - {errorContent}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;

                if (root.TryGetProperty("response", out var responseProperty))
                {
                    var enhancedPrompt = responseProperty.GetString()?.Trim() ?? string.Empty;

                    // Log only a preview of the enhanced prompt to avoid memory issues
                    var preview = enhancedPrompt.Length > 200 ? enhancedPrompt.Substring(0, 200) + "..." : enhancedPrompt;
                    _logger.LogInfo($"Enhanced prompt generated (length: {enhancedPrompt.Length}): {preview}");

                    return enhancedPrompt;
                }
                else
                {
                    _logger.LogError("No response field in Ollama API response");
                    throw new Exception("Invalid response format from Ollama API");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Prompt enhancement was cancelled");
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"Failed to connect to Ollama: {ex.Message}");
                throw new Exception($"Failed to connect to Ollama. Please ensure Ollama is running. Error: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in Ollama API call: {ex.Message}");
                throw new Exception($"Error generating enhanced prompt: {ex.Message}", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public Task SetBaseUrlAsync(string baseUrl)
        {
            if (!string.IsNullOrEmpty(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                _httpClient.BaseAddress = uri;
                _logger.LogInfo($"Ollama base URL updated to: {baseUrl}");
                return Task.CompletedTask;
            }
            else
            {
                throw new ArgumentException("Invalid URL format for Ollama base address");
            }
        }

        private void CheckMemoryUsage()
        {
            var memoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
            if (memoryMB > 500) // 500MB threshold
            {
                _logger.LogInfo($"Memory usage is high ({memoryMB}MB)");
                // Let the runtime manage garbage collection naturally
                // Aggressive GC.Collect() has been removed to allow runtime optimization
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                // Do NOT dispose HttpClient as it comes from IHttpClientFactory
                // _httpClient?.Dispose();  <-- Removed
                _semaphore?.Dispose();
                _disposed = true;
            }
        }
    }
}