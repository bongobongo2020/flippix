using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Step two of a story chain: one LLM call per clip, looped.
    ///
    /// <para>The counterpart to <see cref="StoryBeatSheet"/>, and the half that fixes the failure. Every H3
    /// story tab used to ask for the whole chain in one reply — N structurally identical blocks in a single
    /// turn — and a local model loses that past roughly four blocks: the cast tags start swapping between
    /// characters, then arrive malformed, and the prose degenerates into unpunctuated word-salad, with the
    /// reply usually stopping several clips short of the count asked for.</para>
    ///
    /// <para>Writing one clip per call is ~400 tokens of output, inside every local model's reliable range,
    /// and it moves the clip count out of the model's hands: the chain has N clips because this loop ran N
    /// times. It also makes progress visible and cancellation responsive, where the single call was one
    /// opaque multi-minute wait.</para>
    ///
    /// <para>Shared by 🌹🎯 H3 Eros / 🧪 H3 Experimental, 🪪🎬 H3 Multi and 🪪👥⚡ H3 Cast Hybrid. Each supplies
    /// its own system prompt, request builder and validator; the loop, the retry and the reporting are here.</para>
    /// </summary>
    public static class ClipChainWriter
    {
        /// <summary>
        /// Runs the loop. Returns the clip bodies that came back renderable, in order — which may be fewer
        /// than <paramref name="clipCount"/> if a clip failed both its attempts, and the caller is told so.
        /// </summary>
        /// <param name="lm">The chat service.</param>
        /// <param name="model">The resolved model name.</param>
        /// <param name="system">The system prompt every clip call is sent.</param>
        /// <param name="clipCount">How many clips to write. Authoritative — the loop owns it.</param>
        /// <param name="buildRequest">(clipIndex0Based, rejectionReason) → the user message. The reason is
        /// empty on the first attempt and, on the retry, the sentence <paramref name="validate"/> returned.</param>
        /// <param name="normalize">Raw reply → the clip body: the caller's own cleanup (fence stripping,
        /// label canonicalisation, taking off a clip header the model emitted despite being told not to).</param>
        /// <param name="validate">(clipIndex0Based, body) → null when the clip is good, otherwise the
        /// reason to retry with, phrased to be read by the model as "Your previous attempt was rejected:
        /// &lt;reason&gt;". It takes the index because what a clip must contain is per-clip: the ensemble
        /// checks that a body names the subjects that clip was cast with.</param>
        /// <param name="onProgress">Called with (clipNumber, clipCount) before each clip's first attempt.</param>
        /// <param name="log">Where per-clip and warning lines go.</param>
        /// <param name="describe">Body → the parenthetical detail logged for a written clip
        /// ("1,842 chars, 8 shots"). Optional.</param>
        /// <param name="maxTokens">Per-clip ceiling. A clip is 350-500 words, so the default is roughly four
        /// times an honest one: headroom for a verbose model, and small enough that a runaway costs seconds
        /// rather than an hour.</param>
        public static async Task<List<string>> WriteAsync(
            LMStudioService lm,
            string model,
            string system,
            int clipCount,
            Func<int, string, string> buildRequest,
            Func<string, string> normalize,
            Func<int, string, string?> validate,
            Action<int, int> onProgress,
            Action<string> log,
            Func<string, string>? describe = null,
            int maxTokens = 3000,
            CancellationToken token = default)
        {
            var bodies = new List<string>(clipCount);
            var short_ = new List<int>();

            for (var i = 0; i < clipCount; i++)
            {
                token.ThrowIfCancellationRequested();
                onProgress(i + 1, clipCount);

                var body = await WriteOneAsync(
                    lm, model, system, i, buildRequest, normalize, validate, log, maxTokens, token);

                if (body.Length == 0)
                {
                    short_.Add(i + 1);
                    continue;
                }

                bodies.Add(body);
                log($"Clip {i + 1}/{clipCount} written" +
                    (describe != null ? $" ({describe(body)})" : string.Empty));
            }

            if (short_.Count > 0)
                log($"WARNING: clip(s) {string.Join(", ", short_)} came back with nothing to render and were " +
                    $"skipped — the chain is {short_.Count} clip(s) short. Re-run Analyze, or write them in " +
                    "by hand.");

            return bodies;
        }

        /// <summary>
        /// One clip, written and checked, with a single retry that names what was wrong with the first
        /// attempt. Returns the body, or empty when neither attempt produced anything renderable.
        ///
        /// <para>The retry is what replaces the repair passes the one-reply flow needed. Rewriting the clip
        /// from its beat is strictly better than patching a broken body: a body that dropped a character's
        /// tag has usually also written that character out of the action, and re-tagging it in place leaves
        /// the action wrong.</para>
        /// </summary>
        private static async Task<string> WriteOneAsync(
            LMStudioService lm,
            string model,
            string system,
            int index,
            Func<int, string, string> buildRequest,
            Func<string, string> normalize,
            Func<int, string, string?> validate,
            Action<string> log,
            int maxTokens,
            CancellationToken token)
        {
            var best = string.Empty;
            var reason = string.Empty;

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                token.ThrowIfCancellationRequested();

                var raw = await lm.SendTextChatAsync(
                    model, system, buildRequest(index, reason),
                    maxTokens: maxTokens,
                    cancellationToken: token,
                    sampling: LlmSampling.StoryChainFormatted);

                var body = normalize(raw ?? string.Empty);
                var complaint = validate(index, body);

                if (complaint == null) return body;

                // A body that failed only a soft check is still worth keeping over nothing; one that failed
                // because it is empty or unrenderable is not, and the caller's validator says which by
                // returning a body-less complaint on an empty body.
                if (body.Length > 0) best = body;
                reason = complaint;

                if (attempt == 2)
                    log($"WARNING: clip {index + 1} still fails after a retry — {complaint} " +
                        "Check that clip, or re-run Analyze.");
            }

            return best;
        }
    }
}
