using System;
using System.Collections.Generic;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// One saved prompt from a tab's prompt library. Each tab keeps its own store — see
    /// <see cref="FlipPix.UI.Services.ScenePromptLibrary.FolderFor"/> — so the Character tab's scenes and
    /// the I2V tab's takes never appear in each other's picker; they are not interchangeable, because the
    /// two tabs hold their prompts in different shapes.
    ///
    /// <para><b>On the Character tab the prompt is stored without its reference line.</b> That line names the cast
    /// (<c>&lt;Picture 1&gt;</c> / <c>&lt;Picture 2&gt;</c>) and is rewritten by the tab to match however many
    /// character images are loaded at the time, so a scene recalled a month later works with whichever
    /// characters are in the slots — which is the whole point of the library.</para>
    ///
    /// <para>On the I2V tab there is no reference line to strip, and the take is more than one string:
    /// the base pass sits in <see cref="Prompt"/> and each further pass in
    /// <see cref="ContinuationPrompts"/>, kept apart rather than flattened so recalling one restores the
    /// segments to the boxes they came out of.</para>
    ///
    /// <para>The scene image is kept as a small JPEG thumbnail written next to the index
    /// (<see cref="ThumbnailFile"/>) rather than inline in the JSON, so the index stays small enough to
    /// load cheaply and the entry still shows a picture after the original image is moved or deleted.</para>
    /// </summary>
    public sealed class ScenePrompt
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Label in the picker. Auto-derived from the scene image's file name, user-editable.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The H3 prompt body — no reference line. See the type remarks.</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>Where the scene image came from. Informational only; it may no longer exist.</summary>
        public string SceneImagePath { get; set; } = string.Empty;

        /// <summary>
        /// Every reference image the prompt was written against, in slot order — on the I2V tab that order
        /// <i>is</i> the <c>&lt;Picture N&gt;</c> numbering the prompt refers to, so it is part of the entry
        /// rather than a detail. <see cref="SceneImagePath"/> holds the first of them and is what the
        /// thumbnail is rendered from. Empty on single-image tabs.
        /// </summary>
        public List<string> ReferenceImagePaths { get; set; } = new();

        /// <summary>
        /// Prompts for the passes after the first, in order. Empty means a single-pass entry.
        ///
        /// <para>Character-tab entries are always empty here: a story there is a clip <i>chain</i> that
        /// lives inside <see cref="Prompt"/> behind its <c>=== CLIP n of N ===</c> headers. The I2V tab's
        /// continuations are separate boxes on the form, so they are stored separately too.</para>
        /// </summary>
        public List<string> ContinuationPrompts { get; set; } = new();

        /// <summary>Seconds for each continuation, index-matched to <see cref="ContinuationPrompts"/>. A
        /// short entry here (or none) just means the durations are not restored.</summary>
        public List<int> ContinuationSeconds { get; set; } = new();

        /// <summary>File name (not a path) of the thumbnail under the library's <c>thumbs</c> folder.</summary>
        public string ThumbnailFile { get; set; } = string.Empty;

        /// <summary>Aspect ratio resolved when the prompt was written — restored with the scene.</summary>
        public string AspectRatio { get; set; } = string.Empty;

        /// <summary>Duration of a single clip the prompt was written for. A 15-shot prompt does not fit
        /// into 4 seconds.</summary>
        public double LengthSeconds { get; set; }

        /// <summary>
        /// Target length of the whole video the prompt was written for. Above <see cref="LengthSeconds"/>
        /// the prompt is a clip <i>chain</i> (one H3 prompt per <c>=== CLIP n of N ===</c> header) rather
        /// than a single prompt. 0 on entries saved before story mode existed.
        /// </summary>
        public double StoryDurationSeconds { get; set; }

        /// <summary><c>[Shot n]</c> markers, shown in the picker so long and short prompts are told apart.</summary>
        public int Shots { get; set; }

        /// <summary>
        /// The prose the chain was written from, on the tabs that write clips out of a story (🧪 H3
        /// Experimental). Stored with the entry so a take is self-contained: recalling it puts the story
        /// back beside its clips, and re-running Analyze against a new cast writes the same story again
        /// rather than whatever happens to be in the box. Empty on tabs with no story input.
        /// </summary>
        public string StoryText { get; set; } = string.Empty;

        /// <summary>
        /// The wardrobe the chain was written and stamped with, recorded for reference only — it is
        /// <b>not</b> pushed back into the tab on recall. The whole point of a saved chain is to re-run it
        /// with a different cast, and the tab dresses whoever is loaded now from the story itself.
        /// </summary>
        public string Wardrobe { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastUsed { get; set; } = DateTime.Now;
        public int UseCount { get; set; }
    }
}
