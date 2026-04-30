using System;
using System.Windows;
using System.Windows.Input;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI
{
    public partial class VideoGeneratorWindow : Window
    {
        private readonly VideoGeneratorViewModel _viewModel;
        private readonly WindowPositionService _windowPositionService;

        public VideoGeneratorWindow(VideoGeneratorViewModel viewModel, WindowPositionService windowPositionService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;
            _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
            Loaded += OnLoaded;

            // Wire up video player controls
            _viewModel.PlayRequested += OnPlayRequested;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Ensure window is on screen and fully visible
            _windowPositionService.EnsureWindowVisible(this);
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
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
        }

        protected override void OnClosed(EventArgs e)
        {
            if (VideoPlayer != null)
            {
                VideoPlayer.Stop();
            }
            if (LongVideoPlayer != null)
            {
                LongVideoPlayer.Stop();
            }
            if (WanAnimateVideoPlayer != null)
            {
                WanAnimateVideoPlayer.Stop();
            }
            _viewModel.PlayRequested -= OnPlayRequested;

            // Dispose the ViewModel if it implements IDisposable
            if (_viewModel is IDisposable disposable)
            {
                disposable.Dispose();
            }

            DataContext = null;
            base.OnClosed(e);
        }
    }
}
