# Correction Task: VACE Video — Switch Node 14 from VHS_LoadVideoPath to VHS_LoadVideo

## Found
`VHS_LoadVideoPath` requires an **absolute filesystem path** on the server (e.g. `/home/user/ComfyUI/input/file.mp4`).
We don't know the remote server's absolute path, so all relative path attempts (`input/file.mp4`, bare `file.mp4`) fail validation.

The correct node for uploaded files is **`VHS_LoadVideo`**, which looks up filenames directly from
ComfyUI's managed input folder — exactly where `UploadVideoAsync` puts the file.

## Files to Modify

### 1. `workflow/step1-chunkcreatorAPI.json`

Find node `"14"`. Its current content is:
```json
"14": {
  "inputs": {
    "video": "\"C:\\Users\\x2\\Videos\\vids1\\elechtra - DDhQwJcIkgP.mp4\"",
    "force_rate": 0,
    "custom_width": 0,
    "custom_height": 0,
    "frame_load_cap": 149,
    "skip_first_frames": 0,
    "select_every_nth": 1,
    "format": "Wan"
  },
  "class_type": "VHS_LoadVideoPath",
  "_meta": {
    "title": "Load Video (Path) 🎥🅤🅗🅢"
  }
}
```

Change ONLY two things:
- `class_type`: `"VHS_LoadVideoPath"` → `"VHS_LoadVideo"`
- `_meta.title`: `"Load Video (Path) 🎥🅤🅗🅢"` → `"Load Video 🎥🅤🅗🅢"`
- Leave ALL inputs exactly as-is (same field names, same values — they are compatible between both node types)

### 2. `FlipPix.UI/ViewModels/Video/VACEVideoViewModel.cs`

Find line ~618:
```csharp
WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "14", "video", "input/" + videoName);
```

Change to:
```csharp
WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "14", "video", videoName);
```

`VHS_LoadVideo` accepts just the bare filename (e.g. `kickfight.mp4`). No path prefix needed.

## 4. Completion Instructions
Update this file with a "Changelog" section detailing your changes for my review.
