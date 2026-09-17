using System.Windows;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.UI
{
    /// <summary>
    /// ➕ The ⚡ H3 Express job sheet — see <see cref="ExpressJobViewModel"/>. The window holds no logic:
    /// the two buttons close it, and <see cref="ExpressJobViewModel.Confirmed"/> tells the tab which one
    /// was pressed.
    /// </summary>
    public partial class ExpressJobWindow : Window
    {
        public ExpressJobWindow(ExpressJobViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = viewModel;
        }

        public ExpressJobViewModel ViewModel { get; }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.Confirm();
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
