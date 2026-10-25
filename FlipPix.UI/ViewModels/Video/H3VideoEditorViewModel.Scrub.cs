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
        }

        partial void OnPlayheadSecondsChanged(double value) => NotifyEditCommands();
        partial void OnPreviewDurationSecondsChanged(double value) => NotifyEditCommands();
        partial void OnIsEditingClipChanged(bool value) => NotifyEditCommands();

        partial void OnPreviewVideoPathChanged(string value)
        {
            PlayheadSeconds = 0;
            PreviewDurationSeconds = 0; // set again by the window once the new file opens
        }

        private void NotifyEditCommands()
        {
            SplitAtPlayheadCommand?.NotifyCanExecuteChanged();
            TrimStartToPlayheadCommand?.NotifyCanExecuteChanged();
            TrimEndToPlayheadCommand?.NotifyCanExecuteChanged();
        }

        /// <summary>Rounds to the nearest frame boundary.</summary>
        public static double SnapToFrame(double seconds) => Math.Round(seconds * PreviewFps) / PreviewFps;

        private static string Timecode(double seconds)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
        }

        private enum ClipEdit { Split, TrimStart, TrimEnd }

        private async Task EditAtPlayheadAsync(ClipEdit edit)
        {
            var clip = PreviewedClip;
            if (clip == null || !CanEditAtPlayhead) return;

            var source = clip.OutputPath;
            var duration = PreviewDurationSeconds;
            var cut = SnapToFrame(PlayheadSeconds);
            if (cut < MinPieceSeconds || duration - cut < MinPieceSeconds)
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
