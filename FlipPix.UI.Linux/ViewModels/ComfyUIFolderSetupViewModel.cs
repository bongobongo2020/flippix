using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using FlipPix.Core.Services;
using FlipPix.UI.Linux.Services;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Linux.ViewModels
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
        private bool _canSave = false;
        private string _outputFolderInfo = string.Empty;
        private bool _isTestingConnection = false;
        private bool _isServerConnected = false;
        private string _serverConnectionMessage = "Not tested";

        public event EventHandler<bool>? CloseRequested;

        public ComfyUIFolderSetupViewModel(SettingsService settingsService, IFileDialogService fileDialogService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            if (!string.IsNullOrEmpty(_settingsService.Settings.ComfyUIFolderPath)) FolderPath = _settingsService.Settings.ComfyUIFolderPath;
            if (!string.IsNullOrEmpty(_settingsService.Settings.BaseUrl)) ServerUrl = _settingsService.Settings.BaseUrl;
            if (!string.IsNullOrEmpty(_settingsService.Settings.RemoteOutputFolderPath)) RemoteOutputFolderPath = _settingsService.Settings.RemoteOutputFolderPath;
            if (!string.IsNullOrEmpty(_settingsService.Settings.RemoteLoraFolderPath)) RemoteLoraFolderPath = _settingsService.Settings.RemoteLoraFolderPath;
        }

        public string FolderPath { get => _folderPath; set { if (SetProperty(ref _folderPath, value)) ValidateFolderPath(); } }
        public string ServerUrl { get => _serverUrl; set { if (SetProperty(ref _serverUrl, value)) ValidateFolderPath(); } }
        public string RemoteOutputFolderPath { get => _remoteOutputFolderPath; set { if (SetProperty(ref _remoteOutputFolderPath, value)) ValidateFolderPath(); } }
        public string RemoteLoraFolderPath { get => _remoteLoraFolderPath; set { if (SetProperty(ref _remoteLoraFolderPath, value)) ValidateFolderPath(); } }
        public bool IsTestingConnection { get => _isTestingConnection; set { if (SetProperty(ref _isTestingConnection, value)) TestConnectionCommand.NotifyCanExecuteChanged(); } }
        public bool IsServerConnected { get => _isServerConnected; set { if (SetProperty(ref _isServerConnected, value)) ValidateFolderPath(); } }
        public string ServerConnectionMessage { get => _serverConnectionMessage; set => SetProperty(ref _serverConnectionMessage, value); }
        public string ValidationMessage { get => _validationMessage; set => SetProperty(ref _validationMessage, value); }
        public bool CanSave { get => _canSave; set { if (SetProperty(ref _canSave, value)) SaveCommand.NotifyCanExecuteChanged(); } }
        public string OutputFolderInfo { get => _outputFolderInfo; set => SetProperty(ref _outputFolderInfo, value); }
        public bool ShowOutputFolderInfo => !string.IsNullOrEmpty(_outputFolderInfo);

        [RelayCommand]
        private async Task BrowseFolderAsync()
        {
            var p = await _fileDialogService.OpenFolderDialogAsync("Select ComfyUI root folder", FolderPath);
            if (p != null) FolderPath = p;
        }

        [RelayCommand]
        private async Task BrowseRemoteOutputFolderAsync()
        {
            var p = await _fileDialogService.OpenFolderDialogAsync("Select remote output folder", RemoteOutputFolderPath);
            if (p != null) RemoteOutputFolderPath = p;
        }

        [RelayCommand]
        private async Task BrowseRemoteLoraFolderAsync()
        {
            var p = await _fileDialogService.OpenFolderDialogAsync("Select remote LoRA folder", RemoteLoraFolderPath);
            if (p != null) RemoteLoraFolderPath = p;
        }

        private void ValidateFolderPath()
        {
            try
            {
                if (IsRemoteServer(ServerUrl)) { ValidateRemoteConfiguration(); return; }
                if (string.IsNullOrWhiteSpace(FolderPath)) { ValidationMessage = "Please select a folder."; CanSave = false; return; }
                if (!Directory.Exists(FolderPath)) { ValidationMessage = "Folder does not exist."; CanSave = false; return; }
                var outputFolder = Path.Combine(FolderPath, "output");
                if (!Directory.Exists(outputFolder)) { ValidationMessage = "No 'output' folder found. Select the ComfyUI root."; CanSave = false; return; }
                if (IsServerConnected) { ValidationMessage = "Validated!"; CanSave = true; OutputFolderInfo = $"Output: {outputFolder}"; Save(); }
                else { ValidationMessage = "Folder OK - please test the server connection."; CanSave = false; OutputFolderInfo = $"Output: {outputFolder}"; }
            }
            catch (Exception ex) { ValidationMessage = $"Error: {ex.Message}"; CanSave = false; }
        }

        private void ValidateRemoteConfiguration()
        {
            if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out _)) { ValidationMessage = "Invalid server URL."; CanSave = false; return; }
            if (string.IsNullOrEmpty(RemoteOutputFolderPath)) { ValidationMessage = "Remote output folder required."; CanSave = false; return; }
            if (!Directory.Exists(RemoteOutputFolderPath)) { ValidationMessage = "Remote output folder not accessible."; CanSave = false; return; }
            if (!IsServerConnected) { ValidationMessage = "Folder OK - please test server connection."; CanSave = false; return; }
            ValidationMessage = "Remote configuration validated!"; CanSave = true; OutputFolderInfo = $"Remote output: {RemoteOutputFolderPath}";
        }

        private bool IsRemoteServer(string url)
        {
            try
            {
                var uri = new Uri(url);
                var h = uri.Host.ToLowerInvariant();
                return h != "localhost" && h != "127.0.0.1" && h != "0.0.0.0" && h != "::1";
            }
            catch { return false; }
        }

        [RelayCommand(CanExecute = nameof(CanTestConnection))]
        private async Task TestConnectionAsync()
        {
            IsTestingConnection = true; ServerConnectionMessage = "Testing..."; IsServerConnected = false;
            try
            {
                var url = ServerUrl.StartsWith("http") ? ServerUrl : "http://" + ServerUrl;
                if (url != ServerUrl) ServerUrl = url;
                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var r = await hc.GetAsync($"{url}/system_stats");
                if (r.IsSuccessStatusCode) { IsServerConnected = true; ServerConnectionMessage = "Connected!"; }
                else { ServerConnectionMessage = $"Failed: {r.StatusCode}"; }
            }
            catch (Exception ex) { ServerConnectionMessage = $"Error: {ex.Message}"; }
            finally { IsTestingConnection = false; }
        }

        private bool CanTestConnection() => !IsTestingConnection;

        [RelayCommand(CanExecute = nameof(CanSave))]
        private void Save()
        {
            try
            {
                _settingsService.Settings.BaseUrl = ServerUrl;
                _settingsService.Settings.RemoteOutputFolderPath = RemoteOutputFolderPath;
                _settingsService.Settings.RemoteLoraFolderPath = RemoteLoraFolderPath;
                if (IsRemoteServer(ServerUrl)) { _settingsService.SaveSettings(_settingsService.Settings); CloseRequested?.Invoke(this, true); }
                else if (_settingsService.ValidateAndSetComfyUIFolder(FolderPath)) { _settingsService.SaveSettings(_settingsService.Settings); CloseRequested?.Invoke(this, true); }
                else { ValidationMessage = "Failed to save settings."; }
            }
            catch (Exception ex) { ValidationMessage = $"Error: {ex.Message}"; }
        }

        [RelayCommand]
        private void Cancel() => CloseRequested?.Invoke(this, false);

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { if (!_disposed && disposing) _disposed = true; }
    }
}
