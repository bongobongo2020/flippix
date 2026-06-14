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

        // SCAIL II playhead tracking (trim-track playhead ↔ media element)
        private DispatcherTimer? _scailGgufPosTimer;
        private bool _scailGgufIsPlaying;

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
            // Pick up a video that was already loaded before this window's handlers wired up.
            ApplyWanScailGgufRefSource();
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

        // Assign the reference player's Source from code-behind (it is intentionally
        // NOT bound in XAML). The parent VM re-fires OnPropertyChanged(string.Empty) on
        // every sub-VM change, so a Source binding would be re-evaluated — and re-setting
        // MediaElement.Source reloads the clip back to frame 0 — on every trim-marker drag.
        // Assigning here only when the path actually changes keeps the scrubbed frame stable.
        private void ApplyWanScailGgufRefSource()
        {
            var p = WanScailGgufRefVideoPlayer;
            if (p == null) return;
            var path = _viewModel.WanScailGgufVM.VideoFileUri;
            var target = string.IsNullOrEmpty(path) ? null : new Uri(path, UriKind.RelativeOrAbsolute);
            if (string.Equals(p.Source?.OriginalString, target?.OriginalString, StringComparison.OrdinalIgnoreCase))
                return;
            p.Source = target;
        }

        // ── MediaOpened: enter Paused state so ScrubbingEnabled can render frames ──

        private void WanScailGgufRefPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            WanScailGgufRefVideoPlayer.Play();
            WanScailGgufRefVideoPlayer.Pause();

            // Drive the trim-track playhead from the player position while playing.
            if (_scailGgufPosTimer == null)
            {
                _scailGgufPosTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _scailGgufPosTimer.Tick += (_, _) =>
                {
                    var p = WanScailGgufRefVideoPlayer;
                    if (p?.Source == null) return;
                    // Only follow the player while it is actually playing. A paused
                    // MediaElement reports Position == 0, which would otherwise snap
                    // the playhead back to the start every tick.
                    if (!_scailGgufIsPlaying) return;
                    if (p.NaturalDuration.HasTimeSpan)
                        _viewModel.WanScailGgufVM.PlaybackPositionSeconds = p.Position.TotalSeconds;
                };
            }
            _scailGgufPosTimer.Start();
        }

        // ── SCAIL II scrub / play / mark in-out ──────────────────────────────
        private void WanScailGgufPlay_Click(object sender, RoutedEventArgs e)
        {
            WanScailGgufRefVideoPlayer?.Play();
            _scailGgufIsPlaying = WanScailGgufRefVideoPlayer?.Source != null;
        }

        private void WanScailGgufPause_Click(object sender, RoutedEventArgs e)
        {
            WanScailGgufRefVideoPlayer?.Pause();
            _scailGgufIsPlaying = false;
        }

        private void WanScailGgufRefPlayer_MediaEnded(object sender, RoutedEventArgs e)
            => _scailGgufIsPlaying = false;

        // Seek the reference preview to a position (seconds) and keep the trim-track
        // playhead in sync. Used while dragging the in/out markers so the preview
        // frame updates live. ScrubbingEnabled renders the frame even while paused.
        private void SeekWanScailGgufRefTo(double seconds)
        {
            var p = WanScailGgufRefVideoPlayer;
            if (p?.Source == null) return;
            p.Pause();
            _scailGgufIsPlaying = false;
            var t = Math.Max(0, seconds);
            p.Position = TimeSpan.FromSeconds(t);
            _viewModel.WanScailGgufVM.PlaybackPositionSeconds = t;
        }

        // ── SCAIL II draggable in/out trim markers ───────────────────────────
        // The green/red thumbs write straight to the view model's TrimInSeconds /
        // TrimOutSeconds (which clamp to [0, out] / [in, duration]), so a dropped
        // marker stays put. The purple region between them shows the kept clip,
        // and TrimmedFrames (in/out length in frames) is what the workflow loads.
        private const double ScailTrimThumbWidth = 14.0;

        // WAN processes the clip in 81-frame chunks; the timeline marks each boundary.
        private const int ScailChunkFrames = 81;

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
                case nameof(ViewModels.Video.WanScailViewModel.VideoFileUri):
                    if (Dispatcher.CheckAccess()) ApplyWanScailGgufRefSource();
                    else Dispatcher.Invoke(ApplyWanScailGgufRefSource);
                    break;
                case nameof(ViewModels.Video.WanScailViewModel.TrimInSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.TrimOutSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.VideoDurationSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.PlaybackPositionSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.Fps):
                case nameof(ViewModels.Video.WanScailViewModel.TotalFrames):
                    if (Dispatcher.CheckAccess()) UpdateWanScailGgufTrimMarkers();
                    else Dispatcher.Invoke(UpdateWanScailGgufTrimMarkers);
                    break;
            }
        }

        private void WanScailGgufTrimTrack_SizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateWanScailGgufTrimMarkers();

        // 81-frame boundary tick marks drawn under the trim handles.
        private readonly System.Collections.Generic.List<UIElement> _scailGgufTicks = new();
        private string _scailGgufTickSig = "";

        private void RebuildWanScailGgufTicks()
        {
            if (WanScailGgufTrimTrack == null) return;
            var vm = _viewModel.WanScailGgufVM;
            double fps = vm.Fps > 0 ? vm.Fps : 24.0;
            double dur = vm.VideoDurationSeconds;
            double w = ScailTrimTrackWidth;
            int total = vm.TotalFrames;

            // Only rebuild when something that affects tick placement actually changed.
            string sig = $"{fps:F3}|{dur:F3}|{w:F1}|{total}";
            if (sig == _scailGgufTickSig) return;
            _scailGgufTickSig = sig;

            foreach (var t in _scailGgufTicks) WanScailGgufTrimTrack.Children.Remove(t);
            _scailGgufTicks.Clear();

            if (dur <= 0 || w <= 0 || fps <= 0) return;
            int totalFrames = total > 0 ? total : (int)Math.Round(dur * fps);

            for (int frame = ScailChunkFrames; frame < totalFrames; frame += ScailChunkFrames)
            {
                double x = ScailTrimSecToX(frame / fps, dur);
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
                _scailGgufTicks.Add(tick);
                // Insert just above the base rail but below the region/playhead/thumbs.
                WanScailGgufTrimTrack.Children.Insert(1, tick);
            }
        }

        private void UpdateWanScailGgufTrimMarkers()
        {
            if (WanScailGgufInThumb == null) return; // not yet templated
            RebuildWanScailGgufTicks();
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
            SeekWanScailGgufRefTo(vm.TrimInSeconds);
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
            SeekWanScailGgufRefTo(vm.TrimOutSeconds);
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
