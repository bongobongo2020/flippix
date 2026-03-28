# Task: VACE Video Generator — Queue System
*(Completed directly by Claude — 2026-03-26)*

Add a queue to the VACE Video Generator tab so multiple video generation jobs can be queued and processed sequentially, matching the pattern used by `LTX23BasicViewModel`.

---

## Files to Create

### 1. `FlipPix.UI/Models/VaceQueueItem.cs` — new file

```csharp
using System.IO;
using FlipPix.UI.Models;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// Queue item for VACE video generation jobs.
    /// </summary>
    public class VaceQueueItem : BaseQueueItem
    {
        public string ForegroundImagePath { get; set; } = string.Empty;
        public string InputVideoPath { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string? OutputVideoPath { get; set; }

        public string DisplayText =>
            !string.IsNullOrEmpty(ForegroundImagePath)
                ? $"{Path.GetFileName(ForegroundImagePath)} + {Path.GetFileName(InputVideoPath)}"
                : "(no input)";
    }
}
```

---

## Files to Modify

### 2. `FlipPix.UI/ViewModels/Video/VACEVideoViewModel.cs`

**Add namespace import** at the top (with existing usings):
```csharp
using System.Collections.ObjectModel;
using System.Linq;
```

**Add fields** after the existing private fields (after `private bool _isAnalyzing = false;`):
```csharp
private bool _isProcessingQueue = false;
private string _queueStatus = string.Empty;
private readonly ObservableCollection<VaceQueueItem> _queue = new();
```

**Add new command field** in the `#region Commands` declarations section — add after the existing command properties:
```csharp
public RelayCommand<VaceQueueItem> RemoveQueueItemCommand { get; }
```

**Wire up the new command** in the constructor, after `AnalyzeImageCommand = ...`:
```csharp
RemoveQueueItemCommand = new RelayCommand<VaceQueueItem>(RemoveQueueItem);

_queue.CollectionChanged += (s, e) =>
{
    OnPropertyChanged(nameof(HasQueueItems));
    UpdateQueueStatus();
    OnCanExecuteChanged();
};
```

**Change the GenerateVideoCommand** registration in the constructor from:
```csharp
GenerateVideoCommand = new RelayCommand(async () => await GenerateVideoAsync(), () => CanGenerateVideo);
```
to:
```csharp
GenerateVideoCommand = new RelayCommand(AddToQueueAndProcess, () => CanAddToQueue);
```

**Add queue properties** after the existing `CanAnalyzeImage` property (inside `#region Properties`):
```csharp
public ObservableCollection<VaceQueueItem> Queue => _queue;

public bool IsProcessingQueue
{
    get => _isProcessingQueue;
    private set
    {
        if (_isProcessingQueue != value)
        {
            _isProcessingQueue = value;
            OnPropertyChanged();
            OnCanExecuteChanged();
        }
    }
}

public string QueueStatus
{
    get => _queueStatus;
    private set { if (_queueStatus != value) { _queueStatus = value; OnPropertyChanged(); } }
}

public bool HasQueueItems => _queue.Any();

public bool CanAddToQueue => HasForegroundImage && HasInputVideo && !string.IsNullOrWhiteSpace(Prompt);
```

**Update CanGenerateVideo** — change the existing property from:
```csharp
public bool CanGenerateVideo => HasForegroundImage && HasInputVideo && !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing;
```
to:
```csharp
public bool CanGenerateVideo => HasForegroundImage && HasInputVideo && !string.IsNullOrWhiteSpace(Prompt) && !IsProcessing;
public bool CanAddToQueue => HasForegroundImage && HasInputVideo && !string.IsNullOrWhiteSpace(Prompt);
```
Wait — `CanAddToQueue` was already added in the Queue Properties region above, so just update `CanGenerateVideo` to remove the `CanAddToQueue` definition from the Properties region (avoid duplicate). The queue properties region already defines `CanAddToQueue`.

Actually: remove `CanGenerateVideo` from the existing region OR keep both, since they have different guards (`CanGenerateVideo` checks `!IsProcessing`). Keep `CanGenerateVideo` as-is and add `CanAddToQueue` as a separate property.

