# Task: Show clickable thumbnail instead of "Completed" in image queue

## 1. Context & Objective
When an image finishes generating, the queue list currently shows "✅ Completed" text. Instead, it should display a small clickable thumbnail of the generated image. Clicking the thumbnail should open the image in the default viewer (same pattern used by `StoryPromptItem`).

## 2. Files to Modify

### `FlipPix.UI/Models/ImagePromptQueueItem.cs`
### `FlipPix.UI/ImageGeneratorWindow.xaml`

## 3. Implementation Steps

### Step 1: Update `ImagePromptQueueItem.cs`

Add the following using statements at the top if not already present:
```csharp
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Input;
using System.Windows.Media.Imaging;
```

**Make `OutputImagePath` trigger thumbnail loading.** Change it from an auto-property to a full property with backing field:
```csharp
private string? _outputImagePath;
private BitmapImage? _thumbnailImage;

public string? OutputImagePath
{
    get => _outputImagePath;
    set
    {
        if (_outputImagePath != value)
        {
            _outputImagePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasOutputImage));
            LoadThumbnail();
        }
    }
}

public bool HasOutputImage => !string.IsNullOrEmpty(OutputImagePath);

[JsonIgnore]
public BitmapImage? ThumbnailImage
{
    get => _thumbnailImage;
    private set
    {
        _thumbnailImage = value;
        OnPropertyChanged();
    }
}

[JsonIgnore]
public ICommand OpenImageCommand { get; }
```

**Add a constructor** to initialize the command:
```csharp
public ImagePromptQueueItem()
{
    OpenImageCommand = new RelayCommand(OpenImage, () => !string.IsNullOrEmpty(OutputImagePath));
}
```

**Add `LoadThumbnail()` and `OpenImage()` methods** (copy pattern from `StoryPromptItem`):
```csharp
private void LoadThumbnail()
{
    if (string.IsNullOrEmpty(_outputImagePath) || !File.Exists(_outputImagePath))
    {
        ThumbnailImage = null;
        return;
    }

    try
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(_outputImagePath, UriKind.Absolute);
        bitmap.DecodePixelHeight = 60;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        ThumbnailImage = bitmap;
    }
    catch
    {
        ThumbnailImage = null;
    }
}

private void OpenImage()
{
    if (!string.IsNullOrEmpty(OutputImagePath) && File.Exists(OutputImagePath))
    {
        Process.Start(new ProcessStartInfo(OutputImagePath) { UseShellExecute = true });
    }
}
```

### Step 2: Update `ImageGeneratorWindow.xaml`

Find the **Status** section in the image generator queue DataTemplate (around lines 1172-1197, the one inside the first queue `ItemsControl` that binds to `QueueItems` — NOT the Analyzer queue). Replace the status `StackPanel` (Grid.Column="1") with:

```xml
<!-- Status / Thumbnail -->
<StackPanel Grid.Column="1"
           Orientation="Horizontal"
           VerticalAlignment="Center"
           Margin="10,0">
    <!-- Show status text when NOT completed -->
    <TextBlock Text="{Binding StatusDisplay}"
              FontSize="11"
              FontWeight="Bold"
              Foreground="{Binding StatusColor}">
        <TextBlock.Style>
            <Style TargetType="TextBlock">
                <Setter Property="Visibility" Value="Visible"/>
                <Style.Triggers>
                    <DataTrigger Binding="{Binding Status}" Value="Completed">
                        <Setter Property="Visibility" Value="Collapsed"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </TextBlock.Style>
    </TextBlock>

    <!-- Progress bar during processing -->
    <ProgressBar Value="{Binding Progress}"
               Width="50"
               Height="12"
               Margin="5,0">
        <ProgressBar.Style>
            <Style TargetType="ProgressBar">
                <Setter Property="Visibility" Value="Collapsed"/>
                <Style.Triggers>
                    <DataTrigger Binding="{Binding Status}" Value="Processing">
                        <Setter Property="Visibility" Value="Visible"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </ProgressBar.Style>
    </ProgressBar>

    <!-- Clickable thumbnail when completed -->
    <Button Command="{Binding OpenImageCommand}"
           Background="Transparent"
           BorderThickness="0"
           Cursor="Hand"
           ToolTip="Click to open image"
           Padding="0">
        <Button.Style>
            <Style TargetType="Button">
                <Setter Property="Visibility" Value="Collapsed"/>
                <Style.Triggers>
                    <DataTrigger Binding="{Binding Status}" Value="Completed">
                        <Setter Property="Visibility" Value="Visible"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </Button.Style>
        <Image Source="{Binding ThumbnailImage}"
              Height="50"
              Stretch="Uniform"/>
    </Button>
</StackPanel>
```

**Key points:**
- The `TextBlock` showing status text is **hidden** when status is "Completed"
- The `Button` with thumbnail is **shown only** when status is "Completed"
- The progress bar remains visible only during "Processing" (unchanged)
- Clicking the thumbnail fires `OpenImageCommand` which opens the image in the default viewer

## 4. Completion Instructions
Update this file with a "Changelog" section detailing your changes for my review.

---

## Changelog

### 2026-02-06: Show clickable thumbnail instead of "Completed" in image queue

**Modified Files:**
- `FlipPix.UI/Models/ImagePromptQueueItem.cs`
- `FlipPix.UI/ImageGeneratorWindow.xaml`

**Changes to `ImagePromptQueueItem.cs`:**
- Added using statements: `System.Diagnostics`, `System.IO`, `System.Text.Json.Serialization`, `System.Windows.Input`, `System.Windows.Media.Imaging`
- Added private fields: `_outputImagePath`, `_thumbnailImage`
- Converted `OutputImagePath` from auto-property to full property with backing field that triggers `LoadThumbnail()` on change
- Added `HasOutputImage` boolean property
- Added `ThumbnailImage` BitmapImage property (JsonIgnore)
- Added `OpenImageCommand` ICommand property (JsonIgnore)
- Added constructor initializing `OpenImageCommand` with `RelayCommand`
- Added `LoadThumbnail()` method to load and cache a 60px-high thumbnail
- Added `OpenImage()` method to open the output image in default system viewer

**Changes to `ImageGeneratorWindow.xaml`:**
- Replaced Status section StackPanel with enhanced version:
  - Status text (TextBlock) now hidden when Status is "Completed"
  - Uses `StatusDisplay` and `StatusColor` bindings for proper styling
  - Added clickable thumbnail Button shown only when Status is "Completed"
  - Button opens image via `OpenImageCommand`
  - Thumbnail image displayed at 50px height with Uniform stretch
  - Progress bar behavior unchanged (visible only during Processing)

**Behavior:**
- When image generation completes, the status text is replaced with a clickable thumbnail of the generated image
- Clicking the thumbnail opens the image in the system's default viewer
- Same pattern as used in `StoryPromptItem` for consistency
