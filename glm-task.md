# Task: Add Force Cancel Queue Button for Story Image Generators

## 1. Context & Objective
The Story Image Q and F queue processing can freeze, and there is no way for users to cancel a stuck queue and restart processing. The existing `CancelProcessingCommand` is tied to the `IsProcessing` property which is never set during queue processing (only `IsProcessingQueue` is set).

**Goal:** Add a Force Cancel Queue button that allows users to forcefully cancel stuck queues and reset the state so processing can be restarted.

## 2. Files to Modify
- `FlipPix.UI/ViewModels/StoryImageGeneratorBaseViewModel.cs`: Add ForceCancelQueueCommand and implementation
- `FlipPix.UI/ImageGeneratorWindow.xaml`: Add Force Cancel buttons to StoryGenerator Q and F UIs

## 3. Implementation Steps

1. In `StoryImageGeneratorBaseViewModel.cs`:
   - Add `ForceCancelQueueCommand` property
   - Initialize the command with `ForceCancelQueue` method
   - Enable when `IsProcessingQueue` is true OR there are queued/processing items
   - Implement `ForceCancelQueue()` method that:
     - Cancels the cancellation token
     - Resets all queue state (IsProcessingQueue, IsQueuePaused, progress, etc.)
     - Marks "Processing" items as "Failed"
     - Saves queue state
   - Update `UpdateQueueCountNotifications()` to notify command state changes

2. In `ImageGeneratorWindow.xaml`:
   - Add "⛔ Force Cancel" button to StoryGenerator queue controls (alongside Pause/Resume)
   - Add "⛔ Force Cancel" button to StoryGeneratorF queue controls

## 4. Completion Instructions
Update this file with a "Changelog" section detailing your changes for my review.

---

## Changelog

### Date: 2026-02-16

### Changes Implemented

| File | Change |
|------|--------|
| `FlipPix.UI/ViewModels/StoryImageGeneratorBaseViewModel.cs` | Added ForceCancelQueueCommand and ForceCancelQueue() method |
| `FlipPix.UI/ImageGeneratorWindow.xaml` | Added Force Cancel buttons to StoryGenerator Q and F UIs |

### Technical Details

**ViewModel Changes (`StoryImageGeneratorBaseViewModel.cs`):**
- Added `ForceCancelQueueCommand` ICommand property
- Command enabled when: `IsProcessingQueue == true OR QueueItems.Any(i => i.Status == "Queued" || i.Status == "Processing")`
- `ForceCancelQueue()` method implementation:
  - Cancels `_cancellationTokenSource`
  - Resets: `IsProcessingQueue`, `IsQueuePaused`, `CurrentQueueItem`, `QueueProgress`, `QueueTotal`, `IsProcessing`, `ProcessingStatus`, `ProcessingProgress`
  - Marks any "Processing" items as "Failed" with error message "Force cancelled by user"
  - Calls `SaveQueueToFile()` and updates notifications
- `UpdateQueueCountNotifications()` now calls `ForceCancelQueueCommand.NotifyCanExecuteChanged()`

**UI Changes (`ImageGeneratorWindow.xaml`):**
- StoryGenerator Q section: Added `<Button Content="⛔ Force Cancel" Width="120" ... Command="{Binding StoryGenerator.ForceCancelQueueCommand}"`
- StoryGenerator F section: Added `<Button Content="⛔ Force Cancel" Width="120" ... Command="{Binding StoryGeneratorF.ForceCancelQueueCommand}"`
- Both buttons styled with red background (`#DC3545`) and appear alongside Pause/Resume buttons

### User Impact
- Users can now force-cancel stuck queues with a single button click
- Queue state is fully reset, allowing immediate restart of processing
- Stuck items are marked as "Failed" so they can be identified and retried
- Button is visible during queue processing and when there are queued/processing items