**Add queue management methods** — add a new `#region Queue Management` section before `#region Video Generation`:

```csharp
#region Queue Management

private void AddToQueueAndProcess()
{
    if (!CanAddToQueue) return;

    var item = new VaceQueueItem
    {
        ForegroundImagePath = ForegroundImagePath,
        InputVideoPath = InputVideoPath,
        Prompt = Prompt,
        ItemStatus = QueueItemStatus.Pending
    };

    _queue.Add(item);
    AddLog($"Added to queue: {item.DisplayText}");
    UpdateQueueStatus();

    if (!IsProcessingQueue)
        _ = ProcessQueueAsync();
}

private void RemoveQueueItem(VaceQueueItem? item)
{
    if (item != null && item.ItemStatus != QueueItemStatus.Processing)
    {
        _queue.Remove(item);
        UpdateQueueStatus();
    }
}

private void UpdateQueueStatus()
{
    var pending = _queue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
    var completed = _queue.Count(x => x.ItemStatus == QueueItemStatus.Completed);
    var failed = _queue.Count(x => x.ItemStatus == QueueItemStatus.Failed);
    var total = _queue.Count;

    QueueStatus = total == 0
        ? string.Empty
        : $"{pending} pending • {completed} done • {failed} failed";
}

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

            try
            {
                await GenerateSingleVideoAsync(item);
                item.ItemStatus = QueueItemStatus.Completed;
                AddLog($"Queue item completed: {item.DisplayText}");
            }
            catch (Exception ex)
            {
                item.ItemStatus = QueueItemStatus.Failed;
                item.ErrorMessage = ex.Message;
                AddLog($"Queue item FAILED: {ex.Message}");
            }

            UpdateQueueStatus();
        }
    }
    finally
    {
        IsProcessingQueue = false;
        AddLog("VACE queue processing finished.");
    }
}

#endregion
```

**Refactor `#region Video Generation`** — rename existing `GenerateVideoAsync` and `GenerateVideoAsyncInternal` into a single `GenerateSingleVideoAsync(VaceQueueItem item)` that reads from the item instead of from `this.ForegroundImagePath` etc.

Replace the entire `#region Video Generation` block (from `private async Task GenerateVideoAsync()` through the closing brace of `GenerateVideoAsyncInternal`) with:

