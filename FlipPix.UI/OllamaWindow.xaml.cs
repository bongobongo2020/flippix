using System;
using System.Windows;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI
{
    public partial class OllamaWindow : Window
    {
        private readonly OllamaViewModel _viewModel;

        public OllamaWindow(OllamaViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;
        }

        protected override void OnClosed(EventArgs e)
        {
            // Dispose the ViewModel when the window is closed
            _viewModel?.Dispose();
            base.OnClosed(e);
        }
    }
}