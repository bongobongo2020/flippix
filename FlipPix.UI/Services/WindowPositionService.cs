using System;
using System.Windows;
using System.Configuration;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Service for managing window position persistence
    /// </summary>
    public class WindowPositionService
    {
        /// <summary>
        /// Save the current position of a window to configuration
        /// </summary>
        /// <param name="windowName">Unique name for the window (used as config key prefix)</param>
        /// <param name="window">The window to save position from</param>
        public void SavePosition(string windowName, Window window)
        {
            if (string.IsNullOrWhiteSpace(windowName))
            {
                throw new ArgumentException("Window name cannot be null or empty", nameof(windowName));
            }

            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            try
            {
                var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);

                SaveSetting(config, $"{windowName}.Left", window.Left.ToString());
                SaveSetting(config, $"{windowName}.Top", window.Top.ToString());
                SaveSetting(config, $"{windowName}.Width", window.Width.ToString());
                SaveSetting(config, $"{windowName}.Height", window.Height.ToString());

                config.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection("appSettings");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save {windowName} window position: {ex.Message}");
            }
        }

        /// <summary>
        /// Load and apply a previously saved window position
        /// </summary>
        /// <param name="windowName">Unique name for the window (used as config key prefix)</param>
        /// <param name="window">The window to apply position to</param>
        /// <returns>True if position was loaded successfully, false otherwise</returns>
        public bool LoadPosition(string windowName, Window window)
        {
            if (string.IsNullOrWhiteSpace(windowName))
            {
                throw new ArgumentException("Window name cannot be null or empty", nameof(windowName));
            }

            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            try
            {
                var settings = ConfigurationManager.AppSettings;

                if (double.TryParse(settings[$"{windowName}.Left"], out double left) &&
                    double.TryParse(settings[$"{windowName}.Top"], out double top) &&
                    double.TryParse(settings[$"{windowName}.Width"], out double width) &&
                    double.TryParse(settings[$"{windowName}.Height"], out double height))
                {
                    window.Left = left;
                    window.Top = top;
                    window.Width = width;
                    window.Height = height;

                    EnsureWindowVisible(window);
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load {windowName} window position: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Ensure a window is visible within the screen bounds
        /// </summary>
        /// <param name="window">The window to check and adjust</param>
        public void EnsureWindowVisible(Window window)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            // Ensure window is not outside screen boundaries
            if (window.Left < 0) window.Left = 0;
            if (window.Top < 0) window.Top = 0;
            if (window.Left + window.Width > screenWidth) window.Left = screenWidth - window.Width;
            if (window.Top + window.Height > screenHeight) window.Top = screenHeight - window.Height;
        }

        /// <summary>
        /// Helper method to save a setting value
        /// </summary>
        private void SaveSetting(Configuration config, string key, string value)
        {
            if (config.AppSettings.Settings[key] == null)
            {
                config.AppSettings.Settings.Add(key, value);
            }
            else
            {
                config.AppSettings.Settings[key].Value = value;
            }
        }
    }
}
