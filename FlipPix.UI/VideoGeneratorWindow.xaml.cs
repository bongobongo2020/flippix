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
        private DispatcherTimer? _scrubTimerVace;
        private DispatcherTimer? _scrubTimerCharReplace;

        public VideoGeneratorWindow(VideoGeneratorViewModel viewModel, WindowPositionService windowPositionService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;
            _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
            Loaded += OnLoaded;

            _viewModel.PlayRequested += OnPlayRequested;
            _viewModel.WanScailGgufVM.SeekRequested += OnWanScailGgufSeekRequested;
            _viewModel.VaceVM.SeekRequested += OnVaceSeekRequested;
            _viewModel.WanCharReplaceVM.SeekRequested += OnWanCharReplaceSeekRequested;
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

            if (VACEVideoPlayer != null && VACEVideoPlayer.Source != null)
            {
                VACEVideoPlayer.Position = System.TimeSpan.Zero;
                VACEVideoPlayer.Play();
            }

            if (WanCharReplaceVideoPlayer != null && WanCharReplaceVideoPlayer.Source != null)
            {
                WanCharReplaceVideoPlayer.Position = System.TimeSpan.Zero;
                WanCharReplaceVideoPlayer.Play();
            }

            if (LtxControlVideoPlayer != null && LtxControlVideoPlayer.Source != null)
            {
                LtxControlVideoPlayer.Position = System.TimeSpan.Zero;
                LtxControlVideoPlayer.Play();
            }
        }

        // ── MediaOpened: enter Paused state so ScrubbingEnabled can render frames ──

        private void WanScailGgufRefPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            WanScailGgufRefVideoPlayer.Play();
            WanScailGgufRefVideoPlayer.Pause();
        }

        private void VaceRefPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            VaceRefVideoPlayer.Play();
            VaceRefVideoPlayer.Pause();
        }

        private void WanCharReplaceRefPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            WanCharReplaceRefVideoPlayer.Play();
            WanCharReplaceRefVideoPlayer.Pause();
        }

        private void LtxControlRefPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            LtxControlRefVideoPlayer.Play();
            LtxControlRefVideoPlayer.Pause();
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

        private void OnVaceSeekRequested(object? sender, System.TimeSpan startPos)
        {
            var player = VaceRefVideoPlayer;
            if (player?.Source == null) return;

            _scrubTimerVace?.Stop();

            var vm = _viewModel.VaceVM;
            var chunk = vm.ChunkItems.FirstOrDefault(c => c.IsSelected);
            const double fps = 24.0;
            var endPos = chunk != null
                ? TimeSpan.FromSeconds(chunk.EndFrame / fps)
                : startPos + TimeSpan.FromSeconds(4);
            var midPos = TimeSpan.FromTicks((startPos.Ticks + endPos.Ticks) / 2);

            player.Position = startPos;

            var step = 0;
            _scrubTimerVace = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _scrubTimerVace.Tick += (s, e) =>
            {
                step++;
                if (step == 1) player.Position = midPos;
                else { player.Position = endPos; _scrubTimerVace!.Stop(); }
            };
            _scrubTimerVace.Start();
        }

        private void OnWanCharReplaceSeekRequested(object? sender, System.TimeSpan startPos)
        {
            var player = WanCharReplaceRefVideoPlayer;
            if (player?.Source == null) return;

            _scrubTimerCharReplace?.Stop();

            var vm = _viewModel.WanCharReplaceVM;
            var fps = vm.Fps > 0 ? vm.Fps : 16.0;
            var chunk = vm.ChunkItems.FirstOrDefault(c => c.IsSelected);
            var endPos = chunk != null
                ? TimeSpan.FromSeconds(chunk.EndFrame / fps)
                : startPos + TimeSpan.FromSeconds(4);
            var midPos = TimeSpan.FromTicks((startPos.Ticks + endPos.Ticks) / 2);

            player.Position = startPos;

            var step = 0;
            _scrubTimerCharReplace = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _scrubTimerCharReplace.Tick += (s, e) =>
            {
                step++;
                if (step == 1) player.Position = midPos;
                else { player.Position = endPos; _scrubTimerCharReplace!.Stop(); }
            };
            _scrubTimerCharReplace.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            _scrubTimerGguf?.Stop();
            _scrubTimerVace?.Stop();
            _scrubTimerCharReplace?.Stop();

            VideoPlayer?.Stop();
            LongVideoPlayer?.Stop();
            WanScailGgufVideoPlayer?.Stop();
            WanScailGgufRefVideoPlayer?.Stop();
            VaceRefVideoPlayer?.Stop();
            VACEVideoPlayer?.Stop();
            WanCharReplaceRefVideoPlayer?.Stop();
            WanCharReplaceVideoPlayer?.Stop();
            LtxControlRefVideoPlayer?.Stop();
            LtxControlVideoPlayer?.Stop();

            _viewModel.WanScailGgufVM.SeekRequested -= OnWanScailGgufSeekRequested;
            _viewModel.VaceVM.SeekRequested -= OnVaceSeekRequested;
            _viewModel.WanCharReplaceVM.SeekRequested -= OnWanCharReplaceSeekRequested;
            _viewModel.PlayRequested -= OnPlayRequested;

            if (_viewModel is IDisposable disposable)
                disposable.Dispose();

            DataContext = null;
            base.OnClosed(e);
        }
    }
}
