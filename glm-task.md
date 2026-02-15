# Task: Fix Remote Connection & WebSocket Reconnection

## 1. Context & Objective
Two critical issues when using FlipPix with a remote ComfyUI server:

**Problem A:** Network settings in the Settings window show wrong values when connecting from a remote machine. Multiple fallback references to `localhost`/`127.0.0.1` scattered across the codebase mean that if settings fail to load or any property is null, the app silently uses localhost instead of the configured remote server.

**Problem B:** Video generation works once then stops. The WebSocket client (`ComfyUIWebSocketClient.cs`) has **zero reconnection logic**. When the WebSocket drops after the first job completes (common over remote networks), subsequent jobs submit via HTTP fine, but the app never receives the `execution_complete` event. It hangs until the 10-minute fallback timer fires.

## 2. Files to Modify

### File 1: `FlipPix.ComfyUI/WebSocket/ComfyUIWebSocketClient.cs`
**Add automatic WebSocket reconnection with exponential backoff.**

Changes needed:
- Add fields: `_clientId` (string), `_maxReconnectAttempts` (int, default 10), `_reconnectDelayMs` (int, default 2000), `_isReconnecting` (bool)
- In `ConnectAsync`: Store `clientId` in `_clientId` for reconnection use
- Add a new method `ReconnectAsync()` that:
  - Sets `_isReconnecting = true`
  - Fires `ConnectionStatusChanged` with "Reconnecting"
  - Attempts to create a new `ClientWebSocket`, connect, and restart listening
  - Uses exponential backoff: `delay = _reconnectDelayMs * 2^attempt` capped at 30 seconds
  - On success: fires `ConnectionStatusChanged` with "Reconnected", sets `_isReconnecting = false`
  - On failure after all attempts: fires `ConnectionStatusChanged` with "Failed", sets `_isReconnecting = false`
- Modify `ListenForMessagesAsync`:
  - **Line 96-98 (server close)**: Instead of just returning, call `_ = Task.Run(() => ReconnectAsync())` and then return
  - **Lines 117-120 (exception handler)**: Instead of just logging error, call `_ = Task.Run(() => ReconnectAsync())` and then return (but NOT on `OperationCanceledException` — that should remain as-is since it means intentional disconnect)
- Add a `EnsureConnectedAsync()` public method that checks `IsConnected` and if not, attempts reconnection. This can be called before submitting new workflows.

### File 2: `FlipPix.ComfyUI/Services/ComfyUIService.cs`
**Use reconnection before workflow execution.**

Changes needed:
- In `ExecuteWorkflowAsync` (line ~297), before submitting the workflow, add a WebSocket health check:
  ```csharp
  // Ensure WebSocket is connected before executing workflow
  if (!_webSocketClient.IsConnected)
  {
      _logger.LogWarning("WebSocket not connected, attempting reconnection before workflow execution");
      await _webSocketClient.EnsureConnectedAsync(cancellationToken);
  }
  ```
- In `QueuePromptAsync`, add the same WebSocket health check before submitting.

### File 3: `FlipPix.Core/Models/ComfyUISettings.cs`
**No changes needed** — the defaults here are fine as initial defaults. The real issue is the fallback pattern in ViewModels.

### File 4: All ViewModels with `?? "http://127.0.0.1:8188"` fallback pattern
**Search and fix all instances.** These are in various ViewModels throughout `FlipPix.UI/ViewModels/`.

For each instance, change the fallback pattern from:
```csharp
var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
```
to:
```csharp
var baseUrl = _settingsService.Settings?.BaseUrl ?? _settingsService.LoadSettings().BaseUrl;
```

This ensures the fallback re-reads from the saved settings file rather than defaulting to localhost. If you cannot do this cleanly (e.g., circular dependency), then at minimum log a warning when the fallback is triggered so the user/developer can see that settings weren't loaded properly.

**Search pattern to find all instances:** `grep -rn "127.0.0.1:8188" FlipPix.UI/` and `grep -rn "localhost:8188" FlipPix.UI/`

### File 5: `FlipPix.UI/Services/ComfyUIImageRetriever.cs`
**Lines 274**: Same fallback fix as above — the `?? "http://127.0.0.1:8188"` at line 274 should use the settings-based fallback instead of hardcoded localhost.

