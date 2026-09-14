using System;
using System.Collections.Generic;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// One clip of a saved story, and what the user wants it to do instead — everything
    /// <c>H3ExperimentalViewModel.RewriteClipAsync</c> needs to write that clip again on its own.
    ///
    /// <para><b>Why the story is not in here.</b> The clip writer never sends the whole story to a clip
    /// call either: a model handed the whole story writes a little of all of it into every clip. What a
    /// rewrite needs is the clip as it stands, the clip before it and the clip after it — which is the same
    /// context the original call had, in prose instead of beats.</para>
    /// </summary>
    public sealed record ClipRewriteRequest
    {
        /// <summary>Which clip, counting from zero.</summary>
        public int Index { get; init; }

        /// <summary>How many clips the story has, so the request can say "clip 4 of 11".</summary>
        public int ClipCount { get; init; }

        /// <summary>The clip length the set was written for; the shot timestamps run to it.</summary>
        public double Seconds { get; init; }

        /// <summary>What the user typed under the clip: the action this clip should play.</summary>
        public string Direction { get; init; } = string.Empty;

        /// <summary>The clip as stored — no reference line, no wardrobe lock.</summary>
        public string Body { get; init; } = string.Empty;

        /// <summary>The clip before this one, as it stands in the editor, or null for the first clip.</summary>
        public string? Previous { get; init; }

        /// <summary>The clip after this one, or null for the last clip.</summary>
        public string? Next { get; init; }

        /// <summary>The wardrobe the set was written against; the rewrite may not re-dress anyone.</summary>
        public string Wardrobe { get; init; } = string.Empty;

        /// <summary>The cast's nouns ("man", "woman") in picture order, when the set recorded them.</summary>
        public IReadOnlyList<string> CastNouns { get; init; } = Array.Empty<string>();

        /// <summary>The saved set's build tag — "researched", "shipped" or one of <c>H3SpecPrompt</c>'s.
        /// The rewrite is written in the build the rest of the story is in, whatever the tab's switches
        /// say right now.</summary>
        public string PromptBuild { get; init; } = string.Empty;

        /// <summary>The story's name, for the log lines.</summary>
        public string Title { get; init; } = string.Empty;
    }
}
