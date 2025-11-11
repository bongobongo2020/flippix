using System.Windows;
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
    }
}
