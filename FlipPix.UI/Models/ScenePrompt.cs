using System;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// One saved scene from the MiniMax Character tab's scene library.
    ///
    /// <para><b>The prompt is stored without its reference line.</b> That line names the cast
    /// (<c>&lt;Picture 1&gt;</c> / <c>&lt;Picture 2&gt;</c>) and is rewritten by the tab to match however many
    /// character images are loaded at the time, so a scene recalled a month later works with whichever
    /// characters are in the slots — which is the whole point of the library.</para>
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

        /// <summary>File name (not a path) of the thumbnail under the library's <c>thumbs</c> folder.</summary>
        public string ThumbnailFile { get; set; } = string.Empty;

        /// <summary>Aspect ratio resolved when the prompt was written — restored with the scene.</summary>
        public string AspectRatio { get; set; } = string.Empty;

        /// <summary>Duration the prompt was written for. A 15-shot prompt does not fit into 4 seconds.</summary>
        public double LengthSeconds { get; set; }

        /// <summary><c>[Shot n]</c> markers, shown in the picker so long and short prompts are told apart.</summary>
        public int Shots { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastUsed { get; set; } = DateTime.Now;
        public int UseCount { get; set; }
    }
}
