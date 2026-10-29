using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// The preview's playhead and the edits made at it. The window owns the player and keeps
    /// <see cref="PlayheadSeconds"/> / <see cref="PreviewDurationSeconds"/> current (it ticks while playing and
    /// seeks when the scrub bar is dragged); the edits here read the playhead and rewrite the rendered file with
    /// FFmpeg, re-encoding so the cut lands on the exact frame shown rather than the nearest keyframe.
    ///
    /// <para><b>Split</b> turns one rendered clip into two at the playhead. <b>Trim start/end</b> drop everything
    /// before/after it. The original file is left on disk; the timeline points at the new pieces.</para>
    ///
    /// <para><b>Chain.</b> A clip's saved motion-context latent is the tail of the take as rendered. After a split
    /// that tail is the second piece's end, so the second piece keeps it and the first has none; trimming the end
    /// drops it, since the next clip no longer continues from where this one now stops.</para>
    ///
    /// <para><b>In/Out.</b> Two markers bracket a range of the previewed clip. <b>Delete In→Out</b> cuts that
    /// range out and joins what is either side of it; <b>Keep In→Out</b> keeps only the range. The markers start
    /// at the clip's ends, so an untouched marker means "from the start" / "to the end".</para>
    /// </summary>
    public partial class H3VideoEditorViewModel
    {
        /// <summary>H3 renders at 24 fps; the playhead snaps to its frames.</summary>
        public const double PreviewFps = 24.0;

        /// <summary>Shortest piece an edit may leave, so a slip of the mouse cannot make a 1-frame clip.</summary>
        private const double MinPieceSeconds = 0.25;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayheadDisplay))]
        [NotifyPropertyChangedFor(nameof(CanEditAtPlayhead))]
        private double _playheadSeconds;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlayheadDisplay))]
        [NotifyPropertyChangedFor(nameof(CanEditAtPlayhead))]
        private double _previewDurationSeconds;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEditAtPlayhead))]
        private bool _isEditingClip;

        public RelayCommand SplitAtPlayheadCommand { get; private set; } = null!;
        public RelayCommand TrimStartToPlayheadCommand { get; private set; } = null!;
        public RelayCommand TrimEndToPlayheadCommand { get; private set; } = null!;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MarkRangeDisplay))]
        [NotifyPropertyChangedFor(nameof(CanEditRange))]
        private double _markInSeconds;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MarkRangeDisplay))]
        [NotifyPropertyChangedFor(nameof(CanEditRange))]
        private double _markOutSeconds;

        public RelayCommand MarkInCommand { get; private set; } = null!;
        public RelayCommand MarkOutCommand { get; private set; } = null!;
        public RelayCommand ClearMarksCommand { get; private set; } = null!;
        public RelayCommand DeleteMarkedRangeCommand { get; private set; } = null!;
        public RelayCommand KeepMarkedRangeCommand { get; private set; } = null!;

        /// <summary>"IN 00:01.250 · OUT 00:03.000 · 42 frames marked".</summary>
        public string MarkRangeDisplay
        {
            get
            {
                var frames = (int)Math.Round((MarkOutSeconds - MarkInSeconds) * PreviewFps);
                return $"IN {Timecode(MarkInSeconds)}  ·  OUT {Timecode(MarkOutSeconds)}  ·  {Math.Max(0, frames)} frames marked";
            }
        }

        /// <summary>True when the markers bracket less than the whole clip, so a range edit would change something.</summary>
        public bool CanEditRange =>
            CanEditAtPlayhead &&
            MarkOutSeconds - MarkInSeconds >= MinPieceSeconds &&
            (MarkInSeconds >= MinPieceSeconds || PreviewDurationSeconds - MarkOutSeconds >= MinPieceSeconds);

        /// <summary>"00:03.250 / 00:05.167 · frame 78 / 124".</summary>
        public string PlayheadDisplay
        {
            get
            {
                var frame = (int)Math.Round(PlayheadSeconds * PreviewFps);
                var frames = (int)Math.Round(PreviewDurationSeconds * PreviewFps);
                return $"{Timecode(PlayheadSeconds)} / {Timecode(PreviewDurationSeconds)} · frame {frame} / {frames}";
            }
        }

        /// <summary>The clip the preview is showing, when it is a rendered timeline clip (not the joined film).</summary>
        private H3TimelineClip? PreviewedClip =>
            string.IsNullOrEmpty(PreviewVideoPath)
                ? null
                : TimelineClips.FirstOrDefault(c => c.IsRendered &&
                      string.Equals(c.OutputPath, PreviewVideoPath, StringComparison.OrdinalIgnoreCase));

        public bool CanEditAtPlayhead =>
            !IsEditingClip && !IsGenerating && PreviewDurationSeconds > 0 && PreviewedClip != null;

        private void InitializeScrub()
        {
            SplitAtPlayheadCommand = new RelayCommand(() => _ = EditAtPlayheadAsync(ClipEdit.Split), () => CanEditAtPlayhead);
            TrimStartToPlayheadCommand = new RelayCommand(() => _ = EditAtPlayheadAsync(ClipEdit.TrimStart), () => CanEditAtPlayhead);
            TrimEndToPlayheadCommand = new RelayCommand(() => _ = EditAtPlayheadAsync(ClipEdit.TrimEnd), () => CanEditAtPlayhead);

            MarkInCommand = new RelayCommand(() => SetMarkIn(PlayheadSeconds), () => PreviewDurationSeconds > 0);
            // Out is exclusive (the range is [In, Out)), so marking Out at a frame puts it just after that frame.
            MarkOutCommand = new RelayCommand(() => SetMarkOut(PlayheadSeconds + 1 / PreviewFps), () => PreviewDurationSeconds > 0);
            ClearMarksCommand = new RelayCommand(ResetMarks, () => PreviewDurationSeconds > 0);
            DeleteMarkedRangeCommand = new RelayCommand(() => _ = EditAtPlayheadAsync(ClipEdit.DeleteRange), () => CanEditRange);
            KeepMarkedRangeCommand = new RelayCommand(() => _ = EditAtPlayheadAsync(ClipEdit.KeepRange), () => CanEditRange);
        }

        partial void OnPlayheadSecondsChanged(double value) => NotifyEditCommands();
        partial void OnIsEditingClipChanged(bool value) => NotifyEditCommands();
        partial void OnMarkInSecondsChanged(double value) => NotifyEditCommands();
        partial void OnMarkOutSecondsChanged(double value) => NotifyEditCommands();

        partial void OnPreviewDurationSecondsChanged(double value)
        {
            ResetMarks();
            NotifyEditCommands();
        }

        partial void OnPreviewVideoPathChanged(string value)
        {
            PlayheadSeconds = 0;
            PreviewDurationSeconds = 0; // set again by the window once the new file opens
        }

        /// <summary>Moves the In marker (snapped to a frame); it never passes the Out marker.</summary>
        public void SetMarkIn(double seconds) =>
            MarkInSeconds = Math.Clamp(SnapToFrame(seconds), 0, MarkOutSeconds);

        /// <summary>Moves the Out marker (snapped to a frame); it never passes the In marker.</summary>
        public void SetMarkOut(double seconds) =>
            MarkOutSeconds = Math.Clamp(SnapToFrame(seconds), MarkInSeconds, Math.Max(MarkInSeconds, PreviewDurationSeconds));

        private void ResetMarks()
        {
            MarkInSeconds = 0;
            MarkOutSeconds = PreviewDurationSeconds;
        }

        private void NotifyEditCommands()
        {
            SplitAtPlayheadCommand?.NotifyCanExecuteChanged();
            TrimStartToPlayheadCommand?.NotifyCanExecuteChanged();
            TrimEndToPlayheadCommand?.NotifyCanExecuteChanged();
            MarkInCommand?.NotifyCanExecuteChanged();
            MarkOutCommand?.NotifyCanExecuteChanged();
            ClearMarksCommand?.NotifyCanExecuteChanged();
            DeleteMarkedRangeCommand?.NotifyCanExecuteChanged();
            KeepMarkedRangeCommand?.NotifyCanExecuteChanged();
        }

        /// <summary>Rounds to the nearest frame boundary.</summary>
        public static double SnapToFrame(double seconds) => Math.Round(seconds * PreviewFps) / PreviewFps;

        private static string Timecode(double seconds)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
        }

        private enum ClipEdit { Split, TrimStart, TrimEnd, DeleteRange, KeepRange }

        private async Task EditAtPlayheadAsync(ClipEdit edit)
        {
            var clip = PreviewedClip;
            if (clip == null || !CanEditAtPlayhead) return;

            var source = clip.OutputPath;
            var duration = PreviewDurationSeconds;
            var cut = SnapToFrame(PlayheadSeconds);
            var markIn = SnapToFrame(MarkInSeconds);
            var markOut = SnapToFrame(MarkOutSeconds);
            var isRangeEdit = edit is ClipEdit.DeleteRange or ClipEdit.KeepRange;
            if (isRangeEdit && !CanEditRange)
            {
                AddLog("Set the In and Out markers around the part you want to delete or keep first.");
                return;
            }
            if (!isRangeEdit && (cut < MinPieceSeconds || duration - cut < MinPieceSeconds))
            {
                AddLog($"Move the playhead at least {MinPieceSeconds}s away from either end of the clip to cut there.");
                return;
            }

            var ffmpeg = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpeg))
            {
                AddLog("FFmpeg not found — clip edits need it.");
                return;
            }

            IsEditingClip = true;
            IsLogOpen = true;
            var stamp = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
            var dir = Path.GetDirectoryName(source) ?? ".";
            var stem = Path.GetFileNameWithoutExtension(source);
            string Piece(string tag) => Path.Combine(dir, $"{stem}_{tag}_{stamp}.mp4");

            try
            {
                var index = TimelineClips.IndexOf(clip);
                switch (edit)
                {
                    case ClipEdit.Split:
                    {
                        var first = Piece("a");
                        var second = Piece("b");
                        await Task.Run(() =>
                        {
                            ExtractRange(ffmpeg, source, 0, cut, first);
                            ExtractRange(ffmpeg, source, cut, duration, second);
                        });

                        var tail = new H3TimelineClip
                        {
                            Prompt = clip.Prompt,
                            DurationSeconds = duration - cut,
                            OutputPath = second,
                            ThumbnailPath = await ThumbnailForAsync(ffmpeg, second),
                            State = H3ClipState.Rendered,
                            UseMotionContext = true,
                            ContextLatentFolder = clip.ContextLatentFolder,
                        };
                        clip.OutputPath = first;
                        clip.ThumbnailPath = await ThumbnailForAsync(ffmpeg, first);
                        clip.DurationSeconds = cut;
                        clip.ContextLatentFolder = string.Empty;
                        TimelineClips.Insert(index + 1, tail);
                        AddLog($"✂ Split clip {clip.DisplayIndex} at {Timecode(cut)} → {Timecode(cut)} + {Timecode(duration - cut)}");
                        break;
                    }
                    case ClipEdit.TrimStart:
                    {
                        var kept = Piece("trim");
                        await Task.Run(() => ExtractRange(ffmpeg, source, cut, duration, kept));
                        clip.OutputPath = kept;
                        clip.ThumbnailPath = await ThumbnailForAsync(ffmpeg, kept);
                        clip.DurationSeconds = duration - cut;
                        AddLog($"⇤ Trimmed the first {Timecode(cut)} off clip {clip.DisplayIndex}");
                        break;
                    }
                    case ClipEdit.TrimEnd:
                    {
                        var kept = Piece("trim");
                        await Task.Run(() => ExtractRange(ffmpeg, source, 0, cut, kept));
                        clip.OutputPath = kept;
                        clip.ThumbnailPath = await ThumbnailForAsync(ffmpeg, kept);
                        clip.DurationSeconds = cut;
                        if (!string.IsNullOrEmpty(clip.ContextLatentFolder))
                        {
                            clip.ContextLatentFolder = string.Empty;
                            AddLog("  The next clip can no longer be generated continuing this one — its saved tail was trimmed off.");
                        }
                        AddLog($"Trimmed clip {clip.DisplayIndex} to end at {Timecode(cut)} ⇥");
                        break;
                    }
                    case ClipEdit.DeleteRange:
                    {
                        // Keep what lies either side of the marked range; a side shorter than a usable piece goes too.
                        var keepHead = markIn >= MinPieceSeconds;
                        var keepTail = duration - markOut >= MinPieceSeconds;
                        if (!keepHead && !keepTail)
                        {
                            AddLog("That would delete the whole clip — use × on the clip to remove it instead.");
                            return;
                        }

                        var kept = Piece("cut");
                        if (keepHead && keepTail)
                        {
                            var head = Piece("head");
                            var tailPart = Piece("tail");
                            await Task.Run(() =>
                            {
                                ExtractRange(ffmpeg, source, 0, markIn, head);
                                ExtractRange(ffmpeg, source, markOut, duration, tailPart);
                                JoinPieces(ffmpeg, kept, head, tailPart);
                            });
                            TryDelete(head);
                            TryDelete(tailPart);
                        }
                        else if (keepHead)
                            await Task.Run(() => ExtractRange(ffmpeg, source, 0, markIn, kept));
                        else
                            await Task.Run(() => ExtractRange(ffmpeg, source, markOut, duration, kept));

                        var left = (keepHead ? markIn : 0) + (keepTail ? duration - markOut : 0);
                        clip.OutputPath = kept;
                        clip.ThumbnailPath = await ThumbnailForAsync(ffmpeg, kept);
                        clip.DurationSeconds = left;
                        if (!keepTail && !string.IsNullOrEmpty(clip.ContextLatentFolder))
                        {
                            clip.ContextLatentFolder = string.Empty;
                            AddLog("  The next clip can no longer be generated continuing this one — its saved tail was cut off.");
                        }
                        AddLog($"✕ Deleted {Timecode(markIn)} → {Timecode(markOut)} from clip {clip.DisplayIndex} ({Timecode(left)} left)");
                        break;
                    }
                    case ClipEdit.KeepRange:
                    {
                        var kept = Piece("keep");
                        await Task.Run(() => ExtractRange(ffmpeg, source, markIn, markOut, kept));
                        clip.OutputPath = kept;
                        clip.ThumbnailPath = await ThumbnailForAsync(ffmpeg, kept);
                        clip.DurationSeconds = markOut - markIn;
                        if (duration - markOut >= MinPieceSeconds && !string.IsNullOrEmpty(clip.ContextLatentFolder))
                        {
                            clip.ContextLatentFolder = string.Empty;
                            AddLog("  The next clip can no longer be generated continuing this one — its saved tail was trimmed off.");
                        }
                        AddLog($"Kept only {Timecode(markIn)} → {Timecode(markOut)} of clip {clip.DisplayIndex}");
                        break;
                    }
                }

                RecalculateTimeline();
                SelectedClip = clip;
                PreviewVideoPath = clip.OutputPath;
                AutoSaveTimeline();
                AddLog($"  Original kept on disk: {Path.GetFileName(source)}");
            }
            catch (Exception ex)
            {
                AddLog($"Edit failed: {ex.Message}");
            }
            finally
            {
                IsEditingClip = false;
            }
        }

        /// <summary>
        /// Writes [start, end) of <paramref name="source"/> to <paramref name="destination"/>. Re-encodes (output-side
        /// seek) so the first frame is the one at <paramref name="start"/>, not the keyframe before it.
        /// </summary>
        private void ExtractRange(string ffmpeg, string source, double start, double end, string destination)
        {
            var ss = start.ToString("0.###", CultureInfo.InvariantCulture);
            var t = (end - start).ToString("0.###", CultureInfo.InvariantCulture);
            RunFFmpeg(ffmpeg,
                $"-y -i \"{source}\" -ss {ss} -t {t} -c:v libx264 -crf {Crf} -pix_fmt yuv420p -c:a aac -b:a 192k \"{destination}\"");
            if (!File.Exists(destination))
                throw new InvalidOperationException($"FFmpeg did not write {Path.GetFileName(destination)}");
        }

        /// <summary>Joins pieces cut by <see cref="ExtractRange"/>; they share one encoding, so the streams copy as-is.</summary>
        private void JoinPieces(string ffmpeg, string destination, params string[] pieces)
        {
            var listFile = Path.Combine(Path.GetTempPath(), $"h3ve_join_{Guid.NewGuid():N}.txt");
            try
            {
                // FFmpeg concat demuxer requires forward slashes and escaped quotes
                File.WriteAllLines(listFile, pieces.Select(p => $"file '{p.Replace("\\", "/").Replace("'", @"'\''")}'"));
                RunFFmpeg(ffmpeg, $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{destination}\"");
            }
            finally
            {
                TryDelete(listFile);
            }
            if (!File.Exists(destination))
                throw new InvalidOperationException($"FFmpeg did not write {Path.GetFileName(destination)}");
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* scratch file; harmless if left behind */ }
        }

        private async Task<string> ThumbnailForAsync(string ffmpeg, string video)
        {
            var png = Path.ChangeExtension(video, null) + "_thumb.png";
            try
            {
                await Task.Run(() => RunFFmpeg(ffmpeg, $"-y -i \"{video}\" -frames:v 1 \"{png}\"", 60000));
                return File.Exists(png) ? png : string.Empty;
            }
            catch
            {
                return string.Empty; // a missing thumbnail is cosmetic
            }
        }
    }
}
