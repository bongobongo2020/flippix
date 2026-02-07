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
    }
}
