using System.Windows;
using System.Windows.Input;
using FlipPix.UI.ViewModels;
using FlipPix.Core.Services;

namespace FlipPix.UI
{
    public partial class ImageGeneratorWindow : Window
    {
        private readonly ImageGeneratorViewModel _viewModel;
        private readonly SettingsService _settingsService;

        public ImageGeneratorWindow(ImageGeneratorViewModel viewModel, SettingsService settingsService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;
            _settingsService = settingsService;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settingsWindow = new SettingsWindow(_settingsService);
                settingsWindow.Owner = this;
                settingsWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to open settings: {ex.Message}", "Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }
}
