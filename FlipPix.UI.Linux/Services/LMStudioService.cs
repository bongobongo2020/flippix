using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Linux.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace FlipPix.UI.Linux.Services
{
    public class LMStudioService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly IAppLogger _logger;
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed = false;
        private readonly Func<string> _getBaseUrl;

        public LMStudioService(HttpClient httpClient, IAppLogger logger, Func<string>? getBaseUrl = null)
        {
            _httpClient = httpClient;
            _logger = logger;
            _getBaseUrl = getBaseUrl ?? (() => "http://localhost:8080");
            _httpClient.Timeout = TimeSpan.FromMinutes(15);
            _semaphore = new SemaphoreSlim(1, 1);
        }

        private string _baseUrl => _getBaseUrl();

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

                if (models.Count > 0)
                {
                    for (int i = 0; i < models.Count; i++)
                    {
                        var model = models[i];
                        _logger.LogInfo($"Model {i}: ID='{model.Id}', Name='{model.Name}'");
                        if (string.IsNullOrEmpty(model.Name))
                            model.Name = !string.IsNullOrEmpty(model.Id) ? model.Id : $"Model {i + 1}";
                    }
                }

                return models;
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"Failed to connect to LM Studio: {ex.Message}");
                throw new Exception($"Unable to connect to LM Studio. Please ensure LM Studio is running on {_baseUrl}", ex);
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
                    throw new FileNotFoundException($"Image file not found: {imagePath}");

                var imageBytes = ResizeImageForVision(imagePath, 512, 512, useJpeg: true);
                var base64Image = Convert.ToBase64String(imageBytes);
                var dataUrl = $"data:image/jpeg;base64,{base64Image}";

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
                                new { type = "text", text = SuppressThinking(prompt, modelName) },
                                new { type = "image_url", image_url = new { url = dataUrl } }
                            }
                        }
                    },
                    max_tokens = maxTokens,
                    temperature = 0.7,
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);

                if (result?.Choices?.Count > 0)
                {
                    var analysis = StripThinkingBlocks(result.Choices[0].Message?.EffectiveContent?.Trim() ?? string.Empty);
                    _logger.LogInfo($"Image analysis completed (length: {analysis.Length})");
                    return analysis;
                }

                throw new Exception("Invalid response format from LM Studio API");
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

        public async Task<string> AnalyzeTwoImagesAsync(string modelName, string firstImagePath, string lastImagePath, string prompt, int maxTokens = 2000, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(firstImagePath)) throw new FileNotFoundException($"First frame not found: {firstImagePath}");
                if (!File.Exists(lastImagePath)) throw new FileNotFoundException($"Last frame not found: {lastImagePath}");

                string ToDataUrl(string path)
                {
                    var bytes = ResizeImageForVision(path, 512, 512, useJpeg: true);
                    return $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
                }

                var firstDataUrl = ToDataUrl(firstImagePath);
                var lastDataUrl = ToDataUrl(lastImagePath);

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
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);

                if (result?.Choices?.Count > 0)
                    return StripThinkingBlocks(result.Choices[0].Message?.EffectiveContent?.Trim() ?? string.Empty);

                throw new Exception("Invalid response format from LM Studio API");
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex) { throw new Exception($"Failed to connect to LM Studio at {_baseUrl}: {ex.Message}", ex); }
            finally { _semaphore.Release(); }
        }

        public async Task<string> AnalyzeImageWithSystemPromptAsync(string modelName, string imagePath, string userPrompt, string systemPrompt, int maxTokens = 36000, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(imagePath)) throw new FileNotFoundException($"Image file not found: {imagePath}");

                var imageBytes = ResizeImageForVision(imagePath, 512, 512, useJpeg: true);
                var base64Image = Convert.ToBase64String(imageBytes);
                var dataUrl = $"data:image/jpeg;base64,{base64Image}";

                var requestBody = new
                {
                    model = modelName,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "text", text = SuppressThinking(userPrompt, modelName) },
                                new { type = "image_url", image_url = new { url = dataUrl } }
                            }
                        }
                    },
                    max_tokens = maxTokens,
                    temperature = 0.7,
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);

                if (result?.Choices?.Count > 0)
                    return StripThinkingBlocks(result.Choices[0].Message?.EffectiveContent?.Trim() ?? string.Empty);

                throw new Exception("Invalid response format from LM Studio API");
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex) { throw new Exception($"Failed to connect to LM Studio at {_baseUrl}: {ex.Message}", ex); }
            finally { _semaphore.Release(); }
        }

        public async Task<string> GenerateEnhancedPromptAsync(string modelName, string userPrompt, string enhancementType, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var systemPrompt = enhancementType switch
                {
                    "video" => "You are an expert at creating detailed 5-second video prompts. Enhance the user's image prompt to include motion, camera movement, and timing details suitable for a 5-second video generation. Keep the enhancement concise but descriptive.",
                    "monologue" => "You are an expert scriptwriter. Based on the user's image prompt, create a compelling monologue script that matches the visual content.",
                    _ => "You are an expert at enhancing image prompts. Make the user's prompt more detailed and descriptive for better AI image generation results."
                };

                var requestBody = new LMStudioChatRequest
                {
                    Model = modelName,
                    Stream = false,
                    MaxTokens = 500,
                    Temperature = 0.7,
                    Messages = new List<LMStudioMessage>
                    {
                        new LMStudioMessage { Role = "system", Content = systemPrompt },
                        new LMStudioMessage { Role = "user", Content = userPrompt }
                    }
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);

                if (result?.Choices?.Count > 0)
                    return StripThinkingBlocks(result.Choices[0].Message?.EffectiveContent?.Trim() ?? string.Empty);

                throw new Exception("Invalid response format from LM Studio API");
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex) { throw new Exception($"Failed to connect to LM Studio at {_baseUrl}: {ex.Message}", ex); }
            finally { _semaphore.Release(); }
        }

        public async Task<string> SendTextChatAsync(string modelName, string systemPrompt, string userMessage, int maxTokens = 2000, CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var requestBody = new
                {
                    model = modelName,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = SuppressThinking(userMessage, modelName) }
                    },
                    max_tokens = maxTokens,
                    temperature = 0.7,
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);

                if (result?.Choices?.Count > 0)
                    return StripThinkingBlocks(result.Choices[0].Message?.EffectiveContent?.Trim() ?? string.Empty);

                throw new Exception("No choices in LM Studio API response");
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex) { throw new Exception($"Failed to connect to LM Studio at {_baseUrl}: {ex.Message}", ex); }
            finally { _semaphore.Release(); }
        }

        public async Task<string> AnalyzeMultipleImagesWithSystemPromptAsync(string modelName, IList<string> imagePaths, string userPrompt, string systemPrompt, int maxTokens = 36000, CancellationToken cancellationToken = default)
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
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);

                if (result?.Choices?.Count > 0)
                    return StripThinkingBlocks(result.Choices[0].Message?.EffectiveContent?.Trim() ?? string.Empty);

                throw new Exception("Invalid response format from LM Studio API");
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex) { throw new Exception($"Failed to connect to LM Studio at {_baseUrl}: {ex.Message}", ex); }
            finally { _semaphore.Release(); }
        }

        public Task SetBaseUrlAsync(string baseUrl)
        {
            _logger.LogInfo($"SetBaseUrlAsync called. Current URL: {_baseUrl}");
            return Task.CompletedTask;
        }

        private byte[] ResizeImageForVision(string imagePath, int maxWidth, int maxHeight, bool useJpeg = false)
        {
            try
            {
                using var image = Image.Load(imagePath);
                double aspectRatio = (double)image.Width / image.Height;
                int newWidth, newHeight;

                if (image.Width > image.Height)
                {
                    newWidth = Math.Min(image.Width, maxWidth);
                    newHeight = (int)(newWidth / aspectRatio);
                }
                else
                {
                    newHeight = Math.Min(image.Height, maxHeight);
                    newWidth = (int)(newHeight * aspectRatio);
                }

                if (newWidth <= 0) newWidth = 1;
                if (newHeight <= 0) newHeight = 1;

                image.Mutate(x => x.Resize(newWidth, newHeight));

                using var ms = new MemoryStream();
                if (useJpeg)
                    image.SaveAsJpeg(ms, new JpegEncoder { Quality = 50 });
                else
                    image.SaveAsPng(ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error resizing image {imagePath}: {ex.Message}");
                return File.ReadAllBytes(imagePath);
            }
        }

        private static bool IsThinkingModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            var lower = modelName.ToLowerInvariant();
            return lower.Contains("qwen3") || lower.Contains("qwq") || lower.Contains("deepseek-r1") ||
                   lower.Contains("-r1-") || lower.Contains("gemma4") || lower.Contains("thinking");
        }

        private static string SuppressThinking(string prompt, string modelName)
        {
            if (!IsThinkingModel(modelName)) return prompt;
            return "/no_think\n" + prompt;
        }

        private static string StripThinkingBlocks(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var result = System.Text.RegularExpressions.Regex.Replace(
                text, @"<think(?:ing)?>[\s\S]*?</think(?:ing)?>", string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

            var extracted = PromptParser.ExtractFromNumberedMarkdownThinking(result);
            if (extracted != null) return extracted;

            if (System.Text.RegularExpressions.Regex.IsMatch(result,
                    @"^(?:The user wants|I need to|The image shows|Let me analyze|I'll analyze)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var stripped = PromptParser.StripThinking(result);
                if (stripped.Length > 50 && stripped.Length < result.Length)
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
