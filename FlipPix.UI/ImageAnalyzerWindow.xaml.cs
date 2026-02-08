using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI
{
    public partial class ImageAnalyzerWindow : Window
    {
        private readonly ImageAnalyzerViewModel _viewModel;

        public ImageAnalyzerWindow(ImageAnalyzerViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;

            // Subscribe to the QueueItemAdded event to trigger flash animation
            _viewModel.QueueItemAdded += OnQueueItemAdded;
        }

        private void OnQueueItemAdded()
        {
            // Trigger the flash animation on the UI thread
            Dispatcher.Invoke(() =>
            {
                var flashAnimation = FindResource("QueueFlashAnimation") as Storyboard;
                flashAnimation?.Begin();
            });
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Unsubscribe from event
            if (_viewModel != null)
            {
                _viewModel.QueueItemAdded -= OnQueueItemAdded;
            }

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
