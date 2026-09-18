# FlipPix Architecture Document

## 1. Overview

FlipPix is a .NET 8.0 WPF desktop application for AI-powered creative content generation. It integrates with ComfyUI (local or remote) for image/video generation, and LMStudio/Ollama for LLM-based analysis and prompt enhancement.

**Core capabilities:**
- Text-to-image generation (Z-Image, Qwen2512, Flux2-Klien workflows)
- Image-to-video generation (LTX, Painter, Wan video models)
- Camera angle transformations (Qwen-Edit)
- Story-based batch generation (multiple workflow variants)
- Image analysis via LMStudio vision models
- Image-to-Video-to-Audio multimodal pipeline
- Queue-based batch processing with pause/resume

---

## 2. Solution Structure

```
FlipPix.sln
+-- FlipPix.Core/          (.NET 8.0 Class Library - shared models, interfaces, core services)
+-- FlipPix.ComfyUI/       (.NET 8.0 Class Library - ComfyUI server integration)
+-- FlipPix.UI/             (.NET 8.0 WPF Application - UI layer, MVVM)
+-- workflow/               (ComfyUI workflow JSON files)
+-- prompts/                (System prompts for LLM-based video prompt generation)
```

### Dependency Graph

```
FlipPix.UI --> FlipPix.ComfyUI --> FlipPix.Core
FlipPix.UI --> FlipPix.Core
```

---

## 3. Technology Stack

| Component | Technology | Version |
|---|---|---|
| Framework | .NET | 8.0 |
| UI | WPF (Windows Presentation Foundation) | Built-in |
| Architecture | MVVM | CommunityToolkit.Mvvm 8.2.2 |
| DI Container | Microsoft.Extensions.DependencyInjection | 8.0.0 |
| Logging | Serilog (File) | 3.0.0 |
| HTTP | HttpClient + IHttpClientFactory | 8.0.0 |
| Video Processing | FFMpegCore | 5.0.2 |
| Serialization | System.Text.Json | 9.0.10 |
| Image Processing | System.Drawing.Common | 10.0.1 |
| YAML | YamlDotNet | 15.1.2 |

---

## 4. Project Breakdown

### 4.1 FlipPix.Core

Shared foundation layer with no UI dependencies.

```
FlipPix.Core/
+-- Interfaces/
|   +-- IAppLogger.cs              # Logging abstraction (LogDebug/Info/Warning/Error)
+-- Models/
|   +-- ComfyUISettings.cs         # Main settings: server URLs, timeouts, folder paths, workflow config
|   +-- LMStudioSettings.cs        # LMStudio connection settings
|   +-- ProcessingSession.cs       # Session tracking for multi-step processing
|   +-- ProcessingStatus.cs        # Enum: processing states
|   +-- ProcessingStep.cs          # Individual step within a session
|   +-- StepType.cs                # Step type enumeration
|   +-- VideoInfo.cs               # Video/Image metadata (dimensions, FPS, codec, duration)
+-- Services/
    +-- SettingsService.cs          # Load/save settings to %AppData%/FlipPix/settings.json
    +-- ImageAnalysisService.cs     # Image dimension analysis, resolution calculations
```

### 4.2 FlipPix.ComfyUI

ComfyUI server integration layer handling HTTP and WebSocket communication.

```
FlipPix.ComfyUI/
+-- Exceptions/
|   +-- ComfyUIConnectionException.cs
|   +-- ComfyUIExecutionException.cs
|   +-- ComfyUITimeoutException.cs
+-- Http/
|   +-- ComfyUIHttpClient.cs       # HTTP: upload files, submit prompts, health checks (986 lines)
+-- Models/
|   +-- WebSocketMessage.cs         # Base message + UnknownMessage
|   +-- ProgressMessage.cs          # {value, max} for progress bars
|   +-- ExecutionStartMessage.cs    # Workflow started
|   +-- ExecutingMessage.cs         # Currently executing node
|   +-- ExecutionCompleteMessage.cs # Workflow completed with output metadata
|   +-- StatusMessage.cs            # Server status
|   +-- PromptRequest.cs            # Workflow submission request
|   +-- PromptResponse.cs           # {prompt_id} response
|   +-- QueueResponse.cs            # Queue state
|   +-- UploadResponse.cs           # {name} after file upload
+-- Services/
|   +-- ComfyUIService.cs           # Main orchestrator: connect, execute, monitor (568 lines)
|   +-- ComfyUIProcessManager.cs    # Local process management, auto-restart (506 lines)
+-- WebSocket/
    +-- ComfyUIWebSocketClient.cs   # Real-time progress via WebSocket (195 lines)
```

