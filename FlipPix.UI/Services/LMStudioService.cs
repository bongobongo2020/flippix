using System;
using System.Collections.Generic;
using System.Diagnostics;
#if !FLIPPIX_LINUX
using System.Drawing;
using System.Drawing.Imaging;
#endif
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        // ── llama.cpp LoRA adapters ──────────────────────────────────────────────────────────────────
        //
        // llama-server started with --lora serves GET/POST /lora-adapters, which read and set each adapter's
        // global scale without a restart. The H3 Video Editor turns the MiniMax-H3 prompt-rewriter LoRA on only
        // while it writes clip prompts and off for the story analysis around it. Every other server (LM Studio,
        // Ollama, vLLM) answers 404 here, which reads as "no adapters" and changes nothing.

        /// <summary>The server's LoRA adapters as (id, scale), or null when it is not a llama-server with
        /// adapters loaded. Never throws.</summary>
        public async Task<List<(int Id, double Scale)>?> GetLoraScalesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await _httpClient.GetAsync($"{_baseUrl.TrimEnd('/')}/lora-adapters", cts.Token);
                if (!response.IsSuccessStatusCode) return null;

                var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token)) as JsonArray;
                if (node == null || node.Count == 0) return null;

                var list = new List<(int Id, double Scale)>();
                foreach (var item in node)
                {
                    var id = item?["id"]?.GetValue<int>();
                    var scale = item?["scale"]?.GetValue<double>() ?? 0.0;
                    if (id.HasValue) list.Add((id.Value, scale));
                }
                return list.Count > 0 ? list : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Sets each listed adapter's global scale on a llama-server. False when the server refused or
        /// is not a llama-server. Never throws except for the caller's own cancellation.</summary>
        public async Task<bool> SetLoraScalesAsync(IEnumerable<(int Id, double Scale)> scales, CancellationToken cancellationToken = default)
        {
            try
            {
                var body = new JsonArray(scales
                    .Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["scale"] = s.Scale })
                    .ToArray());
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync($"{_baseUrl.TrimEnd('/')}/lora-adapters", content, cts.Token);
                _logger.LogInfo($"SetLoraScalesAsync: {body.ToJsonString()} → {(int)response.StatusCode}");
                return response.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        // ── llama-service model switching ────────────────────────────────────────────────────────────
        //
        // llama-service (LlamaTray) serves one model at a time on :8080 and takes switch requests on its own
        // port (LMStudioSettings.ModelSwitchPort, 8812): GET /status, POST /switch?model=<name>. Requests with
        // images move it to the vision model when the loaded one has no projector; the H3 Video Editor's
        // rewriter pass moves it to the H3 rewriter model. Any other server has nothing on that port, which
        // reads as "nothing to switch" (remembered for a minute so image requests do not keep paying for it).

        private sealed record SwitchStatus(string? Current, bool Running, bool Ready, bool Vision, List<string> Models);

        // One switch at a time across every service instance: they all talk to the same tray.
        private static readonly SemaphoreSlim _switchGate = new(1, 1);
        private static readonly Dictionary<string, DateTime> _noSwitcherUntil = new();
        private static readonly TimeSpan SwitchLoadTimeout = TimeSpan.FromMinutes(10);

        /// <summary>Makes sure the llama-service model can see images, loading the vision model when it
        /// cannot. True when the server is ready with a vision model; false when there is nothing to switch
        /// or the switch failed (the request then goes to whatever is loaded). Never throws except for the
        /// caller's own cancellation.</summary>
        public Task<bool> EnsureVisionModelAsync(CancellationToken cancellationToken = default) =>
            EnsureServerModelAsync(_getSettings?.Invoke()?.VisionSwitchModel, anyVision: true, cancellationToken);

        /// <summary>Loads the H3 rewriter model (Qwen + MiniMax H3 rewriter LoRA) on llama-service unless it is
        /// already loaded. Same contract as <see cref="EnsureVisionModelAsync"/>.</summary>
        public Task<bool> EnsureRewriterModelAsync(CancellationToken cancellationToken = default) =>
            EnsureServerModelAsync(_getSettings?.Invoke()?.RewriterSwitchModel, anyVision: false, cancellationToken);

        private string? SwitchControlUrl()
        {
            var port = _getSettings?.Invoke()?.ModelSwitchPort ?? 8812;
            if (port <= 0) return null;
            return Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) ? $"http://{uri.Host}:{port}" : null;
        }

        /// <param name="anyVision">True: any loaded model with a vision projector will do; <paramref name="name"/>
        /// is loaded only when the current one has none. False: exactly <paramref name="name"/>.</param>
        private async Task<bool> EnsureServerModelAsync(string? name, bool anyVision, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var control = SwitchControlUrl();
            if (control == null) return false;
            lock (_noSwitcherUntil)
            {
                if (_noSwitcherUntil.TryGetValue(control, out var until) && DateTime.UtcNow < until) return false;
            }

            await _switchGate.WaitAsync(cancellationToken);
            try
            {
                var status = await GetSwitchStatusAsync(control, cancellationToken);
                if (status == null)
                {
                    lock (_noSwitcherUntil) _noSwitcherUntil[control] = DateTime.UtcNow.AddMinutes(1);
                    return false;
                }

                bool Satisfied(SwitchStatus s) => s.Running && (anyVision ? s.Vision : s.Current == name);
                if (!Satisfied(status))
                {
                    if (!status.Models.Contains(name))
                    {
                        _logger.LogWarning($"llama-service has no model named '{name}' (models.json has: {string.Join(", ", status.Models)}) — leaving '{status.Current}' loaded.");
                        return false;
                    }

                    _logger.LogInfo($"llama-service: switching '{status.Current}' → '{name}' for {(anyVision ? "an image request" : "the H3 rewriter")}");
                    using var switchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    switchCts.CancelAfter(TimeSpan.FromSeconds(10));
                    using var response = await _httpClient.PostAsync(
                        $"{control}/switch?model={Uri.EscapeDataString(name)}", new StringContent(string.Empty), switchCts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning($"llama-service refused the switch to '{name}': {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken)}");
                        return false;
                    }
                }

                // Wait for the model (new or still loading) to serve. The tray marks it ready from its own
                // 5-second health poll, so a poll every 2 s is plenty.
                var deadline = DateTime.UtcNow + SwitchLoadTimeout;
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    status = await GetSwitchStatusAsync(control, cancellationToken);
                    if (status != null && status.Ready && Satisfied(status))
                    {
                        if (sw.Elapsed > TimeSpan.FromSeconds(3))
                            _logger.LogInfo($"llama-service: '{status.Current}' ready after {sw.Elapsed.TotalSeconds:F0}s");
                        return true;
                    }
                    if (status != null && !status.Running && sw.Elapsed > TimeSpan.FromSeconds(15))
                    {
                        _logger.LogError($"llama-service: '{name}' stopped while loading — check llama-server.log on the server.");
                        return false;
                    }
                    if (DateTime.UtcNow > deadline)
                    {
                        _logger.LogError($"llama-service: '{name}' still not ready after {SwitchLoadTimeout.TotalMinutes:F0} min.");
                        return false;
                    }
                    await Task.Delay(2000, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"llama-service model switch to '{name}' failed: {ex.Message}");
                return false;
            }
            finally
            {
                _switchGate.Release();
            }
        }

        /// <summary>The tray's /status, or null when nothing answers there.</summary>
        private async Task<SwitchStatus?> GetSwitchStatusAsync(string control, CancellationToken cancellationToken)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                using var response = await _httpClient.GetAsync($"{control}/status", cts.Token);
                if (!response.IsSuccessStatusCode) return null;
                if (JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token)) is not JsonObject node) return null;

                var models = (node["models"] as JsonArray)?
                    .Select(m => m?["name"]?.GetValue<string>())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .ToList() ?? new List<string>();
                return new SwitchStatus(
                    node["current"]?.GetValue<string>(),
                    node["running"]?.GetValue<bool>() ?? false,
                    node["ready"]?.GetValue<bool>() ?? false,
                    node["vision"]?.GetValue<bool>() ?? false,
                    models);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
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
                await EnsureVisionModelAsync(cancellationToken);
                // Off the caller's (UI) thread: on a slow network share the check and the read
                // can take minutes and would freeze the window.
                var imageBytes = await Task.Run(() =>
                {
                    if (!File.Exists(imagePath))
                    {
                        throw new FileNotFoundException($"Image file not found: {imagePath}");
                    }

                    return ResizeImageForVision(imagePath, 512, 512, useJpeg: true);
                }, cancellationToken);
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
                await EnsureVisionModelAsync(cancellationToken);
                string ToDataUrl(string path)
                {
                    var bytes = ResizeImageForVision(path, 512, 512, useJpeg: true);
                    return $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
                }

                // Off the caller's (UI) thread: on a slow network share this can take minutes.
                var (firstDataUrl, lastDataUrl) = await Task.Run(() =>
                {
                    if (!File.Exists(firstImagePath))
                        throw new FileNotFoundException($"First frame image not found: {firstImagePath}");
                    if (!File.Exists(lastImagePath))
                        throw new FileNotFoundException($"Last frame image not found: {lastImagePath}");
                    return (ToDataUrl(firstImagePath), ToDataUrl(lastImagePath));
                }, cancellationToken);

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
                await EnsureVisionModelAsync(cancellationToken);
                // Off the caller's (UI) thread: on a slow network share the check and the read
                // can take minutes and would freeze the window.
                var imageBytes = await Task.Run(() =>
                {
                    if (!File.Exists(imagePath))
                    {
                        throw new FileNotFoundException($"Image file not found: {imagePath}");
                    }

                    return ResizeImageForVision(imagePath, 512, 512, useJpeg: true);
                }, cancellationToken);
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

        /// <summary>
        /// Sends one chat turn whose answer is a forced <b>tool call</b>, and returns the call the model
        /// made — the client-side half of the MCP flow llama-server exposes (the server hands the call back
        /// to whoever asked; it does not run the tool itself).
        ///
        /// <para><c>tool_choice</c> is forced to the named tool, so the reply is the call itself and not prose
        /// about maybe calling it — the dependable shape for a local model. <paramref name="parametersSchemaJson"/>
        /// is a raw JSON-Schema object passed through verbatim, so the caller owns the argument shape the
        /// grammar constrains the model to.</para>
        ///
        /// <para>Returns the first call's name and its arguments <i>string</i>; null when the server replied
        /// with plain content instead (caller decides whether that text is a usable fallback). Servers that
        /// reject the <c>tools</c> field entirely will throw, as usual.</para>
        /// </summary>
        public async Task<LMStudioToolCall?> CallToolAsync(
            string modelName,
            string systemPrompt,
            string userMessage,
            string toolName,
            string toolDescription,
            string parametersSchemaJson,
            int maxTokens = 4000,
            CancellationToken cancellationToken = default,
            LlmSampling? sampling = null)
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                var s = sampling ?? LlmSampling.Default;
                var messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userMessage }
                };

                _logger.LogInfo($"CallToolAsync: model={modelName}, tool={toolName}, userMessage length={userMessage.Length}{s.Describe()}");

                // ── Attempt 1: the tool call proper ───────────────────────────────────
                var body = BuildChatBody(modelName, messages, maxTokens, s);

                // The schema rides as a parsed node so it reaches the wire verbatim, not re-cased.
                var schema = JsonNode.Parse(parametersSchemaJson)
                               ?? throw new ArgumentException("parametersSchemaJson is not valid JSON.");
                body["tools"] = new object[]
                {
                    new { type = "function", function = new { name = toolName, description = toolDescription, parameters = schema } }
                };
                // OpenAI's forced-call shape — {"type":"function","function":{"name":...}} — which is the
                // one an OpenAI-compatible /v1/chat/completions parses. (This used to send Anthropic's
                // {"type":"tool","name":...}; llama-server silently ignores a tool_choice it cannot read,
                // so the call was never actually forced — a model willing to answer in prose just did,
                // which is the failure this pairs with the fallback below to close.)
                body["tool_choice"] = new Dictionary<string, object>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object> { ["name"] = toolName }
                };

                var result = await PostChatAsync(body, cancellationToken);

                var call = result?.Choices?.FirstOrDefault(c => c.Message?.ToolCalls?.Count > 0)?.Message?.ToolCalls?[0];
                if (call != null)
                {
                    _logger.LogInfo($"CallToolAsync: model called '{call.Function.Name}' with {call.Function.Arguments?.Length ?? 0} chars of arguments");
                    return call;
                }

                // ── Attempt 2: the same schema, enforced as structured output ───────────────
                // Not every model reaches this point able to call a tool: the server only emits tool_calls
                // when the loaded chat template knows how to express them, and when it does not, the tools
                // field is dropped on the floor and the model just writes prose until the token ceiling.
                // response_format carries the identical schema down the grammar path instead, which needs
                // no template support, and the JSON that comes back IS the arguments payload the caller was
                // about to parse — so the call is synthesised from it and the flow upstream never learns
                // the difference. It is a floor, not a substitute: a grammar-constrained reply tends to be
                // terser than a real tool call, which is why the caller checks the brief for thinness.
                _logger.LogInfo("CallToolAsync: no tool_calls in the reply — retrying under " +
                                "response_format json_schema (this template may not support tool calling)");

                var jsonBody = BuildChatBody(modelName, messages, maxTokens, s);
                jsonBody["response_format"] = new Dictionary<string, object>
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new Dictionary<string, object>
                    {
                        ["name"] = toolName,
                        // Parsed fresh: the node above is spoken for by the tools payload.
                        ["schema"] = JsonNode.Parse(parametersSchemaJson)!
                    }
                };

                var structured = await PostChatAsync(jsonBody, cancellationToken);
                var text = StripThinkingBlocks(
                    structured?.Choices?.FirstOrDefault()?.Message?.EffectiveContent?.Trim() ?? string.Empty);
                var arguments = ExtractJsonObject(text);

                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    _logger.LogInfo("CallToolAsync: structured-output fallback stood in for the tool call " +
                                    $"({arguments.Length} chars of arguments)");
                    return new LMStudioToolCall
                    {
                        Type = "function",
                        Function = new LMStudioToolCallFunction { Name = toolName, Arguments = arguments }
                    };
                }

                _logger.LogInfo("CallToolAsync: model replied with content instead of a tool call, and the " +
                                "structured-output fallback produced no JSON object either");
                return null;
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
                if (imagePaths.Count > 0) await EnsureVisionModelAsync(cancellationToken);
                var contentParts = new List<object>();
                contentParts.Add(new { type = "text", text = SuppressThinking(userPrompt, modelName) });

                // Off the caller's (UI) thread: on a slow network share this can take minutes.
                var dataUrls = await Task.Run(() => imagePaths
                    .Where(File.Exists)
                    .Select(imagePath => $"data:image/jpeg;base64,{Convert.ToBase64String(ResizeImageForVision(imagePath, 512, 512, useJpeg: true))}")
                    .ToList(), cancellationToken);
                foreach (var dataUrl in dataUrls)
                    contentParts.Add(new { type = "image_url", image_url = new { url = dataUrl } });

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

