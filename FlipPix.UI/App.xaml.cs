using System.Configuration;
using System.Data;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http;
using System.Net.Http;
using FlipPix.UI.ViewModels;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Services;
using FlipPix.UI.Services;
using FlipPix.ComfyUI.Http;
using FlipPix.ComfyUI.WebSocket;
using FlipPix.Core.Models;

namespace FlipPix.UI
{
    public partial class App : System.Windows.Application
    {
        private ServiceProvider? _serviceProvider;

        public ServiceProvider? Services => _serviceProvider;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Set shutdown mode to explicit so the app doesn't close when setup windows close
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Configure services
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            // Check if ComfyUI is configured
            System.Diagnostics.Debug.WriteLine("FlipPix App: OnStartup - Checking if ComfyUI is configured");
            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
            if (!settingsService.IsComfyUIFolderConfigured())
            {
                System.Diagnostics.Debug.WriteLine("FlipPix App: ComfyUI not configured. Showing choice window.");

                // Show choice window first
                var choiceWindow = new SetupChoiceWindow();
                var choiceResult = choiceWindow.ShowDialog();

                if (choiceResult != true)
                {
                    System.Diagnostics.Debug.WriteLine("FlipPix App: User cancelled choice. Shutting down application.");
                    Shutdown();
                    return;
                }

                try
                {
                    if (choiceWindow.IsLocalSelected)
                    {
                        System.Diagnostics.Debug.WriteLine("FlipPix App: Local installation selected. Showing local setup window.");
                        var setupViewModel = _serviceProvider.GetRequiredService<ComfyUIFolderSetupViewModel>();
                        var setupWindow = new ComfyUIFolderSetupWindow(setupViewModel);
                        var result = setupWindow.ShowDialog();

                        if (result != true)
                        {
                            System.Diagnostics.Debug.WriteLine("FlipPix App: User cancelled local setup. Shutting down application.");
                            Shutdown();
                            return;
                        }
                    }
                    else if (choiceWindow.IsRemoteSelected)
                    {
                        System.Diagnostics.Debug.WriteLine("FlipPix App: Remote server selected. Showing remote setup window.");
                        var remoteSetupWindow = new RemoteSetupWindow(settingsService);
                        var result = remoteSetupWindow.ShowDialog();

                        if (result != true)
                        {
                            System.Diagnostics.Debug.WriteLine("FlipPix App: User cancelled remote setup. Shutting down application.");
                            Shutdown();
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"FlipPix App: ERROR showing setup window: {ex}");
                    System.Windows.MessageBox.Show(
                        $"Error showing setup window:\n\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}",
                        "Setup Error",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error
                    );
                    Shutdown();
                    return;
                }

                System.Diagnostics.Debug.WriteLine("FlipPix App: Setup completed successfully. Proceeding to main window.");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"FlipPix App: ComfyUI folder already configured: {settingsService.Settings.ComfyUIFolderPath}");

                // Check server connectivity for existing installations
                await CheckServerConnectivityAsync(settingsService);
            }

            // Create and show Image Generator window as default
            try
            {
                System.Diagnostics.Debug.WriteLine("FlipPix App: Creating and showing Image Generator window as default");
                var imageGeneratorViewModel = _serviceProvider.GetRequiredService<ImageGeneratorViewModel>();
                var imageGeneratorWindow = new ImageGeneratorWindow(imageGeneratorViewModel, settingsService);
                System.Diagnostics.Debug.WriteLine("FlipPix App: ImageGeneratorWindow created successfully");

                // Set shutdown mode to close when main window closes
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                MainWindow = imageGeneratorWindow;

                imageGeneratorWindow.Show();
                System.Diagnostics.Debug.WriteLine("FlipPix App: Main Image Generator window shown successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FlipPix App: CRITICAL ERROR - Failed to create/show main window: {ex}");
                System.Windows.MessageBox.Show(
                    $"Failed to open main window:\n\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}",
                    "FlipPix Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error
                );
                Shutdown();
            }
        }