```csharp
#region Video Generation

private async Task GenerateSingleVideoAsync(VaceQueueItem item)
{
    try
    {
        AddLog($"=== Starting VACE video generation: {item.DisplayText} ===");
        IsProcessing = true;

        HasResult = false;
        ResultVideoPath = string.Empty;
        ResultVideoInfo = string.Empty;
        ProcessingProgress = 0;
        ProcessingStatus = "Preparing VACE workflow...";

        AddLog($"Reference image: {Path.GetFileName(item.ForegroundImagePath)}");
        AddLog($"Input video: {Path.GetFileName(item.InputVideoPath)}");
        AddLog($"Prompt: {item.Prompt}");

        // Get frame count
        ProcessingStatus = "Analysing input video...";
        TotalFrames = GetVideoFrameCount(item.InputVideoPath);
        if (TotalFrames <= 0)
        {
            AddLog("WARNING: Could not determine frame count; defaulting to 1 chunk");
            TotalFrames = FramesPerChunk;
        }
        AddLog($"Total frames: {TotalFrames} → {TotalChunks} chunk(s) of {FramesPerChunk}");

        // ComfyUI health check
        ProcessingStatus = "Checking ComfyUI status...";
        var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
            status => AddLog($"[Auto-Restart] {status}"));

        if (!comfyUIOk)
        {
            AddLog("ERROR: ComfyUI is not running");
            System.Windows.MessageBox.Show(
                "ComfyUI is not running. Please start ComfyUI manually or configure auto-restart in settings.",
                "ComfyUI Not Running", MessageBoxButton.OK, MessageBoxImage.Warning);
            throw new Exception("ComfyUI is not running.");
        }

        if (!_comfyUIService.IsConnected)
        {
            ProcessingStatus = "Connecting to ComfyUI...";
            await _comfyUIService.ConnectAsync();
            AddLog("Connected to ComfyUI");
        }

        // Load workflow
        var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "Wan-VACE_V2V_MasterAPI.json");
        if (!File.Exists(workflowPath))
        {
            AddLog($"ERROR: Workflow file not found: {workflowPath}");
            throw new FileNotFoundException($"VACE workflow file not found: {workflowPath}");
        }

        var workflowJson = await File.ReadAllTextAsync(workflowPath);
        var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

        // Upload assets
        ProcessingStatus = "Uploading assets to ComfyUI...";
        ProcessingProgress = 10;

        AddLog("Uploading reference image...");
        var uploadedImageName = await _comfyUIService.UploadImageAsync(item.ForegroundImagePath);
        if (string.IsNullOrEmpty(uploadedImageName))
        {
            AddLog("ERROR: Reference image upload failed");
            throw new Exception("Failed to upload reference image to ComfyUI.");
        }
        AddLog($"Reference image uploaded: {uploadedImageName}");

        AddLog("Uploading video...");
        var uploadedVideoName = await _comfyUIService.UploadVideoAsync(item.InputVideoPath);
        if (string.IsNullOrEmpty(uploadedVideoName))
        {
            AddLog("ERROR: Video upload failed");
            throw new Exception("Failed to upload video to ComfyUI.");
        }
        AddLog($"Video uploaded: {uploadedVideoName}");

        // Calculate output dimensions from reference image
        int outputWidth = 576, outputHeight = 1024;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(item.ForegroundImagePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            double ar = (double)bitmap.PixelWidth / bitmap.PixelHeight;
            if (ar > 1.2) { outputWidth = 1024; outputHeight = 576; }
            else if (ar >= 0.85) { outputWidth = 720; outputHeight = 720; }
            else { outputWidth = 576; outputHeight = 1024; }
            AddLog($"Output dimensions: {outputWidth}x{outputHeight} (AR: {ar:F2})");
        }
        catch (Exception ex)
        {
            AddLog($"Warning: Could not read image dimensions, using defaults: {ex.Message}");
        }

        // Chunk loop
        var totalChunks = TotalChunks;
        var chunkFiles = new List<string>();
        AddLog($"=== Processing {totalChunks} chunk(s) of {FramesPerChunk} frames ===");

        for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
        {
            try
            {
                var startFrame = chunkIndex * FramesPerChunk;
                var framesInChunk = Math.Min(FramesPerChunk, TotalFrames - startFrame);

                AddLog($"=== Chunk {chunkIndex + 1}/{totalChunks}: frames {startFrame}–{startFrame + framesInChunk - 1} ===");
                ProcessingStatus = $"Processing chunk {chunkIndex + 1}/{totalChunks}";
                var baseProgress = 20.0 + chunkIndex * 60.0 / totalChunks;

                if (chunkIndex > 0 && !_comfyUIService.IsConnected)
                {
                    AddLog("Reconnecting to ComfyUI...");
                    await _comfyUIService.ConnectAsync();
                }

                var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedImageName, uploadedVideoName,
                    startFrame, framesInChunk, outputWidth, outputHeight, item.Prompt);

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = baseProgress + percent * 0.6 / totalChunks;
                            ProcessingStatus = $"Chunk {chunkIndex + 1}/{totalChunks}: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                        });
                    }
                });

                var existingFiles = GetExistingVideoFiles("*.mp4", OutputSubfolder);
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                AddLog($"Chunk {chunkIndex + 1} completed, prompt ID: {promptId}");

                var outputVideo = await TryGetVideoFromHistoryAsync(promptId);

                if (outputVideo == null)
                {
                    AddLog("History API returned no result, falling back to filesystem polling...");
                    outputVideo = await WaitForNewVideoAsync(
                        existingFiles, "*.mp4",
                        TimeSpan.FromMinutes(15),
                        TimeSpan.FromSeconds(5),
                        OutputSubfolder);
                }

                if (outputVideo != null && File.Exists(outputVideo))
                {
                    var chunkFile = Path.Combine(Path.GetTempPath(), $"vace_chunk_{chunkIndex:D3}_{Path.GetFileName(outputVideo)}");
                    File.Copy(outputVideo, chunkFile, true);
                    chunkFiles.Add(chunkFile);
                    AddLog($"Chunk {chunkIndex + 1}/{totalChunks} saved: {Path.GetFileName(chunkFile)}");
                }
                else
                {
                    AddLog($"ERROR: No output video for chunk {chunkIndex + 1} — aborting remaining chunks");
                    break;
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR processing chunk {chunkIndex + 1}: {ex.Message} — aborting remaining chunks");
                break;
            }
        }

        // Merge / finalise
        ProcessingProgress = 85;
        ProcessingStatus = "Merging video chunks...";
        AddLog("=== Merging chunks ===");

        if (chunkFiles.Count > 0)
        {
            var outputDir = Path.Combine(
                _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(),
                "VACE");
            Directory.CreateDirectory(outputDir);

            var finalPath = Path.Combine(outputDir, $"VACE_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

            if (chunkFiles.Count == 1)
            {
                File.Copy(chunkFiles[0], finalPath, true);
                AddLog($"Single chunk copied to: {finalPath}");
            }
            else
            {
                MergeVideoChunksWithFFmpeg(chunkFiles, finalPath);
            }

            foreach (var f in chunkFiles)
                try { File.Delete(f); } catch { }

            item.OutputVideoPath = finalPath;
            ResultVideoPath = finalPath;
            await LocalCopyService.CopyVideoAsync(finalPath);
            HasResult = true;

            var fi = new FileInfo(finalPath);
            ResultVideoInfo = $"VACE Video • {fi.Length / 1024 / 1024:F1}MB";
            ProcessingProgress = 100;
            ProcessingStatus = "VACE Complete!";
            AddLog($"=== VACE generation complete: {finalPath} ===");
        }
        else
        {
            AddLog("ERROR: No video chunks were generated");
            ProcessingStatus = "No output generated";
            throw new Exception("No video chunks were generated.");
        }
    }
    catch (Exception ex)
    {
        AddLog($"ERROR: {ex.Message}");
        AddLog($"Stack trace: {ex.StackTrace}");
        ProcessingStatus = "Error occurred";
        throw;
    }
    finally
    {
        IsProcessing = false;
    }
}

#endregion
```

