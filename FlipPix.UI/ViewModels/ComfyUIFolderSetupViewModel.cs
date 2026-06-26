using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FlipPix.Core.Services;
using FlipPix.ComfyUI.Http;
using FlipPix.UI.Services;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.ViewModels
{
    public partial class ComfyUIFolderSetupViewModel : ObservableObject, IDisposable
    {
        private readonly SettingsService _settingsService;
        private readonly IFileDialogService _fileDialogService;
        private bool _disposed = false;
        private string _folderPath = string.Empty;
        private string _serverUrl = "http://localhost:8188";
        private string _remoteOutputFolderPath = string.Empty;
        private string _remoteLoraFolderPath = string.Empty;
        private string _validationMessage = string.Empty;
        private System.Windows.Media.Brush _validationMessageColor = System.Windows.Media.Brushes.Red;
        private bool _canSave = false;
        private string _outputFolderInfo = string.Empty;
        private System.Windows.Visibility _outputFolderInfoVisibility = System.Windows.Visibility.Collapsed;
        private bool _isTestingConnection = false;
        private bool _isServerConnected = false;
        private string _serverConnectionMessage = "Not tested";

        public event EventHandler<bool>? CloseRequested;

        public ComfyUIFolderSetupViewModel(SettingsService settingsService, IFileDialogService fileDialogService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            // Pre-fill with existing settings if available, otherwise try to auto-detect an
            // install (e.g. the one the FlipPix installer created) so the field starts populated.
            if (!string.IsNullOrEmpty(_settingsService.Settings.ComfyUIFolderPath))
            {
                FolderPath = _settingsService.Settings.ComfyUIFolderPath;
            }
            else
            {
                var detected = _settingsService.TryAutoDetectComfyUIFolder();
                if (!string.IsNullOrEmpty(detected))
                {
                    FolderPath = detected;
                }
            }

            if (!string.IsNullOrEmpty(_settingsService.Settings.BaseUrl))
            {
                ServerUrl = _settingsService.Settings.BaseUrl;
            }

            if (!string.IsNullOrEmpty(_settingsService.Settings.RemoteOutputFolderPath))
            {
                RemoteOutputFolderPath = _settingsService.Settings.RemoteOutputFolderPath;
            }

            if (!string.IsNullOrEmpty(_settingsService.Settings.RemoteLoraFolderPath))
            {
                RemoteLoraFolderPath = _settingsService.Settings.RemoteLoraFolderPath;
            }
        }

        public string FolderPath
        {
            get => _folderPath;
            set
            {
                if (SetProperty(ref _folderPath, value))
                {
                    ValidateFolderPath();
                }
            }
        }

        public string ServerUrl
        {
            get => _serverUrl;
            set
            {
                if (SetProperty(ref _serverUrl, value))
                {
                    ValidateFolderPath(); // This will detect remote/local and validate accordingly
                }
            }
        }

        public string RemoteOutputFolderPath
        {
            get => _remoteOutputFolderPath;
            set
            {
                if (SetProperty(ref _remoteOutputFolderPath, value))
                {
                    ValidateFolderPath(); // This will call the correct validation logic
                }
            }
        }

        public string RemoteLoraFolderPath
        {
            get => _remoteLoraFolderPath;
            set
            {
                if (SetProperty(ref _remoteLoraFolderPath, value))
                {
                    ValidateFolderPath(); // This will call the correct validation logic
                }
            }
        }

        public bool IsTestingConnection
        {
            get => _isTestingConnection;
            set
            {
                if (SetProperty(ref _isTestingConnection, value))
                {
                    TestConnectionCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool IsServerConnected
        {
            get => _isServerConnected;
            set
            {
                if (SetProperty(ref _isServerConnected, value))
                {
                    ValidateFolderPath(); // Trigger validation when connection status changes
                }
            }
        }

        public string ServerConnectionMessage
        {
            get => _serverConnectionMessage;
            set => SetProperty(ref _serverConnectionMessage, value);
        }

        public string ValidationMessage
        {
            get => _validationMessage;
            set => SetProperty(ref _validationMessage, value);
        }

        public System.Windows.Media.Brush ValidationMessageColor
        {
            get => _validationMessageColor;
            set => SetProperty(ref _validationMessageColor, value);
        }

        public bool CanSave
        {
            get => _canSave;
            set
            {
                if (SetProperty(ref _canSave, value))
                {
                    SaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string OutputFolderInfo
        {
            get => _outputFolderInfo;
            set => SetProperty(ref _outputFolderInfo, value);
        }

        public System.Windows.Visibility OutputFolderInfoVisibility
        {
            get => _outputFolderInfoVisibility;
            set => SetProperty(ref _outputFolderInfoVisibility, value);
        }

        [RelayCommand]
        private async Task BrowseFolderAsync()
        {
            var selectedPath = await _fileDialogService.OpenFolderDialogAsync(
                "Select the root folder of your ComfyUI installation",
                !string.IsNullOrEmpty(FolderPath) && Directory.Exists(FolderPath) ? FolderPath : null,
                false);

            if (selectedPath != null)
            {
                FolderPath = selectedPath;
            }
        }

        [RelayCommand]
        private async Task BrowseRemoteOutputFolderAsync()
        {
            var selectedPath = await _fileDialogService.OpenFolderDialogAsync(
                "Select the network path to the remote ComfyUI output folder",
                !string.IsNullOrEmpty(RemoteOutputFolderPath) && Directory.Exists(RemoteOutputFolderPath) ? RemoteOutputFolderPath : null,
                false);

            if (selectedPath != null)
            {
                RemoteOutputFolderPath = selectedPath;
            }
        }

        [RelayCommand]
        private async Task BrowseRemoteLoraFolderAsync()
        {
            var selectedPath = await _fileDialogService.OpenFolderDialogAsync(
                "Select the network path to the remote ComfyUI LoRA folder (e.g., Y:\\ai-models\\loras\\zimage)",
                !string.IsNullOrEmpty(RemoteLoraFolderPath) && Directory.Exists(RemoteLoraFolderPath) ? RemoteLoraFolderPath : null,
                false);

            if (selectedPath != null)
            {
                RemoteLoraFolderPath = selectedPath;
            }
        }

        private void ValidateFolderPath()
        {
            try
            {
                // Check if this is a remote server - if so, use different validation logic
                if (IsRemoteServer(ServerUrl))
                {
                    ValidateRemoteConfiguration();
                    return;
                }

                if (string.IsNullOrWhiteSpace(FolderPath))
                {
                    ValidationMessage = "Please select a folder.";
                    ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                    CanSave = false;
                    OutputFolderInfoVisibility = System.Windows.Visibility.Collapsed;
                    System.Diagnostics.Debug.WriteLine("FlipPix Setup: Validation failed - No folder path provided");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Validating folder path: {FolderPath}");

                if (!Directory.Exists(FolderPath))
                {
                    ValidationMessage = "The selected folder does not exist.";
                    ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                    CanSave = false;
                    OutputFolderInfoVisibility = System.Windows.Visibility.Collapsed;
                    System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Validation failed - Folder does not exist: {FolderPath}");
                    return;
                }

                var outputFolder = Path.Combine(FolderPath, "output");
                System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Checking for output folder: {outputFolder}");

                if (!Directory.Exists(outputFolder))
                {
                    ValidationMessage = "Error: 'output' folder not found in the selected ComfyUI folder.\n" +
                                      "Please ensure you selected the root ComfyUI folder (not a subfolder).";
                    ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                    CanSave = false;
                    OutputFolderInfoVisibility = System.Windows.Visibility.Collapsed;
                    System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Validation failed - Output folder not found: {outputFolder}");
                    return;
                }

                // Check if server URL is also valid
                ValidateServerUrl();
                if (!IsValidServerUrl(ServerUrl))
                {
                    ValidationMessage = "Folder validated successfully! Please test the server connection.";
                    ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 100));
                    CanSave = false;
                    OutputFolderInfo = $"Output folder: {outputFolder}";
                    OutputFolderInfoVisibility = System.Windows.Visibility.Visible;
                    return;
                }

                // Only proceed if both folder and server are validated
                if (IsServerConnected)
                {
                    ValidationMessage = "Folder and server validated successfully! Saving settings...";
                    ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 255, 100));
                    CanSave = true;
                    OutputFolderInfo = $"Output folder: {outputFolder}";
                    OutputFolderInfoVisibility = System.Windows.Visibility.Visible;
                    System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Validation successful - CanSave set to true");

                    // Auto-save and proceed after successful validation
                    System.Diagnostics.Debug.WriteLine("FlipPix Setup: Auto-saving settings after validation");
                    Save();
                }
                else
                {
                    ValidationMessage = "Folder validated successfully! Please test the server connection.";
                    ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 100));
                    CanSave = false;
                    OutputFolderInfo = $"Output folder: {outputFolder}";
                    OutputFolderInfoVisibility = System.Windows.Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                ValidationMessage = $"Error validating folder: {ex.Message}";
                ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                CanSave = false;
                OutputFolderInfoVisibility = System.Windows.Visibility.Collapsed;
                System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Exception during validation - {ex}");
            }
        }

        private void ValidateServerUrl()
        {
            if (IsValidServerUrl(ServerUrl))
            {
                System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Server URL format is valid: {ServerUrl}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Invalid server URL format: {ServerUrl}");
                IsServerConnected = false;
                ServerConnectionMessage = "Invalid URL format";
            }
        }

        private void ValidateRemoteOutputFolder()
        {
            if (string.IsNullOrEmpty(RemoteOutputFolderPath))
            {
                // Remote folder is optional, so this is valid
                return;
            }

            try
            {
                if (Directory.Exists(RemoteOutputFolderPath))
                {
                    System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Remote output folder is accessible: {RemoteOutputFolderPath}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Remote output folder not accessible: {RemoteOutputFolderPath}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Error accessing remote output folder: {ex.Message}");
            }
        }

        private bool IsRemoteServer(string serverUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(serverUrl))
                    return false;

                var uri = new Uri(serverUrl);
                var host = uri.Host.ToLowerInvariant();

                // Check if it's not a local address
                return !host.Equals("localhost") &&
                       !host.Equals("127.0.0.1") &&
                       !host.Equals("0.0.0.0") &&
                       !host.Equals("::1");
            }
            catch
            {
                return false;
            }
        }

        private void ValidateRemoteConfiguration()
        {
            System.Diagnostics.Debug.WriteLine("FlipPix Setup: Validating remote server configuration");

            // Check if server URL is valid
            ValidateServerUrl();
            if (!IsValidServerUrl(ServerUrl))
            {
                ValidationMessage = "Please enter a valid server URL (e.g., http://192.168.1.218:8188).";
                ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                CanSave = false;
                OutputFolderInfoVisibility = System.Windows.Visibility.Collapsed;
                return;
            }

            // Check if remote output folder is configured and accessible
            if (string.IsNullOrEmpty(RemoteOutputFolderPath))
            {
                ValidationMessage = "Remote output folder is required for remote ComfyUI servers.";
                ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 100));
                CanSave = false;
                OutputFolderInfoVisibility = System.Windows.Visibility.Collapsed;
                return;
            }

            if (!Directory.Exists(RemoteOutputFolderPath))
            {
                ValidationMessage = "Remote output folder is not accessible. Please check the network path.";
                ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                CanSave = false;
                OutputFolderInfoVisibility = System.Windows.Visibility.Collapsed;
                return;
            }

            // Test server connection
            if (!IsServerConnected)
            {
                ValidationMessage = "Remote output folder configured! Please test the server connection.";
                ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 100));
                CanSave = false;
                OutputFolderInfo = $"Remote output folder: {RemoteOutputFolderPath}";
                OutputFolderInfoVisibility = System.Windows.Visibility.Visible;
                return;
            }

            // All validations passed
            ValidationMessage = "Remote server configuration validated successfully!";
            ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 255, 100));
            CanSave = true;
            OutputFolderInfo = $"Remote output folder: {RemoteOutputFolderPath}";
            OutputFolderInfoVisibility = System.Windows.Visibility.Visible;
            System.Diagnostics.Debug.WriteLine("FlipPix Setup: Remote validation successful - CanSave set to true");
        }

        private bool IsValidServerUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            // Add protocol if missing
            var normalizedUrl = url;
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                normalizedUrl = "http://" + url;
            }

            // Use regex to validate URL format
            var urlPattern = @"^(https?://)?([\da-z\.-]+)\.([a-z\.]{2,6})(:[0-9]{1,5})?([/?#].*)?$|^https?://localhost(:[0-9]{1,5})?([/?#].*)?$|^https?://(\d{1,3}\.){3}\d{1,3}(:[0-9]{1,5})?([/?#].*)?$";
            return Regex.IsMatch(normalizedUrl, urlPattern, RegexOptions.IgnoreCase);
        }

        [RelayCommand(CanExecute = nameof(CanTestConnection))]
        private async Task TestConnectionAsync()
        {
            if (!IsValidServerUrl(ServerUrl))
            {
                ValidationMessage = "Please enter a valid server URL (e.g., localhost:8188 or 192.168.1.100:8188)";
                ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                return;
            }

            IsTestingConnection = true;
            ServerConnectionMessage = "Testing connection...";
            IsServerConnected = false;

            try
            {
                // Normalize URL (add protocol if missing)
                var normalizedUrl = ServerUrl;
                if (!ServerUrl.StartsWith("http://") && !ServerUrl.StartsWith("https://"))
                {
                    normalizedUrl = "http://" + ServerUrl;
                    ServerUrl = normalizedUrl;
                }

                System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Testing connection to {normalizedUrl}");

                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);

                var response = await httpClient.GetAsync($"{normalizedUrl}/system_stats");

                if (response.IsSuccessStatusCode)
                {
                    IsServerConnected = true;
                    ServerConnectionMessage = $"Connected successfully!";

                    // For remote servers with output folder, just save and close
                    if (IsRemoteServer(ServerUrl) && !string.IsNullOrEmpty(RemoteOutputFolderPath))
                    {
                        System.Diagnostics.Debug.WriteLine("FlipPix Setup: Remote server + output folder - auto-saving and closing");
                        Save();
                    }
                    else
                    {
                        ValidationMessage = "Server connection successful! You can now save the settings.";
                        ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 255, 100));

                        // Check if local folder is also validated to enable save
                        if (!string.IsNullOrWhiteSpace(FolderPath) && Directory.Exists(FolderPath) &&
                            Directory.Exists(Path.Combine(FolderPath, "output")))
                        {
                            CanSave = true;
                        }
                    }

                    System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Connection test successful to {normalizedUrl}");
                }
                else
                {
                    IsServerConnected = false;
                    ServerConnectionMessage = $"Connection failed: {response.StatusCode}";
                    ValidationMessage = $"Server connection failed. Please check if ComfyUI is running at {normalizedUrl}";
                    ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                    CanSave = false;
                    System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Connection test failed with status {response.StatusCode}");
                }
            }
            catch (TaskCanceledException)
            {
                IsServerConnected = false;
                ServerConnectionMessage = "Connection timed out";
                ValidationMessage = "Connection timed out. Please check the server address and ensure ComfyUI is running.";
                ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                CanSave = false;
                System.Diagnostics.Debug.WriteLine("FlipPix Setup: Connection test timed out");
            }
            catch (Exception ex)
            {
                IsServerConnected = false;
                ServerConnectionMessage = "Connection failed";
                ValidationMessage = $"Failed to connect to server: {ex.Message}";
                ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                CanSave = false;
                System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Connection test exception: {ex.Message}");
            }
            finally
            {
                IsTestingConnection = false;
            }
        }

        private bool CanTestConnection() => !IsTestingConnection;

        [RelayCommand(CanExecute = nameof(CanSave))]
        private void Save()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Save button clicked. Attempting to save folder path: {FolderPath}, server URL: {ServerUrl}, and remote output folder: {RemoteOutputFolderPath}");

                // Update server URL in settings
                _settingsService.Settings.BaseUrl = ServerUrl;

                // Save remote output folder setting
                _settingsService.Settings.RemoteOutputFolderPath = RemoteOutputFolderPath;

                // Save remote LoRA folder setting
                _settingsService.Settings.RemoteLoraFolderPath = RemoteLoraFolderPath;

                // For remote servers, don't validate local ComfyUI folder
                if (IsRemoteServer(ServerUrl))
                {
                    // Just save the settings for remote servers
                    _settingsService.SaveSettings(_settingsService.Settings);
                    System.Diagnostics.Debug.WriteLine("FlipPix Setup: Remote server settings saved successfully. Closing setup window.");
                    CloseRequested?.Invoke(this, true);
                }
                else
                {
                    // For local servers, validate local ComfyUI folder
                    if (_settingsService.ValidateAndSetComfyUIFolder(FolderPath))
                    {
                        // Save the updated settings including the server URL and remote output folder
                        _settingsService.SaveSettings(_settingsService.Settings);
                        System.Diagnostics.Debug.WriteLine("FlipPix Setup: Local server settings saved successfully. Closing setup window.");
                        CloseRequested?.Invoke(this, true);
                    }
                    else
                    {
                        ValidationMessage = "Failed to save settings. Please ensure the 'output' folder exists in the selected ComfyUI folder.";
                        ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                        System.Diagnostics.Debug.WriteLine("FlipPix Setup: Save failed - ValidateAndSetComfyUIFolder returned false");
                    }
                }
            }
            catch (Exception ex)
            {
                ValidationMessage = $"Error saving settings: {ex.Message}";
                ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                System.Diagnostics.Debug.WriteLine($"FlipPix Setup: Exception during save - {ex}");
            }
        }

        [RelayCommand]
        private void Cancel()
        {
            CloseRequested?.Invoke(this, false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                // Clear string properties to free memory
                _folderPath = string.Empty;
                _serverUrl = string.Empty;
                _remoteOutputFolderPath = string.Empty;
                _remoteLoraFolderPath = string.Empty;
                _validationMessage = string.Empty;
                _outputFolderInfo = string.Empty;
                _serverConnectionMessage = string.Empty;

                _disposed = true;
            }
        }
    }
}
