# Task: Add Local Video Copy to VideoGeneratorMainViewModel (Single Mode)

## 1. Context & Objective
The single video generation flow in `VideoGeneratorMainViewModel` does not copy generated videos to the local machine (`%USERPROFILE%\Videos\flippix-vids`). All other video view models (VACE, Mocha, LTX2Audio, StoryVideo) already call `LocalCopyService.CopyVideoAsync()` after generation. This task adds the missing call to maintain consistency.

## 2. Files to Modify
- `FlipPix.UI/ViewModels/Video/VideoGeneratorMainViewModel.cs`

## 3. Implementation Steps
1. In `GenerateVideoAsyncInternal()`, locate the block where `ResultVideoPath` is set after successful video generation (around line 1654-1668). The code looks like:
   ```csharp
   if (outputVideo != null && File.Exists(outputVideo))
   {
       ResultVideoPath = outputVideo;
       HasResult = true;
       ...
   }
   ```
2. Add `await LocalCopyService.CopyVideoAsync(outputVideo);` **after** `ResultVideoPath = outputVideo;` and **before** `HasResult = true;`. This matches the exact pattern used in:
   - `VACEVideoViewModel.cs` (line 516)
   - `MochaVideoViewModel.cs` (line 497)
   - `LTX2AudioViewModel.cs` (line 591)

3. Verify that `LocalCopyService` is already injected/available in `VideoGeneratorMainViewModel` (it should be, since the base class `VideoProcessingBaseViewModel` uses it).

## 4. Completion Instructions
Update this file with a "Changelog" section detailing your changes for my review.

---

## Changelog

### 2026-02-15 - Add Local Video Copy to VideoGeneratorMainViewModel (Single Mode)

**File Modified:** `FlipPix.UI/ViewModels/Video/VideoGeneratorMainViewModel.cs`

**Line Changed:** 1656-1657 (in `GenerateVideoAsyncInternal()`)

**Changes Made:**
1. Added `await LocalCopyService.CopyVideoAsync(outputVideo);` call after `ResultVideoPath = outputVideo;`
2. Placement: Between setting `ResultVideoPath` and setting `HasResult = true`

**Code Change:**
```csharp
// Before:
ResultVideoPath = outputVideo;
HasResult = true;

// After:
ResultVideoPath = outputVideo;
await LocalCopyService.CopyVideoAsync(outputVideo);
HasResult = true;
```

**Behavior Change:**
- Before: Videos generated in single mode were not copied to `%USERPROFILE%\Videos\flippix-vids`
- After: Videos are now automatically copied to the local user's Videos folder, matching the behavior of VACE, Mocha, and LTX2Audio video generators

**Technical Notes:**
- `LocalCopyService` is inherited from `VideoProcessingBaseViewModel` base class
- This change maintains consistency across all video generation workflows
