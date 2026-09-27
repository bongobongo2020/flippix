using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlipPix.Remote.Engine;

/// <summary>Where the LLM is and which model to ask for; empty model means whatever the server has loaded.</summary>
public readonly record struct LlmTarget(string Url, string Model)
{
    public bool IsSet => LlmClient.ApiRoot(Url).Length > 0;
}

/// <summary>
/// A minimal OpenAI-compatible chat client pointed at the desktop's LLM server. Thinking is switched
/// off on every call: a reasoning model otherwise spends the whole token budget thinking and returns
/// nothing to show.
/// </summary>
public sealed class LlmClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly Func<LlmTarget> _target;
    private readonly ComfyGateway _comfy;

    public LlmClient(Func<LlmTarget> target, ComfyGateway comfy)
    {
        _target = target;
        _comfy = comfy;
    }

    public LlmTarget Target => _target();

    /// <summary>"http://host:1234" and "http://host:1234/v1" both become ".../v1".</summary>
    public static string ApiRoot(string url)
    {
        var u = RemoteUrls.Normalize(url);
        if (u.Length == 0) return "";
        return u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? u : u + "/v1";
    }

    public Task<string> ChatAsync(string system, string user,
        int maxTokens = 1200, double temperature = 0.8, CancellationToken ct = default) =>
        ChatAsync(system, user, Array.Empty<byte[]>(), maxTokens, temperature, ct);

    /// <summary>
    /// A chat turn with pictures attached as JPEG data URLs, in the OpenAI vision shape that LM Studio,
    /// llama.cpp and Ollama all accept. The server's model has to be a vision model.
    /// </summary>
    public async Task<string> ChatAsync(string system, string user,
        IReadOnlyList<byte[]> jpegs, int maxTokens = 1200, double temperature = 0.8, CancellationToken ct = default,
        double repeatPenalty = 0)
    {
        var target = _target();
        try
        {
            return await ChatOnceAsync(target, system, user, jpegs, maxTokens, temperature, repeatPenalty, ct);
        }
        catch (InvalidOperationException ex) when (IsOutOfMemory(ex.Message))
        {
            // The LLM shares the GPU with ComfyUI, which keeps its last render's models in VRAM. Unload
            // them and try once more; the next render reloads them from RAM in seconds.
            if (!await _comfy.FreeMemoryAsync(ct)) throw;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            return await ChatOnceAsync(target, system, user, jpegs, maxTokens, temperature, repeatPenalty, ct);
        }
    }

    private static bool IsOutOfMemory(string message) =>
        message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
        || message.Contains("OOM", StringComparison.Ordinal)
        || message.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ChatOnceAsync(LlmTarget target, string system, string user,
        IReadOnlyList<byte[]> jpegs, int maxTokens, double temperature, double repeatPenalty, CancellationToken ct)
    {
        var root = ApiRoot(target.Url);
        if (root.Length == 0) throw new InvalidOperationException("The desktop has no LLM server in its Settings.");
        var body = new Dictionary<string, object?>
        {
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                new
                {
                    role = "user",
                    content = jpegs.Count == 0 ? (object)(user + " /no_think")
                        : jpegs.Select(p => (object)new
                            {
                                type = "image_url",
                                image_url = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(p) },
                            })
                          .Append(new { type = "text", text = user + " /no_think" })
                          .ToArray(),
                },
            },
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["stream"] = false,
            ["chat_template_kwargs"] = new { enable_thinking = false },
        };
        if (!string.IsNullOrWhiteSpace(target.Model)) body["model"] = target.Model;
        // llama.cpp's repeat_penalty; OpenAI-style servers that don't know it ignore it. Never presence or
        // frequency penalties: on repetitive structured output they degenerate into word salad.
        if (repeatPenalty > 0) body["repeat_penalty"] = repeatPenalty;

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await SendAsync(root + "/chat/completions", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"The LLM server couldn't answer: {ErrorMessage(text)}");

        using var doc = JsonDocument.Parse(text);
        var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var reply = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        reply = Regex.Replace(reply, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
        if (reply.Length == 0) throw new InvalidOperationException("The LLM replied with nothing. Try a model without thinking.");
        return reply;
    }

    private static async Task<HttpResponseMessage> SendAsync(string url, HttpContent content, CancellationToken ct)
    {
        try
        {
            return await Http.PostAsync(url, content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("The LLM server couldn't be reached: " + ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("The LLM server took more than 5 minutes to answer.");
        }
    }

    /// <summary>OpenAI-style servers put the reason in error.message; show that, not the JSON around it.</summary>
    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var err = doc.RootElement.GetProperty("error");
            var msg = err.ValueKind == JsonValueKind.String ? err.GetString()
                : err.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (!string.IsNullOrWhiteSpace(msg)) return Trim(msg!, 180);
        }
        catch (Exception) { /* not JSON: fall through to the raw text */ }
        return Trim(body, 180);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
