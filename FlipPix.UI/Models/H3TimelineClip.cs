using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// A single clip on the H3 Video Editor timeline. Represents a video segment with its prompt,
    /// duration, and position in the sequence.
    /// </summary>
    public partial class H3TimelineClip : ObservableObject
    {
        public H3TimelineClip()
        {
            Id = Guid.NewGuid().ToString("N");
        }

        /// <summary>Stable ID for drag-drop and tracking.</summary>
        public string Id { get; }

        /// <summary>0-based index in the timeline sequence.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayIndex))]
        private int _index;

        /// <summary>1-based display index for the UI.</summary>
        public int DisplayIndex => Index + 1;

        /// <summary>The prompt text for this clip's generation.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PromptPreview))]
        private string _prompt = string.Empty;

        /// <summary>Shortened prompt for timeline display - shows first 80 chars.</summary>
        public string PromptPreview => Prompt.Length > 80 ? Prompt[..77] + "..." : Prompt;

        /// <summary>Duration of this clip in seconds.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DurationDisplay))]
        private double _durationSeconds = 5.0;

        /// <summary>Formatted duration for display.</summary>
        public string DurationDisplay => $"{DurationSeconds:0.0}s";

        /// <summary>Start time in the overall timeline (computed from previous clips).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TimeDisplay))]
        private double _startTime;

        /// <summary>Formatted time range for display.</summary>
        public string TimeDisplay => $"{FormatTime(StartTime)} - {FormatTime(StartTime + DurationSeconds)}";

        /// <summary>Path to the rendered video file, if available.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsRendered))]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        [NotifyPropertyChangedFor(nameof(PreviewVideoPath))]
        [NotifyPropertyChangedFor(nameof(HasVideoPreview))]
        private string _outputPath = string.Empty;

        /// <summary>Path to a thumbnail image for this clip.</summary>
        [ObservableProperty]
        private string _thumbnailPath = string.Empty;

        /// <summary>Path to the video file for animated timeline preview. Returns OutputPath if video exists.</summary>
        public string? PreviewVideoPath => IsRendered && System.IO.File.Exists(OutputPath) ? OutputPath : null;

        /// <summary>True if this clip has a video preview available.</summary>
        public bool HasVideoPreview => !string.IsNullOrEmpty(PreviewVideoPath);

        /// <summary>True if this clip has been rendered.</summary>
        public bool IsRendered => !string.IsNullOrEmpty(OutputPath);

        /// <summary>Current state of the clip.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        [NotifyPropertyChangedFor(nameof(IsQueued))]
        [NotifyPropertyChangedFor(nameof(IsRendering))]
        private H3ClipState _state = H3ClipState.Pending;

        public bool IsQueued => State == H3ClipState.Queued;
        public bool IsRendering => State == H3ClipState.Rendering;

        public string StatusText => State switch
        {
            H3ClipState.Pending => "pending",
            H3ClipState.Queued => "queued",
            H3ClipState.Rendering => "rendering...",
            H3ClipState.Rendered => "done",
            H3ClipState.Failed => "failed",
            _ => string.Empty
        };

        /// <summary>True if this clip is selected in the timeline.</summary>
        [ObservableProperty]
        private bool _isSelected;

        /// <summary>For extend operations: how many frames to extend by.</summary>
        [ObservableProperty]
        private int _extendFrames;

        /// <summary>Whether to use the previous clip's tail as context (chaining).</summary>
        [ObservableProperty]
        private bool _useMotionContext = true;

        private static string FormatTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalMinutes >= 1
                ? $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds / 100}"
                : $"{ts.Seconds}.{ts.Milliseconds / 100}";
        }
    }

    /// <summary>State of a clip in the rendering pipeline.</summary>
    public enum H3ClipState
    {
        Pending,
        Queued,
        Rendering,
        Rendered,
        Failed
    }
}
