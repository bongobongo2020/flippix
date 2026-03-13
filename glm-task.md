# Correction Task: LoRA Path — RemoteLoraFolderPath Must Be Checked Before isRemoteServer Branch

## Found
The previous fix was wrong about the root cause. The real issue:

- `BaseUrl = "http://localhost:8188"` → `IsRemoteUrl()` returns **false**
- The entire `if (isRemoteServer)` block is skipped
- `RemoteLoraFolderPath = "Y:\ai-models\loras\zimage"` is **never read at all**
- The code falls into the local branch and tries `ComfyUIFolderPath` + `extra_model_paths.yaml`
- ComfyUI runs **locally**, but lora files live on a **mapped network drive (Y:)**
- The `isRemoteServer` check is irrelevant to where loras are stored

## Fix Required

In both `ImageGeneratorViewModel.cs` and `ImageAnalyzerViewModel.cs`, inside `GetLoraModelPath()`:

**Move the `RemoteLoraFolderPath` check to the very top of the method, before the `isRemoteServer` branch.**

If `RemoteLoraFolderPath` is set and the directory is accessible → return it immediately, regardless of whether the server is local or remote. This setting is a universal override for "where loras live."

### ImageGeneratorViewModel.cs — `GetLoraModelPath()` at ~line 764

Insert the following block immediately after the `bool isRemoteServer = IsRemoteUrl(baseUrl);` line and before the `if (isRemoteServer)` block:

```csharp
// Check explicit LoRA folder override first (works for both local and remote servers)
// This handles the case where ComfyUI is local but loras live on a mapped network drive
var overrideLoraPath = _settingsService.Settings?.RemoteLoraFolderPath;
if (!string.IsNullOrEmpty(overrideLoraPath))
{
    if (Directory.Exists(overrideLoraPath))
    {
        AddLog($"Using configured LoRA folder: {overrideLoraPath}");
        return overrideLoraPath;
    }
    else
    {
        AddLog($"Configured LoRA folder not accessible: {overrideLoraPath}");
    }
}
```

Then remove the duplicate `RemoteLoraFolderPath` check that now exists inside the `if (isRemoteServer)` block (the one that was added by the previous fix, checking `explicitLoraPath`). The `if (isRemoteServer)` block should go back to only deriving the path from `RemoteOutputFolderPath`.

### ImageAnalyzerViewModel.cs — `GetLoraModelPath()` at ~line 915

Same change — insert the same block after `bool isRemoteServer = IsRemoteUrl(baseUrl);` and before `if (isRemoteServer)`, using `_logger.LogInfo` / `_logger.LogWarning` instead of `AddLog`:

```csharp
// Check explicit LoRA folder override first (works for both local and remote servers)
var overrideLoraPath = _settingsService.Settings?.RemoteLoraFolderPath;
if (!string.IsNullOrEmpty(overrideLoraPath))
{
    if (Directory.Exists(overrideLoraPath))
    {
        _logger.LogInfo($"Using configured LoRA folder: {overrideLoraPath}");
        return overrideLoraPath;
    }
    else
    {
        _logger.LogWarning($"Configured LoRA folder not accessible: {overrideLoraPath}");
    }
}
```

Then remove the duplicate `explicitLoraPath` check inside the `if (isRemoteServer)` block.

## Files to Modify
- `FlipPix.UI/ViewModels/ImageGeneratorViewModel.cs`
- `FlipPix.UI/ViewModels/ImageAnalyzerViewModel.cs`

## 4. Completion Instructions
Update this file with a "Changelog" section detailing your changes for review.

---

## Previous Task History / Changelogs

### 2026-03-05: Fix LoRA List Empty — RemoteLoraFolderPath Bypassed by Early Null Return
**Changelog:**
1. **FlipPix.UI/ViewModels/ImageGeneratorViewModel.cs** — Fixed `GetLoraModelPath()` to check `RemoteLoraFolderPath` first before `RemoteOutputFolderPath`
2. **FlipPix.UI/ViewModels/ImageAnalyzerViewModel.cs** — Fixed `GetLoraModelPath()` to check `RemoteLoraFolderPath` first before `RemoteOutputFolderPath`

**Change Summary:** Reordered the remote server path resolution logic in `GetLoraModelPath()` to prioritize the explicitly configured `RemoteLoraFolderPath`. Previously, the method returned `null` early when `RemoteOutputFolderPath` was empty, preventing the explicit LoRA path from ever being checked. Now:
- Priority 1: Use `RemoteLoraFolderPath` if set and accessible
- Priority 2: Derive path from `RemoteOutputFolderPath`
- Only return `null` if both are unavailable

---

### LTX2 Audio Tab – Analyze Image & Enhance Prompt via LMStudio
*(Completed)*

### 2026-03-03: LTX2 Audio Workflow Update
### 2026-03-03: Infinite Talk Tab Implementation
### 2026-03-05: Amateur Generator Fixes
### 2026-03-05: Amateur Generator Workflow JSON Fixes
### 2026-03-05: ImageGeneratorViewModel Fixes (amateurZimageAPI)
### 2026-03-05: Fixed Node Removal Issue
### 2026-03-05: Fixed Aspect Ratio Handling for amateurZimageAPI

---

### 2026-03-10: LTX 2.3 Tab – Compact Layout & Auto-Generate
**Changelog:**
1. **FlipPix.UI/ViewModels/Video/LTX23BasicViewModel.cs** — Modified `EnhancePromptWithLMStudioAsync()` to automatically trigger `AddToQueueAndProcess()` after prompt enhancement
2. **FlipPix.UI/VideoGeneratorWindow.xaml** — Redesigned LTX 2.3 reference image section with 2-column layout (50% image, 50% analysis) and removed "Add to Queue & Generate" button

**Change Summary:**
- Made the reference image box more compact with side-by-side layout (image on left, analysis on right)
- Enhanced user experience by removing manual "Add to Queue & Generate" button - the Enhance Prompt button now automatically queues and generates video after LM Studio returns the result
- Reduces scrolling and streamlines the LTX 2.3 workflow
