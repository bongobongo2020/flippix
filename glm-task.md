# Task: Fix Aspect Ratio Dropdown Labels — Remove Numbers & Swap Portrait/Landscape

## 1. Context & Objective
The aspect ratio dropdown menus currently show resolution numbers (e.g. "Portrait 1088x1600") — remove the numbers so they just say "Portrait", "Landscape", "Square".

Additionally, the labels are swapped: selecting "Portrait" (index 0) actually generates a landscape image, and "Landscape" (index 1) generates a portrait image. Fix this by swapping the label text only — do NOT change any resolution values or index mappings.

## 2. Files to Modify

### A. XAML Files — Update ComboBoxItem Content

- `FlipPix.UI/ImageGeneratorWindow.xaml` — 2 ComboBox instances (lines ~507-509 and ~631-633)
- `FlipPix.UI/ImageAnalyzerWindow.xaml` — 1 ComboBox instance (lines ~409-411)

**Change all occurrences from:**
```xml
<ComboBoxItem Content="Portrait 1088x1600"/>
<ComboBoxItem Content="Landscape 1600x1088"/>
<ComboBoxItem Content="Square 1600x1600"/>
```
**To:**
```xml
<ComboBoxItem Content="Landscape"/>
<ComboBoxItem Content="Portrait"/>
<ComboBoxItem Content="Square"/>
```

Note: Index 0 becomes "Landscape", Index 1 becomes "Portrait". This is intentional — it matches what the resolutions actually produce.

### B. `FlipPix.UI/Models/ImageAnalyzerQueueItem.cs` — Update AspectRatioDisplay

The `AspectRatioDisplay` property (~line 151) maps indices to display names. Swap them to match:

**Change from:**
```csharp
public string AspectRatioDisplay => AspectRatioIndex switch
{
    0 => "Portrait",
    1 => "Landscape",
    2 => "Square",
    _ => "?"
};
```
**To:**
```csharp
public string AspectRatioDisplay => AspectRatioIndex switch
{
    0 => "Landscape",
    1 => "Portrait",
    2 => "Square",
    _ => "?"
};
```

### C. Do NOT modify
- Any resolution values or tuples in the ViewModels
- Any aspect ratio index logic
- Any other files

## 3. Implementation Steps
1. Update all 3 XAML ComboBoxes (swap labels + remove numbers)
2. Update `AspectRatioDisplay` in ImageAnalyzerQueueItem.cs
3. Verify no other display mappings exist that reference "Portrait"/"Landscape" for these indices

## 4. Completion Instructions
Update this file with a "Changelog" section detailing your changes for my review.

---

## Changelog

### Changes Implemented

#### 1. FlipPix.UI/ImageGeneratorWindow.xaml
- **Line 507-509**: Changed `Portrait 1088x1600` → `Landscape`, `Landscape 1600x1088` → `Portrait`, `Square 1600x1600` → `Square`
- **Line 631-633**: Same changes applied to second ComboBox instance

#### 2. FlipPix.UI/ImageAnalyzerWindow.xaml
- **Line 409-411**: Changed `Portrait 1088x1600` → `Landscape`, `Landscape 1600x1088` → `Portrait`, `Square 1600x1600` → `Square`

#### 3. FlipPix.UI/Models/ImageAnalyzerQueueItem.cs
- **Lines 151-157**: Updated `AspectRatioDisplay` property:
  - Index 0: `"Portrait"` → `"Landscape"`
  - Index 1: `"Landscape"` → `"Portrait"`
  - Index 2: `"Square"` (unchanged)

### Summary
All aspect ratio dropdown labels now show clean names without resolution numbers. Portrait/Landscape labels have been swapped to match the actual resolutions they produce (Index 0 = Landscape 1600x1088, Index 1 = Portrait 1088x1600).
