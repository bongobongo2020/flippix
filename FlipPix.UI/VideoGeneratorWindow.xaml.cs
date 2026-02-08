using System;
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
            Loaded += OnLoaded;

            // Wire up video player controls
            _viewModel.PlayRequested += OnPlayRequested;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Ensure window is on screen and fully visible
            EnsureWindowVisible();
        }

        private void EnsureWindowVisible()
        {
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            // If window is off-screen, reposition it
            if (Left < 0 || Top < 0 || Left + Width > screenWidth || Top + Height > screenHeight)
            {
                Left = (screenWidth - Width) / 2;
                Top = (screenHeight - Height) / 2;
            }
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

        protected override void OnClosed(EventArgs e)
        {
            if (VideoPlayer != null)
            {
                VideoPlayer.Stop();
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
