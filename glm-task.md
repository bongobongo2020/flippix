# Correction Task: DownloadOutputImageAsync Fails When Filename Contains Subfolder Path

## Found
The previous fix correctly finds the file via `GetOutputFilesForPromptAsync`, but the download silently fails (returns null) every retry. The file path returned is `subfolder/filename.png` (e.g. `flux2-klein_.../flux2-klein_...-21_00001_.png`), but `DownloadOutputImageAsync` passes the entire path as the `filename` query parameter:

```
/view?filename=flux2-klein_20260215_100729-kleinvl-15-qwenvl%2Fflux2-klein_20260215_100729-kleinvl-15-qwenvl-21_00001_.png
```

ComfyUI's `/view` endpoint expects **separate** `filename` and `subfolder` parameters:
```
/view?filename=flux2-klein_20260215_100729-kleinvl-15-qwenvl-21_00001_.png&subfolder=flux2-klein_20260215_100729-kleinvl-15-qwenvl
```

**Root cause:** The Q ViewModel sets `filename_prefix` to `{jsonFileName}/{jsonFileName}-{imageIndex}` (line 391 of StoryImageGeneratorQViewModel.cs), which creates output files in a subfolder. When `GetOutputFilesForPromptAsync` constructs the path as `subfolder/filename`, `DownloadOutputImageAsync` doesn't split this into separate parameters.

Note: The F ViewModel doesn't have this issue because its `filename_prefix` is flat (`{jsonFileName}-{imageIndex}`, no `/`).

## Fix Required

**File:** `FlipPix.ComfyUI/Http/ComfyUIHttpClient.cs` — method `DownloadOutputImageAsync` (line ~347)

At the **beginning** of the method (after the try/log line), add logic to detect and split paths containing `/`:

```csharp
public async Task<byte[]?> DownloadOutputImageAsync(string filename, string subfolder = "", CancellationToken cancellationToken = default)
{
    try
    {
        _logger.LogInfo($"Downloading output image: {filename}");

        // If filename contains a path separator and no explicit subfolder was provided,
        // split it into subfolder + filename for ComfyUI's /view endpoint
        if (string.IsNullOrEmpty(subfolder) && filename.Contains('/'))
        {
            var lastSlash = filename.LastIndexOf('/');
            subfolder = filename.Substring(0, lastSlash);
            filename = filename.Substring(lastSlash + 1);
            _logger.LogInfo($"Split path into subfolder='{subfolder}', filename='{filename}'");
        }

        // Build the URL with query parameters
        var url = $"/view?filename={Uri.EscapeDataString(filename)}";
        // ... rest of method stays the same
```

This is a safe, centralized fix — it will also handle any other callers that pass `subfolder/filename` paths.

## Completion Instructions
Update this file with a "Changelog" section detailing your changes for my review.

---

## Changelog

### 2026-02-16 - Fix for DownloadOutputImageAsync Subfolder Path Handling

**File Modified:** `FlipPix.ComfyUI/Http/ComfyUIHttpClient.cs`

**Issue:** The `DownloadOutputImageAsync` method failed to download images when the filename contained a subfolder path (e.g., `flux2-klein_.../filename.png`). The entire path was passed as the `filename` query parameter, but ComfyUI's `/view` endpoint expects separate `filename` and `subfolder` parameters.

**Fix Applied:** Added path-splitting logic at the beginning of `DownloadOutputImageAsync` (after the try/log line) to detect filenames containing `/` and split them into separate `subfolder` and `filename` components when no explicit subfolder was provided.

**Code Added (lines 351-358):**
```csharp
// If filename contains a path separator and no explicit subfolder was provided,
// split it into subfolder + filename for ComfyUI's /view endpoint
if (string.IsNullOrEmpty(subfolder) && filename.Contains('/'))
{
    var lastSlash = filename.LastIndexOf('/');
    subfolder = filename.Substring(0, lastSlash);
    filename = filename.Substring(lastSlash + 1);
    _logger.LogInfo($"Split path into subfolder='{subfolder}', filename='{filename}'");
}
```

**Impact:** This is a centralized, safe fix that will handle any callers passing `subfolder/filename` paths, not just the Q ViewModel's specific case. The F ViewModel is unaffected as it uses flat filename prefixes.
