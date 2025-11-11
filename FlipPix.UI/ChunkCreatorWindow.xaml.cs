using System.Windows;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI
{
    public partial class ChunkCreatorWindow : Window
    {
        private readonly ChunkCreatorViewModel _viewModel;

        public ChunkCreatorWindow(ChunkCreatorViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _viewModel.Cleanup();
        }
    }
}