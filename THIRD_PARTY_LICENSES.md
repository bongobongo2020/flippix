# Third-party licenses

FlipPix and the FlipPix iOS Companion **do not include any model weights**. The setup wizard downloads
each model directly from its publisher onto your computer, and your use of each model is governed by
its own license, which you accept on the wizard's License page. Custom ComfyUI node packs are likewise
cloned from their own repositories.

## FlipPix iOS Companion — models (`scripts/flippix-models-ios.txt`)

| Model | Used for | Source | License |
|---|---|---|---|
| Krea 2 Raw (int8 ConvRot), Krea 2 turbo LoRA, Qwen3-VL 4B text encoder | Pictures | [Comfy-Org/Krea-2](https://huggingface.co/Comfy-Org/Krea-2) | [Krea 2 Community License](https://krea.ai/krea-2-licensing) |
| Wan 2.1 VAE | Pictures (Krea 2 decodes with it) | [Comfy-Org/Wan_2.1_ComfyUI_repackaged](https://huggingface.co/Comfy-Org/Wan_2.1_ComfyUI_repackaged) | Apache-2.0 |
| MiniMax H3 Ref2VA (pruned int8), Qwen3-VL 32B text encoder, video + audio VAE | Video | [Comfy-Org/MiniMax-H3](https://huggingface.co/Comfy-Org/MiniMax-H3) | [MiniMax H3 Community License](https://huggingface.co/MiniMaxAI/MiniMax-H3/blob/main/LICENSE) |
| MiniMax H3 turbo LoRA (4-step, 768p) | Video | [lightx2v/Minimax-h3-Turbo](https://huggingface.co/lightx2v/Minimax-h3-Turbo) | Apache-2.0 |
| MiniMax H3 latent upscaler (3D conv v1) | Video | [LBH-123-AI/Minimax_h3_latent_Upscaler](https://huggingface.co/LBH-123-AI/Minimax_h3_latent_Upscaler) | Apache-2.0 |
| Qwen2.5-VL 7B Instruct (Q4_K_M + mmproj) | Writing assistant | [ggml-org/Qwen2.5-VL-7B-Instruct-GGUF](https://huggingface.co/ggml-org/Qwen2.5-VL-7B-Instruct-GGUF) | Apache-2.0 |
| nsfw_image_detection (ONNX uint8) | Content filter | [onnx-community/nsfw_image_detection-ONNX](https://huggingface.co/onnx-community/nsfw_image_detection-ONNX), from [Falconsai/nsfw_image_detection](https://huggingface.co/Falconsai/nsfw_image_detection) | Apache-2.0 |

### Terms that affect the iOS app

**Krea 2 Community License**
- Commercial use only while your company's total annual revenue is under **US$1,000,000**; above that, an
  enterprise license from Krea is required (opensource@krea.ai).
- **Content filtering is required** (§4.2). The companion screens every picture and video, and every photo
  sent from the phone, with the classifier above, and will not turn the phone link on without it.
- No circumventing the model's usage restrictions (§4.1c). The phone's Krea 2 graph does not load the
  "filter bypass" LoRA from the authored workflow, nor the unlicensed realism LoRA.
- Distribution must carry the Notice: *"Krea 2 is licensed under the Krea 2 Community License Agreement.
  For more information, visit https://krea.ai/krea-2-licensing."* (in the companion's `NOTICE.txt`).

**MiniMax H3 Community License**
- **Not licensed in the European Union, the United Kingdom, the Republic of Korea or the United States of
  America** ("Excluded Territories"). The App Store listing must exclude those storefronts.
- A commercial product must **prominently display "MiniMax H3"** in its UI. The companion shows it, and
  sends it to the phone as a credit (`StatusDto.Credits`) for the app to show.
- Safeguards against prohibited uses and outputs, and an **accessible way to report violations**, are
  required (§V.5), and users must be bound to the use restrictions (§V.2): the wizard's License page.
- Above US$20M yearly revenue, written authorization from MiniMax is required.

## FlipPix desktop — models (`scripts/flippix-models*.txt`)

All Apache-2.0: Qwen-Image / Qwen-Image-Edit-2509 and the Qwen2.5-VL text encoder (Comfy-Org), Z-Image
Turbo and the Qwen3-4B encoder (Comfy-Org), Wan 2.1 / 2.2 (Comfy-Org, lightx2v), Qwen-Image-Lightning and
the multi-angle LoRA. The optional LTX-2.3 GGUF is under the **LTX-2 Community License** (a paid license is
needed by companies with US$10M+ annual revenue).

Not downloaded by default: the PixelDiT Gemma text encoder used by the Z-Image 4K, Ideogram and Klein
Control workflows is under **NVIDIA's NSCLv1, non-commercial (research or evaluation) use only**. FlipPix
only fetches it on demand, from the Missing Models window, when one of those workflows is opened.

## Software

| Component | License |
|---|---|
| ComfyUI (downloaded by Setup) | GPL-3.0 |
| llama.cpp / llama-server (downloaded by Setup) | MIT |
| ONNX Runtime | MIT |
| SixLabors.ImageSharp | Six Labors Split License (Apache-2.0 under US$1M annual revenue; commercial license above) |
| CommunityToolkit.Mvvm | MIT |

## ComfyUI custom-node packs installed for the iOS Companion (`scripts/flippix-custom-nodes-ios.txt`)

| Pack | License |
|---|---|
| Comfy-Org/ComfyUI-Manager | GPL-3.0 |
| ClownsharkBatwing/RES4LYF | see repository |
| pythongosssss/ComfyUI-Custom-Scripts | MIT |
| BobJohnson24/ComfyUI-INT8-Fast | AGPL-3.0 |
| rgthree/rgthree-comfy | MIT |
| Comfy-Org/Nvidia_RTX_Nodes_ComfyUI | Apache-2.0 |
| kijai/ComfyUI-KJNodes | GPL-3.0 |
| PlagueKind/ComfyUI-PlagueKind-Nodes | MIT |
| LBH-123-AI/Comfyui_Minimax_h3_latent_Upscaler | MIT |
| xmarre/ComfyUI-Spectrum-MiniMax-H3 | GPL-3.0 |
| Urabewe/ComfyUI-AudioTools | MIT |
| Kosinkadink/ComfyUI-VideoHelperSuite | GPL-3.0 |

These run inside ComfyUI on your computer; neither FlipPix nor the companion links against them.
