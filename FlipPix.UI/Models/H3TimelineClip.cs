using System;
using System.Text.RegularExpressions;
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

        /// <summary>Regex to extract the detailed_description section from a prompt.</summary>
        private static readonly Regex DetailedDescriptionRegex = new(
            @"detailed_description:\s*\n(.*?)(?=\n\s*(?:overall_soundscape:|non_diegetic_music:|\z))",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Regex to extract the summary section from a prompt.</summary>
        private static readonly Regex SummaryRegex = new(
            @"summary:\s*\n(.*?)(?=\n\s*(?:retention_analysis:|detailed_description:|\z))",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Stable ID for drag-drop and tracking.</summary>
        public string Id { get; }

        /// <summary>0-based index in the timeline sequence.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayIndex))]
        private int _index;

        /// <summary>
        /// Server folder (under ComfyUI output) holding this clip's sampled AV latent, saved by the chained
        /// render so the next clip can continue from its tail. Empty until the clip renders chained; a clip
        /// whose predecessor has none starts a new chain.
        /// </summary>
        public string ContextLatentFolder { get; set; } = string.Empty;

        /// <summary>1-based display index for the UI.</summary>
        public int DisplayIndex => Index + 1;

        /// <summary>The prompt text for this clip's generation.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PromptPreview))]
        [NotifyPropertyChangedFor(nameof(ClipActions))]
        [NotifyPropertyChangedFor(nameof(ClipSummary))]
        [NotifyPropertyChangedFor(nameof(IsPreviewCurrent))]
        [NotifyPropertyChangedFor(nameof(IsPreviewStale))]
        [NotifyPropertyChangedFor(nameof(TwoPassBadge))]
        private string _prompt = string.Empty;

        /// <summary>Shortened prompt for timeline display - shows first 80 chars.</summary>
        public string PromptPreview => Prompt.Length > 80 ? Prompt[..77] + "..." : Prompt;

        /// <summary>
        /// The detailed_description section of the prompt - the shot-by-shot actions.
        /// Can be edited directly; changes are written back to the full prompt.
        /// </summary>
        public string ClipActions
        {
            get
            {
                var match = DetailedDescriptionRegex.Match(Prompt);
                return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
            }
            set
            {
                var newActions = (value ?? string.Empty).Trim();
                var match = DetailedDescriptionRegex.Match(Prompt);
                if (match.Success)
                {
                    // Replace the existing detailed_description content
                    var before = Prompt[..match.Groups[1].Index];
                    var after = Prompt[(match.Groups[1].Index + match.Groups[1].Length)..];
                    Prompt = before + newActions + after;
                }
                else if (!string.IsNullOrEmpty(newActions))
                {
                    // No detailed_description section - append one
                    Prompt = Prompt.TrimEnd() + "\n\ndetailed_description:\n" + newActions;
                }
                OnPropertyChanged(nameof(ClipActions));
            }
        }

        /// <summary>The summary section of the prompt - brief description of what happens.</summary>
        public string ClipSummary
        {
            get
            {
                var match = SummaryRegex.Match(Prompt);
                return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
            }
        }

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
        [NotifyPropertyChangedFor(nameof(TwoPassBadge))]
        private string _outputPath = string.Empty;

        /// <summary>Path to a thumbnail image for this clip.</summary>
        [ObservableProperty]
        private string _thumbnailPath = string.Empty;

        /// <summary>Path to the video file for animated timeline preview: the finished render, or failing that
        /// the 🌹 2-Pass first-pass preview.</summary>
        public string? PreviewVideoPath =>
            IsRendered && System.IO.File.Exists(OutputPath) ? OutputPath
            : HasPreview ? PreviewPath
            : null;

        // ── 🌹 ErosMax 2-Pass: preview first, refine on approval ───────────────────────────────────

        /// <summary>The first-pass-only render of this beat (low-res, seconds to make), shown on the timeline
        /// until the final refinement replaces it. Empty until previewed.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPreview))]
        [NotifyPropertyChangedFor(nameof(IsPreviewCurrent))]
        [NotifyPropertyChangedFor(nameof(IsPreviewStale))]
        [NotifyPropertyChangedFor(nameof(PreviewVideoPath))]
        [NotifyPropertyChangedFor(nameof(HasVideoPreview))]
        [NotifyPropertyChangedFor(nameof(TwoPassBadge))]
        private string _previewPath = string.Empty;

        /// <summary>The prompt the preview was rendered from. A prompt edited since makes the preview stale.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsPreviewCurrent))]
        [NotifyPropertyChangedFor(nameof(IsPreviewStale))]
        [NotifyPropertyChangedFor(nameof(TwoPassBadge))]
        private string _previewedPrompt = string.Empty;

        /// <summary>The noise seed the preview was rendered with — the final refinement re-runs the same first
        /// pass from it, so the refined clip is the take that was approved, sharpened.</summary>
        public long Seed { get; set; }

        /// <summary>The previous beat's preview this one continued (its guide frames), or empty when it started
        /// on its own. The final refinement continues the same file, so it re-runs the approved first pass.</summary>
        public string PreviewGuidePath { get; set; } = string.Empty;

        /// <summary>Set by the editor when the beat before this one now has a different preview than the one
        /// this preview continued — the join no longer matches until this beat is previewed again.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsPreviewCurrent))]
        [NotifyPropertyChangedFor(nameof(IsPreviewStale))]
        [NotifyPropertyChangedFor(nameof(TwoPassBadge))]
        private bool _guideChanged;

        public bool HasPreview => !string.IsNullOrEmpty(PreviewPath) && System.IO.File.Exists(PreviewPath);

        /// <summary>Previewed from the prompt the clip has now, continuing the beat before as it is now.</summary>
        public bool IsPreviewCurrent => HasPreview && PreviewedPrompt == Prompt && !GuideChanged;

        public bool IsPreviewStale => HasPreview && !IsPreviewCurrent;

        /// <summary>The 2-Pass corner tag: FINAL, PREVIEW, EDITED (previewed from an older prompt) or RE-JOIN
        /// (the beat before it was re-previewed).</summary>
        public string TwoPassBadge =>
            HasPreview && PreviewedPrompt != Prompt ? "EDITED"
            : HasPreview && GuideChanged ? "RE-JOIN"
            : IsRendered ? "FINAL"
            : HasPreview ? "PREVIEW"
            : string.Empty;

        /// <summary>Forgets the preview (the prompt or the clip it continues changed).</summary>
        public void ClearPreview()
        {
            PreviewPath = string.Empty;
            PreviewedPrompt = string.Empty;
            PreviewGuidePath = string.Empty;
            GuideChanged = false;
        }

        /// <summary>True if this clip has a video preview available.</summary>
        public bool HasVideoPreview => !string.IsNullOrEmpty(PreviewVideoPath);

        /// <summary>True if this clip has been rendered.</summary>
        public bool IsRendered => !string.IsNullOrEmpty(OutputPath);

        /// <summary>Current state of the clip.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        [NotifyPropertyChangedFor(nameof(IsQueued))]
        [NotifyPropertyChangedFor(nameof(QueueBadge))]
        [NotifyPropertyChangedFor(nameof(IsRendering))]
        [NotifyPropertyChangedFor(nameof(IsFailed))]
        private H3ClipState _state = H3ClipState.Pending;

        /// <summary>Place in the editor's ↻ queue (1 = next); 0 when not waiting to be regenerated.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsRegenQueued))]
        [NotifyPropertyChangedFor(nameof(QueueBadge))]
        private int _regenQueuePosition;

        public bool IsRegenQueued => RegenQueuePosition > 0;

        /// <summary>"⏳ QUEUED #2" while waiting for ↻, "⏳ QUEUED" while waiting in a Generate run, else empty.</summary>
        public string QueueBadge => IsRegenQueued ? $"⏳ QUEUED #{RegenQueuePosition}" : IsQueued ? "⏳ QUEUED" : string.Empty;

        public bool IsQueued => State == H3ClipState.Queued;
        public bool IsRendering => State == H3ClipState.Rendering;
        public bool IsFailed => State == H3ClipState.Failed;

        public string StatusText => State switch
        {
            H3ClipState.Pending => "pending",
            H3ClipState.Queued => "queued",
            H3ClipState.Rendering => "rendering...",
            H3ClipState.Previewed => "previewed",
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
        Failed,
        /// <summary>🌹 2-Pass: the first-pass preview is ready; the final refinement has not run.</summary>
        Previewed
    }
}
