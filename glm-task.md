# Task: LTX2 Audio Tab – Analyze Image & Enhance Prompt via LMStudio

## 1. Context & Objective

The LTX2 Audio tab (`VideoGeneratorWindow.xaml`, bound to `LTX2AudioViewModel`) needs two new AI-assisted buttons that chain LMStudio calls before video generation:

1. **"Analyze Image"** — below the Source Image section. Sends the source image to LMStudio (Qwen-VL) with `image-analysis-prompt.md` as the system prompt. Stores the description result internally.
2. **"Enhance Prompt"** — enabled after analysis, placed just below the "📝 Video Prompt" header. Sends the analysis result to LMStudio as user message with `ltx-audio-video.md` as the system prompt. Result populates the Video Prompt TextBox.

The prompts are already deployed to the output directory via `FlipPix.UI.csproj`'s `<Content Include="..\prompts\**\*">` item group. Access them at runtime with:
`Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", filename)`

The XAML binds through `VideoGeneratorViewModel` (the composer ViewModel), which proxies everything to `LTX2AudioVM` (`LTX2AudioViewModel`). All new properties and commands must follow this two-layer pattern.

---

## 2. Files to Modify

- `FlipPix.UI/Services/LMStudioService.cs` — add `SendTextChatAsync` method
- `FlipPix.UI/ViewModels/Video/LTX2AudioViewModel.cs` — add LMStudio dependency + new commands/properties/methods
- `FlipPix.UI/ViewModels/VideoGeneratorViewModel.cs` — pass lmStudioService to LTX2AudioVM + add proxy properties/commands
- `FlipPix.UI/VideoGeneratorWindow.xaml` — insert two buttons at the right locations

---

## 3. Implementation Steps

### Step 1 — `LMStudioService.cs`: Add `SendTextChatAsync`

Add this new public method after the `GenerateEnhancedPromptAsync` method:

```csharp
public async Task<string> SendTextChatAsync(
    string modelName,
    string systemPrompt,
    string userMessage,
    int maxTokens = 2000,
    CancellationToken cancellationToken = default)
{
    await _semaphore.WaitAsync(cancellationToken);
    try
    {
        var requestBody = new
        {
            model = modelName,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userMessage  }
            },
            max_tokens = maxTokens,
            temperature = 0.7,
            stream = false
        };

        var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        _logger.LogInfo($"SendTextChatAsync: model={modelName}, userMessage length={userMessage.Length}");

        var fullUrl = $"{_baseUrl.TrimEnd('/')}/v1/chat/completions";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(fullUrl, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"LM Studio API error: {response.StatusCode} - {errorContent}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<LMStudioChatResponse>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);

        if (result?.Choices?.Count > 0)
        {
            var text = result.Choices[0].Message?.Content?.Trim() ?? string.Empty;
            _logger.LogInfo($"SendTextChatAsync completed, response length={text.Length}");
            return text;
        }

        throw new Exception("No choices in LM Studio API response");
    }
    catch (OperationCanceledException) { throw; }
    catch (HttpRequestException ex)
    {
        throw new Exception($"Failed to connect to LM Studio at {_baseUrl}: {ex.Message}", ex);
    }
    finally
    {
        _semaphore.Release();
    }
}
```

---

### Step 2 — `LTX2AudioViewModel.cs`: New dependency, properties, commands, and methods

**2a. Add field and update constructor signature:**

Add field to the private fields region:
```csharp
private readonly LMStudioService _lmStudioService;
private bool _isAnalyzing = false;
private string _analysisResult = string.Empty;
```

Change constructor signature to add `LMStudioService lmStudioService` as the 3rd parameter (after `logger`, before `settingsService`):

```csharp
public LTX2AudioViewModel(
    ComfyUIService comfyUIService,
    IAppLogger logger,
    LMStudioService lmStudioService,           // NEW — 3rd parameter
    FlipPix.Core.Services.SettingsService settingsService,
    IServiceProvider? serviceProvider,
    WorkflowQueueCoordinator workflowCoordinator,
    IFileDialogService fileDialogService)
    : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
{
    _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
    _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
    // ... existing command initializations ...
    AnalyzeImageCommand  = new RelayCommand(async () => await AnalyzeImageWithLMStudioAsync(),  () => CanAnalyzeImage);
    EnhancePromptCommand = new RelayCommand(async () => await EnhancePromptWithLMStudioAsync(), () => CanEnhancePrompt);
    AddLog("LTX2 Audio Video Generator initialized");
}
```

