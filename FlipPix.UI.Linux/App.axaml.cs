using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FlipPix.ComfyUI.Http;
using FlipPix.ComfyUI.Services;
using FlipPix.ComfyUI.WebSocket;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.Core.Services;
using FlipPix.UI.Linux.Windows;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels;
using FlipPix.UI.ViewModels.Video;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace FlipPix.UI.Linux;

/// <summary>
/// Startup mirrors FlipPix.UI/App.xaml.cs: DI with the same registrations, first-run setup
/// (local ComfyUI folder or remote server), a connectivity check with auto-start, then the Image
/// Generator as the main window. Linux additions: a splash while services spin up, the
/// Windows-tool shims (ffmpeg.exe etc.) the shared view models look for, and a smoke-test hook.
/// </summary>
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

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log(e.ExceptionObject as Exception, "Unhandled domain exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log(e.Exception, "Unhandled UI exception");
            ShowError("FlipPix Crash", $"Unhandled UI Exception:\n\n{e.Exception.Message}\n\nStack trace:\n{e.Exception.StackTrace}");
            e.Handled = true; // keep the app alive so the user can see the error, as the WPF app does
        };

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();
        Services = _serviceProvider;

        var logger = _serviceProvider.GetRequiredService<IAppLogger>();
        var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
        settingsService.SetLogger(logger);

        try
        {
            var httpClient = _serviceProvider.GetRequiredService<ComfyUIHttpClient>();
            httpClient.MissingModelResolver = _serviceProvider.GetRequiredService<IMissingModelResolver>();
            httpClient.MissingNodeResolver = _serviceProvider.GetRequiredService<IMissingNodeResolver>();
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Could not wire missing-model/node resolver: {ex.Message}");
        }

        VramContext.Configure(settingsService.Settings.VramTier, settingsService.Settings.DetectedVramGb);
        logger.LogInfo($"VRAM tier: {VramContext.EffectiveTier} (setting={settingsService.Settings.VramTier}, detected={settingsService.Settings.DetectedVramGb:0.#} GB)");

        logger.LogInfo($"FlipPix (Linux) starting up — {Environment.OSVersion}, {(DesktopIntegration.IsChromeOS ? "ChromeOS Linux container" : "desktop Linux")}");
        ConfigureMediaTooling(logger);
        Controls.Menus.Initialize();
        WindowsToolShims.Install(logger);

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.Exit += (_, _) =>
        {
            PhoneRemote.Stop();
            _shutdownCts.Cancel();
        };

        var splash = new SplashWindow();
        desktop.MainWindow = splash;
        splash.Show();

        Dispatcher.UIThread.Post(async () => await StartupFlowAsync(desktop, settingsService, logger, splash));

        base.OnFrameworkInitializationCompleted();
    }

    private void Log(Exception? ex, string what)
    {
        try
        {
            var logger = _serviceProvider?.GetService<IAppLogger>();
            if (ex != null) logger?.LogError(ex, what);
        }
        catch { }
    }

    private static void ShowError(string caption, string message) =>
        System.Windows.MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Error);

    private async Task StartupFlowAsync(
        IClassicDesktopStyleApplicationLifetime desktop, SettingsService settingsService, IAppLogger logger, Window splash)
    {
        try
        {
            logger.LogInfo("OnStartup - Checking if ComfyUI is configured");
            if (!settingsService.IsComfyUIFolderConfigured())
            {
                var detected = settingsService.TryAutoDetectComfyUIFolder();
                if (detected != null && settingsService.ValidateAndSetComfyUIFolder(detected))
                    logger.LogInfo($"Auto-configured ComfyUI folder: {detected}");
            }

            if (!settingsService.IsComfyUIFolderConfigured())
            {
                logger.LogInfo("ComfyUI not configured. Showing choice window.");
                splash.Hide();

                var choice = new SetupChoiceWindow();
                if (await ShowModalAsync(choice) != true)
                {
                    logger.LogInfo("User cancelled choice. Shutting down application.");
                    desktop.Shutdown();
                    return;
                }

                if (choice.IsLocalSelected)
                {
                    var setupViewModel = _serviceProvider!.GetRequiredService<ComfyUIFolderSetupViewModel>();
                    if (await ShowModalAsync(new ComfyUIFolderSetupWindow(setupViewModel)) != true)
                    {
                        desktop.Shutdown();
                        return;
                    }
                }
                else if (choice.IsRemoteSelected)
                {
                    if (await ShowModalAsync(new RemoteSetupWindow(settingsService)) != true)
                    {
                        desktop.Shutdown();
                        return;
                    }
                }
                logger.LogInfo("Setup completed successfully. Proceeding to main window.");
            }
            else
            {
                logger.LogInfo($"ComfyUI folder already configured: {settingsService.Settings.ComfyUIFolderPath}");
                await CheckServerConnectivityAsync(settingsService, logger);
            }

            EnsureLocalWorkingOutputFolder(settingsService, logger);
            ShowMainWindow(desktop, settingsService, logger);
            splash.Close();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during startup");
            splash.Close();
            ShowError("FlipPix Error", $"FlipPix failed to start:\n\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}");
            desktop.Shutdown();
        }
    }

    /// <summary>Shows a setup window on its own (there is no owner yet) and waits for its result.</summary>
    private static Task<bool?> ShowModalAsync(Window window)
    {
        var tcs = new TaskCompletionSource<bool?>();
        window.Closed += (_, _) => tcs.TrySetResult((window as DialogWindow)?.DialogResult);
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        return tcs.Task;
    }

    private void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop, SettingsService settingsService, IAppLogger logger)
    {
        logger.LogInfo("Creating and showing Image Generator window as default");
        var imageGeneratorViewModel = _serviceProvider!.GetRequiredService<ImageGeneratorViewModel>();
        var windowPositionService = _serviceProvider!.GetRequiredService<WindowPositionService>();
        var mainWindow = new ImageGeneratorWindow(imageGeneratorViewModel, settingsService, windowPositionService);

        if (VramContext.IsLowVram)
        {
            var vramNote = VramContext.DetectedVramGb > 0 ? $"16 GB mode ({VramContext.DetectedVramGb:0.#} GB)" : "16 GB mode";
            mainWindow.Title += $"  —  {vramNote}";
        }

        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        // Closing the Image Generator ends the app, as on Windows (OnMainWindowClose).
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        logger.LogInfo("Main Image Generator window shown successfully");

        PhoneRemote.Start(settingsService, logger);

        if (Environment.GetEnvironmentVariable("FLIPPIX_SMOKE") == "1")
            RunSmokeTest(desktop, mainWindow, logger);
    }

    /// <summary>
    /// FLIPPIX_SMOKE=1: opens every window and tab once, logs the tab census, then exits. A XAML,
    /// binding or resource error anywhere throws here and lands in the log instead of waiting for
    /// someone to click through.
    /// </summary>
    private void RunSmokeTest(IClassicDesktopStyleApplicationLifetime desktop, Window mainWindow, IAppLogger logger)
    {
        async Task Run()
        {
            try
            {
                await WalkTabsAsync(mainWindow, "ImageGeneratorWindow", logger);

                var video = _serviceProvider!.GetRequiredService<VideoGeneratorWindow>();
                video.Show();
                await WalkTabsAsync(video, "VideoGeneratorWindow", logger);

                // Playback: load a clip into a real player, play, seek, and record what happened.
                if (Environment.GetEnvironmentVariable("FLIPPIX_SMOKE_VIDEO") is { Length: > 0 } clip)
                {
                    var player = video.GetLogicalDescendants().OfType<Controls.VideoPreview>()
                        .FirstOrDefault(p => p.Name == "H3EditorVideoPlayer");
                    var tabs = video.GetLogicalDescendants().OfType<TabControl>().First();
                    tabs.SelectedIndex = 5;
                    await Task.Delay(400);
                    player ??= video.GetLogicalDescendants().OfType<Controls.VideoPreview>().FirstOrDefault(p => p.Name == "H3EditorVideoPlayer");
                    if (player == null) logger.LogWarning("SMOKE: video player not found");
                    else
                    {
                        var opened = false;
                        player.MediaOpened += (_, _) => opened = true;
                        player.IsVisible = true;
                        player.Source = clip;
                        await Task.Delay(2500);
                        SaveShot(video, "Playback-1-opened");
                        var t0 = player.Position;
                        player.Play();
                        await Task.Delay(1500);
                        var t1 = player.Position;
                        player.Pause();
                        player.Position = TimeSpan.FromSeconds(3);
                        await Task.Delay(800);
                        SaveShot(video, "Playback-2-seek3s");
                        logger.LogInfo($"SMOKE: playback libvlc={Controls.VideoPreview.PlaybackAvailable} opened={opened} " +
                                       $"duration={(player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : -1):0.00}s " +
                                       $"size={player.NaturalVideoWidth}x{player.NaturalVideoHeight} pos {t0.TotalSeconds:0.00}s→{t1.TotalSeconds:0.00}s after 1.5s play, after seek {player.Position.TotalSeconds:0.00}s");
                        player.Source = null;
                    }
                }

                var enhance = _serviceProvider!.GetRequiredService<VideoEnhanceWindow>();
                enhance.Show();
                await WalkTabsAsync(enhance, "VideoEnhanceWindow", logger);

                var settings = _serviceProvider!.GetRequiredService<SettingsService>();
                var dialogs = new (string Name, Window Window)[]
                {
                    ("Settings", new SettingsWindow(settings)),
                    ("SetupChoice", new SetupChoiceWindow()),
                    ("RemoteSetup", new RemoteSetupWindow(settings)),
                    ("ComfyUIFolderSetup", new ComfyUIFolderSetupWindow(_serviceProvider!.GetRequiredService<ComfyUIFolderSetupViewModel>())),
                    ("PhoneRemote", new PhoneRemoteWindow()),
                    ("MissingModels", new MissingModelsWindow(new List<MissingModelInfo> { new() { Name = "test/smoke_model.safetensors", Category = "loras" } },
                        _serviceProvider!.GetRequiredService<ModelInstallerService>(), logger)),
                    ("MissingNodes", new MissingNodesWindow(new List<MissingNodeInfo> { new() { ClassType = "SmokeTestNode", PackName = "smoke-pack", RepoUrl = "" } },
                        _serviceProvider!.GetRequiredService<NodeInstallerService>(), logger)),
                };
                foreach (var (dialogName, dialog) in dialogs)
                {
                    dialog.Show();
                    await Task.Delay(400);
                    SaveShot(dialog, "Dialog-" + dialogName);
                    dialog.Close();
                }
                logger.LogInfo("SMOKE: all windows constructed OK");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SMOKE: failed");
            }
            finally
            {
                await Task.Delay(2000);
                desktop.Shutdown();
            }
        }
        Dispatcher.UIThread.Post(() => _ = Run(), DispatcherPriority.Background);
    }

    private static async Task WalkTabsAsync(Window window, string name, IAppLogger logger)
    {
        await Task.Delay(500);
        foreach (var tabs in window.GetLogicalDescendants().OfType<TabControl>().ToList())
        {
            var headers = tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString()).ToList();
            logger.LogInfo($"SMOKE: {name} tabs={headers.Count}: {string.Join(" | ", headers)}");
            for (var i = 0; i < tabs.ItemCount; i++)
            {
                tabs.SelectedIndex = i;
                await Task.Delay(250);
                SaveShot(window, $"{name}-{i:00}");
            }
            tabs.SelectedIndex = 0;
        }
    }

    /// <summary>FLIPPIX_SMOKE_SHOTS=dir: a PNG of the window as it stands, for checking layouts.</summary>
    private static void SaveShot(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("FLIPPIX_SMOKE_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            Directory.CreateDirectory(dir);
            var size = new PixelSize((int)Math.Max(1, window.Bounds.Width), (int)Math.Max(1, window.Bounds.Height));
            using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
            rtb.Render(window);
            rtb.Save(Path.Combine(dir, name + ".png"));
        }
        catch (Exception ex)
        {
            Services?.GetService<IAppLogger>()?.LogWarning($"SMOKE: screenshot {name} failed: {ex.Message}");
        }
    }

    /// <summary>Points FFMpegCore at this machine's ffmpeg/ffprobe.</summary>
    private static void ConfigureMediaTooling(IAppLogger logger)
    {
        var binaryFolder = MediaTools.BinaryFolder;
        if (binaryFolder is null)
        {
            logger.LogWarning("ffmpeg was not found on PATH. Install it (sudo apt install ffmpeg) "
                            + "or set FLIPPIX_FFMPEG; video features stay disabled until then.");
            return;
        }
        FFMpegCore.GlobalFFOptions.Configure(options => options.BinaryFolder = binaryFolder);
        logger.LogInfo($"Using ffmpeg from {binaryFolder}");
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
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<WindowPositionService>();
        services.AddSingleton<LoraManager>();
        services.AddSingleton<ComfyUIImageRetriever>();
        services.AddSingleton<ModelInstallerService>();
        services.AddSingleton<IMissingModelResolver, MissingModelResolver>();
        services.AddSingleton<NodeInstallerService>();
        services.AddSingleton<IMissingNodeResolver, MissingNodeResolver>();
        services.AddHttpClient<OllamaService>();

        services.AddSingleton<LMStudioService>(provider =>
        {
            var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient();
            var logger = provider.GetRequiredService<IAppLogger>();
            var settingsService = provider.GetRequiredService<SettingsService>();
            return new LMStudioService(
                httpClient,
                logger,
                () => settingsService.Settings.LMStudioSettings?.BaseUrl ?? "http://alien:8080",
                () => settingsService.Settings.LMStudioSettings);
        });

        services.AddSingleton<IPromptService, PromptService>();
        services.AddSingleton<ComfyUISettings>(p => p.GetRequiredService<SettingsService>().Settings);
        services.AddSingleton<ComfyUIHttpClient>(provider =>
        {
            var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ComfyUIHttpClient));
            return new ComfyUIHttpClient(httpClient, provider.GetRequiredService<IAppLogger>(), provider.GetRequiredService<ComfyUISettings>());
        });
        services.AddSingleton<ComfyUIWebSocketClient>(provider =>
            new ComfyUIWebSocketClient(provider.GetRequiredService<IAppLogger>(), provider.GetRequiredService<ComfyUISettings>().BaseUrl));
        services.AddSingleton<FlipPix.ComfyUI.Services.ComfyUIService>();
        services.AddSingleton<WorkflowQueueCoordinator>();

        // View models — the same factories as the WPF app.
        services.AddTransient<VideoGeneratorViewModel>(provider => new VideoGeneratorViewModel(
            provider.GetRequiredService<FlipPix.ComfyUI.Services.ComfyUIService>(),
            provider.GetRequiredService<LMStudioService>(),
            provider.GetRequiredService<IAppLogger>(),
            provider.GetRequiredService<SettingsService>(),
            provider,
            provider.GetRequiredService<IFileDialogService>()));
        services.AddTransient<ImageGeneratorViewModel>(provider => new ImageGeneratorViewModel(
            provider.GetRequiredService<FlipPix.ComfyUI.Services.ComfyUIService>(),
            provider.GetRequiredService<IAppLogger>(),
            provider.GetRequiredService<SettingsService>(),
            provider,
            provider.GetRequiredService<IPromptService>()));
        services.AddTransient<ComfyUIFolderSetupViewModel>(provider => new ComfyUIFolderSetupViewModel(
            provider.GetRequiredService<SettingsService>(),
            provider.GetRequiredService<IFileDialogService>()));
        services.AddTransient<VideoEnhanceViewModel>(provider => new VideoEnhanceViewModel(
            provider.GetRequiredService<FlipPix.ComfyUI.Services.ComfyUIService>(),
            provider.GetRequiredService<IAppLogger>(),
            provider.GetRequiredService<SettingsService>(),
            provider,
            provider.GetRequiredService<WorkflowQueueCoordinator>(),
            provider.GetRequiredService<IFileDialogService>()));

        // Windows — singleton Video Generator (closing hides it), as on Windows.
        services.AddSingleton<VideoGeneratorWindow>(provider => new VideoGeneratorWindow(
            provider.GetRequiredService<VideoGeneratorViewModel>(),
            provider.GetRequiredService<WindowPositionService>()));
        services.AddTransient<ImageGeneratorWindow>(provider => new ImageGeneratorWindow(
            provider.GetRequiredService<ImageGeneratorViewModel>(),
            provider.GetRequiredService<SettingsService>(),
            provider.GetRequiredService<WindowPositionService>()));
        services.AddTransient<VideoEnhanceWindow>(provider => new VideoEnhanceWindow(
            provider.GetRequiredService<VideoEnhanceViewModel>(),
            provider.GetRequiredService<WindowPositionService>()));
    }

    /// <summary>
    /// With a remote ComfyUI, <see cref="ComfyUISettings.OutputFolderPath"/> is where the tabs save
    /// what they download (OutputFolderPath/&lt;Tab&gt;/…) and later list it from. Pointed at the
    /// network share — easy to do on a Chromebook, where both folder fields get the same path —
    /// every save and gallery refresh crosses SMB-over-9p, which takes seconds per directory.
    /// Move it to a local folder; the share stays configured as RemoteOutputFolderPath.
    /// </summary>
    private static void EnsureLocalWorkingOutputFolder(SettingsService settingsService, IAppLogger logger)
    {
        var settings = settingsService.Settings;
        if (!IsRemoteServer(settings.BaseUrl)) return;

        var current = settings.OutputFolderPath;
        if (!string.IsNullOrWhiteSpace(current) && !IsOnNetworkShare(current)
            && !SameFolder(current, settings.RemoteOutputFolderPath))
            return;

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlipPix", "output");
        try
        {
            Directory.CreateDirectory(local);
            settings.OutputFolderPath = local;
            settingsService.SaveSettings(settings);
            logger.LogInfo($"Remote ComfyUI: working output folder moved from '{current}' to local '{local}'");
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Could not set up local working output folder {local}: {ex.Message}");
        }
    }

    // Only string and symlink inspection here: probing the share itself can block for seconds.
    private static string Canonical(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('/');
        try
        {
            var target = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            if (target != null) full = target.FullName.TrimEnd('/');
        }
        catch { /* not a link, or unreadable: compare as typed */ }
        return full;
    }

    private static bool SameFolder(string a, string? b) =>
        !string.IsNullOrWhiteSpace(b) && Canonical(a) == Canonical(b);

    private static bool IsOnNetworkShare(string path)
    {
        var p = Canonical(path);
        return p.StartsWith("/mnt/chromeos/", StringComparison.Ordinal)
            || p.StartsWith("/mnt/shared/", StringComparison.Ordinal);
    }

    private static bool IsRemoteServer(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && uri.Host is not ("127.0.0.1" or "localhost" or "::1" or "[::1]");

    private static double TryParseVramGb(string systemStatsJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(systemStatsJson);
        if (!doc.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != System.Text.Json.JsonValueKind.Array)
            return 0;
        long maxBytes = 0;
        foreach (var device in devices.EnumerateArray())
            if (device.TryGetProperty("vram_total", out var vram) && vram.TryGetInt64(out var bytes) && bytes > maxBytes)
                maxBytes = bytes;
        return maxBytes > 0 ? maxBytes / 1024.0 / 1024.0 / 1024.0 : 0;
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
                try
                {
                    var vramGb = TryParseVramGb(await response.Content.ReadAsStringAsync());
                    if (vramGb > 0)
                    {
                        VramContext.Configure(settings.VramTier, vramGb);
                        logger.LogInfo($"Detected {vramGb:0.#} GB VRAM from /system_stats — tier: {VramContext.EffectiveTier}");
                        if (Math.Abs(settings.DetectedVramGb - vramGb) > 0.1)
                        {
                            settings.DetectedVramGb = vramGb;
                            settingsService.SaveSettings(settings);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Could not parse VRAM from /system_stats: {ex.Message}");
                }
                return;
            }
            logger.LogWarning($"ComfyUI server returned status {response.StatusCode}");
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Server connectivity check failed: {ex.Message}");
        }

        if (settings.AutoRestartComfyUI)
            settingsService.EnsureRestartScriptConfigured();

        if (settings.AutoRestartComfyUI
            && !string.IsNullOrEmpty(settings.ComfyUIRestartScriptPath)
            && File.Exists(settings.ComfyUIRestartScriptPath))
        {
            logger.LogInfo($"ComfyUI not running. Auto-starting using: {settings.ComfyUIRestartScriptPath}");
            var processManager = new ComfyUIProcessManager(logger, settings);
            try
            {
                if (await processManager.StartComfyUIAsync(status => logger.LogInfo($"[AutoStart] {status}"), _shutdownCts.Token))
                {
                    logger.LogInfo("ComfyUI auto-started successfully");
                    return;
                }
                logger.LogWarning("ComfyUI auto-start failed — falling back to manual dialog");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during ComfyUI auto-start");
            }
        }

        var result = System.Windows.MessageBox.Show(
            $"Cannot connect to ComfyUI server at {serverUrl}\n\n" +
            "Please ensure:\n" +
            "• ComfyUI is running\n" +
            "• The server address is correct\n" +
            "• No firewall is blocking the connection\n\n" +
            "Would you like to reconfigure the server settings?",
            "ComfyUI Server Connection Failed",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes && _serviceProvider != null)
        {
            // A remote server (the usual Chromebook setup) is fixed in the remote dialog; a local
            // install in the folder dialog — the same choice the first-run flow offers.
            if (IsRemoteServer(settingsService.Settings.BaseUrl))
                await ShowModalAsync(new RemoteSetupWindow(settingsService));
            else
                await ShowModalAsync(new ComfyUIFolderSetupWindow(_serviceProvider.GetRequiredService<ComfyUIFolderSetupViewModel>()));
        }
    }
}
