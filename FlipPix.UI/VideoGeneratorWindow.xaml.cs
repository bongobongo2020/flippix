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

        private DispatcherTimer? _scrubTimerGguf;

        // SCAIL II scrub-bar position tracking (slider ↔ media element)
        private DispatcherTimer? _scailGgufPosTimer;
        private bool _scailGgufUserScrubbing;

        public VideoGeneratorWindow(VideoGeneratorViewModel viewModel, WindowPositionService windowPositionService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;
            _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
            Loaded += OnLoaded;

            _viewModel.PlayRequested += OnPlayRequested;
            _viewModel.WanScailGgufVM.SeekRequested += OnWanScailGgufSeekRequested;
            _viewModel.WanScailGgufVM.PropertyChanged += WanScailGgufVM_PropertyChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _windowPositionService.EnsureWindowVisible(this);
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

            if (WanScailGgufVideoPlayer != null && WanScailGgufVideoPlayer.Source != null)
            {
                WanScailGgufVideoPlayer.Position = System.TimeSpan.Zero;
                WanScailGgufVideoPlayer.Play();
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

            if (SeedDirectorPlayer != null && SeedDirectorPlayer.Source != null)
            {
                SeedDirectorPlayer.Position = System.TimeSpan.Zero;
                SeedDirectorPlayer.Play();
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

        // ── MediaOpened: enter Paused state so ScrubbingEnabled can render frames ──

        private void WanScailGgufRefPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            WanScailGgufRefVideoPlayer.Play();
            WanScailGgufRefVideoPlayer.Pause();

            // Drive the scrub slider from the playhead while not being dragged by the user.
            if (_scailGgufPosTimer == null)
            {
                _scailGgufPosTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _scailGgufPosTimer.Tick += (_, _) =>
                {
                    var p = WanScailGgufRefVideoPlayer;
                    if (p?.Source == null || _scailGgufUserScrubbing) return;
                    if (p.NaturalDuration.HasTimeSpan)
                        WanScailGgufScrubSlider.Value = p.Position.TotalSeconds;
                };
            }
            _scailGgufPosTimer.Start();
        }

        // ── SCAIL II scrub / play / mark in-out ──────────────────────────────
        private void WanScailGgufPlay_Click(object sender, RoutedEventArgs e) => WanScailGgufRefVideoPlayer?.Play();
        private void WanScailGgufPause_Click(object sender, RoutedEventArgs e) => WanScailGgufRefVideoPlayer?.Pause();

        private void WanScailGgufScrub_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
            => _scailGgufUserScrubbing = true;

        private void WanScailGgufScrub_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _scailGgufUserScrubbing = false;
            SeekWanScailGgufRefTo(WanScailGgufScrubSlider.Value);
        }

        private void WanScailGgufScrub_Click(object sender, MouseButtonEventArgs e)
            => SeekWanScailGgufRefTo(WanScailGgufScrubSlider.Value);

        private void SeekWanScailGgufRefTo(double seconds)
        {
            var p = WanScailGgufRefVideoPlayer;
            if (p?.Source == null) return;
            p.Pause();
            p.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        // ── SCAIL II draggable in/out trim markers ───────────────────────────
        // The green/red thumbs write straight to the view model's TrimInSeconds /
        // TrimOutSeconds (which clamp to [0, out] / [in, duration]), so a dropped
        // marker stays put. The purple region between them shows the kept clip,
        // and TrimmedFrames (in/out length in frames) is what the workflow loads.
        private const double ScailTrimThumbWidth = 14.0;

        private double ScailTrimTrackWidth =>
            WanScailGgufTrimTrack != null && WanScailGgufTrimTrack.ActualWidth > 1
                ? WanScailGgufTrimTrack.ActualWidth
                : 0;

        private double ScailTrimSecToX(double seconds, double duration)
        {
            var w = ScailTrimTrackWidth;
            if (duration <= 0 || w <= 0) return 0;
            return Math.Max(0, Math.Min(w, seconds / duration * w));
        }

        private void WanScailGgufVM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModels.Video.WanScailViewModel.TrimInSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.TrimOutSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.VideoDurationSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.PlaybackPositionSeconds):
                    if (Dispatcher.CheckAccess()) UpdateWanScailGgufTrimMarkers();
                    else Dispatcher.Invoke(UpdateWanScailGgufTrimMarkers);
                    break;
            }
        }

        private void WanScailGgufTrimTrack_SizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateWanScailGgufTrimMarkers();

        private void UpdateWanScailGgufTrimMarkers()
        {
            if (WanScailGgufInThumb == null) return; // not yet templated
            var vm = _viewModel.WanScailGgufVM;
            double dur = vm.VideoDurationSeconds;
            double w = ScailTrimTrackWidth;
            if (dur <= 0 || w <= 0) return;

            double outSec = vm.TrimOutSeconds > 0 ? vm.TrimOutSeconds : dur;
            double inX = ScailTrimSecToX(vm.TrimInSeconds, dur);
            double outX = ScailTrimSecToX(outSec, dur);
            double playX = ScailTrimSecToX(vm.PlaybackPositionSeconds, dur);

            WanScailGgufInThumb.Margin = new Thickness(inX - ScailTrimThumbWidth / 2, 0, 0, 0);
            WanScailGgufOutThumb.Margin = new Thickness(outX - ScailTrimThumbWidth / 2, 0, 0, 0);
            WanScailGgufTrimRegion.Margin = new Thickness(inX, 0, 0, 0);
            WanScailGgufTrimRegion.Width = Math.Max(0, outX - inX);
            WanScailGgufTrimPlayhead.Margin = new Thickness(Math.Max(0, playX - 1), 0, 0, 0);
        }

        private void WanScailGgufInThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            var vm = _viewModel.WanScailGgufVM;
            double dur = vm.VideoDurationSeconds;
            double w = ScailTrimTrackWidth;
            if (dur <= 0 || w <= 0) return;
            vm.TrimInSeconds += e.HorizontalChange / w * dur;
            UpdateWanScailGgufTrimMarkers();
        }

        private void WanScailGgufOutThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            var vm = _viewModel.WanScailGgufVM;
            double dur = vm.VideoDurationSeconds;
            double w = ScailTrimTrackWidth;
            if (dur <= 0 || w <= 0) return;
            double cur = vm.TrimOutSeconds > 0 ? vm.TrimOutSeconds : dur;
            vm.TrimOutSeconds = cur + e.HorizontalChange / w * dur;
            UpdateWanScailGgufTrimMarkers();
        }

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

        // ── Chunk seek with start → mid → end scrub animation ────────────────

        private void OnWanScailGgufSeekRequested(object? sender, System.TimeSpan startPos)
        {
            var player = WanScailGgufRefVideoPlayer;
            if (player?.Source == null) return;

            _scrubTimerGguf?.Stop();

            var vm = _viewModel.WanScailGgufVM;
            var fps = vm.Fps > 0 ? vm.Fps : 24.0;
            var chunk = vm.ChunkItems.FirstOrDefault(c => c.IsSelected);
            var endPos = chunk != null
                ? TimeSpan.FromSeconds(chunk.EndFrame / fps)
                : startPos + TimeSpan.FromSeconds(4);
            var midPos = TimeSpan.FromTicks((startPos.Ticks + endPos.Ticks) / 2);

            player.Position = startPos;

            var step = 0;
            _scrubTimerGguf = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _scrubTimerGguf.Tick += (s, e) =>
            {
                step++;
                if (step == 1) player.Position = midPos;
                else { player.Position = endPos; _scrubTimerGguf!.Stop(); }
            };
            _scrubTimerGguf.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            _scrubTimerGguf?.Stop();

            VideoPlayer?.Stop();
            LongVideoPlayer?.Stop();
            WanScailGgufVideoPlayer?.Stop();
            WanScailGgufRefVideoPlayer?.Stop();
            LtxControlRefVideoPlayer?.Stop();
            LtxControlVideoPlayer?.Stop();
            Vr180InputPlayer?.Stop();
            Vr180VideoPlayer?.Stop();
            SeedDirectorPlayer?.Stop();

            _viewModel.WanScailGgufVM.SeekRequested -= OnWanScailGgufSeekRequested;
            _viewModel.WanScailGgufVM.PropertyChanged -= WanScailGgufVM_PropertyChanged;
            _viewModel.PlayRequested -= OnPlayRequested;

            if (_viewModel is IDisposable disposable)
                disposable.Dispose();

            DataContext = null;
            base.OnClosed(e);
        }
    }
}
