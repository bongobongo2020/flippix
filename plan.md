# FlipPix Refactoring Plan

Implementation plan addressing all issues identified in the code review (architecture.md, Section 11).

---

## Phase 1: Critical Bug Fixes (Safety & Correctness)

These issues can cause deadlocks, data loss, or silent failures in production.

### 1.1 WorkflowQueueCoordinator - Add IDisposable Lease Pattern

**Issue:** If an exception occurs between `AcquireAsync()` and `Release()`, the semaphore deadlocks permanently.

**Files to modify:**
- `FlipPix.UI/Services/WorkflowQueueCoordinator.cs`

**Implementation:**
- Add an inner `WorkflowLease` class implementing `IDisposable` that calls `Release()` on dispose
- Change `AcquireAsync()` to return the lease object
- Callers use `using var lease = await coordinator.AcquireAsync(...)` for automatic cleanup

```csharp
public class WorkflowQueueCoordinator
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _currentWorkflowType;

    public string? CurrentWorkflowType => _currentWorkflowType;

    public async Task<WorkflowLease> AcquireAsync(string workflowType, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        _currentWorkflowType = workflowType;
        return new WorkflowLease(this);
    }

    private void Release()
    {
        _currentWorkflowType = null;
        _lock.Release();
    }

    public sealed class WorkflowLease : IDisposable
    {
        private WorkflowQueueCoordinator? _coordinator;

        internal WorkflowLease(WorkflowQueueCoordinator coordinator)
            => _coordinator = coordinator;

        public void Dispose()
        {
            var coordinator = Interlocked.Exchange(ref _coordinator, null);
            coordinator?.Release();
        }
    }
}
```

**Files to update (callers):** Every ViewModel that calls `AcquireAsync` / `Release`:
- `FlipPix.UI/ViewModels/ImageGeneratorViewModel.cs`
- `FlipPix.UI/ViewModels/ImageAnalyzerViewModel.cs`
- `FlipPix.UI/ViewModels/StoryImageGeneratorViewModel.cs`
- `FlipPix.UI/ViewModels/StoryImageGeneratorQViewModel.cs`
- `FlipPix.UI/ViewModels/StoryImageGeneratorFViewModel.cs`
- `FlipPix.UI/ViewModels/StoryImageGeneratorAmateurViewModel.cs`
- `FlipPix.UI/ViewModels/VideoGeneratorViewModel.cs`
- `FlipPix.UI/ViewModels/FlipPixViewModel.cs`
- `FlipPix.UI/ViewModels/CameraAngleViewModel.cs`
- `FlipPix.UI/ViewModels/AmateurGeneratorViewModel.cs`

Change pattern from:
```csharp
await _workflowCoordinator.AcquireAsync("workflowType", ct);
try { /* workflow */ }
finally { _workflowCoordinator.Release(); }
```
To:
```csharp
using var lease = await _workflowCoordinator.AcquireAsync("workflowType", ct);
// workflow - auto-released even on exception
```

---

### 1.2 WebSocket Buffer - Fix Message Fragmentation

**Issue:** 4KB buffer silently truncates large WebSocket messages, causing JSON parse failures.

**File to modify:**
- `FlipPix.ComfyUI/WebSocket/ComfyUIWebSocketClient.cs`

**Implementation:**
Replace the fixed-buffer read with a loop that accumulates fragments until `EndOfMessage`:

```csharp
private async Task ListenForMessagesAsync(CancellationToken cancellationToken)
{
    var buffer = new byte[4096];

    try
    {
        while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInfo("WebSocket closed by server");
                    ConnectionStatusChanged?.Invoke(this, "Closed");
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var message = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                ProcessMessage(message);
            }
        }
    }
    catch (OperationCanceledException) { /* existing handling */ }
    catch (Exception ex) { /* existing handling */ }
}
```

---

### 1.3 Fix HttpClient Disposal in LMStudioService

**Issue:** `LMStudioService.Dispose()` disposes the HttpClient that was injected via IHttpClientFactory, which can cause socket exhaustion.

**File to modify:**
- `FlipPix.UI/Services/LMStudioService.cs`

**Implementation:**
Remove `_httpClient?.Dispose()` from the `Dispose(bool)` method. Only dispose the semaphore:

```csharp
protected virtual void Dispose(bool disposing)
{
    if (!_disposed && disposing)
    {
        _semaphore?.Dispose();
        _disposed = true;
    }
}
```

---

### 1.4 Thread-Safe Settings Service

