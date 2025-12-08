using System.Windows;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI
{
    public partial class VideoGeneratorWindow : Window
    {
        private readonly VideoGeneratorViewModel _viewModel;

        public VideoGeneratorWindow(VideoGeneratorViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;

            // Wire up video player controls
            _viewModel.PlayRequested += OnPlayRequested;
            _viewModel.StopRequested += OnStopRequested;
        }

        private void OnPlayRequested(object? sender, System.EventArgs e)
        {
            if (VideoPlayer != null && VideoPlayer.Source != null)
            {
                VideoPlayer.Position = System.TimeSpan.Zero;
                VideoPlayer.Play();
            }
        }

        private void OnStopRequested(object? sender, System.EventArgs e)
        {
            if (VideoPlayer != null)
            {
                VideoPlayer.Stop();
            }
        }

        protected override void OnClosed(System.EventArgs e)
        {
            if (VideoPlayer != null)
            {
                VideoPlayer.Stop();
            }
            _viewModel.PlayRequested -= OnPlayRequested;
            _viewModel.StopRequested -= OnStopRequested;
            base.OnClosed(e);
        }
    }
}
