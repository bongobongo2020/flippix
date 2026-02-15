# Task: Skip Local Process Management for Remote ComfyUI Servers

## 1. Context & Objective

**Problem:** When `BaseUrl` points to a remote ComfyUI server (not localhost), the video generator fails to send jobs. The WebSocket test button passes because it creates a standalone connection. But the actual job flow goes through `ComfyUIService.ConnectAsync()` and `DetectAndRestartIfCrashedAsync()`, both of which call `ComfyUIProcessManager` — which:

1. Checks `/system_stats` and `/object_info` with a **5-second timeout** — may time out over network
2. If `/object_info` times out, it thinks ComfyUI crashed and tries to **start it as a local process**
3. Starting locally fails (no local install) → throws exception → job never submitted

**The flow that fails (video generator single mode):**
- `VideoGeneratorMainViewModel.cs` line 1533: calls `DetectAndRestartIfCrashedAsync()` → tries local restart → returns false
- Line 1536-1539: Returns early with "ERROR: ComfyUI is not running"
- Even if that passed, line 1549: `ConnectAsync()` also goes through process management

**Fix:** Add remote server detection to `ComfyUIService`. For remote servers, skip all process management and only validate HTTP + WebSocket connectivity.

## 2. Files to Modify

### `FlipPix.ComfyUI/Services/ComfyUIService.cs`

This is the **only file** that needs changes.

## 3. Implementation Steps

### Step 1: Add `IsRemoteServer()` helper method

Add this private method to the `ComfyUIService` class (place it after the constructor, before `ConnectAsync`):

```csharp
/// <summary>
/// Determines if the configured ComfyUI server is remote (not localhost).
/// Remote servers skip local process management (start/restart/crash detection).
/// </summary>
private bool IsRemoteServer()
{
    try
    {
        var uri = new Uri(_settings.BaseUrl);
        var host = uri.Host;

        if (host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
    catch
    {
        return false; // Default to local behavior if URL parsing fails
    }
}
```

### Step 2: Modify `ConnectAsync()` (currently starts at line 43)

Wrap the entire process manager block (lines 50-88) in `if (!IsRemoteServer())`, and add an else branch that just logs:

**Replace the current method body** so it becomes:

```csharp
public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
{
    try
    {
        _logger.LogInfo("Connecting to ComfyUI service");

        if (IsRemoteServer())
        {
            _logger.LogInfo($"Remote ComfyUI server detected ({_settings.BaseUrl}), skipping local process management");
        }
        else
        {
            // Check if ComfyUI is running, and start it if not
            var isRunning = await _processManager.IsComfyUIRunningAsync(cancellationToken);
            if (!isRunning)
            {
                _logger.LogWarning("ComfyUI is not running. Attempting to start it automatically...");
                var started = await _processManager.StartComfyUIAsync(
                    status => _logger.LogInfo(status),
                    cancellationToken);

                if (!started)
                {
                    throw new ComfyUIConnectionException("Failed to start ComfyUI automatically. Please start ComfyUI manually.");
                }

                _logger.LogInfo("ComfyUI started successfully");
            }
            else
            {
                // Even if ComfyUI is "running", verify it's actually ready (not crashed/hung)
                _logger.LogInfo("ComfyUI is running, verifying it's ready...");
                var isReady = await _processManager.IsComfyUIReadyAsync(cancellationToken);

                if (!isReady)
                {
                    _logger.LogWarning("ComfyUI is running but not ready - may have crashed. Attempting restart...");
                    var started = await _processManager.StartComfyUIAsync(
                        status => _logger.LogInfo(status),
                        cancellationToken);

                    if (!started)
                    {
                        throw new ComfyUIConnectionException("ComfyUI is running but not ready. Failed to restart. Please restart ComfyUI manually.");
                    }

                    _logger.LogInfo("ComfyUI restarted successfully");
                }
                else
                {
                    _logger.LogInfo("ComfyUI verified to be ready");
                }
            }
        }

        // Test HTTP connection first
        var httpConnected = await RetryAsync(
            () => _httpClient.TestConnectionAsync(cancellationToken),
            _settings.MaxRetries,
            TimeSpan.FromMilliseconds(_settings.RetryDelayMilliseconds),
            cancellationToken);

        if (!httpConnected)
        {
            throw new ComfyUIConnectionException("Failed to establish HTTP connection to ComfyUI");
        }

        // Connect WebSocket
        await RetryAsync(
            () => _webSocketClient.ConnectAsync(_clientId, cancellationToken),
            _settings.MaxRetries,
            TimeSpan.FromMilliseconds(_settings.RetryDelayMilliseconds),
            cancellationToken);

        _logger.LogInfo("ComfyUI service connected successfully");
        return true;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to connect to ComfyUI service");
        throw;
    }
}
```

### Step 3: Modify `DetectAndRestartIfCrashedAsync()` (currently at line 145)

Replace the current method with remote-aware version:

```csharp
public async Task<bool> DetectAndRestartIfCrashedAsync(Action<string>? statusCallback = null, CancellationToken cancellationToken = default)
{
    if (IsRemoteServer())
    {
        _logger.LogInfo("Remote server detected, checking HTTP connectivity only...");
        statusCallback?.Invoke("Checking remote ComfyUI connectivity...");
        try
        {
            var connected = await _httpClient.TestConnectionAsync(cancellationToken);
            if (connected)
            {
                _logger.LogInfo("Remote ComfyUI is reachable");
                statusCallback?.Invoke("Remote ComfyUI is reachable");
                return true;
            }
            else
            {
                _logger.LogWarning("Cannot reach remote ComfyUI server");
                statusCallback?.Invoke("Cannot reach remote ComfyUI server. Please check the server is running.");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reach remote ComfyUI");
            statusCallback?.Invoke($"Cannot reach remote ComfyUI: {ex.Message}");
            return false;
        }
    }

    return await _processManager.DetectAndRestartComfyUIAsync(statusCallback, cancellationToken);
}
```

## 4. Completion Instructions
Update this file with a "Changelog" section detailing your changes for my review.

---

## Changelog

### Changes Made

**File: `FlipPix.ComfyUI/Services/ComfyUIService.cs`**

#### Step 1: Added `IsRemoteServer()` helper method (lines 44-67)
- Determines if the configured ComfyUI server is remote (not localhost)
- Checks host against `127.0.0.1`, `localhost`, and `0.0.0.0`
- Returns `false` (local behavior) on URL parsing failures

#### Step 2: Modified `ConnectAsync()` method (lines 69-148)
- Added `if (IsRemoteServer())` check before process management logic
- For remote servers: logs skip message and bypasses all `ComfyUIProcessManager` calls
- For local servers: retains original process management behavior
- HTTP and WebSocket connectivity testing remains unchanged for both cases

#### Step 3: Modified `DetectAndRestartIfCrashedAsync()` method (lines 178-223)
- Added remote server check at method entry
- For remote servers: only validates HTTP connectivity via `_httpClient.TestConnectionAsync()`
  - Returns `true` if reachable
  - Returns `false` with appropriate status callback messages if unreachable
- For local servers: delegates to `_processManager.DetectAndRestartComfyUIAsync()` as before

### Behavior Changes
- Remote ComfyUI servers now skip local process start/restart/crash detection
- Remote servers only require HTTP/WebSocket connectivity to function
- Local servers retain full process management capabilities

---

## Status: COMPLETED

**Build Status:** Succeeded (pre-existing warnings unrelated to this change)
