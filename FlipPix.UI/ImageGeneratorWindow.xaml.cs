using System.Windows;
using System.Windows.Input;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI
{
    public partial class ImageGeneratorWindow : Window
    {
        private readonly ImageGeneratorViewModel _viewModel;

        public ImageGeneratorWindow(ImageGeneratorViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;
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
