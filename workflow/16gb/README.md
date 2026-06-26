# 16 GB-VRAM workflow variants

FlipPix loads workflows from here **instead of** the matching file under `workflow/` when the app
is in low-VRAM mode — i.e. ComfyUI's `/system_stats` reports a GPU at/below ~16 GB, or the user
forced the "16 GB" tier in Settings. Routing is done by `FlipPix.UI/Services/WorkflowLocator.cs`,
which mirrors the relative path: `workflow/<x>` → `workflow/16gb/<x>`. If a workflow has **no**
variant here, the full-size one is used unchanged.

Each file is a copy of its full-size counterpart with conservative, structure-preserving tweaks
that reduce peak VRAM. They are **provisional**: designed to fit 16 GB but not yet OOM-verified on
16 GB hardware. Run each end-to-end on a real 16 GB card and tune if it still OOMs.

| Variant | Source | Changes |
|---|---|---|
| `image/zimage/base/Zib-Zit.json` | `workflow/image/zimage/base/Zib-Zit.json` | UNETLoader `weight_dtype` → `fp8_e4m3fn` (nodes 405, 746) |
| `qwen-edit-camera-API.json` | `workflow/qwen-edit-camera-API.json` | latent 1328×800 → 1024×640 (node 112); edit input 1.0 → 0.75 MP (node 93) |
| `video/ltx/seed-hunter-api.json` | `workflow/video/ltx/seed-hunter-api.json` | diffusion loader `OTUNetLoaderW8A8` (22B int8, ~22 GB) → `UnetLoaderGGUF` GGUF (node 5025:5220); preview longest side 1536 → 1024 (node 5053); clip length 20 s → 5 s (node 5074) |

## Video variant — extra model download required
The full-size SeedHunt workflow loads **LTX-2.3 22B int8 (~22 GB) + Gemma-3-12B**, which can't fit
16 GB. The 16 GB variant swaps **only the diffusion loader** to a GGUF quant (every node ID that
`SeedHuntViewModel` patches is untouched, so the tab keeps working) and trims resolution/length.
It needs two things the full install may not have:

1. **The GGUF node pack** — `city96/ComfyUI-GGUF` (already in `scripts/flippix-custom-nodes.txt`,
   so the non-minimal installer adds it; install via ComfyUI-Manager otherwise).
2. **A GGUF weight** in `models/unet/`. The workflow references **`LTX-2.3-22B-distilled-1.1-Q3_K_S.gguf`** (~14 GB):
   `https://huggingface.co/QuantStack/LTX-2.3-GGUF/resolve/main/LTX-2.3-distilled-1.1/LTX-2.3-22B-distilled-1.1-Q3_K_S.gguf`
   A **full** install on the 16gb tier auto-offers this download (`setup-comfyui-fresh.ps1`, manifest
   `scripts/flippix-models-16gb-video.txt`). To grab it unattended: add `-DownloadModels`.

   Quant ladder for the same model (pick by card / quality, then update node 5025:5220's `unet_name`):
   - `…-Q2_K.gguf` 12.4 GB — safest fit, lowest quality
   - `…-Q3_K_S.gguf` 14 GB — **shipped default**
   - `…-Q3_K_M.gguf` 14.7 GB — slightly better, tighter
   - `…-Q4_K_S.gguf` 16.7 GB / `…-Q4_K_M.gguf` 17.8 GB — only with VRAM headroom / GGUF CPU offload

The video VAEs, audio VAE, spatial upscaler, and Gemma text encoder are kept as-is (the user already
has them and they aren't the binding VRAM constraint — Gemma is offloaded before diffusion runs).
Still **provisional**: SeedHunt runs multiple sampler passes + a spatial upscale, so confirm it fits
on a real 16 GB card and drop to Q2_K if it OOMs. Licensing: the GGUF weights carry the
ltx-2-community-license-agreement, same as the original LTX-2.3 weights.

## Adding more
1. Copy the full-size workflow to the same relative path under `workflow/16gb/`.
2. Reduce VRAM with structure-safe edits: load models as GGUF/fp8, lower resolution/frame count,
   enable tiled VAE decode.
3. The ViewModel must resolve its path through `WorkflowLocator.Resolve(...)` (most core tabs
   already do; others fall back to the full-size file automatically).
