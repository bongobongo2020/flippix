using System.Windows;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI
{
    public partial class ComfyUIFolderSetupWindow : Window
    {
        public ComfyUIFolderSetupWindow(ComfyUIFolderSetupViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            viewModel.CloseRequested += (sender, result) =>
            {
                DialogResult = result;
                Close();
            };
        }
    }
}