NOTE: Keep all existing command initializations. Only add the two new ones and the lmStudioService assignment.

**2b. New properties** (add to the Properties region):

```csharp
public bool IsAnalyzing
{
    get => _isAnalyzing;
    set
    {
        if (_isAnalyzing != value)
        {
            _isAnalyzing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAnalyzeImage));
            OnPropertyChanged(nameof(CanEnhancePrompt));
            OnCanExecuteChanged();
        }
    }
}

public string AnalysisResult
{
    get => _analysisResult;
    set
    {
        if (_analysisResult != value)
        {
            _analysisResult = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAnalysis));
            OnPropertyChanged(nameof(CanEnhancePrompt));
            OnCanExecuteChanged();
        }
    }
}

public bool HasAnalysis    => !string.IsNullOrWhiteSpace(AnalysisResult);
public bool CanAnalyzeImage => HasImage && !IsAnalyzing && !IsProcessing;
public bool CanEnhancePrompt => HasAnalysis && !IsAnalyzing;
```

Also update the `ImagePath` setter to add:
```csharp
OnPropertyChanged(nameof(CanAnalyzeImage));
```
alongside the existing `OnPropertyChanged(nameof(HasImage))` call.

**2c. New commands** (add to the Commands region, after existing commands):
```csharp
public RelayCommand AnalyzeImageCommand  { get; }
public RelayCommand EnhancePromptCommand { get; }
```

**2d. New async methods** (add a new region `#region LMStudio Analysis Methods`):

```csharp
private async Task AnalyzeImageWithLMStudioAsync()
{
    if (!CanAnalyzeImage) return;
    try
    {
        IsAnalyzing = true;
        AddLog("=== Analyzing image with LMStudio ===");

        var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://localhost:1234";
        await _lmStudioService.SetBaseUrlAsync(baseUrl);

        var models = await _lmStudioService.GetAvailableModelsAsync();
        var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
        if (string.IsNullOrEmpty(selectedModel))
        {
            if (models.Count > 0)
                selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;
            else
                throw new Exception("No models available in LM Studio. Please load a vision model.");
        }

        AddLog($"Using model: {selectedModel}");

        var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "prompts", "prompt2json", "image-analysis-prompt.md");
        if (!File.Exists(promptFilePath))
            throw new FileNotFoundException($"Prompt file not found: {promptFilePath}");

        var systemPromptContent = await File.ReadAllTextAsync(promptFilePath);

        var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
            selectedModel,
            ImagePath,
            "Analyze this image.",
            systemPromptContent);

        AnalysisResult = result;
        AddLog($"Image analysis complete ({result.Length} chars)");
        AddLog($"Preview: {(result.Length > 200 ? result.Substring(0, 200) + "..." : result)}");
    }
    catch (Exception ex)
    {
        AddLog($"ERROR analyzing image: {ex.Message}");
        System.Windows.MessageBox.Show(
            $"Image analysis failed:\n{ex.Message}",
            "Analysis Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
    finally
    {
        IsAnalyzing = false;
    }
}

private async Task EnhancePromptWithLMStudioAsync()
{
    if (!CanEnhancePrompt) return;
    try
    {
        IsAnalyzing = true;
        AddLog("=== Enhancing prompt with LMStudio (LTX2 Audio) ===");

        var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://localhost:1234";
        await _lmStudioService.SetBaseUrlAsync(baseUrl);

        var models = await _lmStudioService.GetAvailableModelsAsync();
        var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
        if (string.IsNullOrEmpty(selectedModel))
        {
            if (models.Count > 0)
                selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;
            else
                throw new Exception("No models available in LM Studio.");
        }

        AddLog($"Using model: {selectedModel}");

        var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "prompts", "prompt2json", "ltx-audio-video.md");
        if (!File.Exists(promptFilePath))
            throw new FileNotFoundException($"Prompt file not found: {promptFilePath}");

        var systemPromptContent = await File.ReadAllTextAsync(promptFilePath);

        var enhancedPrompt = await _lmStudioService.SendTextChatAsync(
            selectedModel,
            systemPromptContent,
            AnalysisResult,
            maxTokens: 2000);

        Prompt = enhancedPrompt;
        AddLog($"Prompt enhanced ({enhancedPrompt.Length} chars)");
    }
    catch (Exception ex)
    {
        AddLog($"ERROR enhancing prompt: {ex.Message}");
        System.Windows.MessageBox.Show(
            $"Prompt enhancement failed:\n{ex.Message}",
            "Enhancement Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
    finally
    {
        IsAnalyzing = false;
    }
}
```

