using System.Globalization;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// The sampler settings a single <see cref="LMStudioService"/> call may override.
    ///
    /// <para><b>Why this exists.</b> Every call in the app shipped the same body: temperature 0.7, no
    /// repetition penalty of any kind, and chain-of-thought disabled. That is the right shape for the
    /// short single-answer calls the app is mostly made of — describe this image, write one prompt — and
    /// the wrong shape for the one call that asks a model to produce N structurally identical blocks in a
    /// single turn.</para>
    ///
    /// <para>Observed 2026-08-18: a 15-clip H3 Cast Hybrid chain came back with three distinct beats and
    /// then twelve verbatim copies, alternating two of them to the end. Nothing was truncated — the reply
    /// was 8.5k tokens against a 32k budget. With every block sharing a six-section skeleton and nothing
    /// penalising reuse, the sampler simply fell into a two-cycle, and with reasoning switched off the
    /// model had nowhere to enumerate the beats before writing them. Twelve wasted renders, ~85 minutes of
    /// GPU.</para>
    ///
    /// <para>The defaults reproduce the old request exactly, so this is inert everywhere it is not passed.</para>
    /// </summary>
    /// <param name="Temperature">Sampling temperature. 0.7 is what every call used before this type existed.</param>
    /// <param name="PresencePenalty">
    /// OpenAI-standard flat penalty on any token already emitted. The blunt instrument, and the one that
    /// actually breaks a copy loop — it does not care how often a token appeared, only that it did.
    /// </param>
    /// <param name="FrequencyPenalty">
    /// OpenAI-standard penalty proportional to how often a token has been emitted. Kept lower than
    /// <paramref name="PresencePenalty"/> for structured output: the section labels are *meant* to recur,
    /// and a high frequency penalty starts corrupting them.
    /// </param>
    /// <param name="RepeatPenalty">
    /// llama.cpp's own penalty over a sliding window of recent tokens. 0 = omit the field; 1.0 = no
    /// penalty; ~1.1 is the usual working value. Ignored by servers that do not implement it.
    /// </param>
    /// <param name="AllowThinking">
    /// Lets a reasoning model use its scratchpad. Off everywhere by default, because a model that routes
    /// its whole answer into <c>reasoning_content</c> returns an empty <c>content</c> and silently breaks
    /// the step downstream — <c>StripThinkingBlocks</c> catches the in-band form but not that one. Worth
    /// turning on only where the task is genuinely a planning task and the caller tolerates the latency.
    /// </param>
    public readonly record struct LlmSampling(
        double Temperature = 0.7,
        double PresencePenalty = 0,
        double FrequencyPenalty = 0,
        double RepeatPenalty = 0,
        bool AllowThinking = false)
    {
        /// <summary>Exactly the request the app sent before this type existed.</summary>
        public static LlmSampling Default => new();

        /// <summary>
        /// For writing a multi-clip story chain in one reply. Penalties strong enough to make a verbatim
        /// block copy expensive, temperature nudged up so the model reaches for a different beat rather
        /// than the nearest one, and the scratchpad open so it can split the story before writing it.
        /// </summary>
        public static LlmSampling StoryChain => new(
            Temperature: 0.85, PresencePenalty: 0.6, FrequencyPenalty: 0.35,
            RepeatPenalty: 1.08, AllowThinking: true);

        /// <summary>
        /// The retry after a chain came back looping. Harder on repetition than
        /// <see cref="StoryChain"/> — at this point the model has already demonstrated it will copy a
        /// block, and a slightly mangled beat is worth more than an exact duplicate of one already used.
        /// </summary>
        public static LlmSampling StoryChainRetry => new(
            Temperature: 0.95, PresencePenalty: 1.0, FrequencyPenalty: 0.6,
            RepeatPenalty: 1.12, AllowThinking: true);

        /// <summary>One log fragment naming whatever is not the default, or empty when nothing is.</summary>
        public string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            var parts = new System.Collections.Generic.List<string>();
            if (Temperature != 0.7) parts.Add($"temp {Temperature.ToString("0.##", c)}");
            if (PresencePenalty != 0) parts.Add($"presence {PresencePenalty.ToString("0.##", c)}");
            if (FrequencyPenalty != 0) parts.Add($"frequency {FrequencyPenalty.ToString("0.##", c)}");
            if (RepeatPenalty > 0) parts.Add($"repeat {RepeatPenalty.ToString("0.##", c)}");
            if (AllowThinking) parts.Add("thinking on");
            return parts.Count == 0 ? string.Empty : $", sampling: {string.Join(", ", parts)}";
        }
    }
}
