# Task: Fix Sub-VM Property Forwarding — Image Preview & Generate Button

## 1. Context & Objective

There are two related runtime bugs in the Video Generator window:

**Bug 1 — Image preview never appears (VACE, Mocha, LTX2Audio tabs):**
The parent `VideoGeneratorViewModel` has aliased pass-through properties like:
```csharp
public BitmapImage? VaceBackgroundImagePreview { get => VaceVM.BackgroundImagePreview; ... }
public BitmapImage? MochaImagePreview           { get => MochaVM.ImagePreview; ... }
```
When sub-VMs fire `PropertyChanged("BackgroundImagePreview")` or `PropertyChanged("ImagePreview")`,
`ForwardPropertyChanged` re-fires the **same name** on the parent. But XAML binds to the **aliased names**
(`VaceBackgroundImagePreview`, `MochaImagePreview`, etc.) — so those bindings never get a change notification
and the UI never refreshes.

This affects every pass-through property where the parent renames the sub-VM property with a prefix:
`VaceBackground*`, `VaceForeground*`, `MochaImage*`, `LTX2AudioImage*`, `HasVACE*`, `HasMocha*`,
`IsProcessingVACE`, `IsProcessingMocha`, `IsProcessingLTX2Audio`, progress/log/result properties, etc.

**Bug 2 — Generate button stays disabled (same root cause):**
`CanGenerateMochaVideo`, `CanGenerateVACEVideo`, and `CanGenerateLTX2AudioVideo` bindings on the
generate buttons are also pass-through aliases. They suffer the same notification gap.

## 2. Files to Modify

- `FlipPix.UI/ViewModels/VideoGeneratorViewModel.cs`

## 3. Implementation Steps

### Step 1 — Replace `ForwardPropertyChanged`

Find the current `ForwardPropertyChanged` method:

```csharp
private void ForwardPropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    if (e.PropertyName == null) return;

    OnPropertyChanged(e.PropertyName);

    // When a sub-VM's CanGenerateVideo changes, also fire the parent VM's
    // correctly-named alias so XAML bindings like IsEnabled="{Binding CanGenerateMochaVideo}" update.
    if (e.PropertyName == "CanGenerateVideo")
    {
        if (sender == VaceVM)
            OnPropertyChanged(nameof(CanGenerateVACEVideo));
        else if (sender == MochaVM)
            OnPropertyChanged(nameof(CanGenerateMochaVideo));
        else if (sender == LTX2AudioVM)
            OnPropertyChanged(nameof(CanGenerateLTX2AudioVideo));
    }
}
```

Replace it with:

```csharp
private void ForwardPropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    if (e.PropertyName == null) return;

    // Re-fire with the original property name (handles any direct-name bindings).
    OnPropertyChanged(e.PropertyName);

    // Re-fire with empty string to refresh ALL bindings on this DataContext.
    // This is required because the parent VM exposes aliased pass-through properties
    // (e.g. VaceBackgroundImagePreview → VaceVM.BackgroundImagePreview). When the
    // sub-VM fires PropertyChanged("BackgroundImagePreview"), the XAML binding on
    // VaceBackgroundImagePreview would otherwise never see the notification.
    OnPropertyChanged(string.Empty);
}
```

That's the entire change. The `OnPropertyChanged(string.Empty)` call (equivalent to passing `null`)
tells WPF to re-evaluate every binding on this DataContext, so all aliased properties
(`VaceBackgroundImagePreview`, `MochaImagePreview`, `CanGenerateMochaVideo`, etc.) update correctly.

## 4. Completion Instructions

Update this file with a "Changelog" section detailing the change.

---

## Changelog

### 2026-02-20

**Fixed:** Sub-VM property forwarding causing image previews and generate buttons to not update

**Root Cause:** The `ForwardPropertyChanged` method only re-fired property change notifications with the original property name from sub-VMs. Since the parent `VideoGeneratorViewModel` exposes aliased pass-through properties (e.g., `VaceBackgroundImagePreview` → `VaceVM.BackgroundImagePreview`), XAML bindings to the aliased names never received notifications.

**Solution:** Modified `ForwardPropertyChanged` to also call `OnPropertyChanged(string.Empty)`, which tells WPF to re-evaluate ALL bindings on the DataContext.

**Impact:** This fixes:
- Image previews not appearing in VACE, Mocha, and LTX2Audio tabs
- Generate buttons staying disabled when they should be enabled
- All other aliased pass-through properties (`HasVACE*`, `HasMocha*`, `IsProcessing*`, progress/log/result properties)