## 3. Implementation Steps

1. **Implement WebSocket reconnection** in `ComfyUIWebSocketClient.cs` (File 1) — this is the most critical fix
2. **Add pre-execution WebSocket health check** in `ComfyUIService.cs` (File 2)
3. **Fix all localhost fallback patterns** across ViewModels and services (Files 4 & 5) — search comprehensively with grep
4. **Test compilation** — ensure all changes compile cleanly

## 4. Priority
The WebSocket reconnection (steps 1-2) is the highest priority — it directly fixes the "video works once then stops" issue. The fallback fixes (steps 3-4) fix the settings display issue.

## 5. Changelog

### Changes Implemented:

#### File 1: `FlipPix.ComfyUI/WebSocket/ComfyUIWebSocketClient.cs`
- Added fields: `_clientId` (string), `_maxReconnectAttempts` (const int = 10), `_reconnectDelayMs` (const int = 2000), `_isReconnecting` (bool)
- Modified `ConnectAsync` to store `clientId` in `_clientId` field
- Added `ReconnectAsync()` method with:
  - Exponential backoff (delay = 2000ms * 2^(attempt-1), capped at 30 seconds)
  - Automatic cleanup and recreation of WebSocket connection
  - Status events for "Reconnecting", "Reconnected", and "Failed"
- Added `EnsureConnectedAsync()` public method for manual health checks
- Modified `ListenForMessagesAsync`:
  - Server close (line 186-190): Now triggers `ReconnectAsync()` before returning
  - Exception handler (line 208-213): Now triggers `ReconnectAsync()` before returning (except for `OperationCanceledException`)

#### File 2: `FlipPix.ComfyUI/Services/ComfyUIService.cs`
- Added WebSocket health check in `ExecuteWorkflowAsync` before workflow submission
- Added WebSocket health check in `QueuePromptAsync` before prompt submission
- Both checks log a warning and call `EnsureConnectedAsync()` when disconnected

#### File 3: `FlipPix.UI/ViewModels/Video/VideoProcessingBaseViewModel.cs`
- Updated `GetComfyUIBaseUrl()` method to use settings-based fallback with logging

#### File 4: Multiple ViewModels (localhost fallback fixes)
All ViewModels that used `?? "http://127.0.0.1:8188"` pattern now:
1. First check `_settingsService.Settings?.BaseUrl`
2. If null/empty, reload settings via `_settingsService.LoadSettings().BaseUrl`
3. If still null/empty, log warning and use default `http://127.0.0.1:8188`

Files modified:
- `FlipPix.UI/ViewModels/AmateurGeneratorViewModel.cs`
- `FlipPix.UI/ViewModels/CameraAngleViewModel.cs`
- `FlipPix.UI/ViewModels/FlipPixViewModel.cs`
- `FlipPix.UI/ViewModels/ImageAnalyzerViewModel.cs` (5 instances)
- `FlipPix.UI/ViewModels/ImageGeneratorViewModel.cs`
- `FlipPix.UI/ViewModels/StoryImageGeneratorAmateurViewModel.cs`
- `FlipPix.UI/ViewModels/StoryImageGeneratorFViewModel.cs`
- `FlipPix.UI/ViewModels/StoryImageGeneratorQViewModel.cs`
- `FlipPix.UI/ViewModels/StoryImageGeneratorViewModel.cs`
- `FlipPix.UI/ViewModels/StoryVideoViewModel.cs`

#### File 5: `FlipPix.UI/Services/ComfyUIImageRetriever.cs`
- Fixed 2 instances of localhost fallback in `GetOutputImagesAsync` method
- Fixed `IsComfyUIRemote` method to reload settings instead of defaulting to localhost

### Build Status:
- Compilation successful (1 pre-existing warning unrelated to these changes)

### Summary:
Both critical issues are now addressed:
1. **Problem A (Remote connection settings)**: Fixed by replacing hardcoded localhost fallbacks with settings-based fallbacks that reload from the saved settings file
2. **Problem B (Video works once then stops)**: Fixed by adding automatic WebSocket reconnection with exponential backoff and pre-execution health checks
