# Task: Set Application Icon for FlipPix

## 1. Context & Objective
The app needs to display `flippix.ico` as its icon on Windows desktop shortcuts, taskbar pins, and window title bars. The icon file already exists at the project root: `flippix.ico`.

## 2. Files to Modify

### `FlipPix.UI/FlipPix.UI.csproj`
Add the `ApplicationIcon` property inside the existing `<PropertyGroup>` to embed the icon into the compiled `.exe`:
```xml
<ApplicationIcon>..\flippix.ico</ApplicationIcon>
```

Also add a `<Resource>` item so WPF can reference the icon at runtime:
```xml
<ItemGroup>
  <Resource Include="..\flippix.ico">
    <Link>flippix.ico</Link>
  </Resource>
</ItemGroup>
```

### All Window XAML files
Add `Icon="flippix.ico"` attribute to the root `<Window>` element in each of these files:

- `FlipPix.UI/FlipPixWindow.xaml`
- `FlipPix.UI/I2V2AWindow.xaml`
- `FlipPix.UI/OllamaWindow.xaml`
- `FlipPix.UI/RemoteSetupWindow.xaml`
- `FlipPix.UI/SettingsWindow.xaml`
- `FlipPix.UI/SetupChoiceWindow.xaml`
- `FlipPix.UI/StoryVideoWindow.xaml`
- `FlipPix.UI/VideoGeneratorWindow.xaml`
- `FlipPix.UI/ImageGeneratorWindow.xaml`
- `FlipPix.UI/ImageAnalyzerWindow.xaml`
- `FlipPix.UI/ComfyUIFolderSetupWindow.xaml`

## 3. Implementation Steps

1. Open `FlipPix.UI/FlipPix.UI.csproj`.
2. Inside the existing `<PropertyGroup>`, add: `<ApplicationIcon>..\flippix.ico</ApplicationIcon>`
3. Add a new `<ItemGroup>` with a `<Resource>` entry for the icon (see above).
4. For each `.xaml` Window file listed above, add `Icon="flippix.ico"` to the root `<Window>` tag.

## 4. Completion Instructions
Update this file with a "Changelog" section detailing your changes for my review.