**Update `UpdateWorkflowParameters` signature** — add `string prompt` parameter and use it instead of `this.Prompt`:

Change:
```csharp
private JsonElement UpdateWorkflowParameters(
    JsonElement workflow,
    string imageName,
    string videoName,
    int startFrame,
    int framesInChunk,
    int outputWidth,
    int outputHeight)
```
to:
```csharp
private JsonElement UpdateWorkflowParameters(
    JsonElement workflow,
    string imageName,
    string videoName,
    int startFrame,
    int framesInChunk,
    int outputWidth,
    int outputHeight,
    string prompt)
```

And inside the method, change `Prompt` to `prompt`:
```csharp
WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "31", "string", prompt);
```

**Update `NotifyCommandsCanExecuteChanged`** — add the new command:
```csharp
RemoveQueueItemCommand.NotifyCanExecuteChanged();
```

---

### 3. `FlipPix.UI/ViewModels/VideoGeneratorViewModel.cs`

In the `#region VaceVM Backward Compatibility Properties` section (around line 330), add these properties **after** the existing VACE properties and before the `#endregion`:

```csharp
// Queue
public System.Collections.ObjectModel.ObservableCollection<VaceQueueItem> VaceQueue => VaceVM.Queue;
public bool VaceHasQueueItems => VaceVM.HasQueueItems;
public bool VaceIsProcessingQueue => VaceVM.IsProcessingQueue;
public string VaceQueueStatus => VaceVM.QueueStatus;
public ICommand RemoveVaceQueueItemCommand => VaceVM.RemoveQueueItemCommand;
public bool VaceCanAddToQueue => VaceVM.CanAddToQueue;
```