#if FLIPPIX_LINUX
        // System.Drawing.Common is Windows-only from .NET 7 on; the Linux build resizes with
        // ImageSharp to the same result: fit inside the bounds, then JPEG q50 or PNG.
        private byte[] ResizeImageForVision(string imagePath, int maxWidth, int maxHeight, bool useJpeg = false)
        {
            try
            {
                using var image = SixLabors.ImageSharp.Image.Load(imagePath);
                double aspectRatio = (double)image.Width / image.Height;
                int newWidth = image.Width > image.Height ? maxWidth : (int)(maxHeight * aspectRatio);
                int newHeight = image.Width > image.Height ? (int)(maxWidth / aspectRatio) : maxHeight;
                newWidth = Math.Max(1, newWidth);
                newHeight = Math.Max(1, newHeight);

                SixLabors.ImageSharp.Processing.ProcessingExtensions.Mutate(image, ctx =>
                    SixLabors.ImageSharp.Processing.ResizeExtensions.Resize(ctx, new SixLabors.ImageSharp.Processing.ResizeOptions
                    {
                        Size = new SixLabors.ImageSharp.Size(newWidth, newHeight),
                        Mode = SixLabors.ImageSharp.Processing.ResizeMode.Stretch,
                        Sampler = SixLabors.ImageSharp.Processing.KnownResamplers.Bicubic
                    }));

                using var outputStream = new MemoryStream();
                if (useJpeg)
                    image.Save(outputStream, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 50 });
                else
                    image.Save(outputStream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                return outputStream.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error resizing image {imagePath}: {ex.Message}");
                return File.ReadAllBytes(imagePath);
            }
        }
