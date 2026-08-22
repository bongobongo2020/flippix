using System;
using System.Collections.Generic;
using System.Diagnostics;
using SixLabors.ImageSharp.Processing;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Linux.Models;

namespace FlipPix.UI.Linux.Services
{
    public class LMStudioService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly IAppLogger _logger;
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed = false;
        private readonly Func<string> _getBaseUrl;
        private readonly Func<FlipPix.Core.Models.LMStudioSettings?>? _getSettings;

        public LMStudioService(
            HttpClient httpClient,
            IAppLogger logger,
            Func<string>? getBaseUrl = null,
            Func<FlipPix.Core.Models.LMStudioSettings?>? getSettings = null)
        {
            _httpClient = httpClient;
            _logger = logger;
            _getBaseUrl = getBaseUrl ?? (() => "http://alien:8080");
            _getSettings = getSettings;
            // Don't set BaseAddress - we'll use full URLs instead to allow changing the URL
            _httpClient.Timeout = TimeSpan.FromMinutes(15); // 15 minute timeout for large generation tasks
            _semaphore = new SemaphoreSlim(1, 1); // Limit concurrent requests
        }

        private string _baseUrl => _getBaseUrl();

        /// <summary>
        /// Describes where a request is going using the friendly names configured in Settings,
        /// e.g. "Alien Box (http://alien:8080) · Qwen2.5-VL 7B [qwen2.5-vl-7b-instruct]". Pass the
        /// model actually being used; omit it to describe the configured default.
        /// </summary>
        public string DescribeTarget(string? modelName = null)
        {
            var settings = _getSettings?.Invoke();
            if (settings != null) return settings.DescribeTarget(modelName);

            var url = _baseUrl.TrimEnd('/');
            return string.IsNullOrWhiteSpace(modelName) ? url : $"{url} · {modelName}";
        }

        public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/models";
                using var response = await _httpClient.GetAsync(fullUrl, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<List<LMStudioModel>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/models";
                using var response = await _httpClient.GetAsync(fullUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<LMStudioModelsResponse>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }, cancellationToken);

                var models = result?.Data ?? new List<LMStudioModel>();
                _logger.LogInfo($"Found {models.Count} models from LM Studio");

