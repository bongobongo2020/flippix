# Task: Fix Remote Image Retrieval — Use Prompt-Specific History Lookup

## 1. Context & Objective

**Bug:** When generating batches of story images on a remote ComfyUI server, only some images are retrieved. The rest fail with "No matching files found" even though ComfyUI generated them all successfully.

**Root Cause:** Both `ComfyUIImageRetriever.GetOutputImagesAsync()` and `StoryImageGeneratorFViewModel.GetOutputImagesFromComfyUI()` use `GetOutputFilesAsync()` for remote retrieval, which scans only the **last 5** history entries (sorted by key). In a batch of 10+ prompts, earlier prompts' entries get pushed out of that window, or unrelated workflow entries (e.g. camera-angles) sort higher, causing pattern matches to fail.

**Fix:** A method `GetOutputFilesForPromptAsync(promptId)` already exists in `ComfyUIHttpClient.cs` (line 651) — it queries `/history` and looks up the **specific prompt ID** to extract exact output filenames. Both callers should use this method first, falling back to the generic scan only if needed.

## 2. Files to Modify

### `FlipPix.UI/Services/ComfyUIImageRetriever.cs` — Remote branch (lines ~111-152)

Replace the remote retrieval logic inside the `while` loop. **When `promptId` is available**, try `GetOutputFilesForPromptAsync(promptId)` first. Only fall back to the generic `GetOutputFilesAsync()` + pattern matching if `promptId` is null/empty.

**Replace lines ~111-152** (the `if (isRemoteComfyUI)` block) with:

```csharp
if (isRemoteComfyUI)
{
    Log("Detected remote ComfyUI server, downloading generated image...");

    List<string> imageFiles = new();

    // Strategy 1: Use prompt-specific history lookup (most reliable)
    if (!string.IsNullOrEmpty(promptId))
    {
        var promptOutputFiles = await httpClient.GetOutputFilesForPromptAsync(promptId, ct);
        imageFiles = promptOutputFiles.Where(f => f.EndsWith(".png")).ToList();

        if (imageFiles.Any())
        {
            Log($"Found {imageFiles.Count} output file(s) for prompt {promptId}");
        }
        else
        {
            Log($"No output files found in history for prompt {promptId} yet");
        }
    }

    // Strategy 2: Fall back to scanning recent history with pattern matching
    if (!imageFiles.Any())
    {
        var outputFiles = await httpClient.GetOutputFilesAsync();
        Log($"Found {outputFiles.Count} potential output files in recent history");

        imageFiles = outputFiles.Where(f =>
            f.EndsWith(".png") &&
            (string.IsNullOrEmpty(expectedPattern) || f.Contains(expectedPattern)))
            .ToList();

        if (!string.IsNullOrEmpty(expectedPattern))
        {
            Log($"Looking for pattern: {expectedPattern}");
        }

        if (!imageFiles.Any())
        {
            Log($"No matching files found. Available files: {string.Join(", ", outputFiles.Take(5))}");
        }
    }

    // Download the image
    if (imageFiles.Any())
    {
        var filename = imageFiles.Last();
        Log($"Downloading generated image: {filename}");

        var imageData = await httpClient.DownloadOutputImageAsync(filename);
        if (imageData != null)
        {
            images.Add(imageData);
            Log($"Successfully downloaded image ({imageData.Length} bytes)");
        }
    }
}
```

### `FlipPix.UI/ViewModels/StoryImageGeneratorFViewModel.cs` — Remote branch (lines ~470-509)

Same fix. Replace the `if (isRemoteComfyUI)` block with prompt-specific lookup first:

**Replace lines ~470-509** with:

