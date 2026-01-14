using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using FlipPix.UI.ViewModels;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace FlipPix.UI
{
    public partial class I2V2AWindow : Window
    {
        private readonly I2V2AViewModel _viewModel;

        public I2V2AWindow(I2V2AViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = _viewModel;

            // Set window position
            LoadWindowPosition();
        }

        #region Navigation Methods

        private void NavigateToHome(object sender, RoutedEventArgs e)
        {
            SaveWindowPosition();
            var app = (App)Application.Current;
            var viewModel = app.Services?.GetService(typeof(FlipPixViewModel)) as FlipPixViewModel;
            if (viewModel != null)
            {
                var mainWindow = new FlipPixWindow(viewModel);
                mainWindow.Show();
            }
            Close();
        }

        private void NavigateToImageGen(object sender, RoutedEventArgs e)
        {
            SaveWindowPosition();
            var app = (App)Application.Current;
            var imageGenWindow = app.Services?.GetService(typeof(ImageGeneratorWindow)) as ImageGeneratorWindow;
            if (imageGenWindow != null)
            {
                imageGenWindow.Show();
            }
            else
            {
                // Create new instance if not found
                var viewModel = app.Services?.GetService(typeof(ViewModels.ImageGeneratorViewModel)) as ViewModels.ImageGeneratorViewModel;
                var settingsService = app.Services?.GetService(typeof(FlipPix.Core.Services.SettingsService)) as FlipPix.Core.Services.SettingsService;
                if (viewModel != null && settingsService != null)
                {
                    imageGenWindow = new ImageGeneratorWindow(viewModel, settingsService);
                    imageGenWindow.Show();
                }
            }
            Close();
        }

        private void NavigateToVideoGen(object sender, RoutedEventArgs e)
        {
            SaveWindowPosition();
            var app = (App)Application.Current;
            var videoGenWindow = app.Services?.GetService(typeof(VideoGeneratorWindow)) as VideoGeneratorWindow;
            if (videoGenWindow != null)
            {
                videoGenWindow.Show();
            }
            else
            {
                // Create new instance if not found
                var viewModel = app.Services?.GetService(typeof(ViewModels.VideoGeneratorViewModel)) as ViewModels.VideoGeneratorViewModel;
                if (viewModel != null)
                {
                    videoGenWindow = new VideoGeneratorWindow(viewModel);
                    videoGenWindow.Show();
                }
            }
            Close();
        }

        private void NavigateToSettings(object sender, RoutedEventArgs e)
        {
            SaveWindowPosition();
            // Implement settings window navigation if available
        }

        private void NavigateToAbout(object sender, RoutedEventArgs e)
        {
            // Show about dialog
            MessageBox.Show(
                "FlipPix - Image to Video with Audio Generator\n\n" +
                "This tool generates videos from images using ComfyUI workflows.\n\n" +
                "Version: 1.0.0",
                "About FlipPix I2V2A",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        #endregion

        #region Window Position Management

        private void LoadWindowPosition()
        {
            try
            {
                // Load saved window position if available
                var settings = System.Configuration.ConfigurationManager.AppSettings;
                if (settings != null)
                {
                    if (double.TryParse(settings["I2V2AWindow.Left"], out double left) &&
                        double.TryParse(settings["I2V2AWindow.Top"], out double top) &&
                        double.TryParse(settings["I2V2AWindow.Width"], out double width) &&
                        double.TryParse(settings["I2V2AWindow.Height"], out double height))
                    {
                        Left = left;
                        Top = top;
                        Width = width;
                        Height = height;

                        // Ensure window is visible on screen
                        EnsureWindowVisible();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load I2V2A window position: {ex.Message}");
            }
        }

        private void SaveWindowPosition()
        {
            try
            {
                // Save current window position
                var config = System.Configuration.ConfigurationManager.OpenExeConfiguration(System.Configuration.ConfigurationUserLevel.None);
                if (config.AppSettings.Settings["I2V2AWindow.Left"] == null)
                {
                    config.AppSettings.Settings.Add("I2V2AWindow.Left", Left.ToString());
                }
                else
                {
                    config.AppSettings.Settings["I2V2AWindow.Left"].Value = Left.ToString();
                }

                if (config.AppSettings.Settings["I2V2AWindow.Top"] == null)
                {
                    config.AppSettings.Settings.Add("I2V2AWindow.Top", Top.ToString());
                }
                else
                {
                    config.AppSettings.Settings["I2V2AWindow.Top"].Value = Top.ToString();
                }

                if (config.AppSettings.Settings["I2V2AWindow.Width"] == null)
                {
                    config.AppSettings.Settings.Add("I2V2AWindow.Width", Width.ToString());
                }
                else
                {
                    config.AppSettings.Settings["I2V2AWindow.Width"].Value = Width.ToString();
                }

                if (config.AppSettings.Settings["I2V2AWindow.Height"] == null)
                {
                    config.AppSettings.Settings.Add("I2V2AWindow.Height", Height.ToString());
                }
                else
                {
                    config.AppSettings.Settings["I2V2AWindow.Height"].Value = Height.ToString();
                }

                config.Save(System.Configuration.ConfigurationSaveMode.Modified);
                System.Configuration.ConfigurationManager.RefreshSection("appSettings");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save I2V2A window position: {ex.Message}");
            }
        }

        private void EnsureWindowVisible()
        {
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            // Ensure window is not outside screen boundaries
            if (Left < 0) Left = 0;
            if (Top < 0) Top = 0;
            if (Left + Width > screenWidth) Left = screenWidth - Width;
            if (Top + Height > screenHeight) Top = screenHeight - Height;
        }

        #endregion

        #region Window Events

        protected override void OnClosing(CancelEventArgs e)
        {
            // Cancel any ongoing operations
            if (_viewModel.IsProcessing)
            {
                var result = MessageBox.Show(
                    "Video generation is in progress. Are you sure you want to close this window?",
                    "Generation in Progress",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }

                // Cancel the ongoing generation
                _viewModel.CancelGenerationCommand?.Execute(null);
            }

            SaveWindowPosition();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            // Cleanup resources
            if (_viewModel is IDisposable disposableViewModel)
            {
                disposableViewModel.Dispose();
            }
            base.OnClosed(e);
        }

        #endregion
    }
}