### 4.3 FlipPix.UI

WPF application layer following MVVM pattern.

```
FlipPix.UI/
+-- App.xaml.cs                     # DI setup, startup flow, server connectivity check
+-- Commands/
|   +-- RelayCommand.cs             # ICommand implementation (parameterless + generic)
+-- Converters/                     # BoolToVisibility, StringToVisibility, Subtract
+-- Controls/                       # SectionHeader, ProcessingLogPanel
+-- Models/                         # One queue-item type per tab + prompt/LLM payloads
|   +-- BaseQueueItem.cs            # Shared queue-item state (status, paths, progress)
|   +-- QueueItem.cs                # Video generation queue item
|   +-- ImagePromptQueueItem.cs     # Text-to-image queue item
|   +-- StoryPromptItem.cs          # Story generation queue item with thumbnail
|   +-- StoryVideoQueueItem.cs      # Story video batch item
|   +-- SavedPrompt.cs / ScenePrompt.cs  # Persisted prompts and the scene library
|   +-- LMStudioModels.cs           # LLM API types (ChatRequest, ChatResponse, etc.)
+-- Services/
|   +-- LMStudioService.cs          # LLM integration: vision analysis, prompt enhancement
|   +-- PromptService.cs            # Save/load prompt history, auto-naming
|   +-- ScenePromptLibrary.cs       # Persistent saved-scene DB (index + thumbnails)
|   +-- LoraManager.cs              # Local/network LoRA discovery
|   +-- WorkflowLocator.cs          # Resolves a workflow path, honouring the VRAM tier
|   +-- VramContext.cs              # Effective VRAM tier (auto/forced 16 GB/full)
|   +-- MissingModelResolver.cs     # Offers download / locate / register for absent models
|   +-- MissingNodeResolver.cs      # Resolves + installs missing custom-node packs
|   +-- VideoAnalysisService.cs     # FFMpeg-based video metadata extraction
|   +-- FileLogger.cs               # IAppLogger implementation, logs to %AppData%/FlipPix/logs/
|   +-- WorkflowQueueCoordinator.cs # SemaphoreSlim mutex for workflow execution
+-- ViewModels/                     # One ViewModel per tab
|   +-- BasePromptViewModel.cs              # Abstract base: prompt save/load/delete
|   +-- ImageGeneratorViewModel.cs          # Image Generator window composer + its own tab
|   +-- ImageAnalyzerViewModel.cs           # Analysis-driven generation (the Analyzer child)
|   +-- StoryImageGeneratorBaseViewModel.cs # Shared story batch machinery
|   +-- StoryImageGeneratorQViewModel.cs    # Story Image Q tab
|   +-- AmateurGeneratorViewModel.cs        # Amateur tab
|   +-- CameraAngleViewModel.cs             # Camera Angle tab
|   +-- InpaintEditorViewModel.cs           # Editor tab (mask painting)
|   +-- KleinInpaintViewModel.cs            # Klein inpaint variant used by the Editor tab
|   +-- KleinControlViewModel.cs            # Control tab (ControlNet / Krea2 two-reference)
|   +-- IdeogramViewModel.cs                # Ideogram tab
|   +-- QwenEditViewModel.cs                # Qwen Edit tab
|   +-- RestoreViewModel.cs                 # Restore tab
|   +-- VideoGeneratorViewModel.cs          # Video window composer (one sub-VM per tab)
|   +-- Video/                              # VideoProcessingBaseViewModel + the 11 video tabs
|   +-- ComfyUIFolderSetupViewModel.cs      # Setup wizard
+-- Windows/ (XAML + Code-behind)
    +-- ImageGeneratorWindow.xaml[.cs]       # Main window (default at startup)
    +-- VideoGeneratorWindow.xaml[.cs]       # Video generation
    +-- VideoEnhanceWindow.xaml[.cs]         # Interpolate + upscale
    +-- SettingsWindow.xaml[.cs]             # Global settings
    +-- ScenePromptLibraryWindow.xaml[.cs]   # Saved scenes browser
    +-- MissingModelsWindow / MissingNodesWindow  # Resolver dialogs
    +-- ComfyUIFolderSetupWindow.xaml[.cs]   # Local setup
    +-- RemoteSetupWindow.xaml[.cs]          # Remote server setup
    +-- SetupChoiceWindow.xaml[.cs]          # Local vs remote choice
```