#else
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
#endif

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

        /// <summary>
        /// Posts one already-built chat body to <c>/v1/chat/completions</c> and deserialises the reply.
        /// Shared by the two attempts <see cref="CallToolAsync"/> makes, which differ only in the body.
        /// </summary>
        private async Task<LMStudioChatResponse?> PostChatAsync(
            Dictionary<string, object> body, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
        }

        /// <summary>
        /// Pulls the one JSON object out of a reply that is supposed to be nothing but JSON — grammar
        /// constrained replies are clean, but a model that ignores the constraint wraps it in a ```json
        /// fence or a sentence of preamble. Returns empty when there is no braced span at all, which is
        /// the caller's signal that the structured-output path failed too.
        /// </summary>
        private static string ExtractJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var trimmed = text.Trim();

            // A fenced block, ```json or bare ``` — take what is inside it.
            var fence = System.Text.RegularExpressions.Regex.Match(
                trimmed, @"```(?:json)?\s*([\s\S]*?)```",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (fence.Success) trimmed = fence.Groups[1].Value.Trim();

            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start < 0 || end <= start) return string.Empty;

            return trimmed.Substring(start, end - start + 1).Trim();
        }

        private static string StripThinkingBlocks(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 1. Paired <think>...</think> tags (DeepSeek / Qwen with reasoning_in_content=true,
            //    generation prompt provides opening tag so only closing appears in content).
            //    Try paired first, then orphan closing tag.
            var result = System.Text.RegularExpressions.Regex.Replace(
                text, @"<think(?:ing)?>[\s\S]*?</think(?:ing)?>", string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Trim();

            // 1b. Orphan </think> — server's generation_prompt injects <think>, so the model
            //     emits content as "[thinking]</think>\n\nAnswer" without an opening tag.
            //     If we find an orphan closing tag, take only what follows it.
            if (result == text.Trim()) // nothing was stripped by paired-tag pass
            {
                var orphan = System.Text.RegularExpressions.Regex.Match(
                    result, @"</think(?:ing)?>\s*([\s\S]+)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
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
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            {
                var stripped = PromptParser.StripThinking(result);
                if (stripped.Length > 30 && stripped.Length < result.Length)
                    return stripped;
            }

            return result;
        }

#if !FLIPPIX_LINUX
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
#endif

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