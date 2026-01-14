using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FlipPix.UI.ViewModels
{
    public class OllamaViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly OllamaService _ollamaService;
        private readonly IAppLogger _logger;
        private readonly IServiceProvider? _serviceProvider;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _disposed = false;

        private string _userPrompt = string.Empty;
        private string _enhancedPrompt = string.Empty;
        private string _ollamaUrl = "http://localhost:11434";
        private bool _isProcessing;
        private bool _isConnected;
        private string _statusMessage = "Ready";
        private OllamaModel? _selectedModel;
        private string _selectedEnhancementType = "video";
        private bool _showEnhancedPrompt;

        public ObservableCollection<OllamaModel> AvailableModels { get; } = new ObservableCollection<OllamaModel>();
        public Array EnhancementTypes => new[] { "video", "monologue", "image" };

        public string UserPrompt
        {
            get => _userPrompt;
            set
            {
                _userPrompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEnhancePrompt));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string EnhancedPrompt
        {
            get => _enhancedPrompt;
            set
            {
                _enhancedPrompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSendToGenerator));
            }
        }

        public string OllamaUrl
        {
            get => _ollamaUrl;
            set
            {
                _ollamaUrl = value;
                OnPropertyChanged();
            }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                _isProcessing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanEnhancePrompt));
                OnPropertyChanged(nameof(CanSendToGenerator));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                _isConnected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEnhancePrompt));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }

        public OllamaModel? SelectedModel
        {
            get => _selectedModel;
            set
            {
                _selectedModel = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEnhancePrompt));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string SelectedEnhancementType
        {
            get => _selectedEnhancementType;
            set
            {
                _selectedEnhancementType = value;
                OnPropertyChanged();
            }
        }

        public bool ShowEnhancedPrompt
        {
            get => _showEnhancedPrompt;
            set
            {
                _showEnhancedPrompt = value;
                OnPropertyChanged();
            }
        }

        public bool CanConnect => !IsProcessing && !string.IsNullOrEmpty(OllamaUrl);
        public bool CanEnhancePrompt => !IsProcessing && IsConnected && SelectedModel != null && !string.IsNullOrEmpty(UserPrompt);
        public bool CanSendToGenerator => !string.IsNullOrEmpty(EnhancedPrompt);

        public ICommand ConnectCommand { get; }
        public ICommand RefreshModelsCommand { get; }
        public ICommand EnhancePromptCommand { get; }
        public ICommand SendToImageGeneratorCommand { get; }
        public ICommand SendToVideoGeneratorCommand { get; }
        public ICommand CancelCommand { get; }

        public OllamaViewModel(OllamaService ollamaService, IAppLogger logger, IServiceProvider? serviceProvider = null)
        {
            _ollamaService = ollamaService;
            _logger = logger;
            _serviceProvider = serviceProvider;
            _cancellationTokenSource = new CancellationTokenSource();

            ConnectCommand = new RelayCommand(async () => await ConnectAsync(), () => CanConnect);
            RefreshModelsCommand = new RelayCommand(async () => await RefreshModelsAsync(), () => IsConnected && !IsProcessing);
            EnhancePromptCommand = new RelayCommand(async () => await EnhancePromptAsync(), () => CanEnhancePrompt);
            SendToImageGeneratorCommand = new RelayCommand(SendToImageGenerator, () => CanSendToGenerator);
            SendToVideoGeneratorCommand = new RelayCommand(SendToVideoGenerator, () => CanSendToGenerator && SelectedEnhancementType == "video");
            CancelCommand = new RelayCommand(CancelEnhancement, () => IsProcessing);

            // Check if Ollama is already running
            _ = Task.Run(async () =>
            {
                try
                {
                    IsConnected = await _ollamaService.IsOllamaRunningAsync(_cancellationTokenSource!.Token);
                    if (IsConnected)
                    {
                        StatusMessage = "Connected to Ollama";
                        await RefreshModelsAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Operation was cancelled
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error checking Ollama status: {ex.Message}");
                }
            }, _cancellationTokenSource!.Token);
        }

        private async Task ConnectAsync()
        {
            try
            {
                IsProcessing = true;
                StatusMessage = "Connecting to Ollama...";
                ResetCancellationToken();

                await _ollamaService.SetBaseUrlAsync(OllamaUrl);
                IsConnected = await _ollamaService.IsOllamaRunningAsync(_cancellationTokenSource!.Token);

                if (IsConnected)
                {
                    StatusMessage = "Connected to Ollama successfully";
                    await RefreshModelsAsync();
                }
                else
                {
                    StatusMessage = "Failed to connect to Ollama";
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Connection cancelled";
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error connecting to Ollama: {ex.Message}");
                StatusMessage = $"Connection error: {ex.Message}";
                System.Windows.MessageBox.Show($"Failed to connect to Ollama: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private async Task RefreshModelsAsync()
        {
            try
            {
                StatusMessage = "Fetching available models...";
                var models = await _ollamaService.GetAvailableModelsAsync(_cancellationTokenSource!.Token);

                AvailableModels.Clear();
                foreach (var model in models)
                {
                    AvailableModels.Add(model);
                }

                if (AvailableModels.Count > 0)
                {
                    SelectedModel = AvailableModels.First();
                    StatusMessage = $"Found {AvailableModels.Count} models";
                }
                else
                {
                    StatusMessage = "No models found. Please pull a model in Ollama first.";
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Model fetching cancelled";
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error refreshing models: {ex.Message}");
                StatusMessage = $"Error fetching models: {ex.Message}";
            }
        }

        private async Task EnhancePromptAsync()
        {
            try
            {
                IsProcessing = true;
                StatusMessage = "Enhancing prompt...";
                ResetCancellationToken();

                if (SelectedModel == null)
                {
                    StatusMessage = "No model selected";
                    return;
                }

                if (string.IsNullOrEmpty(UserPrompt))
                {
                    StatusMessage = "Please enter a prompt to enhance";
                    return;
                }

                _logger.LogInfo($"Enhancing prompt with model: {SelectedModel.Name}, type: {SelectedEnhancementType}");
                _logger.LogInfo($"Original prompt: {UserPrompt}");

                var enhanced = await _ollamaService.GenerateEnhancedPromptAsync(
                    SelectedModel.Name,
                    UserPrompt,
                    SelectedEnhancementType,
                    _cancellationTokenSource!.Token
                );

                if (string.IsNullOrEmpty(enhanced))
                {
                    EnhancedPrompt = "Error: No response from Ollama";
                    StatusMessage = "Failed to get enhanced prompt";
                }
                else
                {
                    EnhancedPrompt = enhanced;
                    ShowEnhancedPrompt = true;
                    StatusMessage = "Prompt enhanced successfully";
                    _logger.LogInfo($"Enhanced prompt: {enhanced}");
                }
            }
            catch (OperationCanceledException)
            {
                EnhancedPrompt = "Enhancement was cancelled";
                ShowEnhancedPrompt = true;
                StatusMessage = "Enhancement cancelled";
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error enhancing prompt: {ex.Message}");
                EnhancedPrompt = $"Error: {ex.Message}";
                ShowEnhancedPrompt = true;
                StatusMessage = $"Error: {ex.Message}";
                System.Windows.MessageBox.Show($"Failed to enhance prompt: {ex.Message}\n\nPlease ensure:\n1. Ollama is running (ollama serve)\n2. A model is installed (ollama pull <model-name>)\n3. The model is selected in the dropdown", "Enhancement Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private void CancelEnhancement()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                StatusMessage = "Cancelling operation...";
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error cancelling enhancement: {ex.Message}");
            }
        }

        private void ResetCancellationToken()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
            }
            _cancellationTokenSource = new CancellationTokenSource();
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
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();

                // Clear the collections to free memory
                AvailableModels.Clear();

                // Clear string properties
                _userPrompt = string.Empty;
                _enhancedPrompt = string.Empty;
                _ollamaUrl = string.Empty;
                _statusMessage = string.Empty;

                _disposed = true;
            }
        }

        private void SendToImageGenerator()
        {
            if (_serviceProvider == null || string.IsNullOrEmpty(EnhancedPrompt))
                return;

            try
            {
                var imageWindow = _serviceProvider.GetService(typeof(ImageGeneratorWindow)) as ImageGeneratorWindow;
                if (imageWindow?.DataContext is ImageGeneratorViewModel imageViewModel)
                {
                    imageViewModel.ImagePrompt = EnhancedPrompt;
                }

                // Position and show window
                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var screenHeight = SystemParameters.PrimaryScreenHeight;
                var windowWidth = 800;
                var windowHeight = 600;

                imageWindow!.Left = Math.Max(100, (screenWidth - windowWidth) / 2 + 200);
                imageWindow.Top = Math.Max(100, (screenHeight - windowHeight) / 2 + 100);
                imageWindow.WindowState = WindowState.Normal;
                imageWindow.Show();

                StatusMessage = "Prompt sent to Image Generator";
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error opening Image Generator: {ex.Message}");
                System.Windows.MessageBox.Show($"Failed to open Image Generator: {ex.Message}", "Navigation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SendToVideoGenerator()
        {
            if (_serviceProvider == null || string.IsNullOrEmpty(EnhancedPrompt))
                return;

            try
            {
                var videoWindow = _serviceProvider.GetService(typeof(StoryVideoWindow)) as StoryVideoWindow;
                // Note: Custom prompt transfer removed as StoryVideoViewModel no longer supports prompt generation

                // Position and show window
                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var screenHeight = SystemParameters.PrimaryScreenHeight;
                var windowWidth = 800;
                var windowHeight = 600;

                videoWindow!.Left = Math.Max(100, (screenWidth - windowWidth) / 2 + 250);
                videoWindow.Top = Math.Max(100, (screenHeight - windowHeight) / 2 + 150);
                videoWindow.WindowState = WindowState.Normal;
                videoWindow.Show();

                StatusMessage = "Prompt sent to Video Generator";
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error opening Video Generator: {ex.Message}");
                System.Windows.MessageBox.Show($"Failed to open Video Generator: {ex.Message}", "Navigation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}