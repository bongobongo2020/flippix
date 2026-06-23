using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI
{
    public partial class VideoGeneratorWindow : Window
    {
        private readonly VideoGeneratorViewModel _viewModel;
        private readonly WindowPositionService _windowPositionService;

        // This window is a reused singleton: closing it hides it (so reopening is instant) rather
        // than destroying it. Only a real application shutdown sets this true to allow a true close.
        private bool _allowClose;

        private DispatcherTimer? _scrubTimerScail2;

        // Scail 2 playhead tracking (trim-track playhead ↔ media element)
        private DispatcherTimer? _scail2PosTimer;
        private bool _scail2IsPlaying;

        public VideoGeneratorWindow(VideoGeneratorViewModel viewModel, WindowPositionService windowPositionService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;
            _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
            Loaded += OnLoaded;

            // App shuts down when the main window closes (ShutdownMode.OnMainWindowClose). Let this
            // reused window actually close at that point instead of cancelling into Hide() forever.
            if (System.Windows.Application.Current?.MainWindow is System.Windows.Window main && !ReferenceEquals(main, this))
            {
                main.Closed += (_, _) =>
                {
                    _allowClose = true;
                    Close();
                };
            }

            _viewModel.PlayRequested += OnPlayRequested;
            _viewModel.Scail2VM.SeekRequested += OnScail2SeekRequested;
            _viewModel.Scail2VM.PropertyChanged += Scail2VM_PropertyChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _windowPositionService.EnsureWindowVisible(this);
            // Pick up a video that was already loaded before this window's handlers wired up.
            ApplyScail2RefSource();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                this.DragMove();
        }

        private void OnPlayRequested(object? sender, System.EventArgs e)
        {
            if (VideoPlayer != null && VideoPlayer.Source != null)
            {
                VideoPlayer.Position = System.TimeSpan.Zero;
                VideoPlayer.Play();
            }

            if (LongVideoPlayer != null && LongVideoPlayer.Source != null)
            {
                LongVideoPlayer.Position = System.TimeSpan.Zero;
                LongVideoPlayer.Play();
            }

            if (LtxControlVideoPlayer != null && LtxControlVideoPlayer.Source != null)
            {
                LtxControlVideoPlayer.Position = System.TimeSpan.Zero;
                LtxControlVideoPlayer.Play();
            }

            if (Vr180VideoPlayer != null && Vr180VideoPlayer.Source != null)
            {
                Vr180VideoPlayer.Position = System.TimeSpan.Zero;
                Vr180VideoPlayer.Play();
            }

            if (VideoSoundVideoPlayer != null && VideoSoundVideoPlayer.Source != null)
            {
                VideoSoundVideoPlayer.Position = System.TimeSpan.Zero;
                VideoSoundVideoPlayer.Play();
            }

            if (SeedDirectorPlayer != null && SeedDirectorPlayer.Source != null)
            {
                SeedDirectorPlayer.Position = System.TimeSpan.Zero;
                SeedDirectorPlayer.Play();
            }

            if (Scail2VideoPlayer != null && Scail2VideoPlayer.Source != null)
            {
                Scail2VideoPlayer.Position = System.TimeSpan.Zero;
                Scail2VideoPlayer.Play();
            }
        }

        // ── LTX Director: drag-drop images onto the timeline ─────────────────

        private void LtxDirectorTimeline_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                ? System.Windows.DragDropEffects.Copy
                : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void LtxDirectorTimeline_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                _viewModel.LtxDirectorVM.AddImagesFromPaths(paths);
            e.Handled = true;
        }

        // ── Seed Director: drag-drop images onto the timeline ────────────────

        private void SeedDirectorTimeline_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                ? System.Windows.DragDropEffects.Copy
                : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void SeedDirectorTimeline_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                _viewModel.SeedDirectorVM.AddImagesFromPaths(paths);
            e.Handled = true;
        }

        // Never seek a scrub preview to the exact end of the clip. Landing on the final
        // frame fires MediaElement.MediaEnded, which rewinds/resets the player (the
        // "shrink and expand" flicker) and leaves it in an ended state where
        // ScrubbingEnabled stops rendering new frames — so the Out marker shows no preview
        // and the In marker breaks too. Hold a couple of frames back from the end.
        private static double ClampPreviewSeek(System.Windows.Controls.MediaElement p, double seconds, double fps)
        {
            var t = Math.Max(0, seconds);
            if (p.NaturalDuration.HasTimeSpan)
            {
                double dur = p.NaturalDuration.TimeSpan.TotalSeconds;
                double guard = Math.Max(0.05, 2.0 / (fps > 0 ? fps : 24.0));
                if (dur > guard) t = Math.Min(t, dur - guard);
            }
            return t;
        }

        // ── SCAIL II draggable in/out trim markers ───────────────────────────
        // The green/red thumbs write straight to the view model's TrimInSeconds /
        // TrimOutSeconds (which clamp to [0, out] / [in, duration]), so a dropped
        // marker stays put. The purple region between them shows the kept clip,
        // and TrimmedFrames (in/out length in frames) is what the workflow loads.
        private const double ScailTrimThumbWidth = 14.0;

        // WAN processes the clip in 81-frame chunks; the timeline marks each boundary.
        private const int ScailChunkFrames = 81;

        private void LtxControlRefPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            LtxControlRefVideoPlayer.Play();
            LtxControlRefVideoPlayer.Pause();
        }

        private void Vr180InputPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            Vr180InputPlayer.Play();
            Vr180InputPlayer.Pause();
        }

        private void VideoSoundInputPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            VideoSoundInputPlayer.Play();
            VideoSoundInputPlayer.Pause();
            // These clips often fade in from black, so frame 0 renders as a black thumbnail.
            // Scrub a little way in (ScrubbingEnabled) to show an actual frame.
            VideoSoundInputPlayer.Position = System.TimeSpan.FromMilliseconds(250);

            // Match the output aspect ratio to the uploaded clip automatically.
            var w = VideoSoundInputPlayer.NaturalVideoWidth;
            var h = VideoSoundInputPlayer.NaturalVideoHeight;
            if (w > 0 && h > 0)
                _viewModel.VideoSoundVM.SetOutputAspectFromVideo(w, h);
        }

        // ── Seed Director shared player playback (auto-loop) ─────────────────

        private void SeedDirectorPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            SeedDirectorPlayer.Play();
        }

        private void SeedDirectorPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            SeedDirectorPlayer.Position = System.TimeSpan.FromMilliseconds(1);
            SeedDirectorPlayer.Play();
        }

        private void SeedDirectorReplay_Click(object sender, RoutedEventArgs e)
        {
            if (SeedDirectorPlayer.Source == null) return;
            SeedDirectorPlayer.Position = System.TimeSpan.Zero;
            SeedDirectorPlayer.Play();
        }

        private void FflfDasiwaPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            FflfDasiwaPlayer.Play();
        }

        private void FflfDasiwaPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            FflfDasiwaPlayer.Position = System.TimeSpan.FromMilliseconds(1);
            FflfDasiwaPlayer.Play();
        }

        private void FflfDasiwaReplay_Click(object sender, RoutedEventArgs e)
        {
            if (FflfDasiwaPlayer.Source == null) return;
            FflfDasiwaPlayer.Position = System.TimeSpan.Zero;
            FflfDasiwaPlayer.Play();
        }

        private void FflfSeedHuntPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            FflfSeedHuntPlayer.Play();
        }

        private void FflfSeedHuntPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            FflfSeedHuntPlayer.Position = System.TimeSpan.FromMilliseconds(1);
            FflfSeedHuntPlayer.Play();
        }

        private void FflfSeedHuntPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _viewModel.FflfSeedHuntVM.ReportPreviewFailed(e.ErrorException?.Message ?? "unknown media error");
        }

        // ──────────────────────────────────────────────────────────────────────
        // Scail 2 — same reference-player + trim-marker machinery as WAN SCAIL II,
        // bound to _viewModel.Scail2VM. The trim track doubles as: (a) the scrub used
        // to pick the Klein char-swap frame, and (b) the In/Out range for SCAIL II.
        // ──────────────────────────────────────────────────────────────────────

        private void ApplyScail2RefSource()
        {
            var p = Scail2RefVideoPlayer;
            if (p == null) return;
            var path = _viewModel.Scail2VM.VideoFileUri;
            var target = string.IsNullOrEmpty(path) ? null : new Uri(path, UriKind.RelativeOrAbsolute);
            if (string.Equals(p.Source?.OriginalString, target?.OriginalString, StringComparison.OrdinalIgnoreCase))
                return;
            p.Source = target;
        }

        private void Scail2RefPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            Scail2RefVideoPlayer.Play();
            Scail2RefVideoPlayer.Pause();

            if (_scail2PosTimer == null)
            {
                _scail2PosTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _scail2PosTimer.Tick += (_, _) =>
                {
                    var p = Scail2RefVideoPlayer;
                    if (p?.Source == null) return;
                    if (!_scail2IsPlaying) return;
                    if (p.NaturalDuration.HasTimeSpan)
                        _viewModel.Scail2VM.PlaybackPositionSeconds = p.Position.TotalSeconds;
                };
            }
            _scail2PosTimer.Start();
        }

        private void Scail2Play_Click(object sender, RoutedEventArgs e)
        {
            Scail2RefVideoPlayer?.Play();
            _scail2IsPlaying = Scail2RefVideoPlayer?.Source != null;
        }

        private void Scail2Pause_Click(object sender, RoutedEventArgs e)
        {
            Scail2RefVideoPlayer?.Pause();
            _scail2IsPlaying = false;
            // Pausing settles on a deliberate frame → that's the Klein base frame.
            _viewModel.Scail2VM.NotifyScrubbed();
        }

        private void Scail2RefPlayer_MediaEnded(object sender, RoutedEventArgs e)
            => _scail2IsPlaying = false;

        private void SeekScail2RefTo(double seconds)
        {
            var p = Scail2RefVideoPlayer;
            if (p?.Source == null) return;
            p.Pause();
            _scail2IsPlaying = false;
            var t = ClampPreviewSeek(p, seconds, _viewModel.Scail2VM.Fps);
            p.Position = TimeSpan.FromSeconds(t);
            _viewModel.Scail2VM.PlaybackPositionSeconds = t;
            // Moving an in/out marker seeks the preview to that frame — count it as a deliberate scrub.
            _viewModel.Scail2VM.NotifyScrubbed();
        }

        private double Scail2TrimTrackWidth =>
            Scail2TrimTrack != null && Scail2TrimTrack.ActualWidth > 1 ? Scail2TrimTrack.ActualWidth : 0;

        private double Scail2TrimSecToX(double seconds, double duration)
        {
            var w = Scail2TrimTrackWidth;
            if (duration <= 0 || w <= 0) return 0;
            return Math.Max(0, Math.Min(w, seconds / duration * w));
        }

        private void Scail2VM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModels.Video.WanScailViewModel.VideoFileUri):
                    if (Dispatcher.CheckAccess()) ApplyScail2RefSource();
                    else Dispatcher.Invoke(ApplyScail2RefSource);
                    break;
                case nameof(ViewModels.Video.WanScailViewModel.TrimInSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.TrimOutSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.VideoDurationSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.PlaybackPositionSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.Fps):
                case nameof(ViewModels.Video.WanScailViewModel.TotalFrames):
                    if (Dispatcher.CheckAccess()) UpdateScail2TrimMarkers();
                    else Dispatcher.Invoke(UpdateScail2TrimMarkers);
                    break;
            }
        }

        private void Scail2TrimTrack_SizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateScail2TrimMarkers();

        private readonly System.Collections.Generic.List<UIElement> _scail2Ticks = new();
        private string _scail2TickSig = "";

        private void RebuildScail2Ticks()
        {
            if (Scail2TrimTrack == null) return;
            var vm = _viewModel.Scail2VM;
            double fps = vm.Fps > 0 ? vm.Fps : 24.0;
            double dur = vm.VideoDurationSeconds;
            double w = Scail2TrimTrackWidth;
            int total = vm.TotalFrames;

            string sig = $"{fps:F3}|{dur:F3}|{w:F1}|{total}";
            if (sig == _scail2TickSig) return;
            _scail2TickSig = sig;

            foreach (var t in _scail2Ticks) Scail2TrimTrack.Children.Remove(t);
            _scail2Ticks.Clear();

            if (dur <= 0 || w <= 0 || fps <= 0) return;
            int totalFrames = total > 0 ? total : (int)Math.Round(dur * fps);

            for (int frame = ScailChunkFrames; frame < totalFrames; frame += ScailChunkFrames)
            {
                double x = Scail2TrimSecToX(frame / fps, dur);
                var tick = new System.Windows.Controls.Border
                {
                    Width = 1.5,
                    Height = 18,
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x4B, 0x55, 0x63)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    Margin = new Thickness(x - 0.75, 0, 0, 0)
                };
                _scail2Ticks.Add(tick);
                Scail2TrimTrack.Children.Insert(1, tick);
            }
        }

        private void UpdateScail2TrimMarkers()
        {
            if (Scail2InThumb == null) return; // not yet templated
            RebuildScail2Ticks();
            var vm = _viewModel.Scail2VM;
            double dur = vm.VideoDurationSeconds;
            double w = Scail2TrimTrackWidth;
            if (dur <= 0 || w <= 0) return;

            double outSec = vm.TrimOutSeconds > 0 ? vm.TrimOutSeconds : dur;
            double inX = Scail2TrimSecToX(vm.TrimInSeconds, dur);
            double outX = Scail2TrimSecToX(outSec, dur);
            double playX = Scail2TrimSecToX(vm.PlaybackPositionSeconds, dur);

            Scail2InThumb.Margin = new Thickness(inX - ScailTrimThumbWidth / 2, 0, 0, 0);
            Scail2OutThumb.Margin = new Thickness(outX - ScailTrimThumbWidth / 2, 0, 0, 0);
            Scail2TrimRegion.Margin = new Thickness(inX, 0, 0, 0);
            Scail2TrimRegion.Width = Math.Max(0, outX - inX);
            Scail2TrimPlayhead.Margin = new Thickness(Math.Max(0, playX - 1), 0, 0, 0);
        }

        private void Scail2InThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            var vm = _viewModel.Scail2VM;
            double dur = vm.VideoDurationSeconds;
            double w = Scail2TrimTrackWidth;
            if (dur <= 0 || w <= 0) return;
            vm.TrimInSeconds += e.HorizontalChange / w * dur;
            UpdateScail2TrimMarkers();
            SeekScail2RefTo(vm.TrimInSeconds);
        }

        private void Scail2OutThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            var vm = _viewModel.Scail2VM;
            double dur = vm.VideoDurationSeconds;
            double w = Scail2TrimTrackWidth;
            if (dur <= 0 || w <= 0) return;
            double cur = vm.TrimOutSeconds > 0 ? vm.TrimOutSeconds : dur;
            vm.TrimOutSeconds = cur + e.HorizontalChange / w * dur;
            UpdateScail2TrimMarkers();
            SeekScail2RefTo(vm.TrimOutSeconds);
        }

        // Generation is explicit: the user presses "Generate video" once the In/Out range is set.
        private void Scail2Process_Click(object sender, RoutedEventArgs e)
            => _ = _viewModel.Scail2VM.OnTrimFinalizedAsync();

        private void OnScail2SeekRequested(object? sender, System.TimeSpan startPos)
        {
            var player = Scail2RefVideoPlayer;
            if (player?.Source == null) return;

            _scrubTimerScail2?.Stop();

            var vm = _viewModel.Scail2VM;
            var fps = vm.Fps > 0 ? vm.Fps : 24.0;
            var chunk = vm.ChunkItems.FirstOrDefault(c => c.IsSelected);
            var endPos = chunk != null
                ? TimeSpan.FromSeconds(chunk.EndFrame / fps)
                : startPos + TimeSpan.FromSeconds(4);
            var midPos = TimeSpan.FromTicks((startPos.Ticks + endPos.Ticks) / 2);

            player.Position = startPos;

            var step = 0;
            _scrubTimerScail2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _scrubTimerScail2.Tick += (s, e) =>
            {
                step++;
                if (step == 1) player.Position = midPos;
                else { player.Position = endPos; _scrubTimerScail2!.Stop(); }
            };
            _scrubTimerScail2.Start();
        }

        // Stops every media element so the window releases its hold on the underlying video files
        // (important when only hiding, so the files aren't left locked while the window lingers).
        private void StopAllPlayers()
        {
            _scrubTimerScail2?.Stop();

            VideoPlayer?.Stop();
            LongVideoPlayer?.Stop();
            LtxControlRefVideoPlayer?.Stop();
            LtxControlVideoPlayer?.Stop();
            Vr180InputPlayer?.Stop();
            Vr180VideoPlayer?.Stop();
            VideoSoundInputPlayer?.Stop();
            VideoSoundVideoPlayer?.Stop();
            SeedDirectorPlayer?.Stop();
            FflfDasiwaPlayer?.Stop();
            FflfSeedHuntPlayer?.Stop();
            Scail2RefVideoPlayer?.Stop();
            Scail2VideoPlayer?.Stop();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Reused singleton: a user-initiated close just hides the window so the next open is
            // instant. Stop playback first to release video file handles. Only a true app shutdown
            // (_allowClose) falls through to a real close.
            if (!_allowClose)
            {
                e.Cancel = true;
                StopAllPlayers();
                Hide();
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            StopAllPlayers();

            _viewModel.Scail2VM.SeekRequested -= OnScail2SeekRequested;
            _viewModel.Scail2VM.PropertyChanged -= Scail2VM_PropertyChanged;
            _viewModel.PlayRequested -= OnPlayRequested;

            if (_viewModel is IDisposable disposable)
                disposable.Dispose();

            DataContext = null;
            base.OnClosed(e);
        }
    }
}
