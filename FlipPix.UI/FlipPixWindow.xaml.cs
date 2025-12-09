using System.Windows;
using System.Windows.Input;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI
{
    public partial class FlipPixWindow : Window
    {
        public FlipPixWindow(FlipPixViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
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