> Only three windows are reachable from the UI: Image Generator (startup), Video Generator
> and Enhance Video, plus the modal Settings/setup/resolver dialogs. Anything that isn't
> reachable is dead code — the 2026-08 cleanup removed the previous crop (the standalone
> Image Analyzer, Camera, Story Video, I2V2A and Ollama windows, and the VACE, Mocha,
> LTX2Audio, SeedHunt, LTX Director, WanAnimate and CharReplace ViewModels).

---

## 5. Key Data Flows

### 5.1 Image Generation Flow

```
User Input (prompt, settings, LoRA selection)
    |
    v
ImageGeneratorViewModel
    |-- Validates input
    |-- Loads workflow JSON from /workflow/ directory
    |-- Injects: prompt text, aspect ratio, seed, LoRA config into JSON nodes
    |
    v
WorkflowQueueCoordinator.AcquireAsync()   [Mutex - one workflow at a time]
    |
    v
ComfyUIService.ConnectAsync()
    |-- Checks if ComfyUI is running (HTTP health check)
    |-- Auto-starts local ComfyUI if needed
    |-- Establishes WebSocket connection for progress
    |
    v
ComfyUIHttpClient.UploadImageAsync()       [If reference images needed]
    |
    v
ComfyUIService.QueuePromptAsync()          [Submit workflow JSON]
    |
    v
WebSocket progress monitoring              [Real-time progress updates]
    |-- ProgressMessage -> UI progress bar
    |-- ExecutionCompleteMessage -> Download result
    |
    v
Result image loaded from ComfyUI output folder
    |
    v
WorkflowQueueCoordinator.Release()
```

### 5.2 Video Generation Flow

Same pattern as image generation with additions:
- Multi-step processing (encode, generate, decode, upscale)
- Frame-level progress tracking
- First/last frame image inputs for motion control
- Audio-synced generation (Infinite Talk, Video Sound, the MiniMax H3 family)
- Chunked processing for long inputs (81/121-frame windows)
- Seed-preview flows: cheap low-step samples first, full-resolution re-render of the picks

### 5.3 Batch Queue Processing Flow

```
User adds items to queue (prompts + settings)
    |
    v
Queue items serialized to JSON (crash recovery)
    |
    v
ProcessQueueAsync() iterates items sequentially
    |-- ManualResetEventSlim for pause/resume
    |-- Per-item: execute workflow, update status, capture errors
    |-- WorkflowQueueCoordinator ensures mutual exclusion
    |
    v
Queue status updated: Pending -> Processing -> Completed/Failed
```

### 5.4 Application Startup Flow

```
App.OnStartup()
    |
    v
ConfigureServices() - Register all DI services
    |
    v
SettingsService.IsComfyUIFolderConfigured()?
    |
    +-- NO:  SetupChoiceWindow -> Local/Remote setup -> Save settings
    +-- YES: CheckServerConnectivityAsync() -> Warn if unreachable
    |
    v
Create ImageGeneratorWindow as MainWindow
```

---

## 6. DI Service Registration

| Service | Lifetime | Notes |
|---|---|---|
| SettingsService | Singleton | App-wide settings |
| IAppLogger (FileLogger) | Singleton | Single log destination |
| ComfyUIService | Singleton | Persistent connection |
| ComfyUIHttpClient | Singleton | Managed HttpClient |
| ComfyUIWebSocketClient | Singleton | Persistent WebSocket |
| WorkflowQueueCoordinator | Singleton | Global mutex |
| LMStudioService | Singleton | Dynamic URL from settings (OpenAI-compatible: LM Studio, Ollama, llama-server) |
| IPromptService (PromptService) | Singleton | Prompt history cache |
| ViewModels | Transient | Fresh instance per window |
| VideoGeneratorWindow | Singleton | Expensive to build; hidden on close and reused |

---

