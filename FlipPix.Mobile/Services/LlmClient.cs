using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlipPix.Mobile.Services;

/// <summary>
/// A minimal OpenAI-compatible chat client. Thinking is switched off on every call: a reasoning
/// model otherwise spends the whole token budget thinking and returns nothing to show.
/// </summary>
public sealed class LlmClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>"http://host:1234" and "http://host:1234/v1" both become ".../v1".</summary>
    public static string ApiRoot(string url)
    {
        var u = MobileSettings.NormalizeUrl(url);
        if (u.Length == 0) return "";
        return u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? u : u + "/v1";
    }

    /// <summary>Returns the model ids on offer, or throws with a readable message.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(string url, CancellationToken ct = default)
    {
        var root = ApiRoot(url);
        if (root.Length == 0) throw new InvalidOperationException("That doesn't look like an address. Try 10.0.0.10:1234.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(root + "/models", cts.Token));
            return doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "").Where(s => s.Length > 0).ToList();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("No answer within 6 seconds. Is the LLM server running?");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Couldn't reach it: " + ex.Message);
        }
    }

    public static Task<string> ChatAsync(MobileSettings s, string system, string user,
        int maxTokens = 1200, double temperature = 0.8, CancellationToken ct = default) =>
        ChatAsync(s, system, user, Array.Empty<byte[]>(), maxTokens, temperature, ct);

    /// <summary>
    /// A chat turn with pictures attached as JPEG data URLs, in the OpenAI vision shape that LM Studio,
    /// llama.cpp and Ollama all accept. The server's model has to be a vision model.
    /// </summary>
    public static async Task<string> ChatAsync(MobileSettings s, string system, string user,
        IReadOnlyList<byte[]> jpegs, int maxTokens = 1200, double temperature = 0.8, CancellationToken ct = default)
    {
        var root = ApiRoot(s.LlmUrl);
        if (root.Length == 0) throw new InvalidOperationException("Add your LLM address in Settings first.");
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
        if (!string.IsNullOrWhiteSpace(s.LlmModel)) body["model"] = s.LlmModel;

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(root + "/chat/completions", content, ct);
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