**Issue:** `SettingsService.SaveSettings()` and `LoadSettings()` are called from multiple threads without synchronization.

**File to modify:**
- `FlipPix.Core/Services/SettingsService.cs`

**Implementation:**
Add a `ReaderWriterLockSlim` to protect file access:

```csharp
public class SettingsService
{
    private readonly ReaderWriterLockSlim _lock = new();
    // ...

    public ComfyUISettings LoadSettings()
    {
        _lock.EnterReadLock();
        try { /* existing load logic */ }
        finally { _lock.ExitReadLock(); }
    }

    public void SaveSettings(ComfyUISettings settings)
    {
        _lock.EnterWriteLock();
        try { /* existing save logic */ }
        finally { _lock.ExitWriteLock(); }
    }
}
```

---

### 1.5 Fix CancellationToken Propagation

**Issue:** Async methods create standalone `CancellationTokenSource` instances not linked to application shutdown.

**Files to modify:**
- `FlipPix.UI/ViewModels/ImageGeneratorViewModel.cs`
- `FlipPix.UI/ViewModels/VideoGeneratorViewModel.cs`
- `FlipPix.UI/ViewModels/ImageAnalyzerViewModel.cs`
- `FlipPix.UI/ViewModels/StoryImageGeneratorViewModel.cs`
- `FlipPix.UI/ViewModels/StoryImageGeneratorQViewModel.cs`
- `FlipPix.UI/ViewModels/StoryImageGeneratorFViewModel.cs`
- `FlipPix.UI/ViewModels/StoryImageGeneratorAmateurViewModel.cs`

**Implementation:**
Where ViewModels create `new CancellationTokenSource()`, check if a parent token exists and use `CreateLinkedTokenSource()`:

```csharp
// Before
_cancellationTokenSource = new CancellationTokenSource();

// After (when a parent token is available)
_cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
```

For application-level shutdown, add a `CancellationToken` to the App class and pass it through DI or directly to ViewModels.

---

## Phase 2: Dead Code & Quick Cleanup

Quick wins that reduce confusion and code surface area.

### 2.1 Remove Legacy UI ComfyUIService

**Issue:** `FlipPix.UI.Services.ComfyUIService` (55 lines) is a simplified duplicate of the full `FlipPix.ComfyUI.Services.ComfyUIService`.

**Steps:**
1. Search all files for references to `FlipPix.UI.Services.ComfyUIService` or `using FlipPix.UI.Services; ... ComfyUIService`
2. Verify no code actually uses it (expected: none, all ViewModels use `FlipPix.ComfyUI.Services.ComfyUIService`)
3. Delete `FlipPix.UI/Services/ComfyUIService.cs`

**File to delete:**
- `FlipPix.UI/Services/ComfyUIService.cs`

---

### 2.2 Remove Manual GC.Collect() Calls

**Issue:** Forcing GC is counterproductive and causes pauses.

**File to modify:**
- `FlipPix.UI/Services/LMStudioService.cs`

**Implementation:**
- Remove the `CheckMemoryUsage()` method entirely
- Remove all calls to `CheckMemoryUsage()` (at the start of `GetAvailableModelsAsync`, `AnalyzeImageAsync`, `GenerateEnhancedPromptAsync`)

---

### 2.3 Replace Debug.WriteLine with IAppLogger

**Issue:** Inconsistent logging - some code uses `Debug.WriteLine`, some uses `IAppLogger`.

**Files to modify:**
- `FlipPix.Core/Services/SettingsService.cs` - Replace all `System.Diagnostics.Debug.WriteLine` calls
- `FlipPix.UI/App.xaml.cs` - Replace all `System.Diagnostics.Debug.WriteLine` calls

**Implementation for SettingsService:**
Add `IAppLogger` as a constructor parameter (or make it optional with a NullLogger fallback since SettingsService is created before logging is fully configured):

```csharp
public class SettingsService
{
    private IAppLogger? _logger;

    public void SetLogger(IAppLogger logger) => _logger = logger;

    // Replace: System.Diagnostics.Debug.WriteLine($"...");
    // With:    _logger?.LogInfo($"...");
    // Or:      _logger?.LogError($"...");
}
```

For `App.xaml.cs`, replace `Debug.WriteLine` calls with logger calls after the service provider is built.

---

### 2.4 Move Hardcoded Default Prompts to Settings/Resources

**Issue:** Default prompts are embedded in ViewModel field initializers.

