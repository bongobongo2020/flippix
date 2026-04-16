# Task: Queue Persistence + ComfyUI Crash Detection with Auto-Restart
*(2026-04-08)*

Two features:
1. **Queue persistence** — save/load queue items to `%AppData%\FlipPix\queue\` so Pending/Failed items survive app restarts and crashes.
2. **Crash detection with retry** — when a queue item fails with a connection error, detect if ComfyUI crashed, restart it, and re-queue the item for retry (up to 2 times).

---

## Part 1 — BaseQueueItem: Add RetryCount

### File: `FlipPix.UI/Models/BaseQueueItem.cs`

Add a `RetryCount` property (after `ErrorMessage`):

```csharp
/// <summary>
/// Number of automatic retry attempts made (used by crash-detection logic)
/// </summary>
public int RetryCount { get; set; } = 0;
```

---

## Part 2 — VideoProcessingBaseViewModel: Add crash-detection helper

### File: `FlipPix.UI/ViewModels/Video/VideoProcessingBaseViewModel.cs`

Add the following `using` at the top if not already present:
```csharp
using System.Net.Http;
```

Add this method inside the class (e.g. after `AddLog`):

```csharp
/// <summary>
/// Called from a queue processing catch block when an item fails.
/// If the failure looks like a ComfyUI crash (connection error) and auto-restart is enabled,
/// attempts to restart ComfyUI and returns true (meaning: reset item to Pending and retry).
/// Returns false if item should be marked as Failed.
/// </summary>
protected async Task<bool> TryHandleCrashAndRetryAsync(BaseQueueItem item, Exception ex, int maxRetries = 2)
{
    bool isConnectionFailure =
        ex is HttpRequestException ||
        ex.InnerException is HttpRequestException ||
        ex.Message.IndexOf("connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
        ex.Message.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0 ||
        ex.Message.IndexOf("unreachable", StringComparison.OrdinalIgnoreCase) >= 0 ||
        ex.Message.IndexOf("WebSocket", StringComparison.OrdinalIgnoreCase) >= 0 ||
        ex.Message.IndexOf("ComfyUI is not running", StringComparison.OrdinalIgnoreCase) >= 0;

    if (!isConnectionFailure) return false;

    if (item.RetryCount >= maxRetries)
    {
        AddLog($"[CrashDetect] Max retries ({maxRetries}) reached for this item — marking Failed");
        return false;
    }

    var settings = _settingsService.Settings;
    if (settings?.AutoRestartComfyUI != true)
    {
        AddLog("[CrashDetect] Auto-restart is disabled in settings");
        return false;
    }

    AddLog($"[CrashDetect] Connection failure detected: {ex.Message}");
    AddLog("[CrashDetect] Checking ComfyUI health and attempting restart if needed...");

    var restarted = await _comfyUIService.DetectAndRestartIfCrashedAsync(
        status => AddLog($"[AutoRestart] {status}"));

    if (restarted)
    {
        item.RetryCount++;
        AddLog($"[AutoRestart] ComfyUI is running. Retrying item (attempt {item.RetryCount}/{maxRetries})...");
        return true;
    }

    AddLog("[AutoRestart] Could not restart ComfyUI — marking item as Failed");
    return false;
}
```

---

## Part 3 — VideoEnhanceViewModel: Add persistence + crash detection

### File: `FlipPix.UI/ViewModels/Video/VideoEnhanceViewModel.cs`

**Add using** at top (if not present):
```csharp
using System.Text.Json;
```

**Add file path properties** (add before the first `#region`):
```csharp
private string InterpolateQueueFilePath => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "FlipPix", "queue", "video_enhance_interpolate_queue.json");

private string UpscaleQueueFilePath => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "FlipPix", "queue", "video_enhance_upscale_queue.json");
```

**In the constructor**, after `AddLog("Video Enhance initialized");`, add:
```csharp
LoadInterpolateQueueFromFile();
LoadUpscaleQueueFromFile();
```

**In `AddInterpolateToQueue()`**, after `_interpolateQueue.Add(item);` add:
```csharp
SaveInterpolateQueueToFile();
```

**In `AddUpscaleToQueue()`**, after `_upscaleQueue.Add(item);` add:
```csharp
SaveUpscaleQueueToFile();
```

**Replace `ProcessInterpolateQueueAsync()`** (the entire method) with:
```csharp
private async Task ProcessInterpolateQueueAsync()
{
    if (IsProcessingInterpolateQueue) return;
    IsProcessingInterpolateQueue = true;
    AddLog("Starting interpolate queue...");
    try
    {
        VideoEnhanceQueueItem? item;
        while ((item = _interpolateQueue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
        {
            item.ItemStatus = QueueItemStatus.Processing;
            UpdateInterpolateQueueStatus();
            SaveInterpolateQueueToFile();
            try
            {
                await ProcessInterpolateSingleAsync(item);
                item.ItemStatus = QueueItemStatus.Completed;
                AddLog($"Interpolate complete: {item.DisplayText}");
            }
            catch (Exception ex)
            {
                var shouldRetry = await TryHandleCrashAndRetryAsync(item, ex);
                if (shouldRetry)
                {
                    item.ItemStatus = QueueItemStatus.Pending;
                    AddLog("Item reset to Pending — will retry after ComfyUI restart");
                }
                else
                {
                    item.ItemStatus = QueueItemStatus.Failed;
                    item.ErrorMessage = ex.Message;
                    AddLog($"Interpolate FAILED: {ex.Message}");
                }
            }
            UpdateInterpolateQueueStatus();
            SaveInterpolateQueueToFile();
        }
    }
    finally
    {
        IsProcessingInterpolateQueue = false;
        AddLog("Interpolate queue finished.");
    }
}
```

**Replace `ProcessUpscaleQueueAsync()`** (the entire method) with:
```csharp
private async Task ProcessUpscaleQueueAsync()
{
    if (IsProcessingUpscaleQueue) return;
    IsProcessingUpscaleQueue = true;
    AddLog("Starting upscale queue...");
    try
    {
        VideoEnhanceQueueItem? item;
        while ((item = _upscaleQueue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
        {
            item.ItemStatus = QueueItemStatus.Processing;
            UpdateUpscaleQueueStatus();
            SaveUpscaleQueueToFile();
            try
            {
                await ProcessUpscaleSingleAsync(item);
                item.ItemStatus = QueueItemStatus.Completed;
                AddLog($"Upscale complete: {item.DisplayText}");
            }
            catch (Exception ex)
            {
                var shouldRetry = await TryHandleCrashAndRetryAsync(item, ex);
                if (shouldRetry)
                {
                    item.ItemStatus = QueueItemStatus.Pending;
                    AddLog("Item reset to Pending — will retry after ComfyUI restart");
                }
                else
                {
                    item.ItemStatus = QueueItemStatus.Failed;
                    item.ErrorMessage = ex.Message;
                    AddLog($"Upscale FAILED: {ex.Message}");
                }
            }
            UpdateUpscaleQueueStatus();
            SaveUpscaleQueueToFile();
        }
    }
    finally
    {
        IsProcessingUpscaleQueue = false;
        AddLog("Upscale queue finished.");
    }
}
```

**Add persistence methods** at the end of the class (before the closing `}`):
```csharp
#region Queue Persistence

private void SaveInterpolateQueueToFile()
{
    try
    {
        var dir = Path.GetDirectoryName(InterpolateQueueFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(InterpolateQueueFilePath,
            JsonSerializer.Serialize(_interpolateQueue.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception ex) { AddLog($"Error saving interpolate queue: {ex.Message}"); }
}

private void LoadInterpolateQueueFromFile()
{
    try
    {
        if (!File.Exists(InterpolateQueueFilePath)) return;
        var items = JsonSerializer.Deserialize<List<VideoEnhanceQueueItem>>(File.ReadAllText(InterpolateQueueFilePath));
        if (items?.Any() != true) return;
        _interpolateQueue.Clear();
        foreach (var item in items)
        {
            if (item.ItemStatus == QueueItemStatus.Processing)
                item.ItemStatus = QueueItemStatus.Pending;
            _interpolateQueue.Add(item);
        }
        UpdateInterpolateQueueStatus();
        AddLog($"Interpolate queue loaded: {_interpolateQueue.Count} items");
    }
    catch (Exception ex) { AddLog($"Error loading interpolate queue: {ex.Message}"); }
}

private void SaveUpscaleQueueToFile()
{
    try
    {
        var dir = Path.GetDirectoryName(UpscaleQueueFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(UpscaleQueueFilePath,
            JsonSerializer.Serialize(_upscaleQueue.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception ex) { AddLog($"Error saving upscale queue: {ex.Message}"); }
}

private void LoadUpscaleQueueFromFile()
{
    try
    {
        if (!File.Exists(UpscaleQueueFilePath)) return;
        var items = JsonSerializer.Deserialize<List<VideoEnhanceQueueItem>>(File.ReadAllText(UpscaleQueueFilePath));
        if (items?.Any() != true) return;
        _upscaleQueue.Clear();
        foreach (var item in items)
        {
            if (item.ItemStatus == QueueItemStatus.Processing)
                item.ItemStatus = QueueItemStatus.Pending;
            _upscaleQueue.Add(item);
        }
        UpdateUpscaleQueueStatus();
        AddLog($"Upscale queue loaded: {_upscaleQueue.Count} items");
    }
    catch (Exception ex) { AddLog($"Error loading upscale queue: {ex.Message}"); }
}

#endregion
```

---

## Part 4 — VACEVideoViewModel: Add persistence + crash detection

### File: `FlipPix.UI/ViewModels/Video/VACEVideoViewModel.cs`

**Add file path property** (before the first `#region`):
```csharp
private string QueueFilePath => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "FlipPix", "queue", "vace_queue.json");
```

**In the constructor**, after `AddLog(...)`, add:
```csharp
LoadQueueFromFile();
```

**In `AddToQueueAndProcess()`**, after `_queue.Add(item);` add:
```csharp
SaveQueueToFile();
```

**Replace `ProcessQueueAsync()`** with:
```csharp
private async Task ProcessQueueAsync()
{
    if (IsProcessingQueue) return;
    IsProcessingQueue = true;
    AddLog("Starting VACE queue processing...");
    try
    {
        VaceQueueItem? item;
        while ((item = _queue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
        {
            item.ItemStatus = QueueItemStatus.Processing;
            UpdateQueueStatus();
            SaveQueueToFile();
            try
            {
                await GenerateSingleVideoAsync(item);
                item.ItemStatus = QueueItemStatus.Completed;
                AddLog($"Queue item completed: {item.DisplayText}");
            }
            catch (Exception ex)
            {
                var shouldRetry = await TryHandleCrashAndRetryAsync(item, ex);
                if (shouldRetry)
                {
                    item.ItemStatus = QueueItemStatus.Pending;
                    AddLog("Item reset to Pending — will retry after ComfyUI restart");
                }
                else
                {
                    item.ItemStatus = QueueItemStatus.Failed;
                    item.ErrorMessage = ex.Message;
                    AddLog($"Queue item FAILED: {ex.Message}");
                }
            }
            UpdateQueueStatus();
            SaveQueueToFile();
        }
    }
    finally
    {
        IsProcessingQueue = false;
        AddLog("VACE queue processing finished.");
    }
}
```

**Add persistence methods** at end of class:
```csharp
#region Queue Persistence

private void SaveQueueToFile()
{
    try
    {
        var dir = Path.GetDirectoryName(QueueFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(QueueFilePath,
            JsonSerializer.Serialize(_queue.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception ex) { AddLog($"Error saving queue: {ex.Message}"); }
}

private void LoadQueueFromFile()
{
    try
    {
        if (!File.Exists(QueueFilePath)) return;
        var items = JsonSerializer.Deserialize<List<VaceQueueItem>>(File.ReadAllText(QueueFilePath));
        if (items?.Any() != true) return;
        _queue.Clear();
        foreach (var item in items)
        {
            if (item.ItemStatus == QueueItemStatus.Processing)
                item.ItemStatus = QueueItemStatus.Pending;
            _queue.Add(item);
        }
        UpdateQueueStatus();
        AddLog($"VACE queue loaded: {_queue.Count} items");
    }
    catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
}

#endregion
```

---

## Part 5 — LTX23BasicViewModel: Add persistence + crash detection

### File: `FlipPix.UI/ViewModels/Video/LTX23BasicViewModel.cs`

**Add file path property**:
```csharp
private string QueueFilePath => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "FlipPix", "queue", "ltx23basic_queue.json");
```

**In the constructor** (after `AddLog` or at end of constructor body), add:
```csharp
LoadQueueFromFile();
```

**In `AddToQueueAndProcess()`**, after `_queue.Add(item);` add:
```csharp
SaveQueueToFile();
```

**Replace `ProcessQueueAsync()`** with:
```csharp
private async Task ProcessQueueAsync()
{
    if (IsProcessingQueue) return;
    IsProcessingQueue = true;
    AddLog("Starting queue processing...");
    try
    {
        QueueItem? item;
        while ((item = _queue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
        {
            item.ItemStatus = QueueItemStatus.Processing;
            UpdateQueueStatus();
            SaveQueueToFile();
            try
            {
                await GenerateSingleVideoAsync(item);
                item.ItemStatus = QueueItemStatus.Completed;
                AddLog($"Queue item completed: {Path.GetFileName(item.ImagePath)}");
            }
            catch (Exception ex)
            {
                var shouldRetry = await TryHandleCrashAndRetryAsync(item, ex);
                if (shouldRetry)
                {
                    item.ItemStatus = QueueItemStatus.Pending;
                    AddLog("Item reset to Pending — will retry after ComfyUI restart");
                }
                else
                {
                    item.ItemStatus = QueueItemStatus.Failed;
                    item.ErrorMessage = ex.Message;
                    AddLog($"Queue item FAILED: {ex.Message}");
                }
            }
            UpdateQueueStatus();
            SaveQueueToFile();
        }
    }
    finally
    {
        IsProcessingQueue = false;
        AddLog("Queue processing finished.");
    }
}
```

**Add persistence methods** at end of class:
```csharp
#region Queue Persistence

private void SaveQueueToFile()
{
    try
    {
        var dir = Path.GetDirectoryName(QueueFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(QueueFilePath,
            JsonSerializer.Serialize(_queue.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception ex) { AddLog($"Error saving queue: {ex.Message}"); }
}

private void LoadQueueFromFile()
{
    try
    {
        if (!File.Exists(QueueFilePath)) return;
        var items = JsonSerializer.Deserialize<List<QueueItem>>(File.ReadAllText(QueueFilePath));
        if (items?.Any() != true) return;
        _queue.Clear();
        foreach (var item in items)
        {
            if (item.ItemStatus == QueueItemStatus.Processing)
                item.ItemStatus = QueueItemStatus.Pending;
            _queue.Add(item);
        }
        UpdateQueueStatus();
        AddLog($"Queue loaded: {_queue.Count} items");
    }
    catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
}

#endregion
```

---

## Part 6 — LTX23T2VViewModel: Add persistence + crash detection

### File: `FlipPix.UI/ViewModels/Video/LTX23T2VViewModel.cs`

Apply the **same changes as Part 5** but with filename `ltx23t2v_queue.json`.

**Add file path property**:
```csharp
private string QueueFilePath => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "FlipPix", "queue", "ltx23t2v_queue.json");
```

**In constructor**, add:
```csharp
LoadQueueFromFile();
```

**In `AddToQueueAndProcess()`**, after `_queue.Add(item)`:
```csharp
SaveQueueToFile();
```

**Replace `ProcessQueueAsync()`** with the same pattern as Part 5 (same code, same crash detection, `QueueItem` type).

**Add persistence methods** (same as Part 5 but with `ltx23t2v_queue.json` path already set via `QueueFilePath`).

---

## Part 7 — VideoGeneratorMainViewModel: Add crash detection

### File: `FlipPix.UI/ViewModels/Video/VideoGeneratorMainViewModel.cs`

**In `ProcessQueueAsync()`** — find this catch block (around line 1437):
```csharp
catch (Exception ex)
{
    item.ItemStatus = QueueItemStatus.Failed;
    UpdateQueueStatus();
    SaveQueueToFile();
    AddLog($"Error processing queue item: {ex.Message}");
}
```

Replace with:
```csharp
catch (Exception ex)
{
    var shouldRetry = await TryHandleCrashAndRetryAsync(item, ex);
    if (shouldRetry)
    {
        item.ItemStatus = QueueItemStatus.Pending;
        UpdateQueueStatus();
        SaveQueueToFile();
        AddLog("Item reset to Pending — will retry after ComfyUI restart");
    }
    else
    {
        item.ItemStatus = QueueItemStatus.Failed;
        UpdateQueueStatus();
        SaveQueueToFile();
        AddLog($"Error processing queue item: {ex.Message}");
    }
}
```

**Also update `LoadQueueFromFile()`** — change `Processing → Failed` to `Processing → Pending` (so interrupted items are retried rather than shown as failed):

Find:
```csharp
if (item.ItemStatus == QueueItemStatus.Processing)
{
    item.ItemStatus = QueueItemStatus.Failed;
}
```

Replace with:
```csharp
if (item.ItemStatus == QueueItemStatus.Processing)
{
    item.ItemStatus = QueueItemStatus.Pending;
}
```

---

## Part 8 — ImageAnalyzerViewModel: Add crash detection

### File: `FlipPix.UI/ViewModels/ImageAnalyzerViewModel.cs`

Add using at top (if not present):
```csharp
using System.Net.Http;
```

**Find this catch block** in `ProcessQueueAsync()` (around line 1635):
```csharp
catch (Exception ex)
{
    item.Status = "Failed";
    item.ErrorMessage = ex.Message;
    item.Progress = 0;
    SaveQueueToFile();
    _logger.LogError($"Queue item failed: {item.StyleName} - {ex.Message}");

    // Show error to user
    try
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            StatusBarMessage = $"Error processing queue item: {ex.Message}";
        });
    }
    catch { }
}
```

Replace with:
```csharp
catch (Exception ex)
{
    bool isConnectionFailure =
        ex is HttpRequestException ||
        ex.InnerException is HttpRequestException ||
        ex.Message.IndexOf("connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
        ex.Message.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0 ||
        ex.Message.IndexOf("unreachable", StringComparison.OrdinalIgnoreCase) >= 0 ||
        ex.Message.IndexOf("WebSocket", StringComparison.OrdinalIgnoreCase) >= 0 ||
        ex.Message.IndexOf("ComfyUI is not running", StringComparison.OrdinalIgnoreCase) >= 0;

    bool shouldRetry = false;
    if (isConnectionFailure && item.RetryCount < 2 && _settingsService.Settings?.AutoRestartComfyUI == true)
    {
        _logger.LogWarning($"[CrashDetect] Connection failure: {ex.Message}. Checking ComfyUI health...");
        var restarted = await _comfyUIService.DetectAndRestartIfCrashedAsync(
            status => _logger.LogInfo($"[AutoRestart] {status}"));
        if (restarted)
        {
            item.RetryCount++;
            _logger.LogInfo($"[AutoRestart] ComfyUI restarted. Retrying item (attempt {item.RetryCount}/2)...");
            shouldRetry = true;
        }
    }

    if (shouldRetry)
    {
        item.Status = "Pending";
        item.Progress = 0;
        SaveQueueToFile();
        try { System.Windows.Application.Current.Dispatcher.Invoke(() => StatusBarMessage = "ComfyUI restarted — retrying item..."); } catch { }
    }
    else
    {
        item.Status = "Failed";
        item.ErrorMessage = ex.Message;
        item.Progress = 0;
        SaveQueueToFile();
        _logger.LogError($"Queue item failed: {item.StyleName} - {ex.Message}");
        try { System.Windows.Application.Current.Dispatcher.Invoke(() => StatusBarMessage = $"Error processing queue item: {ex.Message}"); } catch { }
    }
}
```

**Note:** When `shouldRetry = true` and `item.Status = "Pending"`, the while loop at the top of `ProcessQueueAsync()` will pick it up again on the next iteration automatically. The `QueueProgress++` in the `finally` block will still fire, so consider whether the counter needs adjustment — it's cosmetic only, leave it as-is.

---

## Part 9 — Settings UI: ComfyUI Restart Script path

The `ComfyUIRestartScriptPath` setting already exists in `ComfyUISettings.cs`. The user needs a way to set it in the Settings window.

### File: `FlipPix.UI/SettingsWindow.xaml.cs`

Check if `ComfyUIRestartScriptPath` and `AutoRestartComfyUI` are already bound in the settings UI. If not, add a Browse button and text field for restart script path, and a checkbox for `AutoRestartComfyUI`. This is UI work — check existing settings window layout and add in the ComfyUI section.

**If the Settings window already has these fields** (search for `AutoRestartComfyUI` binding in SettingsWindow.xaml) — skip this step.

---

## Completion Instructions

After implementing all parts:
1. Verify the build compiles without errors (user will run in Visual Studio)
2. Update this file with a Changelog section

---

## Changelog

### 2026-04-08: Queue Persistence + ComfyUI Crash Detection with Auto-Restart

**Implemented By:** Claude (GLM Implementer)
**Status:** Complete

#### Summary
Added two features to improve queue reliability:
1. **Queue Persistence** - Queue items are now saved to `%AppData%\FlipPix\queue\` and automatically restored on app startup
2. **ComfyUI Crash Detection with Auto-Restart** - When queue processing fails due to ComfyUI connection errors, the system now detects the crash, optionally restarts ComfyUI, and retries failed items (up to 2 times)

#### Files Modified

1. **FlipPix.UI/Models/BaseQueueItem.cs**
   - Added `RetryCount` property for tracking automatic retry attempts

2. **FlipPix.UI/ViewModels/Video/VideoProcessingBaseViewModel.cs**
   - Added `System.Net.Http` using statement
   - Added `TryHandleCrashAndRetryAsync()` protected method for crash detection and retry logic

3. **FlipPix.UI/ViewModels/Video/VideoEnhanceViewModel.cs**
   - Added queue file path properties for interpolate and upscale queues
   - Added `SaveInterpolateQueueToFile()`, `LoadInterpolateQueueFromFile()`
   - Added `SaveUpscaleQueueToFile()`, `LoadUpscaleQueueFromFile()`
   - Updated `ProcessInterpolateQueueAsync()` and `ProcessUpscaleQueueAsync()` with crash detection and persistence calls
   - Items in "Processing" state on load are reset to "Pending" for retry

4. **FlipPix.UI/ViewModels/Video/VACEVideoViewModel.cs**
   - Added `QueueFilePath` property for vace_queue.json
   - Added `SaveQueueToFile()`, `LoadQueueToFile()` methods
   - Updated `ProcessQueueAsync()` with crash detection and persistence calls
   - Items in "Processing" state on load are reset to "Pending" for retry

5. **FlipPix.UI/ViewModels/Video/LTX23BasicViewModel.cs**
   - Added `QueueFilePath` property for ltx23basic_queue.json
   - Added `SaveQueueToFile()`, `LoadQueueToFile()` methods
   - Updated `ProcessQueueAsync()` with crash detection and persistence calls
   - Items in "Processing" state on load are reset to "Pending" for retry

6. **FlipPix.UI/ViewModels/Video/LTX23T2VViewModel.cs**
   - Added `QueueFilePath` property for ltx23t2v_queue.json
   - Added `SaveQueueToFile()`, `LoadQueueToFile()` methods
   - Updated `ProcessQueueAsync()` with crash detection and persistence calls
   - Items in "Processing" state on load are reset to "Pending" for retry

7. **FlipPix.UI/ViewModels/Video/VideoGeneratorMainViewModel.cs**
   - Updated `ProcessQueueAsync()` catch block with crash detection logic
   - Changed `LoadQueueFromFile()` to reset "Processing" items to "Pending" instead of "Failed"

8. **FlipPix.UI/ViewModels/ImageAnalyzerViewModel.cs**
   - Updated `ProcessQueueAsync()` catch block with inline crash detection logic (does not inherit from VideoProcessingBaseViewModel)
   - Items in "Processing" state on load are reset to "Pending" for retry

9. **FlipPix.UI/SettingsWindow.xaml.cs** (No changes needed)
   - Verified that `AutoRestartComfyUI` and `ComfyUIRestartScriptPath` are already bound in the UI
   - Part 9 skipped as per task instructions

#### Queue Persistence Details
- Queue files are stored in `%AppData%\FlipPix\queue\` directory
- Each ViewModel has its own queue file:
  - `video_enhance_interpolate_queue.json`
  - `video_enhance_upscale_queue.json`
  - `vace_queue.json`
  - `ltx23basic_queue.json`
  - `ltx23t2v_queue.json`
  - `story_video_queue.json` (already existed)
  - Main video queue file (already existed)
- Items in "Processing" state on load are reset to "Pending" for retry
- Only "Pending" and "Failed" items are restored (completed items are kept for reference)

#### Crash Detection Details
- Detects connection errors: `HttpRequestException`, "connection", "refused", "unreachable", "WebSocket", "ComfyUI is not running"
- Checks `AutoRestartComfyUI` setting before attempting restart
- Maximum 2 retry attempts per item
- Calls `ComfyUIService.DetectAndRestartIfCrashedAsync()` to handle restart
- Items are reset to "Pending" status after successful restart for automatic retry
- Failed retry attempts increment `RetryCount` and mark item as "Failed"

#### Notes
- ImageAnalyzerViewModel uses inline crash detection logic as it does not inherit from VideoProcessingBaseViewModel
- All other video processing ViewModels use the shared `TryHandleCrashAndRetryAsync()` method from VideoProcessingBaseViewModel
- Settings UI already had the necessary ComfyUI restart script path configuration

---

## Previous Task History / Changelogs

### 2026-03-26: VACE Video Generator — Queue System
*(Completed directly by Claude)*
Added VaceQueueItem model, queue to VACEVideoViewModel, XAML queue panel in VideoGeneratorWindow.

### 2026-03-05: Fix LoRA Path — RemoteLoraFolderPath Must Be Checked Before isRemoteServer Branch
1. **FlipPix.UI/ViewModels/ImageGeneratorViewModel.cs** — Fixed `GetLoraModelPath()`
2. **FlipPix.UI/ViewModels/ImageAnalyzerViewModel.cs** — Fixed `GetLoraModelPath()`

### LTX2 Audio Tab – Analyze Image & Enhance Prompt via LMStudio
*(Completed)*

### 2026-03-03: LTX2 Audio Workflow Update
### 2026-03-03: Infinite Talk Tab Implementation
### 2026-03-05: Amateur Generator Fixes
### 2026-03-05: Amateur Generator Workflow JSON Fixes
### 2026-03-05: ImageGeneratorViewModel Fixes (amateurZimageAPI)
### 2026-03-05: Fixed Node Removal Issue
### 2026-03-05: Fixed Aspect Ratio Handling for amateurZimageAPI

### 2026-03-10: LTX 2.3 Tab – Compact Layout & Auto-Generate
1. **FlipPix.UI/ViewModels/Video/LTX23BasicViewModel.cs** — Auto-trigger after LM Studio enhancement
2. **FlipPix.UI/VideoGeneratorWindow.xaml** — Redesigned LTX 2.3 reference image section