**2e. Update `NotifyCommandsCanExecuteChanged()`** — add to the existing method body:
```csharp
AnalyzeImageCommand.NotifyCanExecuteChanged();
EnhancePromptCommand.NotifyCanExecuteChanged();
```

---

### Step 3 — `VideoGeneratorViewModel.cs`: Wire LMStudio to LTX2AudioVM + Proxies

**3a. Update `LTX2AudioVM` instantiation** — add `lmStudioService` as the 3rd argument (already available as `_lmStudioService`):

```csharp
LTX2AudioVM = new LTX2AudioViewModel(
    comfyUIService,
    logger,
    lmStudioService,       // NEW — 3rd argument
    settingsService,
    serviceProvider,
    _workflowCoordinator,
    _fileDialogService);
```

**3b. Add proxy properties and commands** in `#region LTX2AudioVM Backward Compatibility Properties`, after the existing proxies (after line ~340):

```csharp
// LMStudio AI analysis properties
public bool LTX2AudioIsAnalyzing   => LTX2AudioVM.IsAnalyzing;
public bool CanLTX2AnalyzeImage    => LTX2AudioVM.CanAnalyzeImage;
public bool CanLTX2EnhancePrompt   => LTX2AudioVM.CanEnhancePrompt;

// LMStudio AI analysis commands
public ICommand AnalyzeLTX2AudioImageCommand  => LTX2AudioVM.AnalyzeImageCommand;
public ICommand EnhanceLTX2AudioPromptCommand => LTX2AudioVM.EnhancePromptCommand;
```

---

### Step 4 — `VideoGeneratorWindow.xaml`: Insert XAML buttons

**4a. "Analyze Image" button** — insert immediately after the `<TextBlock Text="{Binding LTX2AudioImageInfo}" .../>` element (around line 1549–1554) and before the closing `</StackPanel>` of the Source Image inner `<StackPanel>`:

```xml
<Button Content="Analyze Image"
        Style="{StaticResource SecondaryButtonStyle}"
        Command="{Binding AnalyzeLTX2AudioImageCommand}"
        IsEnabled="{Binding CanLTX2AnalyzeImage, Mode=OneWay}"
        Height="35"
        Width="150"
        HorizontalAlignment="Left"
        Margin="0,8,0,0"/>
```

**4b. "Enhance Prompt" button** — insert in the Video Prompt section `<StackPanel>`, after `<TextBlock Text="📝 Video Prompt" Style="{StaticResource HeaderTextStyle}" Foreground="#FF6B35"/>` (around line 1597) and before the `<TextBlock Text="Describe the video action...">`:

```xml
<Button Content="Enhance Prompt"
        Style="{StaticResource SecondaryButtonStyle}"
        Command="{Binding EnhanceLTX2AudioPromptCommand}"
        IsEnabled="{Binding CanLTX2EnhancePrompt, Mode=OneWay}"
        Height="35"
        Width="160"
        HorizontalAlignment="Left"
        Margin="0,5,0,10"/>
```

---

## 4. Completion Instructions

After implementation, update this file with a "Changelog" section listing every file changed and the nature of each change.

### Verification checklist:
- [ ] `LMStudioService.SendTextChatAsync` compiles
- [ ] `LTX2AudioViewModel` constructor takes 7 parameters (`lmStudioService` is 3rd)
- [ ] `VideoGeneratorViewModel` passes `lmStudioService` as 3rd arg to `LTX2AudioViewModel`
- [ ] `AnalyzeLTX2AudioImageCommand` and `EnhanceLTX2AudioPromptCommand` proxies exist in `VideoGeneratorViewModel`
- [ ] `BoolToVisibilityConverter` exists in FlipPix.UI/Converters/
- [ ] "Analyze Image" button appears under Source Image, disabled without an image
- [ ] Analysis Result TextBox appears (and is read-only) after analysis completes
- [ ] "Enhance Prompt" button appears below "📝 Video Prompt" header, disabled until analysis is done
- [ ] Video Prompt TextBox is HIDDEN until after "Enhance Prompt" completes
- [ ] After "Enhance Prompt", the Video Prompt TextBox becomes visible and is populated

