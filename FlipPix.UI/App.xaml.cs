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

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Configure services
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            // Check if ComfyUI folder is configured
            System.Diagnostics.Debug.WriteLine("FlipPix App: OnStartup - Checking if ComfyUI folder is configured");
            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
            if (!settingsService.IsComfyUIFolderConfigured())
            {
                System.Diagnostics.Debug.WriteLine("FlipPix App: ComfyUI folder not configured. Showing setup window.");
                var setupViewModel = _serviceProvider.GetRequiredService<ComfyUIFolderSetupViewModel>();
                var setupWindow = new ComfyUIFolderSetupWindow(setupViewModel);
                var result = setupWindow.ShowDialog();

                System.Diagnostics.Debug.WriteLine($"FlipPix App: Setup window closed with result: {result}");

                // If user cancelled, exit the application
                if (result != true)
                {
                    System.Diagnostics.Debug.WriteLine("FlipPix App: User cancelled setup. Shutting down application.");
                    Shutdown();
                    return;
                }

                System.Diagnostics.Debug.WriteLine("FlipPix App: Setup completed successfully. Proceeding to main window.");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"FlipPix App: ComfyUI folder already configured: {settingsService.Settings.ComfyUIFolderPath}");
            }

            // Create and show FlipPix window
            try
            {
                System.Diagnostics.Debug.WriteLine("FlipPix App: Creating and showing main FlipPix window");
                var flipPixWindow = _serviceProvider.GetRequiredService<FlipPixWindow>();
                System.Diagnostics.Debug.WriteLine("FlipPix App: FlipPixWindow created successfully");

                flipPixWindow.Show();
                System.Diagnostics.Debug.WriteLine("FlipPix App: Main window shown successfully");
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
            services.AddSingleton<WorkflowExecutionService>();
            services.AddSingleton<ChunkCreatorService>();

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
            services.AddSingleton<ComfyUIService>();

            // ViewModels
            services.AddTransient<MainViewModel>();
            services.AddTransient<ChunkCreatorViewModel>(provider =>
            {
                var comfyUIService = provider.GetRequiredService<ComfyUIService>();
                var logger = provider.GetRequiredService<IAppLogger>();
                var chunkCreatorService = provider.GetRequiredService<ChunkCreatorService>();
                var videoAnalysisService = provider.GetRequiredService<VideoAnalysisService>();
                var workflowExecutionService = provider.GetRequiredService<WorkflowExecutionService>();
                return new ChunkCreatorViewModel(comfyUIService, logger, chunkCreatorService, videoAnalysisService, workflowExecutionService);
            });
            services.AddTransient<LongCatViewModel>(provider =>
            {
                var comfyUIService = provider.GetRequiredService<ComfyUIService>();
                var logger = provider.GetRequiredService<IAppLogger>();
                var imageAnalysisService = provider.GetRequiredService<ImageAnalysisService>();
                return new LongCatViewModel(comfyUIService, logger, imageAnalysisService);
            });
            services.AddTransient<FlipPixViewModel>(provider =>
            {
                var comfyUIService = provider.GetRequiredService<ComfyUIService>();
                var logger = provider.GetRequiredService<IAppLogger>();
                var settingsService = provider.GetRequiredService<SettingsService>();
                return new FlipPixViewModel(comfyUIService, logger, settingsService, provider);
            });
            services.AddTransient<VideoGeneratorViewModel>(provider =>
            {
                var comfyUIService = provider.GetRequiredService<ComfyUIService>();
                var logger = provider.GetRequiredService<IAppLogger>();
                var settingsService = provider.GetRequiredService<SettingsService>();
                return new VideoGeneratorViewModel(comfyUIService, logger, settingsService);
            });
            services.AddTransient<ComfyUIFolderSetupViewModel>();

            // Views
            services.AddTransient<MainWindow>();
            services.AddTransient<ChunkCreatorWindow>();
            services.AddTransient<LongCatWindow>();
            services.AddTransient<FlipPixWindow>();
            services.AddTransient<VideoGeneratorWindow>();
            services.AddTransient<ComfyUIFolderSetupWindow>();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _serviceProvider?.Dispose();
            base.OnExit(e);
        }
    }
}