using System;
using System.Windows;
using System.Windows.Input;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI
{
    public partial class StoryVideoWindow : Window
    {
        private readonly StoryVideoViewModel _viewModel;

        public StoryVideoWindow(StoryVideoViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Ensure window is on screen and fully visible
            EnsureWindowVisible();
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
    }
}
