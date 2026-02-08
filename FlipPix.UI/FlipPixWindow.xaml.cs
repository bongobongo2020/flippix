using System;
using System.Windows;
using System.Windows.Input;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI
{
    public partial class FlipPixWindow : Window
    {
        private readonly WindowPositionService _windowPositionService;

        public FlipPixWindow(FlipPixViewModel viewModel, WindowPositionService windowPositionService)
        {
            InitializeComponent();
            _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
            DataContext = viewModel;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Ensure window is on screen and fully visible
            _windowPositionService.EnsureWindowVisible(this);
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
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
