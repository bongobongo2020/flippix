using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using FlipPix.Core.Services;
using FlipPix.Core.Models;

namespace FlipPix.UI
{
    public partial class RemoteSetupWindow : Window
    {
        private readonly SettingsService _settingsService;

        public string ServerUrl { get; private set; } = string.Empty;
        public string RemoteOutputFolder { get; private set; } = string.Empty;

        public RemoteSetupWindow(SettingsService settingsService)
        {
            InitializeComponent();
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            // Pre-fill with existing settings if available
            if (!string.IsNullOrEmpty(_settingsService.Settings.BaseUrl))
            {
                ServerUrlTextBox.Text = _settingsService.Settings.BaseUrl;
            }

            if (!string.IsNullOrEmpty(_settingsService.Settings.RemoteOutputFolderPath))
            {
                RemoteOutputFolderTextBox.Text = _settingsService.Settings.RemoteOutputFolderPath;
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            using (var folderDialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                folderDialog.Description = "Select the network path to the remote ComfyUI output folder";
                folderDialog.ShowNewFolderButton = false;

                if (!string.IsNullOrEmpty(RemoteOutputFolderTextBox.Text) && Directory.Exists(RemoteOutputFolderTextBox.Text))
                {
                    folderDialog.SelectedPath = RemoteOutputFolderTextBox.Text;
                }

                if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    RemoteOutputFolderTextBox.Text = folderDialog.SelectedPath;
                }
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ServerUrl = ServerUrlTextBox.Text.Trim();
                RemoteOutputFolder = RemoteOutputFolderTextBox.Text.Trim();

                // Basic validation
                if (string.IsNullOrEmpty(ServerUrl))
                {
                    System.Windows.MessageBox.Show("Please enter a server URL.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (string.IsNullOrEmpty(RemoteOutputFolder))
                {
                    System.Windows.MessageBox.Show("Please select a remote output folder.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!Directory.Exists(RemoteOutputFolder))
                {
                    System.Windows.MessageBox.Show("The selected remote output folder is not accessible. Please check the network path.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Save settings
                _settingsService.Settings.BaseUrl = ServerUrl;
                _settingsService.Settings.RemoteOutputFolderPath = RemoteOutputFolder;
                _settingsService.SaveSettings(_settingsService.Settings);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}