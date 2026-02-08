using System;
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

        protected override void OnClosed(EventArgs e)
        {
            // Dispose the ViewModel if it implements IDisposable
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }

            DataContext = null;
            base.OnClosed(e);
        }
    }
}
