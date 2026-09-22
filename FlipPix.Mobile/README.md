# FlipPix Mobile

A phone-sized FlipPix for Android: type an idea, tap once, and watch it develop. It drives the
same ComfyUI server as the desktop app, and optionally an OpenAI-style LLM server.

- `FlipPix.Mobile/`: all UI and logic (Avalonia 11, `net8.0`). It reuses `FlipPix.Core` and
  `FlipPix.ComfyUI`, so every graph goes through the desktop's pre-submit repairs.
- `FlipPix.Mobile.Android/`: the Android head (`net9.0-android`, min API 26). It holds no logic.

## Build and install

```sh
# Debug build straight onto a running emulator or a USB-debugging phone
dotnet build FlipPix.Mobile.Android -t:Install -c Debug

# A signed release APK to sideload
dotnet publish FlipPix.Mobile.Android -c Release
#   -> FlipPix.Mobile.Android/bin/Release/net9.0-android/publish/com.flippix.mobile-Signed.apk
```

The release APK is signed with the SDK's debug keystore. That's fine for sideloading, but a
Play Store upload needs a real keystore (`AndroidSigningKeyStore` and friends).

## First run

The app opens in Settings. Enter the ComfyUI address (for example `10.0.0.10:8188`) and,
optionally, the LLM address (`10.0.0.10:11434` for Ollama, `:1234` for LM Studio). Save tests
both. The phone must be able to reach the server: the same Wi-Fi, or a VPN such as Tailscale.
The manifest allows cleartext http because LAN servers don't speak https.

## Pages

| Page  | State | What it runs |
|-------|-------|--------------|
| Image | done  | Three looks, each a desktop graph run as authored with only prompt, canvas and seed written. **Photo** = `krea2RealismV1` (the SaveImageKJ→SaveImage swap is the desktop's too), **Dream** = `qwen21-prompt-enhancer` (the prompt goes into node 468 *only*), **Detail** = `z-image-base`. |
| Video | done  | MiniMax I2V (`h3-minimax-i2v.json`), the desktop's default render in one pass: 1–4 reference photos plus an idea, 5/10/15 s. The LLM (a **vision** model) writes the six-field Ref2VA scene from the photos using `prompts/prompt2json/h3-r2va.md`; without an LLM the idea is wrapped in that shape as written. It plays in the app through Android's VideoView, streamed from `/view`. |
| Story | done  | H3 Express, simplified: a story, an optional cast (1–3 photos) and 30 s / 1 min / 2 min give 3/6/12 shots of 10 s. The desktop's story chain is linked in as source, unchanged (`StoryBeatSheet`, `StoryContinuity`, `ClipChainWriter`, `LlmSampling`; the phone's `Compat/LMStudioService.cs` stands in for the desktop client). Each shot is rendered by the Video page's graph with the same cast pictures, and the finished shots play back to back. |

Workflows are **embedded** (see `FlipPix.Mobile.csproj`), not copied, so a phone has no
`workflow/` folder. If a desktop graph's node ids drift, `Workflows.Set` throws naming the
missing node. The mobile look then fails loudly instead of silently rendering the authored
prompt.

## Video details

- **Photos** are auto-rotated from EXIF, have their EXIF stripped, are shrunk to 1536 px and
  encoded as JPEG 92 (ImageSharp, not Skia, which ignores the rotation tag). Each one is uploaded
  once per session.
- **The graph** is Shipped stack, 0.7 MP finished (0.175 MP draft, 2× latent upscale), SLA 0.85
  with 64-row blocks, audio enhancement on, RTX off. Only sink `49` is kept; the continuation loop
  is pruned. The aspect ratio is the nearest ResolutionSelector option to the first photo.
- **Progress** comes as three runs: the draft sampler, the finish sampler, and a long post-process
  run. The take card shows them as drafting, finishing and final touches.
- **Playback**: `Controls/VideoSurface` is a `NativeControlHost`, and the Android head registers a
  VideoView factory. Native views draw above Avalonia, so nothing may overlap the player.
- **Measured on 10.0.0.10**: 5 s in 42 s (704×1024 with AAC audio). Qwen-VL via Ollama wrote the
  scene in about 50 s.

## Story details

- **Cast.** Each photo is described once by the vision LLM, and that line is handed to every call
  as text. With no photos, the LLM writes a portrait of the lead in the story's setting and the
  Photo look renders it; that picture becomes the cast.
- **Shots.** The beat sheet is asked to name people `PICTURE N`. `StoryRecipe.Retag` turns a
  pictured one into `<Subject N>` and an unpictured number into plain words, so a dangling tag never
  reaches the renderer. Each shot is written in the six-field Ref2VA shape against `h3-r2va.md`,
  with the beat before and after it and the continuity block. `StampScene` writes the planned
  place, hour and light into `detailed_description` in code.
- **Sound.** The Ref2VA spec forbids inventing sound, so every request states that the user wants
  the setting's natural sound (`VideoRecipe.SoundRequest`). Without that, `overall_soundscape`
  came back "N/A".
- **Failures.** One shot failing doesn't stop the film. "Film the rest" re-renders unfinished
  shots from their written scenes.
- **Stop** cancels the phone's job on the server too. Every submitted graph carries a
  `_meta.flippix_run` marker; a pending job with that marker is deleted, and `/interrupt` is sent
  only when the running job is the phone's own.
- **Shared GPU.** When the LLM reports out of memory (ComfyUI holds the last render's weights),
  `LlmClient` asks ComfyUI to `/free` and retries once.
- **Measured on 10.0.0.10, 2026-09-21:** 3 shots with one photo, 6:32 end to end (writing about 1:40,
  then about 78 s per shot). Place, light and wardrobe held across the shots.

## Design

These are tokens, not ad-hoc colours: booth `#1B1D2A`, surface `#252838`, raised `#30344A`,
paper text `#F2EEE6`, muted `#A3A6B8`. Brand orange `#FF6B35` is used for one action per page,
with **dark** text on it (about 6.4:1; white would be 2.8:1). Glow `#FFC48A` marks work in
progress. All of them live in `App.axaml`.

A tile takes its picture's shape before the picture exists: `Controls/AspectPanel` sizes itself
from its width and a ratio, and its children can't change that. While the sampler runs, a warm fill rises inside the tile.

## Checking changes without a phone

The views can be rendered headlessly (`Avalonia.Headless` + `Avalonia.Skia`,
`UseHeadlessDrawing = false`, then `window.CaptureRenderedFrame()`) at 412×915. Do this after
any XAML change: a bad resource key compiles clean and only fails at load.
