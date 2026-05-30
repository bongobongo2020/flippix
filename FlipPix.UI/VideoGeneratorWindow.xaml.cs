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

        private DispatcherTimer? _scrubTimerScail;
        private DispatcherTimer? _scrubTimerGguf;

        public VideoGeneratorWindow(VideoGeneratorViewModel viewModel, WindowPositionService windowPositionService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;
            _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
            Loaded += OnLoaded;

            // Wire up video player controls
            _viewModel.PlayRequested += OnPlayRequested;
            _viewModel.WanScailVM.SeekRequested += OnWanScailSeekRequested;
            _viewModel.WanScailGgufVM.SeekRequested += OnWanScailGgufSeekRequested;
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

            if (WanAnimateVideoPlayer != null && WanAnimateVideoPlayer.Source != null)
            {
                WanAnimateVideoPlayer.Position = System.TimeSpan.Zero;
                WanAnimateVideoPlayer.Play();
            }

            if (WanScailVideoPlayer != null && WanScailVideoPlayer.Source != null)
            {
                WanScailVideoPlayer.Position = System.TimeSpan.Zero;
                WanScailVideoPlayer.Play();
            }

            if (WanScailGgufVideoPlayer != null && WanScailGgufVideoPlayer.Source != null)
            {
                WanScailGgufVideoPlayer.Position = System.TimeSpan.Zero;
                WanScailGgufVideoPlayer.Play();
            }
        }

        // ── MediaOpened handlers: enter Paused state so ScrubbingEnabled can render frames ──

        private void WanScailRefPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            WanScailRefVideoPlayer.Play();
            WanScailRefVideoPlayer.Pause();
        }

        private void WanScailGgufRefPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            WanScailGgufRefVideoPlayer.Play();
            WanScailGgufRefVideoPlayer.Pause();
        }

        // ── Chunk seek with start → mid → end scrub animation ────────────────

        private void OnWanScailSeekRequested(object? sender, System.TimeSpan startPos)
        {
            var player = WanScailRefVideoPlayer;
            if (player?.Source == null) return;

            _scrubTimerScail?.Stop();

            var vm = _viewModel.WanScailVM;
            var fps = vm.Fps > 0 ? vm.Fps : 24.0;
            var chunk = vm.ChunkItems.FirstOrDefault(c => c.IsSelected);
            var endPos = chunk != null
                ? TimeSpan.FromSeconds(chunk.EndFrame / fps)
                : startPos + TimeSpan.FromSeconds(4);
            var midPos = TimeSpan.FromTicks((startPos.Ticks + endPos.Ticks) / 2);

            player.Position = startPos;

            var step = 0;
            _scrubTimerScail = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _scrubTimerScail.Tick += (s, e) =>
            {
                step++;
                if (step == 1) player.Position = midPos;
                else { player.Position = endPos; _scrubTimerScail!.Stop(); }
            };
            _scrubTimerScail.Start();
        }

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
            _scrubTimerScail?.Stop();
            _scrubTimerGguf?.Stop();

            VideoPlayer?.Stop();
            LongVideoPlayer?.Stop();
            WanAnimateVideoPlayer?.Stop();
            WanScailVideoPlayer?.Stop();
            WanScailRefVideoPlayer?.Stop();
            WanScailGgufVideoPlayer?.Stop();
            WanScailGgufRefVideoPlayer?.Stop();

            _viewModel.WanScailVM.SeekRequested -= OnWanScailSeekRequested;
            _viewModel.WanScailGgufVM.SeekRequested -= OnWanScailGgufSeekRequested;
            _viewModel.PlayRequested -= OnPlayRequested;

            if (_viewModel is IDisposable disposable)
                disposable.Dispose();

            DataContext = null;
            base.OnClosed(e);
        }
    }
}
