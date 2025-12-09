using System.Windows;
using System.Windows.Input;
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
        }

        protected override void OnClosed(System.EventArgs e)
        {
            if (VideoPlayer != null)
            {
                VideoPlayer.Stop();
            }
            _viewModel.PlayRequested -= OnPlayRequested;
            base.OnClosed(e);
        }
    }
}