**Files to modify:**
- `FlipPix.UI/ViewModels/ImageGeneratorViewModel.cs` - `_imagePrompt` field
- `FlipPix.UI/ViewModels/VideoGeneratorViewModel.cs` - `_videoPrompt` and `_negativePrompt` fields

**Implementation:**
- Add `DefaultImagePrompt`, `DefaultVideoPrompt`, `DefaultNegativePrompt` properties to `ComfyUISettings`
- Load defaults from settings; fall back to empty string if not set
- Remove hardcoded prompt strings from ViewModel field initializers

---

### 2.5 Remove Unused Using Directives

**Files to modify:** All ViewModel files that import `System.Windows.Controls`, `System.Windows.Media`, `Microsoft.Win32`, or other View-layer namespaces.

Run a tool or manually check:
- `FlipPix.UI/ViewModels/VideoGeneratorViewModel.cs` - `System.Windows.Controls`, `System.Windows.Media`, `Microsoft.Win32`
- `FlipPix.UI/ViewModels/ImageAnalyzerViewModel.cs` - `Microsoft.Win32`
- Other ViewModels with WPF-specific imports

---

## Phase 3: StoryImageGenerator Deduplication

### 3.1 Create StoryImageGeneratorBaseViewModel

**Issue:** 4 ViewModels (~6,086 lines) are near-identical copies differing only in workflow file, defaults, and node ID injection.

**New file to create:**
- `FlipPix.UI/ViewModels/StoryImageGeneratorBaseViewModel.cs`

**Implementation:**
Extract all shared code into the base class:

```csharp
public abstract class StoryImageGeneratorBaseViewModel : INotifyPropertyChanged
{
    // All shared fields: _comfyUIService, _logger, _settingsService, _workflowCoordinator,
    // _promptJsonFilePath, _inputImagePath, _queueItems, _isProcessingQueue, _pauseEvent,
    // _cancellationTokenSource, etc.

    // All shared properties with OnPropertyChanged

    // All shared commands: SelectPromptJson, SelectInputImage, LoadPrompts,
    // ProcessQueue, ClearQueue, OpenOutputFolder, CancelProcessing,
    // PauseQueue, ResumeQueue

    // All shared methods: SelectPromptJson(), LoadPromptsAsync(),
    // ProcessQueueAsync(), PauseQueue(), ResumeQueue(),
    // AddLog(), LoadQueueFromFile(), SaveQueueToFile()

    // Abstract methods that each variant overrides:
    protected abstract string WorkflowFileName { get; }
    protected abstract int DefaultSteps { get; }
    protected abstract double DefaultCfg { get; }
    protected abstract double DefaultDenoise { get; }
    protected abstract Task InjectWorkflowParametersAsync(
        Dictionary<string, object> workflow,
        StoryPromptItem item,
        string uploadedImageName);
    protected abstract string QueuePersistenceFileName { get; }
}
```

### 3.2 Refactor Each Variant to Inherit from Base

**Files to modify:**
- `FlipPix.UI/ViewModels/StoryImageGeneratorViewModel.cs` - Inherit base, keep only: Z-Image specific workflow injection, style/LoRA/orientation settings, upscale settings
- `FlipPix.UI/ViewModels/StoryImageGeneratorQViewModel.cs` - Inherit base, keep only: Qwen-specific node IDs, toggle settings visibility
- `FlipPix.UI/ViewModels/StoryImageGeneratorFViewModel.cs` - Inherit base, keep only: Flux-specific node IDs, portrait/landscape toggle
- `FlipPix.UI/ViewModels/StoryImageGeneratorAmateurViewModel.cs` - Inherit base, keep only: Amateur-specific LoRA settings (hardcoded amateur LoRA), character LoRA options

**Expected reduction:** ~3,000-4,000 lines eliminated.

---

## Phase 4: VideoGeneratorViewModel Split

### 4.1 Extract Sub-Feature ViewModels

**Issue:** `VideoGeneratorViewModel.cs` is 7,573 lines containing 5+ independent features.

**New files to create:**
- `FlipPix.UI/ViewModels/Video/VideoGeneratorMainViewModel.cs` - Core i2v: image selection, prompt, video settings, queue, story video queue (~2,500 lines)
- `FlipPix.UI/ViewModels/Video/VACEVideoViewModel.cs` - VACE extended video: background/foreground images, VACE prompt, VACE generation (~800 lines)
- `FlipPix.UI/ViewModels/Video/LTX2AudioViewModel.cs` - LTX2Audio: image, audio file, audio-synced generation (~800 lines)
- `FlipPix.UI/ViewModels/Video/MochaVideoViewModel.cs` - Mocha: source video, subject image, motion capture generation (~700 lines)

