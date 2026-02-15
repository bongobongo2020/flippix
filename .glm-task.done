# Task: Auto-Copy Generated Images and Videos to Local User Folders

## 1. Context & Objective

After generating images or videos, the app currently saves them to `{AppBaseDir}/output/{subfolder}/` or ComfyUI output folders. The user wants each generated file to **also** be automatically copied to a well-known local folder for easy access:

- **Images** → `C:\Users\x2\Pictures\flippix-images\`
- **Videos** → `C:\Users\x2\Videos\flippix-vids\`

If these folders don't exist, they should be created automatically.

## 2. Approach: Centralized Helper Service

Create a single static helper class that all ViewModels call after saving a file. This avoids scattering duplicate copy logic across 15+ locations.

## 3. Files to Create

### `FlipPix.UI/Services/LocalCopyService.cs`

Create a new static utility class:

```csharp
using System;
using System.IO;
using System.Threading.Tasks;

namespace FlipPix.UI.Services;

/// <summary>
/// Copies generated images and videos to well-known local user folders.
/// </summary>
public static class LocalCopyService
{
    private static readonly string ImageDestination = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "flippix-images");

    private static readonly string VideoDestination = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "flippix-vids");

    /// <summary>
    /// Copies a generated image file to the local pictures folder.
    /// </summary>
    public static async Task CopyImageAsync(string sourcePath)
    {
        try
        {
            Directory.CreateDirectory(ImageDestination);
            var destPath = Path.Combine(ImageDestination, Path.GetFileName(sourcePath));
            destPath = GetUniqueFilePath(destPath);
            File.Copy(sourcePath, destPath, true);
        }
        catch
        {
            // Silently fail — copying to local is a convenience, not critical
        }
    }

    /// <summary>
    /// Writes image bytes to the local pictures folder.
    /// </summary>
    public static async Task CopyImageAsync(byte[] imageData, string fileName)
    {
        try
        {
            Directory.CreateDirectory(ImageDestination);
            var destPath = Path.Combine(ImageDestination, fileName);
            destPath = GetUniqueFilePath(destPath);
            await File.WriteAllBytesAsync(destPath, imageData);
        }
        catch
        {
            // Silently fail
        }
    }

    /// <summary>
    /// Copies a generated video file to the local videos folder.
    /// </summary>
    public static async Task CopyVideoAsync(string sourcePath)
    {
        try
        {
            Directory.CreateDirectory(VideoDestination);
            var destPath = Path.Combine(VideoDestination, Path.GetFileName(sourcePath));
            destPath = GetUniqueFilePath(destPath);
            File.Copy(sourcePath, destPath, true);
        }
        catch
        {
            // Silently fail
        }
    }

    /// <summary>
    /// If a file already exists at the path, appends a numeric suffix to avoid overwriting.
    /// </summary>
    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int counter = 1;

        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name}_{counter}{ext}");
            counter++;
        } while (File.Exists(candidate));

        return candidate;
    }
}
```

## 4. Files to Modify

Add a call to `LocalCopyService` immediately after each image/video is saved. The pattern is simple — after the existing `File.WriteAllBytesAsync` or `File.Copy`, add one line.

### Image Save Points — Add `await LocalCopyService.CopyImageAsync(outputPath);`

Add `using FlipPix.UI.Services;` to each file if not already present, then add the copy call right after the existing save line:

| File | After Line | Existing Code | Add After |
|------|-----------|---------------|-----------|
| `ViewModels/ImageGeneratorViewModel.cs` | ~703 | `await File.WriteAllBytesAsync(outputPath, outputImage);` | `await LocalCopyService.CopyImageAsync(outputPath);` |
| `ViewModels/ImageGeneratorViewModel.cs` | ~2838 | `await File.WriteAllBytesAsync(outputPath, outputImage);` | `await LocalCopyService.CopyImageAsync(outputPath);` |
| `ViewModels/StoryImageGeneratorViewModel.cs` | ~574 | `await File.WriteAllBytesAsync(outputPath, outputImage);` | `await LocalCopyService.CopyImageAsync(outputPath);` |
| `ViewModels/StoryImageGeneratorFViewModel.cs` | ~372 | `await File.WriteAllBytesAsync(outputPath, outputImage);` | `await LocalCopyService.CopyImageAsync(outputPath);` |
| `ViewModels/StoryImageGeneratorQViewModel.cs` | ~336 | `await File.WriteAllBytesAsync(outputPath, outputImage);` | `await LocalCopyService.CopyImageAsync(outputPath);` |
| `ViewModels/StoryImageGeneratorAmateurViewModel.cs` | ~270 | `await File.WriteAllBytesAsync(outputPath, outputImage);` | `await LocalCopyService.CopyImageAsync(outputPath);` |
| `ViewModels/AmateurGeneratorViewModel.cs` | ~553 | `await File.WriteAllBytesAsync(outputPath, outputImage);` | `await LocalCopyService.CopyImageAsync(outputPath);` |
| `ViewModels/CameraAngleViewModel.cs` | ~337 | `await File.WriteAllBytesAsync(outputPath, outputImage);` | `await LocalCopyService.CopyImageAsync(outputPath);` |
| `ViewModels/FlipPixViewModel.cs` | ~818 | `await File.WriteAllBytesAsync(outputPath, outputImage);` | `await LocalCopyService.CopyImageAsync(outputPath);` |
| `ViewModels/FlipPixViewModel.cs` | ~1841 | `await File.WriteAllBytesAsync(outputPath, outputImage);` | `await LocalCopyService.CopyImageAsync(outputPath);` |
| `ViewModels/ImageAnalyzerViewModel.cs` | ~1929 | `await File.WriteAllBytesAsync(outputPath, outputImage);` | `await LocalCopyService.CopyImageAsync(outputPath);` |

### Video Save Points — Add `await LocalCopyService.CopyVideoAsync(...)`

| File | After Line | Context | Add After |
|------|-----------|---------|-----------|
| `ViewModels/Video/LTX2AudioViewModel.cs` | ~590 | After `ResultVideoPath = finalOutputPath;` | `await LocalCopyService.CopyVideoAsync(finalOutputPath);` |
| `ViewModels/Video/MochaVideoViewModel.cs` | ~496 | After `ResultVideoPath = finalOutputPath;` | `await LocalCopyService.CopyVideoAsync(finalOutputPath);` |
| `ViewModels/Video/VACEVideoViewModel.cs` | ~515 | After `ResultVideoPath = outputVideo;` | `await LocalCopyService.CopyVideoAsync(outputVideo);` |
| `ViewModels/Video/VideoProcessingBaseViewModel.cs` | ~352 | After `File.Copy(latestVideo, outputPath, true);` | `await LocalCopyService.CopyVideoAsync(outputPath);` |
| `ViewModels/StoryVideoViewModel.cs` | ~716 | After `File.Copy(generatedVideos[i], destPath, true);` | `await LocalCopyService.CopyVideoAsync(destPath);` |
| `ViewModels/StoryVideoViewModel.cs` | ~1151 | After `File.Copy(latestFile, outputPath, true);` | `await LocalCopyService.CopyVideoAsync(outputPath);` |

### Important Notes for Implementation

1. **Do NOT modify `.bak` files** — skip `VideoGeneratorViewModel.cs.bak`.
2. The `using FlipPix.UI.Services;` may already exist in some files. Only add it if missing.
3. The copy calls should be fire-and-forget safe — the `try/catch` in `LocalCopyService` ensures failures don't break the main flow.
4. Use `Environment.SpecialFolder.MyPictures` and `Environment.SpecialFolder.MyVideos` to resolve paths dynamically (works for any Windows user, not just `x2`).

## 5. Completion Instructions
Update this file with a "Changelog" section detailing your changes for my review.

---

## Changelog

### Files Created
- `FlipPix.UI/Services/LocalCopyService.cs` - New static utility class that copies generated images and videos to well-known local user folders (Pictures/flippix-images and Videos/flippix-vids). Includes automatic folder creation and unique filename handling.

### Image Generator ViewModels Modified (11 locations)
All files already had `using FlipPix.UI.Services;` - added `await LocalCopyService.CopyImageAsync(outputPath);` after each save:

| File | Line(s) | Context |
|------|---------|---------|
| `ImageGeneratorViewModel.cs` | 703, 2838 | Regular and queue-based image generation |
| `StoryImageGeneratorViewModel.cs` | 574 | Story image generation |
| `StoryImageGeneratorFViewModel.cs` | 372 | Story F image generation |
| `StoryImageGeneratorQViewModel.cs` | 336 | Story Q image generation |
| `StoryImageGeneratorAmateurViewModel.cs` | 270 | Story amateur image generation |
| `AmateurGeneratorViewModel.cs` | 553 | Amateur image generation |
| `CameraAngleViewModel.cs` | 337 (inside loop) | Camera angle batch generation |
| `FlipPixViewModel.cs` | 818, 1841 | Camera control and queue processing |
| `ImageAnalyzerViewModel.cs` | 1929 | Image analyzer queue processing |

### Video Generator ViewModels Modified (6 locations)
All files already had `using FlipPix.UI.Services;` - added `await LocalCopyService.CopyVideoAsync(...)` after each save:

| File | Line(s) | Context |
|------|---------|---------|
| `LTX2AudioViewModel.cs` | 590 | After setting ResultVideoPath |
| `MochaVideoViewModel.cs` | 496 | After setting ResultVideoPath |
| `VACEVideoViewModel.cs` | 515 | After setting ResultVideoPath |
| `VideoProcessingBaseViewModel.cs` | 352 | After File.Copy of latest video |
| `StoryVideoViewModel.cs` | 716 (inside loop) | Batch video copying |
| `StoryVideoViewModel.cs` | 1151 | Single video copy from LTX-2 output |

### Notes
- All modified files already had the required `using FlipPix.UI.Services;` statement
- No `.bak` files were modified
- Copy operations are wrapped in try/catch in LocalCopyService to prevent any failures from affecting main application flow
