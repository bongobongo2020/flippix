using FlipPix.Remote.Engine;

namespace FlipPix.UI.Services;

/// <summary>
/// The remote's stand-in for the desktop's LMStudioService, carrying only the two calls the linked
/// story-chain code makes (StoryBeatSheet, ClipChainWriter). Everything goes to the desktop's LLM
/// server through <see cref="LlmClient"/>.
///
/// <para>Thinking is always off, even where the desktop's sampling profile allows it. On a reasoning
/// model the scratchpad is spent from the answer's own token budget, and the desktop measured a beat
/// sheet come back empty that way; with thinking off the same model answered in seconds.</para>
/// </summary>
public sealed class LMStudioService
{
    private readonly LlmClient _llm;

    public LMStudioService(LlmClient llm) => _llm = llm;

    public Task<string> SendTextChatAsync(string model, string systemPrompt, string userMessage,
        int maxTokens = 2000, CancellationToken cancellationToken = default, LlmSampling? sampling = null)
    {
        var s = sampling ?? LlmSampling.Default;
        return _llm.ChatAsync(systemPrompt, userMessage, Array.Empty<byte[]>(), maxTokens,
            s.Temperature, cancellationToken, s.RepeatPenalty);
    }

    /// <summary>The desktop reads a file here; the remote's "path" is a picture already in memory,
    /// looked up by <see cref="Attach"/>'s key.</summary>
    public Task<string> AnalyzeImageWithSystemPromptAsync(string model, string imagePath, string userPrompt,
        string systemPrompt, int maxTokens = 36000, CancellationToken cancellationToken = default,
        LlmSampling? sampling = null)
    {
        var s = sampling ?? LlmSampling.Default;
        var images = _attached.TryGetValue(imagePath, out var jpeg) ? new[] { jpeg } : Array.Empty<byte[]>();
        return _llm.ChatAsync(systemPrompt, userPrompt, images, maxTokens,
            s.Temperature, cancellationToken, s.RepeatPenalty);
    }

    private readonly Dictionary<string, byte[]> _attached = new();

    /// <summary>Makes a JPEG available to <see cref="AnalyzeImageWithSystemPromptAsync"/> under a key.</summary>
    public string Attach(byte[] jpeg)
    {
        var key = "mem:" + _attached.Count;
        _attached[key] = jpeg;
        return key;
    }
}
