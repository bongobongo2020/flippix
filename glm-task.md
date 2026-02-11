# Task: Use Wan 2.2 System Prompt for "Analyze First Frame" When Wan 2.2 Workflow Selected

## 1. Context & Objective
When the user selects the **Wan 2.2** workflow in the Single Video Generator and clicks "Analyze First Frame", the app currently falls back to a generic prompt (`"Describe this image in detail for video generation."`). This is because the prompt selection logic only checks `UseLTXWorkflow` (which controls Story Video mode), not `SelectedSingleWorkflow` (which controls Single Video mode).

The fix: when `SelectedSingleWorkflow == SingleVideoWorkflow.Wan22`, load the Wan-specific system prompt from `prompts/prompt2json/wan-system.md` and send it to LM Studio.

## 2. Files to Modify
- `FlipPix.UI/ViewModels/Video/VideoGeneratorMainViewModel.cs` — the prompt selection block in `AnalyzeImageInternalAsync` (around lines 1155-1175)

## 3. Implementation Steps

In `VideoGeneratorMainViewModel.cs`, replace the prompt selection block in `AnalyzeImageInternalAsync`:

**Current code (around lines 1155-1175):**
```csharp
// Determine which prompt to use based on workflow selection
string analysisPrompt;
if (UseLTXWorkflow)
{
    var ltxPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", "ltx_action_video_system_prompt.md");
    if (File.Exists(ltxPromptPath))
    {
        analysisPrompt = await File.ReadAllTextAsync(ltxPromptPath, _analysisCancellationTokenSource.Token);
        AddLog("Using LTX-2 Action Video system prompt");
    }
    else
    {
        AddLog($"WARNING: LTX action video prompt not found at {ltxPromptPath}, using default");
        analysisPrompt = "Describe this image in detail for video generation.";
    }
}
else
{
    analysisPrompt = "Describe this image in detail for video generation.";
    AddLog("Using default image analysis prompt");
}
```

**Replace with:**
```csharp
// Determine which prompt to use based on workflow selection
string analysisPrompt;
string? promptPath = null;
string promptLabel;

if (SelectedSingleWorkflow == SingleVideoWorkflow.Wan22)
{
    promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", "wan-system.md");
    promptLabel = "Wan 2.2";
}
else
{
    promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", "ltx_action_video_system_prompt.md");
    promptLabel = "LTX-2 Action Video";
}

if (File.Exists(promptPath))
{
    analysisPrompt = await File.ReadAllTextAsync(promptPath, _analysisCancellationTokenSource.Token);
    AddLog($"Using {promptLabel} system prompt");
}
else
{
    AddLog($"WARNING: {promptLabel} prompt not found at {promptPath}, using default");
    analysisPrompt = "Describe this image in detail for video generation.";
}
```

**Key points:**
- Wan 2.2 check comes **first** — it's the most specific/new case
- All non-Wan workflows (LTX2V single, LTX story, Painter story) use the LTX prompt — this preserves existing behavior
- The `promptLabel` keeps log messages descriptive so the user can verify which prompt was loaded

## 4. Completion Instructions
Update this file with a "Changelog" section detailing your changes for my review.

---

## Changelog

### 2026-02-10: Wan 2.2 System Prompt Support for "Analyze First Frame"

**File Modified:** `FlipPix.UI/ViewModels/Video/VideoGeneratorMainViewModel.cs`

**Change:** Replaced the prompt selection logic in `AnalyzeImageInternalAsync` (lines 1155-1175) to support workflow-specific system prompts.

**Before:**
- Only checked `UseLTXWorkflow` (Story Video mode)
- Wan 2.2 workflow in Single Video mode fell back to generic prompt

**After:**
- Checks `SelectedSingleWorkflow == SingleVideoWorkflow.Wan22` first
- Wan 2.2 → loads `wan-system.md`
- All other workflows → load `ltx_action_video_system_prompt.md` (preserves existing behavior)
- Dynamic `promptLabel` for descriptive log messages

**Behavior:**
- Wan 2.2 users now get Wan-specific analysis prompts
- LTX2V single mode, LTX Story mode, and Painter Story mode retain existing LTX prompt behavior
- Graceful fallback to generic prompt if file not found

---

### 2026-02-10: Fixed "Add Prompts from JSON" Button for Story Image F and Q

**Issue:** The "Add Prompts from JSON" button for Story Image F and Story Image Q generators was not becoming enabled even after selecting both the prompt JSON file and input image.

**Root Cause:** The `LoadPromptsCommand` uses `CanLoadPrompts` as its canExecute condition, but when the `PromptJsonFilePath`, `InputImagePath`, or `IsProcessingQueue` properties changed, the command wasn't being properly notified to re-evaluate its canExecute state.

**File Modified:** `FlipPix.UI/ViewModels/StoryImageGeneratorBaseViewModel.cs`

**Changes Made:**

1. **Changed `LoadPromptsCommand` type from `ICommand` to `CommunityToolkit.Mvvm.Input.RelayCommand`** (line 365)
   - This exposes the `NotifyCanExecuteChanged()` method for explicit canExecute notifications

2. **Updated property setters to explicitly notify the command:**
   - `PromptJsonFilePath` setter (line 195): Added `LoadPromptsCommand.NotifyCanExecuteChanged()`
   - `InputImagePath` setter (line 209): Added `LoadPromptsCommand.NotifyCanExecuteChanged()`
   - `IsProcessingQueue` setter (line 235): Added `LoadPromptsCommand.NotifyCanExecuteChanged()`

3. **Replaced `CommandManager.InvalidateRequerySuggested()` with explicit command notifications**
   - The generic `CommandManager.InvalidateRequerySuggested()` call was replaced with the more specific `NotifyCanExecuteChanged()` method

**Behavior:**
- The "Add Prompts from JSON" button now properly enables/disables based on whether:
  1. A prompt JSON file is selected
  2. An input image is selected (required for Story Image F and Q)
  3. The queue is not currently processing

**Note:** This fix applies to all variants that inherit from `StoryImageGeneratorBaseViewModel`: Story Image F, Story Image Q, Story Image Amateur, and Story Image Z.