**File to modify:**
- `FlipPix.UI/ViewModels/VideoGeneratorViewModel.cs` - Becomes a thin shell that composes the sub-VMs:

```csharp
public class VideoGeneratorViewModel : INotifyPropertyChanged
{
    // Compose sub-ViewModels
    public VideoGeneratorMainViewModel Main { get; }
    public VACEVideoViewModel VACE { get; }
    public LTX2AudioViewModel LTX2Audio { get; }
    public MochaVideoViewModel Mocha { get; }

    // Shared state (selectedWorkflow, comfyUI server, etc.)
    // Navigation commands
}
```

**XAML to update:**
- `FlipPix.UI/VideoGeneratorWindow.xaml` - Update bindings from `{Binding Property}` to `{Binding Main.Property}`, `{Binding VACE.Property}`, etc.

### 4.2 Extract Shared Video Processing Base

**New file to create:**
- `FlipPix.UI/ViewModels/Video/VideoProcessingBaseViewModel.cs`

Shared logic for all video sub-VMs:
- Processing status/progress properties
- Log output management
- Result video display
- Open folder / play video commands
- ComfyUI connection and workflow execution

---

## Phase 5: MVVM Infrastructure Improvements

### 5.1 Migrate to CommunityToolkit.Mvvm Attributes

**Issue:** All ViewModels manually implement `INotifyPropertyChanged` boilerplate despite CommunityToolkit.Mvvm being referenced.

**Files to modify:** All ViewModels (start with smaller ones first).

**Migration pattern for each ViewModel:**

