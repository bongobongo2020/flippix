using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;

namespace FlipPix.UI.Services
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
            _getBaseUrl = getBaseUrl ?? (() => "http://localhost:1234");
            // Don't set BaseAddress - we'll use full URLs instead to allow changing the URL
            _httpClient.Timeout = TimeSpan.FromMinutes(5); // 5 minute timeout
            _semaphore = new SemaphoreSlim(1, 1); // Limit concurrent requests
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
                CheckMemoryUsage();

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
                CheckMemoryUsage();

                if (!File.Exists(imagePath))
                {
                    throw new FileNotFoundException($"Image file not found: {imagePath}");
                }

                // Send original image without resizing - Qwen-VL handles image processing
                var imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
                var base64Image = Convert.ToBase64String(imageBytes);
                var imageFormat = Path.GetExtension(imagePath).TrimStart('.').ToLower();
                if (imageFormat == "jpg") imageFormat = "jpeg";
                var dataUrl = $"data:image/{imageFormat};base64,{base64Image}";

                _logger.LogInfo($"Sending original image for LM Studio analysis: {imageBytes.Length} bytes ({imageFormat}), max_tokens: {maxTokens}");

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
                                    text = prompt
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
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                _logger.LogInfo($"Sending image analysis request to LM Studio for model: {modelName}");
                _logger.LogInfo($"Image: {Path.GetFileName(imagePath)}, Size: {imageBytes.Length} bytes");

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
                    var analysis = result.Choices[0].Message?.Content?.Trim() ?? string.Empty;

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
                throw new Exception($"Failed to connect to LM Studio. Please ensure LM Studio is running on {_baseUrl}. Error: {ex.Message}", ex);
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
                CheckMemoryUsage();

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
                    var enhancedPrompt = result.Choices[0].Message?.Content?.Trim() ?? string.Empty;

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

        private byte[] ResizeImageForVision(string imagePath, int maxWidth, int maxHeight, bool useJpeg = false)
        {
            try
            {
                using var originalImage = Image.FromFile(imagePath);

                // Calculate the new dimensions while maintaining aspect ratio
                int newWidth, newHeight;
                double aspectRatio = (double)originalImage.Width / originalImage.Height;

                if (originalImage.Width > originalImage.Height)
                {
                    newWidth = maxWidth;
                    newHeight = (int)(maxWidth / aspectRatio);
                }
                else
                {
                    newHeight = maxHeight;
                    newWidth = (int)(maxHeight * aspectRatio);
                }

                // Ensure dimensions are at least 1
                newWidth = Math.Max(1, newWidth);
                newHeight = Math.Max(1, newHeight);

                using var resizedImage = new Bitmap(newWidth, newHeight);
                using var graphics = Graphics.FromImage(resizedImage);

                // Set high-quality interpolation
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

                // Draw the resized image
                graphics.DrawImage(originalImage, 0, 0, newWidth, newHeight);

                // Convert to byte array
                using var outputStream = new MemoryStream();
                if (useJpeg)
                {
                    // Use JPEG with lower quality for much smaller size
                    var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
                    encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                        System.Drawing.Imaging.Encoder.Quality, 50L);

                    var jpegCodec = GetEncoderInfo("image/jpeg");
                    resizedImage.Save(outputStream, jpegCodec, encoderParams);
                }
                else
                {
                    resizedImage.Save(outputStream, ImageFormat.Png);
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

        private static ImageCodecInfo GetEncoderInfo(string mimeType)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            foreach (var codec in codecs)
            {
                if (codec.MimeType == mimeType)
                    return codec;
            }
            throw new NotSupportedException($"Codec not found for MIME type: {mimeType}");
        }

        private void CheckMemoryUsage()
        {
            var memoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
            if (memoryMB > 500) // 500MB threshold
            {
                _logger.LogInfo($"Memory usage is high ({memoryMB}MB), triggering garbage collection");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
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
                _httpClient?.Dispose();
                _semaphore?.Dispose();
                _disposed = true;
            }
        }
    }
}