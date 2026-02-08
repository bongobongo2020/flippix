using System;
using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace FlipPix.UI
{
    public partial class I2V2AWindow : Window
    {
        private readonly I2V2AViewModel _viewModel;
        private readonly INavigationService _navigationService;
        private readonly WindowPositionService _windowPositionService;

        public I2V2AWindow(
            I2V2AViewModel viewModel,
            INavigationService navigationService,
            WindowPositionService windowPositionService)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
            DataContext = _viewModel;

            // Load window position
            _windowPositionService.LoadPosition("I2V2AWindow", this);
        }

        #region Navigation Methods

        private void NavigateToHome(object sender, RoutedEventArgs e)
        {
            _windowPositionService.SavePosition("I2V2AWindow", this);
            _navigationService.NavigateToAndClose<FlipPixWindow>(this);
        }

        private void NavigateToImageGen(object sender, RoutedEventArgs e)
        {
            _windowPositionService.SavePosition("I2V2AWindow", this);
            _navigationService.NavigateToAndClose<ImageGeneratorWindow>(this);
        }

        private void NavigateToVideoGen(object sender, RoutedEventArgs e)
        {
            _windowPositionService.SavePosition("I2V2AWindow", this);
            _navigationService.NavigateToAndClose<VideoGeneratorWindow>(this);
        }

        private void NavigateToSettings(object sender, RoutedEventArgs e)
        {
            _windowPositionService.SavePosition("I2V2AWindow", this);
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

            _windowPositionService.SavePosition("I2V2AWindow", this);
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            // Cleanup resources
            if (_viewModel is IDisposable disposableViewModel)
            {
                disposableViewModel.Dispose();
            }
            DataContext = null;
            base.OnClosed(e);
        }

        #endregion
    }
}
