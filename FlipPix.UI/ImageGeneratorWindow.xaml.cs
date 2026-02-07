using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
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
            Loaded += OnLoaded;

            // Subscribe to the Analyzer's QueueItemAdded event to trigger flash animation
            if (_viewModel.Analyzer != null)
            {
                _viewModel.Analyzer.QueueItemAdded += OnAnalyzerQueueItemAdded;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Ensure window is on screen and fully visible
            EnsureWindowVisible();
        }

        private void OnAnalyzerQueueItemAdded()
        {
            // Trigger the flash animation on the UI thread
            Dispatcher.Invoke(() =>
            {
                var flashAnimation = FindResource("QueueFlashAnimation") as Storyboard;
                flashAnimation?.Begin();
            });
        }

        private void EnsureWindowVisible()
        {
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            // If window is off-screen, reposition it
            if (Left < 0 || Top < 0 || Left + Width > screenWidth || Top + Height > screenHeight)
            {
                Left = (screenWidth - Width) / 2;
                Top = (screenHeight - Height) / 2;
            }
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

        private void OpenOutputImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is System.Windows.Controls.Button button && button.Tag is string imagePath)
                {
                    if (File.Exists(imagePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = imagePath,
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to open image: {ex.Message}", "Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }
}