## 7. Workflow System

ComfyUI workflows are JSON node graphs stored in `/workflow/`. Each workflow defines a DAG of processing nodes.

### Workflow Categories

| Category | Files | Purpose |
|---|---|---|
| Text-to-Image | image_z_image-TEXTAPI.json, image/qwen/Qwen_Image_2512_INT8_Convrot_WF.json, Klien-Text-API.json | Generate images from text |
| Image Analysis | qwen-zimageAPI.json | Analyze + enhance images |
| Camera Edit | qwen-edit-camera-API.json | Change camera perspectives |
| Story Edit | RapidEditAIO-API.json | Story-driven image modification |
| Image-to-Video | LTX-2_image2video_distilledAPI.json, painteri2vAPI.json | Animate images |
| Extended Video | benji_Wan_Vace-*.json | Longer video generation |
| Audio Video | LTX2-AudioSync-*.json | Audio-synced video |
| Story Video | WCFMAPI.json | Narrative video generation |
| Multimodal | i2v2a_simple_v2.json | Image->Video->Audio pipeline |
| Style Presets | image/zimage/*.json | 14 style-specific workflows |

### Workflow Integration Pattern

ViewModels load workflow JSON, then inject parameters by modifying specific node IDs:
```csharp
workflow["nodeId"]["inputs"]["parameter"] = value;
```

---

## 8. External Service Integrations

### ComfyUI Server
- **Protocol:** HTTP (uploads, queue, health) + WebSocket (real-time progress)
- **Default:** http://127.0.0.1:8188
- **HTTP Endpoints:** /system_stats, /object_info, /upload/image, /prompt, /queue
- **WebSocket:** ws://host:port/ws?clientId={guid}

### LLM server (LMStudioService)
- **Protocol:** HTTP REST (OpenAI-compatible), so LM Studio, Ollama and llama-server all work
- **Default:** http://localhost:1234
- **Endpoints:** /v1/models, /v1/chat/completions
- **Used for:** Image analysis (vision), prompt writing per tab
- **Profiles:** Settings stores named server+model profiles; one is the default, and each
  tab's analysis status line names the target it is talking to

---

## 9. Design Patterns

| Pattern | Usage |
|---|---|
| **MVVM** | Views (XAML) bind to ViewModels via INotifyPropertyChanged |
| **Dependency Injection** | Microsoft.Extensions.DependencyInjection in App.xaml.cs |
| **Command** | RelayCommand for UI actions |
| **Template Method** | BasePromptViewModel defines prompt lifecycle, subclasses implement specifics |
| **Strategy** | Multiple workflow types selected at runtime |
| **Observer** | WebSocket events, PropertyChanged, ComfyUI progress events |
| **Adapter** | LMStudioService adapts OpenAI-compatible API for local use |
| **Singleton** | Services with app-wide state (settings, connections, coordinator) |
| **Mutex** | WorkflowQueueCoordinator prevents concurrent workflow execution |

---

## 10. Configuration

### Settings Location
`%AppData%/FlipPix/settings.json`

### Key Settings (ComfyUISettings)
- `BaseUrl` - ComfyUI server URL (default: http://localhost:8188)
- `ComfyUIFolderPath` - Local ComfyUI installation path
- `OutputFolderPath` - Local output directory
- `RemoteOutputFolderPath` / `RemoteLoraFolderPath` - Remote server paths
- `AutoRestartComfyUI` - Enable auto-restart on crash
- `ConnectionTimeout` / `MaxRetries` / `RetryDelayMilliseconds` - Retry policy
- `SelectedVideoWorkflow` - Default video workflow
- `LMStudioSettings` - LMStudio connection config
- Various folder bookmarks for file dialogs

### Logs Location
`%AppData%/FlipPix/logs/`

---

## 11. Code Review Findings & Optimization Recommendations

### RESOLVED: ViewModel bloat and duplication

The monolithic VideoGeneratorViewModel (7,573 lines) has been split: it is now a ~570-line
composer holding one sub-ViewModel per tab under `ViewModels/Video/`, with shared behaviour in
`VideoProcessingBaseViewModel`. The four copy-pasted story generators collapsed into
`StoryImageGeneratorBaseViewModel` + the surviving Q variant, and the duplicate
`FlipPix.UI.Services.ComfyUIService` wrapper is gone.

The composer still forwards MainVM and InfiniteTalkVM members under flat names because those
two tabs' XAML predates the per-tab `DataContext` pattern. New tabs bind through their own VM
property and add no forwarding.

### Still large

| File | Lines | Concern |
|---|---|---|
| ImageAnalyzerViewModel.cs | 4,608 | Mixes analysis, generation and queue logic |
| ImageGeneratorViewModel.cs | 4,150 | Composer for the Image Generator window *and* its own tab |
| Video/VideoGeneratorMainViewModel.cs | 3,176 | Single-video state plus the whole story queue |

Both composers would benefit from the same treatment the video window got: move the tab's own
state into a sibling ViewModel and leave the composer holding only children.

### Watch for dead code

Tabs get retired faster than their ViewModels do. A ViewModel is only alive if a tab sets it as
`DataContext` **or** the composer forwards its members to names the XAML binds — neither shows up
as a compiler warning, so removals leave a fully-compiling corpse behind. Same for windows: if
nothing resolves them from the container behind a bound command, they are unreachable. The
2026-08 pass removed 7 such ViewModels, 5 windows and their models.

### HIGH: WorkflowQueueCoordinator Missing Error Safety

```csharp
public async Task AcquireAsync(string workflowType, CancellationToken ct)
{
    await _lock.WaitAsync(ct);
    _currentWorkflowType = workflowType;
}

public void Release()
{
    _currentWorkflowType = null;
    _lock.Release();
}
```

If an exception occurs between `AcquireAsync()` and `Release()`, the semaphore is never released, permanently deadlocking all workflow execution.

**Recommendation:** Either:
- Add `IDisposable` lease pattern: `using var lease = await coordinator.AcquireAsync(...)` that auto-releases
- Or wrap all callers in try/finally (but this is error-prone since every caller must remember)

### HIGH: WebSocket Buffer Fragmentation

`ComfyUIWebSocketClient.ListenForMessagesAsync()` uses a fixed 4KB buffer:
```csharp
var buffer = new byte[4096];
var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
```

WebSocket messages larger than 4KB will be fragmented, and the code doesn't handle `result.EndOfMessage == false`. This causes JSON parse failures for large workflow responses.

**Recommendation:** Accumulate fragments in a `StringBuilder` or `MemoryStream` until `result.EndOfMessage` is true before parsing.

### HIGH: HttpClient Lifecycle Issues in LMStudioService

`LMStudioService` receives an `HttpClient` via constructor but also disposes it:
```csharp
public void Dispose() { _httpClient?.Dispose(); }
```

When created via `IHttpClientFactory`, the HttpClient should NOT be disposed by the consumer - the factory manages the handler lifecycle. Disposing it can cause `ObjectDisposedException` or socket exhaustion.

**Recommendation:** Remove `_httpClient.Dispose()` from `LMStudioService.Dispose()`. The DI container and `IHttpClientFactory` manage the lifecycle.

### MEDIUM: Manual GC.Collect() Calls

`LMStudioService.CheckMemoryUsage()` calls `GC.Collect()` when memory exceeds 500MB. Forcing garbage collection is rarely beneficial and can cause performance issues (pauses, generation fragmentation).

**Recommendation:** Remove the manual GC calls. If memory is a concern, investigate the actual source of allocations (likely base64 encoding of images). Use `ArrayPool<byte>` or `Span<byte>` for large byte operations instead.

### MEDIUM: Settings Service Not Thread-Safe

`SettingsService` reads/writes settings from multiple threads (UI thread + background Task.Run in ImageAnalyzerViewModel) without synchronization:
```csharp
// ImageAnalyzerViewModel - fires Task.Run to save settings on property change
_ = Task.Run(() => _settingsService.SaveSettings(_settingsService.Settings));
```

Concurrent reads/writes to the settings file can cause data corruption.

**Recommendation:** Add a lock or use `SemaphoreSlim` in `SaveSettings()` / `LoadSettings()`, or queue settings writes through a single writer.

### MEDIUM: INotifyPropertyChanged Inconsistency

`VideoGeneratorViewModel` and `ImageAnalyzerViewModel` implement `INotifyPropertyChanged` manually, while `ImageGeneratorViewModel` inherits from `BasePromptViewModel` which also implements it. The project references CommunityToolkit.Mvvm but doesn't use `ObservableObject` base class or `[ObservableProperty]` attributes.

**Recommendation:** Migrate ViewModels to inherit from `ObservableObject` (CommunityToolkit.Mvvm) and use `[ObservableProperty]` and `[RelayCommand]` attributes. This would eliminate hundreds of lines of boilerplate property setters and command declarations.

### MEDIUM: Hardcoded Default Prompts

ViewModels contain hardcoded default prompts:
```csharp
// ImageGeneratorViewModel
private string _imagePrompt = "Latina female with thick wavy hair, harbor boats...";

// VideoGeneratorViewModel
private string _negativePrompt = "色调艳丽，过曝，静态..." // Chinese text
```

**Recommendation:** Move default prompts to settings or resource files.

### MEDIUM: Missing CancellationToken Propagation

Several async methods create new `CancellationTokenSource` instances but don't link them to parent tokens. If the application shuts down during processing, orphaned tasks may continue running.

**Recommendation:** Use `CancellationTokenSource.CreateLinkedTokenSource()` to chain cancellation tokens.

### LOW: Unused Using Directives

Multiple files import `System.Windows.Controls`, `System.Windows.Media`, `Microsoft.Win32` in ViewModels - these are View-layer concerns that shouldn't appear in ViewModels.

**Recommendation:** Remove unused usings. The presence of `System.Windows` types in ViewModels suggests some MVVM violations (e.g., file dialogs opened directly from ViewModels via `OpenFileDialog`).

### LOW: File Dialog Calls in ViewModels

Several ViewModels directly instantiate `OpenFileDialog` / `SaveFileDialog` and `System.Windows.Forms.FolderBrowserDialog`. This violates MVVM testability.

**Recommendation:** Extract an `IFileDialogService` interface and inject it, allowing ViewModels to request files without depending on WPF types directly.

### LOW: Missing IDisposable on ViewModels

ViewModels hold `CancellationTokenSource` and `ManualResetEventSlim` instances but don't implement `IDisposable`. These are never cleaned up.

**Recommendation:** Implement `IDisposable` on ViewModels that hold disposable resources, and dispose them when windows close.

### LOW: Debug.WriteLine in Production Code

`SettingsService` and `App.xaml.cs` use `System.Diagnostics.Debug.WriteLine()` extensively alongside the proper `IAppLogger`. Debug output is stripped in Release builds and provides no value.

**Recommendation:** Replace all `Debug.WriteLine` calls with `IAppLogger` calls for consistent logging.

---

## 12. Suggested Refactoring Priority

Done since this document was first written: the legacy UI `ComfyUIService` is gone, the story
generators share a base class, `VideoGeneratorViewModel` is split per tab, and
`IFileDialogService` is injected everywhere.

Still open:

1. **Split the two remaining composers** - ImageGeneratorViewModel and VideoGeneratorMainViewModel
   each hold a window's worth of children *and* their own tab's state
2. **Migrate to CommunityToolkit.Mvvm attributes** - a lot of hand-written INotifyPropertyChanged remains
3. **Link cancellation tokens** - orphaned tasks can outlive a window that closed mid-run
4. **Thread-safe settings writes** - concurrent saves can clobber each other
5. **Replace Debug.WriteLine with IAppLogger** - one log destination, not two
6. **Remove manual GC calls**

---

## 13. File Size Summary

### Source Code (excluding obj/)
- **ViewModels:** ~40,300 lines across 31 files (one per tab, plus two window composers)
- **Services (UI):** ~4,800 lines
- **Services (ComfyUI):** ~1,380 lines
- **HTTP/WebSocket:** ~2,310 lines
- **Models:** ~1,800 lines

### Workflow Files
- 129 workflow JSON files under `workflow/`, including the `16gb/` memory-optimised variants
  and the per-domain subfolders (`image/*`, `video/*`)
- Average workflow: 200-500 node definitions

---

## 14. Build & Deployment

```bash
# Build
dotnet build FlipPix.sln -c Release

# Publish self-contained
dotnet publish FlipPix.UI/FlipPix.UI.csproj -c Release -r win-x64 --self-contained true
```

Workflow JSON files are copied to output via the .csproj `<Content>` directive with `CopyToOutputDirectory=Always`.
