using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;

namespace FlipPix.ComfyUI.Services;

public class ComfyUIProcessManager
{
    private readonly IAppLogger _logger;
    private readonly ComfyUISettings _settings;
    private readonly HttpClient _httpClient;
    private Process? _comfyUIProcess;

    public ComfyUIProcessManager(IAppLogger logger, ComfyUISettings settings)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>
    /// Checks if ComfyUI is running and responsive
    /// </summary>
    public async Task<bool> IsComfyUIRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var baseUrl = _settings.BaseUrl;
            var testUrl = $"{baseUrl}/system_stats";

            _logger.LogInfo($"Checking if ComfyUI is running at {baseUrl}");

            // Use a shorter timeout for quick checks
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

            var response = await _httpClient.GetAsync(testUrl, linkedCts.Token);
            var isRunning = response.IsSuccessStatusCode;

            _logger.LogInfo($"ComfyUI running status: {isRunning}");
            return isRunning;
        }
        catch (Exception ex)
        {
            _logger.LogInfo($"ComfyUI is not running: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to detect ComfyUI process and determine if it has crashed
    /// </summary>
    public bool HasComfyUICrashed()
    {
        try
        {
            // Check if there's a python/comfyui process that might be hung
            var processes = Process.GetProcessesByName("python");
            var comfyuiProcesses = Process.GetProcessesByName("ComfyUI");

            // If we have processes but they're not responding, it might be crashed
            foreach (var process in processes)
            {
                try
                {
                    if (!process.Responding)
                    {
                        _logger.LogWarning($"Detected non-responsive Python process (PID: {process.Id})");
                        return true;
                    }
                }
                catch
                {
                    // Process may have exited
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Error checking for ComfyUI crash: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Starts ComfyUI using the configured restart script
    /// </summary>
    public async Task<bool> StartComfyUIAsync(Action<string>? statusCallback = null, CancellationToken cancellationToken = default)
    {
        if (!_settings.AutoRestartComfyUI)
        {
            _logger.LogInfo("Auto-restart is disabled in settings");
            return false;
        }

        if (string.IsNullOrEmpty(_settings.ComfyUIRestartScriptPath))
        {
            _logger.LogError("ComfyUI restart script path is not configured");
            return false;
        }

        if (!System.IO.File.Exists(_settings.ComfyUIRestartScriptPath))
        {
            _logger.LogError($"ComfyUI restart script not found: {_settings.ComfyUIRestartScriptPath}");
            return false;
        }

        try
        {
            statusCallback?.Invoke("Starting ComfyUI...");
            _logger.LogInfo($"Starting ComfyUI using script: {_settings.ComfyUIRestartScriptPath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = _settings.ComfyUIRestartScriptPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };

            _comfyUIProcess = Process.Start(startInfo);

            if (_comfyUIProcess == null)
            {
                _logger.LogError("Failed to start ComfyUI process");
                return false;
            }

            _logger.LogInfo($"ComfyUI process started (PID: {_comfyUIProcess.Id})");

            // Wait for ComfyUI to start
            statusCallback?.Invoke($"Waiting for ComfyUI to start (up to {_settings.ComfyUIStartupTimeoutSeconds} seconds)...");
            var isRunning = await WaitForComfyUIStartupAsync(cancellationToken);

            if (isRunning)
            {
                _logger.LogInfo("ComfyUI started successfully and is responding");
                return true;
            }
            else
            {
                _logger.LogError("ComfyUI process started but is not responding");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start ComfyUI");
            return false;
        }
    }

    /// <summary>
    /// Waits for ComfyUI to become responsive after starting
    /// </summary>
    private async Task<bool> WaitForComfyUIStartupAsync(CancellationToken cancellationToken = default)
    {
        var maxWaitTime = TimeSpan.FromSeconds(_settings.ComfyUIStartupTimeoutSeconds);
        var startTime = DateTime.Now;
        var checkInterval = TimeSpan.FromSeconds(2);

        _logger.LogInfo($"Waiting for ComfyUI to start (timeout: {maxWaitTime.TotalSeconds}s)");

        while (DateTime.Now - startTime < maxWaitTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isRunning = await IsComfyUIRunningAsync(cancellationToken);
            if (isRunning)
            {
                _logger.LogInfo("ComfyUI is now responding");
                return true;
            }

            var elapsed = (int)(DateTime.Now - startTime).TotalSeconds;
            _logger.LogDebug($"ComfyUI not yet responding... ({elapsed}s elapsed)");

            await Task.Delay(checkInterval, cancellationToken);
        }

        _logger.LogError($"Timeout waiting for ComfyUI to start (waited {maxWaitTime.TotalSeconds}s)");
        return false;
    }

    /// <summary>
    /// Attempts to restart ComfyUI if it has crashed
    /// </summary>
    public async Task<bool> DetectAndRestartComfyUIAsync(Action<string>? statusCallback = null, CancellationToken cancellationToken = default)
    {
        _logger.LogInfo("=== ComfyUI Crash Detection Started ===");
        _logger.LogInfo($"Auto-restart enabled: {_settings.AutoRestartComfyUI}");
        _logger.LogInfo($"Restart script path: {_settings.ComfyUIRestartScriptPath}");

        var isRunning = await IsComfyUIRunningAsync(cancellationToken);

        if (!isRunning || HasComfyUICrashed())
        {
            _logger.LogWarning("ComfyUI crash detected or not running!");
            statusCallback?.Invoke("ComfyUI crash detected!");

            if (!_settings.AutoRestartComfyUI)
            {
                _logger.LogInfo("Auto-restart is disabled, please restart ComfyUI manually");
                statusCallback?.Invoke("Auto-restart disabled. Please restart ComfyUI manually.");
                return false;
            }

            if (string.IsNullOrEmpty(_settings.ComfyUIRestartScriptPath))
            {
                _logger.LogError("ComfyUI restart script path is not configured!");
                statusCallback?.Invoke("Error: Restart script path not configured in settings.");
                return false;
            }

            if (!File.Exists(_settings.ComfyUIRestartScriptPath))
            {
                _logger.LogError($"Restart script not found: {_settings.ComfyUIRestartScriptPath}");
                statusCallback?.Invoke($"Error: Restart script not found at {_settings.ComfyUIRestartScriptPath}");
                return false;
            }

            // Wait before attempting restart
            _logger.LogInfo($"Waiting {_settings.ComfyUIRestartDelaySeconds} seconds before restart...");
            statusCallback?.Invoke($"Waiting {_settings.ComfyUIRestartDelaySeconds} seconds before restart...");
            await Task.Delay(TimeSpan.FromSeconds(_settings.ComfyUIRestartDelaySeconds), cancellationToken);

            // Kill any existing ComfyUI processes
            _logger.LogInfo("Killing existing ComfyUI processes...");
            await KillComfyUIProcessesAsync();

            // Start ComfyUI
            _logger.LogInfo($"Starting ComfyUI with script: {_settings.ComfyUIRestartScriptPath}");
            statusCallback?.Invoke("Restarting ComfyUI...");
            var started = await StartComfyUIAsync(statusCallback, cancellationToken);

            if (started)
            {
                statusCallback?.Invoke("ComfyUI restarted successfully!");
                _logger.LogInfo("ComfyUI restarted successfully after crash");
                return true;
            }
            else
            {
                statusCallback?.Invoke("Failed to restart ComfyUI");
                _logger.LogError("Failed to restart ComfyUI after crash");
                return false;
            }
        }

        _logger.LogInfo("ComfyUI is running normally, no restart needed");
        return true; // ComfyUI is running fine
    }

    /// <summary>
    /// Kills any existing ComfyUI processes to ensure clean restart
    /// </summary>
    private async Task KillComfyUIProcessesAsync()
    {
        try
        {
            _logger.LogInfo("Attempting to terminate existing ComfyUI processes...");

            // Kill python.exe processes that might be ComfyUI
            var pythonProcesses = Process.GetProcessesByName("python");
            var killed = 0;

            foreach (var process in pythonProcesses)
            {
                try
                {
                    // Check if this is a ComfyUI process by checking command line
                    var startTime = DateTime.Now - process.StartTime;

                    // Only kill recent python processes (less than 1 hour old) to avoid killing other Python apps
                    if (startTime.TotalMinutes < 60)
                    {
                        process.Kill();
                        killed++;
                        _logger.LogInfo($"Terminated Python process (PID: {process.Id})");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"Could not terminate process {process.Id}: {ex.Message}");
                }
            }

            if (killed > 0)
            {
                _logger.LogInfo($"Terminated {killed} Python process(es)");
                await Task.Delay(2000); // Give processes time to terminate
            }
            else
            {
                _logger.LogInfo("No ComfyUI processes found to terminate");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Error killing ComfyUI processes: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        _comfyUIProcess?.Dispose();
    }
}