Before:
```csharp
public class MyViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public ICommand DoSomethingCommand { get; }

    public MyViewModel()
    {
        DoSomethingCommand = new RelayCommand(DoSomething);
    }

    private void DoSomething() { /* ... */ }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

After:
```csharp
public partial class MyViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [RelayCommand]
    private void DoSomething() { /* ... */ }
}
```

**Migration order (smallest to largest):**
1. `ComfyUIFolderSetupViewModel.cs` (675 lines) - Test the pattern
2. `OllamaViewModel.cs` (439 lines)
3. `I2V2AViewModel.cs` (877 lines)
4. `CameraAngleViewModel.cs` (810 lines)
5. `StoryVideoViewModel.cs` (1,423 lines)
6. `BasePromptViewModel.cs` (223 lines) - Update to inherit ObservableObject
7. `AmateurGeneratorViewModel.cs` (1,355 lines)
8. `FlipPixViewModel.cs` (1,952 lines)
9. `ImageGeneratorViewModel.cs` (2,523 lines)
10. `ImageAnalyzerViewModel.cs` (3,288 lines)
11. All StoryImageGenerator variants (after Phase 3 base extraction)
12. `VideoGeneratorViewModel.cs` / sub-VMs (after Phase 4 split)

**Note:** `BasePromptViewModel` should change to inherit `ObservableObject`:
```csharp
public abstract class BasePromptViewModel : ObservableObject
{
    // Remove manual INotifyPropertyChanged implementation
    // Replace OnPropertyChanged() calls with SetProperty() or [ObservableProperty]
}
```

**Side effect:** The custom `RelayCommand.cs` in `FlipPix.UI/Commands/` can be deleted since CommunityToolkit.Mvvm provides its own `RelayCommand`.

**File to delete (after full migration):**
- `FlipPix.UI/Commands/RelayCommand.cs`

---

### 5.2 Extract IFileDialogService

**Issue:** ViewModels directly call `OpenFileDialog` / `FolderBrowserDialog`, violating MVVM testability.

**New files to create:**
- `FlipPix.UI/Services/IFileDialogService.cs`
- `FlipPix.UI/Services/FileDialogService.cs`

**Interface:**
```csharp
public interface IFileDialogService
{
    string? OpenFile(string title, string filter);
    string[]? OpenFiles(string title, string filter);
    string? SaveFile(string title, string filter, string defaultName = "");
    string? SelectFolder(string description = "Select folder");
}
```

**Implementation:**
```csharp
public class FileDialogService : IFileDialogService
{
    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    // ... other methods wrapping WPF/WinForms dialogs
}
```

**DI registration:**
```csharp
services.AddSingleton<IFileDialogService, FileDialogService>();
```

**Files to modify:** All ViewModels that directly use `OpenFileDialog` or `FolderBrowserDialog`:
- `ImageGeneratorViewModel.cs`
- `VideoGeneratorViewModel.cs`
- `ImageAnalyzerViewModel.cs`
- `FlipPixViewModel.cs`
- `StoryImageGeneratorViewModel.cs` (and Q/F/Amateur variants)
- `StoryVideoViewModel.cs`
- `I2V2AViewModel.cs`
- `CameraAngleViewModel.cs`
- `AmateurGeneratorViewModel.cs`

Replace direct dialog calls with injected service calls.

---

### 5.3 Add IDisposable to ViewModels

**Issue:** ViewModels hold `CancellationTokenSource` and `ManualResetEventSlim` but never dispose them.

**Files to modify:** All ViewModels holding disposable resources.

**Implementation pattern:**
```csharp
public class SomeViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    private CancellationTokenSource? _cts;
    private readonly ManualResetEventSlim _pauseEvent = new(true);

    public void Dispose()
    {
        if (!_disposed)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _pauseEvent.Dispose();
            _disposed = true;
        }
    }
}
```

**Code-behind updates:** Windows that create/host ViewModels should dispose them on close:
```csharp
protected override void OnClosed(EventArgs e)
{
    (DataContext as IDisposable)?.Dispose();
    base.OnClosed(e);
}
```

**Files to update (code-behind):**
- `ImageGeneratorWindow.xaml.cs`
- `VideoGeneratorWindow.xaml.cs`
- `ImageAnalyzerWindow.xaml.cs`
- `FlipPixWindow.xaml.cs`
- `StoryVideoWindow.xaml.cs`
- `I2V2AWindow.xaml.cs`
- `OllamaWindow.xaml.cs`

---

## Phase 6: Verification & Testing

### 6.1 Build Verification

After each phase, verify the solution builds:
```bash
dotnet build FlipPix.sln -c Release
```

### 6.2 Functional Smoke Tests

After each phase, manually verify:
- [ ] App starts and shows ImageGeneratorWindow
- [ ] Setup flow works (local + remote)
- [ ] Text-to-image generation works (Zimage, Qwen, Klien workflows)
- [ ] Image queue processing works with pause/resume
- [ ] Video generation works (LTX, Painter)
- [ ] VACE video generation works
- [ ] LTX2Audio generation works
- [ ] Mocha generation works
- [ ] Image analysis via LMStudio works
- [ ] Story image generation works (all 4 variants)
- [ ] Camera angle transformation works
- [ ] Settings persist across restarts
- [ ] Queue crash recovery works (kill app during queue, restart, queue resumes)
- [ ] WebSocket progress updates display correctly
- [ ] Concurrent workflow prevention works (only one workflow at a time)

### 6.3 Regression Checks

- [ ] No deadlocks when workflows fail (WorkflowQueueCoordinator lease pattern)
- [ ] Large WebSocket messages parse correctly (buffer fragmentation fix)
- [ ] Multiple rapid settings changes don't corrupt settings.json (thread safety)
- [ ] App shutdown cancels all running workflows (CancellationToken propagation)

---

## Summary: Expected Impact

| Phase | Lines Removed | Lines Added | Net Change | Risk |
|---|---|---|---|---|
| Phase 1: Bug Fixes | ~50 | ~120 | +70 | Low (targeted fixes) |
| Phase 2: Cleanup | ~120 | ~30 | -90 | Very Low (deletions) |
| Phase 3: Story Dedup | ~4,000 | ~1,200 | **-2,800** | Medium (refactor) |
| Phase 4: Video Split | ~7,573 | ~5,500 | **-2,073** | Medium-High (major refactor) |
| Phase 5: MVVM Infra | ~5,000 | ~2,000 | **-3,000** | Medium (boilerplate removal) |
| **Total** | **~16,743** | **~8,850** | **~-7,893** | |

**Overall:** ~8,000 fewer lines of code while fixing all identified bugs and improving maintainability.

---

## Execution Order

Phases should be executed sequentially. Each phase depends on the previous:

1. **Phase 1** - Fix critical bugs first (safety net for all future work)
2. **Phase 2** - Clean up dead code (reduces noise for subsequent phases)
3. **Phase 3** - Story deduplication (establishes the base class pattern)
4. **Phase 4** - Video split (largest refactor, benefits from Phase 1-2 cleanup)
5. **Phase 5** - MVVM infrastructure (final polish, benefits from all prior phases)
6. **Phase 6** - Verification (after each phase, and comprehensive at end)

Phases 1 and 2 can be done together as a single commit since they're small, targeted changes.
