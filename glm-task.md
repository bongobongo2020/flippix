# Task: Fix Mocha Video — Pre-capture Race Condition

## 1. Context & Objective

In `MochaVideoViewModel.cs`, the chunk loop captures the "existing files" snapshot **after** `ExecuteWorkflowAsync` returns. Since `ExecuteWorkflowAsync` waits for ComfyUI to finish, the new video is already on disk when the snapshot is taken. `WaitForNewVideoAsync` then compares against a set that already contains the new video, so it never detects anything as "new" and spins for the full 15-minute timeout.

## 2. Files to Modify

- `FlipPix.UI/ViewModels/Video/MochaVideoViewModel.cs`

## 3. Implementation Steps

Inside `GenerateVideoAsyncInternal()`, in the `for (int chunkIndex ...)` loop, move the `GetExistingVideoFiles` call to **before** `ExecuteWorkflowAsync`.

**Current (broken) order — around lines 438–447:**
```csharp
var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
AddLog($"Chunk {chunkIndex + 1} workflow completed, prompt ID: {promptId}");

// Wait for output video
var existingFiles = GetExistingVideoFiles("*.mp4");
var outputVideo = await WaitForNewVideoAsync(
    existingFiles,
    "*.mp4",
    TimeSpan.FromMinutes(15),
    TimeSpan.FromSeconds(5));
```

**Fixed order:**
```csharp
// Capture existing files BEFORE executing workflow so new output can be detected
var existingFiles = GetExistingVideoFiles("*.mp4");

var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
AddLog($"Chunk {chunkIndex + 1} workflow completed, prompt ID: {promptId}");

// Wait for output video
var outputVideo = await WaitForNewVideoAsync(
    existingFiles,
    "*.mp4",
    TimeSpan.FromMinutes(15),
    TimeSpan.FromSeconds(5));
```

That is the only change — two lines reordered, one comment added. No other modifications.

## 4. Completion Instructions

Update this file with a "Changelog" section detailing your changes for my review.

## Changelog

### MochaVideoViewModel.cs — Pre-capture Race Condition Fix

**Issue:** `GetExistingVideoFiles()` was called after `ExecuteWorkflowAsync()`, causing the snapshot to include the newly generated video. This prevented `WaitForNewVideoAsync()` from detecting any new files, resulting in unnecessary 15-minute timeouts.

**Fix:** Moved `GetExistingVideoFiles("*.mp4")` call to execute **before** `ExecuteWorkflowAsync()`, ensuring the baseline snapshot is taken before the new video is created.

**Lines Modified:** 438-447

**Changes:**
1. Added comment explaining the pre-capture requirement
2. Moved `var existingFiles = GetExistingVideoFiles("*.mp4");` from after `ExecuteWorkflowAsync` to before it
3. Adjusted spacing to maintain readability

**Result:** `WaitForNewVideoAsync()` now correctly compares against a pre-workflow snapshot and will immediately detect the newly generated video file.