```csharp
if (isRemoteComfyUI)
{
    AddLog("Detected remote ComfyUI server, downloading generated image...");

    List<string> imageFiles = new();

    // Strategy 1: Use prompt-specific history lookup (most reliable)
    var promptOutputFiles = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
    imageFiles = promptOutputFiles.Where(f => f.EndsWith(".png")).ToList();

    if (imageFiles.Any())
    {
        AddLog($"Found {imageFiles.Count} output file(s) for prompt {promptId}");
    }
    else
    {
        // Strategy 2: Fall back to scanning recent history with pattern matching
        AddLog($"No output files in history for prompt {promptId} yet, trying pattern match...");

        var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
        AddLog($"Found {outputFiles.Count} potential output files in recent history");

        var expectedPattern = $"{jsonFileName}-{imageIndex}_";
        imageFiles = outputFiles.Where(f =>
            f.EndsWith(".png") &&
            (f.Contains(expectedPattern) || f.Contains($"{jsonFileName}/{expectedPattern}")))
            .ToList();

        AddLog($"Looking for pattern: {expectedPattern} (with or without subfolder prefix)");

        if (!imageFiles.Any())
        {
            AddLog($"No matching files found. Available files: {string.Join(", ", outputFiles.Take(5))}");
        }
    }

    // Download the image
    if (imageFiles.Any())
    {
        var filename = imageFiles.Last();
        AddLog($"Downloading generated image: {filename}");

        var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filename);
        if (imageData != null)
        {
            images.Add(imageData);
            AddLog($"Successfully downloaded image ({imageData.Length} bytes)");
        }
    }
}
```

### Important Notes

1. The `GetOutputFilesForPromptAsync` method already exists in `ComfyUIHttpClient.cs` (line 651) — do NOT create it, just call it.
2. The prompt's history entry may not appear immediately after execution completes (ComfyUI writes it asynchronously). The existing retry loop (20 retries × 5 seconds) handles this — on each retry, it will re-query the history.
3. Keep the pattern-matching fallback as Strategy 2 — it helps when `promptId` is null or the history entry is delayed.
4. Do NOT remove the `TryDownloadRecentOutputAsync` fallback from `ComfyUIImageRetriever` — remove only from the `StoryImageGeneratorFViewModel` since we now have the better prompt-specific lookup.

## 3. Completion Instructions
Update this file with a "Changelog" section detailing your changes for my review.

---

## Changelog

### Changes Implemented

#### 1. `FlipPix.UI/Services/ComfyUIImageRetriever.cs` (lines 111-152)
- **Modified**: Remote ComfyUI image retrieval logic in the retry loop
- **Change**: Now uses `GetOutputFilesForPromptAsync(promptId)` first (Strategy 1) for prompt-specific history lookup
- **Fallback**: If prompt-specific lookup returns no results, falls back to generic `GetOutputFilesAsync()` with pattern matching (Strategy 2)
- **Note**: Kept the existing retry loop mechanism — each retry will re-query the history for the prompt ID

#### 2. `FlipPix.UI/ViewModels/StoryImageGeneratorFViewModel.cs` (lines 470-509)
- **Modified**: Remote ComfyUI image retrieval logic in `GetOutputImagesFromComfyUI()`
- **Change**: Now uses `GetOutputFilesForPromptAsync(promptId)` first (Strategy 1) for prompt-specific history lookup
- **Fallback**: If prompt-specific lookup returns no results, falls back to generic `GetOutputFilesAsync()` with pattern matching (Strategy 2)
- **Removed**: The `TryDownloadRecentOutputAsync()` fallback is no longer needed since we now have the more reliable prompt-specific lookup

### Expected Behavior
- Batch generation of 10+ prompts should now reliably retrieve all images
- Earlier prompts' history entries will be found via prompt ID lookup, not pushed out of the "last 5" window
- The existing retry mechanism (20 retries × 5 seconds) continues to handle asynchronous history entry writing by ComfyUI

---

### Additional Fix (Hotfix)

**Problem**: The prompt-specific lookup was returning all PNG files from that prompt (including temp files, previews), and `.Last()` was selecting the wrong file (e.g., `ComfyUI_temp_ecnpk_00013_.png` instead of `1770791534553914-kleinvl-1_00001_.png`).

**Solution**: Filter the prompt-specific results by the expected pattern as well.

#### Updated `ComfyUIImageRetriever.cs` (line 117-131)
- Added pattern filtering to Strategy 1: `f.EndsWith(".png") && (string.IsNullOrEmpty(expectedPattern) || f.Contains(expectedPattern))`
- Added logging to show the pattern being filtered

#### Updated `StoryImageGeneratorFViewModel.cs` (line 476-496)
- Moved `expectedPattern` declaration before Strategy 1
- Added pattern filtering to Strategy 1: `f.Contains(expectedPattern) || f.Contains($"{jsonFileName}/{expectedPattern}")`
- Enhanced logging to show the matching pattern