---

## Changelog

### Files Modified

1. **FlipPix.UI/Services/LMStudioService.cs**
   - Added `SendTextChatAsync` method for text-only chat completions with system and user messages

2. **FlipPix.UI/ViewModels/Video/LTX2AudioViewModel.cs**
   - Added `LMStudioService` dependency field and `_isAnalyzing`, `_analysisResult` fields
   - Updated constructor to accept `LMStudioService` as 3rd parameter
   - Added `IsAnalyzing`, `AnalysisResult`, `HasAnalysis`, `CanAnalyzeImage`, `CanEnhancePrompt`, `ShowVideoPrompt` properties
   - Added `AnalyzeImageCommand` and `EnhancePromptCommand` commands
   - Added `AnalyzeImageWithLMStudioAsync` method to analyze images with LMStudio
   - Added `EnhancePromptWithLMStudioAsync` method to enhance prompts using analysis results
   - `EnhancePromptWithLMStudioAsync` sets `ShowVideoPrompt` to true after completion
   - Updated `ImagePath` setter to notify `CanAnalyzeImage` property changes
   - Updated `NotifyCommandsCanExecuteChanged` to notify new commands

3. **FlipPix.UI/ViewModels/VideoGeneratorViewModel.cs**
   - Updated `LTX2AudioVM` instantiation to pass `lmStudioService` as 3rd argument
   - Added proxy properties: `LTX2AudioIsAnalyzing`, `CanLTX2AnalyzeImage`, `CanLTX2EnhancePrompt`, `ShowLTX2AudioVideoPrompt`, `LTX2AudioAnalysisResult`, `HasLTX2AudioAnalysis`
   - Added proxy commands: `AnalyzeLTX2AudioImageCommand`, `EnhanceLTX2AudioPromptCommand`

4. **FlipPix.UI/VideoGeneratorWindow.xaml**
   - Added `xmlns:converters` namespace for converters
   - Added `BoolToVisibilityConverter` to Window.Resources
   - Added "Analyze Image" button under Source Image section (bound to `AnalyzeLTX2AudioImageCommand`)
   - Added Analysis Result TextBox (read-only) that appears after analysis completes
   - Added "Enhance Prompt" button in Video Prompt section (bound to `EnhanceLTX2AudioPromptCommand`)
   - Video Prompt TextBox is hidden (`Visibility=Collapsed`) until `ShowLTX2AudioVideoPrompt` becomes true

5. **FlipPix.UI/Converters/BoolToVisibilityConverter.cs** (NEW)
   - Created `BoolToVisibilityConverter` for converting boolean values to Visibility (Visible/Collapsed)
   - Supports optional "Invert" parameter to reverse the logic

### Summary

Implemented LTX2 Audio Tab AI-assisted features with updated workflow:
1. **Analyze Image** button sends source image to LMStudio (Qwen-VL) with `image-analysis-prompt.md` system prompt
2. Analysis result displayed in a **separate read-only TextBox** above the Video Prompt section
3. **Enhance Prompt** button sends the analysis result to LMStudio with `ltx-audio-video.md` system prompt
4. **Video Prompt TextBox is hidden** until after the prompt enhancement is complete (`ShowVideoPrompt` = true)
5. Both buttons are properly enabled/disabled based on state (image loaded, analysis complete, not processing)

---

## 2026-03-03: LTX2 Audio Workflow Update

### Files Modified

1. **FlipPix.UI/ViewModels/Video/LTX2AudioViewModel.cs**
   - Changed workflow file from `LTX2-AudioSync-i2v-Ver2-GGUF (2)(1).json` to `video_ltx2_ai2v_03API.json`
   - Updated node mappings in `UpdateWorkflowParameters`:
     - Image: node 110 → node 180 (LoadImage)
     - Audio: node 12 → node 199 (VHS_LoadAudio)
     - Prompt: node 85 → node 133 (CLIPTextEncode)
     - Frames: node 81 → node 139 (PrimitiveInt)
     - Width/Height: node 68 → node 186 (ImageResizeKJv2)
     - Audio seek/duration: nodes 101/102 → node 199 (same node)

---

## 2026-03-03: Infinite Talk Tab Implementation

### Files Created