        private void ConfigureServices(IServiceCollection services)
        {
            // Logging
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
            });

            // HTTP Client with increased size limits
            services.AddHttpClient<ComfyUIHttpClient>(client =>
            {
                // Increase timeout for large file uploads
                client.Timeout = TimeSpan.FromMinutes(10);
                // Set max request content buffer size to 500MB
                client.MaxResponseContentBufferSize = 500 * 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // Allow large request bodies (500MB)
                MaxRequestContentBufferSize = 500 * 1024 * 1024
            });

            // Settings service
            services.AddSingleton<SettingsService>();

            // Core services
            services.AddSingleton<IAppLogger, FileLogger>();
            services.AddSingleton<VideoAnalysisService>();
            services.AddSingleton<ImageAnalysisService>();
            services.AddHttpClient<OllamaService>();

            // LMStudioService with dynamic URL from SettingsService
            services.AddSingleton<LMStudioService>(provider =>
            {
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient();
                var logger = provider.GetRequiredService<IAppLogger>();
                var settingsService = provider.GetRequiredService<SettingsService>();

                // Pass a function that dynamically retrieves the URL from settings
                return new LMStudioService(httpClient, logger, () => settingsService.Settings.LMStudioSettings?.BaseUrl ?? "http://localhost:1234");
            });

            // Prompt service
            services.AddSingleton<IPromptService, PromptService>();

            // ComfyUI configuration - use settings from SettingsService
            services.AddSingleton<ComfyUISettings>(provider =>
            {
                var settingsService = provider.GetRequiredService<SettingsService>();
                return settingsService.Settings;
            });
            
            // ComfyUI services
            services.AddSingleton<ComfyUIHttpClient>(provider =>
            {
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient(nameof(ComfyUIHttpClient));
                var logger = provider.GetRequiredService<IAppLogger>();
                var settings = provider.GetRequiredService<ComfyUISettings>();
                return new ComfyUIHttpClient(httpClient, logger, settings);
            });
            services.AddSingleton<ComfyUIWebSocketClient>(provider =>
            {
                var logger = provider.GetRequiredService<IAppLogger>();
                var settings = provider.GetRequiredService<ComfyUISettings>();
                return new ComfyUIWebSocketClient(logger, settings.BaseUrl);
            });
            services.AddSingleton<FlipPix.ComfyUI.Services.ComfyUIService>();

            // ViewModels
            services.AddTransient<FlipPixViewModel>(provider =>
            {
                var comfyUIService = provider.GetRequiredService<FlipPix.ComfyUI.Services.ComfyUIService>();
                var logger = provider.GetRequiredService<IAppLogger>();
                var settingsService = provider.GetRequiredService<SettingsService>();
                var promptService = provider.GetRequiredService<IPromptService>();
                return new FlipPixViewModel(comfyUIService, logger, settingsService, provider, promptService);
            });
            services.AddTransient<VideoGeneratorViewModel>(provider =>
            {
                var comfyUIService = provider.GetRequiredService<FlipPix.ComfyUI.Services.ComfyUIService>();
                var lmStudioService = provider.GetRequiredService<LMStudioService>();
                var logger = provider.GetRequiredService<IAppLogger>();
                var settingsService = provider.GetRequiredService<SettingsService>();
                return new VideoGeneratorViewModel(comfyUIService, lmStudioService, logger, settingsService, provider);
            });
            services.AddTransient<ImageGeneratorViewModel>(provider =>
            {
                var comfyUIService = provider.GetRequiredService<FlipPix.ComfyUI.Services.ComfyUIService>();
                var logger = provider.GetRequiredService<IAppLogger>();
                var settingsService = provider.GetRequiredService<SettingsService>();
                var promptService = provider.GetRequiredService<IPromptService>();
                return new ImageGeneratorViewModel(comfyUIService, logger, settingsService, provider, promptService);
            });
            services.AddTransient<ImageAnalyzerViewModel>(provider =>
            {
                var comfyUIService = provider.GetRequiredService<FlipPix.ComfyUI.Services.ComfyUIService>();
                var lmStudioService = provider.GetRequiredService<LMStudioService>();
                var logger = provider.GetRequiredService<IAppLogger>();
                var settingsService = provider.GetRequiredService<SettingsService>();
                return new ImageAnalyzerViewModel(comfyUIService, lmStudioService, logger, settingsService);
            });
            services.AddTransient<StoryVideoViewModel>(provider =>
            {
                var comfyUIService = provider.GetRequiredService<FlipPix.ComfyUI.Services.ComfyUIService>();
                var logger = provider.GetRequiredService<IAppLogger>();
                var settingsService = provider.GetRequiredService<SettingsService>();
                return new StoryVideoViewModel(comfyUIService, logger, settingsService);
            });
            services.AddTransient<OllamaViewModel>(provider =>
            {
                var ollamaService = provider.GetRequiredService<OllamaService>();
                var logger = provider.GetRequiredService<IAppLogger>();
                return new OllamaViewModel(ollamaService, logger, provider);
            });
            services.AddTransient<ComfyUIFolderSetupViewModel>();

            // Views
            services.AddTransient<FlipPixWindow>();
            services.AddTransient<VideoGeneratorWindow>();
            services.AddTransient<ImageGeneratorWindow>(provider =>
            {
                var viewModel = provider.GetRequiredService<ImageGeneratorViewModel>();
                var settingsService = provider.GetRequiredService<SettingsService>();
                return new ImageGeneratorWindow(viewModel, settingsService);
            });
            services.AddTransient<ImageAnalyzerWindow>();
            services.AddTransient<StoryVideoWindow>();
            services.AddTransient<OllamaWindow>();
            services.AddTransient<ComfyUIFolderSetupWindow>();
        }

        private async Task CheckServerConnectivityAsync(SettingsService settingsService)
        {
            try
            {
                var serverUrl = settingsService.Settings.BaseUrl;
                System.Diagnostics.Debug.WriteLine($"FlipPix App: Checking ComfyUI server connectivity at {serverUrl}");

                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(5);

                var response = await httpClient.GetAsync($"{serverUrl}/system_stats");

                if (response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine("FlipPix App: ComfyUI server connection successful");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"FlipPix App: ComfyUI server returned status {response.StatusCode}");

                    // Show message to user that server is not accessible
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        var result = System.Windows.MessageBox.Show(
                            $"ComfyUI server is not accessible at {serverUrl}\n\n" +
                            "Would you like to:\n" +
                            "• Click 'Yes' to reconfigure the server settings\n" +
                            "• Click 'No' to continue anyway (you may need to start ComfyUI manually)",
                            "ComfyUI Server Connection Failed",
                            System.Windows.MessageBoxButton.YesNo,
                            System.Windows.MessageBoxImage.Warning);

                        if (result == System.Windows.MessageBoxResult.Yes)
                        {
                            // Show server configuration dialog
                            if (_serviceProvider != null)
                            {
                                var setupViewModel = _serviceProvider.GetRequiredService<ComfyUIFolderSetupViewModel>();
                                var setupWindow = new ComfyUIFolderSetupWindow(setupViewModel);
                                setupWindow.ShowDialog();
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FlipPix App: Server connectivity check failed: {ex.Message}");

                // Show message to user that server is not accessible
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var result = System.Windows.MessageBox.Show(
                        $"Cannot connect to ComfyUI server at {settingsService.Settings.BaseUrl}\n\n" +
                        "Please ensure:\n" +
                        "• ComfyUI is running\n" +
                        "• The server address is correct\n" +
                        "• No firewall is blocking the connection\n\n" +
                        "Would you like to reconfigure the server settings?",
                        "ComfyUI Server Connection Failed",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Warning);

                    if (result == System.Windows.MessageBoxResult.Yes)
                    {
                        // Show server configuration dialog
                        if (_serviceProvider != null)
                        {
                            var setupViewModel = _serviceProvider.GetRequiredService<ComfyUIFolderSetupViewModel>();
                            var setupWindow = new ComfyUIFolderSetupWindow(setupViewModel);
                            setupWindow.ShowDialog();
                        }
                    }
                });
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _serviceProvider?.Dispose();
            base.OnExit(e);
        }
    }
}