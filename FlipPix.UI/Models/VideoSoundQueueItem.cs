using System.IO;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// A serializable snapshot of a Video Sound job: one input video re-generated through the
    /// LTX-2.3 audio-video workflow (workflow/video/ltx/VideoSound.json) with a [VISUAL]/[SPEECH]/
    /// [SOUNDS] directing prompt, producing a new clip with synchronized speech and sound effects.
    /// </summary>
    public class VideoSoundQueueItem : BaseQueueItem
    {
        public string VideoPath { get; set; } = string.Empty;

        /// <summary>Optional reference voice audio (drives LTXVReferenceAudio). Empty = workflow default.</summary>
        public string ReferenceAudioPath { get; set; } = string.Empty;

        /// <summary>The full directing prompt in [VISUAL]/[SPEECH]/[SOUNDS] format.</summary>
        public string Prompt { get; set; } = string.Empty;

        public int Width { get; set; } = 720;
        public int Height { get; set; } = 1280;

        public long Seed { get; set; } = -1;
        public string? OutputVideoPath { get; set; }

        public string DisplayText =>
            !string.IsNullOrEmpty(VideoPath)
                ? $"{Path.GetFileName(VideoPath)} → {Width}×{Height}"
                : "(no input)";
    }
}
