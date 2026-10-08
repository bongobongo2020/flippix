using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    public partial class H3VideoEditorViewModel
    {
        // ── Timeline Clips ───────────────────────────────────────────────────────────────────────

        public ObservableCollection<H3TimelineClip> TimelineClips { get; } = new();

        /// <summary>Available clips in the browser (from project folder or generated).</summary>
        public ObservableCollection<H3TimelineClip> BrowserClips { get; } = new();

        [ObservableProperty]
        private double _defaultClipDuration = 5.0;

        [ObservableProperty]
        private bool _chainClips = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TimelineDuration))]
        [NotifyPropertyChangedFor(nameof(TimelineDurationDisplay))]
        private double _totalDuration;

        public string TimelineDuration => $"{TotalDuration:0.0}s";
        public string TimelineDurationDisplay => $"Duration: {FormatDuration(TotalDuration)}";

        public RelayCommand AddClipCommand { get; private set; } = null!;
        public RelayCommand<H3TimelineClip> RemoveClipCommand { get; private set; } = null!;
        public RelayCommand<H3TimelineClip> MoveClipUpCommand { get; private set; } = null!;
        public RelayCommand<H3TimelineClip> MoveClipDownCommand { get; private set; } = null!;
        public RelayCommand ClearTimelineCommand { get; private set; } = null!;

        private void InitializeTimeline()
        {
            AddClipCommand = new RelayCommand(AddClip);
            RemoveClipCommand = new RelayCommand<H3TimelineClip>(RemoveClip);
            MoveClipUpCommand = new RelayCommand<H3TimelineClip>(MoveClipUp, c => c != null && TimelineClips.IndexOf(c) > 0);
            MoveClipDownCommand = new RelayCommand<H3TimelineClip>(MoveClipDown, c => c != null && TimelineClips.IndexOf(c) < TimelineClips.Count - 1);
            ClearTimelineCommand = new RelayCommand(ClearTimeline, () => TimelineClips.Count > 0);

            TimelineClips.CollectionChanged += (_, _) =>
            {
                RecalculateTimeline();
                GenerateClipsCommand.NotifyCanExecuteChanged();
                JoinClipsCommand.NotifyCanExecuteChanged();
                ClearTimelineCommand.NotifyCanExecuteChanged();
            };
        }

        private void AddClip()
        {
            var clip = new H3TimelineClip
            {
                Index = TimelineClips.Count,
                Prompt = PromptText,
                DurationSeconds = DefaultClipDuration,
                UseMotionContext = ChainClips
            };
            TimelineClips.Add(clip);
            SelectedClip = clip;
            RecalculateTimeline();
            AddLog($"Added clip {clip.DisplayIndex} ({clip.DurationSeconds}s)");
        }

        /// <summary>Adds a clip from the browser to the timeline.</summary>
        public void AddClipFromBrowser(H3TimelineClip browserClip)
        {
            var clip = new H3TimelineClip
            {
                Index = TimelineClips.Count,
                Prompt = browserClip.Prompt,
                DurationSeconds = browserClip.DurationSeconds,
                OutputPath = browserClip.OutputPath,
                ThumbnailPath = browserClip.ThumbnailPath,
                State = browserClip.IsRendered ? H3ClipState.Rendered : H3ClipState.Pending,
                UseMotionContext = ChainClips
            };
            TimelineClips.Add(clip);
            SelectedClip = clip;
            RecalculateTimeline();
            AddLog($"Added clip from browser: {clip.DisplayIndex}");
        }

        private void RemoveClip(H3TimelineClip? clip)
        {
            if (clip == null) return;
            var index = clip.DisplayIndex;
            TimelineClips.Remove(clip);
            if (SelectedClip == clip)
                SelectedClip = TimelineClips.FirstOrDefault();
            RecalculateTimeline();
            AddLog($"Removed clip {index}");
        }

        private void MoveClipUp(H3TimelineClip? clip)
        {
            if (clip == null) return;
            var index = TimelineClips.IndexOf(clip);
            if (index <= 0) return;
            TimelineClips.Move(index, index - 1);
            RecalculateTimeline();
        }

        private void MoveClipDown(H3TimelineClip? clip)
        {
            if (clip == null) return;
            var index = TimelineClips.IndexOf(clip);
            if (index < 0 || index >= TimelineClips.Count - 1) return;
            TimelineClips.Move(index, index + 1);
            RecalculateTimeline();
        }

        private void ClearTimeline()
        {
            TimelineClips.Clear();
            SelectedClip = null;
            TotalDuration = 0;
            AddLog("Timeline cleared");
        }

        /// <summary>Recalculates clip indices and start times after any change.</summary>
        private void RecalculateTimeline()
        {
            double startTime = 0;
            for (int i = 0; i < TimelineClips.Count; i++)
            {
                var clip = TimelineClips[i];
                clip.Index = i;
                clip.StartTime = startTime;
                startTime += clip.DurationSeconds;
            }
            TotalDuration = startTime;
            OnPropertyChanged(nameof(TimelineDuration));
            OnPropertyChanged(nameof(TimelineDurationDisplay));
        }

        // ── Clip Operations ──────────────────────────────────────────────────────────────────────

        public RelayCommand<H3TimelineClip> CutClipCommand { get; private set; } = null!;
        public RelayCommand<H3TimelineClip> ExtendClipCommand { get; private set; } = null!;
        public RelayCommand PrependClipCommand { get; private set; } = null!;
        public RelayCommand AppendClipCommand { get; private set; } = null!;

        /// <summary>Splits a clip at the midpoint, creating two clips.</summary>
        public void CutClip(H3TimelineClip? clip)
        {
            if (clip == null) return;
            var index = TimelineClips.IndexOf(clip);
            if (index < 0) return;

            var halfDuration = clip.DurationSeconds / 2;
            clip.DurationSeconds = halfDuration;

            var newClip = new H3TimelineClip
            {
                Index = index + 1,
                Prompt = clip.Prompt,
                DurationSeconds = halfDuration,
                UseMotionContext = true
            };

            TimelineClips.Insert(index + 1, newClip);
            RecalculateTimeline();
            AddLog($"Cut clip {clip.DisplayIndex} into two {halfDuration:0.0}s clips");
        }

        /// <summary>Extends a clip's duration.</summary>
        public void ExtendClip(H3TimelineClip? clip, double additionalSeconds = 2.5)
        {
            if (clip == null) return;
            clip.DurationSeconds += additionalSeconds;
            RecalculateTimeline();
            AddLog($"Extended clip {clip.DisplayIndex} by {additionalSeconds}s to {clip.DurationSeconds}s");
        }

        /// <summary>Inserts a new clip before the selected one.</summary>
        public void PrependClip()
        {
            var selected = SelectedClip;
            var index = selected != null ? TimelineClips.IndexOf(selected) : 0;

            var clip = new H3TimelineClip
            {
                Prompt = PromptText,
                DurationSeconds = DefaultClipDuration,
                UseMotionContext = index > 0
            };

            TimelineClips.Insert(index, clip);
            SelectedClip = clip;
            RecalculateTimeline();
            AddLog($"Prepended new clip at position {clip.DisplayIndex}");
        }

        /// <summary>Inserts a new clip after the selected one.</summary>
        public void AppendClip()
        {
            var selected = SelectedClip;
            var index = selected != null ? TimelineClips.IndexOf(selected) + 1 : TimelineClips.Count;

            var clip = new H3TimelineClip
            {
                Prompt = PromptText,
                DurationSeconds = DefaultClipDuration,
                UseMotionContext = ChainClips
            };

            TimelineClips.Insert(index, clip);
            SelectedClip = clip;
            RecalculateTimeline();
            AddLog($"Appended new clip at position {clip.DisplayIndex}");
        }

        // ── Video Browser ────────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        private string _browserFolder = string.Empty;

        public RelayCommand RefreshBrowserCommand { get; private set; } = null!;
        public RelayCommand<H3TimelineClip> AddFromBrowserCommand { get; private set; } = null!;

        public void RefreshBrowser()
        {
            BrowserClips.Clear();
            if (string.IsNullOrEmpty(BrowserFolder) || !System.IO.Directory.Exists(BrowserFolder))
                return;

            try
            {
                var videoFiles = System.IO.Directory.GetFiles(BrowserFolder, "*.mp4")
                    .Concat(System.IO.Directory.GetFiles(BrowserFolder, "*.webm"))
                    .OrderBy(f => f);

                int index = 0;
                foreach (var file in videoFiles)
                {
                    var clip = new H3TimelineClip
                    {
                        Index = index++,
                        OutputPath = file,
                        DurationSeconds = DefaultClipDuration, // TODO: Read actual duration
                        State = H3ClipState.Rendered
                    };
                    BrowserClips.Add(clip);
                }
                AddLog($"Found {BrowserClips.Count} clips in browser folder");
            }
            catch (Exception ex)
            {
                AddLog($"Error scanning browser folder: {ex.Message}");
            }
        }

        private static string FormatDuration(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }
    }
}