                // Log model details for debugging
                if (models.Count > 0)
                {
                    for (int i = 0; i < models.Count; i++)
                    {
                        var model = models[i];
                        _logger.LogInfo($"Model {i}: ID='{model.Id}', Name='{model.Name}', Object='{model.Object}'");

                        // If Name is empty, use ID as fallback
                        if (string.IsNullOrEmpty(model.Name))
                        {
                            model.Name = !string.IsNullOrEmpty(model.Id) ? model.Id : $"Model {i + 1}";
                        }
                    }
                    _logger.LogInfo($"Available models after fixing: {string.Join(", ", models.Select(m => m.Name))}");
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
                _logger.LogError($"Failed to connect to LM Studio: {ex.Message}");
                throw new Exception($"Unable to connect to LM Studio. Please ensure LM Studio is running on {_baseUrl}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching LM Studio models: {ex.Message}");
                throw;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<string> AnalyzeImageAsync(string modelName, string imagePath, string prompt = "Analyze this image in detail.", int maxTokens = 500, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(imagePath))
                {
                    throw new FileNotFoundException($"Image file not found: {imagePath}");
                }

                var imageBytes = ResizeImageForVision(imagePath, 512, 512, useJpeg: true);
                var base64Image = Convert.ToBase64String(imageBytes);
                var dataUrl = $"data:image/jpeg;base64,{base64Image}";

                _logger.LogInfo($"Sending resized image for analysis: {imageBytes.Length} bytes (jpeg 512px), max_tokens: {maxTokens}");

                // Create the request with vision - use the correct LM Studio multi-modal format
                var requestBody = new
                {
                    model = modelName,
                    messages = new object[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new
                                {
                                    type = "text",
                                    text = SuppressThinking(prompt, modelName)
                                },
                                new
                                {
                                    type = "image_url",
                                    image_url = new
                                    {
                                        url = dataUrl
                                    }
                                }
                            }
                        }
                    },
                    max_tokens = maxTokens,
                    temperature = 0.7,
                    // Reasoning models (Qwen3 etc.) ignore the /no_think text hint and route
                    // their whole answer into reasoning_content, leaving content empty. Disable
                    // chain-of-thought at the chat-template level so the analysis lands in
                    // content. Unknown fields are ignored by servers that don't support them.
                    chat_template_kwargs = new { enable_thinking = false },
                    reasoning_budget = 0,
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                _logger.LogInfo($"Sending image {Path.GetFileName(imagePath)} ({imageBytes.Length} bytes) to {DescribeTarget(modelName)}");

                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError($"LM Studio API returned {response.StatusCode}: {errorContent}");
                    throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }, cancellationToken);

                if (result?.Choices?.Count > 0)
                {
                    var analysis = StripThinkingBlocks(result.Choices[0].Message?.EffectiveContent?.Trim() ?? string.Empty);

                    // Log only a preview of the analysis to avoid memory issues
                    var preview = analysis.Length > 200 ? analysis.Substring(0, 200) + "..." : analysis;
                    _logger.LogInfo($"Image analysis completed (length: {analysis.Length}): {preview}");

                    return analysis;
                }
                else
                {
                    _logger.LogError("No choices in LM Studio API response");
                    throw new Exception("Invalid response format from LM Studio API");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Image analysis was cancelled");
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"Failed to connect to LM Studio: {ex.Message}");
                throw new Exception($"Failed to connect to LM Studio. Please ensure llamaserver is running on {_baseUrl}. Error: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in LM Studio API call: {ex.Message}");
                throw new Exception($"Error analyzing image: {ex.Message}", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<string> AnalyzeTwoImagesAsync(string modelName, string firstImagePath, string lastImagePath, string prompt, int maxTokens = 2000, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(firstImagePath))
                    throw new FileNotFoundException($"First frame image not found: {firstImagePath}");
                if (!File.Exists(lastImagePath))
                    throw new FileNotFoundException($"Last frame image not found: {lastImagePath}");

                string ToDataUrl(string path)
                {
                    var bytes = ResizeImageForVision(path, 512, 512, useJpeg: true);
                    return $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
                }

                var firstDataUrl = ToDataUrl(firstImagePath);
                var lastDataUrl  = ToDataUrl(lastImagePath);

                _logger.LogInfo($"Sending 2 images (first={Path.GetFileName(firstImagePath)}, last={Path.GetFileName(lastImagePath)}, resized 512px, max_tokens={maxTokens}) to {DescribeTarget(modelName)}");

                var requestBody = new
                {
                    model = modelName,
                    messages = new object[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "text", text = SuppressThinking($"Image 1 (First Frame):\n{prompt}", modelName) },
                                new { type = "image_url", image_url = new { url = firstDataUrl } },
                                new { type = "text", text = "Image 2 (Last Frame):" },
                                new { type = "image_url", image_url = new { url = lastDataUrl } }
                            }
                        }
                    },
                    max_tokens = maxTokens,
                    temperature = 0.7,
                    // Reasoning models (Qwen3 etc.) ignore the /no_think text hint and route
                    // their whole answer into reasoning_content, leaving content empty. Disable
                    // chain-of-thought at the chat-template level so the analysis lands in
                    // content. Unknown fields are ignored by servers that don't support them.
                    chat_template_kwargs = new { enable_thinking = false },
                    reasoning_budget = 0,
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError($"LM Studio API returned {response.StatusCode}: {errorContent}");
                    throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);

                if (result?.Choices?.Count > 0)
                {
                    var analysis = StripThinkingBlocks(result.Choices[0].Message?.EffectiveContent?.Trim() ?? string.Empty);
                    var preview = analysis.Length > 200 ? analysis.Substring(0, 200) + "..." : analysis;
                    _logger.LogInfo($"Two-image analysis completed (length: {analysis.Length}): {preview}");
                    return analysis;
                }

                _logger.LogError("No choices in LM Studio API response");
                throw new Exception("Invalid response format from LM Studio API");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Two-image analysis was cancelled");
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"Failed to connect to LM Studio: {ex.Message}");
                throw new Exception($"Failed to connect to LM Studio. Please ensure LM Studio is running on {_baseUrl}. Error: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in two-image LM Studio API call: {ex.Message}");
                throw new Exception($"Error analyzing images: {ex.Message}", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<string> AnalyzeImageWithSystemPromptAsync(string modelName, string imagePath, string userPrompt, string systemPrompt, int maxTokens = 36000, CancellationToken cancellationToken = default, LlmSampling? sampling = null)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(imagePath))
                {
                    throw new FileNotFoundException($"Image file not found: {imagePath}");
                }

                var imageBytes = ResizeImageForVision(imagePath, 512, 512, useJpeg: true);
                var base64Image = Convert.ToBase64String(imageBytes);
                var dataUrl = $"data:image/jpeg;base64,{base64Image}";

                _logger.LogInfo($"Sending resized image for analysis with system prompt: {imageBytes.Length} bytes (jpeg 512px), max_tokens: {maxTokens}");
                _logger.LogInfo($"System prompt ({systemPrompt.Length} chars): {systemPrompt.Substring(0, Math.Min(1000, systemPrompt.Length))}");
                _logger.LogInfo($"User prompt: {userPrompt}");

                // Create the request with vision and system prompt
                var s = sampling ?? LlmSampling.Default;
                var requestBody = BuildChatBody(
                    modelName,
                    new object[]
                    {
                        new
                        {
                            role = "system",
                            content = systemPrompt
                        },
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new
                                {
                                    type = "text",
                                    text = s.AllowThinking ? userPrompt : SuppressThinking(userPrompt, modelName)
                                },
                                new
                                {
                                    type = "image_url",
                                    image_url = new
                                    {
                                        url = dataUrl
                                    }
                                }
                            }
                        }
                    },
                    maxTokens, s);

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                _logger.LogInfo($"Sending image {Path.GetFileName(imagePath)} ({imageBytes.Length} bytes) to {DescribeTarget(modelName)}");

                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError($"LM Studio API returned {response.StatusCode}: {errorContent}");
                    throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }, cancellationToken);

                if (result?.Choices?.Count > 0)
                {
                    var analysis = StripThinkingBlocks(result.Choices[0].Message?.EffectiveContent?.Trim() ?? string.Empty);

                    // Log only a preview of the analysis to avoid memory issues
                    var preview = analysis.Length > 200 ? analysis.Substring(0, 200) + "..." : analysis;
                    _logger.LogInfo($"Image analysis completed (length: {analysis.Length}): {preview}");

                    return analysis;
                }
                else
                {
                    _logger.LogError("No choices in LM Studio API response");
                    throw new Exception("Invalid response format from LM Studio API");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Image analysis was cancelled");
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"Failed to connect to LM Studio: {ex.Message}");
                throw new Exception($"Failed to connect to llamaserver. Please ensure llamaserver is running on {_baseUrl}. Error: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in LM Studio API call: {ex.Message}");
                throw new Exception($"Error analyzing image: {ex.Message}", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<string> GenerateEnhancedPromptAsync(string modelName, string userPrompt, string enhancementType, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var systemPrompt = enhancementType switch
                {
                    "video" => "You are an expert at creating detailed 5-second video prompts. Enhance the user's image prompt to include motion, camera movement, and timing details suitable for a 5-second video generation. Keep the enhancement concise but descriptive.",
                    "monologue" => "You are an expert scriptwriter. Based on the user's image prompt, create a compelling monologue script that matches the visual content. The script should be suitable for a short voice-over and match the mood of the image.",
                    _ => "You are an expert at enhancing image prompts. Make the user's prompt more detailed and descriptive for better AI image generation results."
                };

                // Create the request in the correct LM Studio API format
                var requestBody = new LMStudioChatRequest
                {
                    Model = modelName,
                    Stream = false,
                    MaxTokens = 500,
                    Temperature = 0.7,
                    Messages = new List<LMStudioMessage>
                    {
                        new LMStudioMessage
                        {
                            Role = "system",
                            Content = systemPrompt
                        },
                        new LMStudioMessage
                        {
                            Role = "user",
                            Content = userPrompt
                        }
                    }
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                _logger.LogInfo($"Sending enhancement request to LM Studio for model: {modelName}");

                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError($"LM Studio API returned {response.StatusCode}: {errorContent}");
                    throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }, cancellationToken);

                if (result?.Choices?.Count > 0)
                {
                    var enhancedPrompt = StripThinkingBlocks(result.Choices[0].Message?.EffectiveContent?.Trim() ?? string.Empty);

                    // Log only a preview of the enhanced prompt to avoid memory issues
                    var preview = enhancedPrompt.Length > 200 ? enhancedPrompt.Substring(0, 200) + "..." : enhancedPrompt;
                    _logger.LogInfo($"Enhanced prompt generated (length: {enhancedPrompt.Length}): {preview}");

                    return enhancedPrompt;
                }
                else
                {
                    _logger.LogError("No choices in LM Studio API response");
                    throw new Exception("Invalid response format from LM Studio API");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInfo("Prompt enhancement was cancelled");
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"Failed to connect to LM Studio: {ex.Message}");
                throw new Exception($"Failed to connect to LM Studio. Please ensure LM Studio is running on {_baseUrl}. Error: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in LM Studio API call: {ex.Message}");
                throw new Exception($"Error generating enhanced prompt: {ex.Message}", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <param name="sampling">
        /// Optional repetition controls. Omitted, the request is byte-for-byte what it has always been.
        /// Supplied, it is the answer to a long structured reply degenerating into a copy loop — the
        /// failure mode of asking one model for N near-identical blocks in a single turn. See
        /// <see cref="LlmSampling"/>.
        /// </param>
        public async Task<string> SendTextChatAsync(
            string modelName,
            string systemPrompt,
            string userMessage,
            int maxTokens = 2000,
            CancellationToken cancellationToken = default,
            LlmSampling? sampling = null)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var s = sampling ?? LlmSampling.Default;
                var requestBody = BuildChatBody(
                    modelName,
                    new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user",   content = s.AllowThinking ? userMessage : SuppressThinking(userMessage, modelName) }
                    },
                    maxTokens, s);

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                _logger.LogInfo($"SendTextChatAsync: model={modelName}, userMessage length={userMessage.Length}{s.Describe()}");

                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);

                if (result?.Choices?.Count > 0)
                {
                    var text = StripThinkingBlocks(result.Choices[0].Message?.EffectiveContent?.Trim() ?? string.Empty);
                    _logger.LogInfo($"SendTextChatAsync completed, response length={text.Length}");
                    return text;
                }

                throw new Exception("No choices in LM Studio API response");
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                throw new Exception($"Failed to connect to LM Studio at {_baseUrl}: {ex.Message}", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<string> AnalyzeMultipleImagesWithSystemPromptAsync(
            string modelName,
            IList<string> imagePaths,
            string userPrompt,
            string systemPrompt,
            int maxTokens = 2048,
            CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var contentParts = new List<object>();
                contentParts.Add(new { type = "text", text = SuppressThinking(userPrompt, modelName) });

                foreach (var imagePath in imagePaths)
                {
                    if (!File.Exists(imagePath)) continue;
                    var bytes = ResizeImageForVision(imagePath, 512, 512, useJpeg: true);
                    var dataUrl = $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
                    contentParts.Add(new { type = "image_url", image_url = new { url = dataUrl } });
                }

                _logger.LogInfo($"Sending {contentParts.Count - 1} image(s) with system prompt (max_tokens: {maxTokens}) to {DescribeTarget(modelName)}");

                var requestBody = new
                {
                    model = modelName,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = contentParts.ToArray() }
                    },
                    max_tokens = maxTokens,
                    temperature = 0.7,
                    // Reasoning models (Qwen3 etc.) ignore the /no_think hint and otherwise
                    // burn the whole max_tokens budget thinking — sometimes in a repetition
                    // loop that takes ~10 min. Disable chain-of-thought at the chat-template
                    // level so the model emits only the final edit prompt. Unknown fields are
                    // ignored by servers that don't support them.
                    chat_template_kwargs = new { enable_thinking = false },
                    reasoning_budget = 0,
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError($"LM Studio API returned {response.StatusCode}: {errorContent}");
                    throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);

                if (result?.Choices?.Count > 0)
                {
                    var analysis = StripThinkingBlocks(result.Choices[0].Message?.EffectiveContent?.Trim() ?? string.Empty);
                    var preview = analysis.Length > 200 ? analysis.Substring(0, 200) + "..." : analysis;
                    _logger.LogInfo($"Multi-image analysis completed (length: {analysis.Length}): {preview}");
                    return analysis;
                }

                _logger.LogError("No choices in LM Studio API response");
                throw new Exception("Invalid response format from LM Studio API");
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"Failed to connect to LM Studio: {ex.Message}");
                throw new Exception($"Failed to connect to llamaserver at {_baseUrl}: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in multi-image analysis: {ex.Message}");
                throw;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task SetBaseUrlAsync(string baseUrl)
        {
            // No longer needed - URL is now retrieved dynamically from settings
            // This method is kept for backward compatibility
            _logger.LogInfo($"SetBaseUrlAsync called but URL is now dynamically retrieved from settings. Current URL: {_baseUrl}");

            // Test the connection with the current URL from settings
            var isRunning = await IsRunningAsync();
            if (!isRunning)
            {
                _logger.LogWarning($"LM Studio not responding at {_baseUrl}");
            }
        }

        /// <summary>
        /// Downscales an image before sending it to a vision model.
        ///
        /// The WPF build uses System.Drawing here, but System.Drawing.Common is Windows-only
        /// from .NET 7 onward and throws PlatformNotSupportedException elsewhere, so this
        /// implementation uses ImageSharp instead. Behaviour is unchanged: fit inside the
        /// bounds preserving aspect, then encode as JPEG q50 or PNG.
        /// </summary>
        private byte[] ResizeImageForVision(string imagePath, int maxWidth, int maxHeight, bool useJpeg = false)
        {
            try
            {
                using var image = SixLabors.ImageSharp.Image.Load(imagePath);

                double aspectRatio = (double)image.Width / image.Height;
                int newWidth, newHeight;
                if (image.Width > image.Height)
                {
                    newWidth = maxWidth;
                    newHeight = (int)(maxWidth / aspectRatio);
                }
                else
                {
                    newHeight = maxHeight;
                    newWidth = (int)(maxHeight * aspectRatio);
                }

                newWidth = Math.Max(1, newWidth);
                newHeight = Math.Max(1, newHeight);

                image.Mutate(ctx => ctx.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
                {
                    Size = new SixLabors.ImageSharp.Size(newWidth, newHeight),
                    Mode = SixLabors.ImageSharp.Processing.ResizeMode.Stretch,
                    Sampler = SixLabors.ImageSharp.Processing.KnownResamplers.Bicubic
                }));

                using var outputStream = new MemoryStream();
                if (useJpeg)
                {
                    image.Save(outputStream, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 50 });
                }
                else
                {
                    image.Save(outputStream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                }
                return outputStream.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error resizing image {imagePath}: {ex.Message}");
                // Fallback: return original image bytes if resizing fails
                return File.ReadAllBytes(imagePath);
            }
        }

        /// <summary>
        /// Returns true for models known to emit chain-of-thought reasoning in plain text.
        /// These models support /no_think in the user message to suppress CoT output.
        /// </summary>
        private static bool IsThinkingModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            var lower = modelName.ToLowerInvariant();
            return lower.Contains("qwen3") ||
                   lower.Contains("qwq") ||
                   lower.Contains("deepseek-r1") ||
                   lower.Contains("-r1-") ||
                   lower.Contains("gemma4") ||
                   lower.Contains("thinking");
        }

        /// <summary>
        /// Prepends /no_think to the user prompt for thinking models so the model skips
        /// its chain-of-thought preamble and outputs only the final answer.
        /// No-op for non-thinking models.
        /// </summary>
        /// <summary>
        /// The chat/completions body every call in this class used to build inline. Pulled out so the
        /// repetition controls in <see cref="LlmSampling"/> have one place to land, and so the
        /// thinking-suppression fields stay written exactly as they were for every caller that does not
        /// ask for anything else.
        ///
        /// <para>A dictionary rather than an anonymous type because half these fields are conditional: a
        /// server rejects <c>repeat_penalty: 0</c>, and a reasoning model given <c>reasoning_budget: 0</c>
        /// cannot plan. <c>DictionaryKeyPolicy</c> is unset, so the snake_case keys reach the wire
        /// verbatim.</para>
        /// </summary>
        private static Dictionary<string, object> BuildChatBody(
            string modelName, object[] messages, int maxTokens, LlmSampling sampling)
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = modelName,
                ["messages"] = messages,
                ["max_tokens"] = maxTokens,
                ["temperature"] = sampling.Temperature,
                ["stream"] = false,
            };

            // OpenAI-standard, honoured by llama-server and LM Studio alike. 0 is the API default, so
            // sending 0 and leaving them out are the same request — they are omitted anyway so an
            // untouched caller's payload stays byte-for-byte what it was.
            if (sampling.PresencePenalty != 0) body["presence_penalty"] = sampling.PresencePenalty;
            if (sampling.FrequencyPenalty != 0) body["frequency_penalty"] = sampling.FrequencyPenalty;
            // llama.cpp's own knob, and the only one of the three that looks at a window of recent tokens
            // rather than at whole-reply counts. Ignored by servers that do not implement it.
            if (sampling.RepeatPenalty > 0) body["repeat_penalty"] = sampling.RepeatPenalty;

            if (!sampling.AllowThinking)
            {
                // Reasoning models (Qwen3 etc.) ignore the /no_think text hint and route their whole
                // answer into reasoning_content, leaving content empty — which makes the caller return ""
                // and silently breaks the step downstream. Disable chain-of-thought at the chat-template
                // level so the final answer lands in content. Unknown fields are ignored by servers that
                // don't support them.
                body["chat_template_kwargs"] = new { enable_thinking = false };
                body["reasoning_budget"] = 0;
            }

            return body;
        }

        private static string SuppressThinking(string prompt, string modelName)
        {
            if (!IsThinkingModel(modelName)) return prompt;
            return "/no_think\n" + prompt;
        }

        private static string StripThinkingBlocks(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 1. Paired <think>...</think> tags (DeepSeek / Qwen with reasoning_in_content=true,
            //    generation prompt provides opening tag so only closing appears in content).
            //    Try paired first, then orphan closing tag.
            var result = System.Text.RegularExpressions.Regex.Replace(
                text, @"<think(?:ing)?>[\s\S]*?</think(?:ing)?>", string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

            // 1b. Orphan </think> — server's generation_prompt injects <think>, so the model
            //     emits content as "[thinking]</think>\n\nAnswer" without an opening tag.
            //     If we find an orphan closing tag, take only what follows it.
            if (result == text.Trim()) // nothing was stripped by paired-tag pass
            {
                var orphan = System.Text.RegularExpressions.Regex.Match(
                    result, @"</think(?:ing)?>\s*([\s\S]+)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (orphan.Success)
                {
                    var after = orphan.Groups[1].Value.Trim();
                    if (after.Length > 0)
                        result = after;
                }
            }

            // 2. Qwen3 / gemma4 bold-header markdown thinking.
            var extracted = PromptParser.ExtractFromNumberedMarkdownThinking(result);
            if (extracted != null) return extracted;

            // 3. Plain-text-header thinking — model starts by reasoning aloud.
            if (System.Text.RegularExpressions.Regex.IsMatch(result,
                    @"^(?:The user wants|The prompt|I need to|The image shows|Let me|I'll analyze|Let's|" +
                    @"Looking at|Wait[,\.]|I see|Okay[,\.]|Sure[,\.]|Based on|Usually|" +
                    @"First[,\.]|Alright[,\.]|So[,\s]|Hmm|Actually|Here(?:'s| is))",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var stripped = PromptParser.StripThinking(result);
                if (stripped.Length > 30 && stripped.Length < result.Length)
                    return stripped;
            }

            return result;
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
                _semaphore?.Dispose();
                _disposed = true;
            }
        }
    }
}