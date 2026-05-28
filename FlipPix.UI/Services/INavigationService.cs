using System.Windows;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Interface for navigation service to handle window navigation
    /// </summary>
    public interface INavigationService
    {
        /// <summary>
        /// Navigate to a window of type TWindow
        /// </summary>
        void NavigateTo<TWindow>() where TWindow : Window;

        /// <summary>
        /// Navigate to a window by type
        /// </summary>
        void NavigateTo(Type windowType);

        /// <summary>
        /// Navigate to a window of type TWindow and close the current window
        /// </summary>
        void NavigateToAndClose<TWindow>(Window? currentWindow = null) where TWindow : Window;

        /// <summary>
        /// Navigate to a window by type and close the current window
        /// </summary>
        void NavigateToAndClose(Type windowType, Window? currentWindow = null);
    }
}
