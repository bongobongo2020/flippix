using System.Windows;
using System.Windows.Data;
using System.Globalization;
using Microsoft.Win32;
using FlipPix.UI.ViewModels;
using FlipPix.Core.Models;
using FlipPix.UI.Services;
using System.IO;

namespace FlipPix.UI
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly VideoAnalysisService _videoAnalysisService;

        public MainWindow(MainViewModel viewModel, VideoAnalysisService videoAnalysisService)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _videoAnalysisService = videoAnalysisService;
            DataContext = _viewModel;
        }

        private void BrowseVideoButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Video File",
                Filter = "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.wmv;*.webm|All Files|*.*",
                Multiselect = false
            };

            if (openFileDialog.ShowDialog() == true)
            {
                _viewModel.VideoFilePath = openFileDialog.FileName;
                _viewModel.AddLogMessage($"Video selected: {Path.GetFileName(openFileDialog.FileName)}");

                // Analyze video and update info
                AnalyzeVideoFile(openFileDialog.FileName);
            }
        }

        private void BrowseStyleButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Style Image",
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All Files|*.*",
                Multiselect = false
            };

            if (openFileDialog.ShowDialog() == true)
            {
                _viewModel.StyleImagePath = openFileDialog.FileName;
                _viewModel.AddLogMessage($"Style image selected: {Path.GetFileName(openFileDialog.FileName)}");
            }
        }

        private void BrowseFaceButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Face Reference Image",
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All Files|*.*",
                Multiselect = false
            };

            if (openFileDialog.ShowDialog() == true)
            {
                _viewModel.FaceImagePath = openFileDialog.FileName;
                _viewModel.AddLogMessage($"Face image selected: {Path.GetFileName(openFileDialog.FileName)}");
            }
        }

        private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.TestConnectionAsync();
        }

        private async void StartProcessingButton_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.StartProcessingAsync();
        }

        private void StopProcessingButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.StopProcessing();
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ClearLog();
        }

        private void OpenOutputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_viewModel.OutputVideoPath) && File.Exists(_viewModel.OutputVideoPath))
            {
                try
                {
                    // Open the folder and select the file
                    string folder = Path.GetDirectoryName(_viewModel.OutputVideoPath) ?? string.Empty;
                    if (Directory.Exists(folder))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_viewModel.OutputVideoPath}\"");
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Failed to open output folder: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
        }


        private async void AnalyzeVideoFile(string filePath)
        {
            try
            {
                _viewModel.AddLogMessage("Analyzing video file...");
                
                // Perform real video analysis using FFMpeg
                var videoInfo = await _videoAnalysisService.AnalyzeVideoAsync(filePath);

                _viewModel.UpdateVideoInfo(videoInfo);
                VideoInfoPanel.Visibility = Visibility.Visible;
                
                _viewModel.AddLogMessage($"Video analysis complete: {videoInfo.Width}x{videoInfo.Height}, {videoInfo.Duration.ToString(@"mm\:ss")}, {videoInfo.FrameRate:F2}fps");
            }
            catch (Exception ex)
            {
                _viewModel.AddLogMessage($"Error analyzing video: {ex.Message}");
                VideoInfoPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count)
            {
                return count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class FilePathToNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string filePath)
            {
                return Path.GetFileName(filePath);
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}