1. **FlipPix.UI/ViewModels/Video/InfiniteTalkViewModel.cs** (NEW)
   - Created ViewModel for Wan2.1 InfiniteTalk video generation
   - Handles image input, audio file, and video prompt
   - Uses AI-assisted image analysis via LMStudio (image-analysis-prompt.md)
   - Uses AI-assisted prompt enhancement via LMStudio (ltx-audio-video.md)
   - Calculates total frames based on audio duration (25 FPS)
   - Processes video in 81-frame chunks sequentially
   - Merges all chunks with ffmpeg at the end
   - Workflow: `wanvideo_2_1_14B_I2V_InfiniteTalk_example_03API.json`
   - Node mappings:
     - Image: node 284 (LoadImage)
     - Audio: node 125 (LoadAudio)
     - Prompt: node 241 (WanVideoTextEncodeCached)
     - Max frames: node 270 (INTConstant)
     - Width: node 245 (INTConstant)
     - Height: node 246 (INTConstant)

### Files Modified

1. **FlipPix.UI/ViewModels/VideoGeneratorViewModel.cs**
   - Added `InfiniteTalkVM` property for InfiniteTalkViewModel
   - Initialized InfiniteTalkVM in constructor (with lmStudioService dependency)
   - Added InfiniteTalkVM to PlayRequested event forwarding
   - Added InfiniteTalkVM to PropertyChanged forwarding
   - Added `#region InfiniteTalkVM Backward Compatibility Properties` with:
     - Image properties: `InfiniteTalkImagePath`, `InfiniteTalkImagePreview`, `InfiniteTalkImageInfo`
     - Audio properties: `InfiniteTalkAudioPath`, `InfiniteTalkAudioInfo`
     - Prompt properties: `InfiniteTalkPrompt`, `InfiniteTalkWidth`, `InfiniteTalkHeight`
     - Processing state: `IsProcessingInfiniteTalk`, `InfiniteTalkProcessingStatus`, `InfiniteTalkProcessingProgress`
     - Result state: `HasInfiniteTalkResult`, `InfiniteTalkResultPath`, `InfiniteTalkVideoInfo`
     - Frame calculation: `InfiniteTalkAudioDuration`, `InfiniteTalkTotalFrames`, `InfiniteTalkTotalChunks`, `InfiniteTalkEstimatedDuration`
     - LMStudio analysis: `InfiniteTalkIsAnalyzing`, `CanInfiniteTalkAnalyzeImage`, `CanInfiniteTalkEnhancePrompt`, `ShowInfiniteTalkVideoPrompt`, `InfiniteTalkAnalysisResult`, `HasInfiniteTalkAnalysis`
     - Commands: `SelectInfiniteTalkImageCommand`, `SelectInfiniteTalkAudioCommand`, `GenerateInfiniteTalkVideoCommand`, `PlayInfiniteTalkVideoCommand`, `OpenInfiniteTalkResultFolderCommand`, `SendInfiniteTalkToEditCameraCommand`, `AnalyzeInfiniteTalkImageCommand`, `EnhanceInfiniteTalkPromptCommand`
   - Updated `Dispose()` method to include InfiniteTalkVM

2. **FlipPix.UI/VideoGeneratorWindow.xaml**
   - Added new TabItem "🎤 Infinite Talk" (Tab 6)
   - Added source image input with browse button and preview
   - Added "Analyze Image" button (calls LMStudio with image-analysis-prompt.md)
   - Added analysis result TextBox (read-only, shown after analysis)
   - Added audio file input with browse button
   - Added "Enhance Prompt" button (calls LMStudio with ltx-audio-video.md)
   - Added video prompt TextBox (hidden until "Enhance Prompt" is clicked)
   - Added video settings (Width/Height)
   - Added "Generate Video" button
   - Added processing status display with progress bar
   - Added result video section with MediaElement player
   - Added Play, Open Folder, Send to Edit Camera buttons
   - Added collapsible processing log expander
   - Used BoolToVisibilityConverter for conditional UI elements

### Summary

Implemented a new "Infinite Talk" tab in the Video Generator that:
1. Allows uploading a source image and analyzing it with LMStudio AI
2. Enhances the prompt using LMStudio with the ltx-audio-video.md system prompt
3. Allows uploading an audio file
4. Calculates required frames based on audio duration (25 FPS)
5. Generates video using Wan2.1 InfiniteTalk model in 81-frame chunks
6. Merges all chunks with ffmpeg for final output
7. Provides full video playback and file management
