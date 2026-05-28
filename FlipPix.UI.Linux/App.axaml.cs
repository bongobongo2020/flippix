using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FlipPix.UI.Linux.ViewModels;
using FlipPix.UI.Linux.ViewModels.Video;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Services;
using FlipPix.UI.Linux.Services;
using FlipPix.ComfyUI.Http;
using FlipPix.ComfyUI.WebSocket;
using FlipPix.Core.Models;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using ButtonEnum = MessageBox.Avalonia.Enums.ButtonEnum;
using ButtonResult = MsBox.Avalonia.Enums.ButtonResult;
using FlipPix.UI.Linux.Windows;

namespace FlipPix.UI.Linux;

public partial class App : Application
{
    private static readonly CancellationTokenSource _shutdownCts = new();
    public static CancellationToken ShutdownToken => _shutdownCts.Token;
    private ServiceProvider? _serviceProvider;
    public static ServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
        Services = _serviceProvider;

        var logger = _serviceProvider.GetRequiredService<IAppLogger>();
        var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
        settingsService.SetLogger(logger);

        logger.LogInfo("FlipPix Linux starting up");

        // Start startup flow asynchronously
        desktop.MainWindow = new SplashWindow();
        desktop.MainWindow.Show();

        Task.Run(async () =>
        {
            await StartupFlowAsync(desktop, settingsService, logger);
        });

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartupFlowAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SettingsService settingsService,
        IAppLogger logger)
    {
        try
        {
            if (!settingsService.IsComfyUIFolderConfigured())
            {
                logger.LogInfo("ComfyUI not configured. Showing setup.");
                bool setupDone = false;

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var setupWin = new SetupChoiceWindow();
                    desktop.MainWindow = setupWin;
                    setupWin.Show();

                    setupWin.Closed += (_, _) =>
                    {
                        setupDone = setupWin.UserConfirmed;
                        if (!setupWin.UserConfirmed)
                        {
                            desktop.Shutdown();
                        }
                        else
                        {
                            _ = ShowMainWindowAsync(desktop);
                        }
                    };
                });
            }
            else
            {
                await CheckServerConnectivityAsync(settingsService, logger);
                await ShowMainWindowAsync(desktop);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during startup");
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var box = MessageBoxManager.GetMessageBoxStandard(
                    "Startup Error",
                    $"FlipPix failed to start:\n{ex.Message}",
                    ButtonEnum.Ok, Icon.Error);
                await box.ShowAsync();
                desktop.Shutdown();
            });
        }
    }

    private async Task ShowMainWindowAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_serviceProvider == null) return;
            var imageGeneratorViewModel = _serviceProvider.GetRequiredService<ImageGeneratorViewModel>();
            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
            var windowPositionService = _serviceProvider.GetRequiredService<WindowPositionService>();
            var mainWindow = new Windows.ImageGeneratorWindow(imageGeneratorViewModel, settingsService, windowPositionService);
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
        });
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));

        services.AddHttpClient<ComfyUIHttpClient>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
            client.MaxResponseContentBufferSize = 500 * 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            MaxRequestContentBufferSize = 500 * 1024 * 1024
        });

        services.AddSingleton<SettingsService>();
        services.AddSingleton<IAppLogger, FileLogger>();
        services.AddSingleton<VideoAnalysisService>();
        services.AddSingleton<ImageAnalysisService>();
        services.AddHttpClient<OllamaService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<WindowPositionService>();
        services.AddSingleton<LoraManager>();
        services.AddSingleton<ComfyUIImageRetriever>();

        services.AddSingleton<LMStudioService>(provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient();
            var logger = provider.GetRequiredService<IAppLogger>();
            var settingsService = provider.GetRequiredService<SettingsService>();
            return new LMStudioService(httpClient, logger, () => settingsService.Settings.LMStudioSettings?.BaseUrl ?? "http://localhost:8080");
        });

        services.AddSingleton<IPromptService, PromptService>();
        services.AddSingleton<ComfyUISettings>(p => p.GetRequiredService<SettingsService>().Settings);
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
        services.AddSingleton<WorkflowQueueCoordinator>();

        // ViewModels
        services.AddTransient<FlipPixViewModel>(provider =>
        {
            var comfyUIService = provider.GetRequiredService<FlipPix.ComfyUI.Services.ComfyUIService>();
            var logger = provider.GetRequiredService<IAppLogger>();
            var settingsService = provider.GetRequiredService<SettingsService>();
            var promptService = provider.GetRequiredService<IPromptService>();
            var fileDialogService = provider.GetRequiredService<IFileDialogService>();
            return new FlipPixViewModel(comfyUIService, logger, settingsService, provider, promptService, fileDialogService);
        });
        services.AddTransient<VideoGeneratorViewModel>(provider =>
        {
            var comfyUIService = provider.GetRequiredService<FlipPix.ComfyUI.Services.ComfyUIService>();
            var lmStudioService = provider.GetRequiredService<LMStudioService>();
            var logger = provider.GetRequiredService<IAppLogger>();
            var settingsService = provider.GetRequiredService<SettingsService>();
            var fileDialogService = provider.GetRequiredService<IFileDialogService>();
            return new VideoGeneratorViewModel(comfyUIService, lmStudioService, logger, settingsService, provider, fileDialogService);
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
            var workflowCoordinator = provider.GetRequiredService<WorkflowQueueCoordinator>();
            var fileDialogService = provider.GetRequiredService<IFileDialogService>();
            var promptService = provider.GetRequiredService<IPromptService>();
            return new ImageAnalyzerViewModel(comfyUIService, lmStudioService, logger, settingsService, workflowCoordinator, fileDialogService, promptService);
        });
        services.AddTransient<StoryVideoViewModel>(provider =>
        {
            var comfyUIService = provider.GetRequiredService<FlipPix.ComfyUI.Services.ComfyUIService>();
            var logger = provider.GetRequiredService<IAppLogger>();
            var settingsService = provider.GetRequiredService<SettingsService>();
            var fileDialogService = provider.GetRequiredService<IFileDialogService>();
            return new StoryVideoViewModel(comfyUIService, logger, settingsService, fileDialogService);
        });
        services.AddTransient<OllamaViewModel>(provider =>
        {
            var ollamaService = provider.GetRequiredService<OllamaService>();
            var logger = provider.GetRequiredService<IAppLogger>();
            return new OllamaViewModel(ollamaService, logger, provider);
        });
        services.AddTransient<ComfyUIFolderSetupViewModel>(provider =>
        {
            var settingsService = provider.GetRequiredService<SettingsService>();
            var fileDialogService = provider.GetRequiredService<IFileDialogService>();
            return new ComfyUIFolderSetupViewModel(settingsService, fileDialogService);
        });
        services.AddTransient<I2V2AViewModel>(provider =>
        {
            var comfyUIService = provider.GetRequiredService<FlipPix.ComfyUI.Services.ComfyUIService>();
            var logger = provider.GetRequiredService<IAppLogger>();
            var settingsService = provider.GetRequiredService<SettingsService>();
            var fileDialogService = provider.GetRequiredService<IFileDialogService>();
            return new I2V2AViewModel(comfyUIService, logger, settingsService, provider, fileDialogService);
        });
        services.AddTransient<VideoEnhanceViewModel>(provider =>
        {
            var comfyUIService = provider.GetRequiredService<FlipPix.ComfyUI.Services.ComfyUIService>();
            var logger = provider.GetRequiredService<IAppLogger>();
            var settingsService = provider.GetRequiredService<SettingsService>();
            var fileDialogService = provider.GetRequiredService<IFileDialogService>();
            var workflowCoordinator = provider.GetRequiredService<WorkflowQueueCoordinator>();
            return new VideoEnhanceViewModel(comfyUIService, logger, settingsService, provider, workflowCoordinator, fileDialogService);
        });
    }

    private async Task CheckServerConnectivityAsync(SettingsService settingsService, IAppLogger logger)
    {
        var settings = settingsService.Settings;
        var serverUrl = settings.BaseUrl;

        try
        {
            logger.LogInfo($"Checking ComfyUI server connectivity at {serverUrl}");
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync($"{serverUrl}/system_stats");
            if (response.IsSuccessStatusCode)
            {
                logger.LogInfo("ComfyUI server connection successful");
                return;
            }
            logger.LogWarning($"ComfyUI server returned status {response.StatusCode}");
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Server connectivity check failed: {ex.Message}");
        }

        if (settings.AutoRestartComfyUI
            && !string.IsNullOrEmpty(settings.ComfyUIRestartScriptPath)
            && System.IO.File.Exists(settings.ComfyUIRestartScriptPath))
        {
            var processManager = new ComfyUIProcessManager(logger, settings);
            try
            {
                var started = await processManager.StartComfyUIAsync(
                    status => logger.LogInfo($"[AutoStart] {status}"),
                    _shutdownCts.Token);
                if (started)
                {
                    logger.LogInfo("ComfyUI auto-started successfully");
                    return;
                }
                logger.LogWarning("ComfyUI auto-start failed");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during ComfyUI auto-start");
            }
        }

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var box = MessageBoxManager.GetMessageBoxStandard(
                "ComfyUI Server Connection Failed",
                $"Cannot connect to ComfyUI server at {serverUrl}\n\nPlease ensure ComfyUI is running.",
                ButtonEnum.Ok, Icon.Warning);
            await box.ShowAsync();
        });
    }
}
