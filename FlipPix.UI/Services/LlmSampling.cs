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
    /// penalising reuse, the sampler simply fell into a two-cycle. Twelve wasted renders, ~85 minutes of
    /// GPU.</para>
    ///
    /// <para>The penalties that fixed <i>that</i> caused a worse failure of their own — see
    /// <see cref="StoryChainFormatted"/> — and both are now moot: no call in the app asks for N clips in
    /// one reply any more. The chain tabs write one clip per call
    /// (<c>StoryBeatSheet</c> → <c>ClipChainWriter</c>), which is a shape neither failure has.</para>
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
        /// The <b>planning</b> call of a story chain — <c>StoryBeatSheet</c>: read the whole story and
        /// divide it into one beat per clip, with the scratchpad open so the model can enumerate the beats
        /// before committing to the split. Deliberately <b>without</b> the OpenAI-standard
        /// presence/frequency penalties — see <see cref="StoryChainFormatted"/> for why they poison a
        /// single long block.
        /// </summary>
        public static LlmSampling StoryChainBrief => new(
            Temperature: 0.8, RepeatPenalty: 1.05, AllowThinking: true);

        /// <summary>
        /// The <b>writing</b> call of a story chain — <c>ClipChainWriter</c>, once per clip: guide-driven
        /// formatting with the scratchpad closed. The planning has already happened in the beat sheet, so a
        /// reasoning preamble in front of every clip would only add minutes of latency per chain and eat
        /// the token budget.
        ///
        /// <para><b>Why no presence/frequency penalty here (observed 2026-09-01).</b> This profile used to
        /// carry presence 0.6 / frequency 0.35 — the settings the removed <c>StoryChain</c> profile used
        /// for whole-chain-in-one-reply writing — and produced clip
        /// bodies that collapsed into thousands of characters of unpunctuated synonym-walk — "…polishing
        /// buffing shining glimmering sparkling twinkling…", degrading into word fragments ("twink
        /// glitter … radi glow") as the whole-word tokens were used up. llama.cpp applies presence and
        /// frequency penalties over a <i>sliding window</i> of the last <c>penalty_last_n</c> tokens (64 by
        /// default), not over the whole reply: inside that window a flat 0.6 presence penalty is levied on
        /// every token just used — including the function words and, fatally, the sentence-ending
        /// <c>.</c> itself. Once the model enters an enumeration it can neither reuse a word nor close the
        /// sentence, so it walks the thesaurus until the token ceiling.</para>
        ///
        /// <para>The task makes it worse: a chain clip is <i>supposed</i> to restate the style, the setting
        /// and the quoted wardrobe word for word in every clip, which is exactly what these penalties
        /// price out.</para>
        ///
        /// <para>Nothing here guards against a model copying one clip into the next, and nothing needs to:
        /// each call is handed one beat and never sees the others, so there is no block to copy. That is
        /// what the penalties used to be for, back when a single reply had to hold the whole chain.</para>
        /// </summary>
        public static LlmSampling StoryChainFormatted => new(
            Temperature: 0.8, RepeatPenalty: 1.05, AllowThinking: false);

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
