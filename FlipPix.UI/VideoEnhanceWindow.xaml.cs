using System;
using System.Windows;
using System.Windows.Input;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.UI
{
    public partial class VideoEnhanceWindow : Window
    {
        private readonly VideoEnhanceViewModel _viewModel;
        private readonly WindowPositionService _windowPositionService;

        public VideoEnhanceWindow(VideoEnhanceViewModel viewModel, WindowPositionService windowPositionService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;
            _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
            Loaded += OnLoaded;
            _viewModel.PlayRequested += OnPlayRequested;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _windowPositionService.EnsureWindowVisible(this);
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void OnPlayRequested(object? sender, EventArgs e)
        {
            if (VideoPlayer != null && VideoPlayer.Source != null)
            {
                VideoPlayer.Position = TimeSpan.Zero;
                VideoPlayer.Play();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            VideoPlayer?.Stop();
            _viewModel.PlayRequested -= OnPlayRequested;
            if (_viewModel is IDisposable disposable)
                disposable.Dispose();
            DataContext = null;
            base.OnClosed(e);
        }
    }
}
