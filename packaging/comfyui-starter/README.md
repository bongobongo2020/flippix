# FlipPix starter engine

For people who install FlipPix Mobile (iOS or Android) and have neither FlipPix nor ComfyUI. The
phone is a remote: every picture and video is made by the FlipPix desktop, which drives ComfyUI.
The starter is the smallest ComfyUI that can serve every page of the phone app. It is the official
Windows portable build plus the **11 node packs** the phone's graphs use, pinned, with their
Python dependencies already installed.

## What the user does

1. Run FlipPix Setup (`Install-FlipPix.bat` in a release). On the options page, choose
   **Set up the FlipPix engine for me** (the default when FlipPix knows of no ComfyUI).
2. Setup installs FlipPix and downloads the engine (about 2 GB; used straight from the release
   folder if `-IncludeEngine` put it there). It unpacks the engine to `%USERPROFILE%\ComfyUI_FlipPix`
   and points FlipPix at it: `ComfyUIFolderPath`, `run_flippix.bat` as the auto-start script, and
   `BaseUrl` = `127.0.0.1:8188`. It also switches the phone remote on in `remote.json`.
3. On the last page, **Choose models for your phone now** opens **FlipPix Models**. This is also in
   the Start Menu for later.
4. FlipPix starts ComfyUI itself, shows the pairing code, and the phone finds it on the LAN.

## FlipPix Models

`scripts/flippix-models.ps1`. The user ticks what the PC should make, and only missing files download:

| Feature | Graph | Download |
|---|---|---|
| Photo | Krea 2 | 19.6 GB |
| Dream | Qwen Image 2.1 + prompt enhancer | 17.2 GB |
| Detail | Z-Image base | 10.5 GB |
| Video & Story | MiniMax H3 Ref2VA | 40.6 GB |

- **Downloads resume.** Each file goes to `.part` through `curl -C -`. Stop, a closed window or a
  dropped connection all carry on from where they left off next time. A finished file must match
  its size in `starter.json` exactly, or it is thrown away.
- **"I already have models..."** searches a folder by file name and exact size.
  - A file at its usual place under a models folder (`<root>\diffusion_models\...`, or `unet\` /
    `clip\` for the older names) registers `<root>` in the engine's `extra_model_paths.yaml`
    as `flippix_found_N`, and nothing is copied. ComfyUI reads new roots only when it starts.
  - Anything else is hard-linked when it's on the same drive, and otherwise copied (robocopy)
    after asking.
- `-Check` prints what each feature still needs, without the window:
  `powershell -File scripts\flippix-models.ps1 -Check -ModelsDir Z:\`

## Why these packs and no others

`tools/starter_manifest.py` builds every graph the phone can submit with FlipPix.Remote's own
recipes (`tools/mobile-needs`). It then checks that every model is in `starter.json`, and, with
`--server`, that every node class is registered and comes from a listed pack.

The video graph used to need **Impact Pack**, **Easy-Use** and **SolAttn** too. Impact Pack and
Easy-Use pull in sam2 from git, transformers, diffusers and onnxruntime; SolAttn needs triton.
`VideoRecipe` now folds them away without changing what renders (checked on the server:
`execution_success`, 40 s for a 3 s clip):

- Every If/Else switch set to a constant is wired past, so the branch it never takes is pruned.
  That removes SolAttn and the video's RTX upscale.
- The phone never sends a driving audio, so the two `ImpactIfNone` probes always said false. The
  latent switch is now set to Ref2V's latent directly.
- `easy int` (seconds) is now core `PrimitiveInt`.

Pip packages come from each pack's `pip` list in `starter.json`, **never** a pack's
`requirements.txt` (one of those once replaced the CUDA torch with a CPU one). torch, torchvision,
torchaudio, numpy and pillow are held at the portable build's versions, and `PYTHONNOUSERSITE=1`
is set.

## Mirrored models

7 of the 19 files have no public copy that matches what the server runs, so they are mirrored to
`bongo2k22/flippix-models` by `mirror-models.ps1`:

- local conversions: the rank-31 resized H3 turbo LoRA and `qwen_3_4b_int8_convrot`
- `z_image_int8_convrot`: Comfy-Org's file of that name is 98 MB different
- the H3 latent upscaler: LBH's public file is 320 bytes different
- three Civitai LoRAs: Krea2-realism-V1, krea2filterbypass (a 160-byte stub), and skin texture
  style zib v2.1. **Check their licences allow redistribution** before the mirror is public.

Every other file downloads from its publisher (Comfy-Org, Kijai). Sizes are exact, and the
SHA-256 values are Hugging Face's LFS ids.

## Maintainer: building and publishing

```powershell
# once: pip install -U "huggingface_hub[cli]"; hf auth login
powershell -File packaging\comfyui-starter\mirror-models.ps1          # the 7 files, from Z:\
powershell -File packaging\comfyui-starter\build-starter.ps1          # -> release\comfyui-starter\*.7z
powershell -File packaging\comfyui-starter\publish-starter.ps1        # -> bongo2k22/flippix-comfyui
powershell -File scripts\make-release.ps1 [-IncludeEngine]            # FlipPix-Setup(.zip)
```

`build-starter.ps1` verifies the result by starting it with `--cpu` and running
`starter_manifest.py --server` against it. It also lists any pack that failed to import: on a
machine without an NVIDIA GPU, a pack that needs CUDA at import time can fail there and still
work on the user's PC.

**When a phone recipe or its workflow changes:**

1. Run `python tools/starter_manifest.py`. A new model shows up as `MISSING MODEL`.
2. Add it to `starter.json`, with its size and URL (`"mirror": true` if it has no public copy).
3. A new node pack goes into `packs`, pinned, with only the pip packages it needs.
4. Bump `version`, rebuild, and publish.
