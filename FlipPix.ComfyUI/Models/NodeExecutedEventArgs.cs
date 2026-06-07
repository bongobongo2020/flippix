using System;
using System.Collections.Generic;

namespace FlipPix.ComfyUI.Models
{
    /// <summary>A single media file reported by a node's "executed" websocket message.</summary>
    public sealed class OutputFileRef
    {
        public string Filename { get; set; } = string.Empty;
        public string Subfolder { get; set; } = string.Empty;
        public string Type { get; set; } = "output"; // "output" | "temp" | "input"

        /// <summary>"subfolder/filename" or just "filename".</summary>
        public string RelativePath =>
            string.IsNullOrEmpty(Subfolder) ? Filename : $"{Subfolder}/{Filename}";
    }

    /// <summary>
    /// Raised when ComfyUI emits an "executed" message for a node — i.e. that node has
    /// finished and produced output. Lets a ViewModel react to individual nodes completing
    /// (e.g. show each seed-hunt sample as soon as it is rendered).
    /// </summary>
    public sealed class NodeExecutedEventArgs : EventArgs
    {
        public string PromptId { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public List<OutputFileRef> Files { get; } = new();
    }
}