Also add the namespace import at the top of the file:
```csharp
using FlipPix.UI.Models;
```
(check if it's already present — it may be since other queue types are used)

---

### 4. `FlipPix.UI/VideoGeneratorWindow.xaml` — VACE tab changes

**In the VACE Generate Section** (around line 1337–1398), replace the "🎭 Generate VACE Video" button content and add a queue section.

Change the button text and binding:
```xml
<Button Content="➕ Add to VACE Queue"
       Style="{StaticResource PrimaryButtonStyle}"
       Command="{Binding GenerateVACEVideoCommand}"
       IsEnabled="{Binding VaceCanAddToQueue, Mode=OneWay}"
       HorizontalAlignment="Stretch"
       Height="55"
       FontSize="15"
       Margin="0,0,0,15"/>
```

**Add a Queue Panel section** — insert a new `<Border>` after the "VACE Generate Section" border (after line 1398, before the "VACE Result Video Section"):

```xml
<!-- VACE Queue Panel -->
<Border Style="{StaticResource SectionPanelStyle}"
        Visibility="{Binding VaceHasQueueItems, Converter={StaticResource BooleanToVisibilityConverter}}">
    <StackPanel>
        <Grid Margin="0,0,0,8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="🎭 VACE Queue" Style="{StaticResource HeaderTextStyle}" Foreground="#FF6B35"/>
            <TextBlock Grid.Column="1" Text="{Binding VaceQueueStatus}"
                      FontStyle="Italic" Foreground="#666" FontSize="11"
                      VerticalAlignment="Center"/>
        </Grid>

        <ItemsControl ItemsSource="{Binding VaceQueue}">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <Border BorderBrush="#DEE2E6" BorderThickness="1" CornerRadius="4"
                            Padding="10,6" Margin="0,0,0,4" Background="White">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>

                            <!-- Status badge -->
                            <Border Grid.Column="0" CornerRadius="3" Padding="6,2" Margin="0,0,8,0"
                                    Background="{Binding StatusColor}">
                                <TextBlock Text="{Binding StatusDisplay}" FontSize="11" FontWeight="SemiBold" Foreground="White"/>
                            </Border>

                            <!-- Item info -->
                            <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                <TextBlock Text="{Binding DisplayText}" FontWeight="SemiBold" FontSize="11"/>
                                <TextBlock Text="{Binding Prompt}" FontSize="10" Foreground="#666"
                                           TextTrimming="CharacterEllipsis" MaxWidth="300"/>
                            </StackPanel>

                            <!-- Remove button -->
                            <Button Grid.Column="2"
                                    Content="✕"
                                    Style="{StaticResource SecondaryButtonStyle}"
                                    Command="{Binding DataContext.RemoveVaceQueueItemCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                    CommandParameter="{Binding}"
                                    Width="28" Height="28"
                                    Padding="0"
                                    FontSize="10"
                                    Visibility="{Binding ItemStatus, Converter={StaticResource QueueItemNotProcessingConverter}}"/>
                        </Grid>
                    </Border>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </StackPanel>
</Border>
```

**Note on converter**: Check if `QueueItemNotProcessingConverter` already exists in the XAML resources. If not, use a simpler approach — replace the remove button visibility with `IsEnabled="{Binding ItemStatus, Converter=...}"` or just omit the visibility binding and rely on `RemoveQueueItem` method's guard (`item.ItemStatus != Processing`).

**Simpler alternative for remove button** if converter doesn't exist — just show always and let the command's guard handle it:
```xml
<Button Grid.Column="2"
        Content="✕"
        Style="{StaticResource SecondaryButtonStyle}"
        Command="{Binding DataContext.RemoveVaceQueueItemCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
        CommandParameter="{Binding}"
        Width="28" Height="28"
        Padding="0"
        FontSize="10"/>
```

---

## Behavior Summary

1. User fills in Reference Image + Input Video + Prompt
2. Clicks "➕ Add to VACE Queue" → item added to queue, auto-processing starts if idle
3. Items in queue show status: ⏳ Pending → 🔄 Processing → ✅ Completed / ❌ Failed
4. Each item processes the full VACE chunk workflow sequentially
5. Result video player updates after each completed item
6. Items can be removed from queue (unless currently processing)

---

## 5. Completion Instructions
Update this file with a "Changelog" section detailing your changes for review.

---

## Previous Task History / Changelogs

### 2026-03-05: Fix LoRA Path — RemoteLoraFolderPath Must Be Checked Before isRemoteServer Branch
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
